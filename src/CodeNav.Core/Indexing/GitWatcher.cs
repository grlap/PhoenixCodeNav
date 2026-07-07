namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: watching a resolved git metadata dir for HEAD/ref changes (branch switch, merge,
/// reset, pull, commit) and signaling — debounced — that the checked-out commit may have moved.
/// Two narrow, non-recursive watches, so object/index churn is ignored:
///   * the git dir top level — HEAD, packed-refs, MERGE_HEAD, ORIG_HEAD, FETCH_HEAD
///     (branch switch, fast-forward pull, merge/rebase/reset markers);
///   * logs/HEAD — the HEAD reflog, appended on EVERY HEAD move including a plain `git commit`
///     or cherry-pick that only advances a nested loose branch ref (refs/heads/&lt;branch&gt;),
///     which the top-level watch cannot see. Requires reflogs (git's default for non-bare repos).
/// Does not own: computing the diff or applying the refresh (IndexManager), which re-checks the
/// actual commit before doing any work.
/// </summary>
public sealed class GitWatcher : IDisposable
{
    // Matched by file name against events from either watch. "HEAD" matches both the top-level
    // HEAD and logs/HEAD.
    private static readonly string[] Interesting =
    {
        "HEAD", "packed-refs", "MERGE_HEAD", "ORIG_HEAD", "FETCH_HEAD",
    };

    private readonly Action _onHeadMaybeChanged;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Threading.Timer _debounce;
    private readonly object _sync = new();
    private bool _disposed;

    public GitWatcher(string gitDir, Action onHeadMaybeChanged)
    {
        _onHeadMaybeChanged = onHeadMaybeChanged;
        _debounce = new System.Threading.Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        // Top-level pointer files: branch switch, pull, merge/rebase/reset markers.
        TryWatch(gitDir);
        // logs/HEAD reflog: the one signal a plain commit reliably produces.
        TryWatch(Path.Combine(gitDir, "logs"));
    }

    private void TryWatch(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return; // e.g. logs/ absent in a repo with no ref updates yet
            var fsw = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false, // direct children only — no objects/log-subtree churn
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            fsw.Changed += OnEvent;
            fsw.Created += OnEvent;
            fsw.Deleted += OnEvent;
            fsw.Renamed += OnEvent;
            fsw.EnableRaisingEvents = true;
            _watchers.Add(fsw);
        }
        catch
        {
            // watch unavailable for this dir — IndexManager still reconciles via FSW + startup
        }
    }

    private void OnEvent(object? sender, FileSystemEventArgs e)
    {
        string name = Path.GetFileName(e.FullPath);
        if (!Interesting.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        lock (_sync)
        {
            if (_disposed) return;
            // Coalesce multi-step operations (rebase, fetch-then-merge) into one signal.
            _debounce.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_sync)
        {
            if (_disposed) return;
        }
        _onHeadMaybeChanged();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce.Dispose();
        }
        foreach (var fsw in _watchers)
        {
            try
            {
                fsw.EnableRaisingEvents = false;
                fsw.Dispose();
            }
            catch { /* best effort */ }
        }
    }
}
