using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void ReferencesReturnsOnlyCompilerBoundNonDefinitionUsesFromSelectedProject()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-references").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0",
                "Api.fsi", "Api.fs", "Other.fs", "Use.fs"));
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
            WriteProject(root, "Core/Other.fs", """
                namespace StageTwo
                module Other =
                    let first = Api.increment 1
                    let second = Api.increment 2
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace StageTwo
                module Use =
                    let increment = 100
                    let shadowed = increment + 1
                    let result = Api.increment 41
                    let text = "Api.increment"
                    // Api.increment 42
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Core/Use.fs", line: 5, column: 25, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean());
            Assert.Equal("increment",
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal(3, response.GetProperty("totalReferences").GetInt32());
            Assert.False(response.TryGetProperty("totalIsLowerBound", out _));
            Assert.Equal("project", response.GetProperty("groupBy").GetString());
            JsonElement group = Assert.Single(response.GetProperty("groups").EnumerateArray());
            Assert.Equal("Core/Core.fsproj", group.GetProperty("project").GetString());
            Assert.Equal("net10.0", group.GetProperty("targetFramework").GetString());
            Assert.Equal(3, group.GetProperty("count").GetInt32());
            var samples = group.GetProperty("samples").EnumerateArray().ToList();
            Assert.Equal(3, samples.Count);
            Assert.Equal(
                ["Core/Other.fs:3", "Core/Other.fs:4", "Core/Use.fs:5"],
                samples.Select(sample =>
                    $"{sample.GetProperty("path").GetString()}:{sample.GetProperty("line").GetInt32()}")
                    .ToArray());
            Assert.Equal(
                [(17, 3, 30), (18, 4, 31), (18, 5, 31)],
                samples.Select(sample => (
                    sample.GetProperty("startColumn").GetInt32(),
                    sample.GetProperty("endLine").GetInt32(),
                    sample.GetProperty("endColumn").GetInt32())).ToArray());
            Assert.All(samples, sample => Assert.Contains("Api.increment",
                sample.GetProperty("text").GetString()));
            Assert.DoesNotContain(samples, sample =>
                sample.GetProperty("path").GetString() is "Core/Api.fs" or "Core/Api.fsi");
            Assert.Equal("selected_physical_project_declaring_project_and_workspace_dependents",
                response.GetProperty("coverage").GetProperty("scope").GetString());
            Assert.Equal(0, response.GetProperty("coverage")
                .GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(0, response.GetProperty("coverage")
                .GetProperty("workspaceDependentsScanned").GetInt32());
            Assert.True(response.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
            Assert.DoesNotContain("fsharp_workspace_dependents_not_scanned",
                response.GetProperty("partialReason").GetString());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal("semantic",
                response.GetProperty("meta").GetProperty("navigationLayer").GetString());
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(raw) <= Json.HardBudgetBytes);

            JsonElement testsExcluded = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Core/Use.fs", line: 5, column: 25, mode: "semantic",
                includeTests: false, samplesPerGroup: 10, timeoutMs: 60_000)));
            Assert.Equal(3, testsExcluded.GetProperty("totalReferences").GetInt32());
            Assert.Equal(3, Assert.Single(testsExcluded.GetProperty("groups")
                .EnumerateArray()).GetProperty("count").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesScanPinnedWorkspaceDependentsAndReturnACompleteExactTotal()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-dependents").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", """
                module Library
                let value = 1
                let local = value
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs", """
                module Consumer
                let first = Library.value
                let second = Library.value
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var fixture = Fixture.Start(root, dbPath);
            var sourceReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            fixture.Semantic.BeforeFSharpSemanticSourceReadForTest = path =>
                sourceReads[path] = sourceReads.GetValueOrDefault(path) + 1;
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
                WriteProject(root, "Consumer/Consumer.fs", """
                    module Consumer
                    let first = 1
                    let second = 2
                    """);

            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(3, response.GetProperty("totalReferences").GetInt32());
            Assert.False(response.TryGetProperty("totalIsLowerBound", out _));
            var groups = response.GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal(2, groups.Count);
            JsonElement library = Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "Library/Library.fsproj");
            Assert.Equal(1, library.GetProperty("count").GetInt32());
            JsonElement consumer = Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "Consumer/Consumer.fsproj");
            Assert.Equal(2, consumer.GetProperty("count").GetInt32());
            Assert.Equal(["net10.0"], consumer.GetProperty("targetFrameworksScanned")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
            Assert.Equal(
                ["Consumer/Consumer.fs:2", "Consumer/Consumer.fs:3"],
                consumer.GetProperty("samples").EnumerateArray().Select(sample =>
                    $"{sample.GetProperty("path").GetString()}:{sample.GetProperty("line").GetInt32()}")
                    .ToArray());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.Equal(1, coverage.GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(0, coverage.GetProperty("dependentsExcluded").GetInt32());
            Assert.Equal(0, coverage.GetProperty("dependentsFailed").GetInt32());
            Assert.Equal(0, coverage.GetProperty("dependentsPending").GetInt32());
            Assert.True(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.DoesNotContain("fsharp_references_", response.GetProperty("partialReason")
                .GetString());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal(1, sourceReads["Library/Library.fs"]);
            Assert.Equal(1, sourceReads["Consumer/Consumer.fs"]);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesDiscoverPropertyExpandedWorkspaceDependentsFromPinnedAuthority()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-evaluated-edge").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", """
                module Library
                let value = 1
                let local = value
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DependencyDir>../Library</DependencyDir>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="$(DependencyDir)/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs",
                "module Consumer\nlet externalUse = Library.value\n");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000)));

            Assert.Equal(2, response.GetProperty("totalReferences").GetInt32());
            Assert.False(response.TryGetProperty("totalIsLowerBound", out _));
            JsonElement consumer = Assert.Single(response.GetProperty("groups").EnumerateArray(),
                group => group.GetProperty("project").GetString() ==
                         "Consumer/Consumer.fsproj");
            Assert.Equal(1, consumer.GetProperty("count").GetInt32());
            Assert.Equal("Consumer/Consumer.fs", Assert.Single(consumer.GetProperty("samples")
                .EnumerateArray()).GetProperty("path").GetString());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.Equal(1, coverage.GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(2, coverage.GetProperty("potentialConsumers").GetInt32());
            Assert.Equal(2, coverage.GetProperty("potentialConsumersEvaluated").GetInt32());
            Assert.Equal(0, coverage.GetProperty("potentialConsumersUnevaluated").GetInt32());
            Assert.True(coverage.GetProperty("workspaceComplete").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesConsumerPositionIncludesDistinctDeclaringProjectUses()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-declaring-project").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", """
                module Library
                let value = 1
                let local = value
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs",
                "module Consumer\nlet externalUse = Library.value\n");

            using var fixture = Fixture.Create(root);
            JsonElement declaration = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000)));
            JsonElement consumer = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Consumer/Consumer.fs", line: 2, column: 27, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000)));

            Assert.Equal(2, declaration.GetProperty("totalReferences").GetInt32());
            Assert.Equal(declaration.GetProperty("totalReferences").GetInt32(),
                consumer.GetProperty("totalReferences").GetInt32());
            Assert.False(consumer.TryGetProperty("totalIsLowerBound", out _));
            JsonElement declaringGroup = Assert.Single(consumer.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                                           "Library/Library.fsproj");
            Assert.Equal(1, declaringGroup.GetProperty("count").GetInt32());
            Assert.Equal("Library/Library.fsproj", consumer.GetProperty("coverage")
                .GetProperty("declaringProject").GetString());
            Assert.Equal("scanned", consumer.GetProperty("coverage")
                .GetProperty("declaringProjectStatus").GetString());
            Assert.True(consumer.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesConsumerPositionAttributesChangedDeclaringBinary()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-declaring-binary-snapshot").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Library.fs" />
                    <Reference Include="CodeNav.Core">
                      <HintPath>../Lib/CodeNav.Core.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Library/Library.fs", """
                module Library
                let value = 1
                let local = value
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs",
                "module Consumer\nlet externalUse = Library.value\n");
            string binary = Path.Combine(root, "Lib", "CodeNav.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(typeof(SemanticService).Assembly.Location, binary);
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(binary);

            using var fixture = Fixture.Create(root);
            int completedChecks = 0;
            fixture.Semantic.FSharpSemanticCheckCompletedForTest = _ =>
            {
                completedChecks++;
                if (completedChecks != 2) return;
                using var stream = new FileStream(binary, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.Read);
                stream.Position = 32;
                int original = stream.ReadByte();
                Assert.True(original >= 0);
                stream.Position = 32;
                stream.WriteByte((byte)(original ^ 1));
                File.SetLastWriteTimeUtc(binary, originalWriteTime);
            };

            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Consumer/Consumer.fs", line: 2, column: 27, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            JsonElement rootGroup = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                                           "Consumer/Consumer.fsproj");
            Assert.Equal("scanned", rootGroup.GetProperty("status").GetString());
            Assert.Equal(1, rootGroup.GetProperty("count").GetInt32());
            JsonElement declaringGroup = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                                           "Library/Library.fsproj");
            Assert.Equal("failed", declaringGroup.GetProperty("status").GetString());
            Assert.Equal("fsharp_semantic_reference_changed",
                declaringGroup.GetProperty("reason").GetString());
            Assert.Equal(0, declaringGroup.GetProperty("count").GetInt32());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.Equal("Library/Library.fsproj",
                coverage.GetProperty("declaringProject").GetString());
            Assert.Equal("failed",
                coverage.GetProperty("declaringProjectStatus").GetString());
            Assert.Equal("fsharp_semantic_reference_changed",
                coverage.GetProperty("declaringProjectReason").GetString());
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Contains("fsharp_workspace_declaring_project_failed",
                response.GetProperty("partialReason").GetString());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesDiscloseIncompleteEvaluatedDependentDiscovery()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-discovery-partial").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            WriteProject(root, "Broken/Broken.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Broken.fs" />
                    <ProjectReference Include="../Library/Library.fsproj"
                                      PrivateAssets="all" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Broken/Broken.fs",
                "module Broken\nlet result = Library.value\n");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000)));

            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Contains("fsharp_workspace_dependent_discovery_incomplete",
                response.GetProperty("partialReason").GetString());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Equal(2, coverage.GetProperty("potentialConsumers").GetInt32());
            Assert.Equal(1, coverage.GetProperty("potentialConsumersEvaluated").GetInt32());
            Assert.Equal(1, coverage.GetProperty("potentialConsumersUnevaluated").GetInt32());
            Assert.Equal(1, coverage.GetProperty("discoveryFailedByReason")
                .GetProperty("fsharp_semantic_project_reference_metadata_unsupported")
                .GetInt32());
            JsonElement discoveryFailure = Assert.Single(coverage
                .GetProperty("discoveryFailed").EnumerateArray());
            Assert.Equal("Broken/Broken.fsproj",
                discoveryFailure.GetProperty("project").GetString());
            Assert.Equal("fsharp_semantic_project_reference_metadata_unsupported",
                discoveryFailure.GetProperty("reason").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesShareTheExistingSourceBudgetAcrossWorkspaceDependents()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-shared-budget").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            foreach (string consumer in new[] { "A_First", "B_Second" })
            {
                WriteProject(root, $"{consumer}/{consumer}.fsproj", $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                      <ItemGroup>
                        <Compile Include="{{consumer}}.fs" />
                        <ProjectReference Include="../Library/Library.fsproj" />
                      </ItemGroup>
                    </Project>
                    """);
                WriteProject(root, $"{consumer}/{consumer}.fs",
                    $"module {consumer}\nlet result = Library.value\n");
            }

            using var fixture = Fixture.Create(root);
            fixture.Semantic.FSharpSemanticSourceFilesLimitForTest = 2;
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.Equal(2, coverage.GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsFailed").GetInt32());
            Assert.Equal(0, coverage.GetProperty("dependentsPending").GetInt32());
            Assert.Contains("fsharp_workspace_dependent_failed",
                response.GetProperty("partialReason").GetString());
            JsonElement failed = Assert.Single(response.GetProperty("groups").EnumerateArray(),
                group => group.GetProperty("status").GetString() == "failed");
            Assert.Equal("fsharp_semantic_source_limit",
                failed.GetProperty("reason").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesScanEveryApplicableDependentTfmAndDeduplicatePhysicalSites()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-multitfm").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net8.0;net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            WriteProject(root, "App/App.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="App.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "App/App.fs",
                "module App\nlet result = Library.value\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                projectPath: "Library/Library.fsproj", targetFramework: "net8.0",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalReferences").GetInt32());
            JsonElement app = Assert.Single(response.GetProperty("groups").EnumerateArray(),
                group => group.GetProperty("project").GetString() == "App/App.fsproj");
            Assert.Equal(1, app.GetProperty("count").GetInt32());
            Assert.Equal(["net10.0", "net8.0"], app.GetProperty("targetFrameworksScanned")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
            JsonElement sample = Assert.Single(app.GetProperty("samples").EnumerateArray());
            Assert.Equal("App/App.fs", sample.GetProperty("path").GetString());
            Assert.True(response.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesKeepSuccessfulTfmEvidenceWhenAnotherDependentTfmFails()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-partial-tfm").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net10.0;net10.0-windows", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            WriteProject(root, "App/App.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="App.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "App/App.fs",
                "module App\nlet result = Library.value\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                projectPath: "Library/Library.fsproj", targetFramework: "net10.0",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            JsonElement app = Assert.Single(response.GetProperty("groups").EnumerateArray(),
                group => group.GetProperty("project").GetString() == "App/App.fsproj");
            Assert.Equal("partial", app.GetProperty("status").GetString());
            Assert.Equal(1, app.GetProperty("count").GetInt32());
            Assert.Equal(["net10.0"], app.GetProperty("targetFrameworksScanned")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
            Assert.Equal("App/App.fs", Assert.Single(app.GetProperty("samples")
                .EnumerateArray()).GetProperty("path").GetString());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.Equal(1, coverage.GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(0, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsFailed").GetInt32());
            Assert.Equal(0, coverage.GetProperty("dependentsPending").GetInt32());
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Contains("fsharp_workspace_dependent_failed",
                response.GetProperty("partialReason").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesScanTransitiveWorkspaceDependents()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-transitive").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            WriteProject(root, "Mid/Mid.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Mid.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Mid/Mid.fs", "module Mid\nlet mid = Library.value\n");
            WriteProject(root, "App/App.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="App.fs" />
                    <ProjectReference Include="../Mid/Mid.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "App/App.fs",
                "module App\nlet result = Library.value + Mid.mid\n");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000)));

            Assert.Equal(2, response.GetProperty("totalReferences").GetInt32());
            var groups = response.GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal(1, Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "Mid/Mid.fsproj")
                .GetProperty("count").GetInt32());
            Assert.Equal(1, Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "App/App.fsproj")
                .GetProperty("count").GetInt32());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.Equal(2, coverage.GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(2, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.True(coverage.GetProperty("workspaceComplete").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesCaptureSharedProjectNodesOnceAcrossDependentClosures()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-shared-node").FullName;
        try
        {
            WriteProject(root, "Base/Base.fsproj", SdkProject("net10.0", "Base.fs"));
            WriteProject(root, "Base/Base.fs", "module Base\nlet value = 1\n");
            WriteProject(root, "Shared/Shared.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Shared.fs" />
                    <ProjectReference Include="../Base/Base.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Shared/Shared.fs",
                "module Shared\nlet shared = Base.value\n");
            foreach (string consumer in new[] { "A_Consumer", "B_Consumer" })
            {
                WriteProject(root, $"{consumer}/{consumer}.fsproj", $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                      <ItemGroup>
                        <Compile Include="{{consumer}}.fs" />
                        <ProjectReference Include="../Shared/Shared.fsproj" />
                      </ItemGroup>
                    </Project>
                    """);
                WriteProject(root, $"{consumer}/{consumer}.fs",
                    $"module {consumer}\nlet result = Base.value + Shared.shared\n");
            }

            using var fixture = Fixture.Create(root);
            var captures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            fixture.Semantic.FSharpSemanticProjectCapturedForTest = (project, tfm) =>
            {
                string key = $"{project}|{tfm}";
                captures[key] = captures.GetValueOrDefault(key) + 1;
            };

            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Base/Base.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(3, response.GetProperty("totalReferences").GetInt32());
            Assert.Equal(3, response.GetProperty("coverage")
                .GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(1, captures["Base/Base.fsproj|net10.0"]);
            Assert.Equal(1, captures["Shared/Shared.fsproj|net10.0"]);
            Assert.Equal(1, captures["A_Consumer/A_Consumer.fsproj|net10.0"]);
            Assert.Equal(1, captures["B_Consumer/B_Consumer.fsproj|net10.0"]);
            Assert.Equal(4, captures.Count);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesContinueAfterDependentFailuresAndAttributeExcludedConsumers()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-partial").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            WriteProject(root, "A_Broken/A_Broken.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Missing.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Z_Healthy/Z_Healthy.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Z_Healthy.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Z_Healthy/Z_Healthy.fs",
                "module Z_Healthy\nlet result = Library.value\n");
            WriteProject(root, "NoTfm/NoTfm.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Compile Include="NoTfm.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "NoTfm/NoTfm.fs",
                "module NoTfm\nlet result = Library.value\n");
            WriteProject(root, "CSharp/CSharp.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Library/Library.fsproj" /></ItemGroup>
                </Project>
                """);
            WriteProject(root, "CSharp/Program.cs", "public static class Program { }\n");
            WriteProject(root, "Binary/Binary.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Binary.fs" />
                    <Reference Include="Library" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Binary/Binary.fs", "module Binary\nlet value = 1\n");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            var groups = response.GetProperty("groups").EnumerateArray().ToList();
            Assert.Equal(1, Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "Z_Healthy/Z_Healthy.fsproj")
                .GetProperty("count").GetInt32());
            Assert.Equal("failed", Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "A_Broken/A_Broken.fsproj")
                .GetProperty("status").GetString());
            JsonElement noTfm = Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "NoTfm/NoTfm.fsproj");
            Assert.Equal("failed", noTfm.GetProperty("status").GetString());
            Assert.Equal("fsharp_type_check_context_unavailable",
                noTfm.GetProperty("reason").GetString());
            Assert.Equal("excluded", Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "CSharp/CSharp.csproj")
                .GetProperty("status").GetString());
            Assert.Equal("excluded", Assert.Single(groups, group =>
                group.GetProperty("project").GetString() == "Binary/Binary.fsproj")
                .GetProperty("status").GetString());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.False(coverage.TryGetProperty("dependentsTotal", out _));
            Assert.Equal(1, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(2, coverage.GetProperty("dependentsExcluded").GetInt32());
            Assert.Equal(2, coverage.GetProperty("dependentsFailed").GetInt32());
            Assert.False(coverage.TryGetProperty("dependentsPending", out _));
            Assert.Equal(5, coverage.GetProperty("potentialConsumers").GetInt32());
            Assert.Equal(4, coverage.GetProperty("potentialConsumersEvaluated").GetInt32());
            Assert.Equal(1, coverage.GetProperty("potentialConsumersUnevaluated").GetInt32());
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Equal(1, coverage.GetProperty("excludedByReason")
                .GetProperty("binary_reference").GetInt32());
            Assert.Equal(1, coverage.GetProperty("excludedByReason")
                .GetProperty("unsupported_language").GetInt32());
            Assert.Equal(1, coverage.GetProperty("failedByReason")
                .GetProperty("fsharp_type_check_context_unavailable").GetInt32());
            string partialReason = response.GetProperty("partialReason").GetString()!;
            Assert.Contains("fsharp_workspace_dependent_failed", partialReason);
            Assert.Contains("fsharp_workspace_dependent_discovery_incomplete", partialReason);
            Assert.Contains("fsharp_workspace_unsupported_boundary", partialReason);
            Assert.Contains("fsharp_workspace_binary_dependents_not_scanned", partialReason);
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesDeadlineKeepsCompletedRootEvidenceAndReportsPendingDependents()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-deadline").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs",
                "module Library\nlet value = 1\nlet local = value\n");
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs",
                "module Consumer\nlet result = Library.value\n");

            using var fixture = Fixture.Create(root);
            JsonElement warm = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Library/Library.fs", 2, 5, timeoutMs: 60_000)));
            Assert.False(warm.TryGetProperty("error", out _), warm.ToString());
            fixture.Semantic.BeforeFSharpSemanticSourceReadForTest = path =>
            {
                if (path == "Consumer/Consumer.fs") Thread.Sleep(4_000);
            };
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 3_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Single(response.GetProperty("groups").EnumerateArray());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.Equal(1, coverage.GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(0, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsPending").GetInt32());
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Contains("fsharp_workspace_deadline",
                response.GetProperty("partialReason").GetString());
            Assert.True(response.GetProperty("retryRecommended").GetBoolean());
            Assert.Contains("larger timeoutMs", response.GetProperty("retryHint").GetString());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesKeepExternalSymbolsRootOnlyAndDiscloseUnscannedDependents()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-external").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", """
                module Core
                let first = System.String.IsNullOrEmpty ""
                let second = System.String.IsNullOrEmpty "x"
                """);
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Core/Core.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs", """
                module Consumer
                let unscanned = System.String.IsNullOrEmpty "dependent"
                """);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Core/Core.fs", line: 2, column: 34, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(2, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.Single(response.GetProperty("groups").EnumerateArray());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.False(coverage.TryGetProperty("dependentsTotal", out _));
            Assert.Equal(0, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.False(coverage.TryGetProperty("dependentsPending", out _));
            Assert.False(coverage.GetProperty("workspaceComplete").GetBoolean());
            Assert.Contains("fsharp_workspace_dependents_not_scanned",
                response.GetProperty("partialReason").GetString());
            Assert.Contains("no authoritative declaration inside workspace F# source",
                response.GetProperty("detail").GetString());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesUsesPinnedIndexedSourcesInsteadOfChangedDiskContent()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-snapshot").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Api.fs", "Use.fs"));
            WriteProject(root, "Core/Api.fs", """
                namespace Snapshot
                module Api =
                    let indexedOnly value = value + 1
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace Snapshot
                module Use =
                    let first = Api.indexedOnly 1
                    let second = Api.indexedOnly 2
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var fixture = Fixture.Start(root, dbPath);
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
                WriteProject(root, "Core/Use.fs", """
                    namespace Snapshot
                    module Use =
                        let first = 1
                        let second = 2
                    """);

            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Core/Use.fs", line: 3, column: 25, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(2, response.GetProperty("totalReferences").GetInt32());
            JsonElement group = Assert.Single(response.GetProperty("groups").EnumerateArray());
            var samples = group.GetProperty("samples").EnumerateArray().ToList();
            Assert.Equal(2, samples.Count);
            Assert.All(samples, sample => Assert.Contains("Api.indexedOnly",
                sample.GetProperty("text").GetString()));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesUsePinnedDependentSourceAfterThatProjectIsCaptured()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-dependent-source-snapshot").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            WriteProject(root, "Consumer/Consumer.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer/Consumer.fs", """
                module Consumer
                let first = Library.value
                let second = Library.value
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var fixture = Fixture.Start(root, dbPath);
            int dependentCaptures = 0;
            fixture.Semantic.FSharpSemanticProjectCapturedForTest = (project, _) =>
            {
                if (project != "Consumer/Consumer.fsproj") return;
                dependentCaptures++;
                WriteProject(root, "Consumer/Consumer.fs", """
                    module Consumer
                    let first = 1
                    let second = 2
                    """);
            };

            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, dependentCaptures);
            Assert.Equal(2, response.GetProperty("totalReferences").GetInt32());
            JsonElement consumer = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                "Consumer/Consumer.fsproj");
            Assert.Equal(2, consumer.GetProperty("count").GetInt32());
            Assert.All(consumer.GetProperty("samples").EnumerateArray(), sample =>
                Assert.Contains("Library.value", sample.GetProperty("text").GetString()));
            Assert.DoesNotContain("Library.value",
                File.ReadAllText(Path.Combine(root, "Consumer", "Consumer.fs")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesAttributeChangedDependentBinaryAndContinueWithOtherGroups()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-dependent-binary-snapshot").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            WriteProject(root, "A_Binary/A_Binary.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="A_Binary.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                    <Reference Include="CodeNav.Core">
                      <HintPath>../Lib/CodeNav.Core.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "A_Binary/A_Binary.fs",
                "module A_Binary\nlet result = Library.value\n");
            WriteProject(root, "Z_Healthy/Z_Healthy.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Z_Healthy.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Z_Healthy/Z_Healthy.fs",
                "module Z_Healthy\nlet result = Library.value\n");
            string binary = Path.Combine(root, "Lib", "CodeNav.Core.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            File.Copy(typeof(SemanticService).Assembly.Location, binary);
            DateTime originalWriteTime = File.GetLastWriteTimeUtc(binary);

            using var fixture = Fixture.Create(root);
            int completedChecks = 0;
            fixture.Semantic.FSharpSemanticCheckCompletedForTest = _ =>
            {
                completedChecks++;
                if (completedChecks != 2) return;
                using var stream = new FileStream(binary, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.Read);
                stream.Position = 32;
                int original = stream.ReadByte();
                Assert.True(original >= 0);
                stream.Position = 32;
                stream.WriteByte((byte)(original ^ 1));
                File.SetLastWriteTimeUtc(binary, originalWriteTime);
            };

            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(1, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("totalIsLowerBound").GetBoolean());
            JsonElement binaryGroup = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                "A_Binary/A_Binary.fsproj");
            Assert.Equal("failed", binaryGroup.GetProperty("status").GetString());
            Assert.Equal("fsharp_semantic_reference_changed",
                binaryGroup.GetProperty("reason").GetString());
            Assert.Equal(0, binaryGroup.GetProperty("count").GetInt32());
            JsonElement healthyGroup = Assert.Single(response.GetProperty("groups")
                .EnumerateArray(), group => group.GetProperty("project").GetString() ==
                "Z_Healthy/Z_Healthy.fsproj");
            Assert.Equal("scanned", healthyGroup.GetProperty("status").GetString());
            Assert.Equal(1, healthyGroup.GetProperty("count").GetInt32());
            JsonElement coverage = response.GetProperty("coverage");
            Assert.Equal(2, coverage.GetProperty("dependentsTotal").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsScanned").GetInt32());
            Assert.Equal(1, coverage.GetProperty("dependentsFailed").GetInt32());
            Assert.Equal(1, coverage.GetProperty("failedByReason")
                .GetProperty("fsharp_semantic_reference_changed").GetInt32());
            Assert.Contains("fsharp_workspace_dependent_failed",
                response.GetProperty("partialReason").GetString());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PairedMigrationReferencesRequireExplicitPhysicalContextAndNeverMergeCompanion()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-context").FullName;
        try
        {
            WriteProject(root, "Paired/Shared.fs", """
                module Shared
                let value = 1
                """);
            WriteProject(root, "Paired/LegacyUse.fs", """
                module LegacyUse
                let legacy = Shared.value
                """);
            WriteProject(root, "Paired/NetUse.fs", """
                module NetUse
                let modern = Shared.value
                """);
            WriteProject(root, "Paired/Project.fsproj", SdkProject("net8.0",
                "Shared.fs", "LegacyUse.fs"));
            WriteProject(root, "Paired/Project.Net.fsproj", SdkProject("net8.0",
                "Shared.fs", "NetUse.fs"));

            using var fixture = Fixture.Create(root);
            JsonElement ambiguous = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Paired/Shared.fs", line: 2, column: 5, mode: "semantic",
                timeoutMs: 60_000)));
            Assert.Equal("fsharp_type_check_context_required",
                ambiguous.GetProperty("error").GetString());
            Assert.Equal(2, ambiguous.GetProperty("fsharpTypeCheckContextsTotal").GetInt32());

            JsonElement legacy = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Paired/Shared.fs", line: 2, column: 5, mode: "semantic",
                projectPath: "Paired/Project.fsproj", targetFramework: "net8.0",
                samplesPerGroup: 10, timeoutMs: 60_000)));
            Assert.Equal(1, legacy.GetProperty("totalReferences").GetInt32());
            JsonElement legacyGroup = Assert.Single(legacy.GetProperty("groups").EnumerateArray());
            JsonElement legacySample = Assert.Single(legacyGroup.GetProperty("samples")
                .EnumerateArray());
            Assert.Equal("Paired/LegacyUse.fs", legacySample.GetProperty("path").GetString());

            JsonElement modern = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Paired/Shared.fs", line: 2, column: 5, mode: "semantic",
                projectPath: "Paired/Project.Net.fsproj", targetFramework: "net8.0",
                samplesPerGroup: 10, timeoutMs: 60_000)));
            Assert.Equal(1, modern.GetProperty("totalReferences").GetInt32());
            JsonElement modernGroup = Assert.Single(modern.GetProperty("groups").EnumerateArray());
            JsonElement modernSample = Assert.Single(modernGroup.GetProperty("samples")
                .EnumerateArray());
            Assert.Equal("Paired/NetUse.fs", modernSample.GetProperty("path").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesHonorPinnedGeneratedAndTestProjectFactsBeforeCounting()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-filters").FullName;
        try
        {
            WriteProject(root, "Core.Tests/Core.Tests.fsproj", SdkProject("net10.0",
                "Api.fs", "Normal.fs", "Generated.g.fs"));
            WriteProject(root, "Core.Tests/Api.fs", "module Api\nlet value = 1\n");
            WriteProject(root, "Core.Tests/Normal.fs",
                "module Normal\nlet normal = Api.value\n");
            WriteProject(root, "Core.Tests/Generated.g.fs",
                "module Generated\nlet generated = Api.value\n");

            using var fixture = Fixture.Create(root);
            using (var queries = fixture.Manager.OpenQueries())
            {
                Assert.True(queries.FileByPath("Core.Tests/Generated.g.fs")!.IsGenerated);
                Assert.True(Assert.Single(queries.ProjectsContaining("Core.Tests/Api.fs")).IsTest);
            }

            JsonElement generatedExcluded = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Core.Tests/Api.fs", line: 2, column: 5, mode: "semantic",
                includeTests: true, includeGenerated: false, samplesPerGroup: 10,
                timeoutMs: 60_000)));
            Assert.Equal(1, generatedExcluded.GetProperty("totalReferences").GetInt32());
            JsonElement excludedGroup = Assert.Single(generatedExcluded.GetProperty("groups")
                .EnumerateArray());
            Assert.True(excludedGroup.GetProperty("isTest").GetBoolean());
            Assert.Equal("Core.Tests/Normal.fs", Assert.Single(excludedGroup
                .GetProperty("samples").EnumerateArray()).GetProperty("path").GetString());

            JsonElement generatedIncluded = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Core.Tests/Api.fs", line: 2, column: 5, mode: "semantic",
                includeTests: true, includeGenerated: true, samplesPerGroup: 10,
                timeoutMs: 60_000)));
            Assert.Equal(2, generatedIncluded.GetProperty("totalReferences").GetInt32());
            Assert.Equal(2, Assert.Single(generatedIncluded.GetProperty("groups")
                .EnumerateArray()).GetProperty("samples").GetArrayLength());

            JsonElement testsExcluded = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Core.Tests/Api.fs", line: 2, column: 5, mode: "semantic",
                includeTests: false, includeGenerated: true, samplesPerGroup: 10,
                timeoutMs: 60_000)));
            Assert.Equal(0, testsExcluded.GetProperty("totalReferences").GetInt32());
            JsonElement emptyGroup = Assert.Single(testsExcluded.GetProperty("groups")
                .EnumerateArray());
            Assert.Equal(0, emptyGroup.GetProperty("count").GetInt32());
            Assert.Empty(emptyGroup.GetProperty("samples").EnumerateArray());
            Assert.Equal("filtered", emptyGroup.GetProperty("status").GetString());
            Assert.Equal("test_project", emptyGroup.GetProperty("reason").GetString());
            Assert.Contains("Test projects were excluded before counting",
                testsExcluded.GetProperty("summary").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesHonorGeneratedAndTestFiltersInWorkspaceDependents()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-dependent-filters").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj",
                SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            WriteProject(root, "Consumer.Tests/Consumer.Tests.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Normal.fs" />
                    <Compile Include="Generated.g.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Consumer.Tests/Normal.fs",
                "module Normal\nlet normal = Library.value\n");
            WriteProject(root, "Consumer.Tests/Generated.g.fs",
                "module Generated\nlet generated = Library.value\n");
            WriteProject(root, "GeneratedOnly.Tests/GeneratedOnly.Tests.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="GeneratedOnly.g.fs" />
                    <ProjectReference Include="../Library/Library.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "GeneratedOnly.Tests/GeneratedOnly.g.fs",
                "module GeneratedOnly\nlet generated = Library.value\n");

            using var fixture = Fixture.Create(root);
            using (var queries = fixture.Manager.OpenQueries())
            {
                Assert.True(queries.FileByPath("Consumer.Tests/Generated.g.fs")!.IsGenerated);
                Assert.True(Assert.Single(queries.ProjectsContaining(
                    "Consumer.Tests/Normal.fs")).IsTest);
            }

            JsonElement generatedExcluded = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                includeTests: true, includeGenerated: false, samplesPerGroup: 10,
                timeoutMs: 60_000)));
            Assert.Equal(1, generatedExcluded.GetProperty("totalReferences").GetInt32());
            JsonElement filteredDependent = Assert.Single(generatedExcluded
                .GetProperty("groups").EnumerateArray(), group =>
                group.GetProperty("project").GetString() ==
                "Consumer.Tests/Consumer.Tests.fsproj");
            Assert.True(filteredDependent.GetProperty("isTest").GetBoolean());
            Assert.Equal(1, filteredDependent.GetProperty("count").GetInt32());
            Assert.Equal("Consumer.Tests/Normal.fs", Assert.Single(filteredDependent
                .GetProperty("samples").EnumerateArray()).GetProperty("path").GetString());
            JsonElement generatedOnlyFiltered = Assert.Single(generatedExcluded
                .GetProperty("groups").EnumerateArray(), group =>
                group.GetProperty("project").GetString() ==
                "GeneratedOnly.Tests/GeneratedOnly.Tests.fsproj");
            Assert.Equal(0, generatedOnlyFiltered.GetProperty("count").GetInt32());
            Assert.Equal("filtered", generatedOnlyFiltered.GetProperty("status").GetString());
            Assert.Equal("generated_files",
                generatedOnlyFiltered.GetProperty("reason").GetString());
            Assert.Contains("Generated files were excluded before counting",
                generatedExcluded.GetProperty("summary").GetString());

            JsonElement generatedIncluded = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                includeTests: true, includeGenerated: true, samplesPerGroup: 10,
                timeoutMs: 60_000)));
            Assert.Equal(3, generatedIncluded.GetProperty("totalReferences").GetInt32());
            JsonElement unfilteredDependent = Assert.Single(generatedIncluded
                .GetProperty("groups").EnumerateArray(), group =>
                group.GetProperty("project").GetString() ==
                "Consumer.Tests/Consumer.Tests.fsproj");
            Assert.Equal(2, unfilteredDependent.GetProperty("count").GetInt32());

            JsonElement testsExcluded = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                includeTests: false, includeGenerated: true, samplesPerGroup: 10,
                timeoutMs: 60_000)));
            Assert.Equal(0, testsExcluded.GetProperty("totalReferences").GetInt32());
            JsonElement excludedDependent = Assert.Single(testsExcluded
                .GetProperty("groups").EnumerateArray(), group =>
                group.GetProperty("project").GetString() ==
                "Consumer.Tests/Consumer.Tests.fsproj");
            Assert.Equal(0, excludedDependent.GetProperty("count").GetInt32());
            Assert.Empty(excludedDependent.GetProperty("samples").EnumerateArray());
            Assert.Equal("filtered", excludedDependent.GetProperty("status").GetString());
            Assert.Equal("test_project", excludedDependent.GetProperty("reason").GetString());
            Assert.Contains("Test projects were excluded before counting",
                testsExcluded.GetProperty("summary").GetString());
            Assert.Equal(2, testsExcluded.GetProperty("coverage")
                .GetProperty("dependentsExcluded").GetInt32());
            Assert.Equal(2, testsExcluded.GetProperty("coverage")
                .GetProperty("excludedByReason").GetProperty("test_project").GetInt32());
            Assert.True(testsExcluded.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesTrimOnlySamplesWithoutChangingSelectedProjectCount()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-budget").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            string uses = string.Join('\n', Enumerable.Range(1, 20).Select(index =>
                $"let use{index:D2} = value + {index} // {new string('x', 320)}"));
            WriteProject(root, "Core/Core.fs", $"module Core\nlet value = 1\n{uses}\n");

            using var fixture = Fixture.Create(root);
            fixture.Tools.TestOnlyReferencesResponseMaxBytes = 3_000;
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Core/Core.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(20, response.GetProperty("totalReferences").GetInt32());
            Assert.Equal(20, Assert.Single(response.GetProperty("groups").EnumerateArray())
                .GetProperty("count").GetInt32());
            JsonElement sampleCoverage = response.GetProperty("sampleCoverage");
            Assert.Equal(10, sampleCoverage.GetProperty("selected").GetInt32());
            Assert.True(sampleCoverage.GetProperty("returned").GetInt32() < 10);
            Assert.False(sampleCoverage.GetProperty("complete").GetBoolean());
            Assert.True(Json.Utf8Bytes(raw) <= 3_000, raw);
            Assert.True(Json.Utf8Bytes(raw) <= Json.HardBudgetBytes, raw);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesTrimProjectGroupsWithoutLosingExactWorkspaceCounts()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-group-budget").FullName;
        try
        {
            WriteProject(root, "Library/Library.fsproj", SdkProject("net10.0", "Library.fs"));
            WriteProject(root, "Library/Library.fs", "module Library\nlet value = 1\n");
            for (int index = 1; index <= 12; index++)
            {
                string project = $"Consumer_{index:D2}_With_A_Deliberately_Long_Project_Name";
                WriteProject(root, $"{project}/{project}.fsproj", $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                      <ItemGroup>
                        <Compile Include="Consumer.fs" />
                        <ProjectReference Include="../Library/Library.fsproj" />
                      </ItemGroup>
                    </Project>
                    """);
                WriteProject(root, $"{project}/Consumer.fs",
                    $"module Consumer_{index:D2}\nlet result = Library.value\n");
            }

            using var fixture = Fixture.Create(root);
            fixture.Tools.TestOnlyReferencesResponseMaxBytes = 2_500;
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Library/Library.fs", line: 2, column: 5, mode: "semantic",
                samplesPerGroup: 10, timeoutMs: 60_000));
            JsonElement response = Parse(raw);

            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.Equal(12, response.GetProperty("totalReferences").GetInt32());
            Assert.True(response.GetProperty("coverage")
                .GetProperty("workspaceComplete").GetBoolean());
            Assert.Equal(12, response.GetProperty("coverage")
                .GetProperty("dependentsScanned").GetInt32());
            JsonElement groupCoverage = response.GetProperty("groupCoverage");
            Assert.Equal(13, groupCoverage.GetProperty("selected").GetInt32());
            int returned = groupCoverage.GetProperty("returned").GetInt32();
            Assert.True(returned < 13);
            Assert.Equal(returned, response.GetProperty("groups").GetArrayLength());
            Assert.False(groupCoverage.GetProperty("complete").GetBoolean());
            Assert.Equal(NoteIds.FSharpReferenceGroupsByteBudget,
                Assert.Single(groupCoverage.GetProperty("reasons").EnumerateArray())
                    .GetProperty("noteId").GetString());
            JsonElement sampleCoverage = response.GetProperty("sampleCoverage");
            int visibleSamples = response.GetProperty("groups").EnumerateArray()
                .Sum(group => group.GetProperty("samples").GetArrayLength());
            Assert.Equal(visibleSamples, sampleCoverage.GetProperty("returned").GetInt32());
            Assert.True(Json.Utf8Bytes(raw) <= 2_500, raw);
            Assert.True(Json.Utf8Bytes(raw) <= Json.HardBudgetBytes, raw);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReferencesReuseFSharpSelectorAndFilterFailureContracts()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-references-contract").FullName;
        try
        {
            WriteProject(root, "Core/Core.fsproj", SdkProject("net10.0", "Core.fs"));
            WriteProject(root, "Core/Core.fs", """
                module Core
                let value = 1
                // no symbol here
                """);
            WriteProject(root, "CSharp/Library.cs", """
                namespace MixedSelectors;
                public sealed class HandleTarget { }
                public sealed class Consumer
                {
                    private readonly HandleTarget _value = new();
                }
                """);

            using var fixture = Fixture.Create(root);
            JsonElement noSymbol = Parse(CallSemantic(() => fixture.Tools.References(
                path: "Core/Core.fs", line: 3, column: 4, mode: "semantic",
                timeoutMs: 60_000)));
            Assert.Equal("fsharp_symbol_not_resolved", noSymbol.GetProperty("error").GetString());
            Assert.False(noSymbol.GetProperty("found").GetBoolean());
            Assert.False(noSymbol.TryGetProperty("totalReferences", out _));
            Assert.False(noSymbol.TryGetProperty("groups", out _));

            JsonElement indexed = Parse(fixture.Tools.References(
                path: "Core/Core.fs", line: 2, column: 5, mode: "indexed"));
            Assert.Equal("fsharp_indexed_symbols_unavailable",
                indexed.GetProperty("error").GetString());

            JsonElement lineOnly = Parse(fixture.Tools.References(
                path: "Core/Core.fs", line: 2, mode: "semantic"));
            Assert.Equal("fsharp_semantic_position_required",
                lineOnly.GetProperty("error").GetString());

            foreach ((string Field, Func<string> Call) filterCase in
                     new (string Field, Func<string> Call)[]
                     {
                         ("usageKinds", () => fixture.Tools.References(
                             path: "Core/Core.fs", line: 2, column: 5,
                             usageKinds: "call")),
                         ("publicConsumersOnly", () => fixture.Tools.References(
                             path: "Core/Core.fs", line: 2, column: 5,
                             publicConsumersOnly: true)),
                         ("pathGlob", () => fixture.Tools.References(
                             path: "Core/Core.fs", line: 2, column: 5,
                             pathGlob: "Core/**")),
                         ("excludePath", () => fixture.Tools.References(
                             path: "Core/Core.fs", line: 2, column: 5,
                             excludePath: "Generated/**")),
                     })
            {
                JsonElement refusal = Parse(filterCase.Call());
                Assert.Equal("bad_request", refusal.GetProperty("error").GetString());
                Assert.Equal(filterCase.Field, refusal.GetProperty("field").GetString());
                Assert.Equal("incompatible_filter", refusal.GetProperty("reason").GetString());
                Assert.Equal(filterCase.Field == "publicConsumersOnly"
                        ? "false"
                        : $"omit {filterCase.Field}",
                    refusal.GetProperty("expected").GetString());
                Assert.Equal("indexed",
                    refusal.GetProperty("meta").GetProperty("confidence").GetString());
            }

            JsonElement fsharpHit = Assert.Single(Parse(fixture.Tools.SearchSymbol(
                    "value", kinds: "value", match: "exact", pathGlob: "Core/Core.fs"))
                .GetProperty("symbols").EnumerateArray());
            JsonElement handleRefusal = Parse(fixture.Tools.References(
                symbolId: fsharpHit.GetProperty("symbolId").GetString()));
            Assert.Equal("fsharp_semantic_position_required",
                handleRefusal.GetProperty("error").GetString());
            Assert.Equal("references", handleRefusal.GetProperty("operation").GetString());

            JsonElement documentationIdRefusal = Parse(fixture.Tools.References(
                path: "Core/Core.fs", line: 2, column: 5,
                documentationCommentId: "T:Core"));
            Assert.Equal("bad_request", documentationIdRefusal.GetProperty("error").GetString());
            Assert.Equal("documentationCommentId",
                documentationIdRefusal.GetProperty("field").GetString());

            JsonElement csharpHit = Assert.Single(Parse(fixture.Tools.SearchSymbol(
                    "HandleTarget", kinds: "class", match: "exact"))
                .GetProperty("symbols").EnumerateArray());
            string csharpHandle = csharpHit.GetProperty("symbolId").GetString()!;
            JsonElement handleOnly = Parse(fixture.Tools.References(
                symbolId: csharpHandle, mode: "indexed"));
            JsonElement handleWithFSharpPath = Parse(fixture.Tools.References(
                path: "Core/Core.fs", line: 2, column: 5,
                symbolId: csharpHandle, mode: "indexed"));
            Assert.False(handleOnly.TryGetProperty("error", out _));
            Assert.False(handleWithFSharpPath.TryGetProperty("error", out _));
            Assert.Equal(handleOnly.GetProperty("totalCandidates").GetInt32(),
                handleWithFSharpPath.GetProperty("totalCandidates").GetInt32());
        }
        finally
        {
            Cleanup(root);
        }
    }
}
