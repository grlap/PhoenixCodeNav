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
    string? IndexedBranch = null);

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
    // as the reflected git commit after the batch succeeds (git-aware reconcile).
    private sealed record RefreshRequest(IReadOnlyCollection<string>? Paths, string? RecordCommit = null);

    private readonly string _workspaceRoot;
    private readonly string _dbPath;
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
    private volatile bool _disposed;
    private volatile string _state = "missing";
    private volatile string? _error;
    // Index metadata is cached here so Health() (called on tool threads) never touches
    // the single write connection, which only the opening thread and the pump may use.
    // Read once at open, then updated by the pump after each refresh.
    private volatile string? _indexVersion;
    private volatile string? _indexedAtUtc;
    private volatile string? _lastRefreshUtc;
    private volatile string? _indexedCommit;
    private volatile string? _indexedBranch;

    public IndexManager(string workspaceRoot, string? dbPath = null, Action<string>? log = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _dbPath = dbPath ?? IndexBuilder.DefaultDbPath(_workspaceRoot);
        _log = log ?? (_ => { });
    }

    public string WorkspaceRoot => _workspaceRoot;
    public string DbPath => _dbPath;
    public string State => _state;

    /// <summary>Opens the existing index or builds a new one in the background; returns immediately.</summary>
    public void Start(bool forceRebuild = false)
    {
        _pump = Task.Run(PumpRefreshesAsync);

        _startTask = Task.Run(() =>
        {
            try
            {
                if (_disposed) return;
                bool build = forceRebuild || !File.Exists(_dbPath);
                if (!build)
                {
                    // Rebuild when the on-disk index predates the current schema/indexer format —
                    // otherwise a freshly deployed binary would query columns the old index lacks,
                    // or trust field values (accessibility, signatures) the old indexer got wrong.
                    try
                    {
                        using var check = new IndexStore(_dbPath, createNew: false);
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
                    _log($"Building index for {_workspaceRoot} ...");
                    var buildResult = IndexBuilder.Build(_workspaceRoot, _dbPath, _log);
                    _log($"Index built: {buildResult.CsFiles} files, {buildResult.Symbols} symbols in {buildResult.TotalTime.TotalSeconds:F0}s");
                }

                var store = new IndexStore(_dbPath, createNew: false);
                CacheMeta(store);              // read meta before publishing the store

                // If Dispose ran while we were building/opening, don't publish or start the
                // watcher — clean up the store we just opened and leave the manager stopped.
                if (_disposed)
                {
                    store.Dispose();
                    return;
                }

                _store = store;
                _state = "ready";
                StartWatcher();
                if (!build)
                {
                    _log("Existing index opened; running startup freshness sweep ...");
                    _refreshQueue.Writer.TryWrite(new RefreshRequest(null)); // detect-all
                }
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
            if (_store is null) continue;
            string previous = _state;
            try
            {
                _state = "refreshing";
                var result = DeltaRefresher.Refresh(_store, _workspaceRoot, req.Paths, _log);
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
        }
    }

    /// <summary>Queues a manual refresh (targeted paths, or full detection sweep when null).</summary>
    public void RequestRefresh(IReadOnlyCollection<string>? paths = null) =>
        _refreshQueue.Writer.TryWrite(new RefreshRequest(paths));

    public bool IsQueryable => _state is "ready" or "refreshing" && File.Exists(_dbPath);

    public IndexQueries OpenQueries() => new(_dbPath);

    public IndexHealth Health()
    {
        // Reads only cached fields + filesystem — never the write connection, so it is
        // safe to call from tool threads while the pump writes on the store.
        long dbBytes = 0;
        try
        {
            if (File.Exists(_dbPath)) dbBytes = new FileInfo(_dbPath).Length;
        }
        catch (IOException) { /* transient; report 0 */ }

        return new IndexHealth(
            _state, _indexVersion, _indexedAtUtc, _lastRefreshUtc,
            _watcher?.PendingCount ?? 0, _error, dbBytes, _workspaceRoot, _dbPath,
            _indexedCommit, _indexedBranch);
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
        try { startDone = _startTask?.Wait(TimeSpan.FromSeconds(5)) ?? true; } catch { /* faulted/cancelled */ }
        // The start task may have created the watchers after the first Dispose() calls above
        // saw null — tear those down too (Dispose is idempotent).
        _gitWatcher?.Dispose();
        _watcher?.Dispose();
        try { pumpDone = _pump?.Wait(TimeSpan.FromSeconds(5)) ?? true; } catch { /* faulted/cancelled */ }

        if (startDone && pumpDone)
        {
            _store?.Dispose();
        }
        else
        {
            _log("IndexManager.Dispose: background work still running; leaving the index store open to avoid use-after-dispose.");
        }
    }
}
