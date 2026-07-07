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
    List<DeclarationSpan> Declarations);

public sealed record SemanticLocation(string Path, int Line, string LineText, string Project, bool IsTestProject);

public sealed record SemanticRefGroup(string Project, bool IsTestProject, int Count, List<SemanticLocation> Samples);

public sealed record SemanticReferences(
    SemanticDeclaration Symbol,
    int TotalLocations,
    List<SemanticRefGroup> Groups,
    ClusterCoverage Coverage,
    List<string> SkippedCandidateProjects);

public sealed record SemanticImplementations(
    SemanticDeclaration Symbol,
    List<SemanticDeclaration> Implementations,
    ClusterCoverage Coverage);

/// <summary>
/// Owns: exact (compiler-backed) navigation operations with deadlines — symbol
/// resolution, definitions incl. partials, FindReferences scoped to FTS-candidate
/// dependent projects, implementations. Every operation returns null on timeout or
/// resolution failure with a reason, so callers can fall back to the indexed layer.
/// Does not own: cluster loading mechanics (SemanticWorkspace) or MCP shaping.
/// </summary>
public sealed partial class SemanticService : IDisposable
{
    private readonly IndexManager _manager;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private SemanticWorkspace? _workspace;

    public SemanticService(IndexManager manager, Action<string>? log = null)
    {
        _manager = manager;
        _log = log ?? (_ => { });
    }

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
                return _workspace ??= new SemanticWorkspace(_manager.WorkspaceRoot, _manager.DbPath, _log);
            }
        }
    }

    // ---------------------------------------------------------------- definition

    public async Task<(SemanticDeclaration? Result, string? FailReason)> DefinitionAsync(
        string path, int line, int? column, string? nameHint, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 60000));
        try
        {
            var (_, symbol, _) = await LoadOwnerAndResolveAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbol is null) return (null, "symbol_not_resolved");
            return (Describe(symbol), null);
        }
        catch (OperationCanceledException)
        {
            return (null, "semantic_timeout");
        }
        catch (Exception ex)
        {
            _log($"Semantic definition failed: {ex.Message}");
            return (null, $"semantic_error:{ex.GetType().Name}");
        }
    }

    // ---------------------------------------------------------------- references

    public async Task<(SemanticReferences? Result, string? FailReason)> ReferencesAsync(
        string path, int line, int? column, string? nameHint, int maxProjects, int samplesPerGroup, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        try
        {
            // Phase 1: load the owner closure and resolve, to learn the symbol name.
            var (_, symbolA, owningProject) = await LoadOwnerAndResolveAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbolA is null || owningProject is null) return (null, "symbol_not_resolved");

            // Phase 2: load the dependent scan set and re-resolve IN that snapshot, then
            // resolve + search against the SAME solution (no snapshot drift).
            var (solution, symbol, coverage, skipped) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects, cts.Token).ConfigureAwait(false);
            if (symbol is null) return (null, "symbol_not_resolved_in_scope");

            var found = await SymbolFinder.FindReferencesAsync(symbol, solution, cts.Token).ConfigureAwait(false);

            var testFlags = ProjectTestFlags();
            var groups = new Dictionary<string, SemanticRefGroup>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (var referenced in found)
            {
                foreach (var loc in referenced.Locations)
                {
                    if (loc.Location.SourceTree is null) continue;
                    var doc = loc.Document;
                    string project = doc.Project.Name;
                    var lineSpan = loc.Location.GetLineSpan();
                    int refLine = lineSpan.StartLinePosition.Line + 1;
                    string relPath = ToRelPath(doc.FilePath ?? doc.Name);

                    total++;
                    bool isTest = testFlags.TryGetValue(project, out bool t) && t;
                    if (!groups.TryGetValue(project, out var g))
                    {
                        g = new SemanticRefGroup(project, isTest, 0, new List<SemanticLocation>());
                    }
                    var samples = g.Samples;
                    if (samples.Count < samplesPerGroup)
                    {
                        string text = (await loc.Location.SourceTree.GetTextAsync(cts.Token).ConfigureAwait(false))
                            .Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                        samples.Add(new SemanticLocation(relPath, refLine, Truncate(text), project, isTest));
                    }
                    groups[project] = g with { Count = g.Count + 1 };
                }
            }

            var result = new SemanticReferences(
                Describe(symbol),
                total,
                groups.Values.OrderByDescending(g => g.Count).ToList(),
                coverage,
                skipped);
            return (result, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "semantic_timeout");
        }
        catch (Exception ex)
        {
            _log($"Semantic references failed: {ex}");
            return (null, $"semantic_error:{ex.GetType().Name}");
        }
    }

    // ---------------------------------------------------------------- implementations

    public async Task<(SemanticImplementations? Result, string? FailReason)> ImplementationsAsync(
        string path, int line, int? column, string? nameHint, int maxProjects, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        try
        {
            var (_, symbolA, owningProject) = await LoadOwnerAndResolveAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbolA is null || owningProject is null) return (null, "symbol_not_resolved");

            var (solution, symbol, coverage, _) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects, cts.Token).ConfigureAwait(false);
            if (symbol is null) return (null, "symbol_not_resolved_in_scope");

            var results = new List<SemanticDeclaration>();
            if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface)
            {
                var impls = await SymbolFinder.FindImplementationsAsync(iface, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                results.AddRange(impls.OfType<ISymbol>().Select(Describe));
            }
            else if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } cls)
            {
                var derived = await SymbolFinder.FindDerivedClassesAsync(cls, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                results.AddRange(derived.OfType<ISymbol>().Select(Describe));
            }
            else
            {
                var impls = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                results.AddRange(impls.Select(Describe));
                var overrides = await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                results.AddRange(overrides.Select(Describe));
            }

            return (new SemanticImplementations(Describe(symbol), results, coverage), null);
        }
        catch (OperationCanceledException)
        {
            return (null, "semantic_timeout");
        }
        catch (Exception ex)
        {
            _log($"Semantic implementations failed: {ex}");
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
    private async Task<(Solution? Solution, ISymbol? Symbol, string? OwningProject)> LoadOwnerAndResolveAsync(
        string path, int line, int? column, string? nameHint, CancellationToken ct)
    {
        string relPath = path.Replace('\\', '/').TrimStart('/');
        string owningProject;
        HashSet<string> closure;
        using (var q = _manager.OpenQueries())
        {
            var owners = q.ProjectsContaining(relPath);
            if (owners.Count == 0) return (null, null, null);
            owningProject = (owners.FirstOrDefault(o => !o.IsTest) ?? owners[0]).Name;
            closure = q.DependencyClosure(new[] { owningProject });
        }

        var (solution, _) = await Workspace.EnsureLoadedAsync(closure, ct).ConfigureAwait(false);
        var symbol = await ResolveInSolutionAsync(solution, owningProject, relPath, line, column, nameHint, ct)
            .ConfigureAwait(false);
        return (solution, symbol, owningProject);
    }

    /// <summary>
    /// Resolves the symbol at (path, line[, column]) — or the declaration named nameHint
    /// on that line — against a caller-provided Solution snapshot. Does not load or mutate
    /// the workspace, so the returned symbol is valid for SymbolFinder on the same snapshot.
    /// </summary>
    private async Task<ISymbol?> ResolveInSolutionAsync(
        Solution solution, string owningProject, string relPath, int line, int? column, string? nameHint, CancellationToken ct)
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
        int position = ComputePosition(text.Lines[line - 1], column, nameHint);

        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null) return null;

        var token = root.FindToken(position);
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            var declared = model.GetDeclaredSymbol(node, ct);
            if (declared is not null &&
                (nameHint is null || declared.Name.Equals(nameHint, StringComparison.Ordinal) ||
                 token.ValueText == declared.Name))
            {
                return declared;
            }
            if (node is StatementSyntax or MemberDeclarationSyntax)
            {
                break;
            }
        }

        return await SymbolFinder.FindSymbolAtPositionAsync(document, position, ct).ConfigureAwait(false);
    }

    private static int ComputePosition(TextLine textLine, int? column, string? nameHint)
    {
        if (column is { } col && col >= 1)
        {
            return Math.Min(textLine.Start + col - 1, Math.Max(textLine.Start, textLine.End - 1));
        }
        if (nameHint is not null)
        {
            int idx = textLine.ToString().IndexOf(nameHint, StringComparison.Ordinal);
            if (idx >= 0) return textLine.Start + idx;
        }
        string s = textLine.ToString();
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return textLine.Start + Math.Min(i, Math.Max(0, s.Length - 1));
    }

    /// <summary>
    /// Shared phase-2 for dependent-scanning ops (references/implementations/callers/
    /// hierarchy): given the symbol name and owning project, loads the FTS-candidate
    /// dependent scan set and re-resolves the symbol IN the loaded snapshot. The returned
    /// symbol and solution are one consistent snapshot for SymbolFinder.
    /// </summary>
    private async Task<(Solution Solution, ISymbol? Symbol, ClusterCoverage Coverage, List<string> Skipped)> LoadScanSetAndResolveAsync(
        string symbolName, string owningProject, string path, int line, int? column, string? nameHint,
        int maxProjects, CancellationToken ct)
    {
        List<string> skipped;
        HashSet<string> scanSet;
        using (var q = _manager.OpenQueries())
        {
            var dependents = q.DependentClosure(owningProject);
            dependents.Add(owningProject);
            var candidates = q.CandidateProjectsForName(symbolName)
                .Where(c => dependents.Contains(c.Project))
                .ToList();
            var chosen = candidates.Take(Math.Clamp(maxProjects, 1, 200)).Select(c => c.Project).ToList();
            skipped = candidates.Skip(chosen.Count).Select(c => c.Project).ToList();

            scanSet = q.DependencyClosure(new[] { owningProject });
            foreach (var p in chosen) scanSet.Add(p);
        }

        var (solution, coverage) = await Workspace
            .EnsureLoadedAsync(scanSet, ct, ensureReferenceTo: new[] { owningProject })
            .ConfigureAwait(false);
        var symbol = await ResolveInSolutionAsync(
            solution, owningProject, path.Replace('\\', '/').TrimStart('/'), line, column, nameHint, ct)
            .ConfigureAwait(false);
        return (solution, symbol, coverage, skipped);
    }

    // ---------------------------------------------------------------- shaping

    private SemanticDeclaration Describe(ISymbol symbol)
    {
        var spans = new List<DeclarationSpan>();
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var lineSpan = syntaxRef.SyntaxTree.GetLineSpan(syntaxRef.Span);
            spans.Add(new DeclarationSpan(
                ToRelPath(syntaxRef.SyntaxTree.FilePath),
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.EndLinePosition.Line + 1,
                symbol.ContainingAssembly?.Name ?? ""));
        }
        return new SemanticDeclaration(
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            symbol.GetDocumentationCommentId(),
            symbol.Kind.ToString().ToLowerInvariant(),
            symbol.ContainingType?.Name,
            symbol.ContainingNamespace?.ToDisplayString(),
            symbol.ContainingAssembly?.Name,
            spans);
    }

    private Dictionary<string, bool> ProjectTestFlags()
    {
        using var q = _manager.OpenQueries();
        return q.AllProjectTestFlags();
    }

    private string ToRelPath(string fullPath)
    {
        string full = fullPath.Replace('\\', '/');
        string root = _manager.WorkspaceRoot.Replace('\\', '/').TrimEnd('/') + "/";
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : full;
    }

    private static string Truncate(string s) => s.Length <= 240 ? s : s[..240] + "…";

    public void Dispose() => _workspace?.Dispose();
}
