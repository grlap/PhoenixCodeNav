using System.ComponentModel;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: references — dispatch, C# filtering and grouping, and response shaping.
/// Does not own: F# shaping (NavigationTools.FSharp.cs), compiler resolution (CodeNav.Core), or the shared selector, coverage, and budget primitives.
/// </summary>
public sealed partial class NavigationTools
{
    [McpServerTool(Name = "references")]
    [Description("Where a symbol is used, grouped by project with counts and sample lines. C# mode='auto' tries compiler-exact workspace references (target by stable documentationCommentId, position path+line, symbolId, or name) scoped to candidate projects. F# is position-only, type-checks the selected physical .fsproj + exact root TFM with its F# ProjectReference closure under the evaluated MSBuild transitivity policy; exact child TFMs win and table-compatible single-target netstandard2.0/2.1 children are supported. It counts compiler-bound non-definition uses in the selected root and every proven source-ProjectReference F# workspace dependent, scanning applicable dependent TFMs and deduplicating physical sites; coverage identifies excluded, failed, and pending dependents when the total is a lower bound. A C# documentationCommentId is semantic-only and never falls back to indexed name candidates; other C# selectors may. Exact C# references are usage-kind classified (kinds breakdown: call/construction/typeMention/attribute/nameof/xmldoc/usingDirective/baseList/typeof/implicitConversion/explicitConversion/checkedConversion/other) — filter with usageKinds (e.g. 'call' to skip doc mentions) or publicConsumersOnly for external callers. Pass pathGlob/excludePath to scope ordinary indexed C# candidates (e.g. excludePath='3rdparty/**'); documentationCommentId, operator idx handles, and F# positions reject path filters as bad_request with reason incompatible_filter. Genuine operator semantic unavailability remains semantic_required. Call before changing behavior. C# semantic owner sets are assembly-name-keyed; only documentationCommentId requests disclose non-pair collisions through nameKeyedOwnerCollisionGroups.")]
    public string References(
        [Description("Symbol name (whole-identifier). Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of a usage or declaration (position mode — most precise).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional for C#; required for F# references).")] int column = 0,
        [Description("'auto' (semantic first), 'semantic', or 'indexed' (fast candidates).")] string mode = "auto",
        [Description("Include usages in test projects (default true).")] bool includeTests = true,
        [Description("Include usages in generated files (default false).")] bool includeGenerated = false,
        [Description("Comma-separated usage-kind filter or JSON-array encoded string — SEMANTIC (exact) path only: call, construction, typeMention, attribute, nameof, xmldoc, usingDirective, baseList, typeof, implicitConversion, explicitConversion, checkedConversion, other. Null or an empty string = all; whitespace-only values, empty CSV items, and empty JSON arrays are bad_request. Counts and groups honor it (e.g. 'call,construction' = real executions only).")] string? usageKinds = null,
        [Description("Only usages OUTSIDE the symbol's own declaring PROJECT (project-scoped, NOT accessibility-scoped — the name is about API blast radius, not access modifiers). The external-consumer view; semantic path only.")] bool publicConsumersOnly = false,
        [Description("Restrict ordinary indexed C# candidate paths to this glob. Incompatible with documentationCommentId, operator idx handles, and F# position mode; those selectors return bad_request with reason incompatible_filter.")] string? pathGlob = null,
        [Description("Exclude ordinary indexed C# candidate paths matching this glob, e.g. '3rdparty/**'. Incompatible with documentationCommentId, operator idx handles, and F# position mode; those selectors return bad_request with reason incompatible_filter.")] string? excludePath = null,
        [Description("Max candidate files scanned in indexed mode (default 500).")] int maxFiles = 500,
        [Description("Candidate-project budget; 0 (default) loads all matching projects, while a positive value opts into a bound.")] int maxProjects = SemanticService.DefaultCandidateProjectBudget,
        [Description("Sample lines per project group (default 3).")] int samplesPerGroup = 3,
        [Description("Semantic deadline in ms (default 15000, max 120000).")] int timeoutMs = 15000,
        [Description("Resolve by a prior result's handle instead of name/position: 'idx:NNN' (from search_symbol / symbol_at / definition). Takes precedence over name and path+line. Note: 'idx:' handles are index-local and change on reindex. Operator handles require mode='auto' or 'semantic' and reject pathGlob/excludePath.")] string? symbolId = null,
        [Description("Stable C# Roslyn declaration id (T:/M:/P:/F:/E:). Empty means omitted; whitespace-only is bad_request. Mutually exclusive with symbolId, name, path+line, pathGlob, and excludePath; mode must be 'auto' or 'semantic'. A successful response echoes the compiler-canonical id, which is safe to reuse directly; failures echo a bounded form of the caller input.")] string? documentationCommentId = null,
        [Description("F# position mode only: workspace-relative physical .fsproj path. Required with targetFramework when the file has more than one type-check context.")] string? projectPath = null,
        [Description("F# position mode only: exact target framework. Required with projectPath when the file has more than one type-check context.")] string? targetFramework = null)
    {
        if (NotReady() is { } notReady) return notReady;
        if (NormalizeDocumentationCommentId(ref documentationCommentId) is { } idError)
            return idError;
        mode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        if (mode is not ("auto" or "semantic" or "indexed"))
        {
            return Json.Serialize(new
            {
                error = "bad_request",
                field = "mode",
                validValues = new[] { "auto", "semantic", "indexed" },
                detail = "mode must be 'auto', 'semantic', or 'indexed'.",
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
        string? semanticDeclarationKey = null;
        if (!string.IsNullOrEmpty(documentationCommentId))
        {
            if (symbolId is { Length: > 0 } || name is { Length: > 0 } ||
                path is { Length: > 0 } || line > 0 || column > 0)
            {
                return Json.Serialize(new
                {
                    error = "bad_request",
                    field = "documentationCommentId",
                    detail = "documentationCommentId is mutually exclusive with symbolId, name, path, line, and column.",
                });
            }
            if (mode == "indexed")
            {
                return DocumentationIdError(documentationCommentId,
                    (boundedId, truncated) => new
                    {
                        error = "bad_request",
                        field = "mode",
                        reason = "incompatible_mode",
                        expected = "auto or semantic",
                        operation = "references",
                        documentationCommentId = boundedId,
                        documentationCommentIdTruncated = truncated ? true : (bool?)null,
                        documentationCommentIdBytes = truncated
                            ? Json.Utf8Bytes(documentationCommentId)
                            : (int?)null,
                        detail = "documentationCommentId is compiler identity and cannot be combined with mode='indexed'; use mode='auto' or mode='semantic'.",
                        meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                    });
            }
            if (pathGlob is { Length: > 0 } || excludePath is { Length: > 0 })
            {
                string incompatibleField = pathGlob is { Length: > 0 }
                    ? "pathGlob"
                    : "excludePath";
                return DocumentationIdError(documentationCommentId,
                    (boundedId, truncated) => new
                    {
                        error = "bad_request",
                        field = incompatibleField,
                        reason = "incompatible_filter",
                        expected = "omit pathGlob and excludePath",
                        operation = "references",
                        documentationCommentId = boundedId,
                        documentationCommentIdTruncated = truncated ? true : (bool?)null,
                        documentationCommentIdBytes = truncated
                            ? Json.Utf8Bytes(documentationCommentId)
                            : (int?)null,
                        detail = "documentationCommentId is compiler identity and cannot be combined with path filters.",
                        meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                    });
            }
            var (documentedTarget, documentedError) = ResolveDocumentationTarget(
                documentationCommentId, "references", deadlineMs, implementationsOnly: false,
                SemanticNavigationDeadlineMaxMs, semanticDeadline.Token, swSem,
                coldStartTiming);
            if (documentedError is not null) return documentedError;
            requestedDocumentationCommentId = documentedTarget!.CanonicalDocumentationCommentId;
            documentationIdCoverage = documentedTarget.Coverage;
            documentationIdSnapshotIdentity = documentedTarget.SnapshotIdentity;
            documentationIdResolutionMs = documentedTarget.ResolutionElapsedMs;
            name = documentedTarget!.Name;
            path = documentedTarget.Path;
            line = documentedTarget.Line;
            column = documentedTarget.Column;
            semanticDeclarationKey = null;
        }
        SymbolHit? resolvedHandleHit = null;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            resolvedHandleHit = hit;
            name = hit!.Name; path = hit.FilePath; line = hit.StartLine; column = 0;
            semanticDeclarationKey = OperatorDeclarationKey(hit);
        }
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
                    return UnsupportedLanguage(path, indexedFile.Language, "references");
                if (resolvedHandleHit is not null || documentationCommentId is { Length: > 0 })
                {
                    return Json.Serialize(new
                    {
                        error = "fsharp_semantic_position_required",
                        operation = "references",
                        detail = "F# semantic references require an explicit path + line + column; documentationCommentId and idx handle resolution are not available yet.",
                    });
                }
                if (line <= 0 || column <= 0)
                {
                    return Json.Serialize(new
                    {
                        error = "fsharp_semantic_position_required",
                        operation = "references",
                        detail = "F# semantic references require path + line + column; bare-name and line-only resolution are not available yet.",
                    });
                }
                if (mode == "indexed")
                {
                    return Json.Serialize(new
                    {
                        error = "fsharp_indexed_symbols_unavailable",
                        operation = "references",
                        detail = "F# references are compiler-semantic only; use mode='auto' or mode='semantic'.",
                    });
                }
                string? incompatibleField = usageKinds is { Length: > 0 }
                    ? "usageKinds"
                    : publicConsumersOnly
                        ? "publicConsumersOnly"
                        : pathGlob is { Length: > 0 }
                            ? "pathGlob"
                            : excludePath is { Length: > 0 }
                                ? "excludePath"
                                : null;
                if (incompatibleField is not null)
                {
                    return Json.Serialize(new
                    {
                        error = "bad_request",
                        field = incompatibleField,
                        reason = "incompatible_filter",
                        expected = incompatibleField == "publicConsumersOnly"
                            ? "false"
                            : $"omit {incompatibleField}",
                        operation = "references",
                        detail = "Same-project F# references do not support C# usage-kind, public-consumer, or path filters.",
                        meta = FSharpSemanticMeta(_manager.Health(), "bad_request"),
                    });
                }
                return FSharpReferences(path, line, column, projectPath, targetFramework,
                    includeTests, includeGenerated, samplesPerGroup, timeoutMs);
            }
        }
        if (UnsupportedLanguageAtPath(path, "references") is { } unsupportedLanguage)
            return unsupportedLanguage;
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'symbolId', 'name', or 'path'+'line'." });
        }

        // Path filters are honored precisely only on the indexed candidate path (semantic counts
        // are project-level and cannot be re-derived per path), so a filter forces indexed mode.
        bool hasPathFilter = pathGlob is { Length: > 0 } || excludePath is { Length: > 0 };
        if (resolvedHandleHit is { Kind: "operator" } && mode == "indexed")
        {
            return OperatorReferencesBadRequest(
                "mode", "incompatible_mode", "auto or semantic",
                "Operator idx handles require compiler-semantic references so the canonical declaration key remains pinned; mode='indexed' cannot distinguish overload identity.");
        }
        if (resolvedHandleHit is { Kind: "operator" } && hasPathFilter)
        {
            return OperatorReferencesBadRequest(
                pathGlob is { Length: > 0 } ? "pathGlob" : "excludePath",
                "incompatible_filter", "omit pathGlob and excludePath",
                "Operator idx handles require compiler-semantic references so the canonical declaration key remains pinned; path filters force indexed candidates and cannot distinguish overload identity.");
        }
        string? failReason = hasPathFilter && mode != "indexed" ? "path_filter_ran_indexed_candidates" : null;
        // Usage-kind buckets + external-consumers view are syntax/compiler facts — semantic path only.
        if (!TryParseStringList(usageKinds, "usageKinds",
                out List<string>? parsedUsageKinds, out string? usageKindsDetail))
            return StringListBadRequest("usageKinds", usageKindsDetail);
        var kindSet = parsedUsageKinds is { Count: > 0 } uk
            ? new HashSet<string>(uk, StringComparer.OrdinalIgnoreCase)
            : null;
        if (kindSet is not null)
        {
            // Validate up front: a typo ('calls') silently filtering everything to zero would read as
            // "dead code" at exact confidence — the silent-empty anti-pattern this codebase keeps killing.
            var unknown = kindSet.Where(k => !SemanticReferenceKinds.All.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
            {
                return Json.Serialize(new
                {
                    error = "bad_request",
                    detail = $"Unknown usageKinds value(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", SemanticReferenceKinds.All)}.",
                    meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                });
            }
        }
        if (mode is "auto" or "semantic" && !hasPathFilter)
        {
            // Deadline visibility (24n): every semantic response reports the effective deadline and
            // how much of it was spent — "why is this partial / slow?" needs numbers, not guesses.
            var (target, hint) = ResolveSemanticTarget(name, null, null, path, line, column);
            if (TestOnlySemanticFailureReason is { } forcedFailure)
            {
                failReason = forcedFailure;
                SemanticColdStartTiming timing = coldStartTiming.Publish(
                    swSem.ElapsedMilliseconds);
                _semantic.EmitTerminalTelemetry(
                    "references", "degraded", forcedFailure, timing);
            }
            else if (target is { } t)
            {
                var (result, reason) = _semantic
                    .ReferencesAsync(t.Path, t.Line, t.Column, hint, maxProjects,
                        Math.Clamp(samplesPerGroup, 0, 10),
                        RemainingDeadlineMilliseconds(deadlineMs, swSem), includeGenerated,
                        kindSet, publicConsumersOnly, includeTests,
                        semanticDeclarationKey, semanticDeadline.Token,
                        documentationIdSnapshotIdentity, coldStartTiming)
                    .GetAwaiter().GetResult();
                if (result is not null)
                {
                    if (requestedDocumentationCommentId is not null &&
                        !string.Equals(result.Symbol.DocumentationCommentId,
                            requestedDocumentationCommentId, StringComparison.Ordinal))
                    {
                        return DocumentationIdIdentityMismatch(documentationCommentId!,
                            requestedDocumentationCommentId,
                            result.Symbol.DocumentationCommentId,
                            "references",
                            documentationIdCoverage,
                            deadlineMs,
                            swSem,
                            documentationIdResolutionMs,
                            coldStartTiming.Timing);
                    }
                    // includeTests is filtered INSIDE the semantic scan, before counting (wu1) —
                    // so TotalLocations, KindCounts, Groups, and this summary all describe one set.
                    var groups0 = result.Groups;
                    int prod0 = groups0.Where(g => !g.IsTestProject).Sum(g => g.Count);
                    int test0 = groups0.Where(g => g.IsTestProject).Sum(g => g.Count);
                    // "0 test" would misread as "no test usages exist" when they were EXCLUDED.
                    string mix0 = includeTests ? $"{prod0} production, {test0} test" : $"{prod0} production; test projects excluded";
                    // Field 0.7.2 P2: an exact ZERO when the base-list index KNOWS implementers is
                    // almost certainly a loading gap, not dead code — say so, actionably. (The
                    // honesty posture says "0 is a fact", but here the tool holds contrary data.)
                    // GUARDED (review): the note must not fire on FILTER-caused zeros — with
                    // usageKinds:'call' the base-list namers it would cite are exactly the
                    // baseList usages the filter excluded, and "raise maxProjects" cannot help.
                    // Same for publicConsumersOnly/includeTests. And the advice is only honest
                    // when coverage was actually BOUNDED (skipped/failed/under-loaded) — post-
                    // seeds, an unfiltered zero with namers and full coverage shouldn't occur.
                    string? referenceNote = null;
                    string? referenceNoteId = null;
                    bool conversionUsageEnumerationGap =
                        result.ConversionUsageEnumerationIncomplete;
                    if (conversionUsageEnumerationGap)
                    {
                        referenceNote = result.ProjectModelUnproven
                            ? "Compiler reference enumeration does not currently prove user-defined conversion usage sites; totalReferences is incomplete, and project-model uncertainty prevents a directional count guarantee."
                            : "Compiler reference enumeration does not currently prove user-defined conversion usage sites; totalReferences is a lower bound.";
                        referenceNoteId = NoteIds.ReferencesConversionUsageEnumerationGap;
                    }
                    bool zeroUnfiltered = kindSet is null && !publicConsumersOnly && includeTests;
                    bool coverageBounded = result.SkippedCandidateProjects.Count > 0
                        || result.Coverage.FailedProjects.Count > 0
                        || result.Coverage.LoadedProjects < result.Coverage.RequestedProjects;
                    if (!conversionUsageEnumerationGap && result.TotalLocations == 0 &&
                        zeroUnfiltered && coverageBounded)
                    {
                        using var qz = _manager.OpenQueries();
                        string probeName = name ?? hint ?? "";
                        int baseListNamers = probeName.Length > 0 ? qz.ImplementationCandidates(probeName, 5).Count : 0;
                        if (baseListNamers > 0)
                        {
                            referenceNote = $"0 exact references, but {(baseListNamers >= 5 ? "5+" : baseListNamers.ToString())} indexed types name '{probeName}' in their base lists (see implementations) — coverage was bounded (see skipped/failed); raise maxProjects or scope with pathGlob.";
                            referenceNoteId = NoteIds.ReferencesZeroLoadingGap;
                        }
                    }
                    bool exhausted = result.DeadlineExhausted;
                    bool unsupportedLanguageSkipped = result.Coverage.SkippedProjects.Count > 0;
                    bool candidateProjectsSkipped = result.SkippedCandidateProjects.Count > 0;
                    bool failedLoads = result.Coverage.FailedProjects.Count > 0;
                    bool coverageIncomplete = result.Coverage.LoadedProjects <
                        result.Coverage.RequestedProjects;
                    bool outOfGraphCandidates = result.OutOfGraphCandidateCount > 0;
                    // Omitted monotonic scope makes the modeled total a lower bound. Project-model
                    // uncertainty is different: imported MSBuild authority can revoke a locally
                    // modeled relationship, so candidates may include false positives and must
                    // not be described as "at least N".
                    bool monotonicScopeOmitted = exhausted || unsupportedLanguageSkipped ||
                        candidateProjectsSkipped || failedLoads || coverageIncomplete ||
                        outOfGraphCandidates || conversionUsageEnumerationGap;
                    bool partial = monotonicScopeOmitted || result.ProjectModelUnproven;
                    bool totalIsLowerBound = monotonicScopeOmitted &&
                        !result.ProjectModelUnproven;
                    string atLeast = totalIsLowerBound ? "at least " : "";
                    bool indexedConfidence = partial;
                    var meta0 = Meta.From(_manager.Health(),
                        indexedConfidence ? "indexed" : "exact", "semantic");
                    long elapsedMs = swSem.ElapsedMilliseconds;
                    var partialReasons = new List<string>();
                    if (conversionUsageEnumerationGap)
                        partialReasons.Add(result.ProjectModelUnproven
                            ? "conversion_usage_enumeration_gap: compiler reference enumeration does not currently prove user-defined conversion usage sites; count direction is unknown while the project model is unproven"
                            : "conversion_usage_enumeration_gap: compiler reference enumeration does not currently prove user-defined conversion usage sites; counts are a lower bound");
                    if (exhausted)
                        partialReasons.Add($"semantic_timeout: deadline exhausted after {elapsedMs}ms of {deadlineMs}ms; counts cover the scanned portion only (raise timeoutMs)");
                    if (unsupportedLanguageSkipped)
                        partialReasons.Add($"unsupported_language_projects_skipped: {result.Coverage.SkippedProjects.Count} project(s) could contain additional references");
                    if (candidateProjectsSkipped)
                        partialReasons.Add($"candidate_cluster_bounded: skipped {result.SkippedCandidateProjects.Count} candidate project(s) (raise maxProjects)");
                    if (failedLoads)
                    {
                        string failedCause = SemanticCoverageReasons.FailedProjects(result.Coverage)
                            ?? "project_load_failed";
                        partialReasons.Add($"{failedCause}: {result.Coverage.FailedProjects.Count} project(s) were not scanned");
                    }
                    if (coverageIncomplete && !unsupportedLanguageSkipped && !failedLoads)
                        partialReasons.Add($"project_coverage_incomplete: loaded {result.Coverage.LoadedProjects} of {result.Coverage.RequestedProjects} requested projects");
                    if (outOfGraphCandidates)
                        partialReasons.Add($"out_of_graph_candidates: {result.OutOfGraphCandidateCount} textual candidate project(s) have no loadable dependency path");
                    if (result.ProjectModelUnproven)
                        partialReasons.Add("project_model_unproven");
                    string? partialReason = partialReasons.Count > 0
                        ? string.Join("; ", partialReasons)
                        : null;
                    string summary = conversionUsageEnumerationGap
                        ? totalIsLowerBound
                            ? $"User-defined conversion reference enumeration is incomplete; {result.TotalLocations} compiler-reported locations across {groups0.Count} projects ({mix0}) are a lower bound."
                            : $"User-defined conversion reference enumeration is incomplete, and project-model uncertainty prevents a directional count guarantee; {result.TotalLocations} compiler-reported locations across {groups0.Count} projects ({mix0})."
                        : $"{atLeast}{result.TotalLocations} {(indexedConfidence ? "compiler-resolved candidate" : "exact")} references across {groups0.Count} projects ({mix0}).";
                    var boundedOutOfGraph = new List<(string Value, bool Truncated)>();
                    foreach (string project in result.OutOfGraphCandidates ?? [])
                    {
                        string bounded = Json.JsonStringPrefix(project, 512,
                            out bool identityTruncated);
                        boundedOutOfGraph.Add((bounded, identityTruncated));
                    }
                    return Json.WithCompleteSemanticIdentity(
                        Json.WithAuxiliaryListsBudget(groups0,
                        result.SkippedCandidateProjects, boundedOutOfGraph,
                        (items, truncated, skippedItems, skippedTruncated,
                            outOfGraphItems, outOfGraphBudgetTruncated) =>
                        {
                            int emittedReferenceSamples = items.Sum(group =>
                                group.Samples.Count);
                            var sampleCoverageReasons = new List<object>();
                            int textOmitted = result.ReferenceSamplesSelected -
                                result.ReferenceSamplesReturned;
                            int deadlineOmitted = Math.Min(textOmitted,
                                result.ReferenceSamplesDeadlineOmitted);
                            int otherTextOmitted = textOmitted - deadlineOmitted;
                            if (deadlineOmitted > 0)
                            {
                                sampleCoverageReasons.Add(new
                                {
                                    noteId = NoteIds.ReferencesSamplesDeadline,
                                    omitted = deadlineOmitted,
                                    guidance = "Raise timeoutMs to allow bounded sample text to load.",
                                });
                            }
                            if (otherTextOmitted > 0)
                            {
                                sampleCoverageReasons.Add(new
                                {
                                    noteId = NoteIds.ReferencesSamplesTrimmed,
                                    omitted = otherTextOmitted,
                                });
                            }
                            if (emittedReferenceSamples <
                                result.ReferenceSamplesReturned)
                            {
                                sampleCoverageReasons.Add(new
                                {
                                    noteId = NoteIds.ReferencesSamplesByteBudget,
                                    omitted = result.ReferenceSamplesReturned -
                                        emittedReferenceSamples,
                                });
                            }
                            return new
                            {
                                symbol = SemanticSymbolJson(result.Symbol),
                                documentationCommentId = requestedDocumentationCommentId,
                                documentationIdCoverage = documentationIdCoverage is null
                                    ? null
                                    : DocumentationIdCoverageJson(documentationIdCoverage),
                                summary,
                                totalReferences = result.TotalLocations,
                                totalIsLowerBound = totalIsLowerBound ? true : (bool?)null,
                                // HOW the symbol is used, e.g. {"call":20,"xmldoc":480} — the anti-"500 refs
                                // that are mostly doc mentions" signal. Filter with usageKinds.
                                kinds = result.KindCounts is { Count: > 0 } ? result.KindCounts : null,
                                groupBy = "project",
                                groups = items.Select(g => new
                                {
                                    project = g.Project,
                                    isTest = g.IsTestProject,
                                    count = g.Count,
                                    samples = g.Samples.Select(s => new { s.Path, s.Line, text = s.LineText, kind = s.Kind }),
                                }),
                                sampleCoverage = emittedReferenceSamples <
                                    result.ReferenceSamplesSelected
                                    ? new
                                    {
                                        selected = result.ReferenceSamplesSelected,
                                        returned = emittedReferenceSamples,
                                        complete = false,
                                        reasons = sampleCoverageReasons,
                                    }
                                    : null,
                                coverage = CoverageJson(result.Coverage),
                                partial,
                                partialReason,
                                retryRecommended = SemanticRetryRecommended(partialReason)
                                    ? true
                                    : (bool?)null,
                                retryHint = SemanticRetryHint(partialReason, "references",
                                    deadlineMs, SemanticNavigationDeadlineMaxMs),
                                skippedCandidateProjects = skippedItems.Count > 0 ? skippedItems : null,
                                skippedCandidateProjectCount = result.SkippedCandidateProjects.Count > 0
                            ? result.SkippedCandidateProjects.Count
                            : (int?)null,
                                skippedCandidateProjectsTruncated = skippedTruncated ? true : (bool?)null,
                                // kbn: unscanned projects that textually mention the symbol but have no
                                // graph path to its declarer (plugins, config-wired consumers). Scope with
                                // pathGlob or run indexed mode to see their candidate lines.
                                outOfGraphCandidates = outOfGraphItems.Count > 0
                            ? outOfGraphItems.Select(item => item.Value).ToList()
                            : null,
                                outOfGraphCandidateCount = result.OutOfGraphCandidateCount > 0
                            ? result.OutOfGraphCandidateCount
                            : (int?)null,
                                outOfGraphCandidatesReturned = result.OutOfGraphCandidateCount > 0
                            ? outOfGraphItems.Count
                            : (int?)null,
                                outOfGraphCandidatesTruncated = result.OutOfGraphCandidatesTruncated ||
                                                        outOfGraphBudgetTruncated
                            ? true
                            : (bool?)null,
                                outOfGraphCandidateItemsTruncated = outOfGraphItems.Any(item =>
                                    item.Truncated)
                            ? true
                            : (bool?)null,
                                note = referenceNote,
                                noteId = referenceNoteId, // a0b: stable, machine-matchable cause
                                                          // t2b: where the budget went — cluster load+resolve vs find+count.
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
                                meta = meta0,
                            };
                        }, maxBytes: TestOnlyReferencesResponseMaxBytes));
                }
                failReason = ExpandReason(reason); // t2b: cold-load token gains inline retry advice
            }
            else
            {
                failReason = "target_not_found_in_index";
            }
            if (mode == "semantic" || requestedDocumentationCommentId is not null)
            {
                object Error(string boundedId, bool truncated) => new
                {
                    error = "semantic_unavailable",
                    operation = "references",
                    documentationCommentId = requestedDocumentationCommentId is null
                        ? null
                        : boundedId,
                    documentationCommentIdTruncated = requestedDocumentationCommentId is not null &&
                                                      truncated
                        ? true
                        : (bool?)null,
                    partialReason = failReason,
                    retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
                    retryHint = SemanticRetryHint(failReason, "references", deadlineMs,
                        SemanticNavigationDeadlineMaxMs),
                    retry = requestedDocumentationCommentId is null
                        ? null
                        : new
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
                return requestedDocumentationCommentId is null
                    ? Json.Serialize(Error("", false))
                    : DocumentationIdError(documentationCommentId!, Error);
            }
        }

        if (resolvedHandleHit is { Kind: "operator" })
            return OperatorReferencesRequireSemantic(
                failReason ?? "semantic_unavailable",
                SemanticColdStartTimingJson(coldStartTiming, deadlineMs, swSem,
                    documentationIdResolutionMs));

        // Indexed fallback (name required — derive from position when missing).
        using var q = _manager.OpenQueries();
        if (string.IsNullOrEmpty(name) && path is not null)
        {
            var chain = q.SymbolAt(NormalizePath(path), line);
            name = chain.Count > 0 ? chain[0].Name : name;
        }
        if (string.IsNullOrEmpty(name))
        {
            return Json.Serialize(new
            {
                error = "symbol_not_resolved",
                partialReason = failReason,
                retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
                retryHint = SemanticRetryHint(failReason, "references", deadlineMs,
                    SemanticNavigationDeadlineMaxMs),
                timing = SemanticColdStartTimingJson(coldStartTiming, deadlineMs, swSem,
                    documentationIdResolutionMs),
            });
        }
        var excludes = excludePath is { Length: > 0 } ex ? new[] { ex } : null;
        // includeTests is filtered INSIDE the candidate scan, before counting (wu1) — so `total`
        // and the groups describe one set. Summing the filtered groups here instead was itself
        // dishonest (review-reproduced): a file linked into TWO production projects appears in
        // both groups, so the sum double-counted it and the "filtered" total EXCEEDED the real one.
        IndexQueries.ReferenceCandidateResult candidates = q.ReferenceCandidates(
            name, Math.Clamp(maxFiles, 10, 2000), Math.Clamp(samplesPerGroup, 0, 10), pathGlob, excludes, includeGenerated, includeTests);
        int total = candidates.TotalHits;
        int prod = candidates.ProdHits;
        int test = candidates.TestHits;
        List<ReferenceGroup> groups = candidates.Groups;
        bool candidateFilesCapHit = candidates.CandidateFilesTruncated;
        var indexedPartialReasons = new List<string>();
        if (!string.IsNullOrEmpty(failReason)) indexedPartialReasons.Add(failReason);
        if (candidateFilesCapHit)
        {
            indexedPartialReasons.Add(
                $"candidate_file_cap: scanned {candidates.CandidateFilesScanned} candidate files; " +
                $"at least {candidates.CandidateFilesAtLeast} matched (raise maxFiles)");
        }
        string? indexedPartialReason = indexedPartialReasons.Count > 0
            ? string.Join("; ", indexedPartialReasons)
            : null;

        // prod/test are PHYSICAL splits of `total` (each file once — 0ok: the old per-group sums
        // let "4 candidate lines (8 production)" appear when a file is linked into two projects).
        string mix = includeTests ? $"{prod} production, {test} test" : $"{prod} production; test projects excluded";
        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(groups, (items, truncated) => new
        {
            name,
            partial = candidateFilesCapHit ? true : (bool?)null,
            partialReason = indexedPartialReason,
            retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
            retryHint = SemanticRetryHint(failReason, "references", deadlineMs,
                SemanticNavigationDeadlineMaxMs),
            timing = SemanticColdStartTimingJson(coldStartTiming, deadlineMs, swSem,
                documentationIdResolutionMs),
            summary = candidateFilesCapHit
                ? $"At least {total} candidate reference lines across {groups.Count} returned project groups ({mix}); the indexed scan covered {candidates.CandidateFilesScanned} of at least {candidates.CandidateFilesAtLeast} matching files."
                : $"{total} candidate reference lines across {groups.Count} projects ({mix}).",
            totalCandidates = total,
            totalIsLowerBound = candidateFilesCapHit ? true : (bool?)null,
            coverage = new
            {
                candidateFilesScanned = candidates.CandidateFilesScanned,
                candidateFilesTotal = candidateFilesCapHit
                    ? (int?)null
                    : candidates.CandidateFilesScanned,
                candidateFilesAtLeast = candidates.CandidateFilesAtLeast,
                candidateFileLimit = candidates.CandidateFileLimit,
                candidateFilesCapHit = candidateFilesCapHit ? true : (bool?)null,
            },
            groupBy = "project",
            groups = items.Select(g => new
            {
                project = GroupProject(g.Project),
                orphaned = GroupOrphaned(g.Project),
                isTest = g.IsTestProject,
                count = g.Count,
                samples = g.Samples.Select(s => new { path = s.FilePath, s.Line, text = s.LineText }),
            }),
            truncated,
            note = "Candidates are whole-identifier text matches (confidence: indexed), not compiler-resolved references."
                + (candidateFilesCapHit
                    ? " The candidate-file scan reached maxFiles; counts are lower bounds."
                    : "")
                + (kindSet is not null || publicConsumersOnly
                    ? " NOTE: usageKinds/publicConsumersOnly need compiler syntax and were NOT applied on this indexed path."
                    : ""),
            noteId = candidateFilesCapHit ? NoteIds.ReferencesCandidateFilesCap : null,
            meta,
        });
    }
}
