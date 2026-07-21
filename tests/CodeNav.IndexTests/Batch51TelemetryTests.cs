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
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 30_000));
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
                var scanSet = referencesRoot.GetProperty("planning").GetProperty("scanSet");
                Assert.True(scanSet.GetProperty("totalMs").GetDouble() >= 0);
                Assert.True(scanSet.GetProperty("scanProjects").GetInt32() >= 1);

                var queryStages = referencesRoot.GetProperty("queryStages");
                Assert.Equal("symbol_finder", queryStages.GetProperty("path").GetString());
                var compilationPreparation = queryStages.GetProperty("compilationPreparation");
                Assert.True(compilationPreparation.GetProperty("totalMs").GetDouble() >= 0);
                Assert.True(compilationPreparation.GetProperty("queueMs").GetDouble() >= 0);
                Assert.True(compilationPreparation.GetProperty("requestedProjects").GetInt32() >= 1);
                Assert.True(compilationPreparation.GetProperty("graphProjects").GetInt32() >= 1);
                Assert.True(compilationPreparation.GetProperty("laneLimit").GetInt32() >= 1);
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
                    TotalMs: 20, QueueMs: 3, RequestedProjects: 2, GraphProjects: 4,
                    CacheHits: 1, PreparedProjects: 3, FailedProjects: 0, SkippedProjects: 0,
                    UnfinishedProjects: 0, Waves: 2, LaneLimit: 8, EffectiveConcurrency: 3),
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
        Assert.Equal(3, preparation.GetProperty("queueMs").GetDouble());
        Assert.Equal(4, preparation.GetProperty("graphProjects").GetInt32());
        Assert.Equal(3, preparation.GetProperty("preparedProjects").GetInt32());
        Assert.Equal(1, preparation.GetProperty("cacheHits").GetInt32());
        Assert.Equal(2, preparation.GetProperty("waves").GetInt32());
        Assert.Equal(8, preparation.GetProperty("laneLimit").GetInt32());
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
