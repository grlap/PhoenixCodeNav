using CodeNav.Core.Discovery;
using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: mapping files to owning projects — explicit Compile items for legacy csproj,
/// longest-dir-prefix approximation for SDK-style globbing. Shared by IndexBuilder
/// (initial build) and DeltaRefresher (project-phase rebuild).
/// </summary>
public static class CompileItemResolver
{
    public static void Write(
        IndexStore store,
        SqliteTransaction tx,
        IReadOnlyList<ParsedProject> projects,
        IReadOnlyDictionary<string, long> projectIds,
        IReadOnlyDictionary<string, long> csFileIdsByPath)
    {
        var sdkDirs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in projects)
        {
            long pid = projectIds[p.RelPath];
            if (p.ExplicitCompileItems is { } items)
            {
                foreach (var item in items)
                {
                    if (csFileIdsByPath.TryGetValue(item, out long fid)) store.InsertCompileItem(tx, pid, fid);
                }
            }
            else
            {
                string dir = Path.GetDirectoryName(p.RelPath)?.Replace('\\', '/') ?? "";
                sdkDirs[dir] = pid;
            }
        }

        if (sdkDirs.Count == 0) return;

        foreach (var (path, fid) in csFileIdsByPath)
        {
            // Longest project-dir prefix wins (approximation of SDK globbing).
            string dir = path;
            while (true)
            {
                int slash = dir.LastIndexOf('/');
                if (slash < 0) break;
                dir = dir[..slash];
                if (sdkDirs.TryGetValue(dir, out long pid))
                {
                    store.InsertCompileItem(tx, pid, fid);
                    break;
                }
            }
        }
    }
}
