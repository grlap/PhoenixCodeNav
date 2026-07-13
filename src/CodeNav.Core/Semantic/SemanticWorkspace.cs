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
    int SolutionProjects = 0,
    int? CandidateProjects = null,
    int? CandidateProjectBudget = null,
    int LoadedVariants = 0,
    int RequestedVariants = 0,
    int? CandidateVariants = null,
    bool GraphUniverseComplete = true,
    bool CompileOwnershipComplete = true,
    bool ParseContextsComplete = true,
    bool CandidateDiscoveryComplete = true,
    List<string>? IncompleteReasons = null);

/// <summary>
/// Owns an AdhocWorkspace populated lazily with one Roslyn project per persisted compilation
/// variant. Stable variant keys, rather than assembly display names or database-local ids, are the
/// cache identity. The index owns variant/reference/compile evaluation; this class materializes the
/// selected facts against live source bytes and reloads a variant whenever one of its compiled or
/// structural inputs changes.
/// </summary>
public sealed class SemanticWorkspace : IDisposable
{
    private const int MaxLoadedVariants = 512;

    private readonly string _workspaceRoot;
    private readonly string _dbPath;
    private readonly Action<string> _log;
    private readonly bool _poolIndexConnections;
    private readonly AdhocWorkspace _workspace = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LoadedProject> _loaded = new(StringComparer.Ordinal);
    private long _useCounter;

    internal Action<string>? PhysicalProjectParsedForTest { get; set; }
    internal Action<string>? MetadataReferenceAddedForTest { get; set; }

    private sealed class LoadedProject
    {
        public required ProjectId Id { get; init; }
        public required long VariantId { get; set; }
        public required string ProjectPath { get; init; }
        public required (long Count, long Sum) Fingerprint { get; set; }
        public long LastUse { get; set; }
    }

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

    /// <summary>Compatibility entry point for callers that still select physical projects by
    /// assembly name. Every matching physical project expands to every persisted variant; no rows
    /// are merged by name.</summary>
    public Task<(Solution Solution, ClusterCoverage Coverage)> EnsureLoadedAsync(
        IReadOnlyCollection<string> projectNames, CancellationToken ct,
        IReadOnlyCollection<string>? ensureReferenceTo = null)
    {
        using var q = new IndexQueries(_dbPath, pinReadSnapshot: false,
            pooling: _poolIndexConnections);
        var requestedNames = new HashSet<string>(projectNames, StringComparer.OrdinalIgnoreCase);
        List<string> variants = q.AllProjectVariants()
            .Where(variant => requestedNames.Contains(variant.ProjectName))
            .Select(variant => variant.StableVariantKey).ToList();
        List<string>? referencedVariants = ensureReferenceTo is null ? null : q.AllProjectVariants()
            .Where(variant => ensureReferenceTo.Contains(variant.ProjectName,
                StringComparer.OrdinalIgnoreCase))
            .Select(variant => variant.StableVariantKey).ToList();
        return EnsureLoadedVariantsAsync(variants, ct, referencedVariants);
    }

    public async Task<(Solution Solution, ClusterCoverage Coverage)> EnsureLoadedVariantsAsync(
        IReadOnlyCollection<string> stableVariantKeys, CancellationToken ct,
        IReadOnlyCollection<string>? ensureReferenceTo = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var q = new IndexQueries(_dbPath, pinReadSnapshot: false,
                pooling: _poolIndexConnections);
            var requested = new HashSet<string>(stableVariantKeys, StringComparer.Ordinal);
            var failedVariants = new List<string>();
            var reuseIds = new Dictionary<string, ProjectId>(StringComparer.Ordinal);

            foreach (string key in requested.Where(_loaded.ContainsKey).ToList())
            {
                LoadedProject loaded = _loaded[key];
                ProjectVariantRow? currentVariant = q.VariantByStableKey(key);
                (long, long) current = currentVariant is null
                    ? (0L, 0L)
                    : q.VariantFingerprint(currentVariant.Id);
                if (currentVariant is not null && loaded.Fingerprint == current)
                {
                    loaded.VariantId = currentVariant.Id;
                    continue;
                }
                _log($"Semantic reload (variant inputs changed): {key}");
                reuseIds[key] = loaded.Id;
                _workspace.TryApplyChanges(_workspace.CurrentSolution.RemoveProject(loaded.Id));
                _loaded.Remove(key);
            }

            List<ProjectVariantRow> requestedRows = requested
                .Select(q.VariantByStableKey).Where(row => row is not null).Cast<ProjectVariantRow>()
                .ToList();
            foreach (string missing in requested.Where(key => requestedRows.All(row =>
                         !string.Equals(row.StableVariantKey, key, StringComparison.Ordinal))))
                failedVariants.Add(missing);

            bool frameworkRefsAvailable = true;
            foreach (ProjectVariantRow variant in TopoOrder(q, requestedRows.Where(row =>
                         !_loaded.ContainsKey(row.StableVariantKey)).ToList(), ct))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    IReadOnlyList<MetadataReference> frameworkRefs =
                        ReferenceAssemblyLocator.ReferencesForTargetFramework(
                            variant.TargetFramework, out string? refSource);
                    frameworkRefsAvailable &= refSource is not null;
                    ProjectId? reuseId = reuseIds.TryGetValue(variant.StableVariantKey,
                        out ProjectId? existing) ? existing : null;
                    LoadedProject? loaded = LoadVariant(q, variant, frameworkRefs, reuseId,
                        ensureReferenceTo, ct);
                    if (loaded is null) failedVariants.Add(variant.StableVariantKey);
                    else _loaded[variant.StableVariantKey] = loaded;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"Semantic variant load failed for {variant.StableVariantKey}: {ex.Message}");
                    failedVariants.Add(variant.StableVariantKey);
                }
            }

            foreach (string key in requested)
                if (_loaded.TryGetValue(key, out LoadedProject? loaded)) loaded.LastUse = ++_useCounter;

            EvictBeyondCap(requested);
            var requestedProjects = requestedRows.Select(row => row.ProjectPath)
                .Distinct(WorkspacePaths.FileSystemPathComparer).ToList();
            var failedProjects = requestedRows.Where(row => failedVariants.Contains(
                    row.StableVariantKey, StringComparer.Ordinal))
                .Select(row => row.ProjectPath).Distinct(WorkspacePaths.FileSystemPathComparer).ToList();
            int loadedVariantCount = requested.Count(_loaded.ContainsKey);
            int loadedProjectCount = requestedRows.Where(row => _loaded.ContainsKey(row.StableVariantKey))
                .Select(row => row.ProjectPath).Distinct(WorkspacePaths.FileSystemPathComparer).Count();
            Solution solution = _workspace.CurrentSolution;
            return (solution, new ClusterCoverage(
                loadedProjectCount, requestedProjects.Count, [], failedProjects,
                frameworkRefsAvailable, solution.ProjectIds.Count,
                LoadedVariants: loadedVariantCount, RequestedVariants: requested.Count));
        }
        finally
        {
            _gate.Release();
        }
    }

    private LoadedProject? LoadVariant(IndexQueries q, ProjectVariantRow variant,
        IReadOnlyList<MetadataReference> frameworkRefs, ProjectId? reuseId,
        IReadOnlyCollection<string>? ensureReferenceTo, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PhysicalProjectParsedForTest?.Invoke(variant.ProjectPath);
        ct.ThrowIfCancellationRequested();

        List<string> files = q.VariantFiles(variant.Id);
        if (files.Count == 0) return null;
        ProjectId projectId = reuseId ?? ProjectId.CreateNewId(debugName: variant.StableVariantKey);
        var documents = new List<DocumentInfo>();
        foreach (string rel in files)
        {
            ct.ThrowIfCancellationRequested();
            string full = Path.Combine(_workspaceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            byte[]? liveBytes = GitInfo.ReadBoundedWorkspaceFile(_workspaceRoot, rel,
                DeltaRefresher.MaxIndexedFileBytes);
            SourceText text;
            if (liveBytes is not null)
            {
                using var stream = new MemoryStream(liveBytes, writable: false);
                text = SourceText.From(stream);
            }
            else
            {
                string? indexed = q.ContentByPath(rel);
                if (indexed is null) continue;
                text = SourceText.From(indexed);
            }
            documents.Add(DocumentInfo.Create(DocumentId.CreateNewId(projectId, debugName: rel),
                name: Path.GetFileName(rel), loader: TextLoader.From(TextAndVersion.Create(text,
                    VersionStamp.Create(), full)), filePath: full));
        }

        Solution solutionNow = _workspace.CurrentSolution;
        var projectReferences = new List<ProjectReference>();
        var referenceIds = new HashSet<ProjectId>();
        bool WouldCycle(ProjectId target) => ReachesId(solutionNow, target, projectId);
        foreach (ProjectVariantRow dependency in q.VariantDependencies(variant.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (_loaded.TryGetValue(dependency.StableVariantKey, out LoadedProject? loaded) &&
                referenceIds.Add(loaded.Id) && !WouldCycle(loaded.Id))
            {
                projectReferences.Add(new ProjectReference(loaded.Id));
            }
        }
        if (ensureReferenceTo is not null)
        {
            foreach (string targetKey in ensureReferenceTo)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(targetKey, variant.StableVariantKey, StringComparison.Ordinal)) continue;
                if (_loaded.TryGetValue(targetKey, out LoadedProject? loaded) &&
                    referenceIds.Add(loaded.Id) && !WouldCycle(loaded.Id))
                {
                    projectReferences.Add(new ProjectReference(loaded.Id));
                }
            }
        }

        var metadataReferences = new List<MetadataReference>(frameworkRefs);
        var metadataPaths = new HashSet<string>(WorkspacePaths.FileSystemPathComparer);
        var metadataNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PortableExecutableReference reference in frameworkRefs.OfType<PortableExecutableReference>())
        {
            if (reference.FilePath is not { Length: > 0 } path) continue;
            metadataPaths.Add(Path.GetFullPath(path));
            metadataNames.Add(Path.GetFileNameWithoutExtension(path));
        }
        foreach (VariantAssemblyReferenceInput input in q.VariantAssemblyReferenceInputs(variant.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (input.HasSelectedSourceVariant || input.HintPath is not { Length: > 0 } hint) continue;
            string full = Path.IsPathRooted(hint) ? hint : Path.Combine(_workspaceRoot,
                hint.Replace('/', Path.DirectorySeparatorChar));
            AddMetadataReference(full, input.IncludeName);
        }
        foreach (VariantPackageReferenceInput package in q.VariantPackageReferenceInputs(variant.Id))
        {
            ct.ThrowIfCancellationRequested();
            string? dll = ReferenceAssemblyLocator.ResolvePackageDll(package.Package,
                package.Version, variant.TargetFramework);
            if (dll is not null) AddMetadataReference(dll, Path.GetFileNameWithoutExtension(dll));
        }

        ParseContextRow? context = q.VariantParseContext(variant.Id);
        LanguageVersion languageVersion = LanguageVersion.Latest;
        if (context is not null && LanguageVersionFacts.TryParse(context.LanguageVersion,
                out LanguageVersion parsedLanguageVersion))
            languageVersion = parsedLanguageVersion;
        var parseOptions = new CSharpParseOptions(languageVersion,
            preprocessorSymbols: context?.PreprocessorSymbols ?? []);
        string assemblyName = NormalizeAssemblySimpleName(variant.AssemblyName);
        if (string.IsNullOrWhiteSpace(assemblyName)) assemblyName = variant.ProjectName;
        var info = ProjectInfo.Create(projectId, VersionStamp.Create(), variant.ProjectName,
            assemblyName, LanguageNames.CSharp,
            filePath: Path.Combine(_workspaceRoot,
                variant.ProjectPath.Replace('/', Path.DirectorySeparatorChar)),
            compilationOptions: CompilationOptions, parseOptions: parseOptions,
            documents: documents, projectReferences: projectReferences,
            metadataReferences: metadataReferences);
        ct.ThrowIfCancellationRequested();
        if (!_workspace.TryApplyChanges(_workspace.CurrentSolution.AddProject(info))) return null;
        return new LoadedProject
        {
            Id = projectId,
            VariantId = variant.Id,
            ProjectPath = variant.ProjectPath,
            Fingerprint = q.VariantFingerprint(variant.Id),
            LastUse = ++_useCounter,
        };

        void AddMetadataReference(string path, string assemblySimpleName)
        {
            try
            {
                string normalized = Path.GetFullPath(path);
                string simple = NormalizeAssemblySimpleName(assemblySimpleName);
                if (!File.Exists(normalized)) return;
                if (metadataPaths.Contains(normalized) || metadataNames.Contains(simple)) return;
                metadataReferences.Add(MetadataReference.CreateFromFile(normalized));
                metadataPaths.Add(normalized);
                metadataNames.Add(simple);
                MetadataReferenceAddedForTest?.Invoke(normalized);
            }
            catch (Exception) { }
        }
    }

    public ProjectId? ProjectIdForVariant(string stableVariantKey) =>
        _loaded.TryGetValue(stableVariantKey, out LoadedProject? loaded) ? loaded.Id : null;

    private static string NormalizeAssemblySimpleName(string name) =>
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static List<ProjectVariantRow> TopoOrder(IndexQueries q,
        List<ProjectVariantRow> variants, CancellationToken ct)
    {
        var byId = variants.ToDictionary(variant => variant.Id);
        var depth = new Dictionary<long, int>();
        int Depth(long id, int guard)
        {
            ct.ThrowIfCancellationRequested();
            if (guard > 64 || !byId.ContainsKey(id)) return 0;
            if (depth.TryGetValue(id, out int cached)) return cached;
            depth[id] = 0;
            int result = 0;
            foreach (ProjectVariantRow dependency in q.VariantDependencies(id))
                if (byId.ContainsKey(dependency.Id))
                    result = Math.Max(result, Depth(dependency.Id, guard + 1) + 1);
            return depth[id] = result;
        }
        return variants.OrderBy(variant => Depth(variant.Id, 0))
            .ThenBy(variant => variant.StableVariantKey, StringComparer.Ordinal).ToList();
    }

    private static bool ReachesId(Solution solution, ProjectId from, ProjectId target)
    {
        if (from == target) return true;
        var seen = new HashSet<ProjectId>();
        var stack = new Stack<ProjectId>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            ProjectId current = stack.Pop();
            if (current == target) return true;
            if (!seen.Add(current)) continue;
            Project? project = solution.GetProject(current);
            if (project is null) continue;
            foreach (ProjectReference reference in project.AllProjectReferences)
                stack.Push(reference.ProjectId);
        }
        return false;
    }

    private void EvictBeyondCap(HashSet<string> keep)
    {
        if (_loaded.Count <= MaxLoadedVariants) return;
        var referenced = _workspace.CurrentSolution.Projects.SelectMany(project =>
            project.ProjectReferences).Select(reference => reference.ProjectId).ToHashSet();
        foreach ((string key, LoadedProject loaded) in _loaded
                     .Where(pair => !keep.Contains(pair.Key) && !referenced.Contains(pair.Value.Id))
                     .OrderBy(pair => pair.Value.LastUse).Take(_loaded.Count - MaxLoadedVariants).ToList())
        {
            _workspace.TryApplyChanges(_workspace.CurrentSolution.RemoveProject(loaded.Id));
            _loaded.Remove(key);
        }
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _gate.Dispose();
    }
}
