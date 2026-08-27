using System.Diagnostics;
using System.Threading.Channels;

namespace CodeNav.Core.Indexing;

public enum IndexStartupFailureCause
{
    None,
    DestinationUnsafe,
    WriterLeaseContended,
    WriterAuthorityUnavailable,
    DestinationChanged,
    DestinationForeign,
    RebuildRequired,
    DestinationValidationFailed,
}

public sealed record IndexHealth(
    string State,               // missing | building | ready | refreshing | stale | failed
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
    string AccessMode = "writer",
    string? RefreshIncompleteReason = null,
    IReadOnlyList<string>? RefreshIncompletePaths = null,
    int RefreshIncompletePathCount = 0,
    bool RefreshIncompletePathCountIsLowerBound = false,
    string? StartupBuildReason = null,
    string? StartupPriorSchema = null);

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
    private const string WriterPublicationUnavailable =
        "index replacement is being published; wait for the rebuild to finish, then retry";
    private const int RefreshIncompletePathLimit = 32;
    internal const string RefreshIncompleteReasonMeta = "refresh_incomplete_reason";
    internal const string RefreshIncompletePathsMeta = "refresh_incomplete_paths";
    internal const string RefreshIncompletePathCountMeta = "refresh_incomplete_path_count";
    internal const string RefreshIncompletePathCountLowerBoundMeta =
        "refresh_incomplete_path_count_lower_bound";
    public const string RefreshSweepPendingCause = "refresh_sweep_pending";
    public const string RefreshInputUnavailableCause = "refresh_input_unavailable";
    public const string RefreshInputOversizedCause = "refresh_input_oversized";

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
        bool FullRebuild = false, string? Reason = null,
        TaskCompletionSource? CompletionForTest = null,
        bool TimerInitiatedRecovery = false,
        bool RevalidateRecordCommit = false,
        string? RecordBranch = null,
        bool RecordBranchKnown = false,
        bool ResolveGitPathsAtExecution = false,
        bool PublishRevalidatedGitSnapshot = false,
        long RecoveryGitSnapshotGeneration = 0);

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
    private IndexDestinationClaim? _destinationClaim;
    private IndexDirectoryAuthority? _directoryAuthority;
    private string? _authorityDirectoryIdentity;
    private bool _followerDestinationBound;
    private volatile bool _disposed;
    private volatile string _state = "missing";
    private volatile string? _error;
    // Immutable evidence for this manager's startup. The external reusable-index gate reads it
    // after readiness so an automatic schema/recovery rebuild cannot masquerade as ordinary
    // reuse merely because the replacement already matches the current baseline.
    private volatile string? _startupBuildReason;
    private volatile string? _startupPriorSchema;
    private int _startupFailureCause;
    private volatile string? _refreshIncompleteReason;
    private volatile string[]? _refreshIncompletePaths;
    private int _refreshIncompletePathCount;
    private int _refreshIncompletePathCountIsLowerBound;
    // In-memory reason and durable follower visibility are deliberately separate. A failed
    // initial marker write may leave the writer stale in memory, but must not let a later request
    // skip the publication gate merely because that non-durable reason is non-null.
    private int _refreshIncompletePersisted;
    private volatile string _accessMode = UnavailableAccessMode;
    private FollowerPublication _followerPublication =
        new(null, false, "failed", FollowerIndexUnavailable);
    private readonly object _followerMetadataGate = new();
    private long _nextFollowerMetadataRefresh;
    private int _followerMetadataRefreshActive;
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
    internal Action<IndexMetadataSnapshot>? FollowerMetadataAfterPublishForTest { get; set; }
    internal Action? FollowerMetadataBeforeGateForTest { get; set; }
    internal Action? FullRebuildWaitingForLocalSnapshotsForTest { get; set; }
    internal Action<int>? FullRebuildDestructiveBoundaryForTest { get; set; }
    internal Action? FullRebuildCompletedForTest { get; set; }
    internal Action? FullRebuildAfterTelemetryStartedForTest { get; set; }
    internal Action? FullRebuildBeforeAnchoredDestinationOpenForTest { get; set; }
    internal Action<string>? FullRebuildPrivateStageReadyForTest { get; set; }
    internal Action? FullRebuildPrivateStageCompletedForTest { get; set; }
    internal Action? FullRebuildBeforeStageInstallForTest { get; set; }
    internal Action? FullRebuildAfterStageInstallForTest { get; set; }
    internal Action? WriterQueryAfterRegistrationForTest { get; set; }
    internal int ActiveWriterQueriesForTest => Volatile.Read(ref _activeWriterQueries);
    internal Action? StartupPriorPublicationRestoreForTest { get; set; }
    internal TimeSpan FullRebuildPublicationTimeoutForTest { get; set; } =
        TimeSpan.FromMinutes(3);
    internal Action? RefreshRequestDequeuedForTest { get; set; }
    internal Action? RefreshRequestPassedStartupBarrierForTest { get; set; }
    internal Func<string, string, int, GitInfo.WorkspaceFileReadResult>?
        WorkspaceFileReaderForTest
    { get; set; }
    internal Func<GitInfo.HeadSnapshot>? GitHeadSnapshotForTest { get; set; }
    internal Action? ClearRefreshIncompleteBeforeCommitForTest { get; set; }
    internal Action<string>? RefreshIncompleteBeforeCommitForTest { get; set; }
    internal Action? RefreshInputFailureBeforeLatchForTest { get; set; }
    internal Action? StartupAfterLeaseAcquiredForTest { get; set; }
    internal Action? StartupAfterLeaseContentionForTest { get; set; }
    internal Action? CleanupBeforePoolClearForTest { get; set; }
    internal TimeSpan DisposeWaitTimeoutForTest { get; set; } = TimeSpan.FromSeconds(5);
    private readonly object _reviewSnapshotGate = new();
    private readonly ManualResetEventSlim _noActiveReviewSnapshots = new(initialState: true);
    private readonly ManualResetEventSlim _noActiveWriterQueries = new(initialState: true);
    private readonly ManualResetEventSlim _stableIndexEpoch = new(initialState: true);
    private int _activeReviewSnapshots;
    private int _activeWriterQueries;
    // Writer-side reads remain available while a full rebuild writes its private stage. The gate
    // closes only at publication, under _reviewSnapshotGate, so no new local query can barge
    // between the last readiness check and the destination-claim B boundary.
    private int _writerReadsAllowed = 1;
    private readonly object _resourceReleaseLock = new();
    private bool _ownedResourcesReleased;
    private int _serverInfoEmitted;

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
                errorCode = h.RefreshIncompleteReason ??
                    (h.Error is null ? null : "index_error"),
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
        EmitBuildProgressJsonl(buildId, reason, "started", progress);
        TelemetryIpc.Emit("index.build.started",
            ids => new { buildId, indexId = ids.IndexId, reason, phase = "scanning" },
            lifecycle: true);
        long lastJsonlMs = 0;
        string lastJsonlPhase = "scanning";
        object jsonlGate = new();
        progress.PhaseChangedForTelemetry = () =>
        {
            lock (jsonlGate)
            {
                IndexProgress current = progress.Snapshot();
                lastJsonlPhase = current.Phase;
                lastJsonlMs = current.ElapsedMs;
                EmitBuildProgressJsonl(buildId, reason, "running", progress);
            }
        };
        var timer = new System.Threading.Timer(timerState =>
        {
            try
            {
                // Atomic pair (review B-r2): label and in-phase elapsed from ONE lock scope,
                // so a phase transition can't pair the old label with the new phase's clock.
                // Captured BEFORE Snapshot (review B-r3 note): keeps phaseElapsedMs ≤ elapsedMs.
                var (phase, phaseElapsedMs) = progress.CurrentPhase();
                var s = progress.Snapshot();
                lock (jsonlGate)
                {
                    if (!string.Equals(phase, lastJsonlPhase, StringComparison.Ordinal)
                        || s.ElapsedMs - lastJsonlMs >= 1000)
                    {
                        lastJsonlPhase = phase;
                        lastJsonlMs = s.ElapsedMs;
                        EmitBuildProgressJsonl(buildId, reason, "running", progress);
                    }
                }
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

    private void EmitBuildProgressJsonl(
        string buildId,
        string reason,
        string state,
        BuildProgress progress,
        string? errorCode = null)
    {
        try
        {
            var (phase, phaseElapsedMs) = progress.CurrentPhase();
            IndexProgress snapshot = progress.Snapshot();
            Telemetry.Emit(new
            {
                e = "buildProgress",
                ts = DateTimeOffset.UtcNow,
                buildId,
                state,
                reason,
                accessMode = _accessMode,
                phase,
                phaseElapsedMs,
                elapsedMs = snapshot.ElapsedMs,
                filesDone = snapshot.FilesIndexed,
                filesTotal = snapshot.FilesTotal,
                filesSkipped = snapshot.FilesSkipped,
                projectsFailed = snapshot.ProjectsFailed,
                symbolsWritten = snapshot.SymbolsWritten,
                bytesRead = snapshot.BytesRead,
                filesPerSecond = snapshot.FilesPerSecond,
                estimatedRemainingMs = snapshot.EstimatedRemainingMs,
                errorCode
            });
        }
        catch
        {
            // Observability is fail-open: telemetry must never change build behavior.
        }
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

    private void EmitBuildCompleted(
        string buildId,
        string reason,
        BuildProgress progress,
        long durationMs)
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
        EmitBuildProgressJsonl(buildId, reason, "completed", progress);
    }

    private void EmitBuildFailed(
        string buildId,
        string reason,
        BuildProgress progress)
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
        EmitBuildProgressJsonl(
            buildId,
            reason,
            "failed",
            progress,
            errorCode: "index_build_failed");
    }

    private void EmitBuildCancelled(
        string buildId,
        string reason,
        BuildProgress progress)
    {
        EmitBuildProgressJsonl(
            buildId,
            reason,
            "cancelled",
            progress,
            errorCode: "index_build_cancelled");
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

    public void EmitServerInfo(Diagnostics.TelemetryServerInfo info)
    {
        if (Interlocked.Exchange(ref _serverInfoEmitted, 1) != 0)
            return;
        string[] featureIds = info.FeatureIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        Telemetry.Emit(new
        {
            e = "serverInfo",
            ts = DateTimeOffset.UtcNow,
            version = info.Version,
            buildStamp = info.BuildStamp,
            schemaVersion = info.SchemaVersion,
            featureIds,
            featureCount = info.FeatureIds.Count,
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            accessMode = _accessMode,
            processId = Environment.ProcessId
        });
    }

    public string WorkspaceRoot => _workspaceRoot;
    public string DbPath => _dbPath;
    public string AccessMode => _accessMode;
    public IndexStartupFailureCause StartupFailureCause =>
        (IndexStartupFailureCause)Volatile.Read(ref _startupFailureCause);
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

    private bool HasSafeLiveDatabaseAuthority() =>
        _directoryAuthority?.MatchesLiveDatabasePath(_dbPath) == true;

    private void EnsureLivePublicationAuthority()
    {
        EnsureDatabaseAuthority();
        if (!HasSafeLiveDatabaseAuthority())
            throw new IOException(
                "live index destination differs from the retained index authority");
        if (!HasSafeWorkspaceAuthority())
            throw new IOException(
                "live workspace differs from the retained workspace authority");
    }

    private void EnsureDatabaseAuthority()
    {
        if (!HasSafeDatabaseAuthority())
            throw new IOException("index destination authority is no longer safe");
    }

    private bool HasSafeWorkspaceAuthority() =>
        _ownershipLease is not null &&
        IndexOwnershipLease.ProbeWorkspaceIdentity(
            _workspaceRoot, out string? currentWorkspace) ==
            WorkspaceIdentityProbeResult.Found &&
        string.Equals(currentWorkspace, _ownershipLease.WorkspaceIdentity,
            StringComparison.Ordinal);

    private bool CanUseDirectFullRebuildFallback() =>
        HasSafeWorkspaceAuthority() &&
        !AnchoredIndexDestination.IsAnchoredPublicationRequired(
            _workspaceRoot, _workspaceRoot, _dbPath);

    private void ReapAbandonedPublicationArtifacts()
    {
        if (!AnchoredIndexDestination.IsAnchoredPublicationRequired(
                _workspaceRoot, _workspaceRoot, _dbPath))
            return;
        if (_ownershipLease is null || _destinationClaim is null ||
            !AnchoredIndexDestination.TryOpen(
                _workspaceRoot, _workspaceRoot, _dbPath,
                createIndexDirectory: false,
                out AnchoredIndexDestination? destination))
            throw new IOException(
                "anchored index destination could not be reopened for abandoned-stage cleanup");

        using (destination!)
        {
            EnsureStagedDestinationAuthority(destination!);
            if (!destination!.TryReapAbandonedPublicationArtifacts(
                    _ownershipLease, _destinationClaim, out int reaped,
                    out PublicationArtifactReapFailure reapFailure,
                    out int observedCandidates))
                throw new IOException(
                    AnchoredIndexDestination.DescribePublicationArtifactReapFailure(
                        reapFailure, observedCandidates));
            if (reaped != 0)
                _log($"Removed {reaped} abandoned index publication artifact(s).");
        }
    }

    private void EnsureStagedDestinationAuthority(AnchoredIndexDestination destination)
    {
        EnsureDatabaseAuthority();
        if (_ownershipLease is null ||
            _directoryAuthority is null ||
            !_directoryAuthority.TryGetLeaseIdentity(out IndexLeaseIdentity? retained) ||
            !destination.TryGetLeaseIdentity(out IndexLeaseIdentity? staged) ||
            retained is null ||
            staged is null ||
            retained != staged ||
            !string.Equals(staged.DirectoryIdentity, _authorityDirectoryIdentity,
                StringComparison.Ordinal))
            throw new IOException(
                "staged index destination differs from the retained index authority");
        if (!destination.TryGetWorkspaceIdentity(out string? anchoredWorkspace) ||
            anchoredWorkspace is null ||
            !string.Equals(anchoredWorkspace, _ownershipLease.WorkspaceIdentity,
                StringComparison.Ordinal) ||
            !HasSafeWorkspaceAuthority())
            throw new IOException(
                "staged index workspace differs from the retained workspace authority");

        if (!HasSafeLiveDatabaseAuthority())
            throw new IOException(
                "live index destination differs from the retained index authority");
    }

    private void EnsureInstalledDestinationAuthority(AnchoredIndexDestination destination)
    {
        EnsureDatabaseAuthority();
        if (_ownershipLease is null ||
            _directoryAuthority is null ||
            !_directoryAuthority.TryGetLeaseIdentity(out IndexLeaseIdentity? retained) ||
            !destination.TryGetInstalledLeaseIdentity(out IndexLeaseIdentity? installed) ||
            retained is null ||
            installed is null ||
            retained != installed ||
            !string.Equals(installed.DirectoryIdentity, _authorityDirectoryIdentity,
                StringComparison.Ordinal))
            throw new IOException(
                "installed index destination differs from the retained index authority");
        if (!HasSafeLiveDatabaseAuthority())
            throw new IOException(
                "live index destination differs from the retained index authority");

        if (!destination.TryGetWorkspaceIdentity(out string? anchoredWorkspace) ||
            anchoredWorkspace is null ||
            !string.Equals(anchoredWorkspace, _ownershipLease.WorkspaceIdentity,
                StringComparison.Ordinal) ||
            !HasSafeWorkspaceAuthority())
            throw new IOException(
                "installed index workspace differs from the retained workspace authority");
    }

    /// <summary>Opens the existing index or builds a new one in the background; returns immediately.</summary>
    private void SetStartupFailure(IndexStartupFailureCause cause) =>
        Volatile.Write(ref _startupFailureCause, (int)cause);

    public void Start(bool forceRebuild = false)
    {
        lock (_disposeLock)
        {
            if (_disposed || IsWriter || IsFollower || _pump is not null || _startTask is not null)
                return;
            SetStartupFailure(IndexStartupFailureCause.None);
            if (!IndexDirectoryAuthority.TryOpen(_dbPath, createDirectory: true,
                    out IndexDirectoryAuthority? authority) ||
                !authority!.TryGetLeaseIdentity(out IndexLeaseIdentity? leaseIdentity))
            {
                authority?.Dispose();
                // Sanitized like every startup error (9vw): fixed phrase, no filesystem internals.
                // "safely" not "without following links" — this gate also refuses a missing or
                // non-directory parent, not only link/reparse traversal; specifics go to the log.
                _error = "index destination could not be opened safely during index startup (see server log)";
                SetStartupFailure(IndexStartupFailureCause.DestinationUnsafe);
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
                // Directional worktree ownership probes briefly acquire-and-release this mutex.
                // Do not mistake that millisecond-scale probe for a durable writer.
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
                    IndexDestinationClaimState claimState =
                        IndexDestinationClaim.ReadState(_workspaceRoot, _databaseIoPath);
                    for (int retry = 0;
                         retry < 20 && claimState == IndexDestinationClaimState.Missing;
                         retry++)
                    {
                        Thread.Sleep(25);
                        claimState = IndexDestinationClaim.ReadState(
                            _workspaceRoot, _databaseIoPath);
                    }
                    if (claimState is not (IndexDestinationClaimState.Ready or
                        IndexDestinationClaimState.Rebuilding))
                    {
                        authority.Dispose();
                        _directoryAuthority = null;
                        _authorityDirectoryIdentity = null;
                        _databaseIoPath = _dbPath;
                        _accessMode = UnavailableAccessMode;
                        _error = claimState == IndexDestinationClaimState.Foreign
                            ? "index destination belongs to a different workspace"
                            : "writer index destination could not be verified";
                        SetStartupFailure(claimState == IndexDestinationClaimState.Foreign
                            ? IndexStartupFailureCause.DestinationForeign
                            : IndexStartupFailureCause.WriterAuthorityUnavailable);
                        _state = "failed";
                        _log(claimState == IndexDestinationClaimState.Foreign
                            ? "Follower startup refused: the configured index destination is claimed by a different workspace."
                            : "Follower startup refused: the active workspace writer did not claim this configured index destination.");
                        return;
                    }

                    // SQLite WAL supports concurrent committed readers. A contending Phoenix is a
                    // follower: retain only the no-follow directory authority and open short-lived,
                    // nonpooled read-only connections. It never starts a store, pump, or watcher.
                    _followerDestinationBound = true;
                    _accessMode = FollowerAccessMode;
                    _error = FollowerIndexUnavailable;
                    SetStartupFailure(IndexStartupFailureCause.WriterLeaseContended);
                    _state = "failed";
                    if (claimState == IndexDestinationClaimState.Ready &&
                        TryRefreshFollowerMetadata(force: true))
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
                SetStartupFailure(leaseResult == IndexLeaseAcquireResult.Contended
                    ? IndexStartupFailureCause.WriterLeaseContended
                    : IndexStartupFailureCause.WriterAuthorityUnavailable);
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
                SetStartupFailure(IndexStartupFailureCause.DestinationChanged);
                _state = "failed";
                _log("Index startup refused: index destination changed during ownership acquisition.");
                return;
            }
            IndexDestinationClaimAcquireResult destinationResult =
                IndexDestinationClaim.TryAcquire(_workspaceRoot, _databaseIoPath,
                    out _destinationClaim);
            if (destinationResult != IndexDestinationClaimAcquireResult.Acquired)
            {
                _ownershipLease!.Dispose();
                _ownershipLease = null;
                authority.Dispose();
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _accessMode = UnavailableAccessMode;
                _error = destinationResult == IndexDestinationClaimAcquireResult.Contended
                    ? "index destination belongs to a different workspace"
                    : "index destination ownership could not be established safely";
                SetStartupFailure(destinationResult ==
                    IndexDestinationClaimAcquireResult.Contended
                    ? IndexStartupFailureCause.DestinationForeign
                    : IndexStartupFailureCause.DestinationUnsafe);
                _state = "failed";
                _log(destinationResult == IndexDestinationClaimAcquireResult.Contended
                    ? "Index startup refused: the configured database is claimed by another workspace."
                    : "Index startup refused: database destination ownership could not be established.");
                return;
            }
            bool rebindWorkspaceRoot;
            try
            {
                rebindWorkspaceRoot = IndexBuilder.EnsureExistingDatabaseWorkspace(
                    _workspaceRoot, _databaseIoPath,
                    allowMissingStoredRootRebind: forceRebuild,
                    allowLegacySchemaRebind: forceRebuild,
                    configuredDatabasePath: _dbPath);
                ReapAbandonedPublicationArtifacts();
            }
            catch (IndexWorkspaceRebindRequiredException ex)
            {
                _destinationClaim!.Dispose();
                _destinationClaim = null;
                _ownershipLease!.Dispose();
                _ownershipLease = null;
                authority.Dispose();
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _accessMode = UnavailableAccessMode;
                _error = "index database moved with its workspace; run refresh_index force:'full' to rebuild and rebind it";
                SetStartupFailure(IndexStartupFailureCause.RebuildRequired);
                _state = "failed";
                _log($"Index startup requires explicit moved-workspace recovery: {ex}");
                return;
            }
            catch (IndexWorkspaceMismatchException ex)
            {
                _destinationClaim!.Dispose();
                _destinationClaim = null;
                _ownershipLease!.Dispose();
                _ownershipLease = null;
                authority.Dispose();
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _accessMode = UnavailableAccessMode;
                _error = "index destination belongs to a different workspace";
                SetStartupFailure(IndexStartupFailureCause.DestinationForeign);
                _state = "failed";
                _log($"Index startup refused: {ex.GetType().Name} while validating database workspace ownership.");
                return;
            }
            catch (Exception ex)
            {
                _destinationClaim!.Dispose();
                _destinationClaim = null;
                _ownershipLease!.Dispose();
                _ownershipLease = null;
                authority.Dispose();
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _accessMode = UnavailableAccessMode;
                _error = $"{ex.GetType().Name} while validating index destination (see server log)";
                SetStartupFailure(IndexStartupFailureCause.DestinationValidationFailed);
                _state = "failed";
                _log($"Index startup failed while validating database workspace ownership: {ex}");
                return;
            }
            _accessMode = WriterAccessMode;
            SetStartupFailure(IndexStartupFailureCause.None);
            if (rebindWorkspaceRoot)
                _log("Index rebuild will rebind the database to the current workspace root.");
            // Publish both tasks while holding the same lock Dispose uses. Dispose can therefore
            // never release the lease between its acquisition and publication of the workers.
            _pump = Task.Run(PumpRefreshesAsync);

            // Startup can synchronously scan, parse, and publish a complete repository. Give that
            // lifecycle-owned work its own thread so MCP dispatch and progress inspection do not
            // depend on ThreadPool hill-climbing while every parser lane is busy.
            _startTask = Task.Factory.StartNew(() =>
            {
                bool restoreReadyClaimOnFailure = false;
                bool stagedPublicationInstalled = false;
                bool startupMutationActive = false;
                try
                {
                    if (_disposed) return;
                    StartupAfterLeaseAcquiredForTest?.Invoke();
                    bool databaseExists = File.Exists(_databaseIoPath);
                    bool build = forceRebuild || !databaseExists;
                    bool compatibleExistingPublication = false;
                    string? startupPriorSchema = null;
                    // x5ls.1.2: honest v1 build reason — which gate actually forced the build.
                    string buildReason = forceRebuild ? "explicit_full" : "startup_missing";
                    if (databaseExists)
                    {
                        try
                        {
                            using var check = new IndexStore(_databaseIoPath, createNew: false);
                            string? onDisk = check.GetMeta("schema_version");
                            startupPriorSchema = onDisk;
                            compatibleExistingPublication = string.Equals(onDisk,
                                IndexBuilder.SchemaVersion, StringComparison.Ordinal);
                            if (compatibleExistingPublication)
                            {
                                ResetCachedFreshnessMetadata();
                                CacheMeta(check);
                            }
                            // Rebuild when the on-disk index predates the current schema/indexer
                            // format. A force rebuild still performs this read so a compatible old
                            // publication may remain available while its private replacement builds.
                            if (!forceRebuild && !compatibleExistingPublication)
                            {
                                _log($"Index format stale (have {onDisk ?? "none"}, need {IndexBuilder.SchemaVersion}); rebuilding.");
                                build = true;
                                buildReason = "startup_incompatible";
                            }
                        }
                        catch (Exception ex)
                        {
                            compatibleExistingPublication = false;
                            if (!forceRebuild)
                            {
                                _log($"Index open/version-check failed ({ex.Message}); rebuilding.");
                                build = true;
                                buildReason = "recovery";
                            }
                        }
                    }
                    _startupBuildReason = build ? buildReason : null;
                    _startupPriorSchema = build && databaseExists ? startupPriorSchema : null;
                    if (build)
                    {
                        _state = "building";
                        // Live progress for the building window (bead two, field-requested during the
                        // v5 monorepo reindex): published before the build starts, cleared after —
                        // Health() surfaces it only while state == "building".
                        _buildProgress = new BuildProgress();
                        _log($"Building index for {_workspaceRoot} ...");
                        var startupBuildProgress = _buildProgress; // review B6: finally-safe local
                        var (buildId, progressTimer) = // x5ls.1.2
                            BeginBuildTelemetry(buildReason, startupBuildProgress);
                        try
                        {
                            BuildResult buildResult;
                            FullRebuildBeforeAnchoredDestinationOpenForTest?.Invoke();
                            if (AnchoredIndexDestination.TryOpen(_workspaceRoot,
                                    _workspaceRoot, _dbPath, createIndexDirectory: false,
                                    out AnchoredIndexDestination? destination))
                            {
                                AnchoredIndexDestination anchored = destination!;
                                using (anchored)
                                {
                                    EnsureStagedDestinationAuthority(anchored);
                                    if (compatibleExistingPublication)
                                    {
                                        var priorStore = new IndexStore(
                                            _databaseIoPath, createNew: false);
                                        try
                                        {
                                            if (priorStore.GetMeta(
                                                    RefreshIncompleteReasonMeta) is null)
                                                PersistRefreshSweepPending(priorStore);
                                            CacheMeta(priorStore);
                                            _destinationClaim?.SetReady();
                                            _store = priorStore;
                                        }
                                        catch
                                        {
                                            priorStore.Dispose();
                                            throw;
                                        }
                                        _error = _refreshIncompleteReason;
                                        // The claim is born B. Once the compatible old database
                                        // is published through this manager, return it to R for the
                                        // long private build and close it only at atomic install.
                                        restoreReadyClaimOnFailure = true;
                                    }
                                    FullRebuildAfterTelemetryStartedForTest?.Invoke();
                                    string stagePath = anchored.CreateStagePath();
                                    FullRebuildPrivateStageReadyForTest?.Invoke(stagePath);
                                    buildResult = IndexBuilder.BuildOwned(
                                        anchored.WorkspaceReadPath, stagePath, _log,
                                        startupBuildProgress, reservedPrivateStage: true,
                                        publishedWorkspaceRoot: _workspaceRoot);
                                    FullRebuildPrivateStageCompletedForTest?.Invoke();
                                    EnsureStagedDestinationAuthority(anchored);
                                    startupMutationActive = true;
                                    var publicationWait = Stopwatch.StartNew();
                                    TimeSpan publicationTimeout =
                                        FullRebuildPublicationTimeoutForTest;
                                    BeginFullRebuildPublicationBoundary();
                                    _destinationClaim?.SetRebuilding();
                                    DrainLocalReadersAtPublication(publicationWait,
                                        publicationTimeout, () =>
                                        {
                                            _error = "startup rebuild is waiting for existing index readers to drain";
                                            _log("Startup rebuild is waiting for existing index readers to drain.");
                                        });
                                    _store?.Dispose();
                                    _store = null;
                                    ClearOwnedDatabasePools();
                                    EnsureStagedDestinationAuthority(anchored);
                                    FullRebuildBeforeStageInstallForTest?.Invoke();
                                    EnsureStagedDestinationAuthority(anchored);
                                    TimeSpan installTimeout = RemainingPublicationWait(
                                        publicationWait, publicationTimeout);
                                    if (installTimeout <= TimeSpan.Zero ||
                                        !anchored.InstallStage(installTimeout,
                                            waitingForReaders: () =>
                                        {
                                            _error = "startup rebuild is waiting for existing index readers to drain";
                                            _log("Startup rebuild is waiting for existing index readers to drain.");
                                        }))
                                        throw new IOException(
                                            "staged startup index could not replace the live database");
                                    stagedPublicationInstalled = true;
                                    FullRebuildAfterStageInstallForTest?.Invoke();
                                    EnsureInstalledDestinationAuthority(anchored);
                                }
                            }
                            else
                            {
                                if (!CanUseDirectFullRebuildFallback())
                                    throw new IOException(
                                        "required anchored startup publication could not be opened safely");
                                FullRebuildAfterTelemetryStartedForTest?.Invoke();
                                FullRebuildDestructiveBoundaryForTest?.Invoke(0);
                                buildResult = IndexBuilder.BuildOwned(_workspaceRoot,
                                    _databaseIoPath, _log, startupBuildProgress,
                                    waitingForReaders: () =>
                                    {
                                        _error = "startup rebuild is waiting for existing index readers to drain";
                                        _log("Startup rebuild is waiting for existing index readers to drain.");
                                    });
                            }
                            _error = null;
                            _log($"Index built: {buildResult.CsFiles} C# + {buildResult.FsFiles} F# files, " +
                                 $"{buildResult.Symbols} symbols in " +
                                 $"{buildResult.TotalTime.TotalSeconds:F0}s");
                            // Review B1: drain the ticker BEFORE the terminal frame — a
                            // progress frame sequenced after completed regresses portals.
                            DrainDisposeBuildTimer(progressTimer);
                            EmitBuildCompleted(buildId, buildReason, startupBuildProgress,
                                (long)buildResult.TotalTime.TotalMilliseconds);
                        }
                        catch (OperationCanceledException)
                        {
                            DrainDisposeBuildTimer(progressTimer);
                            EmitBuildCancelled(buildId, buildReason, startupBuildProgress);
                            throw;
                        }
                        catch
                        {
                            DrainDisposeBuildTimer(progressTimer);
                            EmitBuildFailed(buildId, buildReason, startupBuildProgress);
                            throw; // the startup catch owns state/error, unchanged
                        }
                        finally
                        {
                            DrainDisposeBuildTimer(progressTimer); // idempotent
                            _buildProgress = null;
                        }
                        FullRebuildCompletedForTest?.Invoke();
                        EnsureLivePublicationAuthority();
                    }

                    var store = new IndexStore(_databaseIoPath, createNew: false);
                    if (rebindWorkspaceRoot)
                    {
                        // The database moved with its physical workspace. Rebind the lexical
                        // metadata before Ready publication so followers validate the new root.
                        store.SetMeta("workspace_root", _workspaceRoot);
                    }
                    // A compatible database is not current until the watcher is attached and the
                    // serialized detect-all pass closes the build/open-to-watch gap. Persist this
                    // before publication so Windows followers cannot observe a false-ready epoch.
                    if (store.GetMeta(RefreshIncompleteReasonMeta) is null)
                        PersistRefreshSweepPending(store);
                    ResetCachedFreshnessMetadata();
                    CacheMeta(store);              // read meta before publishing the store

                    // If Dispose ran while we were building/opening, don't publish or start the
                    // watcher — clean up the store we just opened and leave the manager stopped.
                    if (_disposed)
                    {
                        store.Dispose();
                        return;
                    }
                    EnsureLivePublicationAuthority();

                    _store = store;
                    _error = _refreshIncompleteReason;
                    // Schema-17 persists the writer's incomplete-source latch so Windows
                    // followers inherit stale/coverage state without ever owning a watcher or
                    // refresh queue.
                    _state = "stale";
                    // review F2 parity with FullRebuildInPump: recovery via a
                    // re-entered Start is a DESIGNED failed->ready transition — a healthy index
                    // must not keep reporting the pre-recovery refusal.
                    StartWatcher();
                    _log(build
                        ? "Fresh index opened; running post-build freshness sweep ..."
                        : "Existing index opened; running startup freshness sweep ...");
                    // Always sweep after the watcher is attached. A full build deliberately commits
                    // its verified structural snapshot before the long C# parse; edits made between
                    // that commit and watcher attachment would otherwise be permanently missed.
                    _refreshQueue.Writer.TryWrite(new RefreshRequest(null, Reason: "full_sweep")); // detect-all
                    InitGitTracking(); // watch HEAD, then atomically sample and reconcile it
                    EnsureLivePublicationAuthority();
                    _destinationClaim?.SetReady();
                    if (startupMutationActive)
                    {
                        ReopenWriterReadsAfterPublication();
                        EndIndexMutation();
                        startupMutationActive = false;
                    }
                }
                catch (Exception ex)
                {
                    bool priorPublicationRestored = false;
                    if (restoreReadyClaimOnFailure && !stagedPublicationInstalled)
                    {
                        try
                        {
                            if (_disposed || !HasSafeWorkspaceAuthority() ||
                                !HasSafeLiveDatabaseAuthority())
                                throw new ObjectDisposedException(nameof(IndexManager));
                            if (_store is null)
                                _store = new IndexStore(_databaseIoPath, createNew: false);
                            if (_store.GetMeta(RefreshIncompleteReasonMeta) is null)
                                PersistRefreshSweepPending(_store);
                            ResetCachedFreshnessMetadata();
                            CacheMeta(_store);
                            StartupPriorPublicationRestoreForTest?.Invoke();
                            if (_disposed)
                                throw new ObjectDisposedException(nameof(IndexManager));
                            if (_watcher is null) StartWatcher();
                            _refreshQueue.Writer.TryWrite(
                                new RefreshRequest(null, Reason: "full_sweep"));
                            if (_gitWatcher is null) InitGitTracking();
                            if (_disposed)
                                throw new ObjectDisposedException(nameof(IndexManager));
                            EnsureLivePublicationAuthority();
                            _destinationClaim?.SetReady();
                            if (startupMutationActive)
                            {
                                ReopenWriterReadsAfterPublication();
                                EndIndexMutation();
                                startupMutationActive = false;
                            }
                            _error =
                                "startup rebuild failed; the previous index remains available";
                            _state = "stale";
                            priorPublicationRestored = true;
                        }
                        catch (Exception restoreError)
                        {
                            _log($"Could not restore the previous startup publication claim: " +
                                 $"{restoreError}");
                        }
                    }
                    if (!priorPublicationRestored)
                    {
                        // Client-visible error carries the exception TYPE only (9vw): ex.Message
                        // can embed absolute filesystem paths, account names, or SQLite connection
                        // details — internals that don't belong in a tool response.
                        _error = $"{ex.GetType().Name} during index startup (see server log)";
                        _state = "failed";
                    }
                    _log($"Index startup failed: {ex}");
                }
                finally
                {
                    if (startupMutationActive)
                    {
                        EndIndexMutation();
                        startupMutationActive = false;
                    }
                    _startupComplete.TrySetResult(true);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
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
        _refreshIncompleteReason = store.GetMeta(RefreshIncompleteReasonMeta);
        Volatile.Write(ref _refreshIncompletePersisted,
            _refreshIncompleteReason is null ? 0 : 1);
        string? pathsJson = store.GetMeta(RefreshIncompletePathsMeta);
        try
        {
            _refreshIncompletePaths = pathsJson is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<string[]>(pathsJson)?
                    .Take(RefreshIncompletePathLimit).ToArray();
        }
        catch (System.Text.Json.JsonException)
        {
            _refreshIncompletePaths = null;
        }
        int count = int.TryParse(store.GetMeta(RefreshIncompletePathCountMeta),
            out int parsedCount)
            ? parsedCount
            : _refreshIncompletePaths?.Length ?? 0;
        Volatile.Write(ref _refreshIncompletePathCount,
            Math.Max(count, _refreshIncompletePaths?.Length ?? 0));
        Volatile.Write(ref _refreshIncompletePathCountIsLowerBound,
            string.Equals(store.GetMeta(RefreshIncompletePathCountLowerBoundMeta), "1",
                StringComparison.Ordinal) ? 1 : 0);
    }

    private FollowerPublication CurrentFollowerPublication =>
        Volatile.Read(ref _followerPublication);

    internal IndexMetadataSnapshot? FollowerMetadataForTest =>
        CurrentFollowerPublication.Metadata;
    internal bool HasOwnedStoreForTest => _store is not null;
    internal bool HasWorkspaceWatcherForTest => _watcher is not null;

    private bool PublishFollowerReady(IndexMetadataSnapshot metadata)
    {
        // One reference swap publishes the complete SQLite metadata tuple plus its readiness.
        // Health can therefore observe either the previous committed epoch or this one, never a
        // mixture assembled from independently written fields.
        FollowerMetadataBeforePublishForTest?.Invoke(metadata);
        if (!FollowerDestinationAllowsRead())
            return PublishFollowerUnavailable();
        string state = metadata.RefreshIncompleteReason is null ? "ready" : "stale";
        Volatile.Write(ref _followerPublication,
            new FollowerPublication(metadata, true, state,
                metadata.RefreshIncompleteReason));
        FollowerMetadataAfterPublishForTest?.Invoke(metadata);
        // The writer may publish B after the pre-write check. Recheck after the ready
        // publication and conservatively replace it with unavailable before returning.
        if (!FollowerDestinationAllowsRead())
            return PublishFollowerUnavailable();
        return true;
    }

    private bool IsCompatibleFollowerMetadata(IndexMetadataSnapshot metadata) =>
        string.Equals(metadata.SchemaVersion, IndexBuilder.SchemaVersion,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(metadata.IndexVersion) &&
        !string.IsNullOrWhiteSpace(metadata.IndexedAtUtc) &&
        metadata.WorkspaceRoot is { Length: > 0 } storedRoot &&
        CodeNav.Core.WorkspacePaths.FullPathsEqual(storedRoot, _workspaceRoot);

    private bool FollowerDestinationAllowsRead()
    {
        if (!IsFollower || !_followerDestinationBound) return !IsFollower;
        IndexDestinationClaimState state =
            IndexDestinationClaim.ReadState(_workspaceRoot, _databaseIoPath);
        // A cleanly exited writer removes its claim. Existing followers deliberately remain
        // query-only against the last compatible committed database until they restart.
        return state is IndexDestinationClaimState.Ready or
            IndexDestinationClaimState.Missing;
    }

    /// <summary>Refreshes follower health from a read-only, nonpooled connection. This method never
    /// repairs or creates an index. A transient database replacement makes the follower unavailable
    /// for this call; a later call reopens and recovers after a writer publishes a compatible DB.</summary>
    private bool TryRefreshFollowerMetadata(bool force)
    {
        if (!IsFollower || _disposed) return false;
        long now = Environment.TickCount64;
        if (!force && now < Volatile.Read(ref _nextFollowerMetadataRefresh))
        {
            if (FollowerDestinationAllowsRead())
                return CurrentFollowerPublication.Readable;
            return PublishFollowerUnavailable();
        }
        if (Interlocked.CompareExchange(ref _followerMetadataRefreshActive, 1, 0) != 0)
        {
            if (FollowerDestinationAllowsRead())
                return CurrentFollowerPublication.Readable;
            return PublishFollowerUnavailable();
        }

        try
        {
            Volatile.Write(ref _nextFollowerMetadataRefresh, now + 250);
            lock (_followerMetadataGate)
            {
                if (!FollowerDestinationAllowsRead())
                    return PublishFollowerUnavailable();
                if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? before, out _) ||
                    before?.DatabaseIdentity is null)
                    return PublishFollowerUnavailable();

                using var queries = new IndexQueries(_databaseIoPath, pinReadSnapshot: false,
                    pooling: false);
                IndexMetadataSnapshot metadata = queries.ReadMetadata();
                if (!IsCompatibleFollowerMetadata(metadata) ||
                    !FollowerDestinationAllowsRead())
                    return PublishFollowerUnavailable();

                if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? after, out _) ||
                    after != before ||
                    !FollowerDestinationAllowsRead())
                    return PublishFollowerUnavailable();

                if (!PublishFollowerReady(metadata))
                    return false;
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
    /// Wires git-aware refresh: watches HEAD before atomically sampling the current tuple, then
    /// records it (or reconciles a diff if HEAD moved while the server was down). Best-effort — a repo
    /// without git, or without a git CLI, simply keeps FSW-only behavior.
    /// </summary>
    private void InitGitTracking()
    {
        lock (_disposeLock)
        {
            if (_disposed) return; // teardown began before we got here — skip the git shell-outs
        }
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

        // Attach before the initial sample so a same-OID detach/reattach cannot land between
        // sampling and watcher publication. A callback that wins the observation gate simply
        // performs the same reconcile first; the startup sample then observes a duplicate.
        lock (_disposeLock)
        {
            if (_disposed) return;
            _gitWatcher = new GitWatcher(_gitDir, () => OnGitHeadMaybeChanged());
        }

        bool scheduleRetry = false;
        string? logMessage = null;
        lock (_gitHeadObservationGate)
        {
            if (_disposed) return;
            GitInfo.HeadSnapshot snapshot = ReadGitHeadSnapshot();
            if (!snapshot.IsResolved)
            {
                scheduleRetry = true;
            }
            else if (_latestObservedGitHead is not { } previous ||
                     !SameGitHead(previous, snapshot))
            {
                _latestObservedGitHead = snapshot;
                string head = snapshot.Commit!;
                string? stored = _indexedCommit;
                if (stored is null)
                {
                    // First git-aware run (or a pre-git index): the build/startup-sweep already
                    // reflects the current tree, so just record the commit as the diff baseline.
                    _refreshQueue.Writer.TryWrite(new RefreshRequest(
                        Array.Empty<string>(), head, Reason: "git_head",
                        RecordBranch: snapshot.Branch, RecordBranchKnown: true));
                }
                else if (!string.Equals(stored, head, StringComparison.OrdinalIgnoreCase))
                {
                    logMessage =
                        $"Git HEAD moved while stopped: {Short(stored)} -> {Short(head)}; reconciling.";
                    EnqueueGitReconcile(snapshot);
                }
                else if (!string.Equals(_indexedBranch, snapshot.Branch,
                             StringComparison.Ordinal))
                {
                    logMessage = "Git attachment changed while stopped at the same commit; " +
                                 "refreshing branch metadata.";
                    QueueGitMetadataRefresh(snapshot);
                }
            }
        }

        if (scheduleRetry) ScheduleGitHeadRetry();
        if (logMessage is not null) _log(logMessage);
    }

    private GitInfo.HeadSnapshot ReadGitHeadSnapshot() =>
        GitHeadSnapshotForTest?.Invoke() ?? GitInfo.HeadSnapshotEx(_workspaceRoot);

    private static bool SameGitHead(
        GitInfo.HeadSnapshot left,
        GitInfo.HeadSnapshot right) =>
        string.Equals(left.Commit, right.Commit, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Branch, right.Branch, StringComparison.Ordinal);

    private void QueueGitMetadataRefresh(GitInfo.HeadSnapshot snapshot)
    {
        _refreshQueue.Writer.TryWrite(new RefreshRequest(
            Array.Empty<string>(), snapshot.Commit, Reason: "git_head",
            RecordBranch: snapshot.Branch, RecordBranchKnown: true));
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
    private readonly object _gitHeadObservationGate = new();
    private GitInfo.HeadSnapshot? _latestObservedGitHead;
    private static readonly TimeSpan[] RefreshRecoverySweepDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];
    private System.Threading.Timer? _refreshRecoverySweepRetry;
    private int _refreshRecoverySweepBackoffLevel;
    private int _refreshRecoverySweepEscalationLogged;
    private volatile string? _refreshRecoveryPendingGitCommit;
    // Recovery HEAD samples are ordered independently of queued refresh requests. A request may
    // clear the stale latch only if its resolved generation is at least the latest unavailable
    // generation; this prevents an older resolved tuple from publishing after a newer failed read.
    private long _refreshRecoveryGitSnapshotGeneration;
    private long _refreshRecoveryGitRevalidationRequiredGeneration;
    // A full rebuild replaces the database incarnation targeted by every ordered recovery snapshot
    // sampled before installation. Retire those generations so a request queued behind the rebuild
    // cannot graft obsolete Git metadata onto the replacement or clear its convergence marker.
    private long _refreshRecoveryGitSnapshotRetiredThroughGeneration;
    internal Func<int, TimeSpan>? RefreshRecoverySweepDelayForTest { get; set; }

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
        bool scheduleRetry = false;
        string? logMessage = null;
        lock (_gitHeadObservationGate)
        {
            if (_disposed) return;
            if (!fromRetry) _gitHeadRetriesLeft = GitHeadRetryAttempts;
            // Snapshot acquisition belongs to the same critical section as comparison and queue
            // publication. Otherwise overlapping watcher/retry callbacks can capture old/new HEAD
            // in order but acquire this gate in reverse, permanently publishing the older tuple.
            GitInfo.HeadSnapshot snapshot = ReadGitHeadSnapshot();
            if (_disposed) return;
            if (!snapshot.IsResolved)
            {
                scheduleRetry = true;
            }
            else
            {
                string head = snapshot.Commit!;
                GitInfo.HeadSnapshot? previousObserved = _latestObservedGitHead;
                if (previousObserved is { } previous && SameGitHead(previous, snapshot))
                    return; // duplicate signal for a request already published or queued
                _latestObservedGitHead = snapshot;

                string? current = _indexedCommit;
                bool observedCommitChanged = previousObserved is { } observed &&
                    !string.Equals(observed.Commit, head, StringComparison.OrdinalIgnoreCase);
                if (current is null)
                {
                    // A first-commit signal may arrive while the startup baseline request is still
                    // queued. Resolve its file scope against the baseline that is actually published
                    // when this request reaches the pump.
                    EnqueueGitReconcile(snapshot);
                    logMessage =
                        $"Git baseline signal: queueing first-commit reconcile {Short(head)}.";
                }
                else if (observedCommitChanged)
                {
                    // Even when this snapshot matches the still-published baseline, an older queued
                    // commit may run first. Queue the inverse transition and resolve its diff at pump
                    // execution time so A -> B -> A cannot leave B rows behind.
                    EnqueueGitReconcile(snapshot);
                    logMessage =
                        $"Git HEAD observation changed to {Short(head)}; queueing ordered reconcile.";
                }
                else if (string.Equals(current, head, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(_indexedBranch, snapshot.Branch, StringComparison.Ordinal) &&
                        previousObserved is null)
                        return; // first observation confirms the already-published tuple
                    QueueGitMetadataRefresh(snapshot);
                    logMessage =
                        "Git attachment changed at the current commit; refreshing branch metadata.";
                }
                else
                {
                    EnqueueGitReconcile(snapshot);
                    logMessage =
                        $"Git HEAD changed: {Short(current)} -> {Short(head)}; reconciling.";
                }
            }
        }
        if (scheduleRetry)
        {
            ScheduleGitHeadRetry(); // 17zd: transient — do not swallow the only first-commit signal
            return;
        }
        if (logMessage is not null) _log(logMessage);
    }

    private void ScheduleRefreshRecoverySweep(string unavailablePath)
    {
        if (_disposed) return;
        int level = Math.Min(_refreshRecoverySweepBackoffLevel,
            RefreshRecoverySweepDelays.Length - 1);
        TimeSpan delay = RefreshRecoverySweepDelayForTest?.Invoke(level)
            ?? RefreshRecoverySweepDelays[level];
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        bool logEscalation = false;
        lock (_disposeLock)
        {
            if (_disposed) return;
            _refreshRecoverySweepBackoffLevel = Math.Min(level + 1,
                RefreshRecoverySweepDelays.Length - 1);
            if (level == RefreshRecoverySweepDelays.Length - 1 &&
                Interlocked.Exchange(ref _refreshRecoverySweepEscalationLogged, 1) == 0)
            {
                logEscalation = true;
            }
            try
            {
                (_refreshRecoverySweepRetry ??= new System.Threading.Timer(
                        _ => QueueRefreshRecoverySweep(),
                        null, Timeout.Infinite, Timeout.Infinite))
                    .Change(delay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Dispose can win after the stale-state check. The manager is stopping, so no
                // recovery request should survive it.
            }
        }
        if (logEscalation)
        {
            _log($"Source remains unavailable for {unavailablePath}; automated stale-index " +
                 $"recovery is now paced at one complete sweep every " +
                 $"{delay.TotalSeconds:F0}s until the input becomes readable.");
        }
    }

    private void QueueRefreshRecoverySweep()
    {
        if (_disposed ||
            !string.Equals(_refreshIncompleteReason, RefreshInputUnavailableCause,
                StringComparison.Ordinal))
            return;

        _log("Retrying stale index recovery with a complete workspace sweep.");
        string? pendingGitCommit = _refreshRecoveryPendingGitCommit;
        _refreshQueue.Writer.TryWrite(new RefreshRequest(null,
            RecordCommit: pendingGitCommit, Reason: "recovery_sweep",
            TimerInitiatedRecovery: true,
            RevalidateRecordCommit: pendingGitCommit is not null));
    }

    private void ResetRefreshRecoverySweepBackoff()
    {
        _refreshRecoverySweepBackoffLevel = 0;
        Volatile.Write(ref _refreshRecoverySweepEscalationLogged, 0);
        Volatile.Write(ref _refreshRecoveryGitRevalidationRequiredGeneration, 0);
        _refreshRecoveryPendingGitCommit = null;
        lock (_disposeLock)
        {
            if (_disposed) return;
            try
            {
                _refreshRecoverySweepRetry?.Change(
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Dispose owns shutdown; a successful refresh does not need to retain the timer.
            }
        }
    }

    /// <summary>Queue the captured HEAD tuple. Its file scope is resolved by the serialized pump
    /// against the commit actually published ahead of it, so rapid A -&gt; B -&gt; A transitions
    /// cannot apply an A -&gt; A path list after B rows have entered the index.</summary>
    private void EnqueueGitReconcile(GitInfo.HeadSnapshot snapshot)
    {
        _refreshQueue.Writer.TryWrite(new RefreshRequest(
            Array.Empty<string>(), snapshot.Commit, Reason: "git_head",
            RecordBranch: snapshot.Branch, RecordBranchKnown: true,
            ResolveGitPathsAtExecution: true));
    }

    private static string Short(string commit) => commit.Length >= 8 ? commit[..8] : commit;

    private async Task PumpRefreshesAsync()
    {
        await foreach (var queuedRequest in _refreshQueue.Reader.ReadAllAsync())
        {
            RefreshRequest req = queuedRequest;
            if (req.TimerInitiatedRecovery &&
                !req.PublishRevalidatedGitSnapshot &&
                !string.Equals(_refreshIncompleteReason, RefreshInputUnavailableCause,
                    StringComparison.Ordinal))
            {
                continue;
            }
            if (req.PublishRevalidatedGitSnapshot &&
                req.RecoveryGitSnapshotGeneration <= Volatile.Read(
                    ref _refreshRecoveryGitSnapshotRetiredThroughGeneration))
            {
                req.CompletionForTest?.TrySetResult();
                continue;
            }
            if (req.ResolveGitPathsAtExecution && req.RecordCommit is { } gitTarget)
            {
                string? publishedCommit = _indexedCommit;
                IReadOnlyCollection<string>? resolvedPaths;
                if (publishedCommit is null)
                {
                    resolvedPaths = null;
                }
                else if (string.Equals(publishedCommit, gitTarget,
                             StringComparison.OrdinalIgnoreCase))
                {
                    resolvedPaths = Array.Empty<string>();
                }
                else
                {
                    List<string>? changed = GitInfo.ChangedFiles(
                        _workspaceRoot, publishedCommit, gitTarget);
                    resolvedPaths = changed is null || changed.Count > GitDiffCap
                        ? null
                        : changed;
                }
                req = req with
                {
                    Paths = resolvedPaths,
                    ResolveGitPathsAtExecution = false,
                };
            }
            if (_refreshIncompleteReason is not null && !req.FullRebuild &&
                req.Paths is not null)
            {
                // A later narrow notification cannot prove recovery of the source whose event
                // exhausted its retry budget. Widen the next request before opening a transaction;
                // the single pump still preserves FIFO order and one recovery sweep covers all
                // paths queued behind the failed request.
                req = req with { Paths = null, Reason = "recovery_sweep" };
            }
            if (_refreshIncompleteReason is not null && !req.FullRebuild &&
                !req.PublishRevalidatedGitSnapshot &&
                _refreshRecoveryPendingGitCommit is { } pendingGitCommit &&
                (req.RecordCommit is null ||
                 Volatile.Read(ref _refreshRecoveryGitRevalidationRequiredGeneration) != 0))
            {
                // A failed recovery HEAD read invalidates every tuple captured before it. Revalidate
                // those queued requests too: otherwise one can publish its older commit, clear the
                // stale latch, and cancel the paced retry that was promised for the unknown HEAD.
                req = req with
                {
                    RecordCommit = req.RecordCommit ?? pendingGitCommit,
                    RevalidateRecordCommit = true,
                };
            }
            RefreshRequestDequeuedForTest?.Invoke();
            await _startupComplete.Task.ConfigureAwait(false);
            RefreshRequestPassedStartupBarrierForTest?.Invoke();
            if (req.FullRebuild)
            {
                // The channel continuation may run on the shared ThreadPool. Never execute the
                // synchronous repository build on that continuation: the builder's unrestricted
                // parser fan-out must coexist with lightweight MCP status requests.
                await Task.Factory.StartNew(FullRebuildInPump, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default)
                    .ConfigureAwait(false);
                FullRebuildCompletedForTest?.Invoke();
                continue;
            }
            if (req.RevalidateRecordCommit)
            {
                GitInfo.HeadSnapshot current;
                bool requeued;
                long recoveryGitSnapshotGeneration;
                lock (_gitHeadObservationGate)
                {
                    current = ReadGitHeadSnapshot();
                    recoveryGitSnapshotGeneration =
                        ++_refreshRecoveryGitSnapshotGeneration;
                    if (current.IsResolved)
                    {
                        string currentHead = current.Commit!;
                        // The request is already active, while older Git observations may be
                        // waiting in the channel. Publish this newly sampled tuple only by
                        // appending it under the same observation gate; applying it in-place would
                        // let an older queued request overwrite it afterward.
                        RefreshRequest orderedRecovery = req with
                        {
                            RecordCommit = currentHead,
                            RevalidateRecordCommit = false,
                            RecordBranch = current.Branch,
                            RecordBranchKnown = true,
                            PublishRevalidatedGitSnapshot = true,
                            RecoveryGitSnapshotGeneration =
                                recoveryGitSnapshotGeneration,
                        };
                        requeued = _refreshQueue.Writer.TryWrite(orderedRecovery);
                        if (requeued)
                        {
                            _refreshRecoveryPendingGitCommit = currentHead;
                            _latestObservedGitHead = current;
                        }
                    }
                    else
                    {
                        requeued = false;
                    }
                }
                if (!current.IsResolved)
                {
                    Volatile.Write(
                        ref _refreshRecoveryGitRevalidationRequiredGeneration,
                        recoveryGitSnapshotGeneration);
                    _error = RefreshInputUnavailableCause;
                    _state = "stale";
                    _log("Git HEAD is temporarily unavailable during stale-index recovery; " +
                         "the pending baseline remains uncommitted and recovery stays paced.");
                    ScheduleRefreshRecoverySweep(
                        _refreshIncompletePaths?.FirstOrDefault()
                        ?? "unavailable workspace input");
                    req.CompletionForTest?.TrySetResult();
                    continue;
                }
                if (!requeued)
                {
                    req.CompletionForTest?.TrySetResult();
                }
                continue;
            }
            if (_store is null)
            {
                req.CompletionForTest?.TrySetResult();
                continue;
            }
            string previous = _state;
            // x5ls.1.2: one outcome frame per refresh batch. The reason comes from the
            // PRODUCER's explicit label (review B2: shape-derivation mislabeled tool requests
            // as watcher batches and git fallback sweeps as plain sweeps) — the shape mapping
            // below is only a fallback for unlabeled future sites, not sanctioned semantics.
            string refreshId = Guid.NewGuid().ToString();
            string refreshReason = req.Reason ?? (req.Paths is null ? "full_sweep"
                : req.RecordCommit is not null ? "git_head" : "watcher_batch");
            var refreshWall = System.Diagnostics.Stopwatch.StartNew(); // review B3: failures report MEASURED elapsed
            int captureRetry = 0;
            if (Volatile.Read(ref _refreshIncompletePersisted) == 0 &&
                !MarkRefreshIncomplete(RefreshSweepPendingCause, Array.Empty<string>(),
                    pathCountIsLowerBound: false))
            {
                // Do not mutate rows when followers cannot first observe that a refresh epoch is
                // pending. The old committed database remains intact and the writer stays stale.
                _error = RefreshSweepPendingCause;
                _state = "stale";
                _log("Refresh refused because its follower-visible pending marker could not be persisted.");
                EmitRefreshSnapshot(refreshId, refreshReason, "failed", batchProcessed: 0,
                    elapsedMs: refreshWall.ElapsedMilliseconds,
                    errorCode: RefreshSweepPendingCause);
                req.CompletionForTest?.TrySetResult();
                continue;
            }
            while (true)
            {
                bool retryCapture = false;
                BeginIndexMutation();
                try
                {
                    _state = "refreshing";
                    var result = WorkspaceFileReaderForTest is { } reader
                        ? DeltaRefresher.RefreshWithReaderForTest(_store, _workspaceRoot,
                            req.Paths, reader, _log, recordCommit: req.RecordCommit,
                            recordBranch: req.RecordBranch,
                            recordBranchKnown: req.RecordBranchKnown)
                        : DeltaRefresher.Refresh(_store, _workspaceRoot, req.Paths, _log,
                            recordCommit: req.RecordCommit, recordBranch: req.RecordBranch,
                            recordBranchKnown: req.RecordBranchKnown);
                    // z4c: count what was ACTUALLY applied (the refresh result), not what was
                    // requested — a sweep request has no path count, and hash-identical paths are
                    // rightly skipped without being "processed".
                    Interlocked.Add(ref _pendingProcessed, result.AddedFiles +
                        result.ChangedFiles + result.DeletedFiles);
                    _lastRefreshUtc = result.RefreshedAtUtc ?? DateTime.UtcNow.ToString("O");
                    if (result.AddedFiles + result.ChangedFiles + result.DeletedFiles > 0)
                    {
                        _log($"Delta refresh: +{result.AddedFiles} ~{result.ChangedFiles} -{result.DeletedFiles} " +
                             $"(projects rebuilt: {result.ProjectsRefreshed}) in {result.Elapsed.TotalMilliseconds:F0}ms");
                    }
                    // Record the reflected commit only after a complete reconcile — so the diff
                    // baseline never advances past what the index actually contains.
                    if (req.RecordCommit is { } commit)
                    {
                        _indexedCommit = commit;
                        if (req.RecordBranchKnown)
                            _indexedBranch = req.RecordBranch;
                        _log($"Git baseline recorded: {Short(commit)}."); // 17zd-b: close the loop visibly
                    }
                    long requiredGitGeneration = Volatile.Read(
                        ref _refreshRecoveryGitRevalidationRequiredGeneration);
                    bool gitRecoverySnapshotIsCurrent =
                        requiredGitGeneration == 0 ||
                        req.RecoveryGitSnapshotGeneration >= requiredGitGeneration;
                    if (gitRecoverySnapshotIsCurrent && TryClearRefreshIncomplete())
                    {
                        ResetRefreshRecoverySweepBackoff();
                        _error = null;
                        _state = "ready";
                        EmitRefreshSnapshot(refreshId, refreshReason, "completed", // x5ls.1.2
                            result.AddedFiles + result.ChangedFiles + result.DeletedFiles,
                            (long)result.Elapsed.TotalMilliseconds, errorCode: null);
                    }
                    else
                    {
                        // Row changes committed successfully, but either a newer unavailable Git
                        // generation forbids clearing the latch or its durable delete failed. Keep
                        // serving the result conservatively as stale; do not mislabel successful row
                        // publication as refresh_failed.
                        if (!gitRecoverySnapshotIsCurrent)
                        {
                            _log("Refresh committed, but a newer unresolved Git HEAD recovery " +
                                 "observation keeps the index stale and paced recovery armed.");
                        }
                        _error = _refreshIncompleteReason;
                        _state = "stale";
                        EmitRefreshSnapshot(refreshId, refreshReason, "completed", // x5ls.1.2
                            result.AddedFiles + result.ChangedFiles + result.DeletedFiles,
                            (long)result.Elapsed.TotalMilliseconds,
                            errorCode: _refreshIncompleteReason);
                        if (string.Equals(_refreshIncompleteReason,
                                RefreshInputUnavailableCause, StringComparison.Ordinal))
                        {
                            ScheduleRefreshRecoverySweep(
                                _refreshIncompletePaths?.FirstOrDefault()
                                ?? "unavailable workspace input");
                        }
                    }
                }
                catch (RefreshInputUnavailableException ex)
                {
                    RefreshInputFailureBeforeLatchForTest?.Invoke();
                    MarkRefreshIncomplete(RefreshInputUnavailableCause, [ex.Path],
                        pathCountIsLowerBound: true);
                    _error = RefreshInputUnavailableCause;
                    if (req.RecordCommit is { } failedGitCommit)
                        _refreshRecoveryPendingGitCommit = failedGitCommit;
                    bool timerInitiatedRecovery = req.TimerInitiatedRecovery;
                    if (!timerInitiatedRecovery &&
                        captureRetry < DeltaRefresher.RefreshInputRetryDelays.Length)
                    {
                        retryCapture = true;
                        _log($"Source capture unavailable for {ex.Path}; retrying complete " +
                             $"refresh request after " +
                             $"{DeltaRefresher.RefreshInputRetryDelays[captureRetry].TotalMilliseconds:F0}ms.");
                    }
                    else
                    {
                        _state = "stale";
                        _log(timerInitiatedRecovery
                            ? $"Source capture remains unavailable for {ex.Path}; scheduling " +
                              "the next paced recovery sweep."
                            : $"Source capture unavailable for {ex.Path}; bounded refresh " +
                              "retries exhausted; scheduling a complete recovery sweep.");
                        EmitRefreshSnapshot(refreshId, refreshReason, "failed",
                            batchProcessed: 0, elapsedMs: refreshWall.ElapsedMilliseconds,
                            errorCode: RefreshInputUnavailableCause);
                        ScheduleRefreshRecoverySweep(ex.Path);
                    }
                }
                catch (RefreshInputOversizedException ex)
                {
                    RefreshInputFailureBeforeLatchForTest?.Invoke();
                    MarkRefreshIncomplete(RefreshInputOversizedCause, [ex.Path],
                        pathCountIsLowerBound: true);
                    _error = RefreshInputOversizedCause;
                    _state = "stale";
                    _log($"Source capture exceeds the configured byte limit for {ex.Path}.");
                    EmitRefreshSnapshot(refreshId, refreshReason, "failed",
                        batchProcessed: 0, elapsedMs: refreshWall.ElapsedMilliseconds,
                        errorCode: RefreshInputOversizedCause);
                }
                catch (Exception ex)
                {
                    // Type-name only, like the startup path (9vw) — no ex.Message internals to clients.
                    _error = _refreshIncompleteReason ??
                        $"{ex.GetType().Name} during delta refresh (see server log)";
                    _state = _refreshIncompleteReason is not null
                        ? "stale"
                        : previous == "ready" ? "ready" : previous;
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

                if (!retryCapture) break;
                await Task.Delay(DeltaRefresher.RefreshInputRetryDelays[captureRetry++])
                    .ConfigureAwait(false);
            }
            req.CompletionForTest?.TrySetResult();
        }
    }

    private bool MarkRefreshIncomplete(string reason, IEnumerable<string> paths,
        bool pathCountIsLowerBound)
    {
        string[] distinct = paths
            .Distinct(WorkspacePaths.FileSystemPathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        _refreshIncompletePaths = distinct.Take(RefreshIncompletePathLimit).ToArray();
        Volatile.Write(ref _refreshIncompletePathCount, distinct.Length);
        Volatile.Write(ref _refreshIncompletePathCountIsLowerBound,
            pathCountIsLowerBound ? 1 : 0);
        _refreshIncompleteReason = reason;
        if (_store is null) return false;
        try
        {
            using var tx = _store.BeginTransaction();
            _store.SetMeta(tx, RefreshIncompleteReasonMeta, reason);
            _store.SetMeta(tx, RefreshIncompletePathsMeta,
                System.Text.Json.JsonSerializer.Serialize(_refreshIncompletePaths));
            _store.SetMeta(tx, RefreshIncompletePathCountMeta,
                distinct.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _store.SetMeta(tx, RefreshIncompletePathCountLowerBoundMeta,
                pathCountIsLowerBound ? "1" : "0");
            RefreshIncompleteBeforeCommitForTest?.Invoke(reason);
            tx.Commit();
            Volatile.Write(ref _refreshIncompletePersisted, 1);
            return true;
        }
        catch (Exception ex)
        {
            // The writer still reports the specific in-memory latch. The already-committed sweep
            // marker remains follower-visible if this was an unavailable/oversized refinement.
            _log($"Could not persist incomplete-source refresh state: {ex}");
            return false;
        }
    }

    internal static void PersistRefreshSweepPending(IndexStore store)
    {
        using var tx = store.BeginTransaction();
        store.SetMeta(tx, RefreshIncompleteReasonMeta, RefreshSweepPendingCause);
        store.SetMeta(tx, RefreshIncompletePathsMeta, "[]");
        store.SetMeta(tx, RefreshIncompletePathCountMeta, "0");
        store.SetMeta(tx, RefreshIncompletePathCountLowerBoundMeta, "0");
        tx.Commit();
    }

    private bool TryClearRefreshIncomplete()
    {
        if (_refreshIncompleteReason is null && _refreshIncompletePaths is null &&
            Volatile.Read(ref _refreshIncompletePathCount) == 0)
            return true;
        try
        {
            if (_store is not null)
            {
                using var tx = _store.BeginTransaction();
                _store.DeleteMeta(tx, RefreshIncompleteReasonMeta);
                _store.DeleteMeta(tx, RefreshIncompletePathsMeta);
                _store.DeleteMeta(tx, RefreshIncompletePathCountMeta);
                _store.DeleteMeta(tx, RefreshIncompletePathCountLowerBoundMeta);
                ClearRefreshIncompleteBeforeCommitForTest?.Invoke();
                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            _log($"Could not clear incomplete-source refresh state: {ex}");
            return false;
        }
        _refreshIncompleteReason = null;
        _refreshIncompletePaths = null;
        Volatile.Write(ref _refreshIncompletePathCount, 0);
        Volatile.Write(ref _refreshIncompletePathCountIsLowerBound, 0);
        Volatile.Write(ref _refreshIncompletePersisted, 0);
        return true;
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

    /// <summary>The pump-side rebuild-from-scratch (tky). Windows/Linux workspace-local
    /// destinations build into a pinned private file while the last publication remains readable,
    /// then close the local read gate, publish B, drain bounded old handles, and atomically install
    /// the complete stage. Unsupported destinations retain the established in-place fallback.</summary>
    private void FullRebuildInPump()
    {
        FullRebuildBeforeAnchoredDestinationOpenForTest?.Invoke();
        if (AnchoredIndexDestination.TryOpen(_workspaceRoot, _workspaceRoot, _dbPath,
                createIndexDirectory: false, out AnchoredIndexDestination? destination))
        {
            AnchoredIndexDestination anchored = destination!;
            using (anchored)
                FullRebuildStagedInPump(anchored);
            return;
        }

        if (!CanUseDirectFullRebuildFallback())
        {
            _error = "full rebuild failed (see server log)";
            _state = "failed";
            _log("Full rebuild refused: required anchored publication or workspace authority " +
                 "could not be verified.");
            return;
        }

        bool mutationActive = false;
        try
        {
            mutationActive = true;
            BeginFullRebuildPublicationBoundary();
            FullRebuildDirectInPump();
        }
        finally
        {
            if (_store is not null && _state is "ready" or "refreshing" or "stale")
                ReopenWriterReadsAfterPublication();
            if (mutationActive) EndIndexMutation();
        }
    }

    private void BeginFullRebuildPublicationBoundary()
    {
        BeginIndexMutation();
        lock (_reviewSnapshotGate)
            Volatile.Write(ref _writerReadsAllowed, 0);
    }

    private void DrainLocalReadersAtPublication(Stopwatch publicationWait,
        TimeSpan publicationTimeout, Action waitingForReaders)
    {
        bool hasActiveReaders;
        lock (_reviewSnapshotGate)
        {
            hasActiveReaders = _activeReviewSnapshots > 0 || _activeWriterQueries > 0;
            if (hasActiveReaders)
                FullRebuildWaitingForLocalSnapshotsForTest?.Invoke();
        }
        if (hasActiveReaders) waitingForReaders();
        if (!WaitForPublicationReaders(_noActiveReviewSnapshots, publicationWait,
                publicationTimeout) ||
            !WaitForPublicationReaders(_noActiveWriterQueries, publicationWait,
                publicationTimeout))
            throw new TimeoutException(
                "timed out waiting for local index readers to drain before publication");
        int activeAtBoundary;
        lock (_reviewSnapshotGate)
            activeAtBoundary = _activeReviewSnapshots + _activeWriterQueries;
        FullRebuildDestructiveBoundaryForTest?.Invoke(activeAtBoundary);
    }

    private static bool WaitForPublicationReaders(ManualResetEventSlim readersDrained,
        Stopwatch publicationWait, TimeSpan publicationTimeout)
    {
        if (readersDrained.IsSet) return true;
        TimeSpan remaining = RemainingPublicationWait(publicationWait, publicationTimeout);
        return remaining > TimeSpan.Zero && readersDrained.Wait(remaining);
    }

    private static TimeSpan RemainingPublicationWait(Stopwatch publicationWait,
        TimeSpan publicationTimeout)
    {
        TimeSpan remaining = publicationTimeout - publicationWait.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void ResetCachedFreshnessMetadata()
    {
        _lastRefreshUtc = null;
        _indexedCommit = null;
        _indexedBranch = null;
    }

    private void ReopenWriterReadsAfterPublication()
    {
        lock (_reviewSnapshotGate)
            Volatile.Write(ref _writerReadsAllowed, 1);
    }

    private void FullRebuildStagedInPump(AnchoredIndexDestination destination)
    {
        _log("Full rebuild requested (refresh_index force:'full') — building a private replacement index.");
        string priorState = _state;
        bool priorPublicationReadable = _store is not null &&
            priorState is "ready" or "refreshing" or "stale";
        _state = "building";
        _buildProgress = new BuildProgress();
        BuildProgress rebuildProgress = _buildProgress;
        string? buildId = null;
        System.Threading.Timer? progressTimer = null;
        bool buildCompleted = false;
        bool buildCancelled = false;
        bool mutationActive = false;
        bool oldStoreDisposed = false;
        bool stageInstalled = false;
        try
        {
            EnsureStagedDestinationAuthority(destination);
            string stagePath = destination.CreateStagePath();
            (buildId, progressTimer) = BeginBuildTelemetry("explicit_full", rebuildProgress);
            FullRebuildAfterTelemetryStartedForTest?.Invoke();
            FullRebuildPrivateStageReadyForTest?.Invoke(stagePath);
            BuildResult result = IndexBuilder.BuildOwned(
                destination.WorkspaceReadPath, stagePath, _log,
                rebuildProgress, reservedPrivateStage: true,
                publishedWorkspaceRoot: _workspaceRoot);
            _log($"Private full rebuild ready: {result.CsFiles} C# + {result.FsFiles} F# files, " +
                 $"{result.Symbols} symbols in {result.TotalTime.TotalSeconds:F0}s; publishing ...");
            FullRebuildPrivateStageCompletedForTest?.Invoke();
            EnsureStagedDestinationAuthority(destination);

            // Keep the old publication and claim R throughout scanning, parsing, and bulk SQL.
            // Only the short installation envelope closes new local reads, drains registered
            // snapshots, then publishes B immediately before releasing/replacing SQLite handles.
            mutationActive = true;
            var publicationWait = Stopwatch.StartNew();
            TimeSpan publicationTimeout = FullRebuildPublicationTimeoutForTest;
            BeginFullRebuildPublicationBoundary();
            _destinationClaim?.SetRebuilding();
            DrainLocalReadersAtPublication(publicationWait, publicationTimeout, () =>
            {
                _error = "full rebuild is waiting for existing index readers to drain";
                _log("Full rebuild is waiting for existing index readers to drain.");
            });
            _store?.Dispose();
            _store = null;
            oldStoreDisposed = true;
            ClearOwnedDatabasePools();
            EnsureStagedDestinationAuthority(destination);
            FullRebuildBeforeStageInstallForTest?.Invoke();
            EnsureStagedDestinationAuthority(destination);
            TimeSpan installTimeout =
                RemainingPublicationWait(publicationWait, publicationTimeout);
            if (installTimeout <= TimeSpan.Zero ||
                !destination.InstallStage(installTimeout, waitingForReaders: () =>
                {
                    _error = "full rebuild is waiting for existing index readers to drain";
                    _log("Full rebuild is waiting for existing index readers to drain; " +
                         "new follower requests will retry against the replacement.");
                }))
                throw new IOException("staged index publication could not replace the live database");
            stageInstalled = true;
            FullRebuildAfterStageInstallForTest?.Invoke();
            EnsureInstalledDestinationAuthority(destination);

            OpenFullRebuildPublication();
            ReopenWriterReadsAfterPublication();
            EndIndexMutation();
            mutationActive = false;

            _log($"Full rebuild done: {result.CsFiles} C# + {result.FsFiles} F# files, " +
                 $"{result.Symbols} symbols in {result.TotalTime.TotalSeconds:F0}s");
            DrainDisposeBuildTimer(progressTimer);
            EmitBuildCompleted(buildId, "explicit_full", rebuildProgress,
                (long)result.TotalTime.TotalMilliseconds);
            buildCompleted = true;
        }
        catch (OperationCanceledException ex)
        {
            buildCancelled = true;
            RestorePriorPublicationAfterStagedFailure();
            _log($"Full rebuild cancelled: {ex.Message}");
        }
        catch (Exception ex)
        {
            RestorePriorPublicationAfterStagedFailure();
            _log($"Full rebuild failed: {ex}");
        }
        finally
        {
            if (mutationActive)
            {
                if (_store is not null && _state is "ready" or "refreshing" or "stale")
                    ReopenWriterReadsAfterPublication();
                EndIndexMutation();
            }
            if (progressTimer is not null) DrainDisposeBuildTimer(progressTimer);
            if (buildId is not null && !buildCompleted)
            {
                if (buildCancelled)
                    EmitBuildCancelled(buildId, "explicit_full", rebuildProgress);
                else
                    EmitBuildFailed(buildId, "explicit_full", rebuildProgress);
            }
            _buildProgress = null;
        }
        return;

        void RestorePriorPublicationAfterStagedFailure()
        {
            if (_disposed || !HasSafeWorkspaceAuthority() ||
                !HasSafeLiveDatabaseAuthority())
            {
                _error = buildCancelled
                    ? "full rebuild cancelled"
                    : "full rebuild failed (see server log)";
                _state = "failed";
                return;
            }

            if (!stageInstalled && priorPublicationReadable)
            {
                try
                {
                    if (oldStoreDisposed && _store is null)
                        _store = new IndexStore(_databaseIoPath, createNew: false);
                    if (_store!.GetMeta(RefreshIncompleteReasonMeta) is null)
                        PersistRefreshSweepPending(_store);
                    ResetCachedFreshnessMetadata();
                    CacheMeta(_store);
                    _state = "stale";
                    _error = "full rebuild failed; the previous index remains available";
                    if (_watcher is null) StartWatcher();
                    _refreshQueue.Writer.TryWrite(
                        new RefreshRequest(null, Reason: "full_sweep"));
                    if (_gitWatcher is null) InitGitTracking();
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(IndexManager));
                    _destinationClaim?.SetReady();
                    return;
                }
                catch (Exception restoreError)
                {
                    _log($"Could not reopen the previous index after staged rebuild failure: " +
                         $"{restoreError}");
                }
            }

            _error = buildCancelled
                ? "full rebuild cancelled"
                : "full rebuild failed (see server log)";
            _state = "failed";
        }
    }

    /// <summary>Compatibility fallback for destinations that cannot use the retained
    /// Windows/Linux anchored stage (currently macOS and database paths outside the workspace).
    /// The caller has already closed new local reads; this method drains every writer query and
    /// review snapshot admitted before the publication boundary.</summary>
    private void FullRebuildDirectInPump()
    {
        _log("Full rebuild requested (refresh_index force:'full') — rebuilding the index from scratch.");
        string priorState = _state;
        bool priorPublicationReadable = _store is not null &&
            priorState is "ready" or "refreshing" or "stale";
        _state = "building";
        // x5ls.1.2 review B5: the pre-build teardown (store dispose and pool clears) is NOT
        // building — the build lifecycle (and the phase clock that feeds phaseDurations) starts
        // at BuildOwned. During teardown, state is 'building'
        // with no progress object: Health() honestly shows no progress rather than a
        // "scanning" phase silently absorbing teardown time.
        _buildProgress = null;
        BuildProgress? rebuildProgress = null;
        string? buildId = null;
        System.Threading.Timer? progressTimer = null;
        bool buildCompleted = false;
        bool buildCancelled = false;
        try
        {
            // Publish writer intent before releasing our own SQLite handles. Followers inspect
            // this claim before and after every open, so no new reader can barge while the
            // already-open bounded snapshots drain through the replacement boundary.
            _destinationClaim?.SetRebuilding();
            var publicationWait = Stopwatch.StartNew();
            DrainLocalReadersAtPublication(publicationWait,
                FullRebuildPublicationTimeoutForTest, () =>
                {
                    _error = "full rebuild is waiting for existing index readers to drain";
                    _log("Full rebuild is waiting for existing index readers to drain.");
                });
            _store?.Dispose();
            _store = null;
            ClearOwnedDatabasePools();
            EnsureDatabaseAuthority();

            rebuildProgress = new BuildProgress();
            _buildProgress = rebuildProgress;
            (buildId, progressTimer) = BeginBuildTelemetry("explicit_full", rebuildProgress); // x5ls.1.2
            FullRebuildAfterTelemetryStartedForTest?.Invoke();
            var result = IndexBuilder.BuildOwned(_workspaceRoot, _databaseIoPath, _log,
                rebuildProgress, waitingForReaders: () =>
                {
                    _error = "full rebuild is waiting for existing index readers to drain";
                    _log("Full rebuild is waiting for existing index readers to drain; " +
                         "new follower requests will retry against the replacement.");
                });
            _error = null;
            _log($"Full rebuild done: {result.CsFiles} C# + {result.FsFiles} F# files, " +
                 $"{result.Symbols} symbols in {result.TotalTime.TotalSeconds:F0}s");
            // Review B1: drain the ticker BEFORE the terminal frame (no progress after completed).
            DrainDisposeBuildTimer(progressTimer);
            EmitBuildCompleted(
                buildId,
                "explicit_full",
                rebuildProgress,
                (long)result.TotalTime.TotalMilliseconds);
            buildCompleted = true;

            OpenFullRebuildPublication();
        }
        catch (OperationCanceledException ex)
        {
            buildCancelled = true;
            _error = "full rebuild cancelled";
            _state = "failed";
            _log($"Full rebuild cancelled: {ex.Message}");
        }
        catch (TimeoutException ex) when (priorPublicationReadable && _store is not null)
        {
            if (_store.GetMeta(RefreshIncompleteReasonMeta) is null)
                PersistRefreshSweepPending(_store);
            ResetCachedFreshnessMetadata();
            CacheMeta(_store);
            _state = "stale";
            _error = "full rebuild timed out; the previous index remains available";
            _refreshQueue.Writer.TryWrite(new RefreshRequest(null, Reason: "full_sweep"));
            _destinationClaim?.SetReady();
            _log($"Full rebuild publication timed out before replacing the prior index: {ex}");
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
            if (buildId is not null && !buildCompleted)
            {
                if (buildCancelled)
                    EmitBuildCancelled(buildId, "explicit_full", rebuildProgress!);
                else
                    EmitBuildFailed(buildId, "explicit_full", rebuildProgress!);
            }
            _buildProgress = null;
        }
    }

    private void OpenFullRebuildPublication()
    {
        EnsureLivePublicationAuthority();
        var store = new IndexStore(_databaseIoPath, createNew: false);
        try
        {
            // Reset cached meta BEFORE CacheMeta: its ??= semantics would otherwise resurrect
            // values from the replaced index (stale indexed_commit on a fresh database).
            ResetCachedFreshnessMetadata();
            CacheMeta(store);
            EnsureLivePublicationAuthority();
            _store = store;
            // A rebuild replaces the database that the pending unavailable-source reconcile
            // targeted. Do not graft that obsolete target, or an ordered publication already
            // queued behind this rebuild, onto the post-build convergence sweep.
            _refreshRecoveryPendingGitCommit = null;
            Volatile.Write(ref _refreshRecoveryGitRevalidationRequiredGeneration, 0);
            Volatile.Write(ref _refreshRecoveryGitSnapshotRetiredThroughGeneration,
                Volatile.Read(ref _refreshRecoveryGitSnapshotGeneration));
            // BuildOwned persists refresh_sweep_pending before it writes the compatibility barrier.
            // Keep the replacement queryable only as stale until the queued detect-all succeeds.
            _error = _refreshIncompleteReason;
            _state = "stale";

            if (_watcher is null) StartWatcher();
            _refreshQueue.Writer.TryWrite(new RefreshRequest(null, Reason: "full_sweep"));
            if (_gitWatcher is null)
            {
                InitGitTracking();
            }
            else if (_gitDir is not null)
            {
                GitInfo.HeadSnapshot snapshot;
                lock (_gitHeadObservationGate)
                {
                    snapshot = ReadGitHeadSnapshot();
                    if (snapshot.IsResolved)
                    {
                        _latestObservedGitHead = snapshot;
                        QueueGitMetadataRefresh(snapshot);
                    }
                }
                if (!snapshot.IsResolved) ScheduleGitHeadRetry();
            }
            EnsureLivePublicationAuthority();
            _destinationClaim?.SetReady();
        }
        catch
        {
            if (ReferenceEquals(_store, store)) _store = null;
            store.Dispose();
            throw;
        }
    }

    /// <summary>Queues a manual refresh (targeted paths, or full detection sweep when null).</summary>
    public bool RequestRefresh(IReadOnlyCollection<string>? paths = null)
    {
        if (!IsWriter || _disposed) return false;
        return _refreshQueue.Writer.TryWrite(new RefreshRequest(paths, Reason: "explicit"));
    }

    /// <summary>Queues a targeted refresh and exposes completion of that exact pump request to
    /// test assemblies. Production callers use <see cref="RequestRefresh"/> and observe the
    /// public freshness surface; tests that mutate a live manager need a FIFO barrier that cannot
    /// be confused with an unrelated applied-delta counter increment.</summary>
    internal bool RequestRefreshForTest(IReadOnlyCollection<string>? paths, out Task completion)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = signal.Task;
        if (!IsWriter || _disposed) return false;
        if (_refreshQueue.Writer.TryWrite(new RefreshRequest(paths, Reason: "explicit",
                CompletionForTest: signal)))
            return true;
        signal.TrySetResult();
        return false;
    }

    /// <summary>Queues the same commit-bearing request used by Git reconciliation while exposing
    /// completion to tests. This pins the invariant that an incomplete source capture cannot
    /// advance either the cached or transactional indexed_commit baseline.</summary>
    internal bool RequestGitRefreshForTest(IReadOnlyCollection<string>? paths,
        string commit, out Task completion)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = signal.Task;
        if (!IsWriter || _disposed) return false;
        if (_refreshQueue.Writer.TryWrite(new RefreshRequest(paths, commit,
                Reason: "git_head", CompletionForTest: signal)))
            return true;
        signal.TrySetResult();
        return false;
    }

    internal void NotifyGitHeadChangedForTest() => OnGitHeadMaybeChanged();

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
            if (IsFollower && !FollowerDestinationAllowsRead()) return false;
            if (IsFollower && !TryRefreshFollowerMetadata(force: false)) return false;
            string state = State;
            bool readableState = state is "ready" or "refreshing" or "stale" ||
                (!IsFollower && state == "building" && _store is not null);
            return readableState &&
                   (IsFollower || Volatile.Read(ref _writerReadsAllowed) != 0) &&
                   TryGetSafeDatabaseStatus(out IndexLeaseIdentity? current, out _) &&
                   current?.DatabaseIdentity is not null;
        }
    }

    public IndexQueries OpenQueries()
    {
        if (!IsFollower)
        {
            lock (_reviewSnapshotGate)
            {
                if (Volatile.Read(ref _writerReadsAllowed) == 0)
                    throw new IOException(_state == "failed"
                        ? _error ?? "index is unavailable after a failed rebuild"
                        : WriterPublicationUnavailable);
                _activeWriterQueries++;
                if (_activeWriterQueries == 1) _noActiveWriterQueries.Reset();
            }

            Action? registeredRelease = ReleaseWriterQuery;
            void ReleaseRegisteredQueryOnce() =>
                Interlocked.Exchange(ref registeredRelease, null)?.Invoke();
            try
            {
                WriterQueryAfterRegistrationForTest?.Invoke();
                EnsureDatabaseAuthority();
                // The connection owns the same idempotent release after successful construction.
                return new IndexQueries(_databaseIoPath, pinReadSnapshot: false,
                    releasePublicationLease: ReleaseRegisteredQueryOnce);
            }
            catch
            {
                ReleaseRegisteredQueryOnce();
                throw;
            }
        }

        FollowerMetadataBeforeGateForTest?.Invoke();
        lock (_followerMetadataGate)
        {
            IndexQueries? queries = null;
            try
            {
                if (!FollowerDestinationAllowsRead())
                    throw new IOException(FollowerIndexUnavailable);
                EnsureDatabaseAuthority();
                if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? before, out _) ||
                    before?.DatabaseIdentity is null)
                    throw new IOException(FollowerIndexUnavailable);
                queries = new IndexQueries(_databaseIoPath, pinReadSnapshot: false,
                    pooling: false);
                IndexMetadataSnapshot metadata = queries.ReadMetadata();
                if (!IsCompatibleFollowerMetadata(metadata) ||
                    !TryGetSafeDatabaseStatus(out IndexLeaseIdentity? after, out _) ||
                    after != before ||
                    !FollowerDestinationAllowsRead())
                    throw new IOException(FollowerIndexUnavailable);
                if (!PublishFollowerReady(metadata))
                    throw new IOException(FollowerIndexUnavailable);
                return queries;
            }
            catch
            {
                queries?.Dispose();
                PublishFollowerUnavailable();
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
        if (IsFollower && !FollowerDestinationAllowsRead()) return null;

        if (!TryGetSafeDatabaseStatus(out IndexLeaseIdentity? databaseBefore,
                out long databaseBytes) || databaseBefore?.DatabaseIdentity is null)
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
                    followerDatabaseAfter == databaseBefore &&
                    FollowerDestinationAllowsRead();
                health = FollowerHealth(metadata, databaseBytes);
            }
            else
            {
                health = Health();
            }
            long after = Volatile.Read(ref _refreshEpoch);
            if (followerStable && before == after && (after & 1) == 0 &&
                (health.State is "ready" or "stale" ||
                 (!IsFollower && health.State == "building" && _store is not null)))
            {
                var snapshot = new IndexReadSnapshot(queries, health,
                    ReleaseReviewSnapshot);
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

    private void ReleaseWriterQuery()
    {
        lock (_reviewSnapshotGate)
        {
            if (--_activeWriterQueries == 0) _noActiveWriterQueries.Set();
        }
    }

    private IndexHealth FollowerHealth(IndexMetadataSnapshot metadata, long dbBytes) =>
        FollowerHealthForTest(metadata, dbBytes, _workspaceRoot, _dbPath);

    internal static IndexHealth FollowerHealthForTest(IndexMetadataSnapshot metadata,
        long databaseBytes, string workspaceRoot, string databasePath) => new(
        metadata.RefreshIncompleteReason is null ? "ready" : "stale",
        metadata.IndexVersion, metadata.IndexedAtUtc, metadata.LastRefreshUtc,
        0, metadata.RefreshIncompleteReason, databaseBytes, workspaceRoot, databasePath,
        metadata.IndexedCommit, metadata.IndexedBranch, null, 0, FollowerAccessMode,
        metadata.RefreshIncompleteReason, metadata.RefreshIncompletePaths,
        metadata.RefreshIncompletePathCount,
        metadata.RefreshIncompletePathCountIsLowerBound);

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
                FollowerAccessMode, metadata?.RefreshIncompleteReason,
                metadata?.RefreshIncompletePaths,
                metadata?.RefreshIncompletePathCount ?? 0,
                metadata?.RefreshIncompletePathCountIsLowerBound ?? false);
        }

        // Progress only while genuinely building — a background refresh must never show a
        // cold-build progress bar (field design note; refresh honesty is bead z4c).
        var bp = _buildProgress;
        return new IndexHealth(
            _state, _indexVersion, _indexedAtUtc, _lastRefreshUtc,
            _watcher?.PendingCount ?? 0, _error, dbBytes, _workspaceRoot, _dbPath,
            _indexedCommit, _indexedBranch,
            _state == "building" && bp is not null ? bp.Snapshot() : null,
            Interlocked.Read(ref _pendingProcessed), _accessMode,
            _refreshIncompleteReason, _refreshIncompletePaths,
            Volatile.Read(ref _refreshIncompletePathCount),
            Volatile.Read(ref _refreshIncompletePathCountIsLowerBound) != 0,
            _startupBuildReason, _startupPriorSchema);
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
        WorkspaceWatcher? watcher;
        GitWatcher? gitWatcher;
        lock (_disposeLock)
        {
            _disposed = true; // block any in-flight watcher publication
            watcher = Interlocked.Exchange(ref _watcher, null);
            gitWatcher = Interlocked.Exchange(ref _gitWatcher, null);
        }
        Telemetry.Dispose();                 // epuc.1: flush the bounded stream (2s cap)
        TelemetryIpc.Dispose();              // x5ls.1: stop the IPC producer (2s cap)
        gitWatcher?.Dispose();               // stop git HEAD signals
        _gitHeadRetry?.Dispose();            // 17zd: stop the null-HEAD retry (callback checks _disposed)
        _refreshRecoverySweepRetry?.Dispose(); // stop stale-input recovery retries
        watcher?.Dispose();                  // stop new events reaching the queue
        _refreshQueue.Writer.TryComplete();  // let the pump drain and exit its loop

        // Let the startup task settle first (it may still be opening the store), then wait
        // for the pump to actually stop using the store. Only dispose the store once both
        // have finished — otherwise leak it (the process is tearing down) rather than risk
        // a use-after-dispose on the single write connection.
        bool startDone = true, pumpDone = true;
        try { startDone = _startTask?.Wait(DisposeWaitTimeoutForTest) ?? true; } catch { /* faulted/cancelled */ }
        // The start task may have created the watchers after the first Dispose() calls above
        // saw null — tear those down too (Dispose is idempotent).
        Interlocked.Exchange(ref _gitWatcher, null)?.Dispose();
        Interlocked.Exchange(ref _watcher, null)?.Dispose();
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
                _followerDestinationBound = false;
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
                // The destination claim must remain live until every writer-owned SQLite handle
                // and pool has drained. Remove it before the workspace mutex is released so a
                // successor cannot observe an ownerless claim while the old writer still owns
                // the workspace.
                _destinationClaim?.Dispose();
                _directoryAuthority?.Dispose();
                _ownershipLease?.Dispose();
            }
            catch (Exception ex)
            {
                _log($"IndexManager cleanup retained ownership after authority release failed: {ex.GetType().Name}");
                return;
            }
            _store = null;
            _destinationClaim = null;
            _ownershipLease = null;
            _directoryAuthority = null;
            _authorityDirectoryIdentity = null;
            _databaseIoPath = _dbPath;
            _ownedResourcesReleased = true;
        }
    }
}
