namespace CodeNav.Core.Discovery;

public sealed record ScannedFile(string RelPath, long Size, long MtimeTicks);

public sealed record ScanResult(
    string Root,
    List<ScannedFile> CsFiles,
    List<ScannedFile> ProjectFiles,
    List<ScannedFile> SolutionFiles,
    List<ScannedFile> ConfigFiles);

/// <summary>
/// Owns: walking a workspace root with default exclusions and bucketing files by kind.
/// Does not own: parsing any file contents.
/// </summary>
public static class WorkspaceScanner
{
    public static readonly string[] DefaultExcludedDirs =
    {
        ".git", ".vs", ".idea", ".vscode", "bin", "obj", "packages",
        "node_modules", "TestResults", ".codenav",
    };

    /// <summary>True when any path segment is an excluded directory name — the same rule the scan
    /// walk applies. Shared by the watcher and by targeted refresh paths (git reconcile /
    /// refresh_index feed raw paths), so no excluded-dir file can enter the index by any route.</summary>
    public static bool IsExcludedPath(string relPath)
    {
        foreach (var segment in relPath.Split('/'))
        {
            foreach (var excluded in DefaultExcludedDirs)
            {
                if (segment.Equals(excluded, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static readonly HashSet<string> ConfigFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
        "global.json", "NuGet.config", "nuget.config", "packages.config",
        "app.config", "web.config",
    };

    public static ScanResult Scan(string root)
    {
        root = Path.GetFullPath(root);
        var excluded = new HashSet<string>(DefaultExcludedDirs, StringComparer.OrdinalIgnoreCase);

        var cs = new List<ScannedFile>();
        var projects = new List<ScannedFile>();
        var solutions = new List<ScannedFile>();
        var configs = new List<ScannedFile>();

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo di)
                {
                    if (excluded.Contains(di.Name)) continue;
                    // Do not follow symlinks/junctions (escape protection). The cached
                    // Attributes bit gates the handle-opening LinkTarget read so ordinary
                    // dirs cost nothing; cloud-synced dirs (bit set, null LinkTarget) are walked.
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0 && di.LinkTarget is not null) continue;
                    stack.Push(di.FullName);
                    continue;
                }

                var fi = (FileInfo)entry;
                // Do not index symlinked files (target may be outside the workspace). Same
                // attribute-gated LinkTarget check: ordinary files pay nothing, cloud
                // placeholder source files (bit set, null LinkTarget) are still indexed.
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 && fi.LinkTarget is not null) continue;
                string rel = WorkspacePaths.ToGitPath(Path.GetRelativePath(root, fi.FullName));
                var scanned = new ScannedFile(rel, fi.Length, fi.LastWriteTimeUtc.Ticks);

                string ext = fi.Extension;
                if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    cs.Add(scanned);
                }
                else if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(scanned);
                }
                else if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                         ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
                {
                    solutions.Add(scanned);
                }
                else if (ConfigFileNames.Contains(fi.Name) ||
                         ext.Equals(".json", StringComparison.OrdinalIgnoreCase) && fi.Name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
                {
                    configs.Add(scanned);
                }
            }
        }

        return new ScanResult(root, cs, projects, solutions, configs);
    }
}
