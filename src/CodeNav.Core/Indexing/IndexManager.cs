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
    long PendingProcessed = 0);

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

    // A refresh unit: Paths=null is a full detect-all sweep; RecordCommit, when set, is written
    // as the reflected git commit after the batch succeeds (git-aware reconcile). FullRebuild
    // (tky) throws the whole index away and rebuilds from scratch — the in-band recovery hatch
    // (field: parked at state 'failed' with no remedy but shell rm -rf .codenav).
    private sealed record RefreshRequest(IReadOnlyCollection<string>? Paths, string? RecordCommit = null,
        bool FullRebuild = false);

    private readonly string _workspaceRoot;
    private readonly string _dbPath;
    private string _databaseIoPath;
    private readonly Action<string> _log;
    private readonly Channel<RefreshRequest> _refreshQueue = Channel.CreateUnbounded<RefreshRequest>();

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
    internal Action? FullRebuildWaitingForReviewSnapshotsForTest { get; set; }
    internal Action<int>? FullRebuildDestructiveBoundaryForTest { get; set; }
    internal Action? FullRebuildCompletedForTest { get; set; }
    internal Action? StartupAfterLeaseAcquiredForTest { get; set; }
    internal Action? CleanupBeforePoolClearForTest { get; set; }
    internal TimeSpan DisposeWaitTimeoutForTest { get; set; } = TimeSpan.FromSeconds(5);
    private readonly object _reviewSnapshotGate = new();
    private readonly ManualResetEventSlim _noActiveReviewSnapshots = new(initialState: true);
    private readonly ManualResetEventSlim _stableIndexEpoch = new(initialState: true);
    private int _activeReviewSnapshots;
    private readonly object _resourceReleaseLock = new();
    private bool _ownedResourcesReleased;

    public IndexManager(string workspaceRoot, string? dbPath = null, Action<string>? log = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _dbPath = Path.GetFullPath(dbPath ?? IndexBuilder.DefaultDbPath(_workspaceRoot));
        _databaseIoPath = _dbPath;
        _log = log ?? (_ => { });
    }

    public string WorkspaceRoot => _workspaceRoot;
    public string DbPath => _dbPath;
    internal string DatabaseIoPath
    {
        get
        {
            EnsureDatabaseAuthority();
            return _databaseIoPath;
        }
    }
    public string State => _state;

    public WorktreeIndexResult EnsureWorktreeIndex(string worktreePath, string mode,
        Action<string> log)
    {
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
            if (_disposed || _pump is not null || _startTask is not null) return;
            if (!IndexDirectoryAuthority.TryOpen(_dbPath, createDirectory: true,
                    out IndexDirectoryAuthority? authority) ||
                !authority!.TryGetLeaseIdentity(out IndexLeaseIdentity? leaseIdentity))
            {
                authority?.Dispose();
                // Sanitized like every startup error (9vw): fixed phrase, no filesystem internals.
                // "safely" not "without following links" — this gate also refuses a missing or
                // non-directory parent, not only link/reparse traversal; specifics go to the log.
                _error = "index destination could not be opened safely during index startup (see server log)";
                _state = "failed";
                _log($"Index startup refused: destination '{_dbPath}' is not a safely openable " +
                     "directory tree (missing or non-directory parent, link/reparse point, or " +
                     "inaccessible); force:'full' retries once the blocker clears.");
                return;
            }
            _directoryAuthority = authority;
            _authorityDirectoryIdentity = leaseIdentity!.DirectoryIdentity;
            _databaseIoPath = authority.DatabasePath;
            if (!IndexOwnershipLease.TryAcquire(_workspaceRoot, _dbPath, leaseIdentity,
                    out _ownershipLease))
            {
                authority.Dispose();
                _directoryAuthority = null;
                _authorityDirectoryIdentity = null;
                _databaseIoPath = _dbPath;
                _error = "another phoenix process owns this index";
                _state = "failed";
                _log("Index startup refused: another phoenix process owns this index.");
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
                _error = "index destination changed during ownership acquisition";
                _state = "failed";
                _log("Index startup refused: index destination changed during ownership acquisition.");
                return;
            }
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
                        }
                    }
                    catch (Exception ex)
                    {
                        _log($"Index open/version-check failed ({ex.Message}); rebuilding.");
                        build = true;
                    }
                }
                if (build)
                {
                    _state = "building";
                    // Live progress for the building window (bead two, field-requested during the
                    // v5 monolith reindex): published before the build starts, cleared after —
                    // Health() surfaces it only while state == "building".
                    _buildProgress = new BuildProgress();
                    _log($"Building index for {_workspaceRoot} ...");
                    var buildResult = IndexBuilder.BuildOwned(_workspaceRoot, _databaseIoPath, _log, _buildProgress);
                    _log($"Index built: {buildResult.CsFiles} files, {buildResult.Symbols} symbols in {buildResult.TotalTime.TotalSeconds:F0}s");
                    _buildProgress = null;
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
                _refreshQueue.Writer.TryWrite(new RefreshRequest(null)); // detect-all
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

    private void StartWatcher()
    {
        lock (_disposeLock)
        {
            if (_disposed) return; // Dispose already ran — don't publish a watcher it can't reach
            _watcher = new WorkspaceWatcher(
                _workspaceRoot,
                batch => _refreshQueue.Writer.TryWrite(new RefreshRequest(batch)),
                () => _refreshQueue.Writer.TryWrite(new RefreshRequest(null))); // overflow → detect-all sweep
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
                _refreshQueue.Writer.TryWrite(new RefreshRequest(Array.Empty<string>(), head));
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
            _gitWatcher = new GitWatcher(_gitDir, OnGitHeadMaybeChanged);
        }
    }

    /// <summary>Debounced GitWatcher callback: if HEAD actually moved, reconcile the diff.</summary>
    private void OnGitHeadMaybeChanged()
    {
        if (_disposed) return;
        string? head = GitInfo.HeadCommit(_workspaceRoot);
        if (head is null) return;
        string? current = _indexedCommit;
        if (current is null)
        {
            _refreshQueue.Writer.TryWrite(new RefreshRequest(Array.Empty<string>(), head));
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
            _refreshQueue.Writer.TryWrite(new RefreshRequest(null, to)); // full sweep, then record `to`
        }
        else
        {
            _refreshQueue.Writer.TryWrite(new RefreshRequest(changed, to));
        }
    }

    private static string Short(string commit) => commit.Length >= 8 ? commit[..8] : commit;

    private async Task PumpRefreshesAsync()
    {
        await foreach (var req in _refreshQueue.Reader.ReadAllAsync())
        {
            if (req.FullRebuild)
            {
                BeginIndexMutation();
                try
                {
                    bool waiting;
                    lock (_reviewSnapshotGate) waiting = _activeReviewSnapshots > 0;
                    if (waiting) FullRebuildWaitingForReviewSnapshotsForTest?.Invoke();
                    _noActiveReviewSnapshots.Wait();
                    int activeAtBoundary;
                    lock (_reviewSnapshotGate) activeAtBoundary = _activeReviewSnapshots;
                    FullRebuildDestructiveBoundaryForTest?.Invoke(activeAtBoundary);
                    FullRebuildInPump();
                    FullRebuildCompletedForTest?.Invoke();
                }
                finally { EndIndexMutation(); }
                continue;
            }
            if (_store is null) continue;
            string previous = _state;
            BeginIndexMutation();
            try
            {
                _state = "refreshing";
                var result = DeltaRefresher.Refresh(_store, _workspaceRoot, req.Paths, _log);
                // z4c: count what was ACTUALLY applied (the refresh result), not what was
                // requested — a sweep request has no path count, and hash-identical paths are
                // rightly skipped without being "processed".
                Interlocked.Add(ref _pendingProcessed, result.AddedFiles + result.ChangedFiles + result.DeletedFiles);
                _lastRefreshUtc = DateTime.UtcNow.ToString("O");
                if (result.AddedFiles + result.ChangedFiles + result.DeletedFiles > 0)
                {
                    _log($"Delta refresh: +{result.AddedFiles} ~{result.ChangedFiles} -{result.DeletedFiles} " +
                         $"(projects rebuilt: {result.ProjectsRefreshed}) in {result.Elapsed.TotalMilliseconds:F0}ms");
                }
                // Record the reflected commit only after a successful reconcile — so the diff
                // baseline never advances past what the index actually contains.
                if (req.RecordCommit is { } commit)
                {
                    var branch = GitInfo.HeadBranch(_workspaceRoot);
                    _store.SetMeta("indexed_commit", commit);
                    if (branch is not null) _store.SetMeta("indexed_branch", branch);
                    _indexedCommit = commit;
                    _indexedBranch = branch ?? _indexedBranch;
                }
                _state = "ready";
            }
            catch (Exception ex)
            {
                // Type-name only, like the startup path (9vw) — no ex.Message internals to clients.
                _error = $"{ex.GetType().Name} during delta refresh (see server log)";
                _state = previous == "ready" ? "ready" : previous;
                _log($"Delta refresh failed: {ex}");
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
        _buildProgress = new BuildProgress();
        try
        {
            _store?.Dispose();
            _store = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

            var result = IndexBuilder.BuildOwned(_workspaceRoot, _databaseIoPath, _log, _buildProgress);
            _log($"Full rebuild done: {result.CsFiles} files, {result.Symbols} symbols in {result.TotalTime.TotalSeconds:F0}s");

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
            _refreshQueue.Writer.TryWrite(new RefreshRequest(null));
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
            _buildProgress = null;
        }
    }

    /// <summary>Queues a manual refresh (targeted paths, or full detection sweep when null).</summary>
    public void RequestRefresh(IReadOnlyCollection<string>? paths = null) =>
        _refreshQueue.Writer.TryWrite(new RefreshRequest(paths));

    /// <summary>Queues a REBUILD-FROM-SCRATCH (tky): delete the db, run a full build, reopen.
    /// Serialized on the refresh pump like every other index mutation, so it can never race a
    /// delta batch. This is the in-band recovery hatch for a corrupt/failed index — including
    /// from state 'failed', where the pump is idle and the store may never have opened. When
    /// startup itself was REFUSED (destination authority or ownership), no pump exists to drain
    /// the queue — the hatch re-runs the full startup acquisition instead, so a transient
    /// blocker (AV lock, stale owner) stays recoverable in-band; a still-broken destination
    /// fails closed with the same sanitized shape.</summary>
    public void RequestFullRebuild()
    {
        lock (_disposeLock)
        {
            if (!_disposed && _pump is null && _startTask is null)
            {
                // Start returned before publishing the pump (authority/lease refusal): an
                // enqueued rebuild would never run. Re-enter the sole acquisition site.
                Start(forceRebuild: true);
                return;
            }
        }
        _refreshQueue.Writer.TryWrite(new RefreshRequest(null, FullRebuild: true));
    }

    public bool IsQueryable => _state is "ready" or "refreshing" &&
        TryGetSafeDatabaseStatus(out IndexLeaseIdentity? current, out _) &&
        current?.DatabaseIdentity is not null;

    public IndexQueries OpenQueries()
    {
        EnsureDatabaseAuthority();
        return new IndexQueries(_databaseIoPath);
    }

    /// <summary>
    /// Opens a read transaction whose rows and health metadata describe the same stable refresh
    /// epoch. Returns null when a refresh overlaps snapshot creation; callers should fail closed
    /// and invite a retry instead of combining evidence from different commits.
    /// </summary>
    public IndexReadSnapshot? TryOpenReviewSnapshot()
    {
        // Ordinary delta refreshes are short. Give the serialized pump a bounded chance to reach
        // its next committed epoch so review_pack does not fail spuriously just after a caller's
        // own refresh; a long rebuild still returns the bounded retry response.
        if (!_stableIndexEpoch.Wait(TimeSpan.FromSeconds(2))) return null;
        long before;
        lock (_reviewSnapshotGate)
        {
            before = Volatile.Read(ref _refreshEpoch);
            if ((before & 1) != 0 || !IsQueryable) return null;
            _activeReviewSnapshots++;
            if (_activeReviewSnapshots == 1) _noActiveReviewSnapshots.Reset();
        }

        IndexQueries? queries = null;
        bool transferred = false;
        try
        {
            EnsureDatabaseAuthority();
            queries = new IndexQueries(_databaseIoPath, pinReadSnapshot: true,
                ReviewSnapshotAfterQueryForTest);
            IndexHealth health = Health();
            long after = Volatile.Read(ref _refreshEpoch);
            if (before == after && (after & 1) == 0 && health.State == "ready")
            {
                var snapshot = new IndexReadSnapshot(queries, health, ReleaseReviewSnapshot);
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
                finally { ReleaseReviewSnapshot(); }
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

    public IndexHealth Health()
    {
        // Reads cached fields plus one authority-gated no-follow metadata snapshot. Never inspect
        // the visible database path after its destination authority has changed; macOS reports a
        // conservative zero because it has no retained directory handle for an anchored size read.
        _ = TryGetSafeDatabaseStatus(out _, out long dbBytes);

        // Progress only while genuinely building — a background refresh must never show a
        // cold-build progress bar (field design note; refresh honesty is bead z4c).
        var bp = _buildProgress;
        return new IndexHealth(
            _state, _indexVersion, _indexedAtUtc, _lastRefreshUtc,
            _watcher?.PendingCount ?? 0, _error, dbBytes, _workspaceRoot, _dbPath,
            _indexedCommit, _indexedBranch,
            _state == "building" && bp is not null ? bp.Snapshot() : null,
            Interlocked.Read(ref _pendingProcessed));
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
        _gitWatcher?.Dispose();              // stop git HEAD signals
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
            try
            {
                _store?.Dispose();
                CleanupBeforePoolClearForTest?.Invoke();
                // Release every idle native SQLite handle before releasing the cross-process
                // lease. Otherwise a second Phoenix could legitimately acquire the lease while
                // this process still retained pooled WAL state for the same database.
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
