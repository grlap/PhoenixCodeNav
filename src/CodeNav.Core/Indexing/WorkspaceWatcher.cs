using System.Collections.Concurrent;
using CodeNav.Core.Discovery;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: debounced file-change batching over a workspace root (FileSystemWatcher), with
/// exclusion filtering and directory-change escalation. Directory-level operations
/// (rename/move/delete of a folder) do not emit per-file events for the subtree, so they
/// escalate to a full detect-all sweep. Directory classification is by observation
/// (existing dirs + a background seed + learned ancestors), never by guessing from the
/// path extension — so dotted project folders (Acme.Payments) are not mistaken for files
/// and extensionless files (LICENSE) do not trigger spurious sweeps.
/// Does not own: applying changes (DeltaRefresher) or refresh scheduling (IndexManager).
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private static readonly string[] WatchedExtensions =
    {
        ".cs", ".csproj", ".sln", ".slnx", ".slnf", ".config", ".props", ".targets", ".json",
    };

    private readonly string _root;
    private readonly Action<IReadOnlyCollection<string>> _onBatch;
    private readonly Action _onSweep;
    private readonly FileSystemWatcher _fsw;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _knownDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _debounce;
    private readonly object _sync = new();
    private volatile bool _sweepRequested;
    private volatile bool _seedComplete;
    private bool _disposed;

    public WorkspaceWatcher(string root, Action<IReadOnlyCollection<string>> onBatch, Action onSweep)
    {
        _root = Path.GetFullPath(root);
        _onBatch = onBatch;
        _onSweep = onSweep;
        _debounce = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        // Learn the existing directory layout in the background so even never-touched
        // folders are classifiable when deleted. Best-effort; excludes and links skipped.
        _ = Task.Run(SeedKnownDirs);

        _fsw = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _fsw.Created += (_, e) => Handle(e.FullPath, WatcherChangeTypes.Created);
        _fsw.Changed += (_, e) => Handle(e.FullPath, WatcherChangeTypes.Changed);
        _fsw.Deleted += (_, e) => Handle(e.FullPath, WatcherChangeTypes.Deleted);
        _fsw.Renamed += (_, e) =>
        {
            Handle(e.OldFullPath, WatcherChangeTypes.Deleted);
            Handle(e.FullPath, WatcherChangeTypes.Created);
        };
        _fsw.Error += (_, _) => RequestSweep();
        _fsw.EnableRaisingEvents = true;
    }

    public int PendingCount => _pending.Count;

    private void Handle(string fullPath, WatcherChangeTypes change)
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

        // Directory that currently exists (create / rename-in / mtime bump).
        if (SafeDirectoryExists(fullPath))
        {
            _knownDirs.TryAdd(rel, 0);
            // A create/rename-in can bring a whole subtree with NO per-child events, so it
            // must sweep. A 'Changed' (mtime bump from a child add/remove) is already
            // covered by that child's own event — escalating it would sweep on every edit.
            if (change != WatcherChangeTypes.Changed) RequestSweep();
            return;
        }

        // File that currently exists (create / modify).
        if (File.Exists(fullPath))
        {
            RecordAncestors(rel);
            if (!IsWatchedFile(rel)) return;
            _pending.TryAdd(rel, 0);
            ArmDebounce();
            return;
        }

        // Absent: a delete or a rename-away. We cannot stat it, so classify by what we know
        // rather than by the extension (dotted dirs look like files; LICENSE looks like a dir).
        if (_knownDirs.TryRemove(rel, out _))
        {
            RequestSweep(); // a known directory vanished — reconcile its subtree
            return;
        }
        if (IsWatchedFile(rel))
        {
            _pending.TryAdd(rel, 0); // deleted watched file — the per-file batch marks it removed
            ArmDebounce();
            return;
        }
        // Unknown absent path: an unwatched file (drop — no spurious sweep) OR a directory we
        // never observed. Until the background seed finishes, _knownDirs is incomplete, so we
        // cannot tell those apart — sweep conservatively. Once seeded, an unknown absent path
        // is genuinely not a tracked directory, so dropping it is safe.
        if (!_seedComplete)
        {
            RequestSweep();
        }
    }

    private static bool IsWatchedFile(string rel) =>
        WatchedExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase);

    private static bool SafeDirectoryExists(string fullPath)
    {
        try { return Directory.Exists(fullPath); }
        catch { return false; }
    }

    private void RecordAncestors(string rel)
    {
        int slash = rel.LastIndexOf('/');
        while (slash > 0)
        {
            string dir = rel[..slash];
            if (!_knownDirs.TryAdd(dir, 0)) break; // already known ⇒ its ancestors are too
            slash = dir.LastIndexOf('/');
        }
    }

    private void SeedKnownDirs()
    {
        bool clean = true;
        try
        {
            var stack = new Stack<string>();
            stack.Push(_root);
            while (stack.Count > 0)
            {
                if (_disposed) return;
                string dir = stack.Pop();
                List<string> subs;
                // Materialize under the try so a lazily-thrown enumeration error (denied ACL,
                // dir removed mid-walk) skips just this directory rather than aborting the seed.
                try { subs = Directory.EnumerateDirectories(dir).ToList(); }
                catch { clean = false; continue; }
                foreach (var sub in subs)
                {
                    try
                    {
                        string name = Path.GetFileName(sub);
                        if (WorkspaceScanner.DefaultExcludedDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        if (WorkspacePaths.IsReparsePoint(sub)) continue; // don't follow links
                    }
                    catch { clean = false; continue; }
                    string rel = Path.GetRelativePath(_root, sub).Replace('\\', '/');
                    _knownDirs.TryAdd(rel, 0);
                    stack.Push(sub);
                }
            }
        }
        catch
        {
            clean = false;
        }
        finally
        {
            // Only trust _knownDirs for absent-path classification if the whole tree was
            // enumerated without a swallowed error. If any subtree was skipped, stay
            // conservative (unknown absent paths keep sweeping) so a delete under an
            // unseeded subtree is never silently dropped — at the cost of occasional
            // redundant sweeps in the rare partial-seed case.
            _seedComplete = clean;
        }
    }

    private void RequestSweep()
    {
        _sweepRequested = true;
        ArmDebounce();
    }

    private void ArmDebounce()
    {
        lock (_sync)
        {
            if (_disposed) return;
            // Restart the quiet-period window on every event.
            _debounce.Change(TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
        }
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
        lock (_sync)
        {
            if (_disposed) return;
        }

        // A pending directory-level change supersedes the per-file batch: reconcile the
        // whole tree in one sweep.
        if (_sweepRequested)
        {
            _sweepRequested = false;
            _pending.Clear();
            _onSweep();
            return;
        }

        if (_pending.IsEmpty) return;
        var batch = _pending.Keys.ToList();
        foreach (var key in batch) _pending.TryRemove(key, out _);
        _onBatch(batch);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce.Dispose(); // under the lock: no ArmDebounce can touch it afterward
        }
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
    }
}
