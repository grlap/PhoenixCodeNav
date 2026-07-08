using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Regression coverage for review batch 4: PhoenixCodeNav-cdd (search_text line grading,
/// no silent first-token substitution) and 1ze (heuristic confidence label).
/// </summary>
public class Batch4SearchGradingTests : IClassFixture<IndexFixture>, IDisposable
{
    private readonly IndexFixture _fx;
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;

    public Batch4SearchGradingTests(IndexFixture fx)
    {
        _fx = fx;
        _manager = new IndexManager(_fx.Root, _fx.DbPath);
        _manager.Start();
        for (int i = 0; i < 100 && !_manager.IsQueryable; i++) Thread.Sleep(50);
        _semantic = new SemanticService(_manager);
    }

    public void Dispose()
    {
        _semantic.Dispose();
        _manager.Dispose();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void PreciseHitsContainAllTokens()
    {
        using var q = _manager.OpenQueries();
        var res = q.SearchTextGraded("Guard NotNull", 30, null, 300, 0, "auto");
        Assert.True(res.TotalPrecise > 0, "expected precise co-occurrence hits for Guard.NotNull call sites");
        var precise = res.Hits.Where(h => h.MatchKind == "precise").ToList();
        Assert.NotEmpty(precise);
        Assert.All(precise, h =>
        {
            Assert.Contains("Guard", h.LineText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NotNull", h.LineText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void SingleTokenQueryIsAllPrecise()
    {
        using var q = _manager.OpenQueries();
        var res = q.SearchTextGraded("AcmeException", 20, null, 300, 0, "auto");
        Assert.True(res.TotalPrecise > 0);
        Assert.Equal(0, res.TotalPartial);
        Assert.All(res.Hits, h => Assert.Equal("precise", h.MatchKind));
    }

    [Fact]
    public void SplitTokensYieldTokenCoveringPartials_NotFirstTokenSpam()
    {
        // The exact bug: two tokens both present in a file but never on one line. The old code
        // returned every first-token line as a full hit; the fix returns token-covering partials.
        using var q0 = _manager.OpenQueries();
        var anyCs = q0.FindFiles("*.cs", 1).Single();
        string dir = Path.GetDirectoryName(anyCs.Path)!.Replace('\\', '/');
        string rel = $"{dir}/ZebraSplit.cs";
        string full = Path.Combine(_fx.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(full,
            "namespace Zebra\n{\n" +
            "    // ZebraAlpha marker one\n" +
            "    // ZebraAlpha marker two\n" +
            "    // ZebraAlpha marker three\n" +
            "    class C\n    {\n" +
            "        // ZebraBeta marker\n" +
            "    }\n}\n");
        try
        {
            using (var store = new IndexStore(_fx.DbPath, createNew: false))
            {
                DeltaRefresher.Refresh(store, _fx.Root, new[] { rel });
            }

            using var q = _manager.OpenQueries();
            var res = q.SearchTextGraded("ZebraAlpha ZebraBeta", 20, null, 300, 0, "auto");

            Assert.Equal(0, res.TotalPrecise);                 // no line has both tokens
            Assert.True(res.TotalPartial >= 2, "expected token-covering partials");
            var fileHits = res.Hits.Where(h => h.FilePath == rel).ToList();
            Assert.True(fileHits.Count <= 2,
                $"token-covering means <=1 line per token (<=2 total), got {fileHits.Count} (first-token spam?)");
            Assert.All(res.Hits, h => Assert.Equal("partial", h.MatchKind));
            Assert.Contains(res.Hits, h => h.Matched is not null && h.Matched.Contains("ZebraAlpha"));
            Assert.Contains(res.Hits, h => h.Matched is not null && h.Matched.Contains("ZebraBeta"));
            Assert.Contains(rel, res.FilesMatchedAcrossLines);

            // partials='never' drops them entirely (no precise -> empty).
            var never = q.SearchTextGraded("ZebraAlpha ZebraBeta", 20, null, 300, 0, "never");
            Assert.Empty(never.Hits);
            Assert.Equal(0, never.TotalPrecise);

            // Single token collapses to all-precise (the repeated ZebraAlpha lines).
            var single = q.SearchTextGraded("ZebraAlpha", 20, null, 300, 0, "auto");
            Assert.True(single.TotalPrecise >= 3);
            Assert.All(single.Hits, h => Assert.Equal("precise", h.MatchKind));
        }
        finally
        {
            File.Delete(full);
            using var store = new IndexStore(_fx.DbPath, createNew: false);
            DeltaRefresher.Refresh(store, _fx.Root, new[] { rel });
        }
    }

    [Fact]
    public void SubstringTokenIsNotGradedPrecise()
    {
        // 'Zeb' is a whole-token substring of 'ZebItem'. For query 'Zeb ZebItem', a ZebItem-only
        // line must NOT be graded precise (the pre-fix raw-substring check wrongly did — Order/OrderId).
        using var q0 = _manager.OpenQueries();
        var anyCs = q0.FindFiles("*.cs", 1).Single();
        string dir = Path.GetDirectoryName(anyCs.Path)!.Replace('\\', '/');
        string rel = $"{dir}/ZebSubstring.cs";
        string full = Path.Combine(_fx.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(full,
            "namespace Zebra\n{\n" +
            "    // standalone Zeb marker\n" +
            "    // ZebItem alpha\n" +
            "    // ZebItem beta\n" +
            "}\n");
        try
        {
            using (var store = new IndexStore(_fx.DbPath, createNew: false))
            {
                DeltaRefresher.Refresh(store, _fx.Root, new[] { rel });
            }
            using var q = _manager.OpenQueries();
            var res = q.SearchTextGraded("Zeb ZebItem", 20, null, 300, 0, "auto");
            // No line contains BOTH whole tokens ('Zeb' as a token appears only on the standalone line).
            Assert.Equal(0, res.TotalPrecise);
            Assert.True(res.TotalPartial >= 2);
            Assert.Contains(res.Hits, h => h.Matched is not null && h.Matched.Contains("Zeb"));
            Assert.Contains(res.Hits, h => h.Matched is not null && h.Matched.Contains("ZebItem"));
        }
        finally
        {
            File.Delete(full);
            using var store = new IndexStore(_fx.DbPath, createNew: false);
            DeltaRefresher.Refresh(store, _fx.Root, new[] { rel });
        }
    }

    [Fact]
    public void SearchTextToolExposesMatchKindAndCounts()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.SearchText("AcmeException"));
        Assert.True(json.GetProperty("preciseCount").GetInt32() > 0);
        Assert.Equal(0, json.GetProperty("partialCount").GetInt32());
        var first = json.GetProperty("hits").EnumerateArray().First();
        Assert.Equal("precise", first.GetProperty("matchKind").GetString());
        // 'matched' is null on precise hits (omitted from JSON by the null-ignoring serializer).
        Assert.False(first.TryGetProperty("matched", out var m) && m.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void RelatedTestsIsHeuristic()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.RelatedTests("Guard"));
        Assert.Equal("heuristic", json.GetProperty("meta").GetProperty("confidence").GetString());
    }

    [Fact]
    public void ImplementationsFallbackIsHeuristic()
    {
        // A name with no semantic target skips the exact path and hits the base-list-name fallback.
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.Implementations(name: "NoSuchTypeXyz123", timeoutMs: 5000));
        Assert.Equal("heuristic", json.GetProperty("meta").GetProperty("confidence").GetString());
    }

    [Fact]
    public void CapabilitiesAdvertiseHeuristicConfidence()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.ServerCapabilities());
        // confidenceModel is an object mapping each tier to its meaning (r2o steering).
        var model = json.GetProperty("confidenceModel");
        Assert.False(string.IsNullOrEmpty(model.GetProperty("heuristic").GetString()));
        Assert.Contains("not compiler-verified", model.GetProperty("indexed").GetString());
    }

    // Deploy-verifiability (field feedback: an agent could not confirm a deploy because the version
    // was a hardcoded literal and no build identity was surfaced). A caller must be able to tell WHICH
    // build is running: version is sourced from BuildInfo, build.commit round-trips the git stamp,
    // indexSchema matches the builder.
    [Fact]
    public void CapabilitiesStampBuildIdentityForDeployVerification()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.ServerCapabilities());
        Assert.Equal(BuildInfo.Version, json.GetProperty("version").GetString());
        var build = json.GetProperty("build");
        Assert.Equal(BuildInfo.Version, build.GetProperty("version").GetString());
        string commit = build.GetProperty("commit").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(commit)); // a SHA when built in a repo, else "unknown"
        Assert.Equal(BuildInfo.Commit, commit);           // round-trips the build-time stamp
        Assert.Equal(IndexBuilder.SchemaVersion, build.GetProperty("indexSchema").GetString());
    }

    // The features manifest lets a caller CONFIRM a capability without triggering its silent-when-clean
    // response fields — the exact verification the field agent couldn't do from a bare response.
    [Fact]
    public void CapabilitiesFeatureManifestLetsCallerConfirmCapabilities()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.ServerCapabilities());
        var ids = json.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains("compiled-awareness", ids);
        Assert.Contains("implementer-completeness", ids);
        Assert.Contains("hierarchy-ranking", ids);
    }

    // The build commit comes from the SDK's "<version>+<sha>" AssemblyInformationalVersion. Pin the
    // parse — in particular the "unknown" fallback for a git-less build (no +sha), the exact scenario
    // a review flagged: the stamp must degrade to "unknown", never a partial/garbage commit.
    [Theory]
    [InlineData("1.0.0+868bf8c88be235d377159b7d84b96997a9c1fefc", "868bf8c88be2")]
    [InlineData("0.2.0+abc123", "abc123")]
    [InlineData("1.0.0", "unknown")]  // git-less build: SDK appends no +sha
    [InlineData("1.0.0+", "unknown")] // malformed suffix
    [InlineData(null, "unknown")]
    public void BuildInfoParsesCommitOrFallsBackToUnknown(string? informationalVersion, string expected)
        => Assert.Equal(expected, BuildInfo.ParseCommit(informationalVersion));
}
