using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void SymbolAtAndDefinitionUseFcsAndReturnSignatureAndImplementation()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0",
                "Api.fsi", "Api.fs", "Use.fs"));
            WriteProject(root, "Core/Api.fsi", """
                namespace StageTwo

                module Api =
                    val increment: int -> int
                """);
            WriteProject(root, "Core/Api.fs", """
                namespace StageTwo

                module Api =
                    let increment value = value + 1
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace StageTwo

                module Use =
                    let result = Api.increment 41
                """);

            using var fixture = Fixture.Create(root);
            JsonElement at = Parse(CallSemantic(() => fixture.Tools.SymbolAt("Core/Use.fs", 4, 25)));
            Assert.True(at.GetProperty("found").GetBoolean());
            Assert.Equal("increment", at.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("function", at.GetProperty("symbol").GetProperty("kind").GetString());
            Assert.Equal("Core", at.GetProperty("symbol").GetProperty("assembly").GetString());
            Assert.Equal("Core/Core.fsproj", at.GetProperty("selectedFSharpTypeCheckContext")
                .GetProperty("project").GetString());
            Assert.Equal("net9.0", at.GetProperty("selectedFSharpTypeCheckContext")
                .GetProperty("targetFramework").GetString());
            Assert.Equal("semantic", at.GetProperty("meta").GetProperty("navigationLayer").GetString());
            Assert.Contains("fsharp_core_reference_defaulted",
                at.GetProperty("partialReason").GetString());
            Assert.Equal("indexed", at.GetProperty("meta").GetProperty("confidence").GetString());

            JsonElement definition = Parse(CallSemantic(() => fixture.Tools.Definition(
                path: "Core/Use.fs", line: 4, column: 25, mode: "semantic")));
            Assert.False(definition.TryGetProperty("error", out _));
            var sites = definition.GetProperty("declarations").EnumerateArray().ToList();
            Assert.Contains(sites, site =>
                site.GetProperty("role").GetString() == "signature" &&
                site.GetProperty("path").GetString() == "Core/Api.fsi");
            Assert.Contains(sites, site =>
                site.GetProperty("role").GetString() == "implementation" &&
                site.GetProperty("path").GetString() == "Core/Api.fs");
            Assert.Equal(2, definition.GetProperty("declarationsTotal").GetInt32());
            Assert.Equal(2, sites.Count);
            Assert.False(definition.TryGetProperty("declarationsOutsideSelectedProjectCount",
                out _));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SemanticCheckReadsThePinnedIndexedSourceInsteadOfChangedDiskContent()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-snapshot").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Api.fs", "Use.fs"));
            WriteProject(root, "Core/Api.fs", """
                namespace Snapshot
                module Api =
                    let indexedOnly value = value + 1
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace Snapshot
                module Use =
                    let result = Api.indexedOnly 41
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var fixture = Fixture.Start(root, dbPath);
            // Diverge disk only after the complete project snapshot has been captured. This makes
            // the test independent of watcher startup/reconcile timing: FCS must receive the older
            // indexed Api.fs through DocumentSource.Custom even if the watcher refreshes meanwhile.
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
                WriteProject(root, "Core/Api.fs", """
                    namespace Snapshot
                    module Api =
                        let diskOnly value = value - 1
                    """);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt("Core/Use.fs", 3, 24));
            JsonElement at = Parse(raw);
            Assert.True(at.TryGetProperty("found", out JsonElement found) && found.GetBoolean(), raw);
            Assert.Equal("indexedOnly", at.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Contains(at.GetProperty("declarations").EnumerateArray(), site =>
                site.GetProperty("path").GetString() == "Core/Api.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PairedMigrationProjectsRequireAnExplicitPhysicalTypeCheckContext()
    {
        string container = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-context").FullName;
        string root = Path.Combine(container, "Workspace");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(container, "Directory.Build.props"),
                "<Project><PropertyGroup><ExternalAuthority>true</ExternalAuthority></PropertyGroup></Project>");
            WriteProject(root, "Paired/Shared.fs", "module Shared\nlet value = 1\n");
            string? fsharpCore = ReferenceAssemblyLocator.FSharpCoreReferencePath("net472", out _);
            Assert.NotNull(fsharpCore);
            string copiedFSharpCore = Path.Combine(root, "Lib", "FSharp.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(copiedFSharpCore)!);
            File.Copy(fsharpCore!, copiedFSharpCore);
            WriteProject(root, "Paired/Project.fsproj", """
                <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <Import Project="$(MSBuildToolsPath)\Microsoft.Common.props" Condition="Exists('$(MSBuildToolsPath)\Microsoft.Common.props')" />
                  <PropertyGroup>
                    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
                    <AssemblyName>Project</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="System.Configuration" />
                    <Reference Include="System.ServiceModel" />
                    <Reference Include="System.Web" />
                    <Reference Include="FSharp.Core">
                      <HintPath>..\Lib\FSharp.Core.dll</HintPath>
                    </Reference>
                    <Compile Include="Shared.fs" />
                  </ItemGroup>
                  <Import Project="$(FSharpTargetsPath)" />
                </Project>
                """);
            WriteProject(root, "Paired/Project.Net.fsproj", SdkProject("net472;net8.0",
                "Shared.fs"));

            using var fixture = Fixture.Create(root);
            string? snapshotPath = null;
            fixture.Semantic.FSharpReferenceSnapshotCreatedForTest = path => snapshotPath = path;
            JsonElement response = Parse(CallSemantic(() =>
                fixture.Tools.SymbolAt("Paired/Shared.fs", 2, 5)));
            Assert.Equal("fsharp_type_check_context_required",
                response.GetProperty("error").GetString());
            Assert.Equal(3, response.GetProperty("fsharpTypeCheckContextsTotal").GetInt32());
            Assert.Equal(3, response.GetProperty("fsharpTypeCheckContextsReturned").GetInt32());
            Assert.False(response.GetProperty("fsharpTypeCheckContextsTruncated").GetBoolean());
            Assert.False(response.TryGetProperty("selectedFSharpTypeCheckContext", out _));

            string legacyRaw = CallSemantic(() => fixture.Tools.SymbolAt("Paired/Shared.fs", 2, 5,
                projectPath: "Paired/Project.fsproj", targetFramework: "net472",
                timeoutMs: 60_000));
            JsonElement legacy = Parse(legacyRaw);
            Assert.True(legacy.TryGetProperty("found", out JsonElement legacyFound) &&
                        legacyFound.GetBoolean(), legacyRaw);
            Assert.Equal("value", legacy.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("Project", legacy.GetProperty("symbol").GetProperty("assembly").GetString());
            Assert.Equal("Paired/Project.fsproj",
                legacy.GetProperty("selectedFSharpTypeCheckContext")
                    .GetProperty("project").GetString());
            Assert.Equal("net472", legacy.GetProperty("selectedFSharpTypeCheckContext")
                .GetProperty("targetFramework").GetString());
            Assert.Contains("fsharp_binary_references_snapshotted",
                legacy.GetProperty("partialReason").GetString());
            Assert.Contains("fsharp_semantic_toolchain_implicit_authority",
                legacy.GetProperty("partialReason").GetString());
            Assert.DoesNotContain("fsharp_project_options_imported",
                legacy.GetProperty("partialReason").GetString());
            Assert.NotNull(snapshotPath);
            Assert.False(File.Exists(snapshotPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(snapshotPath)!));
        }
        finally
        {
            Cleanup(container);
        }
    }

    [Fact]
    public void Stage2ARejectsProjectReferenceClosureWithoutFallingBackToIndexedFSharpSymbols()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-reference").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <ProjectReference Include="../Dependency/Dependency.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Dependency/Dependency.fsproj", SdkProject("net9.0",
                "Dependency.fs"));
            WriteProject(root, "Dependency/Dependency.fs", "module Dependency\nlet value = 1\n");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.Definition(
                path: "Core/Core.fs", line: 2, column: 5, mode: "auto")));
            Assert.Equal("fsharp_semantic_project_references_unsupported",
                response.GetProperty("error").GetString());
            Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                         found.ValueKind == JsonValueKind.True);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ExplicitTypeCheckSelectionRequiresBothProjectAndTargetAndControlsIfDef()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-tfm").FullName;
        try
        {
            const string source = """
                module Contextual
                #if NET8_0
                let selectedBranch = 8
                #else
                let selectedBranch = 472
                #endif
                let use = selectedBranch
                """;
            WriteProject(root, "Context/Context.fsproj", SdkProject("net472;net8.0",
                "Shared.fs"));
            WriteProject(root, "Context/Shared.fs", source);

            using var fixture = Fixture.Create(root);
            JsonElement missingTarget = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Context/Shared.fs", 7, 11,
                projectPath: "Context/Context.fsproj", timeoutMs: 60_000)));
            Assert.Equal("fsharp_type_check_context_required",
                missingTarget.GetProperty("error").GetString());
            JsonElement missingProject = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Context/Shared.fs", 7, 11,
                targetFramework: "net8.0", timeoutMs: 60_000)));
            Assert.Equal("fsharp_type_check_context_required",
                missingProject.GetProperty("error").GetString());

            JsonElement selected = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Context/Shared.fs", 7, 11,
                projectPath: "Context/Context.fsproj", targetFramework: "net8.0",
                timeoutMs: 60_000)));
            Assert.True(selected.GetProperty("found").GetBoolean());
            Assert.Equal("selectedBranch",
                selected.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("net8.0", selected.GetProperty("selectedFSharpTypeCheckContext")
                .GetProperty("targetFramework").GetString());
            JsonElement firstAvailable = selected.GetProperty("availableFSharpTypeCheckContexts")[0];
            Assert.Equal("Context/Context.fsproj", firstAvailable.GetProperty("project").GetString());
            Assert.Equal("net8.0", firstAvailable.GetProperty("targetFramework").GetString());
            Assert.Contains(selected.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() == "Context/Shared.fs" &&
                declaration.GetProperty("startLine").GetInt32() == 3);
            Assert.DoesNotContain(selected.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("startLine").GetInt32() == 5);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void LineOnlyResolutionRequiresAColumnWhenSeveralUsesShareTheLine()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-column").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module Core\nlet add left right = left + right\n");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_column_required",
                response.GetProperty("error").GetString());

            JsonElement exact = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 22, timeoutMs: 60_000)));
            Assert.True(exact.GetProperty("found").GetBoolean());
            Assert.Equal("left", exact.GetProperty("symbol").GetProperty("name").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void NoSymbolAtPositionIsACompletedFoundFalseResultAndColumnsAreNotClamped()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-not-found").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n// no symbol here\n");

            using var fixture = Fixture.Create(root);
            JsonElement comment = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 3, 4, timeoutMs: 60_000)));
            Assert.False(comment.TryGetProperty("error", out _));
            Assert.False(comment.GetProperty("found").GetBoolean());

            JsonElement pastEnd = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 10_000, timeoutMs: 60_000)));
            Assert.False(pastEnd.TryGetProperty("error", out _));
            Assert.False(pastEnd.GetProperty("found").GetBoolean());

            JsonElement badLine = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 0, 0, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_position_invalid",
                badLine.GetProperty("error").GetString());
            JsonElement badColumn = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, -1, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_position_invalid",
                badColumn.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void OversizedFSharpSymbolFallsBackToTheHardBudgetEnvelope()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-budget").FullName;
        try
        {
            string identifier = new('x', Json.HardBudgetBytes + 4096);
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs",
                $"module Core\nlet ``{identifier}`` = 1\nlet result = ``{identifier}``\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 3, 16, timeoutMs: 60_000));
            Assert.True(Json.Utf8Bytes(raw) <= Json.HardBudgetBytes, raw);
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("error", out JsonElement error), raw);
            Assert.Equal("fsharp_semantic_response_too_large", error.GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void HintPathMustBeAManagedBoundedStableWorkspaceBinary()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-hintpath").FullName;
        try
        {
            const string project = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <Reference Include="Dependency">
                      <HintPath>../Lib/Dependency.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """;
            WriteProject(root, "Core/Core.fsproj", project);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            WriteProject(root, "Lib/Dependency.dll", "not a managed assembly");

            using var invalidFixture = Fixture.Create(root);
            JsonElement invalid = Parse(CallSemantic(() => invalidFixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_reference_unavailable",
                invalid.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }

        root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-hintpath-race").FullName;
        try
        {
            const string project = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <Reference Include="CodeNav.Core">
                      <HintPath>../Lib/CodeNav.Core.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """;
            WriteProject(root, "Core/Core.fsproj", project);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            string binary = Path.Combine(root, "Lib", "CodeNav.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(typeof(SemanticService).Assembly.Location, binary);
            long originalLength = new FileInfo(binary).Length;
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(binary);

            using var fixture = Fixture.Create(root);
            string? checkError = "not-called";
            fixture.Semantic.FSharpSemanticCheckCompletedForTest = error => checkError = error;
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
            {
                using (var stream = new FileStream(binary, FileMode.Open, FileAccess.ReadWrite,
                           FileShare.Read))
                {
                    stream.Position = 32;
                    int original = stream.ReadByte();
                    Assert.True(original >= 0);
                    stream.Position = 32;
                    stream.WriteByte((byte)(original ^ 1));
                }
                File.SetLastWriteTimeUtc(binary, originalWriteTime);
            };
            JsonElement changed = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_reference_changed",
                changed.GetProperty("error").GetString());
            Assert.Null(checkError);
            Assert.Equal(originalLength, new FileInfo(binary).Length);
            Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(binary));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void CancelledHintPathCaptureRemovesPartialSnapshot()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-cancelled-hintpath").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <Reference Include="CodeNav.Core">
                      <HintPath>../Lib/CodeNav.Core.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            string binary = Path.Combine(root, "Lib", "CodeNav.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(typeof(SemanticService).Assembly.Location, binary);

            using var fixture = Fixture.Create(root);
            string? snapshotPath = null;
            fixture.Semantic.FSharpReferenceSnapshotCreatedForTest = path =>
            {
                snapshotPath = path;
                throw new OperationCanceledException("deterministic capture cancellation");
            };
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000)));

            Assert.Equal("fsharp_semantic_timeout", response.GetProperty("error").GetString());
            Assert.NotNull(snapshotPath);
            Assert.False(File.Exists(snapshotPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(snapshotPath)!));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void HintPathReachedThroughAWorkspaceJunctionIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-junction").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-outside").FullName;
        string junction = Path.Combine(root, "Linked");
        try
        {
            File.Copy(typeof(SemanticService).Assembly.Location,
                Path.Combine(outside, "Dependency.dll"));
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <Reference Include="Dependency">
                      <HintPath>../Linked/Dependency.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            Assert.True(TryCreateJunction(junction, outside),
                "Windows junction creation is required for the no-follow regression");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_reference_unavailable",
                response.GetProperty("error").GetString());
        }
        finally
        {
            RemoveJunction(junction);
            Cleanup(root);
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void HintPathAncestorSwapBetweenValidationAndOpenIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-swap").FullName;
        string outside = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-swap-outside").FullName;
        string linked = Path.Combine(root, "Linked");
        string original = Path.Combine(root, "OriginalLinked");
        try
        {
            Directory.CreateDirectory(linked);
            File.Copy(typeof(SemanticService).Assembly.Location,
                Path.Combine(linked, "Dependency.dll"));
            File.Copy(typeof(SemanticService).Assembly.Location,
                Path.Combine(outside, "Dependency.dll"));
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <Reference Include="Dependency">
                      <HintPath>../Linked/Dependency.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");

            using var fixture = Fixture.Create(root);
            int swaps = 0;
            fixture.Semantic.BeforeFSharpReferenceOpenForTest = _ =>
            {
                if (Interlocked.Increment(ref swaps) != 1) return;
                Directory.Move(linked, original);
                Assert.True(TryCreateJunction(linked, outside));
            };
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_reference_unavailable",
                response.GetProperty("error").GetString());
            Assert.Equal(1, swaps);
        }
        finally
        {
            RemoveJunction(linked);
            Cleanup(root);
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SemanticReferenceCountIsBoundedAtSixtyFour()
    {
        string[] frameworkAssemblyNames = ReferenceAssemblyLocator
            .FrameworkReferencePaths("net9.0", out _)
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SemanticService.MaxFSharpSemanticHintPaths + 1)
            .ToArray();
        Assert.Equal(SemanticService.MaxFSharpSemanticHintPaths + 1,
            frameworkAssemblyNames.Length);

        foreach (int count in new[] { 64, 65 })
        {
            string root = Directory.CreateTempSubdirectory(
                $"codenav-fsharp-semantic-reference-cap-{count}").FullName;
            try
            {
                string references = string.Join(Environment.NewLine,
                    frameworkAssemblyNames.Take(count)
                        .Select(name => $"<Reference Include=\"{name}\" />"));
                WriteProject(root, "Core/Core.fsproj", $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net9.0</TargetFramework>
                        <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                      </PropertyGroup>
                      <ItemGroup>
                        <Compile Include="Core.fs" />
                        {{references}}
                      </ItemGroup>
                    </Project>
                    """);
                WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");

                using var fixture = Fixture.Create(root);
                string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                    "Core/Core.fs", 2, 5, timeoutMs: 60_000));
                JsonElement response = Parse(raw);
                if (count == SemanticService.MaxFSharpSemanticHintPaths)
                {
                    Assert.True(response.TryGetProperty("found", out JsonElement found) &&
                                found.GetBoolean(), raw);
                }
                else
                {
                    Assert.True(response.TryGetProperty("error", out JsonElement error), raw);
                    Assert.Equal("fsharp_semantic_reference_limit",
                        error.GetString());
                }
            }
            finally
            {
                Cleanup(root);
            }
        }
    }

    [Fact]
    public void SemanticReferenceByteBudgetHasAnExactBoundaryWithoutLargeFixtures()
    {
        long total = SemanticService.MaxFSharpSemanticReferenceBytes - 1;
        Assert.True(SemanticService.TryAccumulateFSharpSemanticReferenceBytes(ref total, 1));
        Assert.Equal(SemanticService.MaxFSharpSemanticReferenceBytes, total);

        Assert.False(SemanticService.TryAccumulateFSharpSemanticReferenceBytes(ref total, 1));
        Assert.Equal(SemanticService.MaxFSharpSemanticReferenceBytes, total);

        total = 0;
        Assert.False(SemanticService.TryAccumulateFSharpSemanticReferenceBytes(ref total,
            SemanticService.MaxFSharpSemanticReferenceBytes + 1));
        Assert.Equal(0, total);
    }

    [Fact]
    public void SemanticReferenceCopyRejectsAStreamThatGrowsPastItsAdmittedLength()
    {
        using var exactSource = new MemoryStream([1, 2, 3, 4]);
        using var exactDestination = new MemoryStream();
        Assert.True(SemanticService.TryCopyFSharpReferenceStream(exactSource, exactDestination,
            expectedLength: 4, maximumLength: 4, CancellationToken.None,
            out long exactCopied, out string? exactHash, out bool exactLimitExceeded));
        Assert.Equal(4, exactCopied);
        Assert.Equal(4, exactDestination.Length);
        Assert.NotNull(exactHash);
        Assert.False(exactLimitExceeded);

        using var growingSource = new ReportedLengthStream(4, [1, 2, 3, 4, 5]);
        using var boundedDestination = new MemoryStream();
        Assert.False(SemanticService.TryCopyFSharpReferenceStream(growingSource,
            boundedDestination, expectedLength: 4, maximumLength: 4, CancellationToken.None,
            out long copied, out string? hash, out bool limitExceeded));
        Assert.Equal(0, copied);
        Assert.Equal(0, boundedDestination.Length);
        Assert.Null(hash);
        Assert.True(limitExceeded);
    }

    [Fact]
    public void SemanticReferenceByteLimitRejectsBeforeCopyWithSpecificCause()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-reference-byte-limit").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Core.fs" />
                    <Reference Include="CodeNav.Core">
                      <HintPath>../Lib/CodeNav.Core.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            string binary = Path.Combine(root, "Lib", "CodeNav.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(typeof(SemanticService).Assembly.Location, binary);

            using var fixture = Fixture.Create(root);
            fixture.Semantic.FSharpSemanticReferenceBytesLimitForTest =
                new FileInfo(binary).Length - 1;
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000)));

            Assert.Equal("fsharp_semantic_reference_bytes_limit",
                response.GetProperty("error").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void TypeCheckContextCoverageIsBoundedAtSixtyFour()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-semantic-context-cap").FullName;
        try
        {
            WriteProject(root, "Fanout/Shared.fs", "module Shared\nlet value = 1\n");
            for (int i = 1; i <= 65; i++)
            {
                WriteProject(root, $"Fanout/Owner{i:D2}.fsproj",
                    SdkProject("net9.0", "Shared.fs"));
            }

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt("Fanout/Shared.fs", 2, 5));
            Assert.True(Json.Utf8Bytes(raw) <= Json.HardBudgetBytes);
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_type_check_context_required",
                response.GetProperty("error").GetString());
            Assert.Equal(65, response.GetProperty("fsharpTypeCheckContextsTotal").GetInt32());
            Assert.Equal(64, response.GetProperty("fsharpTypeCheckContextsReturned").GetInt32());
            Assert.True(response.GetProperty("fsharpTypeCheckContextsTruncated").GetBoolean());
            Assert.Equal(64,
                response.GetProperty("availableFSharpTypeCheckContexts").GetArrayLength());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void LineOnlySourceLimitHasItsOwnCauseAndExactCharacterBoundary()
    {
        foreach (int extra in new[] { 0, 1 })
        {
            string root = Directory.CreateTempSubdirectory(
                $"codenav-fsharp-line-limit-{extra}").FullName;
            try
            {
                const string prefix = "module Core\nlet value = 1\n//";
                int length = SemanticService.MaxFSharpSemanticLineOnlySourceChars + extra;
                string source = prefix + new string('x', length - prefix.Length);
                Assert.Equal(length, source.Length);
                WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Core.fs"));
                WriteProject(root, "Core/Core.fs", source);

                using var fixture = Fixture.Create(root);
                JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                    "Core/Core.fs", 2, timeoutMs: 60_000)));
                if (extra == 0)
                {
                    Assert.False(response.TryGetProperty("error", out _));
                    Assert.True(response.GetProperty("found").GetBoolean());
                }
                else
                {
                    Assert.Equal("fsharp_semantic_line_only_source_limit",
                        response.GetProperty("error").GetString());
                    JsonElement limit = response.GetProperty("limit");
                    Assert.Equal(length, limit.GetProperty("actual").GetInt32());
                    Assert.Equal(SemanticService.MaxFSharpSemanticLineOnlySourceChars,
                        limit.GetProperty("maximum").GetInt32());
                    Assert.Equal("characters", limit.GetProperty("unit").GetString());
                }
            }
            finally
            {
                Cleanup(root);
            }
        }
    }

    [Fact]
    public void ErrorDiagnosticsAreStructuredSanitizedAndMakeTheResultPartial()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-diagnostics").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", """
                module Core
                let good = 1
                let broken: int = "wrong"
                let result = good
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 4, 11, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Contains("fsharp_semantic_diagnostics_present",
                response.GetProperty("partialReason").GetString());
            Assert.Equal("indexed",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            JsonElement diagnostic = Assert.Single(response.GetProperty("diagnostics")
                .EnumerateArray(), item =>
                    item.GetProperty("severity").GetString() == "error");
            Assert.StartsWith("FS", diagnostic.GetProperty("code").GetString());
            Assert.Equal("Core/Core.fs", diagnostic.GetProperty("path").GetString());
            Assert.DoesNotContain(root, raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                diagnostic.GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SemanticAdmissionPrecedesSnapshotCapture()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-admission").FullName;
        using var firstCaptured = new ManualResetEventSlim(false);
        using var releaseFirst = new ManualResetEventSlim(false);
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", "module Core\nlet value = 1\n");
            using var fixture = Fixture.Create(root);
            int captureCount = 0;
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
            {
                if (Interlocked.Increment(ref captureCount) == 1)
                {
                    firstCaptured.Set();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
                }
            };

            Task<string> first = Task.Run(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            Assert.True(firstCaptured.Wait(TimeSpan.FromSeconds(10)));
            Task<string> second = Task.Run(() => fixture.Tools.SymbolAt(
                "Core/Core.fs", 2, 5, timeoutMs: 60_000));
            await Task.Delay(250);
            Assert.Equal(1, Volatile.Read(ref captureCount));
            releaseFirst.Set();
            JsonElement[] results = (await Task.WhenAll(first, second)).Select(Parse).ToArray();
            Assert.All(results, result => Assert.True(result.GetProperty("found").GetBoolean()));
            Assert.Equal(2, Volatile.Read(ref captureCount));
        }
        finally
        {
            releaseFirst.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public void FSharpRecoveryMetadataRequiresCompileOwnershipAndSupportedFileKind()
    {
        var health = new IndexHealth("ready", "test", "indexed", "refreshed", 0,
            null, 1, "C:/workspace", "C:/workspace/index.db");
        JsonElement owned = Parse(NavigationTools.UnsupportedLanguageForTest(health,
            "Core/Core.fs", "fs", "references", compileOwnedFSharp: true));
        Assert.Contains("symbol_at", owned.GetProperty("availableForFile").EnumerateArray()
            .Select(item => item.GetString()));
        Assert.Contains("definition", owned.GetProperty("availableForFile").EnumerateArray()
            .Select(item => item.GetString()));

        foreach ((string path, bool ownedFile) in new[]
                 {
                     ("Script.fsx", true),
                     ("Loose.fs", false),
                     ("Loose.fsi", false),
                 })
        {
            JsonElement response = Parse(NavigationTools.UnsupportedLanguageForTest(health,
                path, "fs", "references", ownedFile));
            string[] available = response.GetProperty("availableForFile").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            Assert.DoesNotContain("outline", available);
            Assert.DoesNotContain("symbol_at", available);
            Assert.DoesNotContain("definition", available);
        }
    }

    [Fact]
    public void ProjectDiagnosticsFromEarlierFilesRemainVisibleOnASuccessfulSymbol()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-project-diagnostics").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Broken.fs", "Use.fs"));
            WriteProject(root, "Core/Broken.fs", "module Broken\nlet impossible : int = \"text\"\n");
            WriteProject(root, "Core/Use.fs", "module Use\nlet targetValue = 42\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt("Core/Use.fs", 2, 5,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.True(response.TryGetProperty("found", out JsonElement found) &&
                        found.GetBoolean(), raw);
            Assert.Equal("targetValue", response.GetProperty("symbol").GetProperty("name")
                .GetString());
            Assert.Contains("fsharp_semantic_diagnostics_present",
                response.GetProperty("partialReason").GetString());
            Assert.True(response.GetProperty("diagnosticCount").GetInt32() > 0);
            Assert.Contains(response.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
                diagnostic.GetProperty("severity").GetString() == "error" &&
                diagnostic.GetProperty("path").GetString() == "Core/Broken.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ExternalFSharpDeclarationCoverageIsExplicitAndDefinitionFailsClosed()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-external-declaration").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0", "Use.fs"));
            WriteProject(root, "Core/Use.fs",
                "module Use\nlet result = List.map id [1]\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt("Core/Use.fs", 2, 20,
                timeoutMs: 60_000));
            JsonElement symbolAt = Parse(raw);
            Assert.True(symbolAt.TryGetProperty("found", out JsonElement found) &&
                        found.GetBoolean(), raw);
            Assert.Equal("map", symbolAt.GetProperty("symbol").GetProperty("name").GetString());
            Assert.True(symbolAt.GetProperty("declarationsTotal").GetInt32() > 0);
            Assert.Equal(0, symbolAt.GetProperty("declarations").GetArrayLength());
            Assert.Equal(symbolAt.GetProperty("declarationsTotal").GetInt32(),
                symbolAt.GetProperty("declarationsOutsideSelectedProjectCount").GetInt32());

            string definitionRaw = CallSemantic(() => fixture.Tools.Definition(
                path: "Core/Use.fs", line: 2, column: 20, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement definition = Parse(definitionRaw);
            Assert.Equal("fsharp_definition_not_in_selected_project",
                definition.GetProperty("error").GetString());
            Assert.Equal(symbolAt.GetProperty("declarationsTotal").GetInt32(),
                definition.GetProperty("declarationsOutsideSelectedProjectCount").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void CaseSensitiveHostsPreserveDistinctCaseOnlySourcePaths()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-case-sensitive").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net9.0",
                "Foo.fs", "foo.fs", "Use.fs"));
            WriteProject(root, "Core/Foo.fs", "module Upper\nlet marker = 1\n");
            WriteProject(root, "Core/foo.fs", "module Lower\nlet marker = 2\n");
            WriteProject(root, "Core/Use.fs", "module Use\nlet result = Lower.marker\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt("Core/Use.fs", 2, 20,
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("found", out JsonElement found) &&
                        found.GetBoolean(), raw);
            Assert.Contains(response.GetProperty("declarations").EnumerateArray(), declaration =>
                declaration.GetProperty("path").GetString() == "Core/foo.fs");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SemanticProjectCaptureEvaluatesBoundedInputsAndFailsClosedForUnsupportedOnes()
    {
        const string prefix = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            """;
        FSharpSemanticOptionsSnapshot wildcard =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"**/*.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_compile_order_unavailable", wildcard.Error);

        FSharpSemanticOptionsSnapshot excluded =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Ghost.fs;Core.fs\" Exclude=\"Ghost.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_compile_order_unavailable", excluded.Error);

        FSharpSemanticOptionsSnapshot conditioned =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Null(conditioned.Error);
        Assert.Equal(["Core/Core.fs"], conditioned.SourceFiles);

        FSharpSemanticOptionsSnapshot imported =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup><Import Project=\"Custom.targets\" /></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_import_unsupported", imported.Error);

        FSharpSemanticOptionsSnapshot package =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /><PackageReference Include=\"Example\" Version=\"1.0.0\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_package_references_unsupported", package.Error);

        FSharpSemanticOptionsSnapshot sourceEscape =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"../../Outside.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_path_outside_workspace", sourceEscape.Error);

        FSharpSemanticOptionsSnapshot rootedSource =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"/Outside.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_path_outside_workspace", rootedSource.Error);

        FSharpSemanticOptionsSnapshot hintEscape =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /><Reference Include=\"Outside\"><HintPath>../../Outside.dll</HintPath></Reference></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_path_outside_workspace", hintEscape.Error);

        FSharpSemanticOptionsSnapshot conditionedAssembly =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><AssemblyName>Wrong</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Null(conditionedAssembly.Error);
        Assert.Equal("Wrong", conditionedAssembly.AssemblyName);

        FSharpSemanticOptionsSnapshot unevaluatedAssembly =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<PropertyGroup><AssemblyName>$(TargetName)</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_assembly_name_unavailable", unevaluatedAssembly.Error);

        FSharpSemanticOptionsSnapshot duplicateAssembly =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<PropertyGroup><AssemblyName>First</AssemblyName><AssemblyName>Second</AssemblyName></PropertyGroup><ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Null(duplicateAssembly.Error);
        Assert.Equal("Second", duplicateAssembly.AssemblyName);

        FSharpSemanticOptionsSnapshot conditionedHint =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /><Reference Include=\"Dependency\"><HintPath Condition=\"'$(TargetFramework)' == 'net9.0'\">../Lib/Dependency.dll</HintPath></Reference></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Null(conditionedHint.Error);
        Assert.Equal(["Lib/Dependency.dll"], conditionedHint.HintPathReferences);

        FSharpSemanticOptionsSnapshot duplicateHint =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix + "<ItemGroup><Compile Include=\"Core.fs\" /><Reference Include=\"Dependency\"><HintPath>../Lib/First.dll</HintPath><HintPath>../Lib/Second.dll</HintPath></Reference></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Equal("fsharp_semantic_reference_unresolved", duplicateHint.Error);

        foreach (string unsafeDefaultMembership in new[]
                 {
                     "<PropertyGroup><EnableDefaultCompileItems>$(UseDefaults)</EnableDefaultCompileItems></PropertyGroup>",
                     "<PropertyGroup><EnableDefaultCompileItems>true</EnableDefaultCompileItems></PropertyGroup>",
                     "<PropertyGroup><EnableDefaultCompileItems>maybe</EnableDefaultCompileItems></PropertyGroup>",
                 })
        {
            FSharpSemanticOptionsSnapshot unsafeDefaults =
                ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                    prefix + unsafeDefaultMembership +
                    "<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                    "net9.0", "net9.0");
            Assert.Equal("fsharp_semantic_compile_order_unavailable", unsafeDefaults.Error);
        }

        foreach (string evaluatedDefaultMembership in new[]
                 {
                     "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>",
                     "<PropertyGroup><EnableDefaultCompileItems>true</EnableDefaultCompileItems><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>",
                 })
        {
            FSharpSemanticOptionsSnapshot evaluatedDefaults =
                ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                    prefix + evaluatedDefaultMembership +
                    "<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                    "net9.0", "net9.0");
            Assert.Null(evaluatedDefaults.Error);
            Assert.Equal(["Core/Core.fs"], evaluatedDefaults.SourceFiles);
        }

        FSharpSemanticOptionsSnapshot safeDefaults =
            ProjectFileParser.ParseFSharpSemanticOptionsSnapshot("Core/Core.fsproj",
                prefix +
                "<PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>" +
                "<ItemGroup><Compile Include=\"Core.fs\" /></ItemGroup></Project>",
                "net9.0", "net9.0");
        Assert.Null(safeDefaults.Error);
        Assert.Equal(["Core/Core.fs"], safeDefaults.SourceFiles);
    }

    private static string SdkProject(string targetFrameworks, params string[] sources)
    {
        string frameworkProperty = targetFrameworks.Contains(';')
            ? $"<TargetFrameworks>{targetFrameworks}</TargetFrameworks>"
            : $"<TargetFramework>{targetFrameworks}</TargetFramework>";
        string compileItems = string.Join(Environment.NewLine,
            sources.Select(source => $"<Compile Include=\"{source}\" />"));
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {{frameworkProperty}}
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                {{compileItems}}
              </ItemGroup>
            </Project>
            """;
    }

    private static void WriteProject(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string CallSemantic(Func<string> call, int attempts = 3)
    {
        string last = "";
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(250);
            last = call();
            JsonElement response = Parse(last);
            if (!response.TryGetProperty("error", out JsonElement error) ||
                error.GetString() != "index_snapshot_unavailable")
                return last;
        }
        Assert.Fail($"F# semantic snapshot remained transiently unavailable: {last}");
        return last;
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }

    private static bool TryCreateJunction(string junction, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction);
        start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null || !process.WaitForExit(5_000) || process.ExitCode != 0)
            return false;
        return Directory.Exists(junction) &&
               (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0;
    }

    private static void RemoveJunction(string junction)
    {
        try
        {
            if (Directory.Exists(junction) &&
                (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(junction);
        }
        catch { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IndexManager _manager;
        private readonly SemanticService _semantic;

        private Fixture(IndexManager manager, SemanticService semantic)
        {
            _manager = manager;
            _semantic = semantic;
            Tools = new NavigationTools(manager, semantic);
        }

        public NavigationTools Tools { get; }
        public SemanticService Semantic => _semantic;
        public IndexManager Manager => _manager;

        public static Fixture Create(string root)
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            return Start(root, dbPath);
        }

        public static Fixture Start(string root, string dbPath)
        {
            var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var semantic = new SemanticService(manager);
            return new Fixture(manager, semantic);
        }

        public void Dispose()
        {
            _semantic.Dispose();
            _manager.Dispose();
        }
    }

    private sealed class ReportedLengthStream(long reportedLength, byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => reportedLength;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(count, content.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(content, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static void Cleanup(string root)
    {
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
