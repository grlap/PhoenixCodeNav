using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Batch 57 (jj1q): closure-verified implementer discovery. The global SymbolFinder walk cost
/// 43.9s WARM across 90 compilations for 8 results (field two-trace); the replacement walks
/// the TRANSITIVE base-list closure in the index and semantically verifies only the
/// candidates. This pins the property the fix must never lose — Greg: "transitive, because
/// chain might be long A→B→C→D":
/// (1) deep class chains: IDeepContract ← BaseHandler ← MidHandler ← LeafHandler across FOUR
///     projects — the leaf never names the interface, only the closure walk reaches it;
/// (2) derived-interface hops: ViaInterface : IDeepDerived where IDeepDerived : IDeepContract
///     — an interface hop the old kind-filtered candidate query would have dropped;
/// (3) exact confidence preserved end-to-end through the MCP surface.
/// </summary>
public class Batch57TransitiveImplementationsTests
{
    [Fact]
    public void DeepChainAndInterfaceHopImplementersAreAllFound()
    {
        string root = Directory.CreateTempSubdirectory("codenav-57-deep").FullName;
        try
        {
            void Proj(string name, string? reference, string source)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                string refItem = reference is null
                    ? ""
                    : $"""<ItemGroup><ProjectReference Include="../{reference}/{reference}.csproj" /></ItemGroup>""";
                File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
                    $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                      {refItem}
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(dir, $"{name}Types.cs"), source);
            }

            Proj("P1", null,
                "namespace Deep { public interface IDeepContract { void Run(); } }");
            Proj("P2", "P1",
                """
                namespace Deep
                {
                    public abstract class BaseHandler : IDeepContract { public abstract void Run(); }
                    public interface IDeepDerived : IDeepContract { }
                }
                """);
            Proj("P3", "P2",
                """
                namespace Deep
                {
                    public class MidHandler : BaseHandler { public override void Run() { } }
                    public class ViaInterface : IDeepDerived { public void Run() { } }
                }
                """);
            Proj("P4", "P3",
                "namespace Deep { public class LeafHandler : MidHandler { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var probe = new IndexQueries(dbPath))
            {
                // Layer isolation: the closure-verify step can only see LOADED projects, and
                // the scan set rides the dependents closure — if the graph edges are broken
                // the failure is here, not in jj1q's walk.
                var dependents = probe.DependentClosure("P1");
                Assert.Contains("P2", dependents);
                Assert.Contains("P3", dependents);
                Assert.Contains("P4", dependents);
                var closureProbe = probe.TransitiveImplementationClosure(
                    "IDeepContract", 0, out bool cappedProbe);
                Assert.False(cappedProbe);
                Assert.True(closureProbe.Count >= 5, // BaseHandler, IDeepDerived, MidHandler, ViaInterface, LeafHandler
                    $"closure walk incomplete: [{string.Join(", ", closureProbe.Select(h => h.Name))}]");
            }
            using var m = new IndexManager(root, dbPath);
            var semantic = new SemanticService(m);
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 30_000));
                if (!semantic.FrameworkRefsAvailable) return; // env guard, same as siblings

                var tools = new NavigationTools(m, semantic);
                var impls = SemanticRetry.ParseExactWithRetry(
                    () => tools.Implementations(name: "IDeepContract", timeoutMs: 60000));
                var names = impls.GetProperty("implementations").EnumerateArray()
                    .Select(i => i.GetProperty("symbol").GetProperty("display").GetString() ?? "")
                    .ToList();

                // The whole bead: every link of the chain, at any depth, through both hop
                // kinds — the leaf (P4) never mentions IDeepContract anywhere in its source.
                Assert.Contains(names, n => n.Contains("BaseHandler"));
                Assert.True(names.Any(n => n.Contains("MidHandler")),
                    $"MidHandler missing (class-chain hop): [{string.Join("; ", names)}]");
                Assert.True(names.Any(n => n.Contains("LeafHandler")),
                    $"LeafHandler missing (depth-4, never names the target): [{string.Join("; ", names)}]");
                Assert.Contains(names, n => n.Contains("ViaInterface"));

                // epuc.3: the planning wall must be attributable without exposing the queried
                // symbol or workspace. The field reproducer saw closureMs ~= 5.5s but could not
                // distinguish SQLite reads from exact-identity filtering/frontier bookkeeping.
                string telemetryLine = m.Telemetry.Snapshot().Last(line =>
                    line.Contains("\"tool\":\"implementations\"", StringComparison.Ordinal) &&
                    line.Contains("\"result\":\"exact\"", StringComparison.Ordinal));
                using JsonDocument telemetry = JsonDocument.Parse(telemetryLine);
                Assert.Equal("writer",
                    telemetry.RootElement.GetProperty("accessMode").GetString());
                JsonElement planning = telemetry.RootElement.GetProperty("planning");
                JsonElement closure = planning.GetProperty("implementationClosure");
                Assert.True(closure.GetProperty("dbQueries").GetInt32() > 0);
                Assert.True(closure.GetProperty("rowsReturned").GetInt32() >=
                            closure.GetProperty("matches").GetInt32());
                Assert.True(closure.GetProperty("frontierExpansions").GetInt32() > 0);
                Assert.True(closure.GetProperty("dbQueryAndMapMs").GetDouble() >= 0);
                Assert.Equal(0, closure.GetProperty("managedFilterMs").GetDouble());
                Assert.Equal("closureOwners",
                    planning.GetProperty("seedDiscovery").GetProperty("mode").GetString());
                JsonElement scanSet = planning.GetProperty("scanSet");
                Assert.True(scanSet.GetProperty("candidateProjects").GetInt32() > 0);
                Assert.True(scanSet.GetProperty("scanProjects").GetInt32() >= 4);
                Assert.DoesNotContain("IDeepContract", telemetryLine, StringComparison.Ordinal);
                Assert.DoesNotContain(root, telemetryLine, StringComparison.Ordinal);
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public void CappedSemanticFallbackLoadsEveryTransitiveDependent()
    {
        string root = Directory.CreateTempSubdirectory("codenav-57-capped-fallback").FullName;
        try
        {
            void Proj(string name, string? reference, string source)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                string refItem = reference is null
                    ? ""
                    : $"""<ItemGroup><ProjectReference Include="../{reference}/{reference}.csproj" /></ItemGroup>""";
                File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
                    $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                      {refItem}
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(dir, $"{name}.cs"), source);
            }

            Proj("P1", null,
                "namespace Capped { public interface IRoot { } }");
            Proj("P2", "P1",
                "namespace Capped { public abstract class Intermediate : IRoot { } }");
            Proj("P3", "P2",
                "namespace Capped { public sealed class Leaf : Intermediate { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                List<SymbolHit> cappedClosure = queries.TransitiveImplementationClosure(
                    "IRoot", 0, out bool capped, maxTypes: 1);
                Assert.True(capped);
                Assert.Single(cappedClosure);
                Assert.DoesNotContain("P3", queries.ImplementationCandidateProjects("IRoot"));
                Assert.Contains("P3", queries.DependentClosure("P1"));
            }
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000));
            using (var probe = new SemanticService(manager))
            {
                if (!probe.FrameworkRefsAvailable) return;
            }

            JsonElement implementations = Invoke(tools => tools.Implementations(
                name: "IRoot", maxProjects: 0, timeoutMs: 120_000));
            Assert.False(implementations.TryGetProperty("partial", out JsonElement implementationsPartial) &&
                         implementationsPartial.GetBoolean());
            Assert.Contains(implementations.GetProperty("implementations").EnumerateArray(), item =>
                item.GetProperty("symbol").GetProperty("display").GetString()!
                    .Contains("Leaf", StringComparison.Ordinal));

            JsonElement hierarchy = Invoke(tools => tools.TypeHierarchy(
                name: "IRoot", maxProjects: 0, timeoutMs: 120_000));
            Assert.False(hierarchy.TryGetProperty("partial", out JsonElement hierarchyPartial) &&
                         hierarchyPartial.GetBoolean());
            Assert.Contains(hierarchy.GetProperty("derivedOrImplementing").EnumerateArray(), item =>
                item.GetProperty("display").GetString()!.Contains("Leaf", StringComparison.Ordinal));

            // A deliberate project budget remains honest: the leaf is skipped, the answer is
            // partial, and the omitted transitive dependent is named in coverage.
            JsonElement bounded = Invoke(tools => tools.Implementations(
                name: "IRoot", maxProjects: 1, timeoutMs: 120_000));
            Assert.True(bounded.GetProperty("partial").GetBoolean());
            Assert.StartsWith("candidate_cluster_bounded",
                bounded.GetProperty("partialReason").GetString(), StringComparison.Ordinal);
            Assert.Contains("P3", bounded.GetProperty("skippedCandidateProjects")
                .EnumerateArray().Select(item => item.GetString()));

            string telemetryLine = manager.Telemetry.Snapshot().Last(line =>
                line.Contains("\"tool\":\"implementations\"", StringComparison.Ordinal));
            using JsonDocument telemetry = JsonDocument.Parse(telemetryLine);
            JsonElement planning = telemetry.RootElement.GetProperty("planning");
            Assert.True(planning.GetProperty("implementationClosure")
                .GetProperty("capped").GetBoolean());
            Assert.Equal("dependentClosure",
                planning.GetProperty("seedDiscovery").GetProperty("mode").GetString());

            JsonElement Invoke(Func<NavigationTools, string> operation)
            {
                using var semantic = new SemanticService(manager)
                {
                    TestOnlyImplementationClosureMaxTypes = 1,
                };
                var tools = new NavigationTools(manager, semantic);
                return SemanticRetry.ParseExactWithRetry(() => operation(tools));
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ClosureQueryWalksChainsAndStopsAtTheCap()
    {
        string root = Directory.CreateTempSubdirectory("codenav-57-closure").FullName;
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
            File.WriteAllText(Path.Combine(proj, "Chain.cs"),
                """
                namespace C
                {
                    public interface IRoot { }
                    public class A : IRoot { }
                    public class B : A { }
                    public class C3 : B { }
                    public class D : C3 { }
                    public class Unrelated { }
                }
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            // A deterministic per-command delay makes the decisive attribution contract red if
            // dbQueryAndMapMs is accidentally measured outside Query execution or left at zero.
            using var q = new IndexQueries(dbPath, pinReadSnapshot: false,
                beforeQueryForTest: sql =>
                {
                    if (sql.Contains("FROM type_base_edges e", StringComparison.Ordinal) &&
                        sql.Contains("ORDER BY e.derived_symbol_id", StringComparison.Ordinal))
                        Thread.Sleep(5);
                });

            var statsBox = new ImplementationClosureStatsBox();
            var closure = q.TransitiveImplementationClosure("IRoot", 0, out bool capped,
                statsBox: statsBox);
            Assert.False(capped);
            var names = closure.Select(h => h.Name).ToList();
            Assert.Equal(new[] { "A", "B", "C3", "D" }, names.OrderBy(n => n, StringComparer.Ordinal));

            ImplementationClosureStats stats = Assert.IsType<ImplementationClosureStats>(statsBox.Stats);
            Assert.Equal(5, stats.DbQueries); // IRoot, A, B, C3, then leaf D
            Assert.Equal(4, stats.RowsReturned);
            Assert.Equal(5, stats.FrontierExpansions);
            Assert.Equal(4, stats.Matches);
            Assert.False(stats.Capped);
            Assert.Equal(0, stats.ManagedFilterMs);
            Assert.True(stats.DbQueryAndMapMs >= stats.DbQueries * 4.0,
                $"injected query delay was not attributed: {stats.DbQueryAndMapMs}ms/{stats.DbQueries} queries");
            Assert.True(stats.TotalMs >= stats.DbQueryAndMapMs + stats.ManagedFilterMs,
                $"total {stats.TotalMs} did not contain db {stats.DbQueryAndMapMs} + filter {stats.ManagedFilterMs}");

            // Cap honesty: a walk that cannot finish reports capped instead of a silent cut.
            var cappedStatsBox = new ImplementationClosureStatsBox();
            var partial = q.TransitiveImplementationClosure("IRoot", 0, out bool cappedSmall,
                maxTypes: 2, statsBox: cappedStatsBox);
            Assert.True(cappedSmall);
            Assert.Equal(2, partial.Count);
            Assert.True(cappedStatsBox.Stats!.Capped);
            Assert.Equal(2, cappedStatsBox.Stats.Matches);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
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
}
