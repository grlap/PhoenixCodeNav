using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Compile-graph fidelity (3tz, owner-diagnosed): before any search/resolution touches a .cs file we
/// must KNOW whether it is really compiled. The flagship case: the same interface duplicated in the
/// same namespace — a live WSDL-generated copy and a dead copy whose project EXCLUDES it from
/// compilation. The old graph called the dead copy "compiled" (SDK dir-prefix ignored Compile Remove)
/// and resolution could prefer it — implementations came back empty. Now: Include globs expanded,
/// Remove honored, EnableDefaultCompileItems honored, Conditions ignored; and semantic resolution
/// never targets an uncompiled declaration.
/// </summary>
public class Batch21CompileFidelityTests
{
    // MSBuild-ish glob semantics: '**' spans zero+ segments, '*'/'?' within a segment, case-insensitive.
    [Theory]
    [InlineData("a/b/C.cs", "a/**/*.cs", true)]
    [InlineData("a/C.cs", "a/**/*.cs", true)]          // '**' matches ZERO segments
    [InlineData("a/b/c/D.cs", "a/**/*.cs", true)]
    [InlineData("x/C.cs", "a/**/*.cs", false)]
    [InlineData("a/b/C.cs", "a/*.cs", false)]           // '*' does not cross '/'
    [InlineData("a/C.CS", "a/c.cs", true)]              // case-insensitive
    [InlineData("a/Cx.cs", "a/C?.cs", true)]
    [InlineData("Shared/X.cs", "Shared/X.cs", true)]    // literal pattern
    [InlineData("a/bC.cs", "a/*C.cs", true)]
    public void GlobMatchesMsBuildShapes(string path, string pattern, bool expected)
        => Assert.Equal(expected, MsBuildGlob.IsMatch(path, pattern));

    [Fact]
    public void CompileGraphHonorsIncludeGlobsRemovesAndSettings()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fidelity").FullName;
        try
        {
            // Legacy project with ONLY a wildcard include (previously wholly unowned) PLUS the legacy
            // exclusion idiom: Exclude= prunes the wildcard's expansion (legacy has no Compile Remove).
            Directory.CreateDirectory(Path.Combine(root, "LegWild", "Sub"));
            Directory.CreateDirectory(Path.Combine(root, "LegWild", "Old"));
            File.WriteAllText(Path.Combine(root, "LegWild", "LegWild.csproj"),
                "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
                + "<PropertyGroup><AssemblyName>LegWildLib</AssemblyName></PropertyGroup>"
                + "<ItemGroup><Compile Include=\"**\\*.cs\" Exclude=\"Old\\**\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "LegWild", "Sub", "Deep.cs"), "namespace W { class Deep { } }");
            File.WriteAllText(Path.Combine(root, "LegWild", "Old", "Gone.cs"), "namespace W { class Gone { } }");

            // SDK remove-all-then-reinclude idiom: Remove prunes DEFAULT ownership only; the explicit
            // Include glob is affirmative and wins — Subset stays compiled, the rest is orphaned.
            Directory.CreateDirectory(Path.Combine(root, "Trim", "Subset"));
            File.WriteAllText(Path.Combine(root, "Trim", "Trim.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>TrimLib</AssemblyName></PropertyGroup>"
                + "<ItemGroup><Compile Remove=\"**/*.cs\" /><Compile Include=\"Subset/**/*.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Trim", "Subset", "Kept.cs"), "namespace TR { class Kept { } }");
            File.WriteAllText(Path.Combine(root, "Trim", "Other.cs"), "namespace TR { class Other { } }");

            // SDK project with a Compile Remove — the dead-twin shape.
            Directory.CreateDirectory(Path.Combine(root, "Core"));
            File.WriteAllText(Path.Combine(root, "Core", "Core.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>CoreLib</AssemblyName></PropertyGroup>"
                + "<ItemGroup><Compile Remove=\"DeadTwin.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Core", "DeadTwin.cs"), "namespace T { public interface ITwinThing { } }");
            File.WriteAllText(Path.Combine(root, "Core", "Alive.cs"), "namespace C { class Alive { } }");

            // SDK project opting OUT of default items, compiling only an explicit include —
            // which is a LINKED file from outside the project dir.
            Directory.CreateDirectory(Path.Combine(root, "Picky"));
            Directory.CreateDirectory(Path.Combine(root, "Shared"));
            File.WriteAllText(Path.Combine(root, "Picky", "Picky.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>PickyLib</AssemblyName>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>"
                + "<ItemGroup><Compile Include=\"..\\Shared\\Linked.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Picky", "Ignored.cs"), "namespace P { class Ignored { } }");
            File.WriteAllText(Path.Combine(root, "Shared", "Linked.cs"), "namespace S { class Linked { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var q = new IndexQueries(dbPath);
            // Legacy wildcard: owned (was the biggest false-orphan class); Exclude= pruned.
            Assert.Contains(q.ProjectsContaining("LegWild/Sub/Deep.cs"), p => p.Name == "LegWildLib");
            Assert.Contains("LegWild/Old/Gone.cs", q.OrphanedPaths(new[] { "LegWild/Old/Gone.cs" }));
            // Remove-all-then-reinclude: the re-included subset is COMPILED (review 3a — the first
            // cut falsely orphaned the whole live project); the rest is correctly orphaned.
            Assert.Contains(q.ProjectsContaining("Trim/Subset/Kept.cs"), p => p.Name == "TrimLib");
            Assert.Contains("Trim/Other.cs", q.OrphanedPaths(new[] { "Trim/Other.cs" }));
            // Compile Remove: the dead twin is ORPHANED despite sitting in the project dir.
            Assert.Contains("Core/DeadTwin.cs", q.OrphanedPaths(new[] { "Core/DeadTwin.cs" }));
            Assert.Contains(q.ProjectsContaining("Core/Alive.cs"), p => p.Name == "CoreLib"); // siblings unaffected
            // EnableDefaultCompileItems=false: the in-dir file is NOT compiled; the linked ../ file IS.
            Assert.Contains("Picky/Ignored.cs", q.OrphanedPaths(new[] { "Picky/Ignored.cs" }));
            Assert.Contains(q.ProjectsContaining("Shared/Linked.cs"), p => p.Name == "PickyLib");
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* Windows lock */ }
        }
    }

    // Review 5a: an SDK project that opts OUT of default items must not act as an incremental glob
    // root — a file added under its dir would otherwise be attributed to a project that compiles
    // nothing, diverging from the full rebuild until the next csproj touch.
    [Fact]
    public void DefaultsOffProjectForcesFullRebuildOnAdd()
    {
        string root = Directory.CreateTempSubdirectory("codenav-defoff").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Pack"));
            File.WriteAllText(Path.Combine(root, "Pack", "Pack.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>PackLib</AssemblyName>"
                + "<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Pack", "Existing.cs"), "namespace PK { class Existing { } }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            File.WriteAllText(Path.Combine(root, "Pack", "Added.cs"), "namespace PK { class Added { } }");
            using var store = new IndexStore(dbPath, createNew: false);
            var result = DeltaRefresher.Refresh(store, root, new[] { "Pack/Added.cs" });
            Assert.Equal(1, result.AddedFiles);
            Assert.True(result.ProjectsRefreshed, "defaults-off shape must force the full rebuild");
            using var q = new IndexQueries(dbPath);
            Assert.Contains("Pack/Added.cs", q.OrphanedPaths(new[] { "Pack/Added.cs" })); // compiles nothing
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* Windows lock */ }
        }
    }

    // The flagship dead-twin regression: the SAME interface in the SAME namespace exists twice — a
    // dead copy (excluded via Compile Remove) and a live generated copy the implementers use.
    // implementations(name) must resolve to the LIVE twin and return the implementer at exact
    // confidence. The dead twin's dir sorts BEFORE the live one's, so the un-gated resolver would
    // pick the DEAD file — this pins the resolution gate itself, not just the Remove-awareness.
    [Fact]
    public void ImplementationsResolveTheLiveTwinNotTheDeadFile()
    {
        string root = Directory.CreateTempSubdirectory("codenav-deadtwin").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "AaCore"));
            File.WriteAllText(Path.Combine(root, "AaCore", "AaCore.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>CoreLib</AssemblyName></PropertyGroup>"
                + "<ItemGroup><Compile Remove=\"IPartnerThing.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "AaCore", "IPartnerThing.cs"),
                "namespace Partner { public interface IPartnerThing { void Create(); } }");
            File.WriteAllText(Path.Combine(root, "AaCore", "CoreHelper.cs"), "namespace Partner { class CoreHelper { } }");

            // Live twin: WSDL-generated-style project actually compiling the same declaration.
            Directory.CreateDirectory(Path.Combine(root, "ApiGen"));
            File.WriteAllText(Path.Combine(root, "ApiGen", "ApiGen.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>ApiGenLib</AssemblyName></PropertyGroup></Project>");
            // The <auto-generated> banner is load-bearing (review 4a): real WSDL output carries it, so
            // the file is is_generated=1 — the resolver must still SEE it (includeGenerated:true) or
            // the live twin never enters the candidate set and the dead file wins again.
            File.WriteAllText(Path.Combine(root, "ApiGen", "PartnerContracts.cs"),
                "// <auto-generated/>\nnamespace Partner { public interface IPartnerThing { void Create(); } }");

            // Implementer referencing the LIVE twin's project only.
            Directory.CreateDirectory(Path.Combine(root, "Impl"));
            File.WriteAllText(Path.Combine(root, "Impl", "Impl.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net472</TargetFramework><AssemblyName>ImplLib</AssemblyName></PropertyGroup>"
                + "<ItemGroup><ProjectReference Include=\"..\\ApiGen\\ApiGen.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Impl", "PartnerImpl.cs"),
                "namespace Partner { public class PartnerImpl : IPartnerThing { public void Create() { } } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            var manager = new IndexManager(root, dbPath);
            var semantic = new SemanticService(manager);
            try
            {
                manager.Start();
                for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
                Assert.True(manager.IsQueryable);
                if (!semantic.FrameworkRefsAvailable) return; // env guard: no net472 reference assemblies
                var tools = new NavigationTools(manager, semantic);

                var r = SemanticRetry.ParseExactWithRetry(() => tools.Implementations(name: "IPartnerThing", timeoutMs: 90000)); // n7ly: retries only ride out TRANSIENT degrades — the dead-twin regression is deterministic-heuristic and fails every attempt
                // MUST be exact: resolving the dead twin degrades to heuristic — the pre-fix failure.
                // (A confidence-based skip here would silently mask the regression it exists to pin.)
                Assert.Equal("exact", r.GetProperty("meta").GetProperty("confidence").GetString());

                var impls = r.GetProperty("implementations").EnumerateArray().ToList();
                Assert.Contains(impls, i =>
                    (i.GetProperty("symbol").GetProperty("display").GetString() ?? "").EndsWith("PartnerImpl"));
            }
            finally
            {
                semantic.Dispose();
                manager.Dispose();
            }
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* Windows lock */ }
        }
    }
}
