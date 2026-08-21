using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Generic arity is already persisted by schema 14. These tests pin the narrow tool-surface
/// correction: implementations/type_hierarchy must select that existing identity instead of
/// merging same-simple-name generic families.
/// </summary>
public class Batch47GenericArityTests
{
    [Fact]
    public void BareNameRefusesMixedAritiesForBothTools()
    {
        using var fixture = new GenericArityFixture();

        foreach (string json in new[]
                 {
                     // n7ly sweep: retry toward the DELIBERATE refusal, not a transient degrade
                     SemanticRetry.ParseWithRetry(() => fixture.Tools.Implementations(
                             name: "ICache", maxProjects: 0, timeoutMs: 120_000),
                         j => j.TryGetProperty("error", out var e1) && e1.GetString() == "symbol_ambiguous",
                         "error == symbol_ambiguous").GetRawText(),
                     SemanticRetry.ParseWithRetry(() => fixture.Tools.TypeHierarchy(
                             name: "ICache", maxProjects: 0, timeoutMs: 120_000),
                         j => j.TryGetProperty("error", out var e2) && e2.GetString() == "symbol_ambiguous",
                         "error == symbol_ambiguous").GetRawText(),
                 })
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement response = document.RootElement;
            Assert.Equal("symbol_ambiguous", response.GetProperty("error").GetString());
            Assert.Equal(new[] { 0, 1, 2 }, response.GetProperty("candidates")
                .EnumerateArray()
                .Select(candidate => candidate.GetProperty("arity").GetInt32())
                .OrderBy(arity => arity));
            Assert.All(response.GetProperty("candidates").EnumerateArray(), candidate =>
                Assert.StartsWith("idx:", candidate.GetProperty("symbolId").GetString()));
        }
    }

    [Fact]
    public void ExplicitArityKeepsImplementationAndHierarchyFamiliesDisjoint()
    {
        using var fixture = new GenericArityFixture();
        Assert.True(fixture.Semantic.FrameworkRefsAvailable);

        foreach ((int arity, string target, string implementation) in new[]
                 {
                     (0, "T:Arity.ICache", "T:Arity.PlainCache"),
                     (1, "T:Arity.ICache`1", "T:Arity.OneCache`1"),
                     (2, "T:Arity.ICache`2", "T:Arity.TwoCache`2"),
                 })
        {
            using JsonDocument implementations = JsonDocument.Parse(SemanticRetry.ParseExactWithRetry( // n7ly sweep
                () => fixture.Tools.Implementations(
                    name: "ICache", arity: arity, maxProjects: 0, timeoutMs: 120_000)).GetRawText());
            AssertExactFamily(
                implementations.RootElement, "implementations", target, implementation);

            using JsonDocument hierarchy = JsonDocument.Parse(SemanticRetry.ParseExactWithRetry( // n7ly sweep
                () => fixture.Tools.TypeHierarchy(
                    name: "ICache", arity: arity, maxProjects: 0, timeoutMs: 120_000)).GetRawText());
            AssertExactFamily(
                hierarchy.RootElement, "derivedOrImplementing", target, implementation);
        }
    }

    [Fact]
    public void SearchSymbolArityAndHandlePinTheSameGenericFamily()
    {
        using var fixture = new GenericArityFixture();

        using JsonDocument search = JsonDocument.Parse(fixture.Tools.SearchSymbol(
            query: "ICache", kinds: "interface", match: "exact", includeGenerated: true));
        JsonElement[] hits = search.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, hits
            .Select(hit => hit.GetProperty("arity").GetInt32())
            .OrderBy(arity => arity));

        Assert.True(fixture.Semantic.FrameworkRefsAvailable);
        string handle = hits.Single(hit => hit.GetProperty("arity").GetInt32() == 1)
            .GetProperty("symbolId").GetString()!;

        using JsonDocument implementations = JsonDocument.Parse(SemanticRetry.ParseExactWithRetry( // n7ly sweep
            () => fixture.Tools.Implementations(
                symbolId: handle, maxProjects: 0, timeoutMs: 120_000)).GetRawText());
        AssertExactFamily(implementations.RootElement, "implementations",
            "T:Arity.ICache`1", "T:Arity.OneCache`1");

        using JsonDocument hierarchy = JsonDocument.Parse(SemanticRetry.ParseExactWithRetry( // n7ly sweep
            () => fixture.Tools.TypeHierarchy(
                symbolId: handle, maxProjects: 0, timeoutMs: 120_000)).GetRawText());
        AssertExactFamily(hierarchy.RootElement, "derivedOrImplementing",
            "T:Arity.ICache`1", "T:Arity.OneCache`1");
    }

    [Fact]
    public void IndexedBaseListFallbackMatchesSimpleNameAndArity()
    {
        using var fixture = new GenericArityFixture();
        using IndexQueries queries = fixture.OpenQueries();

        Assert.Equal(new[] { "PlainCache" }, queries
            .ImplementationCandidates("ICache", 50, targetArity: 0)
            .Select(hit => hit.Name));
        Assert.Equal(new[] { "OneCache" }, queries
            .ImplementationCandidates("ICache", 50, targetArity: 1)
            .Select(hit => hit.Name));
        Assert.Equal(new[] { "TwoCache" }, queries
            .ImplementationCandidates("ICache", 50, targetArity: 2)
                .Select(hit => hit.Name));
    }

    [Fact]
    public void ArityFilterPagesPastEarlierWrongArityCandidates()
    {
        using var fixture = new GenericArityFixture();
        using IndexQueries queries = fixture.OpenQueries();

        SymbolHit hit = Assert.Single(queries
            .ImplementationCandidates("ILate", 1, targetArity: 1));
        Assert.Equal("ZLate", hit.Name);
    }

    [Fact]
    public void DeclarationPositionCarriesArityIntoHierarchyFallback()
    {
        using var fixture = new GenericArityFixture();

        using JsonDocument hierarchy = JsonDocument.Parse(fixture.Tools.TypeHierarchy(
            name: "IFallback", path: "Orphan.cs", line: 2,
            maxProjects: 0, timeoutMs: 120_000));
        JsonElement response = hierarchy.RootElement;

        Assert.False(response.TryGetProperty("error", out _));
        Assert.Equal("heuristic", response.GetProperty("derivedConfidence").GetString());
        JsonElement result = Assert.Single(
            response.GetProperty("derivedOrImplementing").EnumerateArray());
        Assert.Equal("RightFallback", result.GetProperty("name").GetString());
    }

    [Fact]
    public void MixedArityUsagePositionWithholdsNameOnlyFallback()
    {
        using var fixture = new GenericArityFixture();

        using JsonDocument hierarchy = JsonDocument.Parse(SemanticRetry.ParseWithRetry( // n7ly sweep: the DELIBERATE withhold = unavailable AND no candidates
            () => fixture.Tools.TypeHierarchy(
                name: "IFallback", path: "Orphan.cs", line: 6, maxProjects: 0, timeoutMs: 120_000),
            j => j.TryGetProperty("error", out var e) && e.GetString() == "semantic_unavailable"
                 && !j.TryGetProperty("derivedOrImplementing", out _),
            "semantic_unavailable with candidates withheld").GetRawText());
        JsonElement response = hierarchy.RootElement;

        Assert.Equal("semantic_unavailable", response.GetProperty("error").GetString());
        Assert.False(response.TryGetProperty("derivedOrImplementing", out _));
    }

    private static void AssertExactFamily(
        JsonElement response,
        string resultProperty,
        string expectedTarget,
        string expectedImplementation)
    {
        Assert.False(response.TryGetProperty("error", out _));
        Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
        Assert.Equal(expectedTarget,
            response.GetProperty("symbol").GetProperty("documentationCommentId").GetString());
        JsonElement result = Assert.Single(response.GetProperty(resultProperty).EnumerateArray());
        JsonElement symbol = resultProperty == "implementations"
            ? result.GetProperty("symbol")
            : result;
        Assert.Equal(expectedImplementation,
            symbol.GetProperty("documentationCommentId").GetString());
    }

    private sealed class GenericArityFixture : IDisposable
    {
        private readonly string _root;
        private readonly IndexManager _manager;

        internal SemanticService Semantic { get; }
        internal NavigationTools Tools { get; }

        internal GenericArityFixture()
        {
            _root = Directory.CreateTempSubdirectory("codenav-generic-arity").FullName;
            string projectDirectory = Path.Combine(_root, "Arity");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Arity.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(projectDirectory, "Types.cs"),
                """
                namespace Arity;

                public interface ICache { } public interface ICache<T> { }
                public interface ICache<TKey, TValue> { }

                public sealed class PlainCache : ICache { }
                public sealed class OneCache<T> : ICache<T> { }
                public sealed class TwoCache<TKey, TValue> : ICache<TKey, TValue> { }
                """);
            string wrongArityCandidates = string.Join(Environment.NewLine,
                Enumerable.Range(0, 70).Select(index =>
                    $"public sealed class A{index:D2}Late : ILate {{ }}"));
            File.WriteAllText(Path.Combine(projectDirectory, "Late.cs"),
                $$"""
                namespace Arity;
                public interface ILate { }
                public interface ILate<T> { }
                {{wrongArityCandidates}}
                public sealed class ZLate<T> : ILate<T> { }
                """);
            File.WriteAllText(Path.Combine(_root, "Orphan.cs"),
                """
                namespace Orphan;
                public interface IFallback<T> { }
                public interface IFallback { }
                public sealed class WrongFallback : IFallback { }
                public sealed class RightFallback<T> : IFallback<T> { }
                public sealed class Usage { public IFallback<int>? Value { get; set; } }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(_root);
            IndexBuilder.Build(_root, dbPath);
            _manager = new IndexManager(_root, dbPath);
            _manager.Start();
            Assert.True(WaitUntil(() => _manager.Health().State == "ready", 30_000),
                _manager.Health().Error);
            Semantic = new SemanticService(_manager);
            Tools = new NavigationTools(_manager, Semantic);
        }

        internal IndexQueries OpenQueries() => _manager.OpenQueries();

        public void Dispose()
        {
            Semantic.Dispose();
            _manager.Dispose();
            TestWorkspaceCleanup.ClearIndexPools(_root);
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(50);
            }
            return condition();
        }
    }
}
