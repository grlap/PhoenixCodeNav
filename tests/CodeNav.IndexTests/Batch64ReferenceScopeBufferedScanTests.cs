using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Tests;

public sealed class Batch64ReferenceScopeBufferedScanTests
{
    [Fact]
    public void BufferedProbeMatchesStringOracleAcrossEveryWindowBoundary()
    {
        const int bufferChars = 32;
        string supplementaryFormat = char.ConvertFromUtf32(0xE0001);
        var cases = new (string Text, bool Candidate, bool Transformed)[]
        {
            ("", false, false),
            ("x", false, false),
            ("ITarget", true, false),
            ("@ITarget", true, false),
            ("preITarget", false, false),
            ("ITargetExtended", false, false),
            ("itarget", false, false),
            (@"C:\Users\ITargets", false, false),
            (@"\u0049Target", true, true),
            (@"\u049", false, false),
            (@"\u00G", false, false),
            (@"\u12\u0049", true, true),
            (@"\u0049F", true, true),
            (@"\U00000049Target", true, true),
            (@"\U0000049", false, false),
            (@"\U0000012G", false, false),
            (@"\U00000049F", true, true),
            (@"\x49Target", true, true),
            (@"\x", false, false),
            (@"\xFz", true, true),
            (@"\\u0049", true, true),
            ("&#73;Target", true, true),
            ("&#x49;Target", true, true),
            ("&#;", false, false),
            ("&#x;", false, false),
            ("&#73", false, false),
            ("&#12x;", false, false),
            ("&#xG", false, false),
            ("&#12&#34;", true, true),
            ("&&#x49;", true, true),
            (@"&#x49\u0049", true, true),
            ("&#" + supplementaryFormat, true, true),
            ("I\u200cTarget", true, true),
            (supplementaryFormat, true, true),
            ("plain text", false, false),
            ("&&#73;", true, true),
            ("&#" + new string('7', bufferChars * 3) + ";", true, true),
            ("&#" + new string('7', bufferChars * 3), false, false),
        };

        foreach ((string payload, bool candidate, bool transformed) in cases)
        {
            for (int offset = 0; offset <= bufferChars * 3; offset++)
            {
                string content = new string(' ', offset) + payload + " ";
                SourceText text = SourceText.From(content);
                SemanticService.ReferenceTextProbe oracle = SemanticService
                    .TestOnlyProbeReferenceText(text, ["ITarget"], forceBuffered: false,
                        bufferChars);
                SemanticService.ReferenceTextProbe buffered = SemanticService
                    .TestOnlyProbeReferenceText(text, ["ITarget"], forceBuffered: true,
                        bufferChars);
                SemanticService.ReferenceTextProbe optimizedString = SemanticService
                    .TestOnlyProbeReferenceString(text, ["ITarget"]);

                Assert.Equal(candidate, oracle.Candidate);
                Assert.Equal(transformed, oracle.ContainsValueTextTransformation);
                Assert.Equal(oracle, optimizedString);
                Assert.Equal(oracle, buffered);
            }
        }

        string longName = new('A', bufferChars + 8);
        foreach (string content in new[]
                 {
                     $" {longName} ",
                     $" x{longName} ",
                     $" {longName}x ",
                 })
        {
            SourceText text = SourceText.From(content);
            SemanticService.ReferenceTextProbe oracle = SemanticService
                .TestOnlyProbeReferenceText(text, [longName], forceBuffered: false,
                    bufferChars);
            Assert.Equal(oracle,
                SemanticService.TestOnlyProbeReferenceString(text, [longName]));
            Assert.Equal(oracle,
                SemanticService.TestOnlyProbeReferenceText(text, [longName],
                    forceBuffered: true, bufferChars));
        }
    }

    [Fact]
    public void GlobalAliasPrefilterIsConservativeAcrossEveryWindowBoundary()
    {
        const int bufferChars = 32;
        var cases = new (string Text, bool Expected)[]
        {
            ("global using TargetAlias = ITarget;", true),
            (@"global using TargetAlias = \u0049Target;", true),
            ("global using System;", false),
            ("using TargetAlias = ITarget;", false),
            ("global TargetAlias = ITarget;", false),
            ("global using /* false positive is conservative */ =", true),
            (@"gl\u006fbal using TargetAlias = ITarget;", false),
            ("gl\u200cobal using TargetAlias = ITarget;", false),
            ("plain\u200ctext", false),
        };

        foreach ((string payload, bool expected) in cases)
        {
            for (int offset = 0; offset <= bufferChars * 2; offset++)
            {
                SourceText text = SourceText.From(new string(' ', offset) + payload + " ");
                SemanticService.ReferenceTextProbe oracle = SemanticService
                    .TestOnlyProbeReferenceText(text, ["ITarget"], forceBuffered: false,
                        bufferChars);
                SemanticService.ReferenceTextProbe optimizedString = SemanticService
                    .TestOnlyProbeReferenceString(text, ["ITarget"]);
                SemanticService.ReferenceTextProbe buffered = SemanticService
                    .TestOnlyProbeReferenceText(text, ["ITarget"], forceBuffered: true,
                        bufferChars);

                Assert.Equal(expected, oracle.MayContainGlobalAlias);
                Assert.Equal(oracle, optimizedString);
                Assert.Equal(oracle, buffered);
            }
        }
    }

    [Fact]
    public void BufferedProbeObservesCancellationBetweenWindows()
    {
        SourceText text = SourceText.From(new string(' ', 1024));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SemanticService
            .TestOnlyProbeReferenceText(text, ["ITarget"], forceBuffered: true,
                bufferChars: 32, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task LargeGlobalAliasStillWidensItsProjectAndMatchesFullRoslyn()
    {
        string root = Directory.CreateTempSubdirectory("codenav-64-buffered-alias").FullName;
        using var workspace = new AdhocWorkspace();
        try
        {
            ProjectId declarations = ProjectId.CreateNewId("Declarations");
            ProjectId consumers = ProjectId.CreateNewId("Consumers");
            ProjectId nonAlias = ProjectId.CreateNewId("NonAlias");
            ProjectId scattered = ProjectId.CreateNewId("Scattered");
            ProjectId escapedKeyword = ProjectId.CreateNewId("EscapedKeyword");
            ProjectId unrelated = ProjectId.CreateNewId("Unrelated");
            Solution solution = AddProject(workspace.CurrentSolution, declarations,
                    "Declarations")
                .AddDocument(DocumentId.CreateNewId(declarations), "Target.cs",
                    SourceText.From("public interface X { }"),
                    filePath: Path.Combine(root, "Declarations", "Target.cs"));
            solution = AddProject(solution, consumers, "Consumers")
                .AddProjectReference(consumers, new ProjectReference(declarations))
                .AddDocument(DocumentId.CreateNewId(consumers), "Global.cs",
                    LargeTextAt(SemanticService.ReferenceScopeScanBufferChars - 2,
                        "", "global", @" /* comment */
using TargetAlias = \u0058;
class Global { }"),
                    filePath: Path.Combine(root, "Consumers", "Global.cs"))
                .AddDocument(DocumentId.CreateNewId(consumers), "AliasUse.cs",
                    SourceText.From("class AliasUse { TargetAlias value; }"),
                    filePath: Path.Combine(root, "Consumers", "AliasUse.cs"))
                .AddDocument(DocumentId.CreateNewId(consumers), "Noise.cs",
                    SourceText.From("class ConsumerNoise { }"),
                    filePath: Path.Combine(root, "Consumers", "Noise.cs"));
            solution = AddProject(solution, nonAlias, "NonAlias")
                .AddProjectReference(nonAlias, new ProjectReference(declarations))
                .AddDocument(DocumentId.CreateNewId(nonAlias), "GlobalStatic.cs",
                    LargeTextAt(SemanticService.ReferenceScopeScanBufferChars - 2,
                        "global\nusing static ", "X", ";"),
                    filePath: Path.Combine(root, "NonAlias", "GlobalStatic.cs"))
                .AddDocument(DocumentId.CreateNewId(nonAlias), "Noise.cs",
                    SourceText.From("class NonAliasNoise { }"),
                    filePath: Path.Combine(root, "NonAlias", "Noise.cs"));
            solution = AddProject(solution, scattered, "Scattered")
                .AddProjectReference(scattered, new ProjectReference(declarations))
                .AddDocument(DocumentId.CreateNewId(scattered), "Scattered.cs",
                    LargeTextAt(SemanticService.ReferenceScopeScanBufferChars - 2,
                        "// global using = ", "X", "\nclass Scattered { }"),
                    filePath: Path.Combine(root, "Scattered", "Scattered.cs"))
                .AddDocument(DocumentId.CreateNewId(scattered), "Noise.cs",
                    SourceText.From("class ScatteredNoise { }"),
                    filePath: Path.Combine(root, "Scattered", "Noise.cs"));
            solution = AddProject(solution, escapedKeyword, "EscapedKeyword")
                .AddProjectReference(escapedKeyword, new ProjectReference(declarations))
                .AddDocument(DocumentId.CreateNewId(escapedKeyword), "EscapedGlobal.cs",
                    LargeTextAt(SemanticService.ReferenceScopeScanBufferChars - 2,
                        @"gl\u006fbal using WouldBeAlias = ", "X", ";"),
                    filePath: Path.Combine(root, "EscapedKeyword", "EscapedGlobal.cs"))
                .AddDocument(DocumentId.CreateNewId(escapedKeyword), "AliasUse.cs",
                    SourceText.From("class EscapedAliasUse { WouldBeAlias value; }"),
                    filePath: Path.Combine(root, "EscapedKeyword", "AliasUse.cs"));
            solution = AddProject(solution, unrelated, "Unrelated")
                .AddDocument(DocumentId.CreateNewId(unrelated), "Noise.cs",
                    SourceText.From("class UnrelatedNoise { }"),
                    filePath: Path.Combine(root, "Unrelated", "Noise.cs"));

            Compilation compilation = (await solution.GetProject(declarations)!
                .GetCompilationAsync())!;
            INamedTypeSymbol symbol = compilation.GetTypeByMetadataName("X")!;
            using var manager = new IndexManager(root, Path.Combine(root, "unused.db"));
            using var semantic = new SemanticService(manager);

            SemanticService.ReferenceDocumentScope scope = await semantic
                .PlanReferenceDocumentScopeAsync(symbol, solution, CancellationToken.None);

            Assert.NotNull(scope.Documents);
            Assert.Equal(11, scope.Stats.SolutionDocuments);
            Assert.Equal(5, scope.Stats.CandidateDocuments);
            Assert.Equal(7, scope.Stats.ScopedDocuments);
            Assert.Equal(1, scope.Stats.AliasWidenedProjects);
            Assert.Equal(2, scope.Stats.TransformedIncludedDocuments);
            Assert.Contains(scope.Documents!, document => document.Name == "AliasUse.cs");
            Assert.Contains(scope.Documents!, document => document.Name == "Noise.cs" &&
                document.Project.Id == consumers);
            Assert.DoesNotContain(scope.Documents!, document =>
                document.Name == "Noise.cs" && document.Project.Id == nonAlias);
            Assert.DoesNotContain(scope.Documents!, document =>
                document.Name == "Noise.cs" && document.Project.Id == scattered);
            Assert.DoesNotContain(scope.Documents!, document =>
                document.Name == "AliasUse.cs" && document.Project.Id == escapedKeyword);
            Assert.DoesNotContain(scope.Documents!, document =>
                document.Project.Id == unrelated);

            IEnumerable<ReferencedSymbol> full = await SymbolFinder.FindReferencesAsync(
                symbol, solution, CancellationToken.None);
            IEnumerable<ReferencedSymbol> scoped = await SymbolFinder.FindReferencesAsync(
                symbol, solution, scope.Documents!, CancellationToken.None);
            Assert.Equal(CanonicalLocations(full), CanonicalLocations(scoped));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task LargeDocumentsKeepScopeCountersAndFullRoslynParityAcrossBoundaries()
    {
        string root = Directory.CreateTempSubdirectory("codenav-64-buffered-scope").FullName;
        using var workspace = new AdhocWorkspace();
        try
        {
            int boundary = SemanticService.ReferenceScopeScanBufferChars;
            ProjectId projectId = ProjectId.CreateNewId("P");
            Solution solution = AddProject(workspace.CurrentSolution, projectId, "P")
                .AddMetadataReference(projectId,
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddDocument(DocumentId.CreateNewId(projectId), "Target.cs",
                    SourceText.From("public interface ITarget { }"),
                    filePath: Path.Combine(root, "Target.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "ExactCross.cs",
                    LargeTextAt(boundary - 2, "class ExactCross { ", "ITarget",
                        " value; }"), filePath: Path.Combine(root, "ExactCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "PrefixCross.cs",
                    LargeTextAt(boundary - 2, "class PrefixCross { x", "ITarget",
                        " value; }"), filePath: Path.Combine(root, "PrefixCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "SuffixCross.cs",
                    LargeTextAt(boundary - 2, "class SuffixCross { ", "ITarget",
                        "Extended value; }"), filePath: Path.Combine(root, "SuffixCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "EscapeCross.cs",
                    LargeTextAt(boundary - 1, "class EscapeCross { ", @"\u0049Target",
                        " value; }"), filePath: Path.Combine(root, "EscapeCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "EntityCross.cs",
                    LargeTextAt(boundary - 1, "/// <summary><see cref=\"",
                        "&#73;Target", "\"/></summary>\nclass EntityCross { }"),
                    filePath: Path.Combine(root, "EntityCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "FormatCross.cs",
                    LargeTextAt(boundary - 1, "class FormatCross { ", "I\u200cTarget",
                        " value; }"), filePath: Path.Combine(root, "FormatCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "SupplementaryFormatCross.cs",
                    LargeTextAt(boundary - 1, "// ", char.ConvertFromUtf32(0xE0001),
                        "\nclass SupplementaryFormatCross { }"),
                    filePath: Path.Combine(root, "SupplementaryFormatCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "LongEntityCross.cs",
                    LargeTextAt(boundary - 1, "/// <summary>",
                        "&#" + new string('7', boundary + 17) + ";",
                        "</summary>\nclass LongEntityCross { }"),
                    filePath: Path.Combine(root, "LongEntityCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "InvalidEscapeCross.cs",
                    LargeTextAt(boundary - 1, "// ", @"\u049",
                        "\nclass InvalidEscapeCross { }"),
                    filePath: Path.Combine(root, "InvalidEscapeCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "InvalidEntityCross.cs",
                    LargeTextAt(boundary - 1, "// ", "&#73",
                        "\nclass InvalidEntityCross { }"),
                    filePath: Path.Combine(root, "InvalidEntityCross.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "WindowsPathCross.cs",
                    LargeTextAt(boundary - 1, "class WindowsPathCross { string p = @\"",
                        @"C:\Users\ITargets", "\"; }"),
                    filePath: Path.Combine(root, "WindowsPathCross.cs"));

            Compilation compilation = (await solution.GetProject(projectId)!
                .GetCompilationAsync())!;
            INamedTypeSymbol symbol = compilation.GetTypeByMetadataName("ITarget")!;
            using var manager = new IndexManager(root, Path.Combine(root, "unused.db"));
            using var semantic = new SemanticService(manager);

            SemanticService.ReferenceDocumentScope scope = await semantic
                .PlanReferenceDocumentScopeAsync(symbol, solution, CancellationToken.None);

            Assert.NotNull(scope.Documents);
            Assert.Equal(12, scope.Stats.SolutionDocuments);
            Assert.Equal(7, scope.Stats.CandidateDocuments);
            Assert.Equal(7, scope.Stats.ScopedDocuments);
            Assert.Equal(5, scope.Stats.TransformedIncludedDocuments);
            Assert.Equal(0, scope.Stats.AliasWidenedProjects);
            Assert.Equal(new[]
            {
                "EntityCross.cs",
                "EscapeCross.cs",
                "ExactCross.cs",
                "FormatCross.cs",
                "LongEntityCross.cs",
                "SupplementaryFormatCross.cs",
                "Target.cs",
            }, scope.Documents!.Select(document => document.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());

            IEnumerable<ReferencedSymbol> full = await SymbolFinder.FindReferencesAsync(
                symbol, solution, CancellationToken.None);
            IEnumerable<ReferencedSymbol> scoped = await SymbolFinder.FindReferencesAsync(
                symbol, solution, scope.Documents!, CancellationToken.None);
            Assert.Equal(CanonicalLocations(full), CanonicalLocations(scoped));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static SourceText LargeTextAt(int tokenStart, string prefix, string token,
        string suffix)
    {
        int leadingSpaces = tokenStart - prefix.Length;
        Assert.True(leadingSpaces >= 0);
        string content = new string(' ', leadingSpaces) + prefix + token + suffix;
        int minimumLength = SemanticService.ReferenceScopeFastStringLimitChars + 128;
        if (content.Length < minimumLength)
            content += new string(' ', minimumLength - content.Length);
        return SourceText.From(content);
    }

    private static Solution AddProject(Solution solution, ProjectId id, string name)
        => solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name,
            LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary)));

    private static string[] CanonicalLocations(IEnumerable<ReferencedSymbol> symbols)
        => symbols.SelectMany(referenced => referenced.Locations)
            .Where(location => location.Location.IsInSource)
            .Select(location => $"{location.Document.Project.Name}|{location.Document.Name}|" +
                $"{location.Location.SourceSpan.Start}|{location.Location.SourceSpan.Length}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
