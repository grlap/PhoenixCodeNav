using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Compiled-awareness (id8, tightened by 3tz): the "orphaned" signal = a .cs file indexed from the
/// tree but in NO project's compile set. Since 3tz the compile graph expands &lt;Compile Include&gt;
/// wildcard globs and honors &lt;Compile Remove&gt;, so the old legacy-wildcard false positive is gone;
/// the flag remains additive only (never a filter) because residual gaps remain (.projitems, props
/// globs, ignored Conditions). These tests pin: a genuinely-dead root file is flagged, and a
/// legacy-wildcard file is correctly COMPILED.
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

            // 3tz flipped this: a legacy project whose sources are a wildcard <Compile> include used
            // to be a KNOWN false positive (wildcards were skipped -> the whole live project looked
            // orphaned). The resolver now EXPANDS include globs, so this file is correctly attributed.
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
                // 3tz: the legacy-wildcard file is now correctly COMPILED (include globs expanded) —
                // the signal's biggest false-positive class is gone.
                Assert.Empty(q.OrphanedPaths(new[] { "LegacyWild/LiveButWild.cs" }));
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
