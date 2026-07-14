using System.Text.Json;
using System.Threading.Channels;

namespace CodeNav.Core.Diagnostics;

/// <summary>
/// Owns: the bounded, non-blocking, privacy-safe telemetry stream (epuc.1) — one JSONL record
/// per semantic operation, appended by a background drainer to
/// {workspace}/.codenav/telemetry/phoenix-{pid}-{start}-{seq}.jsonl and mirrored into a bounded
/// in-memory ring (the future portal's snapshot source; see
/// docs/internal-operations-portal.md — the portal consumes files/ring and can never block
/// the MCP server).
/// Shaped by the portal spec's privacy posture: correlation ids, phase timings, bounded work
/// COUNTS — no source code, no query arguments, no symbol payloads, no absolute paths.
/// Emission NEVER adds latency or exceptions to a request path: a bounded channel drops the
/// oldest record under pressure (counted and disclosed in the next record that fits), the
/// file is size-capped and announces its own truncation, and every I/O failure downgrades to
/// a server-log line once.
/// Does not own: what gets measured (SemanticService/SemanticWorkspace own their spans) or
/// any HTTP surface (x5ls, tracked separately, not authorized).
/// </summary>
public sealed class TelemetryLog : IDisposable
{
    private const int RingCapacity = 256;
    private const int ChannelCapacity = 1024;
    internal long FileCapBytes = 16 * 1024 * 1024; // internal: tests shrink it to pin the cap

    private readonly Channel<string> _pending;
    private readonly Task _drainer;
    private readonly string _filePath;
    private readonly Action<string> _log;
    private readonly Queue<string> _ring = new();
    private readonly object _ringGate = new();
    private static int _fileSequence; // review F6: uniquifies same-second same-pid streams
    private long _dropped;
    private long _written;
    private bool _capAnnounced;
    private bool _ioFailed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public TelemetryLog(string workspaceRoot, Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        string dir = Path.Combine(workspaceRoot, ".codenav", "telemetry");
        // Review F6: pid+seconds collided when two managers opened one root within a second
        // (second stream silently dead after its Append failed against FileShare.Read) — the
        // per-process sequence makes every stream's file unique.
        int seq = Interlocked.Increment(ref _fileSequence);
        string file = $"phoenix-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMddHHmmss}-{seq}.jsonl";
        _filePath = Path.Combine(dir, file);
        _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // never block a request path
            SingleReader = true,
        },
        // Review F1: DropOldest makes TryWrite return TRUE while silently evicting the oldest
        // queued record — the itemDropped callback is the ONLY place evictions are visible.
        // Without it the "counted and disclosed" contract (and the portal spec's observable
        // dropped counter) was dead code.
        _ => Interlocked.Increment(ref _dropped));
        _drainer = Task.Run(DrainAsync);
        TryCleanupStale(dir);
    }

    /// <summary>The file the current process writes — callers may surface it in logs so an
    /// operator can find today's telemetry without guessing.</summary>
    public string FilePath => _filePath;

    /// <summary>Serialize and enqueue one record. Never throws, never blocks; under pressure
    /// the OLDEST queued record is dropped and the loss is disclosed in-band.</summary>
    public void Emit(object record)
    {
        string line;
        try
        {
            line = JsonSerializer.Serialize(record, JsonOpts);
        }
        catch (Exception ex)
        {
            _log($"Telemetry serialization failed: {ex.GetType().Name}");
            return;
        }
        lock (_ringGate)
        {
            _ring.Enqueue(line);
            while (_ring.Count > RingCapacity) _ring.Dequeue();
        }
        if (!_pending.Writer.TryWrite(line))
        {
            // Only reachable after Dispose completed the channel (DropOldest never rejects a
            // live write — evictions surface via the itemDropped callback above).
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Bounded most-recent records — the in-process snapshot surface the portal's
    /// IPC layer will read; also handy for a future diagnostics dump.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_ringGate) return _ring.ToArray();
    }

    private async Task DrainAsync()
    {
        StreamWriter? writer = null;
        try
        {
            await foreach (string line in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_capAnnounced || _ioFailed) continue; // keep draining so Emit never backs up
                try
                {
                    if (writer is null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                        writer = new StreamWriter(new FileStream(_filePath, FileMode.Append,
                            FileAccess.Write, FileShare.Read));
                    }
                    long dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0)
                    {
                        string disclosure = JsonSerializer.Serialize(
                            new { e = "telemetry_dropped", count = dropped }, JsonOpts);
                        await writer.WriteLineAsync(disclosure).ConfigureAwait(false);
                        _written += disclosure.Length + 2;
                    }
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    _written += line.Length + 2;
                    if (_written >= FileCapBytes)
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(
                            new { e = "telemetry_truncated", capBytes = FileCapBytes }, JsonOpts)).ConfigureAwait(false);
                        await writer.FlushAsync().ConfigureAwait(false);
                        _capAnnounced = true; // ring keeps rolling; the file honestly ends here
                    }
                }
                catch (Exception ex)
                {
                    _ioFailed = true; // disk full / locked dir: telemetry dies quietly, server does not
                    _log($"Telemetry file write failed ({ex.GetType().Name}) — telemetry disabled for this process.");
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    /// <summary>Best-effort: delete telemetry files older than 7 days so long-lived worktrees
    /// do not accumulate one file per historical process forever.</summary>
    private void TryCleanupStale(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "phoenix-*.jsonl"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(7))
                {
                    try { File.Delete(f); } catch { /* in use by a live process */ }
                }
            }
        }
        catch { /* enumeration hiccups never matter here */ }
    }

    public void Dispose()
    {
        _pending.Writer.TryComplete();
        try { _drainer.Wait(TimeSpan.FromSeconds(2)); } catch { /* teardown is best-effort */ }
    }
}
