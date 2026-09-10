using System.Text;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Theory]
    [InlineData("UsingMicrosoftNETSdk", null)]
    [InlineData("UsingMicrosoftNETSdk", "Directory.Build.props")]
    [InlineData("UsingMicrosoftNETSdk", "Directory.Packages.props")]
    [InlineData("UsingMicrosoftNETSdk", "Build/Shared.props")]
    [InlineData("UsingNETSdkDefaults", null)]
    [InlineData("UsingNETSdkDefaults", "Directory.Build.props")]
    [InlineData("UsingNETSdkDefaults", "Directory.Packages.props")]
    [InlineData("UsingNETSdkDefaults", "Build/Shared.props")]
    public void SdkContextResolvesConditionsBeforeProjectAndImportedProperties(string property, string? importPath)
    {
        string body = $$"""
            <PropertyGroup Condition="'$({{property}})' == 'true'">
              <AssemblyName>SdkDetected</AssemblyName>
              <DefineConstants>SDK_CONTEXT</DefineConstants>
            </PropertyGroup>
            """;
        string project = SdkContextProject(importPath is null ? body :
            importPath == "Build/Shared.props" ? "<Import Project=\"../Build/Shared.props\" />" : "");
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: path => path == importPath ? $"<Project>{body}</Project>" : null,
            directoryBuildPropsPath: importPath == "Directory.Build.props" ? importPath : null,
            directoryPackagesPropsPath: importPath == "Directory.Packages.props" ? importPath : null);

        Assert.Null(result.Error);
        Assert.Equal("SdkDetected", result.AssemblyName);
        Assert.Contains("--define:SDK_CONTEXT", result.CommandLineArgs);
        Assert.Contains("fsharp_semantic_sdk_implicit_authority", result.PartialReason);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Theory]
    [InlineData("UsingMicrosoftNETSdk")]
    [InlineData("UsingNETSdkDefaults")]
    public void SdkContextDoesNotInferSdkFromFSharpExtensionOrTargetFramework(string property)
    {
        string project = SdkContextProject($$"""
            <PropertyGroup Condition="'$({{property}})' == 'true'"><AssemblyName>WrongSdk</AssemblyName></PropertyGroup>
            """, sdk: null);
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project, "net8.0", "net8.0");
        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.DoesNotContain("fsharp_semantic_sdk_implicit_authority", result.PartialReason ?? "");
    }

    [Theory]
    [InlineData("Custom.Sdk", "")]
    [InlineData("Microsoft.NET.Sdk.Web", "")]
    [InlineData("Microsoft.NET.Sdk/10.0.400", "")]
    [InlineData("Microsoft.NET.Sdk;Microsoft.NET.Sdk", "")]
    [InlineData(null, "<Sdk Name=\"Microsoft.NET.Sdk\" />")]
    [InlineData("Microsoft.NET.Sdk", "<Sdk Name=\"Microsoft.NET.Sdk\" />")]
    [InlineData(null, "<Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" />")]
    public void SdkContextDoesNotBroadenFSharpSdkDeclarationAuthority(string? sdk, string body)
    {
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            SdkContextProject(body, sdk), "net8.0", "net8.0");
        Assert.Equal("fsharp_semantic_sdk_unsupported", result.Error);
        Assert.DoesNotContain("fsharp_semantic_sdk_implicit_authority", result.PartialReason ?? "");
    }

    [Theory]
    [InlineData("UsingMicrosoftNETSdk", "false", "true|false")]
    [InlineData("UsingMicrosoftNETSdk", "", "true|")]
    [InlineData("UsingNETSdkDefaults", "false", "true|false")]
    [InlineData("UsingNETSdkDefaults", "", "true|")]
    public void SdkContextKeepsAssignmentsFromEarlyImportsAuthoritative(string property, string value, string expected)
    {
        string props = $$"""
            <Project><PropertyGroup>
              <BeforeOverride>$({{property}})</BeforeOverride>
              <{{property}}>{{value}}</{{property}}>
            </PropertyGroup></Project>
            """;
        string project = SdkContextProject($"<PropertyGroup><AssemblyName>$(BeforeOverride)|$({property})</AssemblyName></PropertyGroup>");
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => props, directoryBuildPropsPath: "Directory.Build.props");
        Assert.Null(result.Error);
        Assert.Equal(expected, result.AssemblyName);
    }

    [Theory]
    [InlineData("UsingMicrosoftNETSdk")]
    [InlineData("UsingNETSdkDefaults")]
    public void SdkContextDoesNotHealIncompleteProjectAssignments(string property)
    {
        string project = SdkContextProject($$"""
            <PropertyGroup><{{property}}>$(Unknown)</{{property}}></PropertyGroup>
            <PropertyGroup Condition="'$({{property}})' == 'true'"><AssemblyName>WrongSdk</AssemblyName></PropertyGroup>
            """);
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project, "net8.0", "net8.0");
        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk", "", "1.2.3-true.true")]
    [InlineData(" microsoft.net.sdk ", "", "1.2.3-true.true")]
    [InlineData("Microsoft.NET.Sdk", "<UsingMicrosoftNETSdk>false</UsingMicrosoftNETSdk>", "1.2.3-false.true")]
    [InlineData(null, "<UsingMicrosoftNETSdk>true</UsingMicrosoftNETSdk><UsingNETSdkDefaults>true</UsingNETSdkDefaults>", "1.2.3-true.true")]
    public void SdkContextFeedsTheSameCentralPropertyExpansionInBothLanguages(string? sdk, string overrides, string expectedVersion)
    {
        string central = $$"""
            <Project><PropertyGroup>
              <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              {{overrides}}
              <PackageVersionValue>1.2.3-$(UsingMicrosoftNETSdk).$(UsingNETSdkDefaults)</PackageVersionValue>
            </PropertyGroup>
            <ItemGroup><PackageVersion Include="Example.Package" Version="$(PackageVersionValue)" /></ItemGroup></Project>
            """;
        string project = SdkContextProject("<ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup>", sdk);
        var fsharp = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => central, directoryPackagesPropsPath: "Directory.Packages.props");
        var bytes = Encoding.UTF8.GetBytes(project);
        var parsed = ProjectFileParser.ParseSnapshot("Core/Core.csproj", bytes, null);
        var csharp = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(bytes, parsed.PackageRefs, central, false);

        Assert.Null(fsharp.Error);
        Assert.Equal(expectedVersion, Assert.Single(fsharp.PackageReferences!).RequestedVersion);
        Assert.Equal(expectedVersion, Assert.Single(csharp).Version);
        Assert.True(Assert.Single(csharp).CentrallyManaged);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Custom.Sdk", "")]
    [InlineData("Microsoft.NET.Sdk.Web", "")]
    [InlineData("Microsoft.NET.Sdk/10.0.400", "")]
    [InlineData("Microsoft.NET.Sdk", "<Sdk Name=\"Microsoft.NET.Sdk\" />")]
    public void SdkContextDoesNotInventCSharpSdkProperties(string? sdk, string body)
    {
        const string central = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Example.Package" Version="1.2.3-$(UsingMicrosoftNETSdk)" /></ItemGroup></Project>
            """;
        var bytes = Encoding.UTF8.GetBytes(SdkContextProject(body + "<ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup>", sdk));
        var parsed = ProjectFileParser.ParseSnapshot("Core/Core.csproj", bytes, null);
        var result = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(bytes, parsed.PackageRefs, central, false);
        Assert.Equal("", Assert.Single(result).Version);
        Assert.False(Assert.Single(result).CentrallyManaged);
    }

    [Fact]
    public void SdkContextCapturesExistsAndRefreshesWhenProjectSdkChanges()
    {
        string root = Directory.CreateTempSubdirectory("codenav-sdk-context").FullName;
        try
        {
            const string props = """
                <Project><PropertyGroup Condition="'$(UsingMicrosoftNETSdk)' == 'true' And Exists('Web.config')">
                  <DefineConstants>SDK_FILE</DefineConstants>
                </PropertyGroup></Project>
                """;
            string project = SdkContextProject("");
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Core/Core.fsproj", project);
            WriteProject(root, "Directory.Build.props", props);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            using var pinned = new IndexQueries(db, pinReadSnapshot: true);
            Assert.True(pinned.TryGetCapturedMsBuildFilePresence("Core/Web.config", out bool? initial));
            Assert.Equal(false, initial);

            FSharpSemanticOptionsSnapshot Current()
            {
                using var queries = new IndexQueries(db);
                return ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
                    "net8.0", "net8.0", importResolver: _ => props, directoryBuildPropsPath: "Directory.Build.props",
                    existsResolver: path => SemanticService.ResolveIndexedFSharpExists(queries, path));
            }
            bool? Captured()
            {
                using var queries = new IndexQueries(db);
                return queries.TryGetCapturedMsBuildFilePresence("Core/Web.config", out bool? value) ? value : null;
            }

            Assert.Null(Current().Error);
            Assert.DoesNotContain("--define:SDK_FILE", Current().CommandLineArgs);
            WriteProject(root, "Core/Web.config", "<configuration />");
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Captured());
            Assert.Null(Current().Error);
            Assert.Contains("--define:SDK_FILE", Current().CommandLineArgs);
            Assert.True(pinned.TryGetCapturedMsBuildFilePresence("Core/Web.config", out bool? old));
            Assert.Equal(false, old);

            project = SdkContextProject("", sdk: null);
            WriteProject(root, "Core/Core.fsproj", project);
            DeltaRefresher.Refresh(store, root, ["Core/Core.fsproj"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Null(Captured());
            Assert.Equal("fsharp_semantic_condition_property_unresolved", Current().Error);

            project = SdkContextProject("");
            WriteProject(root, "Core/Core.fsproj", project);
            DeltaRefresher.Refresh(store, root, ["Core/Core.fsproj"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Captured());
            Assert.Contains("--define:SDK_FILE", Current().CommandLineArgs);
            File.Delete(Path.Combine(root, "Core", "Web.config"));
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(false, Captured());
            Assert.Null(Current().Error);
            Assert.DoesNotContain("--define:SDK_FILE", Current().CommandLineArgs);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string SdkContextProject(string body, string? sdk = "Microsoft.NET.Sdk") => $$"""
        <Project{{(sdk is null ? "" : $" Sdk=\"{sdk}\"")}}>
          <PropertyGroup><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
          {{body}}
          <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
        </Project>
        """;
}
