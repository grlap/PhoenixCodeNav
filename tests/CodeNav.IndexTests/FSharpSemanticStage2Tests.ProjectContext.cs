using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using System.Text;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Theory]
    [InlineData("Core/Core.csproj", "1.2.3-kind.csproj")]
    [InlineData("Core/Core.Net.fsproj", "1.2.3-kind.fsproj")]
    [InlineData("Core/Core.proj", "1.2.3-kind.proj")]
    [InlineData("Core/NoSuffix", "1.2.3-kind")]
    [InlineData(null, "")]
    public void ProjectExtensionCSharpAdapterUsesActualPathWithoutSdkOrSuffixGuess(string? path, string expectedVersion)
    {
        byte[] project = Encoding.UTF8.GetBytes("<Project><ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup></Project>");
        const string central = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Example.Package" Version="1.2.3-kind$(MSBuildProjectExtension)" /></ItemGroup>
            </Project>
            """;
        var direct = ProjectFileParser.ParseSnapshot(path ?? "Core/Unknown.csproj", project).PackageRefs;
        var result = Assert.Single(ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(project,
            direct, central, false, hasPotentialImportedSdkPropertyAuthority: true, projectPath: path));
        Assert.Equal(expectedVersion, result.Version);
        Assert.Equal(path is not null, result.CentrallyManaged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectExtensionCSharpAdapterRefusesReservedAssignments(bool centralAssignment)
    {
        const string assignment = "<PropertyGroup><MSBuildProjectExtension>.fsproj</MSBuildProjectExtension></PropertyGroup>";
        byte[] project = Encoding.UTF8.GetBytes("<Project>" + (centralAssignment ? "" : assignment) +
            "<ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup></Project>");
        string central = "<Project>" + (centralAssignment ? assignment : "") +
            "<PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>" +
            "<ItemGroup><PackageVersion Include=\"Example.Package\" Version=\"1.2.3-kind$(MSBuildProjectExtension)\" /></ItemGroup></Project>";
        var result = Assert.Single(ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(project,
            ProjectFileParser.ParseSnapshot("Core/Core.csproj", project).PackageRefs, central, false, projectPath: "Core/Core.csproj"));
        Assert.Equal("", result.Version);
        Assert.False(result.CentrallyManaged);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "Directory.Build.props")]
    [InlineData(null, "Directory.Packages.props")]
    [InlineData(null, "Build/Shared.props")]
    [InlineData("Microsoft.NET.Sdk", null)]
    [InlineData("Microsoft.NET.Sdk", "Directory.Build.props")]
    [InlineData("Microsoft.NET.Sdk", "Directory.Packages.props")]
    [InlineData("Microsoft.NET.Sdk", "Build/Shared.props")]
    public void ProjectExtensionIsAvailableBeforeImportsWithoutSdkInference(string? sdk, string? importPath)
    {
        const string body = """
            <PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.fsproj'">
              <AssemblyName>ExtensionDetected</AssemblyName><DefineConstants>PROJECT_EXTENSION</DefineConstants>
            </PropertyGroup>
            <PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.props'">
              <AssemblyName>WrongImportedExtension</AssemblyName>
            </PropertyGroup>
            """;
        string project = SdkContextProject(importPath is null ? body :
            importPath == "Build/Shared.props" ? "<Import Project=\"../Build/Shared.props\" />" : "", sdk);
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.Net.fsproj", project,
            "net8.0", "net8.0", importResolver: path => path == importPath ? $"<Project>{body}</Project>" : null,
            directoryBuildPropsPath: importPath == "Directory.Build.props" ? importPath : null,
            directoryPackagesPropsPath: importPath == "Directory.Packages.props" ? importPath : null);
        Assert.Null(result.Error);
        Assert.Equal("ExtensionDetected", result.AssemblyName);
        Assert.Contains("--define:PROJECT_EXTENSION", result.CommandLineArgs);
        if (sdk is null) Assert.DoesNotContain("fsharp_semantic_sdk_implicit_authority", result.PartialReason ?? "");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    [InlineData("Directory.Build.targets")]
    public void ProjectExtensionRejectsReservedPropertyAssignment(string? importPath)
    {
        const string body = "<PropertyGroup><MSBuildProjectExtension>.csproj</MSBuildProjectExtension></PropertyGroup>";
        // No Compile item: pin reserved-property refusal itself, not the separate late-item ordering guard.
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            "<Project>" + (importPath is null ? body : "") + "</Project>", "net8.0", "net8.0",
            importResolver: _ => $"<Project>{body}</Project>",
            directoryBuildPropsPath: importPath == "Directory.Build.props" ? importPath : null,
            directoryPackagesPropsPath: importPath == "Directory.Packages.props" ? importPath : null,
            directoryBuildTargetsPath: importPath == "Directory.Build.targets" ? importPath : null);
        Assert.Equal("fsharp_semantic_property_unsupported", result.Error);
    }

    [Fact]
    public void ProjectExtensionCapturesExistsAcrossColdAndDeltaIndexes()
    {
        string root = Directory.CreateTempSubdirectory("codenav-extension").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkContextProject("", sdk: null));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            const string props = """
                <Project><PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.fsproj' And Exists('Web.config')">
                  <DefineConstants>EXTENSION_FILE</DefineConstants>
                </PropertyGroup></Project>
                """;
            WriteProject(root, "Directory.Build.props", props);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            bool? Captured()
            {
                using var queries = new IndexQueries(db);
                return queries.TryGetCapturedMsBuildFilePresence("Core/Web.config", out bool? value) ? value : null;
            }
            Assert.Equal(false, Captured());
            WriteProject(root, "Core/Web.config", "<configuration />");
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Captured());
            WriteProject(root, "Directory.Build.props", props.Replace(".fsproj", ".csproj", StringComparison.Ordinal));
            DeltaRefresher.Refresh(store, root, ["Directory.Build.props"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Null(Captured());
            WriteProject(root, "Directory.Build.props", props);
            DeltaRefresher.Refresh(store, root, ["Directory.Build.props"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Captured());
        }
        finally { Cleanup(root); }
    }
}
