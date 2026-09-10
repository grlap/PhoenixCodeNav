using System.Xml.Linq;
using static CodeNav.Core.Discovery.ProjectFileParser;

namespace CodeNav.Core.Discovery;

public static partial class SimpleProjectModelBuilder
{
    // One project XML parse, the same raw inputs as C#, plus the FCS source-order/flags adapter.
    internal static FSharpSemanticOptionsSnapshot BuildFSharp(
        string path, string xml, string frameworks, string selectedFramework,
        IReadOnlyList<string> indexedSources, Func<byte[]?> packagesConfig,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            XDocument document = ReadProject(xml);
            XDocument? packages = null;
            if (document.Root is { } root && !IsSdkProject(root))
            {
                byte[]? bytes = packagesConfig();
                if (bytes is not null)
                {
                    try { packages = LoadSnapshotXml(bytes); }
                    catch { /* Same optional packages.config policy as C#. */ }
                }
            }
            return BuildFSharp(path, document, frameworks, selectedFramework, indexedSources,
                packages, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new([], [], [], Path.GetFileNameWithoutExtension(path), selectedFramework,
                SimpleFSharpProjectModelReason, "fsharp_project_options_unavailable");
        }
    }

    // Dependent discovery needs only direct project edges, never packages or source membership.
    internal static FSharpSemanticOptionsSnapshot BuildFSharpReferences(string path, string xml,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ParsedProject project = ParseDocuments(path, ReadProject(xml), null);
            return new([], [], [], project.Name, "", SimpleFSharpProjectModelReason)
            {
                ProjectReferences = project.ProjectRefRelPaths
                    .Where(reference => !ContainsMsBuildExpression(reference))
                    .Select(reference => new FSharpProjectReferenceSnapshot(reference)).ToList(),
                ProjectReferencesTransitive = project.Style == "sdk",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new([], [], [], Path.GetFileNameWithoutExtension(path), "",
                SimpleFSharpProjectModelReason, "fsharp_project_options_unavailable");
        }
    }

    private static FSharpSemanticOptionsSnapshot BuildFSharp(
        string path, XDocument document, string frameworks, string selectedFramework,
        IReadOnlyList<string> indexedSources, XDocument? packagesConfig,
        CancellationToken cancellationToken)
    {
        FSharpParsingOptionsSnapshot parsing = ParseFSharpParsingOptionsSnapshot(
            document, frameworks, selectedFramework);
        ParsedProject project = ParseDocuments(path, document, packagesConfig);
        ParsedProject shape = ParseCompileShape(path, document);
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
                sources.RemoveAll(source => MsBuildGlob.IsMatch(source, operation.Pattern, ignoreCase: OperatingSystem.IsWindows()));
                included.IntersectWith(sources);
                continue;
            }
            IEnumerable<string> matches = MsBuildGlob.ContainsWildcard(operation.Pattern)
                ? candidates.Where(source => MsBuildGlob.IsMatch(source, operation.Pattern, ignoreCase: OperatingSystem.IsWindows()))
                : sourceByPath.TryGetValue(operation.Pattern, out string? literal) ? [literal] : [];
            foreach (string source in matches)
            {
                if (!(operation.Excludes?.Any(exclude => MsBuildGlob.IsMatch(source, exclude, ignoreCase: OperatingSystem.IsWindows())) ?? false))
                    Include(source);
            }
        }

        return new(sources, parsing.CommandLineArgs,
            project.AssemblyRefs.Where(reference => reference.HintPath is not null &&
                    !ContainsMsBuildExpression(reference.HintPath))
                .Select(reference => reference.HintPath!).Distinct(WorkspacePaths.FileSystemPathComparer).ToList(),
            project.Name, selectedFramework, reasons,
            BareReferences: project.AssemblyRefs.Where(reference => reference.HintPath is null &&
                    !ContainsMsBuildExpression(reference.Assembly))
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
