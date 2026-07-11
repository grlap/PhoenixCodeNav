using CodeNav.Core.Discovery;
using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: mapping files to owning projects — the compile graph every search/resolution decision
/// trusts ("is this file REALLY compiled?", 3tz). Per project: explicit legacy &lt;Compile&gt; items and
/// expanded &lt;Compile Include&gt; wildcard globs (each pruned only by its own Exclude=), plus the SDK
/// default **/*.cs-under-project-dir approximated by longest-dir-prefix MINUS unconditioned
/// &lt;Compile Remove&gt; globs — a default-owned file the project removes is exactly the dead-twin
/// case: on disk, never compiled. Removes deliberately do NOT prune explicit includes (an include is
/// affirmative evidence; remove-all-then-reinclude then comes out right without ordered evaluation),
/// and a removed file does not cascade to a shallower project — that is this nearest-root MODEL's
/// rule (real MSBuild globs can reach deeper, but cascading would resurrect dead twins). Shared by
/// IndexBuilder (initial build) and DeltaRefresher (project-phase rebuild).
/// Does not own: parsing csproj (ProjectFileParser) or glob matching (MsBuildGlob).
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
        // Sorted path index so an include glob scans only the range under its literal prefix
        // instead of every file (a 2k-legacy-project monolith would otherwise pay P x F matches).
        var sortedPaths = csFileIdsByPath.Keys.ToArray();
        Array.Sort(sortedPaths, WorkspacePaths.FileSystemPathComparer);

        var defaultRoots = new Dictionary<string, long>(WorkspacePaths.FileSystemPathComparer);
        var removesByPid = new Dictionary<long, List<string>>();

        foreach (var p in projects)
        {
            long pid = projectIds[p.RelPath];
            var removes = p.CompileRemoveGlobs;
            if (removes is { Count: > 0 }) removesByPid[pid] = removes;

            // POLICY (review 3a): a Remove prunes DEFAULT-item ownership only — an explicit Include
            // (exact or glob) is affirmative evidence of compilation and always wins. This makes the
            // common remove-all-then-reinclude idiom (<Compile Remove="**/*.cs"/> then
            // <Compile Include="Subset/**"/>) come out right without ordered MSBuild evaluation, and
            // errs toward over-inclusion, never toward falsely orphaning live files. Exclude= is
            // different: it prunes only its OWN include's wildcard expansion.
            if (p.ExplicitCompileItems is { } items)
            {
                foreach (var item in items)
                {
                    if (csFileIdsByPath.TryGetValue(item, out long fid))
                    {
                        store.InsertCompileItem(tx, pid, fid);
                    }
                }
            }
            else if (p.DefaultCompileItems)
            {
                // SDK default items (or a failed-parse project, whose shape is unknowable): the
                // project dir becomes a glob root; ownership resolved in the prefix pass below.
                string dir = WorkspacePaths.ToGitPath(Path.GetDirectoryName(p.RelPath) ?? "");
                defaultRoots[dir] = pid;
            }

            if (p.CompileIncludeGlobs is { } globs)
            {
                foreach (var glob in globs)
                {
                    foreach (var path in CandidatePaths(sortedPaths, csFileIdsByPath, glob.Include))
                    {
                        if (MsBuildGlob.IsMatch(path, glob.Include) && !IsRemoved(glob.Excludes, path))
                        {
                            store.InsertCompileItem(tx, pid, csFileIdsByPath[path]);
                        }
                    }
                }
            }
        }

        if (defaultRoots.Count == 0) return;

        foreach (var (path, fid) in csFileIdsByPath)
        {
            // Longest project-dir prefix wins (approximation of SDK globbing) — but a file the
            // winning project explicitly <Compile Remove>d is NOT compiled by it, and this nearest-
            // root model does not cascade to a shallower project (cascading would resurrect dead
            // twins as "compiled by outer"): it is simply dead (orphaned).
            string dir = path;
            while (true)
            {
                int slash = dir.LastIndexOf('/');
                if (slash < 0) break;
                dir = dir[..slash];
                if (defaultRoots.TryGetValue(dir, out long pid))
                {
                    if (!removesByPid.TryGetValue(pid, out var removes) || !IsRemoved(removes, path))
                    {
                        store.InsertCompileItem(tx, pid, fid);
                    }
                    break;
                }
            }
        }
    }

    private static bool IsRemoved(List<string>? removes, string path)
    {
        if (removes is null) return false;
        foreach (var g in removes)
        {
            if (MsBuildGlob.IsMatch(path, g)) return true;
        }
        return false;
    }

    /// <summary>Paths that can possibly match the glob: an exact pattern is a dictionary probe; a
    /// wildcard pattern binary-searches the sorted list for its literal-prefix range.</summary>
    private static IEnumerable<string> CandidatePaths(
        string[] sortedPaths, IReadOnlyDictionary<string, long> byPath, string glob)
    {
        if (!MsBuildGlob.ContainsWildcard(glob))
        {
            if (byPath.ContainsKey(glob)) yield return glob;
            yield break;
        }
        string prefix = MsBuildGlob.LiteralPrefix(glob);
        int lo = LowerBound(sortedPaths, prefix);
        for (int i = lo; i < sortedPaths.Length; i++)
        {
            if (prefix.Length > 0 && !sortedPaths[i].StartsWith(prefix, WorkspacePaths.FileSystemPathComparison)) break;
            yield return sortedPaths[i];
        }
    }

    private static int LowerBound(string[] sorted, string prefix)
    {
        if (prefix.Length == 0) return 0;
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (WorkspacePaths.FileSystemPathComparer.Compare(sorted[mid], prefix) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
