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
                    // Do not follow reparse points (symlink/junction escape protection).
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push(di.FullName);
                    continue;
                }

                var fi = (FileInfo)entry;
                string rel = Path.GetRelativePath(root, fi.FullName).Replace('\\', '/');
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
