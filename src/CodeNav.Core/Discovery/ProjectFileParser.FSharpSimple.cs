using System.Text;

namespace CodeNav.Core.Discovery;

public static partial class ProjectFileParser
{
    internal const string SimpleFSharpProjectModelReason = "fsharp_semantic_simple_project_model";

    // Navigation pilot: reuse the same raw project facts as C#, not the MSBuild evaluator.
    // Indexed ownership bounds the candidates; the authored Compile sequence supplies F# order.
    internal static FSharpSemanticOptionsSnapshot ParseSimpleFSharpSemanticOptions(
        string path, string xml, string frameworks, string selectedFramework,
        IReadOnlyList<string> indexedSources, byte[]? packagesConfig,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FSharpParsingOptionsSnapshot parsing = ParseFSharpParsingOptionsSnapshot(
            path, xml, frameworks, selectedFramework);
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        ParsedProject project = ParseSnapshot(path, bytes, packagesConfig);
        ParsedProject shape = ParseCompileShape(path, bytes);
        string reasons = string.IsNullOrEmpty(parsing.PartialReason)
            ? SimpleFSharpProjectModelReason
            : $"{SimpleFSharpProjectModelReason};{parsing.PartialReason}";
        if (parsing.Error is not null || project.LoadStatus.StartsWith("failed:", StringComparison.Ordinal) ||
            shape.LoadStatus.StartsWith("failed:", StringComparison.Ordinal))
            return new([], [], [], project.Name, selectedFramework, reasons,
                parsing.Error ?? "fsharp_project_options_unavailable");

        string[] candidates = indexedSources.Where(source =>
                source.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase))
            .Distinct(WorkspacePaths.FileSystemPathComparer)
            .OrderBy(source => source, WorkspacePaths.FileSystemPathComparer).ToArray();
        var sources = new List<string>();
        var sourceByPath = candidates.ToDictionary(source => source, WorkspacePaths.FileSystemPathComparer);
        var included = new HashSet<string>(WorkspacePaths.FileSystemPathComparer);
        void Include(string source)
        {
            if (included.Add(source)) sources.Add(source);
        }
        if (shape.DefaultCompileItems)
        {
            foreach (string source in candidates.Where(source => source.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)))
                Include(source);
        }
        foreach (CompileMembershipOperation operation in shape.CompileOperations ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContainsMsBuildExpression(operation.Pattern)) continue;
            if (!operation.Include)
            {
                sources.RemoveAll(source => MsBuildGlob.IsMatch(source, operation.Pattern));
                included.IntersectWith(sources);
                continue;
            }
            IEnumerable<string> matches = MsBuildGlob.ContainsWildcard(operation.Pattern)
                ? candidates.Where(source => MsBuildGlob.IsMatch(source, operation.Pattern))
                : sourceByPath.TryGetValue(operation.Pattern, out string? literal) ? [literal] : [];
            foreach (string source in matches)
            {
                if (!(operation.Excludes?.Any(exclude => MsBuildGlob.IsMatch(source, exclude)) ?? false))
                    Include(source);
            }
        }

        return new(sources, parsing.CommandLineArgs,
            project.AssemblyRefs.Where(reference => reference.HintPath is not null &&
                    !ContainsMsBuildExpression(reference.HintPath))
                .Select(reference => reference.HintPath!).Distinct(WorkspacePaths.FileSystemPathComparer).ToList(),
            project.Name, selectedFramework, reasons,
            BareReferences: project.AssemblyRefs.Where(reference => reference.HintPath is null)
                .Select(reference => reference.Assembly).ToList(),
            PackageReferences: project.PackageRefs.Where(reference =>
                    !ContainsMsBuildExpression(reference.Package) && !ContainsMsBuildExpression(reference.Version))
                .Select(reference => new FSharpPackageReferenceSnapshot(reference.Package, reference.Version)).ToList())
        {
            ProjectReferences = project.ProjectRefRelPaths.Where(reference => !ContainsMsBuildExpression(reference))
                .Select(reference => new FSharpProjectReferenceSnapshot(reference)).ToList(),
            ProjectReferencesTransitive = project.Style == "sdk",
        };
    }
}
