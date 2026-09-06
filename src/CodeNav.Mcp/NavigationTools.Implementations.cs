using System.ComponentModel;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

public sealed partial class NavigationTools
{
    [McpServerTool(Name = "implementations")]
    [Description("Implementations of an interface/type or dispatch member, ranked concrete-first with derivation path via. C# accepts stable documentationCommentId, generic arity, symbolId, name, or position and may use the documented heuristic fallback. A C# documentationCommentId is semantic-only and never falls back to a name lookup. C# semantic owner sets are assembly-name-keyed; only documentationCommentId requests disclose non-pair collisions through nameKeyedOwnerCollisionGroups. F# is compiler-semantic and position-only for compile-owned .fs/.fsi: it scans the selected ProjectReference closure, distinct declaring projects, and proven workspace dependents across applicable TFMs, returning named implementers, derived types, exact overrides, and typed object expressions with implementationKind and honest lower-bound coverage. F# rejects name/arity/symbolId/documentationCommentId/positive maxProjects; projectPath, targetFramework, includeTests, and includeGenerated are F#-only. A single concrete implementation is likelyImplementation only for a complete, untruncated result with exact semantic confidence.")]
    public string Implementations(
        [Description("Interface/type/member name. Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of the declaration or a usage (position mode).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("Candidate-project budget; 0 (default) loads all matching projects, while a positive value opts into a bound.")] int maxProjects = SemanticService.DefaultCandidateProjectBudget,
        [Description("Semantic deadline in ms (default 15000, max 120000).")] int timeoutMs = 15000,
        [Description("Optional generic type-parameter count. Use 0 for a non-generic declaration, 1 for Foo<T>, etc. A bare name with multiple available arities is refused.")] int? arity = null,
        [Description("Resolve by a search_symbol candidate's current idx: handle. Takes precedence over name, path+line, and arity. Operator handles return unsupported_symbol_kind.")] string? symbolId = null,
        [Description("Stable C# Roslyn declaration id (T:/M:/P:/E:; constructors, fields, and operators are unsupported here). Empty means omitted; whitespace-only is bad_request. Mutually exclusive with other selectors. A successful response echoes the compiler-canonical id, which is safe to reuse directly; failures echo a bounded form of the caller input.")] string? documentationCommentId = null,
        [Description("F# position mode only: workspace-relative physical .fsproj path. Required with targetFramework when the file has more than one type-check context.")] string? projectPath = null,
        [Description("F# position mode only: exact target framework. Required with projectPath when the file has more than one type-check context.")] string? targetFramework = null,
        [Description("F# position mode only: include implementations in test projects (default true).")] bool includeTests = true,
        [Description("F# position mode only: include implementations in generated files (default false).")] bool includeGenerated = false)
    {
        if (NotReady() is { } notReady) return notReady;
        if (NormalizeDocumentationCommentId(ref documentationCommentId) is { } idError)
            return idError;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = NormalizePath(path);
            FileHit? indexedFile;
            bool compileOwnedFSharp = false;
            using (var languageQueries = _manager.OpenQueries())
            {
                indexedFile = languageQueries.FileByPath(path);
                if (indexedFile is { Language: "fs" } &&
                    !IsFSharpScriptPath(path) &&
                    (Path.GetExtension(path).Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(path).Equals(".fsi", StringComparison.OrdinalIgnoreCase)))
                {
                    compileOwnedFSharp = languageQueries.ProjectsContaining(path)
                        .Any(project => project.Language == "fs");
                }
            }
            if (indexedFile is { Language: "fs" })
            {
                if (!compileOwnedFSharp)
                    return UnsupportedLanguage(path, indexedFile.Language,
                        "implementations");
                if (line <= 0 || column <= 0)
                {
                    return Json.Serialize(new
                    {
                        error = "fsharp_semantic_position_required",
                        operation = "implementations",
                        detail = "F# implementations requires an explicit path + line + column; bare-name and line-only resolution are unavailable.",
                    });
                }
                string? incompatibleField = name is { Length: > 0 }
                    ? "name"
                    : arity is not null
                        ? "arity"
                        : symbolId is { Length: > 0 }
                            ? "symbolId"
                            : documentationCommentId is { Length: > 0 }
                                ? "documentationCommentId"
                                : maxProjects > 0
                                    ? "maxProjects"
                                    : null;
                if (incompatibleField is not null)
                {
                    return Json.Serialize(new
                    {
                        error = "bad_request",
                        field = incompatibleField,
                        reason = "incompatible_filter",
                        expected = incompatibleField == "maxProjects"
                            ? "0"
                            : $"omit {incompatibleField}",
                        operation = "implementations",
                        detail = "F# implementations is position-only and does not apply C# selectors or a candidate-project cap.",
                        meta = FSharpSemanticMeta(_manager.Health(), "bad_request"),
                    });
                }
                return FSharpImplementations(path, line, column, projectPath,
                    targetFramework, includeTests, includeGenerated, timeoutMs);
            }
        }
        string? incompatibleCSharpField = projectPath is { Length: > 0 }
            ? "projectPath"
            : targetFramework is { Length: > 0 }
                ? "targetFramework"
                : !includeTests
                    ? "includeTests"
                    : includeGenerated
                        ? "includeGenerated"
                        : null;
        if (incompatibleCSharpField is not null)
        {
            return Json.Serialize(new
            {
                error = "bad_request",
                field = incompatibleCSharpField,
                reason = "incompatible_filter",
                expected = incompatibleCSharpField == "includeTests"
                    ? "true"
                    : incompatibleCSharpField == "includeGenerated"
                        ? "false"
                        : $"omit {incompatibleCSharpField}",
                operation = "implementations",
                detail = "projectPath, targetFramework, includeTests, and includeGenerated are F# position-mode arguments and are not ignored for C# requests.",
                meta = Meta.From(_manager.Health(), "indexed", "syntax"),
            });
        }
        int deadlineMs = Math.Clamp(timeoutMs, 500, SemanticNavigationDeadlineMaxMs);
        var swSem = System.Diagnostics.Stopwatch.StartNew();
        var coldStartTiming = new SemanticColdStartTimingBox();
        using var semanticDeadline = new CancellationTokenSource(deadlineMs);
        string? requestedDocumentationCommentId = null;
        DocumentationIdResolutionCoverage? documentationIdCoverage = null;
        IndexSnapshotIdentity? documentationIdSnapshotIdentity = null;
        long? documentationIdResolutionMs = null;
        if (!string.IsNullOrEmpty(documentationCommentId))
        {
            if (symbolId is { Length: > 0 } || name is { Length: > 0 } ||
                path is { Length: > 0 } || line > 0 || column > 0 || arity is not null)
            {
                return Json.Serialize(new
                {
                    error = "bad_request",
                    field = "documentationCommentId",
                    detail = "documentationCommentId is mutually exclusive with symbolId, name, path, line, column, and arity.",
                });
            }
            var (documentedTarget, documentedError) = ResolveDocumentationTarget(
                documentationCommentId, "implementations", deadlineMs,
                implementationsOnly: true, SemanticNavigationDeadlineMaxMs,
                semanticDeadline.Token, swSem, coldStartTiming);
            if (documentedError is not null) return documentedError;
            requestedDocumentationCommentId = documentedTarget!.CanonicalDocumentationCommentId;
            documentationIdCoverage = documentedTarget.Coverage;
            documentationIdSnapshotIdentity = documentedTarget.SnapshotIdentity;
            documentationIdResolutionMs = documentedTarget.ResolutionElapsedMs;
            name = documentedTarget!.Name;
            path = documentedTarget.Path;
            line = documentedTarget.Line;
            column = documentedTarget.Column;
        }
        if (symbolId is not { Length: > 0 } &&
            UnsupportedLanguageAtPath(path, "implementations") is { } unsupportedLanguage)
            return unsupportedLanguage;
        var (selection, selectionError) = ResolveArityTarget(
            name, path, line, column, arity, symbolId, typeOnly: false);
        if (selectionError is not null) return selectionError;
        name = selection!.Name;
        path = selection.Path;
        line = selection.Line;
        column = selection.Column;
        arity = selection.Arity;
        bool allowHeuristicFallback = requestedDocumentationCommentId is null &&
                                      selection.AllowHeuristicFallback;

        string? failReason = null;
        // dve (b): when the compiler RESOLVED the symbol but found no implementers, the fallback
        // used to discard that exact identity and relabel EVERYTHING heuristic — keep it for
        // mixed-section honesty (symbol exact, list heuristic), mirroring type_hierarchy's
        // derivedConfidence split.
        SemanticDeclaration? resolvedSymbol = null;
        var (target, hint) = ResolveSemanticTarget(name, null, null, path, line, column, arity);
        if (target is { } t)
        {
            SemanticImplementations? result;
            string? reason;
            if (TestOnlySemanticFailureReason is { } forcedFailure)
            {
                result = null;
                reason = forcedFailure;
                SemanticColdStartTiming timing = coldStartTiming.Publish(
                    swSem.ElapsedMilliseconds);
                _semantic.EmitTerminalTelemetry(
                    "implementations", "degraded", forcedFailure, timing);
            }
            else
            {
                (result, reason) = _semantic
                    .ImplementationsAsync(t.Path, t.Line, t.Column, hint, maxProjects,
                        RemainingDeadlineMilliseconds(deadlineMs, swSem), arity,
                        semanticDeadline.Token, documentationIdSnapshotIdentity,
                        coldStartTiming)
                    .GetAwaiter().GetResult();
            }
            if (result is not null && TestOnlyImplementationsResultTransform is not null)
                result = TestOnlyImplementationsResultTransform(result);
            resolvedSymbol = result?.Symbol;
            if (result is not null && requestedDocumentationCommentId is not null &&
                !string.Equals(result.Symbol.DocumentationCommentId,
                    requestedDocumentationCommentId, StringComparison.Ordinal))
            {
                return DocumentationIdIdentityMismatch(documentationCommentId!,
                    requestedDocumentationCommentId,
                    result.Symbol.DocumentationCommentId,
                    "implementations",
                    documentationIdCoverage,
                    deadlineMs,
                    swSem,
                    documentationIdResolutionMs,
                    coldStartTiming.Timing);
            }
            if (result is { Implementations.Count: > 0 })
            {
                var impls = result.Implementations; // already ranked concrete-first by the semantic layer
                int concreteCount = impls.Count(r => !r.Declaration.IsAbstract);
                bool exhausted = result.DeadlineExhausted;
                bool unsupportedLanguageSkipped = result.Coverage.SkippedProjects.Count > 0;
                bool candidateBounded = result.SkippedCandidateProjects.Count > 0;
                bool coverageGap = result.Coverage.LoadedProjects <
                    result.Coverage.RequestedProjects;
                bool loadIncomplete = result.Coverage.FailedProjects.Count > 0 ||
                    (coverageGap && !candidateBounded && !unsupportedLanguageSkipped);
                bool partial = exhausted || unsupportedLanguageSkipped || candidateBounded ||
                    loadIncomplete || result.ProjectModelUnproven;
                string? partialCause = SemanticCoverageReasons.Primary(result.Coverage,
                    exhausted, candidateBounded,
                    projectModelUnproven: result.ProjectModelUnproven);
                var meta0 = Meta.From(_manager.Health(),
                    unsupportedLanguageSkipped || loadIncomplete || result.ProjectModelUnproven
                        ? "indexed"
                        : "exact", "semantic");
                long elapsedMs = swSem.ElapsedMilliseconds;
                string? likelyImplementation = concreteCount == 1 && !partial
                    ? impls.First(r => !r.Declaration.IsAbstract).Declaration.SymbolDisplay
                    : null;
                string BuildImplementations(string? likely, bool likelyOmitted) =>
                    Json.WithAuxiliaryListBudget(impls, result.SkippedCandidateProjects,
                    (items, truncated, skippedItems, skippedTruncated) => new
                    {
                        symbol = SemanticSymbolJson(result.Symbol),
                        documentationCommentId = requestedDocumentationCommentId,
                        documentationIdCoverage = documentationIdCoverage is null
                            ? null
                            : DocumentationIdCoverageJson(documentationIdCoverage),
                        implementations = items.Select(r => new
                        {
                            symbol = SemanticSymbolJson(r.Declaration),
                            isAbstract = r.Declaration.IsAbstract ? true : (bool?)null, // omitted when concrete
                            rank = r.Declaration.IsAbstract ? "abstract" : "concrete", // make the ranking legible to the model
                            via = r.Via, // the base type that introduces the interface, when implemented indirectly
                        }),
                        concreteCount,
                        // High-signal case: exactly one instantiable implementation is very likely THE
                        // runtime type; anything else is abstract scaffolding. Never claimed when the
                        // deadline cut the search short — the "one" may just be the one found in time.
                        likelyImplementation = likely,
                        likelyImplementationTruncated = likelyOmitted ? true : (bool?)null,
                        likelyImplementationBytes = likelyOmitted && likelyImplementation is not null
                            ? Json.Utf8Bytes(likelyImplementation)
                            : (int?)null,
                        coverage = CoverageJson(result.Coverage),
                        skippedCandidateProjects = skippedItems.Count > 0
                        ? skippedItems
                        : null,
                        skippedCandidateProjectCount = result.SkippedCandidateProjects.Count > 0
                        ? result.SkippedCandidateProjects.Count
                        : (int?)null,
                        skippedCandidateProjectsTruncated = skippedTruncated ? true : (bool?)null,
                        partial = partial ? true : (bool?)null,
                        partialReason = partialCause,
                        retryRecommended = SemanticRetryRecommended(partialCause)
                            ? true
                            : (bool?)null,
                        retryHint = SemanticRetryHint(partialCause, "implementations",
                            deadlineMs, SemanticNavigationDeadlineMaxMs),
                        // t2b: where the budget went — cluster load+resolve vs the finder passes.
                        timing = new
                        {
                            deadlineMs,
                            elapsedMs,
                            documentationIdResolutionMs,
                            clusterLoadMs = result.ClusterLoadMs,
                            queryMs = result.QueryMs,
                            semanticColdStart = coldStartTiming.Timing,
                        },
                        truncated,
                        hint = concreteCount == 1 && !partial
                        ? "One concrete implementation — likely the runtime target. Ranked concrete-first; isAbstract/rank mark non-instantiable scaffolding."
                        : null,
                        meta = meta0,
                    });
                string response = BuildImplementations(likelyImplementation,
                    likelyOmitted: false);
                if (Json.Utf8Bytes(response) > Json.HardBudgetBytes &&
                    likelyImplementation is not null)
                {
                    response = BuildImplementations(null, likelyOmitted: true);
                }
                return Json.WithCompleteSemanticIdentity(response);
            }
            if (requestedDocumentationCommentId is not null)
            {
                if (result is not null)
                {
                    bool partial = result.DeadlineExhausted ||
                                   result.Coverage.SkippedProjects.Count > 0 ||
                                   result.Coverage.FailedProjects.Count > 0 ||
                                   result.Coverage.LoadedProjects <
                                   result.Coverage.RequestedProjects ||
                                   result.SkippedCandidateProjects.Count > 0 ||
                                   result.ProjectModelUnproven;
                    string? partialReason = SemanticCoverageReasons.Primary(
                        result.Coverage,
                        result.DeadlineExhausted,
                        result.SkippedCandidateProjects.Count > 0,
                        projectModelUnproven: result.ProjectModelUnproven);
                    return Json.WithCompleteSemanticIdentity(Json.Serialize(new
                    {
                        symbol = SemanticSymbolJson(result.Symbol),
                        documentationCommentId = requestedDocumentationCommentId,
                        documentationIdCoverage = documentationIdCoverage is null
                            ? null
                            : DocumentationIdCoverageJson(documentationIdCoverage),
                        implementations = Array.Empty<object>(),
                        concreteCount = 0,
                        coverage = CoverageJson(result.Coverage),
                        partial = partial ? true : (bool?)null,
                        partialReason,
                        retryRecommended = SemanticRetryRecommended(partialReason)
                            ? true
                            : (bool?)null,
                        retryHint = SemanticRetryHint(partialReason, "implementations",
                            deadlineMs, SemanticNavigationDeadlineMaxMs),
                        timing = new
                        {
                            deadlineMs,
                            elapsedMs = swSem.ElapsedMilliseconds,
                            documentationIdResolutionMs,
                            clusterLoadMs = result.ClusterLoadMs,
                            queryMs = result.QueryMs,
                            semanticColdStart = coldStartTiming.Timing,
                        },
                        meta = Meta.From(_manager.Health(), partial ? "indexed" : "exact",
                            "semantic"),
                    }));
                }
                failReason = ExpandReason(reason);
                object Error(string boundedId, bool truncated) => new
                {
                    error = "semantic_unavailable",
                    operation = "implementations",
                    documentationCommentId = boundedId,
                    documentationCommentIdTruncated = truncated ? true : (bool?)null,
                    documentationCommentIdBytes = truncated
                        ? Json.Utf8Bytes(documentationCommentId!)
                        : (int?)null,
                    partialReason = failReason,
                    retryRecommended = SemanticRetryRecommended(failReason)
                        ? true
                        : (bool?)null,
                    retryHint = SemanticRetryHint(failReason, "implementations", deadlineMs,
                        SemanticNavigationDeadlineMaxMs),
                    retry = new
                    {
                        tool = "search_symbol",
                        arguments = new { query = name, match = "exact", lang = "csharp" },
                    },
                    documentationIdCoverage = documentationIdCoverage is null
                        ? null
                        : DocumentationIdCoverageJson(documentationIdCoverage),
                    timing = new
                    {
                        deadlineMs,
                        elapsedMs = swSem.ElapsedMilliseconds,
                        documentationIdResolutionMs,
                        semanticColdStart = coldStartTiming.Timing,
                    },
                    meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                };
                return DocumentationIdError(documentationCommentId!, Error);
            }
            // Semantic RESOLVED the symbol but found no implementers, OR it could not resolve. Be
            // honest about which: bounded coverage (raising maxProjects may help) vs genuinely none.
            failReason = result is null ? ExpandReason(reason)
                : SemanticCoverageReasons.Primary(result.Coverage,
                    candidateProjectsSkipped: result.SkippedCandidateProjects.Count > 0,
                    projectModelUnproven: result.ProjectModelUnproven)
                  ?? "no_semantic_implementers";
        }

        if (requestedDocumentationCommentId is not null)
        {
            return DocumentationIdError(documentationCommentId!, (boundedId, truncated) => new
            {
                error = "semantic_unavailable",
                operation = "implementations",
                documentationCommentId = boundedId,
                documentationCommentIdTruncated = truncated ? true : (bool?)null,
                documentationCommentIdBytes = truncated
                    ? Json.Utf8Bytes(documentationCommentId!)
                    : (int?)null,
                partialReason = "target_not_resolved",
                retry = new
                {
                    tool = "search_symbol",
                    arguments = new { query = name, match = "exact", lang = "csharp" },
                },
                documentationIdCoverage = documentationIdCoverage is null
                    ? null
                    : DocumentationIdCoverageJson(documentationIdCoverage),
                timing = new
                {
                    deadlineMs,
                    elapsedMs = swSem.ElapsedMilliseconds,
                    documentationIdResolutionMs,
                    semanticColdStart = coldStartTiming.Timing,
                },
                meta = Meta.From(_manager.Health(), "indexed", "semantic"),
            });
        }

        // Heuristic fallback: types with a normalized direct base-head edge for the name — a
        // naming guess, not a compiler fact, so it is labeled confidence 'heuristic'.
        using var q = _manager.OpenQueries();
        string lookupName = name ?? hint ?? "";
        SymbolHit? targetSym = null;
        // Position mode (path+line): resolve the cursor so the fallback has a name AND the target's
        // kind + declaring type — the base-list heuristic only makes sense for a TYPE target.
        if (path is not null)
        {
            string normalizedPath = NormalizePath(path);
            var lineSymbols = q.SymbolsStartingAt(normalizedPath, line);
            targetSym = lineSymbols.FirstOrDefault(hit =>
                (lookupName.Length == 0 || string.Equals(hit.Name, lookupName, StringComparison.OrdinalIgnoreCase)) &&
                (arity is null || hit.Arity == arity.Value));
            var chain = q.SymbolAt(normalizedPath, line);
            if (chain.Count > 0)
            {
                targetSym ??= chain[0];
                if (lookupName.Length == 0) lookupName = targetSym.Name;
            }
        }
        if (lookupName.Length == 0)
        {
            return Json.Serialize(new
            {
                error = "symbol_not_resolved",
                partialReason = failReason,
                retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
                retryHint = SemanticRetryHint(failReason, "implementations", deadlineMs,
                    SemanticNavigationDeadlineMaxMs),
                timing = SemanticColdStartTimingJson(coldStartTiming, deadlineMs, swSem,
                    documentationIdResolutionMs),
                meta = Meta.From(_manager.Health(), "heuristic", "syntax"),
            });
        }
        targetSym ??= q.SearchSymbols(
            lookupName, "exact", null, 1, arity: arity, language: "cs").FirstOrDefault();
        int? fallbackArity = targetSym?.Arity ?? arity;
        string? targetKind = targetSym?.Kind;
        var meta = Meta.From(_manager.Health(), "heuristic", "syntax");

        // The base-list heuristic is a TYPE operation. For a MEMBER target, scope to the declaring
        // type's syntactic implementers and return the SAME-named member in each — not the type-only
        // base-list sweep (pure noise), and not a bare empty when the members are actually there.
        if (targetKind is not (null or "interface" or "class" or "struct" or "record" or "record_struct"))
        {
            List<SymbolHit> memberImpls = new();
            int implementerCount = 0;
            if (targetSym?.Container is { Length: > 0 } declType)
            {
                // Scope the member lookup to the implementer types by (namespace, type name) IDENTITY —
                // so the query's cap bounds only genuine implementer members (not every same-simple-named
                // type across all namespaces) and an unrelated type can't sneak in. ImplementationCandidates
                // now matches normalized direct base-head identities, so a superstring interface
                // (IFooBar) doesn't scope in.
                var typeKeys = q.ImplementationCandidates(declType, 100)
                    .Select(t => (t.Ns ?? "", t.Name))
                    .ToList();
                implementerCount = typeKeys.Count;
                if (typeKeys.Count > 0)
                    memberImpls = q.MembersNamedInTypes(lookupName, typeKeys, 100).Take(50).ToList();
            }
            if (memberImpls.Count > 0)
            {
                // Coverage transparency: an implementer that declares no such member (an interface impl
                // without this override) is legitimately omitted — say how many, so the caller knows.
                int matchedTypes = memberImpls.Select(m => (m.Ns ?? "", m.Container ?? "")).Distinct().Count();
                int omitted = Math.Max(0, implementerCount - matchedTypes);
                return Json.WithListBudget(memberImpls, (items, truncated) => new
                {
                    name = lookupName,
                    declaringType = targetSym!.Container,
                    // dve (b): same mixed-section honesty as the type fallback below.
                    symbol = resolvedSymbol is not null ? SemanticSymbolJson(resolvedSymbol) : null,
                    symbolConfidence = resolvedSymbol is not null ? "exact" : null,
                    implementationsConfidence = resolvedSymbol is not null ? "heuristic" : null,
                    implementerCount,
                    omittedImplementers = omitted > 0 ? omitted : (int?)null,
                    implementations = items.Select(SymbolJson),
                    partialReason = "member_scoped_syntactic",
                    semanticReason = failReason,
                    retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
                    retryHint = SemanticRetryHint(failReason, "implementations", deadlineMs,
                        SemanticNavigationDeadlineMaxMs),
                    timing = SemanticColdStartTimingJson(coldStartTiming, deadlineMs, swSem,
                        documentationIdResolutionMs),
                    note = $"Same-named members of the syntactic implementers of {targetSym!.Container} (confidence heuristic — compiler-exact override resolution found none, likely a type-twin identity mismatch)."
                        + (omitted > 0 ? $" {omitted} of {implementerCount} implementer(s) declare no such member and were omitted." : "")
                        + " Verify with source_context.",
                    truncated = truncated || memberImpls.Count >= 50,
                    meta,
                });
            }
            // Nothing to scope to — honest empty + the recovery note. Policy reason, not the transient
            // semantic one: the type-only heuristic won't help on retry.
            return Json.Serialize(new
            {
                name = lookupName,
                symbol = resolvedSymbol is not null ? SemanticSymbolJson(resolvedSymbol) : null,
                symbolConfidence = resolvedSymbol is not null ? "exact" : null,
                implementations = Array.Empty<object>(),
                partialReason = "member_fallback_type_scoped",
                semanticReason = failReason, // why the exact path returned nothing (context, not actionable)
                retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
                retryHint = SemanticRetryHint(failReason, "implementations", deadlineMs,
                    SemanticNavigationDeadlineMaxMs),
                timing = SemanticColdStartTimingJson(coldStartTiming, deadlineMs, swSem,
                    documentationIdResolutionMs),
                note = "No compiler-exact member implementations in the loaded cluster (possibly a type-twin identity mismatch), and no same-named member found in the declaring type's implementers. Run implementations on the declaring interface/type, then read this member in each implementer.",
                meta,
            });
        }

        var heuristic = allowHeuristicFallback
            ? q.ImplementationCandidates(lookupName, 50, fallbackArity)
            : new List<SymbolHit>();
        return Json.WithListBudget(heuristic, (items, truncated) => new
        {
            name = lookupName,
            // dve (b): mixed-section honesty, mirroring type_hierarchy.derivedConfidence — the
            // compiler-resolved identity survives into the fallback with its own confidence,
            // instead of the whole payload flattening to one heuristic label. Both fields are
            // omitted when the symbol itself never resolved (then meta's heuristic covers all).
            symbol = resolvedSymbol is not null ? SemanticSymbolJson(resolvedSymbol) : null,
            symbolConfidence = resolvedSymbol is not null ? "exact" : null,
            implementationsConfidence = resolvedSymbol is not null ? "heuristic" : null,
            implementations = items.Select(SymbolJson),
            partialReason = failReason ?? "semantic_unavailable",
            retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
            retryHint = SemanticRetryHint(failReason, "implementations", deadlineMs,
                SemanticNavigationDeadlineMaxMs),
            timing = SemanticColdStartTimingJson(coldStartTiming, deadlineMs, swSem,
                documentationIdResolutionMs),
            note = items.Count > 0 && failReason is "no_semantic_implementers" or "candidate_cluster_bounded"
                // Field (lhg): the old "declared in more than one assembly / generated twin" wording
                // went stale once compiled-awareness + assembly-ref edges landed — say what we now
                // actually know and what to do about it.
                ? "Compiler-exact resolution matched no implementers, but these types name it in their base list (confidence heuristic). Implementer projects were likely not loaded into the semantic cluster (raise maxProjects, or scope with pathGlob), or the implementers bind the name to a declaration outside the workspace. Verify with source_context."
                : "Base-list name matches from the index (confidence heuristic) — verify with source_context.",
            truncated = truncated || heuristic.Count >= 50, // count-capped even if the byte budget fit
            meta,
        });
    }
}
