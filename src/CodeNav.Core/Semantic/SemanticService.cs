using CodeNav.Core.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Core.Semantic;

public sealed record DeclarationSpan(string Path, int StartLine, int EndLine, string Project);

public sealed record SemanticDeclaration(
    string SymbolDisplay,
    string? DocumentationCommentId,
    string Kind,
    string? ContainingType,
    string? Namespace,
    string? Assembly,
    List<DeclarationSpan> Declarations,
    bool IsAbstract = false);

public sealed record SemanticLocation(string Path, int Line, string LineText, string Project, bool IsTestProject,
    string? Kind = null);

public sealed record SemanticRefGroup(string Project, bool IsTestProject, int Count, List<SemanticLocation> Samples);

public sealed record SemanticReferences(
    SemanticDeclaration Symbol,
    int TotalLocations,
    List<SemanticRefGroup> Groups,
    ClusterCoverage Coverage,
    List<string> SkippedCandidateProjects,
    IReadOnlyDictionary<string, int>? KindCounts = null,
    // 24n: the deadline fired MID-COUNT and the counts are a salvaged LOWER BOUND of the scanned
    // portion — previously seconds of completed compiler work were discarded into "semantic_timeout".
    bool DeadlineExhausted = false,
    // kbn: bounded sample of textual candidates outside the dependency closure that were not
    // pulled into the actual Roslyn solution (plugins, config-wired consumers).
    List<string>? OutOfGraphCandidates = null,
    int OutOfGraphCandidateCount = 0,
    bool OutOfGraphCandidatesTruncated = false,
    // t2b: where the deadline budget went — cluster load+resolve vs find+count. The field's
    // cold-cluster confusion ("first call after rebuild times out, second is instant") is
    // answerable from these two numbers without guessing.
    long? ClusterLoadMs = null,
    long? QueryMs = null,
    bool ProjectModelUnproven = false);

/// <summary>One implementation / derived class / override, tagged for hierarchy ranking.
/// <paramref name="Via"/> names the base type that introduces the queried interface when the type
/// implements it indirectly (null = implements it directly, or not applicable to the query).</summary>
public sealed record SemanticImplementation(SemanticDeclaration Declaration, string? Via);

public sealed record SemanticImplementations(
    SemanticDeclaration Symbol,
    List<SemanticImplementation> Implementations,   // concrete (instantiable) leaves first, then abstract scaffolding
    ClusterCoverage Coverage,
    List<string> SkippedCandidateProjects,
    bool DeadlineExhausted = false,                 // 24n: deadline fired mid-search — list is a lower bound
    long? ClusterLoadMs = null,                     // t2b: deadline budget split — load+resolve vs
    long? QueryMs = null,                           // find; see SemanticReferences for rationale
    bool ProjectModelUnproven = false);

/// <summary>
/// Owns: exact (compiler-backed) navigation operations with deadlines — symbol
/// resolution, definitions incl. partials, FindReferences scoped to candidate
/// dependent projects and (when exactness permits) leased-snapshot documents,
/// implementations. Every operation returns null on timeout or
/// resolution failure with a reason, so callers can fall back to the indexed layer.
/// Does not own: cluster loading mechanics (SemanticWorkspace) or MCP shaping.
/// </summary>
public sealed partial class SemanticService : IDisposable
{
    // Large-repository default: zero is the public "all matching projects" sentinel. Phoenix does
    // not silently discard candidate projects; callers that need a latency/memory tradeoff can opt
    // into one by supplying a positive maxProjects value.
    public const int DefaultCandidateProjectBudget = 0;

    internal static int NormalizeCandidateProjectBudget(int maxProjects) =>
        maxProjects == DefaultCandidateProjectBudget ? int.MaxValue : Math.Max(1, maxProjects);

    private readonly IndexManager _manager;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private SemanticWorkspace? _workspace;

    public SemanticService(IndexManager manager, Action<string>? log = null)
    {
        _manager = manager;
        _log = log ?? (_ => { });
    }

    /// <summary>TEST SEAM (tof): invoked once per counted reference location with the running
    /// total. Lets a test throw OperationCanceledException mid-count to exercise the 24n salvage
    /// branch deterministically — a real deadline landing inside the loop is not reproducible on
    /// demand (the seam gap the salvage shipped with). Never set in production; instance-scoped
    /// so parallel tests cannot cross-trip it.</summary>
    internal Action<int>? TestOnlyPerLocationCounted;

    /// <summary>TEST SEAM (t2b): invoked at ReferencesAsync's phase boundaries —
    /// "beforeScanSetLoad" / "afterScanSetLoad" — so a test can burn the deadline in a CHOSEN
    /// phase and pin the cluster_cold_load-vs-semantic_timeout ternary deterministically. A
    /// real cold load's duration is machine-dependent (a warm reference-assembly cache made a
    /// 21-project load finish inside the minimum 500ms clamp on the dev box, silently skipping
    /// the cold branch). Never set in production; instance-scoped.</summary>
    internal Action<string>? TestOnlyPhaseHook;

    /// <summary>TEST SEAM (epuc.3): lowers the normally defensive 2,000-row implementation
    /// closure cap so the semantic fallback and its project-coverage contract can be exercised
    /// with a tiny deterministic graph. Never set in production; instance-scoped.</summary>
    internal int? TestOnlyImplementationClosureMaxTypes;

    /// <summary>TEST SEAM (epuc.6): keeps project loading and compilation preparation identical
    /// while forcing the legacy full-solution SymbolFinder overload. Parity tests compare this
    /// authority path with document narrowing without changing any other semantic input.</summary>
    internal bool TestOnlyForceFullSolutionReferences;

    public bool FrameworkRefsAvailable
    {
        get
        {
            ReferenceAssemblyLocator.Net472References(out string? dir);
            return dir is not null;
        }
    }

    private SemanticWorkspace Workspace
    {
        get
        {
            lock (_gate)
            {
                string databasePath = _manager.DatabaseIoPath; // validates held authority each use
                return _workspace ??= new SemanticWorkspace(_manager.WorkspaceRoot,
                    databasePath, _log, poolIndexConnections: _manager.IsWriter);
            }
        }
    }

    // ---------------------------------------------------------------- epuc.1 telemetry

    private sealed record ScanPlanStats(
        double TotalMs,
        double DependentGraphMs,
        double CandidateDiscoveryMs,
        double DependencyGraphMs,
        double OtherMs,
        int SeedProjects,
        int CandidateProjects,
        int SelectedProjects,
        int ScanProjects,
        int SkippedProjects,
        int OutOfGraphProjects,
        int UnsupportedLanguageProjects);

    private sealed class ScanPlanStatsBox
    {
        public ScanPlanStats? Stats { get; set; }
    }

    private sealed class SemanticPlanningStats
    {
        public ImplementationClosureStatsBox? ImplementationClosure { get; set; }
        public string? SeedDiscoveryMode { get; set; }
        public double? SeedDiscoveryMs { get; set; }
        public int? SeedInputs { get; set; }
        public int? SeedProjects { get; set; }
        public ScanPlanStatsBox ScanSet { get; } = new();
    }

    /// <summary>Privacy-safe attribution for the post-resolution references query. Durations are
    /// operation-local wall time; counters disclose work volume without symbol names, source, or
    /// paths. Mutable so cancellation can publish the stages completed before the deadline.</summary>
    internal sealed class ReferenceQueryStats
    {
        internal SemanticWorkspace.CompilationPreparationStatsBox CompilationPreparation { get; } =
            new();
        internal ReferenceDocumentScopeStatsBox DocumentScope { get; } = new();
        public double FindReferencesMs { get; set; }
        public double PostProcessMs { get; set; }
        public double SyntaxRootLoadMs { get; set; }
        public double ClassificationMs { get; set; }
        public double SampleTextMs { get; set; }
        public int ReferencedSymbols { get; set; }
        public int RawLocations { get; set; }
        public int SourceLocations { get; set; }
        public int UniqueSyntaxTrees { get; set; }
        public int UniqueSites { get; set; }
        public int SamplesRead { get; set; }

        internal object Shape(double queryMs)
        {
            SemanticWorkspace.CompilationPreparationStats? compilationPreparation =
                CompilationPreparation.Stats;
            ReferenceDocumentScopeStats? documentScope = DocumentScope.Stats;
            double postProcessOtherMs = Math.Max(0,
                PostProcessMs - SyntaxRootLoadMs - ClassificationMs - SampleTextMs);
            double otherMs = Math.Max(0, queryMs - FindReferencesMs - PostProcessMs -
                (compilationPreparation?.TotalMs ?? 0) - (documentScope?.TotalMs ?? 0));
            return new
            {
                path = "symbol_finder",
                compilationPreparation = compilationPreparation is null ? null : new
                {
                    totalMs = Math.Round(compilationPreparation.TotalMs, 1),
                    queueMs = Math.Round(compilationPreparation.QueueMs, 1),
                    busySumMs = Math.Round(compilationPreparation.BusySumMs, 1),
                    maxProjectBusyMs = Math.Round(compilationPreparation.MaxProjectBusyMs, 1),
                    waveMaxSumMs = Math.Round(compilationPreparation.WaveMaxSumMs, 1),
                    criticalPathMs = Math.Round(compilationPreparation.CriticalPathMs, 1),
                    requestedProjects = compilationPreparation.RequestedProjects,
                    graphProjects = compilationPreparation.GraphProjects,
                    cacheHits = compilationPreparation.CacheHits,
                    preparedProjects = compilationPreparation.PreparedProjects,
                    failedProjects = compilationPreparation.FailedProjects,
                    skippedProjects = compilationPreparation.SkippedProjects,
                    unfinishedProjects = compilationPreparation.UnfinishedProjects,
                    waves = compilationPreparation.Waves,
                    laneLimit = compilationPreparation.LaneLimit,
                    effectiveConcurrency = compilationPreparation.EffectiveConcurrency,
                },
                documentScope = documentScope is null ? null : new
                {
                    mode = documentScope.Mode,
                    reason = documentScope.Reason,
                    candidateSource = documentScope.CandidateSource,
                    totalMs = Math.Round(documentScope.TotalMs, 1),
                    cacheHit = documentScope.CacheHit,
                    solutionDocuments = documentScope.SolutionDocuments,
                    candidateDocuments = documentScope.CandidateDocuments,
                    scopedDocuments = documentScope.ScopedDocuments,
                    scopedProjects = documentScope.ScopedProjects,
                    documentsInScopedProjects = documentScope.DocumentsInScopedProjects,
                    aliasWidenedProjects = documentScope.AliasWidenedProjects,
                    transformedIncludedDocuments = documentScope.TransformedIncludedDocuments,
                },
                findReferencesMs = Math.Round(FindReferencesMs, 1),
                postProcessMs = Math.Round(PostProcessMs, 1),
                syntaxRootLoadMs = Math.Round(SyntaxRootLoadMs, 1),
                classificationMs = Math.Round(ClassificationMs, 1),
                sampleTextMs = Math.Round(SampleTextMs, 1),
                postProcessOtherMs = Math.Round(postProcessOtherMs, 1),
                otherMs = Math.Round(otherMs, 1),
                referencedSymbols = ReferencedSymbols,
                rawLocations = RawLocations,
                sourceLocations = SourceLocations,
                uniqueSyntaxTrees = UniqueSyntaxTrees,
                uniqueSites = UniqueSites,
                samplesRead = SamplesRead,
            };
        }
    }

    /// <summary>Emits one bounded telemetry record for a semantic operation — tool,
    /// outcome/reason, and the per-CALL stage stats of the loads this op itself ran (review F2:
    /// an earlier cut read an ambient last-load property, which let concurrent ops steal each
    /// other's stats, buried phase-1's cold split under phase-2's overwrite, and lost the split
    /// entirely when a load died mid-flight — the cluster_cold_load case telemetry exists for).
    /// ownerLoad = the owning-project closure load (phase 1); scanLoad = the scan-set load
    /// (phase 2, scan ops only). cold is derived from phase 1: nothing was loaded before it.
    /// Privacy per docs/internal-operations-portal.md: no symbol names, no arguments, no paths.
    /// TelemetryLog.Emit never blocks or throws into this path.</summary>
    private void EmitOpTelemetry(string tool, string result, string? reason,
        SemanticWorkspace.LoadStats? ownerLoad = null,
        SemanticWorkspace.LoadStats? scanLoad = null,
        long? clusterLoadMs = null, long? queryMs = null,
        object? queryStages = null,
        SemanticPlanningStats? planning = null)
    {
        _manager.Telemetry.Emit(new
        {
            e = "semanticOp",
            // InvariantCulture: `:` in a custom format is the CULTURE time separator — on
            // locales like fi-FI it renders `.`, breaking ISO-8601 (x5ls.1 review F6).
            ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                System.Globalization.CultureInfo.InvariantCulture),
            corr = Guid.NewGuid().ToString("N")[..8],
            tool,
            accessMode = _manager.IsWriter
                ? "writer"
                : _manager.IsFollower ? "follower" : "unattached",
            result,
            reason,
            // Field regression fix (IPartnerFrameworkInterface, 48s query invisible): the F2
            // redesign dropped the op's own load/query wall split — the response envelope kept
            // it, the record lost it, and query became the dominant cost with zero telemetry.
            clusterLoadMs,
            queryMs,
            // jj1q: the closure-verified find path reports its own stage split — the field's
            // "break down the 43.7s inside queryMs" ask, answered by owning the stages.
            queryStages,
            // epuc.3: planning is intentionally separate from ownerLoad/scanLoad. It runs before
            // EnsureLoadedAsync and was previously folded into clusterLoadMs, making a 5.5s
            // SQLite-backed closure walk look like Roslyn project loading.
            planning = ShapePlanning(planning),
            cold = ownerLoad is { LoadedBefore: 0 } ? true : (bool?)null,
            ownerLoad = Shape(ownerLoad),
            scanLoad = Shape(scanLoad),
        });

        static object? Shape(SemanticWorkspace.LoadStats? load) => load is null ? null : new
        {
            gateWaitMs = Math.Round(load.GateWaitMs, 1),
            fingerprintMs = Math.Round(load.FingerprintMs, 1),
            topoMs = Math.Round(load.TopoMs, 1),
            projectLoadMs = Math.Round(load.ProjectLoadMs, 1),
            // x5ls.1.3: the sub-splits of projectLoadMs. The residue (lump minus the four)
            // is NOT noise: per-project index SQL (ProjectByName/ProjectFiles/fingerprint),
            // ProjectInfo.Create, ProjectReference wiring, and cap evictions (review s1).
            // These answer wusi-vs-xqxw with field data.
            projectParseMs = Math.Round(load.ProjectParseMs, 1),
            sourceReadMs = Math.Round(load.SourceReadMs, 1),
            metadataResolveMs = Math.Round(load.MetadataResolveMs, 1),
            workspaceMutationMs = Math.Round(load.WorkspaceMutationMs, 1),
            planMs = Math.Round(load.PlanMs, 1),
            preparationMs = Math.Round(load.PreparationMs, 1),
            preparationQueueMs = Math.Round(load.PreparationQueueMs, 1),
            preparedProjects = load.PreparedProjects,
            committedProjects = load.CommittedProjects,
            effectiveProjectConcurrency = load.EffectiveProjectConcurrency,
            admittedBytesHighWater = load.AdmittedBytesHighWater,
            retainedBytes = load.RetainedBytes,
            replanCount = load.ReplanCount,
            totalElapsedMs = Math.Round(load.TotalElapsedMs, 1),
            loadedBefore = load.LoadedBefore,
            requested = load.Requested,
            reloaded = load.Reloaded,
            loaded = load.Loaded,
            failed = load.Failed,
        };
    }

    private static object? ShapePlanning(SemanticPlanningStats? planning)
    {
        ImplementationClosureStats? closure = planning?.ImplementationClosure?.Stats;
        ScanPlanStats? scan = planning?.ScanSet.Stats;
        bool hasSeed = planning?.SeedDiscoveryMs is not null;
        if (closure is null && scan is null && !hasSeed) return null;

        return new
        {
            implementationClosure = closure is null ? null : new
            {
                totalMs = Math.Round(closure.TotalMs, 1),
                dbQueryAndMapMs = Math.Round(closure.DbQueryAndMapMs, 1),
                managedFilterMs = Math.Round(closure.ManagedFilterMs, 1),
                otherMs = Math.Round(closure.OtherMs, 1),
                dbQueries = closure.DbQueries,
                rowsReturned = closure.RowsReturned,
                frontierExpansions = closure.FrontierExpansions,
                matches = closure.Matches,
                capped = closure.Capped,
            },
            seedDiscovery = !hasSeed ? null : new
            {
                mode = planning!.SeedDiscoveryMode,
                totalMs = Math.Round(planning.SeedDiscoveryMs!.Value, 1),
                inputs = planning.SeedInputs,
                projects = planning.SeedProjects,
            },
            scanSet = scan is null ? null : new
            {
                totalMs = Math.Round(scan.TotalMs, 1),
                dependentGraphMs = Math.Round(scan.DependentGraphMs, 1),
                candidateDiscoveryMs = Math.Round(scan.CandidateDiscoveryMs, 1),
                dependencyGraphMs = Math.Round(scan.DependencyGraphMs, 1),
                otherMs = Math.Round(scan.OtherMs, 1),
                seedProjects = scan.SeedProjects,
                candidateProjects = scan.CandidateProjects,
                selectedProjects = scan.SelectedProjects,
                scanProjects = scan.ScanProjects,
                skippedProjects = scan.SkippedProjects,
                outOfGraphProjects = scan.OutOfGraphProjects,
                unsupportedLanguageProjects = scan.UnsupportedLanguageProjects,
            },
        };
    }

    // ---------------------------------------------------------------- definition

    public async Task<(SemanticDeclaration? Result, string? FailReason,
        bool ProjectModelUnproven, string? PartialReason)> DefinitionAsync(
        string path, int line, int? column, string? nameHint, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 60000));
        bool loadCompleted = false;
        var swOp = System.Diagnostics.Stopwatch.StartNew(); // field 48s gap: op wall split
        long loadMs = 0;
        var ownerBox = new SemanticWorkspace.LoadStatsBox(); // epuc.1
        try
        {
            using var indexSnapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (indexSnapshot is null)
            {
                EmitOpTelemetry("definition", "unresolved", "index_snapshot_unavailable"); // epuc.1
                return (null, "index_snapshot_unavailable", false, null);
            }
            var (ownerLease, symbol, owningProject, coverage) = await LoadOwnerAndResolveAsync(
                path, line, column, nameHint, cts.Token, indexSnapshot.Queries,
                statsBox: ownerBox).ConfigureAwait(false);
            using var ownerOperation = ownerLease;
            loadCompleted = true;
            loadMs = swOp.ElapsedMilliseconds;
            if (symbol is null)
            {
                string reason = SemanticCoverageReasons.FailedProjects(coverage)
                    ?? "symbol_not_resolved";
                EmitOpTelemetry("definition", "unresolved", reason, ownerBox.Stats); // epuc.1
                return (null, reason, false, null);
            }
            // Review r2: materialize BEFORE the emit — a Describe throw after an "exact"
            // record would add a second ("error") record for the same op via the catch.
            var described = Describe(symbol);
            bool projectModelUnproven = FriendAssemblyAuthorityUnproven(symbol,
                owningProject is null ? [] : [owningProject], coverage);
            string? partialReason = SemanticCoverageReasons.ResolvedDefinition(coverage,
                projectModelUnproven);
            EmitOpTelemetry("definition", partialReason is not null ? "partial" : "exact",
                partialReason, ownerBox.Stats,
                clusterLoadMs: loadMs, queryMs: swOp.ElapsedMilliseconds - loadMs); // epuc.1
            return (described, null, projectModelUnproven, partialReason);
        }
        catch (OperationCanceledException)
        {
            // t2b: a deadline that dies during cluster LOAD is not a scan timeout — it is the
            // first-call-after-(re)build warm-up, and an immediate retry usually succeeds. The
            // old uniform "semantic_timeout" sent agents raising timeoutMs (or distrusting the
            // tool) when the fix was simply to call again.
            string reason = loadCompleted ? "semantic_timeout" : "cluster_cold_load";
            EmitOpTelemetry("definition", "degraded", reason, ownerBox.Stats,
                clusterLoadMs: loadCompleted ? loadMs : swOp.ElapsedMilliseconds,
                queryMs: loadCompleted ? swOp.ElapsedMilliseconds - loadMs : null); // epuc.1
            return (null, reason, false, null);
        }
        catch (Exception ex)
        {
            _log($"Semantic definition failed: {ex.Message}");
            // In-load error carries the whole wall as load (review q2-r2: shape parity
            // with the four two-phase ops — the running phase absorbs the wall).
            EmitOpTelemetry("definition", "error", ex.GetType().Name, ownerBox.Stats,
                clusterLoadMs: loadCompleted ? loadMs : swOp.ElapsedMilliseconds,
                queryMs: loadCompleted ? swOp.ElapsedMilliseconds - loadMs : null); // epuc.1 review F3
            return (null, $"semantic_error:{ex.GetType().Name}", false, null);
        }
    }

    // ---------------------------------------------------------------- references

    public async Task<(SemanticReferences? Result, string? FailReason)> ReferencesAsync(
        string path, int line, int? column, string? nameHint, int maxProjects, int samplesPerGroup, int timeoutMs,
        bool includeGenerated = true, IReadOnlySet<string>? usageKinds = null, bool publicConsumersOnly = false,
        bool includeTests = true)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        bool clusterLoadInProgress = true;
        var swPhase = System.Diagnostics.Stopwatch.StartNew();
        long clusterLoadMs = 0;
        var ownerBox = new SemanticWorkspace.LoadStatsBox(); // epuc.1
        var scanBox = new SemanticWorkspace.LoadStatsBox();
        var planning = new SemanticPlanningStats();
        var queryStages = new ReferenceQueryStats();
        try
        {
            using var indexSnapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (indexSnapshot is null)
            {
                EmitOpTelemetry("references", "unresolved", "index_snapshot_unavailable"); // epuc.1
                return (null, "index_snapshot_unavailable");
            }

            // Phase 1: load the owner closure and resolve, to learn the symbol name.
            var (ownerLease, symbolA, owningProject, ownerCoverage) = await LoadOwnerAndResolveAsync(
                path, line, column, nameHint, cts.Token, indexSnapshot.Queries,
                statsBox: ownerBox).ConfigureAwait(false);
            using var ownerOperation = ownerLease;
            clusterLoadInProgress = false; // candidate discovery is a query phase, not cold loading
            // Review q2 (progressive stamp): a deadline in the DISCOVERY window otherwise
            // reports clusterLoadMs 0 / queryMs = whole wall — the exact inverse of the truth
            // when phase-1 burned the budget. The post-phase-2 stamp overwrites cumulatively.
            clusterLoadMs = swPhase.ElapsedMilliseconds;
            if (symbolA is null || owningProject is null)
            {
                string reason = SemanticCoverageReasons.FailedProjects(ownerCoverage)
                    ?? "symbol_not_resolved";
                EmitOpTelemetry("references", "unresolved", reason, ownerBox.Stats,
                    planning: planning); // epuc.1 + epuc.4
                return (null, reason);
            }

            // Implementer seeds for TYPE targets — parity with Implementations/TypeHierarchy
            // (field 0.7.2 regression report): references was the ONLY exact tool relying purely
            // on graph edges for candidate discovery, so any edge gap (e.g. the paired-declarer
            // collision) made it load 1/1 and return an "exact" zero while the seeded tools found
            // all 8. Base-list implementer projects carry usages BY DEFINITION; seed them first.
            List<string>? implementerSeeds = null;
            if (symbolA is INamedTypeSymbol)
            {
                planning.SeedDiscoveryMode = "directCandidates";
                var swSeeds = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    implementerSeeds = indexSnapshot.Queries
                        .ImplementationCandidateProjects(symbolA.Name, cts.Token,
                            includeGenerated, includeTests);
                }
                finally
                {
                    planning.SeedDiscoveryMs = swSeeds.Elapsed.TotalMilliseconds;
                }
                planning.SeedProjects = implementerSeeds.Count;
            }

            // Phase 2: load the dependent scan set and re-resolve IN that snapshot, then
            // resolve + search against the SAME solution (no snapshot drift).
            clusterLoadInProgress = true;
            TestOnlyPhaseHook?.Invoke("beforeScanSetLoad");
            var (scanLease, symbol, coverage, skipped, outOfGraph) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects,
                indexSnapshot.Queries, cts.Token, implementerSeeds,
                statsBox: scanBox, includeGenerated: includeGenerated,
                includeTests: includeTests,
                planStatsBox: planning.ScanSet).ConfigureAwait(false);
            using var scanOperation = scanLease;
            Solution solution = scanLease.Solution;
            clusterLoadInProgress = false;
            clusterLoadMs = swPhase.ElapsedMilliseconds; // load+resolve budget; the rest is find+count
            TestOnlyPhaseHook?.Invoke("afterScanSetLoad");
            if (symbol is null)
            {
                string reason = SemanticCoverageReasons.FailedProjects(coverage)
                    ?? "symbol_not_resolved_in_scope";
                EmitOpTelemetry("references", "unresolved", reason,
                    ownerBox.Stats, scanBox.Stats, planning: planning); // epuc.1 + epuc.4
                return (null, reason);
            }

            // epuc.5: materialize this operation's selected project compilations in dependency-first
            // waves before SymbolFinder. The exact leased Solution is reused below, so Roslyn's
            // CompilationTracker turns this into parallel preparation rather than duplicate work.
            await Workspace.PrepareCompilationsAsync(scanLease, owningProject,
                queryStages.CompilationPreparation, cts.Token).ConfigureAwait(false);

            ReferenceDocumentScope documentScope = await PlanReferenceDocumentScopeAsync(
                symbol, solution, queryStages.DocumentScope, cts.Token).ConfigureAwait(false);

            IEnumerable<ReferencedSymbol> found;
            long findStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                found = documentScope.Documents is null
                    ? await SymbolFinder.FindReferencesAsync(symbol, solution, cts.Token)
                        .ConfigureAwait(false)
                    : await SymbolFinder.FindReferencesAsync(symbol, solution,
                        documentScope.Documents, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                queryStages.FindReferencesMs += System.Diagnostics.Stopwatch
                    .GetElapsedTime(findStarted).TotalMilliseconds;
            }

            long postProcessStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var testFlags = ProjectTestFlags();
            // When excluding generated code, drop reference locations in generated files from BOTH the
            // counts and the samples (bug wi3: the semantic path previously ignored includeGenerated).
            HashSet<string>? generatedPaths = null;
            if (!includeGenerated)
            {
                using var gq = _manager.OpenQueries();
                generatedPaths = gq.GeneratedPaths();
            }
            var groups = new Dictionary<string, SemanticRefGroup>(StringComparer.OrdinalIgnoreCase);
            var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var rootCache = new Dictionary<SyntaxTree, SyntaxNode>(); // one parse-root fetch per tree
            bool symbolIsType = symbol is INamedTypeSymbol;
            // publicConsumersOnly anchors on the symbol's DECLARING project (assemblyName == index
            // project name in the adhoc workspace). Anchoring on the query-position file INVERTS the
            // filter when the caller targets a USAGE position (review-reproduced: the declaring
            // project was kept and the usage's project dropped).
            string declaringProject = symbol.ContainingAssembly?.Name ?? owningProject ?? "";
            int total = 0;
            // Physical-site dedupe (field P1, 0.7.0): twin-declaration types can surface the SAME
            // usage site through more than one ReferencedSymbol entry — the canary returned
            // totalReferences 13 for 8 implementers with Interface.cs:11 twice INSIDE one group.
            // One (project, path, line, kind) site counts once: the semantic mirror of 0ok's
            // physical indexed totals. Keyed WITH the project so a file genuinely linked into two
            // projects keeps its per-project attribution (the documented blast-radius property).
            var seenSites = new HashSet<(string Project, string Path, int Line, string Kind)>();
            bool deadlineExhausted = false;
            // Salvage wrapper (24n): if the deadline fires INSIDE the counting loop, keep what was
            // counted as a lower bound instead of discarding completed compiler work into a bare
            // "semantic_timeout". A deadline during resolve/FindReferences still falls through to
            // the outer catch — there is genuinely nothing to salvage there.
            try
            {
                foreach (var referenced in found)
                {
                    queryStages.ReferencedSymbols++;
                    foreach (var loc in referenced.Locations)
                    {
                        queryStages.RawLocations++;
                        if (loc.Location.SourceTree is null) continue;
                        queryStages.SourceLocations++;
                        var doc = loc.Document;
                        string project = doc.Project.Name;
                        var lineSpan = loc.Location.GetLineSpan();
                        int refLine = lineSpan.StartLinePosition.Line + 1;
                        string relPath = ToRelPath(doc.FilePath ?? doc.Name);
                        if (generatedPaths is not null && generatedPaths.Contains(relPath)) continue;
                        // includeTests filters BEFORE counting (wu1) — same discipline as
                        // includeGenerated/usageKinds, so TotalLocations, KindCounts, and the group
                        // list all describe the same filtered set. Previously the tool dropped test
                        // GROUPS after the fact while summary/kinds still counted their locations.
                        bool isTest = testFlags.TryGetValue(project, out bool t) && t;
                        if (!includeTests && isTest) continue;
                        // publicConsumersOnly: API blast-radius view — drop usages from the DECLARING
                        // project itself, before counting, so totals reflect external consumers only.
                        if (publicConsumersOnly && string.Equals(project, declaringProject, StringComparison.OrdinalIgnoreCase)) continue;

                        // Classify HOW the symbol is used (call vs xmldoc mention vs ...); filter before
                        // counting so totals honor usageKinds (same discipline as includeGenerated).
                        if (!rootCache.TryGetValue(loc.Location.SourceTree, out var rootNode))
                        {
                            long rootStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                            try
                            {
                                rootNode = await loc.Location.SourceTree.GetRootAsync(cts.Token)
                                    .ConfigureAwait(false);
                            }
                            finally
                            {
                                queryStages.SyntaxRootLoadMs += System.Diagnostics.Stopwatch
                                    .GetElapsedTime(rootStarted).TotalMilliseconds;
                            }
                            rootCache[loc.Location.SourceTree] = rootNode;
                        }
                        string kind;
                        long classifyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        try
                        {
                            kind = SemanticReferenceKinds.Classify(rootNode,
                                loc.Location.SourceSpan.Start, symbolIsType);
                        }
                        finally
                        {
                            queryStages.ClassificationMs += System.Diagnostics.Stopwatch
                                .GetElapsedTime(classifyStarted).TotalMilliseconds;
                        }
                        if (usageKinds is not null && !usageKinds.Contains(kind)) continue;
                        if (!seenSites.Add((project, relPath, refLine, kind))) continue; // field P1: idempotent site counting

                        // ALL bookkeeping commits before the awaitable sample fetch (review, 24n): with
                        // the group commit trailing GetTextAsync, a deadline OCE on that await salvaged
                        // a response whose total/kinds included the location but whose groups did not —
                        // worst case "at least 1 exact references across 0 projects". The with-copy
                        // shares the Samples List instance, so samples added below still land in the
                        // stored group; a sample lost to the OCE costs a sample line, never a count.
                        total++;
                        kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
                        if (!groups.TryGetValue(project, out var g))
                        {
                            g = new SemanticRefGroup(project, isTest, 0, new List<SemanticLocation>());
                        }
                        groups[project] = g with { Count = g.Count + 1 };
                        // Placed AFTER the bookkeeping commit, matching where a real deadline OCE is
                        // survivable (the awaitable sample fetch below) — counted state is consistent.
                        TestOnlyPerLocationCounted?.Invoke(total);
                        var samples = g.Samples;
                        if (samples.Count < samplesPerGroup)
                        {
                            long sampleStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                            try
                            {
                                string text = (await loc.Location.SourceTree.GetTextAsync(cts.Token)
                                        .ConfigureAwait(false))
                                    .Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                                samples.Add(new SemanticLocation(relPath, refLine, Truncate(text), project, isTest, kind));
                                queryStages.SamplesRead++;
                            }
                            finally
                            {
                                queryStages.SampleTextMs += System.Diagnostics.Stopwatch
                                    .GetElapsedTime(sampleStarted).TotalMilliseconds;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (total > 0)
            {
                // counted-so-far survives as a lower bound (24n). The when-guard keeps a ZERO
                // salvage falling through to semantic_timeout — "0 exact references" at exact
                // confidence would read as dead code when the deadline simply beat the count.
                deadlineExhausted = true;
            }
            queryStages.PostProcessMs += System.Diagnostics.Stopwatch
                .GetElapsedTime(postProcessStarted).TotalMilliseconds;
            queryStages.UniqueSyntaxTrees = rootCache.Count;
            queryStages.UniqueSites = seenSites.Count;

            bool projectModelUnproven = FriendAssemblyAuthorityUnproven(symbol,
                groups.Keys, coverage);
            int outOfGraphCandidateCount = outOfGraph.Count;
            List<string>? outOfGraphSample = outOfGraphCandidateCount > 0
                ? outOfGraph.Take(20).ToList()
                : null;
            var result = new SemanticReferences(
                Describe(symbol),
                total,
                groups.Values.OrderByDescending(g => g.Count).ToList(),
                coverage,
                skipped,
                kindCounts,
                deadlineExhausted,
                outOfGraphSample,
                OutOfGraphCandidateCount: outOfGraphCandidateCount,
                OutOfGraphCandidatesTruncated: outOfGraphCandidateCount >
                    (outOfGraphSample?.Count ?? 0),
                ClusterLoadMs: clusterLoadMs,
                QueryMs: swPhase.ElapsedMilliseconds - clusterLoadMs,
                ProjectModelUnproven: projectModelUnproven);
            bool unsupportedLanguageSkipped = coverage.SkippedProjects.Count > 0;
            bool candidateProjectsSkipped = skipped.Count > 0;
            bool failedLoads = coverage.FailedProjects.Count > 0;
            bool coverageIncomplete = coverage.LoadedProjects < coverage.RequestedProjects;
            bool outOfGraphCandidates = outOfGraph.Count > 0;
            bool incomplete = deadlineExhausted || unsupportedLanguageSkipped ||
                candidateProjectsSkipped || failedLoads || coverageIncomplete ||
                outOfGraphCandidates || projectModelUnproven;
            string? telemetryReason = SemanticCoverageReasons.Primary(coverage,
                deadlineExhausted, candidateProjectsSkipped, outOfGraphCandidates,
                projectModelUnproven);
            long telemetryQueryMs = swPhase.ElapsedMilliseconds - clusterLoadMs;
            EmitOpTelemetry("references", incomplete ? "partial" : "exact",
                telemetryReason,
                ownerBox.Stats, scanBox.Stats,
                clusterLoadMs, telemetryQueryMs,
                queryStages.Shape(telemetryQueryMs), planning); // epuc.1 + epuc.4
            return (result, null);
        }
        catch (OperationCanceledException)
        {
            // t2b: cold-cluster warm-up vs real scan timeout — see DefinitionAsync for rationale.
            string reason = clusterLoadInProgress ? "cluster_cold_load" : "semantic_timeout";
            // Field 48s gap: a timed-out op is exactly the record that NEEDS the query wall.
            EmitOpTelemetry("references", "degraded", reason, ownerBox.Stats, scanBox.Stats,
                clusterLoadInProgress ? swPhase.ElapsedMilliseconds : clusterLoadMs,
                clusterLoadInProgress ? null : swPhase.ElapsedMilliseconds - clusterLoadMs,
                queryStages: clusterLoadInProgress ? null : queryStages.Shape(
                    swPhase.ElapsedMilliseconds - clusterLoadMs),
                planning: planning); // epuc.1 + epuc.4
            return (null, reason);
        }
        catch (Exception ex)
        {
            _log($"Semantic references failed: {ex}");
            EmitOpTelemetry("references", "error", ex.GetType().Name, ownerBox.Stats, scanBox.Stats,
                clusterLoadInProgress ? swPhase.ElapsedMilliseconds : clusterLoadMs,
                clusterLoadInProgress ? null : swPhase.ElapsedMilliseconds - clusterLoadMs,
                queryStages: clusterLoadInProgress ? null : queryStages.Shape(
                    swPhase.ElapsedMilliseconds - clusterLoadMs),
                planning: planning); // epuc.1 + epuc.4
            return (null, $"semantic_error:{ex.GetType().Name}");
        }
    }

    // ---------------------------------------------------------------- implementations

    public async Task<(SemanticImplementations? Result, string? FailReason)> ImplementationsAsync(
        string path, int line, int? column, string? nameHint, int maxProjects, int timeoutMs,
        int? arityHint = null)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        bool clusterLoadInProgress = true;
        var swPhase = System.Diagnostics.Stopwatch.StartNew();
        long clusterLoadMs = 0;
        var ownerBox = new SemanticWorkspace.LoadStatsBox(); // epuc.1
        var scanBox = new SemanticWorkspace.LoadStatsBox();
        var planning = new SemanticPlanningStats(); // epuc.3
        try
        {
            using var indexSnapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (indexSnapshot is null)
            {
                EmitOpTelemetry("implementations", "unresolved", "index_snapshot_unavailable"); // epuc.1
                return (null, "index_snapshot_unavailable");
            }

            var (ownerLease, symbolA, owningProject, ownerCoverage) = await LoadOwnerAndResolveAsync(
                path, line, column, nameHint, cts.Token, indexSnapshot.Queries, arityHint,
                statsBox: ownerBox)
                .ConfigureAwait(false);
            using var ownerOperation = ownerLease;
            clusterLoadInProgress = false;
            clusterLoadMs = swPhase.ElapsedMilliseconds; // review q2: progressive stamp (see references)
            if (symbolA is null || owningProject is null)
            {
                string reason = SemanticCoverageReasons.FailedProjects(ownerCoverage)
                    ?? "symbol_not_resolved";
                EmitOpTelemetry("implementations", "unresolved", reason, ownerBox.Stats); // epuc.1
                return (null, reason);
            }

            // Seed the scan-set with the projects that syntactically implement/derive the type.
            // jj1q upgraded this from level-1 base-list matches to the TRANSITIVE closure's
            // projects — the deep-chain fixture proved level-1 seeding was a correctness hole:
            // a leaf implementer four hops down never textually mentions the target, so its
            // project was never loaded and even the exhaustive walk could not see it.
            List<string> implementerSeeds;
            IReadOnlyList<SymbolHit>? typeClosure = null;
            bool closureCapped = false;
            long closureMs = 0;
            if (symbolA is INamedTypeSymbol typeA)
            {
                planning.ImplementationClosure = new ImplementationClosureStatsBox();
                var swClosure = System.Diagnostics.Stopwatch.StartNew();
                typeClosure = indexSnapshot.Queries.TransitiveImplementationClosure(
                    typeA.Name, typeA.Arity, out closureCapped,
                    maxTypes: Math.Max(1, TestOnlyImplementationClosureMaxTypes ?? 2000),
                    cancellationToken: cts.Token,
                    statsBox: planning.ImplementationClosure);
                closureMs = swClosure.ElapsedMilliseconds;
                planning.SeedDiscoveryMode = closureCapped
                    ? "dependentClosure"
                    : "closureOwners";
                planning.SeedInputs = closureCapped ? null : typeClosure.Count;
                var swSeeds = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    implementerSeeds = closureCapped
                        ? CompleteDependentSeeds(indexSnapshot.Queries, owningProject)
                        : ClosureSeedProjects(indexSnapshot.Queries, typeClosure);
                }
                finally
                {
                    planning.SeedDiscoveryMs = swSeeds.Elapsed.TotalMilliseconds;
                }
            }
            else
            {
                planning.SeedDiscoveryMode = "directCandidates";
                var swSeeds = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    implementerSeeds = indexSnapshot.Queries
                        .ImplementationCandidateProjects(symbolA.Name, cts.Token);
                }
                finally
                {
                    planning.SeedDiscoveryMs = swSeeds.Elapsed.TotalMilliseconds;
                }
            }
            planning.SeedProjects = implementerSeeds.Count;

            clusterLoadInProgress = true;
            var (scanLease, symbol, coverage, skipped, _) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects,
                indexSnapshot.Queries, cts.Token, implementerSeeds, arityHint,
                statsBox: scanBox, planStatsBox: planning.ScanSet).ConfigureAwait(false);
            using var scanOperation = scanLease;
            Solution solution = scanLease.Solution;
            clusterLoadInProgress = false;
            clusterLoadMs = swPhase.ElapsedMilliseconds; // load+resolve budget; the rest is find
            if (symbol is null)
            {
                string reason = SemanticCoverageReasons.FailedProjects(coverage)
                    ?? "symbol_not_resolved_in_scope";
                EmitOpTelemetry("implementations", "unresolved", reason,
                    ownerBox.Stats, scanBox.Stats, planning: planning); // epuc.1 + epuc.3
                return (null, reason);
            }

            var results = new List<SemanticImplementation>();
            bool deadlineExhausted = false;
            object? queryStages = null;
            // Salvage wrapper (24n): a deadline firing after SOME finder pass completed (e.g. the
            // member path's implementations found, overrides not yet) keeps the found portion as a
            // lower bound. A deadline before anything was found still exits via the outer catch.
            try
            {
                if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Class } targetType)
                {
                    // jj1q: closure-verified path — the index walked the transitive subtype
                    // closure at the seeds point (any chain depth), its projects were LOADED
                    // as seeds, and semantics now verifies each candidate. Only candidate
                    // projects materialize compilations (field two-trace: the global walk cost
                    // 43.9s WARM across 90 compilations for 8 results).
                    if (typeClosure is not null && !closureCapped)
                    {
                        var verification = await VerifyClosureImplementersAsync(
                            targetType, solution, typeClosure,
                            impl => results.Add(new SemanticImplementation(
                                Describe(impl), DerivationVia(impl, targetType))),
                            cts.Token).ConfigureAwait(false);
                        queryStages = new
                        {
                            path = "closure_verified",
                            closureMs,
                            verifyMs = verification.VerifyMs,
                            candidates = typeClosure.Count,
                            verified = verification.Verified,
                            documentCandidates = verification.DocumentCandidates,
                            declarationsMatched = verification.DeclarationsMatched,
                            symbolsResolved = verification.SymbolsResolved,
                            directBaseLinks = verification.DirectBaseLinks,
                            errorDirectBaseLinks = verification.ErrorDirectBaseLinks,
                            targetDirectMatches = verification.TargetDirectMatches,
                        };
                    }
                    else
                    {
                        // Pathological fan-out (or a non-type resolution oddity): the
                        // exhaustive compiler walk — slower, never truncated. A capped
                        // closure must not ship as an exact answer.
                        if (targetType.TypeKind == TypeKind.Interface)
                        {
                            var impls = await SymbolFinder.FindImplementationsAsync(targetType, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                            foreach (var impl in impls)
                                results.Add(new SemanticImplementation(Describe(impl), DerivationVia(impl, targetType)));
                        }
                        else
                        {
                            var derived = await SymbolFinder.FindDerivedClassesAsync(targetType, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                            foreach (var d in derived)
                                results.Add(new SemanticImplementation(Describe(d), DerivationVia(d, targetType)));
                        }
                        queryStages = new { path = "exhaustive_fallback", closureCapped };
                    }
                }
                else
                {
                    var impls = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                    results.AddRange(impls.Select(s => new SemanticImplementation(Describe(s), null)));
                    var overrides = await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                    results.AddRange(overrides.Select(s => new SemanticImplementation(Describe(s), null)));
                }
            }
            catch (OperationCanceledException) when (results.Count > 0)
            {
                deadlineExhausted = true; // found-so-far survives as a lower bound (24n)
            }

            // Hierarchy ranking: concrete (instantiable) leaves first — the actual runtime targets —
            // then abstract scaffolding; stable by display within each tier.
            results = results
                .OrderBy(r => r.Declaration.IsAbstract ? 1 : 0)
                .ThenBy(r => r.Declaration.SymbolDisplay, StringComparer.Ordinal)
                .ToList();

            // Review r2: materialize BEFORE the emit — see DefinitionAsync.
            bool projectModelUnproven = FriendAssemblyAuthorityUnproven(symbol,
                results.Select(result => result.Declaration.Assembly ?? ""), coverage);
            var payload = new SemanticImplementations(Describe(symbol), results, coverage, skipped,
                deadlineExhausted,
                ClusterLoadMs: clusterLoadMs,
                QueryMs: swPhase.ElapsedMilliseconds - clusterLoadMs,
                ProjectModelUnproven: projectModelUnproven);
            bool unsupportedLanguageSkipped = coverage.SkippedProjects.Count > 0;
            bool candidateProjectsSkipped = skipped.Count > 0;
            bool failedLoads = coverage.FailedProjects.Count > 0;
            bool coverageIncomplete = coverage.LoadedProjects < coverage.RequestedProjects;
            bool incomplete = deadlineExhausted || unsupportedLanguageSkipped ||
                candidateProjectsSkipped || failedLoads || coverageIncomplete ||
                projectModelUnproven;
            string? telemetryReason = SemanticCoverageReasons.Primary(coverage,
                deadlineExhausted, candidateProjectsSkipped,
                projectModelUnproven: projectModelUnproven);
            EmitOpTelemetry("implementations", incomplete ? "partial" : "exact",
                telemetryReason,
                ownerBox.Stats, scanBox.Stats,
                clusterLoadMs, swPhase.ElapsedMilliseconds - clusterLoadMs,
                queryStages, planning); // epuc.1 + jj1q + epuc.3 stages
            return (payload, null);
        }
        catch (OperationCanceledException)
        {
            // t2b: cold-cluster warm-up vs real scan timeout — see DefinitionAsync for rationale.
            string reason = clusterLoadInProgress ? "cluster_cold_load" : "semantic_timeout";
            EmitOpTelemetry("implementations", "degraded", reason, ownerBox.Stats, scanBox.Stats,
                clusterLoadInProgress ? swPhase.ElapsedMilliseconds : clusterLoadMs,
                clusterLoadInProgress ? null : swPhase.ElapsedMilliseconds - clusterLoadMs,
                planning: planning); // epuc.1 + epuc.3
            return (null, reason);
        }
        catch (Exception ex)
        {
            _log($"Semantic implementations failed: {ex}");
            EmitOpTelemetry("implementations", "error", ex.GetType().Name, ownerBox.Stats, scanBox.Stats,
                clusterLoadInProgress ? swPhase.ElapsedMilliseconds : clusterLoadMs,
                clusterLoadInProgress ? null : swPhase.ElapsedMilliseconds - clusterLoadMs,
                planning: planning); // epuc.1 + epuc.3
            return (null, $"semantic_error:{ex.GetType().Name}");
        }
    }

    // ---------------------------------------------------------------- resolution

    /// <summary>
    /// Determines the owning project of a file position, loads its dependency closure,
    /// and resolves the symbol — all against a single Solution snapshot returned to the
    /// caller. Callers that then run SymbolFinder MUST use the returned Solution so that
    /// resolution and search share one snapshot (otherwise a reload/eviction between them
    /// silently orphans the symbol and yields empty "exact" results).
    /// </summary>
    private async Task<(SemanticSolutionLease? Lease, ISymbol? Symbol, string? OwningProject,
        ClusterCoverage? Coverage)> LoadOwnerAndResolveAsync(
        string path, int line, int? column, string? nameHint, CancellationToken ct,
        IndexQueries? snapshotQueries = null, int? arityHint = null,
        SemanticWorkspace.LoadStatsBox? statsBox = null)
    {
        string relPath = WorkspacePaths.Normalize(path);
        string owningProject;
        HashSet<string> closure;
        string[] operationReferenceTargets;
        if (snapshotQueries is not null)
        {
            var owners = snapshotQueries.ProjectsContaining(relPath);
            if (owners.Count == 0) return (null, null, null, null);
            owningProject = (owners.FirstOrDefault(o => !o.IsTest) ?? owners[0]).Name;
            closure = snapshotQueries.DependencyClosure(new[] { owningProject });
            operationReferenceTargets = UnambiguousDeclarationProjects(
                snapshotQueries, nameHint, arityHint, owningProject);
        }
        else
        {
            using var queries = _manager.OpenQueries();
            var owners = queries.ProjectsContaining(relPath);
            if (owners.Count == 0) return (null, null, null, null);
            owningProject = (owners.FirstOrDefault(o => !o.IsTest) ?? owners[0]).Name;
            closure = queries.DependencyClosure(new[] { owningProject });
            operationReferenceTargets = UnambiguousDeclarationProjects(
                queries, nameHint, arityHint, owningProject);
        }

        SemanticSolutionLease lease = await Workspace.EnsureLoadedAsync(closure, ct,
                ensureReferenceTo: operationReferenceTargets, statsBox: statsBox)
            .ConfigureAwait(false);
        try
        {
            var symbol = await ResolveInSolutionAsync(
                    lease.Solution, owningProject, relPath, line, column, nameHint, ct, arityHint)
                .ConfigureAwait(false);
            return (lease, symbol, owningProject, lease.Coverage);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static string[] UnambiguousDeclarationProjects(IndexQueries queries,
        string? nameHint, int? arityHint, string owningProject)
    {
        if (string.IsNullOrWhiteSpace(nameHint)) return [];
        const int proofLimit = 16;
        List<SymbolHit> hits = queries.SearchSymbols(nameHint, "exact", kinds: null,
            limit: proofLimit + 1, arity: arityHint);
        if (hits.Count == 0 || hits.Count > proofLimit) return [];

        string[] projects = hits
            .SelectMany(hit => queries.ProjectsContaining(hit.FilePath))
            .Where(project => project.Language.Equals("cs", StringComparison.OrdinalIgnoreCase))
            .Select(project => project.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return projects.Length == 1 &&
               !projects[0].Equals(owningProject, StringComparison.OrdinalIgnoreCase)
            ? projects
            : [];
    }

    /// <summary>
    /// Resolves the symbol at (path, line[, column]) — or the declaration named nameHint
    /// on that line — against a caller-provided Solution snapshot. Does not load or mutate
    /// the workspace, so the returned symbol is valid for SymbolFinder on the same snapshot.
    /// </summary>
    private async Task<ISymbol?> ResolveInSolutionAsync(
        Solution solution, string owningProject, string relPath, int line, int? column,
        string? nameHint, CancellationToken ct, int? arityHint = null)
    {
        var project = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, owningProject, StringComparison.OrdinalIgnoreCase));
        if (project is null) return null;

        string fullPath = Path.GetFullPath(Path.Combine(_manager.WorkspaceRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var docId = solution.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault(d => d.ProjectId == project.Id)
                    ?? solution.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault();
        if (docId is null) return null;
        var document = solution.GetDocument(docId);
        if (document is null) return null;

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count) return null;
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null) return null;

        TextLine targetLine = text.Lines[line - 1];
        if (nameHint is { Length: > 0 } && arityHint is { } exactArity)
        {
            ISymbol? declaration = root.DescendantNodes(targetLine.Span)
                .Select(node => model.GetDeclaredSymbol(node, ct))
                .Where(symbol => symbol is not null &&
                    symbol.Name.Equals(nameHint, StringComparison.Ordinal) &&
                    ArityOf(symbol) == exactArity)
                .OrderBy(symbol => symbol!.Locations
                    .Where(location => location.IsInSource)
                    .Select(location => location.SourceSpan.Start)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min())
                .FirstOrDefault();
            if (declaration is not null) return declaration;
        }

        int position = ComputePosition(targetLine, column, nameHint);

        var token = root.FindToken(position);
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            var declared = model.GetDeclaredSymbol(node, ct);
            if (declared is not null &&
                (nameHint is null || declared.Name.Equals(nameHint, StringComparison.Ordinal)) &&
                (arityHint is null || ArityOf(declared) == arityHint.Value))
            {
                // With a name hint we require an exact name match: the old `token.ValueText ==
                // declared.Name` fallback accepted a sibling declarator on a multi-variable line
                // (e.g. resolving "Height" to "HeightRatio"), returning the wrong symbol as 'exact'.
                return declared;
            }
            if (node is StatementSyntax or MemberDeclarationSyntax)
            {
                break;
            }
        }

        ISymbol? positioned = await SymbolFinder.FindSymbolAtPositionAsync(document, position, ct)
            .ConfigureAwait(false);
        return positioned is not null &&
               (nameHint is null || positioned.Name.Equals(nameHint, StringComparison.Ordinal)) &&
               (arityHint is null || ArityOf(positioned) == arityHint.Value)
            ? positioned
            : null;
    }

    private static int ArityOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.Arity,
        IMethodSymbol method => method.Arity,
        _ => 0,
    };

    private static int ComputePosition(TextLine textLine, int? column, string? nameHint)
    {
        if (column is { } col && col >= 1)
        {
            return Math.Min(textLine.Start + col - 1, Math.Max(textLine.Start, textLine.End - 1));
        }
        if (nameHint is not null)
        {
            // Whole-identifier match so a hint like "Height" does not land inside "HeightRatio"
            // on a multi-declarator line and resolve the wrong sibling.
            int idx = IndexOfWholeIdentifier(textLine.ToString(), nameHint);
            if (idx >= 0) return textLine.Start + idx;
        }
        string s = textLine.ToString();
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return textLine.Start + Math.Min(i, Math.Max(0, s.Length - 1));
    }

    /// <summary>Index of <paramref name="token"/> in <paramref name="line"/> where it occurs as a
    /// whole identifier (bounded by non-identifier characters), or -1. Prevents a name hint from
    /// matching inside a longer identifier (e.g. "Height" inside "HeightRatio").</summary>
    internal static int IndexOfWholeIdentifier(string line, string token)
    {
        if (token.Length == 0) return -1;
        int from = 0;
        while ((from = line.IndexOf(token, from, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = from == 0 || !IsIdentifierChar(line[from - 1]);
            int end = from + token.Length;
            bool rightOk = end >= line.Length || !IsIdentifierChar(line[end]);
            if (leftOk && rightOk) return from;
            from = end;
        }
        return -1;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Shared phase-2 for dependent-scanning ops (references/implementations/callers/
    /// hierarchy): given the symbol name and owning project, loads the FTS-candidate
    /// dependent scan set and re-resolves the symbol IN the loaded snapshot. The returned
    /// symbol and solution are one consistent snapshot for SymbolFinder.
    /// </summary>
    private async Task<(SemanticSolutionLease Lease, ISymbol? Symbol, ClusterCoverage Coverage, List<string> Skipped, List<string> OutOfGraph)> LoadScanSetAndResolveAsync(
        string symbolName, string owningProject, string path, int line, int? column, string? nameHint,
        int maxProjects, IndexQueries q, CancellationToken ct,
        IReadOnlyList<string>? prioritySeeds = null, int? arityHint = null,
        SemanticWorkspace.LoadStatsBox? statsBox = null,
        bool includeGenerated = true, bool includeTests = true,
        ScanPlanStatsBox? planStatsBox = null)
    {
        bool capturePlan = planStatsBox is not null;
        long planStarted = capturePlan ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long dependentGraphTicks = 0;
        long candidateDiscoveryTicks = 0;
        long dependencyGraphTicks = 0;
        int seedProjectCount = 0;
        int candidateProjectCount = 0;
        int selectedProjectCount = 0;
        List<string> skipped = [];
        var outOfGraph = new List<string>();
        var outOfGraphSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsupportedCandidatePaths = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> scanSet = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            HashSet<string> dependents;
            long dependentStarted = capturePlan ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                dependents = q.DependentClosure(owningProject);
            }
            finally
            {
                if (capturePlan)
                {
                    dependentGraphTicks +=
                        System.Diagnostics.Stopwatch.GetTimestamp() - dependentStarted;
                }
            }
            dependents.Add(owningProject);
            int budget = NormalizeCandidateProjectBudget(maxProjects);

            var chosen = new List<string>();
            var chosenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var relevant = new List<string>();
            var relevantSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Priority seeds (e.g. the projects that syntactically IMPLEMENT the type) load first —
            // they carry the answer even though they rank low on raw name-mention frequency (a class
            // names the interface once, while heavy consumers name it dozens of times and would
            // otherwise exhaust the budget first, leaving the implementers unloaded).
            var orderedSeeds = (prioritySeeds ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            seedProjectCount = orderedSeeds.Count;

            void Consider(string project)
            {
                if (relevantSet.Add(project)) relevant.Add(project);
                if (chosen.Count < budget && chosenSet.Add(project)) chosen.Add(project);
            }

            void AddOutOfGraph(string project)
            {
                if (outOfGraphSet.Add(project)) outOfGraph.Add(project);
            }

            // Graph-valid implementer seeds are strongest: they both name the type in a base list
            // and can legally reach its declaring project. Same-simple-name seeds outside the graph
            // are recovery candidates, but must not starve graph-valid textual consumers.
            foreach (string project in orderedSeeds.Where(dependents.Contains)) Consider(project);

            // kbn: textual candidates OUTSIDE the dependency closure (and not seed-chosen) were
            // dropped SILENTLY — not even in `skipped`. Post-lhg the closure covers assembly-ref
            // consumers, so this residue is reflection-loaded plugins / config-wired consumers:
            // projects that mention the name but have no graph path to the declarer. Retain all of
            // them until after the loaded-solution filter; only the public sample is capped.
            var candidates = new List<(string Project, int FileCount)>();
            List<SemanticTextCandidateProject> discoveredCandidates;
            long candidateStarted = capturePlan ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                discoveredCandidates = q.CandidateProjectsForName(symbolName, ct,
                    includeGenerated, includeTests);
            }
            finally
            {
                if (capturePlan)
                {
                    candidateDiscoveryTicks +=
                        System.Diagnostics.Stopwatch.GetTimestamp() - candidateStarted;
                }
            }
            candidateProjectCount = discoveredCandidates.Count;
            foreach (var c in discoveredCandidates)
            {
                // Candidate ownership is a physical fact. An F# file that mentions the symbol
                // must not nominate a same-logical-name C# project for Roslyn loading; that would
                // turn an unscanned use into an apparently exact zero. Retain the physical path as
                // explicit unsupported-language coverage instead.
                if (!c.Language.Equals("cs", StringComparison.OrdinalIgnoreCase))
                {
                    unsupportedCandidatePaths.Add(c.ProjectPath);
                    continue;
                }
                if (dependents.Contains(c.Project))
                    candidates.Add((c.Project, c.FileCount));
                else if (!chosenSet.Contains(c.Project)) AddOutOfGraph(c.Project);
            }
            foreach (var c in candidates)
                Consider(c.Project);
            foreach (string project in orderedSeeds.Where(project => !dependents.Contains(project)))
            {
                Consider(project);
                AddOutOfGraph(project);
            }
            long dependencyStarted = capturePlan ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                scanSet = q.DependencyClosure(new[] { owningProject });
            }
            finally
            {
                if (capturePlan)
                {
                    dependencyGraphTicks +=
                        System.Diagnostics.Stopwatch.GetTimestamp() - dependencyStarted;
                }
            }
            foreach (var p in chosen) scanSet.Add(p);
            selectedProjectCount = chosen.Count;
            // The owning project's mandatory dependency closure is loaded regardless of the
            // optional candidate budget. Report only relevant projects absent from the FINAL scan
            // set, otherwise a budget filled by seeds can falsely mark a project that was loaded.
            skipped = relevant.Where(project => !scanSet.Contains(project)).ToList();
        }
        finally
        {
            if (planStatsBox is not null)
            {
                double totalMs = System.Diagnostics.Stopwatch
                    .GetElapsedTime(planStarted).TotalMilliseconds;
                double dependentMs = TicksToMs(dependentGraphTicks);
                double candidatesMs = TicksToMs(candidateDiscoveryTicks);
                double dependencyMs = TicksToMs(dependencyGraphTicks);
                planStatsBox.Stats = new ScanPlanStats(
                    totalMs,
                    dependentMs,
                    candidatesMs,
                    dependencyMs,
                    Math.Max(0, totalMs - dependentMs - candidatesMs - dependencyMs),
                    seedProjectCount,
                    candidateProjectCount,
                    selectedProjectCount,
                    scanSet.Count,
                    skipped.Count,
                    outOfGraph.Count,
                    unsupportedCandidatePaths.Count);
            }
        }

        SemanticSolutionLease lease = await Workspace
            .EnsureLoadedAsync(scanSet, ct, ensureReferenceTo: new[] { owningProject },
                statsBox: statsBox).ConfigureAwait(false);
        try
        {
            Solution solution = lease.Solution;
            ClusterCoverage coverage = lease.Coverage;
            if (unsupportedCandidatePaths.Count > 0)
            {
                coverage = coverage with
                {
                    SkippedProjects = coverage.SkippedProjects
                        .Concat(unsupportedCandidatePaths)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                };
            }
            // `outOfGraph` is coverage evidence, not merely topology trivia: callers use it to
            // decide whether the reference census is a lower bound. Loading one chosen project can
            // pull in additional dependencies that were not explicit members of `scanSet`, so filter
            // against the actual Roslyn solution rather than the requested names.
            var loadedProjectNames = solution.Projects
                .Select(project => project.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            outOfGraph.RemoveAll(project =>
            {
                if (!loadedProjectNames.Contains(project)) return false;
                outOfGraphSet.Remove(project);
                return true;
            });
            var symbol = await ResolveInSolutionAsync(
                solution, owningProject, WorkspacePaths.Normalize(path), line, column, nameHint, ct,
                arityHint).ConfigureAwait(false);
            return (lease, symbol, coverage, skipped, outOfGraph);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        static double TicksToMs(long ticks) =>
            ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ---------------------------------------------------------------- shaping

    private static bool FriendAssemblyAuthorityUnproven(ISymbol symbol,
        IEnumerable<string> accessingProjects, ClusterCoverage? coverage)
    {
        string? declaringAssembly = symbol.ContainingAssembly?.Name;
        if (declaringAssembly is null || !RequiresFriendAssemblyAccess(symbol) ||
            coverage?.UnprovenFriendAssemblyProjects is not { Count: > 0 } unproven ||
            !unproven.Contains(declaringAssembly, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        return accessingProjects.Any(project =>
            !string.IsNullOrWhiteSpace(project) &&
            !project.Equals(declaringAssembly, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RequiresFriendAssemblyAccess(ISymbol symbol)
    {
        for (ISymbol? current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is Accessibility.Internal or
                Accessibility.ProtectedAndInternal or Accessibility.ProtectedOrInternal)
            {
                return true;
            }
            if (current.ContainingType is null) break;
        }
        return false;
    }

    private SemanticDeclaration Describe(ISymbol symbol)
    {
        var spans = new List<DeclarationSpan>();
        var seenSpans = new HashSet<(string, int, int)>();
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var lineSpan = syntaxRef.SyntaxTree.GetLineSpan(syntaxRef.Span);
            string rel = ToRelPath(syntaxRef.SyntaxTree.FilePath);
            int start = lineSpan.StartLinePosition.Line + 1;
            int end = lineSpan.EndLinePosition.Line + 1;
            // A file linked into more than one project can surface the SAME declaration span through
            // different project graphs; dedupe so declarations[] has no duplicate path+span entry.
            if (seenSpans.Add((rel, start, end)))
                spans.Add(new DeclarationSpan(rel, start, end, symbol.ContainingAssembly?.Name ?? ""));
        }
        return new SemanticDeclaration(
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            symbol.GetDocumentationCommentId(),
            symbol.Kind.ToString().ToLowerInvariant(),
            symbol.ContainingType?.Name,
            symbol.ContainingNamespace?.ToDisplayString(),
            symbol.ContainingAssembly?.Name,
            spans,
            symbol.IsAbstract);
    }

    /// <summary>The base type that introduces <paramref name="contract"/> into
    /// <paramref name="type"/>'s hierarchy when it is implemented/inherited indirectly (so the caller
    /// sees "via BarBase"); null when <paramref name="type"/> declares it directly or the link cannot
    /// be pinpointed. Powers the derivation path in implementations ranking.</summary>
    /// <summary>jj1q: closure-verified implementer discovery — replaces the global
    /// SymbolFinder walk (field two-trace: 43.9s warm across 90 compilations for 8 results;
    /// the walk forces compilation materialization per visited project, and Roslyn's weak
    /// compilation cache makes repeats pay again). Syntax narrows: the index supplies the full
    /// TRANSITIVE base-list closure (A→B→C→D chains of any depth, derived-interface hops
    /// included). Semantics verifies: each candidate resolves in the loaded solution and its
    /// AllInterfaces/base chain must actually reach the target (same-name collisions across
    /// namespaces are pruned here). Only candidate-declaring projects materialize
    /// compilations. Verified symbols stream through <paramref name="onVerified"/> so a
    /// mid-loop deadline preserves the found-so-far lower bound (24n).
    /// Returns Capped=true WITHOUT verifying when the closure walk aborted on pathological
    /// fan-out — the caller MUST run the exhaustive compiler search then (a truncated closure
    /// must never ship as an exact answer).</summary>
    private sealed record ClosureVerificationResult(
        long VerifyMs,
        int Verified,
        int DocumentCandidates,
        int DeclarationsMatched,
        int SymbolsResolved,
        int DirectBaseLinks,
        int ErrorDirectBaseLinks,
        int TargetDirectMatches);

    private async Task<ClosureVerificationResult>
        VerifyClosureImplementersAsync(INamedTypeSymbol target, Solution solution,
            IReadOnlyList<SymbolHit> closure, Action<INamedTypeSymbol> onVerified,
            CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string targetId = target.OriginalDefinition.GetDocumentationCommentId()
            ?? target.OriginalDefinition.ToDisplayString();
        // HOP-LOCAL verification (deep-chain fixture): a candidate's AllInterfaces binds in
        // ITS OWN compilation, and workspace wiring is depth-1 - four projects down, the
        // chain's middle types are unresolvable and AllInterfaces silently severs (the old
        // exhaustive walk had the same blindness). Instead, each candidate only needs its
        // DIRECT bases - always resolvable via the direct project reference - checked against
        // the already-verified set. The closure arrives in BFS order from the target, so
        // parents verify before children; interface hops verify (join the set) but are not
        // emitted (parity with FindImplementationsAsync's output shape).
        var verifiedIds = new HashSet<string>(StringComparer.Ordinal) { targetId };
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        int verifiedCount = 0;

        // Pass 1: resolve every candidate ONCE (per-file document lookup + declaration match).
        int documentCandidates = 0;
        int declarationsMatched = 0;
        int symbolsResolved = 0;
        int directBaseLinks = 0;
        int errorDirectBaseLinks = 0;
        int targetDirectMatches = 0;
        var resolved = new List<(INamedTypeSymbol Candidate, string Id, string[] DirectBaseIds)>();
        var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in closure)
        {
            ct.ThrowIfCancellationRequested();
            string full = Path.Combine(_manager.WorkspaceRoot,
                hit.FilePath.Replace('/', Path.DirectorySeparatorChar));
            foreach (var docId in solution.GetDocumentIdsWithFilePath(full))
            {
                documentCandidates++;
                var document = solution.GetDocument(docId);
                if (document is null) continue;
                var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (root is null || model is null) continue;
                var declaration = root.DescendantNodes()
                    .OfType<BaseTypeDeclarationSyntax>()
                    .FirstOrDefault(d => d.Identifier.ValueText == hit.Name
                        && d.GetLocation().GetLineSpan().StartLinePosition.Line + 1 <= hit.StartLine
                        && d.GetLocation().GetLineSpan().EndLinePosition.Line + 1 >= hit.StartLine);
                if (declaration is null) continue;
                declarationsMatched++;
                if (model.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol candidate) continue;
                symbolsResolved++;
                string id = candidate.OriginalDefinition.GetDocumentationCommentId()
                    ?? candidate.OriginalDefinition.ToDisplayString();
                string[] directBaseIds = DirectBaseIds(candidate).ToArray();
                directBaseLinks += directBaseIds.Length;
                errorDirectBaseLinks += (candidate.BaseType?.TypeKind == TypeKind.Error ? 1 : 0)
                    + candidate.Interfaces.Count(i => i.TypeKind == TypeKind.Error);
                if (directBaseIds.Contains(targetId, StringComparer.Ordinal)) targetDirectMatches++;
                if (resolvedIds.Add(id)) resolved.Add((candidate, id, directBaseIds)); // partials dedupe here
                break; // one document resolution per hit (linked files are duplicates)
            }
        }

        // Pass 2..N: hop-local verification to FIXPOINT. BFS order verifies most chains in one
        // pass, but a candidate first discovered through an impostor name-line can have its
        // only LEGITIMATE parent verify later - iterate until no new verification lands
        // (bounded by closure size; each pass is cheap symbol-graph reads, no re-resolution).
        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            ct.ThrowIfCancellationRequested();
            foreach (var (candidate, id, directBaseIds) in resolved)
            {
                if (verifiedIds.Contains(id)) continue;
                if (!directBaseIds.Any(verifiedIds.Contains)) continue;
                verifiedIds.Add(id); // children hop through this candidate
                progressed = true;
                // Interface hops verify (the chain continues through them) but are not
                // implementations - parity with FindImplementationsAsync's output shape.
                if (candidate.TypeKind != TypeKind.Interface && emitted.Add(id))
                {
                    verifiedCount++;
                    onVerified(candidate);
                }
            }
        }
        return new ClosureVerificationResult(
            sw.ElapsedMilliseconds,
            verifiedCount,
            documentCandidates,
            declarationsMatched,
            symbolsResolved,
            directBaseLinks,
            errorDirectBaseLinks,
            targetDirectMatches);
    }

    /// <summary>jj1q: distinct projects declaring the closure's candidates — these SEED the
    /// scan-set load. The deep-chain fixture proved this is a CORRECTNESS requirement, not an
    /// optimization: a transitive implementer's project that never textually mentions the
    /// target (LeafHandler four hops down) was never a load candidate, so even the exhaustive
    /// compiler walk could not see it — the old path silently missed deep chains.</summary>
    private static List<string> ClosureSeedProjects(IndexQueries queries,
        IReadOnlyList<SymbolHit> closure)
    {
        var seeds = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in closure)
        {
            if (!files.Add(hit.FilePath)) continue;
            foreach (var owner in queries.ProjectsContaining(hit.FilePath))
            {
                if (seen.Add(owner.Name)) seeds.Add(owner.Name);
            }
        }
        return seeds;
    }

    /// <summary>A capped syntactic closure cannot prove which transitive implementer projects
    /// matter. Seed every graph-valid dependent so the compiler fallback is genuinely exhaustive.
    /// A positive maxProjects budget is applied later by LoadScanSetAndResolveAsync, where omitted
    /// dependents become explicit skipped-candidate coverage instead of a false exact answer.</summary>
    private static List<string> CompleteDependentSeeds(IndexQueries queries, string owningProject) =>
        queries.DependentClosure(owningProject)
            .OrderBy(project => project, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The candidate's DIRECT bases only - its immediate BaseType and its
    /// syntactically declared interfaces. Direct bases always resolve (the direct project
    /// reference is wired); anything deeper may not (depth-1 wiring), which is exactly why
    /// verification hops one level at a time against the verified set.</summary>
    private static IEnumerable<string> DirectBaseIds(INamedTypeSymbol candidate)
    {
        if (candidate.BaseType is { } b)
        {
            yield return b.OriginalDefinition.GetDocumentationCommentId()
                ?? b.OriginalDefinition.ToDisplayString();
        }
        foreach (var i in candidate.Interfaces)
        {
            yield return i.OriginalDefinition.GetDocumentationCommentId()
                ?? i.OriginalDefinition.ToDisplayString();
        }
    }

    private static string? DerivationVia(ISymbol type, INamedTypeSymbol contract)
    {
        if (type is not INamedTypeSymbol named) return null;
        var target = contract.OriginalDefinition;

        if (contract.TypeKind == TypeKind.Interface)
        {
            if (named.Interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target)))
                return null; // declares the interface directly
            for (var b = named.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            {
                if (b.Interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target)))
                    return b.Name; // the base that introduces the interface
            }
            return null; // inherited but not pinpointable (e.g. through another interface)
        }

        // Derived-class query: direct when contract is the immediate base; else name that base.
        if (named.BaseType is { } bt && SymbolEqualityComparer.Default.Equals(bt.OriginalDefinition, target))
            return null;
        return named.BaseType?.Name;
    }

    private Dictionary<string, bool> ProjectTestFlags()
    {
        using var q = _manager.OpenQueries();
        return q.AllProjectTestFlags("cs");
    }

    private string ToRelPath(string fullPath)
    {
        try
        {
            string root = Path.GetFullPath(_manager.WorkspaceRoot);
            string full = Path.GetFullPath(fullPath);
            string relative = Path.GetRelativePath(root, full);
            if (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return WorkspacePaths.ToGitPath(relative);
            }
            return WorkspacePaths.ToGitPath(full);
        }
        catch
        {
            return WorkspacePaths.ToGitPath(fullPath);
        }
    }

    private static string Truncate(string s) => s.Length <= 240 ? s : s[..240] + "…";

    public void Dispose()
    {
        _workspace?.Dispose();
        // SemaphoreSlim.Dispose may race an in-flight semantic request between WaitAsync and
        // Release. It owns no unmanaged resource unless its WaitHandle is materialized (we never
        // do that), so let it be collected with the service after outstanding calls drain.
    }
}
