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
    private volatile string _state = "missing";
    private volatile string? _error;
    private string? _lastRefreshUtc;

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

        Task.Run(() =>
        {
            try
            {
                if (forceRebuild || !File.Exists(_dbPath))
                {
                    _state = "building";
                    _log($"Building index for {_workspaceRoot} ...");
                    var result = IndexBuilder.Build(_workspaceRoot, _dbPath, _log);
                    _log($"Index built: {result.CsFiles} files, {result.Symbols} symbols in {result.TotalTime.TotalSeconds:F0}s");
                    _store = new IndexStore(_dbPath, createNew: false);
                    _state = "ready";
                }
                else
                {
                    _store = new IndexStore(_dbPath, createNew: false);
                    _state = "ready";
                    _log("Existing index opened; running startup freshness sweep ...");
                    _refreshQueue.Writer.TryWrite(null); // detect-all
                }
                StartWatcher();
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _state = "failed";
                _log($"Index startup failed: {ex}");
            }
        });
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
        string? version = null, indexedAt = null;
        long dbBytes = 0;
        if (File.Exists(_dbPath))
        {
            dbBytes = new FileInfo(_dbPath).Length;
            if (_store is { } store)
            {
                version = store.GetMeta("index_version");
                indexedAt = store.GetMeta("indexed_at_utc");
                _lastRefreshUtc ??= store.GetMeta("last_refresh_utc");
            }
        }
        return new IndexHealth(
            _state, version, indexedAt, _lastRefreshUtc,
            _watcher?.PendingCount ?? 0, _error, dbBytes, _workspaceRoot, _dbPath);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _refreshQueue.Writer.TryComplete();
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutdown */ }
        _store?.Dispose();
    }
}
