using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using System.Text;

namespace CodeNav.Core.Semantic;

public sealed partial class SemanticService
{
    public ProjectModelMode SelectedFSharpProjectModel { get; }

    private FSharpSemanticOptionsSnapshot SimpleFSharpSemanticOptions(IndexQueries queries,
        ProjectRow owner, string xml, string framework, CancellationToken cancellationToken)
    {
        return SimpleProjectModelBuilder.BuildFSharp(owner.Path, xml, owner.Tfms, framework,
            queries.FSharpProjectFiles(owner.Id, cancellationToken), () =>
            {
                string directory = WorkspacePaths.ToGitPath(Path.GetDirectoryName(owner.Path) ?? "");
                string packagesPath = directory.Length == 0 ? "packages.config" : $"{directory}/packages.config";
                string? packages = queries.ContentByPathBounded(packagesPath,
                    IndexBuilder.MaxStructuralFileBytes, cancellationToken);
                return packages is null ? null : Encoding.UTF8.GetBytes(packages);
            }, cancellationToken);
    }

    private FSharpPackageAssetsSnapshot SimpleFSharpPackageAssets(
        IReadOnlyCollection<FSharpPackageReferenceSnapshot> packages, CancellationToken cancellationToken,
        ISet<string> reasons)
    {
        var assets = new List<FSharpPackageCompileAsset>();
        foreach (FSharpPackageReferenceSnapshot package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The C# navigation heuristic: available direct package DLLs, no restore/import proof.
            // Model provenance qualifies coverage, not whether FCS resolved a symbol.
            if (!package.IncludeCompileAssets) continue;
            string? dll = ReferenceAssemblyLocator.ResolvePackageDll(package.Id, package.RequestedVersion ?? "");
            if (dll is not null)
                assets.Add(new($"simple-package:{package.Id}", dll, ReferenceAssemblyLocator.GlobalPackagesRoot()));
            else
                reasons.Add("fsharp_simple_package_reference_unavailable");
        }
        return new("simple-project-model", assets);
    }
}
