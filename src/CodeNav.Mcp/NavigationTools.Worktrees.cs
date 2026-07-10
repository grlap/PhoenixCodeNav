using System.ComponentModel;
using CodeNav.Core.Indexing;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: the worktree index surface (fgq/c36) — listing the repository's worktrees with their
/// index status, and creating/refreshing SIBLING worktree indexes from this (main) instance:
/// the review-system flow where the skill creates worktrees, phoenix seeds each from a
/// VACUUM INTO snapshot and reconciles with two read-only git calls (commit diff UNION status
/// dirt). Phoenix never creates or removes worktrees — git stays READ-ONLY here by decision.
/// Does not own: the orchestration mechanics (WorktreeIndexer, Core) or this workspace's own
/// index lifecycle (refresh_index).
/// Split out of: NavigationTools.cs — new surface, kept in its own small file per house rule.
/// </summary>
public sealed partial class NavigationTools
{
    [McpServerTool(Name = "worktrees")]
    [Description("All git worktrees of this repository with per-worktree index status (READ-ONLY — phoenix never creates/removes worktrees). The enumeration to loop for 'refresh all' via index_worktree.")]
    public string Worktrees()
    {
        if (NotReady() is { } notReady) return notReady;
        var status = WorktreeIndexer.Status(_manager.WorkspaceRoot);
        if (status is null)
        {
            return Json.Serialize(new
            {
                error = "git_unavailable",
                detail = "git worktree list failed — git absent, or this workspace is not inside a git repository.",
                meta = Meta.From(_manager.Health(), "indexed", "text"),
            });
        }
        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(status, (items, truncated) => new
        {
            worktrees = items.Select(w => new
            {
                path = w.Path,
                branch = w.Branch, // null (omitted) when detached
                head = w.Head,
                isThisWorkspace = w.IsThisWorkspace ? true : (bool?)null,
                hasIndex = w.HasIndex,
                indexSchema = w.IndexSchema,
                schemaCurrent = w.IndexSchema is null ? (bool?)null
                    : string.Equals(w.IndexSchema, IndexBuilder.SchemaVersion, StringComparison.Ordinal),
                indexedCommit = w.IndexedCommit,
                // Commit-level only — uncommitted dirt is invisible to a listing by design;
                // index_worktree's reconcile covers it (git status union).
                inSyncWithHead = w.InSync,
                dbBytes = w.DbBytes > 0 ? w.DbBytes : (long?)null,
            }),
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "index_worktree")]
    [Description("Create or refresh the index of a SIBLING git worktree from this instance: 'create' seeds a transactionally consistent snapshot of this index (VACUUM INTO — the live pump never pauses) into the worktree and reconciles it; 'refresh' reconciles an existing one; 'auto' (default) creates when missing or schema-stale. Reconcile = git diff of the worktree's indexed_commit->HEAD UNION git status dirt -> ONE targeted delta (no mtime sweep of a fresh checkout). Target must be a worktree from the 'worktrees' tool. A worktree whose own phoenix instance is running reports worktree_index_locked — refresh from that session instead.")]
    public string IndexWorktree(
        [Description("Worktree path exactly as the 'worktrees' tool lists it.")] string path,
        [Description("'auto' (default) | 'create' (recreate from a fresh seed) | 'refresh' (existing only).")] string mode = "auto")
    {
        if (NotReady() is { } notReady) return notReady;
        if (mode is not ("auto" or "create" or "refresh"))
        {
            return Json.Serialize(new { error = "bad_request", detail = "mode must be 'auto', 'create', or 'refresh'." });
        }
        // The RUNNING instance owns its own index — the ownership probe would trip on our own
        // handles and the right tool already exists. Say so instead of a confusing lock error.
        string ownFull = System.IO.Path.GetFullPath(_manager.WorkspaceRoot).Replace('\\', '/').TrimEnd('/');
        string targetFull = System.IO.Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        if (string.Equals(ownFull, targetFull, StringComparison.OrdinalIgnoreCase))
        {
            return Json.Serialize(new
            {
                error = "own_workspace",
                detail = "This is the current instance's own workspace — use refresh_index (force:'incremental'|'full') instead.",
                meta = Meta.From(_manager.Health(), "indexed", "text"),
            });
        }

        var result = WorktreeIndexer.Ensure(_manager.WorkspaceRoot, _manager.DbPath, path, mode, _ => { });
        var meta2 = Meta.From(_manager.Health(), "indexed", "text");
        bool ok = result.Action is "created" or "refreshed";
        return Json.Serialize(new
        {
            path,
            action = ok ? result.Action : null,
            error = ok ? null : result.Action,
            detail = result.Detail,
            addedFiles = ok ? result.AddedFiles : (int?)null,
            changedFiles = ok ? result.ChangedFiles : (int?)null,
            deletedFiles = ok ? result.DeletedFiles : (int?)null,
            // Honest reconcile provenance: false = the targeted git-diff/status path; true =
            // the full hash sweep (git failed, no stored commit, or the union blew the cap).
            usedFullSweep = ok ? result.UsedFullSweep : (bool?)null,
            indexedCommit = result.IndexedCommit,
            elapsedMs = result.ElapsedMs,
            meta = meta2,
        });
    }
}
