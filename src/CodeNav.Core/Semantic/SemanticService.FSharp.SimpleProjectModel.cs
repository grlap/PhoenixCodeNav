using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using System.Text;

namespace CodeNav.Core.Semantic;

public enum FSharpProjectModel
{
    Evaluated,
    Simple,
}

public sealed partial class SemanticService
{
    public FSharpProjectModel SelectedFSharpProjectModel { get; }

    private FSharpProjectModel SelectFSharpProjectModel(FSharpProjectModel? requested)
    {
        // An explicit caller selection wins; otherwise capture process configuration once.
        if (requested is { } value) return value;
        string? configured = Environment.GetEnvironmentVariable("PHOENIX_FSHARP_PROJECT_MODEL")?.Trim();
        if (string.Equals(configured, "simple", StringComparison.OrdinalIgnoreCase))
            return FSharpProjectModel.Simple;
        if (!string.IsNullOrEmpty(configured) &&
            !configured.Equals("evaluated", StringComparison.OrdinalIgnoreCase))
            _log("Unrecognized PHOENIX_FSHARP_PROJECT_MODEL; using evaluated. Expected simple or evaluated.");
        return FSharpProjectModel.Evaluated;
    }

    private FSharpSemanticOptionsSnapshot SimpleFSharpSemanticOptions(IndexQueries queries,
        ProjectRow owner, string xml, string framework, CancellationToken cancellationToken)
    {
        string directory = WorkspacePaths.ToGitPath(Path.GetDirectoryName(owner.Path) ?? "");
        string packagesPath = directory.Length == 0 ? "packages.config" : $"{directory}/packages.config";
        string? packages = queries.ContentByPathBounded(packagesPath,
            IndexBuilder.MaxStructuralFileBytes, cancellationToken);
        return ProjectFileParser.ParseSimpleFSharpSemanticOptions(owner.Path, xml, owner.Tfms,
            framework, queries.FSharpProjectFiles(owner.Id, cancellationToken),
            packages is null ? null : Encoding.UTF8.GetBytes(packages), cancellationToken);
    }

    private FSharpPackageAssetsSnapshot SimpleFSharpPackageAssets(
        IReadOnlyCollection<FSharpPackageReferenceSnapshot> packages, CancellationToken cancellationToken)
    {
        var assets = new List<FSharpPackageCompileAsset>();
        foreach (FSharpPackageReferenceSnapshot package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The C# navigation heuristic: available direct package DLLs, no restore/import proof.
            // The model reason always downgrades this result, including successful resolutions.
            if (!package.IncludeCompileAssets) continue;
            string? dll = ReferenceAssemblyLocator.ResolvePackageDll(package.Id, package.RequestedVersion ?? "");
            if (dll is not null)
                assets.Add(new($"simple-package:{package.Id}", dll, ReferenceAssemblyLocator.GlobalPackagesRoot()));
        }
        return new("simple-project-model", assets);
    }
}
