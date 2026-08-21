using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodeNav.Tests;

/// <summary>
/// Batch 58 (epuc.3): direct base-list identities are stored as normalized edges instead of
/// recovered by repeatedly scanning and reparsing the 400-character display signature. These
/// tests pin the completeness gain, the indexed query plan, and full-build/delta parity.
/// </summary>
public class Batch58BaseTypeEdgeTests
{
    [Fact]
    public void SyntaxEdgesUseOnlyBaseHeadsBeforeSignatureTruncation()
    {
        string padding = string.Join(", ", Enumerable.Range(0, 60).Select(i => $"IPadding{i:D2}"));
        ParsedCsFile parsed = SyntaxIndexer.Parse("Types.cs",
            $$"""
            namespace EdgeSyntax;
            using Alias = Other;
            public interface IHandler<T> { }
            public class Composite<T> : global::One.IFoo<int>, Alias::IBar<string, int>,
                IPlain, IHandler<First>, IHandler<Second> where T : IConstraint { }
            public class LongType : {{padding}}, ITail { }
            """);

        SymbolRow composite = parsed.Symbols.Single(symbol => symbol.Name == "Composite");
        Assert.Equal(
            [
                new BaseTypeIdentity("IFoo", 1),
                new BaseTypeIdentity("IBar", 2),
                new BaseTypeIdentity("IPlain", 0),
                new BaseTypeIdentity("IHandler", 1),
            ],
            composite.BaseTypes);
        Assert.DoesNotContain(composite.BaseTypes!, edge => edge.Name is "First" or "Second" or "IConstraint");

        SymbolRow longType = parsed.Symbols.Single(symbol => symbol.Name == "LongType");
        Assert.Equal(400, longType.Signature.Length);
        Assert.DoesNotContain("ITail", longType.Signature, StringComparison.Ordinal);
        Assert.Contains(new BaseTypeIdentity("ITail", 0), longType.BaseTypes!);
    }

    [Fact]
    public void IndexedEdgesFindBasesBeyondTheDisplaySignatureCapAndUseThePrimaryKey()
    {
        string root = Directory.CreateTempSubdirectory("codenav-58-edges").FullName;
        try
        {
            string project = Path.Combine(root, "P");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            string definitions = string.Join(Environment.NewLine,
                Enumerable.Range(0, 60).Select(i => $"public interface IPadding{i:D2} {{ }}"));
            string bases = string.Join(", ", Enumerable.Range(0, 60).Select(i => $"IPadding{i:D2}"));
            File.WriteAllText(Path.Combine(project, "Types.cs"),
                $$"""
                namespace EdgeFixture;
                {{definitions}}
                public interface ITail { }
                public interface IHandler<T> { }
                public class LongImpl : {{bases}}, ITail { }
                public class Handler : IHandler<Foo> { }
                public class Foo { }
                public class ConstraintOnly<T> where T : ITail { }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var queries = new IndexQueries(dbPath);

            SymbolHit direct = Assert.Single(queries.ImplementationCandidates("ITail", 10, 0));
            Assert.Equal("LongImpl", direct.Name);
            Assert.DoesNotContain("ITail", direct.Signature, StringComparison.Ordinal);
            Assert.Contains("P", queries.ImplementationCandidateProjects("ITail"));

            List<SymbolHit> closure = queries.TransitiveImplementationClosure(
                "ITail", 0, out bool capped);
            Assert.False(capped);
            Assert.Contains(closure, hit => hit.Name == "LongImpl");
            Assert.DoesNotContain(closure, hit => hit.Name == "ConstraintOnly");
            Assert.Empty(queries.ImplementationCandidates("Foo", 10, 0));

            using var connection = new SqliteConnection(
                IndexQueries.ReadConnectionString(dbPath, pinReadSnapshot: false, pooling: false));
            connection.Open();
            using var plan = connection.CreateCommand();
            plan.CommandText = "EXPLAIN QUERY PLAN " + IndexQueries.BaseListMentionsExactSql;
            plan.Parameters.AddWithValue("$n", "ITail");
            plan.Parameters.AddWithValue("$arity", 0);
            plan.Parameters.AddWithValue("$after", 0);
            plan.Parameters.AddWithValue("$lim", 10);
            var details = new List<string>();
            using (SqliteDataReader reader = plan.ExecuteReader())
            {
                while (reader.Read()) details.Add(reader.GetString(3));
            }
            Assert.Contains(details, detail =>
                detail.Contains("SEARCH e USING PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(details, detail =>
                detail.Contains("SCAN s", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("SCAN symbols", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void DeltaRefreshProducesTheSameEdgesAsAFullBuild()
    {
        string root = Directory.CreateTempSubdirectory("codenav-58-delta").FullName;
        try
        {
            string project = Path.Combine(root, "P");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(project, "Contracts.cs"),
                "namespace DeltaEdges { public interface IOld { } public interface INew { } }");
            File.WriteAllText(Path.Combine(project, "Impl.cs"),
                "namespace DeltaEdges { public class Impl : IOld { } }");
            File.WriteAllText(Path.Combine(project, "Deleted.cs"),
                "namespace DeltaEdges { public class Deleted : IOld { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            File.WriteAllText(Path.Combine(project, "Impl.cs"),
                "namespace DeltaEdges { public class Impl : INew { } }");
            File.WriteAllText(Path.Combine(project, "Added.cs"),
                "namespace DeltaEdges { public class Added : INew { } }");
            File.Delete(Path.Combine(project, "Deleted.cs"));
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult result = DeltaRefresher.Refresh(store, root,
                    ["P/Impl.cs", "P/Added.cs", "P/Deleted.cs"]);
                Assert.Equal(1, result.ChangedFiles);
                Assert.Equal(1, result.AddedFiles);
                Assert.Equal(1, result.DeletedFiles);
            }

            List<string> deltaEdges = DumpEdges(dbPath);
            Assert.Contains("INew`0 -> Added @ P/Added.cs", deltaEdges);
            Assert.Contains("INew`0 -> Impl @ P/Impl.cs", deltaEdges);
            Assert.DoesNotContain(deltaEdges, edge => edge.Contains("Deleted", StringComparison.Ordinal));
            Assert.DoesNotContain(deltaEdges, edge => edge.StartsWith("IOld`0 -> Impl", StringComparison.Ordinal));

            IndexBuilder.Build(root, dbPath);
            Assert.Equal(deltaEdges, DumpEdges(dbPath));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void SameNameAndNamespaceDistinctCandidatesReachExactSemanticVerification()
    {
        string root = Directory.CreateTempSubdirectory("codenav-58-collisions").FullName;
        try
        {
            string project = Path.Combine(root, "P");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(project, "Collisions.cs"),
                """
                namespace A
                {
                    public class Foo { }
                    public interface IRoot { }
                    public interface IDiamondRoot { }
                    public interface ILeft : IDiamondRoot { }
                    public interface IRight : IDiamondRoot { }
                }
                namespace B
                {
                    public class Foo : A.Foo { }
                    public class Impl : A.IRoot { }
                }
                namespace C
                {
                    public class Impl : A.IRoot { }
                }
                namespace D
                {
                    public class Outer
                    {
                        public class Impl : A.IRoot { }
                    }
                    public class Outer<T>
                    {
                        public class Impl : A.IRoot { }
                    }
                    public class Diamond : A.ILeft, A.IRight { }
                }
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                List<SymbolHit> sameName = queries.TransitiveImplementationClosure(
                    "Foo", 0, out bool sameNameCapped);
                Assert.False(sameNameCapped);
                SymbolHit derivedFoo = Assert.Single(sameName);
                Assert.Equal(("B", "Foo"), (derivedFoo.Ns, derivedFoo.Name));

                List<SymbolHit> sameDeclarationKeys = queries.TransitiveImplementationClosure(
                    "IRoot", 0, out bool declarationKeysCapped);
                Assert.False(declarationKeysCapped);
                Assert.Equal(
                    ["B.Impl", "C.Impl", "D.Outer.Impl", "D.Outer.Impl"],
                    sameDeclarationKeys.Select(hit =>
                            hit.Container is null
                                ? $"{hit.Ns}.{hit.Name}"
                                : $"{hit.Ns}.{hit.Container}.{hit.Name}")
                        .OrderBy(identity => identity, StringComparer.Ordinal));
                Assert.Equal(2, sameDeclarationKeys
                    .Where(hit => hit.Ns == "D" && hit.Container == "Outer" && hit.Name == "Impl")
                    .Select(hit => hit.Id)
                    .Distinct()
                    .Count());

                // The public nullable-arity variant must retain every physical candidate too.
                List<SymbolHit> anyArity = queries.TransitiveImplementationClosure(
                    "IRoot", null, out bool anyArityCapped);
                Assert.False(anyArityCapped);
                Assert.Equal(4, anyArity.Count);

                List<SymbolHit> diamond = queries.TransitiveImplementationClosure(
                    "IDiamondRoot", 0, out bool diamondCapped);
                Assert.False(diamondCapped);
                Assert.Equal(2, diamond.Count(hit => hit.Name == "Diamond"));
                Assert.Single(diamond.Where(hit => hit.Name == "Diamond")
                    .Select(hit => hit.Id).Distinct());
            }

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000),
                "manager did not become queryable");
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return; // deterministic index assertions above still run
            var tools = new NavigationTools(manager, semantic);

            AssertExactIdentities(
                SemanticRetry.ParseExactWithRetry(() => tools.Implementations(
                    path: "P/Collisions.cs", line: 3, maxProjects: 0, timeoutMs: 120_000)),
                "implementations", ["T:B.Foo"]);
            AssertExactIdentities(
                SemanticRetry.ParseExactWithRetry(() => tools.TypeHierarchy(
                    path: "P/Collisions.cs", line: 3, maxProjects: 0, timeoutMs: 120_000)),
                "derivedOrImplementing", ["T:B.Foo"]);
            AssertExactIdentities(
                SemanticRetry.ParseExactWithRetry(() => tools.Implementations(
                    path: "P/Collisions.cs", line: 4, maxProjects: 0, timeoutMs: 120_000)),
                "implementations",
                ["T:B.Impl", "T:C.Impl", "T:D.Outer.Impl", "T:D.Outer`1.Impl"]);
            AssertExactIdentities(
                SemanticRetry.ParseExactWithRetry(() => tools.TypeHierarchy(
                    path: "P/Collisions.cs", line: 4, maxProjects: 0, timeoutMs: 120_000)),
                "derivedOrImplementing",
                ["T:B.Impl", "T:C.Impl", "T:D.Outer.Impl", "T:D.Outer`1.Impl"]);
            AssertExactIdentities(
                SemanticRetry.ParseExactWithRetry(() => tools.Implementations(
                    path: "P/Collisions.cs", line: 5, maxProjects: 0, timeoutMs: 120_000)),
                "implementations", ["T:D.Diamond"]);
            AssertExactIdentities(
                SemanticRetry.ParseExactWithRetry(() => tools.TypeHierarchy(
                    path: "P/Collisions.cs", line: 5, maxProjects: 0, timeoutMs: 120_000)),
                "derivedOrImplementing", ["T:D.Diamond"]);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static List<string> DumpEdges(string dbPath)
    {
        using var connection = new SqliteConnection(
            IndexQueries.ReadConnectionString(dbPath, pinReadSnapshot: false, pooling: false));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.base_name, e.base_arity, s.name, f.path
            FROM type_base_edges e
            JOIN symbols s ON s.id = e.derived_symbol_id
            JOIN files f ON f.id = e.file_id
            ORDER BY e.base_name COLLATE NOCASE, e.base_arity, s.name, f.path
            """;
        var result = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add($"{reader.GetString(0)}`{reader.GetInt32(1)} -> " +
                       $"{reader.GetString(2)} @ {reader.GetString(3)}");
        }
        return result;
    }

    private static void AssertExactIdentities(
        JsonElement response, string resultProperty, string[] expected)
    {
        Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
        string[] actual = response.GetProperty(resultProperty).EnumerateArray()
            .Select(item => resultProperty == "implementations"
                ? item.GetProperty("symbol")
                : item)
            .Select(symbol => symbol.GetProperty("documentationCommentId").GetString()!)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
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
