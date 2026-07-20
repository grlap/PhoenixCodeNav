using System.Reflection;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeNav.Tests;

public class SemanticColdStartLoaderTests
{
    [Fact]
    public async Task IndependentProjectsPrepareConcurrentlyAndCommitReferencesInOrder()
    {
        string root = CreateWorkspace("parallel", ("A", null), ("B", "A"));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            int active = 0;
            int maximum = 0;
            var bothEntered = NewSignal();
            workspace.TestOnlyBeforeProjectCaptureAsync = async (_, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximum, current);
                if (current >= 2) bothEntered.TrySetResult(true);
                await bothEntered.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref active);
            };

            var stats = new SemanticWorkspace.LoadStatsBox();
            using SemanticSolutionLease load = await workspace.EnsureLoadedAsync(
                ["A", "B"], CancellationToken.None, statsBox: stats);

            Assert.True(maximum >= 2, $"project preparation stayed serial (max={maximum})");
            Assert.Equal(2, load.Coverage.LoadedProjects);
            var projectB = Assert.Single(load.Solution.Projects, project => project.Name == "B");
            var reference = Assert.Single(projectB.ProjectReferences);
            Assert.Equal("A", load.Solution.GetProject(reference.ProjectId)?.Name);
            Assert.NotNull(stats.Stats);
            Assert.Equal(2, stats.Stats.PreparedProjects);
            Assert.Equal(2, stats.Stats.CommittedProjects);
            Assert.True(stats.Stats.EffectiveProjectConcurrency >= 2);
            Assert.True(stats.Stats.PreparationMs >= 0);
            Assert.True(stats.Stats.PreparationQueueMs >= 0);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ConcurrentWaitersSharePreparationAndOneCancellationDoesNotCancelTheOther()
    {
        string root = CreateWorkspace("singleflight", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            int captures = 0;
            var captureEntered = NewSignal();
            var releaseCapture = NewSignal();
            workspace.TestOnlyBeforeProjectCaptureAsync = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref captures);
                captureEntered.TrySetResult(true);
                await releaseCapture.Task.WaitAsync(cancellationToken);
            };

            using var canceledWaiter = new CancellationTokenSource();
            Task<SemanticSolutionLease> first = workspace.EnsureLoadedAsync(
                ["P"], canceledWaiter.Token);
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<SemanticSolutionLease> second = workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);
            await WaitUntilAsync(() => workspace.TestOnlyPreparationWaiters >= 2,
                "both calls did not join the same preparation");

            canceledWaiter.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
            releaseCapture.TrySetResult(true);
            using SemanticSolutionLease surviving = await second;

            Assert.Equal(1, captures);
            Assert.Equal(1, surviving.Coverage.LoadedProjects);
            Assert.Single(surviving.Solution.Projects);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task SoleCanceledWaiterStopsItsUnpublishedPreparationAndReleasesAdmission()
    {
        string root = CreateWorkspace("cancel-preparation", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            var captureEntered = NewSignal();
            workspace.TestOnlyBeforeProjectCaptureAsync = async (_, cancellationToken) =>
            {
                captureEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            };
            using var canceled = new CancellationTokenSource();
            var stats = new SemanticWorkspace.LoadStatsBox();
            Task<SemanticSolutionLease> abandoned = workspace.EnsureLoadedAsync(
                ["P"], canceled.Token, statsBox: stats);
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);

            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);
            Assert.NotNull(stats.Stats);
            Assert.True(stats.Stats.PreparationMs > 0,
                "canceled preparation time disappeared from phase telemetry");
            await WaitUntilAsync(() => workspace.RetainedSemanticInputBytes == 0,
                "canceled single-flight preparation retained its admission reservation");

            workspace.TestOnlyBeforeProjectCaptureAsync = null;
            using SemanticSolutionLease retry = await workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);
            Assert.Equal(1, retry.Coverage.LoadedProjects);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CancellationRetainsTelemetryForProjectsThatFinishedPreparation()
    {
        string root = CreateWorkspace("partial-preparation-telemetry",
            ("Fast", null), ("Slow", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            var fastPrepared = NewSignal();
            workspace.TestOnlyAfterProjectPrepared = name =>
            {
                if (name == "Fast") fastPrepared.TrySetResult(true);
            };
            workspace.TestOnlyBeforeProjectCaptureAsync = async (name, cancellationToken) =>
            {
                if (name == "Slow")
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            };
            using var canceled = new CancellationTokenSource();
            var stats = new SemanticWorkspace.LoadStatsBox();
            Task<SemanticSolutionLease> loading = workspace.EnsureLoadedAsync(
                ["Fast", "Slow"], canceled.Token, statsBox: stats);
            await fastPrepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);

            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loading);

            Assert.NotNull(stats.Stats);
            Assert.True(stats.Stats.PreparedProjects >= 1);
            Assert.True(stats.Stats.ProjectParseMs + stats.Stats.SourceReadMs > 0,
                "completed preparation subphase work disappeared after peer cancellation");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CancellationDuringPlanningSqlReleasesDescriptorAdmission()
    {
        string root = CreateWorkspace("planning-sql-cancellation", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 1);
            using var canceled = new CancellationTokenSource();
            int projectRowQueries = 0;
            int captures = 0;
            workspace.TestOnlyBeforeColdStartSql = sql =>
            {
                if (!IsBatchedProjectRowsQuery(sql)) return;
                Interlocked.Increment(ref projectRowQueries);
                canceled.Cancel();
            };
            workspace.TestOnlyBeforeProjectCaptureAsync = (_, _) =>
            {
                Interlocked.Increment(ref captures);
                return Task.CompletedTask;
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await workspace.EnsureLoadedAsync(["P"], canceled.Token)
                    .WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(1, projectRowQueries);
            Assert.Equal(0, captures);
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);
            Assert.Equal(0, workspace.TestOnlyPlannedProjectIds);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CancellationDuringPreCommitVerificationSqlReleasesPreparedAdmission()
    {
        string root = CreateWorkspace("verification-sql-cancellation", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 1);
            using var canceled = new CancellationTokenSource();
            int projectRowQueries = 0;
            workspace.TestOnlyBeforeColdStartSql = sql =>
            {
                if (!IsBatchedProjectRowsQuery(sql)) return;
                if (Interlocked.Increment(ref projectRowQueries) == 2)
                    canceled.Cancel();
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await workspace.EnsureLoadedAsync(["P"], canceled.Token)
                    .WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(2, projectRowQueries);
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);
            Assert.Equal(0, workspace.TestOnlyPlannedProjectIds);

            using SemanticSolutionLease retry = await workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);
            Assert.Equal(1, retry.Coverage.LoadedProjects);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task SharedPreparationKeepsEachCallersReferenceWiringIntent()
    {
        string root = CreateWorkspace("caller-wiring",
            ("OwnerA", null), ("OwnerB", null), ("BridgeA", "OwnerA"),
            ("BridgeB", "OwnerB"), ("Candidate", null));
        try
        {
            File.WriteAllText(Path.Combine(root, "Candidate", "Candidate.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../BridgeA/BridgeA.csproj" />
                    <ProjectReference Include="../BridgeB/BridgeB.csproj" />
                  </ItemGroup>
                </Project>
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            using (SemanticSolutionLease owners = await workspace.EnsureLoadedAsync(
                       ["OwnerA", "OwnerB"], CancellationToken.None))
            {
                Assert.Equal(2, owners.Coverage.LoadedProjects);
            }

            int candidateCaptures = 0;
            var captureEntered = NewSignal();
            var releaseCapture = NewSignal();
            workspace.TestOnlyBeforeProjectCaptureAsync = async (name, cancellationToken) =>
            {
                if (name != "Candidate") return;
                Interlocked.Increment(ref candidateCaptures);
                captureEntered.TrySetResult(true);
                await releaseCapture.Task.WaitAsync(cancellationToken);
            };

            Task<SemanticSolutionLease> forA = workspace.EnsureLoadedAsync(
                ["Candidate"], CancellationToken.None, ensureReferenceTo: ["OwnerA"]);
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<SemanticSolutionLease> forB = workspace.EnsureLoadedAsync(
                ["Candidate"], CancellationToken.None, ensureReferenceTo: ["OwnerB"]);
            await WaitUntilAsync(() => workspace.TestOnlyPreparationWaiters >= 2,
                "both callers did not join the same candidate preparation");
            Assert.Equal(1, Volatile.Read(ref candidateCaptures));
            releaseCapture.TrySetResult(true);

            using SemanticSolutionLease loadA = await forA;
            using SemanticSolutionLease loadB = await forB;
            Assert.Equal(1, candidateCaptures);
            IReadOnlySet<string> referencesA = DirectReferenceNames(loadA.Solution, "Candidate");
            IReadOnlySet<string> referencesB = DirectReferenceNames(loadB.Solution, "Candidate");
            Assert.Contains("OwnerA", referencesA);
            Assert.DoesNotContain("OwnerB", referencesA);
            Assert.Contains("OwnerB", referencesB);
            Assert.DoesNotContain("OwnerA", referencesB);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task WarmCallsRemoveRetiredOperationReferenceWiringWithoutRecapture()
    {
        string root = CreateWorkspace("retired-operation-wiring",
            ("OwnerA", null), ("OwnerB", null), ("BridgeA", "OwnerA"),
            ("BridgeB", "OwnerB"), ("Candidate", null));
        try
        {
            File.WriteAllText(Path.Combine(root, "Candidate", "Candidate.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../BridgeA/BridgeA.csproj" />
                    <ProjectReference Include="../BridgeB/BridgeB.csproj" />
                  </ItemGroup>
                </Project>
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            using (SemanticSolutionLease owners = await workspace.EnsureLoadedAsync(
                       ["OwnerA", "OwnerB"], CancellationToken.None))
            {
                Assert.Equal(2, owners.Coverage.LoadedProjects);
            }

            int candidateCaptures = 0;
            workspace.TestOnlyBeforeProjectCaptureAsync = (name, _) =>
            {
                if (name == "Candidate") Interlocked.Increment(ref candidateCaptures);
                return Task.CompletedTask;
            };

            using (SemanticSolutionLease both = await workspace.EnsureLoadedAsync(
                       ["Candidate"], CancellationToken.None,
                       ensureReferenceTo: ["OwnerA", "OwnerB"]))
            {
                Assert.Equal(1, candidateCaptures);
                Assert.Contains("OwnerA", DirectReferenceNames(both.Solution, "Candidate"));
                Assert.Contains("OwnerB", DirectReferenceNames(both.Solution, "Candidate"));
            }

            using (SemanticSolutionLease subset = await workspace.EnsureLoadedAsync(
                       ["Candidate"], CancellationToken.None,
                       ensureReferenceTo: ["OwnerA"]))
            {
                Assert.Equal(1, candidateCaptures);
                IReadOnlySet<string> references =
                    DirectReferenceNames(subset.Solution, "Candidate");
                Assert.Contains("OwnerA", references);
                Assert.DoesNotContain("OwnerB", references);
            }

            using (SemanticSolutionLease empty = await workspace.EnsureLoadedAsync(
                       ["Candidate"], CancellationToken.None))
            {
                Assert.Equal(1, candidateCaptures);
                IReadOnlySet<string> references =
                    DirectReferenceNames(empty.Solution, "Candidate");
                Assert.DoesNotContain("OwnerA", references);
                Assert.DoesNotContain("OwnerB", references);
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task DefinitionAddsItsOwnUnambiguousTransitiveDeclarationEdge()
    {
        string root = Directory.CreateTempSubdirectory("codenav-definition-operation-edge")
            .FullName;
        try
        {
            const string targetSource =
                "namespace Target; public interface ITarget { }";
            const string consumerSource =
                "namespace Consumer; public sealed class Impl : Target.ITarget { }";
            WriteProject(root, "Target", null, targetSource);
            WriteProject(root, "Intermediate", "Target",
                "namespace Intermediate; public sealed class Marker { }");
            WriteProject(root, "Consumer", "Intermediate", consumerSource);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);

            int column = consumerSource.IndexOf("ITarget", StringComparison.Ordinal) + 1;
            var (definition, reason, _, _) = await semantic.DefinitionAsync(
                "Consumer/Consumer.cs", 1, column, "ITarget", 30_000);

            Assert.Null(reason);
            Assert.NotNull(definition);
            Assert.Contains(definition.Declarations,
                declaration => declaration.Path == "Target/Target.cs");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ConcurrentFailedWaitersReleaseTheirSharedPlannedProjectId()
    {
        string root = CreateWorkspace("failed-planned-id", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            int captures = 0;
            var captureEntered = NewSignal();
            var releaseCapture = NewSignal();
            workspace.TestOnlyBeforeProjectCaptureAsync = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref captures);
                captureEntered.TrySetResult(true);
                await releaseCapture.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("shared terminal preparation failure");
            };

            Task<SemanticSolutionLease> first = workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);
            await captureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<SemanticSolutionLease> second = workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);
            await WaitUntilAsync(() => workspace.TestOnlyPreparationWaiters >= 2,
                "both failing callers did not join one preparation");
            releaseCapture.TrySetResult(true);

            using SemanticSolutionLease failedFirst = await first;
            using SemanticSolutionLease failedSecond = await second;
            Assert.Equal(1, captures);
            Assert.Contains("P", failedFirst.Coverage.FailedProjects);
            Assert.Contains("P", failedSecond.Coverage.FailedProjects);
            Assert.Equal(0, workspace.TestOnlyPlannedProjectIds);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task IndexedFallbackCancellationIsChargedToPreparationAndReleasesAdmission()
    {
        string root = CreateWorkspace("fallback-cancellation", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            File.Delete(Path.Combine(root, "P", "P.cs"));
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 1);
            var fallbackEntered = NewSignal();
            workspace.TestOnlyBeforeIndexedFallbackAsync = async (_, cancellationToken) =>
            {
                fallbackEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            };
            var stats = new SemanticWorkspace.LoadStatsBox();
            using var canceled = new CancellationTokenSource();

            Task<SemanticSolutionLease> loading = workspace.EnsureLoadedAsync(
                ["P"], canceled.Token, statsBox: stats);
            await fallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await loading);

            Assert.NotNull(stats.Stats);
            Assert.True(stats.Stats.PreparedProjects >= 1);
            Assert.True(stats.Stats.PreparationMs > 0,
                "indexed fallback wait was omitted from preparation timing");
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CanceledIndexedFallbackDoesNotPoisonASharedPreparation()
    {
        string root = CreateWorkspace("shared-fallback-cancellation", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            File.Delete(Path.Combine(root, "P", "P.cs"));
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            int captures = 0;
            workspace.TestOnlyBeforeProjectCaptureAsync = (_, _) =>
            {
                Interlocked.Increment(ref captures);
                return Task.CompletedTask;
            };
            using var preparationCompleted = new ManualResetEventSlim();
            using var publishPreparation = new ManualResetEventSlim();
            workspace.TestOnlyAfterProjectPrepared = _ =>
            {
                preparationCompleted.Set();
                publishPreparation.Wait(TimeSpan.FromSeconds(5));
            };
            int fallbackEntries = 0;
            using var firstFallbackEntered = new ManualResetEventSlim();
            using var releaseFirstFallback = new ManualResetEventSlim();
            workspace.TestOnlyAfterIndexedFallbackLock = _ =>
            {
                if (Interlocked.Increment(ref fallbackEntries) != 1) return;
                firstFallbackEntered.Set();
                releaseFirstFallback.Wait(TimeSpan.FromSeconds(5));
            };
            using var canceled = new CancellationTokenSource();

            Task<SemanticSolutionLease> first = workspace.EnsureLoadedAsync(["P"], canceled.Token);
            Assert.True(preparationCompleted.Wait(TimeSpan.FromSeconds(5)),
                "shared preparation did not reach its publication boundary");
            Task<SemanticSolutionLease> second = workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);
            await WaitUntilAsync(() => workspace.TestOnlyPreparationWaiters >= 2,
                "surviving caller did not join the shared preparation");
            publishPreparation.Set();
            Assert.True(firstFallbackEntered.Wait(TimeSpan.FromSeconds(5)),
                "first caller did not reach indexed fallback");
            canceled.Cancel();
            releaseFirstFallback.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
            using SemanticSolutionLease surviving = await second;
            Assert.Equal(1, captures);
            Assert.Equal(1, surviving.Coverage.LoadedProjects);
            Project project = Assert.Single(surviving.Solution.Projects,
                candidate => candidate.Name == "P");
            Assert.Single(project.Documents, document => document.Name == "P.cs");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task WarmResidentRefreshBeforePreparedCommitForcesAReplan()
    {
        string root = CreateWorkspace("warm-refresh", ("A", null), ("B", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var managerLog = new List<string>();
            using var manager = new IndexManager(root, dbPath, managerLog.Add);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            using (SemanticSolutionLease warm = await workspace.EnsureLoadedAsync(
                       ["A"], CancellationToken.None))
            {
                Assert.Equal(1, warm.Coverage.LoadedProjects);
            }

            int beforeCommitCalls = 0;
            var beforeFirstCommit = NewSignal();
            var releaseFirstCommit = NewSignal();
            workspace.TestOnlyBeforeCommitAsync = async cancellationToken =>
            {
                if (Interlocked.Increment(ref beforeCommitCalls) != 1) return;
                beforeFirstCommit.TrySetResult(true);
                await releaseFirstCommit.Task.WaitAsync(cancellationToken);
            };
            var stats = new SemanticWorkspace.LoadStatsBox();
            Task<SemanticSolutionLease> loading = workspace.EnsureLoadedAsync(
                ["A", "B"], CancellationToken.None, statsBox: stats);
            await beforeFirstCommit.Task.WaitAsync(TimeSpan.FromSeconds(5));

            File.WriteAllText(Path.Combine(root, "A", "A.cs"),
                "namespace A; public sealed class AfterRefresh { }");
            IndexManagerTestSupport.RefreshAndWait(manager, ["A/A.cs"],
                queries => queries.ContentByPath("A/A.cs")?.Contains(
                    "AfterRefresh", StringComparison.Ordinal) == true,
                "warm source refresh did not reach the index");
            releaseFirstCommit.TrySetResult(true);

            using SemanticSolutionLease refreshed = await loading;
            Assert.NotNull(stats.Stats);
            Assert.True(stats.Stats.ReplanCount >= 1);
            Project projectA = Assert.Single(refreshed.Solution.Projects,
                project => project.Name == "A");
            Compilation? compilation = await projectA.GetCompilationAsync();
            Assert.NotNull(compilation?.GetTypeByMetadataName("A.AfterRefresh"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task UnrelatedIndexRefreshDoesNotInvalidateAProjectPreparation()
    {
        string root = CreateWorkspace("unrelated-refresh", ("P", null), ("Unrelated", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var managerLog = new List<string>();
            using var manager = new IndexManager(root, dbPath, managerLog.Add);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            int captures = 0;
            int beforeCommitCalls = 0;
            var beforeCommit = NewSignal();
            var releaseCommit = NewSignal();
            workspace.TestOnlyBeforeProjectCaptureAsync = (name, _) =>
            {
                if (name == "P") Interlocked.Increment(ref captures);
                return Task.CompletedTask;
            };
            workspace.TestOnlyBeforeCommitAsync = async cancellationToken =>
            {
                if (Interlocked.Increment(ref beforeCommitCalls) != 1) return;
                beforeCommit.TrySetResult(true);
                await releaseCommit.Task.WaitAsync(cancellationToken);
            };
            var stats = new SemanticWorkspace.LoadStatsBox();
            Task<SemanticSolutionLease> loading = workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None, statsBox: stats);
            await beforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(5));

            File.WriteAllText(Path.Combine(root, "Unrelated", "Unrelated.cs"),
                "namespace Unrelated; public sealed class Refreshed { }");
            IndexManagerTestSupport.RefreshAndWait(manager, ["Unrelated/Unrelated.cs"],
                queries => queries.ContentByPath("Unrelated/Unrelated.cs")?.Contains(
                    "Refreshed", StringComparison.Ordinal) == true,
                "unrelated refresh did not reach the index");
            releaseCommit.TrySetResult(true);

            using SemanticSolutionLease load = await loading;
            Assert.Equal(1, load.Coverage.LoadedProjects);
            Assert.NotNull(stats.Stats);
            Assert.Equal(0, stats.Stats.ReplanCount);
            Assert.Equal(1, captures);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task PlanningDescriptorsAreAdmittedBeforeLargeWorkspaceListsMaterialize()
    {
        string root = CreateWorkspace("planning-budget", ("P", null));
        try
        {
            for (int i = 0; i < 220; i++)
            {
                File.WriteAllText(Path.Combine(root, "P", $"Generated{i:D3}.cs"),
                    $"namespace P; public sealed class Generated{i:D3} {{ }}");
            }
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            const long budget = 64 * 1024;
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: budget,
                preparationConcurrency: 2);

            using SemanticSolutionLease load = await workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);

            Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
                load.Coverage.FailedProjectCauses?["P"]);
            Assert.True(workspace.SemanticInputHighWaterBytes <= budget);
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task DeepDirectoryBuildAuthorityCandidatesAreIncludedInPlanningAdmission()
    {
        string root = Directory.CreateTempSubdirectory("codenav-cold-deep-authority").FullName;
        try
        {
            string directory = root;
            foreach (string segment in Enumerable.Range(0, 25).Select(index => $"d{index:D2}"))
                directory = Path.Combine(directory, segment);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "DeepProject.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(directory, "DeepProject.cs"),
                "namespace DeepProject; public sealed class Marker { }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            const long budget = 16 * 1024;
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: budget,
                preparationConcurrency: 1);

            using SemanticSolutionLease load = await workspace.EnsureLoadedAsync(
                ["DeepProject"], CancellationToken.None);

            Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
                load.Coverage.FailedProjectCauses?["DeepProject"]);
            Assert.True(workspace.SemanticInputHighWaterBytes <= budget);
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CanceledMutationGateWaitIsStillAttributedToGateTelemetry()
    {
        string root = CreateWorkspace("gate-cancel", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 1);
            var gate = (SemaphoreSlim)typeof(SemanticWorkspace).GetField("_gate",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
            await gate.WaitAsync();
            var stats = new SemanticWorkspace.LoadStatsBox();
            using var canceled = new CancellationTokenSource();
            try
            {
                Task<SemanticSolutionLease> waiting = workspace.EnsureLoadedAsync(
                    ["P"], canceled.Token, statsBox: stats);
                await Task.Delay(40);
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            }
            finally
            {
                gate.Release();
            }
            Assert.NotNull(stats.Stats);
            Assert.True(stats.Stats.GateWaitMs > 0,
                "canceled queue time disappeared from gate telemetry");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ExactCSharpDefinitionIsNotDowngradedBySkippedFSharpDependency()
    {
        string root = Directory.CreateTempSubdirectory("codenav-cold-mixed-definition").FullName;
        try
        {
            string fsharpDirectory = Path.Combine(root, "FSharpDependency");
            Directory.CreateDirectory(fsharpDirectory);
            File.WriteAllText(Path.Combine(fsharpDirectory, "FSharpDependency.fsproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Library.fs" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(fsharpDirectory, "Library.fs"),
                "namespace FSharpDependency\ntype Marker = class end");

            string csharpDirectory = Path.Combine(root, "CSharpOwner");
            Directory.CreateDirectory(csharpDirectory);
            File.WriteAllText(Path.Combine(csharpDirectory, "CSharpOwner.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../FSharpDependency/FSharpDependency.fsproj" />
                  </ItemGroup>
                </Project>
                """);
            const string source = "namespace CSharpOwner;\npublic sealed class ExactType { }\n";
            File.WriteAllText(Path.Combine(csharpDirectory, "ExactType.cs"), source);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var managerLog = new List<string>();
            using var manager = new IndexManager(root, dbPath, managerLog.Add);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);

            var (declaration, reason, projectModelUnproven, partialReason) =
                await semantic.DefinitionAsync("CSharpOwner/ExactType.cs", 2,
                    "public sealed class ".Length + 1, "ExactType", 30_000);

            Assert.NotNull(declaration);
            Assert.Null(reason);
            Assert.False(projectModelUnproven);
            Assert.Null(partialReason);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ConcurrentCommitInvalidatesThePlanAndForcesAReplan()
    {
        string root = CreateWorkspace("replan", ("A", null), ("B", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            var beforeCommit = NewSignal();
            var releaseCommit = NewSignal();
            workspace.TestOnlyBeforeCommitAsync = async cancellationToken =>
            {
                beforeCommit.TrySetResult(true);
                await releaseCommit.Task.WaitAsync(cancellationToken);
            };
            var stats = new SemanticWorkspace.LoadStatsBox();
            Task<SemanticSolutionLease> first = workspace.EnsureLoadedAsync(
                ["A"], CancellationToken.None, statsBox: stats);
            await beforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(5));

            workspace.TestOnlyBeforeCommitAsync = null;
            using SemanticSolutionLease competing = await workspace.EnsureLoadedAsync(
                ["B"], CancellationToken.None);
            releaseCommit.TrySetResult(true);
            using SemanticSolutionLease replanned = await first;

            Assert.NotNull(stats.Stats);
            Assert.True(stats.Stats.ReplanCount >= 1);
            Assert.Contains(replanned.Solution.Projects, project => project.Name == "A");
            Assert.Contains(replanned.Solution.Projects, project => project.Name == "B");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CancellationBeforeCommitPublishesNothingAndLeaksNoPreparedInputs()
    {
        string root = CreateWorkspace("cancel-commit", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            var commitEntered = NewSignal();
            workspace.TestOnlyBeforeCommitAsync = async cancellationToken =>
            {
                commitEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            };
            using var canceled = new CancellationTokenSource();
            Task<SemanticSolutionLease> abandoned = workspace.EnsureLoadedAsync(
                ["P"], canceled.Token);
            await commitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);

            workspace.TestOnlyBeforeCommitAsync = null;
            using SemanticSolutionLease retry = await workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);
            Assert.Equal(1, retry.Coverage.LoadedProjects);
            Assert.Single(retry.Solution.Projects);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RejectedPreparedCommitPublishesNothingAndReleasesStagedInputs()
    {
        string root = CreateWorkspace("rejected-commit", ("P", null));
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2)
            {
                TestOnlyRejectPreparedCommit = true,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                workspace.EnsureLoadedAsync(["P"], CancellationToken.None));
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);

            workspace.TestOnlyRejectPreparedCommit = false;
            using SemanticSolutionLease retry = await workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);
            Assert.Equal(1, retry.Coverage.LoadedProjects);
            Assert.Single(retry.Solution.Projects);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ActiveSolutionLeaseKeepsOldGenerationAliveAcrossReload()
    {
        string root = Directory.CreateTempSubdirectory("codenav-cold-lease").FullName;
        try
        {
            WriteProject(root, "P", null, "public sealed class BeforeReload { }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var managerLog = new List<string>();
            using var manager = new IndexManager(root, dbPath, managerLog.Add);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            try
            {
                SemanticSolutionLease before = await workspace.EnsureLoadedAsync(
                    ["P"], CancellationToken.None);
                string sourcePath = Path.Combine(root, "P", "P.cs");
                File.WriteAllText(sourcePath,
                    "namespace P; public sealed class AfterReload { }");
                IndexManagerTestSupport.RefreshAndWait(manager, ["P/P.cs"],
                    queries => queries.ContentByPath("P/P.cs")?.Contains(
                        "AfterReload", StringComparison.Ordinal) == true,
                    "reload source was not indexed");

                using SemanticSolutionLease after = await workspace.EnsureLoadedAsync(
                    ["P"], CancellationToken.None);
                long withBothGenerations = workspace.RetainedSemanticInputBytes;
                var oldCompilation = await Assert.Single(before.Solution.Projects)
                    .GetCompilationAsync();
                var newCompilation = await Assert.Single(after.Solution.Projects)
                    .GetCompilationAsync();
                Assert.NotNull(oldCompilation?.GetTypeByMetadataName("BeforeReload"));
                Assert.Null(oldCompilation?.GetTypeByMetadataName("P.AfterReload"));
                Assert.NotNull(newCompilation?.GetTypeByMetadataName("P.AfterReload"));

                before.Dispose();
                Assert.True(workspace.RetainedSemanticInputBytes < withBothGenerations,
                    "disposing the active old snapshot did not release its project reservation");
            }
            finally
            {
                workspace.Dispose();
            }
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task OversizeProjectFailsImmediatelyWithStableResourceCauseAndNoLeak()
    {
        string root = CreateWorkspace("budget", ("Resident", null));
        try
        {
            WriteProject(root, "P", null,
                "namespace P; public sealed class PType { } /*" +
                new string('x', 32 * 1024) + "*/");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 100 * 1024,
                preparationConcurrency: 2);
            try
            {
                using (SemanticSolutionLease resident = await workspace.EnsureLoadedAsync(
                           ["Resident"], CancellationToken.None))
                {
                    Assert.Equal(1, resident.Coverage.LoadedProjects);
                }
                using SemanticSolutionLease load = await workspace.EnsureLoadedAsync(
                    ["P"], CancellationToken.None);
                Assert.Equal(0, load.Coverage.LoadedProjects);
                Assert.Equal("P", Assert.Single(load.Coverage.FailedProjects));
                Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
                    load.Coverage.FailedProjectCauses?["P"]);
                Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
                    SemanticCoverageReasons.Primary(load.Coverage));
                Assert.Contains(load.Solution.Projects,
                    project => project.Name == "Resident");
                Assert.True(workspace.RetainedSemanticInputBytes > 0);
            }
            finally
            {
                workspace.Dispose();
            }
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task FriendAssemblySyntheticDocumentIsIncludedInProjectAdmission()
    {
        string root = CreateWorkspace("friend-admission", ("P", null));
        try
        {
            File.WriteAllText(Path.Combine(root, "P", "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><InternalsVisibleTo Include="Friend.Consumer" /></ItemGroup>
                </Project>
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 1);

            using SemanticSolutionLease load = await workspace.EnsureLoadedAsync(
                ["P"], CancellationToken.None);

            Assert.Equal(1, load.Coverage.LoadedProjects);
            Assert.Empty(load.Coverage.FailedProjects);
            Project project = Assert.Single(load.Solution.Projects);
            Assert.Contains(project.Documents,
                document => document.Name == "__PhoenixCodeNav.InternalsVisibleTo.g.cs");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task DisjointConcurrentLoadsShareOneProcessBudgetWithoutOvercommit()
    {
        string root = CreateWorkspace("disjoint-budget", ("A", null), ("B", null));
        try
        {
            const long budget = 100 * 1024;
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: budget,
                preparationConcurrency: 2);
            var firstReserved = NewSignal();
            var releaseFirst = NewSignal();
            workspace.TestOnlyBeforeProjectCaptureAsync = async (name, cancellationToken) =>
            {
                if (name != "A") return;
                firstReserved.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(cancellationToken);
            };
            try
            {
                Task<SemanticSolutionLease> first = workspace.EnsureLoadedAsync(
                    ["A"], CancellationToken.None);
                await firstReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using SemanticSolutionLease bounded = await workspace.EnsureLoadedAsync(
                    ["B"], CancellationToken.None);
                Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
                    bounded.Coverage.FailedProjectCauses?["B"]);
                Assert.True(workspace.SemanticInputHighWaterBytes <= budget);

                releaseFirst.TrySetResult(true);
                using SemanticSolutionLease loaded = await first;
                Assert.Equal(1, loaded.Coverage.LoadedProjects);
                Assert.True(workspace.SemanticInputHighWaterBytes <= budget);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                workspace.Dispose();
            }
            Assert.Equal(0, workspace.RetainedSemanticInputBytes);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task AdmissionReclaimsAnUnreferencedResidentBeforeFailingTheNextProject()
    {
        string root = CreateWorkspace("admission-reclaim", ("A", null), ("B", null));
        try
        {
            const long budget = 100 * 1024;
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: budget,
                preparationConcurrency: 1);

            using (SemanticSolutionLease first = await workspace.EnsureLoadedAsync(
                       ["A"], CancellationToken.None))
            {
                Assert.Equal(1, first.Coverage.LoadedProjects);
            }
            using SemanticSolutionLease second = await workspace.EnsureLoadedAsync(
                ["B"], CancellationToken.None);

            Assert.Equal(1, second.Coverage.LoadedProjects);
            Assert.DoesNotContain(second.Solution.Projects, project => project.Name == "A");
            Assert.Contains(second.Solution.Projects, project => project.Name == "B");
            Assert.True(workspace.SemanticInputHighWaterBytes <= budget);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task AdmissionDoesNotEvictAResidentRequestedByTheActiveLoad()
    {
        string root = CreateWorkspace("admission-active-request", ("A", null), ("B", null));
        try
        {
            const long budget = 100 * 1024;
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: budget,
                preparationConcurrency: 1);
            using (SemanticSolutionLease first = await workspace.EnsureLoadedAsync(
                       ["A"], CancellationToken.None))
            {
                Assert.Equal(1, first.Coverage.LoadedProjects);
            }

            using SemanticSolutionLease second = await workspace.EnsureLoadedAsync(
                ["A", "B"], CancellationToken.None);

            Assert.Contains(second.Solution.Projects, project => project.Name == "A");
            Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
                second.Coverage.FailedProjectCauses?["B"]);
            Assert.True(workspace.SemanticInputHighWaterBytes <= budget);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task FailedDependencyPreparationKeepsTheConsumersBinaryFallback()
    {
        string root = Directory.CreateTempSubdirectory("codenav-cold-binary-fallback").FullName;
        try
        {
            WriteProject(root, "Dependency", null,
                "namespace Dependency; public class ExternalType { } /*" +
                new string('x', 32 * 1024) + "*/");
            WriteProject(root, "Consumer", null,
                "namespace Consumer; public sealed class Derived : Dependency.ExternalType { }");
            string bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            string dependencyDll = Path.Combine(bin, "Dependency.dll");
            EmitAssembly(dependencyDll, "Dependency",
                "namespace Dependency; public class ExternalType { }");
            File.WriteAllText(Path.Combine(root, "Consumer", "Consumer.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Dependency/Dependency.csproj" />
                    <Reference Include="Dependency">
                      <HintPath>../bin/Dependency.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 96 * 1024,
                preparationConcurrency: 1);
            using SemanticSolutionLease load = await workspace.EnsureLoadedAsync(
                ["Dependency", "Consumer"], CancellationToken.None);

            Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
                load.Coverage.FailedProjectCauses?["Dependency"]);
            Project consumer = Assert.Single(load.Solution.Projects,
                project => project.Name == "Consumer");
            Assert.Contains(consumer.MetadataReferences.OfType<PortableExecutableReference>(),
                reference => string.Equals(reference.FilePath, dependencyDll,
                    StringComparison.OrdinalIgnoreCase));
            Compilation? compilation = await consumer.GetCompilationAsync();
            Assert.NotNull(compilation?.GetTypeByMetadataName("Dependency.ExternalType"));
            Assert.Empty(consumer.ProjectReferences);

            var managerLog = new List<string>();
            using var manager = new IndexManager(root, dbPath, managerLog.Add);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var constrainedWorkspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 96 * 1024,
                preparationConcurrency: 1);
            typeof(SemanticService).GetField("_workspace",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(semantic, constrainedWorkspace);
            var tools = new NavigationTools(manager, semantic);
            using JsonDocument definition = JsonDocument.Parse(tools.Definition(
                name: "Derived",
                path: "Consumer/Consumer.cs",
                line: 1,
                mode: "semantic"));
            Assert.True(definition.RootElement.GetProperty("partial").GetBoolean());
            Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
                definition.RootElement.GetProperty("partialReason").GetString());
            Assert.Equal("indexed", definition.RootElement.GetProperty("meta")
                .GetProperty("confidence").GetString());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RecoveredDependencyRewiresWarmConsumerAndSuppressesBinaryFallback()
    {
        string root = Directory.CreateTempSubdirectory("codenav-cold-dependency-recovery").FullName;
        try
        {
            WriteProject(root, "Dependency", null,
                "namespace Dependency; public class SourceType { }");
            WriteProject(root, "Consumer", null,
                "namespace Consumer; public sealed class Derived : Dependency.SourceType { }");
            string bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            string dependencyDll = Path.Combine(bin, "Dependency.dll");
            EmitAssembly(dependencyDll, "Dependency",
                "namespace Dependency; public class SourceType { }");
            File.WriteAllText(Path.Combine(root, "Consumer", "Consumer.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Dependency/Dependency.csproj" />
                    <Reference Include="Dependency">
                      <HintPath>../bin/Dependency.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            int dependencyAttempts = 0;
            workspace.TestOnlyBeforeProjectCaptureAsync = (name, _) =>
            {
                if (name == "Dependency" && Interlocked.Increment(ref dependencyAttempts) == 1)
                    throw new InvalidOperationException("transient dependency capture failure");
                return Task.CompletedTask;
            };

            using (SemanticSolutionLease first = await workspace.EnsureLoadedAsync(
                       ["Dependency", "Consumer"], CancellationToken.None))
            {
                Project consumer = Assert.Single(first.Solution.Projects,
                    project => project.Name == "Consumer");
                Assert.Empty(consumer.ProjectReferences);
                Assert.Contains(consumer.MetadataReferences.OfType<PortableExecutableReference>(),
                    reference => string.Equals(reference.FilePath, dependencyDll,
                        StringComparison.OrdinalIgnoreCase));
            }

            using SemanticSolutionLease recovered = await workspace.EnsureLoadedAsync(
                ["Dependency"], CancellationToken.None);
            Project recoveredConsumer = Assert.Single(recovered.Solution.Projects,
                project => project.Name == "Consumer");
            ProjectReference direct = Assert.Single(recoveredConsumer.ProjectReferences);
            Assert.Equal("Dependency", recovered.Solution.GetProject(direct.ProjectId)?.Name);
            Assert.DoesNotContain(
                recoveredConsumer.MetadataReferences.OfType<PortableExecutableReference>(),
                reference => string.Equals(reference.FilePath, dependencyDll,
                    StringComparison.OrdinalIgnoreCase));
            Compilation? compilation = await recoveredConsumer.GetCompilationAsync();
            Assert.NotNull(compilation?.GetTypeByMetadataName("Dependency.SourceType"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task FailedDependencyReloadRestoresWarmConsumersBinaryFallback()
    {
        string root = Directory.CreateTempSubdirectory("codenav-cold-dependency-reload-failure")
            .FullName;
        try
        {
            WriteProject(root, "Dependency", null,
                "namespace Dependency; public class SourceType { }");
            WriteProject(root, "Consumer", null,
                "namespace Consumer; public sealed class Derived : Dependency.SourceType { }");
            string bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            string dependencyDll = Path.Combine(bin, "Dependency.dll");
            EmitAssembly(dependencyDll, "Dependency",
                "namespace Dependency; public class SourceType { }");
            File.WriteAllText(Path.Combine(root, "Consumer", "Consumer.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Dependency/Dependency.csproj" />
                    <Reference Include="Dependency">
                      <HintPath>../bin/Dependency.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            var managerLog = new List<string>();
            using var manager = new IndexManager(root, dbPath, managerLog.Add);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000));
            using var workspace = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            using (SemanticSolutionLease warm = await workspace.EnsureLoadedAsync(
                       ["Dependency", "Consumer"], CancellationToken.None))
            {
                Project consumer = Assert.Single(warm.Solution.Projects,
                    project => project.Name == "Consumer");
                Assert.Single(consumer.ProjectReferences);
                Assert.DoesNotContain(
                    consumer.MetadataReferences.OfType<PortableExecutableReference>(),
                    reference => string.Equals(reference.FilePath, dependencyDll,
                        StringComparison.OrdinalIgnoreCase));
            }

            File.WriteAllText(Path.Combine(root, "Dependency", "Dependency.cs"),
                "namespace Dependency; public class SourceType { public int Changed; }");
            IndexManagerTestSupport.RefreshAndWait(manager, ["Dependency/Dependency.cs"],
                queries => queries.ContentByPath("Dependency/Dependency.cs")?.Contains(
                    "Changed", StringComparison.Ordinal) == true,
                "dependency refresh did not reach the index");
            workspace.TestOnlyBeforeProjectCaptureAsync = (name, _) =>
            {
                if (name == "Dependency")
                    throw new InvalidOperationException("dependency reload failed");
                return Task.CompletedTask;
            };

            using SemanticSolutionLease fallback = await workspace.EnsureLoadedAsync(
                ["Dependency"], CancellationToken.None);
            Project fallbackConsumer = Assert.Single(fallback.Solution.Projects,
                project => project.Name == "Consumer");
            Assert.Empty(fallbackConsumer.ProjectReferences);
            Assert.Contains(
                fallbackConsumer.MetadataReferences.OfType<PortableExecutableReference>(),
                reference => string.Equals(reference.FilePath, dependencyDll,
                    StringComparison.OrdinalIgnoreCase));
            Compilation? compilation = await fallbackConsumer.GetCompilationAsync();
            Assert.NotNull(compilation?.GetTypeByMetadataName("Dependency.SourceType"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ResourceFailureCoverageIsBoundedAndMixedFailuresKeepGenericPrecedence()
    {
        var coverage = new ClusterCoverage(
            LoadedProjects: 0,
            RequestedProjects: 2,
            SkippedProjects: [],
            FailedProjects: ["Budget", "Broken"],
            FrameworkRefsAvailable: true,
            FailedProjectCauses: new Dictionary<string, string>
            {
                ["Budget"] = SemanticCoverageReasons.ResourceBudgetExhausted,
            });
        Assert.Equal("project_load_failed", SemanticCoverageReasons.Primary(coverage));

        MethodInfo method = typeof(NavigationTools).GetMethod("CoverageJson",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object shaped = method.Invoke(null, [coverage])!;
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(shaped));
        JsonElement root = json.RootElement;
        Assert.Equal(1, root.GetProperty("resourceBudgetFailedProjectCount").GetInt32());
        JsonElement cause = Assert.Single(root.GetProperty("failedProjectCauses").EnumerateArray());
        Assert.Equal(SemanticCoverageReasons.ResourceBudgetExhausted,
            cause.GetProperty("cause").GetString());
        Assert.Equal(1, cause.GetProperty("projectCount").GetInt32());

        var manyFailures = Enumerable.Range(0, 10).Select(index => $"P{index}").ToList();
        var manyCauses = manyFailures.ToDictionary(
            project => project,
            project => $"semantic_test_cause_{project}",
            StringComparer.OrdinalIgnoreCase);
        manyCauses["NotFailed"] = SemanticCoverageReasons.ResourceBudgetExhausted;
        var boundedCoverage = coverage with
        {
            RequestedProjects = manyFailures.Count,
            FailedProjects = manyFailures,
            FailedProjectCauses = manyCauses,
        };
        object boundedShape = method.Invoke(null, [boundedCoverage])!;
        using JsonDocument boundedJson = JsonDocument.Parse(
            JsonSerializer.Serialize(boundedShape));
        Assert.Equal(8, boundedJson.RootElement.GetProperty("failedProjectCauses")
            .GetArrayLength());
        Assert.Equal(10, boundedJson.RootElement.GetProperty("failedProjectCauseCount")
            .GetInt32());
        Assert.True(boundedJson.RootElement.GetProperty("failedProjectCausesTruncated")
            .GetBoolean());
        Assert.Equal(JsonValueKind.Null, boundedJson.RootElement
            .GetProperty("resourceBudgetFailedProjectCount").ValueKind);

        var completeCoverage = new ClusterCoverage(1, 1, [], [], true);
        object completeShape = method.Invoke(null, [completeCoverage])!;
        using JsonDocument completeJson = JsonDocument.Parse(
            JsonSerializer.Serialize(completeShape));
        Assert.Equal(JsonValueKind.Null,
            completeJson.RootElement.GetProperty("failedProjectCauses").ValueKind);
    }

    private static string CreateWorkspace(string suffix,
        params (string Name, string? Reference)[] projects)
    {
        string root = Directory.CreateTempSubdirectory($"codenav-cold-{suffix}").FullName;
        foreach ((string name, string? reference) in projects)
            WriteProject(root, name, reference,
                $"namespace {name}; public sealed class {name}Type {{ }}");
        return root;
    }

    private static void WriteProject(string root, string name, string? reference, string source)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        string itemGroup = reference is null
            ? ""
            : $"<ItemGroup><ProjectReference Include=\"../{reference}/{reference}.csproj\" /></ItemGroup>";
        File.WriteAllText(Path.Combine(directory, $"{name}.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{itemGroup}</Project>");
        File.WriteAllText(Path.Combine(directory, $"{name}.cs"), source);
    }

    private static void EmitAssembly(string path, string name, string source)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var emitted = compilation.Emit(path);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics.Take(3)));
    }

    private static HashSet<string> DirectReferenceNames(Solution solution, string projectName)
    {
        Project project = Assert.Single(solution.Projects, candidate =>
            candidate.Name == projectName);
        return project.ProjectReferences
            .Select(reference => solution.GetProject(reference.ProjectId)?.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBatchedProjectRowsQuery(string sql) =>
        sql.StartsWith("SELECT id, path, name, style, tfms, is_test, load_status, lang ",
            StringComparison.Ordinal) &&
        sql.Contains("WHERE name COLLATE NOCASE IN", StringComparison.Ordinal);

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> predicate, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new Xunit.Sdk.XunitException(failure);
            await Task.Delay(10);
        }
    }

    private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline) Thread.Sleep(20);
        return predicate();
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
