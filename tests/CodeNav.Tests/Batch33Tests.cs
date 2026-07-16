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

                var refs = SemanticRetry.ParseWithRetry(
                    () => tools.References(name: "IContractD", timeoutMs: 90000),
                    json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                            (reason.GetString() ?? "").Contains(
                                "out_of_graph_candidates", StringComparison.Ordinal),
                    "out-of-graph candidate honesty");
                Assert.Equal("indexed", refs.GetProperty("meta")
                    .GetProperty("confidence").GetString());
                Assert.True(refs.GetProperty("partial").GetBoolean());
                Assert.True(refs.GetProperty("totalIsLowerBound").GetBoolean());
                var outOfGraph = refs.GetProperty("outOfGraphCandidates").EnumerateArray()
                    .Select(e => e.GetString()).ToList();
                Assert.Contains("PluginD", outOfGraph);
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void OutOfGraphCandidatesAreFilteredBeforeThePublicSampleIsCapped()
    {
        string root = Directory.CreateTempSubdirectory("codenav-kbn-filter-before-cap").FullName;
        try
        {
            string api = Path.Combine(root, "Api");
            Directory.CreateDirectory(api);
            File.WriteAllText(Path.Combine(api, "Api.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(api, "IContract.cs"),
                "namespace Contracts { public interface IContract { void Go(); } }");

            string impl = Path.Combine(root, "Impl");
            Directory.CreateDirectory(impl);
            File.WriteAllText(Path.Combine(impl, "Impl.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"../Api/Api.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(impl, "Handler.cs"),
                "namespace Impl { public sealed class Handler : Contracts.IContract { public void Go() { } } }");

            var warmReferences = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                string name = $"Warm{i:00}";
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(dir, "One.cs"),
                    $"namespace {name} {{ public class One {{ public string Value = \"Contracts.IContract\"; }} }}");
                File.WriteAllText(Path.Combine(dir, "Two.cs"),
                    $"namespace {name} {{ public class Two {{ public string Value = \"Contracts.IContract\"; }} }}");
                warmReferences.Add($"<ProjectReference Include=\"../{name}/{name}.csproj\" />");
            }

            string hub = Path.Combine(root, "WarmHub");
            Directory.CreateDirectory(hub);
            File.WriteAllText(Path.Combine(hub, "WarmHub.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
                $"<ItemGroup>{string.Join("", warmReferences)}</ItemGroup></Project>");
            File.WriteAllText(Path.Combine(hub, "Hub.cs"),
                "namespace WarmHub { public sealed class Hub { } }");

            for (int i = 0; i < 21; i++)
            {
                string name = $"Tail{i:00}";
                string tail = Path.Combine(root, name);
                Directory.CreateDirectory(tail);
                File.WriteAllText(Path.Combine(tail, $"{name}.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(tail, "Tail.cs"),
                    $"namespace {name} {{ public class Tail {{ public string Value = \"Contracts.IContract\"; }} }}");
            }

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);

            SemanticRetry.ParseExactWithRetry(() =>
                tools.Definition(path: "WarmHub/Hub.cs", line: 1, timeoutMs: 90_000));
            JsonElement refs = SemanticRetry.ParseWithRetry(
                () => tools.References(name: "IContract", timeoutMs: 90_000),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        (reason.GetString() ?? "").Contains("out_of_graph_candidates",
                            StringComparison.Ordinal),
                "post-filter out-of-graph candidate honesty");

            Assert.True(refs.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Equal(21, refs.GetProperty("outOfGraphCandidateCount").GetInt32());
            Assert.True(refs.GetProperty("outOfGraphCandidatesTruncated").GetBoolean());
            var sample = refs.GetProperty("outOfGraphCandidates").EnumerateArray()
                .Select(candidate => candidate.GetString()).ToList();
            Assert.Equal(20, sample.Count);
            Assert.Contains("Tail00", sample);
            Assert.DoesNotContain("Tail20", sample);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void OutOfGraphCandidateDiagnosticsRespectTheHardByteBudget()
    {
        string root = Directory.CreateTempSubdirectory("codenav-kbn-byte-budget").FullName;
        try
        {
            string api = Path.Combine(root, "Api");
            Directory.CreateDirectory(api);
            File.WriteAllText(Path.Combine(api, "Api.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(api, "IContract.cs"),
                "namespace Contracts { public interface IContract { void Go(); } }");

            for (int i = 0; i < 20; i++)
            {
                string directoryName = $"P{i:00}";
                string directory = Path.Combine(root, directoryName);
                Directory.CreateDirectory(directory);
                string assemblyName = $"Plugin{i:00}_" + new string('界', 2_000);
                File.WriteAllText(Path.Combine(directory, $"{directoryName}.csproj"),
                    $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(directory, "Host.cs"),
                    $"namespace P{i:00} {{ public sealed class Host {{ public string Wire = \"Contracts.IContract\"; }} }}");
            }

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);
            string response = "";
            JsonElement references = SemanticRetry.ParseWithRetry(
                () => response = tools.References(name: "IContract", timeoutMs: 90_000),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        (reason.GetString() ?? "").Contains("out_of_graph_candidates",
                            StringComparison.Ordinal),
                "byte-bounded out-of-graph diagnostics");

            Assert.True(Json.Utf8Bytes(response) <= Json.HardBudgetBytes,
                $"references response used {Json.Utf8Bytes(response)} bytes");
            Assert.Equal(20, references.GetProperty("outOfGraphCandidateCount").GetInt32());
            Assert.Equal(20, references.GetProperty("outOfGraphCandidatesReturned").GetInt32());
            Assert.True(references.GetProperty("outOfGraphCandidateItemsTruncated").GetBoolean());
            Assert.All(references.GetProperty("outOfGraphCandidates").EnumerateArray(), item =>
                Assert.True(Json.Utf8Bytes(Json.Serialize(item.GetString()!)) <= 512));
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
