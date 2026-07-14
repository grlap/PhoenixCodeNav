using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodeNav.Core.Telemetry;

/// <summary>
/// Owns: the producer side of telemetry API v1 (docs/telemetry-api.md, bead x5ls.1) — the
/// current-user named-pipe CLIENT that negotiates hello/welcome, derives salted identities,
/// and publishes NDJSON frames from the bounded <see cref="TelemetryQueue"/> on a dedicated
/// background path. Portal absent/slow/disconnected/crashed never affects Phoenix: connect
/// attempts back off (capped exponential + jitter), Emit never blocks or throws, and every
/// eviction is disclosed via telemetry.dropped.
/// Does not own: WHAT gets measured (instrumentation call sites own their fields), the frame
/// bounds/privacy gate (TelemetryBounds, applied at send), or any HTTP/UI (Project B).
/// Non-Windows: disabled in v1 (the normative endpoint is a Windows named pipe; the Unix
/// socket path is specified but not yet implemented — portal absence semantics apply).
/// </summary>
public sealed class TelemetryProducer : IDisposable
{
    /// <summary>Set once by the MCP host (BuildInfo.Version) — Core cannot reference Mcp.</summary>
    public static string? ProductVersion { get; set; }

    private const int ProtocolVersion = 1;
    // NamedPipeClientStream.ConnectAsync(timeout) SPIN-POLLS the pipe until the deadline —
    // a long timeout against an ABSENT portal is pure CPU burn (measured: dozens of parallel
    // test managers at 5000ms starved index-build startups into timeouts). A listening
    // server accepts instantly; only the absent case pays, so keep it tiny and let the
    // capped backoff (1s→60s) own the cadence.
    private const int ConnectTimeoutMs = 250;
    private const int NegotiateTimeoutMs = 5000;
    private const int MinHeartbeatIntervalMs = 2000;  // contract: heartbeat at most 0.5 Hz
    private const int MinSnapshotIntervalMs = 1000;

    private readonly string _canonicalRoot;
    private readonly string _pipeName;
    private readonly Action<string> _log;
    private readonly Func<TelemetryIds, object> _snapshotData;
    private readonly Func<object>? _heartbeatData;
    private readonly TelemetryQueue _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;
    private readonly string _instanceId = Guid.NewGuid().ToString();
    private readonly HashSet<string> _loggedRejects = new(StringComparer.Ordinal);
    private long _validationRejected;
    private long _lastPublishedSequence;

    /// <summary>Highest sequence actually written to a portal (0 = none yet) — the honest
    /// instance.snapshot `telemetry.lastPublishedSequence` gauge.</summary>
    public long LastPublishedSequence => Interlocked.Read(ref _lastPublishedSequence);

    /// <summary>Frames evicted under backpressure since process start (snapshot gauge).</summary>
    public long DroppedRecords => _queue.TotalDropped;

    /// <summary>Frames rejected by the privacy/bounds gate since process start — nonzero
    /// means an instrumentation site is producing frames the contract forbids.</summary>
    public long ValidationRejected => Interlocked.Read(ref _validationRejected);

    /// <summary>Frames currently queued (snapshot gauge).</summary>
    public int QueuedRecords => _queue.Count;

    public bool Enabled { get; }
    internal string InstanceId => _instanceId;
    internal TelemetryQueue Queue => _queue;

    public TelemetryProducer(string workspaceRoot, string dbPath,
        Func<TelemetryIds, object> snapshotData, Action<string>? log = null,
        Func<object>? heartbeatData = null, string? pipeName = null, bool? enabled = null)
    {
        _canonicalRoot = TelemetryIdentity.CanonicalPath(workspaceRoot);
        CanonicalDbIdentity = TelemetryIdentity.CanonicalPath(dbPath);
        _log = log ?? (_ => { });
        _snapshotData = snapshotData;
        _heartbeatData = heartbeatData;
        _pipeName = pipeName ?? DefaultPipeName();
        Enabled = enabled ?? (OperatingSystem.IsWindows()
            && Environment.GetEnvironmentVariable("PHOENIX_TELEMETRY_IPC") != "0"
            && _pipeName.Length > 0);
        _run = Enabled ? Task.Run(() => RunAsync(_cts.Token)) : Task.CompletedTask;
    }

    internal string CanonicalDbIdentity { get; }

    /// <summary>V1 normative endpoint name (docs/telemetry-api.md): product id + user SID.
    /// Empty when the identity cannot be determined (producer stays disabled).</summary>
    private static string DefaultPipeName()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            return sid is null ? "" : $"phoenix.codenav.telemetry.v1.{sid}";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Enqueues one frame. Never blocks, never throws; identity fields are bound at
    /// send time via the factory (the salt arrives with welcome and changes per portal
    /// session). The factory must reference only redaction-safe primitives it captured.</summary>
    public void Emit(string type, Func<TelemetryIds, object> dataFactory, bool lifecycle = false)
    {
        if (!Enabled) return;
        // Review F11: the envelope is hand-assembled JSON, so the type must be a stable
        // lowercase dotted identifier — anything else could emit a malformed frame, and a
        // malformed frame rejects the whole connection per the framing contract.
        if (!IsValidType(type))
        {
            CountReject("invalid_type");
            return;
        }
        try
        {
            _queue.Enqueue(type, dataFactory, NowIso(), lifecycle);
        }
        catch (Exception ex)
        {
            _log($"Telemetry enqueue failed: {ex.GetType().Name}");
        }
    }

    private static bool IsValidType(string type)
    {
        if (string.IsNullOrEmpty(type) || type.Length > 64) return false;
        foreach (char c in type)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_')) return false;
        }
        return true;
    }

    /// <summary>Review F6: `:` in a custom format is the CULTURE time separator — on locales
    /// like fi-FI it renders `.`, breaking ISO-8601 for every frame from that machine.</summary>
    private static string NowIso()
        => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- connection loop

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            Session? session = null;
            try
            {
                session = await ConnectAndNegotiateAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Portal absent (timeout) or handshake failure — both are normal states.
            }

            if (session is not null)
            {
                attempt = 0;
                // Review F15: a REJECTED session must not park a snapshot per 300s cycle —
                // the frame could only ever be sent if the portal later upgrades.
                if (!session.Rejected) EnqueueSnapshot();
                try
                {
                    await RunSessionAsync(session, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log($"Telemetry session ended: {ex.GetType().Name}");
                }
                finally
                {
                    session.Dispose();
                }
            }

            // Capped exponential backoff with jitter. A protocol reject waits the full cap —
            // the portal must be upgraded/restarted before another attempt can succeed.
            attempt = Math.Min(attempt + 1, 7); // review F14: 2^6=64s so the 60s cap binds
            double baseDelayMs = session is { Rejected: true }
                ? 300_000
                : Math.Min(1000 * Math.Pow(2, attempt - 1), 60_000);
            double jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelayMs * jitter), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private sealed class Session : IDisposable
    {
        public required NamedPipeClientStream Pipe { get; init; }
        public required PipeLineReader Reader { get; init; }
        public TelemetryIds Ids { get; set; } = new("", "");
        public int HeartbeatIntervalMs { get; set; } = MinHeartbeatIntervalMs;
        public int SnapshotIntervalMs { get; set; } = 10_000;
        public int MaxBatchRecords { get; set; } = 100;
        public int MaxFrameBytes { get; set; } = TelemetryBounds.MaxFrameBytes;
        public bool Rejected { get; init; }
        public void Dispose() => Pipe.Dispose();
    }

    private async Task<Session?> ConnectAndNegotiateAsync(CancellationToken ct)
    {
        // Review F7: CurrentUserOnly makes the runtime verify the pipe SERVER is owned by the
        // current user before the connection is usable — the one-flag mitigation for pipe-name
        // squatting the contract's hardening clause asks for (frames are redacted regardless,
        // but a squatter should get nothing at all). The portal must set it server-side too.
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);

            var hello = new
            {
                protocol = "phoenix.telemetry",
                type = "hello",
                supportedVersions = new[] { ProtocolVersion },
                instanceId = _instanceId,
                mcpVersion = ProductVersion ?? "unknown",
                processId = Environment.ProcessId,
                processStartUtc = ProcessStartUtc(),
                platform = PlatformLabel(),
                clientLabel = ClientLabel(),
            };
            await WriteLineAsync(pipe,
                JsonSerializer.Serialize(hello, TelemetryBounds.JsonOpts), ct)
                .ConfigureAwait(false);

            var reader = new PipeLineReader(pipe);
            using var negotiationTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            negotiationTimeout.CancelAfter(NegotiateTimeoutMs);
            string? line = await reader.ReadLineAsync(negotiationTimeout.Token).ConfigureAwait(false);
            if (line is null) { pipe.Dispose(); return null; }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "reject")
            {
                _log("Telemetry portal rejected protocol v1 — standing down until it upgrades.");
                pipe.Dispose();
                return new Session { Pipe = pipe, Reader = reader, Rejected = true };
            }
            if (type != "welcome") { pipe.Dispose(); return null; }

            byte[] salt = Convert.FromBase64String(
                root.GetProperty("identitySaltBase64").GetString() ?? "");
            if (salt.Length < 16) { pipe.Dispose(); return null; } // refuse a degenerate salt
            int selected = root.TryGetProperty("selectedVersion", out var sv) ? sv.GetInt32() : -1;
            if (selected != ProtocolVersion) { pipe.Dispose(); return null; }

            var session = new Session
            {
                Pipe = pipe,
                Reader = reader,
                Ids = new TelemetryIds(
                    TelemetryIdentity.WorkspaceId(salt, _canonicalRoot),
                    TelemetryIdentity.IndexId(salt, CanonicalDbIdentity)),
            };
            if (root.TryGetProperty("heartbeatIntervalMs", out var hb))
                session.HeartbeatIntervalMs = Math.Max(hb.GetInt32(), MinHeartbeatIntervalMs);
            if (root.TryGetProperty("fullSnapshotIntervalMs", out var fs))
                session.SnapshotIntervalMs = Math.Max(fs.GetInt32(), MinSnapshotIntervalMs);
            if (root.TryGetProperty("maxBatchRecords", out var mb))
                session.MaxBatchRecords = Math.Clamp(mb.GetInt32(), 1, 100);
            if (root.TryGetProperty("maxFrameBytes", out var mf))
                session.MaxFrameBytes = Math.Clamp(mf.GetInt32(), 4096, TelemetryBounds.MaxFrameBytes);
            _log($"Telemetry connected to portal (pipe {_pipeName}).");
            return session;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private async Task RunSessionAsync(Session session, CancellationToken ct)
    {
        if (session.Rejected) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender = SenderLoopAsync(session, linked.Token);
        var reader = ReaderLoopAsync(session, linked.Token);
        var heartbeat = HeartbeatLoopAsync(session, linked.Token);
        var snapshots = SnapshotLoopAsync(session, linked.Token);
        await Task.WhenAny(sender, reader, heartbeat, snapshots).ConfigureAwait(false);
        linked.Cancel();
        await Task.WhenAll(
            Swallow(sender), Swallow(reader), Swallow(heartbeat), Swallow(snapshots))
            .ConfigureAwait(false);

        static async Task Swallow(Task t)
        {
            try { await t.ConfigureAwait(false); } catch { /* session teardown */ }
        }
    }

    private async Task SenderLoopAsync(Session session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Disclose drops FIRST — through the queue, so the wire keeps sequence order.
            if (_queue.TakeDropReport() is { } drop)
            {
                Emit("telemetry.dropped", _ => new
                {
                    records = drop.Records,
                    reason = "producer_buffer_full",
                    sinceSequence = drop.SinceSequence,
                }, lifecycle: true);
            }
            // Review F4: frames rejected at send consumed a sequence at enqueue — without a
            // disclosure the portal sees a permanent unexplained gap while every drop gauge
            // reads 0. Same telemetry.dropped channel, distinct reason.
            if (TakeRejectReport() is { } rejected)
            {
                Emit("telemetry.dropped", _ => new
                {
                    records = rejected.Records,
                    reason = "producer_validation_rejected",
                    sinceSequence = rejected.SinceSequence,
                }, lifecycle: true);
            }
            var batch = await _queue.DequeueBatchAsync(session.MaxBatchRecords, ct)
                .ConfigureAwait(false);
            if (batch.Count == 0) return; // cancelled
            foreach (var frame in batch)
            {
                string? dataJson = MaterializeData(frame, session.Ids);
                if (dataJson is null) continue; // rejected: counted, disclosed next round
                string envelope =
                    $"{{\"protocol\":\"phoenix.telemetry\",\"version\":{ProtocolVersion}," +
                    $"\"type\":\"{frame.Type}\",\"instanceId\":\"{_instanceId}\"," +
                    $"\"sequence\":{frame.Sequence},\"timestampUtc\":\"{frame.TimestampUtc}\"," +
                    $"\"data\":{dataJson}}}";
                if (Encoding.UTF8.GetByteCount(envelope) + 1 > session.MaxFrameBytes)
                {
                    RejectFrame(frame, "frame_too_large");
                    continue;
                }
                await WriteLineAsync(session.Pipe, envelope, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastPublishedSequence, frame.Sequence);
            }
            await session.Pipe.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private string? MaterializeData(PendingFrame frame, TelemetryIds ids)
    {
        object data;
        try
        {
            data = frame.DataFactory(ids);
        }
        catch (Exception ex)
        {
            RejectFrame(frame, $"factory_failed:{ex.GetType().Name}");
            return null;
        }
        string? json = TelemetryBounds.SerializeData(data, _canonicalRoot, out string? reason);
        if (json is null) RejectFrame(frame, reason ?? "rejected");
        return json;
    }

    private readonly object _rejectGate = new();
    private long _rejectedPending;
    private long _rejectedSince = -1;

    /// <summary>A frame that already holds a sequence was refused at send — count it for the
    /// next in-band disclosure (the gap it leaves must be explained, review F4).</summary>
    private void RejectFrame(PendingFrame frame, string reason)
    {
        CountReject(reason);
        lock (_rejectGate)
        {
            if (_rejectedSince < 0) _rejectedSince = frame.Sequence;
            _rejectedPending++;
        }
    }

    private (long Records, long SinceSequence)? TakeRejectReport()
    {
        lock (_rejectGate)
        {
            if (_rejectedPending == 0) return null;
            var report = (_rejectedPending, _rejectedSince);
            _rejectedPending = 0;
            _rejectedSince = -1;
            return report;
        }
    }

    private void CountReject(string reason)
    {
        Interlocked.Increment(ref _validationRejected);
        LogNoise($"Telemetry frame rejected before send: {reason}", reason);
    }

    /// <summary>Once-per-reason server-log line WITHOUT touching the rejection gauge — for
    /// conditions where no frame was actually refused (e.g. a heartbeat provider throw whose
    /// fallback payload still ships).</summary>
    private void LogNoise(string message, string? dedupeKey = null)
    {
        lock (_loggedRejects)
        {
            if (_loggedRejects.Count < 16 && _loggedRejects.Add(dedupeKey ?? message))
            {
                _log(message);
            }
        }
    }

    private async Task ReaderLoopAsync(Session session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await session.Reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return; // portal closed the pipe
            try
            {
                using var doc = JsonDocument.Parse(line);
                string? type = doc.RootElement.TryGetProperty("type", out var t)
                    ? t.GetString() : null;
                switch (type)
                {
                    case "resync":
                        EnqueueSnapshot();
                        break;
                    case "shutdown_notice":
                        return; // reconnect with normal backoff; portal is going away politely
                    default:
                        break; // unknown control frames are ignored (v1 tolerance)
                }
            }
            catch (JsonException)
            {
                return; // malformed control stream: drop the session, reconnect fresh
            }
        }
    }

    private async Task HeartbeatLoopAsync(Session session, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(session.HeartbeatIntervalMs));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            object? extra = null;
            // Review F12: an unguarded provider throw would fault this loop and churn the
            // session every heartbeat; fall back to the minimal honest payload instead.
            // Log-only (review r2): no frame is rejected here — the fallback still ships —
            // so the validationRejected gauge must not be inflated.
            try { extra = _heartbeatData?.Invoke(); }
            catch (Exception ex) { LogNoise($"heartbeat_provider:{ex.GetType().Name}"); }
            Emit("heartbeat", _ => extra ?? new { uptimeMs = (long)UptimeMs() });
        }
    }

    private async Task SnapshotLoopAsync(Session session, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(session.SnapshotIntervalMs));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            EnqueueSnapshot();
        }
    }

    private void EnqueueSnapshot()
        => Emit("instance.snapshot", ids => _snapshotData(ids), lifecycle: true);

    // ---------------------------------------------------------------- helpers

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static readonly DateTime ProcessStart = ReadProcessStart();

    private static DateTime ReadProcessStart()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow; // access denied edge: uptime starts at first use
        }
    }

    private static double UptimeMs() => (DateTime.UtcNow - ProcessStart).TotalMilliseconds;

    /// <summary>Cached UTC process start — shared with snapshot providers so uptime gauges
    /// agree across frame types.</summary>
    public static DateTime ProcessStartUtcValue => ProcessStart;

    private static string ProcessStartUtc() => ProcessStart.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
        System.Globalization.CultureInfo.InvariantCulture); // review F6: culture-proof ISO-8601

    private static string PlatformLabel()
    {
        string os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos" : "other";
        return $"{os}-{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}"
            .ToLowerInvariant();
    }

    /// <summary>The contract's "explicit safe configuration value" — an env var the OPERATOR
    /// sets, never a command line or environment dump.</summary>
    private static string? ClientLabel()
    {
        string? label = Environment.GetEnvironmentVariable("PHOENIX_CLIENT_LABEL");
        return string.IsNullOrWhiteSpace(label) ? null : TelemetryBounds.BoundedLabel(label);
    }

    /// <summary>Bounded NDJSON line reader over the pipe — enforces the frame ceiling while
    /// STREAMING (the contract forbids allocating an unbounded line first).</summary>
    internal sealed class PipeLineReader
    {
        private readonly Stream _stream;
        private readonly byte[] _chunk = new byte[4096];
        private readonly List<byte> _buffer = new();
        private int _consumed;

        public PipeLineReader(Stream stream) => _stream = stream;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                int nl = _buffer.IndexOf((byte)'\n', _consumed);
                if (nl >= 0)
                {
                    string line = Encoding.UTF8.GetString(
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer)
                            [_consumed..nl]);
                    _consumed = nl + 1;
                    if (_consumed > 65536)
                    {
                        _buffer.RemoveRange(0, _consumed);
                        _consumed = 0;
                    }
                    return line.TrimEnd('\r');
                }
                if (_buffer.Count - _consumed > TelemetryBounds.MaxFrameBytes)
                {
                    return null; // oversized control frame: reject the connection
                }
                int read;
                try
                {
                    read = await _stream.ReadAsync(_chunk, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                if (read == 0) return null; // pipe closed
                _buffer.AddRange(new ArraySegment<byte>(_chunk, 0, read));
            }
        }
    }

    private int _disposed;

    public void Dispose()
    {
        // Idempotent + thread-safe: IndexManager.Dispose is commonly called twice
        // (using-block plus explicit), and a second Cancel on a disposed CTS would throw
        // OUT of the manager's Dispose, aborting the rest of ITS teardown midway.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* concurrent teardown */ }
        bool exited = false;
        try { exited = _run.Wait(TimeSpan.FromSeconds(2)); }
        catch { exited = true; /* faulted counts as exited */ }
        // Review F10: disposing the CTS while the run task still holds its token would fault
        // that task with ObjectDisposedException; on a timed-out wait, leak the CTS instead
        // (the process is tearing down anyway).
        if (exited) _cts.Dispose();
    }
}
