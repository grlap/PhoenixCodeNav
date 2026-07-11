using System.ComponentModel;
using CodeNav.Core.Indexing;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: the worktree index surface (fgq/c36) — listing the repository's worktrees with their
/// index status, and creating/refreshing SIBLING worktree indexes from this (main) instance:
/// the review-system flow where the skill creates worktrees, phoenix seeds each from a
/// transactionally consistent snapshot. Windows uses a targeted commit-diff/status reconcile;
/// Linux uses an anchored full sweep; macOS returns an explicit unsupported_platform refusal.
/// Phoenix never creates or removes worktrees — git stays READ-ONLY here by decision.
/// Does not own: the orchestration mechanics (WorktreeIndexer, Core) or this workspace's own
/// index lifecycle (refresh_index).
/// Split out of: NavigationTools.cs — new surface, kept in its own small file per house rule.
/// </summary>
public sealed partial class NavigationTools
{
    // Dynamic worktree paths and Core diagnostics originate outside the MCP response budget.
    // Keep each bounded well below the hard envelope, then still verify the complete serialized
    // result because JSON escaping and the metadata envelope also consume bytes.
    private const int WorktreeDynamicTextBytes = 4 * 1024;

    [McpServerTool(Name = "worktrees")]
    [Description("All git worktrees of this repository with anchored per-worktree index status on Windows/Linux (READ-ONLY — phoenix never creates/removes worktrees); macOS returns unsupported_platform. The enumeration to loop for 'refresh all' via index_worktree.")]
    public string Worktrees()
    {
        if (NotReady() is { } notReady) return notReady;
        var status = WorktreeIndexer.Status(_manager.WorkspaceRoot);
        if (status is null)
        {
            (string error, string detail) = WorktreeUnavailableForHost(
                OperatingSystem.IsMacOS());
            return Json.Serialize(new
            {
                error,
                detail,
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

    internal static (string Error, string Detail) WorktreeUnavailableForHost(bool isMacOS) =>
        isMacOS
            ? ("unsupported_platform", "Anchored worktree index status is not supported on macOS.")
            : ("git_unavailable", "git worktree list failed — git absent, or this workspace is not inside a git repository.");

    [McpServerTool(Name = "index_worktree")]
    [Description("Create or refresh a SIBLING worktree index. Windows uses a targeted indexed_commit-to-HEAD plus dirt reconcile; Linux uses an anchored full sweep and reports usedFullSweep=true; macOS returns unsupported_platform. Target must come from worktrees. A target owned by another Phoenix reports worktree_index_locked.")]
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
        if (!CodeNav.Core.WorkspacePaths.TryNormalizeFullForComparison(path, out _))
        {
            return Json.Serialize(new
            {
                error = "bad_request",
                detail = "path must be a non-empty valid filesystem path from the worktrees tool.",
                meta = Meta.From(_manager.Health(), "indexed", "text"),
            });
        }
        if (CodeNav.Core.WorkspacePaths.FullPathsEqual(_manager.WorkspaceRoot, path))
        {
            return Json.Serialize(new
            {
                error = "own_workspace",
                detail = "This is the current instance's own workspace — use refresh_index (force:'incremental'|'full') instead.",
                meta = Meta.From(_manager.Health(), "indexed", "text"),
            });
        }

        var result = _manager.EnsureWorktreeIndex(path, mode, _ => { });
        return SerializeIndexWorktreeResult(path, result,
            Meta.From(_manager.Health(), "indexed", "text"));
    }

    internal static string SerializeIndexWorktreeResult(string path,
        WorktreeIndexResult result, Meta meta)
    {
        int pathBytes = Json.Utf8Bytes(path);
        int detailBytes = result.Detail is null ? 0 : Json.Utf8Bytes(result.Detail);
        int pathLimit = Math.Min(pathBytes, WorktreeDynamicTextBytes);
        int detailLimit = Math.Min(detailBytes, WorktreeDynamicTextBytes);

        while (true)
        {
            string boundedPath = Json.Utf8Prefix(path, pathLimit, out bool pathTruncated);
            string? boundedDetail = result.Detail is null
                ? null
                : Json.Utf8Prefix(result.Detail, detailLimit, out _);
            bool detailTruncated = result.Detail is not null &&
                                   Json.Utf8Bytes(boundedDetail!) < detailBytes;
            bool ok = result.Action is "created" or "refreshed";
            string json = Json.Serialize(new
            {
                path = boundedPath,
                pathTruncated = pathTruncated ? true : (bool?)null,
                pathBytes = pathTruncated ? pathBytes : (int?)null,
                action = ok ? result.Action : null,
                error = ok ? null : result.Action,
                detail = boundedDetail,
                detailTruncated = detailTruncated ? true : (bool?)null,
                detailBytes = detailTruncated ? detailBytes : (int?)null,
                addedFiles = ok ? result.AddedFiles : (int?)null,
                changedFiles = ok ? result.ChangedFiles : (int?)null,
                deletedFiles = ok ? result.DeletedFiles : (int?)null,
                // Honest reconcile provenance: false = the Windows targeted git-diff/status
                // path; true = Linux's mandatory anchored sweep or a Windows fallback sweep.
                usedFullSweep = ok ? result.UsedFullSweep : (bool?)null,
                indexedCommit = result.IndexedCommit,
                elapsedMs = result.ElapsedMs,
                meta,
            });
            if (Json.Utf8Bytes(json) <= Json.HardBudgetBytes) return json;

            // Defensive future-proofing: should the fixed envelope grow, shrink dynamic fields
            // deterministically all the way to zero rather than ever emitting an oversized result.
            if (detailLimit > 0) detailLimit /= 2;
            else if (pathLimit > 0) pathLimit /= 2;
            else
            {
                return Json.Serialize(new
                {
                    error = "response_budget_exceeded",
                    detail = "The worktree result could not fit the server response budget.",
                });
            }
        }
    }
}
