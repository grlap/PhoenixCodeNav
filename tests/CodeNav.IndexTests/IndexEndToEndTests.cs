using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// Builds one small synthetic workspace + index for either an exclusive mutable class or the
/// shared read-only functional collection.
/// </summary>
public class IndexFixture : IDisposable
{
    public string Root { get; }
    public string DbPath { get; }

    private readonly object _toolsGate = new();
    private IndexManager? _manager;
    private CodeNav.Core.Semantic.SemanticService? _semantic;
    private NavigationTools? _tools;

    public IndexFixture()
    {
        Root = Directory.CreateTempSubdirectory("codenav-e2e").FullName;
        WorkspaceGenerator.Generate(Root, targetProjects: 40, seed: 7);
        DbPath = IndexBuilder.DefaultDbPath(Root);
        IndexBuilder.Build(Root, DbPath);
    }

    public IndexQueries Open() => new(DbPath);

    /// <summary>
    /// One live IndexManager per fixture instance, created on first use and disposed with the
    /// fixture. Writer ownership is exclusive per database — a manager per TEST would leak the
    /// writer (xUnit never disposes test-created managers) and force every subsequent manager into
    /// read-only follower mode, where fixture refreshes cannot run. Lazy so classes that
    /// only use Open() (direct read connections need no lease) never attach a live watcher.
    /// </summary>
    private void EnsureSharedHost()
    {
        lock (_toolsGate)
        {
            if (_tools is not null) return;

            var manager = new IndexManager(Root, DbPath);
            manager.Start();
            for (int i = 0; i < 600 && !manager.IsQueryable; i++) Thread.Sleep(50); // 30s: the 5s wait was the suite-wide startup-starvation flake class
            Assert.True(manager.IsQueryable, "index did not become queryable");
            _manager = manager;
            _semantic = new CodeNav.Core.Semantic.SemanticService(manager);
            _tools = new NavigationTools(manager, _semantic);
        }
    }

    public IndexManager SharedManager
    {
        get { EnsureSharedHost(); return _manager!; }
    }

    public CodeNav.Core.Semantic.SemanticService SharedSemantic
    {
        get { EnsureSharedHost(); return _semantic!; }
    }

    public NavigationTools SharedTools
    {
        get { EnsureSharedHost(); return _tools!; }
    }

    public void Dispose()
    {
        _semantic?.Dispose();
        _manager?.Dispose(); // releases the store, pooled connections, and the ownership lease
        TestWorkspaceCleanup.DeleteWorkspace(Root);
    }
}

public sealed class SharedIndexFixture : IndexFixture
{
    private static int _instancesCreated;

    public SharedIndexFixture() => Interlocked.Increment(ref _instancesCreated);

    public static int InstancesCreated => Volatile.Read(ref _instancesCreated);
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
        var (total, prod, test, groups) = q.ReferenceCandidates("Guard", 200, 2);
        Assert.True(total > 10);
        Assert.Equal(total, prod + test); // physical split always sums to the physical total (0ok)
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

        // excludePaths: excluding the owning subtree drops it; excluding elsewhere keeps it.
        Assert.Empty(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, excludePaths: new[] { $"{topDir}/**" }));
        Assert.Single(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, excludePaths: new[] { "no_such_dir_zz/**" }));

        // Bare name (no '/') matches the file at any depth for both include and exclude.
        Assert.Single(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, pathGlob: "Guard.cs"));
        Assert.Empty(q.SearchSymbols("Guard", "exact", new[] { "class" }, 10, excludePaths: new[] { "Guard.cs" }));
    }

    [Fact]
    public void IndexedPathSuggestionsPreferPreservedPrefixAndReportCoverage()
    {
        const string basename = "PhoenixPathSuggestionProbe.cs";
        string[] paths =
        [
            $"src/Platform/Common/Service/{basename}",
            $"other/Area/Service/{basename}",
            $"else/One/Service/{basename}",
            $"else/Two/Service/{basename}",
            $"src/[Generated]/Nested/{basename}",
        ];
        foreach (string relativePath in paths)
        {
            string fullPath = Path.Combine(
                _fx.Root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "namespace PathSuggestionProbe { class Marker { } }");
        }

        using var store = new IndexStore(_fx.DbPath, createNew: false);
        try
        {
            DeltaRefresher.Refresh(store, _fx.Root, paths);
            using var q = _fx.Open();

            PathSuggestionResult result =
                q.SuggestFilePaths($"src/Platform/Service/{basename}");
            Assert.Equal(paths.Length, result.Total);
            Assert.Equal(3, result.Paths.Count);
            Assert.Equal(paths[0], result.Paths[0]);

            PathSuggestionResult bracket =
                q.SuggestFilePaths($"src/[Generated]/{basename}");
            Assert.Equal(paths.Length, bracket.Total);
            Assert.Equal(paths[4], bracket.Paths[0]);
        }
        finally
        {
            foreach (string relativePath in paths)
            {
                File.Delete(Path.Combine(
                    _fx.Root,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
            DeltaRefresher.Refresh(store, _fx.Root, paths);
        }
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
            Assert.Empty(q.SearchSymbols("RootMarkerClass", "exact", null, 5, excludePaths: new[] { rel }));
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
        // zki: in a PURE-SDK workspace an added .cs is attributed incrementally (no global rebuild);
        // any legacy project forces the full rebuild (a legacy explicit <Compile> list can claim a
        // re-added file without its csproj changing). Either way the ownership assertion below is the
        // real contract.
        bool hasLegacy;
        using (var q = _fx.Open()) hasLegacy = q.Overview().LegacyProjects > 0;
        Assert.Equal(hasLegacy, result.ProjectsRefreshed);

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

[Collection(SharedIndexCollection.Name)]
public class McpToolLayerTests
{
    private readonly IndexFixture _fx;

    public McpToolLayerTests(SharedIndexFixture fx) => _fx = fx;

    // One shared writer for the functional collection; individual tests never attach competing
    // managers to this immutable index.
    private NavigationTools Tools() => _fx.SharedTools;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void EveryResponseCarriesMetaEnvelope()
    {
        var tools = Tools();
        foreach (var json in new[]
                 {
                     tools.RepoOverview(),
                     tools.FindFile("*.cs", limit: 5),
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
    public void MissingDirectorySegmentSuggestsTheIndexedPathAcrossPathTools()
    {
        var tools = Tools();
        using var q = _fx.Open();
        string indexedPath = Assert.Single(q.FindFiles("Guard.cs", 10)).Path;
        string[] parts = indexedPath.Split('/');
        Assert.True(parts.Length >= 5, $"expected a nested Guard.cs fixture, got '{indexedPath}'");
        string guessedPath = string.Join('/', parts.Where((_, index) => index != 2));
        Assert.Empty(q.FindFiles(guessedPath, 10));

        static JsonElement Suggestions(JsonElement response) =>
            response.GetProperty("pathSuggestions");

        static string[] SuggestionPaths(JsonElement response) =>
            Suggestions(response).GetProperty("paths").EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();

        JsonElement outline = Parse(tools.Outline(guessedPath));
        Assert.Equal("file_not_indexed", outline.GetProperty("error").GetString());
        Assert.Equal([indexedPath], SuggestionPaths(outline));
        Assert.Equal(1, Suggestions(outline).GetProperty("total").GetInt32());
        Assert.False(Suggestions(outline).GetProperty("truncated").GetBoolean());
        Assert.False(outline.TryGetProperty("didYouMean", out _));

        JsonElement source = Parse(tools.SourceContext(guessedPath, "1", contextLines: 0));
        Assert.Equal("file_not_found", source.GetProperty("error").GetString());
        Assert.Equal([indexedPath], SuggestionPaths(source));

        JsonElement find = Parse(tools.FindFile(guessedPath));
        Assert.Empty(find.GetProperty("files").EnumerateArray());
        Assert.Equal([indexedPath], SuggestionPaths(find));

        JsonElement exact = Parse(tools.Outline(indexedPath));
        Assert.False(exact.TryGetProperty("pathSuggestions", out _));

        JsonElement irrelevant = Parse(tools.Outline("missing/DefinitelyUnknownPathSuggestion.cs"));
        Assert.Equal("file_not_indexed", irrelevant.GetProperty("error").GetString());
        Assert.False(irrelevant.TryGetProperty("pathSuggestions", out _));

        JsonElement globMiss = Parse(tools.FindFile("missing/**/*.DefinitelyUnknownPathSuggestion"));
        Assert.Empty(globMiss.GetProperty("files").EnumerateArray());
        Assert.False(globMiss.TryGetProperty("pathSuggestions", out _));

        JsonElement excluded = Parse(tools.FindFile(guessedPath, excludePath: indexedPath));
        Assert.Empty(excluded.GetProperty("files").EnumerateArray());
        Assert.False(excluded.TryGetProperty("pathSuggestions", out _));
    }

    [Fact]
    public void IndexedPathSuggestionsRankSuffixMatchesDeterministicallyAndCapAtThree()
    {
        using var q = _fx.Open();
        string[] modelPaths = q.FindFiles("Models.cs", 100)
            .Select(file => file.Path)
            .Where(path => path.Split('/').Length >= 5)
            .ToArray();
        Assert.True(modelPaths.Length >= 4, "expected at least four duplicate Models.cs fixtures");

        string target = modelPaths[0];
        string[] parts = target.Split('/');
        string guessedPath = string.Join('/', parts.Where((_, index) => index != 2));
        Assert.Null(q.FileByPath(guessedPath));

        PathSuggestionResult suggestions = q.SuggestFilePaths(guessedPath);
        Assert.True(suggestions.Total >= 4);
        Assert.Equal(3, suggestions.Paths.Count);
        Assert.Equal(target, suggestions.Paths[0]);

        JsonElement response = Parse(Tools().Outline(guessedPath));
        JsonElement responseSuggestions = response.GetProperty("pathSuggestions");
        Assert.Equal(suggestions.Total, responseSuggestions.GetProperty("total").GetInt32());
        Assert.Equal(3, responseSuggestions.GetProperty("paths").GetArrayLength());
        Assert.True(responseSuggestions.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, q.SuggestFilePaths("missing/**/*.cs").Total);
        Assert.Equal(
            0,
            q.SuggestFilePaths("missing/DefinitelyUnknownPathSuggestion.cs").Total);
    }

    [Fact]
    public void PathSuggestionPayloadUsesItsOwnShapeAndHonorsTheHardBudget()
    {
        var oversizedPaths = Enumerable.Range(0, 3)
            .Select(index => $"{new string('x', 29_980)}/Target{index}.cs")
            .ToList();
        string json = Json.WithListBudget(
            oversizedPaths,
            (paths, budgetTruncated) => new
            {
                error = "file_not_indexed",
                pathSuggestions = NavigationTools.PathSuggestionsJson(
                    oversizedPaths.Count,
                    paths,
                    budgetTruncated),
            });

        Assert.True(
            Json.Utf8Bytes(json) <= Json.HardBudgetBytes,
            $"path-suggestion response used {Json.Utf8Bytes(json)} bytes");
        JsonElement response = Parse(json);
        JsonElement suggestions = response.GetProperty("pathSuggestions");
        Assert.Equal(oversizedPaths.Count, suggestions.GetProperty("total").GetInt32());
        Assert.True(suggestions.GetProperty("truncated").GetBoolean());
        Assert.False(response.TryGetProperty("didYouMean", out _));
    }

    [Fact]
    public void SourceContextAcceptsRangeCompatibilityAliasAndRejectsConflicts()
    {
        var parameterNames = typeof(NavigationTools)
            .GetMethod(nameof(NavigationTools.SourceContext))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.Contains("range", parameterNames);

        var tools = Tools();
        var guardPath = Parse(tools.FindFile("Guard.cs", limit: 1))
            .GetProperty("files").EnumerateArray().First().GetProperty("path").GetString()!;
        JsonElement canonical = Parse(tools.SourceContext(
            path: guardPath,
            spans: "5-8",
            contextLines: 0));
        JsonElement alias = Parse(tools.SourceContext(
            path: guardPath,
            contextLines: 0,
            range: "5-8"));

        Assert.Equal(
            canonical.GetProperty("spans").GetRawText(),
            alias.GetProperty("spans").GetRawText());

        JsonElement identical = Parse(tools.SourceContext(
            path: guardPath,
            spans: "5-8",
            contextLines: 0,
            range: "5-8"));
        Assert.Equal(
            canonical.GetProperty("spans").GetRawText(),
            identical.GetProperty("spans").GetRawText());

        JsonElement guardSymbol = Parse(tools.SearchSymbol(
                "Guard",
                kinds: "class",
                match: "exact"))
            .GetProperty("symbols")
            .EnumerateArray()
            .First(symbol => symbol.GetProperty("path").GetString() == guardPath);
        JsonElement bySymbolId = Parse(tools.SourceContext(
            contextLines: 0,
            symbolId: guardSymbol.GetProperty("symbolId").GetString(),
            range: "1-2"));
        JsonElement symbolSpan = bySymbolId.GetProperty("spans").EnumerateArray().Single();
        Assert.Equal(
            guardSymbol.GetProperty("startLine").GetInt32(),
            symbolSpan.GetProperty("startLine").GetInt32());
        Assert.Equal(
            guardSymbol.GetProperty("endLine").GetInt32(),
            symbolSpan.GetProperty("endLine").GetInt32());

        JsonElement conflict = Parse(tools.SourceContext(
            path: guardPath,
            spans: "5-8",
            range: "6-9"));
        Assert.Equal("bad_request", conflict.GetProperty("error").GetString());
        Assert.Contains("'spans'", conflict.GetProperty("detail").GetString());
        Assert.Contains("'range'", conflict.GetProperty("detail").GetString());

        JsonElement missing = Parse(tools.SourceContext(path: guardPath));
        Assert.Equal("bad_request", missing.GetProperty("error").GetString());
        Assert.Contains("'spans'", missing.GetProperty("detail").GetString());
        Assert.Contains("'range'", missing.GetProperty("detail").GetString());
    }

    [Fact]
    public void SourceContextReadsLiveSpans()
    {
        var tools = Tools();
        var guardPath = Parse(tools.FindFile("Guard.cs", limit: 1))
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
        var guardPath = Parse(tools.FindFile("Guard.cs", limit: 1))
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
