using System.Collections.Concurrent;
using CodeNav.Core.Discovery;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: debounced file-change batching over a workspace root (FileSystemWatcher),
/// with exclusion filtering and overflow signalling. Does not own: applying changes
/// (DeltaRefresher) or refresh scheduling policy (IndexManager).
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private static readonly string[] WatchedExtensions =
    {
        ".cs", ".csproj", ".sln", ".slnx", ".slnf", ".config", ".props", ".targets", ".json",
    };

    private readonly string _root;
    private readonly Action<IReadOnlyCollection<string>> _onBatch;
    private readonly Action _onOverflow;
    private readonly FileSystemWatcher _fsw;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _debounce;

    public WorkspaceWatcher(string root, Action<IReadOnlyCollection<string>> onBatch, Action onOverflow)
    {
        _root = Path.GetFullPath(root);
        _onBatch = onBatch;
        _onOverflow = onOverflow;
        _debounce = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        _fsw = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _fsw.Created += (_, e) => Enqueue(e.FullPath);
        _fsw.Changed += (_, e) => Enqueue(e.FullPath);
        _fsw.Deleted += (_, e) => Enqueue(e.FullPath);
        _fsw.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
        _fsw.Error += (_, _) => _onOverflow();
        _fsw.EnableRaisingEvents = true;
    }

    public int PendingCount => _pending.Count;

    private void Enqueue(string fullPath)
    {
        string rel;
        try
        {
            rel = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        }
        catch
        {
            return;
        }
        if (rel.StartsWith("..", StringComparison.Ordinal)) return;
        if (IsExcluded(rel)) return;

        string ext = Path.GetExtension(rel);
        if (!WatchedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return;

        _pending.TryAdd(rel, 0);
        // Restart the quiet-period window on every event.
        _debounce.Change(TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
    }

    private static bool IsExcluded(string relPath)
    {
        foreach (var segment in relPath.Split('/'))
        {
            foreach (var excluded in WorkspaceScanner.DefaultExcludedDirs)
            {
                if (segment.Equals(excluded, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        var batch = _pending.Keys.ToList();
        foreach (var key in batch) _pending.TryRemove(key, out _);
        _onBatch(batch);
    }

    public void Dispose()
    {
        _fsw.Dispose();
        _debounce.Dispose();
    }
}
