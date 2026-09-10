using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("false", false, false)]
    [InlineData("true", false, false)]
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    [InlineData("false", true, false)]
    [InlineData("true", true, false)]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    public void ImportSentinelPreservesReferencesAndSkipsSecondDistinctImport(string? initial, bool early, bool namespaced)
    {
        string directoryFile = early ? "Directory.Build.props" : "Directory.Build.targets";
        var imports = new Dictionary<string, string>
        {
            [directoryFile] = """
                <Project>
                  <Import Project="Build/First.targets" Condition="'$(Imported)' != 'true'" />
                  <Import Project="Build/MustNotRun.targets" Condition="'$(Imported)' != 'true'" />
                </Project>
                """,
            ["Build/First.targets"] = """
                <Project>
                  <PropertyGroup><Imported>true</Imported></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj" /></ItemGroup>
                </Project>
                """,
            ["Build/MustNotRun.targets"] = """
                <Project><ItemGroup><ProjectReference Include="../Wrong/Wrong.fsproj" /></ItemGroup></Project>
                """,
        };
        // Seed before early imports too, without relying on environment/global properties.
        if (initial is not null && early)
            imports[directoryFile] = imports[directoryFile].Replace("<Project>",
                $"<Project><PropertyGroup><Imported>{initial}</Imported></PropertyGroup>", StringComparison.Ordinal);
        if (namespaced)
            foreach (string key in imports.Keys.ToArray())
                imports[key] = imports[key].Replace("<Project>",
                    "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">", StringComparison.Ordinal);
        var reads = new List<string>();
        string rootSeed = initial is not null && !early ? $"<Imported>{initial}</Imported>" : "";
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", $$"""
            <Project>
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Core</AssemblyName>{{rootSeed}}</PropertyGroup>
              <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
            </Project>
            """, "net10.0", "net10.0", importResolver: path =>
            {
                reads.Add(path);
                return imports.GetValueOrDefault(path);
            }, directoryBuildPropsPath: early ? directoryFile : null,
            directoryBuildTargetsPath: early ? null : directoryFile);

        Assert.Null(result.Error);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
        Assert.Equal("Core", result.AssemblyName);
        Assert.Equal(initial == "true" ? [] : new[] { new FSharpProjectReferenceSnapshot("Dependency/Dependency.fsproj") },
            result.ProjectReferences);
        Assert.DoesNotContain("Build/MustNotRun.targets", reads);
        if (initial == "true") Assert.DoesNotContain("Build/First.targets", reads);
        if (initial is null) Assert.Contains("fsharp_semantic_import_property_assumed_empty", result.PartialReason);
        else Assert.DoesNotContain("fsharp_semantic_import_property_assumed_empty", result.PartialReason ?? "");
    }

    [Theory]
    [InlineData("<ItemGroup><Compile Include=\"Other.fs\" Condition=\"'$(Imported)' == 'true'\" /></ItemGroup>")]
    [InlineData("<ItemGroup Condition=\"'$(Imported)' == 'true'\"><Compile Include=\"Other.fs\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Reference Include=\"System\" Condition=\"'$(Imported)' == 'true'\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Compile Include=\"Other.fs\" Condition=\"'$(Imported)' == 'false'\" /></ItemGroup>")]
    [InlineData("<ItemGroup Condition=\"'$(Imported)' == 'false'\"><Compile Include=\"Other.fs\" /></ItemGroup>")]
    [InlineData("<ItemGroup><Reference Include=\"System\" Condition=\"'$(Imported)' == 'false'\" /></ItemGroup>")]
    [InlineData("<ItemGroup><ProjectReference Include=\"../Other/Other.fsproj\"><ReferenceOutputAssembly>$(Imported)</ReferenceOutputAssembly></ProjectReference></ItemGroup>")]
    [InlineData("<ItemGroup><Refs Include=\"System\" Condition=\"'$(Imported)' == 'true'\" /></ItemGroup><ItemGroup><Reference Include=\"@(Refs)\" /></ItemGroup>")]
    public void ImportSentinelCannotRewriteAnEarlierItemInput(string body)
    {
        var imports = new Dictionary<string, string>
        {
            ["Directory.Build.targets"] = """
                <Project><Import Project="Build/First.targets" Condition="'$(Imported)' != 'true'" /></Project>
                """,
            ["Build/First.targets"] = "<Project><PropertyGroup><Imported>true</Imported></PropertyGroup></Project>",
        };
        var result = EvaluateBoundedProject("<PropertyGroup><Imported>false</Imported></PropertyGroup>" + body,
            imports, directoryBuildTargetsPath: "Directory.Build.targets");
        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
        Assert.Empty(result.SourceFiles);
    }

    [Theory]
    [InlineData("Imported", "$(Unknown)", false)]
    [InlineData("MSBuildToolsPath", null, false)]
    [InlineData("OS", null, false)]
    [InlineData("Imported", null, true)]
    public void ImportSentinelDoesNotInventAssignedOrBuiltInValues(string name, string? assignment, bool supported)
    {
        string body = assignment is null ? "" : $"<PropertyGroup><{name}>{assignment}</{name}></PropertyGroup>";
        body += $"<Import Project=\"../Build/First.props\" Condition=\"'$({name})' != 'true'\" />";
        var result = EvaluateBoundedProject(body, new Dictionary<string, string>
        {
            ["Build/First.props"] = $"<Project><PropertyGroup><{name}>true</{name}><AssemblyName>ImportedAssembly</AssemblyName></PropertyGroup></Project>",
        });
        if (supported)
        {
            Assert.Null(result.Error);
            Assert.Equal("ImportedAssembly", result.AssemblyName);
            Assert.Contains("fsharp_semantic_import_property_assumed_empty", result.PartialReason);
        }
        else
        {
            Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
            Assert.DoesNotContain("fsharp_semantic_import_property_assumed_empty", result.PartialReason ?? "");
        }
    }

    [Theory]
    [InlineData("<Reference Include=\"Lib\"><HintPath>$(Input)/Lib.dll</HintPath></Reference>", "old", "new")]
    [InlineData("<ProjectReference Include=\"../Dependency/Dependency.fsproj\"><ReferenceOutputAssembly>$(Input)</ReferenceOutputAssembly></ProjectReference>", "true", "false")]
    [InlineData("<ProjectReference Include=\"../Dependency/Dependency.fsproj\"><ReferenceOutputAssembly Condition=\"'$(Input)' == 'true'\">false</ReferenceOutputAssembly></ProjectReference>", "true", "false")]
    public void ImportSentinelConsumedMetadataCannotBeRetargetedByDirectoryTargets(string item, string initial, string later)
    {
        string body = $"<PropertyGroup><Input>{initial}</Input></PropertyGroup><ItemGroup>{item}</ItemGroup>";
        var imports = new Dictionary<string, string>
        {
            ["Directory.Build.targets"] = $"<Project><PropertyGroup><Input>{later}</Input></PropertyGroup></Project>",
        };
        var result = EvaluateBoundedProject(body, imports, directoryBuildTargetsPath: "Directory.Build.targets");
        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);

        // A group proven false does not rewrite a consumed value and must remain harmless.
        imports["Directory.Build.targets"] = imports["Directory.Build.targets"].Replace(
            "<PropertyGroup>", "<PropertyGroup Condition=\"false\">", StringComparison.Ordinal);
        Assert.Null(EvaluateBoundedProject(body, imports, directoryBuildTargetsPath: "Directory.Build.targets").Error);
    }

    [Theory]
    [InlineData("<Compile Include=\"Before.fs\" />")]
    [InlineData("<compile Include=\"Before.fs\" />")]
    [InlineData("<cOmPiLe Include=\"Before.fs\" />")]
    [InlineData("<Reference Include=\"System\" />")]
    [InlineData("<reference Include=\"System\" />")]
    [InlineData("<rEfErEnCe Include=\"System\" />")]
    [InlineData("<ProjectReference Include=\"../Dependency/Dependency.fsproj\" />")]
    [InlineData("<projectreference Include=\"../Dependency/Dependency.fsproj\" />")]
    [InlineData("<pRoJeCtReFeReNcE Include=\"../Dependency/Dependency.fsproj\" />")]
    public void ImportSentinelItemPrecisionMatchesCaseInsensitiveDispatch(string item)
    {
        var result = EvaluateBoundedProject($"<ItemGroup>{item}</ItemGroup><PropertyGroup><Unrelated>true</Unrelated></PropertyGroup>");
        Assert.Null(result.Error);

        string conditioned = item.Replace(" Include=", " Condition=\"'$(Input)' == 'true'\" Include=", StringComparison.Ordinal);
        var consumed = EvaluateBoundedProject($"<PropertyGroup><Input>true</Input></PropertyGroup><ItemGroup>{conditioned}</ItemGroup><PropertyGroup><Input>false</Input></PropertyGroup>");
        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", consumed.Error);
    }

    [Fact]
    public void ImportSentinelPureItemPrecisionPreservesExactPublicConfidence()
    {
        string? baselineReasons = null;
        foreach (bool laterAssignment in new[] { false, true })
        {
            string root = Directory.CreateTempSubdirectory("codenav-item-precision").FullName;
            try
            {
                string project = SdkProject("net10.0", "Core.fs");
                if (laterAssignment)
                    project = project.Replace("</Project>", "<PropertyGroup><Unrelated>true</Unrelated></PropertyGroup></Project>", StringComparison.Ordinal);
                WriteProject(root, "Core/Core.fsproj", project);
                WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\nlet result = value\n");
                using var fixture = Fixture.Create(root);
                string raw = CallSemantic(() => fixture.Tools.SymbolAt("Core/Core.fs", 3, 14, timeoutMs: 60_000));
                var response = Parse(raw);
                Assert.True(response.GetProperty("found").GetBoolean(), raw);
                Assert.Equal("value", response.GetProperty("symbol").GetProperty("name").GetString());
                Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
                string? reasons = response.TryGetProperty("partialReason", out var reason) ? reason.GetString() : null;
                Assert.DoesNotContain("fsharp_semantic_import_property_assumed_empty", reasons ?? "");
                Assert.DoesNotContain("fsharp_semantic_diagnostics_present", reasons ?? "");
                if (laterAssignment) Assert.Equal(baselineReasons, reasons);
                else baselineReasons = reasons;
            }
            finally { Cleanup(root); }
        }
    }

    [Theory]
    [InlineData("<PropertyGroup><Other>true</Other></PropertyGroup>")]
    [InlineData("<PropertyGroup Condition=\"false\"><Imported>true</Imported></PropertyGroup>")]
    [InlineData("<PropertyGroup><Imported Condition=\"false\">true</Imported></PropertyGroup>")]
    [InlineData("<PropertyGroup><Imported>$(Another)</Imported></PropertyGroup>")]
    [InlineData("<Target Name=\"Deploy\"><PropertyGroup><Imported>true</Imported></PropertyGroup></Target>")]
    public void ImportSentinelRequiresTheImportedDocumentsOwnUnconditionalAssignment(string content)
    {
        var result = EvaluateBoundedProject("<Import Project=\"../Build/First.props\" Condition=\"'$(Imported)' != 'true'\" />",
            new Dictionary<string, string> { ["Build/First.props"] = "<Project>" + content + "</Project>" });
        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.DoesNotContain("fsharp_semantic_import_property_assumed_empty", result.PartialReason ?? "");
    }

    [Theory]
    [InlineData("'true' != '$(Imported)'", true)]
    [InlineData("$(Imported) != 'TRUE'", true)]
    [InlineData("'$(Imported)' == ''", false)]
    [InlineData("'$(Imported)' != 'false'", false)]
    [InlineData("'$(Imported)' != 'true' And true", false)]
    [InlineData("'$(Imported)extra' != 'true'", false)]
    public void ImportSentinelAssumptionHasAnExactScalarBoundary(string condition, bool supported)
    {
        var result = EvaluateBoundedProject($"<Import Project=\"../Build/First.props\" Condition=\"{condition}\" />",
            new Dictionary<string, string>
            {
                ["Build/First.props"] = "<Project><PropertyGroup><Imported>true</Imported><AssemblyName>ImportedAssembly</AssemblyName></PropertyGroup></Project>",
            });
        if (supported)
        {
            Assert.Null(result.Error);
            Assert.Equal("ImportedAssembly", result.AssemblyName);
            Assert.Contains("fsharp_semantic_import_property_assumed_empty", result.PartialReason);
        }
        else
        {
            Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
            Assert.DoesNotContain("fsharp_semantic_import_property_assumed_empty", result.PartialReason ?? "");
        }
    }

    [Fact]
    public void ImportSentinelPrecisionRetainsUnknownReadSyntaxBarrier()
    {
        // Copy-local is deliberately ignored by compiler projection, but its unknown syntax
        // cannot be described by the read tracker: precision must not silently default to safe.
        var result = EvaluateBoundedProject("""
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj"><Private>$(Unknown.Function())</Private></ProjectReference></ItemGroup>
            <PropertyGroup><Unrelated>true</Unrelated></PropertyGroup>
            """);
        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
    }

    [Fact]
    public void ImportSentinelTrackingDoesNotTurnCapturedScalarsIntoLiveAliases()
    {
        var result = EvaluateBoundedProject("""
            <PropertyGroup><Original>Before.fs</Original><Captured>$(Original)</Captured></PropertyGroup>
            <ItemGroup><Compile Include="$(Captured)" /></ItemGroup>
            <PropertyGroup><Original>After.fs</Original></PropertyGroup>
            """);
        Assert.Null(result.Error);
        Assert.Equal(["Core/Before.fs", "Core/Core.fs"], result.SourceFiles);
    }

    [Theory]
    [InlineData("<Compile Include=\"$(Input)\" />", "Before.fs", "After.fs")]
    [InlineData("<Reference Include=\"System\"><HintPath>$(Input)</HintPath></Reference>", "Before.dll", "After.dll")]
    [InlineData("<ProjectReference Include=\"../Dependency/Dependency.fsproj\"><ReferenceOutputAssembly Condition=\"'$(Input)' == 'true'\">false</ReferenceOutputAssembly></ProjectReference>", "true", "false")]
    public void ImportSentinelTrackingFreezesEachLiveItemInputChannel(string item, string initial, string later)
    {
        var result = EvaluateBoundedProject($"<PropertyGroup><Input>{initial}</Input></PropertyGroup><ItemGroup>{item}</ItemGroup><PropertyGroup><Input>{later}</Input></PropertyGroup>");
        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
    }

    [Fact]
    public void ImportSentinelAssumptionIsVisibleInPublicSemanticResults()
    {
        string root = Directory.CreateTempSubdirectory("codenav-import-marker").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module Core\nlet result = Dependency.value\n");
            WriteProject(root, "Dependency/Dependency.fsproj", SdkProject("net10.0", "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", "module Dependency\nlet value = 42\n");
            WriteProject(root, "Directory.Build.targets", """
                <Project><ImportGroup Condition="'$(MSBuildProjectName)' == 'Core'">
                  <Import Project="Build/First.targets" Condition="'$(Imported)' != 'true'" />
                  <Import Project="Build/MustNotRun.targets" Condition="'$(Imported)' != 'true'" />
                </ImportGroup></Project>
                """);
            WriteProject(root, "Build/First.targets", """
                <Project><PropertyGroup><Imported>true</Imported></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "Build/MustNotRun.targets", "<Project><Target Name=\"CoreCompile\"><Fsc Sources=\"Wrong.fs\" /></Target></Project>");
            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt("Core/Core.fs", 2, 25, timeoutMs: 60_000));
            var response = Parse(raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("value", response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Contains("fsharp_semantic_import_property_assumed_empty", response.GetProperty("partialReason").GetString());
            Assert.DoesNotContain("fsharp_semantic_diagnostics_present", response.GetProperty("partialReason").GetString());
            Assert.Equal("indexed", response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ImportSentinelExistsCaptureTracksColdDeltaAndPinnedSnapshots()
    {
        string root = Directory.CreateTempSubdirectory("codenav-import-exists").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\n");
            WriteProject(root, "Directory.Build.targets", """
                <Project><Import Project="Build/First.targets" Condition="'$(Imported)' != 'true'" /></Project>
                """);
            const string imported = """
                <Project><PropertyGroup><Imported>true</Imported></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj" Condition="Exists('web.config')" /></ItemGroup>
                </Project>
                """;
            WriteProject(root, "Build/First.targets", imported);
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
            WriteProject(root, "Build/First.targets", imported.Replace("<Imported>true</Imported>", "<Other>true</Other>", StringComparison.Ordinal));
            DeltaRefresher.Refresh(store, root, ["Build/First.targets"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Null(Captured());
            WriteProject(root, "Build/First.targets", imported);
            DeltaRefresher.Refresh(store, root, ["Build/First.targets"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Captured());
        }
        finally { Cleanup(root); }
    }
}

[Collection(CSharpCpmEnvironmentIsolationCollection.Name)]
public sealed class ImportMarkerEnvironmentTests
{
    [Fact]
    public void ImportSentinelAssumptionDoesNotClaimProcessEnvironmentAuthority()
    {
        const string name = "PhoenixImportMarkerEnvironmentProbe";
        string? previous = Environment.GetEnvironmentVariable(name);
        try
        {
            const string project = """
                <Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <Import Project="../Build/First.props" Condition="'$(PhoenixImportMarkerEnvironmentProbe)' != 'true'" />
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """;
            foreach (string? external in new string?[] { null, "true" })
            {
                Environment.SetEnvironmentVariable(name, external);
                var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", project,
                    "net10.0", "net10.0", importResolver: _ => """
                        <Project><PropertyGroup><PhoenixImportMarkerEnvironmentProbe>true</PhoenixImportMarkerEnvironmentProbe>
                          <AssemblyName>SelectedAnalysisContext</AssemblyName></PropertyGroup></Project>
                        """);
                Assert.Null(result.Error);
                Assert.Equal("SelectedAnalysisContext", result.AssemblyName);
                Assert.Contains("fsharp_semantic_import_property_assumed_empty", result.PartialReason);
            }
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }
}
