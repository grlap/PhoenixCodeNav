using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// F# support starts with indexed source/project topology and an FCS syntax outline. Operations that
/// require compiler semantics still fail explicitly instead of returning false-complete empty results.
/// These fixtures stay tiny so the language contract remains part of the fast unit-test loop.
/// </summary>
public class FSharpTierATests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("Library.fs")]
    [InlineData("Prelude.fsi")]
    [InlineData("Scratch.fsx")]
    [InlineData("Core.fsproj")]
    public void WatcherRecognizesFSharpInputs(string path)
    {
        Assert.True(WorkspaceWatcher.IsWatchedFile(path));
    }

    [Fact]
    public void SemanticCoverageUsesOneStableCauseClassifierForResponsesAndTelemetry()
    {
        var failed = new ClusterCoverage(1, 2, [], ["Broken"], true);
        Assert.Equal("project_load_failed", SemanticCoverageReasons.Primary(failed));

        var gap = new ClusterCoverage(1, 2, [], [], true);
        Assert.Equal("project_coverage_incomplete", SemanticCoverageReasons.Primary(gap));

        var unsupported = new ClusterCoverage(1, 2, ["FSharp"], ["Broken"], true);
        Assert.Equal("unsupported_language_projects_skipped",
            SemanticCoverageReasons.Primary(unsupported, candidateProjectsSkipped: true));

        Assert.Equal("candidate_cluster_bounded",
            SemanticCoverageReasons.Primary(gap with { LoadedProjects = 2 },
                candidateProjectsSkipped: true));
    }

    [Fact]
    public void ScannerAndParserRecognizeFSharpInputsWithoutInventingDefaultCompileItems()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-scan").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Core"));
            File.WriteAllText(Path.Combine(root, "Core", "Prelude.fsi"), "module Core.Prelude");
            File.WriteAllText(Path.Combine(root, "Core", "Library.fs"), "module Core.Library");
            File.WriteAllText(Path.Combine(root, "Core", "Scratch.fsx"), "printfn \"scratch\"");
            Directory.CreateDirectory(Path.Combine(root, "Build"));
            File.WriteAllText(Path.Combine(root, "Build", "PackagePaths.props"),
                "<Project><PropertyGroup><Packages>lib</Packages></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Build", "Custom.targets"),
                "<Project />");
            File.WriteAllText(Path.Combine(root, "Core", "Core.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Prelude.fsi" />
                    <Compile Include="Library.fs" />
                    <ProjectReference Include="../Shared/Shared.csproj" />
                    <Reference Include="Legacy"><HintPath>../lib/Legacy.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);

            ScanResult scan = WorkspaceScanner.Scan(root);
            Assert.Equal(3, scan.FsFiles.Count);
            Assert.Contains(scan.ProjectFiles, file => file.RelPath == "Core/Core.fsproj");
            Assert.Contains(scan.ConfigFiles, file => file.RelPath == "Build/PackagePaths.props");
            Assert.Contains(scan.ConfigFiles, file => file.RelPath == "Build/Custom.targets");

            ParsedProject project = ProjectFileParser.Parse(root, "Core/Core.fsproj");
            Assert.Equal("fs", project.Language);
            Assert.Equal("sdk", project.Style);
            Assert.False(project.DefaultCompileItems);
            Assert.Contains("Shared/Shared.csproj", project.ProjectRefRelPaths);
            Assert.Contains(project.CompileIncludeGlobs!, glob => glob.Include == "Core/Prelude.fsi");
            Assert.Contains(project.CompileIncludeGlobs!, glob => glob.Include == "Core/Library.fs");
            Assert.Contains(project.AssemblyRefs, reference =>
                reference.Assembly == "Legacy" && reference.HintPath == "lib/Legacy.dll");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ColdFSharpIndexingUsesBoundedWriterBatches()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-batches").FullName;
        try
        {
            string sourceDirectory = Path.Combine(root, "FSharp");
            Directory.CreateDirectory(sourceDirectory);
            for (int i = 0; i < 3; i++)
            {
                File.WriteAllText(Path.Combine(sourceDirectory, $"File{i:D4}.fs"),
                    $"module Batch.File{i}\nlet value = {i}\n");
            }

            var progress = new List<string>();
            Assert.Equal(2000, IndexBuilder.SourceWriteBatchSize);
            BuildResult result = IndexBuilder.BuildWithSourceBatchSizeForTest(
                root, sourceWriteBatchSize: 2, progress: progress.Add);
            Assert.Equal(3, result.FsFiles);
            Assert.Contains(progress, message => message.Contains(
                "F# files in 2 writer batches", StringComparison.Ordinal));
            using var queries = new IndexQueries(IndexBuilder.DefaultDbPath(root));
            Assert.Equal(3, queries.Overview().FsFiles);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ColdFSharpReadBatchesBoundAggregateMemoryAndIsolateOversizedFiles()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-memory-batches").FullName;
        try
        {
            string sourceDirectory = Path.Combine(root, "FSharp");
            Directory.CreateDirectory(sourceDirectory);
            for (int i = 0; i < 3; i++)
            {
                File.WriteAllText(Path.Combine(sourceDirectory, $"Small{i}.fs"),
                    $"module Memory.Small{i}\n// " + new string('x', 4_000));
            }
            File.WriteAllText(Path.Combine(sourceDirectory, "Oversized.fs"),
                "module Memory.Oversized\n// " + new string('y', 40_000));

            const long budgetBytes = 30_000;
            var batches = new List<(long Bytes, int Count)>();
            var hooks = new FSharpPipelineTestHooks(budgetBytes,
                (bytes, count) => batches.Add((bytes, count)));

            BuildResult result = IndexBuilder.BuildWithSourceBatchSizeForTest(
                root, sourceWriteBatchSize: 100, fSharpPipelineTestHooks: hooks);

            Assert.Equal(4, result.FsFiles);
            Assert.NotEmpty(batches);
            Assert.DoesNotContain(batches, batch => batch.Count == 0);
            Assert.Contains(batches, batch => batch.Count > 1);
            Assert.Contains(batches, batch =>
                batch.Count == 1 && batch.Bytes > budgetBytes);
            Assert.All(batches, batch => Assert.True(
                batch.Count == 1 || batch.Bytes <= budgetBytes,
                $"F# batch retained {batch.Bytes} bytes across {batch.Count} files " +
                $"under a {budgetBytes}-byte aggregate budget"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ColdFSharpWriterFailureUnwindsAfterAllReadersHaveJoined()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-writer-failure").FullName;
        try
        {
            string sourceDirectory = Path.Combine(root, "FSharp");
            Directory.CreateDirectory(sourceDirectory);
            for (int i = 0; i < 32; i++)
            {
                File.WriteAllText(Path.Combine(sourceDirectory, $"File{i:D4}.fs"),
                    $"module Failure.File{i}\n// " + new string('z', 1_000));
            }

            int readBatches = 0;
            int persistCalls = 0;
            var activeReaderSamples = new List<int>();
            var hooks = new FSharpPipelineTestHooks(12_000,
                (_, _) => readBatches++,
                (_, activeReaders) =>
                {
                    persistCalls++;
                    activeReaderSamples.Add(activeReaders);
                    throw new InvalidOperationException("injected F# writer failure");
                });

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                IndexBuilder.BuildWithSourceBatchSizeForTest(root,
                    sourceWriteBatchSize: 2, fSharpPipelineTestHooks: hooks));
            stopwatch.Stop();

            Assert.Equal("injected F# writer failure", error.Message);
            Assert.Equal(1, readBatches);
            Assert.Equal(1, persistCalls);
            Assert.Equal(0, Assert.Single(activeReaderSamples));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"writer failure took {stopwatch.Elapsed} to unwind");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ExplicitFSharpDefaultCompileItemsAreHonoredInColdAndDeltaOwnership()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-default-items").FullName;
        try
        {
            WriteProject(root, "Core", "Core.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                  </PropertyGroup>
                </Project>
                """,
                ("Library.fs", "module Core.Library\nlet coldDefaultMarker = 1\n"),
                ("Prelude.fsi", "module Core.Prelude\n"),
                ("Scratch.fsx", "printfn \"script\"\n"));

            ParsedProject parsed = ProjectFileParser.Parse(root, "Core/Core.fsproj");
            Assert.True(parsed.DefaultCompileItems);
            ParsedProject shape = ProjectFileParser.ParseCompileShape("Core/Core.fsproj",
                File.ReadAllBytes(Path.Combine(root, "Core", "Core.fsproj")));
            Assert.True(shape.DefaultCompileItems);
            Assert.True(shape.CompileOwnershipComplete);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Equal("fs", Assert.Single(queries.ProjectsContaining("Core/Library.fs")).Language);
                Assert.Empty(queries.ProjectsContaining("Core/Prelude.fsi"));
                Assert.Empty(queries.ProjectsContaining("Core/Scratch.fsx"));
            }

            File.WriteAllText(Path.Combine(root, "Core", "Added.fs"),
                "module Core.Added\nlet deltaDefaultMarker = 2\n");
            File.WriteAllText(Path.Combine(root, "Core", "Added.fsi"), "module Core.Added\n");
            File.WriteAllText(Path.Combine(root, "Core", "Added.fsx"), "printfn \"added\"\n");
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                    ["Core/Added.fs", "Core/Added.fsi", "Core/Added.fsx"]);
                Assert.Equal(3, refresh.AddedFiles);
            }
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Equal("fs", Assert.Single(queries.ProjectsContaining("Core/Added.fs")).Language);
                Assert.Empty(queries.ProjectsContaining("Core/Added.fsi"));
                Assert.Empty(queries.ProjectsContaining("Core/Added.fsx"));
            }

            string projectPath = Path.Combine(root, "Core", "Core.fsproj");
            File.WriteAllText(projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Target Name="LateDefaults">
                    <PropertyGroup>
                      <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                    </PropertyGroup>
                  </Target>
                </Project>
                """);
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root, ["Core/Core.fsproj"]);
                Assert.True(refresh.ProjectsRefreshed);
            }
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Empty(queries.ProjectsContaining("Core/Library.fs"));
                Assert.Empty(queries.ProjectsContaining("Core/Added.fs"));
            }

            File.WriteAllText(projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                  </PropertyGroup>
                </Project>
                """);
            using (var store = new IndexStore(dbPath, createNew: false))
                DeltaRefresher.Refresh(store, root, ["Core/Core.fsproj"]);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Single(queries.ProjectsContaining("Core/Library.fs"));
                Assert.Single(queries.ProjectsContaining("Core/Added.fs"));
            }

            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\">");
            using (var store = new IndexStore(dbPath, createNew: false))
                DeltaRefresher.Refresh(store, root, ["Core/Core.fsproj"]);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Empty(queries.ProjectsContaining("Core/Library.fs"));
                Assert.Empty(queries.ProjectsContaining("Core/Added.fs"));
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RootLevelExplicitFSharpDefaultsOwnRootSourcesInColdAndDeltaPaths()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-root-default-items").FullName;
        try
        {
            WriteProject(root, "", "Root.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                  </PropertyGroup>
                </Project>
                """,
                ("Library.fs", "module Root.Library\nlet coldRootMarker = 1\n"),
                ("Prelude.fsi", "module Root.Prelude\n"),
                ("Scratch.fsx", "printfn \"root script\"\n"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Equal("fs", Assert.Single(
                    queries.ProjectsContaining("Library.fs")).Language);
                Assert.Empty(queries.ProjectsContaining("Prelude.fsi"));
                Assert.Empty(queries.ProjectsContaining("Scratch.fsx"));
            }

            File.WriteAllText(Path.Combine(root, "Added.fs"),
                "module Root.Added\nlet deltaRootMarker = 2\n");
            File.WriteAllText(Path.Combine(root, "Added.fsi"), "module Root.Added\n");
            File.WriteAllText(Path.Combine(root, "Added.fsx"), "printfn \"added\"\n");
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                    ["Added.fs", "Added.fsi", "Added.fsx"]);
                Assert.Equal(3, refresh.AddedFiles);
            }
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Equal("fs", Assert.Single(
                    queries.ProjectsContaining("Added.fs")).Language);
                Assert.Empty(queries.ProjectsContaining("Added.fsi"));
                Assert.Empty(queries.ProjectsContaining("Added.fsx"));
            }
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("<EnableDefaultCompileItems Condition=\"'$(Configuration)' == 'Debug'\">true</EnableDefaultCompileItems>")]
    [InlineData("<EnableDefaultCompileItems>$(EnableFSharpDefaults)</EnableDefaultCompileItems>")]
    public void FSharpDefaultCompileItemsRequireAnUnconditionalLiteralTrue(string property)
    {
        byte[] xml = System.Text.Encoding.UTF8.GetBytes(
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{property}</PropertyGroup></Project>");
        ParsedProject shape = ProjectFileParser.ParseCompileShape("Core/Core.fsproj", xml);
        Assert.False(shape.DefaultCompileItems);
        Assert.False(shape.CompileOwnershipComplete);
    }

    [Fact]
    public void NonAuthoritativeFSharpProjectShapesFailClosedInColdOwnership()
    {
        var cases = new[]
        {
            (Name: "target", Xml:
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Target Name="LateDefaults">
                    <PropertyGroup>
                      <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
                    </PropertyGroup>
                  </Target>
                </Project>
                """),
            (Name: "malformed", Xml: "<Project Sdk=\"Microsoft.NET.Sdk\">"),
        };

        foreach (var testCase in cases)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(testCase.Xml);
            ParsedProject parsed = ProjectFileParser.ParseSnapshot("Core/Core.fsproj", bytes);
            Assert.False(parsed.DefaultCompileItems);
            ParsedProject shape = ProjectFileParser.ParseCompileShape("Core/Core.fsproj", bytes);
            Assert.False(shape.DefaultCompileItems);
            Assert.False(shape.CompileOwnershipComplete);

            string root = Directory.CreateTempSubdirectory(
                $"codenav-fsharp-fail-closed-{testCase.Name}").FullName;
            try
            {
                WriteProject(root, "Core", "Core.fsproj", testCase.Xml,
                    ("Library.fs", "module Core.Library\nlet mustNotBeOwned = 1\n"));
                string dbPath = IndexBuilder.DefaultDbPath(root);
                IndexBuilder.Build(root, dbPath);
                using var queries = new IndexQueries(dbPath);
                Assert.Empty(queries.ProjectsContaining("Core/Library.fs"));

                string addedPath = Path.Combine(root, "Core", "Added.fs");
                File.WriteAllText(addedPath, "module Core.Added\nlet mustRemainUnowned = 2\n");
                using (var store = new IndexStore(dbPath, createNew: false))
                {
                    RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                        ["Core/Added.fs"]);
                    Assert.Equal(1, refresh.AddedFiles);
                    Assert.True(refresh.ProjectsRefreshed);
                }
                using var afterDelta = new IndexQueries(dbPath);
                Assert.Empty(afterDelta.ProjectsContaining("Core/Added.fs"));
            }
            finally { Cleanup(root); }
        }
    }

    [Fact]
    public void ExactCompileIncludeCannotCrossSourceLanguages()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-cross-language-owner").FullName;
        try
        {
            WriteProject(root, "Owner", "Owner.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
                  <ItemGroup><Compile Include="../FSharp/Library.fs" /></ItemGroup>
                </Project>
                """);
            Directory.CreateDirectory(Path.Combine(root, "FSharp"));
            File.WriteAllText(Path.Combine(root, "FSharp", "Library.fs"), "module FSharp.Library\n");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var queries = new IndexQueries(dbPath);
            Assert.Empty(queries.ProjectsContaining("FSharp/Library.fs"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task BuildIndexesFSharpTextOwnershipAndCrossLanguageGraphWithHonestToolGates()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-tier-a").FullName;
        try
        {
            WriteMixedWorkspace(root);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            BuildResult build = IndexBuilder.Build(root, dbPath);
            Assert.Equal(2, build.FsFiles);
            Assert.Equal(2, build.CsFiles);

            using (var q = new IndexQueries(dbPath))
            {
                OverviewStats overview = q.Overview();
                Assert.Equal(2, overview.FsFiles);
                Assert.Equal(1, overview.FSharpProjects);
                Assert.Equal(2, overview.CSharpProjects);

                ProjectRow core = q.ProjectByName("Streams.Core")!;
                Assert.Equal("fs", core.Language);
                ProjectRow owner = Assert.Single(q.ProjectsContaining("Core/Library.fs"));
                Assert.Equal("Streams.Core", owner.Name);
                Assert.Equal("fs", owner.Language);
                Assert.Empty(q.ProjectsContaining("Core/NotCompiled.fsx"));

                List<GraphEdge> wrapperEdges = q.ProjectGraph("Streams.CSharp", 1, "downstream");
                Assert.Contains(wrapperEdges, edge =>
                    edge.FromProject == "Streams.CSharp" && edge.ToProject == "Streams.Core" &&
                    edge.Kind == "project");
                List<GraphEdge> transitive = q.ProjectGraph("Streams.App", 2, "downstream");
                Assert.Contains(transitive, edge =>
                    edge.FromProject == "Streams.CSharp" && edge.ToProject == "Streams.Core");

                TextSearchResult text = q.SearchTextGraded("fsharpTierAMarker", 10,
                    new IndexQueries.TextFilter(Lang: "fs"), 50, 0, "never");
                Assert.Contains(text.Hits, hit => hit.FilePath == "Core/Library.fs");
                FileHit fsFile = Assert.Single(q.FindFiles("*.fs", 10));
                Assert.Equal("fs", fsFile.Language);
                Assert.Empty(q.Outline("Core/Library.fs"));
            }

            using (var semanticWorkspace = new SemanticWorkspace(root, dbPath))
            {
                var (solution, coverage) = await semanticWorkspace.EnsureLoadedAsync(
                    ["Streams.CSharp", "Streams.Core"], CancellationToken.None);
                Assert.Equal(2, coverage.RequestedProjects);
                Assert.Equal(1, coverage.LoadedProjects);
                Assert.Equal("Streams.Core", Assert.Single(coverage.SkippedProjects));
                Assert.DoesNotContain(solution.Projects,
                    project => project.Name == "Streams.Core");
            }

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement capabilities = Parse(tools.ServerCapabilities());
            Assert.Contains("fsharp", capabilities.GetProperty("languages").EnumerateArray()
                .Select(language => language.GetString()));
            var featureIds = capabilities.GetProperty("features").EnumerateArray()
                .Select(feature => feature.GetProperty("id").GetString()).ToHashSet();
            Assert.Contains("fsharp-text-indexing", featureIds);
            Assert.Contains("fsharp-project-graph", featureIds);
            Assert.Contains("fsharp-outline", featureIds);
            Assert.Contains("fsharp-outline-parse-context-selection", featureIds);
            Assert.Contains("fsharp-outline-parse-context-budget", featureIds);
            Assert.Contains("fsharp-symbol-at-semantic", featureIds);
            Assert.Contains("fsharp-definition-same-project", featureIds);
            Assert.Contains("fsharp-type-check-context-selection", featureIds);
            Assert.Contains("fsharp-semantic-snapshot", featureIds);
            Assert.Contains("fsharp-semantic-bounded-project-evaluation", featureIds);
            Assert.Contains("workspace-msbuild-config-indexing", featureIds);
            Assert.DoesNotContain("fsharp-outline-context-selection", featureIds);
            Assert.DoesNotContain("fsharp-outline-context-budget", featureIds);
            Assert.Contains("fsharp-unsupported-language-boundary", featureIds);
            Assert.Contains("review-fsharp-file-coverage", featureIds);
            Assert.Equal("text", capabilities.GetProperty("languageLayers")
                .GetProperty("fsharp")[0].GetString());
            Assert.Equal("syntax", capabilities.GetProperty("languageLayers")
                .GetProperty("fsharp")[1].GetString());
            Assert.Equal("semantic", capabilities.GetProperty("languageLayers")
                .GetProperty("fsharp")[2].GetString());

            JsonElement repo = Parse(tools.RepoOverview());
            Assert.Equal(2, repo.GetProperty("fsFiles").GetInt64());
            Assert.Equal(1, repo.GetProperty("projects").GetProperty("fsharp").GetInt64());

            JsonElement files = Parse(tools.FindFile("*.fs"));
            Assert.Equal("fs", files.GetProperty("files")[0].GetProperty("language").GetString());
            JsonElement config = Parse(tools.ConfigLookup("PhoenixFSharpEvalMarker"));
            Assert.Contains(config.GetProperty("hits").EnumerateArray(), hit =>
                hit.GetProperty("path").GetString() == "Build/Stage2.props");
            Assert.Contains("fsharpTierAMarker", tools.SearchText("fsharpTierAMarker", lang: "fs"));
            JsonElement regex = Parse(tools.SearchText("\\d{2}", regex: true,
                pathGlob: "Core/Library.fs"));
            Assert.False(regex.GetProperty("narrowed").GetBoolean());
            Assert.Equal(1, regex.GetProperty("matchCount").GetInt32());
            Assert.Contains("fsharpTierAMarker",
                tools.SourceContext("Core/Library.fs", "1-3", contextLines: 0));

            string outlineJson = tools.Outline("Core/Library.fs");
            JsonElement outline = Parse(outlineJson);
            Assert.True(outline.TryGetProperty("symbols", out JsonElement outlineSymbols), outlineJson);
            JsonElement module = Assert.Single(outlineSymbols.EnumerateArray());
            Assert.Equal("Streams.Core", module.GetProperty("name").GetString());
            Assert.Equal("module", module.GetProperty("kind").GetString());
            Assert.Equal(1, module.GetProperty("startLine").GetInt32());
            Assert.Equal(2, module.GetProperty("endLine").GetInt32());
            JsonElement marker = Assert.Single(module.GetProperty("members").EnumerateArray());
            Assert.Equal("fsharpTierAMarker", marker.GetProperty("name").GetString());
            Assert.Equal(2, marker.GetProperty("startLine").GetInt32());
            Assert.Equal("indexed", outline.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal("syntax", outline.GetProperty("meta").GetProperty("navigationLayer").GetString());
            JsonElement symbols = Parse(tools.SearchSymbol("fsharpTierAMarker",
                pathGlob: "Core/Library.fs"));
            Assert.Equal("unsupported_language", symbols.GetProperty("error").GetString());
            foreach (string fsharpScope in new[] { "Library.fs", "*.fs", "Core/*.fs" })
            {
                JsonElement scoped = Parse(tools.SearchSymbol("fsharpTierAMarker",
                    pathGlob: fsharpScope));
                Assert.Equal("unsupported_language", scoped.GetProperty("error").GetString());
                Assert.Equal("search_symbol", scoped.GetProperty("operation").GetString());
            }
            JsonElement unscopedCSharp = Parse(tools.SearchSymbol("Wrapper"));
            Assert.Contains(unscopedCSharp.GetProperty("symbols").EnumerateArray(), symbol =>
                symbol.GetProperty("name").GetString() == "Wrapper");
            Assert.False(unscopedCSharp.TryGetProperty("partial", out _));
            JsonElement mixedScope = Parse(tools.SearchSymbol("Wrapper", pathGlob: "**/*.*"));
            Assert.Contains(mixedScope.GetProperty("symbols").EnumerateArray(), symbol =>
                symbol.GetProperty("name").GetString() == "Wrapper");
            Assert.True(mixedScope.GetProperty("partial").GetBoolean());
            Assert.Equal("unsupported_language_files_skipped",
                mixedScope.GetProperty("partialReason").GetString());
            Assert.Contains("cs", mixedScope.GetProperty("scopeLanguages").EnumerateArray()
                .Select(language => language.GetString()));
            Assert.Contains("fs", mixedScope.GetProperty("unsupportedLanguages").EnumerateArray()
                .Select(language => language.GetString()));
            JsonElement projectOutline = Parse(tools.Outline("Core/Core.fsproj"));
            Assert.Equal("unsupported_language", projectOutline.GetProperty("error").GetString());
            Assert.Equal("fsproj", projectOutline.GetProperty("language").GetString());
            Assert.DoesNotContain("F# is indexed", projectOutline.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
            JsonElement fsharpAt = Parse(tools.SymbolAt("Core/Library.fs", 2, 5,
                timeoutMs: 60_000));
            Assert.True(fsharpAt.GetProperty("found").GetBoolean());
            Assert.Equal("fsharpTierAMarker",
                fsharpAt.GetProperty("symbol").GetProperty("name").GetString());
            JsonElement fsharpDefinition = Parse(tools.Definition(path: "Core/Library.fs",
                line: 2, column: 5, mode: "semantic", timeoutMs: 60_000));
            Assert.Contains(fsharpDefinition.GetProperty("declarations").EnumerateArray(), site =>
                site.GetProperty("path").GetString() == "Core/Library.fs");

            var gatedOperations = new Dictionary<string, string>
            {
                ["references"] = tools.References(path: "Core/Library.fs", line: 1),
                ["implementations"] = tools.Implementations(path: "Core/Library.fs", line: 1),
                ["callers"] = tools.Callers(path: "Core/Library.fs", line: 1),
                ["callees"] = tools.Callees(path: "Core/Library.fs", line: 1),
                ["type_hierarchy"] = tools.TypeHierarchy(path: "Core/Library.fs", line: 1),
            };
            foreach ((string operation, string response) in gatedOperations)
            {
                JsonElement gated = Parse(response);
                Assert.Equal("unsupported_language", gated.GetProperty("error").GetString());
                Assert.Equal(operation, gated.GetProperty("operation").GetString());
                Assert.Equal("fs", gated.GetProperty("language").GetString());
            }

            if (semantic.FrameworkRefsAvailable)
            {
                JsonElement callees = SemanticRetry.ParseWithRetry(
                    () => tools.Callees(name: "Run", timeoutMs: 90_000),
                    json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                            reason.GetString() == "unsupported_language_projects_skipped",
                    "F# dependency coverage in callees");
                Assert.True(callees.GetProperty("partial").GetBoolean());
                Assert.Equal("indexed", callees.GetProperty("meta")
                    .GetProperty("confidence").GetString());
                Assert.Contains("Streams.Core", callees.GetProperty("coverage")
                    .GetProperty("skippedProjects").EnumerateArray()
                    .Select(project => project.GetString()));
                Assert.Contains(manager.Telemetry.Snapshot(), record =>
                    record.Contains("\"tool\":\"callees\"", StringComparison.Ordinal) &&
                    record.Contains("\"result\":\"partial\"", StringComparison.Ordinal) &&
                    record.Contains(
                        "\"reason\":\"unsupported_language_projects_skipped\"",
                        StringComparison.Ordinal));
            }

            JsonElement graph = Parse(tools.ProjectGraph("Streams.CSharp", 1, "downstream"));
            JsonElement crossLanguageEdge = Assert.Single(graph.GetProperty("edges").EnumerateArray(),
                edge => edge.GetProperty("to").GetString() == "Streams.Core");
            Assert.Equal("cs", crossLanguageEdge.GetProperty("fromLanguage").GetString());
            Assert.Equal("fs", crossLanguageEdge.GetProperty("toLanguage").GetString());
            JsonElement containing = Parse(tools.ProjectsContaining("Core/Library.fs"));
            Assert.Equal("fs", containing.GetProperty("projects")[0]
                .GetProperty("language").GetString());
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedLanguageLogicalProjectNameStillLoadsCSharpAndReportsMixedGraphLanguage(
        bool fsharpSortsFirst)
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-name-collision").FullName;
        try
        {
            string fsharpDirectory = fsharpSortsFirst ? "A_FSharp" : "Z_FSharp";
            string csharpDirectory = fsharpSortsFirst ? "Z_CSharp" : "A_CSharp";
            WriteProject(root, fsharpDirectory, "Shared.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Shared.Logical</AssemblyName></PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """,
                ("Library.fs", "module Shared.FSharp\nlet marker = 1\n"));
            WriteProject(root, csharpDirectory, "Shared.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Shared.Logical</AssemblyName></PropertyGroup>
                </Project>
                """,
                ("Library.cs", "namespace Shared.CSharp; public sealed class CSharpWins { }"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var workspace = new SemanticWorkspace(root, dbPath))
            {
                var (solution, coverage) = await workspace.EnsureLoadedAsync(
                    ["Shared.Logical"], CancellationToken.None);
                Assert.Equal(1, coverage.LoadedProjects);
                Assert.Empty(coverage.SkippedProjects);
                var loaded = Assert.Single(solution.Projects);
                Assert.Equal("Shared.Logical", loaded.Name);
                Assert.Contains(loaded.Documents, document =>
                    (document.FilePath ?? "").Replace('\\', '/').EndsWith(
                        $"{csharpDirectory}/Library.cs", StringComparison.Ordinal));
                Assert.DoesNotContain(loaded.Documents, document =>
                    (document.FilePath ?? "").EndsWith(".fs", StringComparison.OrdinalIgnoreCase));
            }

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);
            JsonElement graph = Parse(tools.ProjectGraph("Shared.Logical", 1, "both"));
            Assert.Equal("mixed", graph.GetProperty("root").GetProperty("language").GetString());
            Assert.Equal($"{csharpDirectory}/Shared.csproj",
                graph.GetProperty("root").GetProperty("path").GetString());
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SemanticWiringUsesPhysicalCSharpEdgesWithoutSubstitutingFSharpTwins(
        bool fsharpSortsFirst)
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-physical-edges").FullName;
        try
        {
            string fsharpDirectory = fsharpSortsFirst ? "A_FSharp" : "Z_FSharp";
            string csharpDirectory = fsharpSortsFirst ? "Z_CSharp" : "A_CSharp";
            WriteProject(root, "CsDependency", "CsDependency.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Cs.Dependency</AssemblyName></PropertyGroup>
                </Project>
                """,
                ("Dependency.cs", "namespace CsDependency; public sealed class Marker { }"));
            WriteProject(root, "FsDependency", "FsDependency.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Fs.Dependency</AssemblyName></PropertyGroup>
                  <ItemGroup><Compile Include="Dependency.fs" /></ItemGroup>
                </Project>
                """,
                ("Dependency.fs", "module FsDependency\nlet marker = 1\n"));
            WriteProject(root, fsharpDirectory, "Shared.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Shared.Logical</AssemblyName></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Library.fs" />
                    <ProjectReference Include="../FsDependency/FsDependency.fsproj" />
                  </ItemGroup>
                </Project>
                """,
                ("Library.fs", "module Shared.FSharp\nlet marker = 1\n"));
            WriteProject(root, csharpDirectory, "Shared.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Shared.Logical</AssemblyName></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../CsDependency/CsDependency.csproj" />
                  </ItemGroup>
                </Project>
                """,
                ("Library.cs", "namespace Shared.CSharp; public sealed class CSharpWins { }"));
            WriteProject(root, "Consumer", "Consumer.csproj",
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Consumer</AssemblyName></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../{fsharpDirectory}/Shared.fsproj" />
                  </ItemGroup>
                </Project>
                """,
                ("Consumer.cs", "namespace Consumer; public sealed class ConsumerType { private Shared.CSharp.CSharpWins? WrongTwin; }"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                List<GraphEdge> publicEdges = queries.ProjectGraph(
                    "Shared.Logical", 1, "downstream");
                Assert.Contains(publicEdges, edge => edge.ToProject == "Cs.Dependency");
                Assert.Contains(publicEdges, edge => edge.ToProject == "Fs.Dependency");

                List<SemanticProjectEdge> semanticEdges = queries.SemanticProjectEdges(
                    "Shared.Logical");
                SemanticProjectEdge csharpEdge = Assert.Single(semanticEdges);
                Assert.Equal("cs", csharpEdge.FromLanguage);
                Assert.Equal("Cs.Dependency", csharpEdge.ToProject);
                Assert.Equal("cs", csharpEdge.ToLanguage);

                SemanticProjectEdge consumerEdge = Assert.Single(
                    queries.SemanticProjectEdges("Consumer"));
                Assert.Equal("Shared.Logical", consumerEdge.ToProject);
                Assert.Equal("fs", consumerEdge.ToLanguage);
                Assert.Equal($"{fsharpDirectory}/Shared.fsproj", consumerEdge.ToPath);
                Assert.True(queries.HasSemanticCSharpPath("Shared.Logical", "Cs.Dependency"));
                Assert.False(queries.HasSemanticCSharpPath("Consumer", "Shared.Logical"));
            }

            using (var workspace = new SemanticWorkspace(root, dbPath))
            {
                var (_, warmCoverage) = await workspace.EnsureLoadedAsync(
                    ["Shared.Logical", "Cs.Dependency"],
                    CancellationToken.None);
                Assert.Equal(2, warmCoverage.LoadedProjects);
                Assert.Empty(warmCoverage.SkippedProjects);

                // Production reference scans resolve the owner first, then load candidates while
                // asking each one to see that already-warm owner. This second phase is decisive:
                // the old force-reference path bypassed the physical F# edge rejection below and
                // wired Consumer to the loaded C# namesake.
                var (solution, coverage) = await workspace.EnsureLoadedAsync(
                    ["Shared.Logical", "Cs.Dependency", "Fs.Dependency", "Consumer"],
                    CancellationToken.None, ensureReferenceTo: ["Shared.Logical"]);
                Assert.Equal(3, coverage.LoadedProjects);
                Assert.Contains("Fs.Dependency", coverage.SkippedProjects);
                Assert.Contains($"{fsharpDirectory}/Shared.fsproj", coverage.SkippedProjects);

                Microsoft.CodeAnalysis.Project shared = Assert.Single(solution.Projects,
                    project => project.Name == "Shared.Logical");
                List<string?> sharedReferences = shared.AllProjectReferences
                    .Select(reference => solution.GetProject(reference.ProjectId)?.Name)
                    .ToList();
                Assert.Equal(new[] { "Cs.Dependency" }, sharedReferences);
                Microsoft.CodeAnalysis.Project consumer = Assert.Single(solution.Projects,
                    project => project.Name == "Consumer");
                Assert.Empty(consumer.AllProjectReferences);
            }

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);
            JsonElement references = SemanticRetry.ParseWithRetry(
                () => tools.References(name: "CSharpWins",
                    path: $"{csharpDirectory}/Library.cs", line: 1,
                    mode: "semantic", timeoutMs: 90_000),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        (reason.GetString() ?? "").Contains(
                            "unsupported_language_projects_skipped", StringComparison.Ordinal),
                "physical F# twin remains unsupported after the owner is warm");
            Assert.Equal(0, references.GetProperty("totalReferences").GetInt32());
            Assert.True(references.GetProperty("partial").GetBoolean());
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveredAssemblyReferenceCollisionRetainsCSharpAndFSharpAuthority(
        bool fsharpSortsFirst)
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-assembly-collision").FullName;
        try
        {
            string fsharpDirectory = fsharpSortsFirst ? "A_FSharp" : "Z_FSharp";
            string csharpDirectory = fsharpSortsFirst ? "Z_CSharp" : "A_CSharp";
            WriteProject(root, fsharpDirectory, "Shared.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Shared.Assembly</AssemblyName></PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """,
                ("Library.fs", "module Shared.FSharp\nlet value = 1\n"));
            WriteProject(root, csharpDirectory, "Shared.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Shared.Assembly</AssemblyName></PropertyGroup>
                </Project>
                """,
                ("Library.cs", "namespace Shared.CSharp; public sealed class SupportedTwin { }"));
            WriteProject(root, "Bare", "Bare.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><Reference Include="Shared.Assembly" /></ItemGroup>
                </Project>
                """,
                ("Bare.cs", "namespace Bare; public sealed class Consumer { }"));
            WriteProject(root, "Hint", "Hint.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Reference Include="Shared.Assembly">
                      <HintPath>../Common/Shared.Assembly.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """,
                ("Hint.cs", "namespace Hint; public sealed class Consumer { }"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                foreach (string consumer in new[] { "Bare", "Hint" })
                {
                    List<SemanticProjectEdge> edges = queries.SemanticProjectEdges(consumer);
                    Assert.Equal(new[] { "cs", "fs" }, edges.Select(edge => edge.ToLanguage)
                        .OrderBy(language => language, StringComparer.Ordinal).ToArray());
                    List<GraphEdge> publicEdges = queries.ProjectGraph(consumer, 1, "downstream")
                        .Where(edge => edge.ToProject == "Shared.Assembly").ToList();
                    Assert.Equal(2, publicEdges.Count);
                    Assert.All(publicEdges, edge => Assert.Equal("assembly", edge.Kind));
                }
            }

            using var workspace = new SemanticWorkspace(root, dbPath);
            var (_, coverage) = await workspace.EnsureLoadedAsync(
                ["Bare", "Shared.Assembly"], CancellationToken.None);
            Assert.Equal(2, coverage.LoadedProjects);
            Assert.Contains($"{fsharpDirectory}/Shared.fsproj", coverage.SkippedProjects);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SemanticCSharpReachabilityBatchesDeepGraphAndHonorsCancellation()
    {
        string root = Directory.CreateTempSubdirectory("codenav-semantic-reachability").FullName;
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            const int projectCount = 128;
            using (var store = new IndexStore(dbPath, createNew: true))
            {
                using var tx = store.BeginTransaction();
                var ids = new long[projectCount];
                for (int i = 0; i < projectCount; i++)
                {
                    ids[i] = store.InsertProject(tx, new ParsedProject(
                        $"P{i}/P{i}.csproj", $"P{i}", "sdk", null, "net9.0",
                        false, [], [], null, [], "parsed"));
                }
                for (int i = 1; i < projectCount; i++)
                    store.InsertProjectRef(tx, ids[i], ids[i - 1]);
                tx.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            string[] sources = Enumerable.Range(0, projectCount)
                .Select(i => $"P{i}").ToArray();
            Dictionary<string, HashSet<string>> reachable =
                queries.SemanticCSharpReachability(sources, ["P0"]);
            Assert.Equal(projectCount, reachable.Count);
            Assert.All(sources, source => Assert.Contains("P0", reachable[source]));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                queries.SemanticCSharpReachability(sources, ["P0"], cancelled.Token));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SkippedFSharpReferenceCandidateIsAnObservableLowerBound()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-reference-coverage").FullName;
        try
        {
            WriteProject(root, "Contracts", "Contracts.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Contracts</AssemblyName></PropertyGroup>
                </Project>
                """,
                ("Target.cs", "namespace Contracts; public sealed class TierAReferenceTarget { } public interface ITierAContract { } public static class TierACallTarget { public static void Run() { } }"));
            WriteProject(root, "FSharpConsumer", "Consumer.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Consumer</AssemblyName></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Contracts/Contracts.csproj" />
                  </ItemGroup>
                </Project>
                """,
                ("Consumer.fs", "module Consumer\ntype FSharpImpl() = interface Contracts.ITierAContract\nlet consume (value: Contracts.TierAReferenceTarget) = value\nlet call () = Contracts.TierACallTarget.Run()\n"));
            WriteProject(root, "CSharpTwin", "Consumer.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Consumer</AssemblyName></PropertyGroup>
                </Project>
                """,
                ("Consumer.cs", "namespace Consumer; public sealed class NonReferencingTwin { }"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                SemanticTextCandidateProject candidate = Assert.Single(
                    queries.CandidateProjectsForName("TierAReferenceTarget"), item =>
                        item.Language == "fs");
                Assert.Equal("Consumer", candidate.Project);
                Assert.Equal("FSharpConsumer/Consumer.fsproj", candidate.ProjectPath);
                Assert.Equal("fs", candidate.Language);
            }
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);

            JsonElement references = SemanticRetry.ParseWithRetry(
                () => tools.References(name: "TierAReferenceTarget", mode: "semantic",
                    timeoutMs: 90_000),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        (reason.GetString() ?? "").Contains(
                            "unsupported_language_projects_skipped", StringComparison.Ordinal),
                "unsupported-language project coverage disclosure");
            Assert.True(references.GetProperty("partial").GetBoolean());
            Assert.True(references.GetProperty("totalIsLowerBound").GetBoolean());
            Assert.StartsWith("at least ", references.GetProperty("summary").GetString());
            Assert.Equal("indexed", references.GetProperty("meta")
                .GetProperty("confidence").GetString());
            Assert.Equal(1, references.GetProperty("coverage")
                .GetProperty("skippedProjectCount").GetInt32());
            Assert.Contains("FSharpConsumer/Consumer.fsproj", references.GetProperty("coverage")
                .GetProperty("skippedProjects").EnumerateArray().Select(item => item.GetString()));

            JsonElement callers = SemanticRetry.ParseWithRetry(
                () => tools.Callers(name: "Run", timeoutMs: 90_000),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        reason.GetString() == "unsupported_language_projects_skipped",
                "unsupported-language callers coverage");
            Assert.True(callers.GetProperty("partial").GetBoolean());
            Assert.Equal("indexed", callers.GetProperty("meta")
                .GetProperty("confidence").GetString());

            JsonElement hierarchy = SemanticRetry.ParseWithRetry(
                () => tools.TypeHierarchy(name: "ITierAContract", timeoutMs: 90_000),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        reason.GetString() == "unsupported_language_projects_skipped",
                "unsupported-language hierarchy coverage");
            Assert.True(hierarchy.GetProperty("partial").GetBoolean());
            Assert.Equal("indexed", hierarchy.GetProperty("meta")
                .GetProperty("confidence").GetString());

            JsonElement implementations = SemanticRetry.ParseWithRetry(
                () => tools.Implementations(name: "ITierAContract", timeoutMs: 90_000),
                json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                        reason.GetString() == "unsupported_language_projects_skipped",
                "unsupported-language implementation coverage");
            Assert.NotEqual("candidate_cluster_bounded",
                implementations.GetProperty("partialReason").GetString());

            foreach (string tool in new[]
                     {
                         "references", "callers", "type_hierarchy", "implementations",
                     })
            {
                Assert.Contains(manager.Telemetry.Snapshot(), record =>
                    record.Contains($"\"tool\":\"{tool}\"", StringComparison.Ordinal) &&
                    record.Contains("\"result\":\"partial\"", StringComparison.Ordinal) &&
                    record.Contains(
                        "\"reason\":\"unsupported_language_projects_skipped\"",
                        StringComparison.Ordinal));
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReferenceCandidateFiltersApplyBeforeBudgetAndCoverageAccounting()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-reference-candidate-filters").FullName;
        try
        {
            WriteProject(root, "Contracts", "Contracts.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Contracts</AssemblyName></PropertyGroup>
                </Project>
                """,
                ("Targets.cs", "namespace Contracts; public sealed class TestOnlyReferenceTarget { } public sealed class GeneratedOnlyReferenceTarget { } public interface TestOnlyContract { } public interface GeneratedOnlyContract { }"));
            WriteProject(root, "TestConsumer", "Consumer.Tests.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Consumer.Tests</AssemblyName></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Consumer.fs" />
                    <ProjectReference Include="../Contracts/Contracts.csproj" />
                  </ItemGroup>
                </Project>
                """,
                ("Consumer.fs", "module TestConsumer\nlet use (value: Contracts.TestOnlyReferenceTarget) = value\n"));
            WriteProject(root, "GeneratedConsumer", "GeneratedConsumer.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Compile Include="Consumer.g.fs" />
                    <ProjectReference Include="../Contracts/Contracts.csproj" />
                  </ItemGroup>
                </Project>
                """,
                ("Consumer.g.fs", "module GeneratedConsumer\nlet use (value: Contracts.GeneratedOnlyReferenceTarget) = value\n"));
            WriteProject(root, "TestImplementer", "Implementer.Tests.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Implementer.Tests</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Contracts/Contracts.csproj" /></ItemGroup>
                </Project>
                """,
                ("Impl.cs", "namespace TestImplementer; public sealed class Impl : Contracts.TestOnlyContract { }"));
            WriteProject(root, "GeneratedImplementer", "GeneratedImplementer.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><ProjectReference Include="../Contracts/Contracts.csproj" /></ItemGroup>
                </Project>
                """,
                ("Impl.g.cs", "namespace GeneratedImplementer; public sealed class Impl : Contracts.GeneratedOnlyContract { }"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Contains(queries.CandidateProjectsForName("TestOnlyReferenceTarget"),
                    candidate => candidate.Language == "fs" && candidate.Project == "Consumer.Tests");
                Assert.DoesNotContain(queries.CandidateProjectsForName(
                        "TestOnlyReferenceTarget", includeTests: false),
                    candidate => candidate.Language == "fs");
                Assert.Contains(queries.CandidateProjectsForName(
                        "GeneratedOnlyReferenceTarget", includeGenerated: true),
                    candidate => candidate.Language == "fs" &&
                                 candidate.Project == "GeneratedConsumer");
                Assert.DoesNotContain(queries.CandidateProjectsForName(
                        "GeneratedOnlyReferenceTarget", includeGenerated: false),
                    candidate => candidate.Language == "fs");
                Assert.Contains("Implementer.Tests",
                    queries.ImplementationCandidateProjects("TestOnlyContract"));
                Assert.DoesNotContain("Implementer.Tests",
                    queries.ImplementationCandidateProjects("TestOnlyContract",
                        includeTests: false));
                Assert.Contains("GeneratedImplementer",
                    queries.ImplementationCandidateProjects("GeneratedOnlyContract"));
                Assert.DoesNotContain("GeneratedImplementer",
                    queries.ImplementationCandidateProjects("GeneratedOnlyContract",
                        includeGenerated: false));
            }

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);

            JsonElement unfilteredTest = SemanticRetry.ParseWithRetry(
                () => tools.References(name: "TestOnlyReferenceTarget", mode: "semantic",
                    includeTests: true, includeGenerated: false, maxProjects: 1,
                    timeoutMs: 90_000),
                response => response.TryGetProperty("partialReason", out JsonElement reason) &&
                            (reason.GetString() ?? "").Contains(
                                "unsupported_language_projects_skipped",
                                StringComparison.Ordinal),
                "unfiltered test-only F# candidate coverage");
            Assert.True(unfilteredTest.GetProperty("totalIsLowerBound").GetBoolean());

            JsonElement filteredTest = SemanticRetry.ParseExactWithRetry(
                () => tools.References(name: "TestOnlyReferenceTarget", mode: "semantic",
                    includeTests: false, includeGenerated: false, maxProjects: 1,
                    timeoutMs: 90_000));
            Assert.Equal(0, filteredTest.GetProperty("totalReferences").GetInt32());
            Assert.False(filteredTest.GetProperty("partial").GetBoolean());
            Assert.False(filteredTest.TryGetProperty("totalIsLowerBound", out _));

            JsonElement unfilteredGenerated = SemanticRetry.ParseWithRetry(
                () => tools.References(name: "GeneratedOnlyReferenceTarget", mode: "semantic",
                    includeTests: true, includeGenerated: true, maxProjects: 1,
                    timeoutMs: 90_000),
                response => response.TryGetProperty("partialReason", out JsonElement reason) &&
                            (reason.GetString() ?? "").Contains(
                                "unsupported_language_projects_skipped",
                                StringComparison.Ordinal),
                "unfiltered generated-only F# candidate coverage");
            Assert.True(unfilteredGenerated.GetProperty("totalIsLowerBound").GetBoolean());

            JsonElement filteredGenerated = SemanticRetry.ParseExactWithRetry(
                () => tools.References(name: "GeneratedOnlyReferenceTarget", mode: "semantic",
                    includeTests: true, includeGenerated: false, maxProjects: 1,
                    timeoutMs: 90_000));
            Assert.Equal(0, filteredGenerated.GetProperty("totalReferences").GetInt32());
            Assert.False(filteredGenerated.GetProperty("partial").GetBoolean());
            Assert.False(filteredGenerated.TryGetProperty("totalIsLowerBound", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MixedLanguageNameCollisionKeepsProductionCSharpOutsideTestFilter()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-mixed-language-test-classification").FullName;
        try
        {
            WriteProject(root, "Contracts", "Contracts.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>Contracts</AssemblyName></PropertyGroup>
                </Project>
                """,
                ("Target.cs", "namespace Contracts; public sealed class CollisionTarget { }"));
            WriteProject(root, "ProductionConsumer", "Consumer.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>SharedConsumer</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Contracts/Contracts.csproj" /></ItemGroup>
                </Project>
                """,
                ("Use.cs", "namespace ProductionConsumer; public sealed class Use { public Contracts.CollisionTarget? Value; }"));
            WriteProject(root, "FSharpTests", "Consumer.Tests.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><AssemblyName>SharedConsumer</AssemblyName></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Tests.fs" />
                    <PackageReference Include="xunit" Version="2.9.0" />
                  </ItemGroup>
                </Project>
                """,
                ("Tests.fs", "module FSharpTests\nlet testMarker = 1\n"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                ProjectRow csharp = Assert.Single(
                    queries.ProjectsContaining("ProductionConsumer/Use.cs"));
                ProjectRow fsharp = Assert.Single(
                    queries.ProjectsContaining("FSharpTests/Tests.fs"));
                Assert.Equal("cs", csharp.Language);
                Assert.False(csharp.IsTest);
                Assert.Equal("fs", fsharp.Language);
                Assert.True(fsharp.IsTest);
                Assert.False(queries.AllProjectTestFlags("cs")["SharedConsumer"]);
                Assert.True(queries.AllProjectTestFlags("fs")["SharedConsumer"]);
                Assert.True(queries.AllProjectTestFlags()["SharedConsumer"]);
                Assert.Contains(queries.CandidateProjectsForName(
                        "CollisionTarget", includeTests: false),
                    candidate => candidate.Project == "SharedConsumer" &&
                                 candidate.Language == "cs");
            }

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);
            JsonElement references = SemanticRetry.ParseExactWithRetry(
                () => tools.References(name: "CollisionTarget", mode: "semantic",
                    includeTests: false, includeGenerated: false, maxProjects: 4,
                    timeoutMs: 90_000));
            Assert.Equal(1, references.GetProperty("totalReferences").GetInt32());
            Assert.False(references.GetProperty("partial").GetBoolean());
            JsonElement group = Assert.Single(references.GetProperty("groups").EnumerateArray());
            Assert.Equal("SharedConsumer", group.GetProperty("project").GetString());
            Assert.False(group.GetProperty("isTest").GetBoolean());
            Assert.Equal(1, group.GetProperty("count").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnsupportedLanguageEnvelopeBoundsLongMultibyteIndexedPath()
    {
        var health = new IndexHealth("ready", "test", "indexed", "refreshed", 0,
            null, 1, "C:/workspace", "C:/workspace/index.db");
        foreach (string longPath in new[]
                 {
                     new string('界', 12_000) + ".fs",
                     new string('\u0001', 12_000) + ".fs",
                 })
        {
            string response = NavigationTools.UnsupportedLanguageForTest(health, longPath, "fs",
                "symbol_at");
            Assert.True(Json.Utf8Bytes(response) <= Json.HardBudgetBytes,
                $"unsupported-language response used {Json.Utf8Bytes(response)} bytes");
            JsonElement json = Parse(response);
            Assert.Equal("unsupported_language", json.GetProperty("error").GetString());
            Assert.True(json.GetProperty("pathTruncated").GetBoolean());
            Assert.True(Json.Utf8Bytes(json.GetProperty("path").GetString()!) <= 4096);
        }
    }

    [Fact]
    public void GeneratedFSharpClassificationIsConsistentAcrossColdAndDelta()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-generated").FullName;
        try
        {
            WriteProject(root, "Core", "Core.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Compile Include="Normal.fs" />
                    <Compile Include="Generated.g.fs" />
                    <Compile Include="Banner.fs" />
                  </ItemGroup>
                </Project>
                """,
                ("Normal.fs", "module Core.Normal\nlet normalGeneratedFilterMarker = 1\n"),
                ("Generated.g.fs",
                    "module Core.Generated\nlet suffixGeneratedFilterMarker = 2\n"),
                ("Banner.fs",
                    "// <auto-generated/>\nmodule Core.Banner\nlet bannerGeneratedFilterMarker = 3\n"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.False(queries.FileByPath("Core/Normal.fs")!.IsGenerated);
                Assert.True(queries.FileByPath("Core/Generated.g.fs")!.IsGenerated);
                Assert.True(queries.FileByPath("Core/Banner.fs")!.IsGenerated);
                Assert.Empty(FSharpTextHits(queries, "suffixGeneratedFilterMarker",
                    includeGenerated: false));
                Assert.Single(FSharpTextHits(queries, "suffixGeneratedFilterMarker",
                    includeGenerated: true));
                Assert.Empty(FSharpTextHits(queries, "bannerGeneratedFilterMarker",
                    includeGenerated: false));
            }

            File.WriteAllText(Path.Combine(root, "Core", "Normal.fs"),
                "// <auto-generated/>\nmodule Core.Normal\nlet normalGeneratedFilterMarker = 4\n");
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root, ["Core/Normal.fs"]);
                Assert.Equal(1, refresh.ChangedFiles);
            }
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.True(queries.FileByPath("Core/Normal.fs")!.IsGenerated);
                Assert.Empty(FSharpTextHits(queries, "normalGeneratedFilterMarker",
                    includeGenerated: false));
                Assert.Single(FSharpTextHits(queries, "normalGeneratedFilterMarker",
                    includeGenerated: true));
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void OverviewExcludesFSharpScriptsFromOrphanCount()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-orphans").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Loose.fs"), "module Loose\n");
            File.WriteAllText(Path.Combine(root, "Loose.fsi"), "module Loose\n");
            File.WriteAllText(Path.Combine(root, "Loose.fsx"), "printfn \"script\"\n");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var queries = new IndexQueries(dbPath);
            OverviewStats overview = queries.Overview();
            Assert.Equal(3, overview.FsFiles);
            Assert.Equal(2, overview.OrphanedFiles);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewPackReportsFSharpChangesAtFileGranularity()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-review-pack").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            WriteProject(root, "Core", "Core.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Compile Include="Staged.fs" />
                    <Compile Include="Unstaged.fsi" />
                  </ItemGroup>
                </Project>
                """,
                ("Staged.fs", "module Core.Staged\nlet value = 1\n"),
                ("Unstaged.fsi", "module Core.Unstaged\nval value: int\n"),
                ("Deleted.fsx", "printfn \"delete me\"\n"));
            RunGit(root, "init");
            RunGit(root, "config", "user.email", "tests@example.invalid");
            RunGit(root, "config", "user.name", "Phoenix Tests");
            RunGit(root, "add", "--", ".");
            RunGit(root, "commit", "-m", "baseline");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            File.WriteAllText(Path.Combine(root, "Core", "Staged.fs"),
                "module Core.Staged\nlet value = 2\n");
            RunGit(root, "add", "--", "Core/Staged.fs");
            File.WriteAllText(Path.Combine(root, "Core", "Unstaged.fsi"),
                "module Core.Unstaged\nval value: string\n");
            File.Delete(Path.Combine(root, "Core", "Deleted.fsx"));
            File.WriteAllText(Path.Combine(root, "Core", "Untracked.fs"),
                "module Core.Untracked\nlet value = 3\n");

            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var tools = new NavigationTools(manager, semantic);
            JsonElement pack = SemanticRetry.ParseWithRetry(
                () => tools.ReviewPack(maxBytes: Json.HardBudgetBytes),
                json => json.TryGetProperty("unsupportedLanguageFiles", out JsonElement files) &&
                        files.ValueKind == JsonValueKind.Array && files.GetArrayLength() == 4,
                "file-level F# review evidence");

            Dictionary<string, string> changes = pack.GetProperty("unsupportedLanguageFiles")
                .EnumerateArray().ToDictionary(
                    item => item.GetProperty("path").GetString()!,
                    item => item.GetProperty("change").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal("changed", changes["Core/Staged.fs"]);
            Assert.Equal("changed", changes["Core/Unstaged.fsi"]);
            Assert.Equal("deleted", changes["Core/Deleted.fsx"]);
            Assert.Equal("untracked", changes["Core/Untracked.fs"]);
            Assert.Equal(3, pack.GetProperty("changedFiles").GetProperty("fs").GetInt32());
            JsonElement coverage = pack.GetProperty("unsupportedLanguageFilesCoverage");
            Assert.Equal(4, coverage.GetProperty("total").GetInt32());
            Assert.Equal(4, coverage.GetProperty("returned").GetInt32());
            Assert.False(coverage.TryGetProperty("truncated", out _));
            Assert.Contains(pack.GetProperty("notes").EnumerateArray(), note =>
                note.GetProperty("id").GetString() == "review.unsupported_language_files");
            Assert.True(Json.Utf8Bytes(pack.GetRawText()) <= Json.HardBudgetBytes);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void IncrementalRefreshAddsUpdatesAndDeletesExplicitFSharpCompileItem()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-delta").FullName;
        try
        {
            string projectDir = Path.Combine(root, "Core");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "Core.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            string sourcePath = Path.Combine(projectDir, "Library.fs");
            File.WriteAllText(sourcePath, "module Core.Library\nlet deltaMarkerOne = 1\n");
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult added = DeltaRefresher.Refresh(store, root,
                    new[] { "Core/Library.fs" });
                Assert.Equal(1, added.AddedFiles);
                Assert.True(added.ProjectsRefreshed);
            }
            using (var q = new IndexQueries(dbPath))
            {
                Assert.Equal("Core", Assert.Single(q.ProjectsContaining("Core/Library.fs")).Name);
                Assert.Contains(q.SearchText("deltaMarkerOne", 5), hit =>
                    hit.FilePath == "Core/Library.fs");
            }

            File.WriteAllText(sourcePath, "module Core.Library\nlet deltaMarkerTwo = 2\n");
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult changed = DeltaRefresher.Refresh(store, root,
                    new[] { "Core/Library.fs" });
                Assert.Equal(1, changed.ChangedFiles);
                Assert.False(changed.ProjectsRefreshed);
            }
            using (var q = new IndexQueries(dbPath))
            {
                Assert.Empty(q.SearchText("deltaMarkerOne", 5));
                Assert.Contains(q.SearchText("deltaMarkerTwo", 5), hit =>
                    hit.FilePath == "Core/Library.fs");
            }

            File.Delete(sourcePath);
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult deleted = DeltaRefresher.Refresh(store, root,
                    new[] { "Core/Library.fs" });
                Assert.Equal(1, deleted.DeletedFiles);
            }
            using (var q = new IndexQueries(dbPath))
            {
                Assert.Null(q.FileByPath("Core/Library.fs"));
                Assert.Empty(q.ProjectsContaining("Core/Library.fs"));
            }
        }
        finally { Cleanup(root); }
    }

    private static void WriteMixedWorkspace(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Build"));
        File.WriteAllText(Path.Combine(root, "Build", "Stage2.props"),
            "<Project><PropertyGroup><PhoenixFSharpEvalMarker>bounded</PhoenixFSharpEvalMarker></PropertyGroup></Project>");

        WriteProject(root, "Core", "Core.fsproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>Streams.Core</AssemblyName>
              </PropertyGroup>
              <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
            </Project>
            """,
            ("Library.fs", "module Streams.Core\nlet fsharpTierAMarker = 42\n"),
            ("NotCompiled.fsx", "let scriptOnlyMarker = 1\n"));

        WriteProject(root, "Wrapper", "Wrapper.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>Streams.CSharp</AssemblyName>
              </PropertyGroup>
              <ItemGroup><ProjectReference Include="../Core/Core.fsproj" /></ItemGroup>
            </Project>
            """,
            ("Wrapper.cs", "namespace Streams.CSharp; public sealed class Wrapper { public void Run() { System.Console.WriteLine(1); } }"));

        WriteProject(root, "App", "App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>Streams.App</AssemblyName>
              </PropertyGroup>
              <ItemGroup><ProjectReference Include="../Wrapper/Wrapper.csproj" /></ItemGroup>
            </Project>
            """,
            ("Program.cs", "namespace Streams.App; public sealed class Program { }"));
    }

    private static void WriteProject(string root, string directory, string projectName,
        string projectXml, params (string Name, string Content)[] files)
    {
        string fullDirectory = Path.Combine(root, directory);
        Directory.CreateDirectory(fullDirectory);
        File.WriteAllText(Path.Combine(fullDirectory, projectName), projectXml);
        foreach (var file in files)
            File.WriteAllText(Path.Combine(fullDirectory, file.Name), file.Content);
    }

    private static List<TextHit> FSharpTextHits(IndexQueries queries, string marker,
        bool includeGenerated)
        => queries.SearchTextGraded(marker, 10,
            new IndexQueries.TextFilter(IncludeGenerated: includeGenerated, Lang: "fs"),
            200, 0, "never").Hits;

    private static void RunGit(string root, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using System.Diagnostics.Process process =
            System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException(
                "Failed to start Git for the review_pack fixture.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(20_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"git {string.Join(' ', arguments)} timed out");
        }
        Assert.True(process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed ({process.ExitCode})\n{stdout}\n{stderr}");
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

    private static void Cleanup(string root)
    {
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
