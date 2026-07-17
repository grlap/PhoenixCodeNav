using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// bxw (schema v10) — edge PROVENANCE. Since lhg the graph mixes two very different couplings
/// under one edge shape: real &lt;ProjectReference&gt; edges and edges recovered from
/// &lt;Reference&gt;+HintPath (the monolith's multi-staged build). The difference matters for
/// change planning — a HintPath consumer binds to the last-BUILT dll, so refactors ripple only
/// after the staged build re-emits it, and ProjectReference-aware tooling won't follow the edge —
/// yet project_graph HARDCODED kind:'projectReference' for every edge. Under test:
/// (1) project_refs.kind stored 'project'/'assembly' with FIRST-WRITER precedence (a pair wired
/// both ways records 'project'; ProjectReference edges insert before recovery in both builders);
/// (2) kind survives the ProjectGraph BFS reconstruction at depth&gt;1 and the delta graph rebuild;
/// (3) the MCP surface: project_graph.edges.kind, dependency_path.structuredPaths per-hop 'via',
/// impact.directDependentProjects split + HintPath-only risk, context_pack edge flags;
/// (4) rider: reference groups for files NO project compiles are structured
/// {project:null, orphaned:true} instead of the magic "(no project)" display string.
/// </summary>
public class Batch36EdgeProvenanceTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void StoredEdgeKindDistinguishesProjectFromAssemblyWithFirstWriterPrecedence()
    {
        string root = Directory.CreateTempSubdirectory("codenav-bxw-kind").FullName;
        try
        {
            WriteProvenanceWorkspace(root);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using (var q = new IndexQueries(dbPath))
            {
                AssertKinds(q);

                // BFS depth>1 rebuilds result edges from name-keyed adjacency maps — kind must be
                // re-attached from the loaded edge set, not defaulted (the far hop here is the
                // recovered one: Top -ProjectReference-> AppBin -assembly-> Lib).
                var deep = q.ProjectGraph("Top", 2, "downstream");
                var farHop = deep.First(e =>
                    e.FromProject.Equals("AppBin", StringComparison.OrdinalIgnoreCase) &&
                    e.ToProject.Equals("Lib", StringComparison.OrdinalIgnoreCase));
                Assert.Equal("assembly", farHop.Kind);
            }

            // Delta parity: the csproj-touch path drops and rebuilds the WHOLE graph — the
            // ProjectReference-before-recovery insert order (first-writer precedence) must hold
            // there too, or AppBoth would flip to 'assembly' on the first touch.
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                DeltaRefresher.RefreshProjectData(store, root);
            }
            using (var q = new IndexQueries(dbPath))
            {
                AssertKinds(q);
            }
        }
        finally { Cleanup(root); }

        static void AssertKinds(IndexQueries q)
        {
            // Real <ProjectReference> — 'project'.
            var viaProject = q.ProjectGraph("AppProj", 1, "downstream")
                .First(e => e.ToProject.Equals("Lib", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("project", viaProject.Kind);

            // Recovered <Reference>+HintPath — 'assembly'.
            var viaAssembly = q.ProjectGraph("AppBin", 1, "downstream")
                .First(e => e.ToProject.Equals("Lib", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("assembly", viaAssembly.Kind);

            // Wired BOTH ways: first-writer precedence records 'project' — an explicit
            // ProjectReference is the stronger provenance claim and inserts before recovery.
            Assert.All(
                q.ProjectGraph("AppBoth", 1, "downstream")
                    .Where(e => e.ToProject.Equals("Lib", StringComparison.OrdinalIgnoreCase)),
                e => Assert.Equal("project", e.Kind));

            // EdgeKindMap (dependency_path's hop-annotation source) agrees on all three.
            var map = q.EdgeKindMap();
            Assert.Equal("project", map[("appproj", "lib")]);
            Assert.Equal("assembly", map[("appbin", "lib")]);
            Assert.Equal("project", map[("appboth", "lib")]);

            // MIXED kinds across one NAME-level edge: each AppMixed* consumer holds a 'project'
            // row to its own twin, and pick-first recovery gives exactly ONE of them an
            // 'assembly' row to the OTHER twin (whichever row won the name). Vacuousness guard
            // first: the mixed shape must EXIST at row level — 3 edge rows total, exactly one
            // 'assembly' (the first fixture cut silently produced 2/0 and armed nothing).
            var mixedRows = q.ProjectGraph("AppMixedA", 1, "downstream")
                .Concat(q.ProjectGraph("AppMixedZ", 1, "downstream"))
                .Where(e => e.ToProject.Equals("Twin", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Equal(3, mixedRows.Count);
            Assert.Equal(1, mixedRows.Count(e => e.Kind == "assembly"));
            // The name-level answer must be 'project' for BOTH consumers, whatever row order:
            // an explicit ProjectReference is the stronger provenance claim — the mixed one
            // exercises the tiebreak, the other is trivially project.
            Assert.Equal("project", map[("appmixeda", "twin")]);
            Assert.Equal("project", map[("appmixedz", "twin")]);

            // Review F2: depth>1 is NAME-granular (BFS over name-keyed adjacency; depth 1 is
            // row-granular) — the mixed pair must take the name-level policy answer 'project'
            // DETERMINISTICALLY at depth 2, same as EdgeKindMap. Plain last-writer rode the
            // JOIN's planner-dependent row order: same rows, contradictory kinds by depth, and
            // a recovered coupling could vanish at the tool's DEFAULT depth (2).
            var mixedDeep = q.ProjectGraph("Twin", 2, "upstream")
                .Where(e => e.FromProject.StartsWith("AppMixed", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.NotEmpty(mixedDeep);
            Assert.All(mixedDeep, e => Assert.Equal("project", e.Kind));

            // Review F4: the BFS ROOT node carries the CALLER's casing — the kind lookup must
            // fold case like the adjacency maps and the depth-1 SQL (COLLATE NOCASE) already
            // do, or every first-hop kind silently defaults to 'project' on case-variant input.
            var caseVariant = q.ProjectGraph("appbin", 2, "downstream")
                .Where(e => e.ToProject.Equals("Lib", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.NotEmpty(caseVariant);
            Assert.All(caseVariant, e => Assert.Equal("assembly", e.Kind));
        }
    }

    [Fact]
    public void ProjectGraphToolReportsProvenanceInsteadOfHardcodedProjectReference()
    {
        string root = Directory.CreateTempSubdirectory("codenav-bxw-graph").FullName;
        try
        {
            WriteProvenanceWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            var graph = Parse(tools.ProjectGraph("Lib", 1, "upstream"));
            var kindsByFrom = graph.GetProperty("edges").EnumerateArray()
                .ToLookup(e => e.GetProperty("from").GetString()!,
                          e => e.GetProperty("kind").GetString()!,
                          StringComparer.OrdinalIgnoreCase);
            // Pre-bxw every edge said 'projectReference' — the recovered coupling was invisible.
            Assert.Contains("hintPathReference", kindsByFrom["AppBin"]);
            Assert.Contains("projectReference", kindsByFrom["AppProj"]);
            Assert.DoesNotContain("hintPathReference", kindsByFrom["AppBoth"]); // both-ways = project
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DependencyPathAnnotatesHopsAndKeepsArrowStrings()
    {
        string root = Directory.CreateTempSubdirectory("codenav-bxw-path").FullName;
        try
        {
            WriteProvenanceWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            var dep = Parse(tools.DependencyPath("Top", "Lib"));
            Assert.True(dep.GetProperty("found").GetBoolean());
            // Arrow strings stay — structuredPaths is ADDITIVE.
            Assert.Contains(dep.GetProperty("paths").EnumerateArray(),
                p => p.GetString() == "Top -> AppBin -> Lib");

            var hops = dep.GetProperty("structuredPaths").EnumerateArray()
                .Select(path => path.EnumerateArray().ToList())
                .First(path => path.Count == 3 && path[2].GetProperty("project").GetString() == "Lib");
            Assert.Equal("Top", hops[0].GetProperty("project").GetString());
            Assert.False(hops[0].TryGetProperty("via", out _), "the origin hop has no incoming edge");
            Assert.Equal("AppBin", hops[1].GetProperty("project").GetString());
            Assert.Equal("projectReference", hops[1].GetProperty("via").GetString());
            Assert.Equal("hintPathReference", hops[2].GetProperty("via").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImpactSplitsDirectDependentsByProvenanceAndFlagsHintPathOnlyConsumers()
    {
        string root = Directory.CreateTempSubdirectory("codenav-bxw-impact").FullName;
        try
        {
            WriteProvenanceWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            var impact = Parse(tools.Impact("Zeta"));
            var split = impact.GetProperty("directDependentProjects");
            // AppBin's same-AssemblyName twin mints a SECOND (AppBin, Lib) edge row — the split is
            // grouped by NAME, so the pair counts once: AppProj + AppBin + AppBoth = 3.
            Assert.Equal(3, split.GetProperty("total").GetInt32());
            // AppBoth is wired BOTH ways — any real ProjectReference edge carries refactors, so it
            // lands in viaProjectReference, never in the HintPath-only risk bucket.
            Assert.Equal(2, split.GetProperty("viaProjectReference").GetInt32());
            Assert.Equal(1, split.GetProperty("viaHintPathOnly").GetInt32());

            Assert.Contains(impact.GetProperty("risks").EnumerateArray(),
                r => r.GetString()!.Contains("only via <Reference>/HintPath"));

            // MIXED rows within one dependent name (one AppMixed* consumer carries a 'project'
            // row to its own twin AND an 'assembly' row to the other — whichever row pick-first
            // chose): ANY real ProjectReference row means refactors DO carry — both consumers
            // must land in viaProjectReference, and the HintPath-only risk must stay silent.
            // (Counting groups by Any(assembly) instead of All would flip this in every order.)
            var twinImpact = Parse(tools.Impact("TTwinA"));
            var twinSplit = twinImpact.GetProperty("directDependentProjects");
            Assert.Equal(2, twinSplit.GetProperty("total").GetInt32());
            Assert.Equal(2, twinSplit.GetProperty("viaProjectReference").GetInt32());
            Assert.Equal(0, twinSplit.GetProperty("viaHintPathOnly").GetInt32());
            Assert.DoesNotContain(twinImpact.GetProperty("risks").EnumerateArray(),
                r => r.GetString()!.Contains("only via <Reference>/HintPath"));
        }
        finally { Cleanup(root); }
    }

    // Rider: reference groups for files in NO project's compile set were shipped as the magic
    // display string "(no project)" — callers had to know to string-match it. Now structured:
    // project:null + orphaned:true (the same 'orphaned' vocabulary search_symbol already uses),
    // and compiled rows stay clean (orphaned omitted via WhenWritingNull).
    [Fact]
    public void OrphanedReferenceGroupsAreStructuredNotMagicStrings()
    {
        string root = Directory.CreateTempSubdirectory("codenav-bxw-orphan").FullName;
        try
        {
            WriteProvenanceWorkspace(root);
            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));

            // House serialization style: null fields are OMITTED (WhenWritingNull) — so the
            // orphan row ships {orphaned:true, ...} with project ABSENT; orphaned is the
            // positive discriminator, never a magic display string.
            string refsJson = tools.References(name: "Zeta", mode: "indexed");
            Assert.DoesNotContain("(no project)", refsJson, StringComparison.Ordinal);
            var groups = Parse(refsJson).GetProperty("groups").EnumerateArray().ToList();
            var orphanGroup = groups.First(g => !g.TryGetProperty("project", out _));
            Assert.True(orphanGroup.GetProperty("orphaned").GetBoolean());
            var compiledGroup = groups.First(g => g.TryGetProperty("project", out var p) && p.ValueKind == JsonValueKind.String);
            Assert.False(compiledGroup.TryGetProperty("orphaned", out _), "compiled rows stay clean");

            string impactJson = tools.Impact("Zeta");
            Assert.DoesNotContain("(no project)", impactJson, StringComparison.Ordinal);
            Assert.Contains(
                Parse(impactJson).GetProperty("references").GetProperty("topProjects").EnumerateArray(),
                g => !g.TryGetProperty("project", out _)
                     && g.TryGetProperty("orphaned", out var o) && o.GetBoolean());

            // context_pack: same structured rows, and ownerProjectEdges flags ONLY the
            // hintPathReference coupling (plain ProjectReferences omit kind to stay compact).
            string packJson = tools.ContextPack("Zeta");
            Assert.DoesNotContain("(no project)", packJson, StringComparison.Ordinal);
            var pack = Parse(packJson);
            var edges = pack.GetProperty("ownerProjectEdges").EnumerateArray().ToList();
            var binEdge = edges.First(e => e.GetProperty("from").GetString()!.Equals("AppBin", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("hintPathReference", binEdge.GetProperty("kind").GetString());
            var projEdge = edges.First(e => e.GetProperty("from").GetString()!.Equals("AppProj", StringComparison.OrdinalIgnoreCase));
            Assert.False(projEdge.TryGetProperty("kind", out _), "plain ProjectReference edges omit kind");

            // Review F1: callers/callees share IndexedReferencesFallback — the ONE group
            // emitter still shipping the magic string after the structured replacement landed
            // everywhere else (a caller adopting the structured contract missed orphan groups
            // on exactly this path). A class name deterministically takes the fallback.
            string callersJson = tools.Callers(name: "Zeta");
            Assert.DoesNotContain("(no project)", callersJson, StringComparison.Ordinal);
            var callerGroups = Parse(callersJson).GetProperty("groups").EnumerateArray().ToList();
            var callerOrphan = callerGroups.First(g => !g.TryGetProperty("project", out _));
            Assert.True(callerOrphan.GetProperty("orphaned").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    // Review F3: structuredPaths ~2.4x the dependency_path payload, and the tool serialized
    // UNBUDGETED — a deep, wide monolith graph with 10 path variants exceeds the
    // 64KB HARD wire cap every other list-bearing tool enforces. Paths must trim as pairs.
    [Fact]
    public void DependencyPathStaysWithinHardBudgetOnDeepWideGraphs()
    {
        string root = Directory.CreateTempSubdirectory("codenav-bxw-budget").FullName;
        try
        {
            // 13-layer lattice, 3-wide in the middle, dotted ~390-char names — enough that ten
            // 13-hop structured paths cannot fit the hard cap untrimmed. Depth is deliberately
            // MODEST: DependencyPaths' BFS enumerates every equal-length partial path before
            // the first result, so path count grows 3^layers (a 17-layer cut of this fixture
            // ran 69 SECONDS; 13 layers runs in a few). The pre-existing exponential hazard is
            // filed separately — this test only pins the wire budget.
            static string Name(int layer, int c) =>
                $"Acme.Monolith.Enterprise.Platform.Integration.Layer{layer:D2}.Component{c}.ServiceHostingModuleImpl" +
                new string('X', 300);
            static string ProjectId(int layer, int c) => $"L{layer:D2}C{c}";
            static int Width(int layer) => layer is 0 or 12 ? 1 : 3;
            for (int layer = 0; layer <= 12; layer++)
            {
                for (int c = 0; c < Width(layer); c++)
                {
                    string name = Name(layer, c);
                    string projectId = ProjectId(layer, c);
                    string dir = Path.Combine(root, projectId);
                    Directory.CreateDirectory(dir);
                    string refs = layer == 12
                        ? ""
                        : string.Join("\n", Enumerable.Range(0, Width(layer + 1)).Select(n =>
                            $"    <ProjectReference Include=\"../{ProjectId(layer + 1, n)}/{ProjectId(layer + 1, n)}.csproj\" />"));
                    File.WriteAllText(Path.Combine(dir, $"{projectId}.csproj"),
                        $"""
                        <Project Sdk="Microsoft.NET.Sdk">
                          <PropertyGroup>
                            <TargetFramework>net9.0</TargetFramework>
                            <AssemblyName>{name}</AssemblyName>
                          </PropertyGroup>
                          <ItemGroup>
                        {refs}
                          </ItemGroup>
                        </Project>
                        """);
                }
            }

            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            string json = tools.DependencyPath(Name(0, 0), Name(12, 0), maxPaths: 10);

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= Json.HardBudgetBytes,
                $"dependency_path exceeded the hard wire budget ({System.Text.Encoding.UTF8.GetByteCount(json)} bytes)");
            var dep = Parse(json);
            Assert.True(dep.GetProperty("found").GetBoolean());
            // truncated must have ENGAGED — otherwise this test passes because the payload
            // happened to be small, not because the budget works.
            Assert.True(dep.GetProperty("truncated").GetBoolean(), "expected the trim to engage on this graph");
            // Pairs stay in lockstep: one hops array per display string, same path per index.
            var displays = dep.GetProperty("paths").EnumerateArray().ToList();
            var structured = dep.GetProperty("structuredPaths").EnumerateArray().ToList();
            Assert.True(displays.Count > 0);
            Assert.Equal(displays.Count, structured.Count);
            for (int i = 0; i < displays.Count; i++)
            {
                Assert.Equal(displays[i].GetString()!.Split(" -> ").Length, structured[i].GetArrayLength());
            }
        }
        finally { Cleanup(root); }
    }

    // Review (verification round, residual): WithListBudget can never trim below ONE item, and
    // a single very deep path's hops array ALONE breaches the hard cap — dependency_path is the
    // first tool whose items are unbounded in size. The lone-item overflow must degrade
    // honestly: structured hops dropped AND flagged, arrow strings intact, bytes within cap.
    [Fact]
    public void DependencyPathDropsStructuredHopsWhenASingleLongPathExceedsBudget()
    {
        string root = Directory.CreateTempSubdirectory("codenav-bxw-chain").FullName;
        try
        {
            static string Name(int i) =>
                $"Acme.Monolith.Enterprise.Platform.Integration.Chain{i:D3}.Component.ServiceHostingModuleImpl";
            const int len = 300; // one 300-node shortest chain: hops alone exceed the 64KB cap
            for (int i = 0; i < len; i++)
            {
                string name = Name(i);
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                string refs = i == len - 1
                    ? ""
                    : $"    <ProjectReference Include=\"../{Name(i + 1)}/{Name(i + 1)}.csproj\" />";
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

            using var m = BuildAndStart(root);
            var tools = new NavigationTools(m, new SemanticService(m));
            string json = tools.DependencyPath(Name(0), Name(len - 1), maxPaths: 10);

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= Json.HardBudgetBytes,
                $"lone-item overflow shipped over the hard cap ({System.Text.Encoding.UTF8.GetByteCount(json)} bytes)");
            var dep = Parse(json);
            Assert.True(dep.GetProperty("found").GetBoolean());
            Assert.False(dep.TryGetProperty("structuredPaths", out _),
                "structured hops must be DROPPED on lone-item overflow, not shipped over-budget");
            Assert.Contains("structured hops", dep.GetProperty("structuredPathsOmitted").GetString()!);
            // The degrade is never silent AND never lossy on the answer itself: the arrow
            // string still carries the FULL chain.
            Assert.Equal(len, dep.GetProperty("paths").EnumerateArray().First().GetString()!.Split(" -> ").Length);
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- fixture

    /// <summary>Lib is consumed four ways: AppProj (real ProjectReference), AppBin (HintPath
    /// assembly ref) + a same-AssemblyName twin row (the net-old/net-new pair idiom — arms
    /// name-level grouping), AppBoth (BOTH — arms first-writer precedence), and an ORPHANED
    /// file no project compiles (arms the structured "(no project)" replacement). Top sits one
    /// ProjectReference above AppBin so depth-2 BFS and dependency_path cross a mixed chain.</summary>
    private static void WriteProvenanceWorkspace(string root)
    {
        static void WriteProject(string root, string name, string body, params (string File, string Source)[] files)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), body);
            foreach (var (file, source) in files) File.WriteAllText(Path.Combine(dir, file), source);
        }

        WriteProject(root, "Lib",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """,
            ("Zeta.cs", "namespace LibNs { public class Zeta { } }"));

        WriteProject(root, "AppProj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """,
            ("UseProj.cs", "namespace AppProj { public class UseProj { public LibNs.Zeta? Z; } }"));

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

        // AppBin's same-AssemblyName twin (net-old/net-new): a SECOND project row named 'AppBin'
        // with the same recovered edge — provenance consumers must group by NAME, not row.
        WriteProject(root, "AppBin.NetNew",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>AppBin</AssemblyName>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="../AppBin/UseBin.cs" />
                <Reference Include="Lib"><HintPath>../Common/Lib.dll</HintPath></Reference>
              </ItemGroup>
            </Project>
            """);

        WriteProject(root, "AppBoth",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
                <Reference Include="Lib"><HintPath>../Common/Lib.dll</HintPath></Reference>
              </ItemGroup>
            </Project>
            """,
            ("UseBoth.cs", "namespace AppBoth { public class UseBoth { public LibNs.Zeta? Z; } }"));

        WriteProject(root, "Top",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../AppBin/AppBin.csproj" />
              </ItemGroup>
            </Project>
            """,
            ("TopUser.cs", "namespace Top { public class TopUser { } }"));

        // Mixed-kind NAME edge: TwinA/TwinZ are a same-AssemblyName pair, and TWO consumers each
        // hold a real ProjectReference to ONE twin row plus a <Reference Include="Twin">. Assembly
        // recovery picks ONE row for the whole name (pick-first, scan-order dependent — the first
        // fixture cut assumed alphabetical and the recovery landed on the SAME row as the
        // ProjectReference, so INSERT OR IGNORE swallowed it and the mixed shape never existed).
        // With a consumer per twin, WHICHEVER row wins, exactly one consumer's recovery hits the
        // OTHER twin's row: one name-level edge carries a 'project' row AND an 'assembly' row in
        // EVERY scan order — arming EdgeKindMap's project-wins tiebreak and impact's All-rows test.
        foreach (var twin in new[] { "TwinA", "TwinZ" })
        {
            WriteProject(root, twin,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <AssemblyName>Twin</AssemblyName>
                  </PropertyGroup>
                </Project>
                """,
                ("T.cs", $"namespace {twin} {{ public class T{twin} {{ }} }}"));
            string consumer = twin == "TwinA" ? "AppMixedA" : "AppMixedZ";
            WriteProject(root, consumer,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../{twin}/{twin}.csproj" />
                    <Reference Include="Twin"><HintPath>../Common/Twin.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """,
                ("M.cs", $"namespace {consumer} {{ public class M{consumer} {{ }} }}"));
        }

        // Indexed, compiled by NO project — the copied-then-removed-from-csproj field shape.
        string orphanDir = Path.Combine(root, "Orphan");
        Directory.CreateDirectory(orphanDir);
        File.WriteAllText(Path.Combine(orphanDir, "OrphanUse.cs"),
            "namespace Orphan { public class OrphanUse { public LibNs.Zeta? Z; } }");
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
