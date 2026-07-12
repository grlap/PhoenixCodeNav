using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// P0 field regression: implementations(name) must not collapse C# type identity to a simple
/// name. Generic arity and namespace are semantic identity, not display decoration.
/// </summary>
public class Batch47ImplementationIdentityTests
{
    [Fact]
    public void BareNameRejectsGenericAndNamespaceAmbiguity()
    {
        using var fixture = new ImplementationIdentityFixture();

        using JsonDocument document = JsonDocument.Parse(fixture.Tools.Implementations(
            name: "ICache", maxProjects: 0, timeoutMs: 120_000));
        JsonElement response = document.RootElement;

        Assert.Equal("symbol_ambiguous", response.GetProperty("error").GetString());
        Assert.Equal("ICache", response.GetProperty("name").GetString());
        JsonElement[] candidates = response.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(3, candidates.Length);
        Assert.Contains(candidates, candidate =>
            candidate.GetProperty("ns").GetString() == "Alpha" &&
            candidate.GetProperty("arity").GetInt32() == 0);
        Assert.Contains(candidates, candidate =>
            candidate.GetProperty("ns").GetString() == "Alpha" &&
            candidate.GetProperty("arity").GetInt32() == 2);
        Assert.Contains(candidates, candidate =>
            candidate.GetProperty("ns").GetString() == "Beta" &&
            candidate.GetProperty("arity").GetInt32() == 0);
        Assert.All(candidates, candidate => Assert.StartsWith("idx:",
            candidate.GetProperty("symbolId").GetString(), StringComparison.Ordinal));
        Assert.False(response.TryGetProperty("implementations", out _));
        Assert.Equal("indexed", response.GetProperty("meta").GetProperty("confidence").GetString());
    }

    [Fact]
    public void ExactHandleAndPositionKeepImplementationFamiliesDisjoint()
    {
        using var fixture = new ImplementationIdentityFixture();
        if (!fixture.Semantic.FrameworkRefsAvailable) return;

        using JsonDocument ambiguousDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            name: "ICache", maxProjects: 0, timeoutMs: 120_000));
        string genericHandle = ambiguousDocument.RootElement.GetProperty("candidates")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("ns").GetString() == "Alpha" &&
                                 candidate.GetProperty("arity").GetInt32() == 2)
            .GetProperty("symbolId").GetString()!;

        using JsonDocument genericDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            symbolId: genericHandle, maxProjects: 0, timeoutMs: 120_000));
        AssertExactImplementations(genericDocument.RootElement,
            "T:Alpha.ICache`2", "T:Alpha.GenericCache`2");

        using JsonDocument alphaDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            name: "ICache", path: "Identity/Types.cs", line: 3, column: 22,
            maxProjects: 0, timeoutMs: 120_000));
        AssertExactImplementations(alphaDocument.RootElement,
            "T:Alpha.ICache", "T:Alpha.PlainCache");

        using JsonDocument betaDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            name: "ICache", path: "Identity/Types.cs", line: 10,
            maxProjects: 0, timeoutMs: 120_000));
        AssertExactImplementations(betaDocument.RootElement,
            "T:Beta.ICache", "T:Beta.OtherCache");
    }

    [Fact]
    public void SearchSymbolHandleResolvesImplementations()
    {
        using var fixture = new ImplementationIdentityFixture();
        if (!fixture.Semantic.FrameworkRefsAvailable) return;

        using JsonDocument searchDocument = JsonDocument.Parse(fixture.Tools.SearchSymbol(
            query: "IUsageTarget", kinds: "interface", match: "exact", includeGenerated: true));
        string handle = Assert.Single(searchDocument.RootElement.GetProperty("symbols")
                .EnumerateArray())
            .GetProperty("symbolId").GetString()!;

        using JsonDocument implementationsDocument = JsonDocument.Parse(
            fixture.Tools.Implementations(
                symbolId: handle, maxProjects: 0, timeoutMs: 120_000));
        AssertExactImplementations(implementationsDocument.RootElement,
            "T:Usage.Targets.IUsageTarget", "T:Usage.Targets.UsageImpl");
    }

    [Fact]
    public void ContainingTypeArityProducesDistinctTargets()
    {
        using var fixture = new ImplementationIdentityFixture();

        using JsonDocument ambiguousDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            name: "ITarget", maxProjects: 0, timeoutMs: 120_000));
        JsonElement response = ambiguousDocument.RootElement;
        Assert.Equal("symbol_ambiguous", response.GetProperty("error").GetString());
        JsonElement[] candidates = response.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.Contains(candidates, candidate =>
            candidate.GetProperty("containingTypeIdentity").GetString() == "Outer");
        Assert.Contains(candidates, candidate =>
            candidate.GetProperty("containingTypeIdentity").GetString() == "Outer`1");

        if (!fixture.Semantic.FrameworkRefsAvailable) return;
        string genericHandle = candidates.Single(candidate =>
                candidate.GetProperty("containingTypeIdentity").GetString() == "Outer`1")
            .GetProperty("symbolId").GetString()!;
        using JsonDocument genericDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            symbolId: genericHandle, maxProjects: 0, timeoutMs: 120_000));
        AssertExactImplementations(genericDocument.RootElement,
            "T:Nested.Outer`1.ITarget", "T:Nested.GenericNested`1");
    }

    [Fact]
    public void LinkedDeclarationRequiresAndHonorsOwningProjectIdentity()
    {
        using var fixture = new ImplementationIdentityFixture();

        using JsonDocument positionOnlyDocument = JsonDocument.Parse(
            fixture.Tools.Implementations(
                path: "Shared/Linked.cs", line: 1, maxProjects: 0, timeoutMs: 120_000));
        JsonElement positionOnly = positionOnlyDocument.RootElement;
        Assert.Equal("symbol_ambiguous", positionOnly.GetProperty("error").GetString());
        Assert.Equal(new[] { "OwnerA", "OwnerB" }, positionOnly.GetProperty("candidates")
            .EnumerateArray()
            .Select(candidate => candidate.GetProperty("owningProject").GetString())
            .OrderBy(project => project, StringComparer.Ordinal));

        using JsonDocument ambiguousDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            name: "ILinked", maxProjects: 0, timeoutMs: 120_000));
        JsonElement response = ambiguousDocument.RootElement;
        Assert.Equal("symbol_ambiguous", response.GetProperty("error").GetString());
        JsonElement[] candidates = response.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.Equal(new[] { "OwnerA", "OwnerB" }, candidates
            .Select(candidate => candidate.GetProperty("owningProject").GetString())
            .OrderBy(project => project, StringComparer.Ordinal));

        string ownerBoundHandle = candidates[0].GetProperty("symbolId").GetString()!;
        using JsonDocument crossToolDocument = JsonDocument.Parse(
            fixture.Tools.Definition(symbolId: ownerBoundHandle));
        Assert.Equal("bad_request",
            crossToolDocument.RootElement.GetProperty("error").GetString());

        if (!fixture.Semantic.FrameworkRefsAvailable) return;
        foreach ((string owner, string implementation) in new[]
                 {
                     ("OwnerA", "T:Linked.OwnerAImpl"),
                     ("OwnerB", "T:Linked.OwnerBImpl"),
                 })
        {
            string handle = candidates.Single(candidate =>
                    candidate.GetProperty("owningProject").GetString() == owner)
                .GetProperty("symbolId").GetString()!;
            using JsonDocument exactDocument = JsonDocument.Parse(fixture.Tools.Implementations(
                symbolId: handle, maxProjects: 0, timeoutMs: 120_000));
            Assert.Equal(owner,
                exactDocument.RootElement.GetProperty("symbol").GetProperty("assembly").GetString());
            AssertExactImplementations(exactDocument.RootElement,
                "T:Linked.ILinked", implementation);
        }
    }

    [Fact]
    public void PositionUsageDoesNotPinSameNamedEnclosingType()
    {
        using var fixture = new ImplementationIdentityFixture();
        if (!fixture.Semantic.FrameworkRefsAvailable) return;

        using JsonDocument document = JsonDocument.Parse(fixture.Tools.Implementations(
            name: "IUsageTarget", path: "Identity/Usage.cs", line: 11, column: 41,
            maxProjects: 0, timeoutMs: 120_000));

        AssertExactImplementations(document.RootElement,
            "T:Usage.Targets.IUsageTarget", "T:Usage.Targets.UsageImpl");
    }

    [Fact]
    public void ImplementationHandleRejectsAncestorArityReuseInCurrentIndex()
    {
        using var fixture = new ImplementationIdentityFixture();

        using JsonDocument searchDocument = JsonDocument.Parse(fixture.Tools.SearchSymbol(
            query: "ITarget", kinds: "interface", match: "exact", includeGenerated: true));
        string oldGeneralHandle = searchDocument.RootElement.GetProperty("symbols")
            .EnumerateArray()
            .OrderByDescending(symbol => symbol.GetProperty("startLine").GetInt32())
            .First()
            .GetProperty("symbolId").GetString()!;

        using JsonDocument ambiguousDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            name: "ITarget", maxProjects: 0, timeoutMs: 120_000));
        string oldGenericHandle = ambiguousDocument.RootElement.GetProperty("candidates")
            .EnumerateArray()
            .Single(candidate =>
                candidate.GetProperty("containingTypeIdentity").GetString() == "Outer`1")
            .GetProperty("symbolId").GetString()!;

        fixture.ChangeIndexedAncestorArityForTest();

        using JsonDocument staleDocument = JsonDocument.Parse(fixture.Tools.Implementations(
            symbolId: oldGenericHandle, maxProjects: 0, timeoutMs: 120_000));
        Assert.Equal("stale_handle",
            staleDocument.RootElement.GetProperty("error").GetString());

        using JsonDocument staleGeneralDocument = JsonDocument.Parse(
            fixture.Tools.Definition(symbolId: oldGeneralHandle));
        Assert.Equal("stale_handle",
            staleGeneralDocument.RootElement.GetProperty("error").GetString());
    }

    private static void AssertExactImplementations(
        JsonElement response, string expectedTarget, string expectedImplementation)
    {
        Assert.False(response.TryGetProperty("error", out _));
        Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
        Assert.Equal(expectedTarget,
            response.GetProperty("symbol").GetProperty("documentationCommentId").GetString());
        JsonElement implementation = Assert.Single(
            response.GetProperty("implementations").EnumerateArray());
        Assert.Equal(expectedImplementation,
            implementation.GetProperty("symbol").GetProperty("documentationCommentId").GetString());
    }

    private sealed class ImplementationIdentityFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _dbPath;
        private readonly IndexManager _manager;

        internal SemanticService Semantic { get; }
        internal NavigationTools Tools { get; }

        internal ImplementationIdentityFixture()
        {
            _root = Directory.CreateTempSubdirectory("codenav-implementation-identity").FullName;
            string projectDirectory = Path.Combine(_root, "Identity");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Identity.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(projectDirectory, "Types.cs"),
                """
                namespace Alpha
                {
                    public interface ICache { } public interface ICache<TKey, TValue> { }
                    public sealed class PlainCache : ICache { }
                    public sealed class GenericCache<TKey, TValue> : ICache<TKey, TValue> { }
                }

                namespace Beta
                {
                    public interface ICache { }
                    public sealed class OtherCache : ICache { }
                }

                namespace Nested
                {
                    public class Outer { public interface ITarget { } }
                    public class Outer<T> { public interface ITarget { } }
                    public sealed class PlainNested : Outer.ITarget { }
                    public sealed class GenericNested<T> : Outer<T>.ITarget { }
                }
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Usage.cs"),
                """
                namespace Usage.Targets
                {
                    public interface IUsageTarget { }
                    public sealed class UsageImpl : IUsageTarget { }
                }

                namespace Usage.Container
                {
                    public sealed class IUsageTarget
                    {
                        public void Touch(Usage.Targets.IUsageTarget value) { }
                    }
                }
                """);

            string sharedDirectory = Path.Combine(_root, "Shared");
            Directory.CreateDirectory(sharedDirectory);
            File.WriteAllText(Path.Combine(sharedDirectory, "Linked.cs"),
                "namespace Linked; public interface ILinked { }");
            WriteLinkedOwner("OwnerA");
            WriteLinkedOwner("OwnerB");

            _dbPath = IndexBuilder.DefaultDbPath(_root);
            IndexBuilder.Build(_root, _dbPath);
            _manager = new IndexManager(_root, _dbPath);
            _manager.Start();
            Assert.True(WaitUntil(() => _manager.Health().State == "ready", 30_000),
                _manager.Health().Error);
            Semantic = new SemanticService(_manager);
            Tools = new NavigationTools(_manager, Semantic);

            void WriteLinkedOwner(string owner)
            {
                string directory = Path.Combine(_root, owner);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"{owner}.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                    "<TargetFramework>net9.0</TargetFramework>" +
                    "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>" +
                    "</PropertyGroup><ItemGroup>" +
                    $"<Compile Include=\"{owner}.cs\" />" +
                    "<Compile Include=\"../Shared/Linked.cs\" Link=\"Linked.cs\" />" +
                    "</ItemGroup></Project>");
                File.WriteAllText(Path.Combine(directory, $"{owner}.cs"),
                    $"namespace Linked; public sealed class {owner}Impl : ILinked {{ }}");
            }
        }

        internal void ChangeIndexedAncestorArityForTest()
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE symbols SET arity = 0 WHERE name = 'Outer' AND arity = 1";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void Dispose()
        {
            Semantic.Dispose();
            _manager.Dispose();
            SqliteConnection.ClearAllPools();
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
