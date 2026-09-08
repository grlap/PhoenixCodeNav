using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;

namespace CodeNav.Tests;

/// <summary>
/// Direct execution of the test assembly runs only this explicit canary. VSTest still discovers
/// the ordinary tests normally. Each invocation owns one workspace; process exit, not Roslyn's
/// incomplete disposal API, separates the persistent-storage lifetimes.
/// </summary>
internal static class SemanticPersistenceCanary
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;
    private static readonly Type SqlExceptionType =
        RoslynType("Microsoft.CodeAnalysis.SQLite.Interop.SqlException");

    internal const string GraphContractSource = "namespace Graph; public interface IContract { void Run(); }";
    internal const string GraphBaseSource = "namespace Graph; public class Base { public virtual void Run() { } }";
    internal const string GraphDerivedSource = "namespace Graph; public sealed class Derived : Base, IContract { public override void Run() { } }";
    internal const string GraphUseSource = "namespace Graph; public static class Use { public static void Invoke(IContract value) { value.Run(); } }";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--roslyn-persistence-graph-canary")
        {
            try
            {
                GraphResult graph = await RunGraphAsync(args[1], bool.Parse(args[2]), bool.Parse(args[3]));
                Console.WriteLine(JsonSerializer.Serialize(graph));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
        if (args.Length != 6 || args[0] != "--roslyn-persistence-canary")
        {
            Console.Error.WriteLine("Expected --roslyn-persistence-canary root enabled includeReference expectedHit expectedReferences, " +
                "or --roslyn-persistence-graph-canary root enabled expectedHit");
            return 64;
        }

        try
        {
            Result result = await RunAsync(args[1], bool.Parse(args[2]), bool.Parse(args[3]),
                bool.Parse(args[4]), int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    internal sealed record Result(int ProcessId, bool Enabled, bool CacheHit, int References,
        string StorageKind, string? DatabaseFile, string SolutionId, string ProjectId,
        string DocumentId);

    internal sealed record GraphResult(int ProcessId, string SolutionId, string[] ReferenceSites,
        string[] Implementations, string[] DerivedTypes, string[] Overrides);

    private static async Task<Result> RunAsync(string root, bool enabled, bool includeReference,
        bool expectedHit, int expectedReferences)
    {
        var sqlErrors = new ConcurrentQueue<string>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe = (_, args) =>
        {
            if (args.Exception.GetType() == SqlExceptionType)
                sqlErrors.Enqueue(args.Exception.ToString());
        };
        AppDomain.CurrentDomain.FirstChanceException += observe;
        try
        {
            Console.Error.WriteLine($"single-project: loading enabled={enabled}, include={includeReference}, expectedHit={expectedHit}");
            // Deliberately exercise the production constructor default on the enabled branch.
            using var workspace = enabled
                ? new SemanticWorkspace(root, IndexBuilder.DefaultDbPath(root))
                : new SemanticWorkspace(root, IndexBuilder.DefaultDbPath(root), enableRoslynPersistence: false);
            Assert.Equal(enabled, workspace.TestOnlyCurrentSolution.FilePath is not null);
            using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(["P"], CancellationToken.None);
            Project project = Assert.Single(lease.Solution.Projects);
            Solution solution = lease.Solution.WithProjectParseOptions(project.Id,
                new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols(
                    includeReference ? new[] { "INCLUDE_REFERENCE" } : Array.Empty<string>()));
            project = solution.GetProject(project.Id)!;
            Document use = project.Documents.Single(document => document.Name == "Use.cs");

            object storage = await OpenStorageAsync(solution, Path.Combine(root, "roslyn-cache"));
            string storageKind = storage.GetType().FullName!;
            Assert.Equal(enabled
                ? "Microsoft.CodeAnalysis.SQLite.v2.SQLitePersistentStorage"
                : "Microsoft.CodeAnalysis.Host.NoOpPersistentStorage", storageKind);
            if (enabled) AssertActive(storage);

            Type indexType = RoslynType("Microsoft.CodeAnalysis.FindSymbols.SyntaxTreeIndex");
            MethodInfo loadOnly = indexType.GetMethod("GetIndexAsync", All,
                [typeof(Document), typeof(bool), typeof(CancellationToken)])!;
            // This must precede GetRequiredIndexAsync and SymbolFinder: a fresh-process hit then
            // proves disk reuse, rather than a ConditionalWeakTable hit in the same workspace.
            object? cached = await AwaitAsync(loadOnly.Invoke(null, [use, true, CancellationToken.None])!);
            Assert.Equal(expectedHit, cached is not null);
            Console.Error.WriteLine($"single-project: load-only hit={cached is not null}; computing semantic references");
            MethodInfo required = indexType.GetMethod("GetRequiredIndexAsync", All,
                [typeof(Document), typeof(CancellationToken)])!;
            Assert.NotNull(await AwaitAsync(required.Invoke(null, [use, CancellationToken.None])!));

            Compilation compilation = (await project.GetCompilationAsync())!;
            INamedTypeSymbol target = compilation.GetTypeByMetadataName("N.ITarget")!;
            Assert.NotNull(target);
            IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(
                target, solution, CancellationToken.None);
            int count = references.SelectMany(reference => reference.Locations).Count();
            Assert.Equal(expectedReferences, count);
            Console.Error.WriteLine($"single-project: references={count}; finishing persistence checks");

            string? databaseFile = null;
            if (enabled)
            {
                AssertActive(storage);
                MethodInfo flush = storage.GetType().GetMethod("FlushInMemoryDataToDiskIfNotShutdownAsync", All)!;
                await AwaitAsync(flush.Invoke(storage, [CancellationToken.None])!);
                AssertActive(storage);
                databaseFile = (string)storage.GetType().GetProperty("DatabaseFile", All)!.GetValue(storage)!;
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "roslyn-cache", "sqlite3", "v2", "storage.ide")),
                    Path.GetFullPath(databaseFile));
                Assert.True(File.Exists(databaseFile), "Roslyn did not create its disk database.");
                // The fresh-process reuse phases are the positive proof of persisted bytes.
            }
            else
            {
                Assert.False(Directory.Exists(Path.Combine(root, "roslyn-cache")),
                    "The persistence opt-out created a cache directory.");
            }
            Assert.True(sqlErrors.IsEmpty, string.Join(Environment.NewLine, sqlErrors));
            return new Result(Environment.ProcessId, enabled, cached is not null, count,
                storageKind, databaseFile, solution.Id.Id.ToString(), project.Id.Id.ToString(),
                use.Id.Id.ToString());
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= observe;
        }
    }

    private static async Task<GraphResult> RunGraphAsync(string root, bool enabled, bool expectedHit)
    {
        var sqlErrors = new ConcurrentQueue<string>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe = (_, args) =>
        {
            if (args.Exception.GetType() == SqlExceptionType) sqlErrors.Enqueue(args.Exception.ToString());
        };
        AppDomain.CurrentDomain.FirstChanceException += observe;
        try
        {
            Console.Error.WriteLine($"two-project: loading enabled={enabled}, expectedHit={expectedHit}");
            using var workspace = enabled
                ? new SemanticWorkspace(root, IndexBuilder.DefaultDbPath(root))
                : new SemanticWorkspace(root, IndexBuilder.DefaultDbPath(root), enableRoslynPersistence: false);
            Assert.Equal(enabled, workspace.TestOnlyCurrentSolution.FilePath is not null);
            using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(["Contracts", "Consumer"], CancellationToken.None);
            Assert.Equal(2, lease.Coverage.LoadedProjects);
            Assert.Equal(2, lease.Coverage.RequestedProjects);
            Assert.Empty(lease.Coverage.SkippedProjects);
            Assert.Empty(lease.Coverage.FailedProjects);
            Solution solution = lease.Solution;
            Assert.Equal(2, solution.ProjectIds.Count);
            Project contracts = solution.Projects.Single(project => project.Name == "Contracts");
            Project consumer = solution.Projects.Single(project => project.Name == "Consumer");
            Assert.Equal(contracts.Id, Assert.Single(consumer.ProjectReferences).ProjectId);
            Document[] documents = solution.Projects.SelectMany(project => project.Documents).ToArray();
            Assert.Equal(4, documents.Length);

            object storage = await OpenStorageAsync(solution, Path.Combine(root, "roslyn-cache"));
            Assert.Equal(enabled ? "Microsoft.CodeAnalysis.SQLite.v2.SQLitePersistentStorage"
                : "Microsoft.CodeAnalysis.Host.NoOpPersistentStorage", storage.GetType().FullName);
            if (enabled) AssertActive(storage);

            Type[] indexTypes = [RoslynType("Microsoft.CodeAnalysis.FindSymbols.SyntaxTreeIndex"),
                RoslynType("Microsoft.CodeAnalysis.FindSymbols.TopLevelSyntaxTreeIndex")];
            // Check every persisted input before computing any index or invoking SymbolFinder.
            // Hierarchy/implementation discovery uses TopLevelSyntaxTreeIndex, not just SyntaxTreeIndex.
            foreach (Type indexType in indexTypes)
            {
                MethodInfo loadOnly = indexType.GetMethod("GetIndexAsync", All,
                    [typeof(Document), typeof(bool), typeof(CancellationToken)])!;
                foreach (Document document in documents)
                {
                    object? cached = await AwaitAsync(loadOnly.Invoke(null, [document, true, CancellationToken.None])!);
                    Assert.True(expectedHit == (cached is not null),
                        $"{indexType.Name}/{document.Project.Name}/{document.Name}: expectedHit={expectedHit}, actual={cached is not null}");
                }
            }
            Console.Error.WriteLine("two-project: both index families passed load-only assertions; computing indexes");
            foreach (Type indexType in indexTypes)
            {
                MethodInfo required = indexType.GetMethod("GetRequiredIndexAsync", All,
                    [typeof(Document), typeof(CancellationToken)])!;
                foreach (Document document in documents)
                    Assert.NotNull(await AwaitAsync(required.Invoke(null, [document, CancellationToken.None])!));
            }

            Compilation compilation = (await contracts.GetCompilationAsync())!;
            foreach (Project project in solution.Projects)
            {
                Compilation loaded = (await project.GetCompilationAsync())!;
                Assert.Empty(loaded.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            }
            INamedTypeSymbol contract = compilation.GetTypeByMetadataName("Graph.IContract")!;
            INamedTypeSymbol baseType = compilation.GetTypeByMetadataName("Graph.Base")!;
            Assert.NotNull(contract);
            Assert.NotNull(baseType);
            IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(contract, solution, CancellationToken.None);
            string[] sites = references.SelectMany(reference => reference.Locations).Select(reference =>
                $"{Path.GetRelativePath(root, reference.Document.FilePath!).Replace('\\', '/')}:{reference.Location.SourceSpan.Start}:{reference.Location.SourceSpan.Length}")
                .Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                $"Consumer/Derived.cs:{GraphDerivedSource.IndexOf("IContract", StringComparison.Ordinal)}:9",
                $"Consumer/Use.cs:{GraphUseSource.IndexOf("IContract", StringComparison.Ordinal)}:9"
            }, sites);
            string[] implementations = (await SymbolFinder.FindImplementationsAsync(contract, solution, cancellationToken: CancellationToken.None))
                .Select(symbol => GraphDeclaration(symbol, solution)).Order(StringComparer.Ordinal).ToArray();
            string[] derived = (await SymbolFinder.FindDerivedClassesAsync(baseType, solution, transitive: true, cancellationToken: CancellationToken.None))
                .Select(symbol => GraphDeclaration(symbol, solution)).Order(StringComparer.Ordinal).ToArray();
            string[] overrides = (await SymbolFinder.FindOverridesAsync(Assert.Single(baseType.GetMembers("Run")), solution, cancellationToken: CancellationToken.None))
                .Select(symbol => GraphDeclaration(symbol, solution)).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "T:Graph.Derived@Consumer/Derived.cs" }, implementations);
            Assert.Equal(new[] { "T:Graph.Derived@Consumer/Derived.cs" }, derived);
            Assert.Equal(new[] { "M:Graph.Derived.Run@Consumer/Derived.cs" }, overrides);
            Console.Error.WriteLine("two-project: exact reference/implementation/derived/override assertions passed; flushing");
            if (enabled)
            {
                AssertActive(storage);
                MethodInfo flush = storage.GetType().GetMethod("FlushInMemoryDataToDiskIfNotShutdownAsync", All)!;
                await AwaitAsync(flush.Invoke(storage, [CancellationToken.None])!);
                AssertActive(storage);
                string databaseFile = (string)storage.GetType().GetProperty("DatabaseFile", All)!.GetValue(storage)!;
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "roslyn-cache", "sqlite3", "v2", "storage.ide")), Path.GetFullPath(databaseFile));
                Assert.True(File.Exists(databaseFile));
            }
            else Assert.False(Directory.Exists(Path.Combine(root, "roslyn-cache")));
            Assert.True(sqlErrors.IsEmpty, string.Join(Environment.NewLine, sqlErrors));
            return new GraphResult(Environment.ProcessId, solution.Id.Id.ToString(), sites, implementations, derived, overrides);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= observe;
        }
    }

    private static string GraphDeclaration(ISymbol symbol, Solution solution)
    {
        Location location = Assert.Single(symbol.Locations.Where(location => location.IsInSource));
        Document? document = solution.GetDocument(location.SourceTree!);
        Assert.NotNull(document);
        string? identity = symbol.GetDocumentationCommentId();
        Assert.NotNull(identity);
        return $"{identity}@{document.Project.Name}/{document.Name}";
    }

    private static async Task<object> OpenStorageAsync(Solution solution, string workingFolder)
    {
        Type serviceType = RoslynType("Microsoft.CodeAnalysis.SQLite.v2.SQLitePersistentStorageService");
        MethodInfo getService = typeof(HostWorkspaceServices).GetMethods()
            .Single(method => method.Name == nameof(HostWorkspaceServices.GetService) && method.IsGenericMethodDefinition);
        object service = getService.MakeGenericMethod(serviceType)
            .Invoke(solution.Workspace.Services, null)!;
        Assert.NotNull(service);
        Type solutionKeyType = RoslynType("Microsoft.CodeAnalysis.Storage.SolutionKey");
        object key = solutionKeyType.GetMethod("ToSolutionKey", All, [typeof(Solution)])!
            .Invoke(null, [solution])!;

        // Use the real exported service and its existing test-configuration overload. Only the
        // working folder is redirected into this fixture; store selection, null-path opt-out,
        // ownership, writes, and subsequent default service lookups remain Roslyn's own code.
        Type configurationType = RoslynType("Microsoft.CodeAnalysis.Host.IPersistentStorageConfiguration");
        var configuration = (CanaryStorageConfiguration)DispatchProxy.Create(configurationType,
            typeof(CanaryStorageConfiguration));
        configuration.WorkingFolder = workingFolder;
        MethodInfo open = serviceType.GetMethods(All)
            .Single(method => method.Name == "GetStorageAsync" && method.GetParameters().Length == 4);
        object storage = (await AwaitAsync(open.Invoke(service, [key, configuration, null, CancellationToken.None])!))!;
        MethodInfo normalOpen = serviceType.GetMethods(All)
            .Single(method => method.Name == "GetStorageAsync" && method.GetParameters().Length == 2);
        object normalStorage = (await AwaitAsync(normalOpen.Invoke(service, [key, CancellationToken.None])!))!;
        if (solution.FilePath is not null) Assert.Same(storage, normalStorage);
        else Assert.Equal(storage.GetType(), normalStorage.GetType());
        return storage;
    }

    private static void AssertActive(object storage)
    {
        FieldInfo disabled = storage.GetType().BaseType!.GetField("_isDisabled", All)!;
        Assert.False((bool)disabled.GetValue(storage)!, "Roslyn silently disabled persistent storage.");
    }

    private static Type RoslynType(string name) => typeof(Workspace).Assembly.GetType(name, throwOnError: true)!;

    private static async Task<object?> AwaitAsync(object awaitable)
    {
        Task task = awaitable as Task ?? (Task)awaitable.GetType().GetMethod("AsTask")!.Invoke(awaitable, null)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
}

public class CanaryStorageConfiguration : DispatchProxy
{
    internal string WorkingFolder { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_ThrowOnFailure" => true,
        "TryGetStorageLocation" => WorkingFolder,
        _ => throw new InvalidOperationException($"Unexpected storage configuration member: {targetMethod}")
    };
}
