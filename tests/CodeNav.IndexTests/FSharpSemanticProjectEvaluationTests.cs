using System.Text;
using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void LegacyImportedPropsAndChooseReachSemanticResolution()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-project-evaluation").FullName;
        try
        {
            string? fsharpCore = ReferenceAssemblyLocator.FSharpCoreReferencePath("net472", out _);
            Assert.NotNull(fsharpCore);
            string copiedFSharpCore = Path.Combine(root, "Lib", "FSharp.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(copiedFSharpCore)!);
            File.Copy(fsharpCore!, copiedFSharpCore);

            WriteProject(root, "Build/PackagePaths.props", """
                <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <FSharpCoreHint>..\..\Lib\FSharp.Core.dll</FSharpCoreHint>
                    <ImportedDefine>LEGACY_IMPORTED</ImportedDefine>
                  </PropertyGroup>
                </Project>
                """);
            WriteProject(root, "Shared/AssemblyVersionInfo.fs", """
                namespace Legacy

                module Shared =
                #if LEGACY_IMPORTED
                    let importedBranch = 42
                #else
                    let wrongBranch = -1
                #endif
                """);
            WriteProject(root, "src/App/Use.fs", """
                namespace Legacy

                module Use =
                    let result = Shared.importedBranch
                """);
            WriteProject(root, "src/App/App.fsproj", """
                <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <Import Project="..\..\Build\PackagePaths.props" />
                  <PropertyGroup>
                    <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
                    <Platform Condition="'$(Platform)' == ''">AnyCPU</Platform>
                    <VisualStudioVersion Condition="'$(VisualStudioVersion)' == ''">17.0</VisualStudioVersion>
                    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                    <AssemblyName>LegacyApp</AssemblyName>
                  </PropertyGroup>
                  <Choose>
                    <When Condition="'$(VisualStudioVersion)' &gt;= '11.0'">
                      <PropertyGroup>
                        <FSharpTargetsPath>$(MSBuildToolsPath)\Microsoft.FSharp.Targets</FSharpTargetsPath>
                        <SelectedDefine>$(ImportedDefine)</SelectedDefine>
                      </PropertyGroup>
                    </When>
                    <Otherwise>
                      <PropertyGroup>
                        <FSharpTargetsPath>$(MSBuildToolsPath)\Legacy.FSharp.Targets</FSharpTargetsPath>
                        <SelectedDefine>WRONG_BRANCH</SelectedDefine>
                      </PropertyGroup>
                    </Otherwise>
                  </Choose>
                  <PropertyGroup>
                    <DefineConstants>$(DefineConstants);$(SelectedDefine)</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="FSharp.Core">
                      <HintPath>$(FSharpCoreHint)</HintPath>
                    </Reference>
                    <Compile Include="..\..\Shared\AssemblyVersionInfo.fs" />
                    <Compile Include="Use.fs" />
                  </ItemGroup>
                  <Import Project="$(FSharpTargetsPath)" />
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "src/App/Use.fs", 4, 30, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("found", out JsonElement found) &&
                        found.GetBoolean(), raw);
            Assert.Equal("importedBranch",
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("LegacyApp",
                response.GetProperty("symbol").GetProperty("assembly").GetString());
            Assert.Equal("src/App/App.fsproj",
                response.GetProperty("selectedFSharpTypeCheckContext")
                    .GetProperty("project").GetString());
            Assert.Equal("net472",
                response.GetProperty("selectedFSharpTypeCheckContext")
                    .GetProperty("targetFramework").GetString());

            string definitionRaw = CallSemantic(() => fixture.Tools.Definition(
                path: "src/App/Use.fs", line: 4, column: 30, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement definition = Parse(definitionRaw);
            Assert.Contains(definition.GetProperty("declarations").EnumerateArray(), site =>
                site.GetProperty("path").GetString() ==
                "Shared/AssemblyVersionInfo.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ConditionsChooseExistsAndInactiveStage2BItemsAreEvaluatedInOrder()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Build/Exists.props"] = "<Project />",
            ["Build/Inactive.props"] = """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Inactive.Package" Condition="false" />
                    <ProjectReference Include="Inactive.fsproj" Condition="false" />
                  </ItemGroup>
                </Project>
                """,
        };
        const string body = """
            <Import Project="../Build/Inactive.props" />
            <Import Project="../Build/Custom.targets" Condition="false" />
            <Import Project="$(FSharpTargetsPath)" Condition="false" />
            <PropertyGroup>
              <Flavor>Legacy</Flavor>
            </PropertyGroup>
            <Choose>
              <When Condition="Exists('../Build/Exists.props') And Exists(&quot;../Build/Exists.props&quot;) And ('$(Flavor)' == 'Legacy' Or false)">
                <PropertyGroup><DefineConstants>SELECTED</DefineConstants></PropertyGroup>
              </When>
              <Otherwise>
                <PropertyGroup><DefineConstants>WRONG</DefineConstants></PropertyGroup>
              </Otherwise>
            </Choose>
            <ItemGroup>
              <PackageReference Include="Skipped.Package" Condition="'$(Flavor)' != 'Legacy'" />
              <ProjectReference Include="Skipped.fsproj" Condition="false" />
            </ItemGroup>
            """;

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(body, imports);

        Assert.Null(result.Error);
        Assert.Contains("--define:SELECTED", result.CommandLineArgs);
        Assert.DoesNotContain("--define:WRONG", result.CommandLineArgs);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Fact]
    public void DirectoryBuildConditionsAndReferenceListsAreEvaluatedInOrder()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = """
                <Project>
                  <PropertyGroup>
                    <ReferenceFlavor Condition="'$(ReferenceFlavor)' == ''">Monolith</ReferenceFlavor>
                  </PropertyGroup>
                  <ItemGroup Condition="'$(ReferenceFlavor)' == 'Monolith'">
                    <ReferencesToRemove Include="Missing.Reference" />
                    <ReferencesToAdd Include="System.Xml.Linq;System.Runtime" />
                    <InactiveReferences Include="Still.Missing" Condition="false" />
                  </ItemGroup>
                </Project>
                """,
            ["Directory.Build.targets"] = """
                <Project>
                  <ItemGroup>
                    <Reference Remove="@(ReferencesToRemove)" />
                    <Reference Include="@(ReferencesToAdd)" />
                    <Reference Include="@(InactiveReferences)" />
                  </ItemGroup>
                </Project>
                """,
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            "<ItemGroup><Reference Include=\"Missing.Reference\" /></ItemGroup>",
            imports, directoryBuildPropsPath: "Directory.Build.props",
            directoryBuildTargetsPath: "Directory.Build.targets");

        Assert.Null(result.Error);
        Assert.Equal(["System.Runtime", "System.Xml.Linq"],
            result.BareReferences!.OrderBy(reference => reference,
                StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    public void DirectoryBuildTargetsCannotRetargetEarlierReferenceConditions(
        string initialValue, string finalValue)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] =
                $"<Project><PropertyGroup><UseGhost>{initialValue}</UseGhost></PropertyGroup></Project>",
            ["Directory.Build.targets"] =
                $"<Project><PropertyGroup><UseGhost>{finalValue}</UseGhost></PropertyGroup></Project>",
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <ItemGroup>
              <Reference Include="Ghost.Reference"
                         Condition="'$(UseGhost)' == 'true'" />
            </ItemGroup>
            """, imports, directoryBuildPropsPath: "Directory.Build.props",
            directoryBuildTargetsPath: "Directory.Build.targets");

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    public void DirectoryBuildReferenceHelperItemsStartTheSemanticItemPhase(
        string initialValue, string finalValue)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = $$"""
                <Project>
                  <PropertyGroup><RemoveMissing>{{initialValue}}</RemoveMissing></PropertyGroup>
                  <ItemGroup>
                    <ReferencesToRemove Include="Missing.Reference"
                      Condition="'$(RemoveMissing)' == 'true'" />
                  </ItemGroup>
                  <PropertyGroup><RemoveMissing>{{finalValue}}</RemoveMissing></PropertyGroup>
                </Project>
                """,
            ["Directory.Build.targets"] =
                "<Project><ItemGroup><Reference Remove=\"@(ReferencesToRemove)\" /></ItemGroup></Project>",
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            "<ItemGroup><Reference Include=\"Missing.Reference\" /></ItemGroup>",
            imports, directoryBuildPropsPath: "Directory.Build.props",
            directoryBuildTargetsPath: "Directory.Build.targets");

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    public void DirectoryBuildReferenceHelpersTrackEnclosingItemGroupConditions(
        string initialValue, string finalValue)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = $$"""
                <Project>
                  <PropertyGroup><RemoveMissing>{{initialValue}}</RemoveMissing></PropertyGroup>
                  <ItemGroup Condition="'$(RemoveMissing)' == 'true'">
                    <ReferencesToRemove Include="Missing.Reference" />
                  </ItemGroup>
                  <PropertyGroup><RemoveMissing>{{finalValue}}</RemoveMissing></PropertyGroup>
                </Project>
                """,
            ["Directory.Build.targets"] =
                "<Project><ItemGroup><Reference Remove=\"@(ReferencesToRemove)\" /></ItemGroup></Project>",
        };

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported",
            EvaluateBoundedProject(
                "<ItemGroup><Reference Include=\"Missing.Reference\" /></ItemGroup>",
                imports, directoryBuildPropsPath: "Directory.Build.props",
                directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    public void DirectoryBuildReferenceHelpersTrackEnclosingChooseConditions(
        string initialValue, string finalValue)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = $$"""
                <Project>
                  <PropertyGroup><RemoveMissing>{{initialValue}}</RemoveMissing></PropertyGroup>
                  <Choose>
                    <When Condition="'$(RemoveMissing)' == 'true'">
                      <ItemGroup><ReferencesToRemove Include="Missing.Reference" /></ItemGroup>
                    </When>
                  </Choose>
                  <PropertyGroup><RemoveMissing>{{finalValue}}</RemoveMissing></PropertyGroup>
                </Project>
                """,
            ["Directory.Build.targets"] =
                "<Project><ItemGroup><Reference Remove=\"@(ReferencesToRemove)\" /></ItemGroup></Project>",
        };

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported",
            EvaluateBoundedProject(
                "<ItemGroup><Reference Include=\"Missing.Reference\" /></ItemGroup>",
                imports, directoryBuildPropsPath: "Directory.Build.props",
                directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Fact]
    public void DirectoryBuildReferenceHelpersAllowUnrelatedLateProperties()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = """
                <Project>
                  <PropertyGroup><RemoveMissing>true</RemoveMissing></PropertyGroup>
                  <ItemGroup Condition="'$(RemoveMissing)' == 'true'">
                    <ReferencesToRemove Include="Missing.Reference" />
                  </ItemGroup>
                  <PropertyGroup><Unrelated>still-supported</Unrelated></PropertyGroup>
                </Project>
                """,
            ["Directory.Build.targets"] =
                "<Project><ItemGroup><Reference Remove=\"@(ReferencesToRemove)\" /></ItemGroup></Project>",
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            "<ItemGroup><Reference Include=\"Missing.Reference\" /></ItemGroup>",
            imports, directoryBuildPropsPath: "Directory.Build.props",
            directoryBuildTargetsPath: "Directory.Build.targets");
        Assert.Null(result.Error);
        Assert.DoesNotContain("Missing.Reference", result.BareReferences!);
    }

    [Fact]
    public void DirectoryBuildWildcardReferenceOperationsFailClosed()
    {
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] =
                "<Project><ItemGroup><Reference Remove=\"System.*\" /></ItemGroup></Project>",
        };
        Assert.Equal("fsharp_semantic_reference_unresolved", EvaluateBoundedProject(
            "<ItemGroup><Reference Include=\"System.Runtime\" /></ItemGroup>", direct,
            directoryBuildTargetsPath: "Directory.Build.targets").Error);

        var throughList = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] =
                "<Project><ItemGroup><ReferencesToRemove Include=\"System.*\" /></ItemGroup></Project>",
            ["Directory.Build.targets"] =
                "<Project><ItemGroup><Reference Remove=\"@(ReferencesToRemove)\" /></ItemGroup></Project>",
        };
        Assert.Equal("fsharp_semantic_reference_unresolved", EvaluateBoundedProject(
            "<ItemGroup><Reference Include=\"System.Runtime\" /></ItemGroup>", throughList,
            directoryBuildPropsPath: "Directory.Build.props",
            directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Fact]
    public void DirectoryBuildReferenceInputItemsRejectIncludeAndRemoveTogether()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = """
                <Project><ItemGroup>
                  <ReferencesToAdd Include="System.Runtime" Remove="System.Xml.Linq" />
                </ItemGroup></Project>
                """,
            ["Directory.Build.targets"] =
                "<Project><ItemGroup><Reference Include=\"@(ReferencesToAdd)\" /></ItemGroup></Project>",
        };

        Assert.Equal("fsharp_semantic_reference_unresolved", EvaluateBoundedProject("",
            imports, directoryBuildPropsPath: "Directory.Build.props",
            directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Fact]
    public void DirectoryBuildTargetReferencePathMutationsFailClosed()
    {
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] = """
                <Project><Target Name="Retarget">
                  <ItemGroup><ReferencePath Include="Ghost.dll" /></ItemGroup>
                </Target></Project>
                """,
        };
        Assert.Equal("fsharp_semantic_target_evaluation_unsupported",
            EvaluateBoundedProject("", direct,
                directoryBuildTargetsPath: "Directory.Build.targets").Error);

        var chained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] = """
                <Project><Import Project="Build/References.targets"
                  Condition="'$(UnknownReferenceFlavor)' == 'active'" /></Project>
                """,
            ["Build/References.targets"] = direct["Directory.Build.targets"],
        };
        Assert.Equal("fsharp_semantic_condition_property_unresolved",
            EvaluateBoundedProject("", chained,
                directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Fact]
    public void DirectoryBuildCompilerHookedMutationTasksFailClosed()
    {
        const string compilerHook = """
            <Project><Target Name="RewriteSource" BeforeTargets="CoreCompile">
              <WriteLinesToFile File="Core.fs" Lines="module Rewritten" Overwrite="true" />
            </Target></Project>
            """;
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] = compilerHook,
        };
        Assert.Equal("fsharp_semantic_target_evaluation_unsupported",
            EvaluateBoundedProject("", direct,
                directoryBuildTargetsPath: "Directory.Build.targets").Error);

        direct["Directory.Build.targets"] = """
            <Project><Target Name="DescribeBuild" BeforeTargets="CoreCompile">
              <Message Text="diagnostic only" />
            </Target></Project>
            """;
        Assert.Null(EvaluateBoundedProject("", direct,
            directoryBuildTargetsPath: "Directory.Build.targets").Error);

        var chained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] = """
                <Project><Import Project="Build/Rewrite.targets"
                  Condition="'$(UnknownRewriteFlavor)' == 'active'" /></Project>
                """,
            ["Build/Rewrite.targets"] = compilerHook,
        };
        Assert.Equal("fsharp_semantic_condition_property_unresolved",
            EvaluateBoundedProject("", chained,
                directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Theory]
    [InlineData("CoreCompileDependsOn")]
    [InlineData("CompileDependsOn")]
    public void DirectoryBuildCompilerDependencySchedulingFailsClosed(string propertyName)
    {
        string scheduledMutation = $$"""
            <Project>
              <PropertyGroup>
                <{{propertyName}}>$({{propertyName}});RewriteSource</{{propertyName}}>
              </PropertyGroup>
              <Target Name="RewriteSource">
                <WriteLinesToFile File="Core.fs" Lines="module Rewritten" Overwrite="true" />
              </Target>
            </Project>
            """;
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] = scheduledMutation,
        };
        Assert.Equal("fsharp_semantic_target_evaluation_unsupported",
            EvaluateBoundedProject("", direct,
                directoryBuildTargetsPath: "Directory.Build.targets").Error);

        var chained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] = """
                <Project><Import Project="Build/Scheduled.targets"
                  Condition="'$(UnknownSchedule)' == 'active'" /></Project>
                """,
            ["Build/Scheduled.targets"] = scheduledMutation,
        };
        Assert.Equal("fsharp_semantic_condition_property_unresolved",
            EvaluateBoundedProject("", chained,
                directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Fact]
    public void DirectoryBuildInitialTargetsSchedulingFailsClosed()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] = """
                <Project InitialTargets="RewriteSource">
                  <Target Name="RewriteSource">
                    <WriteLinesToFile File="Core.fs" Lines="module Rewritten"
                                      Overwrite="true" />
                  </Target>
                </Project>
                """,
        };

        Assert.Equal("fsharp_semantic_target_evaluation_unsupported",
            EvaluateBoundedProject("", imports,
                directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Fact]
    public void AmbiguousDirectoryBuildAuthorityFailsFSharpEvaluationClosed()
    {
        Assert.Equal("fsharp_semantic_directory_build_ambiguous",
            EvaluateBoundedProject("", hasAmbiguousDirectoryBuildAuthority: true).Error);
    }

    [Fact]
    public void DirectoryBuildDependencyWorklistIsBoundedAtTheDeclaredLimit()
    {
        static FSharpSemanticOptionsSnapshot Evaluate(int count)
        {
            var items = new StringBuilder("<Project><ItemGroup>");
            for (int index = count - 1; index >= 0; index--)
            {
                string value = index == count - 1
                    ? "System.Runtime"
                    : $"@(ReferenceList{index + 1:D4})";
                items.Append($"<ReferenceList{index:D4} Include=\"{value}\" />");
            }
            items.Append("</ItemGroup></Project>");
            var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Directory.Build.props"] = items.ToString(),
            };
            return EvaluateBoundedProject(
                "<ItemGroup><Reference Include=\"@(ReferenceList0000)\" /></ItemGroup>",
                imports, directoryBuildPropsPath: "Directory.Build.props");
        }

        FSharpSemanticOptionsSnapshot atCap = Evaluate(
            ProjectFileParser.MaxFSharpSemanticDependencyNodes);
        Assert.Null(atCap.Error);
        Assert.Equal(["System.Runtime"], atCap.BareReferences);

        Assert.Equal("fsharp_semantic_dependency_limit", Evaluate(
            ProjectFileParser.MaxFSharpSemanticDependencyNodes + 1).Error);
    }

    [Fact]
    public void DirectoryBuildChainedTargetsOnlyFailWhenTheyCanAffectSemanticInputs()
    {
        const string directoryTargets = """
            <Project>
              <Import Project="Build/Chained.targets"
                      Condition="'$(UnknownChainedFlavor)' == 'Release'" />
            </Project>
            """;
        var irrelevantImports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.targets"] = directoryTargets,
            ["Build/Chained.targets"] = """
                <Project>
                  <PropertyGroup Condition="'$(UnknownRedirectFlavor)' == 'Legacy'">
                    <GenerateBindingRedirects>true</GenerateBindingRedirects>
                  </PropertyGroup>
                  <Target Name="Deploy" Condition="'$(UnknownDeployFlavor)' == 'Release'">
                    <Message Text="deployment only" />
                  </Target>
                </Project>
                """,
        };
        FSharpSemanticOptionsSnapshot irrelevant = EvaluateBoundedProject("",
            irrelevantImports, directoryBuildTargetsPath: "Directory.Build.targets");
        Assert.Null(irrelevant.Error);

        var referenceImports = new Dictionary<string, string>(irrelevantImports,
            StringComparer.OrdinalIgnoreCase)
        {
            ["Build/Chained.targets"] = """
                <Project>
                  <Target Name="MutateReferences">
                    <ItemGroup><Reference Include="Unknown.Reference" /></ItemGroup>
                  </Target>
                </Project>
                """,
        };
        FSharpSemanticOptionsSnapshot referenceAffecting = EvaluateBoundedProject("",
            referenceImports, directoryBuildTargetsPath: "Directory.Build.targets");
        Assert.Equal("fsharp_semantic_condition_property_unresolved",
            referenceAffecting.Error);

        referenceImports["Directory.Build.targets"] =
            "<Project><Import Project=\"Build/Chained.targets\" /></Project>";
        FSharpSemanticOptionsSnapshot activeReferenceTarget = EvaluateBoundedProject("",
            referenceImports, directoryBuildTargetsPath: "Directory.Build.targets");
        Assert.Equal("fsharp_semantic_target_evaluation_unsupported",
            activeReferenceTarget.Error);
    }

    [Fact]
    public void DirectoryBuildReferenceItemListCapAndCapPlusOneAreDecisive()
    {
        FSharpSemanticOptionsSnapshot Evaluate(int count)
        {
            string references = string.Join(';', Enumerable.Range(0, count)
                .Select(index => $"Reference{index:D4}"));
            var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Directory.Build.props"] =
                    $"<Project><ItemGroup><ReferencesToAdd Include=\"{references}\" /></ItemGroup></Project>",
                ["Directory.Build.targets"] =
                    "<Project><ItemGroup><Reference Include=\"@(ReferencesToAdd)\" /></ItemGroup></Project>",
            };
            string unrelatedItems = string.Concat(Enumerable.Range(0,
                ProjectFileParser.MaxFSharpSemanticItemListEntries + 1)
                .Select(index => $"<Content Include=\"asset{index:D4}.txt\" />"));
            return EvaluateBoundedProject(
                $"<ItemGroup>{unrelatedItems}</ItemGroup>", imports,
                directoryBuildPropsPath: "Directory.Build.props",
                directoryBuildTargetsPath: "Directory.Build.targets");
        }

        FSharpSemanticOptionsSnapshot atCap = Evaluate(
            ProjectFileParser.MaxFSharpSemanticItemListEntries);
        Assert.Null(atCap.Error);
        Assert.Equal(ProjectFileParser.MaxFSharpSemanticItemListEntries,
            atCap.BareReferences!.Count);

        Assert.Equal("fsharp_semantic_item_list_limit", Evaluate(
            ProjectFileParser.MaxFSharpSemanticItemListEntries + 1).Error);
    }

    [Theory]
    [InlineData("<Import Project=\"../Build/*.props\" />", "fsharp_semantic_import_unsupported")]
    [InlineData("<Import Project=\"../Build/Microsoft.FSharp.Targets\" />", "fsharp_semantic_import_unsupported")]
    [InlineData("<PropertyGroup><FSharpTargetsPath>../Build/Custom.targets</FSharpTargetsPath></PropertyGroup><Import Project=\"$(FSharpTargetsPath)\" />", "fsharp_semantic_import_unsupported")]
    [InlineData("<Import Project=\"../../Outside.props\" />", "fsharp_semantic_import_path_outside_workspace")]
    [InlineData("<Import Project=\"../Build/Missing.props\" />", "fsharp_semantic_import_unavailable")]
    [InlineData("<PropertyGroup><DefineConstants>$([System.String]::Copy('X'))</DefineConstants></PropertyGroup>", "fsharp_semantic_property_function_unsupported")]
    [InlineData("<PropertyGroup Condition=\"'$(Flavor.ToUpper())' == 'X'\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_property_function_unsupported")]
    [InlineData("<PropertyGroup Condition=\"HasTrailingSlash('x')\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_condition_unsupported")]
    [InlineData("<PropertyGroup Condition=\"'x' == 'x')\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_condition_unsupported")]
    [InlineData("<ItemGroup><Compile Include=\"Ghost.fs\" Condition=\"('x' == 'x'\" /></ItemGroup>", "fsharp_semantic_condition_unsupported")]
    [InlineData("<PropertyGroup Condition=\"true Or 'x' == 'x' junk\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_condition_unsupported")]
    [InlineData("<PropertyGroup Condition=\"false And HasTrailingSlash('x')\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_condition_unsupported")]
    [InlineData("<PropertyGroup Condition=\"Exists('../Build/A.props','../Build/B.props')\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_condition_unsupported")]
    [InlineData("<PropertyGroup Condition=\"true Or Exists('../Build/A.props','../Build/B.props')\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_condition_unsupported")]
    [InlineData("<Target Name=\"Mutate\"><ItemGroup><Compile Include=\"Generated.fs\" /></ItemGroup></Target>", "fsharp_semantic_target_evaluation_unsupported")]
    [InlineData("<Target Name=\"Mutate\"><CreateProperty Value=\"X\"><Output TaskParameter=\"Value\" PropertyName=\"DefineConstants\" /></CreateProperty></Target>", "fsharp_semantic_target_evaluation_unsupported")]
    [InlineData("<Import Project=\"$(FSharpTargetsPath)\" /><Target Name=\"CoreCompile\"><Fsc Sources=\"Other.fs\" References=\"Other.dll\" DefineConstants=\"TARGET\" OtherFlags=\"--checked+\" /></Target>", "fsharp_semantic_target_evaluation_unsupported")]
    [InlineData("<ItemDefinitionGroup><Compile><Link>Generated.fs</Link></Compile></ItemDefinitionGroup>", "fsharp_semantic_item_definition_unsupported")]
    public void UnsupportedProjectConstructsFailClosedWithStableCauses(
        string body, string expectedError)
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(body);

        Assert.Equal(expectedError, result.Error);
        Assert.Empty(result.SourceFiles);
    }

    [Fact]
    public void ImportedSemanticItemsAndImportCyclesFailClosed()
    {
        FSharpSemanticOptionsSnapshot importedItems = EvaluateBoundedProject(
            "<Import Project=\"../Build/Items.props\" />",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Build/Items.props"] =
                    "<Project><ItemGroup><Compile Include=\"Injected.fs\" /></ItemGroup></Project>",
            });
        Assert.Equal("fsharp_semantic_import_items_unsupported", importedItems.Error);

        foreach ((string item, string expected) in new[]
                 {
                     ("<PackageReference Include=\"Imported.Package\" />",
                         "fsharp_semantic_package_references_unsupported"),
                     ("<ProjectReference Include=\"Imported.fsproj\" />",
                         "fsharp_semantic_project_references_unsupported"),
                 })
        {
            FSharpSemanticOptionsSnapshot importedStage2B = EvaluateBoundedProject(
                "<Import Project=\"../Build/Stage2B.props\" />",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Build/Stage2B.props"] =
                        $"<Project><ItemGroup>{item}</ItemGroup></Project>",
                });
            Assert.Equal(expected, importedStage2B.Error);
        }

        FSharpSemanticOptionsSnapshot cycle = EvaluateBoundedProject(
            "<Import Project=\"../Build/A.props\" />",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Build/A.props"] = "<Project><Import Project=\"B.props\" /></Project>",
                ["Build/B.props"] = "<Project><Import Project=\"A.props\" /></Project>",
            });
        Assert.Equal("fsharp_semantic_import_cycle", cycle.Error);
    }

    [Fact]
    public void ImportCountCapAndCapPlusOneAreDecisive()
    {
        FSharpSemanticOptionsSnapshot atCap = EvaluateImportCount(
            ProjectFileParser.MaxFSharpSemanticImportFiles);
        Assert.Null(atCap.Error);

        FSharpSemanticOptionsSnapshot overCap = EvaluateImportCount(
            ProjectFileParser.MaxFSharpSemanticImportFiles + 1);
        Assert.Equal("fsharp_semantic_import_count_limit", overCap.Error);
    }

    [Fact]
    public void RepeatedImportOccurrencesAreBoundedEvenWhenTheSnapshotIsCached()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Build/Repeated.props"] =
                "<Project><PropertyGroup><Accumulated>$(Accumulated)X</Accumulated></PropertyGroup></Project>",
        };

        string Imports(int count) => string.Concat(Enumerable.Repeat(
            "<Import Project=\"../Build/Repeated.props\" />", count));

        FSharpSemanticOptionsSnapshot atCap = EvaluateBoundedProject(Imports(
            ProjectFileParser.MaxFSharpSemanticImportOccurrences) +
            "<PropertyGroup><DefineConstants>$(Accumulated)</DefineConstants></PropertyGroup>",
            imports);
        Assert.Null(atCap.Error);
        Assert.Contains("--define:" + new string('X',
            ProjectFileParser.MaxFSharpSemanticImportOccurrences), atCap.CommandLineArgs);
        Assert.Equal("fsharp_semantic_import_occurrence_limit", EvaluateBoundedProject(
            Imports(ProjectFileParser.MaxFSharpSemanticImportOccurrences + 1), imports).Error);
    }

    [Fact]
    public void BoundedProjectEvaluationObservesCancellationBeforeAndDuringImportTraversal()
    {
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            EvaluateBoundedProject("", cancellationToken: preCancelled.Token));

        const string project = """
            <Project>
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Import Project="../Build/One.props" />
              <Import Project="../Build/Two.props" />
              <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
            </Project>
            """;
        using var midEvaluation = new CancellationTokenSource();
        int resolverCalls = 0;
        Assert.Throws<OperationCanceledException>(() =>
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
                "Core/Core.fsproj", project, "net10.0", "net10.0",
                importResolver: _ =>
                {
                    resolverCalls++;
                    midEvaluation.Cancel();
                    return "<Project />";
                },
                cancellationToken: midEvaluation.Token));
        Assert.Equal(1, resolverCalls);
    }

    [Fact]
    public void ImportDepthCapAndCapPlusOneAreDecisive()
    {
        FSharpSemanticOptionsSnapshot atCap = EvaluateImportDepth(
            ProjectFileParser.MaxFSharpSemanticImportDepth);
        Assert.Null(atCap.Error);

        FSharpSemanticOptionsSnapshot overCap = EvaluateImportDepth(
            ProjectFileParser.MaxFSharpSemanticImportDepth + 1);
        Assert.Equal("fsharp_semantic_import_depth_limit", overCap.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateImportUtf8ByteCapAndCapPlusOneAreDecisive(bool multibyte)
    {
        string atCap = PropsWithExactUtf8Bytes(
            ProjectFileParser.MaxFSharpSemanticImportBytes, multibyte);
        Assert.Equal(ProjectFileParser.MaxFSharpSemanticImportBytes,
            Encoding.UTF8.GetByteCount(atCap));
        Assert.Null(EvaluateBoundedProject(
            "<Import Project=\"../Build/Large.props\" />",
            new Dictionary<string, string> { ["Build/Large.props"] = atCap }).Error);

        string overCap = PropsWithExactUtf8Bytes(
            ProjectFileParser.MaxFSharpSemanticImportBytes + 1, multibyte);
        Assert.Equal(ProjectFileParser.MaxFSharpSemanticImportBytes + 1,
            Encoding.UTF8.GetByteCount(overCap));
        Assert.Equal("fsharp_semantic_import_bytes_limit", EvaluateBoundedProject(
            "<Import Project=\"../Build/Large.props\" />",
            new Dictionary<string, string> { ["Build/Large.props"] = overCap }).Error);
    }

    [Fact]
    public void OversizedIndexedImportReportsTheByteLimitRatherThanMissingContent()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-import-bytes").FullName;
        try
        {
            WriteProject(root, "Build/Large.props", PropsWithExactUtf8Bytes(
                ProjectFileParser.MaxFSharpSemanticImportBytes + 1, multibyte: false));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="../Build/Large.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_import_bytes_limit",
                response.GetProperty("error").GetString());
            Assert.Contains("import", response.GetProperty("detail").GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ImportedPropsReachedThroughAWorkspaceJunctionAreNotIndexedOrOpened()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-import-junction").FullName;
        string outside = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-import-outside").FullName;
        string junction = Path.Combine(root, "Build");
        try
        {
            WriteProject(outside, "External.props",
                "<Project><PropertyGroup><DefineConstants>ESCAPED</DefineConstants></PropertyGroup></Project>");
            if (!TryCreateJunction(junction, outside)) return;
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="../Build/External.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_import_unavailable",
                response.GetProperty("error").GetString());
        }
        finally
        {
            RemoveJunction(junction);
            Cleanup(root);
            Cleanup(outside);
        }
    }

    [Fact]
    public void PropertyCountValueAndConditionCapsHaveCapAndCapPlusOneCoverage()
    {
        string AtPropertyCount(int additionalProperties) => string.Concat(
            Enumerable.Range(0, additionalProperties).Select(index =>
                $"<P{index}>value</P{index}>"));
        Assert.Null(EvaluateBoundedProject("<PropertyGroup>" + AtPropertyCount(
            ProjectFileParser.MaxFSharpSemanticProperties - 1) + "</PropertyGroup>").Error);
        Assert.Equal("fsharp_semantic_property_limit", EvaluateBoundedProject(
            "<PropertyGroup>" + AtPropertyCount(
                ProjectFileParser.MaxFSharpSemanticProperties) + "</PropertyGroup>").Error);

        Assert.Null(EvaluateBoundedProject($"<PropertyGroup><P>{new string('x',
            ProjectFileParser.MaxFSharpSemanticPropertyValueChars)}</P></PropertyGroup>").Error);
        Assert.Equal("fsharp_semantic_property_value_limit", EvaluateBoundedProject(
            $"<PropertyGroup><P>{new string('x',
                ProjectFileParser.MaxFSharpSemanticPropertyValueChars + 1)}</P></PropertyGroup>").Error);

        const string comparison = "'x' == 'x'";
        string AtConditionLength(int length) =>
            new string(' ', length - comparison.Length) + comparison;
        Assert.Null(EvaluateBoundedProject($"<PropertyGroup Condition=\"{AtConditionLength(
            ProjectFileParser.MaxFSharpSemanticConditionChars)}\"><P>value</P></PropertyGroup>").Error);
        Assert.Equal("fsharp_semantic_condition_limit", EvaluateBoundedProject(
            $"<PropertyGroup Condition=\"{AtConditionLength(
                ProjectFileParser.MaxFSharpSemanticConditionChars + 1)}\"><P>value</P></PropertyGroup>").Error);

        string UnaryCondition(int depth) => new string('!', depth) + "true";
        Assert.Null(EvaluateBoundedProject($"<PropertyGroup Condition=\"{UnaryCondition(
            ProjectFileParser.MaxFSharpSemanticConditionDepth)}\"><P>value</P></PropertyGroup>").Error);
        Assert.Equal("fsharp_semantic_condition_depth_limit", EvaluateBoundedProject(
            $"<PropertyGroup Condition=\"{UnaryCondition(
                ProjectFileParser.MaxFSharpSemanticConditionDepth + 1)}\"><P>value</P></PropertyGroup>").Error);

        string ParenthesizedCondition(int depth) =>
            new string('(', depth) + "true" + new string(')', depth);
        Assert.Null(EvaluateBoundedProject($"<PropertyGroup Condition=\"{ParenthesizedCondition(
            ProjectFileParser.MaxFSharpSemanticConditionDepth)}\"><P>value</P></PropertyGroup>").Error);
        Assert.Equal("fsharp_semantic_condition_depth_limit", EvaluateBoundedProject(
            $"<PropertyGroup Condition=\"{ParenthesizedCondition(
                ProjectFileParser.MaxFSharpSemanticConditionDepth + 1)}\"><P>value</P></PropertyGroup>").Error);

        string NestedChoose(int depth)
        {
            string body = "<PropertyGroup><DefineConstants>DEEP</DefineConstants></PropertyGroup>";
            for (int index = 0; index < depth; index++)
                body = $"<Choose><When Condition=\"true\">{body}</When></Choose>";
            return body;
        }
        Assert.Null(EvaluateBoundedProject(NestedChoose(
            ProjectFileParser.MaxFSharpSemanticEvaluationDepth - 1)).Error);
        Assert.Equal("fsharp_semantic_evaluation_depth_limit", EvaluateBoundedProject(
            NestedChoose(ProjectFileParser.MaxFSharpSemanticEvaluationDepth)).Error);
    }

    [Fact]
    public void UnknownAmbientConditionPropertiesFailClosedButCanonicalSelfDefaultsWork()
    {
        FSharpSemanticOptionsSnapshot unknown = EvaluateBoundedProject("""
            <PropertyGroup Condition="'$(AmbientFlavor)' == ''">
              <DefineConstants>AMBIENT_WAS_ASSUMED_EMPTY</DefineConstants>
            </PropertyGroup>
            """);
        Assert.Equal("fsharp_semantic_condition_property_unresolved", unknown.Error);

        FSharpSemanticOptionsSnapshot canonicalDefault = EvaluateBoundedProject("""
            <PropertyGroup>
              <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
              <DefineConstants Condition="'$(Configuration)' == 'Debug'">DEBUG</DefineConstants>
            </PropertyGroup>
            """);
        Assert.Null(canonicalDefault.Error);
        Assert.Contains("--define:DEBUG", canonicalDefault.CommandLineArgs);
    }

    [Fact]
    public void UnknownConditionsOnIrrelevantTargetsDoNotBlockSemanticEvaluation()
    {
        FSharpSemanticOptionsSnapshot irrelevant = EvaluateBoundedProject("""
            <Target Name="Package" Condition="'$(AmbientFlavor)' == 'release'">
              <Message Text="Packaging only" />
            </Target>
            """);
        Assert.Null(irrelevant.Error);

        FSharpSemanticOptionsSnapshot semantic = EvaluateBoundedProject("""
            <Target Name="CoreCompile" Condition="'$(AmbientFlavor)' == 'release'">
              <Fsc Sources="Other.fs" />
            </Target>
            """);
        Assert.Equal("fsharp_semantic_condition_property_unresolved", semantic.Error);
    }

    [Fact]
    public void PropertyAssignmentsAfterSemanticItemsFailClosed()
    {
        FSharpSemanticOptionsSnapshot snapshot = EvaluateBoundedProject("""
            <PropertyGroup><Flavor>Before</Flavor></PropertyGroup>
            <ItemGroup>
              <Compile Include="Wrong.fs" Condition="'$(Flavor)' == 'Before'" />
            </ItemGroup>
            <PropertyGroup><Flavor>After</Flavor></PropertyGroup>
            """);

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", snapshot.Error);

        FSharpSemanticOptionsSnapshot targetSnapshot = EvaluateBoundedProject("""
            <PropertyGroup><Flavor>Before</Flavor></PropertyGroup>
            <Choose>
              <When Condition="'$(Flavor)' == 'After'">
                <Target Name="CoreCompile"><Fsc Sources="Other.fs" /></Target>
              </When>
            </Choose>
            <PropertyGroup><Flavor>After</Flavor></PropertyGroup>
            """);

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported",
            targetSnapshot.Error);
    }

    [Fact]
    public void LaterPropertiesCannotRetargetChooseSemanticItemsSilently()
    {
        FSharpSemanticOptionsSnapshot snapshot = EvaluateBoundedProject("""
            <PropertyGroup><Flavor>Before</Flavor></PropertyGroup>
            <Choose>
              <When Condition="'$(Flavor)' == 'After'">
                <ItemGroup><Compile Include="Chosen.fs" /></ItemGroup>
              </When>
            </Choose>
            <PropertyGroup><Flavor>After</Flavor></PropertyGroup>
            """);

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", snapshot.Error);
    }

    [Fact]
    public void SdkAuthorityIsDisclosedOrRejectedBeforeSemanticEvaluation()
    {
        const string explicitItems = """
            <PropertyGroup>
              <TargetFramework>net10.0</TargetFramework>
              <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            </PropertyGroup>
            <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
            """;

        FSharpSemanticOptionsSnapshot standard =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                $"<Project Sdk=\"Microsoft.NET.Sdk\">{explicitItems}</Project>",
                "net10.0", "net10.0");
        Assert.Null(standard.Error);
        Assert.Contains("fsharp_semantic_sdk_implicit_authority", standard.PartialReason);

        FSharpSemanticOptionsSnapshot qualified =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                $"<Project Sdk=\"Microsoft.NET.Sdk/9.0.100\">{explicitItems}</Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_sdk_unsupported", qualified.Error);

        FSharpSemanticOptionsSnapshot custom =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                $"<Project Sdk=\"Custom.FSharp.Sdk\">{explicitItems}</Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_sdk_unsupported", custom.Error);

        FSharpSemanticOptionsSnapshot child =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                $"<Project><Sdk Name=\"Custom.FSharp.Sdk\" />{explicitItems}</Project>",
                "net10.0", "net10.0");
        Assert.Equal("fsharp_semantic_sdk_unsupported", child.Error);

        bool importResolverCalled = false;
        FSharpSemanticOptionsSnapshot sdkImport =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                $"<Project>{explicitItems}<Import Project=\"../Build/Sdk.props\" Sdk=\"Custom.FSharp.Sdk\" /></Project>",
                "net10.0", "net10.0", _ =>
                {
                    importResolverCalled = true;
                    return "<Project />";
                });
        Assert.Equal("fsharp_semantic_sdk_unsupported", sdkImport.Error);
        Assert.False(importResolverCalled);

        foreach (string importedProject in new[]
                 {
                     "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                     "<Project Sdk=\"Custom.FSharp.Sdk\" />",
                     "<Project><Sdk Name=\"Custom.FSharp.Sdk\" /></Project>",
                 })
        {
            FSharpSemanticOptionsSnapshot importedSdk = EvaluateBoundedProject(
                "<Import Project=\"../Build/SdkRoot.props\" />",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Build/SdkRoot.props"] = importedProject,
                });
            Assert.Equal("fsharp_semantic_sdk_unsupported", importedSdk.Error);
        }
    }

    [Fact]
    public void AncestorDirectoryBuildReferenceMutationsReachSemanticResolution()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-build").FullName;
        try
        {
            WriteProject(root, "Directory.Build.props", """
                <Project>
                  <ItemGroup>
                    <ReferencesToRemove Include="Missing.Reference" />
                    <ReferencesToAdd Include="System.Xml.Linq" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Directory.Build.targets", """
                <Project>
                  <ItemGroup>
                    <Reference Remove="@(ReferencesToRemove)" />
                    <Reference Include="@(ReferencesToAdd)" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", """
                namespace Ambient

                module Core =
                    let target = 42
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace Ambient

                module Use =
                    let result = Core.target
                """);
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Missing.Reference" />
                    <Compile Include="Core.fs" />
                    <Compile Include="Use.fs" />
                  </ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 27, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("found", out JsonElement found) &&
                        found.GetBoolean(), raw);
            Assert.Equal("target",
                response.GetProperty("symbol").GetProperty("name").GetString());

            string definitionRaw = CallSemantic(() => fixture.Tools.Definition(
                path: "Core/Use.fs", line: 4, column: 27, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement definition = Parse(definitionRaw);
            Assert.Contains(definition.GetProperty("declarations").EnumerateArray(), site =>
                site.GetProperty("path").GetString() == "Core/Core.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DirectoryBuildTargetsPropertyRetargetingFailsBeforeFcs()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-order").FullName;
        try
        {
            WriteProject(root, "Directory.Build.props", """
                <Project><PropertyGroup><UseExtra>true</UseExtra></PropertyGroup></Project>
                """);
            WriteProject(root, "Directory.Build.targets", """
                <Project><PropertyGroup><UseExtra>false</UseExtra></PropertyGroup></Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet target = 42\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="System.Xml.Linq"
                               Condition="'$(UseExtra)' == 'true'" />
                    <Compile Include="Core.fs" />
                  </ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_evaluation_order_unsupported",
                response.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DirectoryBuildHelperRetargetingFailsBeforeFcs()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-helper-order").FullName;
        try
        {
            WriteProject(root, "Directory.Build.props", """
                <Project>
                  <PropertyGroup><RemoveMissing>true</RemoveMissing></PropertyGroup>
                  <ItemGroup>
                    <ReferencesToRemove Include="Missing.Reference"
                      Condition="'$(RemoveMissing)' == 'true'" />
                  </ItemGroup>
                  <PropertyGroup><RemoveMissing>false</RemoveMissing></PropertyGroup>
                </Project>
                """);
            WriteProject(root, "Directory.Build.targets", """
                <Project><ItemGroup>
                  <Reference Remove="@(ReferencesToRemove)" />
                </ItemGroup></Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet target = 42\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Missing.Reference" />
                    <Compile Include="Core.fs" />
                  </ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_evaluation_order_unsupported",
                response.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DirectoryBuildCompilerHookedMutationFailsBeforeFcs()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-compiler-hook").FullName;
        try
        {
            WriteProject(root, "Directory.Build.targets", """
                <Project><Target Name="RewriteSource" BeforeTargets="CoreCompile">
                  <WriteLinesToFile File="Core/Core.fs" Lines="module Rewritten"
                                    Overwrite="true" />
                </Target></Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet target = 42\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_target_evaluation_unsupported",
                response.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DirectoryBuildCompilerDependencySchedulingFailsBeforeFcs()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-dependency-hook").FullName;
        try
        {
            WriteProject(root, "Directory.Build.targets", """
                <Project>
                  <PropertyGroup>
                    <CoreCompileDependsOn>$(CoreCompileDependsOn);RewriteSource</CoreCompileDependsOn>
                  </PropertyGroup>
                  <Target Name="RewriteSource">
                    <WriteLinesToFile File="Core/Core.fs" Lines="module Rewritten"
                                      Overwrite="true" />
                  </Target>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet target = 42\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_target_evaluation_unsupported",
                response.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void WorkspaceImportCasingFollowsPinnedIndexHostPathPolicy()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-import-case").FullName;
        try
        {
            WriteProject(root, "Build/Defines.props", """
                <Project>
                  <PropertyGroup><ImportedDefine>CASE_IMPORT</ImportedDefine></PropertyGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", """
                module Core
                #if CASE_IMPORT
                let selected = 1
                #else
                let fallback = 2
                #endif
                """);
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="../build/defines.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <DefineConstants>$(ImportedDefine)</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 3, 8, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(response.GetProperty("found").GetBoolean(), raw);
                Assert.Equal("selected",
                    response.GetProperty("symbol").GetProperty("name").GetString());
            }
            else
            {
                Assert.Equal("fsharp_semantic_import_unavailable",
                    response.GetProperty("error").GetString());
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void HostAwareIndexedImportLookupUsesOnlyPinnedRows()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-indexed-case").FullName;
        string dbPath = Path.Combine(root, "index.sqlite");
        try
        {
            using (var store = new IndexStore(dbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                store.InsertFile(transaction, "Build/Defines.props", 0, 0, 0,
                    "config", 0, isGenerated: false, hasTestAttrs: false);
                transaction.Commit();
            }

            // There is deliberately no Build/Defines.props on disk. The fallback is allowed to
            // select only the immutable indexed row, never enumerate the live workspace.
            using var queries = new IndexQueries(dbPath);
            FileHit? hit = queries.FileByPathForHost("build/defines.props");
            if (OperatingSystem.IsWindows())
                Assert.Equal("Build/Defines.props", Assert.IsType<FileHit>(hit).Path);
            else
                Assert.Null(hit);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void HostAwareIndexedImportLookupRejectsAmbiguousCaseAliases()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-indexed-case-ambiguous").FullName;
        string dbPath = Path.Combine(root, "index.sqlite");
        try
        {
            using (var store = new IndexStore(dbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                foreach (string path in new[]
                         { "Build/Defines.props", "build/defines.props" })
                    store.InsertFile(transaction, path, 0, 0, 0,
                        "config", 0, isGenerated: false, hasTestAttrs: false);
                transaction.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            Assert.Null(queries.FileByPathForHost("BUILD/DEFINES.PROPS"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public void DirectoryBuildAuthoritySearchReachesTheWorkspaceRootBeyondFormerCap(
        int directoryDepth)
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-depth").FullName;
        string dbPath = Path.Combine(root, "index.sqlite");
        try
        {
            string nestedDirectory = string.Join('/',
                Enumerable.Repeat("d", directoryDepth / 2));
            using (var store = new IndexStore(dbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                store.InsertFile(transaction, "Directory.Build.props", 0, 0, 0,
                    "config", 0, isGenerated: false, hasTestAttrs: false);
                store.InsertFile(transaction, "Directory.Build.targets", 0, 0, 0,
                    "config", 0, isGenerated: false, hasTestAttrs: false);
                store.InsertFile(transaction,
                    nestedDirectory + "/Directory.Build.props", 0, 0, 0,
                    "config", 0, isGenerated: false, hasTestAttrs: false);
                transaction.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            string projectPath = string.Join('/',
                Enumerable.Repeat("d", directoryDepth)) + "/Core.fsproj";
            Assert.True(queries.HasApplicableDirectoryBuildAuthority(projectPath));
            DirectoryBuildAuthorityPaths authority =
                queries.ApplicableDirectoryBuildAuthority(projectPath);
            Assert.Equal(nestedDirectory + "/Directory.Build.props", authority.PropsPath);
            Assert.Equal("Directory.Build.targets", authority.TargetsPath);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void WindowsDirectoryBuildAuthorityPrefersAnExactIndexedPathOverCaseAliases()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-case-exact").FullName;
        string dbPath = Path.Combine(root, "index.sqlite");
        try
        {
            using (var store = new IndexStore(dbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                foreach (string path in new[]
                         { "Core/Directory.Build.props", "Core/directory.build.props" })
                    store.InsertFile(transaction, path, 0, 0, 0,
                        "config", 0, isGenerated: false, hasTestAttrs: false);
                transaction.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            DirectoryBuildAuthorityPaths authority =
                queries.ApplicableDirectoryBuildAuthority(
                    "Core/Sub/Core.fsproj", useWindowsPathPolicy: true);
            Assert.Equal("Core/Directory.Build.props", authority.PropsPath);
            Assert.False(authority.PropsPathAmbiguous);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void WindowsDirectoryBuildAuthorityStopsAtAnAmbiguousNearestAncestor()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-case-ambiguous").FullName;
        string dbPath = Path.Combine(root, "index.sqlite");
        try
        {
            using (var store = new IndexStore(dbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                foreach (string path in new[]
                         {
                             "Core/DIRECTORY.BUILD.PROPS",
                             "Core/directory.build.props",
                             "Directory.Build.props",
                             "Directory.Build.targets",
                         })
                    store.InsertFile(transaction, path, 0, 0, 0,
                        "config", 0, isGenerated: false, hasTestAttrs: false);
                transaction.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            DirectoryBuildAuthorityPaths authority =
                queries.ApplicableDirectoryBuildAuthority(
                    "Core/Sub/Core.fsproj", useWindowsPathPolicy: true);
            Assert.Null(authority.PropsPath);
            Assert.True(authority.PropsPathAmbiguous);
            Assert.Equal("Directory.Build.targets", authority.TargetsPath);
            Assert.True(authority.HasAmbiguity);
            Assert.True(authority.HasPotentialAuthority);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void CompileOrderDiagnosticDoesNotRejectSupportedConditions()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-compile-detail").FullName;
        try
        {
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="*.fs" /></ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_compile_order_unavailable",
                response.GetProperty("error").GetString());
            string detail = response.GetProperty("detail").GetString()!;
            Assert.Contains("deterministic literal Compile", detail,
                StringComparison.Ordinal);
            Assert.DoesNotContain("unconditional", detail,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ImportedPropertyRefreshInvalidatesTheFcsView()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-import-refresh").FullName;
        try
        {
            WriteProject(root, "Build/Defines.props",
                "<Project><PropertyGroup><ImportedDefine>BEFORE</ImportedDefine></PropertyGroup></Project>");
            WriteProject(root, "Core/Core.fs", """
                module Core
                let beforeValue = 1
                let afterValue = 2
                """);
            WriteProject(root, "Core/Use.fs", """
                module Use
                let result =
                #if BEFORE
                    Core.beforeValue
                #else
                    Core.afterValue
                #endif
                """);
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="../Build/Defines.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <DefineConstants>$(ImportedDefine)</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <Compile Include="Use.fs" />
                  </ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string beforeRaw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 15, timeoutMs: 60_000));
            JsonElement before = Parse(beforeRaw);
            Assert.Equal("beforeValue",
                before.GetProperty("symbol").GetProperty("name").GetString());

            WriteProject(root, "Build/Defines.props",
                "<Project><PropertyGroup><ImportedDefine>AFTER</ImportedDefine></PropertyGroup></Project>");
            Assert.True(fixture.Manager.RequestRefreshForTest(
                ["Build/Defines.props"], out Task refreshed));
            await refreshed.WaitAsync(TimeSpan.FromSeconds(20));

            string afterRaw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 6, 15, timeoutMs: 60_000));
            JsonElement after = Parse(afterRaw);
            Assert.Equal("afterValue",
                after.GetProperty("symbol").GetProperty("name").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static FSharpSemanticOptionsSnapshot EvaluateBoundedProject(
        string body,
        IReadOnlyDictionary<string, string>? imports = null,
        CancellationToken cancellationToken = default,
        string? directoryBuildPropsPath = null,
        string? directoryBuildTargetsPath = null,
        bool hasAmbiguousDirectoryBuildAuthority = false)
    {
        string project = $$"""
            <Project>
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              {{body}}
              <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
            </Project>
            """;
        return ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", project, "net10.0", "net10.0",
            path => imports is not null && imports.TryGetValue(path, out string? content)
                ? content
                : null,
            directoryBuildPropsPath: directoryBuildPropsPath,
            directoryBuildTargetsPath: directoryBuildTargetsPath,
            cancellationToken: cancellationToken,
            hasAmbiguousDirectoryBuildAuthority: hasAmbiguousDirectoryBuildAuthority);
    }

    private static FSharpSemanticOptionsSnapshot EvaluateImportCount(int count)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = new StringBuilder();
        for (int index = 1; index <= count; index++)
        {
            string name = $"P{index:D2}.props";
            body.Append($"<Import Project=\"../Build/{name}\" />");
            imports[$"Build/{name}"] = "<Project />";
        }
        return EvaluateBoundedProject(body.ToString(), imports);
    }

    private static FSharpSemanticOptionsSnapshot EvaluateImportDepth(int depth)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index <= depth; index++)
        {
            string next = index == depth
                ? ""
                : $"<Import Project=\"P{index + 1:D2}.props\" />";
            imports[$"Build/P{index:D2}.props"] = $"<Project>{next}</Project>";
        }
        return EvaluateBoundedProject(
            "<Import Project=\"../Build/P01.props\" />", imports);
    }

    private static string PropsWithExactUtf8Bytes(int byteCount, bool multibyte)
    {
        const string prefix = "<Project><!--";
        const string suffix = "--></Project>";
        int remaining = byteCount - Encoding.UTF8.GetByteCount(prefix + suffix);
        Assert.True(remaining >= 0);
        string filler;
        if (multibyte)
        {
            int pairs = remaining / 2;
            filler = new string('\u00e9', pairs) + (remaining % 2 == 0 ? "" : "x");
        }
        else
        {
            filler = new string('x', remaining);
        }
        return prefix + filler + suffix;
    }
}
