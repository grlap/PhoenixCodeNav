using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// Builds one small synthetic workspace + index for the whole test class.
/// </summary>
public sealed class IndexFixture : IDisposable
{
    public string Root { get; }
    public string DbPath { get; }

    public IndexFixture()
    {
        Root = Directory.CreateTempSubdirectory("codenav-e2e").FullName;
        WorkspaceGenerator.Generate(Root, targetProjects: 40, seed: 7);
        DbPath = IndexBuilder.DefaultDbPath(Root);
        IndexBuilder.Build(Root, DbPath);
    }

    public IndexQueries Open() => new(DbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch { /* leave temp on Windows lock */ }
    }
}

public class IndexEndToEndTests : IClassFixture<IndexFixture>
{
    private readonly IndexFixture _fx;

    public IndexEndToEndTests(IndexFixture fx) => _fx = fx;

    [Fact]
    public void OverviewCountsAreConsistent()
    {
        using var q = _fx.Open();
        var o = q.Overview();
        Assert.True(o.Projects >= 40);
        Assert.True(o.CsFiles > 100);
        Assert.True(o.Symbols > 500);
        Assert.True(o.LegacyProjects + o.SdkProjects == o.Projects);
        Assert.Contains("net472", o.TfmBreakdown);
        Assert.NotNull(o.IndexVersion);
    }

    [Fact]
    public void FindFileSupportsGlobsAndNames()
    {
        using var q = _fx.Open();
        Assert.NotEmpty(q.FindFiles("Guard.cs", 10));
        Assert.NotEmpty(q.FindFiles("*.csproj", 10));
        Assert.Empty(q.FindFiles("DoesNotExist_zz.cs", 10));
    }

    [Fact]
    public void SymbolSearchFindsWellKnownTypes()
    {
        using var q = _fx.Open();
        var guard = q.SearchSymbols("Guard", "exact", new[] { "class" }, 10);
        Assert.Single(guard);
        Assert.Equal("Acme.Platform.Common", guard[0].Ns);

        var prefixed = q.SearchSymbols("Sy", "prefix", new[] { "class" }, 50);
        Assert.Contains(prefixed, s => s.Name == "SystemClock");
    }

    [Fact]
    public void OutlineAndSymbolAtAgree()
    {
        using var q = _fx.Open();
        var guardFile = q.FindFiles("Guard.cs", 1).Single();
        var outline = q.Outline(guardFile.Path);
        var notNull = outline.Single(s => s.Name == "NotNull");

        var chain = q.SymbolAt(guardFile.Path, notNull.StartLine + 1);
        Assert.Equal("NotNull", chain[0].Name);
        Assert.Equal("Guard", chain[1].Name);
        Assert.Equal("namespace", chain[^1].Kind);
    }

    [Fact]
    public void ReferencesFindWholeIdentifierUsagesOnly()
    {
        using var q = _fx.Open();
        var (total, groups) = q.ReferenceCandidates("Guard", 200, 2);
        Assert.True(total > 10);
        Assert.NotEmpty(groups);
        // Whole-identifier matching: "GuardXyz" must not count. All samples contain "Guard" as a token.
        foreach (var sample in groups.SelectMany(g => g.Samples))
        {
            Assert.Contains("Guard", sample.LineText);
        }
    }

    [Fact]
    public void ProjectGraphAndOwnershipWork()
    {
        using var q = _fx.Open();
        var upstream = q.ProjectGraph("Acme.Platform.Common", 1, "upstream");
        Assert.True(upstream.Count > 5); // the hot node has many dependents
        Assert.All(upstream, e => Assert.Equal("Acme.Platform.Common", e.ToProject));

        // Every legacy project's explicitly listed file resolves to exactly that owner.
        var guardFile = q.FindFiles("Guard.cs", 1).Single();
        var owners = q.ProjectsContaining(guardFile.Path);
        Assert.Contains(owners, p => p.Name == "Acme.Platform.Common");
    }

    [Fact]
    public void SearchTextHonorsFilters()
    {
        using var q = _fx.Open();
        var all = q.SearchText("AcmeException", 30);
        Assert.NotEmpty(all);

        var configOnly = q.SearchText("repositoryPath", 10, new IndexQueries.TextFilter(Lang: "config"));
        Assert.NotEmpty(configOnly);
        Assert.All(configOnly, h => Assert.EndsWith("NuGet.config", h.FilePath));

        var scoped = q.SearchText("AcmeException", 30, new IndexQueries.TextFilter(PathGlob: "src/Platform/**"));
        Assert.All(scoped, h => Assert.StartsWith("src/Platform/", h.FilePath));
    }

    [Fact]
    public void SearchSymbolHonorsPathAndNamespaceFilters()
    {
        using var q = _fx.Open();

        // Baseline: exactly one Guard class, in namespace Acme.Platform.Common.
        var baseline = q.SearchSymbols("Guard", "exact", new[] { "class" }, 10);
        Assert.Single(baseline);
        string guardPath = baseline[0].FilePath;
        string topDir = guardPath.Split('/')[0];

        // namespace subtree: exact namespace and a parent prefix both match; a foreign one does not.
        Assert.Single(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, ns: "Acme.Platform.Common"));
        Assert.Single(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, ns: "Acme.Platform"));
        Assert.Empty(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, ns: "Acme.Nonexistent"));
        // A prefix that is not a namespace *segment* boundary must not match (trailing dot guards it).
        Assert.Empty(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, ns: "Acme.Plat"));

        // pathGlob include: the owning subtree matches; a bogus subtree does not.
        Assert.Single(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, pathGlob: $"{topDir}/**"));
        Assert.Empty(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, pathGlob: "no_such_dir_zz/**"));

        // excludePath: excluding the owning subtree drops it; excluding elsewhere keeps it.
        Assert.Empty(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, excludePath: $"{topDir}/**"));
        Assert.Single(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, excludePath: "no_such_dir_zz/**"));

        // Bare name (no '/') matches the file at any depth for both include and exclude.
        Assert.Single(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, pathGlob: "Guard.cs"));
        Assert.Empty(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, excludePath: "Guard.cs"));
    }

    [Fact]
    public void BareGlobsReachWorkspaceRootFiles()
    {
        // A symbol-bearing file at depth 0. Kept OUT of the shared fixture on purpose:
        // several tests pick FindFiles("*.cs", 1) and assume a parent directory exists.
        const string rel = "RootMarker.cs";
        string full = Path.Combine(_fx.Root, rel);
        File.WriteAllText(full, "namespace RootNs { public class RootMarkerClass { } }");
        try
        {
            using (var store = new IndexStore(_fx.DbPath, createNew: false))
            {
                DeltaRefresher.Refresh(store, _fx.Root, new[] { rel });
            }

            using var q = _fx.Open();
            // Only the bare $incBare/$excBare arms can reach a root file — reverting them
            // to the single '%/name' pattern fails these (mutation guard).
            Assert.Single(q.SearchSymbols("RootMarkerClass", "exact", null, 5, pathGlob: rel));
            Assert.Empty(q.SearchSymbols("RootMarkerClass", "exact", null, 5, excludePath: rel));
            // Sanity: the file really is at root — a nested-only pattern must not see it.
            Assert.Empty(q.SearchSymbols("RootMarkerClass", "exact", null, 5, pathGlob: $"*/{rel}"));

            // search_text shares AppendPathFilter — same root reach (consistency pin).
            var rootHits = q.SearchText("RootMarkerClass", 10, new IndexQueries.TextFilter(PathGlob: rel));
            Assert.NotEmpty(rootHits);
            Assert.All(rootHits, h => Assert.Equal(rel, h.FilePath));
        }
        finally
        {
            File.Delete(full);
            using var store = new IndexStore(_fx.DbPath, createNew: false);
            DeltaRefresher.Refresh(store, _fx.Root, new[] { rel });
        }
    }

    [Fact]
    public void DeltaRefreshHandlesEditAddDelete()
    {
        using var store = new IndexStore(_fx.DbPath, createNew: false);

        // --- edit: add a method with a unique marker to Guard.cs
        using var q0 = _fx.Open();
        var guardFile = q0.FindFiles("Guard.cs", 1).Single();
        string full = Path.Combine(_fx.Root, guardFile.Path.Replace('/', Path.DirectorySeparatorChar));
        string original = File.ReadAllText(full);
        string marker = "ZebraUnicornMethod";
        File.WriteAllText(full, original.Replace(
            "public static void NotNull",
            $"public static void {marker}() {{ }}\n\n        public static void NotNull"));

        var result = DeltaRefresher.Refresh(store, _fx.Root, new[] { guardFile.Path });
        Assert.Equal(1, result.ChangedFiles);

        using (var q = _fx.Open())
        {
            Assert.NotEmpty(q.SearchSymbols(marker, "exact", null, 5));      // symbols updated
            Assert.NotEmpty(q.SearchText(marker, 5));                        // FTS updated
        }

        // --- add: a new file inside an SDK-style project dir
        using var q1 = _fx.Open();
        var sdkProject = q1.SearchText("Microsoft.NET.Sdk", 5, new IndexQueries.TextFilter(Lang: "csproj")).First();
        string projectDir = Path.GetDirectoryName(sdkProject.FilePath)!.Replace('\\', '/');
        string newRel = $"{projectDir}/ZebraAddedFile.cs";
        File.WriteAllText(
            Path.Combine(_fx.Root, newRel.Replace('/', Path.DirectorySeparatorChar)),
            "namespace Zebra { public class ZebraAddedClass { } }");

        result = DeltaRefresher.Refresh(store, _fx.Root, new[] { newRel });
        Assert.Equal(1, result.AddedFiles);
        Assert.True(result.ProjectsRefreshed);

        using (var q = _fx.Open())
        {
            Assert.NotEmpty(q.SearchSymbols("ZebraAddedClass", "exact", null, 5));
            var owners = q.ProjectsContaining(newRel);
            Assert.NotEmpty(owners); // SDK longest-prefix ownership resolved
        }

        // --- delete
        File.Delete(Path.Combine(_fx.Root, newRel.Replace('/', Path.DirectorySeparatorChar)));
        result = DeltaRefresher.Refresh(store, _fx.Root, new[] { newRel });
        Assert.Equal(1, result.DeletedFiles);

        using (var q = _fx.Open())
        {
            Assert.Empty(q.SearchSymbols("ZebraAddedClass", "exact", null, 5));
            Assert.Empty(q.SearchText("ZebraAddedClass", 5));
        }

        // restore Guard.cs
        File.WriteAllText(full, original);
        DeltaRefresher.Refresh(store, _fx.Root, new[] { guardFile.Path });
    }
}

public class McpToolLayerTests : IClassFixture<IndexFixture>
{
    private readonly IndexFixture _fx;

    public McpToolLayerTests(IndexFixture fx) => _fx = fx;

    private NavigationTools Tools()
    {
        var manager = new IndexManager(_fx.Root, _fx.DbPath);
        manager.Start();
        // Index already exists — Start opens it quickly; wait for queryable.
        for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
        Assert.True(manager.IsQueryable, "index did not become queryable");
        return new NavigationTools(manager, new CodeNav.Core.Semantic.SemanticService(manager));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void EveryResponseCarriesMetaEnvelope()
    {
        var tools = Tools();
        foreach (var json in new[]
                 {
                     tools.RepoOverview(),
                     tools.FindFile("*.cs", 5),
                     tools.SearchSymbol("Guard"),
                     tools.References("Guard", mode: "indexed", maxFiles: 100),
                 })
        {
            var meta = Parse(json).GetProperty("meta");
            Assert.Contains(meta.GetProperty("confidence").GetString(), new[] { "indexed", "exact" });
            Assert.False(string.IsNullOrEmpty(meta.GetProperty("indexStatus").GetString()));
        }
    }

    [Fact]
    public void ResponsesRespectHardBudget()
    {
        var tools = Tools();
        // The hot-node graph would serialize to hundreds of KB without budgeting.
        string json = tools.ProjectGraph("Acme.Platform.Common", depth: 3, direction: "both");
        Assert.True(json.Length <= Json.HardBudgetBytes, $"graph response {json.Length} bytes exceeds budget");

        string refs = tools.References("Guard", maxFiles: 500, samplesPerGroup: 5);
        Assert.True(refs.Length <= Json.HardBudgetBytes, $"references response {refs.Length} bytes exceeds budget");
    }

    [Fact]
    public void FindFilePagingRoundTrips()
    {
        var tools = Tools();
        var page1 = Parse(tools.FindFile("*.cs", limit: 5));
        string? cursor = page1.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var page2 = Parse(tools.FindFile("*.cs", limit: 5, cursor: cursor));
        var first = page1.GetProperty("files").EnumerateArray().First().GetProperty("path").GetString();
        var second = page2.GetProperty("files").EnumerateArray().First().GetProperty("path").GetString();
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SourceContextReadsLiveSpans()
    {
        var tools = Tools();
        var guardPath = Parse(tools.FindFile("Guard.cs", 1))
            .GetProperty("files").EnumerateArray().First().GetProperty("path").GetString()!;
        var ctx = Parse(tools.SourceContext(guardPath, "5-8", contextLines: 0));
        Assert.Equal("live", ctx.GetProperty("freshness").GetString());
        string source = ctx.GetProperty("spans").EnumerateArray().First().GetProperty("source").GetString()!;
        Assert.Contains("5|", source);
    }

    [Fact]
    public void OutlineDepthOneOmitsMembers()
    {
        var tools = Tools();
        var guardPath = Parse(tools.FindFile("Guard.cs", 1))
            .GetProperty("files").EnumerateArray().First().GetProperty("path").GetString()!;

        string shallow = tools.Outline(guardPath, depth: 1);
        Assert.DoesNotContain("NotNull", shallow);

        string deep = tools.Outline(guardPath, depth: 2);
        Assert.Contains("NotNull", deep);
    }

    [Fact]
    public void SearchSymbolToolAppliesFilters()
    {
        var tools = Tools();
        static int Count(JsonElement r) => r.GetProperty("symbols").GetArrayLength();
        static bool AnyUnder(JsonElement r, string dir) =>
            r.GetProperty("symbols").EnumerateArray().Any(s => s.GetProperty("path").GetString()!.StartsWith(dir + "/"));

        var all = Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact"));
        Assert.True(Count(all) >= 1);
        string topDir = all.GetProperty("symbols").EnumerateArray().First().GetProperty("path").GetString()!.Split('/')[0];

        // excludePath drops the owning subtree.
        Assert.Equal(0, Count(Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact", excludePath: $"{topDir}/**"))));

        // pathGlob include: owning subtree matches, bogus subtree drops.
        Assert.True(Count(Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact", pathGlob: $"{topDir}/**"))) >= 1);
        Assert.Equal(0, Count(Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact", pathGlob: "no_such_dir_zz/**"))));

        // namespace subtree keeps it; a foreign namespace drops it (discriminating, not a tautology).
        Assert.True(Count(Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact", @namespace: "Acme.Platform"))) >= 1);
        Assert.Equal(0, Count(Parse(tools.SearchSymbol("Guard", kinds: "class", match: "exact", @namespace: "Acme.Nonexistent"))));

        // Auto-mode fallthrough (exact 'Guar' -> prefix) must still honor excludePath.
        Assert.True(AnyUnder(Parse(tools.SearchSymbol("Guar", kinds: "class")), topDir));
        Assert.False(AnyUnder(Parse(tools.SearchSymbol("Guar", kinds: "class", excludePath: $"{topDir}/**")), topDir));

        // Auto-mode EXACT hit (no match arg) must honor filters too — guards the first
        // auto-mode call site, not just the fallthrough ones.
        Assert.Equal(0, Count(Parse(tools.SearchSymbol("Guard", kinds: "class", excludePath: $"{topDir}/**"))));
    }
}
