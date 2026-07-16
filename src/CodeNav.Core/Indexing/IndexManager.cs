using System.Threading.Channels;

namespace CodeNav.Core.Indexing;

public sealed record IndexHealth(
    string State,               // missing | building | ready | refreshing | failed
    string? IndexVersion,
    string? IndexedAtUtc,
    string? LastRefreshUtc,
    int PendingChanges,
    string? Error,
    long DbBytes,
    string WorkspaceRoot,
    string DbPath,
    string? IndexedCommit = null,   // git commit the index reflects (git-aware refresh)
    string? IndexedBranch = null,
    IndexProgress? Progress = null, // live build progress — non-null ONLY while state == building
                                    // z4c: MONOTONIC count of file deltas the pump has APPLIED (added+changed+deleted across
                                    // all refreshes since this manager started). Paired with PendingChanges it turns the
                                    // refreshing state from a binary into movement: pending drains while processed climbs —
                                    // a stuck pump (pending flat, processed flat) is distinguishable from a busy one.
    long PendingProcessed = 0,
    string AccessMode = "writer");

public sealed class IndexReadSnapshot : IDisposable
{
    private Action? _release;

    internal IndexReadSnapshot(IndexQueries queries, IndexHealth health, Action release)
    {
        Queries = queries;
        Health = health;
        _release = release;
    }

    public IndexQueries Queries { get; }
    public IndexHealth Health { get; }

    public void Dispose()
    {
        Action? release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;
        try { Queries.Dispose(); }
        finally { release(); }
    }
}

/// <summary>
/// Owns: index lifecycle for one workspace — open-or-build (in background, never
/// blocking server startup), watcher wiring, serialized delta refreshes, and health
/// snapshots for tool responses. Does not own: query shapes (IndexQueries) or the
/// MCP protocol surface.
/// </summary>
public sealed class IndexManager : IDisposable
{
    private const int GitDiffCap = 5000; // beyond this, a full sweep beats a giant targeted batch
    public const string WriterAccessMode = "writer";
    public const string FollowerAccessMode = "follower";
    public const string UnavailableAccessMode = "unavailable";
    private const string FollowerWriterRequired =
        "This Phoenix process is a read-only follower; run this operation from the writer process.";
    private const string FollowerIndexUnavailable =
        "read-only follower requires a compatible index from the writer; wait for the writer to finish building or rebuilding, then retry or restart this process";
    private const string FollowerWriterUnavailable =
        "the index writer is no longer running; restart this follower to acquire writer mode, or start another writer process";
    private const string FullRebuildDeferredForReaders =
        "full rebuild is waiting for active index readers; the queued rebuild remains pending";

    private sealed record FollowerPublication(
        IndexMetadataSnapshot? Metadata,
        bool Readable,
        string State,
        string? Error);

    // A refresh unit: Paths=null is a full detect-all sweep; RecordCommit, when set, is written
    // as the reflected git commit after the batch succeeds (git-aware reconcile). FullRebuild
    // (tky) throws the whole index away and rebuilds from scratch — the in-band recovery hatch
    // (field: parked at state 'failed' with no remedy but shell rm -rf .codenav).
    // Reason (x5ls.1.2 review B2): explicit provenance for the refresh telemetry frame —
    // shape-derivation mislabeled tool-requested batches as watcher_batch and git fallback
    // sweeps as full_sweep. Producers label at the source; the pump falls back to shape.
    private sealed record RefreshRequest(IReadOnlyCollection<string>? Paths, string? RecordCommit = null,
        bool FullRebuild = false, string? Reason = null);

    private readonly string _workspaceRoot;
    private readonly string _dbPath;
    private string _databaseIoPath;
    private readonly Action<string> _log;
    private readonly Channel<RefreshRequest> _refreshQueue = Channel.CreateUnbounded<RefreshRequest>();
    private readonly TaskCompletionSource<bool> _startupComplete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IndexStore? _store;
    private WorkspaceWatcher? _watcher;
    private GitWatcher? _gitWatcher;
    private string? _gitDir;
    private Task? _pump;
    private Task? _startTask;
    // Serializes watcher publication (StartWatcher / InitGitTracking, on the start task) against
    // Dispose. Without it, a slow start task (big build) can create a watcher AFTER Dispose's
    // bounded wait already gave up, leaking the FileSystemWatcher + timer. Under the lock, a
    // watcher is created only if Dispose has not already set _disposed.
    private readonly object _disposeLock = new();
    private IndexOwnershipLease? _ownershipLease;
    private IndexDirectoryAuthority? _directoryAuthority;
    private string? _authorityDirectoryIdentity;
    private volatile bool _disposed;
    private volatile string _state = "missing";
    private volatile string? _error;
    private volatile string _accessMode = UnavailableAccessMode;
    private FollowerPublication _followerPublication =
        new(null, false, "failed", FollowerIndexUnavailable);
    private readonly object _followerMetadataGate = new();
    private long _nextFollowerMetadataRefresh;
    private int _followerMetadataRefreshActive;
    private volatile bool _followerWriterPresent;
    private long _nextFollowerWriterProbe;
    private int _followerWriterProbeActive;
    private long _pendingProcessed; // z4c: lifetime count of applied file deltas (see IndexHealth)
    // Index metadata is cached here so Health() (called on tool threads) never touches
    // the single write connection, which only the opening thread and the pump may use.
    // Read once at open, then updated by the pump after each refresh.
    private volatile string? _indexVersion;
    private volatile string? _indexedAtUtc;
    private volatile string? _lastRefreshUtc;
    private volatile string? _indexedCommit;
    private volatile string? _indexedBranch;
    private volatile BuildProgress? _buildProgress; // non-null only while a build is running
    // Even values identify stable committed index epochs; odd values mean the serialized pump is
    // mutating the database or its cached metadata. A review snapshot validates the same even
    // value before and after pinning its SQLite WAL read transaction.
    private long _refreshEpoch;
    internal Action<string>? ReviewSnapshotAfterQueryForTest { get; set; }
    internal Action<IndexMetadataSnapshot>? FollowerMetadataBeforePublishForTest { get; set; }
    internal Action? FollowerMetadataBeforeGateForTest { get; set; }
    internal Action? FullRebuildWaitingForReviewSnapshotsForTest { get; set; }
    internal Action<int>? FullRebuildDestructiveBoundaryForTest { get; set; }
    internal Action? FullRebuildCompletedForTest { get; set; }
    internal Action? RefreshRequestDequeuedForTest { get; set; }
    internal Action? RefreshRequestPassedStartupBarrierForTest { get; set; }
    internal Action? StartupAfterLeaseAcquiredForTest { get; set; }
    internal Action? StartupAfterLeaseContentionForTest { get; set; }
    internal Action? CleanupBeforePoolClearForTest { get; set; }
    internal TimeSpan DisposeWaitTimeoutForTest { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan FullRebuildReviewWaitTimeoutForTest { get; set; } =
        TimeSpan.FromSeconds(30);
    private readonly object _reviewSnapshotGate = new();
    private readonly ManualResetEventSlim _noActiveReviewSnapshots = new(initialState: true);
    private readonly ManualResetEventSlim _stableIndexEpoch = new(initialState: true);
    private int _activeReviewSnapshots;
    private readonly object _resourceReleaseLock = new();
    private bool _ownedResourcesReleased;

    public IndexManager(string workspaceRoot, string? dbPath = null, Action<string>? log = null,
        string? telemetryPipeName = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _dbPath = Path.GetFullPath(dbPath ?? IndexBuilder.DefaultDbPath(_workspaceRoot));
        _databaseIoPath = _dbPath;
        _log = log ?? (_ => { });
        // epuc.1: one bounded telemetry stream per manager (== per workspace per process).
        // Lazy-free by design: the writer task parks on an empty channel until first Emit.
        Telemetry = new Diagnostics.TelemetryLog(_workspaceRoot, _log);
        // x5ls.1: the telemetry API v1 IPC producer. Portal absent = cheap periodic connect
        // attempts with capped backoff; disabled entirely via PHOENIX_TELEMETRY_IPC=0.
        // telemetryPipeName overrides the normative endpoint (contract tests only).
        TelemetryIpc = new CodeNav.Core.Telemetry.TelemetryProducer(
            _workspaceRoot, _dbPath, BuildTelemetrySnapshot, _log,
            pipeName: telemetryPipeName);
    }

    /// <summary>x5ls.1: the telemetry API v1 producer (docs/telemetry-api.md). Instrumentation
    /// call sites Emit through it; the portal connects out-of-process.</summary>
    internal CodeNav.Core.Telemetry.TelemetryProducer TelemetryIpc { get; }

    /// <summary>Shapes the v1 instance.snapshot data payload from state this manager can
    /// report HONESTLY today (x5ls.1 Batch A). Unknown gauges are omitted, never zeroed:
    /// cpuPercent/threadCount need sampling (Batch C), semantic/operations blocks need their
    /// instrumentation (Batch C); followers omit writer-only pending counters they cannot
    /// know, and a writer without a live watcher reports pendingChanges as unknown rather
    /// than a fabricated 0 (review F5). Contract fields only — no paths, no raw error text.</summary>
    private object BuildTelemetrySnapshot(CodeNav.Core.Telemetry.TelemetryIds ids)
    {
        var h = Health();
        bool follower = string.Equals(h.AccessMode, FollowerAccessMode, StringComparison.Ordinal);
        return new
        {
            workspace = new
            {
                id = ids.WorkspaceId,
                label = CodeNav.Core.Telemetry.TelemetryBounds.BoundedLabel(Path.GetFileName(_workspaceRoot)),
            },
            index = new
            {
                id = ids.IndexId,
                accessMode = h.AccessMode,
                // Raw pass-through (review F16): the contract's consumers render unknown enum
                // values as "unknown (<value>)" — suppressing the field would hide more.
                state = h.State,
                indexVersion = h.IndexVersion,
                indexedAtUtc = h.IndexedAtUtc,
                lastRefreshUtc = h.LastRefreshUtc,
                databaseBytes = h.DbBytes > 0 ? h.DbBytes : (long?)null,
                pendingChanges = follower || _watcher is null ? null : (int?)h.PendingChanges,
                pendingProcessed = follower ? null : (long?)h.PendingProcessed,
                // h.Error may carry text; the contract allows stable codes only.
                errorCode = h.Error is null ? null : "index_error",
            },
            process = new
            {
                uptimeMs = (long)(DateTime.UtcNow
                    - CodeNav.Core.Telemetry.TelemetryProducer.ProcessStartUtcValue).TotalMilliseconds,
                workingSetBytes = Environment.WorkingSet,
                managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                gen0Collections = GC.CollectionCount(0),
                gen1Collections = GC.CollectionCount(1),
                gen2Collections = GC.CollectionCount(2),
            },
            // Review F13: the producer's background task can invoke this factory before the
            // ctor finishes assigning TelemetryIpc — omit the block instead of NRE-ing the
            // connection's first snapshot into a silent factory_failed drop.
            telemetry = TelemetryIpc is not { } ipc ? (object?)null : new
            {
                queuedRecords = ipc.QueuedRecords,
                droppedRecords = ipc.DroppedRecords,
                lastPublishedSequence = ipc.LastPublishedSequence,
                // Additive within v1 (consumers ignore unknown counters): frames the
                // privacy/bounds gate refused — a nonzero value is a producer-side bug signal.
                validationRejected = ipc.ValidationRejected,
            },
        };
    }

    // ------------------------------------------------------------ x5ls.1.2 build/refresh frames

    /// <summary>Starts the v1 build-lifecycle telemetry for one build run: emits
    /// index.build.started and returns a 250ms sampling timer (the contract's 4 Hz progress
    /// ceiling) that snapshots the SAME BuildProgress the tool surface reads — no second
    /// bookkeeping, no builder hot-path hooks. Every factory closes over primitives captured
    /// at tick time, never over the progress object (frames materialize at send).</summary>
    private (string BuildId, System.Threading.Timer Timer) BeginBuildTelemetry(
        string reason, BuildProgress progress)
    {
        string buildId = Guid.NewGuid().ToString();
        TelemetryIpc.Emit("index.build.started",
            ids => new { buildId, indexId = ids.IndexId, reason, phase = "scanning" },
            lifecycle: true);
        var timer = new System.Threading.Timer(timerState =>
        {
            try
            {
                // Atomic pair (review B-r2): label and in-phase elapsed from ONE lock scope,
                // so a phase transition can't pair the old label with the new phase's clock.
                // Captured BEFORE Snapshot (review B-r3 note): keeps phaseElapsedMs ≤ elapsedMs.
                var (phase, phaseElapsedMs) = progress.CurrentPhase();
                var s = progress.Snapshot();
                _ = TryGetSafeDatabaseStatus(out _, out long dbBytes);
                TelemetryIpc.Emit("index.build.progress", ids => new
                {
                    buildId,
                    indexId = ids.IndexId,
                    phase,
                    phaseElapsedMs,                          // review B4: contract field
                    elapsedMs = s.ElapsedMs,
                    filesIndexed = s.FilesIndexed,
                    filesTotal = s.FilesTotal,               // null until honestly known (0tn)
                    filesSkipped = s.FilesSkipped,
                    projectsFailed = s.ProjectsFailed,
                    filesPerSecond = s.FilesPerSecond,       // null until the gate opens (0tn)
                    estimatedRemainingMs = s.EstimatedRemainingMs,
                    databaseBytes = dbBytes > 0 ? dbBytes : (long?)null, // review B4
                });
            }
            catch { /* a progress tick must never hurt the build */ }
        }, null, 250, 250);
        return (buildId, timer);
    }

    /// <summary>Review B1: stops the progress timer AND waits out any in-flight tick before
    /// the terminal frame is emitted — otherwise progress frames with the same buildId land
    /// at higher sequences than completed/failed and a portal's latest-frame state regresses
    /// completed → "building". Task-signaled (review B-r2: the WaitHandle pattern crashed the
    /// PROCESS on a timed-out drain — the disposed MRE got Set by the straggler tick on a
    /// threadpool thread; DisposeAsync leaves nothing to corrupt when the bounded wait gives
    /// up). Idempotent — double-disposal returns a completed task.</summary>
    private static void DrainDisposeBuildTimer(System.Threading.Timer timer)
    {
        try
        {
            timer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* drain is best-effort; a stuck tick must never hurt the build path */ }
    }

    private void EmitBuildCompleted(string buildId, BuildProgress progress, long durationMs)
    {
        var s = progress.Snapshot();
        var phases = progress.PhaseDurations()
            .Select(p => new { phase = p.Phase, durationMs = p.DurationMs }).ToArray();
        _ = TryGetSafeDatabaseStatus(out _, out long dbBytes);
        TelemetryIpc.Emit("index.build.completed", ids => new
        {
            buildId,
            indexId = ids.IndexId,
            durationMs,
            filesIndexed = s.FilesIndexed,
            filesSkipped = s.FilesSkipped,
            projectsFailed = s.ProjectsFailed,
            databaseBytes = dbBytes > 0 ? dbBytes : (long?)null,
            phaseDurations = phases,
        }, lifecycle: true);
    }

    private void EmitBuildFailed(string buildId, BuildProgress progress)
    {
        string failedPhase = progress.Snapshot().Phase;
        TelemetryIpc.Emit("index.build.failed", ids => new
        {
            buildId,
            indexId = ids.IndexId,
            failedPhase,
            errorCode = "index_build_failed", // stable code; raw exception text never crosses IPC
            retryable = true,                 // refresh_index force:'full' remains the remedy
        }, lifecycle: true);
    }

    /// <summary>One v1 refresh outcome frame (completed/failed). Refresh batches are debounced
    /// upstream, so per-outcome emission stays far under the gauge cadence ceiling.</summary>
    private void EmitRefreshSnapshot(string refreshId, string reason, string state,
        int batchProcessed, long elapsedMs, string? errorCode)
    {
        int? pendingChanges = _watcher?.PendingCount;
        long pendingProcessed = Interlocked.Read(ref _pendingProcessed);
        TelemetryIpc.Emit("index.refresh.snapshot", ids => new
        {
            refreshId,
            indexId = ids.IndexId,
            state,
            reason,
            pendingChanges,          // null before a watcher exists — unknown, never fabricated
            pendingProcessed,
            batchProcessed,
            elapsedMs,
            errorCode,
        });
    }

    /// <summary>epuc.1: the workspace's bounded telemetry stream (JSONL file + in-memory
    /// ring). Consumers: SemanticService per-operation records today; the x5ls portal's IPC
    /// snapshots later. Never blocks or throws into request paths.</summary>
    internal Diagnostics.TelemetryLog Telemetry { get; }

    public string WorkspaceRoot => _workspaceRoot;
    public string DbPath => _dbPath;
    public string AccessMode => _accessMode;
    public bool IsWriter => string.Equals(_accessMode, WriterAccessMode,
        StringComparison.Ordinal);
    public bool IsFollower => string.Equals(_accessMode, FollowerAccessMode,
        StringComparison.Ordinal);
    internal string DatabaseIoPath
    {
        get
        {
            EnsureDatabaseAuthority();
            if (IsFollower && !TryRefreshFollowerMetadata(force: true))
                throw new IOException(FollowerIndexUnavailable);
            return _databaseIoPath;
        }
    }
    public string State => IsFollower
        ? Volatile.Read(ref _followerPublication).State
        : _state;

    public WorktreeIndexResult EnsureWorktreeIndex(string worktreePath, string mode,
        Action<string> log)
    {
        if (!IsWriter)
            return new WorktreeIndexResult("index_writer_required",
                FollowerWriterRequired, 0, 0, 0, 0, null, false);
        if (!HasSafeDatabaseAuthority())
            return new WorktreeIndexResult("snapshot_failed",
                "the source index destination is no longer safe", 0, 0, 0, 0, null, false);
        return WorktreeIndexer.Ensure(
            _workspaceRoot, _databaseIoPath, worktreePath, mode, log);
    }

    private bool TryGetSafeDatabaseStatus(out IndexLeaseIdentity? current, out long dbBytes)
    {
        current = null;
        dbBytes = 0;
        return _directoryAuthority?.TryGetDatabaseStatus(out current, out dbBytes) == true &&
               current?.DirectoryIdentity == _authorityDirectoryIdentity;
    }

    private bool HasSafeDatabaseAuthority() =>
        TryGetSafeDatabaseStatus(out _, out _);

    private void EnsureDatabaseAuthority()
    {
        if (!HasSafeDatabaseAuthority())
            throw new IOException("index destination authority is no longer safe");
    }

    /// <summary>Opens the existing index or builds a new one in the background; returns immediately.</summary>
    public void Start(bool forceRebuild = false)
    {
        lock (_disposeLock)
        {
            if (_disposed || IsWriter || IsFollower || _pump is not null || _startTask is not null)
                return;
            if (!IndexDirectoryAuthority.TryOpen(_dbPath, createDirectory: true,
                    out IndexDirectoryAuthority? authority) ||
                !authority!.TryGetLeaseIdentity(out IndexLeaseIdentity? leaseIdentity))
            {
                authority?.Dispose();
                // Sanitized like every startup error (9vw): fixed phrase, no filesystem internals.
                // "safely" not "without following links" — this gate also refuses a missing or
                // non-directory parent, not only link/reparse traversal; specifics go to the log.
                _error = "index destination could not be opened safely during index startup (see server log)";
                _accessMode = UnavailableAccessMode;
                _state = "failed";
                _log($"Index startup refused: destination '{_dbPath}' is not a safely openable " +
                     "directory tree (missing or non-directory parent, link/reparse point, or " +
                     "inaccessible); force:'full' retries once the blocker clears.");
                return;
            }
            _directoryAuthority = authority;
            _authorityDirectoryIdentity = leaseIdentity!.DirectoryIdentity;
            _databaseIoPath = authority.DatabasePath;
            IndexLeaseAcquireResult leaseResult = IndexOwnershipLease.TryAcquireDetailed(
                _workspaceRoot, _dbPath, leaseIdentity, out _ownershipLease);
            if (leaseResult == IndexLeaseAcquireResult.Contended && OperatingSystem.IsWindows())
            {
                // A follower's liveness probe must briefly acquire-and-release the same mutexes
                // to prove that no writer remains. Do not mistake that millisecond-scale probe for
                // a durable writer: retry briefly after the first contender has fully unwound.
                StartupAfterLeaseContentionForTest?.Invoke();
                for (int retry = 0;
                     retry < 3 && leaseResult == IndexLeaseAcquireResult.Contended;
                     retry++)
                {
                    Thread.Sleep(25);
                    leaseResult = IndexOwnershipLease.TryAcquireDetailed(
                        _workspaceRoot, _dbPath, leaseIdentity, out _ownershipLease);
                }
            }
            if (leaseResult != IndexLeaseAcquireResult.Acquired)
            {
                if (leaseResult == IndexLeaseAcquireResult.Contended && OperatingSystem.IsWindows())
                {
                    if (!authority.TryAnchorReviewCoordinationFile(create: false))
                    {
                        authority.Dispose();
                        _directoryAuthority = null;
                        _authorityDirectoryIdentity = null;
                        _databaseIoPath = _dbPath;
                        _accessMode = UnavailableAccessMode;
                        _error = "index reader coordination is unavailable; restart after the writer is upgraded or restarted";
                        _state = "failed";
                        _log("Read-only follower refused: the active writer has not published a safe reader/rebuild coordination file.");
                        return;
                    }
                    // SQLite WAL supports concurrent committed readers. A contending Phoenix is a
                    // follower: retain only the no-follow directory authority and open short-lived,
                    // nonpooled read-only connections. It never starts a store, pump, or watcher.
                    _accessMode = FollowerAccessMode;
                    _followerWriterPresent = true; // the contended mutex is direct owner evidence
                    Volatile.Write(ref _nextFollowerWriterProbe,
                        Environment.TickCount64 + 1000);
                    _error = FollowerIndexUnavailable;
                    _state = "failed";
                    if (TryRefreshFollowerMetadata(force: true))
                    {
                        _log(forceRebuild
                            ? "Index rebuild requested, but another Phoenix owns the writer lease; attached as a read-only follower instead."
                            : "Another Phoenix owns the writer lease; attached as a read-only follower.");
                    }
                    else
                    {
                        _log("Read-only follower is waiting for the writer to publish a safe compatible index.");
                    }
                    return;
                }

                authority.Dispose();
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _accessMode = UnavailableAccessMode;
                _error = leaseResult == IndexLeaseAcquireResult.Contended
                    ? "another phoenix process owns this index"
                    : "index writer ownership could not be acquired safely (see server log)";
                _state = "failed";
                _log(leaseResult == IndexLeaseAcquireResult.Contended
                    ? "Index startup refused: another Phoenix process owns this index."
                    : "Index startup refused: writer ownership coordination failed.");
                return;
            }
            if (!authority.TryGetLeaseIdentity(out IndexLeaseIdentity? afterLease) ||
                afterLease != leaseIdentity)
            {
                _ownershipLease!.Dispose();
                _ownershipLease = null;
                authority.Dispose();
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _accessMode = UnavailableAccessMode;
                _error = "index destination changed during ownership acquisition";
                _state = "failed";
                _log("Index startup refused: index destination changed during ownership acquisition.");
                return;
            }
            if (!authority.TryAnchorReviewCoordinationFile(create: true))
            {
                _ownershipLease!.Dispose();
                _ownershipLease = null;
                authority.Dispose();
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _accessMode = UnavailableAccessMode;
                _error = "index reader coordination could not be initialized safely";
                _state = "failed";
                _log("Index startup refused: reader/rebuild coordination could not be initialized safely.");
                return;
            }
            _accessMode = WriterAccessMode;
            // Publish both tasks while holding the same lock Dispose uses. Dispose can therefore
            // never release the lease between its acquisition and publication of the workers.
            _pump = Task.Run(PumpRefreshesAsync);

            _startTask = Task.Run(() =>
            {
                try
                {
                    if (_disposed) return;
                    StartupAfterLeaseAcquiredForTest?.Invoke();
                    bool build = forceRebuild || !File.Exists(_databaseIoPath);
                    // x5ls.1.2: honest v1 build reason — which gate actually forced the build.
                    string buildReason = forceRebuild ? "explicit_full" : "startup_missing";
                    if (!build)
                    {
                        // Rebuild when the on-disk index predates the current schema/indexer format —
                        // otherwise a freshly deployed binary would query columns the old index lacks,
                        // or trust field values (accessibility, signatures) the old indexer got wrong.
                        try
                        {
                            using var check = new IndexStore(_databaseIoPath, createNew: false);
                            string? onDisk = check.GetMeta("schema_version");
                            if (!string.Equals(onDisk, IndexBuilder.SchemaVersion, StringComparison.Ordinal))
                            {
                                _log($"Index format stale (have {onDisk ?? "none"}, need {IndexBuilder.SchemaVersion}); rebuilding.");
                                build = true;
                                buildReason = "startup_incompatible";
                            }
                        }
                        catch (Exception ex)
                        {
                            _log($"Index open/version-check failed ({ex.Message}); rebuilding.");
                            build = true;
                            buildReason = "recovery";
                        }
                    }
                    if (build)
                    {
                        IndexReviewCoordinationAcquireResult coordination;
                        IndexReviewCoordinationLease? startupRebuildLease;
                        bool waitingLogged = false;
                        do
                        {
                            coordination = TryAcquireFullRebuildReviewLease(
                                out startupRebuildLease);
                            if (coordination != IndexReviewCoordinationAcquireResult.Contended)
                                break;
                            _error = FullRebuildDeferredForReaders;
                            _state = "building";
                            if (!waitingLogged)
                            {
                                waitingLogged = true;
                                _log("Startup rebuild is waiting for active cross-process index readers; " +
                                     "the rebuild remains pending.");
                            }
                            if (!_disposed) Thread.Sleep(50);
                        } while (!_disposed);
                        if (_disposed) return;
                        if (coordination != IndexReviewCoordinationAcquireResult.Acquired)
                        {
                            _error = "startup rebuild reader coordination failed";
                            _state = "failed";
                            _log("Startup rebuild refused: cross-process reader coordination failed safely.");
                            return;
                        }

                        using (startupRebuildLease)
                        {
                            _state = "building";
                            // Live progress for the building window (bead two, field-requested during the
                            // v5 monolith reindex): published before the build starts, cleared after —
                            // Health() surfaces it only while state == "building".
                            _buildProgress = new BuildProgress();
                            int activeAtBoundary;
                            lock (_reviewSnapshotGate)
                                activeAtBoundary = _activeReviewSnapshots;
                            FullRebuildDestructiveBoundaryForTest?.Invoke(activeAtBoundary);
                            _log($"Building index for {_workspaceRoot} ...");
                            var startupBuildProgress = _buildProgress; // review B6: finally-safe local
                            var (buildId, progressTimer) = // x5ls.1.2
                                BeginBuildTelemetry(buildReason, startupBuildProgress);
                            try
                            {
                                var buildResult = IndexBuilder.BuildOwned(_workspaceRoot,
                                    _databaseIoPath, _log, startupBuildProgress);
                                _log($"Index built: {buildResult.CsFiles} C# + {buildResult.FsFiles} F# files, " +
                                     $"{buildResult.Symbols} symbols in " +
                                     $"{buildResult.TotalTime.TotalSeconds:F0}s");
                                // Review B1: drain the ticker BEFORE the terminal frame — a
                                // progress frame sequenced after completed regresses portals.
                                DrainDisposeBuildTimer(progressTimer);
                                EmitBuildCompleted(buildId, startupBuildProgress,
                                    (long)buildResult.TotalTime.TotalMilliseconds);
                            }
                            catch
                            {
                                DrainDisposeBuildTimer(progressTimer);
                                EmitBuildFailed(buildId, startupBuildProgress);
                                throw; // the startup catch owns state/error, unchanged
                            }
                            finally
                            {
                                DrainDisposeBuildTimer(progressTimer); // idempotent
                                _buildProgress = null;
                            }
                            FullRebuildCompletedForTest?.Invoke();
                        }
                    }

                    var store = new IndexStore(_databaseIoPath, createNew: false);
                    CacheMeta(store);              // read meta before publishing the store

                    // If Dispose ran while we were building/opening, don't publish or start the
                    // watcher — clean up the store we just opened and leave the manager stopped.
                    if (_disposed)
                    {
                        store.Dispose();
                        return;
                    }

                    _store = store;
                    _error = null;   // review F2 parity with FullRebuildInPump: recovery via a
                                     // re-entered Start is a DESIGNED failed->ready transition — a
                                     // healthy index must not keep reporting the pre-recovery refusal
                    _state = "ready";
                    StartWatcher();
                    _log(build
                        ? "Fresh index opened; running post-build freshness sweep ..."
                        : "Existing index opened; running startup freshness sweep ...");
                    // Always sweep after the watcher is attached. A full build deliberately commits
                    // its verified structural snapshot before the long C# parse; edits made between
                    // that commit and watcher attachment would otherwise be permanently missed.
                    _refreshQueue.Writer.TryWrite(new RefreshRequest(null, Reason: "full_sweep")); // detect-all
                    InitGitTracking(); // record/reconcile the git commit, then watch HEAD
                }
                catch (Exception ex)
                {
                    // Client-visible error carries the exception TYPE only (9vw): ex.Message can embed
                    // absolute filesystem paths, account names, or SQLite connection details — internals
                    // that don't belong in a tool response. The full exception goes to the server log.
                    _error = $"{ex.GetType().Name} during index startup (see server log)";
                    _state = "failed";
                    _log($"Index startup failed: {ex}");
                }
                finally
                {
                    _startupComplete.TrySetResult(true);
                }
            });
        }
    }

    /// <summary>Reads immutable-after-build index metadata into cached fields. Must be
    /// called on the thread that owns <paramref name="store"/>, before it is published.</summary>
    private void CacheMeta(IndexStore store)
    {
        _indexVersion = store.GetMeta("index_version");
        _indexedAtUtc = store.GetMeta("indexed_at_utc");
        _lastRefreshUtc ??= store.GetMeta("last_refresh_utc");
        _indexedCommit ??= store.GetMeta("indexed_commit");
        _indexedBranch ??= store.GetMeta("indexed_branch");
    }

    private FollowerPublication CurrentFollowerPublication =>
        Volatile.Read(ref _followerPublication);

    internal IndexMetadataSnapshot? FollowerMetadataForTest =>
        CurrentFollowerPublication.Metadata;

    private void PublishFollowerReady(IndexMetadataSnapshot metadata)
    {
        // One reference swap publishes the complete SQLite metadata tuple plus its readiness.
        // Health can therefore observe either the previous committed epoch or this one, never a
        // mixture assembled from independently written fields.
        FollowerMetadataBeforePublishForTest?.Invoke(metadata);
        Volatile.Write(ref _followerPublication,
            new FollowerPublication(metadata, true, "ready", null));
    }

    private bool IsCompatibleFollowerMetadata(IndexMetadataSnapshot metadata) =>
        string.Equals(metadata.SchemaVersion, IndexBuilder.SchemaVersion,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(metadata.IndexVersion) &&
        !string.IsNullOrWhiteSpace(metadata.IndexedAtUtc) &&
        metadata.WorkspaceRoot is { Length: > 0 } storedRoot &&
        CodeNav.Core.WorkspacePaths.FullPathsEqual(storedRoot, _workspaceRoot);

    private bool HasActiveFollowerWriter()
    {
        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextFollowerWriterProbe))
            return _followerWriterPresent;
        if (Interlocked.CompareExchange(ref _followerWriterProbeActive, 1, 0) != 0)
            return _followerWriterPresent;
        try
        {
            bool held = IndexOwnershipLease.IsHeld(_workspaceRoot, _dbPath);
            _followerWriterPresent = held;
            Volatile.Write(ref _nextFollowerWriterProbe, now + 1000);
            return held;
        }
        finally
        {
            Volatile.Write(ref _followerWriterProbeActive, 0);
        }
    }

    /// <summary>Refreshes follower health from a read-only, nonpooled connection. This method never
    /// repairs or creates an index. A transient writer replacement makes the follower unavailable
    /// for this call; a later call retries and recovers after the writer publishes a compatible DB.</summary>
    private bool TryRefreshFollowerMetadata(bool force)
    {
        if (!IsFollower || _disposed) return false;
        long now = Environment.TickCount64;
        if (!force && now < Volatile.Read(ref _nextFollowerMetadataRefresh))
            return CurrentFollowerPublication.Readable;
        if (Interlocked.CompareExchange(ref _followerMetadataRefreshActive, 1, 0) != 0)
            return CurrentFollowerPublication.Readable;

        try
        {
            Volatile.Write(ref _nextFollowerMetadataRefresh, now + 250);
            lock (_followerMetadataGate)
            {
                if (!HasActiveFollowerWriter())
                    return PublishFollowerUnavailable(FollowerWriterUnavailable);
                if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? before, out _) ||
                    before?.DatabaseIdentity is null)
                    return PublishFollowerUnavailable();

                using var queries = new IndexQueries(_databaseIoPath, pinReadSnapshot: false,
                    pooling: false);
                IndexMetadataSnapshot metadata = queries.ReadMetadata();
                if (!IsCompatibleFollowerMetadata(metadata))
                    return PublishFollowerUnavailable();

                if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? after, out _) ||
                    after != before)
                    return PublishFollowerUnavailable();

                PublishFollowerReady(metadata);
                if (_gitDir is null && GitInfo.GitAvailable)
                    _gitDir = GitInfo.ResolveGitDir(_workspaceRoot);
                return true;
            }
        }
        catch (Exception)
        {
            lock (_followerMetadataGate)
                return PublishFollowerUnavailable();
        }
        finally
        {
            Volatile.Write(ref _followerMetadataRefreshActive, 0);
        }
    }

    private bool PublishFollowerUnavailable(string? error = null)
    {
        FollowerPublication previous = CurrentFollowerPublication;
        Volatile.Write(ref _followerPublication,
            new FollowerPublication(previous.Metadata, false, "failed",
                error ?? FollowerIndexUnavailable));
        return false;
    }

    private void StartWatcher()
    {
        lock (_disposeLock)
        {
            if (_disposed) return; // Dispose already ran — don't publish a watcher it can't reach
            _watcher = new WorkspaceWatcher(
                _workspaceRoot,
                batch => _refreshQueue.Writer.TryWrite(new RefreshRequest(batch, Reason: "watcher_batch")),
                () => _refreshQueue.Writer.TryWrite(new RefreshRequest(null, Reason: "full_sweep"))); // overflow → detect-all sweep
        }
    }

    /// <summary>
    /// Wires git-aware refresh: records the current commit (or reconciles a diff if HEAD moved
    /// while the server was down), then watches for future HEAD changes. Best-effort — a repo
    /// without git, or without a git CLI, simply keeps FSW-only behavior.
    /// </summary>
    private void InitGitTracking()
    {
        if (_disposed) return; // teardown began before we got here — skip the git shell-outs
        if (!GitInfo.GitAvailable)
        {
            // Say WHY the feature is off (h99): silently degrading to watcher-only made
            // "why doesn't the index follow my branch switches?" undiagnosable in the field.
            _log("git executable not found (searched PATH for git.exe/git.cmd/git.bat; " +
                 "set CODENAV_GIT_EXE to override) — git-aware refresh disabled, watcher-only mode.");
            return;
        }
        _gitDir = GitInfo.ResolveGitDir(_workspaceRoot);
        if (_gitDir is null) return;

        string? head = GitInfo.HeadCommit(_workspaceRoot);
        if (head is not null)
        {
            string? stored = _indexedCommit;
            if (stored is null)
            {
                // First git-aware run (or a pre-git index): the build/startup-sweep already
                // reflects the current tree, so just record the commit as the diff baseline.
                _refreshQueue.Writer.TryWrite(new RefreshRequest(Array.Empty<string>(), head, Reason: "git_head"));
            }
            else if (!string.Equals(stored, head, StringComparison.OrdinalIgnoreCase))
            {
                _log($"Git HEAD moved while stopped: {Short(stored)} -> {Short(head)}; reconciling.");
                EnqueueGitReconcile(stored, head);
            }
        }

        lock (_disposeLock)
        {
            if (_disposed) return; // Dispose already ran — don't publish a watcher it can't reach
            _gitWatcher = new GitWatcher(_gitDir, () => OnGitHeadMaybeChanged());
        }
    }

    // 17zd: bounded retry for a git signal whose HEAD is transiently unresolvable — the
    // logs/-created event of a repo's FIRST commit fires while `git commit` is still finalizing
    // (refs/heads/* not yet written), and under heavy load the 400ms debounce elapses inside
    // that window (or a starved git.exe spawn fails). Swallowing that signal loses the first
    // commit PERMANENTLY: it is the only top-level event the commit produces, and the reflog
    // append lands before the late-attached logs watcher is live.
    private const int GitHeadRetryAttempts = 5;
    private int _gitHeadRetriesLeft = GitHeadRetryAttempts;
    private System.Threading.Timer? _gitHeadRetry;

    private void ScheduleGitHeadRetry()
    {
        if (_disposed) return;
        if (Interlocked.Decrement(ref _gitHeadRetriesLeft) < 0)
        {
            _log("Git HEAD unresolvable after retries — waiting for the next git signal.");
            return;
        }
        try
        {
            (_gitHeadRetry ??= new System.Threading.Timer(
                    _ => OnGitHeadMaybeChanged(fromRetry: true),
                    null, Timeout.Infinite, Timeout.Infinite))
                .Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Review (17zd): Dispose can win the race between the _disposed check above and this
            // Change — the retry chain simply ends with the manager instead of crashing the
            // timer thread.
        }
    }

    /// <summary>Debounced GitWatcher callback: if HEAD actually moved, reconcile the diff.
    /// Review (17zd): every WATCHER-originated signal grants a fresh retry budget; only the
    /// retry timer's own re-checks spend it. The old refill-on-resolvable-HEAD variant was
    /// self-defeating in the commit-less repo this exists for — HEAD is null by definition
    /// there, so any pre-first-commit signal (fetch, branch creation) burned the budget
    /// permanently and the eventual first commit was lost exactly as before the fix.</summary>
    private void OnGitHeadMaybeChanged(bool fromRetry = false)
    {
        if (_disposed) return;
        if (!fromRetry) _gitHeadRetriesLeft = GitHeadRetryAttempts;
        string? head = GitInfo.HeadCommit(_workspaceRoot);
        if (head is null)
        {
            ScheduleGitHeadRetry(); // 17zd: transient — do not swallow the only first-commit signal
            return;
        }
        string? current = _indexedCommit;
        if (current is null)
        {
            // 17zd-b: this branch was SILENT — a first-commit signal that subsequently died in
            // the pump (store-null skip, or the catch-all discarding RecordCommit) left no trace
            // to diagnose. Log the handoff; the pump logs the completed record.
            _log($"Git baseline signal: queueing first-commit record {Short(head)}.");
            _refreshQueue.Writer.TryWrite(new RefreshRequest(Array.Empty<string>(), head, Reason: "git_head"));
            return;
        }
        if (string.Equals(current, head, StringComparison.OrdinalIgnoreCase)) return; // spurious ref churn
        _log($"Git HEAD changed: {Short(current)} -> {Short(head)}; reconciling.");
        EnqueueGitReconcile(current, head);
    }

    /// <summary>Diff-scope the reconcile from <paramref name="from"/> to <paramref name="to"/>;
    /// fall back to a full sweep when the diff is unavailable or too large.</summary>
    private void EnqueueGitReconcile(string from, string to)
    {
        var changed = GitInfo.ChangedFiles(_workspaceRoot, from, to);
        if (changed is null || changed.Count > GitDiffCap)
        {
            _refreshQueue.Writer.TryWrite(new RefreshRequest(null, to, Reason: "git_head")); // full sweep, then record `to`
        }
        else
        {
            _refreshQueue.Writer.TryWrite(new RefreshRequest(changed, to, Reason: "git_head"));
        }
    }

    private static string Short(string commit) => commit.Length >= 8 ? commit[..8] : commit;

    private IndexReviewCoordinationAcquireResult TryAcquireFullRebuildReviewLease(
        out IndexReviewCoordinationLease? lease)
    {
        lease = null;
        if (!OperatingSystem.IsWindows())
            return IndexReviewCoordinationAcquireResult.Acquired;
        if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? before, out _))
            return IndexReviewCoordinationAcquireResult.Failed;
        if (before?.DatabaseIdentity is null)
            return IndexReviewCoordinationAcquireResult.Acquired;

        IndexReviewCoordinationAcquireResult result =
            IndexReviewCoordinationLease.TryAcquireExclusive(_directoryAuthority!,
                FullRebuildReviewWaitTimeoutForTest,
                FullRebuildWaitingForReviewSnapshotsForTest, out lease);
        if (result != IndexReviewCoordinationAcquireResult.Acquired) return result;

        if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? after, out _) ||
            after != before)
        {
            lease!.Dispose();
            lease = null;
            return IndexReviewCoordinationAcquireResult.Failed;
        }
        return IndexReviewCoordinationAcquireResult.Acquired;
    }

    private async Task PumpRefreshesAsync()
    {
        await foreach (var req in _refreshQueue.Reader.ReadAllAsync())
        {
            RefreshRequestDequeuedForTest?.Invoke();
            await _startupComplete.Task.ConfigureAwait(false);
            RefreshRequestPassedStartupBarrierForTest?.Invoke();
            if (req.FullRebuild)
            {
                IndexReviewCoordinationAcquireResult coordination =
                    TryAcquireFullRebuildReviewLease(out IndexReviewCoordinationLease? rebuildLease);
                if (coordination != IndexReviewCoordinationAcquireResult.Acquired)
                {
                    if (coordination == IndexReviewCoordinationAcquireResult.Contended)
                    {
                        _error = FullRebuildDeferredForReaders;
                        _log("Full rebuild is waiting for active cross-process index readers; " +
                             "the queued rebuild remains pending.");
                        if (!_disposed) _refreshQueue.Writer.TryWrite(req);
                        continue;
                    }
                    _error = "full rebuild reader coordination failed";
                    _state = "failed";
                    _log("Full rebuild reader coordination failed safely; the queued rebuild " +
                         "remains pending and will be retried.");
                    if (!_disposed)
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        if (!_disposed) _refreshQueue.Writer.TryWrite(req);
                    }
                    continue;
                }
                using (rebuildLease)
                {
                    BeginIndexMutation();
                    try
                    {
                        _noActiveReviewSnapshots.Wait();
                        int activeAtBoundary;
                        lock (_reviewSnapshotGate) activeAtBoundary = _activeReviewSnapshots;
                        FullRebuildDestructiveBoundaryForTest?.Invoke(activeAtBoundary);
                        FullRebuildInPump();
                        FullRebuildCompletedForTest?.Invoke();
                    }
                    finally { EndIndexMutation(); }
                }
                continue;
            }
            if (_store is null) continue;
            string previous = _state;
            // x5ls.1.2: one outcome frame per refresh batch. The reason comes from the
            // PRODUCER's explicit label (review B2: shape-derivation mislabeled tool requests
            // as watcher batches and git fallback sweeps as plain sweeps) — the shape mapping
            // below is only a fallback for unlabeled future sites, not sanctioned semantics.
            string refreshId = Guid.NewGuid().ToString();
            string refreshReason = req.Reason ?? (req.Paths is null ? "full_sweep"
                : req.RecordCommit is not null ? "git_head" : "watcher_batch");
            var refreshWall = System.Diagnostics.Stopwatch.StartNew(); // review B3: failures report MEASURED elapsed
            BeginIndexMutation();
            try
            {
                _state = "refreshing";
                string? recordBranch = req.RecordCommit is null
                    ? null
                    : GitInfo.HeadBranch(_workspaceRoot);
                var result = DeltaRefresher.Refresh(_store, _workspaceRoot, req.Paths, _log,
                    recordCommit: req.RecordCommit, recordBranch: recordBranch);
                // z4c: count what was ACTUALLY applied (the refresh result), not what was
                // requested — a sweep request has no path count, and hash-identical paths are
                // rightly skipped without being "processed".
                Interlocked.Add(ref _pendingProcessed, result.AddedFiles + result.ChangedFiles + result.DeletedFiles);
                _lastRefreshUtc = result.RefreshedAtUtc ?? DateTime.UtcNow.ToString("O");
                if (result.AddedFiles + result.ChangedFiles + result.DeletedFiles > 0)
                {
                    _log($"Delta refresh: +{result.AddedFiles} ~{result.ChangedFiles} -{result.DeletedFiles} " +
                         $"(projects rebuilt: {result.ProjectsRefreshed}) in {result.Elapsed.TotalMilliseconds:F0}ms");
                }
                // Record the reflected commit only after a successful reconcile — so the diff
                // baseline never advances past what the index actually contains.
                if (req.RecordCommit is { } commit)
                {
                    _indexedCommit = commit;
                    _indexedBranch = recordBranch ?? _indexedBranch;
                    _log($"Git baseline recorded: {Short(commit)}."); // 17zd-b: close the loop visibly
                }
                _state = "ready";
                EmitRefreshSnapshot(refreshId, refreshReason, "completed", // x5ls.1.2
                    result.AddedFiles + result.ChangedFiles + result.DeletedFiles,
                    (long)result.Elapsed.TotalMilliseconds, errorCode: null);
            }
            catch (Exception ex)
            {
                // Type-name only, like the startup path (9vw) — no ex.Message internals to clients.
                _error = $"{ex.GetType().Name} during delta refresh (see server log)";
                _state = previous == "ready" ? "ready" : previous;
                _log($"Delta refresh failed: {ex}");
                // batchProcessed 0 is TRUE, not fabricated: DeltaRefresher runs one
                // transaction, so a throw rolls back to zero applied (review B3).
                EmitRefreshSnapshot(refreshId, refreshReason, "failed", // x5ls.1.2
                    batchProcessed: 0, elapsedMs: refreshWall.ElapsedMilliseconds,
                    errorCode: "refresh_failed");
            }
            finally
            {
                EndIndexMutation();
            }
        }
    }

    private void BeginIndexMutation()
    {
        _stableIndexEpoch.Reset();
        long epoch = Interlocked.Increment(ref _refreshEpoch);
        if ((epoch & 1) == 0)
            throw new InvalidOperationException("Index refresh epoch entered an invalid state.");
    }

    private void EndIndexMutation()
    {
        long epoch = Interlocked.Increment(ref _refreshEpoch);
        if ((epoch & 1) != 0)
            throw new InvalidOperationException("Index refresh epoch entered an invalid state.");
        _stableIndexEpoch.Set();
    }

    /// <summary>kae: scoped pool release for every path this manager's readers may have pooled —
    /// the live IO path and, when the directory authority redirected it, the original logical
    /// path. Replaces process-global ClearAllPools, which could invalidate an unrelated
    /// database's pooled reader at the rent boundary (rqek). The OrdinalIgnoreCase guard only
    /// DEDUPES the common identical-path case; a genuinely case-variant pair would clear one
    /// spelling — acceptable because pool keys are GetFullPath-canonical and no caller opens
    /// with a case-variant of either field (see IndexQueries.ReadConnectionString).</summary>
    private void ClearOwnedDatabasePools()
    {
        IndexQueries.ClearPoolsFor(_databaseIoPath);
        if (!string.Equals(_databaseIoPath, _dbPath, StringComparison.OrdinalIgnoreCase))
            IndexQueries.ClearPoolsFor(_dbPath);
    }

    /// <summary>The pump-side rebuild-from-scratch (tky): runs ON the pump thread so no delta
    /// batch can interleave with the teardown. Discards the store, deletes the db, rebuilds with
    /// live progress (the same building-state surface as a first run), reopens, and re-records
    /// the git baseline (the fresh build reflects HEAD, and the old indexed_commit died with the
    /// db). Failure latches state 'failed' with the sanitized error — from which this same hatch
    /// remains the recovery path.</summary>
    private void FullRebuildInPump()
    {
        _log("Full rebuild requested (refresh_index force:'full') — rebuilding the index from scratch.");
        _state = "building";
        // x5ls.1.2 review B5: the pre-build teardown (store dispose, pool clears, bounded
        // db-delete retries) is NOT building — the build lifecycle (and the phase clock that
        // feeds phaseDurations) starts at BuildOwned. During teardown, state is 'building'
        // with no progress object: Health() honestly shows no progress rather than a
        // "scanning" phase silently absorbing teardown time.
        _buildProgress = null;
        BuildProgress? rebuildProgress = null;
        string? buildId = null;
        System.Threading.Timer? progressTimer = null;
        bool buildCompleted = false;
        try
        {
            _store?.Dispose();
            _store = null;
            ClearOwnedDatabasePools();
            EnsureDatabaseAuthority();
            // Delete with a bounded retry: in-flight tool calls hold short-lived READ connections
            // (an open handle blocks Windows File.Delete); pooled ones are cleared, live ones
            // release within milliseconds when their `using` scope ends. A persistently held file
            // fails honestly after ~3s (state 'failed', from which this hatch remains the remedy).
            // SIDECARS FIRST (review F3): main-db-then-sidecars left a crash window where a stale
            // -wal survived into a fresh build (IndexStore's createNew cleanup only ran when the
            // MAIN file existed) — the classic SQLite stale-WAL-replay footgun.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    ClearOwnedDatabasePools();
                    foreach (var sidecar in new[]
                             { _databaseIoPath + "-wal", _databaseIoPath + "-shm",
                               _databaseIoPath + "-journal" })
                    {
                        if (File.Exists(sidecar)) File.Delete(sidecar);
                    }
                    if (File.Exists(_databaseIoPath)) File.Delete(_databaseIoPath);
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(300);
                }
            }

            rebuildProgress = new BuildProgress();
            _buildProgress = rebuildProgress;
            (buildId, progressTimer) = BeginBuildTelemetry("explicit_full", rebuildProgress); // x5ls.1.2
            var result = IndexBuilder.BuildOwned(_workspaceRoot, _databaseIoPath, _log, rebuildProgress);
            _log($"Full rebuild done: {result.CsFiles} C# + {result.FsFiles} F# files, " +
                 $"{result.Symbols} symbols in {result.TotalTime.TotalSeconds:F0}s");
            // Review B1: drain the ticker BEFORE the terminal frame (no progress after completed).
            DrainDisposeBuildTimer(progressTimer);
            EmitBuildCompleted(buildId, rebuildProgress, (long)result.TotalTime.TotalMilliseconds);
            buildCompleted = true;

            var store = new IndexStore(_databaseIoPath, createNew: false);
            // Reset cached meta BEFORE CacheMeta: its ??= semantics would otherwise resurrect
            // values from the deleted index (stale indexed_commit on a fresh db).
            _lastRefreshUtc = null;
            _indexedCommit = null;
            _indexedBranch = null;
            CacheMeta(store);
            _store = store;
            _error = null;   // review F2: recovery is a DESIGNED failed->ready transition — a
                             // healthy index must not keep reporting the pre-recovery failure
            _state = "ready";

            // Review F1: recovery FROM FAILED never had a watcher or git tracking — startup died
            // before attaching them, and the recovered index silently went stale (PendingChanges
            // read 0 with no watcher at all; branch switches never reconciled). Attach whatever
            // is missing; null-guards keep the from-READY rebuild from double-attaching.
            if (_watcher is null) StartWatcher();
            // Recovery can start from a failed state with no watcher. Queue a detect-all pass
            // after attachment so edits during BuildOwned's pre-watcher interval converge too.
            _refreshQueue.Writer.TryWrite(new RefreshRequest(null, Reason: "full_sweep"));
            if (_gitWatcher is null)
            {
                // Resolves _gitDir, queues the baseline commit record, attaches the GitWatcher —
                // the same first-run path Start uses (queued behind us on this very pump).
                InitGitTracking();
            }
            else if (_gitDir is not null && GitInfo.HeadCommit(_workspaceRoot) is { } head)
            {
                // From-READY rebuild: tracking is live; re-record the baseline immediately (the
                // fresh build reflects HEAD and the old indexed_commit died with the db).
                store.SetMeta("indexed_commit", head);
                _indexedCommit = head;
                if (GitInfo.HeadBranch(_workspaceRoot) is { } branch)
                {
                    store.SetMeta("indexed_branch", branch);
                    _indexedBranch = branch;
                }
            }
        }
        catch (Exception ex)
        {
            _error = $"{ex.GetType().Name} during full rebuild (see server log)";
            _state = "failed";
            _log($"Full rebuild failed: {ex}");
        }
        finally
        {
            if (progressTimer is not null) DrainDisposeBuildTimer(progressTimer); // idempotent
            // Post-build steps (reopen/watcher/git) can fail AFTER a successful BuildOwned —
            // only a build that never emitted completed reports failed. A TEARDOWN failure
            // (buildId null) emits nothing: the build lifecycle never started (review B5);
            // state 'failed' surfaces via instance.snapshot.
            if (buildId is not null && !buildCompleted) EmitBuildFailed(buildId, rebuildProgress!);
            _buildProgress = null;
        }
    }

    /// <summary>Queues a manual refresh (targeted paths, or full detection sweep when null).</summary>
    public bool RequestRefresh(IReadOnlyCollection<string>? paths = null)
    {
        if (!IsWriter || _disposed) return false;
        return _refreshQueue.Writer.TryWrite(new RefreshRequest(paths, Reason: "explicit"));
    }

    /// <summary>Queues a REBUILD-FROM-SCRATCH (tky): delete the db, run a full build, reopen.
    /// Serialized on the refresh pump like every other index mutation, so it can never race a
    /// delta batch. This is the in-band recovery hatch for a corrupt/failed index — including
    /// from state 'failed', where the pump is idle and the store may never have opened. When
    /// startup itself was REFUSED (destination authority or ownership), no pump exists to drain
    /// the queue — the hatch re-runs the full startup acquisition instead, so a transient
    /// blocker (AV lock, stale owner) stays recoverable in-band; a still-broken destination
    /// fails closed with the same sanitized shape.</summary>
    public bool RequestFullRebuild()
    {
        if (IsFollower || _disposed) return false;
        lock (_disposeLock)
        {
            if (!_disposed && _pump is null && _startTask is null)
            {
                // Start returned before publishing the pump (authority/lease refusal): an
                // enqueued rebuild would never run. Re-enter the sole acquisition site.
                Start(forceRebuild: true);
                return IsWriter;
            }
        }
        return IsWriter && _refreshQueue.Writer.TryWrite(
            new RefreshRequest(null, FullRebuild: true));
    }

    public bool IsQueryable
    {
        get
        {
            if (IsFollower && !TryRefreshFollowerMetadata(force: false)) return false;
            string state = State;
            return state is "ready" or "refreshing" &&
                   TryGetSafeDatabaseStatus(out IndexLeaseIdentity? current, out _) &&
                   current?.DatabaseIdentity is not null;
        }
    }

    public IndexQueries OpenQueries()
    {
        if (!IsFollower)
        {
            EnsureDatabaseAuthority();
            return new IndexQueries(_databaseIoPath);
        }

        FollowerMetadataBeforeGateForTest?.Invoke();
        lock (_followerMetadataGate)
        {
            IndexQueries? queries = null;
            try
            {
                EnsureDatabaseAuthority();
                if (!HasActiveFollowerWriter())
                    throw new IOException(FollowerWriterUnavailable);
                if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? before, out _) ||
                    before?.DatabaseIdentity is null)
                    throw new IOException(FollowerIndexUnavailable);
                queries = new IndexQueries(_databaseIoPath, pinReadSnapshot: false,
                    pooling: false);
                IndexMetadataSnapshot metadata = queries.ReadMetadata();
                if (!IsCompatibleFollowerMetadata(metadata) ||
                    !TryGetSafeDatabaseStatus(out IndexLeaseIdentity? after, out _) ||
                    after != before)
                    throw new IOException(FollowerIndexUnavailable);
                PublishFollowerReady(metadata);
                return queries;
            }
            catch
            {
                queries?.Dispose();
                PublishFollowerUnavailable(HasActiveFollowerWriter()
                    ? FollowerIndexUnavailable
                    : FollowerWriterUnavailable);
                throw;
            }
        }
    }

    /// <summary>
    /// Opens a read transaction whose rows and health metadata describe the same stable refresh
    /// epoch. Returns null when a refresh overlaps snapshot creation; callers should fail closed
    /// and invite a retry instead of combining evidence from different commits.
    /// </summary>
    public IndexReadSnapshot? TryOpenReviewSnapshot(CancellationToken cancellationToken = default)
    {
        // Ordinary delta refreshes are short. Give the serialized pump a bounded chance to reach
        // its next committed epoch so review_pack does not fail spuriously just after a caller's
        // own refresh; a long rebuild still returns the bounded retry response.
        if (!_stableIndexEpoch.Wait(TimeSpan.FromSeconds(2), cancellationToken)) return null;

        if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? databaseBefore,
                out long databaseBytes) || databaseBefore?.DatabaseIdentity is null)
            return null;

        IndexReviewCoordinationLease? coordinationLease = null;
        if (OperatingSystem.IsWindows() &&
            IndexReviewCoordinationLease.TryAcquireReader(_directoryAuthority!,
                TimeSpan.FromSeconds(2), out coordinationLease, cancellationToken) !=
            IndexReviewCoordinationAcquireResult.Acquired)
            return null;

        long before = 0;
        bool registered = false;
        IndexQueries? queries = null;
        bool transferred = false;
        try
        {
            if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? databaseAfterGate, out _) ||
                databaseAfterGate != databaseBefore)
                return null;

            lock (_reviewSnapshotGate)
            {
                before = Volatile.Read(ref _refreshEpoch);
                if ((before & 1) != 0 || !IsQueryable) return null;
                _activeReviewSnapshots++;
                registered = true;
                if (_activeReviewSnapshots == 1) _noActiveReviewSnapshots.Reset();
            }

            EnsureDatabaseAuthority();
            queries = new IndexQueries(_databaseIoPath, pinReadSnapshot: true,
                ReviewSnapshotAfterQueryForTest, pooling: IsWriter);
            IndexHealth health;
            bool followerStable = true;
            if (IsFollower)
            {
                IndexMetadataSnapshot metadata = queries.ReadMetadata();
                followerStable = IsCompatibleFollowerMetadata(metadata) &&
                    TryGetSafeDatabaseStatus(out IndexLeaseIdentity? followerDatabaseAfter, out _) &&
                    followerDatabaseAfter == databaseBefore;
                health = FollowerHealth(metadata, databaseBytes);
            }
            else
            {
                health = Health();
            }
            long after = Volatile.Read(ref _refreshEpoch);
            if (followerStable && before == after && (after & 1) == 0 &&
                health.State == "ready")
            {
                IndexReviewCoordinationLease? transferredLease = coordinationLease;
                coordinationLease = null;
                var snapshot = new IndexReadSnapshot(queries, health, () =>
                {
                    try { ReleaseReviewSnapshot(); }
                    finally { transferredLease?.Dispose(); }
                });
                transferred = true;
                return snapshot;
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A full rebuild can replace the database between the epoch read and open. The epoch
            // check is the contract; the transient provider detail is intentionally not surfaced.
        }
        catch (IOException)
        {
            // Same race on hosts where database replacement manifests as an IO failure.
        }
        finally
        {
            if (!transferred)
            {
                try { queries?.Dispose(); }
                finally
                {
                    if (registered) ReleaseReviewSnapshot();
                    coordinationLease?.Dispose();
                }
            }
        }
        return null;
    }

    private void ReleaseReviewSnapshot()
    {
        lock (_reviewSnapshotGate)
        {
            if (--_activeReviewSnapshots == 0) _noActiveReviewSnapshots.Set();
        }
    }

    private IndexHealth FollowerHealth(IndexMetadataSnapshot metadata, long dbBytes) => new(
        "ready", metadata.IndexVersion, metadata.IndexedAtUtc, metadata.LastRefreshUtc,
        0, null, dbBytes, _workspaceRoot, _dbPath, metadata.IndexedCommit,
        metadata.IndexedBranch, null, 0, FollowerAccessMode);

    public IndexHealth Health()
    {
        if (IsFollower) _ = TryRefreshFollowerMetadata(force: false);
        // Reads cached fields plus one authority-gated no-follow metadata snapshot. Never inspect
        // the visible database path after its destination authority has changed; macOS reports a
        // conservative zero because it has no retained directory handle for an anchored size read.
        _ = TryGetSafeDatabaseStatus(out _, out long dbBytes);

        if (IsFollower)
        {
            FollowerPublication publication = CurrentFollowerPublication;
            IndexMetadataSnapshot? metadata = publication.Metadata;
            return new IndexHealth(
                publication.State, metadata?.IndexVersion, metadata?.IndexedAtUtc,
                metadata?.LastRefreshUtc, 0, publication.Error, dbBytes, _workspaceRoot,
                _dbPath, metadata?.IndexedCommit, metadata?.IndexedBranch, null, 0,
                FollowerAccessMode);
        }

        // Progress only while genuinely building — a background refresh must never show a
        // cold-build progress bar (field design note; refresh honesty is bead z4c).
        var bp = _buildProgress;
        return new IndexHealth(
            _state, _indexVersion, _indexedAtUtc, _lastRefreshUtc,
            _watcher?.PendingCount ?? 0, _error, dbBytes, _workspaceRoot, _dbPath,
            _indexedCommit, _indexedBranch,
            _state == "building" && bp is not null ? bp.Snapshot() : null,
            Interlocked.Read(ref _pendingProcessed), _accessMode);
    }

    /// <summary>Current git HEAD commit for the workspace, or null if not a git repo / git absent.
    /// A live call (shells out to git) — for repo_overview, not the per-response meta.</summary>
    public string? CurrentHeadCommit() => _gitDir is null ? null : GitInfo.HeadCommit(_workspaceRoot);

    /// <summary>HEAD commit with an honest status for repo_overview (field: a silent null after the
    /// hang guard fired was undiagnosable). "unavailable" = not a git repo / git absent / error;
    /// "timed_out" = the hang guard fired (git itself is slow, not a hang).</summary>
    public (string? Sha, string Status) CurrentHeadCommitEx() =>
        _gitDir is null ? (null, "unavailable") : GitInfo.HeadCommitEx(_workspaceRoot);

    public void Dispose()
    {
        lock (_disposeLock) { _disposed = true; } // block any in-flight watcher publication
        Telemetry.Dispose();                 // epuc.1: flush the bounded stream (2s cap)
        TelemetryIpc.Dispose();              // x5ls.1: stop the IPC producer (2s cap)
        _gitWatcher?.Dispose();              // stop git HEAD signals
        _gitHeadRetry?.Dispose();            // 17zd: stop the null-HEAD retry (callback checks _disposed)
        _watcher?.Dispose();                 // stop new events reaching the queue
        _refreshQueue.Writer.TryComplete();  // let the pump drain and exit its loop

        // Let the startup task settle first (it may still be opening the store), then wait
        // for the pump to actually stop using the store. Only dispose the store once both
        // have finished — otherwise leak it (the process is tearing down) rather than risk
        // a use-after-dispose on the single write connection.
        bool startDone = true, pumpDone = true;
        try { startDone = _startTask?.Wait(DisposeWaitTimeoutForTest) ?? true; } catch { /* faulted/cancelled */ }
        // The start task may have created the watchers after the first Dispose() calls above
        // saw null — tear those down too (Dispose is idempotent).
        _gitWatcher?.Dispose();
        _watcher?.Dispose();
        try { pumpDone = _pump?.Wait(DisposeWaitTimeoutForTest) ?? true; } catch { /* faulted/cancelled */ }

        if (startDone && pumpDone)
        {
            ReleaseOwnedResources();
        }
        else
        {
            _log("IndexManager.Dispose: background work still running; deferring index resource release until it stops.");
            Task start = _startTask ?? Task.CompletedTask;
            Task pump = _pump ?? Task.CompletedTask;
            _ = Task.WhenAll(start, pump).ContinueWith(
                _ => ReleaseOwnedResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void ReleaseOwnedResources()
    {
        lock (_resourceReleaseLock)
        {
            if (_ownedResourcesReleased) return;
            if (IsFollower)
            {
                try
                {
                    // Followers own no write connection, pool, pump, or mutex. Release only their
                    // retained no-follow directory authority; the writer lease belongs elsewhere.
                    _directoryAuthority?.Dispose();
                }
                catch (Exception ex)
                {
                    _log($"IndexManager follower cleanup failed: {ex.GetType().Name}");
                    return;
                }
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _ownedResourcesReleased = true;
                return;
            }
            try
            {
                _store?.Dispose();
                CleanupBeforePoolClearForTest?.Invoke();
                // Release this database's idle native SQLite handles before releasing the
                // cross-process lease. Otherwise a second Phoenix could legitimately acquire the
                // lease while this process still retained pooled WAL state for the same database.
                // kae: scoped — a global clear here could invalidate an unrelated database's
                // pooled reader mid-rent elsewhere in the process (rqek).
                ClearOwnedDatabasePools();
            }
            catch (Exception ex)
            {
                // Fail closed: a teardown failure may mean native DB/WAL handles still survive.
                // Retain the lease and authority; another Dispose call can retry safely.
                _log($"IndexManager cleanup retained ownership after SQLite teardown failed: {ex.GetType().Name}");
                return;
            }

            try
            {
                _directoryAuthority?.Dispose();
                _ownershipLease?.Dispose();
            }
            catch (Exception ex)
            {
                _log($"IndexManager cleanup retained ownership after authority release failed: {ex.GetType().Name}");
                return;
            }
            _store = null;
            _ownershipLease = null;
            _directoryAuthority = null;
            _authorityDirectoryIdentity = null;
            _databaseIoPath = _dbPath;
            _ownedResourcesReleased = true;
        }
    }
}
