namespace CodeNav.Core.Indexing;

/// <summary>One worktree's index status for the listing tool. IndexedCommit/IndexSchema are
/// null when no index exists (or it is unreadable); InSync means indexedCommit == worktree
/// HEAD (commit-level only — uncommitted dirt is invisible to a status listing by design;
/// the reconcile handles it).</summary>
public sealed record WorktreeIndexStatus(
    string Path, string? Head, string? Branch,
    bool HasIndex, string? IndexSchema, string? IndexedCommit, bool? InSync, long DbBytes,
    bool IsThisWorkspace);

/// <summary>Result of ensuring one worktree's index. Action: created | refreshed |
/// error codes (bad_request [empty/invalid caller path] | unsupported_platform [macOS] |
/// worktree_not_found | worktree_not_indexable | worktree_index_locked | git_unavailable |
/// snapshot_failed | refresh_failed). schema_stale is gone: every reconcile re-seeds from the
/// live main index and never inspects the target's schema. Counts describe what the
/// reconcile APPLIED.</summary>
public sealed record WorktreeIndexResult(
    string Action, string? Detail,
    int AddedFiles, int ChangedFiles, int DeletedFiles, long ElapsedMs,
    string? IndexedCommit, bool UsedFullSweep,
    string? PartialReason = null,
    IReadOnlyList<string>? IncompleteSourcePaths = null,
    int IncompleteSourcePathCount = 0,
    bool IncompleteSourcePathCountIsLowerBound = false);

internal enum WorktreeIndexPlatformPolicy
{
    WindowsTargeted,
    LinuxAnchoredFullSweep,
    Unsupported,
}

/// <summary>
/// Owns: creating and refreshing indexes in SIBLING git worktrees from the main instance —
/// the review-system flow (Greg): the skill creates worktrees (phoenix stays git-READ-ONLY,
/// decided explicitly — no worktree add/remove here, ever), phoenix seeds each one from a
/// transactionally consistent reserved-file backup of the live main index and reconciles it
/// against that worktree's
/// state. Windows uses ChangedFiles(indexed_commit -> HEAD) UNION DirtyFiles for one targeted
/// delta; Linux uses a pinned no-follow full sweep; macOS mutation is explicitly unsupported.
/// Every primitive used here is
/// root-parameterized (IndexBuilder/DeltaRefresher/GitInfo statics): no manager, watcher, or
/// semantic layer is spun up for a sibling — a one-shot open, reconcile, close.
/// Targets are VALIDATED against `git worktree list` — an arbitrary filesystem path can never
/// be smuggled into an index-write operation.
/// Ownership honesty: a phoenix instance RUNNING in the target worktree owns a separate
/// cross-process lease for that db. The lease is independent of native SQLite sharing behavior
/// and returns worktree_index_locked instead of contending with a foreign pump.
/// Does not own: the main instance's own index (IndexManager/refresh_index), worktree
/// lifecycle (the caller's), or response shaping (NavigationTools).
/// Split out of: nothing — new for fgq/c36.
/// </summary>
public static class WorktreeIndexer
{
    private const int DiffCap = 5000; // mirror IndexManager.GitDiffCap: beyond this a sweep wins
    // Carries the target db path: the hook is a process-global static and xUnit runs test
    // CLASSES in parallel, so a foreign Ensure call can fire a test's closure against the wrong
    // destination. Consumers filter to their own target (same shape as
    // AnchoredIndexDestination.BeforeStageSidecarReservationForTest).
    internal static Action<string>? BeforeAnchoredInstallForTest { get; set; }
    internal static WorktreeIndexPlatformPolicy PlatformPolicyForHost(
        bool isWindows, bool isLinux) => isWindows
        ? WorktreeIndexPlatformPolicy.WindowsTargeted
        : isLinux
            ? WorktreeIndexPlatformPolicy.LinuxAnchoredFullSweep
            : WorktreeIndexPlatformPolicy.Unsupported;

    private static WorktreeIndexPlatformPolicy PlatformPolicy =>
        PlatformPolicyForHost(OperatingSystem.IsWindows(), OperatingSystem.IsLinux());

    /// <summary>All worktrees of the repo at <paramref name="mainRoot"/> with anchored index
    /// status. Null on unsupported platforms, when git is unavailable, or when the root is not a
    /// repository; the MCP layer maps the platform case to unsupported_platform explicitly.</summary>
    public static List<WorktreeIndexStatus>? Status(string mainRoot)
    {
        if (PlatformPolicy == WorktreeIndexPlatformPolicy.Unsupported) return null;
        var worktrees = GitInfo.Worktrees(mainRoot);
        if (worktrees is null) return null;
        var targets = MapWorkspaceRoots(mainRoot, worktrees);
        if (targets is null) return null;
        var result = new List<WorktreeIndexStatus>();
        foreach (var target in targets)
        {
            GitInfo.Worktree wt = target.Worktree;
            string dbPath = IndexBuilder.DefaultDbPath(target.WorkspacePath);
            string? schema = null, indexedCommit = null;
            long dbBytes = 0;
            // Listing must not inspect an external target reached through a mapped workspace,
            // .codenav directory, database, or sidecar link. Unsafe targets remain visible as
            // worktrees but deliberately report no readable index.
            bool hasIndex = false;
            if (IsSafeMappedWorkspace(target) &&
                AnchoredIndexDestination.TryOpen(target.GitRoot, target.WorkspacePath,
                    dbPath, createIndexDirectory: false,
                    out AnchoredIndexDestination? destination))
            {
                using (destination!)
                {
                    hasIndex = destination!.DatabaseExists;
                    dbBytes = destination.DatabaseLength;
                    if (hasIndex && !destination.HasRecoverySidecars &&
                        destination.DatabaseReadPath is { } readPath)
                    {
                        (schema, indexedCommit) = TryReadMeta(readPath);
                    }
                }
            }
            bool? inSync = indexedCommit is not null && wt.Head is not null
                ? string.Equals(indexedCommit, wt.Head, StringComparison.OrdinalIgnoreCase)
                : null;
            result.Add(new WorktreeIndexStatus(
                target.WorkspacePath, wt.Head, wt.Branch, hasIndex, schema, indexedCommit,
                inSync, dbBytes,
                IsThisWorkspace: WorkspacePaths.FullPathsEqual(target.WorkspacePath, mainRoot)));
        }
        return result;
    }

    /// <summary>Create (seed from a snapshot of <paramref name="mainDbPath"/>) or refresh the
    /// index of ONE sibling worktree. Mode: 'auto' (create when missing or schema-stale, else
    /// refresh), 'create' (recreate from a fresh seed), 'refresh' (existing only).</summary>
    public static WorktreeIndexResult Ensure(
        string mainRoot, string mainDbPath, string worktreePath, string mode, Action<string> log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (PlatformPolicy == WorktreeIndexPlatformPolicy.Unsupported)
        {
            return Fail("unsupported_platform",
                "anchored worktree index mutation is not supported on macOS", sw);
        }
        if (!WorkspacePaths.TryNormalizeFullForComparison(worktreePath,
                out string requestedPath))
        {
            return Fail("bad_request", "worktree path is empty or invalid", sw);
        }
        var worktrees = GitInfo.Worktrees(mainRoot);
        if (worktrees is null)
        {
            return Fail("git_unavailable", "git worktree list failed — git absent or not a repo", sw);
        }
        var targets = MapWorkspaceRoots(mainRoot, worktrees);
        if (targets is null)
        {
            return Fail("git_unavailable",
                "git worktree roots could not be mapped to this workspace", sw);
        }
        var target = targets.FirstOrDefault(w =>
            WorkspacePaths.FullPathsEqual(w.WorkspacePath, requestedPath));
        if (target is null)
        {
            return Fail("worktree_not_found", $"'{worktreePath}' is not a worktree of this repository (see the worktrees tool)", sw);
        }
        // Review F2: a BARE entry (bare-main + linked-worktrees layout — a real monolith
        // arrangement) has no working tree to index; the unguarded path seeded a junk index
        // INSIDE the bare repository directory and reported success. No HEAD = not indexable.
        if (target.Worktree.Head is null)
        {
            return Fail("worktree_not_indexable", "bare or headless worktree entry — no working tree to index", sw);
        }
        if (!Directory.Exists(target.WorkspacePath) || !IsSafeMappedWorkspace(target))
        {
            return Fail("worktree_not_indexable",
                "the corresponding workspace subtree is absent or linked outside that worktree", sw);
        }

        string dbPath = IndexBuilder.DefaultDbPath(target.WorkspacePath);
        if (!IsSafeIndexDestination(target, dbPath))
        {
            return Fail("worktree_not_indexable",
                "the worktree index destination contains a link or reparse escape", sw);
        }
        bool createDirectory = mode is not "refresh";
        if (!AnchoredIndexDestination.TryOpen(target.GitRoot, target.WorkspacePath,
                dbPath, createDirectory, out AnchoredIndexDestination? destination))
        {
            return Fail(createDirectory ? "worktree_not_indexable" : "worktree_index_missing",
                "the worktree index destination could not be opened without following links", sw);
        }
        using (destination!)
        {
            if (!destination!.TryGetLeaseIdentity(out IndexLeaseIdentity? leaseIdentity))
            {
                return Fail("worktree_not_indexable",
                    "the pinned worktree index destination could not be identified", sw);
            }
            if (!IndexOwnershipLease.TryAcquire(target.WorkspacePath, dbPath,
                    leaseIdentity, out IndexOwnershipLease? ownershipLease))
            {
                return Fail("worktree_index_locked",
                    "a phoenix instance owns this worktree's index; refresh from that session instead", sw);
            }
            using (ownershipLease!)
            {
                return EnsureOwned(mainDbPath, mode, log, sw, target, dbPath,
                    destination!);
            }
        }
    }

    private static WorktreeIndexResult EnsureOwned(
        string mainDbPath, string mode, Action<string> log,
        System.Diagnostics.Stopwatch sw, WorkspaceWorktree target, string dbPath,
        AnchoredIndexDestination destination)
    {
        if (destination.HasRecoverySidecars)
        {
            return Fail("worktree_not_indexable",
                "the worktree index has recovery sidecars; recover it in its owning Phoenix session before replacement", sw);
        }
        bool exists = destination.DatabaseExists;

        bool create = mode == "create" || (mode != "refresh" && !exists);
        if (!create && !exists)
        {
            return Fail("worktree_index_missing",
                "no index in this worktree; use mode 'create' (or 'auto')", sw);
        }
        return ReconcileStaged(mainDbPath, log, sw, target, dbPath, create,
            destination);
    }

    private static WorktreeIndexResult ReconcileStaged(
        string mainDbPath, Action<string> log, System.Diagnostics.Stopwatch sw,
        WorkspaceWorktree target, string dbPath, bool create,
        AnchoredIndexDestination destination)
    {
        string stagedDb;
        try
        {
            stagedDb = destination.CreateStagePath();
        }
        catch (Exception ex)
        {
            log($"Worktree stage reservation failed: {ex}");
            return Fail(create ? "snapshot_failed" : "refresh_failed",
                $"{ex.GetType().Name} while reserving the staged index (see server log)", sw);
        }
        try
        {
            try
            {
                // Every reconcile starts from the live main index. The target database is derived
                // state and is never opened, deleted, or used as the seed; commit/status reconcile
                // deterministically transforms the main snapshot into the sibling view.
                IndexStore.SnapshotToReserved(mainDbPath, stagedDb);
                log($"Staged worktree index privately for {target.WorkspacePath}");
            }
            catch (Exception ex)
            {
                log($"Worktree seed failed: {ex}");
                return Fail("snapshot_failed",
                    $"{ex.GetType().Name} while seeding (see server log)", sw);
            }

            string? head;
            bool sweep = true;
            int added;
            int changed;
            int deleted;
            try
            {
                using var store = new IndexStore(stagedDb, createNew: false,
                    privateStaging: true);
                string? indexedCommit = store.GetMeta("indexed_commit");
                string readRoot = destination.WorkspaceReadPath;
                (head, _) = GitInfo.HeadCommitEx(readRoot);

                // Commit movement UNION working-tree dirt — both read-only; either failing (or the
                // union blowing the cap) falls back to the honest full sweep.
                List<string>? paths = null;
                // Linux source reads are pinned through /proc/<pid>/fd. Git's repository-layout
                // validation cannot safely prove that a visible-path diff/status snapshot names
                // that same held tree across an ABA swap, so Linux deliberately performs the
                // anchored full sweep. Windows pins every path component against rename and can
                // retain the targeted commit+dirt reconcile.
                if (PlatformPolicy == WorktreeIndexPlatformPolicy.WindowsTargeted &&
                    indexedCommit is not null && head is not null)
                {
                    var moved = string.Equals(indexedCommit, head,
                            StringComparison.OrdinalIgnoreCase)
                        ? new List<string>()
                        : GitInfo.ChangedFiles(readRoot, indexedCommit, head);
                    var dirty = GitInfo.DirtyFiles(readRoot);
                    if (moved is not null && dirty is not null)
                    {
                        var union = new HashSet<string>(moved, GitPathComparer);
                        union.UnionWith(dirty);
                        if (union.Count <= DiffCap)
                        {
                            paths = union.ToList();
                            sweep = false;
                        }
                    }
                }
                var result = RefreshWithTransientRetries(store, readRoot,
                    sweep ? null : paths, log);
                added = result.AddedFiles;
                changed = result.ChangedFiles;
                deleted = result.DeletedFiles;
                if (head is not null)
                {
                    store.SetMeta("indexed_commit", head);
                    var branch = GitInfo.HeadBranch(readRoot);
                    if (branch is not null) store.SetMeta("indexed_branch", branch);
                    else store.DeleteMeta("indexed_branch");
                }
                else
                {
                    // The seed carries the main workspace's commit. A pinned-root Git lookup may
                    // be unavailable on Linux; never publish that unrelated seed claim after a
                    // successful full sweep of the sibling tree.
                    store.DeleteMeta("indexed_commit");
                    store.DeleteMeta("indexed_branch");
                }
                store.CheckpointForAtomicInstall();
            }
            catch (RefreshInputUnavailableException ex)
            {
                log($"Worktree reconcile source capture retries exhausted: {ex}");
                return Incomplete(IndexManager.RefreshInputUnavailableCause,
                    "a regular source file remained unavailable after bounded retries",
                    [ex.Path], sw, sweep);
            }
            catch (RefreshInputOversizedException ex)
            {
                log($"Worktree reconcile source exceeds the configured byte limit: {ex}");
                return Incomplete(IndexManager.RefreshInputOversizedCause,
                    "a regular source file exceeds the configured byte limit",
                    [ex.Path], sw, sweep);
            }
            catch (Exception ex)
            {
                log($"Worktree reconcile failed: {ex}");
                return Fail("refresh_failed",
                    $"{ex.GetType().Name} during reconcile (see server log)", sw);
            }

            // kae: scoped — release THIS worktree database's pooled reader handles so the atomic
            // install can replace the file; a global clear could hit unrelated databases (rqek).
            IndexQueries.ClearPoolsFor(dbPath);
            BeforeAnchoredInstallForTest?.Invoke(dbPath);
            if (!destination.InstallStage())
            {
                return Fail(create ? "snapshot_failed" : "refresh_failed",
                    "the staged index could not be atomically installed", sw);
            }

            // A path swap cannot redirect the handle-relative install. Report the race honestly
            // if the visible path no longer names the pinned destination.
            if (!IsSafeIndexDestination(target, dbPath))
            {
                return Fail("worktree_not_indexable",
                    "the worktree index destination changed during reconcile", sw);
            }
            return new WorktreeIndexResult(
                create ? "created" : "refreshed", null,
                added, changed, deleted,
                sw.ElapsedMilliseconds, head, UsedFullSweep: sweep);
        }
        finally
        {
            // kae: scoped teardown for the same reason as the pre-install clear above.
            IndexQueries.ClearPoolsFor(dbPath);
        }
    }

    private static RefreshResult RefreshWithTransientRetries(
        IndexStore store, string workspaceRoot, IReadOnlyCollection<string>? paths,
        Action<string> log)
    {
        int retry = 0;
        while (true)
        {
            try
            {
                return DeltaRefresher.Refresh(store, workspaceRoot, paths, log);
            }
            catch (RefreshInputUnavailableException ex)
                when (retry < DeltaRefresher.RefreshInputRetryDelays.Length)
            {
                TimeSpan delay = DeltaRefresher.RefreshInputRetryDelays[retry++];
                log($"Worktree source capture unavailable for {ex.Path}; retrying complete " +
                    $"reconcile after {delay.TotalMilliseconds:F0}ms.");
                Thread.Sleep(delay);
            }
        }
    }

    private static WorktreeIndexResult Incomplete(
        string reason, string detail, IReadOnlyList<string> paths,
        System.Diagnostics.Stopwatch sw, bool usedFullSweep)
    {
        string[] distinct = paths.Distinct(GitPathComparer)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        return new WorktreeIndexResult(reason, detail, 0, 0, 0,
            sw.ElapsedMilliseconds, null, usedFullSweep, reason,
            distinct.Take(32).ToArray(), distinct.Length,
            IncompleteSourcePathCountIsLowerBound: true);
    }

    private static WorktreeIndexResult Fail(string action, string detail, System.Diagnostics.Stopwatch sw) =>
        new(action, detail, 0, 0, 0, sw.ElapsedMilliseconds, null, false);

    private sealed record WorkspaceWorktree(GitInfo.Worktree Worktree, string GitRoot,
        string WorkspacePath);

    private static StringComparer GitPathComparer =>
        GitPathComparerForHost(OperatingSystem.IsWindows());

    internal static StringComparer GitPathComparerForHost(bool isWindows) => isWindows
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>Maps the configured workspace's repository-relative prefix onto every Git
    /// worktree root. A Phoenix rooted at repo/src must therefore read/write sibling indexes at
    /// sibling/src, never at the sibling repository root.</summary>
    private static List<WorkspaceWorktree>? MapWorkspaceRoots(string mainRoot,
        IReadOnlyList<GitInfo.Worktree> worktrees)
    {
        if (!WorkspacePaths.TryNormalizeFullForComparison(mainRoot, out string mainFull))
            return null;

        var roots = new List<WorkspaceWorktree>(worktrees.Count);
        foreach (GitInfo.Worktree worktree in worktrees)
        {
            if (!WorkspacePaths.TryNormalizeFullForComparison(worktree.Path,
                    out string gitRoot))
            {
                return null;
            }
            roots.Add(new WorkspaceWorktree(worktree, gitRoot, gitRoot));
        }

        // Worktrees can technically be nested. The deepest listed root containing this configured
        // workspace is the current worktree; using the main worktree merely because Git lists it
        // first would derive the wrong prefix when called from a linked checkout.
        WorkspaceWorktree? current = roots
            .Where(candidate => WorkspacePaths.IsSameOrDescendantPath(mainFull,
                candidate.GitRoot))
            .OrderByDescending(candidate => candidate.GitRoot.Length)
            .FirstOrDefault();
        if (current is null) return null;

        string relativePrefix;
        try
        {
            relativePrefix = Path.GetRelativePath(current.GitRoot, mainFull);
        }
        catch
        {
            return null;
        }
        if (relativePrefix == ".") relativePrefix = "";
        if (Path.IsPathRooted(relativePrefix) || relativePrefix == ".." ||
            relativePrefix.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return null;
        }

        var mapped = new List<WorkspaceWorktree>(roots.Count);
        foreach (WorkspaceWorktree root in roots)
        {
            string candidate = relativePrefix.Length == 0
                ? root.GitRoot
                : Path.Combine(root.GitRoot, relativePrefix);
            if (!WorkspacePaths.TryNormalizeFullForComparison(candidate,
                    out string workspacePath) ||
                !WorkspacePaths.IsSameOrDescendantPath(workspacePath, root.GitRoot))
            {
                return null;
            }
            mapped.Add(root with { WorkspacePath = workspacePath });
        }
        return mapped;
    }

    private static bool IsSafeMappedWorkspace(WorkspaceWorktree target)
    {
        if (!WorkspacePaths.IsSameOrDescendantPath(target.WorkspacePath, target.GitRoot) ||
            WorkspacePaths.IsReparsePoint(target.GitRoot) ||
            WorkspacePaths.IsReparsePoint(target.WorkspacePath))
        {
            return false;
        }
        return !WorkspacePaths.EscapesViaReparsePoint(target.GitRoot, target.WorkspacePath);
    }

    private static bool IsSafeIndexDestination(WorkspaceWorktree target, string dbPath)
    {
        if (!IsSafeMappedWorkspace(target) ||
            !WorkspacePaths.IsSameOrDescendantPath(dbPath, target.WorkspacePath))
        {
            return false;
        }

        string? indexDirectory = Path.GetDirectoryName(dbPath);
        if (indexDirectory is null ||
            WorkspacePaths.EscapesViaReparsePoint(target.WorkspacePath, indexDirectory))
        {
            return false;
        }

        foreach (string path in new[]
                 { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
        {
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (WorkspacePaths.IsReparsePoint(path) ||
                 WorkspacePaths.EscapesViaReparsePoint(target.WorkspacePath, path)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Schema + indexed_commit from a db WITHOUT holding it open — a read-only,
    /// pooling-free connection so the status listing never pins a sibling's files.</summary>
    private static (string? Schema, string? IndexedCommit) TryReadMeta(string dbPath)
    {
        try
        {
            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                connectionString.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM meta WHERE key IN ('schema_version','indexed_commit')";
            string? schema = null, commit = null;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.GetString(0) == "schema_version") schema = r.GetString(1);
                else commit = r.GetString(1);
            }
            return (schema, commit);
        }
        catch
        {
            return (null, null); // unreadable/foreign file — status shows hasIndex without detail
        }
    }
}
