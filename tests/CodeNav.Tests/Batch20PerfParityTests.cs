using CodeNav.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Perf-cluster parity regressions (review findings). The perf rewrites must be behavior-preserving:
/// (1) zki — pure-SDK workspaces attribute an added .cs incrementally; ANY legacy project forces the
/// full rebuild, because a legacy explicit &lt;Compile&gt; list can claim a re-added file without its
/// csproj changing (git stash pop / branch switch) — the incremental walk permanently lost that
/// ownership. (2) dz3 — the batched fingerprint query must union case-variant project names exactly
/// like the single query, or the warm check and load fingerprint permanently disagree (reload loop).
/// (3) 22s — an unknown graph direction returns EMPTY at depth 1, like the BFS. (4) 8tb — a name
/// containing '\n' must return no hits (the single-pass scan would otherwise loop forever).
/// </summary>
public class Batch20PerfParityTests
{
    private static string NewRoot(string tag) => Directory.CreateTempSubdirectory(tag).FullName;

    private static void WriteSdkWorkspace(string root)
    {
        // Two SDK projects with CASE-VARIANT assembly names + a project reference between them.
        Directory.CreateDirectory(Path.Combine(root, "Sdk1"));
        File.WriteAllText(Path.Combine(root, "Sdk1", "Sdk1.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>CaseProj</AssemblyName></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Sdk1", "A.cs"), "namespace S1 { public class AlphaOne { } }");
        Directory.CreateDirectory(Path.Combine(root, "Sdk2"));
        File.WriteAllText(Path.Combine(root, "Sdk2", "Sdk2.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>caseproj</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Sdk1\\Sdk1.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Sdk2", "B.cs"), "namespace S2 { public class BetaTwo { } }");
    }

    [Fact]
    public void PureSdkWorkspaceAttributesAddedFileIncrementally()
    {
        string root = NewRoot("codenav-sdkonly");
        try
        {
            WriteSdkWorkspace(root);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            File.WriteAllText(Path.Combine(root, "Sdk1", "New.cs"), "namespace S1 { public class NewOne { } }");
            using var store = new IndexStore(dbPath, createNew: false);
            var result = DeltaRefresher.Refresh(store, root, new[] { "Sdk1/New.cs" });
            Assert.Equal(1, result.AddedFiles);
            Assert.False(result.ProjectsRefreshed); // no legacy -> incremental, no global rebuild
            using var q = new IndexQueries(dbPath);
            Assert.Contains(q.ProjectsContaining("Sdk1/New.cs"), p => p.Name == "CaseProj");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* Windows lock */ }
        }
    }

    // Review A.1 repro: a legacy-listed file deleted then re-added (stash pop / branch switch) must
    // keep its ownership — the incremental path cannot know legacy lists, so legacy forces the rebuild.
    [Fact]
    public void LegacyReAddedFileKeepsOwnership()
    {
        string root = NewRoot("codenav-legacyadd");
        try
        {
            WriteSdkWorkspace(root);
            Directory.CreateDirectory(Path.Combine(root, "Leg"));
            File.WriteAllText(Path.Combine(root, "Leg", "Leg.csproj"),
                "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
                + "<PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><AssemblyName>LegLib</AssemblyName></PropertyGroup>"
                + "<ItemGroup><Compile Include=\"L.cs\" /></ItemGroup></Project>");
            string lPath = Path.Combine(root, "Leg", "L.cs");
            const string lContent = "namespace L { public class LegacyOne { } }";
            File.WriteAllText(lPath, lContent);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var store = new IndexStore(dbPath, createNew: false);
            using (var q0 = new IndexQueries(dbPath))
                Assert.Contains(q0.ProjectsContaining("Leg/L.cs"), p => p.Name == "LegLib"); // baseline

            File.Delete(lPath);
            DeltaRefresher.Refresh(store, root, new[] { "Leg/L.cs" });
            File.WriteAllText(lPath, lContent); // re-add, csproj UNCHANGED
            var result = DeltaRefresher.Refresh(store, root, new[] { "Leg/L.cs" });

            Assert.Equal(1, result.AddedFiles);
            Assert.True(result.ProjectsRefreshed, "legacy present — the full rebuild must run");
            using var q = new IndexQueries(dbPath);
            Assert.Contains(q.ProjectsContaining("Leg/L.cs"), p => p.Name == "LegLib"); // ownership restored
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* Windows lock */ }
        }
    }

    [Fact]
    public void BatchedFingerprintsUnionCaseVariantNamesLikeTheSingleQuery()
    {
        string root = NewRoot("codenav-fpcase");
        try
        {
            WriteSdkWorkspace(root); // CaseProj + caseproj
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);

            var single = q.ProjectFingerprint("CaseProj"); // NOCASE union of BOTH projects' files
            var batch = q.ProjectFingerprints(new[] { "CaseProj" });
            Assert.True(batch.TryGetValue("CaseProj", out var batched), "batched fingerprint missing");
            Assert.Equal(single, batched); // review dz3: mismatch = permanent reload loop
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* Windows lock */ }
        }
    }

    [Fact]
    public async Task GraphDepthOneMatchesBfsContract()
    {
        string root = NewRoot("codenav-graph1");
        try
        {
            WriteSdkWorkspace(root);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);

            Assert.Single(q.ProjectGraph("caseproj", 1, "downstream")); // Sdk2 -> Sdk1
            Assert.Single(q.ProjectGraph("CaseProj", 1, "upstream"));
            Assert.Empty(q.ProjectGraph("caseproj", 1, "bogus")); // review 22s: BFS returned [] for unknown directions
            // 8tb hang guard: a name containing '\n' returns instantly with no hits (never loops).
            var scan = Task.Run(() => q.ReferenceCandidates("\nAlphaOne", 50).TotalHits);
            var winner = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(winner == scan, "newline-name scan did not terminate (8tb hang)");
            Assert.Equal(0, await scan);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* Windows lock */ }
        }
    }
}
