using System.Reflection;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Tests;

public sealed class Batch61PersistentSemanticIdentityTests
{
    [Fact]
    public void StableSemanticIdentitiesAreRepeatableAndWorkspaceScoped()
    {
        string root = Directory.CreateTempSubdirectory("codenav-61-identity").FullName;
        string otherRoot = Directory.CreateTempSubdirectory("codenav-61-other").FullName;
        try
        {
            using var first = new SemanticWorkspace(root, Path.Combine(root, "index.db"));
            using var second = new SemanticWorkspace(root, Path.Combine(root, "other.db"));
            using var other = new SemanticWorkspace(otherRoot, Path.Combine(otherRoot, "index.db"));

            Assert.Equal(first.TestOnlyCurrentSolution.Id, second.TestOnlyCurrentSolution.Id);
            Assert.NotEqual(first.TestOnlyCurrentSolution.Id, other.TestOnlyCurrentSolution.Id);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), ".codenav",
                    "phoenix-semantic-workspace.sln"),
                first.TestOnlyCurrentSolution.FilePath);

            ProjectId firstProject = first.TestOnlyStableProjectId("Example");
            ProjectId secondProject = second.TestOnlyStableProjectId("example");
            ProjectId otherProject = other.TestOnlyStableProjectId("Example");
            Assert.Equal(firstProject, secondProject);
            Assert.NotEqual(firstProject, otherProject);

            DocumentId firstDocument = SemanticWorkspace.TestOnlyStableDocumentId(
                firstProject, "src\\Example.cs");
            DocumentId secondDocument = SemanticWorkspace.TestOnlyStableDocumentId(
                secondProject, "src/Example.cs");
            Assert.Equal(firstDocument, secondDocument);
            Assert.NotEqual(firstDocument, SemanticWorkspace.TestOnlyStableDocumentId(
                firstProject, "src/Other.cs"));
            Assert.NotEqual(firstDocument, SemanticWorkspace.TestOnlyStableDocumentId(
                firstProject, "generated:src/Example.cs"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(otherRoot);
        }
    }

    [Fact]
    public async Task FreshSemanticWorkspacesReuseLoadedProjectAndDocumentIds()
    {
        string root = Directory.CreateTempSubdirectory("codenav-61-reload").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(projectDirectory, "Target.cs"),
                "namespace N; public interface ITarget { }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            (SolutionId Solution, ProjectId Project, DocumentId Document) first =
                await CaptureIds(root, dbPath);
            (SolutionId Solution, ProjectId Project, DocumentId Document) second =
                await CaptureIds(root, dbPath);

            Assert.Equal(first, second);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void DefaultAdhocHostExportsRoslynSqlitePersistentStorage()
    {
        string root = Directory.CreateTempSubdirectory("codenav-61-storage").FullName;
        try
        {
            using var workspace = new SemanticWorkspace(root, Path.Combine(root, "index.db"));
            HostWorkspaceServices services = workspace.TestOnlyCurrentSolution.Workspace.Services;
            Type serviceType = typeof(Workspace).Assembly.GetType(
                "Microsoft.CodeAnalysis.SQLite.v2.SQLitePersistentStorageService",
                throwOnError: true)!;
            MethodInfo getService = typeof(HostWorkspaceServices).GetMethods()
                .Single(method => method.Name == nameof(HostWorkspaceServices.GetService) &&
                                  method.IsGenericMethodDefinition);
            object? service = getService.MakeGenericMethod(serviceType)
                .Invoke(services, null);

            Assert.NotNull(service);
            Assert.Equal(serviceType, service.GetType());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task StableDocumentIdsStillInvalidateOnChangedBytesAndParseOptions()
    {
        string root = Directory.CreateTempSubdirectory("codenav-61-invalidation").FullName;
        try
        {
            using var identity = new SemanticWorkspace(root, Path.Combine(root, "index.db"));
            SolutionId solutionId = identity.TestOnlyCurrentSolution.Id;
            string solutionPath = identity.TestOnlyCurrentSolution.FilePath!;
            ProjectId projectId = identity.TestOnlyStableProjectId("P");
            DocumentId targetId = SemanticWorkspace.TestOnlyStableDocumentId(
                projectId, "Target.cs");
            DocumentId useId = SemanticWorkspace.TestOnlyStableDocumentId(
                projectId, "Use.cs");

            Assert.Equal(0, await ReferenceCount(solutionId, solutionPath, projectId,
                targetId, useId, "class Noise { }", CSharpParseOptions.Default));
            Assert.Equal(1, await ReferenceCount(solutionId, solutionPath, projectId,
                targetId, useId, "class Use { N.ITarget value; }", CSharpParseOptions.Default));

            const string conditionalUse =
                "#if INCLUDE_REFERENCE\nclass ConditionalUse { N.ITarget value; }\n#endif";
            Assert.Equal(0, await ReferenceCount(solutionId, solutionPath, projectId,
                targetId, useId, conditionalUse, CSharpParseOptions.Default));
            Assert.Equal(1, await ReferenceCount(solutionId, solutionPath, projectId,
                targetId, useId, conditionalUse,
                CSharpParseOptions.Default.WithPreprocessorSymbols("INCLUDE_REFERENCE")));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static async Task<(SolutionId Solution, ProjectId Project, DocumentId Document)>
        CaptureIds(string root, string dbPath)
    {
        using var workspace = new SemanticWorkspace(root, dbPath);
        using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(
            ["P"], CancellationToken.None);
        Project project = Assert.Single(lease.Solution.Projects);
        Document document = Assert.Single(project.Documents);
        return (lease.Solution.Id, project.Id, document.Id);
    }

    private static async Task<int> ReferenceCount(SolutionId solutionId, string solutionPath,
        ProjectId projectId, DocumentId targetId, DocumentId useId, string useText,
        CSharpParseOptions parseOptions)
    {
        using var workspace = new AdhocWorkspace();
        Solution solution = workspace.AddSolution(SolutionInfo.Create(
            solutionId, VersionStamp.Create(), solutionPath));
        solution = solution.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), "P", "P", LanguageNames.CSharp,
                filePath: Path.Combine(solutionPath, "..", "P.csproj"),
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                parseOptions: parseOptions,
                metadataReferences:
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(targetId, "Target.cs",
                SourceText.From("namespace N; public interface ITarget { }"),
                filePath: Path.Combine(solutionPath, "..", "Target.cs"))
            .AddDocument(useId, "Use.cs", SourceText.From(useText),
                filePath: Path.Combine(solutionPath, "..", "Use.cs"));
        Assert.True(workspace.TryApplyChanges(solution));

        Project project = workspace.CurrentSolution.GetProject(projectId)!;
        Assert.NotNull(project);
        Compilation compilation = (await project.GetCompilationAsync())!;
        INamedTypeSymbol target = compilation.GetTypeByMetadataName("N.ITarget")!;
        IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(
            target, workspace.CurrentSolution, CancellationToken.None);
        return references.SelectMany(reference => reference.Locations).Count();
    }
}
