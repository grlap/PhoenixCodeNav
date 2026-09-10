using System.Text;
using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Theory]
    [InlineData("false", false)]
    [InlineData("true", false)]
    [InlineData("false", true)]
    [InlineData("true", true)]
    public void ProjectReferenceCopyLocalPreservesCSharpStructuralEdge(string copyLocal, bool attribute)
    {
        string metadata = attribute ? $" Private=\"{copyLocal}\" />" :
            $"><Private>{copyLocal}</Private></ProjectReference>";
        var project = ProjectFileParser.ParseSnapshot("Core/Core.csproj", Encoding.UTF8.GetBytes(
            "<Project><ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\"" +
            metadata + "</ItemGroup></Project>"));

        Assert.Equal("parsed", project.LoadStatus);
        Assert.Equal("Dependency/Dependency.csproj", Assert.Single(project.ProjectRefRelPaths));
    }

    [Theory]
    [InlineData("false", false, "csproj")]
    [InlineData("true", false, "csproj")]
    [InlineData("false", true, "csproj")]
    [InlineData("true", true, "csproj")]
    [InlineData("false", false, "fsproj")]
    [InlineData("true", false, "fsproj")]
    [InlineData("false", true, "fsproj")]
    [InlineData("true", true, "fsproj")]
    public void ProjectReferenceCopyLocalDoesNotChangeCompilerIdentity(
        string copyLocal, bool attribute, string targetExtension)
    {
        string reference = $"<ProjectReference Include=\"../Dependency/Dependency.{targetExtension}\"" +
            (attribute ? $" Private=\"{copyLocal}\" />" : $"><Private>{copyLocal}</Private></ProjectReference>");
        var result = EvaluateBoundedProject($"<ItemGroup>{reference}</ItemGroup>");

        Assert.Null(result.Error);
        Assert.Equal($"Dependency/Dependency.{targetExtension}", Assert.Single(result.ProjectReferences).ProjectPath);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void ProjectReferenceCopyLocalNeverOverridesReferenceOutputAssembly(string copyLocal)
    {
        var result = EvaluateBoundedProject($$"""
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj">
              <Private>{{copyLocal}}</Private><ReferenceOutputAssembly>false</ReferenceOutputAssembly>
            </ProjectReference></ItemGroup>
            """);

        Assert.Null(result.Error);
        Assert.Empty(result.ProjectReferences);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Theory]
    [InlineData("Aliases", "alternate")]
    [InlineData("PrivateAssets", "all")]
    [InlineData("ReferenceOutputAssembly", "$(Unknown)")]
    public void CopyLocalAdmissionDoesNotAdmitOtherProjectReferenceMetadata(string name, string value)
    {
        var result = EvaluateBoundedProject($$"""
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj">
              <Private>false</Private><{{name}}>{{value}}</{{name}}>
            </ProjectReference></ItemGroup>
            """);

        Assert.Equal("fsharp_semantic_project_reference_metadata_unsupported", result.Error);
        Assert.Empty(result.SourceFiles);
    }

    [Theory]
    [InlineData("<Private Condition=\"'$(CopyLocalSetting)' != ''\">$(CopyLocalSetting)</Private>")]
    [InlineData("<Private Condition=\"false\">false</Private>")]
    [InlineData("<Private>$(Unknown)</Private>")]
    public void CopyLocalDoesNotRequireAuthorityOverOutputCopying(string metadata)
    {
        var result = EvaluateBoundedProject($"<ItemGroup><ProjectReference Include=\"../Dependency/Dependency.fsproj\">{metadata}</ProjectReference></ItemGroup>");
        Assert.Null(result.Error);
        Assert.Equal("Dependency/Dependency.fsproj", Assert.Single(result.ProjectReferences).ProjectPath);
    }

    [Theory]
    [InlineData("csproj")]
    [InlineData("fsproj")]
    public void CompilerExcludedCopyLocalReferenceStillHasAStructuralBuildEdge(string extension)
    {
        string xml = """
            <Project><ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj">
            <Private>false</Private><ReferenceOutputAssembly>false</ReferenceOutputAssembly>
            </ProjectReference></ItemGroup></Project>
            """;
        var project = ProjectFileParser.ParseSnapshot($"Core/Core.{extension}", Encoding.UTF8.GetBytes(xml));
        Assert.Equal("Dependency/Dependency.fsproj", Assert.Single(project.ProjectRefRelPaths));
        var semantic = EvaluateBoundedProject(System.Xml.Linq.XElement.Parse(xml).Element("ItemGroup")!.ToString());
        Assert.Null(semantic.Error);
        Assert.Empty(semantic.ProjectReferences);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void FSharpCopyLocalReferenceResolvesTheDependencySource(string copyLocal)
    {
        string root = Directory.CreateTempSubdirectory("codenav-copy-local-fsharp").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", $$"""
                <ItemGroup>
                  <Compile Include="App.fs" />
                  <ProjectReference Include="../Dependency/Dependency.fsproj"><Private>{{copyLocal}}</Private></ProjectReference>
                </ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet observed = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj", SdkProject("net10.0", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", "module Dependency\nlet value = 42\n");
            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Definition(
                path: "App/App.fs", line: 2, column: 28, mode: "semantic", timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Contains(response.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() == "Dependency/Dependency.fs");
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task CSharpCopyLocalReferenceKeepsTheDependencyInTheCompilerModel(string copyLocal)
    {
        string root = Directory.CreateTempSubdirectory("codenav-copy-local-csharp").FullName;
        try
        {
            WriteProject(root, "App/App.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                <ItemGroup><ProjectReference Include="../Dependency/Dependency.csproj"><Private>{{copyLocal}}</Private></ProjectReference></ItemGroup>
                </Project>
                """);
            WriteProject(root, "App/App.cs", "public class App { public Dependency Value => new(); }");
            WriteProject(root, "Dependency/Dependency.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
            WriteProject(root, "Dependency/Dependency.cs", "public class Dependency { }");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var workspace = new SemanticWorkspace(root, db, enableRoslynPersistence: false);
            // EnsureLoadedAsync takes an already-planned project set; the normal semantic
            // service expands the dependency closure before calling this workspace API.
            using var lease = await workspace.EnsureLoadedAsync(["App", "Dependency"], CancellationToken.None);
            Assert.Empty(lease.Coverage.FailedProjects);
            var project = Assert.Single(lease.Solution.Projects, candidate => candidate.Name == "App");
            Assert.Single(project.ProjectReferences);
            var compilation = await project.GetCompilationAsync();
            Assert.NotNull(compilation);
            Assert.NotNull(compilation.GetTypeByMetadataName("Dependency"));
            Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void CopyLocalAdmissionDoesNotClaimFSharpToCSharpClosureSupport()
    {
        string root = Directory.CreateTempSubdirectory("codenav-copy-local-cross-language").FullName;
        try
        {
            WriteProject(root, "App/App.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup><Compile Include="App.fs" />
                <ProjectReference Include="../Dependency/Dependency.csproj"><Private>false</Private></ProjectReference></ItemGroup>
                """));
            WriteProject(root, "App/App.fs", "module App\nlet value = 1\n");
            WriteProject(root, "Dependency/Dependency.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
            WriteProject(root, "Dependency/Dependency.cs", "public class Dependency { }");
            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "App/App.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_project_references_unsupported", response.GetProperty("error").GetString());
            Assert.Contains("stale last-built project DLL", response.GetProperty("detail").GetString());
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("SFMC_MONOLITH_VSTOOLS_PATH")]
    [InlineData("OptionalBuildToolsRoot")]
    public void AbsentOptionalBuildToolsGuardSkipsEarlyPropertyGroup(string propertyName)
    {
        string props = $$"""
            <Project>
              <PropertyGroup Condition="'$({{propertyName}})' != ''">
                <FSharpTargetsPath>$({{propertyName}})\Common7\IDE\CommonExtensions\Microsoft\FSharp\Microsoft.FSharp.Targets</FSharpTargetsPath>
                <AssemblyName>WRONG</AssemblyName>
              </PropertyGroup>
            </Project>
            """;
        var result = EvaluateBoundedProject("", new Dictionary<string, string>
        {
            ["Directory.Build.props"] = props
        }, directoryBuildPropsPath: "Directory.Build.props");

        Assert.Null(result.Error);
        Assert.Equal("Core", result.AssemblyName);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
        Assert.Contains("fsharp_semantic_optional_property_assumed_empty", result.PartialReason);
    }

    [Theory]
    [InlineData("", "Core", null)]
    [InlineData("configured", "Configured", null)]
    [InlineData("$(Uncaptured)", null, "fsharp_semantic_condition_property_unresolved")]
    public void OptionalGuardRetainsExplicitPropertyAuthority(string value, string? assemblyName, string? error)
    {
        var result = EvaluateBoundedProject($$"""
            <PropertyGroup><OptionalRoot>{{value}}</OptionalRoot></PropertyGroup>
            <PropertyGroup Condition="'$(OptionalRoot)' != ''"><AssemblyName>Configured</AssemblyName></PropertyGroup>
            """);
        Assert.Equal(error, result.Error);
        if (error is null) Assert.Equal(assemblyName, result.AssemblyName);
        else Assert.Empty(result.SourceFiles);
        Assert.DoesNotContain("fsharp_semantic_optional_property_assumed_empty", result.PartialReason ?? "");
    }

    [Theory]
    [InlineData("'$(OptionalRoot)' != ''")]
    [InlineData("'' != '$(OptionalRoot)'")]
    [InlineData("$(OptionalRoot) != ''")]
    [InlineData("&quot;$(OptionalRoot)&quot; != &quot;&quot;")]
    public void OptionalGuardAcceptsOnlyTheAbsentScalarAndDoesNotSeedIt(string condition)
    {
        var result = EvaluateBoundedProject($$"""
            <PropertyGroup Condition="{{condition}}"><AssemblyName>WRONG</AssemblyName></PropertyGroup>
            <PropertyGroup><AssemblyName Condition="'$(OptionalRoot)' == ''">WRONG_TOO</AssemblyName></PropertyGroup>
            """);
        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.Contains("fsharp_semantic_optional_property_assumed_empty", result.PartialReason);
        Assert.Empty(result.SourceFiles);
    }

    [Theory]
    [InlineData("'$(OptionalRoot)' == ''")]
    [InlineData("'$(OptionalRoot)x' != ''")]
    [InlineData("'$(OptionalRoot) ' != ''")]
    [InlineData("'$(OptionalRoot)' != ' '")]
    [InlineData("'$(OptionalRoot)' != '' And true")]
    [InlineData("'$(OptionalRoot)' != '' Or true")]
    [InlineData("'$(OptionalRoot.StartsWith('x'))' != ''")]
    public void OptionalGuardDoesNotChangeOtherMissingPropertyConditions(string condition)
    {
        var result = EvaluateBoundedProject($"<PropertyGroup Condition=\"{condition}\"><AssemblyName>WRONG</AssemblyName></PropertyGroup>");
        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.DoesNotContain("fsharp_semantic_optional_property_assumed_empty", result.PartialReason ?? "");
        Assert.Empty(result.SourceFiles);
    }

    [Theory]
    [InlineData("<ItemGroup Condition=\"'$(OptionalRoot)' != ''\"><Compile Include=\"Other.fs\" /></ItemGroup>")]
    [InlineData("<Import Condition=\"'$(OptionalRoot)' != ''\" Project=\"Optional.props\" />")]
    [InlineData("<PropertyGroup><AssemblyName Condition=\"'$(OptionalRoot)' != ''\">WRONG</AssemblyName></PropertyGroup>")]
    [InlineData("<ItemGroup><ProjectReference Include=\"../Dependency/Dependency.fsproj\"><ReferenceOutputAssembly Condition=\"'$(OptionalRoot)' != ''\">false</ReferenceOutputAssembly></ProjectReference></ItemGroup>")]
    public void OptionalGuardDoesNotApplyToOtherElementContexts(string body)
    {
        var result = EvaluateBoundedProject(body);
        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.DoesNotContain("fsharp_semantic_optional_property_assumed_empty", result.PartialReason ?? "");
        Assert.Empty(result.SourceFiles);
    }

    [Fact]
    public void OptionalGuardAllowsLaterAssignmentsButDoesNotWeakenItemOrdering()
    {
        const string before = """
            <PropertyGroup Condition="'$(OptionalRoot)' != ''"><AssemblyName>WRONG</AssemblyName></PropertyGroup>
            <PropertyGroup><OptionalRoot>configured</OptionalRoot></PropertyGroup>
            <PropertyGroup Condition="'$(OptionalRoot)' != ''"><AssemblyName>Configured</AssemblyName></PropertyGroup>
            """;
        var result = EvaluateBoundedProject(before);
        Assert.Null(result.Error);
        Assert.Equal("Configured", result.AssemblyName);
        Assert.Contains("fsharp_semantic_optional_property_assumed_empty", result.PartialReason);
        var ordered = EvaluateBoundedProject(before + """
            <ItemGroup Condition="'$(OptionalRoot)' == 'other'"><Compile Include="Other.fs" /></ItemGroup>
            <PropertyGroup><OptionalRoot>other</OptionalRoot></PropertyGroup>
            """);
        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", ordered.Error);
        Assert.Empty(ordered.SourceFiles);
    }

    [Fact]
    public void OptionalGuardKeepsConditionBoundsAndMalformedMetadataRefusals()
    {
        string overlong = new(' ', ProjectFileParser.MaxFSharpSemanticConditionChars);
        var bounded = EvaluateBoundedProject($"<PropertyGroup Condition=\"{overlong}'$(OptionalRoot)' != ''\"><AssemblyName>WRONG</AssemblyName></PropertyGroup>");
        Assert.Equal("fsharp_semantic_condition_limit", bounded.Error);
        Assert.DoesNotContain("fsharp_semantic_optional_property_assumed_empty", bounded.PartialReason ?? "");
        var metadata = EvaluateBoundedProject("""
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj"><Private><Nested>false</Nested></Private></ProjectReference></ItemGroup>
            """);
        Assert.Equal("fsharp_semantic_project_reference_metadata_unsupported", metadata.Error);
    }

    [Fact]
    public void OptionalGuardAssumptionIsDisclosedByThePublicSemanticResponse()
    {
        string root = Directory.CreateTempSubdirectory("codenav-optional-guard").FullName;
        try
        {
            WriteProject(root, "Directory.Build.props", """
                <Project><PropertyGroup Condition="'$(OptionalBuildToolsRoot)' != ''">
                <AssemblyName>WRONG</AssemblyName></PropertyGroup></Project>
                """);
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\n");
            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt("Core/Core.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Contains("fsharp_semantic_optional_property_assumed_empty", response.GetProperty("partialReason").GetString());
            Assert.Equal("indexed", response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void OptionalGuardAndCopyLocalCaptureExistsAcrossColdAndDeltaIndexes()
    {
        string root = Directory.CreateTempSubdirectory("codenav-optional-exists").FullName;
        try
        {
            const string props = """
                <Project><PropertyGroup Condition="'$(OptionalRoot)' != ''"><AssemblyName>Configured</AssemblyName></PropertyGroup></Project>
                """;
            WriteProject(root, "Directory.Build.props", props);
            WriteProject(root, "Core/Core.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj"><Private>false</Private>
                <ReferenceOutputAssembly Condition="Exists('web.config')">false</ReferenceOutputAssembly></ProjectReference>
                <Compile Include="Core.fs" /></ItemGroup>
                """));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\n");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            using var pinned = new IndexQueries(db, pinReadSnapshot: true);
            bool? Captured()
            {
                using var queries = new IndexQueries(db);
                return queries.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? value) ? value : null;
            }
            Assert.Equal(false, Captured());
            Assert.True(pinned.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? prior));
            Assert.Equal(false, prior);
            WriteProject(root, "Core/web.config", "present");
            DeltaRefresher.Refresh(store, root, ["Core/web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Captured());
            Assert.True(pinned.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? stillPrior));
            Assert.Equal(false, stillPrior);
            WriteProject(root, "Directory.Build.props", props.Replace("<Project>",
                "<Project><PropertyGroup><OptionalRoot>$(Unknown)</OptionalRoot></PropertyGroup>", StringComparison.Ordinal));
            DeltaRefresher.Refresh(store, root, ["Directory.Build.props"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Null(Captured());
            WriteProject(root, "Directory.Build.props", props);
            DeltaRefresher.Refresh(store, root, ["Directory.Build.props"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Captured());
        }
        finally { Cleanup(root); }
    }
}

// This test changes process environment to prove that it is NOT an input to the projection.
// Reuse the assembly's existing nonparallel environment collection to isolate other readers.
[Collection(CSharpCpmEnvironmentIsolationCollection.Name)]
public sealed class SharedMsBuildInputEnvironmentTests
{
    [Fact]
    public void OptionalGuardAnalysisIsIndependentOfProcessEnvironment()
    {
        const string property = "PhoenixOptionalGuardEnvironmentProbe";
        string? previous = Environment.GetEnvironmentVariable(property);
        try
        {
            const string xml = """
                <Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                <PropertyGroup Condition="'$(PhoenixOptionalGuardEnvironmentProbe)' != ''"><AssemblyName>WRONG</AssemblyName></PropertyGroup>
                <ItemGroup><Compile Include="Core.fs" /></ItemGroup></Project>
                """;
            foreach (string? value in new string?[] { null, "external-build-value" })
            {
                Environment.SetEnvironmentVariable(property, value);
                var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
                    "Core/Core.fsproj", xml, "net10.0", "net10.0");
                Assert.Null(result.Error);
                Assert.Equal("Core", result.AssemblyName);
                Assert.Equal(["Core/Core.fs"], result.SourceFiles);
                Assert.Contains("fsharp_semantic_optional_property_assumed_empty", result.PartialReason);
            }
        }
        finally { Environment.SetEnvironmentVariable(property, previous); }
    }
}
