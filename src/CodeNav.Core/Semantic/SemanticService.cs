using CodeNav.Core.Indexing;
using Microsoft.CodeAnalysis;
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
            var (symbol, _, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
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
            var (symbol, declaringProject, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbol is null || declaringProject is null) return (null, "symbol_not_resolved");

            // Scope: projects that can see the symbol AND textually mention its name.
            List<string> skipped;
            HashSet<string> scanSet;
            using (var q = _manager.OpenQueries())
            {
                var dependents = q.DependentClosure(declaringProject);
                dependents.Add(declaringProject);
                var candidates = q.CandidateProjectsForName(symbol.Name)
                    .Where(c => dependents.Contains(c.Project))
                    .ToList();
                var chosen = candidates.Take(Math.Clamp(maxProjects, 1, 200)).Select(c => c.Project).ToList();
                skipped = candidates.Skip(chosen.Count).Select(c => c.Project).ToList();

                scanSet = q.DependencyClosure(new[] { declaringProject });
                foreach (var p in chosen) scanSet.Add(p);
            }

            var (solution, coverage) = await Workspace
                .EnsureLoadedAsync(scanSet, cts.Token, ensureReferenceTo: new[] { declaringProject })
                .ConfigureAwait(false);

            // Re-resolve the symbol against the (possibly reloaded) solution snapshot.
            var (symbol2, _, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            symbol = symbol2 ?? symbol;
            solution = (await Workspace.EnsureLoadedAsync(scanSet, cts.Token, new[] { declaringProject }).ConfigureAwait(false)).Solution;

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
            var (symbol, declaringProject, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbol is null || declaringProject is null) return (null, "symbol_not_resolved");

            HashSet<string> scanSet;
            using (var q = _manager.OpenQueries())
            {
                var dependents = q.DependentClosure(declaringProject);
                dependents.Add(declaringProject);
                var candidates = q.CandidateProjectsForName(symbol.Name)
                    .Where(c => dependents.Contains(c.Project))
                    .Take(Math.Clamp(maxProjects, 1, 200))
                    .Select(c => c.Project);
                scanSet = q.DependencyClosure(new[] { declaringProject });
                foreach (var p in candidates) scanSet.Add(p);
            }

            var (solution, coverage) = await Workspace
                .EnsureLoadedAsync(scanSet, cts.Token, new[] { declaringProject })
                .ConfigureAwait(false);
            var (symbol2, _, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            symbol = symbol2 ?? symbol;
            solution = (await Workspace.EnsureLoadedAsync(scanSet, cts.Token, new[] { declaringProject }).ConfigureAwait(false)).Solution;

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
    /// Resolves the symbol at (path, line[, column]) — or the declaration named nameHint
    /// on that line — loading the owning project's dependency closure first.
    /// </summary>
    private async Task<(ISymbol? Symbol, string? DeclaringProject, Document? Document)> ResolveSymbolAsync(
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
        var projectId = Workspace.LoadedProjectId(owningProject);
        if (projectId is null) return (null, owningProject, null);

        string fullPath = Path.GetFullPath(Path.Combine(_manager.WorkspaceRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var docId = solution.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault(d => d.ProjectId == projectId)
                    ?? solution.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault();
        if (docId is null) return (null, owningProject, null);
        var document = solution.GetDocument(docId);
        if (document is null) return (null, owningProject, null);

        var text = await document.GetTextAsync(ct).ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count) return (null, owningProject, document);
        var textLine = text.Lines[line - 1];

        int position;
        if (column is { } col && col >= 1)
        {
            position = Math.Min(textLine.Start + col - 1, Math.Max(textLine.Start, textLine.End - 1));
        }
        else if (nameHint is not null)
        {
            string lineText = textLine.ToString();
            int idx = lineText.IndexOf(nameHint, StringComparison.Ordinal);
            position = idx >= 0 ? textLine.Start + idx : SkipIndentation(textLine);
        }
        else
        {
            position = SkipIndentation(textLine);
        }

        // Prefer the declared symbol when the position sits on a declaration.
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (root is null || model is null) return (null, owningProject, document);

        var token = root.FindToken(position);
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            var declared = model.GetDeclaredSymbol(node, ct);
            if (declared is not null &&
                (nameHint is null || declared.Name.Equals(nameHint, StringComparison.Ordinal) ||
                 token.ValueText == declared.Name))
            {
                return (declared, owningProject, document);
            }
            if (node is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax)
            {
                break;
            }
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, ct).ConfigureAwait(false);
        return (symbol, owningProject, document);

        int SkipIndentation(TextLine tl)
        {
            string s = tl.ToString();
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return tl.Start + Math.Min(i, Math.Max(0, s.Length - 1));
        }
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
