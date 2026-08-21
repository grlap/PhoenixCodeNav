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
    /// <summary>Comparer for filesystem/Git path identity on this host. Names and language
    /// identifiers use their own comparers; this is only for path-keyed collections.</summary>
    public static StringComparer FileSystemPathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static StringComparison FileSystemPathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>Normalizes a caller/platform path to forward-slash, workspace-relative form.
    /// Backslash is a separator on Windows, but remains a legal filename character on Unix.</summary>
    public static string Normalize(string path) =>
        Normalize(path, Path.DirectorySeparatorChar);

    internal static string Normalize(string path, char directorySeparator) =>
        ToGitPath(path, directorySeparator).TrimStart('/');

    /// <summary>Converts the current platform's directory separator to Git/index '/' form without
    /// rewriting a literal Unix backslash. Use this only for paths produced by filesystem APIs;
    /// Git paths already use '/' and can be retained verbatim.</summary>
    public static string ToGitPath(string platformPath) =>
        ToGitPath(platformPath, Path.DirectorySeparatorChar);

    internal static string ToGitPath(string platformPath, char directorySeparator) =>
        platformPath.Replace(directorySeparator, '/');

    /// <summary>Canonicalizes an absolute caller/filesystem path for comparison and display.
    /// Only the host separator is rewritten, so a literal Unix backslash cannot alias a
    /// different slash-delimited path.</summary>
    public static string NormalizeFullForComparison(string path) =>
        NormalizeFullForComparison(Path.GetFullPath(path), Path.DirectorySeparatorChar);

    internal static string NormalizeFullForComparison(string fullPath,
        char directorySeparator)
    {
        string normalized = ToGitPath(fullPath, directorySeparator);
        string root = ToGitPath(Path.GetPathRoot(fullPath) ?? "", directorySeparator);
        return normalized.Length == root.Length ? normalized : normalized.TrimEnd('/');
    }

    /// <summary>Attempts to canonicalize an absolute-or-relative caller path for comparison.
    /// Invalid path strings fail without throwing across the MCP boundary.</summary>
    public static bool TryNormalizeFullForComparison(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalized = NormalizeFullForComparison(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Compares filesystem paths with the host's path-name case rules. Unix paths are
    /// ordinal so case-distinct worktrees cannot alias; Windows paths are case-insensitive.</summary>
    public static bool FullPathsEqual(string left, string right) =>
        TryNormalizeFullForComparison(left, out string normalizedLeft) &&
        TryNormalizeFullForComparison(right, out string normalizedRight) &&
        string.Equals(normalizedLeft, normalizedRight, FileSystemPathComparison);

    /// <summary>True when <paramref name="candidate"/> is the same filesystem path as, or is
    /// lexically contained by, <paramref name="parent"/> under the host's path rules.</summary>
    public static bool IsSameOrDescendantPath(string candidate, string parent)
    {
        if (!TryNormalizeFullForComparison(candidate, out string normalizedCandidate) ||
            !TryNormalizeFullForComparison(parent, out string normalizedParent))
        {
            return false;
        }

        if (string.Equals(normalizedCandidate, normalizedParent, FileSystemPathComparison)) return true;
        string prefix = normalizedParent.EndsWith('/') ? normalizedParent : normalizedParent + "/";
        return normalizedCandidate.StartsWith(prefix, FileSystemPathComparison);
    }

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
               current.StartsWith(rootWithSep, FileSystemPathComparison))
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

        // Reject drive-rooted ("C:/x") and, after platform-aware slash normalization,
        // anything the framework still considers rooted. On Unix a backslash is a literal
        // filename character; on Windows it was normalized to '/'.
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

    /// <summary>Resolves a Git path, whose separator is always '/', without rewriting a literal
    /// backslash on Unix (where '\\' is a legal filename character). This is intentionally
    /// separate from caller-path normalization, which accepts either slash style.</summary>
    public static bool TryResolveGitPathInside(string workspaceRoot, string gitPath,
        out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(gitPath) || gitPath[0] == '/' || gitPath.Contains('\0'))
            return false;
        string platformPath = gitPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(platformPath)) return false;

        try
        {
            string root = Path.GetFullPath(workspaceRoot);
            string combined = Path.GetFullPath(Path.Combine(root, platformPath));
            string relativeBack = Path.GetRelativePath(root, combined);
            if (Path.IsPathRooted(relativeBack) || relativeBack == ".." ||
                relativeBack.StartsWith(".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return false;
            }
            fullPath = combined;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
