using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void SimpleProvenanceAloneAllowsExactButOmissionsAndUnknownCausesDoNot()
    {
        const string simple = ProjectFileParser.SimpleFSharpProjectModelReason;
        Assert.Equal("exact", NavigationTools.FSharpSemanticConfidence(simple));
        Assert.Equal("exact", NavigationTools.FSharpSemanticConfidence(
            simple + ";fsharp_project_options_imported;fsharp_semantic_simple_package_heuristic"));
        Assert.Equal("indexed", NavigationTools.FSharpSemanticConfidence("fsharp_project_options_imported"));
        foreach (string cause in new[] { "fsharp_simple_project_reference_missing",
                     "fsharp_simple_project_reference_language_unsupported",
                     "fsharp_simple_bare_reference_unresolved", "fsharp_simple_hint_reference_unavailable",
                     "fsharp_simple_package_reference_unavailable", "fsharp_semantic_diagnostics_present",
                     "fsharp_future_unclassified_reason" })
            Assert.Equal("indexed", NavigationTools.FSharpSemanticConfidence(simple + ";" + cause));
    }

    [Theory]
    [InlineData("csproj", "cs")]
    [InlineData("fsproj", "fs")]
    public void SharedSimpleBuilderRetainsDirectProjectInputs(string extension, string language)
    {
        byte[] xml = System.Text.Encoding.UTF8.GetBytes("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>Shared</AssemblyName><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Dependency/Dependency.csproj" />
                <PackageReference Include="Example.Package" Version="1.2.3" />
                <Reference Include="Example"><HintPath>../Lib/Example.dll</HintPath></Reference>
              </ItemGroup>
              <Import Project="../Deployment.targets" />
            </Project>
            """);
        var parsed = SimpleProjectModelBuilder.Build($"Core/Core.{extension}", xml);
        Assert.Equal("parsed", parsed.LoadStatus);
        Assert.Equal("Shared", parsed.Name);
        Assert.Equal(language, parsed.Language);
        Assert.Equal("Dependency/Dependency.csproj", Assert.Single(parsed.ProjectRefRelPaths));
        Assert.Equal(("Example.Package", "1.2.3"), Assert.Single(parsed.PackageRefs));
        Assert.Equal(("Example", "Lib/Example.dll"), Assert.Single(parsed.AssemblyRefs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimpleDefaultCompileOrderingAndSignatureMembershipAreExplicit(bool defaultItems)
    {
        string xml = SdkProjectWithBody("net10.0", """
            <ItemGroup><Compile Include="Z.fsi" /><Compile Include="Z.fs" />
              <Compile Include="A.fs" /><Compile Remove="A.fs" /><Compile Include="A.fs" />
            </ItemGroup>
            """).Replace("<EnableDefaultCompileItems>false", $"<EnableDefaultCompileItems>{defaultItems.ToString().ToLowerInvariant()}");
        var options = ProjectFileParser.ParseSimpleFSharpSemanticOptions("Core/Core.fsproj", xml,
            "net10.0", "net10.0", ["Core/Z.fs", "Core/Z.fsi", "Core/A.fs", "Core/B.fs", "Core/Unused.fsi"], null, default);
        Assert.Null(options.Error);
        Assert.Equal(defaultItems
            ? ["Core/B.fs", "Core/Z.fs", "Core/Z.fsi", "Core/A.fs"]
            : new[] { "Core/Z.fsi", "Core/Z.fs", "Core/A.fs" }, options.SourceFiles);
        Assert.DoesNotContain("Core/Unused.fsi", options.SourceFiles);
    }

    [Fact]
    public void SimpleProjectionUsesOneHostCasePolicyForLiteralGlobExcludeAndRemove()
    {
        var literal = Project("foo.fs");
        var glob = Project("foo*.fs");
        Assert.Equal(literal.SourceFiles, glob.SourceFiles);
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, literal.SourceFiles.Count);
        var removed = Project("*.fs", "<Compile Remove=\"foo*.fs\" />");
        Assert.Equal(OperatingSystem.IsWindows() ? 0 : 1, removed.SourceFiles.Count);
        var excluded = Project("*.fs", exclude: "foo*.fs");
        Assert.Equal(removed.SourceFiles, excluded.SourceFiles);

        FSharpSemanticOptionsSnapshot Project(string include, string extra = "", string? exclude = null) =>
            ProjectFileParser.ParseSimpleFSharpSemanticOptions("Core/Core.fsproj",
                SdkProjectWithBody("net10.0",
                    $"<ItemGroup><Compile Include=\"{include}\" Exclude=\"{exclude}\" />{extra}</ItemGroup>"),
                "net10.0", "net10.0", ["Core/Foo.fs"], null, default);
    }

    [Fact]
    public void SimpleSdkProjectionDoesNotReadPackagesConfigAndFiltersBareExpressions()
    {
        var options = SimpleProjectModelBuilder.BuildFSharp("Core/Core.fsproj",
            SdkProjectWithBody("net10.0", """
                <ItemGroup><Compile Include="Core.fs" /><Reference Include="$(SomeLib)" /></ItemGroup>
                """), "net10.0", "net10.0", ["Core/Core.fs"],
            () => throw new InvalidOperationException("SDK packages.config must not be read"), default);
        Assert.Null(options.Error);
        Assert.Empty(options.BareReferences!);
        var edges = SimpleProjectModelBuilder.BuildFSharpReferences("Core/Core.fsproj",
            SdkProjectWithBody("net10.0", """
                <ItemGroup><ProjectReference Include="../Dependency/Dependency.fsproj" /></ItemGroup>
                <PropertyGroup><OtherFlags>$(Unknown)</OtherFlags></PropertyGroup>
                """), default);
        Assert.Null(edges.Error);
        Assert.Equal("Dependency/Dependency.fsproj", Assert.Single(edges.ProjectReferences).ProjectPath);
        Assert.Empty(edges.SourceFiles);
        Assert.Empty(edges.CommandLineArgs);
        Assert.Empty(edges.PackageReferences ?? []);
    }

    [Fact]
    public void SimpleSignatureAndImplementationOrderSupportsCompilerExactNavigation()
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-signature").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Api.fsi", "Api.fs", "Use.fs"));
            WriteProject(root, "Core/Api.fsi", "module Api\nval value: int\n");
            WriteProject(root, "Core/Api.fs", "module Api\nlet value = 42\n");
            WriteProject(root, "Core/Use.fs", "module Use\nlet answer = Api.value\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Simple);
            var response = Parse(CallSemantic(() => fixture.Tools.SymbolAt("Core/Use.fs", 2, 20, timeoutMs: 60_000)));
            Assert.True(response.GetProperty("found").GetBoolean(), response.ToString());
            Assert.Equal("value", response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.DoesNotContain("fsharp_semantic_diagnostics_present", response.GetProperty("partialReason").GetString());
            var (definition, record) = FSharpSemanticTelemetryAssert.Observe(fixture.Manager.Telemetry,
                () => fixture.Tools.Definition(path: "Core/Use.fs", line: 2, column: 20,
                    mode: "semantic", timeoutMs: 60_000), "definition", "partial",
                assertResponse: definition => Assert.Equal("exact",
                    definition.GetProperty("meta").GetProperty("confidence").GetString()));
            Assert.True(definition.GetProperty("found").GetBoolean(), definition.ToString());
            Assert.Equal("value", definition.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("exact", definition.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.True(definition.GetProperty("partial").GetBoolean());
            Assert.Equal(new[] { "Core/Api.fs", "Core/Api.fsi" },
                definition.GetProperty("declarations").EnumerateArray()
                    .Select(declaration => declaration.GetProperty("path").GetString()).Order().ToArray());
            Assert.Equal(definition.GetProperty("partialReason").GetString(), record.GetProperty("reason").GetString());
            var timing = definition.GetProperty("timing").GetProperty("semanticColdStart");
            foreach (string phase in new[] { "snapshotCaptureMs", "fcsSetupMs", "projectParseAndCheckMs", "fileParseAndCheckMs" })
                Assert.True(timing.GetProperty(phase).GetInt64() >= 0);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SimpleMissingAndUnsupportedProjectReferencesHaveDistinctDisclosures()
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-omission").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup><Compile Include="Core.fs" />
                  <ProjectReference Include="../Missing/Missing.fsproj" />
                  <ProjectReference Include="../Other/Other.csproj" />
                </ItemGroup>
                """));
            WriteProject(root, "Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\nlet answer = value\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Simple);
            var response = Parse(CallSemantic(() => fixture.Tools.SymbolAt("Core/Core.fs", 3, 15, timeoutMs: 60_000)));
            Assert.True(response.GetProperty("found").GetBoolean(), response.ToString());
            string? reasons = response.GetProperty("partialReason").GetString();
            Assert.Contains("fsharp_simple_project_reference_missing", reasons);
            Assert.Contains("fsharp_simple_project_reference_language_unsupported", reasons);
            Assert.Equal("indexed", response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SimpleColdAndDeltaSkipExistsWhileEmptyEvaluatedRefreshPreparesPinnedFacts()
    {
        string root = Directory.CreateTempSubdirectory("cn-model-epoch").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                <Import Project="../Guard.props" Condition="Exists('web.config')" />
                """));
            WriteProject(root, "Guard.props", "<Project><PropertyGroup><AssemblyName>Guarded</AssemblyName></PropertyGroup></Project>");
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\n");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: ProjectModelMode.Simple);
            using var store = new IndexStore(db, createNew: false);
            using var simpleEpoch = new IndexQueries(db, pinReadSnapshot: true);
            Assert.False(simpleEpoch.EvaluatedFSharpInputsReady());
            Assert.False(simpleEpoch.TryGetCapturedMsBuildFilePresence("Core/web.config", out _));
            DeltaRefresher.Refresh(store, root, [], fsharpProjectModel: ProjectModelMode.Simple);
            DeltaRefresher.Refresh(store, root, [], fsharpProjectModel: ProjectModelMode.Evaluated);
            using var evaluatedEpoch = new IndexQueries(db, pinReadSnapshot: true);
            Assert.True(evaluatedEpoch.EvaluatedFSharpInputsReady());
            Assert.True(evaluatedEpoch.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? absent));
            Assert.Equal(false, absent);
            Assert.False(simpleEpoch.EvaluatedFSharpInputsReady());
            Assert.False(simpleEpoch.TryGetCapturedMsBuildFilePresence("Core/web.config", out _));
            WriteProject(root, "Core/web.config", "present");
            DeltaRefresher.Refresh(store, root, [], fsharpProjectModel: ProjectModelMode.Evaluated);
            using (var current = new IndexQueries(db))
            {
                Assert.True(current.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? present));
                Assert.Equal(true, present);
            }
            DeltaRefresher.Refresh(store, root, [], fsharpProjectModel: ProjectModelMode.Simple);
            using (var current = new IndexQueries(db))
            {
                Assert.False(current.EvaluatedFSharpInputsReady());
                Assert.False(current.TryGetCapturedMsBuildFilePresence("Core/web.config", out _));
            }
            Assert.True(evaluatedEpoch.EvaluatedFSharpInputsReady());
            Assert.True(evaluatedEpoch.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? stillAbsent));
            Assert.Equal(false, stillAbsent);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void EvaluatedStartupAfterSimplePreparesFactsWithoutAnyFileEdit()
    {
        string root = Directory.CreateTempSubdirectory("cn-evaluated-start").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                <Import Project="../Guard.props" Condition="Exists('web.config')" />
                """));
            WriteProject(root, "Guard.props", "<Project />");
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 42\nlet answer = value\n");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: ProjectModelMode.Simple);
            using var fixture = Fixture.Start(root, db, ProjectModelMode.Evaluated);
            using var query = new IndexQueries(db, pinReadSnapshot: true);
            Assert.True(query.EvaluatedFSharpInputsReady());
            Assert.True(query.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? absent));
            Assert.Equal(false, absent);
            var response = Parse(CallSemantic(() => fixture.Tools.SymbolAt("Core/Core.fs", 3, 15, timeoutMs: 60_000)));
            Assert.True(response.GetProperty("found").GetBoolean(), response.ToString());
            Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SimpleDeadlineDisclosesIncompleteScanWithoutClaimingRealWorkspaceLowerBound()
    {
        string root = Directory.CreateTempSubdirectory("cn-simple-deadline").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\nlet local = value\n");
            WriteProject(root, "Consumer/Consumer.fsproj", SdkProjectWithBody("net10.0", """
                <ItemGroup><Compile Include="Consumer.fs" /><ProjectReference Include="../Library/Library.fsproj" /></ItemGroup>
                """));
            WriteProject(root, "Consumer/Consumer.fs", "module Consumer\nlet answer = Library.value\n");
            using var fixture = Fixture.Create(root, ProjectModelMode.Simple);
            fixture.Semantic.BeforeFSharpSemanticSourceReadForTest = path =>
            {
                if (path == "Consumer/Consumer.fs") throw new OperationCanceledException();
            };
            var response = ReadReferences(fixture.Tools, "Library/Library.fs", 2, 5);
            Assert.Equal(1, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("scanIncomplete").GetBoolean());
            Assert.True(response.GetProperty("retryRecommended").GetBoolean());
            Assert.Contains("Scan incomplete", response.GetProperty("summary").GetString());
            Assert.Contains("fsharp_workspace_deadline", response.GetProperty("partialReason").GetString());
            AssertApproximateCountScope(response);
        }
        finally { Cleanup(root); }
    }
}
