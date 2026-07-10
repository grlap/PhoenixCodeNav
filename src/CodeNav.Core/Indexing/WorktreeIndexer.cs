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
/// error codes (worktree_not_found | worktree_index_locked | schema_stale | git_unavailable |
/// snapshot_failed | refresh_failed). Counts describe what the reconcile APPLIED.</summary>
public sealed record WorktreeIndexResult(
    string Action, string? Detail,
    int AddedFiles, int ChangedFiles, int DeletedFiles, long ElapsedMs,
    string? IndexedCommit, bool UsedFullSweep);

/// <summary>
/// Owns: creating and refreshing indexes in SIBLING git worktrees from the main instance —
/// the review-system flow (Greg): the skill creates worktrees (phoenix stays git-READ-ONLY,
/// decided explicitly — no worktree add/remove here, ever), phoenix seeds each one from a
/// VACUUM INTO snapshot of the live main index and reconciles it against that worktree's
/// state with two read-only git calls: ChangedFiles(indexed_commit -> HEAD) for commit
/// movement UNION DirtyFiles (git status) for uncommitted edits — one targeted delta, never
/// an mtime sweep of 54k fresh-checkout files. Every primitive used here is already
/// root-parameterized (IndexBuilder/DeltaRefresher/GitInfo statics): no manager, watcher, or
/// semantic layer is spun up for a sibling — a one-shot open, reconcile, close.
/// Targets are VALIDATED against `git worktree list` — an arbitrary filesystem path can never
/// be smuggled into an index-write operation.
/// Ownership honesty: a phoenix instance RUNNING in the target worktree owns that db; a brief
/// FileShare.None probe detects any live handle and returns worktree_index_locked instead of
/// contending with a foreign pump (the 6g7 hazard class).
/// Does not own: the main instance's own index (IndexManager/refresh_index), worktree
/// lifecycle (the caller's), or response shaping (NavigationTools).
/// Split out of: nothing — new for fgq/c36.
/// </summary>
public static class WorktreeIndexer
{
    private const int DiffCap = 5000; // mirror IndexManager.GitDiffCap: beyond this a sweep wins

    /// <summary>All worktrees of the repo at <paramref name="mainRoot"/> with index status.
    /// Null when git is unavailable or the root is not a repo.</summary>
    public static List<WorktreeIndexStatus>? Status(string mainRoot)
    {
        var worktrees = GitInfo.Worktrees(mainRoot);
        if (worktrees is null) return null;
        string mainFull = NormalizeFull(mainRoot);
        var result = new List<WorktreeIndexStatus>();
        foreach (var wt in worktrees)
        {
            string dbPath = IndexBuilder.DefaultDbPath(wt.Path);
            string? schema = null, indexedCommit = null;
            long dbBytes = 0;
            bool hasIndex = File.Exists(dbPath);
            if (hasIndex)
            {
                try { dbBytes = new FileInfo(dbPath).Length; } catch (IOException) { }
                (schema, indexedCommit) = TryReadMeta(dbPath);
            }
            bool? inSync = indexedCommit is not null && wt.Head is not null
                ? string.Equals(indexedCommit, wt.Head, StringComparison.OrdinalIgnoreCase)
                : null;
            result.Add(new WorktreeIndexStatus(
                wt.Path, wt.Head, wt.Branch, hasIndex, schema, indexedCommit, inSync, dbBytes,
                IsThisWorkspace: string.Equals(NormalizeFull(wt.Path), mainFull, StringComparison.OrdinalIgnoreCase)));
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
        var worktrees = GitInfo.Worktrees(mainRoot);
        if (worktrees is null)
        {
            return Fail("git_unavailable", "git worktree list failed — git absent or not a repo", sw);
        }
        var target = worktrees.FirstOrDefault(w =>
            string.Equals(NormalizeFull(w.Path), NormalizeFull(worktreePath), StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return Fail("worktree_not_found", $"'{worktreePath}' is not a worktree of this repository (see the worktrees tool)", sw);
        }
        // Review F2: a BARE entry (bare-main + linked-worktrees layout — a real monolith
        // arrangement) has no working tree to index; the unguarded path seeded a junk index
        // INSIDE the bare repository directory and reported success. No HEAD = not indexable.
        if (target.Head is null)
        {
            return Fail("worktree_not_indexable", "bare or headless worktree entry — no working tree to index", sw);
        }

        string dbPath = IndexBuilder.DefaultDbPath(target.Path);
        bool exists = File.Exists(dbPath);
        string? schema = exists ? TryReadMeta(dbPath).Schema : null;
        bool stale = exists && !string.Equals(schema, IndexBuilder.SchemaVersion, StringComparison.Ordinal);

        bool create = mode switch
        {
            "create" => true,
            "refresh" => false,
            _ => !exists || stale, // auto
        };
        if (!create && !exists)
        {
            return Fail("worktree_index_missing", "no index in this worktree — use mode 'create' (or 'auto')", sw);
        }
        if (!create && stale)
        {
            return Fail("schema_stale", $"worktree index schema {schema ?? "?"} != {IndexBuilder.SchemaVersion} — use mode 'create' (or 'auto')", sw);
        }

        // Ownership probe BEFORE any write: a running instance in that worktree (a review
        // session's own phoenix) holds handles; FileShare.None fails on ANY live handle.
        // Clear OUR OWN idle connection pools first (test-caught, not hypothetical): a prior
        // Ensure's disposed IndexStore — or any disposed reader — parks its handle in the
        // ADO.NET pool, and the probe cannot tell pool residue from a foreign pump. Idle-only:
        // ClearAllPools never touches active connections (the main pump's writer is untouched).
        if (exists)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (IsHeldByAnotherProcess(dbPath))
            {
                return Fail("worktree_index_locked", "a phoenix instance appears to own this worktree's index — refresh from that session (refresh_index) instead", sw);
            }
        }

        try
        {
            if (create)
            {
                if (exists) DeleteIndexFiles(dbPath);
                IndexStore.SnapshotTo(mainDbPath, dbPath);
                log($"Seeded worktree index: {dbPath}");
            }
        }
        catch (Exception ex)
        {
            log($"Worktree seed failed: {ex}");
            return Fail("snapshot_failed", $"{ex.GetType().Name} while seeding (see server log)", sw);
        }

        try
        {
            using var store = new IndexStore(dbPath, createNew: false);
            string? indexedCommit = store.GetMeta("indexed_commit");
            var (head, _) = GitInfo.HeadCommitEx(target.Path);

            // Commit movement UNION working-tree dirt — both read-only; either failing (or the
            // union blowing the cap) falls back to the honest full sweep.
            List<string>? paths = null;
            bool sweep = true;
            if (indexedCommit is not null && head is not null)
            {
                var moved = string.Equals(indexedCommit, head, StringComparison.OrdinalIgnoreCase)
                    ? new List<string>()
                    : GitInfo.ChangedFiles(target.Path, indexedCommit, head);
                var dirty = GitInfo.DirtyFiles(target.Path);
                if (moved is not null && dirty is not null)
                {
                    var union = new HashSet<string>(moved, StringComparer.OrdinalIgnoreCase);
                    union.UnionWith(dirty);
                    if (union.Count <= DiffCap)
                    {
                        paths = union.ToList();
                        sweep = false;
                    }
                }
            }
            var result = DeltaRefresher.Refresh(store, target.Path, sweep ? null : paths, log);
            if (head is not null)
            {
                store.SetMeta("indexed_commit", head);
                var branch = GitInfo.HeadBranch(target.Path);
                if (branch is not null) store.SetMeta("indexed_branch", branch);
            }
            return new WorktreeIndexResult(
                create ? "created" : "refreshed", null,
                result.AddedFiles, result.ChangedFiles, result.DeletedFiles,
                sw.ElapsedMilliseconds, head, UsedFullSweep: sweep);
        }
        catch (Exception ex)
        {
            log($"Worktree reconcile failed: {ex}");
            return Fail("refresh_failed", $"{ex.GetType().Name} during reconcile (see server log)", sw);
        }
    }

    private static WorktreeIndexResult Fail(string action, string detail, System.Diagnostics.Stopwatch sw) =>
        new(action, detail, 0, 0, 0, sw.ElapsedMilliseconds, null, false);

    private static string NormalizeFull(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    /// <summary>Brief exclusive-open probe: true when ANY other handle (a running instance's
    /// pump or reader) holds the db. Never contend with a foreign pump — report it.</summary>
    private static bool IsHeldByAnotherProcess(string dbPath)
    {
        try
        {
            using var probe = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void DeleteIndexFiles(string dbPath)
    {
        // Sidecars FIRST, then the main db (the Batch-33 recovery ordering: a main-db-first
        // delete leaves a stale WAL that a recreated db could replay).
        foreach (var suffix in new[] { "-wal", "-shm", "" })
        {
            string p = dbPath + suffix;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    /// <summary>Schema + indexed_commit from a db WITHOUT holding it open — a read-only,
    /// pooling-free connection so the status listing never pins a sibling's files.</summary>
    private static (string? Schema, string? IndexedCommit) TryReadMeta(string dbPath)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Mode=ReadOnly;Pooling=false");
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
