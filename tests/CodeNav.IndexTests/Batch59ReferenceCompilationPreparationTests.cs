using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Tests;

public sealed class Batch59ReferenceCompilationPreparationTests
{
    [Fact]
    public async Task PreparationUsesDependencyWavesAndOverlapsOnlyReadySiblings()
    {
        string root = Directory.CreateTempSubdirectory("codenav-59-waves").FullName;
        using var roslyn = new AdhocWorkspace();
        using var semantic = new CodeNav.Core.Semantic.SemanticWorkspace(root,
            Path.Combine(root, "unused.db"), preparationConcurrency: 2);
        try
        {
            (Solution solution, Dictionary<string, ProjectId> ids) = CreateDiamond(roslyn);
            ProjectId orphan = ProjectId.CreateNewId("Orphan");
            solution = AddProject(solution, orphan, "Orphan");
            using var lease = Lease(solution, ids["Top"], orphan);
            var stats = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();
            var siblingGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var leftStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var rightStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int baseCompleted = 0;
            int siblingsCompleted = 0;
            int active = 0;
            int highWater = 0;

            semantic.TestOnlyGetCompilationAsync = async (project, cancellationToken) =>
            {
                Assert.Same(solution, project.Solution);
                switch (project.Name)
                {
                    case "Base":
                        Interlocked.Exchange(ref baseCompleted, 1);
                        break;
                    case "Left":
                    case "Right":
                        Assert.Equal(1, Volatile.Read(ref baseCompleted));
                        int now = Interlocked.Increment(ref active);
                        UpdateMax(ref highWater, now);
                        (project.Name == "Left" ? leftStarted : rightStarted).TrySetResult(true);
                        await siblingGate.Task.WaitAsync(cancellationToken);
                        Interlocked.Decrement(ref active);
                        Interlocked.Increment(ref siblingsCompleted);
                        break;
                    case "Top":
                        Assert.Equal(2, Volatile.Read(ref siblingsCompleted));
                        break;
                }
                return CSharpCompilation.Create(project.Name);
            };

            Task preparing = semantic.PrepareCompilationsAsync(lease, "Base", stats,
                CancellationToken.None);
            await Task.WhenAll(leftStarted.Task, rightStarted.Task).WaitAsync(
                TimeSpan.FromSeconds(10));
            Assert.False(preparing.IsCompleted);
            siblingGate.TrySetResult(true);
            await preparing.WaitAsync(TimeSpan.FromSeconds(10));

            var measured = Assert.IsType<CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStats>(stats.Stats);
            Assert.Equal(2, measured.RequestedProjects);
            Assert.Equal(4, measured.GraphProjects);
            Assert.Equal(4, measured.PreparedProjects);
            Assert.Equal(0, measured.CacheHits);
            Assert.Equal(3, measured.Waves);
            Assert.Equal(2, measured.LaneLimit);
            Assert.Equal(2, measured.EffectiveConcurrency);
            Assert.Equal(2, highWater);
            Assert.Equal(0, measured.UnfinishedProjects);
        }
        finally
        {
            semantic.TestOnlyGetCompilationAsync = null;
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task PreparationReusesTheExactSolutionCompilationTracker()
    {
        string root = Directory.CreateTempSubdirectory("codenav-59-cache").FullName;
        using var roslyn = new AdhocWorkspace();
        using var semantic = new CodeNav.Core.Semantic.SemanticWorkspace(root,
            Path.Combine(root, "unused.db"), preparationConcurrency: 2);
        try
        {
            ProjectId id = ProjectId.CreateNewId("P");
            Solution solution = AddProject(roslyn.CurrentSolution, id, "P");
            using var lease = Lease(solution, id);
            var first = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();
            var second = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();

            await semantic.PrepareCompilationsAsync(lease, "P", first, CancellationToken.None);
            await semantic.PrepareCompilationsAsync(lease, "P", second, CancellationToken.None);

            Assert.Equal(1, first.Stats?.PreparedProjects);
            Assert.Equal(0, first.Stats?.CacheHits);
            Assert.Equal(0, second.Stats?.PreparedProjects);
            Assert.Equal(1, second.Stats?.CacheHits);
            Assert.Equal(0, second.Stats?.UnfinishedProjects);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task ConcurrentOperationsShareTheWorkspaceLaneLimit()
    {
        string root = Directory.CreateTempSubdirectory("codenav-59-shared-limit").FullName;
        using var roslyn = new AdhocWorkspace();
        using var semantic = new CodeNav.Core.Semantic.SemanticWorkspace(root,
            Path.Combine(root, "unused.db"), preparationConcurrency: 2);
        try
        {
            var ids = Enumerable.Range(0, 4).ToDictionary(i => $"P{i}",
                i => ProjectId.CreateNewId($"P{i}"));
            Solution solution = roslyn.CurrentSolution;
            foreach ((string name, ProjectId id) in ids) solution = AddProject(solution, id, name);
            solution = solution
                .AddProjectReference(ids["P1"], new ProjectReference(ids["P0"]))
                .AddProjectReference(ids["P3"], new ProjectReference(ids["P2"]));
            using var firstLease = Lease(solution, ids["P0"], ids["P1"]);
            using var secondLease = Lease(solution, ids["P2"], ids["P3"]);
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var twoEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0;
            int highWater = 0;
            int entered = 0;
            semantic.TestOnlyGetCompilationAsync = async (project, cancellationToken) =>
            {
                int now = Interlocked.Increment(ref active);
                UpdateMax(ref highWater, now);
                if (Interlocked.Increment(ref entered) >= 2) twoEntered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref active);
                return CSharpCompilation.Create(project.Name);
            };

            var first = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();
            var second = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();
            Task firstTask = semantic.PrepareCompilationsAsync(firstLease, "P0", first,
                CancellationToken.None);
            Task secondTask = semantic.PrepareCompilationsAsync(secondLease, "P2", second,
                CancellationToken.None);
            await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Volatile.Read(ref highWater));
            release.TrySetResult(true);
            await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, highWater);
            Assert.Equal(4, first.Stats!.PreparedProjects + second.Stats!.PreparedProjects);
            Assert.Equal(0, first.Stats.UnfinishedProjects + second.Stats.UnfinishedProjects);
        }
        finally
        {
            semantic.TestOnlyGetCompilationAsync = null;
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task SeparateWorkspacesShareTheProductionProcessLaneLimit()
    {
        string firstRoot = Directory.CreateTempSubdirectory("codenav-59-process-a").FullName;
        string secondRoot = Directory.CreateTempSubdirectory("codenav-59-process-b").FullName;
        using var firstRoslyn = new AdhocWorkspace();
        using var secondRoslyn = new AdhocWorkspace();
        using var firstSemantic = new CodeNav.Core.Semantic.SemanticWorkspace(firstRoot,
            Path.Combine(firstRoot, "unused.db"));
        using var secondSemantic = new CodeNav.Core.Semantic.SemanticWorkspace(secondRoot,
            Path.Combine(secondRoot, "unused.db"));
        int laneLimit = Math.Min(8, Math.Max(1, Environment.ProcessorCount));
        try
        {
            (Solution firstSolution, ProjectId[] firstIds) = CreateWideGraph(firstRoslyn,
                "First", laneLimit);
            (Solution secondSolution, ProjectId[] secondIds) = CreateWideGraph(secondRoslyn,
                "Second", laneLimit);
            using var firstLease = Lease(firstSolution, firstIds);
            using var secondLease = Lease(secondSolution, secondIds);
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var lanesFilled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0;
            int entered = 0;
            int highWater = 0;

            async Task<Compilation?> Compile(Project project, CancellationToken cancellationToken)
            {
                if (project.Name.EndsWith("Base", StringComparison.Ordinal))
                    return CSharpCompilation.Create(project.Name);
                int now = Interlocked.Increment(ref active);
                UpdateMax(ref highWater, now);
                if (Interlocked.Increment(ref entered) >= laneLimit)
                    lanesFilled.TrySetResult(true);
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return CSharpCompilation.Create(project.Name);
                }
                finally { Interlocked.Decrement(ref active); }
            }

            firstSemantic.TestOnlyGetCompilationAsync = Compile;
            secondSemantic.TestOnlyGetCompilationAsync = Compile;
            var firstStats = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();
            var secondStats = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();
            Task first = firstSemantic.PrepareCompilationsAsync(firstLease, "FirstBase",
                firstStats, CancellationToken.None);
            Task second = secondSemantic.PrepareCompilationsAsync(secondLease, "SecondBase",
                secondStats, CancellationToken.None);

            await lanesFilled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(laneLimit, Volatile.Read(ref highWater));
            release.TrySetResult(true);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(laneLimit, highWater);
            Assert.Equal(laneLimit, firstStats.Stats?.LaneLimit);
            Assert.Equal(laneLimit, secondStats.Stats?.LaneLimit);
            Assert.Equal(0, firstStats.Stats!.UnfinishedProjects +
                secondStats.Stats!.UnfinishedProjects);
        }
        finally
        {
            firstSemantic.TestOnlyGetCompilationAsync = null;
            secondSemantic.TestOnlyGetCompilationAsync = null;
            TestWorkspaceCleanup.DeleteWorkspace(firstRoot);
            TestWorkspaceCleanup.DeleteWorkspace(secondRoot);
        }
    }

    [Fact]
    public async Task CancellationPublishesUnfinishedWorkAndReleasesTheLane()
    {
        string root = Directory.CreateTempSubdirectory("codenav-59-cancel").FullName;
        using var roslyn = new AdhocWorkspace();
        using var semantic = new CodeNav.Core.Semantic.SemanticWorkspace(root,
            Path.Combine(root, "unused.db"), preparationConcurrency: 1);
        try
        {
            ProjectId id = ProjectId.CreateNewId("P");
            Solution solution = AddProject(roslyn.CurrentSolution, id, "P");
            using var lease = Lease(solution, id);
            var entered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            semantic.TestOnlyGetCompilationAsync = async (_, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            };
            using var cancellation = new CancellationTokenSource();
            var cancelledStats = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();
            Task cancelled = semantic.PrepareCompilationsAsync(lease, "P", cancelledStats,
                cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            Assert.Equal(1, cancelledStats.Stats?.UnfinishedProjects);
            Assert.Equal(1, cancelledStats.Stats?.EffectiveConcurrency);

            semantic.TestOnlyGetCompilationAsync = (project, _) =>
                Task.FromResult<Compilation?>(CSharpCompilation.Create(project.Name));
            var retryStats = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();
            await semantic.PrepareCompilationsAsync(lease, "P", retryStats,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, retryStats.Stats?.PreparedProjects);
            Assert.Equal(0, retryStats.Stats?.UnfinishedProjects);
        }
        finally
        {
            semantic.TestOnlyGetCompilationAsync = null;
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task PreparationFailureIsCountedAndDoesNotReplaceSymbolFinderAuthority()
    {
        string root = Directory.CreateTempSubdirectory("codenav-59-failure").FullName;
        using var roslyn = new AdhocWorkspace();
        using var semantic = new CodeNav.Core.Semantic.SemanticWorkspace(root,
            Path.Combine(root, "unused.db"), preparationConcurrency: 1);
        try
        {
            ProjectId dependency = ProjectId.CreateNewId("Dependency");
            ProjectId target = ProjectId.CreateNewId("Target");
            ProjectId uncancelled = ProjectId.CreateNewId("Uncancelled");
            Solution solution = AddProject(roslyn.CurrentSolution, dependency, "Dependency");
            solution = AddProject(solution, target, "Target")
                .AddProjectReference(target, new ProjectReference(dependency));
            solution = AddProject(solution, uncancelled, "Uncancelled")
                .AddProjectReference(uncancelled, new ProjectReference(dependency));
            using var lease = Lease(solution, target, uncancelled);
            semantic.TestOnlyGetCompilationAsync = (project, _) => project.Name switch
            {
                "Dependency" => Task.FromException<Compilation?>(
                    new InvalidOperationException("probe")),
                "Uncancelled" => Task.FromException<Compilation?>(
                    new OperationCanceledException("unrelated token")),
                _ => Task.FromResult<Compilation?>(CSharpCompilation.Create(project.Name))
            };
            var stats = new CodeNav.Core.Semantic.SemanticWorkspace
                .CompilationPreparationStatsBox();

            await semantic.PrepareCompilationsAsync(lease, "Dependency", stats,
                CancellationToken.None);

            Assert.Equal(2, stats.Stats?.FailedProjects);
            Assert.Equal(1, stats.Stats?.PreparedProjects);
            Assert.Equal(0, stats.Stats?.UnfinishedProjects);
            Assert.Equal(2, stats.Stats?.Waves);
        }
        finally
        {
            semantic.TestOnlyGetCompilationAsync = null;
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static CodeNav.Core.Semantic.SemanticSolutionLease Lease(Solution solution,
        params ProjectId[] requested) => new(solution,
        new CodeNav.Core.Semantic.ClusterCoverage(requested.Length, requested.Length, [], [], true,
            solution.ProjectIds.Count), requested, () => { });

    private static (Solution Solution, Dictionary<string, ProjectId> Ids) CreateDiamond(
        AdhocWorkspace workspace)
    {
        var ids = new[] { "Base", "Left", "Right", "Top" }.ToDictionary(name => name,
            ProjectId.CreateNewId);
        Solution solution = workspace.CurrentSolution;
        foreach ((string name, ProjectId id) in ids) solution = AddProject(solution, id, name);
        solution = solution
            .AddProjectReference(ids["Left"], new ProjectReference(ids["Base"]))
            .AddProjectReference(ids["Right"], new ProjectReference(ids["Base"]))
            .AddProjectReference(ids["Top"], new ProjectReference(ids["Left"]))
            .AddProjectReference(ids["Top"], new ProjectReference(ids["Right"]));
        return (solution, ids);
    }

    private static Solution AddProject(Solution solution, ProjectId id, string name)
    {
        solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name,
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary)));
        return solution.AddDocument(DocumentId.CreateNewId(id), $"{name}.cs",
            SourceText.From($"public sealed class {name} {{ }}"));
    }

    private static (Solution Solution, ProjectId[] Ids) CreateWideGraph(
        AdhocWorkspace workspace, string prefix, int consumerCount)
    {
        ProjectId baseId = ProjectId.CreateNewId($"{prefix}Base");
        Solution solution = AddProject(workspace.CurrentSolution, baseId, $"{prefix}Base");
        var ids = new List<ProjectId> { baseId };
        for (int i = 0; i < consumerCount; i++)
        {
            ProjectId consumerId = ProjectId.CreateNewId($"{prefix}Consumer{i}");
            solution = AddProject(solution, consumerId, $"{prefix}Consumer{i}")
                .AddProjectReference(consumerId, new ProjectReference(baseId));
            ids.Add(consumerId);
        }
        return (solution, ids.ToArray());
    }

    private static void UpdateMax(ref int target, int value)
    {
        int observed;
        while (value > (observed = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, observed) != observed)
        {
        }
    }
}
