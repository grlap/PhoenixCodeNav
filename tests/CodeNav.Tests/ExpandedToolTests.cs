using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public class ExpandedToolTests : IClassFixture<IndexFixture>, IDisposable
{
    private readonly IndexFixture _fx;
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;
    private readonly NavigationTools _tools;

    public ExpandedToolTests(IndexFixture fx)
    {
        _fx = fx;
        _manager = new IndexManager(_fx.Root, _fx.DbPath);
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
    public void BatchOutlineReturnsMultipleFiles()
    {
        using var q = _manager.OpenQueries();
        var files = q.FindFiles("*.cs", 3).Select(f => f.Path).ToList();
        var json = Parse(_tools.BatchOutline(string.Join(",", files)));
        Assert.Equal(files.Count, json.GetProperty("outlines").GetArrayLength());
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
