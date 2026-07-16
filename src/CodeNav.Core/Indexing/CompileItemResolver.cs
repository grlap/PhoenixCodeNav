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
/// F# SDK projects have no invented default unless the project explicitly enables SDK default
/// compile items; their ordered Compile items otherwise remain the ownership authority. Does not
/// own: parsing project files (ProjectFileParser) or glob matching (MsBuildGlob).
/// </summary>
public static class CompileItemResolver
{
    public static void Write(
        IndexStore store,
        SqliteTransaction tx,
        IReadOnlyList<ParsedProject> projects,
        IReadOnlyDictionary<string, long> projectIds,
        IReadOnlyDictionary<string, long> sourceFileIdsByPath)
    {
        // Sorted path index so an include glob scans only the range under its literal prefix
        // instead of every file (a 2k-legacy-project monolith would otherwise pay P x F matches).
        var pathsByLanguage = sourceFileIdsByPath.Keys
            .GroupBy(SourceLanguage, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(path => path, WorkspacePaths.FileSystemPathComparer).ToArray(),
                StringComparer.Ordinal);

        var defaultRoots = new Dictionary<(string Language, string Dir), long>(
            new LanguageDirectoryComparer());
        var removesByPid = new Dictionary<long, List<string>>();

        foreach (var p in projects)
        {
            long pid = projectIds[p.RelPath];
            pathsByLanguage.TryGetValue(p.Language, out string[]? projectLanguagePaths);
            projectLanguagePaths ??= [];
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
                    if (SourceLanguage(item) == p.Language &&
                        sourceFileIdsByPath.TryGetValue(item, out long fid))
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
                defaultRoots[(p.Language, dir)] = pid;
            }

            if (p.CompileIncludeGlobs is { } globs)
            {
                foreach (var glob in globs)
                {
                    foreach (var path in CandidatePaths(projectLanguagePaths,
                                 sourceFileIdsByPath, glob.Include))
                    {
                        if (SourceLanguage(path) == p.Language &&
                            MsBuildGlob.IsMatch(path, glob.Include) &&
                            !IsRemoved(glob.Excludes, path))
                        {
                            store.InsertCompileItem(tx, pid, sourceFileIdsByPath[path]);
                        }
                    }
                }
            }
        }

        if (defaultRoots.Count == 0) return;

        foreach (var (path, fid) in sourceFileIdsByPath)
        {
            // Language grouping is broader than SDK implicit ownership: F# projects may explicitly
            // compile .fsi files, and .fsx remains searchable text, but the SDK default glob is
            // **/*.fs only. Never turn an unlisted signature or script into a compiled item.
            if (!IsImplicitDefaultSource(path)) continue;

            // Longest project-dir prefix wins (approximation of SDK globbing) — but a file the
            // winning project explicitly <Compile Remove>d is NOT compiled by it, and this nearest-
            // root model does not cascade to a shallower project (cascading would resurrect dead
            // twins as "compiled by outer"): it is simply dead (orphaned).
            string dir = path;
            while (true)
            {
                int slash = dir.LastIndexOf('/');
                dir = slash < 0 ? "" : dir[..slash];
                if (defaultRoots.TryGetValue((SourceLanguage(path), dir), out long pid))
                {
                    if (!removesByPid.TryGetValue(pid, out var removes) || !IsRemoved(removes, path))
                    {
                        store.InsertCompileItem(tx, pid, fid);
                    }
                    break;
                }
                if (slash < 0) break;
            }
        }
    }

    private static string SourceLanguage(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsx", StringComparison.OrdinalIgnoreCase)
            ? "fs"
            : "cs";
    }

    internal static bool IsImplicitDefaultSource(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fs", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LanguageDirectoryComparer : IEqualityComparer<(string Language, string Dir)>
    {
        public bool Equals((string Language, string Dir) x, (string Language, string Dir) y) =>
            StringComparer.Ordinal.Equals(x.Language, y.Language) &&
            WorkspacePaths.FileSystemPathComparer.Equals(x.Dir, y.Dir);

        public int GetHashCode((string Language, string Dir) value) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Language),
                WorkspacePaths.FileSystemPathComparer.GetHashCode(value.Dir));
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
