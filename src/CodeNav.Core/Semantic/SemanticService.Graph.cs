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
    List<SemanticDeclaration> DerivedOrImplementing,
    bool ProjectModelUnproven = false);

/// <summary>
/// Owns: call-graph and type-hierarchy operations (split out of SemanticService.cs).
/// Same ownership rules: deadline-bounded, exact-confidence, null + reason on failure.
/// </summary>
public sealed partial class SemanticService
{
    public async Task<(List<SemanticCaller>? Result, ClusterCoverage? Coverage,
        List<string>? SkippedCandidateProjects, bool ProjectModelUnproven,
        string? FailReason)> CallersAsync(
        string path, int line, int? column, string? nameHint, int maxProjects, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        bool clusterLoadInProgress = true;
        var swOp = System.Diagnostics.Stopwatch.StartNew(); // field 48s gap: op wall split
        long loadMs = 0;
        var ownerBox = new SemanticWorkspace.LoadStatsBox(); // epuc.1
        var scanBox = new SemanticWorkspace.LoadStatsBox();
        try
        {
            using var indexSnapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (indexSnapshot is null)
            {
                EmitOpTelemetry("callers", "unresolved", "index_snapshot_unavailable"); // epuc.1
                return (null, null, null, false, "index_snapshot_unavailable");
            }

            var (_, symbolA, owningProject, _) = await LoadOwnerAndResolveAsync(
                path, line, column, nameHint, cts.Token, indexSnapshot.Queries,
                statsBox: ownerBox).ConfigureAwait(false);
            clusterLoadInProgress = false;
            loadMs = swOp.ElapsedMilliseconds; // review q2: progressive stamp (see references)
            if (symbolA is null || owningProject is null)
            {
                EmitOpTelemetry("callers", "unresolved", "symbol_not_resolved", ownerBox.Stats); // epuc.1
                return (null, null, null, false, "symbol_not_resolved");
            }

            clusterLoadInProgress = true;
            var (solution, symbol, coverage, skipped, _) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects,
                indexSnapshot.Queries, cts.Token, statsBox: scanBox).ConfigureAwait(false);
            clusterLoadInProgress = false;
            loadMs = swOp.ElapsedMilliseconds;
            if (symbol is null)
            {
                EmitOpTelemetry("callers", "unresolved", "symbol_not_resolved_in_scope",
                    ownerBox.Stats, scanBox.Stats); // epuc.1
                return (null, null, null, false, "symbol_not_resolved_in_scope");
            }

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
            bool projectModelUnproven = FriendAssemblyAuthorityUnproven(symbol,
                results.SelectMany(result => result.CallSites).Select(site => site.Project),
                coverage);
            EmitOpTelemetry("callers", projectModelUnproven ? "partial" : "exact",
                projectModelUnproven ? "project_model_unproven" : null,
                ownerBox.Stats, scanBox.Stats,
                clusterLoadMs: loadMs, queryMs: swOp.ElapsedMilliseconds - loadMs); // epuc.1
            return (results, coverage, skipped, projectModelUnproven, null);
        }
        catch (OperationCanceledException)
        {
            // t2b: see DefinitionAsync — a deadline dying during LOAD is warm-up, not a timeout.
            string reason = clusterLoadInProgress ? "cluster_cold_load" : "semantic_timeout";
            EmitOpTelemetry("callers", "degraded", reason, ownerBox.Stats, scanBox.Stats,
                clusterLoadMs: clusterLoadInProgress ? swOp.ElapsedMilliseconds : loadMs,
                queryMs: clusterLoadInProgress ? null : swOp.ElapsedMilliseconds - loadMs); // epuc.1
            return (null, null, null, false, reason);
        }
        catch (Exception ex)
        {
            _log($"Semantic callers failed: {ex}");
            EmitOpTelemetry("callers", "error", ex.GetType().Name, ownerBox.Stats, scanBox.Stats,
                clusterLoadMs: clusterLoadInProgress ? swOp.ElapsedMilliseconds : loadMs,
                queryMs: clusterLoadInProgress ? null : swOp.ElapsedMilliseconds - loadMs); // epuc.1
            return (null, null, null, false, $"semantic_error:{ex.GetType().Name}");
        }
    }

    public async Task<(List<SemanticCallee>? Result, bool ProjectModelUnproven,
        string? FailReason)> CalleesAsync(
        string path, int line, int? column, string? nameHint, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        bool loadCompleted = false; // t2b: cold-cluster warm-up vs real scan timeout
        var swOp = System.Diagnostics.Stopwatch.StartNew(); // field 48s gap: op wall split
        long loadMs = 0;
        var ownerBox = new SemanticWorkspace.LoadStatsBox(); // epuc.1
        try
        {
            using var indexSnapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (indexSnapshot is null)
            {
                EmitOpTelemetry("callees", "unresolved", "index_snapshot_unavailable"); // epuc.1
                return (null, false, "index_snapshot_unavailable");
            }
            var (solution, symbol, owningProject, coverage) = await LoadOwnerAndResolveAsync(
                path, line, column, nameHint, cts.Token, indexSnapshot.Queries,
                statsBox: ownerBox).ConfigureAwait(false);
            loadCompleted = true;
            loadMs = swOp.ElapsedMilliseconds;
            if (symbol is null || solution is null)
            {
                EmitOpTelemetry("callees", "unresolved", "symbol_not_resolved", ownerBox.Stats); // epuc.1
                return (null, false, "symbol_not_resolved");
            }

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
            bool projectModelUnproven = owningProject is not null && byTarget.Keys.Any(target =>
                FriendAssemblyAuthorityUnproven(target, [owningProject], coverage));
            EmitOpTelemetry("callees", projectModelUnproven ? "partial" : "exact",
                projectModelUnproven ? "project_model_unproven" : null, ownerBox.Stats,
                clusterLoadMs: loadMs, queryMs: swOp.ElapsedMilliseconds - loadMs); // epuc.1
            return (results, projectModelUnproven, null);
        }
        catch (OperationCanceledException)
        {
            // t2b: see DefinitionAsync — a deadline dying during LOAD is warm-up, not a timeout.
            string reason = loadCompleted ? "semantic_timeout" : "cluster_cold_load";
            EmitOpTelemetry("callees", "degraded", reason, ownerBox.Stats,
                clusterLoadMs: loadCompleted ? loadMs : swOp.ElapsedMilliseconds,
                queryMs: loadCompleted ? swOp.ElapsedMilliseconds - loadMs : null); // epuc.1
            return (null, false, reason);
        }
        catch (Exception ex)
        {
            _log($"Semantic callees failed: {ex}");
            // Shape parity with the two-phase ops (review q2-r2): in-load error = whole wall.
            EmitOpTelemetry("callees", "error", ex.GetType().Name, ownerBox.Stats,
                clusterLoadMs: loadCompleted ? loadMs : swOp.ElapsedMilliseconds,
                queryMs: loadCompleted ? swOp.ElapsedMilliseconds - loadMs : null); // epuc.1
            return (null, false, $"semantic_error:{ex.GetType().Name}");
        }
    }

    public async Task<(SemanticTypeHierarchy? Result, ClusterCoverage? Coverage,
        List<string>? SkippedCandidateProjects, string? FailReason)> TypeHierarchyAsync(
        string path, int line, int? column, string? nameHint, int maxProjects, int timeoutMs,
        int? arityHint = null)
    {
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120000));
        bool clusterLoadInProgress = true;
        var swOp = System.Diagnostics.Stopwatch.StartNew(); // field 48s gap: op wall split
        long loadMs = 0;
        var ownerBox = new SemanticWorkspace.LoadStatsBox(); // epuc.1
        var scanBox = new SemanticWorkspace.LoadStatsBox();
        try
        {
            using var indexSnapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (indexSnapshot is null)
            {
                EmitOpTelemetry("type_hierarchy", "unresolved", "index_snapshot_unavailable"); // epuc.1
                return (null, null, null, "index_snapshot_unavailable");
            }

            var (_, symbolA, owningProject, _) = await LoadOwnerAndResolveAsync(
                path, line, column, nameHint, cts.Token, indexSnapshot.Queries, arityHint,
                statsBox: ownerBox)
                .ConfigureAwait(false);
            clusterLoadInProgress = false;
            loadMs = swOp.ElapsedMilliseconds; // review q2: progressive stamp (see references)
            if (symbolA is null || owningProject is null)
            {
                EmitOpTelemetry("type_hierarchy", "unresolved", "symbol_not_resolved", ownerBox.Stats); // epuc.1
                return (null, null, null, "symbol_not_resolved");
            }
            if (symbolA is not INamedTypeSymbol)
            {
                EmitOpTelemetry("type_hierarchy", "unresolved", "not_a_type", ownerBox.Stats); // epuc.1
                return (null, null, null, "not_a_type");
            }

            // Implementer seeds, same as ImplementationsAsync (field 0.7.0: type_hierarchy showed
            // 8 exact hits with coverage 1/1 — the hits were RESIDUE from a prior implementations
            // call's loads, not this call's own discovery; unseeded, a cold type_hierarchy on a
            // cross-project interface finds nothing). Seeding makes it self-sufficient AND makes
            // coverage describe the projects this answer actually needed.
            // jj1q: transitive-closure seeds (see ImplementationsAsync — level-1 seeding
            // was a correctness hole for deep chains, not just a perf ceiling).
            var typeA = (INamedTypeSymbol)symbolA;
            var swClosure = System.Diagnostics.Stopwatch.StartNew();
            var typeClosure = indexSnapshot.Queries.TransitiveImplementationClosure(
                typeA.Name, typeA.Arity, out bool closureCapped, cancellationToken: cts.Token);
            long closureMs = swClosure.ElapsedMilliseconds;
            List<string> implementerSeeds = closureCapped
                ? indexSnapshot.Queries.ImplementationCandidateProjects(symbolA.Name, cts.Token)
                : ClosureSeedProjects(indexSnapshot.Queries, typeClosure);

            clusterLoadInProgress = true;
            var (solution, symbol, coverage, skipped, _) = await LoadScanSetAndResolveAsync(
                symbolA.Name, owningProject, path, line, column, nameHint, maxProjects,
                indexSnapshot.Queries, cts.Token, implementerSeeds, arityHint,
                statsBox: scanBox).ConfigureAwait(false);
            clusterLoadInProgress = false;
            loadMs = swOp.ElapsedMilliseconds;
            if (symbol is not INamedTypeSymbol type)
            {
                string why = symbol is null ? "symbol_not_resolved_in_scope" : "not_a_type";
                EmitOpTelemetry("type_hierarchy", "unresolved", why, ownerBox.Stats, scanBox.Stats); // epuc.1
                return (null, null, null, why);
            }

            var baseSymbols = new List<INamedTypeSymbol>();
            var baseTypes = new List<SemanticDeclaration>();
            for (var b = type.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            {
                baseSymbols.Add(b);
                baseTypes.Add(Describe(b));
            }
            var interfaceSymbols = type.AllInterfaces.ToList();
            var interfaces = interfaceSymbols.Select(i => Describe((ISymbol)i)).ToList();

            var down = new List<SemanticDeclaration>();
            // jj1q: closure-verified downward direction — same walk as implementations (the
            // global SymbolFinder scan materialized all loaded compilations; field row 83 was
            // type_hierarchy blowing its deadline on exactly this).
            object? queryStages;
            if (!closureCapped)
            {
                var verification = await VerifyClosureImplementersAsync(type, solution,
                    typeClosure,
                    impl => down.Add(Describe(impl)),
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
                queryStages = new { path = "exhaustive_fallback", closureCapped };
            }

            // Review r2: materialize BEFORE the emit — see DefinitionAsync.
            string accessingAssembly = type.ContainingAssembly?.Name ?? owningProject;
            bool projectModelUnproven =
                baseSymbols.Cast<ISymbol>().Concat(interfaceSymbols).Any(baseOrInterface =>
                    FriendAssemblyAuthorityUnproven(baseOrInterface, [accessingAssembly], coverage)) ||
                FriendAssemblyAuthorityUnproven(type,
                    down.Select(candidate => candidate.Assembly ?? ""), coverage);
            var payload = new SemanticTypeHierarchy(
                Describe(type), baseTypes, interfaces, down, projectModelUnproven);
            EmitOpTelemetry("type_hierarchy", projectModelUnproven ? "partial" : "exact",
                projectModelUnproven ? "project_model_unproven" : null,
                ownerBox.Stats, scanBox.Stats,
                clusterLoadMs: loadMs, queryMs: swOp.ElapsedMilliseconds - loadMs,
                queryStages: queryStages); // epuc.1 + jj1q stages
            return (payload, coverage, skipped, null);
        }
        catch (OperationCanceledException)
        {
            // t2b: see DefinitionAsync — a deadline dying during LOAD is warm-up, not a timeout.
            string reason = clusterLoadInProgress ? "cluster_cold_load" : "semantic_timeout";
            EmitOpTelemetry("type_hierarchy", "degraded", reason, ownerBox.Stats, scanBox.Stats,
                clusterLoadMs: clusterLoadInProgress ? swOp.ElapsedMilliseconds : loadMs,
                queryMs: clusterLoadInProgress ? null : swOp.ElapsedMilliseconds - loadMs); // epuc.1
            return (null, null, null, reason);
        }
        catch (Exception ex)
        {
            _log($"Semantic type hierarchy failed: {ex}");
            EmitOpTelemetry("type_hierarchy", "error", ex.GetType().Name, ownerBox.Stats, scanBox.Stats,
                clusterLoadMs: clusterLoadInProgress ? swOp.ElapsedMilliseconds : loadMs,
                queryMs: clusterLoadInProgress ? null : swOp.ElapsedMilliseconds - loadMs); // epuc.1 review F3
            return (null, null, null, $"semantic_error:{ex.GetType().Name}");
        }
    }
}
