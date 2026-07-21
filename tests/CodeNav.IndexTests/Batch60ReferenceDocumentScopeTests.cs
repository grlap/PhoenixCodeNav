using System.Collections.Immutable;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Tests;

public sealed class Batch60ReferenceDocumentScopeTests
{
    [Fact]
    public async Task SemanticReferencesPreserveTheFullAuthorityContract()
    {
        string root = Directory.CreateTempSubdirectory("codenav-60-integration").FullName;
        try
        {
            WriteProject(root, "Declarations", null,
                "namespace N;\npublic interface ITarget { }\n");
            WriteProject(root, "Consumers", "Declarations",
                "namespace C; class Use { N.ITarget value; }\n");
            File.WriteAllText(Path.Combine(root, "Consumers", "Noise.cs"),
                string.Join(Environment.NewLine,
                    Enumerable.Range(0, 12).Select(index => $"class Noise{index} {{ }}")));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000));
            if (!semantic.FrameworkRefsAvailable) return;

            using var queries = manager.OpenQueries();
            SymbolHit target = Assert.Single(queries.SearchSymbols("ITarget", "exact",
                ["interface"], 10));

            semantic.TestOnlyForceFullSolutionReferences = true;
            var (full, fullReason) = await semantic.ReferencesAsync(target.FilePath,
                target.StartLine, null, "ITarget", maxProjects: 0, samplesPerGroup: 5,
                timeoutMs: 60_000);
            Assert.NotNull(full);
            Assert.Null(fullReason);

            semantic.TestOnlyForceFullSolutionReferences = false;
            var (scoped, scopedReason) = await semantic.ReferencesAsync(target.FilePath,
                target.StartLine, null, "ITarget", maxProjects: 0, samplesPerGroup: 5,
                timeoutMs: 60_000);
            Assert.NotNull(scoped);
            Assert.Null(scopedReason);
            Assert.Equal(CanonicalResult(full!), CanonicalResult(scoped!));

            string telemetryLine = manager.Telemetry.Snapshot().Last(line =>
                line.Contains("\"tool\":\"references\"", StringComparison.Ordinal) &&
                line.Contains("\"result\":\"exact\"", StringComparison.Ordinal));
            using var telemetry = System.Text.Json.JsonDocument.Parse(telemetryLine);
            var documentScope = telemetry.RootElement.GetProperty("queryStages")
                .GetProperty("documentScope");
            Assert.Equal("documentScoped", documentScope.GetProperty("mode").GetString());
            Assert.True(documentScope.GetProperty("candidateDocuments").GetInt32() <
                        documentScope.GetProperty("solutionDocuments").GetInt32());
            Assert.True(documentScope.GetProperty("scopedDocuments").GetInt32() <
                        documentScope.GetProperty("solutionDocuments").GetInt32());
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task LeasedTextScopeMatchesFullAuthorityAcrossGlobalAliasAndEscapedIdentifier()
    {
        string root = Directory.CreateTempSubdirectory("codenav-60-scope").FullName;
        using var workspace = new AdhocWorkspace();
        try
        {
            ProjectId declarations = ProjectId.CreateNewId("Declarations");
            ProjectId consumers = ProjectId.CreateNewId("Consumers");
            ProjectId linked = ProjectId.CreateNewId("Linked");
            ProjectId unrelated = ProjectId.CreateNewId("Unrelated");
            DocumentId pathlessDocument = DocumentId.CreateNewId(consumers);
            DocumentId consumerLinkedDocument = DocumentId.CreateNewId(consumers);
            DocumentId linkedProjectDocument = DocumentId.CreateNewId(linked);
            string linkedPath = Path.Combine(root, "Shared", "LinkedUse.cs");
            SourceText linkedText = SourceText.From(
                "namespace Shared { class LinkedUse { N.ITarget value; } }");
            Solution solution = AddProject(workspace.CurrentSolution, declarations,
                "Declarations")
                .AddDocument(DocumentId.CreateNewId(declarations), "Target.cs",
                    SourceText.From("namespace N { public interface ITarget { } }"),
                    filePath: Path.Combine(root, "Declarations", "Target.cs"));
            solution = AddProject(solution, consumers, "Consumers")
                .AddProjectReference(consumers, new ProjectReference(declarations))
                .AddDocument(DocumentId.CreateNewId(consumers), "Global.cs",
                    SourceText.From("global using TargetAlias = N.ITarget;"),
                    filePath: Path.Combine(root, "Consumers", "Global.cs"))
                .AddDocument(DocumentId.CreateNewId(consumers), "AliasUse.cs",
                    SourceText.From("class AliasUse { TargetAlias value; }"),
                    filePath: Path.Combine(root, "Consumers", "AliasUse.cs"))
                .AddDocument(DocumentId.CreateNewId(consumers), "EscapedUse.cs",
                    SourceText.From("class EscapedUse { N.\\u0049Target value; }"),
                    filePath: Path.Combine(root, "Consumers", "EscapedUse.cs"))
                .AddDocument(pathlessDocument, "PathlessUse.cs",
                    SourceText.From("class PathlessUse { N.ITarget value; }"))
                .AddDocument(consumerLinkedDocument, "LinkedUse.cs", linkedText,
                    filePath: linkedPath)
                .AddDocument(DocumentId.CreateNewId(consumers), "Noise.cs",
                    SourceText.From("class ConsumerNoise { }"),
                    filePath: Path.Combine(root, "Consumers", "Noise.cs"));
            solution = AddProject(solution, linked, "Linked")
                .AddProjectReference(linked, new ProjectReference(declarations))
                .AddDocument(linkedProjectDocument, "LinkedUse.cs", linkedText,
                    filePath: linkedPath)
                .AddDocument(DocumentId.CreateNewId(linked), "Noise.cs",
                    SourceText.From("class LinkedNoise { }"),
                    filePath: Path.Combine(root, "Linked", "Noise.cs"));
            solution = AddProject(solution, unrelated, "Unrelated")
                .AddDocument(DocumentId.CreateNewId(unrelated), "Noise.cs",
                    SourceText.From("class UnrelatedNoise { }"),
                    filePath: Path.Combine(root, "Unrelated", "Noise.cs"));

            Compilation compilation = (await solution.GetProject(declarations)!
                .GetCompilationAsync())!;
            INamedTypeSymbol symbol = compilation.GetTypeByMetadataName("N.ITarget")!;
            using var manager = new IndexManager(root, Path.Combine(root, "unused.db"));
            using var semantic = new SemanticService(manager);

            SemanticService.ReferenceDocumentScope scope = await semantic
                .PlanReferenceDocumentScopeAsync(symbol, solution, CancellationToken.None);

            Assert.NotNull(scope.Documents);
            Assert.Equal("documentScoped", scope.Stats.Mode);
            Assert.Equal("eligible", scope.Stats.Reason);
            Assert.Equal("leasedSolutionText", scope.Stats.CandidateSource);
            Assert.Equal(1, scope.Stats.AliasWidenedProjects);
            Assert.Equal(1, scope.Stats.TransformedIncludedDocuments);
            Assert.True(scope.Stats.ScopedDocuments < scope.Stats.SolutionDocuments);
            Assert.Contains(scope.Documents!, document => document.Name == "AliasUse.cs");
            Assert.Contains(scope.Documents!, document => document.Name == "EscapedUse.cs");
            Assert.Contains(scope.Documents!, document => document.Id == pathlessDocument &&
                document.FilePath is null);
            Assert.Contains(scope.Documents!, document => document.Id == consumerLinkedDocument);
            Assert.Contains(scope.Documents!, document => document.Id == linkedProjectDocument);
            Assert.DoesNotContain(scope.Documents!, document =>
                document.Project.Name == "Unrelated");

            SemanticService.ReferenceDocumentScope cachedScope = await semantic
                .PlanReferenceDocumentScopeAsync(symbol, solution, CancellationToken.None);
            Assert.True(cachedScope.Stats.CacheHit);
            Assert.Equal(scope.Documents, cachedScope.Documents);

            IEnumerable<ReferencedSymbol> full = await SymbolFinder.FindReferencesAsync(
                symbol, solution, CancellationToken.None);
            IEnumerable<ReferencedSymbol> scoped = await SymbolFinder.FindReferencesAsync(
                symbol, solution, scope.Documents!, CancellationToken.None);
            Assert.Equal(CanonicalLocations(full), CanonicalLocations(scoped));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task SourceGeneratedReferenceIsIncludedAndMatchesFullAuthority()
    {
        string root = Directory.CreateTempSubdirectory("codenav-60-generated").FullName;
        using var workspace = new AdhocWorkspace();
        try
        {
            ProjectId projectId = ProjectId.CreateNewId("P");
            Solution solution = AddProject(workspace.CurrentSolution, projectId, "P")
                .AddMetadataReference(projectId,
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddDocument(DocumentId.CreateNewId(projectId), "Target.cs",
                    SourceText.From("public interface ITarget { }"),
                    filePath: Path.Combine(root, "Target.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Noise.cs",
                    SourceText.From("class Noise { }"),
                    filePath: Path.Combine(root, "Noise.cs"))
                .AddAnalyzerReference(projectId,
                    new InMemoryGeneratorReference(new GeneratedReferenceGenerator()));

            Project project = solution.GetProject(projectId)!;
            Compilation compilation = (await project.GetCompilationAsync())!;
            INamedTypeSymbol symbol = compilation.GetTypeByMetadataName("ITarget")!;
            SourceGeneratedDocument generatedUse = Assert.Single(
                await project.GetSourceGeneratedDocumentsAsync(),
                document => document.Name == "GeneratedUse.g.cs");
            using var manager = new IndexManager(root, Path.Combine(root, "unused.db"));
            using var semantic = new SemanticService(manager);

            SemanticService.ReferenceDocumentScope scope = await semantic
                .PlanReferenceDocumentScopeAsync(symbol, solution, CancellationToken.None);

            Assert.NotNull(scope.Documents);
            Assert.Contains(scope.Documents!, document => document.Id == generatedUse.Id);
            Assert.True(scope.Stats.SolutionDocuments > project.DocumentIds.Count);
            IEnumerable<ReferencedSymbol> full = await SymbolFinder.FindReferencesAsync(
                symbol, solution, CancellationToken.None);
            IEnumerable<ReferencedSymbol> scoped = await SymbolFinder.FindReferencesAsync(
                symbol, solution, scope.Documents!, CancellationToken.None);
            Assert.Equal(CanonicalLocations(full), CanonicalLocations(scoped));
            Assert.Contains(CanonicalLocations(scoped), location =>
                location.Contains("GeneratedUse.g.cs", StringComparison.Ordinal));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task CandidateTextUsesOrdinalIdentifierBoundariesAndCompleteEscapes()
    {
        string root = Directory.CreateTempSubdirectory("codenav-60-lexical").FullName;
        using var workspace = new AdhocWorkspace();
        try
        {
            ProjectId projectId = ProjectId.CreateNewId("P");
            Solution solution = AddProject(workspace.CurrentSolution, projectId, "P")
                .AddMetadataReference(projectId,
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddDocument(DocumentId.CreateNewId(projectId), "Target.cs",
                    SourceText.From("public interface ITarget { }"),
                    filePath: Path.Combine(root, "Target.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Exact.cs",
                    SourceText.From("class Exact { ITarget value; }"),
                    filePath: Path.Combine(root, "Exact.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Verbatim.cs",
                    SourceText.From("class Verbatim { @ITarget value; }"),
                    filePath: Path.Combine(root, "Verbatim.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Escaped.cs",
                    SourceText.From("class Escaped { \\u0049Target value; }"),
                    filePath: Path.Combine(root, "Escaped.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Suppression.cs",
                    SourceText.From("[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(" +
                        "\"Category\", \"Check\", Scope = \"type\", " +
                        "Target = \"~T:\\x49Target\")]"),
                    filePath: Path.Combine(root, "Suppression.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Cref.cs",
                    SourceText.From("/// <summary><see cref=\"&#73;Target\"/></summary> " +
                        "class Cref { }"),
                    filePath: Path.Combine(root, "Cref.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Format.cs",
                    SourceText.From("class Format { I\u200cTarget value; }"),
                    filePath: Path.Combine(root, "Format.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Prefix.cs",
                    SourceText.From("class preITarget { }"),
                    filePath: Path.Combine(root, "Prefix.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Suffix.cs",
                    SourceText.From("class ITargetExtended { }"),
                    filePath: Path.Combine(root, "Suffix.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Case.cs",
                    SourceText.From("class itarget { }"),
                    filePath: Path.Combine(root, "Case.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "WindowsPath.cs",
                    SourceText.From("class Paths { string p = @\"C:\\Users\\ITargets\"; }"),
                    filePath: Path.Combine(root, "WindowsPath.cs"));

            Compilation compilation = (await solution.GetProject(projectId)!
                .GetCompilationAsync())!;
            INamedTypeSymbol symbol = compilation.GetTypeByMetadataName("ITarget")!;
            using var manager = new IndexManager(root, Path.Combine(root, "unused.db"));
            using var semantic = new SemanticService(manager);

            SemanticService.ReferenceDocumentScope scope = await semantic
                .PlanReferenceDocumentScopeAsync(symbol, solution, CancellationToken.None);

            Assert.NotNull(scope.Documents);
            string[] included = scope.Documents!.Select(document => document.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Contains("Target.cs", included);
            Assert.Contains("Exact.cs", included);
            Assert.Contains("Verbatim.cs", included);
            Assert.Contains("Escaped.cs", included);
            Assert.Contains("Suppression.cs", included);
            Assert.Contains("Cref.cs", included);
            Assert.Contains("Format.cs", included);
            Assert.DoesNotContain("Prefix.cs", included);
            Assert.DoesNotContain("Suffix.cs", included);
            Assert.DoesNotContain("Case.cs", included);
            Assert.DoesNotContain("WindowsPath.cs", included);
            Assert.Equal(4, scope.Stats.TransformedIncludedDocuments);

            IEnumerable<ReferencedSymbol> full = await SymbolFinder.FindReferencesAsync(
                symbol, solution, CancellationToken.None);
            IEnumerable<ReferencedSymbol> scoped = await SymbolFinder.FindReferencesAsync(
                symbol, solution, scope.Documents!, CancellationToken.None);
            Assert.Equal(CanonicalLocations(full), CanonicalLocations(scoped));
            string[] locations = CanonicalLocations(scoped);
            Assert.Contains(locations, location =>
                location.Contains("Suppression.cs", StringComparison.Ordinal));
            Assert.Contains(locations, location =>
                location.Contains("Cref.cs", StringComparison.Ordinal));
            Assert.Contains(locations, location =>
                location.Contains("Format.cs", StringComparison.Ordinal));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task NewLeasedSolutionFindsFirstMentionInsideSelectedProject()
    {
        string root = Directory.CreateTempSubdirectory("codenav-60-live-edit").FullName;
        using var workspace = new AdhocWorkspace();
        try
        {
            ProjectId declarations = ProjectId.CreateNewId("Declarations");
            ProjectId consumers = ProjectId.CreateNewId("Consumers");
            DocumentId useDocument = DocumentId.CreateNewId(consumers);
            Solution before = AddProject(workspace.CurrentSolution, declarations, "Declarations")
                .AddDocument(DocumentId.CreateNewId(declarations), "Target.cs",
                    SourceText.From("namespace N { public interface ITarget { } }"),
                    filePath: Path.Combine(root, "Declarations", "Target.cs"));
            before = AddProject(before, consumers, "Consumers")
                .AddProjectReference(consumers, new ProjectReference(declarations))
                .AddDocument(useDocument, "Use.cs", SourceText.From("class Use { }"),
                    filePath: Path.Combine(root, "Consumers", "Use.cs"))
                .AddDocument(DocumentId.CreateNewId(consumers), "Noise.cs",
                    SourceText.From("class Noise { }"),
                    filePath: Path.Combine(root, "Consumers", "Noise.cs"));

            Compilation beforeCompilation = (await before.GetProject(declarations)!
                .GetCompilationAsync())!;
            INamedTypeSymbol beforeSymbol = beforeCompilation.GetTypeByMetadataName("N.ITarget")!;
            using var manager = new IndexManager(root, Path.Combine(root, "unused.db"));
            using var semantic = new SemanticService(manager);
            SemanticService.ReferenceDocumentScope prior = await semantic
                .PlanReferenceDocumentScopeAsync(beforeSymbol, before, CancellationToken.None);
            Assert.DoesNotContain(prior.Documents!, document => document.Id == useDocument);

            // This is the follower-critical document-scope case: an already-selected project's
            // immutable semantic snapshot contains a first mention that committed FTS cannot know.
            Solution after = before.WithDocumentText(useDocument,
                SourceText.From("class Use { N.ITarget value; }"));
            Compilation afterCompilation = (await after.GetProject(declarations)!
                .GetCompilationAsync())!;
            INamedTypeSymbol afterSymbol = afterCompilation.GetTypeByMetadataName("N.ITarget")!;
            SemanticService.ReferenceDocumentScope current = await semantic
                .PlanReferenceDocumentScopeAsync(afterSymbol, after, CancellationToken.None);

            Assert.False(current.Stats.CacheHit);
            Assert.Contains(current.Documents!, document => document.Id == useDocument);
            IEnumerable<ReferencedSymbol> full = await SymbolFinder.FindReferencesAsync(
                afterSymbol, after, CancellationToken.None);
            IEnumerable<ReferencedSymbol> scoped = await SymbolFinder.FindReferencesAsync(
                afterSymbol, after, current.Documents!, CancellationToken.None);
            Assert.Equal(CanonicalLocations(full), CanonicalLocations(scoped));
            Assert.Contains(CanonicalLocations(scoped), location =>
                location.Contains("Consumers|Use.cs", StringComparison.Ordinal));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task UnsafeKindsAndForceSeamUseFullSolutionAuthority()
    {
        string root = Directory.CreateTempSubdirectory("codenav-60-fallback").FullName;
        using var workspace = new AdhocWorkspace();
        try
        {
            ProjectId projectId = ProjectId.CreateNewId("P");
            Solution solution = AddProject(workspace.CurrentSolution, projectId, "P")
                .AddMetadataReference(projectId,
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddDocument(DocumentId.CreateNewId(projectId), "Types.cs", SourceText.From(
                    "public class Concrete { public void GetEnumerator() { } } " +
                    "public interface IEligible { } " +
                    "public enum Choice { One } " +
                    "public interface MarkerAttribute { } " +
                    "public delegate void Work(); " +
                    "[System.Runtime.CompilerServices.CollectionBuilder(typeof(Builder), " +
                    "nameof(Builder.Create))] public class MyCollection { } " +
                    "public static class Builder { public static MyCollection Create(" +
                    "System.ReadOnlySpan<int> values) => null!; }"),
                    filePath: Path.Combine(root, "Types.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Uses.cs", SourceText.From(
                    "[Marker] class Marked { } " +
                    "class ChoiceUse { static void M(Choice value) { } " +
                    "static void Use() { M(new()); } }"),
                    filePath: Path.Combine(root, "Uses.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "Noise.cs",
                    SourceText.From("class Noise { }"),
                    filePath: Path.Combine(root, "Noise.cs"));
            Compilation compilation = (await solution.GetProject(projectId)!
                .GetCompilationAsync())!;
            INamedTypeSymbol concrete = compilation.GetTypeByMetadataName("Concrete")!;
            IMethodSymbol pattern = concrete.GetMembers("GetEnumerator")
                .OfType<IMethodSymbol>().Single();
            INamedTypeSymbol eligible = compilation.GetTypeByMetadataName("IEligible")!;
            INamedTypeSymbol enumType = compilation.GetTypeByMetadataName("Choice")!;
            INamedTypeSymbol attributeNamedInterface =
                compilation.GetTypeByMetadataName("MarkerAttribute")!;
            INamedTypeSymbol delegateType = compilation.GetTypeByMetadataName("Work")!;
            IMethodSymbol collectionBuilder = compilation.GetTypeByMetadataName("Builder")!
                .GetMembers("Create").OfType<IMethodSymbol>().Single();
            using var manager = new IndexManager(root, Path.Combine(root, "unused.db"));
            using var semantic = new SemanticService(manager);

            SemanticService.ReferenceDocumentScope concreteScope = await semantic
                .PlanReferenceDocumentScopeAsync(concrete, solution, CancellationToken.None);
            SemanticService.ReferenceDocumentScope patternScope = await semantic
                .PlanReferenceDocumentScopeAsync(pattern, solution, CancellationToken.None);
            SemanticService.ReferenceDocumentScope delegateScope = await semantic
                .PlanReferenceDocumentScopeAsync(delegateType, solution, CancellationToken.None);
            SemanticService.ReferenceDocumentScope enumScope = await semantic
                .PlanReferenceDocumentScopeAsync(enumType, solution, CancellationToken.None);
            SemanticService.ReferenceDocumentScope collectionBuilderScope = await semantic
                .PlanReferenceDocumentScopeAsync(collectionBuilder, solution,
                    CancellationToken.None);
            Assert.Null(concreteScope.Documents);
            Assert.Null(patternScope.Documents);
            Assert.Null(delegateScope.Documents);
            Assert.Null(enumScope.Documents);
            Assert.Null(collectionBuilderScope.Documents);
            Assert.Equal("ineligible_kind", concreteScope.Stats.Reason);
            Assert.Equal("ineligible_kind", patternScope.Stats.Reason);
            Assert.Equal("ineligible_kind", delegateScope.Stats.Reason);
            Assert.Equal("ineligible_kind", enumScope.Stats.Reason);
            Assert.Equal("ineligible_kind", collectionBuilderScope.Stats.Reason);

            IEnumerable<ReferencedSymbol> enumReferences = await SymbolFinder.FindReferencesAsync(
                enumType, solution, CancellationToken.None);
            bool foundTargetTypedNew = false;
            foreach (ReferenceLocation location in enumReferences.SelectMany(reference =>
                         reference.Locations).Where(location => location.Document.Name == "Uses.cs"))
            {
                SourceText text = await location.Document.GetTextAsync();
                if (text.ToString(location.Location.SourceSpan) == "new")
                    foundTargetTypedNew = true;
            }
            Assert.True(foundTargetTypedNew,
                "Full Roslyn authority must expose the enum constructor reference at new().");

            SemanticService.ReferenceDocumentScope attributeScope = await semantic
                .PlanReferenceDocumentScopeAsync(attributeNamedInterface, solution,
                    CancellationToken.None);
            Assert.NotNull(attributeScope.Documents);
            Assert.Contains(attributeScope.Documents!, document => document.Name == "Uses.cs");
            IEnumerable<ReferencedSymbol> fullAttribute = await SymbolFinder.FindReferencesAsync(
                attributeNamedInterface, solution, CancellationToken.None);
            IEnumerable<ReferencedSymbol> scopedAttribute = await SymbolFinder.FindReferencesAsync(
                attributeNamedInterface, solution, attributeScope.Documents!,
                CancellationToken.None);
            Assert.Equal(CanonicalLocations(fullAttribute), CanonicalLocations(scopedAttribute));
            Assert.Contains(CanonicalLocations(scopedAttribute), location =>
                location.Contains("Uses.cs", StringComparison.Ordinal));

            semantic.TestOnlyForceFullSolutionReferences = true;
            SemanticService.ReferenceDocumentScope forced = await semantic
                .PlanReferenceDocumentScopeAsync(eligible, solution, CancellationToken.None);
            Assert.Null(forced.Documents);
            Assert.Equal("forced_full_solution", forced.Stats.Reason);

            semantic.TestOnlyForceFullSolutionReferences = false;
            var cancelledStats = new SemanticService.ReferenceDocumentScopeStatsBox();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => semantic
                .PlanReferenceDocumentScopeAsync(eligible, solution, cancelledStats,
                    cancelled.Token));
            Assert.Equal("notCompleted", cancelledStats.Stats?.Mode);
            Assert.Equal("cancelled", cancelledStats.Stats?.Reason);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task ScopeCacheIsBoundedPerImmutableSolution()
    {
        string root = Directory.CreateTempSubdirectory("codenav-60-cache").FullName;
        using var workspace = new AdhocWorkspace();
        try
        {
            ProjectId projectId = ProjectId.CreateNewId("P");
            Solution solution = AddProject(workspace.CurrentSolution, projectId, "P");
            for (int index = 0; index < 70; index++)
            {
                string name = $"ITarget{index}";
                solution = solution.AddDocument(DocumentId.CreateNewId(projectId),
                    $"{name}.cs", SourceText.From($"public interface {name} {{ }}"),
                    filePath: Path.Combine(root, $"{name}.cs"));
            }
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "Noise.cs",
                SourceText.From("class Noise { }"), filePath: Path.Combine(root, "Noise.cs"));
            Compilation compilation = (await solution.GetProject(projectId)!
                .GetCompilationAsync())!;
            using var manager = new IndexManager(root, Path.Combine(root, "unused.db"));
            using var semantic = new SemanticService(manager);

            for (int index = 0; index < 70; index++)
            {
                INamedTypeSymbol symbol = compilation.GetTypeByMetadataName($"ITarget{index}")!;
                SemanticService.ReferenceDocumentScope scope = await semantic
                    .PlanReferenceDocumentScopeAsync(symbol, solution, CancellationToken.None);
                Assert.NotNull(scope.Documents);
            }

            Assert.InRange(semantic.TestOnlyReferenceDocumentScopeCacheCount(solution), 1, 64);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private static Solution AddProject(Solution solution, ProjectId id, string name)
        => solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name,
            LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary)));

    #pragma warning disable RS1042
    private sealed class GeneratedReferenceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
            => context.RegisterPostInitializationOutput(static output => output.AddSource(
                "GeneratedUse.g.cs", "class GeneratedUse { ITarget value; }"));
    }
    #pragma warning restore RS1042

    private sealed class InMemoryGeneratorReference(IIncrementalGenerator generator)
        : AnalyzerReference
    {
        private readonly ISourceGenerator _generator = generator.AsSourceGenerator();

        public override string? FullPath => null;
        public override object Id { get; } = new object();
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language)
            => [_generator];
    }

    private static void WriteProject(string root, string name, string? reference, string source)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        string projectReference = reference is null ? "" :
            $"<ItemGroup><ProjectReference Include=\"../{reference}/{reference}.csproj\" />" +
            "</ItemGroup>";
        File.WriteAllText(Path.Combine(directory, $"{name}.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            $"<TargetFramework>net9.0</TargetFramework></PropertyGroup>{projectReference}" +
            "</Project>");
        File.WriteAllText(Path.Combine(directory, $"{name}.cs"), source);
    }

    private static string[] CanonicalResult(SemanticReferences result)
        =>
        [
            $"symbol:{result.Symbol.DocumentationCommentId}",
            $"total:{result.TotalLocations}",
            $"deadline:{result.DeadlineExhausted}",
            $"unproven:{result.ProjectModelUnproven}",
            $"coverage:{result.Coverage.LoadedProjects}/{result.Coverage.RequestedProjects}/" +
                $"{result.Coverage.SolutionProjects}",
            .. (result.KindCounts ?? new Dictionary<string, int>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"kind:{pair.Key}:{pair.Value}"),
            .. result.Groups.OrderBy(group => group.Project, StringComparer.Ordinal)
                .Select(group => $"group:{group.Project}:{group.Count}:{group.IsTestProject}"),
            .. result.Groups.SelectMany(group => group.Samples)
                .OrderBy(sample => sample.Project, StringComparer.Ordinal)
                .ThenBy(sample => sample.Path, StringComparer.Ordinal)
                .ThenBy(sample => sample.Line)
                .Select(sample => $"sample:{sample.Project}:{sample.Path}:{sample.Line}:" +
                    $"{sample.Kind}:{sample.LineText}"),
            .. result.SkippedCandidateProjects.OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => $"skipped:{value}"),
            .. result.Coverage.FailedProjects.OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => $"failed:{value}"),
        ];

    private static string[] CanonicalLocations(IEnumerable<ReferencedSymbol> symbols)
        => symbols.SelectMany(referenced => referenced.Locations)
            .Where(location => location.Location.IsInSource)
            .Select(location => $"{location.Document.Project.Name}|{location.Document.Name}|" +
                $"{location.Location.SourceSpan.Start}|{location.Location.SourceSpan.Length}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return true;
            Thread.Sleep(25);
        }
        return predicate();
    }
}
