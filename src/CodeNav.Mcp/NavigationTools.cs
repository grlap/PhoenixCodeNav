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
            confidenceModel = new[] { "exact", "indexed", "heuristic" },
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
            targetFrameworks = stats.TfmBreakdown,
            git,
            meta = Meta.From(h, "indexed", "text"),
        });
    }

    // ---------------------------------------------------------------- files / text

    [McpServerTool(Name = "find_file")]
    [Description("Find files by name or glob (e.g. 'InvoiceService.cs', '*Controller.cs', 'src/Billing/**/*.csproj'). Cheap path-only lookup; use search_symbol for code symbols.")]
    public string FindFile(
        [Description("File name or glob pattern. '*' matches any characters including '/'.")] string nameOrGlob,
        [Description("Max results (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call to fetch the next page.")] string? cursor = null)
    {
        if (NotReady() is { } notReady) return notReady;
        (limit, int offset) = Page(limit, cursor);
        using var q = _manager.OpenQueries();
        var files = q.FindFiles(nameOrGlob, limit + 1, offset);
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
            Lang: lang);
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
        using var q = _manager.OpenQueries();
        var rows = q.Outline(NormalizePath(path));
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
            return new
            {
                s.Name,
                s.Kind,
                s.Signature,
                s.Accessibility,
                s.StartLine,
                s.EndLine,
                isPartial = s.IsPartial ? true : (bool?)null,
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
        if (nested.Length <= Json.HardBudgetBytes) return nested;

        if (depth >= 2)
        {
            string typesOnly = BuildNested(includeMembers: false, truncated: true);
            if (typesOnly.Length <= Json.HardBudgetBytes) return typesOnly;
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
        [Description("Workspace-relative file path.")] string path,
        [Description("Spans as 'start-end' or 'line', comma-separated (e.g. '42-88,120').")] string spans,
        [Description("Extra context lines around each span (default 2).")] int contextLines = 2,
        [Description("Byte budget for returned source (default 8192, max 32768).")] int maxBytes = 8192)
    {
        if (NotReady() is { } notReady) return notReady;
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
            try { content = File.ReadAllText(full); } catch (IOException) { }
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
        var spanResults = new List<object>();
        long budget = maxBytes;
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
                if (budget - line.Length < 0)
                {
                    truncated = true;
                    break;
                }
                budget -= line.Length + 1;
                numbered.Add(line);
            }
            spanResults.Add(new { startLine = start, endLine = start + numbered.Count - 1, source = string.Join("\n", numbered) });
            if (truncated) break;
        }

        return Json.Serialize(new
        {
            path,
            freshness,
            spans = spanResults,
            truncated,
            meta = Meta.From(_manager.Health(), freshness == "live" ? "exact" : "indexed", "text"),
        });
    }

    // ---------------------------------------------------------------- symbols

    [McpServerTool(Name = "search_symbol")]
    [Description("Find declared symbols by name across the workspace (types, methods, properties...). Prefer this over search_text for anything that is a code identifier.")]
    public string SearchSymbol(
        [Description("Symbol name. Match behavior set by 'match'.")] string query,
        [Description("Comma-separated kind filter: class,interface,struct,record,enum,delegate,method,constructor,property,field,event,enum_member. Empty = all.")] string? kinds = null,
        [Description("'auto' (exact, then prefix, then substring), 'exact', 'prefix', or 'substring'.")] string match = "auto",
        [Description("Include symbols in generated files (default false).")] bool includeGenerated = false,
        [Description("Max results (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call.")] string? cursor = null)
    {
        if (NotReady() is { } notReady) return notReady;
        (limit, int offset) = Page(limit, cursor);
        var kindList = SplitCsv(kinds);
        using var q = _manager.OpenQueries();

        List<SymbolHit> hits;
        string effectiveMatch = match;
        if (match == "auto")
        {
            hits = q.SearchSymbols(query, "exact", kindList, limit + 1, includeGenerated, offset);
            effectiveMatch = "exact";
            if (hits.Count == 0 && offset == 0)
            {
                hits = q.SearchSymbols(query, "prefix", kindList, limit + 1, includeGenerated, offset);
                effectiveMatch = "prefix";
            }
            if (hits.Count == 0 && offset == 0)
            {
                hits = q.SearchSymbols(query, "substring", kindList, limit + 1, includeGenerated, offset);
                effectiveMatch = "substring";
            }
        }
        else
        {
            hits = q.SearchSymbols(query, match, kindList, limit + 1, includeGenerated, offset);
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
    [Description("Declaration site(s) for a symbol — all partial declarations included. Target by exact name (optionally 'container' to disambiguate) OR by position (path+line[,column]) from a usage site. Tries compiler-exact resolution first (confidence 'exact' with documentationCommentId), falling back to the name index.")]
    public string Definition(
        [Description("Exact symbol name (case-insensitive). Optional when path+line given.")] string? name = null,
        [Description("Optional containing type or namespace fragment to disambiguate.")] string? container = null,
        [Description("Comma-separated kind filter (defaults to all kinds).")] string? kinds = null,
        [Description("Workspace-relative file path of a usage or declaration site (position mode).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("'auto' (semantic first, indexed fallback), 'semantic', or 'indexed'.")] string mode = "auto",
        [Description("Semantic resolution deadline in ms (default 10000).")] int timeoutMs = 10000)
    {
        if (NotReady() is { } notReady) return notReady;
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'name', or 'path'+'line'." });
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
                    return Json.Serialize(new
                    {
                        name = name ?? decl.SymbolDisplay,
                        symbol = SemanticSymbolJson(decl),
                        declarations = decl.Declarations.Select(d => new { d.Path, d.StartLine, d.EndLine }),
                        meta = Meta.From(_manager.Health(), "exact", "semantic"),
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

        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            name = lookupName,
            declarations = items.Select(SymbolJson),
            partialReason = failReason,
            hint = items.Count == 0
                ? "No declaration found. Try search_symbol with match='substring', or the name may come from a package/generated source."
                : null,
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "implementations")]
    [Description("Implementations of an interface (or interface member), derived classes, and overrides. Compiler-exact within the loaded cluster; falls back to base-list name matching (confidence 'heuristic').")]
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
            if (result is not null)
            {
                var impls = result.Implementations;
                var meta0 = Meta.From(_manager.Health(), "exact", "semantic");
                return Json.WithListBudget(impls, (items, truncated) => new
                {
                    symbol = SemanticSymbolJson(result.Symbol),
                    implementations = items.Select(SemanticSymbolJson),
                    coverage = CoverageJson(result.Coverage),
                    truncated,
                    meta = meta0,
                });
            }
            failReason = reason;
        }

        // Heuristic fallback: types whose base list textually mentions the name — a naming
        // guess, not a compiler fact, so it is labeled confidence 'heuristic'.
        using var q = _manager.OpenQueries();
        string lookupName = name ?? hint ?? "";
        var candidates = q.SearchSymbols(lookupName, "exact", null, 5, includeGenerated: true);
        var heuristic = q.ImplementationCandidates(lookupName, 50);
        var meta = Meta.From(_manager.Health(), "heuristic", "syntax");
        return Json.WithListBudget(heuristic, (items, truncated) => new
        {
            name = lookupName,
            implementations = items.Select(SymbolJson),
            partialReason = failReason ?? "semantic_unavailable",
            note = "Base-list name matches from the index — verify with source_context.",
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "references")]
    [Description("Where a symbol is used across the workspace, grouped by project with counts and sample lines. mode='auto' tries compiler-exact references (target by position path+line, or by name) scoped to candidate projects, falling back to index candidates. Call before changing behavior.")]
    public string References(
        [Description("Symbol name (whole-identifier). Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of a usage or declaration (position mode — most precise).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("'auto' (semantic first), 'semantic', or 'indexed' (fast candidates).")] string mode = "auto",
        [Description("Include usages in test projects (default true).")] bool includeTests = true,
        [Description("Include usages in generated files (default false).")] bool includeGenerated = false,
        [Description("Max candidate files scanned in indexed mode (default 500).")] int maxFiles = 500,
        [Description("Max projects loaded semantically (default 24; raise for hot symbols).")] int maxProjects = 24,
        [Description("Sample lines per project group (default 3).")] int samplesPerGroup = 3,
        [Description("Semantic deadline in ms (default 15000).")] int timeoutMs = 15000)
    {
        if (NotReady() is { } notReady) return notReady;
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'name', or 'path'+'line'." });
        }

        string? failReason = null;
        if (mode is "auto" or "semantic")
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
        var (total, groups) = q.ReferenceCandidates(name, Math.Clamp(maxFiles, 10, 2000), Math.Clamp(samplesPerGroup, 0, 10));
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

    private static object SemanticSymbolJson(SemanticDeclaration d) => new
    {
        display = d.SymbolDisplay,
        documentationCommentId = d.DocumentationCommentId,
        d.Kind,
        containingType = d.ContainingType,
        ns = d.Namespace,
        assembly = d.Assembly,
        declarations = d.Declarations.Select(s => new { s.Path, s.StartLine, s.EndLine, project = s.Project }),
    };

    private static object CoverageJson(ClusterCoverage c) => new
    {
        loadedProjects = c.LoadedProjects,
        requestedProjects = c.RequestedProjects,
        failedProjects = c.FailedProjects.Count > 0 ? c.FailedProjects : null,
        frameworkRefsAvailable = c.FrameworkRefsAvailable,
    };

    private static object SymbolJson(SymbolHit s) => new
    {
        symbolId = $"idx:{s.Id}",
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
