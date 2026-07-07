using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public class SemanticTests : IClassFixture<IndexFixture>, IDisposable
{
    private readonly IndexFixture _fx;
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;

    public SemanticTests(IndexFixture fx)
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

    [Fact]
    public void FrameworkReferenceAssembliesAreFound()
    {
        Assert.True(_semantic.FrameworkRefsAvailable,
            "net472 reference assemblies not found — semantic tests need a targeting pack, " +
            "the NuGet reference-assemblies package, or an installed .NET Framework runtime");
    }

    [Fact]
    public async Task DefinitionFromUsagePositionResolvesExactly()
    {
        // Find a real Guard.NotNull call site (raw substring match excludes the declaration).
        using var q = _manager.OpenQueries();
        var hit = q.SearchText("Guard.NotNull", 5).First();
        int column = hit.LineText.IndexOf("NotNull", StringComparison.Ordinal) + 1;
        // LineText is trimmed for display — recompute column against the real line.
        string content = q.ContentByPath(hit.FilePath)!;
        string realLine = content.Split('\n')[hit.Line - 1];
        column = realLine.IndexOf("NotNull", StringComparison.Ordinal) + 1;

        var (decl, reason) = await _semantic.DefinitionAsync(hit.FilePath, hit.Line, column, null, 30000);

        Assert.True(decl is not null, $"semantic definition failed: {reason}");
        Assert.Equal("M:Acme.Platform.Common.Guard.NotNull(System.Object,System.String)", decl!.DocumentationCommentId);
        Assert.Single(decl.Declarations);
        Assert.EndsWith("Guard.cs", decl.Declarations[0].Path);
    }

    [Fact]
    public async Task ReferencesForInterfaceAreCompilerExactAndCrossProject()
    {
        // Pick an interface that has an implementation (application impl classes reference it).
        using var q = _manager.OpenQueries();
        var iface = q.SearchSymbols("IClock", "exact", new[] { "interface" }, 1).Single();

        var (result, reason) = await _semantic.ReferencesAsync(
            iface.FilePath, iface.StartLine, null, "IClock", maxProjects: 30, samplesPerGroup: 2, timeoutMs: 60000);

        Assert.True(result is not null, $"semantic references failed: {reason}");
        Assert.True(result!.TotalLocations > 0, "expected at least one exact reference to IClock");
        Assert.Equal("T:Acme.Platform.Common.IClock", result.Symbol.DocumentationCommentId);
        // SystemClock implements IClock in the same project; consumers may exist elsewhere.
        Assert.Contains(result.Groups, g => g.Project == "Acme.Platform.Common");
        Assert.All(result.Groups.SelectMany(g => g.Samples), s => Assert.Contains("IClock", s.LineText));
    }

    [Fact]
    public async Task ImplementationsFindConcreteClasses()
    {
        using var q = _manager.OpenQueries();
        var iface = q.SearchSymbols("IClock", "exact", new[] { "interface" }, 1).Single();

        var (result, reason) = await _semantic.ImplementationsAsync(
            iface.FilePath, iface.StartLine, null, "IClock", maxProjects: 30, timeoutMs: 60000);

        Assert.True(result is not null, $"semantic implementations failed: {reason}");
        Assert.Contains(result!.Implementations, i => i.Declaration.SymbolDisplay.EndsWith("SystemClock"));

        // Hierarchy ranking: concrete (instantiable) leaves are ordered before abstract scaffolding.
        var abstractFlags = result.Implementations.Select(i => i.Declaration.IsAbstract).ToList();
        Assert.Equal(abstractFlags.OrderBy(a => a).ToList(), abstractFlags);
        var systemClock = result.Implementations.First(i => i.Declaration.SymbolDisplay.EndsWith("SystemClock"));
        Assert.False(systemClock.Declaration.IsAbstract);
    }

    [Fact]
    public void ImplementationsToolRanksConcreteFirstAndFlagsLikelyTarget()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = JsonDocument.Parse(tools.Implementations(name: "IClock", timeoutMs: 60000)).RootElement;
        // Ranking is an exact-path feature; the heuristic fallback is unranked, so only assert there.
        if (json.GetProperty("meta").GetProperty("confidence").GetString() != "exact") return;

        var impls = json.GetProperty("implementations").EnumerateArray().ToList();
        Assert.NotEmpty(impls);
        // Concrete-first: no concrete implementation may appear after an abstract one.
        bool sawAbstract = false;
        foreach (var i in impls)
        {
            bool isAbstract = i.TryGetProperty("isAbstract", out var a) && a.GetBoolean();
            if (isAbstract) sawAbstract = true;
            else Assert.False(sawAbstract, "a concrete implementation appeared after an abstract one");
        }
        // The fixture's IClock has a single concrete implementation (SystemClock) → flagged as likely.
        if (json.GetProperty("concreteCount").GetInt32() == 1)
        {
            Assert.EndsWith("SystemClock", json.GetProperty("likelyImplementation").GetString());
        }
    }

    // Coverage fix: an interface the index syntactically knows is implemented must never come back
    // empty — either semantic covered the implementers (seeded), or the tool falls back to the
    // index base-list implementers rather than returning an empty list.
    [Fact]
    public void ImplementationsDoesNotReturnEmptyWhenIndexKnowsImplementers()
    {
        using var q = _manager.OpenQueries();
        var iface = q.SearchSymbols("I", "prefix", new[] { "interface" }, 100)
            .FirstOrDefault(i => q.ImplementationCandidates(i.Name, 5).Count > 0);
        Assert.True(iface is not null, "fixture has no interface with base-list implementers to exercise the case");

        var tools = new NavigationTools(_manager, _semantic);
        var json = JsonDocument.Parse(tools.Implementations(name: iface!.Name, timeoutMs: 60000)).RootElement;
        Assert.True(json.GetProperty("implementations").GetArrayLength() > 0,
            $"implementations({iface.Name}) was empty despite indexed base-list implementers");
    }

    // Guards the implementations empty-name fallback (position mode with no resolvable name): an
    // empty name must not collapse the base-list LIKE into the '%: %' catch-all matching every type.
    [Fact]
    public void ImplementationCandidatesRejectsEmptyName()
    {
        using var q = _manager.OpenQueries();
        Assert.Empty(q.ImplementationCandidates("", 50));
        Assert.NotEmpty(q.ImplementationCandidates("IClock", 50)); // a real interface still matches
    }

    [Fact]
    public void ReferencesToolFallsBackToIndexedWhenAskedFor()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = JsonDocument.Parse(tools.References(name: "Guard", mode: "indexed", maxFiles: 100)).RootElement;
        Assert.Equal("indexed", json.GetProperty("meta").GetProperty("confidence").GetString());
        Assert.True(json.GetProperty("totalCandidates").GetInt32() > 0);
    }

    [Fact]
    public void DefinitionToolSemanticPathProducesExactConfidence()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = JsonDocument.Parse(tools.Definition(name: "Guard", timeoutMs: 60000)).RootElement;
        Assert.Equal("exact", json.GetProperty("meta").GetProperty("confidence").GetString());
        Assert.Equal("T:Acme.Platform.Common.Guard",
            json.GetProperty("symbol").GetProperty("documentationCommentId").GetString());
    }
}
