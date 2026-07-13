using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Batch 38 (v0.9.2):
/// 5u5 — impact accepts symbolId (idx:N~fp) and PINS the primary declaration to the handle's
///       row (the last tool of the P1: handles were emitted everywhere but impact could not
///       take one back, so callers redid name+container disambiguation on every follow-up);
/// dve — (a) type_hierarchy's semantic_unavailable path degrades to base-list candidates
///       (derivedConfidence heuristic, baseTypes/interfaces OMITTED) instead of a bare error,
///       parity with implementations; (b) the implementations fallback keeps the
///       compiler-resolved identity (symbol + symbolConfidence 'exact' beside
///       implementationsConfidence 'heuristic') instead of flattening everything to one label;
/// 46p — DependencyPaths no longer materializes every equal-length partial path (a 17-layer
///       3-wide lattice took 69 SECONDS): reverse-BFS distances + a shortest-DAG walk bound
///       enumeration to O(maxPaths × pathLength), and pair-row duplicate edges no longer
///       emit duplicate identical paths.
/// </summary>
public class Batch38Tests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void DependencyPathsOnTheSixtyNineSecondLatticeCompleteInBoundedTime()
    {
        string root = Directory.CreateTempSubdirectory("codenav-38-lattice").FullName;
        try
        {
            // The EXACT shape that ran 69s pre-fix (Batch 36 budget-test comment): 17 layers,
            // 3-wide middle — 3^15 equal-length partial paths under the old FIFO enumeration.
            static string Name(int layer, int c) => $"Acme.Lattice.Layer{layer:D2}.Component{c}.Module";
            static int Width(int layer) => layer is 0 or 16 ? 1 : 3;
            for (int layer = 0; layer <= 16; layer++)
            {
                for (int c = 0; c < Width(layer); c++)
                {
                    string name = Name(layer, c);
                    string dir = Path.Combine(root, name);
                    Directory.CreateDirectory(dir);
                    string refs = layer == 16
                        ? ""
                        : string.Join("\n", Enumerable.Range(0, Width(layer + 1)).Select(n =>
                            $"    <ProjectReference Include=\"../{Name(layer + 1, n)}/{Name(layer + 1, n)}.csproj\" />"));
                    File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
                        $"""
                        <Project Sdk="Microsoft.NET.Sdk">
                          <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                          <ItemGroup>
                        {refs}
                          </ItemGroup>
                        </Project>
                        """);
                }
            }
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var paths = q.DependencyPaths(Name(0, 0), Name(16, 0), 10);
            sw.Stop();

            // Generous CI margin — the fix runs in milliseconds; the OLD code needed ~69,000ms.
            Assert.True(sw.ElapsedMilliseconds < 15000,
                $"DependencyPaths took {sw.ElapsedMilliseconds}ms — the exponential enumeration is back");
            Assert.Equal(10, paths.Count); // maxPaths cap
            Assert.All(paths, p => Assert.Equal(17, p.Count)); // shortest length only
            Assert.All(paths, p => { Assert.Equal(Name(0, 0), p[0]); Assert.Equal(Name(16, 0), p[^1]); });
            Assert.Equal(10, paths.Select(p => string.Join("|", p)).Distinct(StringComparer.Ordinal).Count());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void PairRowDuplicateEdgesYieldOneDistinctPathNotDuplicates()
    {
        string root = Directory.CreateTempSubdirectory("codenav-38-dup").FullName;
        try
        {
            // MidOld/MidNew share AssemblyName 'Mid' (net-old/net-new pair) and both reference
            // LibM; Top references BOTH csprojs — the name-level adjacency holds duplicate
            // entries at every hop, which the old enumeration emitted as duplicate paths.
            static string Csproj(string? asmName, params string[] refs) =>
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    {(asmName is null ? "" : $"<AssemblyName>{asmName}</AssemblyName>")}
                  </PropertyGroup>
                  <ItemGroup>
                {string.Join("\n", refs.Select(r => $"    <ProjectReference Include=\"../{r}/{r}.csproj\" />"))}
                  </ItemGroup>
                </Project>
                """;
            foreach (var (proj, asm, refs) in new (string, string?, string[])[]
                     {
                         ("LibM", null, Array.Empty<string>()),
                         ("MidOld", "Mid", new[] { "LibM" }),
                         ("MidNew", "Mid", new[] { "LibM" }),
                         ("Top", null, new[] { "MidOld", "MidNew" }),
                     })
            {
                string dir = Path.Combine(root, proj);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{proj}.csproj"), Csproj(asm, refs));
            }
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);

            var paths = q.DependencyPaths("Top", "LibM", 10);
            Assert.Single(paths); // ONE name-level shortest path, not 2–4 identical copies
            Assert.Equal(new[] { "Top", "Mid", "LibM" }, paths[0]);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImpactAcceptsAHandleAndPinsThePrimaryDeclaration()
    {
        string root = Directory.CreateTempSubdirectory("codenav-38-handle").FullName;
        try
        {
            WriteParityWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // Two compiled declarations share the name 'Dual'; name-mode picks the first by
            // path order (AaCore). A handle to the ZzOther row must PIN it — no re-picking.
            var hits = Parse(tools.SearchSymbol("Dual")).GetProperty("symbols").EnumerateArray().ToList();
            var zz = hits.First(h => h.GetProperty("path").GetString()!.StartsWith("ZzOther", StringComparison.OrdinalIgnoreCase));
            string handle = zz.GetProperty("symbolId").GetString()!;

            var byName = Parse(tools.Impact("Dual"));
            Assert.StartsWith("AaCore", byName.GetProperty("declaration").GetProperty("path").GetString());

            var byHandle = Parse(tools.Impact(symbolId: handle));
            Assert.StartsWith("ZzOther", byHandle.GetProperty("declaration").GetProperty("path").GetString());
            Assert.Equal("ZzOther", byHandle.GetProperty("owningProject").GetString());
            // Pinned = single declaration; the "N declarations share this name" risk must not fire.
            Assert.DoesNotContain(byHandle.GetProperty("risks").EnumerateArray(),
                r => r.GetString()!.Contains("share this name"));

            // Wiring for the handle error paths (full matrix lives in the Batch 8 tests).
            var bad = Parse(tools.Impact(symbolId: "idx:999999~00000000"));
            Assert.True(bad.TryGetProperty("error", out _), "an unknown/stale handle must error, not fall back silently");
            var neither = Parse(tools.Impact());
            Assert.Equal("bad_request", neither.GetProperty("error").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void TypeHierarchySemanticFailureDegradesToCandidatesNotABareError()
    {
        string root = Directory.CreateTempSubdirectory("codenav-38-thfall").FullName;
        try
        {
            WriteParityWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // IGhost is declared ONLY in an orphaned file (no project compiles it), so semantic
            // resolution deterministically fails — while the base-list index knows GhostImpl.
            var th = Parse(tools.TypeHierarchy("IGhost"));
            Assert.False(th.TryGetProperty("error", out _), "semantic failure must degrade, not error, when candidates exist");
            Assert.Equal("heuristic", th.GetProperty("derivedConfidence").GetString());
            Assert.Contains(th.GetProperty("derivedOrImplementing").EnumerateArray(),
                d => d.GetProperty("name").GetString() == "GhostImpl");
            Assert.True(th.TryGetProperty("partialReason", out _));
            // Omitted, never empty-claimed — baseTypes/interfaces need the compiler.
            Assert.False(th.TryGetProperty("baseTypes", out _));
            Assert.False(th.TryGetProperty("interfaces", out _));
            Assert.Equal("heuristic", th.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally { Cleanup(root); }
    }

    // Review (reproduced): POSITION mode carries no kind gate — path+line can hand the
    // semantic layer a METHOD, whose 'not_a_type' is a DEFINITIVE compiler refusal, not an
    // availability gap. The degrade must never override it with a base-list sweep of the
    // member's name (the reviewer's repro presented a class as deriving from a method).
    [Fact]
    public void TypeHierarchyNeverOverridesNotATypeWithHeuristicCandidates()
    {
        string root = Directory.CreateTempSubdirectory("codenav-38-notatype").FullName;
        try
        {
            WriteParityWorkspace(root);
            // A METHOD named Widget at a known position + a class hierarchy named Widget.
            string libW = Path.Combine(root, "LibW");
            Directory.CreateDirectory(libW);
            File.WriteAllText(Path.Combine(libW, "LibW.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(libW, "Widgets.cs"),
                "namespace LibW { public class Widget { } public class Panel : Widget { } }");
            File.WriteAllText(Path.Combine(libW, "Svc.cs"),
                """
                namespace LibW
                {
                    public class Svc
                    {
                        public void Widget() { }
                    }
                }
                """);
            using var m = BuildAndStart(root);
            using var sem = new SemanticService(m);
            if (!sem.FrameworkRefsAvailable) return; // env guard: needs the semantic path to answer
            var tools = new NavigationTools(m, sem);

            var th = SemanticRetry.ParseWithRetry( // n7ly: transient cluster degrades also say semantic_unavailable — wait for the DELIBERATE not_a_type verdict
                () => tools.TypeHierarchy(name: "Widget", path: "LibW/Svc.cs", line: 5, timeoutMs: 90000),
                j => j.TryGetProperty("partialReason", out var p) && p.GetString() == "not_a_type",
                "the deliberate not_a_type verdict");
            Assert.Equal("semantic_unavailable", th.GetProperty("error").GetString());
            Assert.Equal("not_a_type", th.GetProperty("partialReason").GetString());
            Assert.False(th.TryGetProperty("derivedOrImplementing", out _),
                "a definitive not_a_type must never degrade into base-list candidates");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImplementationsFallbackKeepsTheExactIdentityWhenTheCompilerResolvedIt()
    {
        string root = Directory.CreateTempSubdirectory("codenav-38-mixed").FullName;
        try
        {
            WriteParityWorkspace(root);
            using var m = BuildAndStart(root);
            using var sem = new SemanticService(m);
            if (!sem.FrameworkRefsAvailable) return; // env guard
            var tools = new NavigationTools(m, sem);

            // The type-twin mismatch: AaCore.IFoo resolves (path-sorted first), ZzOther's Impl
            // implements ZzOther's OWN IFoo — compiler-exact implementers of AaCore.IFoo is
            // zero while the base-list index names Impl. The fallback must keep the exact
            // identity beside the heuristic list.
            var impls = SemanticRetry.ParseWithRetry( // n7ly: transient degrades drop symbolConfidence entirely — wait for the mixed shape
                () => tools.Implementations(name: "IFoo", timeoutMs: 90000),
                j => j.TryGetProperty("symbolConfidence", out var sc) && sc.GetString() == "exact",
                "the mixed shape (symbolConfidence == exact beside the heuristic list)");
            Assert.Equal("exact", impls.GetProperty("symbolConfidence").GetString());
            Assert.Contains("IFoo", impls.GetProperty("symbol").GetProperty("display").GetString());
            Assert.Equal("heuristic", impls.GetProperty("implementationsConfidence").GetString());
            Assert.Contains(impls.GetProperty("implementations").EnumerateArray(),
                i => i.GetProperty("name").GetString() == "Impl");
            Assert.Equal("heuristic", impls.GetProperty("meta").GetProperty("confidence").GetString());

            // Negative: when the symbol itself never resolved (orphan-only IGhost), the mixed
            // fields stay OMITTED — meta's single heuristic label covers everything.
            var ghost = Parse(tools.Implementations(name: "IGhost", timeoutMs: 90000));
            Assert.False(ghost.TryGetProperty("symbolConfidence", out _),
                "unresolved symbol must not claim an exact identity");
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- fixture

    /// <summary>AaCore declares IFoo + Dual; ZzOther declares its OWN IFoo twin, Impl : IFoo,
    /// and another Dual (same-name pinning target). Orphan/ holds IGhost (no project compiles
    /// it); GhostProj compiles GhostImpl : IGhost (base-list index sees the name; semantics
    /// never can). AaCore paths sort before ZzOther so name-mode picks are deterministic.</summary>
    private static void WriteParityWorkspace(string root)
    {
        static void WriteProject(string root, string name, params (string File, string Source)[] files)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            foreach (var (file, source) in files) File.WriteAllText(Path.Combine(dir, file), source);
        }

        WriteProject(root, "AaCore",
            ("IFoo.cs", "namespace AaCore { public interface IFoo { void Go(); } }"),
            ("Dual.cs", "namespace AaCore { public class Dual { } }"));
        WriteProject(root, "ZzOther",
            ("IFoo.cs", "namespace ZzOther { public interface IFoo { void Go(); } }"),
            ("Impl.cs", "namespace ZzOther { public class Impl : IFoo { public void Go() { } } }"),
            ("Dual.cs", "namespace ZzOther { public class Dual { } }"));
        WriteProject(root, "GhostProj",
            ("GhostImpl.cs", "namespace GhostProj { public class GhostImpl : IGhost { } }"));
        string orphanDir = Path.Combine(root, "Orphan");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "IGhost.cs"),
            "namespace GhostNs { public interface IGhost { } }");
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
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }
}
