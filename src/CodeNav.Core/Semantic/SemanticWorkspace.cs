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
/// LRU-evicts beyond a project cap; reloads projects whose files changed (index fingerprint).
/// Does not own: which projects to load (SemanticService decides) or result shaping.
/// </summary>
public sealed partial class SemanticWorkspace : IDisposable
{
    private const int MaxLoadedProjects = 160;

    private readonly string _workspaceRoot;
    private readonly string _dbPath;
    private readonly Action<string> _log;
    private readonly bool _poolIndexConnections;
    private readonly AdhocWorkspace _workspace = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LoadedProject> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private long _useCounter;

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
        double PreparationQueueMs = 0, int CommittedProjects = 0);

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
        LoadStatsBox? statsBox = null)
        => EnsureLoadedParallelAsync(projectNames, ct, ensureReferenceTo, statsBox);

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

    // Memory backstop (njw): the project-count cap is SOFT — referenced projects are never evicted —
    // so heavy clusters can hold compilations well past it with no byte accounting. Past this managed-
    // heap threshold the effective cap halves, so subsequent passes drain harder while preserving the
    // no-dangling-reference invariant. A pressure signal, not a hard ceiling.
    private const long ManagedHeapBackstopBytes = 3L * 1024 * 1024 * 1024;

    private void EvictBeyondCap(HashSet<string> keep)
    {
        int cap = MaxLoadedProjects;
        if (_loaded.Count > 0 && GC.GetTotalMemory(false) > ManagedHeapBackstopBytes)
        {
            cap = Math.Max(8, MaxLoadedProjects / 2);
        }
        if (_loaded.Count <= cap) return;
        if (cap != MaxLoadedProjects)
        {
            // Logged only when the tightened cap actually drives an eviction pass — under sustained
            // heap pressure with nothing over cap this would otherwise spam every semantic call.
            _log($"Semantic cache memory backstop: managed heap over {ManagedHeapBackstopBytes / (1024 * 1024)} MB — tightening cap {MaxLoadedProjects} -> {cap}.");
        }

        // Evict only projects that no currently-loaded project references, so eviction
        // never leaves a dangling ProjectReference (Roslyn would silently drop it and
        // corrupt the dependent's symbol visibility). This drains the graph from the top;
        // if nothing is safely evictable we stay over the soft cap until it is.
        var referenced = new HashSet<ProjectId>();
        foreach (var p in _workspace.CurrentSolution.Projects)
        {
            foreach (var pr in p.ProjectReferences) referenced.Add(pr.ProjectId);
        }

        var evictable = _loaded
            .Where(kv => !keep.Contains(kv.Key) && !referenced.Contains(kv.Value.Id))
            .OrderBy(kv => kv.Value.LastUse)
            .Take(_loaded.Count - cap)
            .ToList();
        if (evictable.Count == 0) return;
        Solution next = _workspace.CurrentSolution;
        foreach ((_, LoadedProject project) in evictable)
            next = next.RemoveProject(project.Id);
        if (!_workspace.TryApplyChanges(next))
        {
            _log("Semantic cache eviction was rejected by the workspace; resident ownership is unchanged.");
            return;
        }
        foreach (var (name, lp) in evictable)
        {
            _loaded.Remove(name);
            lp.Resources?.Release();
        }
        _workspaceGeneration++;
        _log($"Semantic cache evicted {evictable.Count} projects (cap {cap}).");
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
