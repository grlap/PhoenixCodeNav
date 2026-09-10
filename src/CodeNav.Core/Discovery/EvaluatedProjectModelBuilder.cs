using System.Xml;
using System.Xml.Linq;
using static CodeNav.Core.Discovery.ProjectFileParser;

namespace CodeNav.Core.Discovery;

/// <summary>Opt-in bounded F# MSBuild evaluation, separate from the default raw project model.</summary>
internal static class EvaluatedProjectModelBuilder
{
    internal static FSharpSemanticOptionsSnapshot Build(
        string relPath, string projectXml, string indexedTargetFrameworks,
        string selectedTargetFramework,
        Func<string, string?>? importResolver,
        Func<string, long?>? importSizeResolver,
        string? directoryPackagesPropsPath,
        string? directoryBuildPropsPath,
        string? directoryBuildTargetsPath,
        CancellationToken cancellationToken,
        bool hasAmbiguousDirectoryBuildAuthority,
        bool hasAmbiguousDirectoryPackagesAuthority,
        FSharpSemanticEvaluationBudget budget,
        Func<string, bool?>? existsResolver,
        string? workspaceRoot, string diagnosticOrigin)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FSharpParsingOptionsSnapshot selection = ParseFSharpParsingOptionsSnapshot(
            relPath, projectXml, indexedTargetFrameworks, selectedTargetFramework);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Error is not null || selection.SelectedTargetFramework is null)
        {
            return new([], [], [], Path.GetFileNameWithoutExtension(relPath),
                selectedTargetFramework, selection.PartialReason,
                selection.Error ?? "fsharp_project_options_unavailable");
        }
        if (hasAmbiguousDirectoryBuildAuthority)
        {
            return new([], [], [], Path.GetFileNameWithoutExtension(relPath),
                selection.SelectedTargetFramework, selection.PartialReason,
                "fsharp_semantic_directory_build_ambiguous");
        }
        if (hasAmbiguousDirectoryPackagesAuthority)
        {
            return new([], [], [], Path.GetFileNameWithoutExtension(relPath),
                selection.SelectedTargetFramework, selection.PartialReason,
                "fsharp_semantic_directory_packages_ambiguous");
        }

        XDocument doc;
        try
        {
            if (projectXml.Length > MaxSnapshotBytes)
                throw new InvalidDataException("project XML exceeds the snapshot limit");
            using var input = new StringReader(projectXml);
            using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxSnapshotBytes,
            });
            doc = XDocument.Load(reader, LoadOptions.None);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new([], [], [], Path.GetFileNameWithoutExtension(relPath),
                selectedTargetFramework, selection.PartialReason,
                "fsharp_project_options_unavailable");
        }

        XElement? root = doc.Root;
        if (root is null)
        {
            return new([], [], [], Path.GetFileNameWithoutExtension(relPath),
                selectedTargetFramework, selection.PartialReason,
                "fsharp_project_options_unavailable");
        }

        using var diagnostics = Diagnostics.MsBuildDiagnosticSession.Start(workspaceRoot,
            relPath, selection.SelectedTargetFramework, diagnosticOrigin);
        var evaluator = new FSharpSemanticProjectEvaluator(relPath,
            selection.SelectedTargetFramework,
            selection.AvailableTargetFrameworks?.ToArray() ?? [], importResolver,
            importSizeResolver, directoryPackagesPropsPath, directoryBuildPropsPath,
            directoryBuildTargetsPath, cancellationToken, budget, existsResolver, workspaceRoot, diagnostics);
        FSharpSemanticEvaluation evaluation = evaluator.Evaluate(root);
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics?.End(evaluation.Error, evaluation.PartialReason);
        if (evaluation.Error is not null)
        {
            return new FSharpSemanticOptionsSnapshot([], evaluation.CommandLineArgs,
                evaluation.HintPathReferences,
                evaluation.AssemblyName, selection.SelectedTargetFramework,
                evaluation.PartialReason ?? selection.PartialReason, evaluation.Error,
                evaluation.BareReferences, evaluation.PackageReferences)
            {
                ProjectReferences = evaluation.ProjectReferences,
                ProjectReferencesTransitive = evaluation.ProjectReferencesTransitive,
                ExistsDependencies = evaluation.ExistsDependencies,
            };
        }

        return new FSharpSemanticOptionsSnapshot(evaluation.SourceFiles,
            evaluation.CommandLineArgs,
            evaluation.HintPathReferences, evaluation.AssemblyName,
            selection.SelectedTargetFramework, evaluation.PartialReason,
            BareReferences: evaluation.BareReferences,
            PackageReferences: evaluation.PackageReferences)
        {
            ProjectReferences = evaluation.ProjectReferences,
            ProjectReferencesTransitive = evaluation.ProjectReferencesTransitive,
            ExistsDependencies = evaluation.ExistsDependencies,
        };
    }

}
