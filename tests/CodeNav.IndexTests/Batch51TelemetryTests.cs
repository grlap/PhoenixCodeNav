using CodeNav.Core.Diagnostics;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

/// <summary>
/// Batch 51 (epuc.1): bounded semantic-operation telemetry. Pins the four contracts the
/// portal (x5ls) and the field's cold-start analysis depend on:
/// (1) a semantic operation emits one semanticOp JSONL record into
///     {workspace}/.codenav/telemetry/phoenix-{pid}-*.jsonl carrying ITS OWN per-call stage
///     split (ownerLoad — review F2: not some ambient last-load's stats);
/// (2) privacy — records carry no absolute paths (the portal spec forbids them; a drive-rooted
///     or UNC path in any record is a red);
/// (3) the in-memory ring is bounded (the portal reads it live; unbounded would leak);
/// (4) the file cap truncates honestly in-band and never kills Emit/ring (review F5).
/// </summary>
public class Batch51TelemetryTests
{
    [Fact]
    public void SemanticColdStartContractCoversFallbackTerminalAndExactPaths()
    {
        string root = Directory.CreateTempSubdirectory("codenav-51-cold-phases").FullName;
        try
        {
            string library = Path.Combine(root, "Library");
            string consumer = Path.Combine(root, "Consumer");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(consumer);
            File.WriteAllText(Path.Combine(library, "Library.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(library, "Target.cs"),
                "namespace ColdPhaseFixture { public class Target { } }");
            File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Library/Library.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(consumer, "Use.cs"),
                "namespace ColdPhaseFixture { public class Use : Target { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            using var semantic = new SemanticService(manager);
            manager.Start();
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(30),
                "cold-phase telemetry index did not become fresh");
            var tools = new CodeNav.Mcp.NavigationTools(manager, semantic);

            void AssertResponseAndTelemetry(string tool, Func<string> invoke,
                string? expectedTelemetryReason, Action<System.Text.Json.JsonElement> assertResponse,
                IReadOnlyCollection<string>? requiredTimingFields = null)
            {
                int before = SemanticOpLines(root, tool).Count;
                string raw = invoke();
                Assert.True(CodeNav.Mcp.Json.Utf8Bytes(raw) <=
                            CodeNav.Mcp.Json.HardBudgetBytes);
                using var responseDocument = System.Text.Json.JsonDocument.Parse(raw);
                var responseRoot = responseDocument.RootElement;
                assertResponse(responseRoot);
                var responseCold = responseRoot.GetProperty("timing")
                    .GetProperty("semanticColdStart");
                Assert.True(responseCold.GetProperty("loadMs").GetInt64() >= 0);
                foreach (string field in requiredTimingFields ?? [])
                    Assert.True(responseCold.TryGetProperty(field, out _), field);

                Assert.True(WaitUntil(() => SemanticOpLines(root, tool).Count > before,
                        10_000),
                    $"{tool} semanticOp record did not reach telemetry");
                List<string> lines = SemanticOpLines(root, tool);
                Assert.Equal(before + 1, lines.Count);
                using var telemetryDocument = System.Text.Json.JsonDocument.Parse(lines[^1]);
                var telemetryRoot = telemetryDocument.RootElement;
                if (expectedTelemetryReason is not null)
                    Assert.Equal(expectedTelemetryReason,
                        telemetryRoot.GetProperty("reason").GetString());
                var telemetryCold = telemetryRoot.GetProperty("semanticColdStart");
                Assert.Equal(responseCold.EnumerateObject().Select(property => property.Name),
                    telemetryCold.EnumerateObject().Select(property => property.Name));
                foreach (var property in responseCold.EnumerateObject())
                {
                    Assert.Equal(property.Value.ToString(),
                        telemetryCold.GetProperty(property.Name).ToString());
                }
            }

            tools.TestOnlySemanticFailureReason = "cluster_cold_load";
            try
            {
                (string Tool, Func<string> Invoke, string Confidence)[] fallbackCases =
                [
                    ("definition", () => tools.Definition(name: "Target", mode: "auto",
                        timeoutMs: 60_000), "indexed"),
                    ("implementations", () => tools.Implementations(name: "Target",
                        maxProjects: 0, timeoutMs: 120_000), "heuristic"),
                    ("references", () => tools.References(name: "Target", mode: "auto",
                        maxProjects: 0, samplesPerGroup: 0, timeoutMs: 120_000), "indexed"),
                    ("type_hierarchy", () => tools.TypeHierarchy(name: "Target",
                        maxProjects: 0, timeoutMs: 120_000), "heuristic"),
                ];
                foreach ((string tool, Func<string> invoke, string confidence) in fallbackCases)
                {
                    AssertResponseAndTelemetry(tool, invoke, "cluster_cold_load", response =>
                    {
                        Assert.False(response.TryGetProperty("error", out _), response.ToString());
                        string partialReason = response.GetProperty("partialReason").GetString()!;
                        Assert.StartsWith("cluster_cold_load", partialReason,
                            StringComparison.Ordinal);
                        Assert.Equal(confidence,
                            response.GetProperty("meta").GetProperty("confidence").GetString());
                    });
                }
            }
            finally
            {
                tools.TestOnlySemanticFailureReason = null;
            }

            var declaration = new SemanticDeclaration(
                "ColdPhaseFixture.Target", "T:ColdPhaseFixture.Target", "class",
                null, "ColdPhaseFixture", "Library",
                [new DeclarationSpan("Library/Target.cs", 1, 1, "Library")]);
            var match = new DocumentationIdResolution(
                "Target", "T:ColdPhaseFixture.Target", "Library",
                "Library/Target.cs", 1, 1, declaration);
            var completeCoverage = new DocumentationIdResolutionCoverage(
                1, 1, 1, 1, 1, [], CompilerScanned: true);
            tools.TestOnlyDocumentationIdSeedTimeout = true;
            try
            {
                (string Reason, string Error, string Confidence,
                    Func<DocumentationIdResolutionResult, DocumentationIdResolutionResult>
                        Transform)[] terminalCases =
                [
                    ("documentation_id_seed_timeout", "semantic_unavailable", "indexed",
                        result => result),
                    ("documentation_id_not_found", "symbol_not_found", "exact", result => result with
                    {
                        Matches = [], FailReason = null,
                        MissReason = "documentation_id_not_found",
                        Coverage = completeCoverage,
                    }),
                    ("documentation_id_ambiguous", "symbol_ambiguous", "exact", result => result with
                    {
                        Matches =
                        [
                            match,
                            match with
                            {
                                ProjectName = "Shadow",
                                Declaration = declaration with { Assembly = "Shadow" },
                            },
                        ],
                        FailReason = null, MissReason = null,
                        Coverage = completeCoverage,
                    }),
                    ("documentation_id_coverage_incomplete",
                        "documentation_id_coverage_incomplete", "indexed", result => result with
                    {
                        Matches = [match], FailReason = null, MissReason = null,
                        Coverage = completeCoverage with
                        {
                            CompilerScanned = false,
                            SkippedProjects = ["Skipped"],
                        },
                    }),
                    ("index_snapshot_unavailable", "semantic_unavailable", "indexed",
                        result => result with
                    {
                        Matches = [match], FailReason = null, MissReason = null,
                        Coverage = completeCoverage, SnapshotIdentity = null,
                    }),
                ];
                foreach ((string reason, string error, string confidence,
                             Func<DocumentationIdResolutionResult,
                                  DocumentationIdResolutionResult> transform)
                         in terminalCases)
                {
                    tools.TestOnlyDocumentationIdResolutionTransform = transform;
                    AssertResponseAndTelemetry("definition", () => tools.Definition(
                        documentationCommentId: "T:ColdPhaseFixture.Target",
                        timeoutMs: 60_000), reason, response =>
                    {
                        Assert.Equal(error, response.GetProperty("error").GetString());
                        Assert.Equal(confidence,
                            response.GetProperty("meta").GetProperty("confidence").GetString());
                        switch (reason)
                        {
                            case "documentation_id_seed_timeout":
                            case "index_snapshot_unavailable":
                                Assert.Equal(reason,
                                    response.GetProperty("partialReason").GetString());
                                Assert.False(response.TryGetProperty("reason", out _));
                                break;
                            case "documentation_id_not_found":
                                Assert.Equal(reason, response.GetProperty("reason").GetString());
                                Assert.False(response.TryGetProperty("partialReason", out _));
                                break;
                            case "documentation_id_coverage_incomplete":
                                Assert.Equal(reason, response.GetProperty("reason").GetString());
                                Assert.Equal(reason,
                                    response.GetProperty("partialReason").GetString());
                                break;
                            default:
                                Assert.False(response.TryGetProperty("reason", out _));
                                Assert.False(response.TryGetProperty("partialReason", out _));
                                break;
                        }
                    });
                }
            }
            finally
            {
                tools.TestOnlyDocumentationIdResolutionTransform = null;
                tools.TestOnlyDocumentationIdSeedTimeout = false;
            }

            if (!semantic.FrameworkRefsAvailable) return;

            string coldRaw = tools.References(name: "Target", mode: "semantic",
                maxProjects: 0, samplesPerGroup: 0, timeoutMs: 120000);
            Assert.True(CodeNav.Mcp.Json.Utf8Bytes(coldRaw) <=
                        CodeNav.Mcp.Json.HardBudgetBytes);
            using var coldDocument = System.Text.Json.JsonDocument.Parse(coldRaw);
            var coldRoot = coldDocument.RootElement;
            Assert.False(coldRoot.TryGetProperty("error", out _));
            Assert.Equal(1, coldRoot.GetProperty("totalReferences").GetInt32());
            Assert.Equal("exact",
                coldRoot.GetProperty("meta").GetProperty("confidence").GetString());
            var publicTiming = coldRoot.GetProperty("timing");
            var publicCold = publicTiming.GetProperty("semanticColdStart");
            Assert.True(publicCold.GetProperty("cold").GetBoolean());
            Assert.Equal(publicTiming.GetProperty("clusterLoadMs").GetInt64(),
                publicCold.GetProperty("loadMs").GetInt64());
            string[] timingFields =
            [
                "cold", "loadMs", "preparationMs", "metadataReferenceWorkMs",
                "compilationMs", "resolutionMs",
            ];
            Assert.Equal(timingFields.OrderBy(value => value),
                publicCold.EnumerateObject().Select(property => property.Name)
                    .OrderBy(value => value));
            foreach (string field in timingFields.Where(field => field.EndsWith("Ms",
                         StringComparison.Ordinal)))
            {
                Assert.True(publicCold.GetProperty(field).TryGetInt64(out long value), field);
                Assert.True(value >= 0, field);
            }

            string telemetryDir = Path.Combine(root, ".codenav", "telemetry");
            Assert.True(WaitUntil(() => Directory.Exists(telemetryDir) &&
                Directory.EnumerateFiles(telemetryDir, "phoenix-*.jsonl").Any(file =>
                    ReadShared(file).Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
                        IsColdReferencesTelemetryLine(line))), 10_000),
                "cold references semanticOp record did not reach telemetry");
            string content = string.Join('\n', Directory
                .EnumerateFiles(telemetryDir, "phoenix-*.jsonl")
                .Select(ReadShared));
            string coldLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .First(IsColdReferencesTelemetryLine);
            using var telemetryDocument = System.Text.Json.JsonDocument.Parse(coldLine);
            var telemetryCold = telemetryDocument.RootElement.GetProperty("semanticColdStart");
            foreach (string field in timingFields)
                Assert.Equal(publicCold.GetProperty(field).ToString(),
                    telemetryCold.GetProperty(field).ToString());
            Assert.DoesNotContain("Target", coldLine);
            Assert.DoesNotContain("Use", coldLine);
            Assert.DoesNotContain("Target.cs", coldLine);
            Assert.DoesNotContain(root, coldLine, StringComparison.OrdinalIgnoreCase);

            string warmRaw = tools.References(name: "Target", mode: "semantic",
                maxProjects: 0, samplesPerGroup: 0, timeoutMs: 120000);
            using var warmDocument = System.Text.Json.JsonDocument.Parse(warmRaw);
            Assert.Equal(coldRoot.GetProperty("totalReferences").GetInt32(),
                warmDocument.RootElement.GetProperty("totalReferences").GetInt32());
            Assert.False(warmDocument.RootElement.GetProperty("timing")
                .GetProperty("semanticColdStart").GetProperty("cold").GetBoolean());

            (string Tool, Func<string> Invoke)[] exactCases =
            [
                ("definition", () => tools.Definition(
                    name: "Target", mode: "semantic", timeoutMs: 60_000)),
                ("implementations", () => tools.Implementations(
                    name: "Target", maxProjects: 0, timeoutMs: 120_000)),
                ("type_hierarchy", () => tools.TypeHierarchy(
                    name: "Target", maxProjects: 0, timeoutMs: 120_000)),
            ];
            foreach ((string tool, Func<string> invoke) in exactCases)
            {
                AssertResponseAndTelemetry(tool, invoke, null, response =>
                {
                    Assert.False(response.TryGetProperty("error", out _), response.ToString());
                    Assert.False(response.TryGetProperty("reason", out _));
                    Assert.False(response.TryGetProperty("partialReason", out _));
                    Assert.Equal("exact",
                        response.GetProperty("meta").GetProperty("confidence").GetString());
                });
            }

            tools.TestOnlyDocumentationIdResolutionTransform = result => result with
            {
                Matches = [],
                FailReason = null,
                MissReason = "documentation_id_not_found",
            };
            try
            {
                AssertResponseAndTelemetry("definition", () => tools.Definition(
                        documentationCommentId: "T:ColdPhaseFixture.Target",
                        timeoutMs: 60_000), "documentation_id_not_found", response =>
                    {
                        Assert.Equal("symbol_not_found",
                            response.GetProperty("error").GetString());
                        Assert.Equal("documentation_id_not_found",
                            response.GetProperty("reason").GetString());
                        Assert.Equal("exact",
                            response.GetProperty("meta").GetProperty("confidence").GetString());
                    }, ["compilationMs", "resolutionMs"]);
            }
            finally
            {
                tools.TestOnlyDocumentationIdResolutionTransform = null;
            }

            tools.TestOnlySemanticFailureReason = "cluster_cold_load";
            try
            {
                string degradedRaw = tools.References(name: "Target", mode: "semantic",
                    maxProjects: 0, samplesPerGroup: 0, timeoutMs: 120000);
                using var degradedDocument = System.Text.Json.JsonDocument.Parse(degradedRaw);
                var degradedCold = degradedDocument.RootElement.GetProperty("timing")
                    .GetProperty("semanticColdStart");
                Assert.True(degradedCold.GetProperty("loadMs").GetInt64() >= 0);
                Assert.Equal(["loadMs"], degradedCold.EnumerateObject()
                    .Select(property => property.Name).ToArray());
            }
            finally
            {
                tools.TestOnlySemanticFailureReason = null;
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void SemanticOperationEmitsBoundedPrivacySafeTelemetry()
    {
        string root = Directory.CreateTempSubdirectory("codenav-51-telemetry").FullName;
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(proj, "Core.cs"),
                "namespace S { public class Core { public void Ping() { } } public class Use { public Core Value = new Core(); } }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            var semantic = new SemanticService(m);
            using var phaseListener = new TestSemanticPhaseListener();
            try
            {
                m.Start();
                IndexManagerTestSupport.WaitUntilReady(m, TimeSpan.FromSeconds(30),
                    "telemetry index did not become fresh");
                if (!semantic.FrameworkRefsAvailable) return;

                // One cold semantic op (retry rides transients, per the n7ly family).
                var tools = new CodeNav.Mcp.NavigationTools(m, semantic);
                _ = SemanticRetry.ParseExactWithRetry(() =>
                    tools.Definition(name: "Core", timeoutMs: 60000));
                _ = SemanticRetry.ParseExactWithRetry(() =>
                    tools.References(name: "Core", mode: "semantic", timeoutMs: 60000));

                // (1) the record reached the file (drainer is async — bounded wait, no sleep-only).
                string telemetryDir = Path.Combine(root, ".codenav", "telemetry");
                // Portal contract detail this test just proved the hard way: the writer holds
                // the file with FileShare.Read, so LIVE readers must request
                // FileShare.ReadWrite or Windows refuses them (File.ReadAllText does) —
                // see ReadShared below.
                Assert.True(WaitUntil(() =>
                    Directory.Exists(telemetryDir) &&
                    Directory.EnumerateFiles(telemetryDir, "phoenix-*.jsonl")
                        .Any(f => ReadShared(f).Contains("\"tool\":\"references\"") &&
                                  ReadShared(f).Contains("\"queryStages\"")), 10_000),
                    "no attributed references semanticOp record reached the telemetry file");

                string content = ReadShared(
                    Directory.EnumerateFiles(telemetryDir, "phoenix-*.jsonl").First());
                Assert.Contains("\"tool\":\"definition\"", content);
                Assert.Contains("\"result\":\"exact\"", content);
                // Review F2: the split must be THIS op's own phase-1 load, not an ambient
                // last-load — the field name is the contract (ownerLoad, not load).
                Assert.Contains("\"ownerLoad\":", content);
                Assert.Contains("\"gateWaitMs\":", content);
                // Field regression (48s query invisible): the op's own load/query wall split
                // must ride the EXACT record itself — a retried first attempt can leave a
                // degraded record carrying the fields, so whole-file Contains could false-pass
                // (review q3): assert on the exact record's own line.
                string exactLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .First(l => l.Contains("\"result\":\"exact\"")
                             && l.Contains("\"tool\":\"definition\""));
                Assert.Contains("\"clusterLoadMs\":", exactLine);
                Assert.Contains("\"queryMs\":", exactLine);
                // x5ls.1.3: the projectLoadMs sub-splits ride every load block — these decide
                // the wusi (index-first text) question from field data.
                Assert.Contains("\"sourceReadMs\":", exactLine);
                Assert.Contains("\"metadataResolveMs\":", exactLine);
                Assert.Contains("\"planMs\":", exactLine);
                Assert.Contains("\"preparationMs\":", exactLine);
                Assert.Contains("\"preparationQueueMs\":", exactLine);
                Assert.Contains("\"preparedProjects\":", exactLine);
                Assert.Contains("\"committedProjects\":", exactLine);
                Assert.Contains("\"effectiveProjectConcurrency\":", exactLine);
                Assert.Contains("\"admittedBytesHighWater\":", exactLine);
                Assert.Contains("\"retainedBytes\":", exactLine);
                Assert.Contains("\"retainedInputBytes\":", exactLine);
                Assert.Contains("\"residentProjects\":", exactLine);
                Assert.Contains("\"evictedProjects\":", exactLine);
                Assert.Contains("\"evictedInputBytes\":", exactLine);
                Assert.Contains("\"managedHeapBytes\":", exactLine);
                Assert.Contains("\"replanCount\":", exactLine);
                Assert.Contains("\"totalElapsedMs\":", exactLine);

                // epuc.4: references candidate/graph discovery belongs to clusterLoadMs and
                // queryStages owns the post-resolution wall. The field sample that motivated this
                // contract had queryMs=10.4s with no way to distinguish Roslyn finding from
                // syntax-root/classification/sample work.
                string referencesLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .First(l => l.Contains("\"result\":\"exact\"")
                             && l.Contains("\"tool\":\"references\""));
                using var referencesRecord = System.Text.Json.JsonDocument.Parse(referencesLine);
                var referencesRoot = referencesRecord.RootElement;
                string correlationId = Assert.IsType<string>(
                    referencesRoot.GetProperty("corr").GetString());
                Assert.NotEmpty(correlationId);
                Assert.True(referencesRoot.GetProperty("clusterLoadProcessWideCpuMs")
                    .GetDouble() >= 0);
                var scanSet = referencesRoot.GetProperty("planning").GetProperty("scanSet");
                Assert.True(scanSet.GetProperty("totalMs").GetDouble() >= 0);
                Assert.True(scanSet.GetProperty("scanProjects").GetInt32() >= 1);

                var queryStages = referencesRoot.GetProperty("queryStages");
                Assert.Equal("symbol_finder", queryStages.GetProperty("path").GetString());
                var compilationPreparation = queryStages.GetProperty("compilationPreparation");
                Assert.True(compilationPreparation.GetProperty("totalMs").GetDouble() >= 0);
                Assert.True(compilationPreparation.GetProperty("processWideCpuMs")
                    .GetDouble() >= 0);
                Assert.True(compilationPreparation.GetProperty("queueMs").GetDouble() >= 0);
                double busySumMs = compilationPreparation.GetProperty("busySumMs").GetDouble();
                double maxProjectBusyMs =
                    compilationPreparation.GetProperty("maxProjectBusyMs").GetDouble();
                double waveMaxSumMs =
                    compilationPreparation.GetProperty("waveMaxSumMs").GetDouble();
                double criticalPathMs =
                    compilationPreparation.GetProperty("criticalPathMs").GetDouble();
                Assert.True(busySumMs >= maxProjectBusyMs);
                Assert.True(maxProjectBusyMs <= criticalPathMs + 0.1);
                Assert.True(criticalPathMs <= waveMaxSumMs + 0.1);
                Assert.True(waveMaxSumMs <=
                    compilationPreparation.GetProperty("totalMs").GetDouble() + 0.1);
                Assert.True(compilationPreparation.GetProperty("requestedProjects").GetInt32() >= 1);
                Assert.True(compilationPreparation.GetProperty("graphProjects").GetInt32() >= 1);
                Assert.True(compilationPreparation.GetProperty("laneLimit").GetInt32() >= 1);
                Assert.True(compilationPreparation.GetProperty("processorCount").GetInt32() >= 1);
                Assert.True(compilationPreparation.GetProperty("effectiveConcurrency").GetInt32() >= 0);
                foreach (string countField in new[]
                         {
                             "cacheHits", "preparedProjects", "failedProjects", "skippedProjects",
                             "waves",
                         })
                {
                    Assert.True(compilationPreparation.GetProperty(countField).GetInt32() >= 0,
                        countField);
                }
                Assert.Equal(0, compilationPreparation.GetProperty("unfinishedProjects").GetInt32());
                var documentScope = queryStages.GetProperty("documentScope");
                Assert.Equal("fullSolution", documentScope.GetProperty("mode").GetString());
                Assert.Equal("ineligible_kind", documentScope.GetProperty("reason").GetString());
                Assert.Equal("leasedSolutionText",
                    documentScope.GetProperty("candidateSource").GetString());
                Assert.True(documentScope.GetProperty("totalMs").GetDouble() >= 0);
                Assert.False(documentScope.GetProperty("cacheHit").GetBoolean());
                Assert.False(documentScope.TryGetProperty("solutionDocuments", out _));
                Assert.False(documentScope.TryGetProperty("candidateDocuments", out _));
                Assert.False(documentScope.TryGetProperty("scopedDocuments", out _));
                Assert.False(documentScope.TryGetProperty("scopedProjects", out _));
                Assert.False(documentScope.TryGetProperty("documentsInScopedProjects", out _));
                Assert.Equal(0, documentScope.GetProperty("aliasWidenedProjects").GetInt32());
                Assert.Equal(0,
                    documentScope.GetProperty("transformedIncludedDocuments").GetInt32());
                foreach (string field in new[]
                         {
                             "findReferencesMs", "postProcessMs", "syntaxRootLoadMs",
                             "classificationMs", "sampleTextMs", "postProcessOtherMs", "otherMs",
                         })
                {
                    Assert.True(queryStages.GetProperty(field).GetDouble() >= 0, field);
                }
                Assert.True(queryStages.GetProperty("referencedSymbols").GetInt32() >= 1);
                Assert.True(queryStages.GetProperty("rawLocations").GetInt32() >= 1);
                Assert.True(queryStages.GetProperty("sourceLocations").GetInt32() >= 1);
                Assert.True(queryStages.GetProperty("uniqueSyntaxTrees").GetInt32() >= 1);
                Assert.True(queryStages.GetProperty("uniqueSites").GetInt32() >= 1);
                Assert.True(queryStages.GetProperty("samplesRead").GetInt32() >= 1);

                TestSemanticPhaseEvent[] correlatedPhases = phaseListener.Events
                    .Where(e => e.OperationId == correlationId)
                    .ToArray();
                foreach (string phase in new[]
                         {
                             "ownerLoad", "scanLoad", "compilationPreparation",
                             "documentScope", "findReferences",
                         })
                {
                    Assert.Equal(1, correlatedPhases.Count(e =>
                        e.EventName == "PhaseStart" && e.PhaseName == phase));
                    Assert.Equal(1, correlatedPhases.Count(e =>
                        e.EventName == "PhaseStop" && e.PhaseName == phase));
                }

                // (2) privacy: no drive-rooted path may appear in any record —
                // neither drive-letter (C:\\) nor UNC (\\\\server\\share) shaped.
                foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Assert.DoesNotContain(":\\\\", line);   // JSON-escaped C:\\ etc.
                    Assert.DoesNotContain("\\\\\\\\", line); // JSON-escaped \\ (UNC root)
                    Assert.DoesNotContain(root.Replace('\\', '/'), line);
                }
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public void ReferencesQueryStageShapeAttributesFinderAndPostProcessingWithoutSensitiveData()
    {
        // Unguarded contract coverage: the full semantic fixture above is skipped on machines
        // without the pinned framework references, but the telemetry wire shape must never become
        // latent there. Values make both residue buckets decisive.
        var stats = new SemanticService.ReferenceQueryStats
        {
            FindReferencesMs = 100,
            CompilationPreparation =
            {
                Stats = new CodeNav.Core.Semantic.SemanticWorkspace.CompilationPreparationStats(
                    TotalMs: 20, ProcessWideCpuMs: 7, GcPauseMs: 2,
                    QueueMs: 3, BusySumMs: 30,
                    MaxProjectBusyMs: 8,
                    WaveMaxSumMs: 15, CriticalPathMs: 12,
                    RequestedProjects: 2, GraphProjects: 4,
                    CacheHits: 1, PreparedProjects: 3, FailedProjects: 0, SkippedProjects: 0,
                    UnfinishedProjects: 0, Waves: 2, LaneLimit: 8, ProcessorCount: 12,
                    EffectiveConcurrency: 3),
            },
            DocumentScope =
            {
                Stats = new SemanticService.ReferenceDocumentScopeStats(
                    Mode: "documentScoped", Reason: "eligible",
                    CandidateSource: "leasedSolutionText", TotalMs: 5,
                    CacheHit: false,
                    SolutionDocuments: 20, CandidateDocuments: 6, ScopedDocuments: 8,
                    ScopedProjects: 3, DocumentsInScopedProjects: 17,
                    AliasWidenedProjects: 1, TransformedIncludedDocuments: 2),
            },
            PostProcessMs = 50,
            SyntaxRootLoadMs = 10,
            ClassificationMs = 5,
            SampleTextMs = 15,
            ReferencedSymbols = 3,
            RawLocations = 9,
            SourceLocations = 8,
            UniqueSyntaxTrees = 4,
            UniqueSites = 7,
            SamplesRead = 2,
        };

        string json = System.Text.Json.JsonSerializer.Serialize(stats.Shape(queryMs: 200));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("symbol_finder", root.GetProperty("path").GetString());
        Assert.Equal(100, root.GetProperty("findReferencesMs").GetDouble());
        Assert.Equal(50, root.GetProperty("postProcessMs").GetDouble());
        Assert.Equal(20, root.GetProperty("postProcessOtherMs").GetDouble());
        Assert.Equal(25, root.GetProperty("otherMs").GetDouble());
        var preparation = root.GetProperty("compilationPreparation");
        Assert.Equal(20, preparation.GetProperty("totalMs").GetDouble());
        Assert.Equal(7, preparation.GetProperty("processWideCpuMs").GetDouble());
        Assert.Equal(2, preparation.GetProperty("gcPauseMs").GetInt64());
        Assert.Equal(3, preparation.GetProperty("queueMs").GetDouble());
        Assert.Equal(30, preparation.GetProperty("busySumMs").GetDouble());
        Assert.Equal(8, preparation.GetProperty("maxProjectBusyMs").GetDouble());
        Assert.Equal(15, preparation.GetProperty("waveMaxSumMs").GetDouble());
        Assert.Equal(12, preparation.GetProperty("criticalPathMs").GetDouble());
        Assert.Equal(4, preparation.GetProperty("graphProjects").GetInt32());
        Assert.Equal(3, preparation.GetProperty("preparedProjects").GetInt32());
        Assert.Equal(1, preparation.GetProperty("cacheHits").GetInt32());
        Assert.Equal(2, preparation.GetProperty("waves").GetInt32());
        Assert.Equal(8, preparation.GetProperty("laneLimit").GetInt32());
        Assert.Equal(12, preparation.GetProperty("processorCount").GetInt32());
        Assert.Equal(3, preparation.GetProperty("effectiveConcurrency").GetInt32());
        var documentScope = root.GetProperty("documentScope");
        Assert.Equal("documentScoped", documentScope.GetProperty("mode").GetString());
        Assert.Equal("eligible", documentScope.GetProperty("reason").GetString());
        Assert.Equal("leasedSolutionText",
            documentScope.GetProperty("candidateSource").GetString());
        Assert.Equal(5, documentScope.GetProperty("totalMs").GetDouble());
        Assert.False(documentScope.GetProperty("cacheHit").GetBoolean());
        Assert.Equal(20, documentScope.GetProperty("solutionDocuments").GetInt32());
        Assert.Equal(6, documentScope.GetProperty("candidateDocuments").GetInt32());
        Assert.Equal(8, documentScope.GetProperty("scopedDocuments").GetInt32());
        Assert.Equal(3, documentScope.GetProperty("scopedProjects").GetInt32());
        Assert.Equal(17, documentScope.GetProperty("documentsInScopedProjects").GetInt32());
        Assert.Equal(1, documentScope.GetProperty("aliasWidenedProjects").GetInt32());
        Assert.Equal(2, documentScope.GetProperty("transformedIncludedDocuments").GetInt32());
        Assert.Equal(3, root.GetProperty("referencedSymbols").GetInt32());
        Assert.Equal(9, root.GetProperty("rawLocations").GetInt32());
        Assert.Equal(8, root.GetProperty("sourceLocations").GetInt32());
        Assert.Equal(4, root.GetProperty("uniqueSyntaxTrees").GetInt32());
        Assert.Equal(7, root.GetProperty("uniqueSites").GetInt32());
        Assert.Equal(2, root.GetProperty("samplesRead").GetInt32());
        Assert.False(root.TryGetProperty("symbolName", out _));
        Assert.False(root.TryGetProperty("workspacePath", out _));
        Assert.False(root.TryGetProperty("sourceText", out _));
        Assert.False(root.TryGetProperty("arguments", out _));

        stats.CompilationPreparation.Stats =
            stats.CompilationPreparation.Stats with
            {
                ProcessWideCpuMs = null,
                GcPauseMs = null,
            };
        using var unavailableDocument = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(stats.Shape(queryMs: 200),
                CodeNav.Core.Telemetry.TelemetryBounds.JsonOpts));
        var unavailablePreparation = unavailableDocument.RootElement
            .GetProperty("compilationPreparation");
        Assert.False(unavailablePreparation.TryGetProperty("processWideCpuMs", out _));
        Assert.False(unavailablePreparation.TryGetProperty("gcPauseMs", out _));
    }

    [Fact]
    public void RingIsBoundedAndEmitNeverThrows()
    {
        string root = Directory.CreateTempSubdirectory("codenav-51-ring").FullName;
        try
        {
            using var log = new TelemetryLog(root);
            for (int i = 0; i < 600; i++) log.Emit(new { e = "probe", i });
            Assert.True(log.Snapshot().Count <= 256, "ring must stay bounded");
            log.Emit(new { e = "still-alive" }); // after churn, Emit still never throws
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public void FileCapTruncatesHonestlyWhileRingKeepsRolling()
    {
        // Review F5: the 16 MiB cap was documented but unexercised — a broken cap means a
        // long-lived server writes an unbounded file into every indexed workspace.
        string root = Directory.CreateTempSubdirectory("codenav-51-cap").FullName;
        try
        {
            string dir = Path.Combine(root, ".codenav", "telemetry");
            long fileLenAtCap = 0;
            using (var log = new TelemetryLog(root))
            {
                log.FileCapBytes = 2_000; // test hook: shrink 16 MiB to something a test can cross
                for (int i = 0; i < 200; i++) log.Emit(new { e = "capProbe", i });
                Assert.True(WaitUntil(() =>
                    Directory.Exists(dir) &&
                    Directory.EnumerateFiles(dir, "phoenix-*.jsonl")
                        .Any(f => ReadShared(f).Contains("\"telemetry_truncated\"")), 10_000),
                    "cap crossing must be announced in-band as telemetry_truncated");

                string file = Directory.EnumerateFiles(dir, "phoenix-*.jsonl")
                    .First(f => ReadShared(f).Contains("\"telemetry_truncated\""));
                fileLenAtCap = new FileInfo(file).Length;

                // Past the cap: the file stops growing, but Emit/ring stay alive (the portal
                // still reads the ring even after the file honestly ends).
                for (int i = 0; i < 300; i++) log.Emit(new { e = "afterCap", i });
                Assert.True(log.Snapshot().Any(l => l.Contains("\"afterCap\"")),
                    "ring must keep rolling after the file cap");
                Assert.Equal(fileLenAtCap, new FileInfo(file).Length);
            }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task GateDeathStillPublishesGateOnlySplit()
    {
        // Review r2: a deadline dying while QUEUED for the workspace gate (cold workspace, two
        // parallel ops) is the primary gate-contention signal — the stats box must still carry
        // a gate-only split: gateWaitMs = whole wall, phases-never-entered = 0, and
        // loadedBefore ABSENT (null — the warm-set size is unreadable without the gate).
        string root = Directory.CreateTempSubdirectory("codenav-51-gate").FullName;
        try
        {
            using var ws = new SemanticWorkspace(root, Path.Combine(root, "index.db"));
            var box = new SemanticWorkspace.LoadStatsBox();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ws.EnsureLoadedAsync(new[] { "P" }, new CancellationToken(canceled: true),
                    statsBox: box));
            Assert.NotNull(box.Stats);
            Assert.Null(box.Stats!.LoadedBefore);   // unknown, never fabricated as 0
            Assert.Equal(1, box.Stats.Requested);
            Assert.Equal(0, box.Stats.FingerprintMs); // phase never entered
            Assert.Equal(0, box.Stats.ProjectLoadMs);
            Assert.Equal(0, box.Stats.Loaded);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static List<string> SemanticOpLines(string workspaceRoot, string tool)
    {
        string telemetryDir = Path.Combine(workspaceRoot, ".codenav", "telemetry");
        if (!Directory.Exists(telemetryDir)) return [];
        return Directory.EnumerateFiles(telemetryDir, "phoenix-*.jsonl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => ReadShared(path)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Where(line => IsSemanticOpLine(line, tool))
            .ToList();
    }

    private static bool IsSemanticOpLine(string line, string tool)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.TryGetProperty("e", out var eventName) &&
                   eventName.GetString() == "semanticOp" &&
                   root.TryGetProperty("tool", out var recordedTool) &&
                   recordedTool.GetString() == tool;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool IsColdReferencesTelemetryLine(string line)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.TryGetProperty("tool", out var tool) &&
                   tool.GetString() == "references" &&
                   root.TryGetProperty("semanticColdStart", out var timing) &&
                   timing.TryGetProperty("cold", out var cold) &&
                   cold.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return cond();
    }
}
