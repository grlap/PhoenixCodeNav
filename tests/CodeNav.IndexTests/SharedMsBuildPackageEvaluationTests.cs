using System.Text;
using System.Xml.Linq;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public sealed class SharedMsBuildPackageEvaluationTests
{
    [Theory]
    [InlineData("PackageVersion")]
    [InlineData("PackageReference")]
    [InlineData("GlobalPackageReference")]
    public void PositionalPackageHelpersStillConsumeFinalProperties(string kind)
    {
        string package = kind == "PackageReference"
            ? "<PackageReference Include=\"@(Ids)\" VersionOverride=\"$(VersionValue)\" />"
            : $"<{kind} Include=\"@(Ids)\" Version=\"$(VersionValue)\" />";
        string reference = kind == "PackageVersion" ? "<PackageReference Include=\"First.Package\" />" : "";
        string project = $$"""
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <VersionValue>1.0.0</VersionValue></PropertyGroup>
              <ItemGroup><Ids Include="First.Package" />{{package}}{{reference}}<Ids Include="Later.Package" /></ItemGroup>
              <PropertyGroup><VersionValue>2.0.0</VersionValue></PropertyGroup>
              <ItemGroup><Compile Include="Core.fs" /></ItemGroup></Project>
            """;
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project, "net8.0", "net8.0");
        Assert.Null(result.Error);
        var actual = Assert.Single(result.PackageReferences!);
        Assert.Equal("First.Package", actual.Id);
        Assert.Equal("2.0.0", actual.RequestedVersion);
        Assert.Equal(kind != "GlobalPackageReference", actual.IncludeCompileAssets);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaterTargetsCannotSupplyAnEarlierPackageHelper(bool initiallyPresent)
    {
        string initial = initiallyPresent ? "<Ids Include=\"First.Package\" />" : "";
        string project = $$"""
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>{{initial}}<PackageReference Include="@(Ids)" VersionOverride="1.2.3" />
              <Compile Include="Core.fs" /></ItemGroup></Project>
            """;
        const string targets = "<Project><ItemGroup><Ids Include=\"Later.Package\" /></ItemGroup></Project>";
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => targets, directoryBuildTargetsPath: "Directory.Build.targets");
        if (initiallyPresent)
        {
            Assert.Null(result.Error);
            Assert.Equal("First.Package", Assert.Single(result.PackageReferences!).Id);
        }
        else
        {
            Assert.Equal("fsharp_semantic_reference_unresolved", result.Error);
        }
    }

    [Fact]
    public void IncompleteHelperAtPackagePositionIsNotHealedByLaterItems()
    {
        const string project = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><Ids Include="$(Unknown)" /><PackageReference Include="@(Ids)" VersionOverride="1.2.3" />
                <Ids Include="First.Package" /><Compile Include="Core.fs" /></ItemGroup></Project>
            """;
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project, "net8.0", "net8.0");
        Assert.Equal("fsharp_semantic_reference_unresolved", result.Error);
    }

    [Fact]
    public void GlobalVersionCannotHealAnUnresolvedOrdinaryReferenceCollision()
    {
        const string project = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageReference Include="Build.Tool" /><GlobalPackageReference Include="Build.Tool" Version="1.2.3" />
                <Compile Include="Core.fs" /></ItemGroup></Project>
            """;
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project, "net8.0", "net8.0");
        Assert.Equal("fsharp_semantic_central_package_management_unsupported", result.Error);
    }

    [Theory]
    [InlineData("<Ids Include=\"Later.Package\" />", false)]
    [InlineData("<Ids Remove=\"First.Package\" />", false)]
    [InlineData("<Ids Include=\"$(Unknown)\" />", false)]
    [InlineData("<Ids Include=\"Later.Package\" />", true)]
    [InlineData("<Ids Remove=\"First.Package\" />", true)]
    public void DeferredPackageSpecsKeepTheirDocumentPositionHelperState(string laterItem, bool separateGroups)
    {
        const string central = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="First.Package;Later.Package" Version="1.2.3" /></ItemGroup></Project>
            """;
        string boundary = separateGroups ? "</ItemGroup><ItemGroup>" : "";
        string project = $$"""
            <Project><ItemGroup><Ids Include="First.Package" />{{boundary}}
              <PackageReference Include="@(Ids)" />{{boundary}}{{laterItem}}
              <Compile Include="Core.fs" /></ItemGroup></Project>
            """;
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => central,
            directoryPackagesPropsPath: "Directory.Packages.props");
        Assert.Null(result.Error);
        var reference = Assert.Single(result.PackageReferences!);
        Assert.Equal("First.Package", reference.Id);
        Assert.Equal("1.2.3", reference.RequestedVersion);
    }

    [Theory]
    [InlineData("Removed.Package")]
    [InlineData(" Removed.Package ")]
    public void PairedCentralVersionAndReferenceRemovalLeavesNeitherLanguageAReference(string include)
    {
        const string central = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Removed.Package" Version="1.2.3" /></ItemGroup></Project>
            """;
        string project = $$"""
            <Project><ItemGroup><PackageReference Include="{{include}}" />
              <PackageVersion Remove="Removed.Package" /><PackageReference Remove="Removed.Package" />
              <Compile Include="Core.fs" /></ItemGroup></Project>
            """;
        var fsharp = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => central,
            directoryPackagesPropsPath: "Directory.Packages.props");
        Assert.Null(fsharp.Error);
        Assert.Empty(fsharp.PackageReferences!);
        var csharp = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
            Encoding.UTF8.GetBytes(project), [("Removed.Package", "")], central, false);
        Assert.Empty(csharp);
    }

    [Fact]
    public void CSharpRemovalDoesNotReappendWhitespaceNormalizedDirectReference()
    {
        const string central = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Example.Package" Version="1.2.3" /></ItemGroup></Project>
            """;
        const string project = "<Project><ItemGroup><PackageReference Include=\" Example.Package \" /><PackageReference Remove=\"Example.Package\" /></ItemGroup></Project>";
        var result = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
            Encoding.UTF8.GetBytes(project), [("Example.Package", "")], central, false);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlySurvivingReferencesNeedFinalCentralVersions(bool supplyOverride)
    {
        const string central = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>
            """;
        string update = supplyOverride ? "<PackageReference Update=\"Example.Package\" VersionOverride=\"2.0.0\" />" : "";
        string project = $$"""
            <Project><ItemGroup><PackageReference Include="Example.Package" />{{update}}
              <Compile Include="Core.fs" /></ItemGroup></Project>
            """;
        var fsharp = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => central,
            directoryPackagesPropsPath: "Directory.Packages.props");
        var csharp = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
            Encoding.UTF8.GetBytes(project), [("Example.Package", "")], central, false);
        if (supplyOverride)
        {
            Assert.Null(fsharp.Error);
            Assert.Equal("2.0.0", Assert.Single(fsharp.PackageReferences!).RequestedVersion);
            Assert.Equal("2.0.0", Assert.Single(csharp).Version);
            Assert.True(Assert.Single(csharp).CentrallyManaged);
        }
        else
        {
            Assert.Equal("fsharp_semantic_package_reference_unresolved", fsharp.Error);
            Assert.Equal("", Assert.Single(csharp).Version);
            Assert.False(Assert.Single(csharp).CentrallyManaged);
        }
    }

    [Theory]
    [InlineData("PackageVersion")]
    [InlineData("PackageReference")]
    public void DuplicateOrdinaryAndGlobalIdentitiesFailClosedInBothLanguages(string conflictingItem)
    {
        string conflict = conflictingItem == "PackageVersion"
            ? "<PackageVersion Include=\"Build.Tool\" Version=\"1.0.0\" />"
            : "<PackageReference Include=\"Build.Tool\" VersionOverride=\"1.0.0\" />";
        string central = $$"""
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>{{conflict}}<GlobalPackageReference Include="Build.Tool" Version="3.0.0" /></ItemGroup></Project>
            """;
        const string project = "<Project><ItemGroup><PackageReference Include=\"Example.Package\" VersionOverride=\"2.0.0\" /><Compile Include=\"Core.fs\" /></ItemGroup></Project>";
        var fsharp = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => central,
            directoryPackagesPropsPath: "Directory.Packages.props");
        Assert.Equal("fsharp_semantic_central_package_management_unsupported", fsharp.Error);
        var csharp = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
            Encoding.UTF8.GetBytes(project), [("Example.Package", "")], central, false);
        Assert.Equal("", Assert.Single(csharp).Version);
        Assert.False(Assert.Single(csharp).CentrallyManaged);
    }

    [Fact]
    public void DisabledVersionOverrideStillRefusesBothLanguages()
    {
        const string central = """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled></PropertyGroup>
              <ItemGroup><PackageVersion Include="Example.Package" Version="1.2.3" /></ItemGroup></Project>
            """;
        const string project = "<Project><ItemGroup><PackageReference Include=\"Example.Package\" VersionOverride=\"2.0.0\" /><Compile Include=\"Core.fs\" /></ItemGroup></Project>";
        var fsharp = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => central,
            directoryPackagesPropsPath: "Directory.Packages.props");
        Assert.Equal("fsharp_semantic_package_reference_unresolved", fsharp.Error);
        var csharp = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
            Encoding.UTF8.GetBytes(project), [("Example.Package", "")], central, false);
        Assert.Equal("", Assert.Single(csharp).Version);
        Assert.False(Assert.Single(csharp).CentrallyManaged);
    }

    [Theory]
    [InlineData("PackageVersion")]
    [InlineData("PackageReference")]
    [InlineData("GlobalPackageReference")]
    public void DeferredPackagePassesObserveCancellationBetweenSpecs(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase)
        {
            ["ManagePackageVersionsCentrally"] = new("true", true),
        };
        static bool Expand(string value, string document, out string expanded) { expanded = value; return true; }
        static bool ExpandItems(string value, string document, out List<string> items) { items = [.. value.Split(';')]; return true; }
        static bool Condition(XElement element, string document, out bool process) { process = true; return true; }
        var evaluator = new BoundedMsBuildPackageEvaluator(properties, Expand, ExpandItems, Condition,
            () => { cancellation.Cancel(); return true; }, cancellation.Token);
        string versionAttribute = kind == "PackageReference" ? "VersionOverride" : "Version";
        var group = XElement.Parse($"<ItemGroup><{kind} Include=\"First.Package;Second.Package\" {versionAttribute}=\"1.0.0\" /></ItemGroup>");
        evaluator.Add(group, Assert.Single(group.Elements()), "Core.fsproj");
        Assert.Throws<OperationCanceledException>(() => evaluator.Evaluate());
    }

    [Theory]
    [InlineData("1.2.3", "")]
    [InlineData("$(VersionValue)", "<VersionValue>1.2.3</VersionValue>")]
    public void SharedAttributesPreserveEstablishedCSharpCaseInsensitiveAdmission(string version, string property)
    {
        string central = $$"""
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>{{property}}</PropertyGroup>
              <ItemGroup><packageversion include="Example.Package" version="{{version}}" /></ItemGroup></Project>
            """;
        const string project = """
            <Project><ItemGroup><packagereference include="Example.Package" /><Compile Include="Core.fs" /></ItemGroup></Project>
            """;
        var csharp = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
            Encoding.UTF8.GetBytes(project), [("Example.Package", "")], central, false);
        Assert.Equal("1.2.3", Assert.Single(csharp).Version);
        Assert.True(Assert.Single(csharp).CentrallyManaged);
        var fsharp = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
            "net8.0", "net8.0", importResolver: _ => central,
            directoryPackagesPropsPath: "Directory.Packages.props");
        Assert.Null(fsharp.Error);
        Assert.Equal("1.2.3", Assert.Single(fsharp.PackageReferences!).RequestedVersion);
    }

    [Fact]
    public void DeferredCentralConditionsCaptureFinalPropertyProbesAndRefreshTheirDependencies()
    {
        string root = Directory.CreateTempSubdirectory("codenav-cpm-probes").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Core"));
            const string central = """
                <Project>
                  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
                  <ItemGroup Condition="Exists('$(Probe)')">
                    <PackageReference Include="Example.Package" />
                    <PackageVersion Include="Example.Package" Version="1.2.3" />
                  </ItemGroup>
                </Project>
                """;
            string Project(string probe) => $"""
                <Project><PropertyGroup><TargetFramework>net8.0</TargetFramework><Probe>{probe}</Probe></PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup></Project>
                """;
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), central);
            File.WriteAllText(Path.Combine(root, "Core", "Core.fsproj"), Project("Web.config"));
            File.WriteAllText(Path.Combine(root, "Core", "Core.fs"), "module Core\nlet value = 1\n");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using var store = new IndexStore(db, createNew: false);
            bool? Captured(string path)
            {
                using var queries = new IndexQueries(db);
                return queries.TryGetCapturedMsBuildFilePresence(path, out bool? value) ? value : null;
            }
            Assert.Equal(false, Captured("Core/Web.config"));
            File.WriteAllText(Path.Combine(root, "Core", "Web.config"), "<configuration />");
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"]);
            Assert.Equal(true, Captured("Core/Web.config"));
            File.WriteAllText(Path.Combine(root, "Core", "Core.fsproj"), Project("app.config"));
            DeltaRefresher.Refresh(store, root, ["Core/Core.fsproj"]);
            Assert.Null(Captured("Core/Web.config"));
            Assert.Equal(false, Captured("Core/app.config"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void CentralReferencesUseFinalProjectPropertiesAndFinalVersionItems()
    {
        const string central = """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup Condition="'$(Flavor)' == 'Selected'">
                <PackageReference Include="Example.Package" />
                <PackageVersion Include="Example.Package" Version="$(SelectedVersion)" />
              </ItemGroup>
            </Project>
            """;
        const string project = """
            <Project>
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Flavor>Selected</Flavor><SelectedVersion>4.0.0</SelectedVersion>
              </PropertyGroup>
              <ItemGroup><PackageVersion Update="Example.Package" Version="5.0.0" /><Compile Include="Core.fs" /></ItemGroup>
            </Project>
            """;
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", project, "net8.0", "net8.0",
            importResolver: _ => central, directoryPackagesPropsPath: "Directory.Packages.props");
        Assert.Null(result.Error);
        Assert.Equal("5.0.0", Assert.Single(result.PackageReferences!).RequestedVersion);
    }

    [Theory]
    [InlineData("central-reference", "1.2.3", "2.0.0")]
    [InlineData("version-update", "1.2.4", null)]
    [InlineData("unmatched-update", "1.2.3", null)]
    [InlineData("global-reference", "1.2.3", null)]
    public void CentralPackageItemsHaveTheSameMeaningForBothLanguages(
        string scenario, string expectedVersion, string? extraVersion)
    {
        string extra = scenario switch
        {
            "central-reference" => """
                <PackageVersion Include="Other.Package" Version="2.0.0" />
                <PackageReference Include="Other.Package" />
                """,
            "version-update" => "<PackageVersion Update=\"Example.Package\" Version=\"1.2.4\" />",
            "unmatched-update" => "<PackageVersion Update=\"Not.Referenced\" Version=\"8.0.0\" />",
            "global-reference" => "<GlobalPackageReference Include=\"Build.Tool\" Version=\"3.0.0\" />",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        string central = $$"""
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Example.Package" Version="1.2.3" />
                {{extra}}
              </ItemGroup>
            </Project>
            """;
        const string project = """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Consumer</AssemblyName></PropertyGroup>
              <ItemGroup><PackageReference Include="Example.Package" /><Compile Include="Core.fs" /></ItemGroup>
            </Project>
            """;

        var csharp = ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(
            Encoding.UTF8.GetBytes(project), [("Example.Package", "")], central, false);
        var fsharp = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", project, "net8.0", "net8.0",
            importResolver: path => path == "Directory.Packages.props" ? central : null,
            directoryPackagesPropsPath: "Directory.Packages.props");

        Assert.Null(fsharp.Error);
        Assert.Equal(expectedVersion, Assert.Single(csharp, r => r.Id == "Example.Package").Version);
        Assert.True(Assert.Single(csharp, r => r.Id == "Example.Package").CentrallyManaged);
        Assert.Equal(expectedVersion,
            Assert.Single(fsharp.PackageReferences!, r => r.Id == "Example.Package").RequestedVersion);
        if (extraVersion is not null)
        {
            Assert.Equal(extraVersion, Assert.Single(csharp, r => r.Id == "Other.Package").Version);
            Assert.Equal(extraVersion,
                Assert.Single(fsharp.PackageReferences!, r => r.Id == "Other.Package").RequestedVersion);
        }
        if (scenario == "global-reference")
        {
            Assert.DoesNotContain(csharp, r => r.Id == "Build.Tool");
            var global = Assert.Single(fsharp.PackageReferences!, r => r.Id == "Build.Tool");
            Assert.Equal("3.0.0", global.RequestedVersion);
            Assert.False(global.IncludeCompileAssets);
        }
    }

    [Theory]
    [InlineData("IncludeAssets=\"all\"", "true")]
    [InlineData("", "$(Unknown)")]
    [InlineData("", "sometimes")]
    public void GlobalProjectionRefusesUnmodeledMetadataAndUnknownAuthority(string metadata, string enabled)
    {
        string central = $$"""
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>{{enabled}}</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><GlobalPackageReference Include="Build.Tool" Version="3.0.0" {{metadata}} /></ItemGroup>
            </Project>
            """;
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", "<Project><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
            "net8.0", "net8.0", importResolver: _ => central,
            directoryPackagesPropsPath: "Directory.Packages.props");
        Assert.Equal("fsharp_semantic_central_package_management_unsupported", result.Error);
        Assert.Empty(result.PackageReferences!);
    }
}
