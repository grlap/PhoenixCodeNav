namespace CodeNav.Core.Discovery;

public static partial class ProjectFileParser
{
    internal const string SimpleFSharpProjectModelReason = "fsharp_semantic_simple_project_model";

    internal static FSharpSemanticOptionsSnapshot ParseSimpleFSharpSemanticOptions(
        string path, string xml, string frameworks, string selectedFramework,
        IReadOnlyList<string> indexedSources, byte[]? packagesConfig,
        CancellationToken cancellationToken) =>
        SimpleProjectModelBuilder.BuildFSharp(path, xml, frameworks, selectedFramework,
            indexedSources, () => packagesConfig, cancellationToken);
}
