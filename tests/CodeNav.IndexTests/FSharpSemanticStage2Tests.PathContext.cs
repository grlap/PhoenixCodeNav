using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using Microsoft.Data.Sqlite;
using System.Text;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Theory]
    [InlineData("Import", false)]
    [InlineData("Compile", false)]
    [InlineData("ProjectReference", false)]
    [InlineData("HintPath", false)]
    [InlineData("Exists", false)]
    [InlineData("Import", true)]
    [InlineData("Compile", true)]
    [InlineData("ProjectReference", true)]
    [InlineData("HintPath", true)]
    [InlineData("Exists", true)]
    public void SharedPathMixedSeparatorsPreserveNativeComponents(string consumer, bool captured)
    {
        // On Unix these backslashes are real characters in captured filesystem names, whereas
        // the backslashes in XML below are MSBuild separators. Both kinds coexist in one value.
        string directory = OperatingSystem.IsWindows() ? "Core" : "Core\\Native";
        string root = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "native root" : "native\\root");
        const string authored = "$(MSBuildProjectDirectory)\\Sub\\Input";
        string value = captured ? "$(Alias)" : authored;
        string setup = captured ? "<PropertyGroup><Captured>" + authored + "</Captured><Alias>$(Captured)</Alias></PropertyGroup>" : "";
        string body = consumer switch
        {
            "Import" => $"<Import Project=\"{value}.props\" />",
            "Compile" => $"<ItemGroup><Compile Include=\"{value}.fs\" /></ItemGroup>",
            "ProjectReference" => $"<ItemGroup><ProjectReference Include=\"{value}.fsproj\" /></ItemGroup>",
            "HintPath" => $"<ItemGroup><Reference Include=\"Input\"><HintPath>{value}.dll</HintPath></Reference></ItemGroup>",
            _ => $"<PropertyGroup Condition=\"Exists('{value}.config')\"><DefineConstants>MIXED_PATH</DefineConstants></PropertyGroup>",
        };
        var imports = new List<string>();
        var probes = new List<string>();
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(directory + "/Core.fsproj",
            SdkContextProject(setup + body), "net8.0", "net8.0", workspaceRoot: root,
            importResolver: path =>
            {
                imports.Add(path);
                return path == directory + "/Sub/Input.props" ? "<Project><PropertyGroup><DefineConstants>MIXED_PATH</DefineConstants></PropertyGroup></Project>" : null;
            },
            existsResolver: path => { probes.Add(path); return path == directory + "/Sub/Input.config"; });
        Assert.Null(result.Error);
        if (consumer == "Import") Assert.Equal([directory + "/Sub/Input.props"], imports);
        if (consumer == "Compile") Assert.Contains(directory + "/Sub/Input.fs", result.SourceFiles);
        if (consumer == "ProjectReference") Assert.Equal(directory + "/Sub/Input.fsproj", Assert.Single(result.ProjectReferences!).ProjectPath);
        if (consumer == "HintPath") Assert.Equal([directory + "/Sub/Input.dll"], result.HintPathReferences);
        if (consumer == "Exists") Assert.Equal([directory + "/Sub/Input.config"], probes);
        if (consumer is "Import" or "Exists") Assert.Contains("--define:MIXED_PATH", result.CommandLineArgs);
    }

    [Fact]
    public void SharedPathMixedCompositionRetainsScalarSpellingAndItemListCapture()
    {
        string root = Path.Combine(Path.GetTempPath(), "codenav composed paths");
        string fileName = OperatingSystem.IsWindows() ? "Core.Net.fsproj" : "Core\\Native.Net.fsproj";
        string project = "Core/" + fileName;
        string expectedScalar = Path.Combine(root, "Core") + "\\" + fileName;
        string escaped = System.Security.SecurityElement.Escape(expectedScalar)!;
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(project, SdkContextProject($$"""
            <PropertyGroup>
              <CapturedName>$(MSBuildProjectFile)</CapturedName>
              <Composed>$(MSBuildProjectDirectory)\$(CapturedName)</Composed>
              <Alias>$(Composed)</Alias>
              <AssemblyName>$(Alias)</AssemblyName>
            </PropertyGroup>
            <PropertyGroup Condition="'$(Alias)' == '{{escaped}}'"><DefineConstants>SCALAR_UNCHANGED</DefineConstants></PropertyGroup>
            <ItemGroup><CapturedProjects Include="$(Alias)" /><MoreProjects Include="@(CapturedProjects)" /></ItemGroup>
            <ItemGroup><ProjectReference Include="@(MoreProjects)" /></ItemGroup>
            """), "net8.0", "net8.0", workspaceRoot: root);
        Assert.Null(result.Error);
        Assert.Equal(expectedScalar, result.AssemblyName);
        Assert.Contains("--define:SCALAR_UNCHANGED", result.CommandLineArgs);
        Assert.Equal(project, Assert.Single(result.ProjectReferences!).ProjectPath);
    }

    [Fact]
    public void SharedPathMixedThisFileCaptureRestoresAcrossImportsAndConfiguredTargets()
    {
        string root = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "native" : "native\\root");
        string propsFile = OperatingSystem.IsWindows() ? "Outer.props" : "Outer\\Native.props";
        var imports = new Dictionary<string, string>
        {
            ["Build/" + propsFile] = """
                <Project><PropertyGroup><Captured>$(MSBuildThisFileDirectory)\Sub</Captured></PropertyGroup>
                <Import Project="$(Captured)\Inner.props" />
                <PropertyGroup><CapturedAfter>$(MSBuildThisFileDirectory)\Sub</CapturedAfter></PropertyGroup></Project>
                """,
            ["Build/Sub/Inner.props"] = "<Project><PropertyGroup><InnerPath>$(MSBuildThisFileDirectory)\\Inner.fs</InnerPath></PropertyGroup></Project>",
        };
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", SdkContextProject("""
            <PropertyGroup><FSharpTargetsPath>$(EntryPath)</FSharpTargetsPath></PropertyGroup>
            <Import Project="$(FSharpTargetsPath)" />
            <ItemGroup><Compile Include="$(InnerPath);$(CapturedAfter)\After.fs" /></ItemGroup>
            """), "net8.0", "net8.0", workspaceRoot: root,
            directoryBuildPropsPath: "Build/" + propsFile,
            importResolver: path => imports.GetValueOrDefault(path));
        // An unavailable user property must stay incomplete, regardless of path projection.
        Assert.Equal("fsharp_semantic_import_unsupported", result.Error);

        result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", SdkContextProject("""
            <PropertyGroup><FSharpTargetsPath>$(Captured)\Inner.props</FSharpTargetsPath></PropertyGroup>
            <Import Project="$(FSharpTargetsPath)" />
            <ItemGroup><Compile Include="$(InnerPath);$(CapturedAfter)\After.fs" /></ItemGroup>
            """), "net8.0", "net8.0", workspaceRoot: root,
            directoryBuildPropsPath: "Build/" + propsFile,
            importResolver: path => imports.GetValueOrDefault(path));
        Assert.Null(result.Error);
        Assert.Equal(["Build/Sub/Inner.fs", "Build/Sub/After.fs", "Core/Core.fs"], result.SourceFiles);
    }

    [Fact]
    public void SharedPathMixedSeparatorsCaptureExistsAcrossColdDeltaAndPinnedSnapshots()
    {
        string sandbox = Directory.CreateTempSubdirectory("codenav-mixed-capture").FullName;
        string root = Path.Combine(sandbox, OperatingSystem.IsWindows() ? "native" : "native\\root");
        try
        {
            const string body = """
                <PropertyGroup><Probe>$(MSBuildProjectDirectory)\web.config</Probe><Alias>$(Probe)</Alias></PropertyGroup>
                <PropertyGroup Condition="Exists('$(Alias)')"><DefineConstants>MIXED_PATH</DefineConstants></PropertyGroup>
                """;
            WriteProject(root, "Core/Core.fsproj", SdkContextProject(body));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using var store = new IndexStore(db, createNew: false);
            using var pinned = new IndexQueries(db, pinReadSnapshot: true);
            Assert.True(pinned.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? oldPresence));
            Assert.Equal(false, oldPresence);
            WriteProject(root, "Core/web.config", "present");
            DeltaRefresher.Refresh(store, root, ["Core/web.config"]);
            using (var fresh = new IndexQueries(db))
            {
                Assert.True(fresh.TryGetCapturedMsBuildFilePresence("Core/web.config", out bool? presence));
                Assert.Equal(true, presence);
            }
            Assert.True(pinned.TryGetCapturedMsBuildFilePresence("Core/web.config", out oldPresence));
            Assert.Equal(false, oldPresence);
            WriteProject(root, "Core/Core.fsproj", SdkContextProject(body.Replace("Exists('$(Alias)')", "false", StringComparison.Ordinal)));
            DeltaRefresher.Refresh(store, root, ["Core/Core.fsproj"]);
            using var retracted = new IndexQueries(db);
            Assert.False(retracted.TryGetCapturedMsBuildFilePresence("Core/web.config", out _));
        }
        finally
        {
            IndexQueries.ClearPoolsFor(IndexBuilder.DefaultDbPath(root));
            Directory.Delete(sandbox, recursive: true);
        }
    }

    public static TheoryData<string, string> PathPropertyCases => new()
    {
        { "MSBuildProjectDirectory", "project-directory" },
        { "MSBuildProjectDirectoryNoRoot", "project-no-root" },
        { "MSBuildProjectFullPath", "project-full" },
        { "MSBuildProjectFile", "Core.Net.fsproj" },
        { "MSBuildProjectName", "Core.Net" },
        { "MSBuildProjectExtension", ".fsproj" },
        { "MSBuildThisFileDirectory", "document-directory" },
        { "MSBuildThisFileDirectoryNoRoot", "document-no-root" },
        { "MSBuildThisFileFullPath", "document-full" },
        { "MSBuildThisFile", "Shared.Build.props" },
        { "MSBuildThisFileName", "Shared.Build" },
        { "MSBuildThisFileExtension", ".props" },
    };

    [Theory]
    [MemberData(nameof(PathPropertyCases))]
    public void SharedPathPropertiesUseRootProjectAndCurrentDocument(string name, string expectedKind)
    {
        string root = Path.Combine(Path.GetTempPath(), "codenav logical root");
        string projectDirectory = Path.Combine(root, "Core Space");
        string documentDirectory = Path.Combine(root, "Build Space") + Path.DirectorySeparatorChar;
        string expected = expectedKind switch
        {
            "project-directory" => projectDirectory,
            "project-no-root" => projectDirectory[Path.GetPathRoot(projectDirectory)!.Length..],
            "project-full" => Path.Combine(projectDirectory, "Core.Net.fsproj"),
            "document-directory" => documentDirectory,
            "document-no-root" => documentDirectory[Path.GetPathRoot(documentDirectory)!.Length..],
            "document-full" => Path.Combine(documentDirectory, "Shared.Build.props"),
            _ => expectedKind,
        };
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core Space/Core.Net.fsproj",
            SdkContextProject("<Import Project=\"../Build Space/Shared.Build.props\" />", sdk: null),
            "net8.0", "net8.0", importResolver: path => path == "Build Space/Shared.Build.props"
                ? $"<Project><PropertyGroup><AssemblyName>$({name})</AssemblyName></PropertyGroup></Project>" : null,
            workspaceRoot: root);
        Assert.Null(result.Error);
        Assert.Equal(expected, result.AssemblyName);
        Assert.Equal(["Core Space/Core.fs"], result.SourceFiles);
    }

    [Fact]
    public void SharedPathPropertiesCaptureAtAssignmentAndRestoreAcrossNestedImports()
    {
        string root = Path.Combine(Path.GetTempPath(), "codenav nested paths");
        var imports = new Dictionary<string, string>
        {
            ["Build/Outer.props"] = """
                <Project>
                  <PropertyGroup><OuterBefore>$(MSBuildThisFileDirectory)</OuterBefore></PropertyGroup>
                  <Import Project="Deep/Inner.props" />
                  <PropertyGroup><OuterAfter>$(MSBuildThisFileDirectory)</OuterAfter></PropertyGroup>
                </Project>
                """,
            ["Build/Deep/Inner.props"] = """
                <Project><PropertyGroup><InnerCaptured>$(MSBuildThisFileDirectory)</InnerCaptured></PropertyGroup></Project>
                """,
        };
        string body = """
            <Import Project="../Build/Outer.props" />
            <PropertyGroup><AssemblyName>$(OuterBefore)|$(InnerCaptured)|$(OuterAfter)|$(MSBuildThisFileDirectory)</AssemblyName></PropertyGroup>
            """;
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", SdkContextProject(body),
            "net8.0", "net8.0", importResolver: path => imports.GetValueOrDefault(path), workspaceRoot: root);
        Assert.Null(result.Error);
        string DirectoryValue(string relative) => Path.Combine(root, relative) + Path.DirectorySeparatorChar;
        Assert.Equal(string.Join('|', DirectoryValue("Build"), DirectoryValue(Path.Combine("Build", "Deep")),
            DirectoryValue("Build"), DirectoryValue("Core")), result.AssemblyName);
    }

    [Theory]
    [InlineData("MSBuildThisFileDirectory", true)]
    [InlineData("MSBuildProjectDirectory", true)]
    [InlineData("MSBuildThisFileDirectory", false)]
    [InlineData("MSBuildProjectDirectory", false)]
    [InlineData("MSBuildToolsPath", true)]
    [InlineData("MSBuildExtensionsPath", true)]
    [InlineData("MSBuildRuntimeType", true)]
    [InlineData("OS", true)]
    public void SharedPathPropertiesDoNotTurnUnavailableBuiltinsIntoOptionalEmpty(string name, bool hasRoot)
    {
        string body = $"<PropertyGroup Condition=\"'$({name})' != ''\"><DefineConstants>TOOLING</DefineConstants></PropertyGroup>";
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", SdkContextProject(body),
            "net8.0", "net8.0", workspaceRoot: hasRoot ? Path.Combine(Path.GetTempPath(), "codenav builtin paths") : null);
        bool supported = hasRoot && (name == "MSBuildThisFileDirectory" || name == "MSBuildProjectDirectory");
        Assert.Equal(supported ? null : "fsharp_semantic_condition_property_unresolved", result.Error);
        Assert.DoesNotContain("fsharp_semantic_optional_property_assumed_empty", result.PartialReason ?? "");
        if (supported) Assert.Contains("--define:TOOLING", result.CommandLineArgs);
    }

    [Theory]
    [MemberData(nameof(ReservedPathPropertyCases))]
    public void SharedPathPropertiesRejectReservedAssignments(string name)
    {
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            SdkContextProject($"<PropertyGroup><{name}>wrong</{name}></PropertyGroup>"), "net8.0", "net8.0",
            workspaceRoot: Path.GetTempPath());
        Assert.Equal("fsharp_semantic_property_unsupported", result.Error);
    }

    public static IEnumerable<object[]> ReservedPathPropertyCases => PathPropertyCases.Select(row => new object[] { row[0] });

    [Theory]
    [MemberData(nameof(ReservedPathPropertyCases))]
    public void SharedPathPropertiesCSharpReservedAssignmentsStayRefused(string name)
    {
        foreach (bool centralAssignment in new[] { false, true })
        {
            string assignment = $"<PropertyGroup><{name}>wrong</{name}></PropertyGroup>";
            byte[] project = Encoding.UTF8.GetBytes("<Project>" + (centralAssignment ? "" : assignment) +
                "<ItemGroup><PackageReference Include=\"Example\" /></ItemGroup></Project>");
            string central = "<Project>" + (centralAssignment ? assignment : "") +
                "<PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>" +
                "<ItemGroup><PackageVersion Include=\"Example\" Version=\"1.2.3\" /></ItemGroup></Project>";
            var reference = Assert.Single(ProjectFileParser.EvaluateCSharpPackageReferencesSnapshot(project,
                ProjectFileParser.ParseSnapshot("Core/Core.csproj", project).PackageRefs, central, false,
                projectPath: "Core/Core.csproj", workspaceRoot: Path.GetTempPath(), directoryPackagesPath: "Directory.Packages.props"));
            Assert.Equal("", reference.Version);
            Assert.False(reference.CentrallyManaged);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative-root")]
    public void SharedPathPropertiesMissingRootKeepsOnlyKnownFilenameComponents(string? root)
    {
        var context = new BoundedMsBuildProjectContext("Root.Net.fsproj", root);
        Assert.Equal("Root.Net.fsproj", context.Resolve("MSBuildProjectFile", null)?.Value);
        Assert.Equal("Root.Net", context.Resolve("MSBuildProjectName", null)?.Value);
        Assert.Equal(".props", context.Resolve("MSBuildThisFileExtension", "Shared.props")?.Value);
        Assert.Null(context.Resolve("MSBuildProjectDirectory", "Shared.props"));
        Assert.Null(context.Resolve("MSBuildThisFileFullPath", "Shared.props"));
        Assert.Null(context.Resolve("MSBuildThisFileName", null));
        Assert.False(context.TryMakeWorkspaceRelative(Path.Combine(Path.GetTempPath(), "Shared.props"), out _));
    }

    [Fact]
    public void SharedPathPropertiesStringFunctionsResolveTheLexicalDocument()
    {
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            SdkContextProject("<Import Project=\"../Build/Shared.props\" />"), "net8.0", "net8.0",
            importResolver: _ => """
                <Project><PropertyGroup Condition="$(MSBuildThisFileName.StartsWith('Shared')) And '$(msbuildprojectname)' == 'Core'">
                <DefineConstants>DOCUMENT_SCALAR</DefineConstants></PropertyGroup></Project>
                """, workspaceRoot: Path.GetTempPath());
        Assert.Null(result.Error);
        Assert.Contains("--define:DOCUMENT_SCALAR", result.CommandLineArgs);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    public void SharedPathPropertiesCaptureEarlyAuthorityBeforeReturningToProject(string earlyPath)
    {
        string root = Path.Combine(Path.GetTempPath(), "codenav early paths");
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            SdkContextProject("<PropertyGroup><AssemblyName>$(CapturedDocument)</AssemblyName></PropertyGroup>"),
            "net8.0", "net8.0", workspaceRoot: root,
            directoryBuildPropsPath: earlyPath == "Directory.Build.props" ? earlyPath : null,
            directoryPackagesPropsPath: earlyPath == "Directory.Packages.props" ? earlyPath : null,
            importResolver: path => path == earlyPath ? "<Project><PropertyGroup><CapturedDocument>$(MSBuildThisFileFullPath)</CapturedDocument></PropertyGroup></Project>" : null);
        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(root, earlyPath), result.AssemblyName);
    }

    [Fact]
    public void SharedPathPropertiesDistinguishNoRootDirectoryConventionsAtFilesystemRoot()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        var context = new BoundedMsBuildProjectContext("Core.fsproj", root);
        Assert.Equal(root, context.Resolve("MSBuildProjectDirectory", "Shared.props")?.Value);
        Assert.Equal(root, context.Resolve("MSBuildThisFileDirectory", "Shared.props")?.Value);
        Assert.Equal("", context.Resolve("MSBuildProjectDirectoryNoRoot", "Shared.props")?.Value);
        Assert.Equal(Path.DirectorySeparatorChar.ToString(), context.Resolve("MSBuildThisFileDirectoryNoRoot", "Shared.props")?.Value);
        Assert.Equal(Path.Combine(root, "Core.fsproj"), context.Resolve("MSBuildProjectFullPath", "Shared.props")?.Value);
        Assert.Equal(Path.Combine(root, "Shared.props"), context.Resolve("MSBuildThisFileFullPath", "Shared.props")?.Value);
    }

    [Theory]
    [InlineData("MSBuildProjectExtension", null)]
    [InlineData("MSBuildThisFileExtension", "fsharp_semantic_evaluation_order_unsupported")]
    public void SharedPathPropertiesKeepDocumentValuesOutOfGlobalInvariantProof(string name, string? error)
    {
        string imported = $"<Project><ItemGroup Condition=\"'$({name})' == '.csproj'\"><Compile Include=\"Wrong.fs\" /></ItemGroup></Project>";
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            SdkContextProject("<Import Project=\"../Build/Shared.props\" /><PropertyGroup><AssemblyName>Core</AssemblyName></PropertyGroup>"),
            "net8.0", "net8.0", importResolver: _ => imported, workspaceRoot: Path.GetTempPath());
        Assert.Equal(error, result.Error);
        if (error is null) Assert.Equal(["Core/Core.fs"], result.SourceFiles);
    }

    [Fact]
    public void SharedPathPropertiesNormalizeContainedAbsoluteConsumersAndKeepRelativeBases()
    {
        string root = Path.Combine(Path.GetTempPath(), "codenav absolute inputs");
        var imports = new Dictionary<string, string>
        {
            ["Build/Outer.props"] = "<Project><Import Project=\"$(MSBuildThisFileDirectory)Deep/Inner.props\" /></Project>",
            ["Build/Deep/Inner.props"] = """
                <Project>
                  <PropertyGroup Condition="Exists('$(MSBuildThisFileDirectory)marker.config') And Exists('relative.config')">
                    <DefineConstants>PATH_CONTEXT</DefineConstants>
                  </PropertyGroup>
                  <PropertyGroup><CapturedDirectory>$(MSBuildThisFileDirectory)</CapturedDirectory></PropertyGroup>
                </Project>
                """,
        };
        var probes = new List<string>();
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
            SdkContextProject("""
                <Import Project="$(MSBuildProjectDirectory)/../Build/Outer.props" />
                <ItemGroup>
                  <Compile Include="$(MSBuildProjectDirectory)/Core.fs" /><Compile Include="Relative.fs" />
                  <ProjectReference Include="$(CapturedDirectory)../../Dependency/Dependency.fsproj" />
                  <Reference Include="Library"><HintPath>$(CapturedDirectory)../../lib/Library.dll</HintPath></Reference>
                </ItemGroup>
                """),
            "net8.0", "net8.0", importResolver: path => imports.GetValueOrDefault(path), workspaceRoot: root,
            existsResolver: path => { probes.Add(path); return true; });
        Assert.Null(result.Error);
        Assert.Equal(["Core/Core.fs", "Core/Relative.fs"], result.SourceFiles);
        Assert.Equal(["Dependency/Dependency.fsproj"], result.ProjectReferences!.Select(reference => reference.ProjectPath));
        Assert.Equal(["lib/Library.dll"], result.HintPathReferences);
        Assert.Equal(["Build/Deep/marker.config", "Core/relative.config"], probes);
        Assert.Contains("--define:PATH_CONTEXT", result.CommandLineArgs);
    }

    [Theory]
    [InlineData("Import", "fsharp_semantic_import_path_outside_workspace")]
    [InlineData("Compile", "fsharp_semantic_path_outside_workspace")]
    [InlineData("ProjectReference", "fsharp_semantic_path_outside_workspace")]
    [InlineData("HintPath", "fsharp_semantic_path_outside_workspace")]
    [InlineData("Exists", "fsharp_semantic_condition_unsupported")]
    public void SharedPathPropertiesDoNotAdmitOutsideAbsoluteConsumers(string consumer, string error)
    {
        const string outside = "$(MSBuildProjectDirectory)/../../outside";
        string body = consumer switch
        {
            "Import" => $"<Import Project=\"{outside}.props\" />",
            "HintPath" => $"<ItemGroup><Reference Include=\"Outside\"><HintPath>{outside}.dll</HintPath></Reference></ItemGroup>",
            "Exists" => $"<PropertyGroup Condition=\"Exists('{outside}.config')\"><DefineConstants>WRONG</DefineConstants></PropertyGroup>",
            _ => $"<ItemGroup><{consumer} Include=\"{outside}.{(consumer == "Compile" ? "fs" : "fsproj")}\" /></ItemGroup>",
        };
        var result = ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj", SdkContextProject(body),
            "net8.0", "net8.0", workspaceRoot: Path.Combine(Path.GetTempPath(), "codenav containment"),
            importResolver: _ => throw new InvalidOperationException("outside import must not be read"),
            existsResolver: _ => throw new InvalidOperationException("outside probe must not be read"));
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void SharedPathPropertiesUsePublishedRootDuringColdDeltaAndPinnedEvaluation()
    {
        string sandbox = Directory.CreateTempSubdirectory("codenav-published-path").FullName;
        string physical = Path.Combine(sandbox, "private-read");
        string logical = Path.Combine(sandbox, "published identity");
        try
        {
            string guard = System.Security.SecurityElement.Escape(Path.Combine(logical, "Build") + Path.DirectorySeparatorChar)!;
            string props = $$"""
                <Project><PropertyGroup Condition="'$(MSBuildThisFileDirectory)' == '{{guard}}' And Exists('$(MSBuildThisFileDirectory)web.config')">
                  <DefineConstants>PUBLISHED_PATH</DefineConstants>
                </PropertyGroup></Project>
                """;
            WriteProject(physical, "Core/Core.fsproj", SdkContextProject("<Import Project=\"../Build/Shared.props\" />"));
            WriteProject(physical, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(physical, "Build/Shared.props", props);
            string db = IndexBuilder.DefaultDbPath(physical);
            IndexBuilder.BuildOwned(physical, db, publishedWorkspaceRoot: logical);
            using var store = new IndexStore(db, createNew: false);
            using var pinned = new IndexQueries(db, pinReadSnapshot: true);
            Assert.Equal(logical, pinned.ReadMetadata().WorkspaceRoot);
            Assert.True(pinned.TryGetCapturedMsBuildFilePresence("Build/web.config", out bool? before),
                System.Text.Json.JsonSerializer.Serialize(Evaluate(pinned)));
            Assert.Equal(false, before);
            var oldOptions = Evaluate(pinned);
            Assert.Null(oldOptions.Error);
            Assert.DoesNotContain("--define:PUBLISHED_PATH", oldOptions.CommandLineArgs);
            WriteProject(physical, "Build/web.config", "present");
            DeltaRefresher.Refresh(store, physical, ["Build/web.config"]);
            using (var fresh = new IndexQueries(db))
            {
                Assert.True(fresh.TryGetCapturedMsBuildFilePresence("Build/web.config", out bool? after));
                Assert.Equal(true, after);
                Assert.Contains("--define:PUBLISHED_PATH", Evaluate(fresh).CommandLineArgs);
            }
            Assert.DoesNotContain("--define:PUBLISHED_PATH", Evaluate(pinned).CommandLineArgs);
            WriteProject(physical, "Build/Shared.props", props.Replace(" == ", " != ", StringComparison.Ordinal));
            DeltaRefresher.Refresh(store, physical, ["Build/Shared.props"]);
            using var retracted = new IndexQueries(db);
            Assert.False(retracted.TryGetCapturedMsBuildFilePresence("Build/web.config", out _));
            AssertDerivedIdentitiesRemainRelative(db, physical, logical);

            FSharpSemanticOptionsSnapshot Evaluate(IndexQueries queries) => ProjectFileParser.ParseFSharpSemanticOptionsSnapshot(
                "Core/Core.fsproj", queries.ContentByPathBounded("Core/Core.fsproj", IndexBuilder.MaxStructuralFileBytes)!, "net8.0", "net8.0",
                importResolver: path => queries.ContentByPathBounded(path, ProjectFileParser.MaxFSharpSemanticImportBytes),
                existsResolver: path => queries.TryGetCapturedMsBuildFilePresence(path, out bool? value) ? value : null,
                workspaceRoot: queries.ReadMetadata().WorkspaceRoot);
        }
        finally { Cleanup(sandbox); }
    }

    [Fact]
    public async Task SharedPathPropertiesReachPublicFSharpNavigationAndInvalidateWarmOptions()
    {
        string root = Directory.CreateTempSubdirectory("codenav-path-navigation").FullName;
        try
        {
            string docDirectory = System.Security.SecurityElement.Escape(root + Path.DirectorySeparatorChar)!;
            string projectDirectory = System.Security.SecurityElement.Escape(Path.Combine(root, "Core"))!;
            string props = $$"""
                <Project><PropertyGroup Condition="'$(MSBuildThisFileDirectory)' == '{{docDirectory}}' And '$(MSBuildProjectDirectory)' == '{{projectDirectory}}'">
                  <DefineConstants>TOOLING</DefineConstants>
                </PropertyGroup></Project>
                """;
            WriteProject(root, "Directory.Build.props", props);
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module Core\n#if TOOLING\nlet configured = 42\n#else\nlet configured = \"wrong\"\n#endif\nlet observed = configured\n");
            using var fixture = Fixture.Create(root);
            AssertDefinition(3);
            // Warm the same semantic service before changing a condition input.
            AssertDefinition(3);
            WriteProject(root, "Directory.Build.props", props.Replace(" == ", " != ", StringComparison.Ordinal));
            Assert.True(fixture.Manager.RequestRefreshForTest(["Directory.Build.props"], out Task completion));
            await completion.WaitAsync(TimeSpan.FromSeconds(30));
            AssertDefinition(5);
            AssertDerivedIdentitiesRemainRelative(IndexBuilder.DefaultDbPath(root), root);

            void AssertDefinition(int line)
            {
                string raw = CallSemantic(() => fixture.Tools.Definition(path: "Core/Core.fs", line: 7, column: 18, mode: "semantic", timeoutMs: 60_000));
                var result = Parse(raw);
                Assert.True(result.GetProperty("found").GetBoolean(), raw);
                Assert.Contains(result.GetProperty("declarations").EnumerateArray(), declaration =>
                    declaration.GetProperty("path").GetString() == "Core/Core.fs" && declaration.GetProperty("startLine").GetInt32() == line);
                if (result.TryGetProperty("partialReason", out var reason))
                    Assert.DoesNotContain("fsharp_semantic_optional_property_assumed_empty", reason.GetString() ?? "");
            }
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedPathPropertiesRequireExplicitRebuildAfterRelocation(bool rootSensitive)
    {
        string sandbox = Directory.CreateTempSubdirectory("codenav-path-move").FullName;
        string beforeRoot = Path.Combine(sandbox, "before");
        string afterRoot = Path.Combine(sandbox, "after");
        try
        {
            string expectedRoot = System.Security.SecurityElement.Escape(Path.Combine(beforeRoot, "Core"))!;
            string condition = rootSensitive ? $"'$(MSBuildProjectDirectory)' == '{expectedRoot}'" : "'$(MSBuildProjectName)' == 'Core'";
            WriteProject(beforeRoot, "Core/Core.fsproj", SdkContextProject($"<PropertyGroup Condition=\"{condition}\"><DefineConstants>ORIGINAL_CONTEXT</DefineConstants></PropertyGroup>"));
            WriteProject(beforeRoot, "Core/Core.fs", "module Core\nlet value = 42\n");
            IndexBuilder.Build(beforeRoot);
            Assert.Contains("--define:ORIGINAL_CONTEXT", ReadOptions(beforeRoot).CommandLineArgs);
            IndexQueries.ClearPoolsFor(IndexBuilder.DefaultDbPath(beforeRoot));
            Directory.Move(beforeRoot, afterRoot);
            string movedDb = IndexBuilder.DefaultDbPath(afterRoot);
            Assert.Throws<IndexWorkspaceRebindRequiredException>(() => IndexBuilder.EnsureExistingDatabaseWorkspace(afterRoot, movedDb));
            IndexBuilder.Build(afterRoot, movedDb); // Explicit build, not implicit relocation recovery.
            var after = ReadOptions(afterRoot);
            Assert.Null(after.Error);
            Assert.Equal(["Core/Core.fs"], after.SourceFiles);
            Assert.Equal(!rootSensitive, after.CommandLineArgs.Contains("--define:ORIGINAL_CONTEXT"));
            AssertDerivedIdentitiesRemainRelative(movedDb, beforeRoot, afterRoot);

            FSharpSemanticOptionsSnapshot ReadOptions(string root)
            {
                using var queries = new IndexQueries(IndexBuilder.DefaultDbPath(root));
                return ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                    queries.ContentByPathBounded("Core/Core.fsproj", IndexBuilder.MaxStructuralFileBytes)!, "net8.0", "net8.0",
                    workspaceRoot: queries.ReadMetadata().WorkspaceRoot);
            }
        }
        finally { Cleanup(sandbox); }
    }

    private static void AssertDerivedIdentitiesRemainRelative(string db, params string[] roots)
    {
        using var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        // Item, project-reference and solution-membership edges use IDs into these tables.
        // Deliberate meta.workspace_root and original source/content snapshots are not identities.
        command.CommandText = "SELECT path FROM files UNION ALL SELECT path FROM projects UNION ALL SELECT dir FROM projects UNION ALL SELECT path FROM solutions UNION ALL SELECT path FROM msbuild_exists_inputs";
        using var reader = command.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            string value = reader.GetString(0);
            Assert.False(Path.IsPathRooted(value), value);
            foreach (string root in roots) Assert.DoesNotContain(root, value, StringComparison.OrdinalIgnoreCase);
            count++;
        }
        Assert.True(count > 0);
    }
}
