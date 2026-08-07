using System.Text;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using Microsoft.CodeAnalysis;

namespace CodeNav.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CSharpCpmEnvironmentIsolationCollection
{
    public const string Name = "C# CPM environment isolation";
}

[Collection(CSharpCpmEnvironmentIsolationCollection.Name)]
public sealed class CSharpCentralPackageManagementTests
{
    private const string PackageId = "Microsoft.CodeAnalysis.CSharp";

    [Fact]
    public async Task CentralPackageVersionSuppliesCSharpCompilerReference()
    {
        string root = Directory.CreateTempSubdirectory("codenav-csharp-cpm").FullName;
        try
        {
            WriteWorkspace(root, "5.6");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var workspace = new SemanticWorkspace(root, dbPath);
            using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(
                ["Cpm.Consumer"], CancellationToken.None);

            Project project = Assert.Single(lease.Solution.Projects);
            Assert.Contains("5.6.0", PackageReferencePath(project),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CentralPackageVersionRefreshReloadsTheWarmCSharpProject()
    {
        string root = Directory.CreateTempSubdirectory("codenav-csharp-cpm-refresh").FullName;
        try
        {
            WriteWorkspace(root, "5.6.0");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20000));

            var log = new List<string>();
            using var workspace = new SemanticWorkspace(root, dbPath, log.Add);
            using (SemanticSolutionLease before = await workspace.EnsureLoadedAsync(
                       ["Cpm.Consumer"], CancellationToken.None))
            {
                Assert.Contains("5.6.0", PackageReferencePath(
                    Assert.Single(before.Solution.Projects)), StringComparison.OrdinalIgnoreCase);
            }

            WriteDirectoryPackages(root, "3.6.0");
            Assert.True(manager.RequestRefresh(["Directory.Packages.props"]));
            Assert.True(WaitUntil(() =>
            {
                using IndexQueries queries = manager.OpenQueries();
                return queries.ContentByPath("Directory.Packages.props")?.Contains(
                    "3.6.0", StringComparison.Ordinal) == true;
            }, 20000));

            using SemanticSolutionLease after = await workspace.EnsureLoadedAsync(
                ["Cpm.Consumer"], CancellationToken.None);
            Assert.Contains("3.6.0", PackageReferencePath(
                Assert.Single(after.Solution.Projects)), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(log, message => message.Contains(
                "Semantic reload (files changed): Cpm.Consumer", StringComparison.Ordinal));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task NearestCentralPackageAuthoritySuppliesTheProjectVersion()
    {
        string root = Directory.CreateTempSubdirectory("codenav-csharp-cpm-nearest").FullName;
        try
        {
            WriteWorkspace(root, "99.0.0");
            WriteDirectoryPackages(Path.Combine(root, "src"), "5.6.0");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var workspace = new SemanticWorkspace(root, dbPath);
            using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(
                ["Cpm.Consumer"], CancellationToken.None);

            Project project = Assert.Single(lease.Solution.Projects);
            Assert.Empty(lease.Coverage.FailedProjects);
            Assert.Contains("5.6.0", PackageReferencePath(project),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Theory]
    [InlineData("conditioned")]
    [InlineData("imported")]
    [InlineData("project-item-conditioned")]
    [InlineData("project-group-conditioned")]
    [InlineData("project-target")]
    [InlineData("project-choose")]
    public async Task UnsupportedCentralAuthorityPreservesEstablishedCSharpLoading(
        string scenario)
    {
        string root = Directory.CreateTempSubdirectory("codenav-csharp-cpm-fail").FullName;
        try
        {
            WriteWorkspace(root, "5.6.0");
            if (scenario == "conditioned")
            {
                File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), """
                    <Project>
                      <PropertyGroup>
                        <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                      </PropertyGroup>
                      <ItemGroup Condition="'$(Configuration)' == 'Release'">
                        <PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="5.6.0" />
                      </ItemGroup>
                    </Project>
                    """);
            }
            else if (scenario == "imported")
            {
                File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), """
                    <Project>
                      <PropertyGroup>
                        <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                      </PropertyGroup>
                      <Import Project="eng/Packages.props" />
                    </Project>
                    """);
            }
            else
            {
                WriteProjectWithUnsupportedPackageScope(root, scenario);
            }
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var workspace = new SemanticWorkspace(root, dbPath);
            using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(
                ["Cpm.Consumer"], CancellationToken.None);

            Project project = Assert.Single(lease.Solution.Projects);
            Assert.DoesNotContain(project.MetadataReferences.OfType<PortableExecutableReference>(),
                IsPackageReference);
            Assert.Empty(lease.Coverage.FailedProjects);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ConfiguredGlobalPackagesRootSuppliesExactCentralPackage()
    {
        const string packageId = "Phoenix.Redirected.Cpm";
        const string version = "1.2.3";
        string sandbox = Directory.CreateTempSubdirectory("codenav-csharp-cpm-redirect").FullName;
        string packagesRoot = Path.Combine(sandbox, "packages");
        string fixtureDirectory = Path.Combine(packagesRoot, packageId.ToLowerInvariant(), version,
            "lib", "netstandard2.0");
        string fixturePath = Path.Combine(fixtureDirectory, "Phoenix.Redirected.Cpm.dll");
        string workspaceRoot = Path.Combine(sandbox, "workspace");
        string? priorPackagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Directory.CreateDirectory(fixtureDirectory);
            File.Copy(typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.Location,
                fixturePath);
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packagesRoot);

            Directory.CreateDirectory(workspaceRoot);
            WriteWorkspace(workspaceRoot, version, packageId);
            string dbPath = IndexBuilder.DefaultDbPath(workspaceRoot);
            IndexBuilder.Build(workspaceRoot, dbPath);

            using var workspace = new SemanticWorkspace(workspaceRoot, dbPath);
            using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(
                ["Cpm.Consumer"], CancellationToken.None);

            Project project = Assert.Single(lease.Solution.Projects);
            Assert.Empty(lease.Coverage.FailedProjects);
            Assert.Contains(project.MetadataReferences.OfType<PortableExecutableReference>(),
                reference => string.Equals(reference.FilePath, fixturePath,
                    StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", priorPackagesRoot);
            TestWorkspaceCleanup.DeleteWorkspace(sandbox);
        }
    }

    [Theory]
    [InlineData("version-override")]
    [InlineData("global-package-reference")]
    [InlineData("duplicate-package-version")]
    [InlineData("update-package-version")]
    [InlineData("remove-package-version")]
    [InlineData("range-version")]
    [InlineData("floating-version")]
    [InlineData("missing-mpvc")]
    [InlineData("project-disables-mpvc")]
    [InlineData("sdk-central-project")]
    [InlineData("conditioned-package-reference")]
    [InlineData("conditioned-project-item-group")]
    [InlineData("target-scoped-package-reference")]
    [InlineData("choose-scoped-package-reference")]
    [InlineData("ambiguous-authority")]
    public void UnsupportedEvaluatorShapePreservesDirectReference(string scenario)
    {
        (string projectXml, string centralXml, bool ambiguous) = EvaluatorScenario(scenario);

        List<CSharpPackageReferenceSnapshot> result =
            ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
                Encoding.UTF8.GetBytes(projectXml), [(PackageId, "")], centralXml, ambiguous);

        CSharpPackageReferenceSnapshot reference = Assert.Single(result);
        Assert.Equal(PackageId, reference.Id);
        Assert.Equal("", reference.Version);
        Assert.False(reference.CentrallyManaged);
    }

    [Theory]
    [InlineData("1", "1.0.0")]
    [InlineData("01.002", "1.2.0")]
    [InlineData("01.002.0003.0+BUILD.7", "1.2.3")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3-Preview.01+Build.9", "1.2.3-preview.01")]
    public void SupportedCentralVersionIsNormalizedForTheNuGetCache(string input,
        string expected)
    {
        List<CSharpPackageReferenceSnapshot> result =
            ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
                Encoding.UTF8.GetBytes(ProjectXml()), [(PackageId, "")],
                CentralXml(input), hasAmbiguousDirectoryPackagesAuthority: false);

        CSharpPackageReferenceSnapshot reference = Assert.Single(result);
        Assert.Equal(expected, reference.Version);
        Assert.True(reference.CentrallyManaged);
    }

    [Fact]
    public async Task SelectedCentralVersionWithoutExactCacheAssetFailsClosed()
    {
        string root = Directory.CreateTempSubdirectory("codenav-csharp-cpm-missing").FullName;
        try
        {
            WriteWorkspace(root, "99.0.0");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var workspace = new SemanticWorkspace(root, dbPath);
            using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(
                ["Cpm.Consumer"], CancellationToken.None);

            Assert.Empty(lease.Solution.Projects);
            Assert.Equal(["Cpm.Consumer"], lease.Coverage.FailedProjects);
            Assert.NotNull(lease.Coverage.FailedProjectCauses);
            Assert.Equal("csharp_semantic_central_package_asset_unavailable",
                lease.Coverage.FailedProjectCauses["Cpm.Consumer"]);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RestoredCentralPackageWithoutCompatibleCompilerAssetKeepsEstablishedLoading()
    {
        const string packageId = "Microsoft.CodeAnalysis.Analyzers";
        const string version = "5.3.0";
        string packageDirectory = Path.Combine(ReferenceAssemblyLocator.GlobalPackagesRoot(),
            packageId.ToLowerInvariant(), version);
        Assert.True(Directory.Exists(packageDirectory),
            $"Required restored package fixture is missing: {packageDirectory}");
        Assert.False(Directory.Exists(Path.Combine(packageDirectory, "lib")),
            $"Package fixture unexpectedly contains compiler libraries: {packageDirectory}");

        string root = Directory.CreateTempSubdirectory("codenav-csharp-cpm-analyzer").FullName;
        try
        {
            WriteWorkspace(root, version, packageId);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var workspace = new SemanticWorkspace(root, dbPath);
            using SemanticSolutionLease lease = await workspace.EnsureLoadedAsync(
                ["Cpm.Consumer"], CancellationToken.None);

            Assert.Single(lease.Solution.Projects);
            Assert.Empty(lease.Coverage.FailedProjects);
            Assert.DoesNotContain(lease.Solution.Projects.Single().MetadataReferences
                .OfType<PortableExecutableReference>(), reference =>
                    reference.FilePath?.Contains(packageId,
                        StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static void WriteWorkspace(string root, string version,
        string packageId = PackageId)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        WriteDirectoryPackages(root, version, packageId);
        File.WriteAllText(Path.Combine(root, "src", "Cpm.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Cpm.Consumer</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{{packageId}}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "src", "Marker.cs"),
            "namespace Cpm.Consumer; public sealed class Marker { }");
    }

    private static void WriteDirectoryPackages(string root, string version,
        string packageId = PackageId) =>
        File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), $$"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="{{packageId}}" Version="{{version}}" />
              </ItemGroup>
            </Project>
            """);

    private static void WriteProjectWithUnsupportedPackageScope(string root, string scenario)
    {
        string item = scenario switch
        {
            "project-item-conditioned" =>
                $"<ItemGroup><PackageReference Include=\"{PackageId}\" Condition=\"'$(TargetFramework)' == 'net472'\" /></ItemGroup>",
            "project-group-conditioned" =>
                $"<ItemGroup Condition=\"'$(TargetFramework)' == 'net472'\"><PackageReference Include=\"{PackageId}\" /></ItemGroup>",
            "project-target" =>
                $"<Target Name=\"CollectPackages\"><ItemGroup><PackageReference Include=\"{PackageId}\" /></ItemGroup></Target>",
            "project-choose" =>
                $"<Choose><When Condition=\"'$(TargetFramework)' == 'net472'\"><ItemGroup><PackageReference Include=\"{PackageId}\" /></ItemGroup></When></Choose>",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        File.WriteAllText(Path.Combine(root, "src", "Cpm.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Cpm.Consumer</AssemblyName>
              </PropertyGroup>
              {{item}}
            </Project>
            """);
    }

    private static (string ProjectXml, string CentralXml, bool Ambiguous)
        EvaluatorScenario(string scenario)
    {
        string project = ProjectXml();
        string central = CentralXml("5.6.0");
        bool ambiguous = false;
        switch (scenario)
        {
            case "version-override":
                project = ProjectXml($"<ItemGroup><PackageReference Include=\"{PackageId}\" VersionOverride=\"5.6.0\" /></ItemGroup>");
                break;
            case "global-package-reference":
                central = CentralXml("5.6.0", $"<GlobalPackageReference Include=\"Other.Package\" Version=\"1.0.0\" />");
                break;
            case "duplicate-package-version":
                central = CentralXml("5.6.0", $"<PackageVersion Include=\"{PackageId}\" Version=\"5.6.0\" />");
                break;
            case "update-package-version":
                central = CentralXml("5.6.0", $"<PackageVersion Update=\"{PackageId}\" Version=\"5.7.0\" />");
                break;
            case "remove-package-version":
                central = CentralXml("5.6.0", $"<PackageVersion Remove=\"{PackageId}\" />");
                break;
            case "range-version":
                central = CentralXml("[5.6.0,6.0.0)");
                break;
            case "floating-version":
                central = CentralXml("5.*");
                break;
            case "missing-mpvc":
                central = CentralXml("5.6.0", managePackageVersionsCentrally: null);
                break;
            case "project-disables-mpvc":
                project = ProjectXml(extraProperty: "<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>");
                break;
            case "sdk-central-project":
                central = CentralXml("5.6.0").Replace("<Project>",
                    "<Project Sdk=\"Custom.Cpm.Sdk\">", StringComparison.Ordinal);
                break;
            case "conditioned-package-reference":
                project = ProjectXml($"<ItemGroup><PackageReference Include=\"{PackageId}\" Condition=\"'$(TargetFramework)' == 'net472'\" /></ItemGroup>");
                break;
            case "conditioned-project-item-group":
                project = ProjectXml($"<ItemGroup Condition=\"'$(TargetFramework)' == 'net472'\"><PackageReference Include=\"{PackageId}\" /></ItemGroup>");
                break;
            case "target-scoped-package-reference":
                project = ProjectXml($"<Target Name=\"Packages\"><ItemGroup><PackageReference Include=\"{PackageId}\" /></ItemGroup></Target>");
                break;
            case "choose-scoped-package-reference":
                project = ProjectXml($"<Choose><When Condition=\"'$(TargetFramework)' == 'net472'\"><ItemGroup><PackageReference Include=\"{PackageId}\" /></ItemGroup></When></Choose>");
                break;
            case "ambiguous-authority":
                ambiguous = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return (project, central, ambiguous);
    }

    private static string ProjectXml(string? packageItems = null, string? extraProperty = null) =>
        $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            {{extraProperty}}
          </PropertyGroup>
          {{packageItems ?? $"<ItemGroup><PackageReference Include=\"{PackageId}\" /></ItemGroup>"}}
        </Project>
        """;

    private static string CentralXml(string version, string? extraItem = null,
        bool? managePackageVersionsCentrally = true) =>
        $$"""
        <Project>
          <PropertyGroup>
            {{(managePackageVersionsCentrally.HasValue ? $"<ManagePackageVersionsCentrally>{managePackageVersionsCentrally.Value.ToString().ToLowerInvariant()}</ManagePackageVersionsCentrally>" : "")}}
          </PropertyGroup>
          <ItemGroup>
            <PackageVersion Include="{{PackageId}}" Version="{{version}}" />
            {{extraItem}}
          </ItemGroup>
        </Project>
        """;

    private static string PackageReferencePath(Project project) =>
        Assert.Single(project.MetadataReferences.OfType<PortableExecutableReference>(),
            IsPackageReference).FilePath!;

    private static bool IsPackageReference(PortableExecutableReference reference) =>
        reference.FilePath is { } filePath && Path.GetFileName(filePath).Equals(
            "Microsoft.CodeAnalysis.CSharp.dll", StringComparison.OrdinalIgnoreCase);

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
