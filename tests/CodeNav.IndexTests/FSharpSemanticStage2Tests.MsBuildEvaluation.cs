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
        Assert.Empty(result.ProjectReferences);
    }

    [Fact]
    public void PropertyStartsWithUsesOrdinalScalarSemanticsAndComposesWithOrExists()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <PropertyGroup Condition="$(TargetFramework.StartsWith('net')) Or Exists('web.config')">
              <DefineConstants>PREFIX_MATCH</DefineConstants>
              <AssemblyName>Before$(TargetFramework.StartsWith('net'))$(TargetFramework.StartsWith('NET'))$(TargetFramework.StartsWith(&quot;net&quot;))After</AssemblyName>
            </PropertyGroup>
            <PropertyGroup Condition="$(TargetFramework.StartsWith('NET'))">
              <DefineConstants>WRONG_CASE</DefineConstants>
            </PropertyGroup>
            """, existsResolver: _ => null);

        Assert.Null(result.Error);
        Assert.Equal("BeforeTrueFalseTrueAfter", result.AssemblyName);
        Assert.Contains("--define:PREFIX_MATCH", result.CommandLineArgs);
        Assert.DoesNotContain("--define:WRONG_CASE", result.CommandLineArgs);
    }

    [Fact]
    public void PropertyStartsWithUnescapesEachScalarOnce()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <PropertyGroup>
              <Escaped>%6eet</Escaped>
              <Malformed>50% faster</Malformed>
              <LineFeed>%0aABC</LineFeed>
              <AssemblyName>Literal$(TargetFramework.StartsWith('%6eet'))Receiver$(Escaped.StartsWith('net'))Single$(Escaped.StartsWith('%256e'))Malformed$(Malformed.StartsWith('50%'))MalformedLiteral$(LineFeed.StartsWith('%A '))</AssemblyName>
              <DefineConstants Condition="$(TargetFramework.StartsWith('%6eet')) And $(Escaped.StartsWith('net'))">ESCAPED_MATCH</DefineConstants>
            </PropertyGroup>
            """);

        Assert.Null(result.Error);
        Assert.Equal(
            "LiteralTrueReceiverTrueSingleFalseMalformedTrueMalformedLiteralFalse",
            result.AssemblyName);
        Assert.Contains("--define:ESCAPED_MATCH", result.CommandLineArgs);
    }

    [Fact]
    public void PropertyStartsWithFailsClosedForAnUnresolvedReceiver()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <PropertyGroup Condition="$(Undefined.StartsWith('x'))">
              <DefineConstants>WRONG_UNRESOLVED</DefineConstants>
            </PropertyGroup>
            """);

        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.DoesNotContain("--define:WRONG_UNRESOLVED", result.CommandLineArgs);
    }

    [Fact]
    public void PropertyStartsWithReceiversParticipateInReferenceEvaluationOrder()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <PropertyGroup><Flavor>net</Flavor></PropertyGroup>
            <ItemGroup><Refs Include="System" Condition="$(Flavor.StartsWith('net'))" /></ItemGroup>
            <PropertyGroup><Flavor>other</Flavor></PropertyGroup>
            <ItemGroup><Reference Include="@(Refs)" /></ItemGroup>
            """);

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
    }

    [Fact]
    public void DirectoryBuildDependencyDiscoveryTracksStartsWithReceivers()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = """
                <Project>
                  <PropertyGroup><Flavor>net</Flavor></PropertyGroup>
                  <ItemGroup><ReferencesToAdd Include="System" Condition="$(Flavor.StartsWith('net'))" /></ItemGroup>
                  <PropertyGroup><Flavor>other</Flavor></PropertyGroup>
                </Project>
                """,
            ["Directory.Build.targets"] =
                "<Project><ItemGroup><Reference Include=\"@(ReferencesToAdd)\" /></ItemGroup></Project>",
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("", imports,
            directoryBuildPropsPath: "Directory.Build.props",
            directoryBuildTargetsPath: "Directory.Build.targets");

        Assert.Equal("fsharp_semantic_evaluation_order_unsupported", result.Error);
    }

    [Fact]
    public void ExistsUsesIndexedNonImportFilesAndCapturesBothPresenceStates()
    {
        var probes = new List<string>();
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <PropertyGroup Condition="Exists('web.config') And Exists('web.config') And !Exists('../Missing/Missing.props')">
              <DefineConstants>INDEXED_EXISTS</DefineConstants>
            </PropertyGroup>
            """, existsResolver: path =>
            {
                probes.Add(path);
                if (path.Equals("Core/web.config", StringComparison.OrdinalIgnoreCase)) return true;
                return null;
            });

        Assert.Null(result.Error);
        Assert.Contains("--define:INDEXED_EXISTS", result.CommandLineArgs);
        Assert.Equal(["Core/web.config"], probes);
        Assert.True(result.ExistsDependencies["Core/web.config"]);
        Assert.False(result.ExistsDependencies["Missing/Missing.props"]);

        FSharpSemanticOptionsSnapshot unavailable = EvaluateBoundedProject(
            "<PropertyGroup Condition=\"Exists('unindexed.txt')\"><DefineConstants>WRONG</DefineConstants></PropertyGroup>",
            existsResolver: _ => null);
        Assert.Equal("fsharp_semantic_condition_unsupported", unavailable.Error);
        Assert.Empty(unavailable.ExistsDependencies);
    }

    [Fact]
    public void IndexedExistsRejectsExcludedInputsAndUnprovenWindowsAliases()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-exists-authority").FullName;
        try
        {
            WriteProject(root, "Core/web.config", "<configuration />");
            WriteProject(root, "Core/obj/web.config", "<configuration />");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var queries = new IndexQueries(db);
            bool? Resolve(string path) => SemanticService.ResolveIndexedFSharpExists(queries, path);

            Assert.True(WorkspaceScanner.IsIndexedFilePath("Core/web.config"));
            Assert.False(WorkspaceScanner.IsIndexedFilePath("Core/obj/web.config"));
            Assert.False(WorkspaceScanner.IsIndexedFilePath("Core/notes.txt"));
            Assert.True(Resolve("Core/web.config") is true);
            Assert.Null(Resolve("Core/obj/web.config"));
            FSharpSemanticOptionsSnapshot excluded = EvaluateBoundedProject(
                "<PropertyGroup Condition=\"!Exists('obj/web.config')\"><DefineConstants>WRONG_ABSENCE</DefineConstants></PropertyGroup>",
                existsResolver: Resolve);
            Assert.Equal("fsharp_semantic_condition_unsupported", excluded.Error);
            Assert.DoesNotContain("--define:WRONG_ABSENCE", excluded.CommandLineArgs);

            if (OperatingSystem.IsWindows())
            {
                Assert.Null(Resolve("Core./web.config"));
                FSharpSemanticOptionsSnapshot alias = EvaluateBoundedProject(
                    "<PropertyGroup Condition=\"!Exists('../Core./web.config')\"><DefineConstants>WRONG_ALIAS_ABSENCE</DefineConstants></PropertyGroup>",
                    existsResolver: Resolve);
                Assert.Equal("fsharp_semantic_condition_unsupported", alias.Error);
                Assert.DoesNotContain("--define:WRONG_ALIAS_ABSENCE", alias.CommandLineArgs);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void LiteralExistsCapturesAbsenceAndKeepsPinnedSnapshotsAcrossRefresh()
    {
        string root = Directory.CreateTempSubdirectory("codenav-exists-snapshot").FullName;
        try
        {
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                <PropertyGroup Condition="!Exists('Web.config')"><DefineConstants>ABSENT</DefineConstants></PropertyGroup>
                <ItemGroup><Compile Include="Core.fs" /></ItemGroup></Project>
                """);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            using var pinned = new IndexQueries(db, pinReadSnapshot: true);
            bool? Resolve(IndexQueries q) => SemanticService.ResolveIndexedFSharpExists(q, "Core/Web.config");
            Assert.Equal(false, Resolve(pinned));
            FSharpSemanticOptionsSnapshot absent = EvaluateBoundedProject(
                "<PropertyGroup Condition=\"!Exists('Web.config')\"><DefineConstants>ABSENT</DefineConstants></PropertyGroup>",
                existsResolver: path => SemanticService.ResolveIndexedFSharpExists(pinned, path));
            Assert.Null(absent.Error);
            Assert.Contains("--define:ABSENT", absent.CommandLineArgs);

            WriteProject(root, "Core/Web.config", "<configuration />");
            using (var beforeRefresh = new IndexQueries(db)) Assert.Equal(false, Resolve(beforeRefresh));
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(false, Resolve(pinned));
            using (var refreshed = new IndexQueries(db)) Assert.Equal(true, Resolve(refreshed));

            File.Delete(Path.Combine(root, "Core", "Web.config"));
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using (var removed = new IndexQueries(db)) Assert.Equal(false, Resolve(removed));

            // A source edit changes the dependency set even though neither target is indexed.
            WriteProject(root, "Core/Core.fsproj", """
                <Project>
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <Import Project="Local.props" />
                  <PropertyGroup Condition="Exists('Other.config')"><AssemblyName>Other</AssemblyName></PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """);
            // app.config is a watched/indexed kind; arbitrary Other.config is deliberately not.
            WriteProject(root, "Core/Local.props", "<Project><PropertyGroup Condition=\"Exists('app.config')\" /></Project>");
            DeltaRefresher.Refresh(store, root, ["Core/Core.fsproj", "Core/Local.props"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var updated = new IndexQueries(db);
            Assert.Null(Resolve(updated));
            Assert.Equal(false, SemanticService.ResolveIndexedFSharpExists(updated, "Core/app.config"));
            Assert.Null(SemanticService.ResolveIndexedFSharpExists(updated, "Core/Other.config"));
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedExistsUsesProjectDirectoryAcrossAppearanceAndRemoval(bool caseVariedImport)
    {
        if (caseVariedImport && !OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-exists-import-base").FullName;
        try
        {
            string importPath = caseVariedImport ? "Build/Shared.props" : "Directory.Build.props";
            WriteProject(root, importPath, """
                <Project><PropertyGroup>
                  <Flavor Condition="Exists('Web.config')">Present</Flavor>
                  <Flavor Condition="!Exists('Web.config')">Absent</Flavor>
                </PropertyGroup></Project>
                """);
            WriteProject(root, "Core/Core.fsproj", $$"""
                <Project>
                  {{(caseVariedImport ? "<Import Project=\"../build/shared.props\" />" : "")}}
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>$(Flavor)</AssemblyName></PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            FSharpSemanticOptionsSnapshot Evaluate()
            {
                using var queries = new IndexQueries(db, pinReadSnapshot: true);
                string? Import(string path) => queries.FileByPathForHost(path) is { } file
                    ? queries.ContentByPathBounded(file.Path, ProjectFileParser.MaxFSharpSemanticImportBytes) : null;
                return ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                    queries.ContentByPathBounded("Core/Core.fsproj", IndexBuilder.MaxStructuralFileBytes)!,
                    "net10.0", "net10.0", Import,
                    path => queries.FileByPathForHost(path)?.Size,
                    directoryBuildPropsPath: caseVariedImport ? null : "Directory.Build.props",
                    existsResolver: path => SemanticService.ResolveIndexedFSharpExists(queries, path));
            }
            FSharpSemanticOptionsSnapshot absent = Evaluate();
            Assert.Null(absent.Error);
            Assert.Equal("Absent", absent.AssemblyName);
            WriteProject(root, "Core/Web.config", "<configuration />");
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            FSharpSemanticOptionsSnapshot present = Evaluate();
            Assert.Null(present.Error);
            Assert.Equal("Present", present.AssemblyName);
            File.Delete(Path.Combine(root, "Core", "Web.config"));
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            FSharpSemanticOptionsSnapshot removed = Evaluate();
            Assert.Null(removed.Error);
            Assert.Equal("Absent", removed.AssemblyName);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void CapturedExistsReevaluatesNewBranchesAcrossTfmsAndSeesImportedEditsInTheWriterTransaction()
    {
        string root = Directory.CreateTempSubdirectory("codenav-exists-branches").FullName;
        try
        {
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project><PropertyGroup>
                  <TargetFrameworks>net9.0;net10.0</TargetFrameworks><ProbeFile>Web.config</ProbeFile>
                </PropertyGroup><Import Project="../Build/Shared.props" />
                <ItemGroup><Compile Include="Core.fs" /></ItemGroup></Project>
                """);
            WriteProject(root, "Build/Shared.props", """
                <Project>
                  <PropertyGroup Condition="'$(TargetFramework)' == 'net9.0' And Exists('app.config')" />
                  <Import Project="Optional.props" Condition="'$(TargetFramework)' == 'net10.0' And Exists('$(ProbeFile)')" />
                </Project>
                """);
            WriteProject(root, "Build/Optional.props", "<Project><PropertyGroup Condition=\"Exists('nested/Web.config')\" /></Project>");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            bool? Resolve(string path)
            {
                using var queries = new IndexQueries(db);
                return SemanticService.ResolveIndexedFSharpExists(queries, path);
            }
            Assert.Equal(false, Resolve("Core/app.config"));
            Assert.Equal(false, Resolve("Core/Web.config"));
            Assert.Null(Resolve("Core/nested/Web.config"));

            WriteProject(root, "Core/Web.config", "<configuration />");
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Resolve("Core/Web.config"));
            Assert.Equal(false, Resolve("Core/app.config"));
            Assert.Equal(false, Resolve("Core/nested/Web.config"));

            WriteProject(root, "Build/Optional.props", "<Project><PropertyGroup Condition=\"Exists('changed/Web.config')\" /></Project>");
            DeltaRefresher.Refresh(store, root, ["Build/Optional.props"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Null(Resolve("Core/nested/Web.config"));
            Assert.Equal(false, Resolve("Core/changed/Web.config"));

            File.Delete(Path.Combine(root, "Core", "Web.config"));
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(false, Resolve("Core/Web.config"));
            Assert.Null(Resolve("Core/changed/Web.config"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LiteralExistsRefreshesNativeAliasesWithDifferentWatcherSpellings()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        string root = Directory.CreateTempSubdirectory("codenav-exists-alias").FullName;
        try
        {
            string probe = OperatingSystem.IsWindows() ? "Core./Web.config" : "Core/Web.config";
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Local.props", $"<Project><PropertyGroup Condition=\"Exists('{probe}')\" /></Project>");
            WriteProject(root, "Root.fsproj", """
                <Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                <Import Project="Local.props" /><ItemGroup><Compile Include="Core/Core.fs" /></ItemGroup></Project>
                """);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            using (var initial = new IndexQueries(db))
                Assert.Equal(false, SemanticService.ResolveIndexedFSharpExists(initial, probe));
            WriteProject(root, "Core/web.config", "<configuration />");
            // The watcher reports the physical spelling, not the spelling in the condition.
            DeltaRefresher.Refresh(store, root, ["Core/web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            bool nativePresence = File.Exists(Path.Combine(root, probe.Replace('/', Path.DirectorySeparatorChar)));
            using (var present = new IndexQueries(db))
                Assert.Equal(nativePresence, SemanticService.ResolveIndexedFSharpExists(present, probe));
            File.Delete(Path.Combine(root, "Core", "web.config"));
            DeltaRefresher.Refresh(store, root, ["Core/web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var removed = new IndexQueries(db);
            Assert.Equal(false, SemanticService.ResolveIndexedFSharpExists(removed, probe));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LiteralExistsKeepsExcludedAndNonRegularPathsUnknownAndRefreshesProbeOnlyChanges()
    {
        string root = Directory.CreateTempSubdirectory("codenav-exists-unsafe").FullName;
        try
        {
            WriteProject(root, "Core/obj/Web.config", "<configuration />");
            Directory.CreateDirectory(Path.Combine(root, "Core", "app.config"));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            foreach (var (name, probe) in new[] { ("Excluded", "obj/Web.config"),
                         ("NonRegular", "app.config"), ("Link", "linked/Web.config"),
                         ("Missing", "missing/Web.config") })
                WriteProject(root, $"Core/{name}.fsproj", $$"""
                    <Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                    <PropertyGroup Condition="Exists('{{probe}}')" />
                    <ItemGroup><Compile Include="Core.fs" /></ItemGroup></Project>
                    """);
            WriteProject(root, "Target/Web.config", "<configuration />");
            Assert.True(TestWorkspaceCleanup.TryCreateDirectoryLink(
                Path.Combine(root, "Core", "linked"), Path.Combine(root, "Target"), out string? failure,
                forceWindowsJunctionFallback: OperatingSystem.IsWindows()), failure);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            using (var queries = new IndexQueries(db))
            {
                Assert.Null(SemanticService.ResolveIndexedFSharpExists(queries, "Core/obj/Web.config"));
                Assert.Null(SemanticService.ResolveIndexedFSharpExists(queries, "Core/app.config"));
                Assert.Null(SemanticService.ResolveIndexedFSharpExists(queries, "Core/linked/Web.config"));
                Assert.Equal(false, SemanticService.ResolveIndexedFSharpExists(queries, "Core/missing/Web.config"));
            }
            Directory.Delete(Path.Combine(root, "Core", "app.config"));
            var result = DeltaRefresher.Refresh(store, root, ["Core/app.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(0, result.AddedFiles + result.ChangedFiles + result.DeletedFiles);
            Assert.NotNull(result.RefreshedAtUtc);
            using (var changed = new IndexQueries(db))
                Assert.Equal(false, SemanticService.ResolveIndexedFSharpExists(changed, "Core/app.config"));

            // A formerly absent ancestor can become a skipped link without creating a files row.
            Assert.True(TestWorkspaceCleanup.TryCreateDirectoryLink(
                Path.Combine(root, "Core", "missing"), Path.Combine(root, "Target"), out failure,
                forceWindowsJunctionFallback: OperatingSystem.IsWindows()), failure);
            DeltaRefresher.Refresh(store, root, null, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var swept = new IndexQueries(db);
            Assert.Null(SemanticService.ResolveIndexedFSharpExists(swept, "Core/missing/Web.config"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExistsDependenciesIndependentlyChangeTheFSharpSemanticFingerprint()
    {
        static string Fingerprint(bool exists) => SemanticService.FSharpSemanticFingerprint(
            "Core/Core.fsproj", "net10.0", "<Project />", ["--target:library"],
            ["Core/Core.fs"], ["module Core\nlet value = 1\n"], [],
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["Core/web.config"] = exists,
            });

        string present = Fingerprint(true);
        string absent = Fingerprint(false);
        Assert.NotEqual(present, absent);
        Assert.Equal(present, Fingerprint(true));
    }

    [Fact]
    public void ImportGroupsPreserveOrderHonorConditionsAndRejectNonImports()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Build/First.props"] =
                "<Project><PropertyGroup><AssemblyName>$(AssemblyName)-FIRST</AssemblyName></PropertyGroup></Project>",
            ["Build/Second.props"] =
                "<Project><PropertyGroup><AssemblyName>$(AssemblyName)-SECOND</AssemblyName></PropertyGroup></Project>",
            ["Build/Skipped.props"] =
                "<Project><PropertyGroup><AssemblyName>SKIPPED</AssemblyName></PropertyGroup></Project>",
        };
        FSharpSemanticOptionsSnapshot ordered = EvaluateBoundedProject("""
            <PropertyGroup><AssemblyName>ROOT</AssemblyName></PropertyGroup>
            <ImportGroup>
              <Import Project="../Build/First.props" />
              <Import Project="../Build/Second.props" />
            </ImportGroup>
            <ImportGroup Condition="false">
              <Import Project="../Build/Skipped.props" />
            </ImportGroup>
            """, imports);

        Assert.Null(ordered.Error);
        Assert.Equal("ROOT-FIRST-SECOND", ordered.AssemblyName);

        FSharpSemanticOptionsSnapshot invalid = EvaluateBoundedProject(
            "<ImportGroup><PropertyGroup /></ImportGroup>");
        Assert.Equal("fsharp_semantic_import_unsupported", invalid.Error);
        Assert.Empty(invalid.SourceFiles);
    }

    [Fact]
    public void ProjectReferencesPreserveLiteralOrderAndBoundedMetadataSemantics()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <ItemGroup>
              <ProjectReference Include="../A/A.fsproj">
                <Project>{11111111-1111-1111-1111-111111111111}</Project>
                <Name>A</Name>
              </ProjectReference>
              <ProjectReference Include="../Skipped/Skipped.fsproj">
                <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
              </ProjectReference>
              <ProjectReference Include="../B/B.fsproj" ReferenceOutputAssembly="true" />
              <ProjectReference Include="../A/A.fsproj" />
            </ItemGroup>
            """);

        Assert.Null(result.Error);
        Assert.Equal(
        [
            new FSharpProjectReferenceSnapshot("A/A.fsproj"),
            new FSharpProjectReferenceSnapshot("B/B.fsproj"),
        ], result.ProjectReferences);

        FSharpSemanticOptionsSnapshot unsupported = EvaluateBoundedProject("""
            <ItemGroup>
              <ProjectReference Include="../A/A.fsproj" Aliases="alternate" />
            </ItemGroup>
            """);
        Assert.Equal("fsharp_semantic_project_reference_metadata_unsupported",
            unsupported.Error);

        FSharpSemanticOptionsSnapshot unsupportedLanguage = EvaluateBoundedProject("""
            <ItemGroup>
              <ProjectReference Include="../VisualBasic/VisualBasic.vbproj" />
            </ItemGroup>
            """);
        Assert.Equal("fsharp_semantic_project_references_unsupported",
            unsupportedLanguage.Error);
    }

    [Fact]
    public void ProjectReferencesHonorSdkTransitivityPolicyAndLegacyDirectness()
    {
        static FSharpSemanticOptionsSnapshot Evaluate(string project) =>
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
                "Core/Core.fsproj", project, "net10.0", "net10.0");

        const string sdkBody = """
            <PropertyGroup>
              <TargetFramework>net10.0</TargetFramework>
              <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            </PropertyGroup>
            <ItemGroup>
              <Compile Include="Core.fs" />
              <ProjectReference Include="../Dependency/Dependency.fsproj" />
            </ItemGroup>
            """;
        FSharpSemanticOptionsSnapshot sdkDefault = Evaluate(
            $"<Project Sdk=\"Microsoft.NET.Sdk\">{sdkBody}</Project>");
        Assert.Null(sdkDefault.Error);
        Assert.True(sdkDefault.ProjectReferencesTransitive);

        FSharpSemanticOptionsSnapshot sdkDirect = Evaluate(
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<DisableTransitiveProjectReferences>true" +
            $"</DisableTransitiveProjectReferences></PropertyGroup>{sdkBody}</Project>");
        Assert.Null(sdkDirect.Error);
        Assert.False(sdkDirect.ProjectReferencesTransitive);

        FSharpSemanticOptionsSnapshot unresolved = Evaluate(
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<DisableTransitiveProjectReferences>$(Unknown)" +
            $"</DisableTransitiveProjectReferences></PropertyGroup>{sdkBody}</Project>");
        Assert.Equal("fsharp_semantic_property_unresolved", unresolved.Error);

        FSharpSemanticOptionsSnapshot legacy = Evaluate($"<Project>{sdkBody}</Project>");
        Assert.Null(legacy.Error);
        Assert.False(legacy.ProjectReferencesTransitive);
    }

    [Fact]
    public void DirectoryBuildConditionsAndReferenceListsAreEvaluatedInOrder()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = """
                <Project>
                  <PropertyGroup>
                    <ReferenceFlavor Condition="'$(ReferenceFlavor)' == ''">Monorepo</ReferenceFlavor>
                  </PropertyGroup>
                  <ItemGroup Condition="'$(ReferenceFlavor)' == 'Monorepo'">
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
                    <ProjectReference Include="Shared/Targets.fsproj" />
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
        Assert.Equal(
        [
            new FSharpProjectReferenceSnapshot("Core/Shared/Targets.fsproj"),
        ], result.ProjectReferences);
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
    [InlineData("<PropertyGroup><RootDir>$([MSBuild]::MakeRelative('$(MSBuildThisFileDirectory)', '$(MSBuildProjectDirectory)'))</RootDir></PropertyGroup>", "fsharp_semantic_property_function_unsupported")]
    [InlineData("<PropertyGroup Condition=\"$(TargetFramework.Contains('net'))\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_property_function_unsupported")]
    [InlineData("<PropertyGroup Condition=\"$(TargetFramework.StartsWith('net').StartsWith('T'))\"><DefineConstants>X</DefineConstants></PropertyGroup>", "fsharp_semantic_property_function_unsupported")]
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

        FSharpSemanticOptionsSnapshot importedPackage = EvaluateBoundedProject(
            "<Import Project=\"../Build/Packages.props\" />",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Build/Packages.props"] =
                    "<Project><ItemGroup><PackageReference Include=\"Imported.Package\" Version=\"1.2.3\" /></ItemGroup></Project>",
            });
        Assert.Null(importedPackage.Error);
        Assert.Equal([new FSharpPackageReferenceSnapshot("Imported.Package", "1.2.3")],
            importedPackage.PackageReferences);

        FSharpSemanticOptionsSnapshot importedProject = EvaluateBoundedProject(
            "<Import Project=\"../Build/Projects.props\" />",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Build/Projects.props"] =
                    "<Project><ItemGroup><ProjectReference Include=\"Imported.fsproj\" /></ItemGroup></Project>",
            });
        Assert.Null(importedProject.Error);
        Assert.Equal([new FSharpProjectReferenceSnapshot("Core/Imported.fsproj")],
            importedProject.ProjectReferences);

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
    public void CentralPackageManagementUsesImportedConditionalPackageVersions()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Core/Directory.Packages.props"] = """
                <Project>
                  <Import Project="../Build/Versions.props" />
                </Project>
                """,
            ["Build/Versions.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Example.Package" Version="1.0.0" />
                    <PackageVersion Update="Example.Package" Version="9.0.0"
                                    Condition="'$(TargetFramework)' == 'net9.0'" />
                    <PackageVersion Update="Example.Package" Version="10.0.10"
                                    Condition="'$(TargetFramework)' == 'net10.0'" />
                  </ItemGroup>
                </Project>
                """,
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            "<ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup>",
            imports, directoryPackagesPropsPath: "Core/Directory.Packages.props");

        Assert.Null(result.Error);
        Assert.Equal([new FSharpPackageReferenceSnapshot("Example.Package", "10.0.10")],
            result.PackageReferences);

        FSharpSemanticOptionsSnapshot overridden = EvaluateBoundedProject(
            "<ItemGroup><PackageReference Include=\"Example.Package\" VersionOverride=\"10.0.11\" /></ItemGroup>",
            imports, directoryPackagesPropsPath: "Core/Directory.Packages.props");
        Assert.Null(overridden.Error);
        Assert.Equal([new FSharpPackageReferenceSnapshot("Example.Package", "10.0.11")],
            overridden.PackageReferences);
    }

    [Theory]
    [InlineData("<PackageReference Include=\"Example.Package\" VersionOverride=\"10.0.11\" Version=\"10.0.10\" />")]
    [InlineData("<PackageReference Include=\"Example.Package\" VersionOverride=\"10.0.11\"><Version>10.0.10</Version></PackageReference>")]
    public void CentralPackageReferenceRejectsConflictingVersionSources(string packageReference)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                </Project>
                """,
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            $"<ItemGroup>{packageReference}</ItemGroup>", imports,
            directoryPackagesPropsPath: "Directory.Packages.props");

        Assert.Equal("fsharp_semantic_package_reference_unresolved", result.Error);
        Assert.Empty(result.PackageReferences!);
    }

    [Fact]
    public void CentralPackageVersionRejectsConflictingVersionSources()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Example.Package" Version="10.0.10">
                      <Version>10.0.11</Version>
                    </PackageVersion>
                  </ItemGroup>
                </Project>
                """,
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            "<ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup>",
            imports, directoryPackagesPropsPath: "Directory.Packages.props");

        Assert.Equal("fsharp_semantic_central_package_management_unsupported", result.Error);
        Assert.Empty(result.PackageReferences!);
    }

    [Fact]
    public void CentralPackageVersionRejectsUnexpandableVersionSource()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Example.Package" Version="$(Undefined)" />
                  </ItemGroup>
                </Project>
                """,
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            "<ItemGroup><PackageReference Include=\"Example.Package\" /></ItemGroup>",
            imports, directoryPackagesPropsPath: "Directory.Packages.props");

        Assert.Equal("fsharp_semantic_central_package_management_unsupported", result.Error);
        Assert.Empty(result.PackageReferences!);
    }

    [Fact]
    public void CentralPackageManagementFailsClosedForMissingOrInvalidAuthority()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Known.Package" Version="1.2.3" />
                  </ItemGroup>
                </Project>
                """,
        };

        Assert.Equal("fsharp_semantic_package_reference_unresolved",
            EvaluateBoundedProject(
                "<ItemGroup><PackageReference Include=\"Missing.Package\" /></ItemGroup>",
                imports, directoryPackagesPropsPath: "Directory.Packages.props").Error);
        Assert.Equal("fsharp_semantic_package_reference_unresolved",
            EvaluateBoundedProject(
                "<ItemGroup><PackageReference Include=\"Known.Package\" Version=\"1.2.3\" /></ItemGroup>",
                imports, directoryPackagesPropsPath: "Directory.Packages.props").Error);
        Assert.Equal("fsharp_semantic_package_reference_unresolved",
            EvaluateBoundedProject(
                "<ItemGroup><PackageReference Include=\"Known.Package\" VersionOverride=\"1.2.4\" /></ItemGroup>",
                imports, directoryPackagesPropsPath: "Directory.Packages.props").Error);
        Assert.Equal("fsharp_semantic_directory_packages_ambiguous",
            EvaluateBoundedProject(
                "<ItemGroup><PackageReference Include=\"Known.Package\" /></ItemGroup>",
                imports, directoryPackagesPropsPath: "Directory.Packages.props",
                hasAmbiguousDirectoryPackagesAuthority: true).Error);

        imports["Directory.Packages.props"] = """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Known.Package" Version="[1.0,2.0)" />
              </ItemGroup>
            </Project>
            """;
        Assert.Equal("fsharp_semantic_central_package_management_unsupported",
            EvaluateBoundedProject(
                "<ItemGroup><PackageReference Include=\"Known.Package\" /></ItemGroup>",
                imports, directoryPackagesPropsPath: "Directory.Packages.props").Error);
    }

    [Fact]
    public void InvalidCentralPackageBooleansPreserveTheirSpecificCause()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>sometimes</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Known.Package" Version="1.2.3" />
                  </ItemGroup>
                </Project>
                """,
        };

        Assert.Equal("fsharp_semantic_central_package_management_unsupported",
            EvaluateBoundedProject(
                "<ItemGroup><PackageReference Include=\"Known.Package\" /></ItemGroup>",
                imports, directoryPackagesPropsPath: "Directory.Packages.props").Error);

        imports["Directory.Packages.props"] = """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <CentralPackageVersionOverrideEnabled>sometimes</CentralPackageVersionOverrideEnabled>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Known.Package" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """;
        Assert.Equal("fsharp_semantic_central_package_management_unsupported",
            EvaluateBoundedProject(
                "<ItemGroup><PackageReference Include=\"Known.Package\" VersionOverride=\"1.2.4\" /></ItemGroup>",
                imports, directoryPackagesPropsPath: "Directory.Packages.props").Error);
    }

    [Fact]
    public void PackageReferenceUpdateDoesNotCreateAnUndeclaredItem()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <ItemGroup>
              <PackageReference Update="Missing.Versionless" />
              <PackageReference Update="Missing.Versioned" Version="1.2.3" />
            </ItemGroup>
            """);

        Assert.Null(result.Error);
        Assert.NotNull(result.PackageReferences);
        Assert.Empty(result.PackageReferences);

        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                </Project>
                """,
        };
        FSharpSemanticOptionsSnapshot centrallyManaged = EvaluateBoundedProject("""
            <ItemGroup>
              <PackageReference Update="Missing.Versionless" />
              <PackageReference Update="Missing.Versioned" Version="1.2.3" />
            </ItemGroup>
            """, imports, directoryPackagesPropsPath: "Directory.Packages.props");
        Assert.Null(centrallyManaged.Error);
        Assert.NotNull(centrallyManaged.PackageReferences);
        Assert.Empty(centrallyManaged.PackageReferences);
    }

    [Fact]
    public void SemanticItemDispatchAcceptsCaseInsensitivePackageReferenceNames()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <ItemGroup>
              <packagereference Include="Lower.Package" Version="1.2.3" />
              <PaCkAgErEfErEnCe Include="Mixed.Package" Version="4.5.6" />
            </ItemGroup>
            """);

        Assert.Null(result.Error);
        Assert.Equal([
                new FSharpPackageReferenceSnapshot("Lower.Package", "1.2.3"),
                new FSharpPackageReferenceSnapshot("Mixed.Package", "4.5.6"),
            ],
            result.PackageReferences);
    }

    [Theory]
    [InlineData("false", "true", false)]
    [InlineData("true", "false", false)]
    [InlineData("true", "true", true)]
    public void GlobalPackageReferencesFollowNuGetProjectionFlags(string centrallyManaged,
        string globalsEnabled, bool expected)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Packages.props"] = $$"""
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>{{centrallyManaged}}</ManagePackageVersionsCentrally>
                    <RestoreEnableGlobalPackageReference>{{globalsEnabled}}</RestoreEnableGlobalPackageReference>
                  </PropertyGroup>
                  <ItemGroup>
                    <GlobalPackageReference Include="Global.Tool" Version="1.2.3" />
                  </ItemGroup>
                </Project>
                """,
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("",
            imports, directoryPackagesPropsPath: "Directory.Packages.props");

        Assert.Null(result.Error);
        Assert.Equal(expected
                ? [new FSharpPackageReferenceSnapshot("Global.Tool", "1.2.3", IncludeCompileAssets: false)]
                : Array.Empty<FSharpPackageReferenceSnapshot>(),
            result.PackageReferences);
    }

    [Fact]
    public void DirectPackageReferenceVersionGrammarIsBounded()
    {
        FSharpSemanticOptionsSnapshot range = EvaluateBoundedProject(
            "<ItemGroup><PackageReference Include=\"Range.Package\" Version=\"[1.0,2.0)\" /></ItemGroup>");
        Assert.Null(range.Error);
        Assert.Equal([new FSharpPackageReferenceSnapshot("Range.Package", "[1.0,2.0)")],
            range.PackageReferences);

        FSharpSemanticOptionsSnapshot floating = EvaluateBoundedProject(
            "<ItemGroup><PackageReference Include=\"Float.Package\" Version=\"1.2.*\" /></ItemGroup>");
        Assert.Null(floating.Error);
        Assert.Equal([new FSharpPackageReferenceSnapshot("Float.Package", "1.2.*")],
            floating.PackageReferences);

        Assert.Equal("fsharp_semantic_package_reference_unresolved",
            EvaluateBoundedProject(
                "<ItemGroup><PackageReference Include=\"Unsupported.Package\" Version=\"*\" /></ItemGroup>")
                .Error);
    }

    [Theory]
    [InlineData("ExcludeAssets", "compile")]
    [InlineData("IncludeAssets", "runtime; build; native")]
    [InlineData("Aliases", "PackageAlias")]
    public void PackageReferenceCompilerMetadataFailsClosed(string metadataName,
        string metadataValue)
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject($$"""
            <ItemGroup>
              <PackageReference Include="Metadata.Package" Version="1.2.3"
                                {{metadataName}}="{{metadataValue}}" />
            </ItemGroup>
            """);

        Assert.Equal("fsharp_semantic_package_reference_metadata_unsupported", result.Error);
        Assert.NotNull(result.PackageReferences);
        Assert.Empty(result.PackageReferences);
    }

    [Fact]
    public void InactivePackageReferenceCompilerMetadataDoesNotBlockEvaluation()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <ItemGroup>
              <PackageReference Include="Metadata.Package" Version="1.2.3">
                <Aliases Condition="false">PackageAlias</Aliases>
              </PackageReference>
            </ItemGroup>
            """);

        Assert.Null(result.Error);
        Assert.Equal([new FSharpPackageReferenceSnapshot("Metadata.Package", "1.2.3")],
            result.PackageReferences);
    }

    [Fact]
    public void DirectoryBuildPropertiesSelectCentralPackageVersions()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = """
                <Project>
                  <PropertyGroup><PackageChannel>Current</PackageChannel></PropertyGroup>
                </Project>
                """,
            ["Directory.Packages.props"] = """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Known.Package" Version="1.2.3"
                                    Condition="'$(PackageChannel)' == 'Current'" />
                  </ItemGroup>
                </Project>
                """,
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            "<ItemGroup><PackageReference Include=\"Known.Package\" /></ItemGroup>",
            imports, directoryPackagesPropsPath: "Directory.Packages.props",
            directoryBuildPropsPath: "Directory.Build.props");

        Assert.Null(result.Error);
        Assert.Equal([new FSharpPackageReferenceSnapshot("Known.Package", "1.2.3")],
            result.PackageReferences);
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
              <LocalFlavor Condition="'$(LocalFlavor)' == ''">Debug</LocalFlavor>
              <DefineConstants Condition="'$(LocalFlavor)' == 'Debug'">DEBUG</DefineConstants>
            </PropertyGroup>
            """);
        Assert.Null(canonicalDefault.Error);
        Assert.Contains("--define:DEBUG", canonicalDefault.CommandLineArgs);
    }

    [Theory]
    [InlineData("Directory.Build.props", false)]
    [InlineData("Directory.Packages.props", false)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("Directory.Packages.props", true)]
    public void AnalysisContextIsCompleteBeforeEarlyImportedConditions(string importPath, bool selfDefaults)
    {
        string assignments = selfDefaults ? """
            <Configuration Condition="'$(Configuration)' == ''">Release</Configuration>
            <Platform Condition="'$(Platform)' == ''">x86</Platform>
            """ : "";
        string props = $$"""
            <Project>
              <PropertyGroup>
                {{assignments}}
                <AssemblyName>$(Configuration)|$(Platform)</AssemblyName>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0' and '$(Configuration)|$(Platform)' == 'Debug|AnyCPU'">
                <DefineConstants>MATCHED_EARLY_CONTEXT</DefineConstants>
              </PropertyGroup>
            </Project>
            """;
        FSharpSemanticOptionsSnapshot result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
            "net8.0", "net8.0", importResolver: path => path == importPath ? props : null,
            directoryBuildPropsPath: importPath == "Directory.Build.props" ? importPath : null,
            directoryPackagesPropsPath: importPath == "Directory.Packages.props" ? importPath : null);
        Assert.Null(result.Error);
        Assert.Equal("Debug|AnyCPU", result.AssemblyName);
        Assert.Contains("--define:MATCHED_EARLY_CONTEXT", result.CommandLineArgs);
        Assert.Contains("fsharp_semantic_default_context_assumed", result.PartialReason);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Fact]
    public void AnalysisContextDoesNotSynthesizeCompilerDefinesOrPlatformTarget()
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("""
            <PropertyGroup><AssemblyName>$(Configuration)|$(Platform)</AssemblyName></PropertyGroup>
            """);
        Assert.Null(result.Error);
        Assert.Equal("Debug|AnyCPU", result.AssemblyName);
        Assert.DoesNotContain("--define:DEBUG", result.CommandLineArgs);
        Assert.DoesNotContain("--define:TRACE", result.CommandLineArgs);
        Assert.DoesNotContain(result.CommandLineArgs, arg => arg.StartsWith("--platform:", StringComparison.Ordinal));
        Assert.Contains("fsharp_semantic_default_context_assumed", result.PartialReason);
        FSharpSemanticOptionsSnapshot unused = EvaluateBoundedProject("");
        Assert.Null(unused.Error);
        Assert.Contains("fsharp_semantic_default_context_assumed", unused.PartialReason);
    }

    [Fact]
    public void CompleteAnalysisContextExpandsTheExactCompoundConditionCompletely()
    {
        var properties = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase)
        {
            ["TargetFramework"] = new("net8.0", true),
            ["Configuration"] = new("Debug", true),
            ["Platform"] = new("AnyCPU", true),
        };
        var expressions = new BoundedMsBuildExpressionEvaluator(properties,
            (_, _) => new(false, ""), (_, _) => new(false, false), CancellationToken.None,
            ProjectFileParser.MaxFSharpSemanticPropertyValueChars,
            ProjectFileParser.MaxFSharpSemanticConditionDepth);
        Assert.True(expressions.TryExpandProperties(
            "'$(TargetFramework)' == 'net8.0' and '$(Configuration)|$(Platform)' == 'Debug|AnyCPU'",
            "Directory.Build.props", null, out string output, out bool complete, out string? error));
        Assert.True(complete);
        Assert.Null(error);
        Assert.Equal("'net8.0' == 'net8.0' and 'Debug|AnyCPU' == 'Debug|AnyCPU'", output);
    }

    [Theory]
    [InlineData("net8.0", "", "Debug|AnyCPU", true)]
    [InlineData("net10.0", "", "Debug|AnyCPU", false)]
    [InlineData("net8.0", "<Configuration>Release</Configuration>", "Release|AnyCPU", false)]
    [InlineData("net8.0", "<Platform>x64</Platform>", "Debug|x64", false)]
    [InlineData("net8.0", "<Configuration></Configuration><Platform></Platform>", "|", false)]
    [InlineData("net8.0", "<Configuration>Custom</Configuration><Platform>x86</Platform>", "Custom|x86", false)]
    [InlineData("net8.0", "<Configuration Condition=\"'$(Configuration)' == ''\">Release</Configuration><Platform Condition=\"'$(Platform)' == ''\">x64</Platform>", "Debug|AnyCPU", true)]
    public void DefaultConfigurationAndPlatformHonorProjectOverrides(
        string targetFramework, string properties, string expectedContext, bool expectedMatch)
    {
        string xml = $$"""
            <Project>
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                {{properties}}
                <AssemblyName>$(Configuration)|$(Platform)</AssemblyName>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0' and '$(Configuration)|$(Platform)' == 'Debug|AnyCPU'">
                <DefineConstants>MATCHED_CONTEXT</DefineConstants>
              </PropertyGroup>
              <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
            </Project>
            """;
        FSharpSemanticOptionsSnapshot result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", xml, "net8.0;net10.0", targetFramework);
        Assert.Null(result.Error);
        Assert.Equal(expectedContext, result.AssemblyName);
        Assert.Contains("fsharp_semantic_default_context_assumed", result.PartialReason);
        Assert.Equal(expectedMatch, result.CommandLineArgs.Contains("--define:MATCHED_CONTEXT"));
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Theory]
    [InlineData("Configuration")]
    [InlineData("Platform")]
    public void DefaultConfigurationAndPlatformDoNotReplaceIncompleteAssignments(string property)
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject($"""
            <PropertyGroup><{property}>$(Unknown)</{property}></PropertyGroup>
            <PropertyGroup Condition="'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'">
              <DefineConstants>WRONG_DEFAULT</DefineConstants>
            </PropertyGroup>
            """);
        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.DoesNotContain("--define:WRONG_DEFAULT", result.CommandLineArgs);
    }

    [Fact]
    public void DefaultConfigurationAndPlatformPreserveImportOverridesAndCaptureExists()
    {
        string root = Directory.CreateTempSubdirectory("codenav-default-context").FullName;
        try
        {
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            string xml = """
                <Project>
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <Platform>x64</Platform>
                    <AssemblyName>$(Configuration)|$(Platform)</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """;
            string props = """
                <Project>
                  <PropertyGroup>
                    <Configuration>Release</Configuration>
                    <Platform>x86</Platform>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0' and '$(Configuration)|$(Platform)' == 'Release|x86'">
                    <DefineConstants Condition="!Exists('Web.config')">IMPORT_CONTEXT</DefineConstants>
                  </PropertyGroup>
                </Project>
                """;
            WriteProject(root, "Core/Core.fsproj", xml);
            WriteProject(root, "Directory.Build.props", props);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var queries = new IndexQueries(db);
            Assert.True(queries.TryGetCapturedMsBuildFilePresence("Core/Web.config", out bool? presence));
            Assert.Equal(false, presence);
            FSharpSemanticOptionsSnapshot result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
                "Core/Core.fsproj", xml, "net8.0", "net8.0",
                importResolver: path => path == "Directory.Build.props" ? props : null,
                directoryBuildPropsPath: "Directory.Build.props",
                existsResolver: path => SemanticService.ResolveIndexedFSharpExists(queries, path));
            Assert.Null(result.Error);
            Assert.Equal("Release|x64", result.AssemblyName);
            Assert.Contains("--define:IMPORT_CONTEXT", result.CommandLineArgs);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("Directory.Build.props", "AmbientConfiguration", false)]
    [InlineData("Directory.Build.props", "AmbientPlatform", false)]
    [InlineData("Directory.Packages.props", "AmbientConfiguration", false)]
    [InlineData("Directory.Packages.props", "AmbientPlatform", false)]
    [InlineData("Directory.Build.props", "AmbientConfiguration", true)]
    [InlineData("Directory.Build.props", "AmbientPlatform", true)]
    [InlineData("Directory.Packages.props", "AmbientConfiguration", true)]
    [InlineData("Directory.Packages.props", "AmbientPlatform", true)]
    public void AnalysisContextDistinguishesAbsentAndIncompletePresenceGuardsInEarlyImports(
        string importPath, string property, bool incomplete)
    {
        string props = $"""
            <Project>
              {(incomplete ? $"<PropertyGroup><{property}>$(Unknown)</{property}></PropertyGroup>" : "")}
              <PropertyGroup Condition="'$({property})' != ''">
                <DefineConstants>WRONG_EARLY_CONTEXT</DefineConstants>
              </PropertyGroup>
            </Project>
            """;
        FSharpSemanticOptionsSnapshot result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
            "net8.0", "net8.0", importResolver: path => path == importPath ? props : null,
            directoryBuildPropsPath: importPath == "Directory.Build.props" ? importPath : null,
            directoryPackagesPropsPath: importPath == "Directory.Packages.props" ? importPath : null);
        Assert.DoesNotContain("--define:WRONG_EARLY_CONTEXT", result.CommandLineArgs);
        if (incomplete)
        {
            Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
            Assert.Empty(result.SourceFiles);
            Assert.DoesNotContain("fsharp_semantic_optional_property_assumed_empty", result.PartialReason ?? "");
        }
        else
        {
            // v0.12.102 selects an explicit absent-as-empty default for this exact guard.
            Assert.Null(result.Error);
            Assert.Equal(["Core/Core.fs"], result.SourceFiles);
            Assert.Contains("fsharp_semantic_optional_property_assumed_empty", result.PartialReason);
        }
    }

    [Theory]
    [InlineData("<Configuration>Release</Configuration>", "Release|AnyCPU", null)]
    [InlineData("<Platform>x86</Platform>", "Debug|x86", null)]
    [InlineData("<Configuration></Configuration><Platform></Platform>", "|", null)]
    [InlineData("<Configuration>$(Unknown)</Configuration>", null, "fsharp_semantic_assembly_name_unavailable")]
    public void DefaultConfigurationAndPlatformHonorEarlyImportAssignments(string properties, string? expected, string? error)
    {
        string props = $"<Project><PropertyGroup>{properties}</PropertyGroup></Project>";
        FSharpSemanticOptionsSnapshot result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>$(Configuration)|$(Platform)</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
            "net8.0", "net8.0", importResolver: path => path == "Directory.Build.props" ? props : null,
            directoryBuildPropsPath: "Directory.Build.props");
        Assert.Equal(error, result.Error);
        if (error is null) Assert.Equal(expected, result.AssemblyName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultConfigurationAndPlatformCaptureConvergesAfterOverride(bool earlyImport)
    {
        string root = Directory.CreateTempSubdirectory("codenav-context-refresh").FullName;
        try
        {
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            string Project(string properties) => $$"""
                <Project>
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework>{{properties}}</PropertyGroup>
                  <PropertyGroup Condition="'$(Configuration)|$(Platform)' == 'Debug|AnyCPU' And Exists('Web.config')" />
                  <PropertyGroup Condition="'$(Configuration)|$(Platform)' == 'Release|x64' And Exists('app.config')" />
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """;
            string owner = earlyImport ? "Directory.Build.props" : "Core/Core.fsproj";
            if (earlyImport)
                WriteProject(root, "Core/Core.fsproj", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>");
            string Input(string properties) => earlyImport
                ? Project(properties).Replace("<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup>", "", StringComparison.Ordinal)
                : Project(properties);
            WriteProject(root, owner, Input(""));
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var store = new IndexStore(db, createNew: false);
            bool? Captured(string path)
            {
                using var queries = new IndexQueries(db);
                return queries.TryGetCapturedMsBuildFilePresence(path, out bool? presence) ? presence : null;
            }
            Assert.Equal(false, Captured("Core/Web.config"));
            Assert.Null(Captured("Core/app.config"));
            WriteProject(root, "Core/Web.config", "<configuration />");
            DeltaRefresher.Refresh(store, root, ["Core/Web.config"], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Equal(true, Captured("Core/Web.config"));
            WriteProject(root, owner, Input("<Configuration>Release</Configuration><Platform>x64</Platform>"));
            DeltaRefresher.Refresh(store, root, [owner], fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            Assert.Null(Captured("Core/Web.config"));
            Assert.Equal(false, Captured("Core/app.config"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("net8.0", null, "true")]
    [InlineData("net10.0", null, "UNCHANGED")]
    [InlineData("net8.0", "false", "false")]
    public void CompoundSelfDefaultHonorsTargetFrameworkAndExistingValue(
        string targetFramework, string? existingValue, string expectedAssemblyName)
    {
        string initialValue = existingValue is null ? "" :
            $"<EnableReplaceBindingRedirects>{existingValue}</EnableReplaceBindingRedirects>";
        string xml = $$"""
            <Project>
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                <AssemblyName>UNCHANGED</AssemblyName>
                {{initialValue}}
                <EnableReplaceBindingRedirects Condition="'$(EnableReplaceBindingRedirects)' == '' and '$(TargetFramework)' == 'net8.0'">true</EnableReplaceBindingRedirects>
                <AssemblyName Condition="'$(TargetFramework)' == 'net8.0'">$(EnableReplaceBindingRedirects)</AssemblyName>
              </PropertyGroup>
              <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
            </Project>
            """;
        FSharpSemanticOptionsSnapshot result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
            "Core/Core.fsproj", xml, "net8.0;net10.0", targetFramework);

        Assert.Null(result.Error);
        Assert.Equal(expectedAssemblyName, result.AssemblyName);
        Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Theory]
    [InlineData("('$(AssemblyName)' == '') and ('$(TargetFramework)' == 'net10.0')", "SELECTED")]
    [InlineData("'$(TargetFramework)' == 'net10.0' AND '' == '$(AssemblyName)'", "SELECTED")]
    [InlineData("&quot;$(AssemblyName)&quot; == &quot;&quot; and true", "SELECTED")]
    [InlineData("('$(AssemblyName)' == '' and false) Or true", "SELECTED")]
    [InlineData("false Or ('$(AssemblyName)' == '' and true)", "SELECTED")]
    [InlineData("!('$(AssemblyName)' == '') and true", "Core")]
    [InlineData("'$(AssemblyName)' == '' and '$(AssemblyName)' == ''", "SELECTED")]
    [InlineData("'$(AssemblyName)' == '' and false", "Core")]
    public void CompoundSelfDefaultsPreserveBooleanStructure(string condition, string expected)
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject($"""
            <PropertyGroup>
              <AssemblyName Condition="{condition}">SELECTED</AssemblyName>
            </PropertyGroup>
            """);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.AssemblyName);
    }

    [Theory]
    [InlineData("'$(AssemblyName)' == '' and '$(Unknown)' == ''")]
    [InlineData("'$(AssemblyName)' == '' Or '$(Unknown)' == ''")]
    [InlineData("'$(AssemblyName)' == '' and '$(AssemblyName)' != 'other'")]
    [InlineData("'$(AssemblyName)' == '' Or $(AssemblyName.StartsWith('x'))")]
    [InlineData("'$(AssemblyName) ' == '' and true")]
    [InlineData("'$(AssemblyName)' == ' ' and true")]
    public void CompoundSelfDefaultsDoNotGrantUnrelatedOrNoncanonicalEmptyValues(string condition)
    {
        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject($"""
            <PropertyGroup>
              <AssemblyName Condition="{condition}">WRONG</AssemblyName>
            </PropertyGroup>
            """);
        Assert.Equal("fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.Empty(result.SourceFiles);
    }

    [Fact]
    public void CompoundSelfDefaultsKeepIncompleteValuesAndGroupConditionsUnresolved()
    {
        FSharpSemanticOptionsSnapshot incomplete = EvaluateBoundedProject("""
            <PropertyGroup>
              <AssemblyName>$(Unknown)</AssemblyName>
              <AssemblyName Condition="'$(AssemblyName)' == '' and true">WRONG</AssemblyName>
            </PropertyGroup>
            """);
        Assert.Equal("fsharp_semantic_condition_property_unresolved", incomplete.Error);
        FSharpSemanticOptionsSnapshot group = EvaluateBoundedProject("""
            <PropertyGroup Condition="'$(AssemblyName)' == '' and true">
              <AssemblyName>WRONG</AssemblyName>
            </PropertyGroup>
            """);
        Assert.Equal("fsharp_semantic_condition_property_unresolved", group.Error);
    }

    [Fact]
    public void CompoundSelfDefaultsRespectExistingDepthAndSyntaxValidation()
    {
        string Nested(int depth) => new string('(', depth) + "'$(AssemblyName)' == ''" +
                                    new string(')', depth);
        FSharpSemanticOptionsSnapshot Evaluate(string condition) => EvaluateBoundedProject($"""
            <PropertyGroup><AssemblyName Condition="{condition}">SELECTED</AssemblyName></PropertyGroup>
            """);
        FSharpSemanticOptionsSnapshot atLimit = Evaluate(Nested(ProjectFileParser.MaxFSharpSemanticConditionDepth));
        Assert.Null(atLimit.Error);
        Assert.Equal("SELECTED", atLimit.AssemblyName);
        FSharpSemanticOptionsSnapshot overLimit = Evaluate(Nested(ProjectFileParser.MaxFSharpSemanticConditionDepth + 1));
        Assert.NotNull(overLimit.Error);
        Assert.Empty(overLimit.SourceFiles);
        FSharpSemanticOptionsSnapshot malformed = Evaluate("'$(AssemblyName)' == '' and (true");
        Assert.Equal("fsharp_semantic_condition_unsupported", malformed.Error);
        Assert.Empty(malformed.SourceFiles);
    }

    [Fact]
    public void CompoundSelfDefaultInImportedPropsEnablesPersistedExistsCapture()
    {
        string root = Directory.CreateTempSubdirectory("codenav-compound-default").FullName;
        try
        {
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Core/Core.fsproj", """
                <Project>
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Core.fs" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "Directory.Build.props", """
                <Project>
                  <PropertyGroup>
                    <EnableReplaceBindingRedirects Condition="'$(EnableReplaceBindingRedirects)' == '' and '$(TargetFramework)' == 'net8.0'">true</EnableReplaceBindingRedirects>
                  </PropertyGroup>
                  <PropertyGroup Condition="'$(EnableReplaceBindingRedirects)' == 'true' And Exists('Web.config')">
                    <DefineConstants>HAS_WEB_CONFIG</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db, fsharpProjectModel: CodeNav.Core.Semantic.ProjectModelMode.Evaluated);
            using var queries = new IndexQueries(db);
            Assert.True(queries.TryGetCapturedMsBuildFilePresence("Core/Web.config", out bool? presence));
            Assert.Equal(false, presence);
        }
        finally
        {
            Cleanup(root);
        }
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
            int semanticSnapshotCount = 0;
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
                Interlocked.Increment(ref semanticSnapshotCount);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 27, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("found", out JsonElement found) &&
                        found.GetBoolean(), raw);
            Assert.Equal("target",
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Contains(response.GetProperty("declarations").EnumerateArray(), site =>
                site.GetProperty("path").GetString() == "Core/Core.fs");
            // SymbolAt already carries the FCS declaration evidence. Re-querying through
            // Definition doubled this regression's exposure to the semantic deadline under
            // full-suite CPU contention without proving another project-evaluation property.
            Assert.Equal(1, Volatile.Read(ref semanticSnapshotCount));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DirectoryBuildMakeRelativeRootEnablesProjectClosureAndImplementations()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-makerelative").FullName;
        try
        {
            WriteProject(root, "Directory.Build.props", """
                <Project>
                  <PropertyGroup>
                    <RootDir>$([MSBuild]::MakeRelative('$(MSBuildProjectDirectory)', '$(MSBuildThisFileDirectory)'))</RootDir>
                  </PropertyGroup>
                </Project>
                """);
            WriteProject(root, "Build/Consumer.props", """
                <Project>
                  <PropertyGroup><DefineConstants>FROM_ROOT_IMPORT</DefineConstants></PropertyGroup>
                </Project>
                """);
            WriteProject(root, "Contracts/Contracts.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Contracts.fs" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "Contracts/Contracts.fs", """
                namespace Contracts
                type IWorker =
                    abstract Run: unit -> int
                """);
            WriteProject(root, "Products/Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="$(RootDir)/Build/Consumer.props" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="$(RootDir)/Contracts/Contracts.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Products/Consumer/Consumer.fs", """
                namespace Consumer
                open Contracts
                #if FROM_ROOT_IMPORT
                type Worker() =
                    interface IWorker with
                        member _.Run() = 42
                #endif
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.Implementations(
                path: "Contracts/Contracts.fs", line: 2, column: 7,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean(), raw);
            JsonElement implementation = Assert.Single(
                response.GetProperty("implementations").EnumerateArray());
            Assert.Equal("Worker", implementation.GetProperty("symbol")
                .GetProperty("name").GetString());
            Assert.Equal("Products/Consumer/Consumer.fs", implementation.GetProperty("symbol")
                .GetProperty("path").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void MakeRelativeUsesTheCurrentImportedFileDirectoryAndKeepsItsSeparator()
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Directory.Build.props"] = """
                <Project><Import Project="Build/Root.props" /></Project>
                """,
            ["Build/Root.props"] = """
                <Project><PropertyGroup>
                  <RelativeToImport>$([MSBuild]::MakeRelative('$(MSBuildProjectDirectory)', '$(MSBuildThisFileDirectory)'))</RelativeToImport>
                </PropertyGroup></Project>
                """,
            ["Build/References.props"] = """
                <Project><PropertyGroup><DefineConstants>FROM_NESTED_IMPORT</DefineConstants></PropertyGroup></Project>
                """,
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject(
            "<Import Project=\"$(RelativeToImport)References.props\" />",
            imports, directoryBuildPropsPath: "Directory.Build.props");

        Assert.Null(result.Error);
        Assert.Contains("--define:FROM_NESTED_IMPORT", result.CommandLineArgs);
    }

    [Theory]
    [InlineData("$([MSBuild]::MakeRelative('$(MSBuildProjectDirectory)', '$(MSBuildThisFileDirectory)'))")]
    [InlineData("$([MSBuild]::MakeRelative($(MSBuildProjectDirectory), $(MSBuildThisFileDirectory)))")]
    [InlineData("$([MSBuild]::MakeRelative(\"$(MSBuildProjectDirectory)\", \"$(MSBuildThisFileDirectory)\"))")]
    public void MakeRelativePreservesScalarDotForIdenticalDirectories(string expression)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Core/Directory.Build.props"] =
                $"<Project><PropertyGroup><RootDir>{expression}</RootDir>" +
                "<AssemblyName>Before$(RootDir)After</AssemblyName>" +
                "<DefineConstants Condition=\"'$(RootDir)' == '.'\">SAME_DIRECTORY_DOT</DefineConstants>" +
                "</PropertyGroup></Project>",
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("", imports,
            directoryBuildPropsPath: "Core/Directory.Build.props");

        Assert.Null(result.Error);
        Assert.Equal("Before.After", result.AssemblyName);
        Assert.Contains("--define:SAME_DIRECTORY_DOT", result.CommandLineArgs);
    }

    [Fact]
    public void MakeRelativeUsesHostPathIdentityAndNativeSeparator()
    {
        char separator = Path.DirectorySeparatorChar;
        string expected = OperatingSystem.IsWindows() ? "." : $"..{separator}core{separator}";
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["core/Directory.Build.props"] =
                "<Project><PropertyGroup>" +
                "<RootDir>$([MSBuild]::MakeRelative('$(MSBuildProjectDirectory)', '$(MSBuildThisFileDirectory)'))</RootDir>" +
                $"<DefineConstants Condition=\"'$(RootDir)' == '{expected}'\">MATCHED_HOST_VALUE</DefineConstants>" +
                "</PropertyGroup></Project>",
        };

        FSharpSemanticOptionsSnapshot result = EvaluateBoundedProject("", imports,
            directoryBuildPropsPath: "core/Directory.Build.props");

        Assert.Null(result.Error);
        Assert.Contains("--define:MATCHED_HOST_VALUE", result.CommandLineArgs);
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

    [Fact]
    public void HostAwareIndexedProjectLookupUsesExactCaseAndRejectsAmbiguousAliases()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-indexed-project-case").FullName;
        try
        {
            string singleDbPath = Path.Combine(root, "single.sqlite");
            using (var store = new IndexStore(singleDbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                store.InsertProject(transaction, new ParsedProject(
                    "Deps/Dependency.fsproj", "Dependency", "sdk", null,
                    "net10.0", false, [], [], null, [], "parsed", Language: "fs"));
                transaction.Commit();
            }
            using (var single = new IndexQueries(singleDbPath))
            {
                Assert.Equal("Deps/Dependency.fsproj",
                    single.ProjectByPathForHost("Deps/Dependency.fsproj")?.Path);
                if (OperatingSystem.IsWindows())
                    Assert.Equal("Deps/Dependency.fsproj",
                        single.ProjectByPathForHost("DEPS/DEPENDENCY.FSPROJ")?.Path);
                else
                    Assert.Null(single.ProjectByPathForHost("DEPS/DEPENDENCY.FSPROJ"));
            }

            string ambiguousDbPath = Path.Combine(root, "ambiguous.sqlite");
            using (var store = new IndexStore(ambiguousDbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                foreach (string path in new[]
                         { "Deps/Dependency.fsproj", "deps/dependency.fsproj" })
                    store.InsertProject(transaction, new ParsedProject(
                        path, Path.GetFileNameWithoutExtension(path), "sdk", null,
                        "net10.0", false, [], [], null, [], "parsed", Language: "fs"));
                transaction.Commit();
            }
            using var ambiguous = new IndexQueries(ambiguousDbPath);
            Assert.Equal("Deps/Dependency.fsproj",
                ambiguous.ProjectByPathForHost("Deps/Dependency.fsproj")?.Path);
            Assert.Equal("deps/dependency.fsproj",
                ambiguous.ProjectByPathForHost("deps/dependency.fsproj")?.Path);
            Assert.Null(ambiguous.ProjectByPathForHost("DEPS/DEPENDENCY.FSPROJ"));
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
    public void DirectoryPackagesAuthorityUsesNearestIndexedAncestorAndRejectsCaseAmbiguity()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-directory-packages-authority").FullName;
        string dbPath = Path.Combine(root, "index.sqlite");
        try
        {
            using (var store = new IndexStore(dbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                foreach (string path in new[]
                         {
                             "Directory.Packages.props",
                             "Core/Directory.Packages.props",
                             "Core/Sub/DIRECTORY.PACKAGES.PROPS",
                             "Core/Sub/directory.packages.props",
                         })
                    store.InsertFile(transaction, path, 0, 0, 0,
                        "config", 0, isGenerated: false, hasTestAttrs: false);
                transaction.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            DirectoryPackagesAuthorityPath unix =
                queries.ApplicableDirectoryPackagesAuthority(
                    "Core/Sub/Project.fsproj", useWindowsPathPolicy: false);
            Assert.Equal("Core/Directory.Packages.props", unix.Path);
            Assert.False(unix.PathAmbiguous);

            DirectoryPackagesAuthorityPath windows =
                queries.ApplicableDirectoryPackagesAuthority(
                    "Core/Sub/Project.fsproj", useWindowsPathPolicy: true);
            Assert.Null(windows.Path);
            Assert.True(windows.PathAmbiguous);
            Assert.True(windows.HasPotentialAuthority);
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

    [Fact]
    public async Task IndexedWebConfigAppearanceChangesTheFcsView()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-exists-refresh").FullName;
        try
        {
            WriteProject(root, "Core/Core.fs", """
                module Core
                let withoutConfig = 1
                let withConfig = 2
                """);
            WriteProject(root, "Core/Use.fs", """
                module Use
                let result =
                #if HAS_WEB_CONFIG
                    Core.withConfig
                #else
                    Core.withoutConfig
                #endif
                """);
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <DefineConstants Condition="Exists('web.config')">HAS_WEB_CONFIG</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <Compile Include="Use.fs" />
                  </ItemGroup>
                </Project>
                """);

            using var fixture = Fixture.Create(root);
            string beforeRaw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 6, 15, timeoutMs: 60_000));
            JsonElement before = Parse(beforeRaw);
            Assert.True(before.TryGetProperty("symbol", out JsonElement beforeSymbol), beforeRaw);
            Assert.Equal("withoutConfig", beforeSymbol.GetProperty("name").GetString());

            WriteProject(root, "Core/web.config", "<configuration />");
            Assert.True(fixture.Manager.RequestRefreshForTest(
                ["Core/web.config"], out Task refreshed));
            await refreshed.WaitAsync(TimeSpan.FromSeconds(20));

            string afterRaw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 15, timeoutMs: 60_000));
            JsonElement after = Parse(afterRaw);
            Assert.True(after.TryGetProperty("symbol", out JsonElement afterSymbol), afterRaw);
            Assert.Equal("withConfig",
                afterSymbol.GetProperty("name").GetString());

            File.Delete(Path.Combine(root, "Core", "web.config"));
            Assert.True(fixture.Manager.RequestRefreshForTest(
                ["Core/web.config"], out Task removed));
            await removed.WaitAsync(TimeSpan.FromSeconds(20));
            JsonElement afterRemoval = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 6, 15, timeoutMs: 60_000)));
            Assert.True(afterRemoval.TryGetProperty("symbol", out JsonElement removedSymbol),
                afterRemoval.GetRawText());
            Assert.Equal("withoutConfig", removedSymbol.GetProperty("name").GetString());
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
        string? directoryPackagesPropsPath = null,
        string? directoryBuildPropsPath = null,
        string? directoryBuildTargetsPath = null,
        bool hasAmbiguousDirectoryBuildAuthority = false,
        bool hasAmbiguousDirectoryPackagesAuthority = false,
        Func<string, bool?>? existsResolver = null)
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
            directoryPackagesPropsPath: directoryPackagesPropsPath,
            directoryBuildPropsPath: directoryBuildPropsPath,
            directoryBuildTargetsPath: directoryBuildTargetsPath,
            cancellationToken: cancellationToken,
            hasAmbiguousDirectoryBuildAuthority: hasAmbiguousDirectoryBuildAuthority,
            hasAmbiguousDirectoryPackagesAuthority:
                hasAmbiguousDirectoryPackagesAuthority,
            existsResolver: existsResolver);
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
