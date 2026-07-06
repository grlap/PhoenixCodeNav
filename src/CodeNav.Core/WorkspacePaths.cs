namespace CodeNav.Core;

/// <summary>
/// Owns: safe resolution of caller-supplied paths against a workspace root. Rejects
/// absolute/rooted paths and any '..' traversal that escapes the root, so tools cannot
/// read or index files outside the workspace via a plain relative path. Shared by the MCP
/// tool layer (source_context) and the delta refresher (refresh_index).
/// Does not own: index lookups keyed by relative path (those are inherently contained
/// because only in-workspace files are ever indexed).
/// Residual risk: containment is lexical. A symlink/NTFS-junction that lives inside the
/// root but targets an external directory is NOT resolved here and can still escape at
/// read time; reparse-point hardening is tracked separately (see backlog).
/// </summary>
public static class WorkspacePaths
{
    /// <summary>Normalizes to forward-slash, workspace-relative form (no leading slash).</summary>
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Resolves a workspace-relative path to a full filesystem path, guaranteeing the
    /// result stays inside <paramref name="workspaceRoot"/>. Returns false for absolute
    /// or rooted paths, malformed paths, or any '..' sequence that escapes the root.
    /// </summary>
    public static bool TryResolveInside(string workspaceRoot, string relPath, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relPath)) return false;

        string normalized = Normalize(relPath);
        if (normalized.Length == 0) return false;

        // Reject drive-rooted ("C:/x") and, after slash normalization, anything the
        // framework still considers rooted. Leading slashes were already trimmed, so a
        // UNC "\\server\share" degrades to a harmless relative "server/share".
        if (Path.IsPathRooted(normalized)) return false;

        string root, combined, relativeBack;
        try
        {
            root = Path.GetFullPath(workspaceRoot);
            combined = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            relativeBack = Path.GetRelativePath(root, combined);
        }
        catch (Exception)
        {
            return false; // illegal characters, path too long, etc.
        }

        // Containment via the relative path from root to target. GetRelativePath applies
        // the platform's own path-comparison semantics (case-insensitive on Windows/macOS,
        // case-sensitive on Linux), so a case-only sibling escape is rejected on the
        // filesystems where those are genuinely distinct directories. An escaping target
        // yields a result that is rooted or begins with "..".
        if (Path.IsPathRooted(relativeBack) ||
            relativeBack == ".." ||
            relativeBack.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        fullPath = combined;
        return true;
    }
}
