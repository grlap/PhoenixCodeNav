namespace CodeNav.Core.Indexing;

/// <summary>Immutable progress snapshot for tool responses. FilesTotal is null until the scan
/// discovers it (never fabricated); counters are MONOTONIC (no percent field — a re-estimated
/// total must never make progress appear to move backwards; callers derive % when total exists).
/// No ETA by design (field: "a wildly wrong ETA is worse than none; elapsedMs alone lets a
/// caller decide '5 minutes elapsed, no movement in 60s — probably stuck'").</summary>
public sealed record IndexProgress(string Phase, int FilesIndexed, int? FilesTotal, long ElapsedMs);

/// <summary>
/// Owns: the thread-safe live progress of ONE index build — phase, monotonic file counters, and
/// elapsed time — written by IndexBuilder from its build threads and snapshotted by
/// IndexManager.Health() for server_capabilities and index_building error bodies (field-requested:
/// "state: 'building' is a binary; even the minimum turns 'no idea how long' into '~60% done'").
/// Phases name OUR real pipeline: scanning | parsing_projects | indexing_files | finalizing —
/// not a borrowed one (this indexer has no semantic warmup phase; the semantic layer loads lazily
/// per query, so state 'ready' already means syntactically ready).
/// Does not own: ETA/throughput estimation (deliberately absent — bead 0tn), refresh progress
/// (state 'refreshing' keeps its own pendingChanges shape — bead z4c), or response shaping.
/// </summary>
public sealed class BuildProgress
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private volatile string _phase = "scanning";
    private int _filesIndexed;
    private volatile int _filesTotal = -1; // -1 = not yet known (omitted from responses)

    public void SetPhase(string phase) => _phase = phase;
    public void SetFilesTotal(int total) => _filesTotal = total;
    public void AddFileIndexed() => Interlocked.Increment(ref _filesIndexed);

    public long ElapsedMs => _sw.ElapsedMilliseconds;

    public IndexProgress Snapshot()
    {
        int total = _filesTotal;
        return new IndexProgress(_phase, Volatile.Read(ref _filesIndexed),
            total < 0 ? null : total, _sw.ElapsedMilliseconds);
    }
}
