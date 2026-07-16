using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodeNav.Tests;

/// <summary>
/// Owns: the contract-test IPC broker for telemetry API v1 (x5ls.1 acceptance step 3) — a
/// minimal named-pipe SERVER standing in for the Operations Portal: accepts producer
/// connections, reads hello, answers welcome (or reject), captures every subsequent NDJSON
/// frame, and can inject portal control frames (resync/shutdown_notice) or drop the
/// connection. Purely a test double; asserts nothing itself.
/// Does not own: any production behavior (lives in the test assembly only).
/// </summary>
internal sealed class TestTelemetryBroker : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private NamedPipeServerStream? _current;
    private readonly object _gate = new();

    public string PipeName { get; }
    public byte[] Salt { get; }
    public bool RejectMode { get; set; }
    public ConcurrentQueue<string> HelloLines { get; } = new();
    public ConcurrentQueue<JsonDocument> Frames { get; } = new();
    public int Connections => _connections;
    private int _connections;

    public TestTelemetryBroker(bool rejectMode = false)
    {
        PipeName = $"phoenix.test.telemetry.{Guid.NewGuid():N}";
        Salt = new byte[32];
        Random.Shared.NextBytes(Salt);
        RejectMode = rejectMode;
        _accept = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                // CurrentUserOnly on BOTH sides per the contract's anti-squatting clause —
                // this broker doubles as proof the flags interoperate.
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            Interlocked.Increment(ref _connections);
            lock (_gate) { _current = server; }
            try
            {
                await ServeAsync(server).ConfigureAwait(false);
            }
            catch { /* connection torn down — accept the next one */ }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_current, server)) _current = null; }
                server.Dispose();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        string? hello = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
        if (hello is null) return;
        HelloLines.Enqueue(hello);

        object reply = RejectMode
            ? new
            {
                protocol = "phoenix.telemetry",
                type = "reject",
                code = "protocol_version_unsupported",
                supportedVersions = new[] { 99 },
            }
            : new
            {
                protocol = "phoenix.telemetry",
                type = "welcome",
                selectedVersion = 1,
                portalSessionId = Guid.NewGuid().ToString(),
                identitySaltBase64 = Convert.ToBase64String(Salt),
                heartbeatIntervalMs = 2000,
                fullSnapshotIntervalMs = 10000,
                maxFrameBytes = 262144,
                maxBatchRecords = 100,
            };
        await WriteLineAsync(server, JsonSerializer.Serialize(reply)).ConfigureAwait(false);
        if (RejectMode) return;

        while (!_cts.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            if (line is null) return;
            try { Frames.Enqueue(JsonDocument.Parse(line)); }
            catch (JsonException) { /* malformed producer frame — a test will notice */ }
        }
    }

    /// <summary>Injects one portal control frame into the live connection (throws if none).</summary>
    public void SendControl(object frame)
    {
        NamedPipeServerStream? current;
        lock (_gate) { current = _current; }
        if (current is null) throw new InvalidOperationException("no live connection");
        WriteLineAsync(current, JsonSerializer.Serialize(frame)).GetAwaiter().GetResult();
    }

    /// <summary>Drops the live producer connection (reconnect-path tests).</summary>
    public void DropConnection()
    {
        NamedPipeServerStream? current;
        lock (_gate) { current = _current; _current = null; }
        current?.Dispose();
    }

    public List<JsonDocument> FramesOfType(string type)
        => Frames.Where(f => f.RootElement.TryGetProperty("type", out var t)
            && t.GetString() == type).ToList();

    private static async Task WriteLineAsync(Stream stream, string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        DropConnection();
        try { _accept.Wait(TimeSpan.FromSeconds(2)); } catch { /* teardown */ }
        _cts.Dispose();
    }
}
