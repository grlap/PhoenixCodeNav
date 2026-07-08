using System.ComponentModel;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: the MCP tool surface — argument handling, result shaping, budgets, and
/// confidence/freshness metadata. Results carry the confidence they earn: "exact" for
/// compiler-backed semantic answers, "indexed" for index/syntax-backed facts, and
/// "heuristic" for naming/text inferences (implementations fallback, related_tests).
/// Does not own: index building/queries (CodeNav.Core).
/// </summary>
[McpServerToolType]
public sealed partial class NavigationTools
{
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;

    public NavigationTools(IndexManager manager, SemanticService semantic)
    {
        _manager = manager;
        _semantic = semantic;
    }

    private static readonly string[] TypeKinds = { "class", "interface", "struct", "record", "record_struct", "enum", "delegate" };

    // ---------------------------------------------------------------- capabilities / overview

    [McpServerTool(Name = "server_capabilities")]
    [Description("Reports supported languages, available tools, index status, and response budgets. Call this first if unsure what is available or whether the index is ready.")]
    public string ServerCapabilities()
    {
        var h = _manager.Health();
        return Json.Serialize(new
        {
            server = "phoenixCodeNav",
            version = "0.1.0",
            languages = new[] { "csharp" },
            navigationLayers = new[] { "text", "syntax", "semantic" },
            tools = new[]
            {
                "server_capabilities", "repo_overview", "find_file", "search_text", "outline",
                "source_context", "search_symbol", "symbol_at", "definition", "references",
                "implementations", "callers", "callees", "type_hierarchy", "related_tests",
                "dependency_path", "config_lookup", "batch_outline", "context_pack", "impact",
                "project_graph", "projects_containing", "refresh_index",
            },
            budgets = new { softBytes = Json.SoftBudgetBytes, hardBytes = Json.HardBudgetBytes, defaultLimit = 20 },
            confidenceModel = new
            {
                exact = "compiler-verified (Roslyn semantic resolution)",
                indexed = "index/syntax-backed, not compiler-verified — confirm with source_context before edits",
                heuristic = "naming/text inference — a lead, verify before relying on it",
            },
            semantic = new
            {
                engine = "roslyn-adhoc (no MSBuild dependency)",
                frameworkRefsAvailable = _semantic.FrameworkRefsAvailable,
                exactTools = new[] { "definition", "references", "implementations" },
                note = "Exact results are scoped to loaded candidate clusters; coverage/partial fields report anything skipped.",
            },
            index = new
            {
                state = h.State,
                h.IndexVersion,
                h.IndexedAtUtc,
                h.LastRefreshUtc,
                h.PendingChanges,
                h.Error,
                dbBytes = h.DbBytes,
                workspaceRoot = h.WorkspaceRoot,
            },
        });
    }

    [McpServerTool(Name = "repo_overview")]
    [Description("Compact workspace map: project/solution/file/symbol counts, styles, target frameworks, and index freshness. Call before starting code work.")]
    public string RepoOverview()
    {
        if (NotReady() is { } notReady) return notReady;
        using var q = _manager.OpenQueries();
        var stats = q.Overview();
        var h = _manager.Health();

        // Live HEAD lookup (occasional call — not in the per-response meta). When it differs
        // from the indexed commit, a branch switch / pull is still reconciling; indexStatus
        // already reports the transient lag.
        string? headCommit = _manager.CurrentHeadCommit();
        object? git = h.IndexedCommit is null && headCommit is null
            ? null
            : new
            {
                indexedCommit = h.IndexedCommit,
                indexedBranch = h.IndexedBranch,
                headCommit,
                headMatchesIndex = headCommit is not null && h.IndexedCommit is not null
                    && string.Equals(headCommit, h.IndexedCommit, StringComparison.OrdinalIgnoreCase),
            };

        return Json.Serialize(new
        {
            workspaceRoot = h.WorkspaceRoot,
            projects = new
            {
                total = stats.Projects,
                legacyStyle = stats.LegacyProjects,
                sdkStyle = stats.SdkProjects,
                test = stats.TestProjects,
            },
            solutions = stats.Solutions,
            csFiles = stats.CsFiles,
            totalLines = stats.TotalLines,
            symbols = stats.Symbols,
            generatedFiles = stats.GeneratedFiles,
            // .cs files in no project's compile set — a best-effort "likely dead code" count (the
            // compile-graph signal grep lacks). Over-counts: wildcard/shared/props <Compile> includes
            // aren't expanded, so some live files are included. Per hit: search_symbol's orphaned flag.
            orphanedFiles = stats.OrphanedFiles,
            targetFrameworks = stats.TfmBreakdown,
            // Vendored/generated directory globs detected in the index — pass to search_symbol /
            // search_text excludePath (or firstPartyOnly) to drop third-party noise. [] when none.
            suggestedExcludes = q.SuggestedExcludes(),
            git,
            meta = Meta.From(h, "indexed", "text"),
        });
    }

    // ---------------------------------------------------------------- files / text

    [McpServerTool(Name = "find_file")]
    [Description("Find files by name or glob (e.g. 'InvoiceService.cs', '*Controller.cs', 'src/Billing/**/*.csproj'). Cheap path-only lookup; use search_symbol for code symbols.")]
    public string FindFile(
        [Description("File name or glob pattern. '*' matches any characters including '/'.")] string nameOrGlob,
        [Description("Exclude paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party files).")] string? excludePath = null,
        [Description("Max results (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call to fetch the next page.")] string? cursor = null)
    {
        if (NotReady() is { } notReady) return notReady;
        (limit, int offset) = Page(limit, cursor);
        using var q = _manager.OpenQueries();
        var excludes = excludePath is { Length: > 0 } ex ? new[] { ex } : null;
        var files = q.FindFiles(nameOrGlob, limit + 1, excludes, offset);
        string? next = files.Count > limit ? $"o:{offset + limit}" : null;
        if (next is not null) files.RemoveAt(files.Count - 1);

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(files, (items, truncated) => new
        {
            files = items.Select(f => new { f.Path, sizeBytes = f.Size, lines = f.LineCount, f.IsGenerated }),
            nextCursor = next,
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "search_text")]
    [Description("Ranked full-text search over the indexed C# surface only (.cs plus .csproj/.sln/config). Does NOT see .sql, .js, or other file types, and is token-based, not regex — use grep for those and for regex/alternation. Hits graded 'precise' (all tokens on the line) or 'partial' (some; a lead). For literals, config keys, error messages, comments. For code identifiers prefer search_symbol/definition/references.")]
    public string SearchText(
        [Description("Text to find. Multi-word queries are AND-ed by token; a line with all tokens is 'precise'.")] string query,
        [Description("Restrict to paths matching this glob (e.g. 'src/Billing/**').")] string? pathGlob = null,
        [Description("Exclude paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party source).")] string? excludePath = null,
        [Description("Drop hits under known vendor/generated dir names (3rdparty, vendor, external, generated...) at ANY depth. Matches the per-hit noise flag; convenience over excludePath.")] bool firstPartyOnly = false,
        [Description("Restrict to files compiled by this project name.")] string? project = null,
        [Description("'all' (default), 'production' (exclude tests), or 'tests'.")] string scope = "all",
        [Description("Restrict by file language: cs | csproj | sln | config.")] string? lang = null,
        [Description("Include generated files (default false).")] bool includeGenerated = false,
        [Description("Partial (some-token) hits: 'auto' (default — only fill space precise did not), 'never', or 'always'.")] string partials = "auto",
        [Description("Max hits (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call.")] string? cursor = null)
    {
        if (NotReady() is { } notReady) return notReady;
        (limit, int offset) = Page(limit, cursor);
        string mode = partials is "never" or "always" ? partials : "auto";
        using var q = _manager.OpenQueries();
        var filter = new IndexQueries.TextFilter(
            PathGlob: pathGlob,
            Project: project,
            IncludeGenerated: includeGenerated,
            TestsOnly: scope switch { "tests" => true, "production" => false, _ => null },
            Lang: lang,
            ExcludePaths: BuildExcludes(excludePath, firstPartyOnly));
        var result = q.SearchTextGraded(query, limit + 1, filter, maxCandidateFiles: 300, offset: offset, partialsMode: mode);
        var hits = result.Hits;
        string? next = hits.Count > limit ? $"o:{offset + limit}" : null;
        if (next is not null) hits.RemoveAt(hits.Count - 1);

        // Only surface the file-level "tokens co-occur but not on one line" signal when the
        // page shows no precise hits — that is exactly when it changes the caller's read.
        bool anyPrecise = hits.Any(h => h.MatchKind == "precise");
        var acrossLines = !anyPrecise && result.FilesMatchedAcrossLines.Count > 0
            ? result.FilesMatchedAcrossLines.Take(10).ToList()
            : null;

        // Best-effort owning symbol per hit (feedback: jump from a text match to the owning
        // method/type without a follow-up symbol_at). Only .cs files carry symbols.
        var owners = new Dictionary<(string Path, int Line), string>();
        foreach (var h in hits)
        {
            if (!h.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (owners.ContainsKey((h.FilePath, h.Line))) continue;
            var sym = q.InnermostSymbolAt(h.FilePath, h.Line);
            if (sym is not null)
            {
                owners[(h.FilePath, h.Line)] = sym.Container is { Length: > 0 } c ? $"{c}.{sym.Name}" : sym.Name;
            }
        }

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            preciseCount = result.TotalPrecise,
            partialCount = result.TotalPartial,
            hits = items.Select(t => new
            {
                path = t.FilePath,
                t.Line,
                text = t.LineText,
                t.IsGenerated,
                matchKind = t.MatchKind,
                matched = t.MatchKind == "partial" ? t.Matched : null, // tokens only meaningful on partials
                containingSymbol = owners.TryGetValue((t.FilePath, t.Line), out var cs) ? cs : null,
                noise = IndexQueries.IsVendorPath(t.FilePath) ? true : (bool?)null, // under a vendored/generated dir
            }),
            filesMatchedAcrossLines = acrossLines,
            nextCursor = next,
            truncated,
            note = "Token-based, not a regex engine. 'precise' = all query tokens on the line; 'partial' = a lead (tokens co-occur in the file, not on one line).",
            meta,
        });
    }

    // ---------------------------------------------------------------- outline / source

    [McpServerTool(Name = "outline")]
    [Description("Syntactic map of a file (namespaces, types, members with line spans) without reading the body. ALWAYS call this before reading a large file, then fetch only needed spans via source_context.")]
    public string Outline(
        [Description("Workspace-relative file path (forward slashes).")] string path,
        [Description("1 = namespaces + types, 2 = + members (default), 3 = reserved (currently same as 2).")] int depth = 2)
    {
        if (NotReady() is { } notReady) return notReady;
        string normPath = NormalizePath(path);
        using var q = _manager.OpenQueries();
        var rows = q.Outline(normPath);
        if (rows.Count == 0)
        {
            return Json.Serialize(new { error = "file_not_indexed", path, meta = Meta.From(_manager.Health(), "indexed", "syntax") });
        }

        var byId = rows.ToDictionary(r => r.Id);
        var children = new Dictionary<long, List<SymbolHit>>();
        var roots = new List<SymbolHit>();
        foreach (var row in rows)
        {
            if (row.ParentId is { } pid && byId.ContainsKey(pid))
            {
                (children.TryGetValue(pid, out var list) ? list : children[pid] = new()).Add(row);
            }
            else
            {
                roots.Add(row);
            }
        }

        // Memoized per type identity: BuildNested runs up to twice on budget degradation,
        // and batch_outline multiplies calls — one lookup per unique partial type, total.
        var partialCache = new Dictionary<(string Name, string? Ns, string Kind, string? Container), List<string>>();

        // includeMembers=false keeps only namespace/type nodes (the depth-1 view);
        // true adds member leaves (methods, properties, ...) — the depth-2 view.
        object Node(SymbolHit s, bool includeMembers)
        {
            List<object>? memberNodes = null;
            if (children.TryGetValue(s.Id, out var kids))
            {
                var kept = kids
                    .Where(k => includeMembers || TypeKinds.Contains(k.Kind) || k.Kind == "namespace")
                    .Select(k => Node(k, includeMembers))
                    .ToList();
                if (kept.Count > 0) memberNodes = kept;
            }
            // Partial-type cross-links (feedback): the other files declaring this type,
            // so a caller need not run definition(name) just to find them.
            List<string>? partialFiles = null;
            bool partialFilesMore = false;
            if (s.IsPartial && TypeKinds.Contains(s.Kind))
            {
                var key = (s.Name, s.Ns, s.Kind, s.Container);
                if (!partialCache.TryGetValue(key, out var others))
                {
                    others = q.PartialDeclarationFiles(s.Name, s.Ns, s.Kind, s.Container, normPath); // up to 11
                    partialCache[key] = others;
                }
                if (others.Count > 0)
                {
                    partialFilesMore = others.Count > 10;
                    partialFiles = partialFilesMore ? others.Take(10).ToList() : others;
                }
            }
            return new
            {
                s.Name,
                s.Kind,
                s.Signature,
                s.Accessibility,
                s.StartLine,
                s.EndLine,
                isPartial = s.IsPartial ? true : (bool?)null,
                partialFiles,
                partialFilesTruncated = partialFilesMore ? true : (bool?)null,
                attributes = s.AttrMarkers,
                members = memberNodes,
            };
        }

        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        bool generated = rows[0].FileIsGenerated;

        string BuildNested(bool includeMembers, bool truncated) => Json.Serialize(new
        {
            path,
            isGenerated = generated,
            symbols = roots.Select(r => Node(r, includeMembers)).ToList(),
            truncated,
            meta,
        });

        // The nested tree lives under one namespace root, so trimming the top-level list
        // cannot bound it. Degrade instead: requested depth -> types-only -> flat capped.
        string nested = BuildNested(includeMembers: depth >= 2, truncated: false);
        if (Json.Utf8Bytes(nested) <= Json.HardBudgetBytes) return nested;

        if (depth >= 2)
        {
            string typesOnly = BuildNested(includeMembers: false, truncated: true);
            if (Json.Utf8Bytes(typesOnly) <= Json.HardBudgetBytes) return typesOnly;
        }

        // Pathological (thousands of types in one file): flatten namespace/type nodes to
        // a bounded row list and let the list budget converge.
        var flat = rows
            .Where(r => r.Kind == "namespace" || TypeKinds.Contains(r.Kind))
            .Select(r => (object)new { r.Name, r.Kind, ns = r.Ns, r.StartLine, r.EndLine })
            .ToList();
        return Json.WithListBudget(flat, (items, _) => new
        {
            path,
            isGenerated = generated,
            symbols = items,
            truncated = true,
            note = "File has too many top-level declarations for a full outline; showing a bounded flat list.",
            meta,
        });
    }

    [McpServerTool(Name = "source_context")]
    [Description("Bounded live source read around one or more line spans (the bridge from navigation results to actual code). Use spans from outline/definition/search results instead of reading whole files.")]
    public string SourceContext(
        [Description("Workspace-relative file path. Optional when symbolId is given.")] string? path = null,
        [Description("Spans as 'start-end' or 'line', comma-separated (e.g. '42-88,120'). Optional when symbolId is given (defaults to the symbol's own declaration span).")] string spans = "",
        [Description("Extra context lines around each span (default 2).")] int contextLines = 2,
        [Description("Byte budget for returned source (default 8192, max 24576).")] int maxBytes = 8192,
        [Description("Show one symbol's source by handle instead of path+spans: 'idx:NNN' from a prior result. Overrides path/spans with the symbol's declaration span. Note: 'idx:' handles are index-local and change on reindex.")] string? symbolId = null)
    {
        if (NotReady() is { } notReady) return notReady;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            path = hit!.FilePath;
            spans = $"{hit.StartLine}-{hit.EndLine}";
        }
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(spans))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'symbolId', or 'path'+'spans'." });
        }
        path = NormalizePath(path);
        maxBytes = Math.Clamp(maxBytes, 256, Json.HardBudgetBytes);

        // Reject paths that escape the workspace root before touching the filesystem.
        if (!CodeNav.Core.WorkspacePaths.TryResolveInside(_manager.WorkspaceRoot, path, out string full))
        {
            return Json.Serialize(new { error = "path_outside_workspace", path, meta = Meta.From(_manager.Health(), "indexed", "text") });
        }

        string freshness = "live";
        string? content = null;
        // Skip paths reaching outside via a symlink/junction (target or any ancestor) so an
        // in-workspace link cannot be followed to external content; fall through to the index.
        if (File.Exists(full) && !CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(_manager.WorkspaceRoot, full))
        {
            try { content = File.ReadAllText(full); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (content is null)
        {
            // Index fallback is keyed by relative path, so it can only ever return
            // in-workspace content — a contained path that is simply not on disk.
            using var q = _manager.OpenQueries();
            content = q.ContentByPath(path);
            freshness = "index";
        }
        if (content is null)
        {
            return Json.Serialize(new { error = "file_not_found", path, meta = Meta.From(_manager.Health(), "indexed", "text") });
        }

        var lines = content.Split('\n');

        (List<object> Spans, bool Truncated) BuildSpans(long rawBudget)
        {
            var spanResults = new List<object>();
            long budget = rawBudget;
            bool truncated = false;
            foreach (var spec in spans.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = spec.Split('-');
                if (!int.TryParse(parts[0], out int start)) continue;
                int end = parts.Length > 1 && int.TryParse(parts[1], out int e) ? e : start;
                start = Math.Max(1, start - contextLines);
                end = Math.Min(lines.Length, end + contextLines);

                var numbered = new List<string>();
                for (int i = start; i <= end; i++)
                {
                    string line = $"{i,5}| {lines[i - 1].TrimEnd('\r')}";
                    int cost = Json.Utf8Bytes(line) + 1; // budget is a UTF-8 byte contract
                    if (budget - cost < 0) { truncated = true; break; }
                    budget -= cost;
                    numbered.Add(line);
                }
                // Skip spans that yielded nothing (e.g. start past EOF) — no inverted ranges.
                if (numbered.Count > 0)
                    spanResults.Add(new { startLine = start, endLine = start + numbered.Count - 1, source = string.Join("\n", numbered) });
                if (truncated) break;
            }
            return (spanResults, truncated);
        }

        string BuildResponse(long rawBudget)
        {
            var (spanResults, truncated) = BuildSpans(rawBudget);
            // When JSON-escaping headroom forced rawBudget below the caller's maxBytes, "raise
            // maxBytes" is unfollowable (they may already be at the max) — advise narrowing instead.
            string? hint = !truncated ? null
                : rawBudget < maxBytes
                    ? $"cut at {rawBudget} bytes (escaping headroom below your maxBytes {maxBytes}) — narrow the spans"
                    : maxBytes >= Json.HardBudgetBytes
                        ? $"cut at {rawBudget} bytes (at the max) — narrow the spans"
                        : $"cut at {rawBudget} bytes — raise maxBytes (max {Json.HardBudgetBytes}) or narrow the spans";
            return Json.Serialize(new
            {
                path,
                freshness,
                spans = spanResults,
                truncated,
                hint,
                meta = Meta.From(_manager.Health(), freshness == "live" ? "exact" : "indexed", "text"),
            });
        }

        // Raw budget bounds the source bytes; JSON escaping still inflates, so shrink the raw
        // budget until the SERIALIZED response fits the hard cap.
        long effective = maxBytes;
        string json = BuildResponse(effective);
        while (Json.Utf8Bytes(json) > Json.HardBudgetBytes && effective > 256)
        {
            effective /= 2;
            json = BuildResponse(effective);
        }
        return json;
    }

    // ---------------------------------------------------------------- symbols

    [McpServerTool(Name = "search_symbol")]
    [Description("Find declared symbols by name across the workspace (types, methods, properties...). Prefer this over search_text for anything that is a code identifier. Scope with pathGlob / excludePath / namespace (e.g. excludePath='3rdparty/**' to drop vendored third-party source).")]
    public string SearchSymbol(
        [Description("Symbol name. Match behavior set by 'match'.")] string query,
        [Description("Comma-separated kind filter: class,interface,struct,record,enum,delegate,method,constructor,property,field,event,enum_member. Empty = all.")] string? kinds = null,
        [Description("'auto' (exact, then prefix, then substring), 'exact', 'prefix', or 'substring'.")] string match = "auto",
        [Description("Include symbols in generated files (default false).")] bool includeGenerated = false,
        [Description("Restrict to file paths matching this glob (e.g. 'SOAPAPI/**'); a bare name matches at any depth.")] string? pathGlob = null,
        [Description("Exclude file paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party source).")] string? excludePath = null,
        [Description("Drop hits under known vendor/generated dir names (3rdparty, vendor, external, generated...) at ANY depth. Matches the per-hit noise flag; convenience over excludePath. See repo_overview.suggestedExcludes for the dirs actually present.")] bool firstPartyOnly = false,
        [Description("Restrict to a namespace subtree: the exact namespace or anything nested under it (e.g. 'ExactTarget.Integration'). Distinct from a containing type.")] string? @namespace = null,
        [Description("Max results (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call.")] string? cursor = null)
    {
        if (NotReady() is { } notReady) return notReady;
        (limit, int offset) = Page(limit, cursor);
        var kindList = SplitCsv(kinds);
        using var q = _manager.OpenQueries();
        var excludes = BuildExcludes(excludePath, firstPartyOnly);

        List<SymbolHit> hits;
        string effectiveMatch = match;
        if (match == "auto")
        {
            hits = q.SearchSymbols(query, "exact", kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
            effectiveMatch = "exact";
            if (hits.Count == 0 && offset == 0)
            {
                hits = q.SearchSymbols(query, "prefix", kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
                effectiveMatch = "prefix";
            }
            if (hits.Count == 0 && offset == 0)
            {
                hits = q.SearchSymbols(query, "substring", kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
                effectiveMatch = "substring";
            }
        }
        else
        {
            hits = q.SearchSymbols(query, match, kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
        }

        // Flag hits in files that no project compiles — a best-effort "likely dead code" signal grep
        // can't give (phoenix has the compile graph). Additive ONLY: the hit is still returned and
        // tagged orphaned:true, never hidden. The signal over-counts (wildcard/shared/props <Compile>
        // includes aren't expanded), so hiding on it could bury live code — hence flag, don't filter.
        if (hits.Count > 0)
        {
            var orphaned = q.OrphanedPaths(hits.Select(h => h.FilePath).ToList());
            if (orphaned.Count > 0)
                hits = hits.Select(h => orphaned.Contains(h.FilePath) ? h with { IsOrphaned = true } : h).ToList();
        }

        string? next = hits.Count > limit ? $"o:{offset + limit}" : null;
        if (next is not null) hits.RemoveAt(hits.Count - 1);

        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            matchMode = effectiveMatch,
            symbols = items.Select(SymbolJson),
            nextCursor = next,
            truncated,
            // Steer the follow-up (feedback: nothing nudged toward references after a symbol
            // hit). First page only — repeating it on cursored pages just burns budget.
            hint = items.Count > 0 && cursor is null
                ? "Next: references(name) for usages by project; definition(name, includeBody:true) for the declaration with source."
                : null,
            meta,
        });
    }

    [McpServerTool(Name = "symbol_at")]
    [Description("Reverse lookup: given a file + line (from a stack trace, build error, diff hunk, or grep hit), returns the smallest containing symbol and its enclosing chain plus owning projects.")]
    public string SymbolAt(
        [Description("Workspace-relative file path.")] string path,
        [Description("1-based line number.")] int line,
        [Description("Optional 1-based column (currently unused; line-level resolution).")] int column = 0)
    {
        if (NotReady() is { } notReady) return notReady;
        path = NormalizePath(path);
        using var q = _manager.OpenQueries();
        var chain = q.SymbolAt(path, line);
        var projects = q.ProjectsContaining(path);
        return Json.Serialize(new
        {
            path,
            line,
            found = chain.Count > 0,
            chain = chain.Select(SymbolJson),
            owningProjects = projects.Select(p => new { p.Name, p.Path, p.Style, p.IsTest }),
            meta = Meta.From(_manager.Health(), "indexed", "syntax"),
        });
    }

    [McpServerTool(Name = "definition")]
    [Description("Declaration site(s) for a symbol — all partial declarations included. Target by exact name (optionally 'container' to disambiguate) OR by position (path+line[,column]) from a usage site. Tries compiler-exact resolution first (confidence 'exact' with documentationCommentId), falling back to the name index. includeBody=true also returns the primary declaration's source inline — no follow-up source_context needed.")]
    public string Definition(
        [Description("Exact symbol name (case-insensitive). Optional when path+line given.")] string? name = null,
        [Description("Optional containing type or namespace fragment to disambiguate.")] string? container = null,
        [Description("Comma-separated kind filter (defaults to all kinds).")] string? kinds = null,
        [Description("Workspace-relative file path of a usage or declaration site (position mode).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("'auto' (semantic first, indexed fallback), 'semantic', or 'indexed'.")] string mode = "auto",
        [Description("Semantic resolution deadline in ms (default 10000).")] int timeoutMs = 10000,
        [Description("Also return the primary declaration's source body (numbered lines, budget-bounded).")] bool includeBody = false,
        [Description("Byte budget for the inline body (default 12288, max 16384).")] int bodyMaxBytes = 12288,
        [Description("Resolve by a prior result's handle instead of name/position: 'idx:NNN' (from search_symbol / symbol_at / definition). Takes precedence over name and path+line. Note: 'idx:' handles are index-local and change on reindex; a documentationCommentId is not yet accepted here.")] string? symbolId = null)
    {
        if (NotReady() is { } notReady) return notReady;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            name = hit!.Name; path = hit.FilePath; line = hit.StartLine; column = 0;
            // The handle already disambiguated the symbol — caller kinds/container filters exist to
            // narrow a bare name, so applying them here can only wrongly suppress the resolved hit.
            kinds = null; container = null;
        }
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'symbolId', 'name', or 'path'+'line'." });
        }

        string? failReason = null;
        if (mode is "auto" or "semantic")
        {
            var (target, hint) = ResolveSemanticTarget(name, container, kinds, path, line, column);
            if (target is { } t)
            {
                var (decl, reason) = _semantic
                    .DefinitionAsync(t.Path, t.Line, t.Column, hint, timeoutMs)
                    .GetAwaiter().GetResult();
                if (decl is not null)
                {
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
                        symbol = SemanticIdentityJson(decl),
                        declarations = items.Select(d => new { d.Path, d.StartLine, d.EndLine }),
                        declarationsTruncated = (listTrunc || totalDecls > MaxDeclarationSites) ? true : (bool?)null,
                        body,
                        meta = Meta.From(_manager.Health(), "exact", "semantic"),
                    });
                    // Semantic spans come from live sources — pair them with live content.
                    object? MakeSemanticBody(int budget) =>
                        d0 is null ? null : BuildDeclarationBody(d0.Path, d0.StartLine, d0.EndLine, budget, preferLive: true);
                    return SerializeBodyBounded(BuildSemantic, includeBody ? MakeSemanticBody(bodyMaxBytes) : null, MakeSemanticBody, bodyMaxBytes);
                }
                failReason = reason;
            }
            else
            {
                failReason = "target_not_found_in_index";
            }
            if (mode == "semantic")
            {
                return Json.Serialize(new
                {
                    error = "semantic_unavailable",
                    partialReason = failReason,
                    hint = "Retry with mode='indexed' for name-index declarations.",
                    meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                });
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
        var hits = q.SearchSymbols(lookupName, "exact", SplitCsv(kinds), 100, includeGenerated: true);
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
    private object BuildDeclarationBody(string path, int startLine, int endLine, int maxBytes, bool preferLive)
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

    [McpServerTool(Name = "implementations")]
    [Description("Implementations of an interface (or interface member), derived classes, and overrides — RANKED concrete-first (instantiable leaves before abstract scaffolding), each with its derivation path (via). A single concrete implementation is flagged as likelyImplementation (the probable runtime target). Compiler-exact within the loaded cluster; falls back to base-list name matching (confidence 'heuristic', unranked).")]
    public string Implementations(
        [Description("Interface/type/member name. Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of the declaration or a usage (position mode).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("Max candidate projects loaded semantically (default 24).")] int maxProjects = 24,
        [Description("Semantic deadline in ms (default 15000).")] int timeoutMs = 15000)
    {
        if (NotReady() is { } notReady) return notReady;
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'name', or 'path'+'line'." });
        }

        string? failReason = null;
        var (target, hint) = ResolveSemanticTarget(name, null, null, path, line, column);
        if (target is { } t)
        {
            var (result, reason) = _semantic
                .ImplementationsAsync(t.Path, t.Line, t.Column, hint, maxProjects, timeoutMs)
                .GetAwaiter().GetResult();
            if (result is { Implementations.Count: > 0 })
            {
                var impls = result.Implementations; // already ranked concrete-first by the semantic layer
                int concreteCount = impls.Count(r => !r.Declaration.IsAbstract);
                var meta0 = Meta.From(_manager.Health(), "exact", "semantic");
                return Json.WithListBudget(impls, (items, truncated) => new
                {
                    symbol = SemanticSymbolJson(result.Symbol),
                    implementations = items.Select(r => new
                    {
                        symbol = SemanticSymbolJson(r.Declaration),
                        isAbstract = r.Declaration.IsAbstract ? true : (bool?)null, // omitted when concrete
                        rank = r.Declaration.IsAbstract ? "abstract" : "concrete", // make the ranking legible to the model
                        via = r.Via, // the base type that introduces the interface, when implemented indirectly
                    }),
                    concreteCount,
                    // High-signal case: exactly one instantiable implementation is very likely THE
                    // runtime type; anything else is abstract scaffolding.
                    likelyImplementation = concreteCount == 1
                        ? impls.First(r => !r.Declaration.IsAbstract).Declaration.SymbolDisplay
                        : null,
                    coverage = CoverageJson(result.Coverage),
                    truncated,
                    hint = concreteCount == 1
                        ? "One concrete implementation — likely the runtime target. Ranked concrete-first; isAbstract/rank mark non-instantiable scaffolding."
                        : null,
                    meta = meta0,
                });
            }
            // Semantic RESOLVED the symbol but found no implementers, OR it could not resolve. Be
            // honest about which: bounded coverage (raising maxProjects may help) vs genuinely none.
            failReason = result is null ? reason
                : (result.Coverage.LoadedProjects < result.Coverage.RequestedProjects || result.Coverage.FailedProjects.Count > 0)
                    ? "candidate_cluster_bounded"
                    : "no_semantic_implementers";
        }

        // Heuristic fallback: types whose base list textually mentions the name — a naming
        // guess, not a compiler fact, so it is labeled confidence 'heuristic'.
        using var q = _manager.OpenQueries();
        string lookupName = name ?? hint ?? "";
        SymbolHit? targetSym = null;
        // Position mode (path+line): resolve the cursor so the fallback has a name AND the target's
        // kind + declaring type — the base-list heuristic only makes sense for a TYPE target.
        if (path is not null)
        {
            var chain = q.SymbolAt(NormalizePath(path), line);
            if (chain.Count > 0)
            {
                targetSym = chain[0];
                if (lookupName.Length == 0) lookupName = chain[0].Name;
            }
        }
        if (lookupName.Length == 0)
        {
            return Json.Serialize(new
            {
                error = "symbol_not_resolved",
                partialReason = failReason,
                meta = Meta.From(_manager.Health(), "heuristic", "syntax"),
            });
        }
        targetSym ??= q.SearchSymbols(lookupName, "exact", null, 1).FirstOrDefault();
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
                // now whole-token-matches the base list, so a superstring interface (IFooBar) doesn't scope in.
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
                    implementerCount,
                    omittedImplementers = omitted > 0 ? omitted : (int?)null,
                    implementations = items.Select(SymbolJson),
                    partialReason = "member_scoped_syntactic",
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
                implementations = Array.Empty<object>(),
                partialReason = "member_fallback_type_scoped",
                semanticReason = failReason, // why the exact path returned nothing (context, not actionable)
                note = "No compiler-exact member implementations in the loaded cluster (possibly a type-twin identity mismatch), and no same-named member found in the declaring type's implementers. Run implementations on the declaring interface/type, then read this member in each implementer.",
                meta,
            });
        }

        var heuristic = q.ImplementationCandidates(lookupName, 50);
        return Json.WithListBudget(heuristic, (items, truncated) => new
        {
            name = lookupName,
            implementations = items.Select(SymbolJson),
            partialReason = failReason ?? "semantic_unavailable",
            note = items.Count > 0 && failReason is "no_semantic_implementers" or "candidate_cluster_bounded"
                ? "Compiler-exact resolution matched no implementers, but these types name it in their base list (confidence heuristic). Common cause: the type is declared in more than one assembly (e.g. a generated twin) and implementers reference a different declaration. Verify with source_context."
                : "Base-list name matches from the index (confidence heuristic) — verify with source_context.",
            truncated = truncated || heuristic.Count >= 50, // count-capped even if the byte budget fit
            meta,
        });
    }

    [McpServerTool(Name = "references")]
    [Description("Where a symbol is used across the workspace, grouped by project with counts and sample lines. mode='auto' tries compiler-exact references (target by position path+line, or by name) scoped to candidate projects, falling back to index candidates. Pass pathGlob/excludePath to scope candidates (e.g. excludePath='3rdparty/**'); a path filter runs the indexed candidate path so counts reflect the filter. Call before changing behavior.")]
    public string References(
        [Description("Symbol name (whole-identifier). Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of a usage or declaration (position mode — most precise).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("'auto' (semantic first), 'semantic', or 'indexed' (fast candidates).")] string mode = "auto",
        [Description("Include usages in test projects (default true).")] bool includeTests = true,
        [Description("Include usages in generated files (default false).")] bool includeGenerated = false,
        [Description("Restrict candidate paths to this glob (supplying a path filter runs indexed candidates).")] string? pathGlob = null,
        [Description("Exclude candidate paths matching this glob, e.g. '3rdparty/**' (supplying a path filter runs indexed candidates).")] string? excludePath = null,
        [Description("Max candidate files scanned in indexed mode (default 500).")] int maxFiles = 500,
        [Description("Max projects loaded semantically (default 24; raise for hot symbols).")] int maxProjects = 24,
        [Description("Sample lines per project group (default 3).")] int samplesPerGroup = 3,
        [Description("Semantic deadline in ms (default 15000).")] int timeoutMs = 15000,
        [Description("Resolve by a prior result's handle instead of name/position: 'idx:NNN' (from search_symbol / symbol_at / definition). Takes precedence over name and path+line. Note: 'idx:' handles are index-local and change on reindex; a documentationCommentId is not yet accepted here.")] string? symbolId = null)
    {
        if (NotReady() is { } notReady) return notReady;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            name = hit!.Name; path = hit.FilePath; line = hit.StartLine; column = 0;
        }
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'symbolId', 'name', or 'path'+'line'." });
        }

        // Path filters are honored precisely only on the indexed candidate path (semantic counts
        // are project-level and cannot be re-derived per path), so a filter forces indexed mode.
        bool hasPathFilter = pathGlob is { Length: > 0 } || excludePath is { Length: > 0 };
        string? failReason = hasPathFilter && mode != "indexed" ? "path_filter_ran_indexed_candidates" : null;
        if (mode is "auto" or "semantic" && !hasPathFilter)
        {
            var (target, hint) = ResolveSemanticTarget(name, null, null, path, line, column);
            if (target is { } t)
            {
                var (result, reason) = _semantic
                    .ReferencesAsync(t.Path, t.Line, t.Column, hint, maxProjects, Math.Clamp(samplesPerGroup, 0, 10), timeoutMs)
                    .GetAwaiter().GetResult();
                if (result is not null)
                {
                    var groups0 = result.Groups;
                    if (!includeTests) groups0 = groups0.Where(g => !g.IsTestProject).ToList();
                    int prod0 = groups0.Where(g => !g.IsTestProject).Sum(g => g.Count);
                    int test0 = groups0.Where(g => g.IsTestProject).Sum(g => g.Count);
                    bool partial = result.SkippedCandidateProjects.Count > 0 || result.Coverage.FailedProjects.Count > 0;
                    var meta0 = Meta.From(_manager.Health(), "exact", "semantic");
                    return Json.WithListBudget(groups0, (items, truncated) => new
                    {
                        symbol = SemanticSymbolJson(result.Symbol),
                        summary = $"{result.TotalLocations} exact references across {result.Groups.Count} projects ({prod0} production, {test0} test).",
                        totalReferences = result.TotalLocations,
                        groupBy = "project",
                        groups = items.Select(g => new
                        {
                            project = g.Project,
                            isTest = g.IsTestProject,
                            count = g.Count,
                            samples = g.Samples.Select(s => new { s.Path, s.Line, text = s.LineText }),
                        }),
                        coverage = CoverageJson(result.Coverage),
                        partial,
                        partialReason = partial
                            ? $"skipped {result.SkippedCandidateProjects.Count} candidate projects (raise maxProjects), {result.Coverage.FailedProjects.Count} failed loads"
                            : null,
                        skippedCandidateProjects = result.SkippedCandidateProjects.Count > 0 ? result.SkippedCandidateProjects : null,
                        truncated,
                        meta = meta0,
                    });
                }
                failReason = reason;
            }
            else
            {
                failReason = "target_not_found_in_index";
            }
            if (mode == "semantic")
            {
                return Json.Serialize(new
                {
                    error = "semantic_unavailable",
                    partialReason = failReason,
                    hint = "Retry with mode='indexed' for fast text candidates.",
                    meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                });
            }
        }

        // Indexed fallback (name required — derive from position when missing).
        using var q = _manager.OpenQueries();
        if (string.IsNullOrEmpty(name) && path is not null)
        {
            var chain = q.SymbolAt(NormalizePath(path), line);
            name = chain.Count > 0 ? chain[0].Name : name;
        }
        if (string.IsNullOrEmpty(name))
        {
            return Json.Serialize(new { error = "symbol_not_resolved", partialReason = failReason });
        }
        var excludes = excludePath is { Length: > 0 } ex ? new[] { ex } : null;
        var (total, groups) = q.ReferenceCandidates(
            name, Math.Clamp(maxFiles, 10, 2000), Math.Clamp(samplesPerGroup, 0, 10), pathGlob, excludes);
        if (!includeTests) groups = groups.Where(g => !g.IsTestProject).ToList();
        if (!includeGenerated)
        {
            groups = groups
                .Select(g => g with { Samples = g.Samples.Where(s => !s.IsGenerated).ToList() })
                .ToList();
        }

        int prod = groups.Where(g => !g.IsTestProject).Sum(g => g.Count);
        int test = groups.Where(g => g.IsTestProject).Sum(g => g.Count);
        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(groups, (items, truncated) => new
        {
            name,
            partialReason = failReason,
            summary = $"{total} candidate reference lines across {groups.Count} projects ({prod} production, {test} test).",
            totalCandidates = total,
            groupBy = "project",
            groups = items.Select(g => new
            {
                project = g.Project,
                isTest = g.IsTestProject,
                count = g.Count,
                samples = g.Samples.Select(s => new { path = s.FilePath, s.Line, text = s.LineText }),
            }),
            truncated,
            note = "Candidates are whole-identifier text matches (confidence: indexed), not compiler-resolved references.",
            meta,
        });
    }

    // ---------------------------------------------------------------- projects

    [McpServerTool(Name = "project_graph")]
    [Description("Project dependency edges around a project (upstream = dependents, downstream = dependencies). Use to understand ownership and blast radius before changes.")]
    public string ProjectGraph(
        [Description("Project name (e.g. 'Acme.Billing.Invoicing.Application').")] string project,
        [Description("Traversal depth (default 2, max 5).")] int depth = 2,
        [Description("'upstream', 'downstream', or 'both' (default).")] string direction = "both")
    {
        if (NotReady() is { } notReady) return notReady;
        depth = Math.Clamp(depth, 1, 5);
        using var q = _manager.OpenQueries();
        var root = q.ProjectByName(project);
        if (root is null)
        {
            return Json.Serialize(new { error = "project_not_found", project, meta = Meta.From(_manager.Health(), "indexed", "text") });
        }
        var edges = q.ProjectGraph(root.Name, depth, direction);
        var nodes = edges.SelectMany(e => new[] { e.FromProject, e.ToProject })
            .Append(root.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(edges, (items, truncated) => new
        {
            root = new { root.Name, root.Path, root.Style, root.Tfms, root.IsTest },
            direction,
            depth,
            nodeCount = nodes.Count,
            edges = items.Select(e => new { from = e.FromProject, to = e.ToProject, kind = "projectReference" }),
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "projects_containing")]
    [Description("All projects that compile a given file (multiple for linked/shared files). Needed before interpreting diagnostics or symbol identity for shared files.")]
    public string ProjectsContaining(
        [Description("Workspace-relative file path.")] string path)
    {
        if (NotReady() is { } notReady) return notReady;
        using var q = _manager.OpenQueries();
        var projects = q.ProjectsContaining(NormalizePath(path));
        return Json.Serialize(new
        {
            path,
            projects = projects.Select(p => new { p.Name, p.Path, p.Style, p.Tfms, p.IsTest }),
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
    }

    // ---------------------------------------------------------------- maintenance

    [McpServerTool(Name = "refresh_index")]
    [Description("Queue an index refresh: targeted (comma-separated workspace-relative paths) or a full change-detection sweep when no paths given. Normally unnecessary — a file watcher keeps the index fresh.")]
    public string RefreshIndex(
        [Description("Optional comma-separated workspace-relative paths to refresh.")] string? paths = null)
    {
        var list = SplitCsv(paths);
        _manager.RequestRefresh(list?.Count > 0 ? list : null);
        return Json.Serialize(new
        {
            queued = true,
            scope = list?.Count > 0 ? $"{list.Count} paths" : "full sweep",
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Turns a name- or position-target into a concrete (path, line, column) for the
    /// semantic layer, using the index to locate the best declaration for name targets.
    /// </summary>
    private ((string Path, int Line, int? Column)? Target, string? NameHint) ResolveSemanticTarget(
        string? name, string? container, string? kinds, string? path, int line, int column)
    {
        if (path is not null && line > 0)
        {
            return ((NormalizePath(path), line, column > 0 ? column : null), name);
        }
        if (name is null) return (null, null);

        using var q = _manager.OpenQueries();
        var hits = q.SearchSymbols(name, "exact", SplitCsv(kinds), 20, includeGenerated: false);
        if (container is { } c)
        {
            hits = hits.Where(h =>
                (h.Container?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Ns?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }
        var best = hits
            .OrderBy(h => h.Kind switch { "interface" => 0, "class" => 1, "struct" => 1, "enum" => 1, _ => 2 })
            .FirstOrDefault();
        if (best is null) return (null, name);
        return ((best.FilePath, best.StartLine, null), name);
    }

    // Cap on declaration sites emitted for one symbol. A namespace or heavily-partial type can
    // have hundreds of spans; this bounds the count for tools that serialize it via a plain
    // Serialize (implementations/references/etc.). definition additionally routes its (single)
    // declaration list through WithListBudget for a hard byte bound; this cap keeps the common
    // case small and gives an early declarationsTruncated signal.
    private const int MaxDeclarationSites = 20;

    // Kept single-arg so method-group use (.Select(SemanticSymbolJson)) still binds.
    private static object SemanticSymbolJson(SemanticDeclaration d) => SemanticSymbolCore(d, includeDeclarations: true);

    // Identity without the declaration list — for definition, which renders declarations once
    // in its own budgeted top-level array (avoids serializing the same sites twice).
    private static object SemanticIdentityJson(SemanticDeclaration d) => SemanticSymbolCore(d, includeDeclarations: false);

    private static object SemanticSymbolCore(SemanticDeclaration d, bool includeDeclarations)
    {
        var sites = includeDeclarations ? d.Declarations.Take(MaxDeclarationSites + 1).ToList() : null;
        return new
        {
            display = d.SymbolDisplay,
            documentationCommentId = d.DocumentationCommentId,
            d.Kind,
            containingType = d.ContainingType,
            ns = d.Namespace,
            assembly = d.Assembly,
            declarations = sites?.Take(MaxDeclarationSites).Select(s => new { s.Path, s.StartLine, s.EndLine, project = s.Project }),
            declarationsTruncated = sites is { Count: > MaxDeclarationSites }
                ? $"more than {MaxDeclarationSites} declaration sites — narrow the query or use references"
                : (string?)null,
        };
    }

    private static object CoverageJson(ClusterCoverage c) => new
    {
        loadedProjects = c.LoadedProjects,
        requestedProjects = c.RequestedProjects,
        failedProjects = c.FailedProjects.Count > 0 ? c.FailedProjects : null,
        frameworkRefsAvailable = c.FrameworkRefsAvailable,
    };

    /// <summary>Combines an explicit excludePath glob with firstPartyOnly's whole-segment vendor
    /// globs into one exclude list (null when empty, so callers pass null for "no filter").
    /// firstPartyOnly uses <see cref="IndexQueries.VendorExcludeGlobs"/> — a complete, scan-free
    /// exclusion that matches exactly what the per-hit noise flag reports.</summary>
    private static IReadOnlyList<string>? BuildExcludes(string? excludePath, bool firstPartyOnly)
    {
        var list = new List<string>();
        if (excludePath is { Length: > 0 } ex) list.Add(ex);
        if (firstPartyOnly) list.AddRange(IndexQueries.VendorExcludeGlobs());
        return list.Count > 0 ? list.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;
    }

    /// <summary>Resolves a symbolId handle to its indexed declaration row. Accepts the idx:NNN
    /// handle (from any search_symbol / symbol_at / definition result) — index-local, so it changes
    /// on reindex. A documentationCommentId is not yet accepted as an input handle. Returns the hit
    /// on success, or an error-JSON string to hand straight back to the caller.</summary>
    private (SymbolHit? Hit, string? Error) ResolveSymbolIdHandle(string symbolId)
    {
        // Handle shape: idx:<rowid>[~<fingerprint>]. The fingerprint is optional so a hand-typed
        // idx:<rowid> still resolves (best-effort, unverified); emitted handles always carry it.
        string body = symbolId.StartsWith("idx:", StringComparison.Ordinal) ? symbolId[4..] : "";
        string? fp = null;
        int tilde = body.IndexOf('~');
        if (tilde >= 0) { fp = body[(tilde + 1)..]; body = body[..tilde]; }
        if (body.Length == 0 || !long.TryParse(body, out long id))
        {
            return (null, Json.Serialize(new
            {
                error = "bad_request",
                detail = "symbolId must be an 'idx:NNN' handle (from a prior search_symbol / symbol_at / definition result). A documentationCommentId (e.g. 'T:Ns.Type') is not yet accepted as input — use name, or path+line.",
            }));
        }
        using var q = _manager.OpenQueries();
        var hit = q.SymbolById(id);
        if (hit is null)
        {
            return (null, Json.Serialize(new
            {
                error = "symbol_not_found",
                detail = $"No indexed symbol with id {id}. 'idx:' handles are index-local — re-run search_symbol for a current handle.",
            }));
        }
        if (fp is not null && !string.Equals(fp, Fingerprint(hit), StringComparison.Ordinal))
        {
            // The rowid still exists but now holds a DIFFERENT symbol (a reindex reused it). Refuse
            // rather than return the wrong symbol as if the handle were exact.
            return (null, Json.Serialize(new
            {
                error = "stale_handle",
                detail = $"symbolId 'idx:{id}' now refers to a different symbol ({hit.Name}) — the index changed since the handle was issued. Re-run search_symbol for a current handle.",
            }));
        }
        return (hit, null);
    }

    /// <summary>Short, stable (cross-process) identity hash of a symbol row — FNV-1a over
    /// name/kind/line/path. Embedded in the idx: handle so a rowid the index later reuses for a
    /// different symbol fails the check instead of resolving silently.</summary>
    private static string Fingerprint(SymbolHit s)
    {
        string identity = $"{s.Name}{s.Kind}{s.StartLine}{s.FilePath}";
        uint h = 2166136261u;
        foreach (char c in identity) h = (h ^ c) * 16777619u;
        return h.ToString("x8");
    }

    private static object SymbolJson(SymbolHit s) => new
    {
        // idx:<rowid>~<identity fingerprint>. The fingerprint lets a rowid that delta refresh
        // reused/reassigned be DETECTED on the way back in (stale_handle) rather than silently
        // resolving to a different symbol — the id alone is not stable across reindex.
        symbolId = $"idx:{s.Id}~{Fingerprint(s)}",
        s.Name,
        s.Kind,
        ns = s.Ns,
        containingType = s.Container,
        s.Signature,
        s.Accessibility,
        path = s.FilePath,
        s.StartLine,
        s.EndLine,
        isPartial = s.IsPartial ? true : (bool?)null,
        isGenerated = s.FileIsGenerated ? true : (bool?)null,
        // Best-effort "this hit lives under a vendored/generated dir" signal (only present when
        // true). Lets a caller spot third-party noise without firstPartyOnly, or excludePath it.
        noise = IndexQueries.IsVendorPath(s.FilePath) ? true : (bool?)null,
        // Best-effort "likely dead code": this file is in no project's compile set (only present when
        // true) — the compile-graph signal grep lacks, but syntactic/over-inclusive (wildcard, shared,
        // and props <Compile> includes aren't expanded), so treat as "worth checking", not proof.
        orphaned = s.IsOrphaned ? true : (bool?)null,
        attributes = s.AttrMarkers,
    };

    private string? NotReady()
    {
        if (_manager.IsQueryable) return null;
        var h = _manager.Health();
        return Json.Serialize(new
        {
            error = h.State == "building" ? "index_building" : "index_unavailable",
            state = h.State,
            detail = h.Error,
            hint = h.State == "building"
                ? "The workspace index is still building (first run). Retry shortly; use shell tools meanwhile."
                : "Index unavailable. Falling back to shell search is appropriate.",
        });
    }

    private static (int Limit, int Offset) Page(int limit, string? cursor)
    {
        limit = Math.Clamp(limit, 1, 100);
        int offset = 0;
        if (cursor is not null && cursor.StartsWith("o:", StringComparison.Ordinal) &&
            int.TryParse(cursor[2..], out int parsed) && parsed > 0)
        {
            offset = parsed;
        }
        return (limit, offset);
    }

    private static List<string>? SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string NormalizePath(string path) => CodeNav.Core.WorkspacePaths.Normalize(path);
}
