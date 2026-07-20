using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>Large-repository semantic candidate-budget regressions.</summary>
public class Batch46SemanticBudgetTests
{
    [Theory]
    [InlineData(nameof(NavigationTools.Implementations))]
    [InlineData(nameof(NavigationTools.References))]
    [InlineData(nameof(NavigationTools.Callers))]
    [InlineData(nameof(NavigationTools.TypeHierarchy))]
    public void SemanticToolsShareTheLargeRepositoryDefault(string methodName)
    {
        MethodInfo method = Assert.Single(typeof(NavigationTools).GetMethods(),
            candidate => candidate.Name == methodName);
        ParameterInfo parameter = Assert.Single(method.GetParameters(),
            candidate => candidate.Name == "maxProjects");

        Assert.Equal(SemanticService.DefaultCandidateProjectBudget, parameter.DefaultValue);
        string description = Assert.IsType<DescriptionAttribute>(
            parameter.GetCustomAttribute<DescriptionAttribute>()).Description;
        Assert.Contains("0 (default) loads all matching projects", description, StringComparison.Ordinal);
        Assert.Contains("positive value opts into a bound", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, int.MaxValue)]
    [InlineData(1, 1)]
    [InlineData(128, 128)]
    [InlineData(512, 512)]
    [InlineData(513, 513)]
    [InlineData(2_001, 2_001)]
    [InlineData(10_000, 10_000)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void CandidateProjectBudgetHasNoPhoenixImposedMaximum(int requested, int expected)
        => Assert.Equal(expected, SemanticService.NormalizeCandidateProjectBudget(requested));

    [Fact]
    public void CandidateDiscoveryEnumeratesProjectsBeyondFormerFileAndSeedCaps()
    {
        const int projectCount = 2_001;
        string root = Directory.CreateTempSubdirectory("codenav-candidate-budget").FullName;
        string dbPath = Path.Combine(root, "candidates.db");
        try
        {
            using (var store = new IndexStore(dbPath, createNew: true))
            using (var transaction = store.BeginTransaction())
            {
                for (int i = 0; i < projectCount; i++)
                {
                    string projectName = $"P{i:D4}";
                    long projectId = store.InsertProject(transaction, new ParsedProject(
                        RelPath: $"{projectName}/{projectName}.csproj",
                        Name: projectName,
                        Style: "sdk",
                        Guid: null,
                        TargetFrameworks: "net9.0",
                        IsTest: false,
                        ProjectRefRelPaths: [],
                        PackageRefs: [],
                        ExplicitCompileItems: null,
                        AssemblyRefs: [],
                        LoadStatus: "parsed"));
                    string content = $"public sealed class Impl{i:D4} : IProbe {{ }}";
                    long fileId = store.InsertFile(transaction,
                        $"{projectName}/Impl{i:D4}.cs", content.Length, 0, (ulong)(i + 1),
                        "cs", 1, isGenerated: false, hasTestAttrs: false);
                    store.InsertContent(transaction, fileId, content);
                    store.InsertSymbols(transaction, fileId,
                    [
                        new SymbolRow(
                            OrdinalInFile: 0,
                            ParentOrdinal: -1,
                            Kind: "class",
                            Name: $"Impl{i:D4}",
                            Namespace: null,
                            Container: null,
                            Signature: $"public sealed class Impl{i:D4} : IProbe",
                            Accessibility: "public",
                            StartLine: 1,
                            EndLine: 1,
                            IsPartial: false,
                            Arity: 0,
                            AttrMarkers: null,
                            BaseTypes: [new BaseTypeIdentity("IProbe", 0)])
                    ]);
                    store.InsertCompileItem(transaction, projectId, fileId);
                }
                transaction.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            var textCandidates = queries.CandidateProjectsForName("IProbe");
            Assert.Equal(projectCount, textCandidates.Count);
            Assert.Contains(textCandidates, candidate => candidate.Project == "P2000");

            var implementationCandidates = queries.ImplementationCandidateProjects("IProbe");
            Assert.Equal(projectCount, implementationCandidates.Count);
            Assert.Contains("P2000", implementationCandidates);
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* Windows may retain a handle briefly. */ }
        }
    }

    [Fact]
    public async Task DefaultBudgetLoadsImplementersBeyondTheFormerTwentyFourProjectWindow()
    {
        const int implementerCount = 54;
        string root = Directory.CreateTempSubdirectory("codenav-semantic-budget").FullName;
        try
        {
            WriteWorkspace(root, implementerCount);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000), "index did not become queryable");

            SymbolHit target;
            using (var queries = manager.OpenQueries())
            {
                target = Assert.Single(queries.SearchSymbols(
                    "IBudgetProbe", "exact", new[] { "interface" }, 10));
            }

            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;

            // n7ly: startup publishes queryability before its queued freshness sweep has
            // necessarily completed. Under full-suite CPU pressure that sweep can hold the
            // snapshot epoch beyond the bounded acquisition window, whose contract is retry.
            // Retry only the two documented transient semantic states; a stable wrong result
            // or any other failure reason still reaches the decisive assertions below.
            SemanticImplementations? result = null;
            string? reason = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                (result, reason) = await semantic.ImplementationsAsync(
                    target.FilePath,
                    target.StartLine,
                    column: null,
                    nameHint: target.Name,
                    maxProjects: SemanticService.DefaultCandidateProjectBudget,
                    timeoutMs: 120_000);
                if (result is not null ||
                    reason is not ("index_snapshot_unavailable" or "cluster_cold_load"))
                {
                    break;
                }
                await Task.Delay(250);
            }

            Assert.True(result is not null, $"semantic implementations failed: {reason}");
            Assert.False(result!.DeadlineExhausted);
            Assert.Equal(implementerCount, result.Implementations.Count);
            Assert.Empty(result.Coverage.SkippedProjects);
            Assert.True(result.Coverage.RequestedProjects >= implementerCount + 1);
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* Windows may retain a handle briefly. */ }
        }
    }

    [Fact]
    public async Task TypeHierarchyAndImplementationsLoadTheSameEightCandidatesInOneIndexEpoch()
    {
        const int implementerCount = 8;
        string root = Directory.CreateTempSubdirectory("codenav-semantic-parity").FullName;
        try
        {
            WriteWorkspace(root, implementerCount);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000), manager.Health().Error);
            string? indexVersion = manager.Health().IndexVersion;

            SymbolHit target;
            using (var queries = manager.OpenQueries())
            {
                target = Assert.Single(queries.SearchSymbols(
                    "IBudgetProbe", "exact", new[] { "interface" }, 10));
            }

            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;

            var (hierarchy, hierarchyCoverage, hierarchySkipped, hierarchyReason) =
                await semantic.TypeHierarchyAsync(
                    target.FilePath, target.StartLine, column: null, nameHint: target.Name,
                    maxProjects: SemanticService.DefaultCandidateProjectBudget,
                    timeoutMs: 120_000);
            Assert.NotNull(hierarchy);
            Assert.Null(hierarchyReason);
            Assert.Equal(implementerCount, hierarchy!.DerivedOrImplementing.Count);
            AssertCompleteCandidateCoverage(hierarchyCoverage, hierarchySkipped);

            var (implementations, implementationsReason) = await semantic.ImplementationsAsync(
                target.FilePath, target.StartLine, column: null, nameHint: target.Name,
                maxProjects: SemanticService.DefaultCandidateProjectBudget,
                timeoutMs: 120_000);
            Assert.NotNull(implementations);
            Assert.Null(implementationsReason);
            Assert.Equal(implementerCount, implementations!.Implementations.Count);
            AssertCompleteCandidateCoverage(implementations.Coverage,
                implementations.SkippedCandidateProjects);
            Assert.Equal(indexVersion, manager.Health().IndexVersion);

            static void AssertCompleteCandidateCoverage(ClusterCoverage? coverage,
                IReadOnlyCollection<string>? skipped)
            {
                ClusterCoverage complete = Assert.IsType<ClusterCoverage>(coverage);
                Assert.Equal(implementerCount + 1, complete.RequestedProjects);
                Assert.Equal(complete.RequestedProjects, complete.LoadedProjects);
                Assert.Empty(complete.FailedProjects);
                Assert.Empty(complete.SkippedProjects);
                Assert.Empty(skipped ?? []);
            }
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExplicitBudgetReportsPartialAcrossExactSemanticListTools()
    {
        string root = Directory.CreateTempSubdirectory("codenav-semantic-partial").FullName;
        try
        {
            WriteWorkspace(root, implementerCount: 4);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000), "index did not become queryable");

            using (var probe = new SemanticService(manager))
            {
                if (!probe.FrameworkRefsAvailable) return;
            }

            JsonElement implementations = Invoke(tools => tools.Implementations(
                name: "IBudgetProbe", maxProjects: 1, timeoutMs: 120_000));
            AssertPartial(implementations);
            Assert.False(implementations.TryGetProperty("likelyImplementation", out _));

            JsonElement hierarchy = Invoke(tools => tools.TypeHierarchy(
                name: "IBudgetProbe", maxProjects: 1, timeoutMs: 120_000));
            AssertPartial(hierarchy);

            JsonElement callers = Invoke(tools => tools.Callers(
                name: "Run", maxProjects: 1, timeoutMs: 120_000));
            AssertPartial(callers);

            using (var referenceSemantic = new SemanticService(manager))
            {
                var referenceTools = new NavigationTools(manager, referenceSemantic);
                JsonElement references = SemanticRetry.ParseWithRetry(
                    () => referenceTools.References(name: "Run", maxProjects: 1,
                        timeoutMs: 120_000),
                    json => json.TryGetProperty("partialReason", out JsonElement reason) &&
                            (reason.GetString() ?? "").Contains(
                                "candidate_cluster_bounded", StringComparison.Ordinal),
                    "bounded reference census");
                Assert.True(references.GetProperty("partial").GetBoolean());
                Assert.True(references.GetProperty("totalIsLowerBound").GetBoolean());
                Assert.StartsWith("at least ", references.GetProperty("summary").GetString());
                Assert.Equal("indexed", references.GetProperty("meta")
                    .GetProperty("confidence").GetString());
                Assert.NotEmpty(references.GetProperty("skippedCandidateProjects")
                    .EnumerateArray());
            }

            JsonElement Invoke(Func<NavigationTools, string> invoke)
            {
                using var semantic = new SemanticService(manager);
                var tools = new NavigationTools(manager, semantic);
                // n7ly: the deliberate budget-partial answers at EXACT confidence; transient
                // degrades do not — retry rides them out (a fresh cold cluster per Invoke made
                // this the suite's most cold-load-exposed site, red at 1.17s under load).
                return SemanticRetry.ParseExactWithRetry(() => invoke(tools));
            }

            static void AssertPartial(JsonElement response)
            {
                Assert.True(response.GetProperty("partial").GetBoolean());
                Assert.Equal("candidate_cluster_bounded",
                    response.GetProperty("partialReason").GetString()!.Split(':')[0]);
                Assert.NotEmpty(response.GetProperty("skippedCandidateProjects").EnumerateArray());
                Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
            }
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* Windows may retain a handle briefly. */ }
        }
    }

    [Fact]
    public void MandatoryOwnerClosureDoesNotCreateFalsePartialCoverage()
    {
        string root = Directory.CreateTempSubdirectory("codenav-semantic-owner-coverage").FullName;
        try
        {
            WriteWorkspace(root, implementerCount: 1);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000), manager.Health().Error);
            using var semantic = new SemanticService(manager);
            if (!semantic.FrameworkRefsAvailable) return;
            var tools = new NavigationTools(manager, semantic);
            using JsonDocument document = JsonDocument.Parse(SemanticRetry.ParseExactWithRetry( // n7ly sweep
                () => tools.Implementations(name: "IBudgetProbe", maxProjects: 1, timeoutMs: 120_000)).GetRawText());
            JsonElement response = document.RootElement;

            Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Single(response.GetProperty("implementations").EnumerateArray());
            Assert.False(response.TryGetProperty("partial", out _));
            Assert.False(response.TryGetProperty("skippedCandidateProjects", out _));
            Assert.EndsWith("Impl000", response.GetProperty("likelyImplementation").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("implementations")]
    [InlineData("groups")]
    [InlineData("callers")]
    [InlineData("derivedOrImplementing")]
    public void SemanticAuxiliaryProjectSamplesHonorTheHardResponseBudget(string resultField)
    {
        var skipped = Enumerable.Range(0, 2_001)
            .Select(i => $"Project_{i:D4}_{new string('x', 96)}")
            .ToList();
        string json = Json.WithAuxiliaryListBudget(new List<int> { 1 }, skipped,
            (items, truncated, sample, sampleTruncated) => new Dictionary<string, object?>
            {
                [resultField] = items,
                ["truncated"] = truncated,
                ["skippedCandidateProjects"] = sample,
                ["skippedCandidateProjectCount"] = skipped.Count,
                ["skippedCandidateProjectsTruncated"] = sampleTruncated,
            });

        Assert.True(Json.Utf8Bytes(json) <= Json.HardBudgetBytes,
            $"{resultField} response used {Json.Utf8Bytes(json)} bytes");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement response = document.RootElement;
        Assert.Equal(skipped.Count,
            response.GetProperty("skippedCandidateProjectCount").GetInt32());
        Assert.True(response.GetProperty("skippedCandidateProjectsTruncated").GetBoolean());
        Assert.InRange(response.GetProperty("skippedCandidateProjects").GetArrayLength(), 1, 16);
    }

    [Fact]
    public async Task WindowsReaderCoordinationHasNoFormerSlotCeilingAndStopsBarging()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-scalable-reader-gate").FullName;
        string dbPath = Path.Combine(root, "index.db");
        var readers = new List<IndexReviewCoordinationLease>();
        try
        {
            Assert.True(IndexDirectoryAuthority.TryOpen(dbPath, createDirectory: true,
                out IndexDirectoryAuthority? authority));
            using (IndexDirectoryAuthority opened = Assert.IsType<IndexDirectoryAuthority>(authority))
            {
                Assert.True(opened.TryAnchorReviewCoordinationFile(create: true));
                for (int i = 0; i < 128; i++)
                {
                    Assert.Equal(IndexReviewCoordinationAcquireResult.Acquired,
                        IndexReviewCoordinationLease.TryAcquireReader(opened,
                            TimeSpan.FromSeconds(1), out IndexReviewCoordinationLease? reader));
                    readers.Add(reader!);
                }

                using var writerWaiting = new ManualResetEventSlim(false);
                Task<(IndexReviewCoordinationAcquireResult Result,
                    IndexReviewCoordinationLease? Lease)> writer = Task.Run(() =>
                {
                    IndexReviewCoordinationAcquireResult result =
                        IndexReviewCoordinationLease.TryAcquireExclusive(opened,
                            TimeSpan.FromSeconds(10), writerWaiting.Set,
                            out IndexReviewCoordinationLease? lease);
                    return (result, lease);
                });
                Assert.True(writerWaiting.Wait(TimeSpan.FromSeconds(5)),
                    "writer never acquired the turnstile and began draining readers");

                for (int i = 0; i < 16; i++)
                {
                    Assert.Equal(IndexReviewCoordinationAcquireResult.Contended,
                        IndexReviewCoordinationLease.TryAcquireReader(opened,
                            TimeSpan.FromMilliseconds(20), out IndexReviewCoordinationLease? barging));
                    Assert.Null(barging);
                }

                foreach (IndexReviewCoordinationLease reader in readers) reader.Dispose();
                readers.Clear();
                var acquired = await writer.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(IndexReviewCoordinationAcquireResult.Acquired, acquired.Result);
                acquired.Lease!.Dispose();
            }
        }
        finally
        {
            foreach (IndexReviewCoordinationLease reader in readers) reader.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SemanticSnapshotAcquisitionObservesTheRequestDeadline()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-semantic-gate-deadline").FullName;
        try
        {
            WriteWorkspace(root, implementerCount: 1);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000), manager.Health().Error);

            Assert.True(IndexDirectoryAuthority.TryOpen(dbPath, createDirectory: false,
                out IndexDirectoryAuthority? authority));
            using (IndexDirectoryAuthority opened = Assert.IsType<IndexDirectoryAuthority>(authority))
            {
                Assert.True(opened.TryAnchorReviewCoordinationFile(create: false));
                Assert.Equal(IndexReviewCoordinationAcquireResult.Acquired,
                    IndexReviewCoordinationLease.TryAcquireExclusive(opened,
                        TimeSpan.FromSeconds(2), waiting: null,
                        out IndexReviewCoordinationLease? writer));
                using (writer!)
                using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
                {
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    Assert.Throws<OperationCanceledException>(() =>
                        manager.TryOpenReviewSnapshot(deadline.Token));
                    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1),
                        $"snapshot gate ignored the deadline for {elapsed.Elapsed}");
                }
            }
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LongSemanticScanDefersWindowsFullRebuildUntilItsReadGuardReleases()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-semantic-rebuild-guard").FullName;
        using var waiting = new ManualResetEventSlim(false);
        using var destructiveBoundary = new ManualResetEventSlim(false);
        using var rebuildCompleted = new ManualResetEventSlim(false);
        int activeAtBoundary = -1;
        try
        {
            WriteWorkspace(root, implementerCount: 4);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var manager = new IndexManager(root, dbPath)
            {
                FullRebuildReviewWaitTimeoutForTest = TimeSpan.FromMilliseconds(100),
                FullRebuildWaitingForReviewSnapshotsForTest = () => waiting.Set(),
                FullRebuildDestructiveBoundaryForTest = activeReaders =>
                {
                    activeAtBoundary = activeReaders;
                    destructiveBoundary.Set();
                },
                FullRebuildCompletedForTest = () => rebuildCompleted.Set(),
            };
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 30_000), manager.Health().Error);

            int rebuildRequested = 0;
            manager.ReviewSnapshotAfterQueryForTest = _ =>
            {
                if (Interlocked.Exchange(ref rebuildRequested, 1) != 0) return;
                Assert.True(manager.RequestFullRebuild());
                Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)),
                    "full rebuild never observed the semantic read guard");
            };
            try
            {
                using var semantic = new SemanticService(manager);
                var tools = new NavigationTools(manager, semantic);
                using JsonDocument response = JsonDocument.Parse(SemanticRetry.ParseExactWithRetry( // n7ly sweep
                    () => tools.Implementations(name: "IBudgetProbe", maxProjects: 4, timeoutMs: 120_000)).GetRawText());
                JsonElement result = response.RootElement;
                Assert.Equal("exact", result.GetProperty("meta").GetProperty("confidence").GetString());
            }
            finally
            {
                manager.ReviewSnapshotAfterQueryForTest = null;
            }

            Assert.Equal(1, Volatile.Read(ref rebuildRequested));
            Assert.True(destructiveBoundary.Wait(TimeSpan.FromSeconds(10)),
                "the original queued rebuild did not resume after the semantic guard released");
            Assert.Equal(0, Volatile.Read(ref activeAtBoundary));
            Assert.True(rebuildCompleted.Wait(TimeSpan.FromSeconds(40)));
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000), manager.Health().Error);
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* Windows may retain a handle briefly. */ }
        }
    }

    private static void WriteWorkspace(string root, int implementerCount)
    {
        string contracts = Path.Combine(root, "Contracts");
        Directory.CreateDirectory(contracts);
        File.WriteAllText(Path.Combine(contracts, "Contracts.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(contracts, "IBudgetProbe.cs"),
            "namespace Budget.Contracts; public interface IBudgetProbe { }");
        File.WriteAllText(Path.Combine(contracts, "ProbeApi.cs"),
            "namespace Budget.Contracts; public static class ProbeApi { public static void Run() { } }");

        for (int i = 0; i < implementerCount; i++)
        {
            string projectName = $"Impl{i:D3}";
            string directory = Path.Combine(root, projectName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{projectName}.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><ProjectReference Include=\"../Contracts/Contracts.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(directory, $"{projectName}.cs"),
                $"namespace Budget.Implementers; public sealed class {projectName} : Budget.Contracts.IBudgetProbe " +
                "{ public void Invoke() => Budget.Contracts.ProbeApi.Run(); }");
        }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
