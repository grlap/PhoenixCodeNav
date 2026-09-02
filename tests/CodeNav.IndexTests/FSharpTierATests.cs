using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;

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
                    $"module Memory.Small{i}\n" +
                    string.Join('\n', Enumerable.Range(0, 20)
                        .Select(value => $"let retainedSymbol{value} = {value}")) +
                    "\n// " + new string('x', 1_000));
            }
            File.WriteAllText(Path.Combine(sourceDirectory, "Oversized.fs"),
                "module Memory.Oversized\n// " + new string('y', 40_000));

            const long budgetBytes = 80_000;
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
            long textOnlyRetainedBytes = Directory.EnumerateFiles(sourceDirectory, "*.fs")
                .Sum(path => new FileInfo(path).Length +
                             (long)File.ReadAllText(path).Length * sizeof(char) + 256);
            Assert.True(batches.Sum(batch => batch.Bytes) > textOnlyRetainedBytes,
                "Prepared F# batch accounting must include retained symbol rows, not only source bytes and decoded text.");
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
                List<SymbolHit> indexedOutline = q.Outline("Core/Library.fs");
                Assert.Contains(indexedOutline, symbol =>
                    symbol.Name == "fsharpTierAMarker" && symbol.Kind == "value");
            }

            using (var semanticWorkspace = new SemanticWorkspace(root, dbPath))
            {
                using var load = await semanticWorkspace.EnsureLoadedAsync(
                    ["Streams.CSharp", "Streams.Core"], CancellationToken.None);
                var (solution, coverage) = load;
                Assert.Equal(2, coverage.RequestedProjects);
                Assert.Equal(1, coverage.LoadedProjects);
                Assert.Equal("Streams.Core", Assert.Single(coverage.SkippedProjects));
                Assert.DoesNotContain(solution.Projects,
                    project => project.Name == "Streams.Core");
            }

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000),
                manager.Health().Error);
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
            Assert.Contains("fsharp-indexed-symbol-name-search", featureIds);
            Assert.Contains("fsharp-indexed-parse-context-budget", featureIds);
            Assert.Contains("fsharp-symbol-at-semantic", featureIds);
            Assert.Contains("fsharp-definition-same-project", featureIds);
            Assert.Contains("fsharp-type-check-context-selection", featureIds);
            Assert.Contains("fsharp-semantic-snapshot", featureIds);
            Assert.Contains("fsharp-semantic-bounded-project-evaluation", featureIds);
            Assert.Contains("fsharp-semantic-directory-build-reference-evaluation", featureIds);
            Assert.Contains("fsharp-semantic-package-asset-closure", featureIds);
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
            JsonElement symbolSearch = Parse(tools.SearchSymbol("fsharpTierAMarker",
                pathGlob: "Core/Library.fs"));
            JsonElement indexedMarker = Assert.Single(symbolSearch.GetProperty("symbols")
                .EnumerateArray());
            Assert.Equal("fsharpTierAMarker", indexedMarker.GetProperty("name").GetString());
            Assert.Equal("value", indexedMarker.GetProperty("kind").GetString());
            Assert.Equal("Core/Library.fs", indexedMarker.GetProperty("path").GetString());
            Assert.Equal(2, indexedMarker.GetProperty("startLine").GetInt32());
            Assert.Equal("indexed",
                symbolSearch.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal("syntax",
                symbolSearch.GetProperty("meta").GetProperty("navigationLayer").GetString());
            foreach (string fsharpScope in new[] { "Library.fs", "*.fs", "Core/*.fs" })
            {
                JsonElement scoped = Parse(tools.SearchSymbol("fsharpTierAMarker",
                    pathGlob: fsharpScope));
                Assert.Contains(scoped.GetProperty("symbols").EnumerateArray(), symbol =>
                    symbol.GetProperty("name").GetString() == "fsharpTierAMarker" &&
                    symbol.GetProperty("path").GetString() == "Core/Library.fs");
            }
            JsonElement unscopedCSharp = Parse(tools.SearchSymbol("Wrapper"));
            Assert.Contains(unscopedCSharp.GetProperty("symbols").EnumerateArray(), symbol =>
                symbol.GetProperty("name").GetString() == "Wrapper");
            Assert.False(unscopedCSharp.TryGetProperty("partial", out _));
            Assert.False(unscopedCSharp.TryGetProperty("partialReason", out _));
            JsonElement advisoryCoverage =
                unscopedCSharp.GetProperty("fsharpProjectOptionCoverage");
            Assert.True(advisoryCoverage.GetProperty("advisoryOnly").GetBoolean());
            Assert.Contains("fsharp_project_options_imported",
                advisoryCoverage.GetProperty("reasons").EnumerateArray()
                    .Select(reason => reason.GetString()));
            JsonElement mixedScope = Parse(tools.SearchSymbol("Wrapper", pathGlob: "**/*.*"));
            Assert.Contains(mixedScope.GetProperty("symbols").EnumerateArray(), symbol =>
                symbol.GetProperty("name").GetString() == "Wrapper");
            Assert.True(mixedScope.GetProperty("partial").GetBoolean());
            Assert.Equal("unsupported_language_files_skipped",
                mixedScope.GetProperty("partialReason").GetString());
            Assert.Contains("cs", mixedScope.GetProperty("scopeLanguages").EnumerateArray()
                .Select(language => language.GetString()));
            Assert.Contains("fs", mixedScope.GetProperty("scopeLanguages").EnumerateArray()
                .Select(language => language.GetString()));
            Assert.Contains("fsx", mixedScope.GetProperty("scopeLanguages").EnumerateArray()
                .Select(language => language.GetString()));
            Assert.Contains("fsx", mixedScope.GetProperty("unsupportedLanguages")
                .EnumerateArray().Select(language => language.GetString()));
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
                using var load = await workspace.EnsureLoadedAsync(
                    ["Shared.Logical"], CancellationToken.None);
                var (solution, coverage) = load;
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
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(30),
                "mixed-language collision index did not become fresh");
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);
            JsonElement ambiguous = Parse(tools.ProjectGraph("Shared.Logical", 1, "both"));
            Assert.Equal("project_ambiguous", ambiguous.GetProperty("error").GetString());
            Assert.Equal(2, ambiguous.GetProperty("totalMatches").GetInt32());
            Assert.Equal(2, ambiguous.GetProperty("matches").GetArrayLength());

            JsonElement graph = Parse(tools.ProjectGraph(
                $"{csharpDirectory}/Shared.csproj", 1, "both"));
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
                using var warmLoad = await workspace.EnsureLoadedAsync(
                    ["Shared.Logical", "Cs.Dependency"],
                    CancellationToken.None);
                ClusterCoverage warmCoverage = warmLoad.Coverage;
                Assert.Equal(2, warmCoverage.LoadedProjects);
                Assert.Empty(warmCoverage.SkippedProjects);

                // Production reference scans resolve the owner first, then load candidates while
                // asking each one to see that already-warm owner. This second phase is decisive:
                // the old force-reference path bypassed the physical F# edge rejection below and
                // wired Consumer to the loaded C# namesake.
                using var scanLoad = await workspace.EnsureLoadedAsync(
                    ["Shared.Logical", "Cs.Dependency", "Fs.Dependency", "Consumer"],
                    CancellationToken.None, ensureReferenceTo: ["Shared.Logical"]);
                var (solution, coverage) = scanLoad;
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
            using var load = await workspace.EnsureLoadedAsync(
                ["Bare", "Shared.Assembly"], CancellationToken.None);
            ClusterCoverage coverage = load.Coverage;
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
    public void UnsupportedLanguageEnvelopeUsesStableFSharpScriptIdentityAndAdvertisesIndexedSearch()
    {
        var health = new IndexHealth("ready", "test", "indexed", "refreshed", 0,
            null, 1, "/workspace", "/workspace/index.db");

        JsonElement script = Parse(NavigationTools.UnsupportedLanguageForTest(
            health, "Scratch.fsx", "fs", "definition"));
        Assert.Equal("fsx", script.GetProperty("language").GetString());

        JsonElement orphan = Parse(NavigationTools.UnsupportedLanguageForTest(
            health, "Loose.fs", "fs", "outline"));
        Assert.Contains("search_symbol", orphan.GetProperty("availableForFile")
            .EnumerateArray().Select(tool => tool.GetString()));
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
                Assert.Empty(queries.SearchSymbols("suffixGeneratedFilterMarker", "exact",
                    ["value"], 5, includeGenerated: false));
                Assert.Single(queries.SearchSymbols("suffixGeneratedFilterMarker", "exact",
                    ["value"], 5, includeGenerated: true));
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
                Assert.Empty(queries.SearchSymbols("normalGeneratedFilterMarker", "exact",
                    ["value"], 5, includeGenerated: false));
                Assert.Single(queries.SearchSymbols("normalGeneratedFilterMarker", "exact",
                    ["value"], 5, includeGenerated: true));
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
    public void IndexedFSharpSymbolSearchCoversKindsArityDuplicatesOwnershipAndFilters()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-symbol-search").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Core");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Core.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Library.fsi" />
                    <Compile Include="Library.fs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Alternate.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Library.fsi"),
                """
                namespace SymbolSearch

                type Pair<'T, 'U> = { Left: 'T; Right: 'U }

                module Api =
                    val transform: int -> int
                """);
            File.WriteAllText(Path.Combine(projectDirectory, "Library.fs"),
                """
                namespace SymbolSearch

                type Pair<'T, 'U> = { Left: 'T; Right: 'U }

                type Shape =
                    | Circle of float
                    | Point

                module Api =
                    let transform value = value + 1
                    let (|Even|Odd|) value = if value % 2 = 0 then Even else Odd
                """);
            File.WriteAllText(Path.Combine(root, "Loose.fs"),
                "module Loose\nlet looseSearchMarker = 1\n");
            File.WriteAllText(Path.Combine(root, "Script.fsx"),
                "let scriptSearchMarker = 1\n");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement pairs = Parse(tools.SearchSymbol("Pair", match: "exact",
                pathGlob: "Core/*"));
            JsonElement[] pairHits = pairs.GetProperty("symbols").EnumerateArray().ToArray();
            Assert.Equal(2, pairHits.Length);
            Assert.Collection(pairHits,
                hit => Assert.Equal("Core/Library.fs",
                    hit.GetProperty("path").GetString()),
                hit => Assert.Equal("Core/Library.fsi",
                    hit.GetProperty("path").GetString()));
            Assert.All(pairHits, hit =>
            {
                Assert.Equal("record", hit.GetProperty("kind").GetString());
                Assert.Equal(2, hit.GetProperty("arity").GetInt32());
                Assert.False(hit.TryGetProperty("orphaned", out _));
            });
            JsonElement fsharpHandleDefinition = Parse(tools.Definition(
                symbolId: pairHits[0].GetProperty("symbolId").GetString()));
            Assert.Equal("fsharp_semantic_position_required",
                fsharpHandleDefinition.GetProperty("error").GetString());
            Assert.Contains("idx handle resolution is not available",
                fsharpHandleDefinition.GetProperty("detail").GetString(),
                StringComparison.Ordinal);

            JsonElement functions = Parse(tools.SearchSymbol("transform", kinds: "function",
                match: "exact", @namespace: "SymbolSearch"));
            Assert.Equal(2, functions.GetProperty("symbols").GetArrayLength());
            Assert.All(functions.GetProperty("symbols").EnumerateArray(), hit =>
                Assert.Equal("function", hit.GetProperty("kind").GetString()));

            JsonElement unionCase = Parse(tools.SearchSymbol("Circle", kinds: "union_case",
                match: "exact", pathGlob: "Core/Library.fs"));
            Assert.Equal("union_case",
                Assert.Single(unionCase.GetProperty("symbols").EnumerateArray())
                    .GetProperty("kind").GetString());
            JsonElement filteredUnionCase = Parse(tools.SearchSymbol("Circle", kinds: "class",
                match: "exact", pathGlob: "Core/Library.fs"));
            Assert.Empty(filteredUnionCase.GetProperty("symbols").EnumerateArray());
            Assert.True(filteredUnionCase.GetProperty("existsUnfiltered").GetBoolean());
            Assert.Contains("union_case",
                filteredUnionCase.GetProperty("unfilteredKinds").EnumerateArray()
                    .Select(kind => kind.GetString()));

            JsonElement namespaceEnumeration = Parse(tools.SearchSymbol("",
                @namespace: "SymbolSearch", kinds: "module"));
            Assert.Contains(namespaceEnumeration.GetProperty("symbols").EnumerateArray(), hit =>
                hit.GetProperty("name").GetString() == "Api");

            JsonElement activePattern = Parse(tools.SearchSymbol("|Even|Odd|", match: "exact",
                pathGlob: "Core/Library.fs"));
            Assert.Equal("function",
                Assert.Single(activePattern.GetProperty("symbols").EnumerateArray())
                    .GetProperty("kind").GetString());

            JsonElement loose = Parse(tools.SearchSymbol("looseSearchMarker", match: "exact"));
            Assert.True(Assert.Single(loose.GetProperty("symbols").EnumerateArray())
                .GetProperty("orphaned").GetBoolean());
            JsonElement script = Parse(tools.SearchSymbol("scriptSearchMarker", match: "exact"));
            Assert.Empty(script.GetProperty("symbols").EnumerateArray());

            JsonElement scriptScope = Parse(tools.SearchSymbol("scriptSearchMarker",
                match: "exact", pathGlob: "Script.fsx"));
            Assert.Equal("unsupported_language",
                scriptScope.GetProperty("error").GetString());
            Assert.Equal("fsx", scriptScope.GetProperty("language").GetString());

            JsonElement mixedScope = Parse(tools.SearchSymbol("missingMarker",
                match: "exact", pathGlob: "*"));
            Assert.True(mixedScope.GetProperty("partial").GetBoolean());
            Assert.Equal("unsupported_language_files_skipped",
                mixedScope.GetProperty("partialReason").GetString());
            Assert.Contains("fsx", mixedScope.GetProperty("unsupportedLanguages")
                .EnumerateArray().Select(language => language.GetString()));

            using var queries = new IndexQueries(dbPath);
            Assert.Equal(2, queries.ProjectsContaining("Core/Library.fs").Count);
            Assert.Equal(2, queries.SearchSymbols("Pair", "exact", ["record"], 10).Count);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FSharpParseFailuresRemainVisibleToColdAndDeltaSymbolSearch()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-symbol-parse-coverage").FullName;
        try
        {
            WriteProject(root, "Core", "Core.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """,
                ("Library.fs",
                    "module Core.Library\nlet previouslyValidMarker = 1\n"));
            File.WriteAllText(Path.Combine(root, "Scratch.fsx"),
                "let scriptOnlyMarker = 1\n");

            string deltaDbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, deltaDbPath);
            File.WriteAllText(Path.Combine(root, "Core", "Library.fs"),
                "module Core.Library\nlet previouslyValidMarker = (\n");
            using (var store = new IndexStore(deltaDbPath, createNew: false))
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                    ["Core/Library.fs"]);
                Assert.Equal(1, refresh.ChangedFiles);
            }
            AssertParseFailureDisclosure(deltaDbPath);

            string coldDbPath = Path.Combine(root, ".phoenix", "cold-parse.db");
            IndexBuilder.Build(root, coldDbPath);
            AssertParseFailureDisclosure(coldDbPath);
        }
        finally { Cleanup(root); }

        void AssertParseFailureDisclosure(string dbPath)
        {
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement result = Parse(tools.SearchSymbol("previouslyValidMarker",
                match: "exact", pathGlob: "Core/Library.fs"));
            Assert.Empty(result.GetProperty("symbols").EnumerateArray());
            Assert.True(result.GetProperty("partial").GetBoolean());
            Assert.Equal("fsharp_parse_failed",
                result.GetProperty("partialReason").GetString());
            JsonElement coverage = result.GetProperty("fsharpParseCoverage");
            Assert.Equal(1, coverage.GetProperty("failedFiles").GetInt32());
            Assert.Equal(1, coverage.GetProperty("totalFailureFiles").GetInt32());
            Assert.Equal(1, coverage.GetProperty("failedContexts").GetInt32());
            Assert.Equal(1, coverage.GetProperty("totalContexts").GetInt32());

            JsonElement combined = Parse(tools.SearchSymbol("missingMarker",
                match: "exact", pathGlob: "*"));
            Assert.Equal(
                "fsharp_parse_failed; unsupported_language_files_skipped",
                combined.GetProperty("partialReason").GetString());
            Assert.Equal(
                ["fsharp_parse_failed", "unsupported_language_files_skipped"],
                combined.GetProperty("partialReasons").EnumerateArray()
                    .Select(reason => reason.GetString()!).ToArray());

            JsonElement csharpOnly = Parse(tools.SearchSymbol("missingMarker",
                match: "exact", lang: "csharp"));
            Assert.Equal("csharp", csharpOnly.GetProperty("languageScope").GetString());
            Assert.False(csharpOnly.TryGetProperty("partial", out _));
            Assert.False(csharpOnly.TryGetProperty("fsharpParseCoverage", out _));

            JsonElement fsharpOnly = Parse(tools.SearchSymbol("missingMarker",
                match: "exact", lang: "fsharp"));
            Assert.Equal("fsharp", fsharpOnly.GetProperty("languageScope").GetString());
            Assert.True(fsharpOnly.GetProperty("partial").GetBoolean());
            Assert.Equal("fsharp_parse_failed",
                fsharpOnly.GetProperty("partialReason").GetString());

            JsonElement invalidLanguage = Parse(tools.SearchSymbol("missingMarker",
                match: "exact", lang: "vb"));
            Assert.Equal("bad_request", invalidLanguage.GetProperty("error").GetString());
            Assert.Equal("lang", invalidLanguage.GetProperty("field").GetString());
            Assert.Equal(["csharp", "fsharp"], invalidLanguage.GetProperty("validValues")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
        }
    }

    [Fact]
    public void StoredFSharpParsingContextsStopAtApprovedBudgetDeterministically()
    {
        var owners = Enumerable.Range(0, 65)
            .Select(index => (
                ProjectPath: $"Owner{index:D2}/Owner{index:D2}.fsproj",
                TargetFrameworks: "net9.0",
                ProjectXml: $"""
                    <Project>
                      <PropertyGroup>
                        <TargetFramework>net9.0</TargetFramework>
                        <DefineConstants>CONTEXT_{index:D2}</DefineConstants>
                      </PropertyGroup>
                    </Project>
                    """))
            .Reverse()
            .ToArray();

        FSharpParsingContextSelection[] selections = owners
            .Select(owner => FSharpSyntaxIndexer.ParsingContextsForProject(
                owner.ProjectPath, owner.TargetFrameworks, owner.ProjectXml))
            .ToArray();
        FSharpParsingContextSelection selection =
            FSharpSyntaxIndexer.CombineParsingContexts(selections);

        Assert.Equal(FSharpSyntaxIndexer.MaxStoredParseContexts,
            selection.Contexts.Count);
        Assert.Equal(65, selection.TotalContextCount);
        Assert.Equal(1, selection.TruncatedContextCount);
        Assert.Equal(1, selection.TruncatedOwnerProjectCount);
        Assert.DoesNotContain(selection.Contexts,
            context => context.Contains("--define:CONTEXT_64", StringComparer.Ordinal));

        FSharpParsingContextSelection boundary =
            FSharpSyntaxIndexer.CombineParsingContexts(selections.Take(64));
        Assert.Equal(64, boundary.Contexts.Count);
        Assert.Equal(64, boundary.TotalContextCount);
        Assert.Equal(0, boundary.TruncatedContextCount);
        Assert.Equal(0, boundary.TruncatedOwnerProjectCount);
    }

    [Fact]
    public void StoredFSharpContextBudgetRepresentsEachCompileOwnerBeforeOrdinalFill()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-symbol-owner-fair-context-limit").FullName;
        try
        {
            string ownerATargetFrameworks = string.Join(';', Enumerable.Range(0, 64)
                .Select(index => $"net9.0-platform{index:D2}"));
            Directory.CreateDirectory(Path.Combine(root, "OwnerA"));
            Directory.CreateDirectory(Path.Combine(root, "OwnerB"));
            Directory.CreateDirectory(Path.Combine(root, "Shared"));
            string ownerAProjectXml = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>{ownerATargetFrameworks}</TargetFrameworks>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="../Shared/Library.fs" /></ItemGroup>
                </Project>
                """;
            string ownerBProjectXml = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <DefineConstants>ZZZ_OWNER_B</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="../Shared/Library.fs" /></ItemGroup>
                </Project>
                """;
            File.WriteAllText(Path.Combine(root, "OwnerA", "OwnerA.fsproj"),
                ownerAProjectXml);
            File.WriteAllText(Path.Combine(root, "OwnerB", "OwnerB.fsproj"),
                ownerBProjectXml);
            string sharedSource = """
                module Shared.Library
                #if ZZZ_OWNER_B
                let ownerBContextMarker = 99
                #else
                #if NET9_0_PLATFORM63
                let ownerATruncatedContextMarker = 63
                #else
                let ownerARetainedContextMarker = 1
                #endif
                #endif
                """;
            File.WriteAllText(Path.Combine(root, "Shared", "Library.fs"), sharedSource);

            FSharpParsingContextSelection ownerASelection =
                FSharpSyntaxIndexer.ParsingContextsForProject(
                    "OwnerA/OwnerA.fsproj", ownerATargetFrameworks, ownerAProjectXml);
            FSharpParsingContextSelection ownerBSelection =
                FSharpSyntaxIndexer.ParsingContextsForProject(
                    "OwnerB/OwnerB.fsproj", "net9.0", ownerBProjectXml);
            Assert.Contains(ownerBSelection.Contexts,
                context => context.Contains("--define:ZZZ_OWNER_B", StringComparer.Ordinal));
            ParsedFSharpFile ownerBParsed = FSharpSyntaxIndexer.Parse(
                "Shared/Library.fs", sharedSource, ownerBSelection);
            Assert.Contains(ownerBParsed.Symbols,
                symbol => symbol.Name == "ownerBContextMarker");
            FSharpParsingContextSelection combined =
                FSharpSyntaxIndexer.CombineParsingContexts(
                    [ownerASelection, ownerBSelection]);
            Assert.Contains(combined.Contexts,
                context => context.Contains("--define:ZZZ_OWNER_B", StringComparer.Ordinal));
            Assert.Equal(1, combined.TruncatedOwnerProjectCount);
            ParsedFSharpFile directlyParsed = FSharpSyntaxIndexer.Parse(
                "Shared/Library.fs", sharedSource, combined);
            Assert.Contains(directlyParsed.Symbols,
                symbol => symbol.Name == "ownerBContextMarker");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement ownerB = Parse(tools.SearchSymbol("ownerBContextMarker",
                match: "exact", pathGlob: "Shared/Library.fs"));
            Assert.Single(ownerB.GetProperty("symbols").EnumerateArray());
            JsonElement ownerA = Parse(tools.SearchSymbol("ownerARetainedContextMarker",
                match: "exact", pathGlob: "Shared/Library.fs"));
            Assert.Single(ownerA.GetProperty("symbols").EnumerateArray());
            JsonElement omitted = Parse(tools.SearchSymbol("ownerATruncatedContextMarker",
                match: "exact", pathGlob: "Shared/Library.fs"));
            Assert.Empty(omitted.GetProperty("symbols").EnumerateArray());

            Assert.Equal("fsharp_parse_contexts_truncated",
                ownerB.GetProperty("partialReason").GetString());
            JsonElement coverage = ownerB.GetProperty("fsharpParseCoverage");
            Assert.Equal(65, coverage.GetProperty("totalContexts").GetInt32());
            Assert.Equal(64, coverage.GetProperty("processedContexts").GetInt32());
            Assert.Equal(1, coverage.GetProperty("truncatedContexts").GetInt32());
            Assert.Equal(1,
                coverage.GetProperty("truncatedOwnerProjects").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void TruncatedFSharpContextsWithEveryProcessedFailureRemainPartial()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-symbol-truncated-failures").FullName;
        try
        {
            string targetFrameworks = string.Join(';', Enumerable.Range(0, 65)
                .Select(index => $"net9.0-platform{index:D2}"));
            WriteProject(root, "Core", "Core.fsproj",
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>{targetFrameworks}</TargetFrameworks>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """,
                ("Library.fs",
                    """
                    module Core.Library
                    #if NET9_0_PLATFORM64
                    let omittedRecoveryMarker = 1
                    #else
                    let everyProcessedContextFails = (
                    #endif
                    """));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement result = Parse(tools.SearchSymbol("omittedRecoveryMarker",
                match: "exact", pathGlob: "Core/Library.fs"));
            Assert.Empty(result.GetProperty("symbols").EnumerateArray());
            Assert.Equal(
                ["fsharp_parse_failed", "fsharp_parse_contexts_truncated"],
                result.GetProperty("partialReasons").EnumerateArray()
                    .Select(reason => reason.GetString()!).ToArray());
            JsonElement coverage = result.GetProperty("fsharpParseCoverage");
            Assert.Equal(1, coverage.GetProperty("failedFiles").GetInt32());
            Assert.Equal(1, coverage.GetProperty("partialFailureFiles").GetInt32());
            Assert.Equal(0, coverage.GetProperty("totalFailureFiles").GetInt32());
            Assert.Equal(1, coverage.GetProperty("truncatedFiles").GetInt32());
            Assert.Equal(64, coverage.GetProperty("failedContexts").GetInt32());
            Assert.Equal(65, coverage.GetProperty("totalContexts").GetInt32());
            Assert.Equal(64, coverage.GetProperty("processedContexts").GetInt32());
            Assert.Equal(1, coverage.GetProperty("truncatedContexts").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void StoredFSharpContextTruncationIsHonestAcrossColdAndDelta()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-symbol-context-limit").FullName;
        try
        {
            string targetFrameworks = string.Join(';', Enumerable.Range(0, 65)
                .Select(index => $"net9.0-platform{index:D2}"));
            WriteProject(root, "Core", "Core.fsproj",
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>{targetFrameworks}</TargetFrameworks>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """,
                ("Library.fs",
                    """
                    module Core.Library
                    #if NET9_0_PLATFORM64
                    let omittedContextMarker = 64
                    #else
                    let retainedContextMarker = 1
                    #endif
                    """));

            string deltaDbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, deltaDbPath);
            AssertTruncationDisclosure(deltaDbPath, totalContexts: 65,
                truncatedContexts: 1);

            targetFrameworks += ";net9.0-platform65";
            File.WriteAllText(Path.Combine(root, "Core", "Core.fsproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>{targetFrameworks}</TargetFrameworks>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """);
            using (var store = new IndexStore(deltaDbPath, createNew: false))
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                    ["Core/Core.fsproj"]);
                Assert.Equal(1, refresh.ChangedFiles);
            }
            AssertTruncationDisclosure(deltaDbPath, totalContexts: 66,
                truncatedContexts: 2);

            string coldDbPath = Path.Combine(root, ".phoenix", "cold-context-limit.db");
            IndexBuilder.Build(root, coldDbPath);
            AssertTruncationDisclosure(coldDbPath, totalContexts: 66,
                truncatedContexts: 2);
        }
        finally { Cleanup(root); }

        void AssertTruncationDisclosure(string dbPath, int totalContexts,
            int truncatedContexts)
        {
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement retained = Parse(tools.SearchSymbol("retainedContextMarker",
                match: "exact", pathGlob: "Core/Library.fs"));
            Assert.Single(retained.GetProperty("symbols").EnumerateArray());
            Assert.True(retained.GetProperty("partial").GetBoolean());
            Assert.Equal("fsharp_parse_contexts_truncated",
                retained.GetProperty("partialReason").GetString());
            Assert.Equal(["fsharp_parse_contexts_truncated"],
                retained.GetProperty("partialReasons").EnumerateArray()
                    .Select(reason => reason.GetString()!).ToArray());
            JsonElement coverage = retained.GetProperty("fsharpParseCoverage");
            Assert.Equal(0, coverage.GetProperty("failedFiles").GetInt32());
            Assert.Equal(1, coverage.GetProperty("truncatedFiles").GetInt32());
            Assert.Equal(0, coverage.GetProperty("failedContexts").GetInt32());
            Assert.Equal(totalContexts, coverage.GetProperty("totalContexts").GetInt32());
            Assert.Equal(64, coverage.GetProperty("processedContexts").GetInt32());
            Assert.Equal(truncatedContexts,
                coverage.GetProperty("truncatedContexts").GetInt32());

            JsonElement omitted = Parse(tools.SearchSymbol("omittedContextMarker",
                match: "exact", pathGlob: "Core/Library.fs"));
            Assert.Empty(omitted.GetProperty("symbols").EnumerateArray());
            Assert.Equal("fsharp_parse_contexts_truncated",
                omitted.GetProperty("partialReason").GetString());
        }
    }

    [Fact]
    public void FSharpProjectOptionFailuresRemainVisibleToColdAndDeltaSymbolSearch()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-symbol-option-coverage").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "BrokenOwner");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(Path.Combine(root, "ValidOwner"));
            Directory.CreateDirectory(Path.Combine(root, "Shared"));
            string projectPath = Path.Combine(projectDirectory, "BrokenOwner.fsproj");
            File.WriteAllText(projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="../Shared/Library.fs" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "ValidOwner", "ValidOwner.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="../Shared/Library.fs" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Shared", "Library.fs"),
                "module Core.Library\nlet optionCoverageMarker = 1\n");

            string deltaDbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, deltaDbPath);
            File.WriteAllText(projectPath, "<Project><PropertyGroup>");
            using (var store = new IndexStore(deltaDbPath, createNew: false))
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                    ["BrokenOwner/BrokenOwner.fsproj"]);
                Assert.Equal(1, refresh.ChangedFiles);
            }
            AssertOptionFailureDisclosure(deltaDbPath);

            string coldDbPath = Path.Combine(root, ".phoenix", "cold-options.db");
            IndexBuilder.Build(root, coldDbPath);
            AssertOptionFailureDisclosure(coldDbPath);
        }
        finally { Cleanup(root); }

        void AssertOptionFailureDisclosure(string dbPath)
        {
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement result = Parse(tools.SearchSymbol("optionCoverageMarker",
                match: "exact", pathGlob: "Shared/Library.fs"));
            Assert.True(result.GetProperty("partial").GetBoolean());
            Assert.Contains("fsharp_project_options_unavailable",
                result.GetProperty("partialReasons").EnumerateArray()
                    .Select(reason => reason.GetString()));
            JsonElement coverage = result.GetProperty("fsharpProjectOptionCoverage");
            Assert.Equal(1, coverage.GetProperty("affectedFiles").GetInt32());
            Assert.Equal(1,
                coverage.GetProperty("failedProjectFileContexts").GetInt32());
            Assert.Contains("fsharp_project_options_unavailable",
                coverage.GetProperty("reasons").EnumerateArray()
                    .Select(reason => reason.GetString()));
        }
    }

    [Fact]
    public void MalformedFSharpProjectAddAndDeleteRefreshGlobalOptionCoverage()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-symbol-global-option-coverage").FullName;
        string deltaDbPath = IndexBuilder.DefaultDbPath(root);
        try
        {
            string targetFrameworks = string.Join(';', Enumerable.Range(0, 65)
                .Select(index => $"net9.0-platform{index:D2}"));
            WriteProject(root, "ValidOwner", "ValidOwner.fsproj",
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>{targetFrameworks}</TargetFrameworks></PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """,
                ("Library.fs",
                    """
                    module ValidOwner.Library
                    #if NET9_0_PLATFORM00
                    let brokenGlobalContextMarker = (
                    #else
                    let globalCoverageMarker = 1
                    #endif
                    """));

            IndexBuilder.Build(root, deltaDbPath);
            AssertGlobalFailureDisclosure(deltaDbPath, expected: false);

            string brokenDirectory = Path.Combine(root, "BrokenOwner");
            Directory.CreateDirectory(brokenDirectory);
            string brokenProjectPath = Path.Combine(brokenDirectory, "BrokenOwner.fsproj");
            File.WriteAllText(brokenProjectPath, "<Project><PropertyGroup>");
            (RefreshResult added, string[] parsedOnAdd) = RefreshWithParseTrace(
                "BrokenOwner/BrokenOwner.fsproj");
            Assert.Equal(1, added.AddedFiles);
            Assert.Empty(parsedOnAdd);
            AssertGlobalFailureDisclosure(deltaDbPath, expected: true);

            string coldDbPath = Path.Combine(root, ".phoenix", "cold-global-options.db");
            IndexBuilder.Build(root, coldDbPath);
            AssertGlobalFailureDisclosure(coldDbPath, expected: true);

            File.Delete(brokenProjectPath);
            (RefreshResult deleted, string[] parsedOnDelete) = RefreshWithParseTrace(
                "BrokenOwner/BrokenOwner.fsproj");
            Assert.Equal(1, deleted.DeletedFiles);
            Assert.Empty(parsedOnDelete);
            AssertGlobalFailureDisclosure(deltaDbPath, expected: false);
        }
        finally { Cleanup(root); }

        void AssertGlobalFailureDisclosure(string dbPath, bool expected)
        {
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);
            JsonElement result = Parse(tools.SearchSymbol("globalCoverageMarker",
                match: "exact", pathGlob: "ValidOwner/Library.fs"));
            Assert.Single(result.GetProperty("symbols").EnumerateArray());
            string[] partialReasons = result.TryGetProperty("partialReasons", out JsonElement reasons)
                ? reasons.EnumerateArray().Select(reason => reason.GetString()!).ToArray()
                : [];
            Assert.Equal(expected,
                partialReasons.Contains("fsharp_project_options_unavailable"));
            Assert.Contains("fsharp_parse_failed", partialReasons);
            Assert.Contains("fsharp_parse_contexts_truncated", partialReasons);
            Assert.True(result.GetProperty("partial").GetBoolean());
            JsonElement parseCoverage = result.GetProperty("fsharpParseCoverage");
            Assert.Equal(1, parseCoverage.GetProperty("failedFiles").GetInt32());
            Assert.Equal(1, parseCoverage.GetProperty("partialFailureFiles").GetInt32());
            Assert.Equal(0, parseCoverage.GetProperty("totalFailureFiles").GetInt32());
            Assert.Equal(1, parseCoverage.GetProperty("truncatedFiles").GetInt32());
            Assert.Equal(1, parseCoverage.GetProperty("failedContexts").GetInt32());
            Assert.Equal(65, parseCoverage.GetProperty("totalContexts").GetInt32());
            Assert.Equal(64, parseCoverage.GetProperty("processedContexts").GetInt32());
            Assert.Equal(1, parseCoverage.GetProperty("truncatedContexts").GetInt32());
            if (expected)
            {
                Assert.Contains("fsharp_project_options_unavailable",
                    result.GetProperty("fsharpProjectOptionCoverage")
                        .GetProperty("reasons").EnumerateArray()
                        .Select(reason => reason.GetString()));
            }
            else
            {
                Assert.DoesNotContain("fsharp_project_options_unavailable", partialReasons);
            }
        }

        (RefreshResult Result, string[] ParsedPaths) RefreshWithParseTrace(string changedPath)
        {
            var parsedPaths = new List<string>();
            var gate = new object();
            FSharpSyntaxIndexer.BeforeParseForTest = path =>
            {
                lock (gate) parsedPaths.Add(path);
            };
            try
            {
                using var store = new IndexStore(deltaDbPath, createNew: false);
                RefreshResult refresh = DeltaRefresher.Refresh(store, root, [changedPath]);
                lock (gate) return (refresh, parsedPaths.ToArray());
            }
            finally
            {
                FSharpSyntaxIndexer.BeforeParseForTest = null;
            }
        }
    }

    [Fact]
    public void SuccessfulFSharpContextsRemainSearchableWhenAnotherContextFails()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-partial-parse-coverage").FullName;
        try
        {
            WriteProject(root, "Core", "Core.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """,
                ("Library.fs",
                    """
                    module Core.Library
                    #if NET8_0
                    let brokenContextMarker = (
                    #else
                    let survivingContextMarker = 9
                    #endif
                    """));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement result = Parse(tools.SearchSymbol("survivingContextMarker",
                match: "exact", pathGlob: "Core/Library.fs"));
            Assert.Single(result.GetProperty("symbols").EnumerateArray());
            Assert.Equal("fsharp_parse_failed",
                result.GetProperty("partialReason").GetString());
            JsonElement coverage = result.GetProperty("fsharpParseCoverage");
            Assert.Equal(1, coverage.GetProperty("failedFiles").GetInt32());
            Assert.Equal(1, coverage.GetProperty("partialFailureFiles").GetInt32());
            Assert.Equal(0, coverage.GetProperty("totalFailureFiles").GetInt32());
            Assert.Equal(1, coverage.GetProperty("failedContexts").GetInt32());
            Assert.Equal(2, coverage.GetProperty("totalContexts").GetInt32());
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("Pair", "type Pair<'T -> 'U, int> = Pair", 2)]
    [InlineData("Outer", "type Outer<Inner<int, string>, bool> = Outer", 2)]
    [InlineData("Pair", "type PairContainer = member Pair<'T, 'U>", 2)]
    [InlineData("Plain", "type Plain = Plain", 0)]
    [InlineData("", "", 0)]
    public void FSharpGenericArityUsesTheDeclaredNameAndIgnoresFunctionArrows(
        string name, string signature, int expected)
    {
        Assert.Equal(expected, FSharpSyntaxIndexer.GenericArity(name, signature));
    }

    [Fact]
    public void FSharpTypeKindsReceiveTheExactNameTypePreference()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-type-ranking").FullName;
        try
        {
            string dbPath = Path.Combine(root, "index.db");
            using (var store = new IndexStore(dbPath, createNew: true))
            {
                using var tx = store.BeginTransaction();
                long fileId = store.InsertFile(tx, "Types.fs", 1, 1, 1, "fs", 6,
                    isGenerated: false, hasTestAttrs: false);
                store.InsertContent(tx, fileId, "type ranking fixture");
                store.InsertSymbols(tx, fileId,
                [
                    Symbol(0, "function", "SharedUnion", 1),
                    Symbol(1, "union", "SharedUnion", 2),
                    Symbol(2, "value", "SharedType", 3),
                    Symbol(3, "type", "SharedType", 4),
                    Symbol(4, "value", "SharedException", 5),
                    Symbol(5, "exception", "SharedException", 6),
                ]);
                tx.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            Assert.Equal("union", queries.SearchSymbols(
                "SharedUnion", "exact", null, 10)[0].Kind);
            Assert.Equal("type", queries.SearchSymbols(
                "SharedType", "exact", null, 10)[0].Kind);
            Assert.Equal("exception", queries.SearchSymbols(
                "SharedException", "exact", null, 10)[0].Kind);
        }
        finally { Cleanup(root); }

        static SymbolRow Symbol(int ordinal, string kind, string name, int line) =>
            new(ordinal, -1, kind, name, "Ranking", null, name, "public",
                line, line, false, 0, null);
    }

    [Fact]
    public void CSharpOnlyDeltaSkipsTheFSharpFileInventoryScan()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-delta-inventory").FullName;
        try
        {
            WriteProject(root, "Core", "Core.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ("Library.cs", "namespace Core; public sealed class Before { }"));
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            File.WriteAllText(Path.Combine(root, "Core", "Library.cs"),
                "namespace Core; public sealed class After { }");

            using var store = new IndexStore(dbPath, createNew: false);
            long before = store.FileIdPathLangExecutionCountForTest;
            RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                ["Core/Library.cs"]);
            Assert.Equal(1, refresh.ChangedFiles);
            Assert.Equal(before, store.FileIdPathLangExecutionCountForTest);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NameBasedRoslynResolutionIgnoresSameNamedFSharpDeclarations()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-csharp-name-collision").FullName;
        try
        {
            WriteProject(root, "A_FSharp", "Library.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """,
                ("Library.fs",
                    "namespace Collision\ntype SharedCollision() = class end\n"));
            WriteProject(root, "Z_CSharp", "Library.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ("Library.cs",
                    "namespace Collision; public sealed class SharedCollision { }"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);

            JsonElement indexed = Parse(tools.Definition(
                name: "SharedCollision", mode: "indexed"));
            Assert.NotEmpty(indexed.GetProperty("declarations").EnumerateArray());
            Assert.All(indexed.GetProperty("declarations").EnumerateArray(),
                declaration => Assert.Equal("Z_CSharp/Library.cs",
                    declaration.GetProperty("path").GetString()));

            if (!semantic.FrameworkRefsAvailable) return;

            JsonElement definition = SemanticRetry.ParseExactWithRetry(() =>
                tools.Definition(name: "SharedCollision", mode: "semantic",
                    timeoutMs: 60_000));
            Assert.Contains(definition.GetProperty("declarations").EnumerateArray(),
                declaration => declaration.GetProperty("path").GetString() ==
                               "Z_CSharp/Library.cs");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FSharpProjectOptionRefreshReindexesConditionalDeclarations()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-symbol-options").FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Core");
            Directory.CreateDirectory(projectDirectory);
            string projectPath = Path.Combine(projectDirectory, "Core.fsproj");
            string sourcePath = Path.Combine(projectDirectory, "Conditional.fs");

            void WriteProjectDefine(string define) => File.WriteAllText(projectPath,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
                    <DefineConstants>{define}</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Conditional.fs" /></ItemGroup>
                </Project>
                """);

            WriteProjectDefine("FIRST_BRANCH");
            File.WriteAllText(sourcePath,
                """
                module Conditional
                #if FIRST_BRANCH
                let firstBranchMarker = 1
                #else
                let secondBranchMarker = 2
                #endif
                #if NET8_0
                let netEightContextMarker = 8
                #else
                let netNineContextMarker = 9
                #endif
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Single(queries.SearchSymbols(
                    "firstBranchMarker", "exact", ["value"], 5));
                Assert.Empty(queries.SearchSymbols(
                    "secondBranchMarker", "exact", ["value"], 5));
                Assert.Single(queries.SearchSymbols(
                    "netEightContextMarker", "exact", ["value"], 5));
                Assert.Single(queries.SearchSymbols(
                    "netNineContextMarker", "exact", ["value"], 5));
            }

            WriteProjectDefine("SECOND_BRANCH");
            using (var store = new IndexStore(dbPath, createNew: false))
            {
                RefreshResult refreshed = DeltaRefresher.Refresh(
                    store, root, ["Core/Core.fsproj"]);
                Assert.Equal(1, refreshed.ChangedFiles);
                Assert.True(refreshed.ProjectsRefreshed);
            }
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Empty(queries.SearchSymbols(
                    "firstBranchMarker", "exact", ["value"], 5));
                Assert.Single(queries.SearchSymbols(
                    "secondBranchMarker", "exact", ["value"], 5));
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FSharpProjectOptionDeltaParsesOnlyAffectedFilesOutsideTheWriteTransaction()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-symbol-option-delta-scope").FullName;
        try
        {
            WriteProject(root, "Alpha", "Alpha.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><DefineConstants>FIRST</DefineConstants></PropertyGroup>
                  <ItemGroup><Compile Include="Alpha.fs" /></ItemGroup>
                </Project>
                """,
                ("Alpha.fs", "module Alpha\nlet alphaMarker = 1\n"));
            WriteProject(root, "Beta", "Beta.fsproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><Compile Include="Beta.fs" /></ItemGroup>
                </Project>
                """,
                ("Beta.fs", "module Beta\nlet betaMarker = 2\n"));

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            File.WriteAllText(Path.Combine(root, "Alpha", "Alpha.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><DefineConstants>SECOND</DefineConstants></PropertyGroup>
                  <ItemGroup><Compile Include="Alpha.fs" /></ItemGroup>
                </Project>
                """);

            using var store = new IndexStore(dbPath, createNew: false);
            var parsedPaths = new List<string>();
            var transactionOpenDuringParse = new List<bool>();
            var observationsLock = new object();
            FSharpSyntaxIndexer.BeforeParseForTest = path =>
            {
                bool? transactionWasOpen = null;
                if (path.Equals("Alpha/Alpha.fs", StringComparison.Ordinal))
                {
                    try
                    {
                        using var probe = store.BeginTransaction();
                        probe.Rollback();
                        transactionWasOpen = false;
                    }
                    catch (InvalidOperationException)
                    {
                        transactionWasOpen = true;
                    }
                }
                lock (observationsLock)
                {
                    parsedPaths.Add(path);
                    if (transactionWasOpen is { } observed)
                        transactionOpenDuringParse.Add(observed);
                }
            };
            try
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                    ["Alpha/Alpha.fsproj"]);
                Assert.Equal(1, refresh.ChangedFiles);
            }
            finally
            {
                FSharpSyntaxIndexer.BeforeParseForTest = null;
            }

            Assert.Equal(["Alpha/Alpha.fs"], parsedPaths);
            Assert.All(transactionOpenDuringParse, Assert.False);
        }
        finally
        {
            FSharpSyntaxIndexer.BeforeParseForTest = null;
            Cleanup(root);
        }
    }

    [Fact]
    public async Task FSharpParseTestHookIsExecutionContextLocal()
    {
        int observed = 0;
        FSharpSyntaxIndexer.BeforeParseForTest = _ => Interlocked.Increment(ref observed);
        try
        {
            Task foreignParse;
            using (ExecutionContext.SuppressFlow())
            {
                foreignParse = Task.Run(() => FSharpSyntaxIndexer.Parse(
                    "Foreign/Library.fs", "module Foreign.Library\nlet marker = 1\n"));
            }
            await foreignParse;
            Assert.Equal(0, Volatile.Read(ref observed));

            _ = FSharpSyntaxIndexer.Parse(
                "Local/Library.fs", "module Local.Library\nlet marker = 1\n");
            Assert.Equal(1, Volatile.Read(ref observed));
        }
        finally
        {
            FSharpSyntaxIndexer.BeforeParseForTest = null;
        }
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
                SymbolHit symbol = Assert.Single(q.SearchSymbols(
                    "deltaMarkerOne", "exact", ["value"], 5));
                Assert.Equal("Core/Library.fs", symbol.FilePath);
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
                Assert.Empty(q.SearchSymbols("deltaMarkerOne", "exact", null, 5));
                Assert.Single(q.SearchSymbols("deltaMarkerTwo", "exact", ["value"], 5));
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
                Assert.Empty(q.SearchSymbols("deltaMarkerTwo", "exact", null, 5));
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FSharpStoredSymbolsAndOrphansMatchColdBuildAfterDeltaRefresh()
    {
        string root = Directory.CreateTempSubdirectory("codenav-fsharp-cold-delta-parity")
            .FullName;
        try
        {
            string projectDirectory = Path.Combine(root, "Core");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "Core.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Owned.fs" /></ItemGroup>
                </Project>
                """);
            string ownedPath = Path.Combine(projectDirectory, "Owned.fs");
            File.WriteAllText(ownedPath,
                "module Core.Owned\nlet deltaInitialMarker = 1\n");

            string deltaDbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, deltaDbPath);

            File.WriteAllText(ownedPath,
                "module Core.Owned\nlet deltaFinalMarker = 2\n");
            File.WriteAllText(Path.Combine(root, "Loose.fs"),
                "module LooseImplementation\nlet looseImplementationMarker = 3\n");
            File.WriteAllText(Path.Combine(root, "Loose.fsi"),
                "module LooseSignature\nval looseSignatureMarker: int\n");
            File.WriteAllText(Path.Combine(root, "Loose.fsx"),
                "let looseScriptMarker = 4\n");

            using (var store = new IndexStore(deltaDbPath, createNew: false))
            {
                RefreshResult refresh = DeltaRefresher.Refresh(store, root,
                [
                    "Core/Owned.fs",
                    "Loose.fs",
                    "Loose.fsi",
                    "Loose.fsx",
                ]);
                Assert.Equal(1, refresh.ChangedFiles);
                Assert.Equal(3, refresh.AddedFiles);
            }

            FSharpStoredSnapshot delta = ReadFSharpStoredSnapshot(deltaDbPath);
            string coldDbPath = Path.Combine(root, ".codenav", "cold-parity.db");
            IndexBuilder.Build(root, coldDbPath);
            FSharpStoredSnapshot cold = ReadFSharpStoredSnapshot(coldDbPath);

            Assert.Equal<string>(cold.SymbolRows, delta.SymbolRows);
            Assert.Equal<string>(cold.OrphanPaths, delta.OrphanPaths);
            Assert.Equal(cold.OrphanedFiles, delta.OrphanedFiles);
            Assert.Equal(["Loose.fs", "Loose.fsi"], delta.OrphanPaths);
            Assert.Equal(2, delta.OrphanedFiles);
            Assert.Contains(delta.SymbolRows, row =>
                row.Contains("deltaFinalMarker", StringComparison.Ordinal));
            Assert.DoesNotContain(delta.SymbolRows, row =>
                row.Contains("deltaInitialMarker", StringComparison.Ordinal));
            Assert.Contains(delta.SymbolRows, row =>
                row.Contains("looseImplementationMarker", StringComparison.Ordinal));
            Assert.Contains(delta.SymbolRows, row =>
                row.Contains("looseSignatureMarker", StringComparison.Ordinal));
            Assert.DoesNotContain(delta.SymbolRows, row =>
                row.Contains("looseScriptMarker", StringComparison.Ordinal));
        }
        finally { Cleanup(root); }
    }

    private static FSharpStoredSnapshot ReadFSharpStoredSnapshot(string dbPath)
    {
        string[] symbolRows;
        string[] orphanPaths;
        using (var store = new IndexStore(dbPath, createNew: false))
        using (SqliteConnection connection = store.OpenReader())
        {
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT f.path, s.kind, s.name, COALESCE(s.ns, ''),
                           COALESCE(s.container, ''), s.signature, s.accessibility,
                           s.start_line, s.end_line, s.is_partial, s.arity,
                           COALESCE(s.attr_markers, ''), COALESCE(s.modifiers, ''),
                           COALESCE(s.accessors, ''), s.declaration_key,
                           COALESCE(parent.declaration_key, '')
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    LEFT JOIN symbols parent ON parent.id = s.parent_id
                    WHERE f.lang = 'fs'
                    ORDER BY f.path, s.start_line, s.end_line, s.kind, s.name,
                             s.declaration_key, COALESCE(parent.declaration_key, '')
                    """;
                using SqliteDataReader reader = command.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                {
                    object?[] values = Enumerable.Range(0, reader.FieldCount)
                        .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index))
                        .ToArray();
                    rows.Add(JsonSerializer.Serialize(values));
                }
                symbolRows = rows.ToArray();
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT path
                    FROM files
                    WHERE lang = 'fs' AND lower(path) NOT LIKE '%.fsx'
                      AND NOT EXISTS (
                        SELECT 1 FROM compile_items ci WHERE ci.file_id = files.id)
                    ORDER BY path
                    """;
                using SqliteDataReader reader = command.ExecuteReader();
                var paths = new List<string>();
                while (reader.Read()) paths.Add(reader.GetString(0));
                orphanPaths = paths.ToArray();
            }
        }

        using var queries = new IndexQueries(dbPath);
        return new FSharpStoredSnapshot(symbolRows, orphanPaths,
            queries.Overview().OrphanedFiles);
    }

    private sealed record FSharpStoredSnapshot(
        string[] SymbolRows,
        string[] OrphanPaths,
        long OrphanedFiles);

    private static void WriteMixedWorkspace(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Build"));
        File.WriteAllText(Path.Combine(root, "Build", "Stage2.props"),
            "<Project><PropertyGroup><PhoenixFSharpEvalMarker>bounded</PhoenixFSharpEvalMarker></PropertyGroup></Project>");

        WriteProject(root, "Core", "Core.fsproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
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
                <TargetFramework>net10.0</TargetFramework>
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
                <TargetFramework>net10.0</TargetFramework>
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
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }
}
