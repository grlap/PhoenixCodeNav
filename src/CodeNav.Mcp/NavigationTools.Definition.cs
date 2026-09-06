using System.ComponentModel;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: symbol_at and definition — dispatch, C# response shaping, declaration-body shaping, and live/indexed source disclosure.
/// Does not own: F# shaping (NavigationTools.FSharp.cs), reference/implementation scans, or the shared selector and coverage primitives.
/// </summary>
public sealed partial class NavigationTools
{
    [McpServerTool(Name = "symbol_at")]
    [Description("Reverse lookup: given a file + line (from a stack trace, build error, diff hunk, or grep hit), returns the smallest containing symbol and its enclosing chain plus owning projects. F# uses an FCS type-check context plus its ProjectReference closure; exact child TFMs win and table-compatible single-target netstandard2.0/2.1 children are supported. SDK projects are transitive by default, while DisableTransitiveProjectReferences=true and legacy projects are direct-only. Column is required when a line contains multiple symbol uses.")]
    public string SymbolAt(
        [Description("Workspace-relative file path.")] string path,
        [Description("1-based line number.")] int line,
        [Description("Optional 1-based column. Used for F# semantic resolution; required when several symbols occur on the line.")] int column = 0,
        [Description("F# only: workspace-relative physical .fsproj path. Required with targetFramework when the file has more than one type-check context.")] string? projectPath = null,
        [Description("F# only: exact target framework (for example net472 or net8.0). Required with projectPath when the file has more than one type-check context.")] string? targetFramework = null,
        [Description("F# semantic resolution deadline in ms (default 10000, max 60000).")] int timeoutMs = 10000)
    {
        if (NotReady() is { } notReady) return notReady;
        path = NormalizePath(path);
        FileHit? indexedFile;
        using (var languageQueries = _manager.OpenQueries())
            indexedFile = languageQueries.FileByPath(path);
        if (indexedFile is { Language: "fs" })
            return FSharpSymbolAt(path, line, column, projectPath, targetFramework, timeoutMs);
        if (indexedFile is { Language: not "cs" } unsupportedFile)
        {
            return UnsupportedLanguage(path, unsupportedFile.Language, "symbol_at");
        }
        using var q = _manager.OpenQueries();
        var chain = q.SymbolAt(path, line);
        var projects = q.ProjectsContaining(path);
        return Json.Serialize(new
        {
            path,
            line,
            found = chain.Count > 0,
            chain = chain.Select(SymbolJson),
            owningProjects = projects.Select(p => new
            {
                p.Name,
                p.Path,
                p.Style,
                language = p.Language,
                p.IsTest,
            }),
            meta = Meta.From(_manager.Health(), "indexed", "syntax"),
        });
    }

    [McpServerTool(Name = "definition")]
    [Description("Declaration site(s) for a symbol — all partial declarations included. Target by stable C# documentationCommentId, exact name (optionally 'container' to disambiguate), symbolId, or position (path+line[,column]) from a usage site. A documentationCommentId is semantic-only and never falls back to a name lookup. Other C# selectors try compiler-exact resolution first and can fall back to the name index. F# semantic definition is position-only and returns declarations from one selected physical .fsproj + exact root TFM and its F# ProjectReference closure; exact child TFMs win and table-compatible single-target netstandard2.0/2.1 children are supported. SDK projects are transitive by default, while DisableTransitiveProjectReferences=true and legacy projects are direct-only. It never falls back to an indexed F# guess or a last-built dependency DLL. includeBody is C# only. C# semantic owner sets are assembly-name-keyed; only documentationCommentId requests disclose non-pair collisions through nameKeyedOwnerCollisionGroups.")]
    public string Definition(
        [Description("Exact symbol name (case-insensitive). Optional when path+line given.")] string? name = null,
        [Description("Optional containing type or namespace fragment to disambiguate.")] string? container = null,
        [Description("Comma-separated kind filter or JSON-array encoded string. Null or an empty string defaults to all kinds; whitespace-only values, empty CSV items, and empty JSON arrays are bad_request.")] string? kinds = null,
        [Description("Workspace-relative file path of a usage or declaration site (position mode).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("'auto' (semantic first, indexed fallback), 'semantic', or 'indexed'.")] string mode = "auto",
        [Description("Semantic resolution deadline in ms (default 10000, max 60000).")] int timeoutMs = 10000,
        [Description("Also return the primary declaration's source body (numbered lines, budget-bounded).")] bool includeBody = false,
        [Description("Byte budget for the inline body (default 12288, max 16384).")] int bodyMaxBytes = 12288,
        [Description("Resolve by a prior result's handle instead of name/position: 'idx:NNN' (from search_symbol / symbol_at / definition). Takes precedence over name and path+line. Note: 'idx:' handles are index-local and change on reindex; use documentationCommentId for a compiler-stable C# selector.")] string? symbolId = null,
        [Description("F# position mode only: workspace-relative physical .fsproj path. Required with targetFramework when the file has more than one type-check context.")] string? projectPath = null,
        [Description("F# position mode only: exact target framework. Required with projectPath when the file has more than one type-check context.")] string? targetFramework = null,
        [Description("Stable C# Roslyn declaration id (T:/M:/P:/F:/E:). Empty means omitted; whitespace-only is bad_request. Mutually exclusive with symbolId, name, and path+line. A successful response echoes the compiler-canonical id, which is safe to reuse directly; failures echo a bounded form of the caller input.")] string? documentationCommentId = null)
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
        int deadlineMs = Math.Clamp(timeoutMs, 500, DefinitionDeadlineMaxMs);
        var swSem = System.Diagnostics.Stopwatch.StartNew();
        var coldStartTiming = new SemanticColdStartTimingBox();
        using var semanticDeadline = new CancellationTokenSource(deadlineMs);
        if (!TryParseStringList(kinds, "kinds", out List<string>? parsedKinds,
                out string? kindsDetail))
            return StringListBadRequest("kinds", kindsDetail);
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
                        operation = "definition",
                        documentationCommentId = boundedId,
                        documentationCommentIdTruncated = truncated ? true : (bool?)null,
                        documentationCommentIdBytes = truncated
                            ? Json.Utf8Bytes(documentationCommentId)
                            : (int?)null,
                        detail = "documentationCommentId is compiler identity and cannot be combined with mode='indexed'; use mode='auto' or mode='semantic'.",
                        meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                    });
            }
            var (documentedTarget, documentedError) = ResolveDocumentationTarget(
                documentationCommentId, "definition", deadlineMs, implementationsOnly: false,
                DefinitionDeadlineMaxMs, semanticDeadline.Token, swSem, coldStartTiming);
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
            container = null;
            parsedKinds = null;
        }
        SymbolHit? resolvedHandleHit = null;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            resolvedHandleHit = hit;
            name = hit!.Name; path = hit.FilePath; line = hit.StartLine; column = 0;
            semanticDeclarationKey = OperatorDeclarationKey(hit);
            // The handle already disambiguated the symbol — caller kinds/container filters exist to
            // narrow a bare name, so applying them here can only wrongly suppress the resolved hit.
            parsedKinds = null; container = null;
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = NormalizePath(path);
            FileHit? indexedFile;
            using (var languageQueries = _manager.OpenQueries())
                indexedFile = languageQueries.FileByPath(path);
            if (indexedFile is { Language: "fs" })
            {
                if (resolvedHandleHit is not null)
                {
                    return Json.Serialize(new
                    {
                        error = "fsharp_semantic_position_required",
                        operation = "definition",
                        detail = "F# semantic definition requires an explicit path + line + column; idx handle resolution is not available yet.",
                    });
                }
                if (line <= 0)
                {
                    return Json.Serialize(new
                    {
                        error = "fsharp_semantic_position_required",
                        operation = "definition",
                        detail = "F# semantic definition requires path + line; bare-name and idx handle resolution are not available yet.",
                    });
                }
                if (mode == "indexed")
                {
                    return Json.Serialize(new
                    {
                        error = "fsharp_indexed_symbols_unavailable",
                        operation = "definition",
                        detail = "F# definition is compiler-semantic only; use mode='auto' or mode='semantic'.",
                    });
                }
                if (mode is not ("auto" or "semantic"))
                    return Json.Serialize(new { error = "bad_request", detail = "mode must be 'auto', 'semantic', or 'indexed'." });
                if (includeBody)
                {
                    return Json.Serialize(new
                    {
                        error = "fsharp_definition_body_unavailable",
                        operation = "definition",
                        detail = "F# semantic definition does not inline a declaration body; use source_context on the returned declaration range.",
                    });
                }
                return FSharpDefinition(path, line, column, projectPath, targetFramework,
                    timeoutMs);
            }
        }
        if (UnsupportedLanguageAtPath(path, "definition") is { } unsupportedLanguage)
            return unsupportedLanguage;
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'symbolId', 'name', or 'path'+'line'." });
        }

        string? failReason = null;
        if (mode is "auto" or "semantic")
        {
            var (target, hint) = ResolveSemanticTarget(name, container, parsedKinds, path, line, column);
            if (TestOnlySemanticFailureReason is { } forcedFailure)
            {
                failReason = forcedFailure;
                SemanticColdStartTiming timing = coldStartTiming.Publish(
                    swSem.ElapsedMilliseconds);
                _semantic.EmitTerminalTelemetry(
                    "definition", "degraded", forcedFailure, timing);
            }
            else if (target is { } t)
            {
                var (decl, reason, _, semanticPartialReason) = _semantic
                    .DefinitionAsync(t.Path, t.Line, t.Column, hint,
                        RemainingDeadlineMilliseconds(deadlineMs, swSem),
                        semanticDeclarationKey, semanticDeadline.Token,
                        documentationIdSnapshotIdentity, coldStartTiming)
                    .GetAwaiter().GetResult();
                if (decl is not null)
                {
                    if (requestedDocumentationCommentId is not null &&
                        !string.Equals(decl.DocumentationCommentId,
                            requestedDocumentationCommentId, StringComparison.Ordinal))
                    {
                        return DocumentationIdIdentityMismatch(documentationCommentId!,
                            requestedDocumentationCommentId,
                            decl.DocumentationCommentId,
                            "definition",
                            documentationIdCoverage,
                            deadlineMs,
                            swSem,
                            documentationIdResolutionMs,
                            coldStartTiming.Timing);
                    }
                    // Order declarations largest-span-first (partial stubs lose), path as the
                    // deterministic tie-break — so the body's declaration (d0) is always the first
                    // shown and never trimmed out of the displayed set.
                    var ordered = decl.Declarations
                        .OrderByDescending(d => d.EndLine - d.StartLine)
                        .ThenBy(d => d.Path, StringComparer.Ordinal)
                        .ToList();
                    var d0 = ordered.FirstOrDefault();
                    int totalDecls = ordered.Count;
                    var shown = ordered.Take(MaxDeclarationSites).ToList();
                    // Declarations serialized ONCE here (symbol omits them) and adaptively
                    // byte-bounded via WithListBudget — the semantic path no longer bypasses the
                    // central budget, so even a bodyless response with long paths stays under cap.
                    string BuildSemantic(object? body) => Json.WithListBudget(shown, (items, listTrunc) => new
                    {
                        name = name ?? decl.SymbolDisplay,
                        documentationCommentId = requestedDocumentationCommentId,
                        documentationIdCoverage = documentationIdCoverage is null
                            ? null
                            : DocumentationIdCoverageJson(documentationIdCoverage),
                        symbol = SemanticIdentityJson(decl),
                        declarations = items.Select(d => new { d.Path, d.StartLine, d.EndLine }),
                        declarationsTruncated = (listTrunc || totalDecls > MaxDeclarationSites) ? true : (bool?)null,
                        body,
                        partial = semanticPartialReason is not null ? true : (bool?)null,
                        partialReason = semanticPartialReason,
                        retryRecommended = SemanticRetryRecommended(semanticPartialReason)
                            ? true
                            : (bool?)null,
                        retryHint = SemanticRetryHint(semanticPartialReason, "definition",
                            deadlineMs, DefinitionDeadlineMaxMs),
                        timing = new
                        {
                            deadlineMs,
                            elapsedMs = swSem.ElapsedMilliseconds,
                            documentationIdResolutionMs,
                            semanticColdStart = coldStartTiming.Timing,
                        },
                        meta = Meta.From(_manager.Health(),
                            semanticPartialReason is not null ? "indexed" : "exact", "semantic"),
                    });
                    // Semantic spans come from live sources — pair them with live content.
                    object? MakeSemanticBody(int budget) =>
                        d0 is null ? null : BuildDeclarationBody(d0.Path, d0.StartLine, d0.EndLine, budget, preferLive: true);
                    return Json.WithCompleteSemanticIdentity(
                        SerializeBodyBounded(BuildSemantic,
                            includeBody ? MakeSemanticBody(bodyMaxBytes) : null,
                            MakeSemanticBody, bodyMaxBytes));
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
                    operation = "definition",
                    documentationCommentId = requestedDocumentationCommentId is null
                        ? null
                        : boundedId,
                    documentationCommentIdTruncated = requestedDocumentationCommentId is not null &&
                                                      truncated
                        ? true
                        : (bool?)null,
                    partialReason = failReason,
                    retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
                    retryHint = SemanticRetryHint(failReason, "definition", deadlineMs,
                        DefinitionDeadlineMaxMs),
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

        // Indexed fallback (name required).
        using var q = _manager.OpenQueries();
        string lookupName = name ?? "";
        if (lookupName.Length == 0 && path is not null)
        {
            var chain = q.SymbolAt(NormalizePath(path), line);
            lookupName = chain.Count > 0 ? chain[0].Name : "";
        }
        var hits = resolvedHandleHit is not null
            ? new List<SymbolHit> { resolvedHandleHit }
            : q.SearchSymbols(lookupName, "exact", parsedKinds, 100,
                includeGenerated: true, language: "cs");
        if (container is { } c)
        {
            hits = hits.Where(h =>
                (h.Container?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Ns?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        // Same primary-declaration rule as the semantic path: largest span first (partial
        // stubs lose), path as the deterministic tie-break — so the two paths agree.
        var primary = hits
            .OrderByDescending(h => h.EndLine - h.StartLine)
            .ThenBy(h => h.FilePath, StringComparer.Ordinal)
            .FirstOrDefault();
        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        // Indexed spans come from the index — pair them with index content (consistent even
        // when the working tree has drifted; freshness is reported on the body).
        object? MakeBody(int budget) =>
            primary is null ? null : BuildDeclarationBody(primary.FilePath, primary.StartLine, primary.EndLine, budget, preferLive: false);
        string Build(object? body) => Json.WithListBudget(hits, (items, truncated) => new
        {
            name = lookupName,
            declarations = items.Select(SymbolJson),
            body,
            partialReason = failReason,
            retryRecommended = SemanticRetryRecommended(failReason) ? true : (bool?)null,
            retryHint = SemanticRetryHint(failReason, "definition", deadlineMs,
                DefinitionDeadlineMaxMs),
            timing = SemanticColdStartTimingJson(coldStartTiming, deadlineMs, swSem,
                documentationIdResolutionMs),
            hint = items.Count == 0
                ? "No declaration found. Try search_symbol with match='substring', or the name may come from a package/generated source."
                : null,
            truncated,
            meta,
        });
        return SerializeBodyBounded(Build, includeBody ? MakeBody(bodyMaxBytes) : null, MakeBody, bodyMaxBytes);
    }

    /// <summary>Numbered source for a declaration span, byte-bounded — what lets
    /// definition(includeBody:true) replace a follow-up source_context call.
    /// preferLive=true reads the working-tree file (the semantic path computed its spans from
    /// live sources, so index content could mismatch them); preferLive=false uses index
    /// content (the indexed path's spans come from the index, so that pairing is consistent).
    /// Returns an { omitted, reason } object instead of null when no content is available.</summary>
    internal object BuildDeclarationBody(string path, int startLine, int endLine, int maxBytes, bool preferLive) // internal for tests (trp)
    {
        maxBytes = Math.Clamp(maxBytes, 512, 16 * 1024);

        string? content = null;
        string freshness = "index";
        if (preferLive
            && CodeNav.Core.WorkspacePaths.TryResolveInside(_manager.WorkspaceRoot, path, out string full)
            && File.Exists(full)
            && !CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(_manager.WorkspaceRoot, full))
        {
            try
            {
                content = File.ReadAllText(full);
                freshness = "live";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* fall through to index content */ }
        }
        if (content is null)
        {
            using var q = _manager.OpenQueries();
            content = q.ContentByPath(path);
            freshness = "index";
        }
        if (content is null)
        {
            return new { omitted = true, reason = "content_unavailable", path };
        }

        var lines = content.Split('\n');
        int start = Math.Max(1, startLine);
        // Span beyond the (possibly stale) content: report it honestly instead of an inverted
        // empty span (start past EOF is reachable when live spans are applied to shorter index content).
        if (start > lines.Length)
        {
            return new { omitted = true, reason = "span_beyond_content", path, contentLines = lines.Length, freshness };
        }
        int end = Math.Min(lines.Length, Math.Max(endLine, start));
        var numbered = new List<string>();
        long budget = maxBytes;
        bool truncated = false;
        for (int i = start; i <= end; i++)
        {
            string lineText = $"{i,5}| {lines[i - 1].TrimEnd('\r')}";
            int cost = Json.Utf8Bytes(lineText) + 1;
            if (budget - cost < 0) { truncated = true; break; }
            budget -= cost;
            numbered.Add(lineText);
        }
        if (numbered.Count == 0)
        {
            // Even the first line overflowed the budget — nothing to show, but say so.
            return new { omitted = true, reason = "first_line_exceeds_budget", path, freshness };
        }
        int lastIncluded = start + numbered.Count - 1;
        return new
        {
            path,
            startLine = start,
            endLine = lastIncluded,
            source = string.Join("\n", numbered),
            truncated,
            hint = truncated
                ? $"body cut at line {lastIncluded} — source_context('{path}', '{lastIncluded + 1}-{end}', maxBytes: {Json.HardBudgetBytes}) resumes where this stopped"
                : null,
            freshness,
        };
    }

    /// <summary>Re-serializes a body-carrying response until the ESCAPED payload fits the hard
    /// budget, halving the body budget each pass and dropping the body as the last resort.
    /// The line-loop budget counts raw chars; JSON escaping (quotes/backslashes) inflates, so
    /// only measuring the serialized length makes the budget contract actually hold.</summary>
    private static string SerializeBodyBounded(Func<object?, string> serialize, object? body, Func<int, object?> rebuildBody, int bodyMaxBytes)
    {
        string json = serialize(body);
        int budget = Math.Clamp(bodyMaxBytes, 512, 16 * 1024); // seed matches BuildDeclarationBody's own clamp
        while (Json.Utf8Bytes(json) > Json.HardBudgetBytes && body is not null)
        {
            budget /= 2;
            body = budget >= 512 ? rebuildBody(budget) : null;
            json = serialize(body);
        }
        return json;
    }
}
