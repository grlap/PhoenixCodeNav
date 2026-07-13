using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeNav.Tests;

/// <summary>
/// lhg — the field's multi-staged-build trap: ET.Api.Generated is consumed via
/// &lt;Reference Include&gt; + HintPath (phase one builds assemblies to a common folder; later
/// projects reference the DLL, not the project). With no ProjectReference the dependency graph
/// had no edge, so (a) references' dependents-closure candidate discovery pruned the implementer
/// projects (0 refs, coverage 1/1) and (b) even the seeded implementations cluster bound the
/// interface to the DLL's METADATA symbol (or an error type), never matching the queried SOURCE
/// declaration — 8 implementers found syntactically, 0 semantically.
/// Fix under test: AssemblyRefEdges recovers &lt;Reference&gt;-to-in-workspace-project edges into
/// project_refs (schema v5), and SemanticWorkspace substitutes the SOURCE project for the hint
/// dll when that project is loaded in the cluster.
/// </summary>
public class Batch26AssemblyRefEdgeTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void BinaryReferenceToWorkspaceProjectBecomesAGraphEdge()
    {
        string root = Directory.CreateTempSubdirectory("codenav-lhg-edge").FullName;
        try
        {
            WriteFieldShapedWorkspace(root, emitDll: false);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);

            // The recovered edge: SoapA/SoapB -> ET.Api.Generated (matched via AssemblyName,
            // which differs from the csproj FILE name ApiGen.csproj on purpose).
            Assert.Contains(q.ProjectGraph("SoapA", 1, "downstream"),
                e => e.ToProject.Equals("ET.Api.Generated", StringComparison.OrdinalIgnoreCase));
            var dependents = q.DependentClosure("ET.Api.Generated");
            Assert.Contains("SoapA", dependents);
            Assert.Contains("SoapB", dependents);

            // Name collisions resolve to the FIRST row, not to no-edge (field 0.7.2 regression:
            // no-edge silently severed every consumer of a PAIRED declarer). The edge is a
            // name-level fact — the semantic workspace loads and merges same-named rows by name
            // anyway, so RefsDup -> Dup.Common is correct whichever row carries it.
            Assert.Contains(q.ProjectGraph("RefsDup", 1, "downstream"),
                e => e.ToProject.Equals("Dup.Common", StringComparison.OrdinalIgnoreCase));

            // External-location guard (review): a HintPath into packages/ marks the dll EXTERNAL —
            // no edge even though the simple name matches workspace project 'ET.Api.Generated'.
            Assert.DoesNotContain(q.ProjectGraph("RefsNuGet", 1, "downstream"),
                e => e.ToProject.Equals("ET.Api.Generated", StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(root); }
    }

    // Review (deferred pass, F1): a same-name PAIR member referencing its own assembly name
    // passed the row-id self-guard (different rows) and minted a name-level SELF-edge —
    // DependentClosure then returned the project as its own dependent (contract: "target
    // excluded") and impact inflated its blast radius.
    [Fact]
    public void PairMemberReferencingItsOwnAssemblyNameMintsNoSelfEdge()
    {
        string root = Directory.CreateTempSubdirectory("codenav-selfedge").FullName;
        try
        {
            // BOTH twins carry the self-name reference: pick-first resolves to ONE row, so
            // whichever row is first, the OTHER twin's reference passes a row-id-only guard —
            // arming the test regardless of scan order (the single-referencer variant passed
            // vacuously when the referencing row happened to be the map winner).
            foreach (var proj in new[] { "UtilsOld", "UtilsNew" })
            {
                string dir = Path.Combine(root, proj);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{proj}.csproj"),
                    """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net9.0</TargetFramework>
                        <AssemblyName>Utils</AssemblyName>
                      </PropertyGroup>
                      <ItemGroup>
                        <Reference Include="Utils"><HintPath>../Common/Utils.dll</HintPath></Reference>
                      </ItemGroup>
                    </Project>
                    """);
                File.WriteAllText(Path.Combine(dir, "A.cs"), $"namespace {proj} {{ class A {{ }} }}");
            }
            string consumer = Path.Combine(root, "Consumer");
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Utils"><HintPath>../Common/Utils.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(consumer, "B.cs"), "namespace Consumer { class B { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var q = new IndexQueries(dbPath);
            Assert.DoesNotContain(q.ProjectGraph("Utils", 1, "downstream"),
                e => e.ToProject.Equals("Utils", StringComparison.OrdinalIgnoreCase));
            var dependents = q.DependentClosure("Utils");
            Assert.Contains("Consumer", dependents);
            Assert.DoesNotContain("Utils", dependents); // the documented contract: target excluded
        }
        finally { Cleanup(root); }
    }

    // Field 0.7.4 extended: impact("IPartnerFrameworkInterface") reported
    // transitiveDependentProjects: 0 against 91 referencing projects — the ORPHANED declaration
    // sorted first, ProjectsContaining(orphanedPath) was empty, owner stayed null. impact (and
    // context_pack's indexed fallback) now prefer a COMPILED declaration — the 3tz gate parity
    // those indexed paths never got. The fixture's orphan sorts FIRST (AaOldCore/) on purpose.
    [Fact]
    public void ImpactOwnershipSkipsOrphanedDeclarations()
    {
        string root = Directory.CreateTempSubdirectory("codenav-lhg-impact").FullName;
        try
        {
            WriteFieldShapedWorkspace(root, emitDll: false);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 20000));
            var tools = new NavigationTools(m, new SemanticService(m));

            var impact = Parse(tools.Impact("IPartnerContract"));
            // Owner must be the COMPILED declarer, so its dependents (SoapA/SoapB/SoapA.NetNew
            // via recovered assembly-ref edges) are counted — not the orphaned copy's zero.
            Assert.True(impact.GetProperty("transitiveDependentProjects").GetInt32() >= 2,
                "orphaned-first declaration ordering must not zero the dependents count");
        }
        finally { Cleanup(root); }
    }

    // Review (LOW): DeltaRefresher must preserve the recovered edges — a csproj touch rebuilds
    // the whole project graph, and losing them would silently re-break cross-project semantics
    // until the next full rebuild. Pin the parity.
    [Fact]
    public void CsprojRefreshPreservesRecoveredEdges()
    {
        string root = Directory.CreateTempSubdirectory("codenav-lhg-delta").FullName;
        try
        {
            WriteFieldShapedWorkspace(root, emitDll: false);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var store = new IndexStore(dbPath, createNew: false);
            DeltaRefresher.RefreshProjectData(store, root); // the csproj-touch code path

            using var q = new IndexQueries(dbPath);
            Assert.Contains(q.ProjectGraph("SoapA", 1, "downstream"),
                e => e.ToProject.Equals("ET.Api.Generated", StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void CrossProjectImplementationsAndReferencesResolveExactly()
    {
        string root = Directory.CreateTempSubdirectory("codenav-lhg-sem").FullName;
        try
        {
            // The dll REALLY exists in the common folder (multi-stage build output) — the
            // strongest failure shape: pre-fix, implementer compilations bind the interface to
            // the dll's metadata symbol and FindImplementations on the source symbol finds zero.
            WriteFieldShapedWorkspace(root, emitDll: true);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 20000));

                // REFERENCES FIRST, on its own SemanticService: implementations' seed loading
                // would otherwise leave the implementer projects sitting in the shared workspace
                // and references would find them WITHOUT its candidate discovery working
                // (reintroduction-caught: the edges-gutted run passed this way). A fresh
                // workspace forces references to discover SoapA/SoapB via the dependents
                // closure — which only the recovered assembly-ref edges can populate.
                using (var semRefs = new SemanticService(m))
                {
                    if (!semRefs.FrameworkRefsAvailable) return; // env guard: no reference assemblies
                    var toolsRefs = new NavigationTools(m, semRefs);
                    var refs = Parse(toolsRefs.References(name: "IPartnerContract", timeoutMs: 90000));
                    Assert.Equal("exact", refs.GetProperty("meta").GetProperty("confidence").GetString());
                    var projects = refs.GetProperty("groups").EnumerateArray()
                        .Select(g => g.GetProperty("project").GetString())
                        .ToList();
                    Assert.Contains("SoapA", projects); // the pre-fix trap: 0 refs, coverage 1/1
                    Assert.Contains("SoapB", projects);

                    // Rider (asked twice in the field): schema keyed per-response.
                    Assert.Equal(IndexBuilder.SchemaVersion,
                        refs.GetProperty("meta").GetProperty("indexSchema").GetString());
                }

                using (var semImpls = new SemanticService(m))
                {
                    var toolsImpls = new NavigationTools(m, semImpls);
                    var impls = Parse(toolsImpls.Implementations(name: "IPartnerContract", timeoutMs: 90000));
                    Assert.Equal("exact", impls.GetProperty("meta").GetProperty("confidence").GetString());
                    var names = impls.GetProperty("implementations").EnumerateArray()
                        .Select(i => i.GetProperty("symbol").GetProperty("display").GetString()!)
                        .ToList();
                    Assert.Contains(names, n => n.Contains("ImplA"));
                    Assert.Contains(names, n => n.Contains("ImplB"));
                }

                // Field goldens (0.7.0 feedback, synthetic mirror of the monolith canary):
                // baseList usage counts must equal the IMPLEMENTER COUNT exactly — the field P1
                // was totalReferences 13 for 8 implementers (same physical site counted twice).
                using (var semGold = new SemanticService(m))
                {
                    var toolsGold = new NavigationTools(m, semGold);
                    var baseList = Parse(toolsGold.References(name: "IPartnerContract", usageKinds: "baseList", timeoutMs: 90000));
                    Assert.Equal("exact", baseList.GetProperty("meta").GetProperty("confidence").GetString());
                    Assert.Equal(2, baseList.GetProperty("totalReferences").GetInt32()); // one per implementer, never 2x

                    // Review F2: a FILTER-caused zero (usageKinds:'call' on an interface whose
                    // only usages are baseList/typeMention) is an honest zero with full coverage —
                    // the loading-gap note must NOT fire ("raise maxProjects" cannot help; the
                    // namers it would cite are exactly what the filter excluded).
                    var filteredZero = Parse(toolsGold.References(name: "IPartnerContract", usageKinds: "call", timeoutMs: 90000));
                    Assert.Equal("exact", filteredZero.GetProperty("meta").GetProperty("confidence").GetString());
                    Assert.Equal(0, filteredZero.GetProperty("totalReferences").GetInt32());
                    Assert.False(filteredZero.TryGetProperty("note", out _),
                        "filter-caused zero must not carry the loading-gap note");

                    // type_hierarchy, cold on its own service: SEEDED discovery (field: 8 exact
                    // hits with coverage 1/1 were residue from a prior implementations call — a
                    // cold call found nothing), timing present (deadline-honesty symmetry).
                    var th = Parse(toolsGold.TypeHierarchy(name: "IPartnerContract", timeoutMs: 90000));
                    Assert.Equal("exact", th.GetProperty("meta").GetProperty("confidence").GetString());
                    Assert.False(th.TryGetProperty("derivedConfidence", out _), "expected the exact path, not the heuristic fallback");
                    Assert.Equal(2, th.GetProperty("derivedOrImplementing").GetArrayLength());
                    Assert.Equal(90000, th.GetProperty("timing").GetProperty("deadlineMs").GetInt32());
                    Assert.True(th.GetProperty("timing").GetProperty("elapsedMs").GetInt64() >= 0);
                    // coverage now reports the whole-solution scan surface (field: "8 hits, 1/1").
                    var cov = th.GetProperty("coverage");
                    Assert.True(cov.GetProperty("solutionProjects").GetInt32() >= cov.GetProperty("loadedProjects").GetInt32());
                }
            }
            finally { m.Dispose(); }
        }
        finally { Cleanup(root); }
    }

    // Review (HIGH): MUTUAL assembly refs (A <Reference> B.dll, B <Reference> A.dll — legal in
    // multi-stage builds) put both edges in the graph. Within one load pass only backward edges
    // wire, but a fingerprint RELOAD re-adds a project while its dependent is already loaded —
    // pre-guard, that wired the forward edge, AdhocWorkspace ACCEPTED the ProjectReference cycle,
    // and GetCompilationAsync deadlocked FOREVER (every later semantic call in the cluster burned
    // its whole deadline into semantic_timeout, unevictable until process restart).
    [Fact]
    public void MutualAssemblyRefsSurviveAReloadWithoutWiringACycle()
    {
        string root = Directory.CreateTempSubdirectory("codenav-lhg-cycle").FullName;
        try
        {
            foreach (var (proj, other) in new[] { ("CycA", "CycB"), ("CycB", "CycA") })
            {
                string dir = Path.Combine(root, proj);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{proj}.csproj"),
                    $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                      <ItemGroup>
                        <Reference Include="{other}">
                          <HintPath>../Common/{other}.dll</HintPath>
                        </Reference>
                      </ItemGroup>
                    </Project>
                    """);
            }
            File.WriteAllText(Path.Combine(root, "CycA", "AlphaCore.cs"),
                "namespace CycA { public class AlphaCore { public void Ping() { } } }");
            File.WriteAllText(Path.Combine(root, "CycA", "AlphaUser.cs"),
                "namespace CycA { public class AlphaUser { public CycB.BetaCore? B; } }");
            File.WriteAllText(Path.Combine(root, "CycB", "BetaCore.cs"),
                "namespace CycB { public class BetaCore { } }");
            File.WriteAllText(Path.Combine(root, "CycB", "BetaUser.cs"),
                "namespace CycB { public class BetaUser { public CycA.AlphaCore? A; } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            var semantic = new SemanticService(m);
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 20000));
                if (!semantic.FrameworkRefsAvailable) return;
                var tools = new NavigationTools(m, semantic);

                // Pass 1 loads both (one direction wired, the other left as a hole/dll).
                var first = SemanticRetry.ParseExactWithRetry(() => tools.References(name: "AlphaCore", timeoutMs: 30000)); // n7ly
                Assert.Equal("exact", first.GetProperty("meta").GetProperty("confidence").GetString());

                // Mutate CycB so its fingerprint changes, and wait until the INDEX reflects it —
                // the next semantic call then takes the reload path for CycB.
                File.AppendAllText(Path.Combine(root, "CycB", "BetaCore.cs"),
                    "\nnamespace CycB { public class CycMutationToken { } }");
                m.RequestRefresh(new[] { "CycB/BetaCore.cs" });
                Assert.True(WaitUntil(() =>
                {
                    using var q = m.OpenQueries();
                    return q.SearchSymbols("CycMutationToken", "exact", null, 2).Count > 0;
                }, 20000), "index did not pick up the mutation");

                // Pass 2: pre-guard this wired CycB->CycA into an accepted cycle and burned the
                // full deadline into semantic_timeout; post-guard it completes exact and fast.
                var second = SemanticRetry.ParseExactWithRetry(() => tools.References(name: "AlphaCore", timeoutMs: 30000)); // n7ly: a REAL reload deadlock times out on every attempt and still fails
                Assert.Equal("exact", second.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.True(second.GetProperty("timing").GetProperty("elapsedMs").GetInt64() < 25000,
                    "second pass burned the deadline — the reload cycle deadlock is back");
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- fixture

    private static void WriteFieldShapedWorkspace(string root, bool emitDll)
    {
        // Declaring project: csproj FILE name (ApiGen) deliberately differs from the
        // AssemblyName (ET.Api.Generated) — <Reference> matching must key on AssemblyName.
        string apiDir = Path.Combine(root, "ApiGen");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "ApiGen.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>ET.Api.Generated</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(apiDir, "IPartnerContract.cs"),
            "namespace ET.Api { public interface IPartnerContract { void Execute(); } }");
        // The DECLARER is a same-AssemblyName pair too (field 0.7.2 regression: the monolith's
        // net-old/net-new idiom applies to ET.Api.Generated ITSELF). The old ambiguity guard
        // poisoned the paired name to NO edge — silently severing every consumer's edge to the
        // flagship interface; cold references then loaded 1/1 and returned an "exact" zero.
        string apiDir2 = Path.Combine(root, "ApiGen.NetNew");
        Directory.CreateDirectory(apiDir2);
        File.WriteAllText(Path.Combine(apiDir2, "ApiGen.NetNew.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>ET.Api.Generated</AssemblyName>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="../ApiGen/IPartnerContract.cs" />
              </ItemGroup>
            </Project>
            """);

        // The field's twin ingredient: an ORPHANED copy of the interface (indexed, compiled by no
        // project — the file-copied-then-removed-from-csproj shape). Resolution must keep ignoring
        // it, and reference counts must not inflate because of it (field P1: totalReferences 13
        // for 8 implementers on exactly this shape).
        // "Aa" prefix so the ORPHANED copy sorts FIRST among the declarations (the field's real
        // layout: Core/Core/Interfaces sorts before Core/ExactTarget.API.Generated) — arming the
        // compiled-declaration-preference asserts instead of passing by lucky path ordering.
        string oldCore = Path.Combine(root, "AaOldCore");
        Directory.CreateDirectory(oldCore);
        File.WriteAllText(Path.Combine(oldCore, "IPartnerContract.cs"),
            "namespace ET.Api { public interface IPartnerContract { void Execute(); } }");

        foreach (var (proj, impl) in new[] { ("SoapA", "ImplA"), ("SoapB", "ImplB") })
        {
            string dir = Path.Combine(root, proj);
            Directory.CreateDirectory(dir);
            // NO ProjectReference — the multi-staged shape: assembly ref + HintPath only.
            File.WriteAllText(Path.Combine(dir, $"{proj}.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Reference Include="ET.Api.Generated">
                      <HintPath>../Common/ET.Api.Generated.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, $"{impl}.cs"),
                $"namespace {proj} {{ public class {impl} : ET.Api.IPartnerContract {{ public void Execute() {{ }} }} }}");
        }
        // A genuine usage (not just base lists) so references has call-site material in SoapB.
        File.WriteAllText(Path.Combine(root, "SoapB", "UseContract.cs"),
            "namespace SoapB { public class UseContract { public void Run(ET.Api.IPartnerContract c) => c.Execute(); } }");

        // The field P1's REAL ingredient: a same-AssemblyName csproj PAIR (the monolith's
        // net-old/net-new multi-target idiom — two project rows both named SoapA compiling the
        // same file). ProjectFiles(name) joins on the name, so without DISTINCT the shared file
        // surfaced twice, became duplicate adhoc documents, and every reference site in it was
        // counted TWICE within one group ("Interface.cs:11 twice ... every implementer ~2x").
        string soapA2 = Path.Combine(root, "SoapA.NetNew");
        Directory.CreateDirectory(soapA2);
        File.WriteAllText(Path.Combine(soapA2, "SoapA.NetNew.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>SoapA</AssemblyName>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="../SoapA/ImplA.cs" />
                <Reference Include="ET.Api.Generated">
                  <HintPath>../Common/ET.Api.Generated.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);

        // Ambiguity fixture: two projects emitting the same assembly name + one referencing it.
        foreach (var dup in new[] { "DupA", "DupB" })
        {
            string dir = Path.Combine(root, dup);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{dup}.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <AssemblyName>Dup.Common</AssemblyName>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "A.cs"), $"namespace {dup} {{ class A {{ }} }}");
        }
        string refsDup = Path.Combine(root, "RefsDup");
        Directory.CreateDirectory(refsDup);
        File.WriteAllText(Path.Combine(refsDup, "RefsDup.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Reference Include="Dup.Common">
                  <HintPath>../Common/Dup.Common.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(refsDup, "B.cs"), "namespace RefsDup { class B { } }");

        // External-location fixture (review): simple name collides with the workspace project,
        // but the HintPath points into packages/ — a NuGet binary, not the project's output.
        string refsNuGet = Path.Combine(root, "RefsNuGet");
        Directory.CreateDirectory(refsNuGet);
        File.WriteAllText(Path.Combine(refsNuGet, "RefsNuGet.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Reference Include="ET.Api.Generated">
                  <HintPath>../packages/ET.Api.Generated.1.0.0/lib/net45/ET.Api.Generated.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(refsNuGet, "C.cs"), "namespace RefsNuGet { class C { } }");

        if (emitDll)
        {
            // Emit a REAL assembly with the interface into the common folder — the phase-one
            // build output the implementer projects' HintPaths point at. VERSIONED like a real
            // build stamp: with the default 0.0.0.0 the dll's identity EQUALS the adhoc source
            // project's and Roslyn resolves the collision toward the project reference, hiding
            // the trap — the field dll's distinct identity is what makes consumers bind to
            // METADATA instead of source (the 8-implementers-found-0 shape).
            string common = Path.Combine(root, "Common");
            Directory.CreateDirectory(common);
            var comp = CSharpCompilation.Create(
                "ET.Api.Generated",
                new[] { CSharpSyntaxTree.ParseText(
                    """
                    [assembly: System.Reflection.AssemblyVersion("4.2.0.0")]
                    namespace ET.Api { public interface IPartnerContract { void Execute(); } }
                    """) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var emit = comp.Emit(Path.Combine(common, "ET.Api.Generated.dll"));
            Assert.True(emit.Success, string.Join("; ", emit.Diagnostics.Take(3)));
        }
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
