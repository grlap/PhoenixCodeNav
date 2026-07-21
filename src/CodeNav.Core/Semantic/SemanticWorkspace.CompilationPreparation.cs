using Microsoft.CodeAnalysis;

namespace CodeNav.Core.Semantic;

public sealed partial class SemanticWorkspace
{
    /// <summary>Per-operation, privacy-safe attribution for eager compilation preparation.
    /// Summed queue time may exceed wall time because projects in one dependency wave wait in
    /// parallel. Unfinished projects are selected graph projects that did not reach a terminal
    /// outcome before cancellation.</summary>
    internal sealed record CompilationPreparationStats(
        double TotalMs,
        double QueueMs,
        int RequestedProjects,
        int GraphProjects,
        int CacheHits,
        int PreparedProjects,
        int FailedProjects,
        int SkippedProjects,
        int UnfinishedProjects,
        int Waves,
        int LaneLimit,
        int EffectiveConcurrency);

    internal sealed class CompilationPreparationStatsBox
    {
        public CompilationPreparationStats? Stats { get; internal set; }
    }

    internal Func<Project, CancellationToken, Task<Compilation?>>? TestOnlyGetCompilationAsync
    { get; set; }

    /// <summary>
    /// Materializes compilations for the projects selected by this operation and their actual
    /// Roslyn project dependencies. Work is performed on the lease's exact immutable Solution;
    /// callers must pass that same Solution to SymbolFinder so CompilationTracker results are
    /// reused instead of duplicated across snapshots.
    /// </summary>
    internal async Task PrepareCompilationsAsync(SemanticSolutionLease lease,
        string owningProject,
        CompilationPreparationStatsBox statsBox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(statsBox);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Solution solution = lease.Solution;
        ProjectDependencyGraph graph = solution.GetProjectDependencyGraph();
        var requested = lease.RequestedProjectIds
            .Where(id => solution.GetProject(id) is not null)
            .Distinct()
            .ToHashSet();
        ProjectId? owningProjectId = solution.ProjectIds.FirstOrDefault(id => string.Equals(
            solution.GetProject(id)?.Name, owningProject, StringComparison.OrdinalIgnoreCase));
        HashSet<ProjectId> searchProjects;
        if (owningProjectId is not null)
        {
            searchProjects = graph.GetProjectsThatTransitivelyDependOnThisProject(owningProjectId)
                .Where(requested.Contains).ToHashSet();
            if (requested.Contains(owningProjectId)) searchProjects.Add(owningProjectId);
            // A missing root would make narrowing speculative. Fall back to every requested
            // project and let SymbolFinder remain the authority.
            if (searchProjects.Count == 0) searchProjects.UnionWith(requested);
        }
        else
        {
            searchProjects = new HashSet<ProjectId>(requested);
        }
        var selected = new HashSet<ProjectId>(searchProjects);
        var pendingDependencies = new Stack<ProjectId>(searchProjects);
        while (pendingDependencies.Count > 0)
        {
            ProjectId projectId = pendingDependencies.Pop();
            foreach (ProjectId dependencyId in
                     graph.GetProjectsThatThisProjectDirectlyDependsOn(projectId))
            {
                if (solution.GetProject(dependencyId) is not null && selected.Add(dependencyId))
                    pendingDependencies.Push(dependencyId);
            }
        }

        long queueTicks = 0;
        int cacheHits = 0;
        int prepared = 0;
        int failed = 0;
        int skipped = 0;
        int completedCount = 0;
        int waves = 0;
        int active = 0;
        int effectiveConcurrency = 0;
        var completed = new HashSet<ProjectId>();
        var remaining = new HashSet<ProjectId>(selected);

        static void Max(ref int target, int value)
        {
            int observed;
            while (value > (observed = Volatile.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, observed) != observed)
            {
            }
        }

        async Task PrepareOneAsync(ProjectId projectId)
        {
            Project? project = solution.GetProject(projectId);
            if (project is null)
            {
                Interlocked.Increment(ref skipped);
                Interlocked.Increment(ref completedCount);
                return;
            }
            if (project.TryGetCompilation(out Compilation? cached) && cached is not null)
            {
                Interlocked.Increment(ref cacheHits);
                Interlocked.Increment(ref completedCount);
                return;
            }

            long queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            bool acquired = false;
            bool queueRecorded = false;
            bool activeEntered = false;
            try
            {
                await _coldStartRuntime.ProjectSlots.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                acquired = true;
                Interlocked.Add(ref queueTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - queuedAt);
                queueRecorded = true;
                // A concurrent references operation may have populated this exact snapshot's
                // CompilationTracker while this project waited for the shared process lane.
                if (project.TryGetCompilation(out cached) && cached is not null)
                {
                    Interlocked.Increment(ref cacheHits);
                    Interlocked.Increment(ref completedCount);
                    return;
                }

                activeEntered = true;
                int nowActive = Interlocked.Increment(ref active);
                Max(ref effectiveConcurrency, nowActive);
                Compilation? compilation = TestOnlyGetCompilationAsync is { } testCompile
                    ? await testCompile(project, cancellationToken).ConfigureAwait(false)
                    : await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null) Interlocked.Increment(ref skipped);
                else Interlocked.Increment(ref prepared);
                Interlocked.Increment(ref completedCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // This is a warming optimization. SymbolFinder remains the authority and gets the
                // opportunity to succeed even when one eager compilation could not be prepared.
                Interlocked.Increment(ref failed);
                Interlocked.Increment(ref completedCount);
                _log($"Semantic compilation preparation failed for {project.Name}: {ex.Message}");
            }
            finally
            {
                if (!queueRecorded)
                {
                    Interlocked.Add(ref queueTicks,
                        System.Diagnostics.Stopwatch.GetTimestamp() - queuedAt);
                }
                if (acquired)
                {
                    if (activeEntered) Interlocked.Decrement(ref active);
                    _coldStartRuntime.ProjectSlots.Release();
                }
            }
        }

        try
        {
            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectId[] ready = remaining
                    .Where(id => graph.GetProjectsThatThisProjectDirectlyDependsOn(id)
                        .Where(selected.Contains).All(completed.Contains))
                    .OrderBy(id => solution.GetProject(id)?.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(id => id.Id)
                    .ToArray();
                if (ready.Length == 0)
                {
                    // Project-reference cycles are rejected while wiring the semantic workspace.
                    // If a future Roslyn snapshot nevertheless exposes one, schedule the remaining
                    // set once instead of hanging the operation; SymbolFinder remains authoritative.
                    ready = remaining
                        .OrderBy(id => solution.GetProject(id)?.Name,
                            StringComparer.OrdinalIgnoreCase)
                        .ThenBy(id => id.Id)
                        .ToArray();
                }

                waves++;
                await Task.WhenAll(ready.Select(PrepareOneAsync)).ConfigureAwait(false);
                foreach (ProjectId projectId in ready)
                {
                    remaining.Remove(projectId);
                    completed.Add(projectId);
                }
            }
        }
        finally
        {
            statsBox.Stats = new CompilationPreparationStats(
                TotalMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                QueueMs: ToMs(Interlocked.Read(ref queueTicks)),
                RequestedProjects: requested.Count,
                GraphProjects: selected.Count,
                CacheHits: Volatile.Read(ref cacheHits),
                PreparedProjects: Volatile.Read(ref prepared),
                FailedProjects: Volatile.Read(ref failed),
                SkippedProjects: Volatile.Read(ref skipped),
                UnfinishedProjects: Math.Max(0, selected.Count - Volatile.Read(ref completedCount)),
                Waves: waves,
                LaneLimit: _coldStartRuntime.Concurrency,
                EffectiveConcurrency: Volatile.Read(ref effectiveConcurrency));
        }
    }
}
