using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Core.Semantic;

public sealed record ClusterCoverage(
    int LoadedProjects,
    int RequestedProjects,
    List<string> SkippedProjects,
    List<string> FailedProjects,
    bool FrameworkRefsAvailable,
    // Field 0.7.0 ("coverage 1/1 but 8 hits from 8 projects"): SymbolFinder scans the WHOLE
    // solution, including projects resident from earlier calls — this makes that visible.
    int SolutionProjects = 0);

/// <summary>
/// Owns: an AdhocWorkspace populated lazily with per-project compilations built from
/// parsed csproj facts (no MSBuild evaluation): documents from live files, framework
/// reference assemblies, hint-path/NuGet package dlls, in-cluster project references.
/// LRU-evicts beyond a project cap; reloads projects whose files changed (index fingerprint).
/// Does not own: which projects to load (SemanticService decides) or result shaping.
/// </summary>
public sealed class SemanticWorkspace : IDisposable
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
        public long LastUse { get; set; }
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
    /// x5ls.1.3: the four sub-splits dissect ProjectLoadMs (field: 6.8s lump post-xqxw/pv1k) —
    /// csproj/packages parsing, source open+read+decode, dll metadata resolution, and Roslyn
    /// workspace mutation. Their sum can undershoot ProjectLoadMs (loop residue); phases never
    /// entered report 0.</summary>
    public sealed record LoadStats(double GateWaitMs, double FingerprintMs, double TopoMs,
        double ProjectLoadMs, int? LoadedBefore, int Requested, int Reloaded, int Loaded,
        int Failed, double ProjectParseMs = 0, double SourceReadMs = 0,
        double MetadataResolveMs = 0, double WorkspaceMutationMs = 0);

    /// <summary>x5ls.1.3: tick accumulator threaded through LoadProject — one per
    /// EnsureLoadedAsync call, summed across that call's projects (single-threaded under the
    /// gate; the pv1k worker fan-out is bracketed from the outside, never written from workers).</summary>
    private sealed class LoadPhaseTicks
    {
        public long Parse, Read, Metadata, Mutation;
    }

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
    /// caller for full-fidelity targets) are loaded; returns a solution snapshot.
    /// Load order is topological (dependencies first); references to projects outside
    /// the requested set are skipped (navigation-grade holes).
    /// </summary>
    public async Task<(Solution Solution, ClusterCoverage Coverage)> EnsureLoadedAsync(
        IReadOnlyCollection<string> projectNames, CancellationToken ct,
        IReadOnlyCollection<string>? ensureReferenceTo = null,
        LoadStatsBox? statsBox = null)
    {
        // Allocated BEFORE the gate: nothing may sit between WaitAsync and try that could
        // throw, or the semaphore leaks. (The finally reads these plus _loaded under the gate.)
        var requested = new HashSet<string>(projectNames, StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();
        var phaseTicks = new LoadPhaseTicks(); // x5ls.1.3: sub-splits of projectLoadMs
        long tFinger = 0, tTopo = 0; // 0 = the phase never completed (died mid-phase)
        int reloadedCount = 0;
        long tEnter = System.Diagnostics.Stopwatch.GetTimestamp(); // epuc.1
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Review r2: a deadline dying while QUEUED behind another caller's load is the
            // primary gate-contention signal (cold workspace, two parallel ops) — without this
            // stamp such records carried no split at all. Gate wait absorbs the whole wall;
            // phases never entered report 0; loadedBefore stays null (unknown without the gate).
            if (statsBox is not null)
            {
                statsBox.Stats = new LoadStats(
                    ToMs(System.Diagnostics.Stopwatch.GetTimestamp() - tEnter), 0, 0, 0,
                    null, requested.Count, 0, 0, 0);
            }
            throw;
        }
        long tGate = System.Diagnostics.Stopwatch.GetTimestamp();
        int loadedBefore = _loaded.Count;
        try
        {
            using var q = new IndexQueries(_dbPath, pinReadSnapshot: false,
                pooling: _poolIndexConnections);
            var skipped = new List<string>();

            // Reload any requested project whose files changed since load. Reuse its
            // existing ProjectId so already-loaded dependents keep valid references — a
            // fresh id would silently orphan them (Roslyn drops references to absent ids).
            var reuseIds = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
            var loadedRequested = requested.Where(_loaded.ContainsKey).ToList();
            // ONE grouped fingerprint query for the whole warm set — this loop ran a point query per
            // already-loaded project on every semantic call (dz3: the dominant warm-path SQL cost).
            var fingerprints = loadedRequested.Count > 0
                ? q.ProjectFingerprints(loadedRequested)
                : new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in loadedRequested)
            {
                var current = fingerprints.TryGetValue(name, out var fp) ? fp : (0L, 0L);
                if (_loaded[name].Fingerprint != current)
                {
                    _log($"Semantic reload (files changed): {name}");
                    reuseIds[name] = _loaded[name].Id;
                    _workspace.TryApplyChanges(_workspace.CurrentSolution.RemoveProject(_loaded[name].Id));
                    _loaded.Remove(name);
                    // epuc.1 review r2: stamped PER REMOVAL, not after the loop — a load dying
                    // mid-loop must still explain why `loaded` dropped below `loadedBefore`.
                    reloadedCount++;
                }
            }

            tFinger = System.Diagnostics.Stopwatch.GetTimestamp(); // epuc.1
            var toLoad = TopoOrder(q, requested.Where(n => !_loaded.ContainsKey(n)).ToList());
            tTopo = System.Diagnostics.Stopwatch.GetTimestamp(); // epuc.1
            var frameworkRefs = ReferenceAssemblyLocator.Net472References(out string? refDir);
            if (refDir is null)
            {
                _log("WARNING: no .NET Framework reference assemblies found — semantic fidelity degraded.");
            }

            foreach (var name in toLoad)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var reuseId = reuseIds.TryGetValue(name, out var rid) ? rid : null;
                    if (LoadProject(q, name, frameworkRefs, reuseId, ensureReferenceTo, phaseTicks) is { } lp)
                    {
                        _loaded[name] = lp;
                    }
                    else
                    {
                        failed.Add(name);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"Semantic load failed for {name}: {ex.Message}");
                    failed.Add(name);
                }
            }

            foreach (var name in requested)
            {
                if (_loaded.TryGetValue(name, out var lp)) lp.LastUse = ++_useCounter;
            }

            EvictBeyondCap(requested);

            var coverage = new ClusterCoverage(
                LoadedProjects: requested.Count(n => _loaded.ContainsKey(n)),
                RequestedProjects: requested.Count,
                SkippedProjects: skipped,
                FailedProjects: failed,
                FrameworkRefsAvailable: refDir is not null,
                SolutionProjects: _workspace.CurrentSolution.ProjectIds.Count);
            return (_workspace.CurrentSolution, coverage);
        }
        finally
        {
            // epuc.1 (review F2): fill THIS call's box even when the load dies mid-flight —
            // cluster_cold_load records are precisely the ones whose split we need most.
            // A phase that never stamped its end gets the remaining wall (died in-phase);
            // phases never entered report 0.
            if (statsBox is not null)
            {
                long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                statsBox.Stats = new LoadStats(
                    ToMs(tGate - tEnter),
                    tFinger > 0 ? ToMs(tFinger - tGate) : ToMs(tEnd - tGate),
                    tTopo > 0 ? ToMs(tTopo - tFinger) : tFinger > 0 ? ToMs(tEnd - tFinger) : 0,
                    tTopo > 0 ? ToMs(tEnd - tTopo) : 0,
                    loadedBefore, requested.Count, reloadedCount,
                    requested.Count(n => _loaded.ContainsKey(n)), failed.Count,
                    ToMs(phaseTicks.Parse), ToMs(phaseTicks.Read),
                    ToMs(phaseTicks.Metadata), ToMs(phaseTicks.Mutation));
            }
            _gate.Release();
        }
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    // xqxw: HintPath/package MetadataReferences were re-created PER PROJECT — the same vendor
    // dll re-mapped, re-validated, and later re-bound for every project in a scan set (field
    // telemetry: scanLoad.projectLoadMs 12.3s cold). PortableExecutableReference is immutable
    // and designed for reuse across compilations; reusing ONE instance also lets Roslyn share
    // the lazily-decoded assembly symbols. One stat per lookup replaces a full map+parse.
    // Invalidation is (mtime, size) — the same heuristic MSBuild up-to-date checks use, with
    // the same KNOWN WINDOW: a timestamp-preserving pipeline (MSBuild Copy propagates source
    // mtime; build caches normalize stamps) dropping a same-length dll serves the stale
    // instance until restart or the next stamp/size change. Accepted deliberately (review):
    // direct NTFS writes cannot collide (100ns stamps); escalate to content hashing only on
    // field evidence. Entries live until workspace Dispose — no eviction; the retained bytes
    // are bounded by the workspace's distinct dll set and strictly beat the old one-copy-per-
    // project duplication while any two consumers share a dll. Only touched under _gate
    // (LoadProject runs inside EnsureLoadedAsync's critical section) — no lock needed.
    private readonly Dictionary<string, (DateTime MTimeUtc, long Size, PortableExecutableReference Ref)>
        _metadataRefCache = new(StringComparer.OrdinalIgnoreCase);

    internal PortableExecutableReference? GetOrCreateMetadataRef(string fullPath) // internal: tests pin identity+invalidation
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return null;
            if (_metadataRefCache.TryGetValue(fullPath, out var cached) &&
                cached.MTimeUtc == fi.LastWriteTimeUtc && cached.Size == fi.Length)
            {
                return cached.Ref;
            }
            var mref = MetadataReference.CreateFromFile(fullPath);
            _metadataRefCache[fullPath] = (fi.LastWriteTimeUtc, fi.Length, mref);
            return mref;
        }
        catch
        {
            return null; // unreadable/malformed dll: same skip semantics as before
        }
    }

    // ---------------------------------------------------------------- loading

    private LoadedProject? LoadProject(
        IndexQueries q, string name, IReadOnlyList<MetadataReference> frameworkRefs,
        ProjectId? reuseId, IReadOnlyCollection<string>? ensureReferenceTo,
        LoadPhaseTicks? phaseTicks = null)
    {
        var row = q.ProjectByName(name);
        if (row is null) return null;

        // Preserve live load-time fidelity (assembly refs, hint paths), but capture through the
        // bounded no-follow reader so a project or packages.config FIFO/socket/link cannot block
        // semantic loading. The byte parser remains BOM/encoding aware.
        long tPhase = System.Diagnostics.Stopwatch.GetTimestamp(); // x5ls.1.3
        byte[] projectBytes = GitInfo.ReadBoundedWorkspaceFile(_workspaceRoot, row.Path,
            IndexBuilder.MaxStructuralFileBytes) ?? [];
        string packagesPath = PackagesConfigPath(row.Path);
        byte[]? packagesBytes = GitInfo.ReadBoundedWorkspaceFile(_workspaceRoot, packagesPath,
            IndexBuilder.MaxStructuralFileBytes);
        var parsed = ProjectFileParser.ParseSnapshot(row.Path, projectBytes, packagesBytes);
        if (phaseTicks is not null)
        {
            phaseTicks.Parse += System.Diagnostics.Stopwatch.GetTimestamp() - tPhase;
        }

        var files = q.ProjectFiles(name);
        if (files.Count == 0) return null;

        var docs = new List<DocumentInfo>();
        // Reuse the prior id on reload (keeps dependents' references valid); mint a new
        // one only for a genuinely new project.
        var projectId = reuseId ?? ProjectId.CreateNewId(debugName: name);

        // pv1k: the per-file open→read→decode ran strictly sequentially — on a cold scan set
        // that is thousands of small CreateFile calls on one thread (field telemetry:
        // scanLoad.projectLoadMs 12.3s; this stage dominates). The reads are independent pure
        // functions, so fan them out bounded and ORDER-PRESERVING (index-addressed results —
        // document order must stay deterministic). Everything that is not thread-safe stays
        // on this thread: the SQLite fallback (q.ContentByPath) runs afterward for the rare
        // disk-miss, and DocumentInfo/workspace mutation below never see worker threads.
        // A worker exception propagates (as AggregateException) into the same per-project
        // catch that marked sequential-era failures — project lands in `failed`; only the
        // log text shape differs (review r2 nit, accepted).
        // Big files (review r2 LOW): DOP workers each staging a ≤256MB raw buffer could hold
        // ~2GB transiently where the old loop peaked at one buffer — anything over the
        // threshold is deferred to the sequential pass, restoring the old one-at-a-time peak
        // (plus at most DOP small buffers).
        const long ParallelReadMaxBytes = 4 * 1024 * 1024;
        tPhase = System.Diagnostics.Stopwatch.GetTimestamp(); // x5ls.1.3: source_read starts
        var decoded = new SourceText?[files.Count];
        var deferredBig = new bool[files.Count];
        System.Threading.Tasks.Parallel.For(0, files.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount),
        }, i =>
        {
            string workerFull = Path.Combine(_workspaceRoot,
                files[i].Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (new FileInfo(workerFull) is { Exists: true, Length: > ParallelReadMaxBytes })
                {
                    deferredBig[i] = true;
                    return;
                }
            }
            catch
            {
                // stat failed: fall through to the bounded reader, which resolves it safely
            }
            byte[]? liveBytes = GitInfo.ReadBoundedWorkspaceFile(_workspaceRoot, files[i],
                DeltaRefresher.MaxIndexedFileBytes);
            if (liveBytes is not null)
            {
                using var stream = new MemoryStream(liveBytes, writable: false);
                decoded[i] = SourceText.From(stream);
            }
        });

        for (int i = 0; i < files.Count; i++)
        {
            string rel = files[i];
            string full = Path.Combine(_workspaceRoot,
                rel.Replace('/', Path.DirectorySeparatorChar));
            SourceText? text = decoded[i];
            if (text is null && deferredBig[i])
            {
                // Sequential big-file read: same bytes, same authority, old-era peak memory.
                byte[]? bigBytes = GitInfo.ReadBoundedWorkspaceFile(_workspaceRoot, rel,
                    DeltaRefresher.MaxIndexedFileBytes);
                if (bigBytes is not null)
                {
                    using var stream = new MemoryStream(bigBytes, writable: false);
                    text = SourceText.From(stream);
                }
            }
            if (text is null)
            {
                string? indexed = q.ContentByPath(rel);
                if (indexed is null) continue;
                text = SourceText.From(indexed);
            }
            docs.Add(DocumentInfo.Create(
                DocumentId.CreateNewId(projectId, debugName: rel),
                name: Path.GetFileName(rel),
                loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), full)),
                filePath: full));
        }

        // Project references FIRST (order matters for the dll-substitution below): in-cluster
        // only; unloaded refs become navigation-grade holes.
        var projectRefs = new List<ProjectReference>();
        var refIds = new HashSet<ProjectId>();
        var refNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // names actually wired
        var solutionNow = _workspace.CurrentSolution;
        // Cycle guard (review, HIGH): mutual assembly refs (A <Reference> B.dll, B <Reference> A.dll —
        // a legal multi-stage shape) put BOTH edges in project_refs. Within one load pass only
        // backward edges wire, but a fingerprint RELOAD re-adds a project while its dependents are
        // already loaded — wiring the forward edge then completes a ProjectReference CYCLE, which
        // AdhocWorkspace ACCEPTS (TryApplyChanges true) and which deadlocks GetCompilationAsync
        // forever after (review-reproduced; eviction can never break it — both sides stay
        // 'referenced'). Skip any edge whose target already REACHES this project id; the skipped
        // direction is deliberately NOT in refNames, so its hint dll below stays — the metadata
        // binding is the correct degraded wiring for the back edge.
        bool WouldCycle(ProjectId target) => ReachesId(solutionNow, target, projectId);
        using (var q2 = new IndexQueries(_dbPath, pinReadSnapshot: false,
                   pooling: _poolIndexConnections))
        {
            foreach (var edge in q2.ProjectGraph(name, 1, "downstream"))
            {
                if (_loaded.TryGetValue(edge.ToProject, out var dep) && !refIds.Contains(dep.Id) &&
                    !WouldCycle(dep.Id) && refIds.Add(dep.Id))
                {
                    projectRefs.Add(new ProjectReference(dep.Id));
                    refNames.Add(edge.ToProject);
                }
            }
        }
        // Guarantee visibility of the symbol-declaring project even when the dependency
        // path is transitive (SDK-style transitivity) — harmless when redundant.
        if (ensureReferenceTo is not null)
        {
            foreach (var target in ensureReferenceTo)
            {
                if (!target.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    _loaded.TryGetValue(target, out var dep) && !refIds.Contains(dep.Id) &&
                    !WouldCycle(dep.Id) && refIds.Add(dep.Id))
                {
                    projectRefs.Add(new ProjectReference(dep.Id));
                    refNames.Add(target);
                }
            }
        }

        if (phaseTicks is not null) // x5ls.1.3: source_read ends (fan-out + fallback + docs)
        {
            phaseTicks.Read += System.Diagnostics.Stopwatch.GetTimestamp() - tPhase;
        }
        tPhase = System.Diagnostics.Stopwatch.GetTimestamp(); // metadata_resolve starts
        var metadataRefs = new List<MetadataReference>(frameworkRefs);
        foreach (var (assembly, hint) in parsed.AssemblyRefs)
        {
            if (hint is null) continue; // plain framework refs covered by reference assemblies
            // Source-over-binary substitution (lhg): when the referenced assembly IS a project we
            // just wired a ProjectReference to (multi-staged builds reference the built dll from a
            // common folder, not the project — the recovered assembly-ref edge supplies the wire),
            // SKIP the metadata dll: the name binds to the SOURCE symbol. The dll ALONE binds
            // consumers to a METADATA symbol that never matches the queried source declaration —
            // the field's 8-implementers-found-0 trap. Keyed on refNames, NOT on _loaded: only a
            // WIRED ProjectReference justifies dropping the dll — a same-named project that is
            // merely loaded but not wired (edge missing, load-order miss, cycle-guard skip) must
            // keep its dll or the consumer is left with NEITHER binding. (Collided names DO
            // produce edges since Batch 29 — the wired-only keying is what stays load-bearing.)
            // Defense-in-depth note: with BOTH references present Roslyn empirically prefers the
            // source project in every shape we reproduced (equal and differing versions), so this
            // skip is not independently test-pinnable — it exists so we never depend on that
            // conflict resolution (e.g. strong-named field dlls whose distinct identities tests
            // cannot reproduce). TopoOrder loads dependencies first, so the edge target is already
            // in _loaded — and thus in refNames — when its consumers load.
            if (refNames.Contains(assembly) ||
                refNames.Contains(Path.GetFileNameWithoutExtension(hint)))
            {
                continue;
            }
            string full = Path.Combine(_workspaceRoot, hint.Replace('/', Path.DirectorySeparatorChar));
            // Existence is checked inside GetOrCreateMetadataRef (one stat serves both).
            if (GetOrCreateMetadataRef(full) is { } mref) metadataRefs.Add(mref);
        }
        foreach (var (pkg, version) in parsed.PackageRefs)
        {
            if (ReferenceAssemblyLocator.ResolvePackageDll(pkg, version) is { } dll)
            {
                if (GetOrCreateMetadataRef(dll) is { } mref) metadataRefs.Add(mref);
            }
        }

        if (phaseTicks is not null) // x5ls.1.3: metadata_resolve ends
        {
            phaseTicks.Metadata += System.Diagnostics.Stopwatch.GetTimestamp() - tPhase;
        }
        var info = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name: name,
                assemblyName: name,
                language: LanguageNames.CSharp,
                filePath: Path.Combine(_workspaceRoot, row.Path.Replace('/', Path.DirectorySeparatorChar)),
                compilationOptions: CompilationOptions,
                parseOptions: ParseOptions,
                documents: docs,
                projectReferences: projectRefs,
                metadataReferences: metadataRefs);

        tPhase = System.Diagnostics.Stopwatch.GetTimestamp(); // x5ls.1.3: workspace_mutation
        bool applied = _workspace.TryApplyChanges(_workspace.CurrentSolution.AddProject(info));
        if (phaseTicks is not null)
        {
            phaseTicks.Mutation += System.Diagnostics.Stopwatch.GetTimestamp() - tPhase;
        }
        if (!applied)
        {
            return null;
        }
        return new LoadedProject
        {
            Id = projectId,
            Fingerprint = q.ProjectFingerprint(name),
            LastUse = ++_useCounter,
        };
    }

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

    private List<string> TopoOrder(IndexQueries q, List<string> names)
    {
        // Order by dependency depth so referenced projects load before referencing ones.
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int Depth(string n, int guard)
        {
            if (guard > 64) return 0;
            if (depth.TryGetValue(n, out int d)) return d;
            depth[n] = 0; // cycle guard
            int max = 0;
            foreach (var e in q.ProjectGraph(n, 1, "downstream"))
            {
                if (set.Contains(e.ToProject))
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
        foreach (var (name, lp) in evictable)
        {
            _workspace.TryApplyChanges(_workspace.CurrentSolution.RemoveProject(lp.Id));
            _loaded.Remove(name);
        }
        if (evictable.Count > 0)
        {
            _log($"Semantic cache evicted {evictable.Count} projects (cap {cap}).");
        }
    }

    private static string PackagesConfigPath(string projectPath)
    {
        int slash = projectPath.LastIndexOf('/');
        return slash < 0 ? "packages.config" : projectPath[..(slash + 1)] + "packages.config";
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _gate.Dispose();
    }
}
