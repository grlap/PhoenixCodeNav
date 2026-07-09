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
    private readonly string _gitDir;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Threading.Timer _debounce;
    private readonly object _sync = new();
    private bool _disposed;
    private bool _logsWatchAttached;

    public GitWatcher(string gitDir, Action onHeadMaybeChanged)
    {
        _gitDir = gitDir;
        _onHeadMaybeChanged = onHeadMaybeChanged;
        _debounce = new System.Threading.Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        // Top-level pointer files: branch switch, pull, merge/rebase/reset markers.
        TryWatch(gitDir);
        // logs/HEAD reflog: the one signal a plain commit reliably produces. In a freshly
        // initialized repo logs/ does not exist yet — the FIRST commit creates it, and the
        // top-level watch sees that creation (OnEvent re-attaches then; wll).
        _logsWatchAttached = TryWatch(Path.Combine(gitDir, "logs"));
    }

    private bool TryWatch(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false; // e.g. logs/ absent in a repo with no ref updates yet
            var fsw = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false, // direct children only — no objects/log-subtree churn
                // DirectoryName is REQUIRED for the wll late-attach: the logs/ reflog dir being
                // born (a git whose init doesn't pre-create it) is a DIRECTORY creation, which a
                // FileName-only filter never reports — the re-attach hook would be dead code.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            fsw.Changed += OnEvent;
            fsw.Created += OnEvent;
            fsw.Deleted += OnEvent;
            fsw.Renamed += OnEvent;
            fsw.EnableRaisingEvents = true;
            _watchers.Add(fsw);
            return true;
        }
        catch
        {
            // watch unavailable for this dir — IndexManager still reconciles via FSW + startup
            return false;
        }
    }

    private void OnEvent(object? sender, FileSystemEventArgs e)
    {
        string name = Path.GetFileName(e.FullPath);
        // Late logs/ attach (wll): the reflog dir is born with the repo's FIRST commit — the
        // constructor could not watch it, and without re-attaching here every subsequent plain
        // commit (which only appends logs/HEAD + moves a nested loose ref) went unseen until an
        // unrelated full sweep. The creating commit itself is signaled below via the debounce.
        if (string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase))
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (!_logsWatchAttached)
                {
                    _logsWatchAttached = TryWatch(Path.Combine(_gitDir, "logs"));
                    if (_logsWatchAttached)
                    {
                        _debounce.Change(TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
                    }
                }
            }
            return;
        }
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
