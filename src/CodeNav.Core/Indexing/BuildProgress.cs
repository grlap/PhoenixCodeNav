namespace CodeNav.Core.Indexing;

/// <summary>Immutable progress snapshot for tool responses. FilesTotal is null until the scan
/// discovers it (never fabricated); counters are MONOTONIC (no percent field — a re-estimated
/// total must never make progress appear to move backwards; callers derive % when total exists).
/// FilesSkipped / ProjectsFailed (efa) are 0 on a clean build — emitters stay silent then.
/// FilesPerSecond / EstimatedRemainingMs (0tn) are DERIVED from the monotonic counters and only
/// present while the indexing_files phase has measurable throughput (>=100 files over >=1s of
/// THAT phase) — labeled estimates that may fluctuate, never a promise; absent otherwise. The
/// original no-ETA stance (field: "a wildly wrong ETA is worse than none") is preserved by the
/// gates: nothing is shown until it is measured, and nothing is ever extrapolated from another
/// phase's clock.</summary>
public sealed record IndexProgress(string Phase, int FilesIndexed, int? FilesTotal, long ElapsedMs,
    int FilesSkipped = 0, int ProjectsFailed = 0,
    double? FilesPerSecond = null, long? EstimatedRemainingMs = null);

/// <summary>
/// Owns: the thread-safe live progress of ONE index build — phase, monotonic file counters,
/// skip/failure visibility (efa), throughput-derived remaining-time estimation (0tn), and
/// elapsed time — written by IndexBuilder from its build threads and snapshotted by
/// IndexManager.Health() for server_capabilities and index_building error bodies (field-requested:
/// "state: 'building' is a binary; even the minimum turns 'no idea how long' into '~60% done'").
/// Phases name OUR real pipeline: scanning | parsing_projects | indexing_files | finalizing —
/// not a borrowed one (this indexer has no semantic warmup phase; the semantic layer loads lazily
/// per query, so state 'ready' already means syntactically ready).
/// Does not own: refresh progress (state 'refreshing' pairs pendingChanges with the manager's
/// pendingProcessed counter — bead z4c, lives on IndexManager) or response shaping.
/// </summary>
public sealed class BuildProgress
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private volatile string _phase = "scanning";
    private int _filesIndexed;
    private int _filesSkipped;
    private volatile int _projectsFailed;
    private volatile int _filesTotal = -1; // -1 = not yet known (omitted from responses)
    private long _indexingPhaseStartMs = -1; // stamped when the indexing_files phase begins (0tn)

    public void SetPhase(string phase)
    {
        _phase = phase;
        if (phase == "indexing_files")
        {
            // The throughput clock starts HERE — deriving a rate from the method-start clock
            // would fold scan/parse time into it and systematically overstate the remaining time.
            Interlocked.Exchange(ref _indexingPhaseStartMs, _sw.ElapsedMilliseconds);
        }
        lock (_phaseLog) { _phaseLog.Add((phase, _sw.ElapsedMilliseconds)); } // x5ls.1.2
    }

    // x5ls.1.2: phase-transition log for the telemetry API's index.build.completed
    // phaseDurations — measured starts, never reconstructed after the fact.
    private readonly List<(string Phase, long StartMs)> _phaseLog = new() { ("scanning", 0) };

    /// <summary>The current phase and its elapsed time, read ATOMICALLY under one lock
    /// (x5ls.1.2 reviews B4 + B-r2: two separate reads let a SetPhase land in between,
    /// pairing the old phase label with the new phase's near-zero elapsed in one frame).</summary>
    public (string Phase, long ElapsedInPhaseMs) CurrentPhase()
    {
        lock (_phaseLog)
        {
            var last = _phaseLog[^1];
            return (last.Phase, Math.Max(0, _sw.ElapsedMilliseconds - last.StartMs));
        }
    }

    /// <summary>Per-phase durations: each phase runs from its recorded start to the next
    /// phase's start (the last one to now). Snapshot-safe; used once at build completion.</summary>
    public IReadOnlyList<(string Phase, long DurationMs)> PhaseDurations()
    {
        lock (_phaseLog)
        {
            var result = new List<(string, long)>(_phaseLog.Count);
            for (int i = 0; i < _phaseLog.Count; i++)
            {
                long end = i + 1 < _phaseLog.Count ? _phaseLog[i + 1].StartMs : _sw.ElapsedMilliseconds;
                result.Add((_phaseLog[i].Phase, Math.Max(0, end - _phaseLog[i].StartMs)));
            }
            return result;
        }
    }

    public void SetFilesTotal(int total) => _filesTotal = total;
    public void AddFileIndexed() => Interlocked.Increment(ref _filesIndexed);

    /// <summary>efa: a source file the build could NOT read (transient IO / access denied) — it is
    /// absent from the index until a delta refresh retries it, and pretending the build was
    /// clean hid that.</summary>
    public void AddFileSkipped() => Interlocked.Increment(ref _filesSkipped);

    /// <summary>efa: project files whose parse failed (LoadStatus 'failed…') — their compile sets and
    /// graph edges are guesses at best; a caller watching the build deserves to know how many.</summary>
    public void SetProjectsFailed(int count) => _projectsFailed = count;

    public long ElapsedMs => _sw.ElapsedMilliseconds;

    public IndexProgress Snapshot()
    {
        int total = _filesTotal;
        int indexed = Volatile.Read(ref _filesIndexed);
        long elapsed = _sw.ElapsedMilliseconds;

        // 0tn gates: only the indexing_files phase has a meaningful file rate, and only once
        // enough of it has happened to measure (>=100 files over >=1s of the phase's own clock).
        double? rate = null;
        long? remaining = null;
        long phaseStart = Interlocked.Read(ref _indexingPhaseStartMs);
        if (_phase == "indexing_files" && total > 0 && indexed >= 100 && phaseStart >= 0)
        {
            long phaseElapsed = elapsed - phaseStart;
            if (phaseElapsed >= 1000)
            {
                double perSec = indexed * 1000.0 / phaseElapsed;
                rate = Math.Round(perSec, 1);
                int left = total - indexed;
                if (left >= 0 && perSec > 0)
                {
                    remaining = (long)(left / perSec * 1000.0);
                }
            }
        }
        return new IndexProgress(_phase, indexed, total < 0 ? null : total, elapsed,
            Volatile.Read(ref _filesSkipped), _projectsFailed, rate, remaining);
    }
}
