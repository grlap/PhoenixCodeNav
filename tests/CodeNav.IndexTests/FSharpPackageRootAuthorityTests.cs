using CodeNav.Core.Semantic;
using System.Text.Json;

namespace CodeNav.Tests;

[Collection(CSharpCpmEnvironmentIsolationCollection.Name)]
public sealed class FSharpPackageRootAuthorityTests
{
    [Fact]
    public void ExplicitGlobalPackagesRootIsExclusiveOutsideTheWorkspace()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "codenav-fsharp-root-authority");
        string workspace = Path.Combine(sandbox, "workspace");
        string configured = Path.Combine(sandbox, "configured-packages");
        string profile = Path.Combine(sandbox, "profile");
        string defaultPackages = Path.Combine(profile, ".nuget", "packages");

        Assert.True(SemanticService.IsAllowedFSharpPackageRootForEnvironment(
            configured, workspace, configured, profile));
        Assert.True(SemanticService.IsAllowedFSharpPackageRootForEnvironment(
            Path.Combine(workspace, ".packages"), workspace, configured, profile));
        Assert.False(SemanticService.IsAllowedFSharpPackageRootForEnvironment(
            defaultPackages, workspace, configured, profile));
    }

    [Fact]
    public void MissingGlobalPackagesOverridePreservesTheUserProfileCache()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "codenav-fsharp-root-default");
        string workspace = Path.Combine(sandbox, "workspace");
        string profile = Path.Combine(sandbox, "profile");
        string defaultPackages = Path.Combine(profile, ".nuget", "packages");

        Assert.True(SemanticService.IsAllowedFSharpPackageRootForEnvironment(
            defaultPackages, workspace, null, profile));
        Assert.True(SemanticService.IsAllowedFSharpPackageRootForEnvironment(
            defaultPackages, workspace, "", profile));
        Assert.False(SemanticService.IsAllowedFSharpPackageRootForEnvironment(
            Path.Combine(sandbox, "other-packages"), workspace, null, profile));
    }

    [Fact]
    public void GlobalPackagesRootUsesTheSameExplicitAndDefaultBranches()
    {
        string profile = Path.Combine(Path.GetTempPath(), "codenav-global-packages-profile");
        string configured = Path.Combine(Path.GetTempPath(), "codenav-global-packages-explicit");

        Assert.Equal(configured,
            ReferenceAssemblyLocator.GlobalPackagesRootForEnvironment(configured, profile));
        Assert.Equal(Path.Combine(profile, ".nuget", "packages"),
            ReferenceAssemblyLocator.GlobalPackagesRootForEnvironment(null, profile));
    }

    [Fact]
    public void AssetsFoldersSelectOnlyTheConfiguredExternalRoot()
    {
        string sandbox = Directory.CreateTempSubdirectory(
            "codenav-fsharp-assets-root-explicit").FullName;
        try
        {
            string workspace = Path.Combine(sandbox, "workspace");
            string configured = Path.Combine(sandbox, "configured-packages");
            string profile = Path.Combine(sandbox, "profile");
            string defaultPackages = Path.Combine(profile, ".nuget", "packages");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(configured);
            Directory.CreateDirectory(defaultPackages);
            using JsonDocument assets = PackageFolders(configured, defaultPackages);

            Assert.True(SemanticService.TryGetFSharpPackageRootsForEnvironment(
                assets.RootElement, CancellationToken.None, workspace, configured, profile,
                out List<string>? roots));
            Assert.Equal([Path.GetFullPath(configured)], roots);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(sandbox);
        }
    }

    [Fact]
    public void AssetsFoldersPreserveTheDefaultCacheWithoutAnOverride()
    {
        string sandbox = Directory.CreateTempSubdirectory(
            "codenav-fsharp-assets-root-default").FullName;
        try
        {
            string workspace = Path.Combine(sandbox, "workspace");
            string profile = Path.Combine(sandbox, "profile");
            string defaultPackages = Path.Combine(profile, ".nuget", "packages");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(defaultPackages);
            using JsonDocument assets = PackageFolders(defaultPackages);

            Assert.True(SemanticService.TryGetFSharpPackageRootsForEnvironment(
                assets.RootElement, CancellationToken.None, workspace, null, profile,
                out List<string>? roots));
            Assert.Equal([Path.GetFullPath(defaultPackages)], roots);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(sandbox);
        }
    }

    [Fact]
    public void ExplicitEmptyFrameworkOverrideDegradesWithoutFallingBackToTheHost()
    {
        string emptyReferences = Directory.CreateTempSubdirectory(
            "codenav-empty-net472-references").FullName;
        string? prior = Environment.GetEnvironmentVariable("CODENAV_NET472_REFS");
        try
        {
            Environment.SetEnvironmentVariable("CODENAV_NET472_REFS", emptyReferences);
            ReferenceAssemblyLocator.ResetCachesForTests();

            Assert.Empty(ReferenceAssemblyLocator.Net472References(out string? csharpSource));
            Assert.Null(csharpSource);
            Assert.Empty(ReferenceAssemblyLocator.FrameworkReferencePaths(
                "net472", out string? fsharpSource));
            Assert.Null(fsharpSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODENAV_NET472_REFS", prior);
            ReferenceAssemblyLocator.ResetCachesForTests();
            TestWorkspaceCleanup.DeleteWorkspace(emptyReferences);
        }
    }

    [Fact]
    public void MissingFrameworkOverrideDegradesWithoutFallingBackToTheHost()
    {
        string sandbox = Directory.CreateTempSubdirectory(
            "codenav-missing-net472-references").FullName;
        try
        {
            AssertRejectedFrameworkOverride(Path.Combine(sandbox, "does-not-exist"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(sandbox);
        }
    }

    [Fact]
    public void PartialManagedFrameworkOverrideIsNotAdvertisedAsAvailable()
    {
        string references = Directory.CreateTempSubdirectory(
            "codenav-partial-net472-references").FullName;
        try
        {
            File.Copy(typeof(object).Assembly.Location,
                Path.Combine(references, "Unrelated.Managed.Assembly.dll"));
            AssertRejectedFrameworkOverride(references);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(references);
        }
    }

    [Fact]
    public void CorruptRequiredFrameworkAssembliesAreNotAdvertisedAsAvailable()
    {
        string references = Directory.CreateTempSubdirectory(
            "codenav-corrupt-net472-references").FullName;
        try
        {
            foreach (string fileName in new[] { "mscorlib.dll", "System.dll", "System.Core.dll" })
                File.WriteAllText(Path.Combine(references, fileName), "not a managed assembly");
            AssertRejectedFrameworkOverride(references);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(references);
        }
    }

    private static void AssertRejectedFrameworkOverride(string configuredPath)
    {
        string? prior = Environment.GetEnvironmentVariable("CODENAV_NET472_REFS");
        try
        {
            Environment.SetEnvironmentVariable("CODENAV_NET472_REFS", configuredPath);
            ReferenceAssemblyLocator.ResetCachesForTests();

            Assert.Empty(ReferenceAssemblyLocator.Net472References(out string? csharpSource));
            Assert.Null(csharpSource);
            Assert.Empty(ReferenceAssemblyLocator.FrameworkReferencePaths(
                "net472", out string? fsharpSource));
            Assert.Null(fsharpSource);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODENAV_NET472_REFS", prior);
            ReferenceAssemblyLocator.ResetCachesForTests();
        }
    }

    private static JsonDocument PackageFolders(params string[] roots)
    {
        var packageFolders = roots.ToDictionary(
            root => Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            _ => new { });
        return JsonDocument.Parse(JsonSerializer.Serialize(new { packageFolders }));
    }
}
