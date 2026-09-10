using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimpleProjectModelPreservesAuthoredOrderAndDisclosesIgnoredConditions(bool sdk)
    {
        const string body = """
            <PropertyGroup><DefineConstants>PILOT</DefineConstants></PropertyGroup>
            <ItemGroup>
              <Compile Include="Z.fs" />
              <Compile Include="M.fs" Condition="'$(Unknown)' == 'yes'" />
              <Compile Include="A.fs" />
              <ProjectReference Include="../Dependency/Dependency.fsproj" />
              <Reference Include="Lib"><HintPath>../Lib/Lib.dll</HintPath></Reference>
            </ItemGroup>
            <Import Project="../Deployment.targets" Condition="'$(Imported)' != 'true'" />
            """;
        string xml = sdk ? SdkProjectWithBody("net10.0", body) : LegacyProjectWithBody("Core", body);
        string tfm = sdk ? "net10.0" : "net472";
        var options = ProjectFileParser.ParseSimpleFSharpSemanticOptions("Core/Core.fsproj", xml,
            tfm, tfm, ["Core/A.fs", "Core/M.fs", "Core/Z.fs", "Core/Unlisted.fs"], null, default);
        Assert.Null(options.Error);
        Assert.Equal(["Core/Z.fs", "Core/M.fs", "Core/A.fs"], options.SourceFiles);
        Assert.Contains("--define:PILOT", options.CommandLineArgs);
        Assert.Contains(options.ProjectReferences, reference => reference.ProjectPath == "Dependency/Dependency.fsproj");
        Assert.Contains("Lib/Lib.dll", options.HintPathReferences);
        Assert.Contains(ProjectFileParser.SimpleFSharpProjectModelReason, options.PartialReason);
        Assert.Contains("fsharp_project_options_imported", options.PartialReason);
        Assert.Equal("Core", options.AssemblyName);
    }

    [Fact]
    public void SimpleProjectModelDoesNotPretendAnIgnoredImportContributedItsReference()
    {
        string xml = SdkProjectWithBody("net10.0", """
            <Import Project="../References.props" />
            <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
            """);
        const string imported = """
            <Project><ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj" /></ItemGroup></Project>
            """;
        var simple = ProjectFileParser.ParseSimpleFSharpSemanticOptions("Core/Core.fsproj", xml,
            "net10.0", "net10.0", ["Core/Core.fs"], null, default);
        var evaluated = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", xml,
            "net10.0", "net10.0", importResolver: _ => imported);
        Assert.Null(evaluated.Error);
        Assert.Equal("Dependency/Dependency.fsproj", Assert.Single(evaluated.ProjectReferences).ProjectPath);
        Assert.Empty(simple.ProjectReferences);
        Assert.Contains(ProjectFileParser.SimpleFSharpProjectModelReason, simple.PartialReason);
        Assert.Contains("fsharp_project_options_imported", simple.PartialReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimpleProjectModelResolvesRealOrderedSourcesDespiteDeploymentGuard(bool sdk)
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-fs").FullName;
        try
        {
            const string body = """
                <ItemGroup><Compile Include="Z.fs" /><Compile Include="M.fs" /><Compile Include="A.fs" /></ItemGroup>
                <Import Project="../Build/Deployment.targets" Condition="'$(DeploymentImported)' != 'true'" />
                """;
            string xml = sdk ? SdkProjectWithBody("net10.0", body) : LegacyProjectWithBody("Core", body);
            WriteProject(root, "Core/Core.fsproj", xml);
            WriteProject(root, "Core/Z.fs", "module First\nlet value = 41\n");
            WriteProject(root, "Core/M.fs", "module Second\nlet value = First.value + 1\n");
            WriteProject(root, "Core/A.fs", "module Last\nlet answer = Second.value\n");
            WriteProject(root, "Build/Deployment.targets", """
                <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup><DeploymentImported>true</DeploymentImported>
                    <DeployEnabled Condition="'$(Service)' == '' and '$(Name)' != ''">true</DeployEnabled>
                    <DeployDir Condition="'$(DeployDir)' == ''">$(DeployRoot)$(Name)/</DeployDir>
                  </PropertyGroup>
                  <Target Name="Deploy"><Message Text="Not relevant to compilation" /></Target>
                </Project>
                """);
            if (!sdk)
            {
                Directory.CreateDirectory(Path.Combine(root, "Lib"));
                File.Copy(Path.Combine(AppContext.BaseDirectory, "FSharp.Core.dll"), Path.Combine(root, "Lib", "FSharp.Core.dll"));
            }
            using var fixture = Fixture.Create(root, ProjectModelMode.Simple);
            var capabilities = Parse(fixture.Tools.ServerCapabilities());
            Assert.Equal("simple", capabilities.GetProperty("semantic").GetProperty("fsharpProjectModel").GetString());
            Assert.Contains(capabilities.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString() == "fsharp-simple-project-model");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string raw = CallSemantic(() => fixture.Tools.SymbolAt("Core/A.fs", 2, 22, timeoutMs: 60_000));
                var response = Parse(raw);
                Assert.True(response.GetProperty("found").GetBoolean(), raw);
                Assert.Equal("value", response.GetProperty("symbol").GetProperty("name").GetString());
                Assert.Contains(response.GetProperty("declarations").EnumerateArray(),
                    declaration => declaration.GetProperty("path").GetString() == "Core/M.fs");
                Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
                Assert.Contains(ProjectFileParser.SimpleFSharpProjectModelReason, response.GetProperty("partialReason").GetString());
                Assert.DoesNotContain("fsharp_semantic_diagnostics_present", response.GetProperty("partialReason").GetString());
                if (!sdk)
                    Assert.Contains("fsharp_binary_references_snapshotted", response.GetProperty("partialReason").GetString());
            }
            using var evaluated = new SemanticService(fixture.Manager, enableRoslynPersistence: false, fsharpProjectModel: ProjectModelMode.Evaluated);
            var strictTools = new NavigationTools(fixture.Manager, evaluated);
            var strict = Parse(CallSemantic(() => strictTools.SymbolAt("Core/A.fs", 2, 22, timeoutMs: 60_000)));
            Assert.True(strict.TryGetProperty("error", out var strictError));
            Assert.Equal("fsharp_evaluated_inputs_not_ready", strictError.GetString());
            Assert.Contains("PHOENIX_FSHARP_PROJECT_MODEL=evaluated", strict.GetProperty("detail").GetString());
            Assert.DoesNotContain(ProjectFileParser.SimpleFSharpProjectModelReason,
                strict.TryGetProperty("partialReason", out var strictReason) ? strictReason.GetString() ?? "" : "");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SimpleProjectModelUsesTheSameModelForChildrenAndDependentDiscovery()
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-refs").FullName;
        try
        {
            foreach (string name in new[] { "Dependency", "Core" })
            {
                string reference = name == "Core"
                    ? "<ProjectReference Include=\"../Dependency/Dependency.fsproj\" />" : "";
                WriteProject(root, $"{name}/{name}.fsproj", SdkProjectWithBody("net10.0", $$"""
                    <ItemGroup><Compile Include="{{name}}.fs" />{{reference}}</ItemGroup>
                    <Import Project="../Deploy.targets" Condition="'$(Unresolved)' == 'true'" />
                    """));
            }
            WriteProject(root, "Dependency/Dependency.fs", "module Dependency\nlet value = 42\n");
            WriteProject(root, "Core/Core.fs", "module Core\nlet result = Dependency.value\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Simple);
            var symbol = Parse(CallSemantic(() => fixture.Tools.SymbolAt("Core/Core.fs", 2, 26, timeoutMs: 60_000)));
            Assert.True(symbol.GetProperty("found").GetBoolean(), symbol.ToString());
            Assert.Contains(symbol.GetProperty("declarations").EnumerateArray(),
                declaration => declaration.GetProperty("path").GetString() == "Dependency/Dependency.fs");
            var references = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Dependency/Dependency.fs", line: 2, column: 6, mode: "semantic", timeoutMs: 60_000)));
            Assert.False(references.TryGetProperty("error", out _), references.ToString());
            Assert.Equal("exact", references.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Contains(ProjectFileParser.SimpleFSharpProjectModelReason, references.GetProperty("partialReason").GetString());
            Assert.Contains(references.GetProperty("groups").EnumerateArray(),
                group => group.GetProperty("project").GetString() == "Core/Core.fsproj" &&
                         group.GetProperty("count").GetInt32() == 1);
        }
        finally { Cleanup(root); }
    }
    [Fact]
    public void SimpleProjectModelDisclosesPackageHeuristicsWithoutBlockingLocalNavigation()
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-package").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProjectWithBody("net10.0", $$"""
                <ItemGroup><Compile Include="Core.fs" />
                  <Reference Include="MissingBareAssembly" />
                  <Reference Include="MissingHint"><HintPath>Missing.dll</HintPath></Reference>
                  <PackageReference Include="Phoenix.Pilot.Missing.{{Guid.NewGuid():N}}" Version="1.0.0" />
                </ItemGroup>
                """));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\nlet answer = value\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Simple);
            var response = Parse(CallSemantic(() => fixture.Tools.SymbolAt("Core/Core.fs", 3, 15, timeoutMs: 60_000)));
            Assert.True(response.GetProperty("found").GetBoolean(), response.ToString());
            Assert.Equal("value", response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("indexed", response.GetProperty("meta").GetProperty("confidence").GetString());
            string? reason = response.GetProperty("partialReason").GetString();
            Assert.Contains(ProjectFileParser.SimpleFSharpProjectModelReason, reason);
            Assert.Contains("fsharp_semantic_simple_package_heuristic", reason);
            Assert.Contains("fsharp_simple_bare_reference_unresolved", reason);
            Assert.Contains("fsharp_simple_hint_reference_unavailable", reason);
            Assert.Contains("fsharp_simple_package_reference_unavailable", reason);
            Assert.DoesNotContain("fsharp_binary_references_snapshotted", reason);
            Assert.DoesNotContain("fsharp_package_references_snapshotted", reason);
        }
        finally { Cleanup(root); }
    }
}

[Collection(CSharpCpmEnvironmentIsolationCollection.Name)]
public sealed class FSharpProjectModelEnvironmentTests
{
    [Fact]
    public void SimpleProjectModelSelectionIsCapturedOnceAndExplicitConstructorWins()
    {
        const string variable = "PHOENIX_FSHARP_PROJECT_MODEL";
        string? previous = Environment.GetEnvironmentVariable(variable);
        string root = Directory.CreateTempSubdirectory("cn-fs-model").FullName;
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            using var manager = new IndexManager(root);
            using var simple = new SemanticService(manager);
            using var explicitEvaluated = new SemanticService(manager, fsharpProjectModel: ProjectModelMode.Evaluated);
            Environment.SetEnvironmentVariable(variable, "evaluated");
            using var sameManager = new SemanticService(manager);
            using var evaluatedManager = new IndexManager(root);
            using var evaluated = new SemanticService(evaluatedManager);
            Assert.Equal(ProjectModelMode.Simple, sameManager.SelectedFSharpProjectModel);
            Assert.Equal(ProjectModelMode.Simple, simple.SelectedFSharpProjectModel);
            Assert.Equal(ProjectModelMode.Evaluated, explicitEvaluated.SelectedFSharpProjectModel);
            Assert.Equal(ProjectModelMode.Evaluated, evaluated.SelectedFSharpProjectModel);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }
}

public sealed class FSharpSimpleProjectionComparison
{
    [Fact]
    public void BothModelsPreserveAuthoredOrderWithDeploymentOnlyImport()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="../Build/Common.props" />
              <ItemGroup><Compile Include="Z.fs" /><Compile Include="M.fs" /><Compile Include="A.fs" /></ItemGroup>
            </Project>
            """;
        string[] expected = ["Core/Z.fs", "Core/M.fs", "Core/A.fs"];
        var simple = ProjectFileParser.ParseSimpleFSharpSemanticOptions("Core/Core.fsproj", xml,
            "net10.0", "net10.0", expected, null, default);
        var evaluated = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", xml,
            "net10.0", "net10.0", importResolver: _ =>
                "<Project><PropertyGroup><Deployment>true</Deployment></PropertyGroup></Project>");
        Assert.Null(simple.Error);
        Assert.Null(evaluated.Error);
        Assert.Equal(expected, simple.SourceFiles);
        Assert.Equal(expected, evaluated.SourceFiles);
    }
}
