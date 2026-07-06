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
            var (symbol, declaringProject, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbol is null || declaringProject is null) return (null, null, "symbol_not_resolved");

            HashSet<string> scanSet;
            using (var q = _manager.OpenQueries())
            {
                var dependents = q.DependentClosure(declaringProject);
                dependents.Add(declaringProject);
                var chosen = q.CandidateProjectsForName(symbol.Name)
                    .Where(c => dependents.Contains(c.Project))
                    .Take(Math.Clamp(maxProjects, 1, 200))
                    .Select(c => c.Project);
                scanSet = q.DependencyClosure(new[] { declaringProject });
                foreach (var p in chosen) scanSet.Add(p);
            }

            var (solution, coverage) = await Workspace
                .EnsureLoadedAsync(scanSet, cts.Token, new[] { declaringProject })
                .ConfigureAwait(false);
            var (symbol2, _, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            symbol = symbol2 ?? symbol;
            solution = (await Workspace.EnsureLoadedAsync(scanSet, cts.Token, new[] { declaringProject }).ConfigureAwait(false)).Solution;

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
            var (symbol, declaringProject, document) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbol is null || document is null) return (null, "symbol_not_resolved");

            var byTarget = new Dictionary<ISymbol, List<int>>(SymbolEqualityComparer.Default);
            foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                var node = await syntaxRef.GetSyntaxAsync(cts.Token).ConfigureAwait(false);
                var declDoc = document.Project.Solution.GetDocument(syntaxRef.SyntaxTree);
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
            var (symbol, declaringProject, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            if (symbol is not INamedTypeSymbol type || declaringProject is null)
            {
                return (null, null, symbol is null ? "symbol_not_resolved" : "not_a_type");
            }

            HashSet<string> scanSet;
            using (var q = _manager.OpenQueries())
            {
                var dependents = q.DependentClosure(declaringProject);
                dependents.Add(declaringProject);
                var chosen = q.CandidateProjectsForName(type.Name)
                    .Where(c => dependents.Contains(c.Project))
                    .Take(Math.Clamp(maxProjects, 1, 200))
                    .Select(c => c.Project);
                scanSet = q.DependencyClosure(new[] { declaringProject });
                foreach (var p in chosen) scanSet.Add(p);
            }
            var (solution, coverage) = await Workspace
                .EnsureLoadedAsync(scanSet, cts.Token, new[] { declaringProject })
                .ConfigureAwait(false);
            var (symbol2, _, _) = await ResolveSymbolAsync(path, line, column, nameHint, cts.Token).ConfigureAwait(false);
            type = symbol2 as INamedTypeSymbol ?? type;
            solution = (await Workspace.EnsureLoadedAsync(scanSet, cts.Token, new[] { declaringProject }).ConfigureAwait(false)).Solution;

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
