using CodeNav.Core.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeNav.Core.Semantic;

public sealed record ClusterCoverage(
    int LoadedProjects,
    int RequestedProjects,
    List<string> SkippedProjects,
    List<string> FailedProjects,
    bool FrameworkRefsAvailable,
    // Field 0.7.0 ("coverage 1/1 but 8 hits from 8 projects"): SymbolFinder scans the WHOLE
    // solution, including projects resident from earlier calls — this makes that visible.
    int SolutionProjects = 0,
    // Projects whose locally modeled InternalsVisibleTo attributes can be changed by imported
    // MSBuild authority. Friend-only relationships involving these projects are candidates, not
    // exact facts from the real build.
    IReadOnlyList<string>? UnprovenFriendAssemblyProjects = null,
    // Stable per-project causes for failures that have an actionable semantic contract. The
    // existing FailedProjects list remains the compatibility carrier for every failed load.
    IReadOnlyDictionary<string, string>? FailedProjectCauses = null);

/// <summary>Stable one-cause classification shared by semantic telemetry and MCP envelopes.
/// Keeping this in Core prevents each protocol shaper from inventing a different token for the
/// same incomplete compiler scan.</summary>
public static class SemanticCoverageReasons
{
    public static string? FailedProjects(ClusterCoverage? coverage)
        => coverage is { FailedProjects.Count: > 0 } ? "project_load_failed" : null;

    public static string? Primary(
        ClusterCoverage? coverage,
        bool deadlineExhausted = false,
        bool candidateProjectsSkipped = false,
        bool outOfGraphCandidates = false,
        bool projectModelUnproven = false)
    {
        if (deadlineExhausted) return "semantic_timeout";
        if (coverage is { SkippedProjects.Count: > 0 })
            return "unsupported_language_projects_skipped";
        if (candidateProjectsSkipped) return "candidate_cluster_bounded";
        if (FailedProjects(coverage) is { } failedReason) return failedReason;
        if (coverage is not null && coverage.LoadedProjects < coverage.RequestedProjects)
            return "project_coverage_incomplete";
        if (outOfGraphCandidates) return "out_of_graph_candidates";
        return projectModelUnproven ? "project_model_unproven" : null;
    }

    /// <summary>Partiality for a declaration already resolved by the C# compiler. Unsupported
    /// language projects are a scan-census gap, but cannot contain another C# declaration for the
    /// resolved symbol; load failures and incomplete C# coverage can still hide partial parts.</summary>
    public static string? ResolvedDefinition(
        ClusterCoverage? coverage, bool projectModelUnproven = false)
    {
        if (FailedProjects(coverage) is { } failedReason) return failedReason;
        if (coverage is not null && coverage.LoadedProjects < coverage.RequestedProjects &&
            coverage.SkippedProjects.Count == 0)
            return "project_coverage_incomplete";
        return projectModelUnproven ? "project_model_unproven" : null;
    }
}

/// <summary>
/// Owns: an AdhocWorkspace populated lazily with per-project compilations built from
/// parsed csproj facts (no MSBuild evaluation): documents from live files, framework
/// reference assemblies, hint-path/NuGet package dlls, in-cluster project references.
/// LRU-evicts under accounted-input or managed-heap pressure; reloads projects whose files
/// changed (index fingerprint).
/// Does not own: which projects to load (SemanticService decides) or result shaping.
/// </summary>
public sealed partial class SemanticWorkspace : IDisposable
{
    private const long RetainedInputPressureBytes = 2L * 1024 * 1024 * 1024;
    private const long RetainedInputTargetBytes = 1536L * 1024 * 1024;
    private const long ManagedHeapSoftPressureBytes = 2600L * 1024 * 1024;
    private const long ManagedHeapHardPressureBytes = 3L * 1024 * 1024 * 1024;

    private readonly string _workspaceRoot;
    private readonly string _dbPath;
    private readonly Action<string> _log;
    private readonly bool _poolIndexConnections;
    private readonly AdhocWorkspace _workspace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LoadedProject> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private long _useCounter;

    /// <summary>TEST SEAM (epuc.13): reenables the legacy project-count proxy so focused tests can
    /// reproduce owner-phase eviction without constructing hundreds of projects. Never set in
    /// production; shipped retention is governed by accounted input bytes and managed-heap
    /// pressure.</summary>
    internal int? TestOnlyMaxLoadedProjects { get; set; }
    internal long? TestOnlyRetentionInputPressureBytes { get; set; }
    internal long? TestOnlyRetentionInputTargetBytes { get; set; }
    internal Func<long>? TestOnlyManagedHeapBytes { get; set; }

    private sealed class LoadedProject
    {
        public required ProjectId Id { get; init; }
        public required (long Count, long Sum) Fingerprint { get; set; }
        public required string ModelIdentity { get; init; }
        public required bool UnprovenFriendAssemblyAuthority { get; init; }
        public long LastUse { get; set; }
        public ProjectResources? Resources { get; init; }
        public IReadOnlyList<PreparedMetadataCandidate> MetadataCandidates { get; init; } = [];
        public IReadOnlySet<string> PhysicalReferenceNames { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, concurrentBuild: true);

    public SemanticWorkspace(string workspaceRoot, string dbPath, Action<string>? log = null,
        bool poolIndexConnections = true)
    {
        _workspaceRoot = workspaceRoot;
        _dbPath = dbPath;
        _log = log ?? (_ => { });
        _poolIndexConnections = poolIndexConnections;

        // Roslyn's SyntaxTreeIndex persistence is disabled when Solution.FilePath is null.
        // AdhocWorkspace starts with such an anonymous solution, so replace it with a stable
        // storage identity before adding projects. The synthetic path is never read and is not
        // solution/build authority; it only selects Roslyn's local application-data cache.
        _workspace = new AdhocWorkspace();
        string solutionIdentityPath = PersistentSolutionIdentityPath(workspaceRoot);
        _workspace.AddSolution(SolutionInfo.Create(
            StableSolutionId(workspaceRoot),
            VersionStamp.Create(),
            filePath: solutionIdentityPath));
    }

    /// <summary>LoadedBefore is null when the load died QUEUED for the gate — the warm-set
    /// size is guarded by the gate and cannot be read honestly from outside it (review r2).
    /// x5ls.1.3: the four sub-splits dissect ProjectLoadMs into project parsing, source capture,
    /// metadata resolution, and Roslyn workspace mutation. Parallel phase totals may overlap wall
    /// time; phases never entered report 0. The newer plan/preparation/commit fields carry the
    /// wall-clock cold-start path.</summary>
    public sealed record LoadStats(double GateWaitMs, double FingerprintMs, double TopoMs,
        double ProjectLoadMs, int? LoadedBefore, int Requested, int Reloaded, int Loaded,
        int Failed, double ProjectParseMs = 0, double SourceReadMs = 0,
        double MetadataResolveMs = 0, double WorkspaceMutationMs = 0,
        double PlanMs = 0, double PreparationMs = 0, int PreparedProjects = 0,
        int EffectiveProjectConcurrency = 0, long AdmittedBytesHighWater = 0,
        long RetainedBytes = 0, int ReplanCount = 0, double TotalElapsedMs = 0,
        double PreparationQueueMs = 0, int CommittedProjects = 0,
        int? ResidentProjects = null, int EvictedProjects = 0,
        long EvictedInputBytes = 0, string? EvictionReason = null,
        long? ManagedHeapBytes = null);

    /// <summary>epuc.1 (review F2): per-CALL stats vehicle. The first cut published stats via
    /// an ambient last-load property — a concurrent caller could emit ANOTHER op's load as its
    /// own, two-phase ops lost their cold phase-1 split to phase-2's overwrite, and a load
    /// that died mid-flight (the cluster_cold_load case itself) published nothing. The box is
    /// filled in EnsureLoadedAsync's finally, so success, cancellation, and failure all carry
    /// THIS call's split, with the dying moment attributed to the phase that was running.</summary>
    public sealed class LoadStatsBox
    {
        public LoadStats? Stats { get; internal set; }
    }

    /// <summary>
    /// Ensures the given projects (dependency closures must already be included by the
    /// caller for full-fidelity targets) are loaded; returns an operation-scoped solution lease.
    /// Load order is topological (dependencies first); references to projects outside
    /// the requested set are skipped (navigation-grade holes).
    /// </summary>
    public Task<SemanticSolutionLease> EnsureLoadedAsync(
        IReadOnlyCollection<string> projectNames, CancellationToken ct,
        IReadOnlyCollection<string>? ensureReferenceTo = null,
        LoadStatsBox? statsBox = null, bool deferRetentionEviction = false)
        => EnsureLoadedParallelAsync(projectNames, ct, ensureReferenceTo, statsBox,
            deferRetentionEviction);

    /// <summary>Completes a pressure pass deliberately deferred by a multi-phase semantic
    /// operation. Terminal paths call this from finally with no caller deadline: memory safety
    /// must not disappear because resolution, planning, or the scan phase failed. The pass is
    /// diagnostic/cleanup work and therefore never replaces the operation's result or failure.</summary>
    internal async Task CompleteDeferredRetentionAsync()
    {
        bool entered = false;
        try
        {
            await _gate.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
            entered = true;
            _ = EvictForRetention(new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                deferRetentionEviction: false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            // Workspace disposal already releases every resident owner.
        }
        catch (ObjectDisposedException) when (_disposeCts.IsCancellationRequested)
        {
            // Same disposal race, after the gate itself has been torn down.
        }
        catch (Exception ex)
        {
            _log($"Deferred semantic retention completion failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>True when <paramref name="from"/> reaches <paramref name="target"/> over the
    /// solution's recorded ProjectReferences. A dangling id (a project removed for reload) has no
    /// Project node to walk THROUGH, but references pointing AT it are still recorded on its
    /// dependents — which is exactly what makes the reload case detectable: while B is removed,
    /// A's reference to B's reused id keeps A→B visible, so B's reload sees that wiring B→A
    /// would complete A→B→A and skips it.</summary>
    private static bool ReachesId(Solution solution, ProjectId from, ProjectId target)
    {
        if (from == target) return true;
        var seen = new HashSet<ProjectId>();
        var stack = new Stack<ProjectId>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == target) return true;
            if (!seen.Add(cur)) continue;
            var p = solution.GetProject(cur);
            if (p is null) continue; // dangling (removed-for-reload) — no outgoing edges to walk
            // AllProjectReferences, NOT ProjectReferences: the latter is FILTERED to projects
            // currently present in the solution, so during a reload's removal window a dependent's
            // recorded reference to the removed id is invisible — which is precisely the moment
            // this walk must see it (diagnosed via wiring telemetry: the filtered walk let the
            // reload wire the back edge and complete the cycle the guard exists to prevent).
            foreach (var r in p.AllProjectReferences) stack.Push(r.ProjectId);
        }
        return false;
    }

    private static List<string> TopoOrder(List<string> names,
        IReadOnlyCollection<SemanticProjectEdge> edges)
    {
        // Order by dependency depth so referenced projects load before referencing ones.
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<SemanticProjectEdge>> edgesByProject = edges
            .GroupBy(edge => edge.FromProject, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(),
                StringComparer.OrdinalIgnoreCase);

        int Depth(string n, int guard)
        {
            if (guard > 64) return 0;
            if (depth.TryGetValue(n, out int d)) return d;
            depth[n] = 0; // cycle guard
            int max = 0;
            foreach (SemanticProjectEdge e in edgesByProject.GetValueOrDefault(n) ?? [])
            {
                if (e.ToLanguage.Equals("cs", StringComparison.OrdinalIgnoreCase) &&
                    set.Contains(e.ToProject))
                {
                    max = Math.Max(max, Depth(e.ToProject, guard + 1) + 1);
                }
            }
            depth[n] = max;
            return max;
        }

        return names.OrderBy(n => Depth(n, 0)).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private readonly record struct RetentionEviction(
        int ResidentProjects,
        int EvictedProjects,
        long EvictedInputBytes,
        string? Reason,
        long ManagedHeapBytes);

    private static long RetentionTargetBytes(long retainedBytes,
        long inputTargetBytes, string reason) => reason switch
        {
            "pressure_inputs" => inputTargetBytes,
            "pressure_heap_soft" => Math.Min(inputTargetBytes,
                retainedBytes / 4 * 3 + retainedBytes % 4 * 3 / 4),
            "pressure_heap_hard" => Math.Min(inputTargetBytes, retainedBytes / 2),
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

    internal static long TestOnlyRetentionTargetBytes(long retainedBytes,
        long inputTargetBytes, string reason) =>
        RetentionTargetBytes(retainedBytes, inputTargetBytes, reason);

    /// <summary>Evicts only this workspace's strict LRU tail after a complete load phase. The
    /// global input/heap gauges are pressure signals because the physical process budget is global;
    /// in-flight reservations count toward pressure but are not residents and therefore cannot be
    /// evicted here. Requested and referenced projects remain protected exactly as under the legacy
    /// cap. Hysteresis (2 GiB trigger -> 1.5 GiB target) avoids one-project-per-operation churn.</summary>
    private RetentionEviction EvictForRetention(HashSet<string> keep,
        bool deferRetentionEviction)
    {
        long retainedBefore = _coldStartRuntime.Accounting.RetainedBytes;
        long managedHeapBytes = Math.Max(0,
            TestOnlyManagedHeapBytes?.Invoke() ?? GC.GetTotalMemory(false));
        if (deferRetentionEviction || _loaded.Count == 0)
            return new(_loaded.Count, 0, 0, null, managedHeapBytes);

        int? testCap = TestOnlyMaxLoadedProjects;
        string? reason = null;
        long targetBytes = retainedBefore;
        int evictCount = 0;
        if (testCap is not null)
        {
            int cap = Math.Max(1, testCap.Value);
            if (_loaded.Count <= cap)
                return new(_loaded.Count, 0, 0, null, managedHeapBytes);
            reason = "test_project_cap";
            evictCount = _loaded.Count - cap;
        }
        else
        {
            long inputPressure = Math.Max(1,
                TestOnlyRetentionInputPressureBytes ?? RetainedInputPressureBytes);
            long inputTarget = Math.Clamp(
                TestOnlyRetentionInputTargetBytes ?? RetainedInputTargetBytes,
                0, inputPressure - 1);
            bool hardHeapPressure = managedHeapBytes >= ManagedHeapHardPressureBytes;
            bool softHeapPressure = managedHeapBytes >= ManagedHeapSoftPressureBytes;
            bool retainedInputPressure = retainedBefore >= inputPressure;
            if (!hardHeapPressure && !softHeapPressure && !retainedInputPressure)
                return new(_loaded.Count, 0, 0, null, managedHeapBytes);

            if (hardHeapPressure)
            {
                reason = "pressure_heap_hard";
                targetBytes = RetentionTargetBytes(
                    retainedBefore, inputTarget, reason);
            }
            else if (softHeapPressure)
            {
                reason = "pressure_heap_soft";
                targetBytes = RetentionTargetBytes(
                    retainedBefore, inputTarget, reason);
            }
            else
            {
                reason = "pressure_inputs";
                targetBytes = RetentionTargetBytes(
                    retainedBefore, inputTarget, reason);
            }
        }

        // Evict only projects that no REMAINING loaded project references, so eviction never
        // leaves a dangling ProjectReference (Roslyn would silently drop it and corrupt the
        // dependent's symbol visibility). Incoming counts are decremented as safe roots are
        // selected, exposing the next dependency layer during this SAME pressure pass.
        var protectedNames = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        lock (_planningOwnershipSync)
        {
            foreach ((string name, int count) in _activeRequestedProjects)
            {
                if (count > 0) protectedNames.Add(name);
            }
        }

        var byId = _loaded.ToDictionary(pair => pair.Value.Id, pair => pair);
        var outgoing = byId.Keys.ToDictionary(id => id, _ => new List<ProjectId>());
        var incoming = byId.Keys.ToDictionary(id => id, _ => 0);
        foreach (Project project in _workspace.CurrentSolution.Projects)
        {
            if (!outgoing.TryGetValue(project.Id, out List<ProjectId>? references)) continue;
            foreach (ProjectReference reference in project.ProjectReferences)
            {
                if (!incoming.ContainsKey(reference.ProjectId)) continue;
                references.Add(reference.ProjectId);
                incoming[reference.ProjectId]++;
            }
        }

        var ready = new SortedSet<KeyValuePair<string, LoadedProject>>(
            Comparer<KeyValuePair<string, LoadedProject>>.Create((left, right) =>
            {
                int byUse = left.Value.LastUse.CompareTo(right.Value.LastUse);
                if (byUse != 0) return byUse;
                int byName = StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key);
                return byName != 0
                    ? byName
                    : StringComparer.Ordinal.Compare(left.Key, right.Key);
            }));
        foreach (KeyValuePair<string, LoadedProject> pair in _loaded)
        {
            if (!protectedNames.Contains(pair.Key) && incoming[pair.Value.Id] == 0)
                ready.Add(pair);
        }

        var evictable = new List<KeyValuePair<string, LoadedProject>>();
        long estimatedRetained = retainedBefore;
        while (ready.Count > 0)
        {
            if (testCap is not null && evictable.Count >= evictCount) break;
            if (testCap is null && estimatedRetained <= targetBytes) break;
            KeyValuePair<string, LoadedProject> candidate = ready.Min;
            ready.Remove(candidate);
            evictable.Add(candidate);
            estimatedRetained = Math.Max(0,
                estimatedRetained - Math.Max(1,
                    candidate.Value.Resources?.ProjectReservationBytes ?? 0));
            foreach (ProjectId dependency in outgoing[candidate.Value.Id])
            {
                incoming[dependency]--;
                if (incoming[dependency] == 0 &&
                    byId.TryGetValue(dependency,
                        out KeyValuePair<string, LoadedProject> newlySafe) &&
                    !protectedNames.Contains(newlySafe.Key))
                    ready.Add(newlySafe);
            }
        }
        if (evictable.Count == 0)
            return new(_loaded.Count, 0, 0, "no_safe_candidates", managedHeapBytes);
        Solution next = _workspace.CurrentSolution;
        foreach ((_, LoadedProject project) in evictable)
            next = next.RemoveProject(project.Id);
        if (!_workspace.TryApplyChanges(next))
        {
            _log("Semantic cache eviction was rejected by the workspace; resident ownership is unchanged.");
            return new(_loaded.Count, 0, 0, reason, managedHeapBytes);
        }
        long evictedInputBytes = 0;
        foreach (var (name, lp) in evictable)
        {
            _loaded.Remove(name);
            evictedInputBytes = SaturatingAdd(evictedInputBytes,
                lp.Resources?.Release() ?? 0);
        }
        _workspaceGeneration++;
        _log($"Semantic cache evicted {evictable.Count} projects " +
             $"({reason}, {evictedInputBytes} accounted bytes released).");
        return new(_loaded.Count, evictable.Count, evictedInputBytes, reason,
            managedHeapBytes);
    }

    private static string PackagesConfigPath(string projectPath)
    {
        int slash = projectPath.LastIndexOf('/');
        return slash < 0 ? "packages.config" : projectPath[..(slash + 1)] + "packages.config";
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        foreach (LoadedProject project in _loaded.Values)
            project.Resources?.Release();
        _loaded.Clear();
        _workspace.Dispose();
        _gate.Dispose();
        _disposeCts.Dispose();
    }
}
