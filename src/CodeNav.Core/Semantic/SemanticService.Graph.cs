using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CodeNav.Core.Semantic;

public sealed record SemanticCaller(SemanticDeclaration Caller, List<SemanticLocation> CallSites);

public sealed record SemanticCallee(SemanticDeclaration Callee, List<int> CallLines);

public sealed record SemanticTypeHierarchy(
    SemanticDeclaration Symbol,
    List<SemanticDeclaration> BaseTypes,
    List<SemanticDeclaration> Interfaces,
    List<SemanticDeclaration> DerivedOrImplementing);

/// <summary>
/// Owns: call-graph and type-hierarchy operations (split out of SemanticService.cs).
/// Same ownership rules: deadline-bounded, exact-confidence, null + reason on failure.
/// </summary>
public sealed partial class SemanticService
{
    public async Task<(List<SemanticCaller>? Result, ClusterCoverage? Coverage, string? FailReason)> CallersAsync(
        string path, int line, int? column, string? nameHint, int maxProjects, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        try
        {
            var (_, symbolA, owningProject) = await LoadOwnerAndResolveAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbolA is null || owningProject is null) return (null, null, "symbol_not_resolved");

            var (solution, symbol, coverage, _, _) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects, cts.Token).ConfigureAwait(false);
            if (symbol is null) return (null, null, "symbol_not_resolved_in_scope");

            var callers = await SymbolFinder.FindCallersAsync(symbol, solution, cts.Token).ConfigureAwait(false);
            var testFlags = ProjectTestFlags();
            var results = new List<SemanticCaller>();
            foreach (var info in callers.Where(c => c.IsDirect))
            {
                var sites = new List<SemanticLocation>();
                foreach (var loc in info.Locations.Where(l => l.IsInSource).Take(5))
                {
                    var lineSpan = loc.GetLineSpan();
                    string relPath = ToRelPath(lineSpan.Path);
                    string text = (await loc.SourceTree!.GetTextAsync(cts.Token).ConfigureAwait(false))
                        .Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                    string project = solution.GetDocument(loc.SourceTree)?.Project.Name ?? "";
                    sites.Add(new SemanticLocation(relPath, lineSpan.StartLinePosition.Line + 1, Truncate(text),
                        project, testFlags.TryGetValue(project, out bool t) && t));
                }
                results.Add(new SemanticCaller(Describe(info.CallingSymbol), sites));
            }
            return (results, coverage, null);
        }
        catch (OperationCanceledException)
        {
            return (null, null, "semantic_timeout");
        }
        catch (Exception ex)
        {
            _log($"Semantic callers failed: {ex}");
            return (null, null, $"semantic_error:{ex.GetType().Name}");
        }
    }

    public async Task<(List<SemanticCallee>? Result, string? FailReason)> CalleesAsync(
        string path, int line, int? column, string? nameHint, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        try
        {
            var (solution, symbol, _) = await LoadOwnerAndResolveAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbol is null || solution is null) return (null, "symbol_not_resolved");

            var byTarget = new Dictionary<ISymbol, List<int>>(SymbolEqualityComparer.Default);
            foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                var node = await syntaxRef.GetSyntaxAsync(cts.Token).ConfigureAwait(false);
                var declDoc = solution.GetDocument(syntaxRef.SyntaxTree);
                if (declDoc is null) continue;
                var model = await declDoc.GetSemanticModelAsync(cts.Token).ConfigureAwait(false);
                if (model is null) continue;

                foreach (var call in node.DescendantNodes().Where(n =>
                             n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax))
                {
                    var target = model.GetSymbolInfo(call, cts.Token).Symbol
                                 ?? model.GetSymbolInfo(call, cts.Token).CandidateSymbols.FirstOrDefault();
                    if (target is null) continue;
                    if (target.Kind is not (SymbolKind.Method or SymbolKind.Property)) continue;
                    int callLine = syntaxRef.SyntaxTree.GetLineSpan(call.Span).StartLinePosition.Line + 1;
                    (byTarget.TryGetValue(target, out var lines) ? lines : byTarget[target] = new()).Add(callLine);
                }
            }

            var results = byTarget
                .Select(kv => new SemanticCallee(Describe(kv.Key), kv.Value.Distinct().Take(8).ToList()))
                .OrderBy(c => c.Callee.SymbolDisplay, StringComparer.Ordinal)
                .ToList();
            return (results, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "semantic_timeout");
        }
        catch (Exception ex)
        {
            _log($"Semantic callees failed: {ex}");
            return (null, $"semantic_error:{ex.GetType().Name}");
        }
    }

    public async Task<(SemanticTypeHierarchy? Result, ClusterCoverage? Coverage, string? FailReason)> TypeHierarchyAsync(
        string path, int line, int? column, string? nameHint, int maxProjects, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        try
        {
            var (_, symbolA, owningProject) = await LoadOwnerAndResolveAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbolA is null || owningProject is null) return (null, null, "symbol_not_resolved");
            if (symbolA is not INamedTypeSymbol) return (null, null, "not_a_type");

            // Implementer seeds, same as ImplementationsAsync (field 0.7.0: type_hierarchy showed
            // 8 exact hits with coverage 1/1 — the hits were RESIDUE from a prior implementations
            // call's loads, not this call's own discovery; unseeded, a cold type_hierarchy on a
            // cross-project interface finds nothing). Seeding makes it self-sufficient AND makes
            // coverage describe the projects this answer actually needed.
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
            if (symbol is not INamedTypeSymbol type)
            {
                return (null, null, symbol is null ? "symbol_not_resolved_in_scope" : "not_a_type");
            }

            var baseTypes = new List<SemanticDeclaration>();
            for (var b = type.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            {
                baseTypes.Add(Describe(b));
            }
            var interfaces = type.AllInterfaces.Select(i => Describe((ISymbol)i)).ToList();

            var down = new List<SemanticDeclaration>();
            if (type.TypeKind == TypeKind.Interface)
            {
                var impls = await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: cts.Token).ConfigureAwait(false);
                down.AddRange(impls.OfType<ISymbol>().Select(Describe));
            }
            else
            {
                var derived = await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cts.Token).ConfigureAwait(false);
                down.AddRange(derived.OfType<ISymbol>().Select(Describe));
            }

            return (new SemanticTypeHierarchy(Describe(type), baseTypes, interfaces, down), coverage, null);
        }
        catch (OperationCanceledException)
        {
            return (null, null, "semantic_timeout");
        }
        catch (Exception ex)
        {
            _log($"Semantic type hierarchy failed: {ex}");
            return (null, null, $"semantic_error:{ex.GetType().Name}");
        }
    }
}
