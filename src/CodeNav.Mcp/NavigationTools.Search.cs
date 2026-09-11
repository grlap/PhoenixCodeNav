using System.ComponentModel;
using CodeNav.Core.Indexing;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: find_file, search_text, and search_symbol — indexed file, text, regex, and symbol discovery and their response shaping.
/// Does not own: file outlines or source reads (NavigationTools.Outline.cs; declaration bodies in NavigationTools.Definition.cs), or compiler-semantic navigation.
/// </summary>
public sealed partial class NavigationTools
{
    // ---------------------------------------------------------------- files / text

    [McpServerTool(Name = "find_file")]
    [Description("Find files by name or glob (e.g. 'InvoiceService.cs', '*Controller.cs', 'src/Billing/**/*.csproj'). A first-page exact-path miss may include pathSuggestions with up to three ranked pinned-index paths plus total/truncated coverage. Cheap path-only lookup; use search_symbol for code symbols.")]
    public string FindFile(
        [Description("File name or glob pattern. '*' matches any characters including '/'.")] string nameOrGlob,
        [Description("Exclude paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party files).")] string? excludePath = null,
        [Description("Max results (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call to fetch the next page.")] string? cursor = null,
        [Description("'default' uses CODENAV_DEFAULT_QUERY_SCOPE; 'all' overrides it; 'first_party' excludes known vendor/generated directory segments without changing the index. Empty means default; whitespace-only is bad_request.")] string queryScope = "default")
    {
        if (NotReady() is { } notReady) return notReady;
        var (scopeSelection, scopeError) = ResolveQueryScope(queryScope);
        if (scopeError is not null) return scopeError;
        (limit, int offset, _) = Page(limit, cursor);
        using var q = _manager.OpenQueries();
        var excludes = BuildExcludes(excludePath,
            scopeSelection!.Applied == "first_party");
        var files = q.FindFiles(nameOrGlob, limit + 1, excludes, offset);
        bool hadMore = files.Count > limit;
        if (hadMore) files.RemoveAt(files.Count - 1);
        PathSuggestionResult suggestions = offset == 0 && files.Count == 0
            ? q.SuggestFilePaths(nameOrGlob, excludePaths: excludes)
            : new([], 0);

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        // nextCursor resumes at offset + the count actually RETURNED: the byte-budget shrink drops the
        // page tail (keeping a prefix), so a fixed offset+limit would skip the dropped items (bug e2q).
        return Json.WithAuxiliaryListBudget(
            files,
            suggestions.Paths.ToList(),
            (items, truncated, suggestionPaths, suggestionsBudgetTruncated) => new
            {
                files = items.Select(f => new
                {
                    f.Path,
                    language = f.Language,
                    sizeBytes = f.Size,
                    lines = f.LineCount,
                    f.IsGenerated,
                }),
                nextCursor = (hadMore || truncated) ? $"o:{offset + items.Count}" : null,
                truncated,
                queryScope = scopeSelection,
                pathSuggestions = PathSuggestionsJson(
                    suggestions.Total,
                    suggestionPaths,
                    suggestionsBudgetTruncated),
                meta,
            });
    }

    [McpServerTool(Name = "search_text")]
    [Description("Ranked full-text search over indexed C# and F# source, Markdown, SQL, and project/solution/config files. WHOLE-WORD and token-based by default: 'Batch' does NOT match 'Batching'. For \\s / alternation / character classes set regex:true (.NET regex, line-based, scoped by pathGlob) — still not rust/ripgrep syntax; other file types need grep. Returns 'precise' hits (all query tokens on one line) by default; set partials='always' for weaker co-occurrence leads. Token mode grades at most 300 filtered candidate files and exposes filesScanned/filesAtLeast/partial when that cap is reached. Use context (or contextBefore/contextAfter) for surrounding lines, like grep -C/-B/-A. Best for literals, config keys, error messages, comments, documentation, and database scripts; only C#/F# participate in syntax or compiler semantics. Token/regex hits and structured suggestion samples carry orphaned:true only for .cs/.fs/.fsi files with no indexed compile owner; omitted for owned sources and text-only files. Indexed-model evidence only: neither the flag nor its absence proves native-build membership; results are never hidden by it.")]
    public string SearchText(
        [Description("Text to find. Multi-word queries are AND-ed by token; a line with all tokens is 'precise'.")] string query,
        [Description("Restrict to paths matching this glob (e.g. 'src/Billing/**').")] string? pathGlob = null,
        [Description("Exclude paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party source).")] string? excludePath = null,
        [Description("Restrict to files compiled by this project name.")] string? project = null,
        [Description("'all' (default), 'production' (exclude tests), or 'tests'.")] string scope = "all",
        [Description("Restrict by file language: cs | fs | md | sql | csproj | fsproj | sln | config.")] string? lang = null,
        [Description("Include generated files (default false).")] bool includeGenerated = false,
        [Description("Weaker 'some query tokens co-occur, not all on one line' leads: 'never' (default — precise only), 'auto' (fill space precise did not), or 'always'. filesMatchedAcrossLines still flags files where all tokens co-occur across lines.")] string partials = "never",
        [Description("Lines of context around each hit, like grep -C (0-20, default 0 = just the line). Applies both before and after.")] int context = 0,
        [Description("Context lines BEFORE each hit (grep -B); overrides 'context' when set.")] int? contextBefore = null,
        [Description("Context lines AFTER each hit (grep -A); overrides 'context' when set.")] int? contextAfter = null,
        [Description("Treat 'query' as a .NET regex instead of tokens — NOT rust/ripgrep syntax. LINE-BASED: a pattern spanning multiple lines matches NOTHING. Case-sensitive; prefix (?i) for insensitive. Scope with pathGlob; ReDoS-guarded (per-match timeout + overall budget) with honest coverage (filesTotal/budgetHit/timedOut). Overrides whole-word/partials.")] bool regex = false,
        [Description("Max hits (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        [Description("'default' uses CODENAV_DEFAULT_QUERY_SCOPE; 'all' overrides it; 'first_party' excludes known vendor/generated directory segments. Empty means default; whitespace-only is bad_request.")] string queryScope = "default")
    {
        if (NotReady() is { } notReady) return notReady;
        var (scopeSelection, scopeError) = ResolveQueryScope(queryScope);
        if (scopeError is not null) return scopeError;
        bool firstPartyScope = scopeSelection!.Applied == "first_party";
        (limit, int offset, _) = Page(limit, cursor);
        // Fail-safe: an unrecognized value falls back to the precise-only default, not the more
        // permissive "auto" — a typo must not silently reintroduce the noisy partial bucket.
        string mode = partials switch { "never" or "auto" or "always" => partials, _ => "never" };
        int ctxBefore = Math.Clamp(contextBefore ?? context, 0, 20);
        int ctxAfter = Math.Clamp(contextAfter ?? context, 0, 20);
        using var q = _manager.OpenQueries();
        if (regex)
            return RegexResponse(q, query, pathGlob, excludePath, firstPartyScope, project, scope,
                lang, includeGenerated, limit, offset, ctxBefore, ctxAfter, scopeSelection);
        var filter = new IndexQueries.TextFilter(
            PathGlob: pathGlob,
            Project: project,
            IncludeGenerated: includeGenerated,
            TestsOnly: scope switch { "tests" => true, "production" => false, _ => null },
            Lang: lang,
            ExcludePaths: BuildExcludes(excludePath, firstPartyScope));
        var result = q.SearchTextGraded(query, limit + 1, filter, maxCandidateFiles: 300, offset: offset, partialsMode: mode, ctxBefore: ctxBefore, ctxAfter: ctxAfter);
        var hits = result.Hits;
        bool hadMore = hits.Count > limit;
        if (hadMore) hits.RemoveAt(hits.Count - 1);

        // Only surface the file-level "tokens co-occur but not on one line" signal when the
        // page shows no precise hits — that is exactly when it changes the caller's read.
        bool anyPrecise = hits.Any(h => h.MatchKind == "precise");
        var acrossLines = !anyPrecise && result.FilesMatchedAcrossLines.Count > 0
            ? result.FilesMatchedAcrossLines.Take(10).ToList()
            : null;

        // Dead-end redirect (field evidence: an agent scoped pathGlob to the wrong dir, got a correct 0,
        // and fell back to manually reading files). The index knows where matches actually are — one
        // bounded unscoped probe turns "0 hits" into "0 HERE, but they exist THERE" or an honest
        // "absent everywhere". Only on a first-page total dead end, so the probe costs nothing normally.
        object? elsewhere = null;
        object? didYouMean = null;
        string? note = null;
        string? noteId = null; // a0b: stable id for the CAUSE behind `note` (catalog: NoteIds)
        // Token-VARIANT probe (field: searched 'Mode4', the code says 'Mode 4' -> 0 hits, agent fell
        // back to grep). Probes the split (Mode4 -> "Mode 4") and joined ("Mode 4" -> Mode4) forms; a
        // hit is SUGGESTED via didYouMean, never silently substituted. Called from EVERY zero-precise
        // first-page branch — review showed the join direction is starved if gated to total dead ends
        // only (common tokens co-occur across lines in any real repo, landing in the co-occur or
        // partial-leads branches instead). Counts are hedged: the probe grades <=100 candidate files.
        void ProbeVariants(bool replaceNote)
        {
            // Candidate order is a policy: token-FORM variants (Mode4 <-> "Mode 4") first —
            // they preserve the caller's spelling — then SPELLING near-misses (1ly, field:
            // didYouMean never fired on identifier typos because only form variants existed).
            // Spelling candidates only for a single bare identifier-ish token: multi-token or
            // punctuated queries aren't symbol names, and the vocabulary is the symbol index.
            var candidates = new List<(string Variant, string Kind)>();
            if (QueryVariants.SplitVariant(query) is { } split) candidates.Add((split, "tokenForm"));
            if (QueryVariants.JoinVariant(query) is { } join) candidates.Add((join, "tokenForm"));
            if (query.Length >= 4 && query.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                candidates.AddRange(q.NearMissSymbolNames(query, 3).Select(n => (n, "spelling")));
            }
            foreach (var (variant, kind) in candidates)
            {
                var vp = q.SearchTextGraded(variant, 5, new IndexQueries.TextFilter(IncludeGenerated: true),
                    maxCandidateFiles: 100, offset: 0, partialsMode: "never");
                if (vp.TotalPrecise > 0)
                {
                    // Structured samples (dzi): the redirect used to drop exactly the owner
                    // context main hits carry; samplePaths stays for compatibility.
                    var sampleHits = vp.Hits.Take(3).ToList();
                    var sampleOwners = OwningSymbols(q, sampleHits);
                    var sampleOrphans = OrphanedTextPaths(q, sampleHits);
                    didYouMean = new
                    {
                        query = variant,
                        variantKind = kind, // 'tokenForm' (Mode4 <-> "Mode 4") | 'spelling' (edit distance 1, probed)
                        preciseCount = vp.TotalPrecise,
                        samplePaths = vp.Hits.Select(h => h.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList(),
                        samples = sampleHits.Select(h => new
                        {
                            path = h.FilePath,
                            h.Line,
                            containingSymbol = sampleOwners.TryGetValue((h.FilePath, h.Line), out var cs) ? cs : null,
                            orphaned = sampleOrphans.Contains(h.FilePath) ? true : (bool?)null,
                        }),
                    };
                    string what = kind == "spelling"
                        ? $"the near-miss identifier '{variant}' (edit distance 1, from the symbol index)"
                        : $"the variant '{variant}'";
                    string msg = $"{what} has at least {vp.TotalPrecise} precise line(s) in the probed candidates (see didYouMean) — retry with that query"
                        + (vp.Hits.All(h => h.IsGenerated) ? " with includeGenerated:true (the probe hits are all in generated files)" : "") + ".";
                    note = replaceNote ? $"No file contains all query tokens together, but {msg}" : $"{note} Also: {msg}";
                    noteId = NoteIds.SearchDidYouMean; // the suggestion is the actionable cause now
                    break;
                }
            }
        }
        if (result.TotalPrecise == 0 && result.TotalPartial == 0 && offset == 0)
        {
            bool scoped = pathGlob is { Length: > 0 } || excludePath is { Length: > 0 } || firstPartyScope
                || project is not null || scope != "all" || lang is not null;
            if (IndexQueries.FtsQuery(query).Length == 0)
            {
                // Pure punctuation/whitespace: the tokenizer sees nothing, so a probe is pointless.
                note = "The query has no indexable tokens (letters/digits/underscore) — token search cannot match it. Use regex:true for punctuation patterns, or grep.";
            }
            else
            {
                var probe = q.SearchTextGraded(query, 5, new IndexQueries.TextFilter(IncludeGenerated: true),
                    maxCandidateFiles: 100, offset: 0, partialsMode: "never");
                if (probe.TotalPrecise > 0)
                {
                    // Structured samples (dzi): parity with main hits — the redirect used to
                    // ship bare path strings, dropping the containingSymbol context that makes
                    // a lead actionable. samplePaths stays for compatibility (their spec rows
                    // reference it). The co-occur branch below keeps paths only — its evidence
                    // is file-level (no line to anchor an owner on).
                    var probeSamples = probe.Hits.Take(3).ToList();
                    var probeOwners = OwningSymbols(q, probeSamples);
                    var probeOrphans = OrphanedTextPaths(q, probeSamples);
                    elsewhere = new
                    {
                        preciseCount = probe.TotalPrecise,
                        samplePaths = probe.Hits.Select(h => h.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList(),
                        samples = probeSamples.Select(h => new
                        {
                            path = h.FilePath,
                            h.Line,
                            containingSymbol = probeOwners.TryGetValue((h.FilePath, h.Line), out var cs) ? cs : null,
                            orphaned = probeOrphans.Contains(h.FilePath) ? true : (bool?)null,
                        }),
                    };
                    bool probeAllGenerated = probe.Hits.All(h => h.IsGenerated);
                    // "Outside your filters" covers every filter, not just pathGlob: a samplePath INSIDE
                    // the caller's glob means scope/lang/project/excludePath excluded it, not the glob.
                    note = scoped
                        ? $"0 hits within your filters, but at least {probe.TotalPrecise} precise line(s) exist outside them (see elsewhere.samplePaths; a sample inside your pathGlob means a different filter — scope/lang/project/excludePath — excluded it) — widen or relax filters."
                          + (probeAllGenerated ? " The probe hits are all in generated files — pass includeGenerated:true." : "")
                        : "The probe found matches only in generated files — pass includeGenerated:true.";
                    noteId = NoteIds.SearchElsewhereMatches;
                }
                else if (probe.TotalPartial > 0)
                {
                    // The tokens DO co-occur, just never on one line (the probe's partial signal —
                    // asserting "absent anywhere" here would be false; a precise line can also exist
                    // below the probe's candidate cap, hence "in the probed candidates").
                    elsewhere = new
                    {
                        coOccurringFiles = probe.FilesMatchedAcrossLines.Count,
                        samplePaths = probe.FilesMatchedAcrossLines.Take(3).ToList(),
                    };
                    note = $"No single line in the probed candidates has all query tokens, but they co-occur across lines in {probe.FilesMatchedAcrossLines.Count} file(s) (see elsewhere.samplePaths) — drop a token or retry with partials:'always'{(scoped ? " without your filters" : "")}.";
                    ProbeVariants(replaceNote: false); // a variant with PRECISE lines beats cross-line leads
                }
                else
                {
                    // Provably index-wide: an FTS AND over all files matched nothing (the inner LIMIT
                    // truncates results, not the match), so no file holds all tokens together.
                    note = "No file contains all query tokens together (whole-word: 'Batch' does not match 'Batching'). Check spelling, drop a token, try search_symbol match='substring' for C# identifiers, or grep for non-indexed file types.";
                    noteId = NoteIds.SearchAbsentEverywhere; // ProbeVariants upgrades this to did_you_mean when a suggestion lands
                    ProbeVariants(replaceNote: true);
                }
            }
        }
        else if (result.TotalPrecise == 0 && result.TotalPartial > 0 && mode == "never")
        {
            note = $"0 precise lines; {result.TotalPartial} weaker cross-line partial lead(s) exist — retry with partials:'always'.";
            if (offset == 0) ProbeVariants(replaceNote: false);
        }
        else if (result.TotalPrecise >= 1000 && offset == 0)
        {
            note = $"Common term: {result.TotalPrecise} precise lines in the scanned candidate set — "
                 + (pathGlob is { Length: > 0 } ? "narrow the glob further or add tokens to sharpen." : "scope with pathGlob or add tokens to sharpen.");
        }

        // Best-effort owning symbol per hit (feedback: jump from a text match to the owning
        // method/type without a follow-up symbol_at). Only .cs files carry symbols.
        var owners = OwningSymbols(q, hits);
        var orphans = OrphanedTextPaths(q, hits);

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            preciseCount = result.TotalPrecise,
            partialCount = result.TotalPartial,
            // Token grading is intentionally file-bounded after every caller filter has been
            // applied. One look-ahead row makes a clipped candidate set observable; counts are
            // lower bounds exactly when partialReason is present.
            filesScanned = result.CandidateFilesScanned,
            filesTotal = result.CandidateFilesTruncated
                ? (int?)null
                : result.CandidateFilesScanned,
            filesAtLeast = result.CandidateFilesAtLeast,
            budgetHit = result.CandidateFilesTruncated ? true : (bool?)null,
            countsAreLowerBounds = result.CandidateFilesTruncated ? true : (bool?)null,
            hits = items.Select(t => new
            {
                path = t.FilePath,
                t.Line,
                text = t.LineText,
                before = t.Before, // surrounding lines — present only when context was requested
                after = t.After,
                t.IsGenerated,
                matchKind = t.MatchKind,
                matched = t.MatchKind == "partial" ? t.Matched : null, // tokens only meaningful on partials
                containingSymbol = owners.TryGetValue((t.FilePath, t.Line), out var cs) ? cs : null,
                noise = IndexQueries.IsVendorPath(t.FilePath) ? true : (bool?)null, // under a vendored/generated dir
                orphaned = orphans.Contains(t.FilePath) ? true : (bool?)null,
            }),
            filesMatchedAcrossLines = acrossLines,
            elsewhere, // dead-end redirect: where matches DO exist when the filtered result is empty
            didYouMean, // dead-end token-form suggestion (Mode4 -> "Mode 4"); a suggestion, never a substitution
            nextCursor = (hadMore || truncated) ? $"o:{offset + items.Count}" : null,
            truncated,
            partial = result.CandidateFilesTruncated ? true : (bool?)null,
            partialReason = result.CandidateFilesTruncated
                ? "candidate_file_cap"
                : null,
            queryScope = scopeSelection,
            // Contextual, not verbatim (feedback: the fixed explainer was duplicated token waste; the
            // whole-word/precise semantics live in the tool description). Present only when it changes
            // the caller's next move: redirect, absent, partial-leads, or common-term steering.
            note,
            noteId, // a0b: stable id for the cause (only the cataloged causes carry one)
            meta,
        });
    }

    // Core owns source eligibility, shared with the overview's indexed orphan count.
    // Batch the pre-budget page once; serialization may shrink it without querying again.
    private static HashSet<string> OrphanedTextPaths(IndexQueries q, IEnumerable<TextHit> hits) =>
        q.OrphanedSourcePaths(hits.Select(hit => hit.FilePath).ToArray());

    // Best-effort owning symbol per .cs hit — BATCHED (one grouped query per ~40 keys) instead of one
    // InnermostSymbolAt point query per hit (9fr N+1: a full page issued up to ~100 queries).
    private static Dictionary<(string Path, int Line), string> OwningSymbols(IndexQueries q, IEnumerable<TextHit> hits)
    {
        var keys = hits.Where(h => h.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(h => (h.FilePath, h.Line)).Distinct().ToList();
        var owners = new Dictionary<(string, int), string>();
        foreach (var (key, sym) in q.InnermostSymbolsAt(keys))
            owners[key] = sym.Container is { Length: > 0 } c ? $"{c}.{sym.Name}" : sym.Name;
        return owners;
    }

    // Regex mode for search_text (Batch B): .NET regex over indexed content, FTS-narrowed by required
    // literals when possible, else a bounded scan; ReDoS-guarded. Mirrors the token response shape
    // (context lines, containingSymbol, noise) so callers get one consistent hit format.
    private string RegexResponse(IndexQueries q, string pattern, string? pathGlob, string? excludePath,
        bool firstPartyScope, string? project, string scope, string? lang, bool includeGenerated,
        int limit, int offset, int ctxBefore, int ctxAfter,
        QueryScopeSelection queryScope)
    {
        var filter = new IndexQueries.TextFilter(
            PathGlob: pathGlob, Project: project, IncludeGenerated: includeGenerated,
            TestsOnly: scope switch { "tests" => true, "production" => false, _ => null },
            Lang: lang, ExcludePaths: BuildExcludes(excludePath, firstPartyScope));
        var res = q.SearchRegex(pattern, filter, maxCandidateFiles: 300, offset, limit + 1, ctxBefore, ctxAfter);
        if (res.Error is not null)
            return Json.Serialize(new { error = "bad_request", detail = res.Error, meta = Meta.From(_manager.Health(), "indexed", "text") });

        var hits = res.Hits;
        bool hadMore = hits.Count > limit;
        if (hadMore) hits.RemoveAt(hits.Count - 1);

        // Owning symbol per .cs hit — parity with token search_text (feedback loved containingSymbol).
        var owners = OwningSymbols(q, hits);
        var orphans = OrphanedTextPaths(q, hits);

        // Contextual note — only when it changes the caller's next move (timeout, clipped coverage, or
        // a zero-hit that needs the line-based/case-sensitivity reminder). Silent on a clean success.
        bool scoped = pathGlob is { Length: > 0 } || excludePath is { Length: > 0 } || firstPartyScope
            || project is not null || scope != "all" || lang is not null;
        bool coverageClipped = res.FilesTotal > res.FilesScanned;
        string? note = res.TimedOut
            ? "PARTIAL: the scan hit its time budget. Narrow with pathGlob or add a distinctive whole-word literal (e.g. \\bWord\\b) so FTS can pre-narrow."
            : coverageClipped
                ? $"PARTIAL coverage: scanned {res.FilesScanned} of {res.FilesTotal} candidate files (cap) — narrow with pathGlob or add a whole-word literal (\\bWord\\b) so FTS can pre-narrow."
                : res.TotalMatches == 0
                    ? ".NET regex is LINE-BASED — a pattern spanning multiple lines matches NOTHING — and case-sensitive without (?i)."
                      + (scoped ? " The scan honored your filters; retry without them to check elsewhere." : "")
                    : null;

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            mode = "regex",
            matchCount = res.TotalMatches,
            filesScanned = res.FilesScanned,
            // Coverage honesty: candidates in scope BEFORE the cap; budgetHit means coverage was clipped
            // (cap or timeout), so "0 matches" or a small count is NOT proof of absence.
            filesTotal = res.FilesTotal,
            budgetHit = coverageClipped ? true : (bool?)null,
            narrowed = res.Narrowed,                 // FTS-pre-narrowed by required literals, vs a full scan
            narrowedOn = res.Literals,               // WHICH whole-token literals narrowed (null on a scan)
            timedOut = res.TimedOut ? true : (bool?)null,
            hits = items.Select(t => new
            {
                path = t.FilePath,
                t.Line,
                text = t.LineText,
                before = t.Before,
                after = t.After,
                t.IsGenerated,
                containingSymbol = owners.TryGetValue((t.FilePath, t.Line), out var cs) ? cs : null,
                noise = IndexQueries.IsVendorPath(t.FilePath) ? true : (bool?)null,
                orphaned = orphans.Contains(t.FilePath) ? true : (bool?)null,
            }),
            nextCursor = (hadMore || truncated) ? $"o:{offset + items.Count}" : null,
            truncated,
            queryScope,
            note,
            meta,
        });
    }

    // ---------------------------------------------------------------- symbols

    [McpServerTool(Name = "search_symbol")]
    [Description("Find C# and F# declared symbols by name across the workspace (types, methods, properties, modules, functions, values, union cases...). Exact-name type declarations receive a soft relevance preference over same-named members. A first-page empty result remains successful and reports existsUnfiltered plus appliedFilters; when filters hid declarations, unfilteredKinds says what exists. C# plus F# .fs/.fsi path scopes are indexed; .fsx and other text-only languages are refused when exclusive and disclosed when mixed. Failed or truncated FCS parse contexts and unavailable/unevaluated F# project options make results explicitly partial and expose fsharpParseCoverage or fsharpProjectOptionCoverage; stored F# indexing processes at most 64 deterministic owner/TFM contexts per file, reserving one per valid compile owner while capacity remains. Owner coverage distinguishes no retained context from some-but-not-all contexts and aggregates owner incidences per affected file, not distinct projects. Ordinary SDK/import limitations remain advisory structured coverage rather than making every search partial. Scope with pathGlob / excludePath / namespace (e.g. excludePath='3rdparty/**' to drop vendored source). Hits carry an 'orphaned' flag (present only when true) for files in NO project's compile set — dead code the compiler never builds (Compile Include globs expanded, Compile Remove honored).")]
    public string SearchSymbol(
        [Description("Symbol name. Match behavior set by 'match'. Empty (or '*') with a 'namespace' or 'pathGlob' ENUMERATES that scope's symbols instead — kind-filterable, paged.")] string query = "",
        [Description("Comma-separated kind filter or JSON-array encoded string. C#: class,interface,struct,record,record_struct,enum,delegate,method,constructor,property,field,event,enum_member. F#: namespace,module,class,interface,struct,record,union,type,exception,delegate,function,value,method,constructor,property,field,union_case,enum_member. Null or an empty string = all; whitespace-only values, empty CSV items, and empty JSON arrays are bad_request.")] string? kinds = null,
        [Description("'auto' (exact, then prefix, then substring), 'exact', 'prefix', or 'substring'.")] string match = "auto",
        [Description("Include symbols in generated files (default false).")] bool includeGenerated = false,
        [Description("Restrict to file paths matching this glob (e.g. 'SOAPAPI/**'); a bare name matches at any depth.")] string? pathGlob = null,
        [Description("Exclude file paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party source).")] string? excludePath = null,
        [Description("Restrict to a namespace subtree: the exact namespace or anything nested under it (e.g. 'ExactTarget.Integration'). Distinct from a containing type.")] string? @namespace = null,
        [Description("Optional language scope: 'csharp' or 'fsharp'. Empty searches both and reports mixed-language coverage.")] string? lang = null,
        [Description("Max results (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call.")] string? cursor = null,
        [Description("'default' uses CODENAV_DEFAULT_QUERY_SCOPE; 'all' overrides it; 'first_party' excludes known vendor/generated directory segments. Empty means default; whitespace-only is bad_request.")] string queryScope = "default")
    {
        if (NotReady() is { } notReady) return notReady;
        var (scopeSelection, scopeError) = ResolveQueryScope(queryScope);
        if (scopeError is not null) return scopeError;
        bool firstPartyScope = scopeSelection!.Applied == "first_party";
        query ??= "";
        const string routingPrefix = "select:";
        string trimmedQuery = query.TrimStart();
        if (trimmedQuery.StartsWith(routingPrefix, StringComparison.OrdinalIgnoreCase) &&
            (trimmedQuery.Length == routingPrefix.Length || trimmedQuery[routingPrefix.Length] != ':'))
        {
            return Json.Serialize(new
            {
                error = "malformed_query",
                hint = "Remove the 'select:' routing prefix and pass only the symbol name.",
                meta = Meta.From(_manager.Health(), "indexed", "syntax"),
            });
        }
        string? indexedLanguage = lang?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "csharp" => "cs",
            "fsharp" => "fs",
            _ => "invalid",
        };
        if (indexedLanguage == "invalid")
        {
            return Json.Serialize(new
            {
                error = "bad_request",
                field = "lang",
                value = lang,
                validValues = new[] { "csharp", "fsharp" },
                detail = "lang must be 'csharp' or 'fsharp'.",
                meta = Meta.From(_manager.Health(), "indexed", "syntax"),
            });
        }
        string languageScope = indexedLanguage switch
        {
            "cs" => "csharp",
            "fs" => "fsharp",
            _ => "all",
        };
        (limit, int offset, string? cursorMode) = Page(limit, cursor);
        if (!TryParseStringList(kinds, "kinds", out List<string>? kindList,
                out string? kindsDetail))
            return StringListBadRequest("kinds", kindsDetail);
        using var q = _manager.OpenQueries();
        var excludes = BuildExcludes(excludePath, firstPartyScope);
        if (pathGlob is { Length: > 0 } exactPath &&
            exactPath.IndexOfAny(new[] { '*', '?', '[' }) < 0 &&
            q.FileByPath(NormalizePath(exactPath)) is { } exactFile &&
            (exactFile.Language is not ("cs" or "fs") || IsFSharpScriptPath(exactPath)))
        {
            return UnsupportedLanguage(exactPath,
                IsFSharpScriptPath(exactPath) ? "fsx" : exactFile.Language,
                "search_symbol");
        }
        List<string> scopeLanguages = pathGlob is { Length: > 0 }
            ? q.SourceLanguagesForPathScope(pathGlob, excludes, includeGenerated)
            : [];
        List<string> unsupportedScopeLanguages = scopeLanguages
            .Where(language =>
                !language.Equals("cs", StringComparison.OrdinalIgnoreCase) &&
                !language.Equals("fs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        bool scopeHasSupportedLanguage = scopeLanguages.Any(language =>
            language.Equals("cs", StringComparison.OrdinalIgnoreCase) ||
            language.Equals("fs", StringComparison.OrdinalIgnoreCase));
        if (pathGlob is { Length: > 0 } unsupportedScope && !scopeHasSupportedLanguage &&
            unsupportedScopeLanguages.Count > 0)
        {
            return UnsupportedLanguage(unsupportedScope,
                string.Join(',', unsupportedScopeLanguages), "search_symbol");
        }
        bool unsupportedLanguageFilesSkipped = indexedLanguage is null &&
            scopeHasSupportedLanguage &&
            unsupportedScopeLanguages.Count > 0;
        FSharpParseCoverage fsharpParseCoverage = indexedLanguage == "cs"
            ? new FSharpParseCoverage(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])
            : q.FSharpParseCoverageForScope(pathGlob, excludes, includeGenerated);
        bool fsharpParseIncomplete = fsharpParseCoverage.IsIncomplete;
        string[] blockingOptionReasons = fsharpParseCoverage.OptionPartialReasons
            .Where(reason => !reason.Equals("fsharp_project_options_imported",
                StringComparison.Ordinal))
            .ToArray();
        var partialReasons = new List<string>();
        if (fsharpParseCoverage.FailedFiles > 0)
            partialReasons.Add("fsharp_parse_failed");
        if (fsharpParseCoverage.TruncatedFiles > 0)
            partialReasons.Add("fsharp_parse_contexts_truncated");
        partialReasons.AddRange(blockingOptionReasons);
        if (unsupportedLanguageFilesSkipped)
            partialReasons.Add("unsupported_language_files_skipped");
        partialReasons = partialReasons.Distinct(StringComparer.Ordinal).ToList();

        List<SymbolHit> hits;
        string effectiveMatch = match;
        if (string.IsNullOrWhiteSpace(query) || query.Trim() == "*")
        {
            // Enumeration mode (field evidence: an agent passed kinds+namespace with no name expecting
            // the namespace's classes, got a silent [] and had to guess a name). An empty name within a
            // namespace/pathGlob scope lists that scope; without a scope it is an explicit error —
            // a silent empty result is the one answer that helps nobody.
            if (!(@namespace is { Length: > 0 } || pathGlob is { Length: > 0 }))
            {
                return Json.Serialize(new
                {
                    error = "bad_request",
                    detail = "An empty name enumerates a scope — provide 'namespace' or 'pathGlob' (optionally 'kinds').",
                    meta = Meta.From(_manager.Health(), "indexed", "syntax"),
                });
            }
            hits = q.SearchSymbols("", "prefix", kindList, limit + 1, includeGenerated,
                offset, pathGlob, excludes, @namespace, language: indexedLanguage);
            effectiveMatch = "enumerate";
        }
        else if (match == "auto" && cursorMode is "exact" or "prefix" or "substring")
        {
            // Continue the mode resolved on page 1. Re-running the exact->prefix->substring ladder on a
            // later page fails: the fallback is gated to offset==0, so exact-at-offset returns [] and the
            // page comes back empty, losing the prefix/substring results (bug cli).
            effectiveMatch = cursorMode;
            hits = q.SearchSymbols(query, cursorMode, kindList, limit + 1, includeGenerated,
                offset, pathGlob, excludes, @namespace, language: indexedLanguage);
        }
        else if (match == "auto")
        {
            hits = q.SearchSymbols(query, "exact", kindList, limit + 1, includeGenerated,
                offset, pathGlob, excludes, @namespace, language: indexedLanguage);
            effectiveMatch = "exact";
            if (hits.Count == 0 && offset == 0)
            {
                hits = q.SearchSymbols(query, "prefix", kindList, limit + 1,
                    includeGenerated, offset, pathGlob, excludes, @namespace,
                    language: indexedLanguage);
                effectiveMatch = "prefix";
            }
            if (hits.Count == 0 && offset == 0)
            {
                hits = q.SearchSymbols(query, "substring", kindList, limit + 1,
                    includeGenerated, offset, pathGlob, excludes, @namespace,
                    language: indexedLanguage);
                effectiveMatch = "substring";
            }
        }
        else
        {
            hits = q.SearchSymbols(query, match, kindList, limit + 1, includeGenerated,
                offset, pathGlob, excludes, @namespace, language: indexedLanguage);
        }

        // Flag hits in files that no project compiles — the "really compiled?" signal grep can't give
        // (phoenix has the compile graph; 3tz expands Include globs + honors Remove). Additive ONLY:
        // the hit is still returned and tagged orphaned:true, never hidden — residual gaps (shared
        // .projitems, props globs, ignored Conditions) mean hiding could still bury live code.
        if (hits.Count > 0)
        {
            var orphaned = q.OrphanedPaths(hits.Select(h => h.FilePath).ToList());
            if (orphaned.Count > 0)
                hits = hits.Select(h => orphaned.Contains(h.FilePath) ? h with { IsOrphaned = true } : h).ToList();
        }

        bool? existsUnfiltered = null;
        List<string>? unfilteredKinds = null;
        object? appliedFilters = null;
        string? zeroResultReason = null;
        string? zeroResultPrefix = null;
        List<SymbolHit> zeroResultSuggestions = [];
        bool zeroResultProbeTruncated = false;
        int zeroResultProbedCount = 0;
        if (hits.Count == 0 && offset == 0 &&
            !string.IsNullOrWhiteSpace(query) && query.Trim() != "*")
        {
            List<string> matchingKinds = q.UnfilteredSymbolKinds(
                query, effectiveMatch, indexedLanguage);
            existsUnfiltered = matchingKinds.Count > 0;
            unfilteredKinds = matchingKinds.Count > 0 ? matchingKinds : null;

            bool hasAppliedFilters =
                kindList is { Count: > 0 } ||
                !includeGenerated ||
                pathGlob is { Length: > 0 } ||
                excludePath is { Length: > 0 } ||
                firstPartyScope ||
                @namespace is { Length: > 0 } ||
                indexedLanguage is not null;
            if (hasAppliedFilters)
            {
                appliedFilters = new
                {
                    kinds = kindList is { Count: > 0 } ? string.Join(',', kindList) : null,
                    includeGenerated = includeGenerated ? (bool?)null : false,
                    pathGlob,
                    excludePath,
                    queryScope = firstPartyScope ? "first_party" : null,
                    @namespace,
                    lang = indexedLanguage is null ? null : languageScope,
                };
            }

            zeroResultReason = existsUnfiltered == true ? "filtered_out" : "symbol_not_found";
            zeroResultPrefix = query.Trim();
            if (zeroResultPrefix.Length > 1)
            {
                List<SymbolHit> probedSuggestions = q.SearchSymbols(
                    zeroResultPrefix[..Math.Min(2, zeroResultPrefix.Length)], "prefix", kindList,
                    limit + 1, includeGenerated, pathGlob: pathGlob,
                    excludePaths: excludes, ns: @namespace, language: indexedLanguage);
                zeroResultProbeTruncated = probedSuggestions.Count > limit;
                if (zeroResultProbeTruncated)
                    probedSuggestions.RemoveAt(probedSuggestions.Count - 1);
                zeroResultProbedCount = probedSuggestions.Count;
                zeroResultSuggestions = probedSuggestions
                    .GroupBy(hit => (hit.Name, hit.Kind))
                    .Select(group => group.First())
                    .Take(limit)
                    .ToList();
            }
        }

        bool hadMore = hits.Count > limit;
        if (hadMore) hits.RemoveAt(hits.Count - 1);

        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        object BuildZeroResult(List<SymbolHit> suggestions, bool budgetTruncated) => new
        {
            reason = zeroResultReason,
            effectiveScope = new
            {
                language = languageScope,
                availableLanguages = scopeLanguages.Count > 0 ? scopeLanguages : null,
                pathGlob,
                excludePath,
                queryScope = scopeSelection.Applied,
                @namespace,
            },
            suggestions = suggestions.Count > 0
                ? suggestions.Select(hit => new
                {
                    hit.Name,
                    hit.Kind,
                    path = hit.FilePath,
                    rationale = "same-prefix indexed declaration in the effective scope",
                })
                : null,
            suggestionCoverage = zeroResultPrefix is { Length: > 1 }
                ? new
                {
                    probed = zeroResultProbedCount,
                    probeLimit = limit,
                    returned = suggestions.Count,
                    probeTruncated = zeroResultProbeTruncated ? true : (bool?)null,
                    truncated = zeroResultProbeTruncated ||
                                zeroResultSuggestions.Count > suggestions.Count ||
                                zeroResultProbedCount > zeroResultSuggestions.Count
                        ? true
                        : (bool?)null,
                    budgetTruncated = budgetTruncated ? true : (bool?)null,
                }
                : null,
            retry = suggestions.Count > 0
                ? new
                {
                    tool = "search_symbol",
                    arguments = new
                    {
                        query = suggestions[0].Name,
                        match = "exact",
                        lang = indexedLanguage is null ? null : languageScope,
                        kinds = kindList is { Count: > 0 }
                            ? string.Join(',', kindList)
                            : null,
                        includeGenerated,
                        pathGlob,
                        excludePath,
                        @namespace,
                        queryScope = scopeSelection.Applied,
                    },
                }
                : null,
        };

        object BuildResponse(List<SymbolHit> items, bool truncated, object? zeroResult) => new
        {
            matchMode = effectiveMatch,
            languageScope,
            symbols = items.Select(SymbolJson),
            existsUnfiltered,
            unfilteredKinds,
            appliedFilters,
            zeroResult,
            // Carry the resolved mode so a later page continues it (bug cli); resume at the returned
            // count so a byte-budget shrink doesn't skip the dropped tail (bug e2q).
            nextCursor = (hadMore || truncated) ? $"o:{offset + items.Count}:{effectiveMatch}" : null,
            truncated,
            partial = unsupportedLanguageFilesSkipped || fsharpParseIncomplete
                ? true
                : (bool?)null,
            partialReason = partialReasons.Count > 0
                ? string.Join("; ", partialReasons)
                : null,
            partialReasons = partialReasons.Count > 0 ? partialReasons : null,
            queryScope = scopeSelection,
            fsharpParseCoverage = fsharpParseCoverage.FailedFiles > 0 ||
                                  fsharpParseCoverage.TruncatedFiles > 0
                ? new
                {
                    fsharpParseCoverage.FailedFiles,
                    fsharpParseCoverage.PartialFailureFiles,
                    fsharpParseCoverage.TotalFailureFiles,
                    fsharpParseCoverage.TruncatedFiles,
                    fsharpParseCoverage.FailedContexts,
                    fsharpParseCoverage.TotalContexts,
                    fsharpParseCoverage.ProcessedContexts,
                    fsharpParseCoverage.TruncatedContexts,
                    fsharpParseCoverage.TruncatedOwnerProjects,
                    fsharpParseCoverage.UnrepresentedOwnerProjects,
                    fsharpParseCoverage.PartiallyTruncatedOwnerProjects,
                }
                : null,
            fsharpProjectOptionCoverage = fsharpParseCoverage.ProjectOptionAffectedFiles > 0
                ? new
                {
                    affectedFiles = fsharpParseCoverage.ProjectOptionAffectedFiles,
                    projectFileContexts = fsharpParseCoverage.OptionProjectCount,
                    failedProjectFileContexts = fsharpParseCoverage.FailedOptionProjects,
                    partialProjectFileContexts = fsharpParseCoverage.PartialOptionProjects,
                    reasons = fsharpParseCoverage.OptionPartialReasons,
                    advisoryOnly = blockingOptionReasons.Length == 0 ? true : (bool?)null,
                }
                : null,
            scopeLanguages = scopeLanguages.Count > 0 ? scopeLanguages : null,
            unsupportedLanguages = unsupportedScopeLanguages.Count > 0
                ? unsupportedScopeLanguages
                : null,
            // Steer the follow-up (feedback: nothing nudged toward references after a symbol
            // hit). First page only — repeating it on cursored pages just burns budget.
            hint = items.Count > 0 && cursor is null
                ? "Next: source_context(path, 'startLine-endLine') for indexed source. C#: references(name) for usages or definition(name, includeBody:true). F#: definition(path+line+column) for compiler-backed declaration evidence."
                : null,
            meta,
        };

        if (zeroResultReason is not null)
        {
            return Json.WithListBudget(zeroResultSuggestions,
                (suggestions, suggestionsTruncated) => BuildResponse([], false,
                    BuildZeroResult(suggestions, suggestionsTruncated)),
                TestOnlySearchSymbolResponseMaxBytes);
        }
        return Json.WithListBudget(hits,
            (items, truncated) => BuildResponse(items, truncated, null),
            TestOnlySearchSymbolResponseMaxBytes);
    }
}
