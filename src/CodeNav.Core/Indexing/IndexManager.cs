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
    string DbPath);

/// <summary>
/// Owns: index lifecycle for one workspace — open-or-build (in background, never
/// blocking server startup), watcher wiring, serialized delta refreshes, and health
/// snapshots for tool responses. Does not own: query shapes (IndexQueries) or the
/// MCP protocol surface.
/// </summary>
public sealed class IndexManager : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _dbPath;
    private readonly Action<string> _log;
    private readonly Channel<IReadOnlyCollection<string>?> _refreshQueue =
        Channel.CreateUnbounded<IReadOnlyCollection<string>?>();

    private IndexStore? _store;
    private WorkspaceWatcher? _watcher;
    private Task? _pump;
    private Task? _startTask;
    private volatile bool _disposed;
    private volatile string _state = "missing";
    private volatile string? _error;
    // Index metadata is cached here so Health() (called on tool threads) never touches
    // the single write connection, which only the opening thread and the pump may use.
    // Read once at open, then _lastRefreshUtc is updated by the pump after each refresh.
    private volatile string? _indexVersion;
    private volatile string? _indexedAtUtc;
    private volatile string? _lastRefreshUtc;

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
                    _refreshQueue.Writer.TryWrite(null); // detect-all
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
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
    }

    private void StartWatcher()
    {
        _watcher = new WorkspaceWatcher(
            _workspaceRoot,
            batch => _refreshQueue.Writer.TryWrite(batch),
            () => _refreshQueue.Writer.TryWrite(null)); // overflow → detect-all sweep
    }

    private async Task PumpRefreshesAsync()
    {
        await foreach (var batch in _refreshQueue.Reader.ReadAllAsync())
        {
            if (_store is null) continue;
            string previous = _state;
            try
            {
                _state = "refreshing";
                var result = DeltaRefresher.Refresh(_store, _workspaceRoot, batch, _log);
                _lastRefreshUtc = DateTime.UtcNow.ToString("O");
                if (result.AddedFiles + result.ChangedFiles + result.DeletedFiles > 0)
                {
                    _log($"Delta refresh: +{result.AddedFiles} ~{result.ChangedFiles} -{result.DeletedFiles} " +
                         $"(projects rebuilt: {result.ProjectsRefreshed}) in {result.Elapsed.TotalMilliseconds:F0}ms");
                }
                _state = "ready";
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _state = previous == "ready" ? "ready" : previous;
                _log($"Delta refresh failed: {ex}");
            }
        }
    }

    /// <summary>Queues a manual refresh (targeted paths, or full detection sweep when null).</summary>
    public void RequestRefresh(IReadOnlyCollection<string>? paths = null) =>
        _refreshQueue.Writer.TryWrite(paths);

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
            _watcher?.PendingCount ?? 0, _error, dbBytes, _workspaceRoot, _dbPath);
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();                 // stop new events reaching the queue
        _refreshQueue.Writer.TryComplete();  // let the pump drain and exit its loop

        // Let the startup task settle first (it may still be opening the store), then wait
        // for the pump to actually stop using the store. Only dispose the store once both
        // have finished — otherwise leak it (the process is tearing down) rather than risk
        // a use-after-dispose on the single write connection.
        bool startDone = true, pumpDone = true;
        try { startDone = _startTask?.Wait(TimeSpan.FromSeconds(5)) ?? true; } catch { /* faulted/cancelled */ }
        // The start task may have created the watcher after the first Dispose() call above
        // saw null — tear that one down too (WorkspaceWatcher.Dispose is idempotent).
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
