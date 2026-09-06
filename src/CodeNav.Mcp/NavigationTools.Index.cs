using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: refresh_index request validation and mutation queueing.
/// Does not own: index build/refresh execution or shared writer/unavailable envelopes.
/// </summary>
public sealed partial class NavigationTools
{
    // ---------------------------------------------------------------- maintenance

    [McpServerTool(Name = "refresh_index")]
    [Description("Queue an index refresh through the shared daemon. A diagnostics-only non-writer instance returns index_writer_required. force='auto'/'incremental': targeted paths or a change-detection sweep — hash-identical files are SKIPPED, so this never rebuilds an intact-looking index. force='full': the 'I know the db is wrong' hatch — delete the index and REBUILD FROM SCRATCH (works even from state 'failed'; watch server_capabilities.index.progress). Normally unnecessary — the daemon's file watcher keeps the index fresh.")]
    public string RefreshIndex(
        [Description("Optional EXACT workspace-relative paths as comma-separated text or a JSON-array string (maximum 256 paths and 64 KiB input) — no globs (a glob silently matches nothing). Rooted, traversing, malformed, or control-character paths return bad_request. Use '/' on Unix, where a single backslash remains a legal filename character; Windows accepts either separator. Ignored with force='full'.")] string? paths = null,
        [Description("'auto' (default) / 'incremental': delta refresh, unchanged files skipped. 'full': rebuild from scratch — corruption/recovery hatch.")] string force = "auto")
    {
        // The two paths are EXPLICIT by contract (field: "calling refresh_index and hoping is
        // not that" — a caller recovering from corruption needs a way in; the delta path's
        // hash-skip makes it a no-op on untouched files by design).
        if (force is not ("auto" or "incremental" or "full"))
        {
            return Json.Serialize(new
            {
                error = "bad_request",
                detail = $"Unknown force value '{force}'. Valid: auto, incremental, full.",
            });
        }
        if (_manager.IsFollower) return IndexWriterRequired();
        if (force == "full")
        {
            if (!_manager.RequestFullRebuild())
                return _manager.IsFollower ? IndexWriterRequired() : IndexMutationUnavailable();
            return Json.Serialize(new
            {
                queued = true,
                scope = "full rebuild from scratch",
                hint = "The index is discarded and rebuilt; poll server_capabilities.index.progress (state 'building') until 'ready'.",
                meta = Meta.From(_manager.Health(), "indexed", "text"),
            });
        }
        List<string>? parsedPaths = null;
        if (paths is not null &&
            !TryParsePathList(paths, ExplicitPathInputLimit, out parsedPaths, out string? detail))
        {
            return Json.Serialize(new { error = "bad_request", detail });
        }

        // Normalize the host platform's separator to the forward-slash form stored by the index.
        // On Unix a backslash is a legal filename character and must remain byte-for-byte distinct;
        // on Windows this still accepts either slash style (bug 9h3).
        var list = parsedPaths?.Select(NormalizePath).ToList();
        if (!_manager.RequestRefresh(list?.Count > 0 ? list : null))
            return _manager.IsFollower ? IndexWriterRequired() : IndexMutationUnavailable();
        return Json.Serialize(new
        {
            queued = true,
            scope = list?.Count > 0 ? $"{list.Count} paths" : "full sweep",
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
    }
}
