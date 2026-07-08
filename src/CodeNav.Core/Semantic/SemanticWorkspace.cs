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
    bool FrameworkRefsAvailable);

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

    public SemanticWorkspace(string workspaceRoot, string dbPath, Action<string>? log = null)
    {
        _workspaceRoot = workspaceRoot;
        _dbPath = dbPath;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Ensures the given projects (dependency closures must already be included by the
    /// caller for full-fidelity targets) are loaded; returns a solution snapshot.
    /// Load order is topological (dependencies first); references to projects outside
    /// the requested set are skipped (navigation-grade holes).
    /// </summary>
    public async Task<(Solution Solution, ClusterCoverage Coverage)> EnsureLoadedAsync(
        IReadOnlyCollection<string> projectNames, CancellationToken ct,
        IReadOnlyCollection<string>? ensureReferenceTo = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var q = new IndexQueries(_dbPath);
            var requested = new HashSet<string>(projectNames, StringComparer.OrdinalIgnoreCase);
            var failed = new List<string>();
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
                }
            }

            var toLoad = TopoOrder(q, requested.Where(n => !_loaded.ContainsKey(n)).ToList());
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
                    if (LoadProject(q, name, frameworkRefs, reuseId, ensureReferenceTo) is { } lp)
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
                FrameworkRefsAvailable: refDir is not null);
            return (_workspace.CurrentSolution, coverage);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- loading

    private LoadedProject? LoadProject(
        IndexQueries q, string name, IReadOnlyList<MetadataReference> frameworkRefs,
        ProjectId? reuseId, IReadOnlyCollection<string>? ensureReferenceTo)
    {
        var row = q.ProjectByName(name);
        if (row is null) return null;

        // Re-parse the csproj for load-time fidelity (assembly refs, hint paths).
        var parsed = ProjectFileParser.Parse(_workspaceRoot, row.Path);

        var files = q.ProjectFiles(name);
        if (files.Count == 0) return null;

        var docs = new List<DocumentInfo>();
        // Reuse the prior id on reload (keeps dependents' references valid); mint a new
        // one only for a genuinely new project.
        var projectId = reuseId ?? ProjectId.CreateNewId(debugName: name);
        foreach (var rel in files)
        {
            string full = Path.Combine(_workspaceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            SourceText text;
            try
            {
                using var stream = File.OpenRead(full);
                text = SourceText.From(stream);
            }
            catch (Exception)
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

        var metadataRefs = new List<MetadataReference>(frameworkRefs);
        foreach (var (assembly, hint) in parsed.AssemblyRefs)
        {
            if (hint is null) continue; // plain framework refs covered by reference assemblies
            string full = Path.Combine(_workspaceRoot, hint.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                try { metadataRefs.Add(MetadataReference.CreateFromFile(full)); } catch { /* skip */ }
            }
        }
        foreach (var (pkg, version) in parsed.PackageRefs)
        {
            if (ReferenceAssemblyLocator.ResolvePackageDll(pkg, version) is { } dll)
            {
                try { metadataRefs.Add(MetadataReference.CreateFromFile(dll)); } catch { /* skip */ }
            }
        }

        // In-cluster project references only; unloaded refs become navigation-grade holes.
        var projectRefs = new List<ProjectReference>();
        var refIds = new HashSet<ProjectId>();
        using (var q2 = new IndexQueries(_dbPath))
        {
            foreach (var edge in q2.ProjectGraph(name, 1, "downstream"))
            {
                if (_loaded.TryGetValue(edge.ToProject, out var dep) && refIds.Add(dep.Id))
                {
                    projectRefs.Add(new ProjectReference(dep.Id));
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
                    _loaded.TryGetValue(target, out var dep) && refIds.Add(dep.Id))
                {
                    projectRefs.Add(new ProjectReference(dep.Id));
                }
            }
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

        if (!_workspace.TryApplyChanges(_workspace.CurrentSolution.AddProject(info)))
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

    public void Dispose()
    {
        _workspace.Dispose();
        _gate.Dispose();
    }
}
