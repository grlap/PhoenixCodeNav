using System.Text;
using CodeNav.Core.Indexing;

namespace CodeNav.Core.Discovery;

/// <summary>One scan/refresh's directory boundaries. Linked worktrees are independent workspaces;
/// ordinary nested repositories, submodules and directories named .worktrees remain traversable.</summary>
internal sealed class WorkspaceExclusions(string readRoot, string? publishedRoot = null)
{
    private readonly string _readRoot = Path.GetFullPath(readRoot);
    private readonly string _publishedRoot = Path.GetFullPath(publishedRoot ?? readRoot);
    private readonly Dictionary<string, bool> _directories = new(WorkspacePaths.FileSystemPathComparer);

    internal bool Contains(string relativePath)
    {
        if (WorkspaceScanner.IsExcludedPath(relativePath)) return true;
        int slash = relativePath.LastIndexOf('/');
        return slash >= 0 && ExcludesDirectory(relativePath[..slash]);
    }

    internal bool ExcludesDirectory(string relativeDirectory)
    {
        if (relativeDirectory.Length == 0 || relativeDirectory == ".") return false;
        if (_directories.TryGetValue(relativeDirectory, out bool excluded)) return excluded;
        int slash = relativeDirectory.LastIndexOf('/');
        excluded = slash >= 0 && ExcludesDirectory(relativeDirectory[..slash]) ||
                   IsLinkedWorktree(relativeDirectory);
        _directories[relativeDirectory] = excluded;
        return excluded;
    }

    private bool IsLinkedWorktree(string relativeDirectory)
    {
        if (!WorkspacePaths.TryResolveGitPathInside(_readRoot, relativeDirectory + "/.git",
                out string pointerPath) || !File.Exists(pointerPath)) return false;
        try
        {
            // Reuse the anchored regular-file reader, including /proc/fd workspace roots.
            // No Git subprocess, directory-name convention, or new metadata size policy.
            byte[]? pointer = GitInfo.ReadBoundedWorkspaceFile(_readRoot,
                relativeDirectory + "/.git", int.MaxValue);
            if (pointer is null) return false;
            string text = Encoding.UTF8.GetString(pointer).TrimEnd('\r', '\n');
            if (!text.StartsWith("gitdir: ", StringComparison.Ordinal)) return false;
            string expected = Path.GetFullPath(relativeDirectory + "/.git", _publishedRoot);
            string gitDirectory = Path.GetFullPath(text[8..], Path.GetDirectoryName(expected)!);
            string? common = ReadMetadata(gitDirectory, "commondir");
            string? backlink = ReadMetadata(gitDirectory, "gitdir");
            if (string.IsNullOrEmpty(common) || string.IsNullOrEmpty(backlink) ||
                !Directory.Exists(Path.GetFullPath(common, gitDirectory))) return false;
            return WorkspacePaths.FullPathsEqual(Path.GetFullPath(backlink, gitDirectory), expected);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static string? ReadMetadata(string directory, string name)
    {
        byte[]? bytes = GitInfo.ReadBoundedRegularFile(Path.Combine(directory, name),
            int.MaxValue, directory);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
    }
}
