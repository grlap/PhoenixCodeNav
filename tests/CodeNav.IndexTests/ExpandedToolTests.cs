using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

[Collection(SharedIndexCollection.Name)]
public class ExpandedToolTests
{
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;
    private readonly NavigationTools _tools;

    public ExpandedToolTests(SharedIndexFixture fx)
    {
        _manager = fx.SharedManager;
        _semantic = fx.SharedSemantic;
        _tools = fx.SharedTools;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void CallersFindsGuardNotNullCallers()
    {
        if (!_semantic.FrameworkRefsAvailable) return; // review C2: deterministic env skip
        var json = SemanticRetry.ParseExactWithRetry(() => _tools.Callers(name: "NotNull", maxProjects: 10, timeoutMs: 60000)); // n7ly/kmoj: ride out transient degrades
        Assert.True(json.TryGetProperty("callers", out var callers), $"no callers property: {json}");
        Assert.True(callers.GetArrayLength() > 0, "expected at least one caller of Guard.NotNull");
        Assert.Equal("exact", json.GetProperty("meta").GetProperty("confidence").GetString());
    }

    [Fact]
    public void CalleesResolvesBodyInvocations()
    {
        // Application service ctors call Guard.NotNull; their methods call dependencies.
        using var q = _manager.OpenQueries();
        var ctor = q.SearchSymbols("SystemClock", "exact", new[] { "class" }, 1).Single();
        var method = q.Outline(ctor.FilePath).First(s => s.Kind == "method" && s.Name == "GetUtcNow");

        if (!_semantic.FrameworkRefsAvailable) return; // review C2: deterministic env skip
        var json = SemanticRetry.ParseExactWithRetry( // n7ly sweep: retries transient degrades
            () => _tools.Callees(path: ctor.FilePath, line: method.StartLine, timeoutMs: 60000));
        Assert.True(json.TryGetProperty("callees", out _), $"unexpected: {json}");
    }

    [Fact]
    public void TypeHierarchyShowsInterfaceAndImplementation()
    {
        if (!_semantic.FrameworkRefsAvailable) return; // review C2: deterministic env skip
        var json = SemanticRetry.ParseExactWithRetry( // n7ly sweep: retries transient degrades
            () => _tools.TypeHierarchy(name: "IClock", maxProjects: 10, timeoutMs: 60000));
        Assert.True(json.TryGetProperty("derivedOrImplementing", out var impls), $"unexpected: {json}");
        Assert.Contains(impls.EnumerateArray(),
            i => i.GetProperty("display").GetString()!.EndsWith("SystemClock"));
    }

    [Fact]
    public void RelatedTestsRanksNameMatchesFirst()
    {
        // Find any production class that has a test class ({Name}Tests exists).
        using var q = _manager.OpenQueries();
        var testClass = q.SearchSymbols("Tests", "substring", new[] { "class" }, 20)
            .First(s => s.Name.EndsWith("Tests"));
        string target = testClass.Name[..^"Tests".Length];

        var json = Parse(_tools.RelatedTests(target));
        var groups = json.GetProperty("testGroups").EnumerateArray().ToList();
        Assert.NotEmpty(groups);
        Assert.Equal("references symbol name", groups[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void DependencyPathExplainsTransitiveDependency()
    {
        using var q = _manager.OpenQueries();
        // Any Api project depends (directly or transitively) on Platform.Common.
        var api = q.ProjectGraph("Acme.Platform.Common", 10, "upstream")
            .Select(e => e.FromProject).First(p => p.EndsWith(".Api"));

        var json = Parse(_tools.DependencyPath(api, "Acme.Platform.Common"));
        Assert.True(json.GetProperty("found").GetBoolean());
        var path0 = json.GetProperty("paths").EnumerateArray().First().GetString()!;
        Assert.StartsWith(api, path0);
        Assert.EndsWith("Acme.Platform.Common", path0);
    }

    [Fact]
    public void ConfigLookupFindsAppSettingsKeys()
    {
        var json = Parse(_tools.ConfigLookup("ConnectionStringName"));
        Assert.True(json.GetProperty("hits").GetArrayLength() > 0);
        Assert.All(json.GetProperty("hits").EnumerateArray(),
            h => Assert.Contains("appsettings", h.GetProperty("path").GetString()));
    }

    [Fact]
    public void BatchOutlineReturnsMultipleCommaSeparatedFiles()
    {
        using var q = _manager.OpenQueries();
        var files = q.FindFiles("*.cs", 3).Select(f => f.Path).ToList();
        var json = Parse(_tools.BatchOutline(string.Join(",", files)));
        Assert.Equal(files.Count, json.GetProperty("outlines").GetArrayLength());
    }

    [Fact]
    public void BatchOutlineAcceptsSerializedJsonArrayWithoutManglingPaths()
    {
        using var q = _manager.OpenQueries();
        var files = q.FindFiles("*.cs", 3).Select(f => f.Path).ToList();

        var json = Parse(_tools.BatchOutline(JsonSerializer.Serialize(files)));
        var outlines = json.GetProperty("outlines").EnumerateArray().ToList();

        Assert.Equal(files.Count, outlines.Count);
        Assert.Equal(files,
            outlines.Select(outline => outline.GetProperty("path").GetString()!).ToList());
        Assert.All(outlines, outline => Assert.False(outline.TryGetProperty("error", out _)));
    }

    [Fact]
    public void BatchOutlinePreservesExactJsonArrayPathText()
    {
        string[] paths =
        {
            " leading.cs",
            "leading.cs",
            "trailing.cs ",
            "\tTabbed.cs",
            "comma,name.cs",
            "Unicode/文件.cs",
        };

        var json = Parse(_tools.BatchOutline(JsonSerializer.Serialize(paths)));
        var outlines = json.GetProperty("outlines").EnumerateArray().ToList();

        Assert.Equal(paths,
            outlines.Select(outline => outline.GetProperty("path").GetString()!).ToArray());
        Assert.All(outlines,
            outline => Assert.Equal("file_not_indexed",
                outline.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData("[\"first.cs\",]")]
    [InlineData("[\"first.cs\",42]")]
    [InlineData("\"first.cs,second.cs\"")]
    public void BatchOutlineRejectsJsonShapedInvalidInputBeforePathLookup(string paths)
    {
        var json = Parse(_tools.BatchOutline(paths));

        Assert.Equal("bad_request", json.GetProperty("error").GetString());
        Assert.False(json.TryGetProperty("path", out _));
        Assert.False(json.TryGetProperty("outlines", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    [InlineData("first.cs,")]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    public void BatchOutlineRejectsBlankPathInputs(string paths)
    {
        var json = Parse(_tools.BatchOutline(paths));

        Assert.Equal("bad_request", json.GetProperty("error").GetString());
        Assert.False(json.TryGetProperty("outlines", out _));
    }

    [Fact]
    public void BatchOutlineAcceptsExactlyTwelvePaths()
    {
        using var q = _manager.OpenQueries();
        var files = q.FindFiles("*.cs", 12).Select(f => f.Path).ToList();
        Assert.Equal(12, files.Count);

        var json = Parse(_tools.BatchOutline(JsonSerializer.Serialize(files)));

        Assert.Equal(12, json.GetProperty("outlines").GetArrayLength());
    }

    [Fact]
    public void BatchOutlineRejectsMoreThanTwelvePaths()
    {
        string[] paths = Enumerable.Range(1, 13).Select(i => $"File{i}.cs").ToArray();

        var jsonArray = Parse(_tools.BatchOutline(JsonSerializer.Serialize(paths)));
        var commaSeparated = Parse(_tools.BatchOutline(string.Join(",", paths)));

        Assert.Equal("bad_request", jsonArray.GetProperty("error").GetString());
        Assert.Contains("at most 12", jsonArray.GetProperty("detail").GetString());
        Assert.Equal("bad_request", commaSeparated.GetProperty("error").GetString());
        Assert.Contains("at most 12", commaSeparated.GetProperty("detail").GetString());
    }

    [Fact]
    public void ContextPackBundlesWithinBudget()
    {
        string raw = _tools.ContextPack("Guard", maxBytes: 8192, timeoutMs: 30000);
        Assert.True(raw.Length <= 8192 + 512, $"context pack {raw.Length} bytes exceeds requested budget");
        var json = Parse(raw);
        Assert.Contains("Guard", json.GetProperty("summary").GetString());
        Assert.True(json.GetProperty("references").GetProperty("totalCandidates").GetInt32() > 0);
        Assert.True(json.GetProperty("declarations").GetArrayLength() > 0);
    }

    [Fact]
    public void ImpactReportsRisksDeterministically()
    {
        var json = Parse(_tools.Impact("Guard"));
        Assert.True(json.GetProperty("publicApi").GetBoolean());
        Assert.True(json.GetProperty("transitiveDependentProjects").GetInt32() > 0);
        Assert.True(json.GetProperty("risks").GetArrayLength() > 0);
        Assert.True(json.GetProperty("references").GetProperty("production").GetInt32() > 0);
    }
}
