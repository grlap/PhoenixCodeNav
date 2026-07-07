namespace CodeNav.Core;

/// <summary>
/// Owns: safe resolution of caller-supplied paths against a workspace root. Rejects
/// absolute/rooted paths and any '..' traversal that escapes the root, so tools cannot
/// read or index files outside the workspace via a plain relative path. Shared by the MCP
/// tool layer (source_context) and the delta refresher (refresh_index).
/// Does not own: index lookups keyed by relative path (those are inherently contained
/// because only in-workspace files are ever indexed).
/// Residual risk: containment is lexical (Path.GetFullPath does not resolve links). The
/// read/scan sites additionally reject reparse-point files via <see cref="IsReparsePoint"/>,
/// but an external target reached only through an ANCESTOR junction on a caller-supplied
/// path is not fully resolved here; the scanner already excludes reparse-point directories,
/// so such a path cannot enter the index that way.
/// </summary>
public static class WorkspacePaths
{
    /// <summary>Normalizes to forward-slash, workspace-relative form (no leading slash).</summary>
    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// True if the path is a genuine symlink or NTFS junction (has a link target).
    /// Deliberately uses <see cref="FileSystemInfo.LinkTarget"/> rather than the
    /// ReparsePoint attribute bit: cloud placeholders (OneDrive Files On-Demand, Dropbox
    /// Smart Sync, ...) also set that bit but are legitimate in-workspace files, not links
    /// to external content — so the attribute bit would wrongly exclude real source files.
    /// </summary>
    public static bool IsReparsePoint(string fullPath)
    {
        try
        {
            // Cheap attribute check first (one GetFileAttributes syscall, no handle). Only
            // the rare reparse-point entries pay for the LinkTarget handle-open that tells a
            // genuine link from a cloud placeholder (OneDrive), which has a null LinkTarget.
            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.ReparsePoint) == 0) return false;
            FileSystemInfo info = (attrs & FileAttributes.Directory) != 0
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath);
            return info.LinkTarget is not null;
        }
        catch
        {
            return false; // absent/unreadable — nothing to follow
        }
    }

    /// <summary>
    /// True if <paramref name="fullPath"/> reaches outside the workspace through a reparse
    /// point (symlink/junction) on the target itself OR any ancestor directory up to the
    /// root. Read/refresh sites use this on caller-supplied paths so an in-workspace link
    /// cannot be followed to external content. Only existing components are checked; a
    /// not-yet-created leaf under a clean tree is not an escape.
    /// </summary>
    public static bool EscapesViaReparsePoint(string workspaceRoot, string fullPath)
    {
        string root, current;
        try
        {
            root = Path.GetFullPath(workspaceRoot);
            current = Path.GetFullPath(fullPath);
        }
        catch
        {
            return true; // unresolvable — treat as unsafe
        }

        string rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        while (current.Length > rootWithSep.Length &&
               current.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(current)) return true; // symlink/junction — not a cloud placeholder
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length) break;
            current = parent;
        }
        return false;
    }

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
