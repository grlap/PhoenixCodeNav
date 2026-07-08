using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Compiled-awareness (id8): the "orphaned" signal = a .cs file indexed from the tree but in NO
/// project's compile set. It is a BEST-EFFORT, over-inclusive "likely dead code" hint (the compile
/// graph grep lacks) — NOT a compiler fact — and it is additive only (a flag, never a filter),
/// because it has false positives. These tests pin both directions: a genuinely-dead root file is
/// flagged (true positive), and a genuinely-COMPILED legacy-wildcard file is ALSO flagged (known
/// false positive) — so nobody mistakes the flag for proof or hides results on it.
/// </summary>
public class Batch12CompiledAwarenessTests
{
    [Fact]
    public void OrphanedFileIsCountedAndFlagged()
    {
        string root = Directory.CreateTempSubdirectory("codenav-orphan").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 6, seed: 11);
            // A .cs at the workspace root — walked by the scanner but in no project's compile set.
            File.WriteAllText(Path.Combine(root, "OrphanMarker.cs"),
                "namespace Dead { public class OrphanMarkerType { } }");

            // KNOWN false positive: a legacy project whose sources are a wildcard <Compile> include.
            // ProjectFileParser skips wildcard includes, so CompileItemResolver takes the (empty)
            // explicit branch and writes NO compile_items — the file lands as orphaned even though it
            // genuinely compiles. Pinning this keeps the signal honest (best-effort / over-inclusive),
            // and is exactly why orphaned is a flag, never a filter: hiding on it could bury live code.
            string legacyDir = Path.Combine(root, "LegacyWild");
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(Path.Combine(legacyDir, "LegacyWild.csproj"),
                "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
                + "<PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion></PropertyGroup>"
                + "<ItemGroup><Compile Include=\"**\\*.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(legacyDir, "LiveButWild.cs"),
                "namespace LegacyWild { public class LiveButWildType { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using (var q = new IndexQueries(dbPath))
            {
                Assert.Contains("OrphanMarker.cs", q.OrphanedPaths(new[] { "OrphanMarker.cs" }));
                Assert.True(q.Overview().OrphanedFiles >= 1, "orphaned root file not counted");
                // A file the generator DOES compile is not orphaned.
                var compiled = q.FindFiles("Guard.cs", 1);
                if (compiled.Count > 0)
                    Assert.Empty(q.OrphanedPaths(new[] { compiled[0].Path }));
                // The known false positive is CHARACTERIZED, not silently "correct": the compiled
                // legacy-wildcard file IS flagged orphaned. If this ever flips, the signal's shape
                // changed and the honesty wording (over-inclusive, additive-only) must be revisited.
                Assert.Contains("LegacyWild/LiveButWild.cs",
                    q.OrphanedPaths(new[] { "LegacyWild/LiveButWild.cs" }));
            }

            var manager = new IndexManager(root, dbPath);
            try
            {
                manager.Start();
                for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
                Assert.True(manager.IsQueryable);
                var tools = new NavigationTools(manager, new CodeNav.Core.Semantic.SemanticService(manager));

                // The per-hit flag is additive: the hit is returned AND tagged orphaned:true (never hidden).
                var hit = JsonDocument.Parse(tools.SearchSymbol("OrphanMarkerType", match: "exact"))
                    .RootElement.GetProperty("symbols").EnumerateArray().Single();
                Assert.True(hit.GetProperty("orphaned").GetBoolean()); // flagged as likely dead code

                Assert.True(JsonDocument.Parse(tools.RepoOverview()).RootElement.GetProperty("orphanedFiles").GetInt64() >= 1);
            }
            finally
            {
                manager.Dispose();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }
}
