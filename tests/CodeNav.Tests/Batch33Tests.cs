using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Batch 33 (v0.8.0):
///  - hu7: per-accessor accessibility ({ get; private set; } — the private setter was invisible;
///    field twice-asked). Emitted only when an accessor differs from the member's own.
///  - tky: refresh_index force='full' — the in-band rebuild-from-scratch hatch (field was parked
///    at state 'failed' with no remedy but shell rm -rf).
///  - kbn: textual candidates with NO graph path to the declarer are reported as
///    outOfGraphCandidates instead of vanishing silently.
/// </summary>
public class Batch33Tests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ------------------------------------------------------------------ hu7

    [Fact]
    public void AccessorSplitsSurfaceOnlyWhenTheyDiffer()
    {
        string root = Directory.CreateTempSubdirectory("codenav-hu7").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(proj, "Props.cs"),
                """
                namespace H
                {
                    public class Props
                    {
                        public int Mixed { get; private set; }
                        public int Uniform { get; set; }
                        protected int Inverted { private get; set; }
                        public int InitUniform { get; init; }
                        public int Expr => 1;
                    }
                }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using (var q = new IndexQueries(dbPath))
            {
                var rows = q.Outline("P/Props.cs");
                string? Acc(string name) => rows.Single(r => r.Name == name).Accessors;
                Assert.Equal("get=public;set=private", Acc("Mixed"));
                Assert.Null(Acc("Uniform"));       // both match the member — silent
                Assert.Equal("get=private;set=protected", Acc("Inverted"));
                Assert.Null(Acc("InitUniform"));   // init matches — silent
                Assert.Null(Acc("Expr"));          // expression-bodied — no accessor list
            }

            // Tool level: structured object, omitted when uniform.
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 15000));
            var tools = new NavigationTools(m, new SemanticService(m));
            var hits = Parse(tools.SearchSymbol("Mixed", match: "exact")).GetProperty("symbols");
            var mixed = hits[0];
            Assert.Equal("public", mixed.GetProperty("accessors").GetProperty("get").GetString());
            Assert.Equal("private", mixed.GetProperty("accessors").GetProperty("set").GetString());
            var uniform = Parse(tools.SearchSymbol("Uniform", match: "exact")).GetProperty("symbols")[0];
            Assert.False(uniform.TryGetProperty("accessors", out _), "uniform accessors must be omitted");
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ tky

    [Fact]
    public void ForceFullRebuildsFromScratchAndRecoversFromFailed()
    {
        string root = Directory.CreateTempSubdirectory("codenav-tky").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(proj, "A.cs"), "namespace P { public class Alpha { } }");

            // The FIELD shape: a blocked db path fails the startup build (state 'failed'), the
            // blocker clears (AV lock released), and the ONLY in-band remedy is force:'full'.
            string blockerDir = Path.Combine(root, ".codenav");
            Directory.CreateDirectory(Path.GetDirectoryName(blockerDir)!);
            File.WriteAllText(blockerDir, "not a directory"); // .codenav as a FILE blocks db creation
            string dbPath = IndexBuilder.DefaultDbPath(root);

            using var m = new IndexManager(root, dbPath);
            var tools = new NavigationTools(m, new SemanticService(m));
            m.Start();
            Assert.True(WaitUntil(() => m.State == "failed", 15000), $"expected failed, got {m.State}");

            File.Delete(blockerDir); // the transient blocker clears

            var bad = Parse(tools.RefreshIndex(force: "everything"));
            Assert.Equal("bad_request", bad.GetProperty("error").GetString());

            var queued = Parse(tools.RefreshIndex(force: "full"));
            Assert.True(queued.GetProperty("queued").GetBoolean());
            Assert.Equal("full rebuild from scratch", queued.GetProperty("scope").GetString());

            Assert.True(WaitUntil(() => m.IsQueryable, 30000), "force:'full' did not recover from failed");
            using (var q = m.OpenQueries()) // tight scope: real tool calls release the db in ms
            {
                Assert.NotEmpty(q.SearchSymbols("Alpha", "exact", null, 5));
            }
            // Review F2: recovery is a designed failed->ready transition — the pre-recovery
            // failure must not keep reporting on a healthy index.
            Assert.Null(m.Health().Error);
            // Review F1: recovery from FAILED must attach the file watcher (startup died before
            // it ever could) — otherwise the recovered index silently goes stale with
            // pendingChanges reading 0. A file added NOW must be indexed with no manual refresh.
            File.WriteAllText(Path.Combine(proj, "B.cs"), "namespace P { public class BetaAfterRecovery { } }");
            Assert.True(WaitUntil(() =>
            {
                using var qw = m.OpenQueries();
                return qw.SearchSymbols("BetaAfterRecovery", "exact", null, 2).Count > 0;
            }, 20000), "watcher absent after recovery-from-failed — the index is silently stale");
            string? v1 = m.Health().IndexVersion;
            Assert.NotNull(v1);

            // A second full rebuild on a HEALTHY index mints a fresh index_version — the
            // observable proof the db was rebuilt, not delta-refreshed.
            tools.RefreshIndex(force: "full");
            Assert.True(WaitUntil(() => m.IsQueryable && m.Health().IndexVersion != v1, 30000),
                "second force:'full' did not mint a fresh index (index_version unchanged)");
        }
        finally { Cleanup(root); }
    }

    // ------------------------------------------------------------------ kbn

    [Fact]
    public void TextualCandidatesOutsideTheGraphAreReported()
    {
        string root = Directory.CreateTempSubdirectory("codenav-kbn").FullName;
        try
        {
            string api = Path.Combine(root, "ApiD");
            Directory.CreateDirectory(api);
            File.WriteAllText(Path.Combine(api, "ApiD.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(api, "IContractD.cs"),
                "namespace D { public interface IContractD { void Go(); } }");

            string impl = Path.Combine(root, "ImplD");
            Directory.CreateDirectory(impl);
            File.WriteAllText(Path.Combine(impl, "ImplD.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../ApiD/ApiD.csproj" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(impl, "HandlerD.cs"),
                "namespace I { public class HandlerD : D.IContractD { public void Go() { } } }");

            // The plugin shape: mentions the name TEXTUALLY (a config string) but has NO
            // reference and no graph path to the declarer — previously vanished silently.
            string plugin = Path.Combine(root, "PluginD");
            Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin, "PluginD.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(plugin, "Host.cs"),
                "namespace PL { public class Host { public string Wire = \"D.IContractD\"; } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            var semantic = new SemanticService(m);
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 15000));
                if (!semantic.FrameworkRefsAvailable) return;
                var tools = new NavigationTools(m, semantic);

                var refs = SemanticRetry.ParseExactWithRetry( // n7ly sweep: retries transient degrades
                    () => tools.References(name: "IContractD", timeoutMs: 90000));
                Assert.Equal("exact", refs.GetProperty("meta").GetProperty("confidence").GetString());
                var outOfGraph = refs.GetProperty("outOfGraphCandidates").EnumerateArray()
                    .Select(e => e.GetString()).ToList();
                Assert.Contains("PluginD", outOfGraph);
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- helpers

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
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }
}
