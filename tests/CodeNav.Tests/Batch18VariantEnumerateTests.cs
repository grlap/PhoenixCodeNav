using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// 2sj: token-variant didYouMean — an agent searched 'Mode4', the code says "Mode 4", got a correct 0
/// and fell back to grep. On a total dead end the split (Mode4 -> "Mode 4") and joined ("Mode 4" ->
/// Mode4) forms are probed and SUGGESTED (didYouMean), never silently substituted.
/// 0to: search_symbol empty-name enumeration — kinds+namespace with no name silently returned [];
/// now an empty name within a namespace/pathGlob scope enumerates it, and without a scope it is an
/// explicit bad_request instead of a silent empty.
/// </summary>
public class Batch18VariantTests
{
    // Pure variant generation: split at case/digit boundaries; join multi-token queries.
    [Theory]
    [InlineData("Mode4", "Mode 4")]
    [InlineData("AsyncAPI", "Async API")]
    [InlineData("IPartnerFramework2", "IPartner Framework 2")] // lower->Upper and letter->digit both split
    [InlineData("plain", null)]
    [InlineData("Mode 4", null)] // nothing to split inside the tokens
    public void SplitVariantSplitsAtCaseAndDigitBoundaries(string query, string? expected)
        => Assert.Equal(expected, QueryVariants.SplitVariant(query));

    [Theory]
    [InlineData("Mode 4", "Mode4")]
    [InlineData("Mode\t4", "Mode4")] // tabs from pasted code/log fragments (review finding)
    [InlineData("a b c", "abc")]
    [InlineData("single", null)]
    public void JoinVariantJoinsMultiTokenQueries(string query, string? expected)
        => Assert.Equal(expected, QueryVariants.JoinVariant(query));

    [Fact]
    public void DeadEndSuggestsTokenVariants()
    {
        string root = Directory.CreateTempSubdirectory("codenav-variant").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 2, seed: 13);
            // The field case: the code says "Zmode 4" (space); the agent will search "Zmode4".
            File.WriteAllText(Path.Combine(root, "ModeDoc.cs"),
                "namespace M { class MM {\n// Zmode 4 comment\n} }");
            // The join direction: the code says "ZzTok9" (one token); the agent searches "Zz Tok9".
            File.WriteAllText(Path.Combine(root, "JoinDoc.cs"),
                "namespace J { class JJ {\n// ZzTok9 marker\n} }");
            // Review finding: the join direction was starved when gated to TOTAL dead ends — common
            // tokens co-occur across lines, landing in the partial-leads branch. Scatter.cs makes the
            // query a partial lead; Joined.cs holds the joined form the variant must still surface.
            File.WriteAllText(Path.Combine(root, "Scatter.cs"),
                "namespace S { class SS {\n// ZqAlpha\n// ZqBeta9\n} }");
            File.WriteAllText(Path.Combine(root, "Joined.cs"),
                "namespace S { class SJ {\n// ZqAlphaZqBeta9 combo\n} }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            var manager = new IndexManager(root, dbPath);
            try
            {
                manager.Start();
                for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
                Assert.True(manager.IsQueryable);
                var tools = new NavigationTools(manager, new SemanticService(manager));

                // Split: "Zmode4" -> 0 hits, but "Zmode 4" exists -> suggested, not substituted.
                var split = JsonDocument.Parse(tools.SearchText("Zmode4")).RootElement;
                Assert.Equal(0, split.GetProperty("preciseCount").GetInt32());
                Assert.Equal(0, split.GetProperty("hits").GetArrayLength()); // no silent substitution
                var dym = split.GetProperty("didYouMean");
                Assert.Equal("Zmode 4", dym.GetProperty("query").GetString());
                Assert.Contains("ModeDoc.cs", dym.GetProperty("samplePaths").EnumerateArray().Select(p => p.GetString()));
                Assert.Contains("Zmode 4", split.GetProperty("note").GetString());

                // Join: "Zz Tok9" -> 0 hits, but "ZzTok9" exists.
                var join = JsonDocument.Parse(tools.SearchText("Zz Tok9")).RootElement;
                Assert.Equal(0, join.GetProperty("preciseCount").GetInt32());
                Assert.Equal("ZzTok9", join.GetProperty("didYouMean").GetProperty("query").GetString());

                // A dead end with no viable variant keeps the honest absent note, no didYouMean.
                var absent = JsonDocument.Parse(tools.SearchText("ZzNothingHere")).RootElement;
                Assert.False(absent.TryGetProperty("didYouMean", out _));
                Assert.Contains("No file contains", absent.GetProperty("note").GetString());

                // Variant fires from the partial-leads branch too (review: gating it to total dead
                // ends starved the join direction — tokens co-occur across lines in any real repo).
                var pl = JsonDocument.Parse(tools.SearchText("ZqAlpha ZqBeta9")).RootElement;
                Assert.Equal(0, pl.GetProperty("preciseCount").GetInt32());
                Assert.True(pl.GetProperty("partialCount").GetInt32() > 0, "expected the partial-leads branch");
                Assert.Equal("ZqAlphaZqBeta9", pl.GetProperty("didYouMean").GetProperty("query").GetString());
                Assert.Contains("Also:", pl.GetProperty("note").GetString()); // appended, not replacing the leads note
            }
            finally { manager.Dispose(); }
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }
}

public class Batch18EnumerateTests : IClassFixture<IndexFixture>, IDisposable
{
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;
    private readonly NavigationTools _tools;

    public Batch18EnumerateTests(IndexFixture fx)
    {
        _manager = new IndexManager(fx.Root, fx.DbPath);
        _manager.Start();
        for (int i = 0; i < 100 && !_manager.IsQueryable; i++) Thread.Sleep(50);
        _semantic = new SemanticService(_manager);
        _tools = new NavigationTools(_manager, _semantic);
    }

    public void Dispose()
    {
        _semantic.Dispose();
        _manager.Dispose();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void EmptyNameWithNamespaceEnumeratesIt()
    {
        var r = Parse(_tools.SearchSymbol("", @namespace: "Acme.Platform.Common"));
        Assert.Equal("enumerate", r.GetProperty("matchMode").GetString());
        Assert.True(r.GetProperty("symbols").GetArrayLength() > 0, "namespace enumeration returned nothing");
        // '*' is an alias, and kinds filtering applies.
        var classes = Parse(_tools.SearchSymbol("*", kinds: "class", @namespace: "Acme.Platform.Common"));
        Assert.Equal("enumerate", classes.GetProperty("matchMode").GetString());
        Assert.All(classes.GetProperty("symbols").EnumerateArray(),
            s => Assert.Equal("class", s.GetProperty("kind").GetString()));
    }

    [Fact]
    public void EmptyNameWithoutScopeIsAnExplicitError()
    {
        var r = Parse(_tools.SearchSymbol(""));
        Assert.Equal("bad_request", r.GetProperty("error").GetString());
        Assert.Contains("namespace", r.GetProperty("detail").GetString());
    }

    [Fact]
    public void EnumerationPagesWithoutDuplicates()
    {
        var p1 = Parse(_tools.SearchSymbol("", @namespace: "Acme.Platform.Common", limit: 2));
        if (!p1.TryGetProperty("nextCursor", out var nc) || nc.ValueKind == JsonValueKind.Null) return; // tiny fixture
        static string Key(JsonElement s) =>
            $"{s.GetProperty("name").GetString()}|{s.GetProperty("path").GetString()}|{s.GetProperty("startLine").GetInt32()}";
        var p1Keys = p1.GetProperty("symbols").EnumerateArray().Select(Key).ToHashSet();
        var p2 = Parse(_tools.SearchSymbol("", @namespace: "Acme.Platform.Common", limit: 2, cursor: nc.GetString()));
        Assert.Equal("enumerate", p2.GetProperty("matchMode").GetString());
        var p2Keys = p2.GetProperty("symbols").EnumerateArray().Select(Key).ToList();
        Assert.NotEmpty(p2Keys); // page 2 of enumeration was non-empty
        Assert.All(p2Keys, k => Assert.DoesNotContain(k, p1Keys)); // and shares no declaration with page 1
    }
}
