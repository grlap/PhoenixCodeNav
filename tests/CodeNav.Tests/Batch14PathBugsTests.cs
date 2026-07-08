using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Path/glob correctness bugs. 6yk: a leading "**/" (any depth, INCLUDING zero) must match root-level
/// files — GlobToLike turned it into "%%/..." which required a leading segment, silently excluding
/// root files. 9h3: refresh_index must normalize backslash paths to the stored forward-slash form, or
/// it refreshes them as new paths and leaves permanent duplicate file rows.
/// </summary>
public class Batch14PathBugsTests
{
    [Fact]
    public void DoubleStarSlashGlobMatchesRootLevelFiles()
    {
        string root = Directory.CreateTempSubdirectory("codenav-6yk").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 2, seed: 5);
            File.WriteAllText(Path.Combine(root, "RootMarker.cs"), "namespace R { class RootMarkerType { } }");
            string nest = Path.Combine(root, "deep", "nest");
            Directory.CreateDirectory(nest);
            File.WriteAllText(Path.Combine(nest, "NestMarker.cs"), "namespace N { class NestMarkerType { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var q = new IndexQueries(dbPath);
            var all = q.FindFiles("**/*.cs", 5000).Select(f => f.Path).ToList();
            Assert.Contains("RootMarker.cs", all);            // bug 6yk: root file was excluded by %%/
            Assert.Contains("deep/nest/NestMarker.cs", all);  // depth still matches
            Assert.Contains("RootMarker.cs", q.FindFiles("**/RootMarker.cs", 10).Select(f => f.Path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }

    // 6yk mirror on the EXCLUDE side: a leading "**/" exclude must drop root-level matches too, or a
    // caller-supplied excludePath="**/gen/**" silently leaks a root-level "gen/..." directory.
    [Fact]
    public void DoubleStarSlashExcludeDropsRootLevelDir()
    {
        string root = Directory.CreateTempSubdirectory("codenav-6yk-ex").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 2, seed: 6);
            Directory.CreateDirectory(Path.Combine(root, "gen"));
            File.WriteAllText(Path.Combine(root, "gen", "GenMarker.cs"), "namespace G { class GenMarkerType { } }");
            Directory.CreateDirectory(Path.Combine(root, "sub", "gen"));
            File.WriteAllText(Path.Combine(root, "sub", "gen", "NestedGen.cs"), "namespace G2 { class NestedGenType { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var q = new IndexQueries(dbPath);
            var unfiltered = q.FindFiles("**/*.cs", 5000).Select(f => f.Path).ToList();
            Assert.Contains("gen/GenMarker.cs", unfiltered);        // sanity: indexed
            Assert.Contains("sub/gen/NestedGen.cs", unfiltered);

            var filtered = q.FindFiles("**/*.cs", 5000, new[] { "**/gen/**" }).Select(f => f.Path).ToList();
            Assert.DoesNotContain("gen/GenMarker.cs", filtered);       // root-level dir excluded (was leaking)
            Assert.DoesNotContain("sub/gen/NestedGen.cs", filtered);   // nested still excluded
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }

    [Fact]
    public void RefreshIndexWithBackslashPathCreatesNoDuplicateRow()
    {
        string root = Directory.CreateTempSubdirectory("codenav-9h3").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 3, seed: 9);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            var manager = new IndexManager(root, dbPath);
            try
            {
                manager.Start();
                for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
                Assert.True(manager.IsQueryable);

                string forward;
                long before;
                using (var q = manager.OpenQueries())
                {
                    var guard = q.FindFiles("Guard.cs", 1);
                    if (guard.Count == 0) return; // fixture guard
                    forward = guard[0].Path; // an indexed .cs, forward slashes
                    before = q.Overview().CsFiles;
                }

                var tools = new NavigationTools(manager, new CodeNav.Core.Semantic.SemanticService(manager));
                tools.RefreshIndex(paths: forward.Replace('/', '\\')); // backslash form of an existing path

                for (int i = 0; i < 200 && manager.Health().PendingChanges > 0; i++) Thread.Sleep(25);
                Thread.Sleep(300); // let the refresh write commit

                long after = manager.OpenQueries().Overview().CsFiles;
                Assert.Equal(before, after); // normalized -> matched the existing row; bug 9h3 would add one
            }
            finally { manager.Dispose(); }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }
}
