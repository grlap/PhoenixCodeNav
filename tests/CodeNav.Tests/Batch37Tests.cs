using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 37 (v0.9.1) — the field-feedback quartet, all additive response shape:
/// t2b — a deadline dying during cluster LOAD reports 'cluster_cold_load' (retry advice
///       inline) instead of masquerading as semantic_timeout, and references/implementations
///       timing splits {clusterLoadMs, queryMs} (field: "first implementations call after a
///       rebuild times out heuristic, the second is instant exact" — the label blamed a
///       timeout when the truth was warm-up);
/// eja — dependency_path(X, X) carries sameProject:true (the trivially-yes shape was real but
///       ambiguous: callers had to compare paths[0] == fromProject);
/// ctx — impact.transitiveNote makes the no-per-kind-transitive-split DESIGN visible whenever
///       assembly wiring is present (the bare number next to the new split read as an oversight);
/// 49k — related_tests groups grade the strongest usage SHAPE among sampled mention lines
///       (callSite > typeUsage > nameMention) and samples carry the real line + text
///       (previously line 1 with empty text).
/// </summary>
public class Batch37Tests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void DependencyPathFlagsTheSameProjectShape()
    {
        string root = Directory.CreateTempSubdirectory("codenav-37-same").FullName;
        try
        {
            WriteQuartetWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // Case-insensitive on purpose — the graph's own comparer.
            var same = Parse(tools.DependencyPath("lib", "Lib"));
            Assert.True(same.GetProperty("found").GetBoolean());
            Assert.True(same.GetProperty("sameProject").GetBoolean());

            // The flag is SILENT on every normal query (house style: nothing to say).
            var normal = Parse(tools.DependencyPath("AppBin", "Lib"));
            Assert.True(normal.GetProperty("found").GetBoolean());
            Assert.False(normal.TryGetProperty("sameProject", out _), "sameProject must be omitted on X != Y");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImpactExplainsTheBareTransitiveNumberOnlyWhenAssemblyWiringIsPresent()
    {
        string root = Directory.CreateTempSubdirectory("codenav-37-note").FullName;
        try
        {
            WriteQuartetWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // Zeta's owner (Lib) has a HintPath consumer — the note states the design inline.
            var mixed = Parse(tools.Impact("Zeta"));
            Assert.Contains("single count by design",
                mixed.GetProperty("transitiveNote").GetString());

            // Omega's owner (Pure) is consumed via ProjectReference only — an all-project
            // graph has nothing to explain; the note stays silent.
            var pure = Parse(tools.Impact("Omega"));
            Assert.True(pure.GetProperty("transitiveDependentProjects").GetInt32() > 0,
                "fixture must have real dependents or the silence assert is vacuous");
            Assert.False(pure.TryGetProperty("transitiveNote", out _),
                "transitiveNote must be omitted when no assembly wiring is present");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RelatedTestsGradeMentionShapesAndCarryRealSampleLines()
    {
        string root = Directory.CreateTempSubdirectory("codenav-37-signal").FullName;
        try
        {
            WriteQuartetWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            var related = Parse(tools.RelatedTests("Zeta"));
            var groups = related.GetProperty("testGroups").EnumerateArray()
                .ToDictionary(g => g.GetProperty("project").GetString()!, StringComparer.OrdinalIgnoreCase);

            // Strongest shape per group: new Zeta( / z.Run() -> callSite; List<Zeta> -> typeUsage;
            // a string literal -> nameMention.
            Assert.Equal("callSite", groups["CallSite.Tests"].GetProperty("signal").GetString());
            Assert.Equal("typeUsage", groups["TypeUse.Tests"].GetProperty("signal").GetString());
            Assert.Equal("nameMention", groups["Mention.Tests"].GetProperty("signal").GetString());

            // The naming-convention group never textually mentions the bare token — signal is
            // omitted there (Reason already carries that tier).
            Assert.Equal("naming convention", groups["Naming.Tests"].GetProperty("reason").GetString());
            Assert.False(groups["Naming.Tests"].TryGetProperty("signal", out _));

            // Samples now carry the REAL mention line + text (previously line 1, empty text).
            var sample = groups["CallSite.Tests"].GetProperty("samples").EnumerateArray().First();
            Assert.True(sample.GetProperty("line").GetInt32() > 1);
            Assert.Contains("Zeta", sample.GetProperty("text").GetString());

            // impact / context_pack ride the same grading.
            var impact = Parse(tools.Impact("Zeta"));
            Assert.Contains(impact.GetProperty("relatedTests").GetProperty("groups").EnumerateArray(),
                g => g.TryGetProperty("signal", out var s) && s.GetString() == "callSite");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExpandReasonAddsRetryAdviceToTheColdLoadTokenOnly()
    {
        // The token must stay the string PREFIX (machine-matchable), advice appended.
        string? expanded = NavigationTools.ExpandReason("cluster_cold_load");
        Assert.StartsWith("cluster_cold_load", expanded);
        Assert.Contains("retry", expanded, StringComparison.OrdinalIgnoreCase);
        // Every other reason passes through untouched.
        Assert.Equal("semantic_timeout", NavigationTools.ExpandReason("semantic_timeout"));
        Assert.Null(NavigationTools.ExpandReason(null));
    }

    [Fact]
    public void WarmSemanticCallsReportTheBudgetSplit()
    {
        string root = Directory.CreateTempSubdirectory("codenav-37-split").FullName;
        try
        {
            WriteQuartetWorkspace(root);
            using var m = BuildAndStart(root);
            using var sem = new SemanticService(m);
            if (!sem.FrameworkRefsAvailable) return; // env guard: no reference assemblies
            var tools = new NavigationTools(m, sem);

            var refs = Parse(tools.References(name: "Zeta", timeoutMs: 90000));
            Assert.Equal("exact", refs.GetProperty("meta").GetProperty("confidence").GetString());
            var timing = refs.GetProperty("timing");
            long load = timing.GetProperty("clusterLoadMs").GetInt64();
            long query = timing.GetProperty("queryMs").GetInt64();
            Assert.True(load >= 0 && query >= 0);
            // The split partitions the measured elapsed time (small slack: the MCP stopwatch
            // starts before target resolution and stops after serialization bookkeeping).
            Assert.True(load + query <= timing.GetProperty("elapsedMs").GetInt64() + 250,
                $"split {load}+{query} exceeds elapsed {timing.GetProperty("elapsedMs").GetInt64()}");

            var impls = Parse(tools.Implementations(name: "IZetaLike", timeoutMs: 90000));
            Assert.Equal("exact", impls.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.True(impls.GetProperty("timing").GetProperty("clusterLoadMs").GetInt64() >= 0);
        }
        finally { Cleanup(root); }
    }

    // t2b's headline, pinned DETERMINISTICALLY via the phase seam (a real cold load's duration
    // is machine-dependent: a warm reference-assembly cache made a 21-project load finish
    // inside the minimum 500ms clamp on the dev box, silently skipping the cold branch of a
    // timing-based version of this test). The seam throws the same OperationCanceledException
    // a dying deadline produces, in a CHOSEN phase — so both sides of the ternary are pinned:
    // during load -> cluster_cold_load; after load -> semantic_timeout keeps its honest name.
    [Fact]
    public void DeadlineDuringLoadReportsColdLoadAndAfterLoadStaysTimeout()
    {
        string root = Directory.CreateTempSubdirectory("codenav-37-cold").FullName;
        try
        {
            WriteQuartetWorkspace(root);
            using var m = BuildAndStart(root);
            using var sem = new SemanticService(m);
            if (!sem.FrameworkRefsAvailable) return; // env guard
            var tools = new NavigationTools(m, sem);

            // Deadline dies DURING cluster load -> the cold-load token, with inline retry advice.
            sem.TestOnlyPhaseHook = phase =>
            {
                if (phase == "beforeScanSetLoad") throw new OperationCanceledException("test: deadline died during load");
            };
            var cold = Parse(tools.References(name: "IZetaLike", mode: "semantic", timeoutMs: 5000));
            Assert.Equal("semantic_unavailable", cold.GetProperty("error").GetString());
            string coldReason = cold.GetProperty("partialReason").GetString()!;
            Assert.StartsWith("cluster_cold_load", coldReason);
            Assert.Contains("retry", coldReason, StringComparison.OrdinalIgnoreCase);

            // Deadline dies right AFTER load completes -> a real scan timeout keeps its old name.
            sem.TestOnlyPhaseHook = phase =>
            {
                if (phase == "afterScanSetLoad") throw new OperationCanceledException("test: deadline died during scan");
            };
            var timeout = Parse(tools.References(name: "IZetaLike", mode: "semantic", timeoutMs: 5000));
            Assert.Equal("semantic_timeout", timeout.GetProperty("partialReason").GetString());

            // The advice the cold token gives is TRUE: an unhooked retry returns exact.
            sem.TestOnlyPhaseHook = null;
            var warm = Parse(tools.References(name: "IZetaLike", timeoutMs: 90000));
            Assert.Equal("exact", warm.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- fixture

    /// <summary>Lib declares Zeta (class, with Run) and IZetaLike; AppBin consumes Lib via
    /// HintPath (assembly wiring — arms impact.transitiveNote); Pure/PureConsumer is the
    /// all-ProjectReference control (note must stay silent). Four *.Tests projects (R4 dotted
    /// names) carry one mention SHAPE each for the 49k signal tiers; Naming.Tests holds only a
    /// ZetaTests class (the bare token never appears — signal omitted).</summary>
    private static void WriteQuartetWorkspace(string root)
    {
        static void WriteProject(string root, string name, string body, params (string File, string Source)[] files)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), body);
            foreach (var (file, source) in files) File.WriteAllText(Path.Combine(dir, file), source);
        }
        const string plain =
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """;

        WriteProject(root, "Lib", plain,
            ("Zeta.cs",
                """
                namespace LibNs
                {
                    public interface IZetaLike { void Run(); }
                    public class Zeta : IZetaLike { public void Run() { } }
                }
                """));

        WriteProject(root, "AppBin",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Reference Include="Lib"><HintPath>../Common/Lib.dll</HintPath></Reference>
              </ItemGroup>
            </Project>
            """,
            ("UseBin.cs", "namespace AppBin { public class UseBin { public LibNs.Zeta? Z; } }"));

        WriteProject(root, "Pure", plain,
            ("Omega.cs", "namespace PureNs { public class Omega { } }"));
        WriteProject(root, "PureConsumer",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Pure/Pure.csproj" />
              </ItemGroup>
            </Project>
            """,
            ("UsePure.cs", "namespace PureConsumer { public class UsePure { public PureNs.Omega? O; } }"));

        // 49k signal fixtures — dotted .Tests names classify as test projects (R4 fallback).
        // They consume the REAL LibNs.Zeta via ProjectReference: a local stand-in class would
        // add competing Zeta declarations and steal impact's primary-declaration pick from Lib.
        const string refsLib =
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """;
        WriteProject(root, "CallSite.Tests", refsLib,
            ("ZetaCallScenario.cs",
                """
                namespace CallSiteTests
                {
                    public class ZetaCallScenario
                    {
                        public void Exercise()
                        {
                            var z = new LibNs.Zeta();
                            z.Run();
                        }
                    }
                }
                """));
        WriteProject(root, "TypeUse.Tests", refsLib,
            ("ZetaTypeScenario.cs",
                """
                using LibNs;
                namespace TypeUseTests
                {
                    public class ZetaTypeScenario
                    {
                        private readonly System.Collections.Generic.List<Zeta> _items = new();
                        public int Count => _items.Count;
                    }
                }
                """));
        WriteProject(root, "Mention.Tests", plain,
            ("ZetaMentionScenario.cs",
                """
                namespace MentionTests
                {
                    public class ZetaMentionScenario
                    {
                        public const string Legacy = "documented against Zeta only";
                    }
                }
                """));
        WriteProject(root, "Naming.Tests", plain,
            ("NamedSuite.cs",
                """
                namespace NamingTests
                {
                    public class ZetaTests
                    {
                        public void Placeholder() { }
                    }
                }
                """));
    }

    private static IndexManager BuildAndStart(string root)
    {
        string dbPath = IndexBuilder.DefaultDbPath(root);
        IndexBuilder.Build(root, dbPath);
        var m = new IndexManager(root, dbPath);
        m.Start();
        Assert.True(WaitUntil(() => m.IsQueryable, 20000));
        return m;
    }

    private static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return cond();
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }
}
