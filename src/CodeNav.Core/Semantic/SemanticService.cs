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
    // kbn: textual candidates OUTSIDE the dependency closure (plugins, config-wired consumers) —
    // previously dropped without a trace; capped at 20.
    List<string>? OutOfGraphCandidates = null);

/// <summary>One implementation / derived class / override, tagged for hierarchy ranking.
/// <paramref name="Via"/> names the base type that introduces the queried interface when the type
/// implements it indirectly (null = implements it directly, or not applicable to the query).</summary>
public sealed record SemanticImplementation(SemanticDeclaration Declaration, string? Via);

public sealed record SemanticImplementations(
    SemanticDeclaration Symbol,
    List<SemanticImplementation> Implementations,   // concrete (instantiable) leaves first, then abstract scaffolding
    ClusterCoverage Coverage,
    bool DeadlineExhausted = false);                // 24n: deadline fired mid-search — list is a lower bound

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

    /// <summary>TEST SEAM (tof): invoked once per counted reference location with the running
    /// total. Lets a test throw OperationCanceledException mid-count to exercise the 24n salvage
    /// branch deterministically — a real deadline landing inside the loop is not reproducible on
    /// demand (the seam gap the salvage shipped with). Never set in production; instance-scoped
    /// so parallel tests cannot cross-trip it.</summary>
    internal Action<int>? TestOnlyPerLocationCounted;

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
        bool includeGenerated = true, IReadOnlySet<string>? usageKinds = null, bool publicConsumersOnly = false,
        bool includeTests = true)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        try
        {
            // Phase 1: load the owner closure and resolve, to learn the symbol name.
            var (_, symbolA, owningProject) = await LoadOwnerAndResolveAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbolA is null || owningProject is null) return (null, "symbol_not_resolved");

            // Implementer seeds for TYPE targets — parity with Implementations/TypeHierarchy
            // (field 0.7.2 regression report): references was the ONLY exact tool relying purely
            // on graph edges for candidate discovery, so any edge gap (e.g. the paired-declarer
            // collision) made it load 1/1 and return an "exact" zero while the seeded tools found
            // all 8. Base-list implementer projects carry usages BY DEFINITION; seed them first.
            List<string>? implementerSeeds = null;
            if (symbolA is INamedTypeSymbol)
            {
                using var q = _manager.OpenQueries();
                implementerSeeds = q.ImplementationCandidates(symbolA.Name, 100)
                    .SelectMany(c => q.ProjectsContaining(c.FilePath).Select(p => p.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Phase 2: load the dependent scan set and re-resolve IN that snapshot, then
            // resolve + search against the SAME solution (no snapshot drift).
            var (solution, symbol, coverage, skipped, outOfGraph) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects, cts.Token, implementerSeeds).ConfigureAwait(false);
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
                foreach (var loc in referenced.Locations)
                {
                    if (loc.Location.SourceTree is null) continue;
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
                        rootNode = await loc.Location.SourceTree.GetRootAsync(cts.Token).ConfigureAwait(false);
                        rootCache[loc.Location.SourceTree] = rootNode;
                    }
                    string kind = SemanticReferenceKinds.Classify(rootNode, loc.Location.SourceSpan.Start, symbolIsType);
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
                        string text = (await loc.Location.SourceTree.GetTextAsync(cts.Token).ConfigureAwait(false))
                            .Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                        samples.Add(new SemanticLocation(relPath, refLine, Truncate(text), project, isTest, kind));
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

            var result = new SemanticReferences(
                Describe(symbol),
                total,
                groups.Values.OrderByDescending(g => g.Count).ToList(),
                coverage,
                skipped,
                kindCounts,
                deadlineExhausted,
                outOfGraph.Count > 0 ? outOfGraph : null);
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

            var (solution, symbol, coverage, _, _) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects, cts.Token, implementerSeeds).ConfigureAwait(false);
            if (symbol is null) return (null, "symbol_not_resolved_in_scope");

            var results = new List<SemanticImplementation>();
            bool deadlineExhausted = false;
            // Salvage wrapper (24n): a deadline firing after SOME finder pass completed (e.g. the
            // member path's implementations found, overrides not yet) keeps the found portion as a
            // lower bound. A deadline before anything was found still exits via the outer catch.
            try
            {
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

            return (new SemanticImplementations(Describe(symbol), results, coverage, deadlineExhausted), null);
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
    private async Task<(Solution Solution, ISymbol? Symbol, ClusterCoverage Coverage, List<string> Skipped, List<string> OutOfGraph)> LoadScanSetAndResolveAsync(
        string symbolName, string owningProject, string path, int line, int? column, string? nameHint,
        int maxProjects, CancellationToken ct, IReadOnlyList<string>? prioritySeeds = null)
    {
        List<string> skipped;
        var outOfGraph = new List<string>();
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

            // kbn: textual candidates OUTSIDE the dependency closure (and not seed-chosen) were
            // dropped SILENTLY — not even in `skipped`. Post-lhg the closure covers assembly-ref
            // consumers, so this residue is reflection-loaded plugins / config-wired consumers:
            // projects that mention the name but have no graph path to the declarer. Report them
            // (capped) so a caller can see there was textual smoke beyond the graph.
            var candidates = new List<(string Project, int FileCount)>();
            foreach (var c in q.CandidateProjectsForName(symbolName))
            {
                if (dependents.Contains(c.Project)) candidates.Add(c);
                else if (!seen.Contains(c.Project) && outOfGraph.Count < 20) outOfGraph.Add(c.Project);
            }
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
        return (solution, symbol, coverage, skipped, outOfGraph);
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
