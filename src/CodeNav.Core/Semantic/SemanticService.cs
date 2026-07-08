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

public sealed record SemanticLocation(string Path, int Line, string LineText, string Project, bool IsTestProject);

public sealed record SemanticRefGroup(string Project, bool IsTestProject, int Count, List<SemanticLocation> Samples);

public sealed record SemanticReferences(
    SemanticDeclaration Symbol,
    int TotalLocations,
    List<SemanticRefGroup> Groups,
    ClusterCoverage Coverage,
    List<string> SkippedCandidateProjects);

/// <summary>One implementation / derived class / override, tagged for hierarchy ranking.
/// <paramref name="Via"/> names the base type that introduces the queried interface when the type
/// implements it indirectly (null = implements it directly, or not applicable to the query).</summary>
public sealed record SemanticImplementation(SemanticDeclaration Declaration, string? Via);

public sealed record SemanticImplementations(
    SemanticDeclaration Symbol,
    List<SemanticImplementation> Implementations,   // concrete (instantiable) leaves first, then abstract scaffolding
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
        string path, int line, int? column, string? nameHint, int maxProjects, int samplesPerGroup, int timeoutMs,
        bool includeGenerated = true)
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
            // When excluding generated code, drop reference locations in generated files from BOTH the
            // counts and the samples (bug wi3: the semantic path previously ignored includeGenerated).
            HashSet<string>? generatedPaths = null;
            if (!includeGenerated)
            {
                using var gq = _manager.OpenQueries();
                generatedPaths = gq.GeneratedPaths();
            }
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
                    if (generatedPaths is not null && generatedPaths.Contains(relPath)) continue;

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

            // Seed the scan-set with the projects that syntactically implement/derive the type (base-list
            // match in the index). Without this a cross-project interface — declared in a core project,
            // implemented in leaf projects — resolves to an empty list because the implementer projects
            // never entered the semantic cluster (they name the interface too rarely to rank in).
            List<string> implementerSeeds;
            using (var q = _manager.OpenQueries())
            {
                implementerSeeds = q.ImplementationCandidates(symbolA.Name, 100)
                    .SelectMany(c => q.ProjectsContaining(c.FilePath).Select(p => p.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var (solution, symbol, coverage, _) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects, cts.Token, implementerSeeds).ConfigureAwait(false);
            if (symbol is null) return (null, "symbol_not_resolved_in_scope");

            var results = new List<SemanticImplementation>();
            if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface)
            {
                var impls = await SymbolFinder.FindImplementationsAsync(iface, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                foreach (var impl in impls)
                    results.Add(new SemanticImplementation(Describe(impl), DerivationVia(impl, iface)));
            }
            else if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } cls)
            {
                var derived = await SymbolFinder.FindDerivedClassesAsync(cls, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                foreach (var d in derived)
                    results.Add(new SemanticImplementation(Describe(d), DerivationVia(d, cls)));
            }
            else
            {
                var impls = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                results.AddRange(impls.Select(s => new SemanticImplementation(Describe(s), null)));
                var overrides = await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                results.AddRange(overrides.Select(s => new SemanticImplementation(Describe(s), null)));
            }

            // Hierarchy ranking: concrete (instantiable) leaves first — the actual runtime targets —
            // then abstract scaffolding; stable by display within each tier.
            results = results
                .OrderBy(r => r.Declaration.IsAbstract ? 1 : 0)
                .ThenBy(r => r.Declaration.SymbolDisplay, StringComparer.Ordinal)
                .ToList();

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
                (nameHint is null || declared.Name.Equals(nameHint, StringComparison.Ordinal)))
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
    private async Task<(Solution Solution, ISymbol? Symbol, ClusterCoverage Coverage, List<string> Skipped)> LoadScanSetAndResolveAsync(
        string symbolName, string owningProject, string path, int line, int? column, string? nameHint,
        int maxProjects, CancellationToken ct, IReadOnlyList<string>? prioritySeeds = null)
    {
        List<string> skipped;
        HashSet<string> scanSet;
        using (var q = _manager.OpenQueries())
        {
            var dependents = q.DependentClosure(owningProject);
            dependents.Add(owningProject);
            int budget = Math.Clamp(maxProjects, 1, 200);

            var chosen = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Priority seeds (e.g. the projects that syntactically IMPLEMENT the type) load first —
            // they carry the answer even though they rank low on raw name-mention frequency (a class
            // names the interface once, while heavy consumers name it dozens of times and would
            // otherwise exhaust the budget first, leaving the implementers unloaded).
            if (prioritySeeds is not null)
                foreach (var p in prioritySeeds)
                    if (chosen.Count < budget && seen.Add(p)) chosen.Add(p);

            var candidates = q.CandidateProjectsForName(symbolName)
                .Where(c => dependents.Contains(c.Project))
                .ToList();
            foreach (var c in candidates)
                if (chosen.Count < budget && seen.Add(c.Project)) chosen.Add(c.Project);
            skipped = candidates.Select(c => c.Project).Where(p => !seen.Contains(p)).ToList();

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
