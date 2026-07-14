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
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
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
            using var q = new IndexQueries(dbPath);

            var closure = q.TransitiveImplementationClosure("IRoot", 0, out bool capped);
            Assert.False(capped);
            var names = closure.Select(h => h.Name).ToList();
            Assert.Equal(new[] { "A", "B", "C3", "D" }, names.OrderBy(n => n, StringComparer.Ordinal));

            // Cap honesty: a walk that cannot finish reports capped instead of a silent cut.
            var partial = q.TransitiveImplementationClosure("IRoot", 0, out bool cappedSmall, maxTypes: 2);
            Assert.True(cappedSmall);
            Assert.Equal(2, partial.Count);
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
