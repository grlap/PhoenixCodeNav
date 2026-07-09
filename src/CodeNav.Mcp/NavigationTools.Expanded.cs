using System.ComponentModel;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: the expanded tool surface (call graph, hierarchy, tests, dependency paths,
/// config lookup, batches, context packs, impact) — split out of NavigationTools.cs.
/// Same conventions: budgets, confidence labels, indexed fallbacks.
/// </summary>
public sealed partial class NavigationTools
{
    // ---------------------------------------------------------------- call graph

    [McpServerTool(Name = "callers")]
    [Description("Who calls a method/property (compiler-exact, direct callers with call sites), scoped to candidate projects. Target by position (path+line) or name.")]
    public string Callers(
        [Description("Method/property name. Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of the declaration or a usage.")] string? path = null,
        [Description("1-based line.")] int line = 0,
        [Description("1-based column (optional).")] int column = 0,
        [Description("Max candidate projects loaded (default 24).")] int maxProjects = 24,
        [Description("Deadline in ms (default 15000).")] int timeoutMs = 15000)
    {
        if (NotReady() is { } notReady) return notReady;
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'name', or 'path'+'line'." });
        }

        var (target, hint) = ResolveSemanticTarget(name, null, "method,property,constructor", path, line, column);
        if (target is { } t)
        {
            var (result, coverage, reason) = _semantic
                .CallersAsync(t.Path, t.Line, t.Column, hint, maxProjects, timeoutMs)
                .GetAwaiter().GetResult();
            reason = ExpandReason(reason); // t2b: cold-load token gains inline retry advice
            if (result is not null)
            {
                var meta = Meta.From(_manager.Health(), "exact", "semantic");
                return Json.WithListBudget(result, (items, truncated) => new
                {
                    callers = items.Select(c => new
                    {
                        caller = SemanticSymbolJson(c.Caller),
                        callSites = c.CallSites.Select(s => new { s.Path, s.Line, text = s.LineText }),
                    }),
                    coverage = coverage is null ? null : CoverageJson(coverage),
                    truncated,
                    meta,
                });
            }
            return IndexedReferencesFallback(name ?? hint, reason);
        }
        return IndexedReferencesFallback(name, "target_not_found_in_index");
    }

    [McpServerTool(Name = "callees")]
    [Description("What a method calls (compiler-resolved invocations and constructions inside its body). Target by position (path+line) or name.")]
    public string Callees(
        [Description("Method name. Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of the declaration.")] string? path = null,
        [Description("1-based line.")] int line = 0,
        [Description("1-based column (optional).")] int column = 0,
        [Description("Deadline in ms (default 10000).")] int timeoutMs = 10000)
    {
        if (NotReady() is { } notReady) return notReady;
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'name', or 'path'+'line'." });
        }

        var (target, hint) = ResolveSemanticTarget(name, null, "method,constructor", path, line, column);
        if (target is not { } t)
        {
            return Json.Serialize(new { error = "target_not_found_in_index", name });
        }
        var (result, reason) = _semantic
            .CalleesAsync(t.Path, t.Line, t.Column, hint, timeoutMs)
            .GetAwaiter().GetResult();
        reason = ExpandReason(reason); // t2b: cold-load token gains inline retry advice
        if (result is null)
        {
            return Json.Serialize(new
            {
                error = "semantic_unavailable",
                partialReason = reason,
                meta = Meta.From(_manager.Health(), "indexed", "semantic"),
            });
        }
        var meta0 = Meta.From(_manager.Health(), "exact", "semantic");
        return Json.WithListBudget(result, (items, truncated) => new
        {
            callees = items.Select(c => new
            {
                callee = SemanticSymbolJson(c.Callee),
                callLines = c.CallLines,
            }),
            truncated,
            meta = meta0,
        });
    }

    [McpServerTool(Name = "type_hierarchy")]
    [Description("Base types, implemented interfaces, and derived/implementing types for a type (compiler-exact within the loaded cluster).")]
    public string TypeHierarchy(
        [Description("Type name. Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of the declaration or a usage.")] string? path = null,
        [Description("1-based line.")] int line = 0,
        [Description("1-based column (optional).")] int column = 0,
        [Description("Max candidate projects loaded (default 24).")] int maxProjects = 24,
        [Description("Deadline in ms (default 15000).")] int timeoutMs = 15000)
    {
        if (NotReady() is { } notReady) return notReady;
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'name', or 'path'+'line'." });
        }

        // Deadline visibility (field 0.7.0: type_hierarchy was the one exact tool without timing).
        int deadlineMs = Math.Clamp(timeoutMs, 500, 120000); // mirrors TypeHierarchyAsync's clamp
        var swSem = System.Diagnostics.Stopwatch.StartNew();
        var (target, hint) = ResolveSemanticTarget(name, null, "class,interface,struct,record,enum", path, line, column);
        if (target is not { } t)
        {
            return Json.Serialize(new { error = "target_not_found_in_index", name });
        }
        var (result, coverage, reason) = _semantic
            .TypeHierarchyAsync(t.Path, t.Line, t.Column, hint, maxProjects, timeoutMs)
            .GetAwaiter().GetResult();
        reason = ExpandReason(reason); // t2b: cold-load token gains inline retry advice
        if (result is null)
        {
            return Json.Serialize(new
            {
                error = "semantic_unavailable",
                partialReason = reason,
                hint = "Use 'implementations' for its indexed fallback, or search_symbol.",
                timing = new { deadlineMs, elapsedMs = swSem.ElapsedMilliseconds },
                meta = Meta.From(_manager.Health(), "indexed", "semantic"),
            });
        }
        var down = result.DerivedOrImplementing;
        var meta1 = Meta.From(_manager.Health(), "exact", "semantic");

        // Parity with implementations: when compiler-exact finds no derived/implementing types — often
        // a type-twin identity mismatch across assemblies, or bounded coverage — surface the index
        // base-list implementers with an honest note instead of a bare exact []. Only
        // derivedOrImplementing becomes heuristic; baseTypes/interfaces stay exact.
        if (down.Count == 0)
        {
            using var q0 = _manager.OpenQueries();
            string lookupName = name ?? hint ?? "";
            string? targetKind = null;
            if (path is not null)
            {
                var chain = q0.SymbolAt(NormalizePath(path), line);
                if (chain.Count > 0) { targetKind = chain[0].Kind; if (lookupName.Length == 0) lookupName = chain[0].Name; }
            }
            targetKind ??= lookupName.Length > 0 ? q0.SearchSymbols(lookupName, "exact", null, 1).FirstOrDefault()?.Kind : null;
            // Only a TYPE has a meaningful base-list implementer set — guard the member-position edge
            // so we don't sweep in every type whose base list contains a member name.
            bool isType = targetKind is null or "interface" or "class" or "struct" or "record" or "record_struct";
            var heuristic = isType && lookupName.Length > 0 ? q0.ImplementationCandidates(lookupName, 50) : new List<SymbolHit>();
            if (heuristic.Count > 0)
            {
                bool bounded = coverage is not null &&
                    (coverage.LoadedProjects < coverage.RequestedProjects || coverage.FailedProjects.Count > 0);
                return Json.WithListBudget(heuristic, (items, truncated) => new
                {
                    symbol = SemanticSymbolJson(result.Symbol),
                    baseTypes = result.BaseTypes.Select(SemanticSymbolJson),
                    interfaces = result.Interfaces.Select(SemanticSymbolJson),
                    derivedOrImplementing = items.Select(SymbolJson),
                    derivedConfidence = "heuristic",
                    partialReason = bounded ? "candidate_cluster_bounded" : "no_semantic_derived",
                    // Field (lhg): stale "generated twin" wording replaced — key on the causes we
                    // can actually still hit post-edge-recovery, with the remediation inline.
                    note = "Compiler-exact resolution found no derived/implementing types, but these name it in their base list (derivedOrImplementing is heuristic here). Implementer projects were likely not loaded into the semantic cluster (raise maxProjects, or scope with pathGlob), or the implementers bind the name to a declaration outside the workspace. baseTypes/interfaces remain exact. Verify with source_context.",
                    coverage = coverage is null ? null : CoverageJson(coverage),
                    timing = new { deadlineMs, elapsedMs = swSem.ElapsedMilliseconds },
                    truncated = truncated || heuristic.Count >= 50,
                    meta = meta1,
                });
            }
        }

        return Json.WithListBudget(down, (items, truncated) => new
        {
            symbol = SemanticSymbolJson(result.Symbol),
            baseTypes = result.BaseTypes.Select(SemanticSymbolJson),
            interfaces = result.Interfaces.Select(SemanticSymbolJson),
            derivedOrImplementing = items.Select(SemanticSymbolJson),
            coverage = coverage is null ? null : CoverageJson(coverage),
            timing = new { deadlineMs, elapsedMs = swSem.ElapsedMilliseconds },
            truncated,
            meta = meta1,
        });
    }

    // ---------------------------------------------------------------- tests / graph / config

    [McpServerTool(Name = "related_tests")]
    [Description("Likely tests for a symbol: test files referencing its name, {Name}Tests naming convention, and test projects referencing its owning project. Call before behavior changes.")]
    public string RelatedTests(
        [Description("Symbol name (class/method).")] string name,
        [Description("Owning project name to include project-reference-based test discovery (optional; inferred from the top declaration when omitted).")] string? owningProject = null,
        [Description("Max test-project groups (default 10).")] int limit = 10)
    {
        if (NotReady() is { } notReady) return notReady;
        using var q = _manager.OpenQueries();
        if (owningProject is null)
        {
            var decl = q.SearchSymbols(name, "exact", null, 1, includeGenerated: false).FirstOrDefault();
            if (decl is not null)
            {
                owningProject = q.ProjectsContaining(decl.FilePath).FirstOrDefault(p => !p.IsTest)?.Name;
            }
        }
        var groups = q.RelatedTests(name, owningProject, Math.Clamp(limit, 1, 50));
        // Test association is inferred from naming conventions, project references, and
        // symbol-name co-occurrence — leads, not compiler facts: confidence 'heuristic'.
        var meta = Meta.From(_manager.Health(), "heuristic", "text");
        return Json.WithListBudget(groups, (items, truncated) => new
        {
            name,
            owningProject,
            testGroups = items.Select(g => new
            {
                project = g.TestProject,
                g.Reason,
                // 49k (field): the strongest usage SHAPE among the sampled mention lines —
                // callSite > typeUsage > nameMention — so a test that CALLS the symbol ranks
                // legibly above one that merely names it in a string. Omitted on the
                // non-mention reasons (naming convention / project reference).
                signal = g.Signal,
                matchingFiles = g.MatchingFiles,
                samples = g.Samples.Select(s => new { path = s.FilePath, s.Line, text = s.LineText.Length > 0 ? s.LineText : null }),
            }),
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "dependency_path")]
    [Description("Shortest project-reference chains explaining why one project depends on another.")]
    public string DependencyPath(
        [Description("Depending project name.")] string fromProject,
        [Description("Dependency project name.")] string toProject,
        [Description("Max distinct shortest paths (default 3).")] int maxPaths = 3)
    {
        if (NotReady() is { } notReady) return notReady;
        using var q = _manager.OpenQueries();
        var paths = q.DependencyPaths(fromProject, toProject, Math.Clamp(maxPaths, 1, 10));
        // Edge provenance per hop (bxw, schema v10): 'projectReference' = a real
        // <ProjectReference>; 'hintPathReference' = recovered from <Reference>+HintPath / bare
        // Include (the monolith's multi-staged build). The distinction matters for change
        // planning: a hintPathReference consumer binds to the last-BUILT dll, so refactors ripple
        // only after the staged build re-emits it, and ProjectReference-aware tooling (rename,
        // IDE "find dependent projects") won't see the coupling at all. Arrow strings stay for
        // display; structuredPaths carries the detail. Every consecutive pair in a returned path
        // IS a project_refs row read on this same connection, so the lookup cannot miss.
        var kinds = q.EdgeKindMap();
        string Via(string from, string to) =>
            kinds.TryGetValue((from.ToLowerInvariant(), to.ToLowerInvariant()), out var k)
                ? EdgeKind(k) : EdgeKind("project");
        // Budget-bounded (review F3): structuredPaths ~2.4x the old payload, and deep monolith
        // chains with several path variants measured PAST the 24KB hard wire cap under plain
        // Serialize. Paths trim as PAIRS — the display string and its hops stay in lockstep —
        // and shortest paths are first in, last dropped. found reflects the pre-trim truth.
        var pathItems = paths.Select(p => new
        {
            display = string.Join(" -> ", p),
            hops = p.Select((proj, i) => i == 0
                ? new { project = proj, via = (string?)null }
                : new { project = proj, via = (string?)Via(p[i - 1], proj) }).ToList(),
        }).ToList();
        bool found = paths.Count > 0;
        // eja (field): dependency_path(X, X) returns a trivially-true single-node path — real,
        // but callers had to compare paths[0] == fromProject to notice. One-token clarity,
        // omitted on every normal query (house style: silent when nothing to say).
        bool? sameProject = fromProject.Equals(toProject, StringComparison.OrdinalIgnoreCase) ? true : null;
        var meta = Meta.From(_manager.Health(), "indexed", "text");
        string BuildJson(bool dropStructured) => Json.WithListBudget(pathItems, (items, truncated) => new
        {
            fromProject,
            toProject,
            found,
            sameProject,
            paths = items.Select(i => i.display),
            structuredPaths = dropStructured ? null : (object?)items.Select(i => i.hops),
            structuredPathsOmitted = dropStructured
                ? "a single path exceeded the byte budget — structured hops dropped; the 'paths' strings carry the full chains"
                : null,
            truncated,
            meta,
        });
        string json = BuildJson(dropStructured: false);
        // Lone-item overflow (review, verification round): WithListBudget can never trim below
        // ONE item, and a single very deep path's hops array ALONE can breach the hard cap
        // (repro: a 120-hop chain with ~90-char names → ~27KB, shipped untruncated). Degrade by
        // dropping the STRUCTURED dimension this batch added — flagged, never silent — while the
        // arrow strings keep the full answer at the pre-provenance payload's weight. (A lone
        // DISPLAY string over the cap would be the pre-existing arrow-string exposure, unchanged.)
        if (Json.Utf8Bytes(json) > Json.HardBudgetBytes)
        {
            json = BuildJson(dropStructured: true);
        }
        return json;
    }

    [McpServerTool(Name = "config_lookup")]
    [Description("Find a key/value across configuration files (appsettings*.json, web/app.config, Directory.Build.props, NuGet.config, packages.config).")]
    public string ConfigLookup(
        [Description("Key or value fragment to find.")] string key,
        [Description("Max hits (default 20).")] int limit = 20)
    {
        if (NotReady() is { } notReady) return notReady;
        using var q = _manager.OpenQueries();
        var hits = q.SearchText(key, Math.Clamp(limit, 1, 100), new IndexQueries.TextFilter(Lang: "config"));
        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            key,
            // Surface the same match grading as search_text so a multi-token key whose parts
            // sit on separate config lines is not presented as a full (precise) hit.
            hits = items.Select(h => new
            {
                path = h.FilePath,
                h.Line,
                text = h.LineText,
                matchKind = h.MatchKind,
                matched = h.MatchKind == "partial" ? h.Matched : null,
            }),
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "batch_outline")]
    [Description("Outlines for several files at once (types only per file, budget-shared). Use before deciding which files to read.")]
    public string BatchOutline(
        [Description("Comma-separated workspace-relative paths (max 12).")] string paths,
        [Description("1 = types only (default), 2 = + members.")] int depth = 1)
    {
        if (NotReady() is { } notReady) return notReady;
        var list = SplitCsv(paths) ?? new List<string>();
        var results = list.Take(12)
            .Select(p => System.Text.Json.JsonSerializer.Deserialize<object>(Outline(p, depth), Json.Options)!)
            .ToList();
        return Json.WithListBudget(results, (items, truncated) => new
        {
            outlines = items,
            truncated,
        });
    }

    // ---------------------------------------------------------------- composites

    [McpServerTool(Name = "context_pack")]
    [Description("A compact, deterministic bundle for starting work on a symbol: exact/indexed definition with source, reference summary by project, related tests, owner project edges, and file siblings — within a byte budget. The fastest way to orient before an edit.")]
    public string ContextPack(
        [Description("Symbol name (class/interface/method).")] string name,
        [Description("Optional containing type/namespace fragment to disambiguate.")] string? container = null,
        [Description("Byte budget (default 12288, max 24576).")] int maxBytes = 12288,
        [Description("Semantic definition deadline in ms (default 5000; indexed fallback after).")] int timeoutMs = 5000)
    {
        if (NotReady() is { } notReady) return notReady;
        maxBytes = Math.Clamp(maxBytes, 2048, Json.HardBudgetBytes);
        using var q = _manager.OpenQueries();

        // 1. Definition (semantic first, indexed fallback).
        var (target, _) = ResolveSemanticTarget(name, container, null, null, 0, 0);
        SemanticDeclaration? semDecl = null;
        if (target is { } t)
        {
            (semDecl, _) = _semantic.DefinitionAsync(t.Path, t.Line, t.Column, name, timeoutMs).GetAwaiter().GetResult();
        }
        var indexedDecls = q.SearchSymbols(name, "exact", null, 5, includeGenerated: false);
        if (container is { } c)
        {
            indexedDecls = indexedDecls.Where(h =>
                (h.Container?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Ns?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }
        // Same compiled-declaration preference as impact (3tz gate parity): the semantic path is
        // already gated via ResolveSemanticTarget; this indexed fallback wasn't.
        var orphanedIdx = q.OrphanedPaths(indexedDecls.Select(d => d.FilePath).Distinct(StringComparer.Ordinal).ToList());
        var primaryDecl = indexedDecls.FirstOrDefault(d => !orphanedIdx.Contains(d.FilePath)) ?? indexedDecls.FirstOrDefault();
        string? declPath = semDecl?.Declarations.FirstOrDefault()?.Path ?? primaryDecl?.FilePath;
        int declStart = semDecl?.Declarations.FirstOrDefault()?.StartLine ?? primaryDecl?.StartLine ?? 0;
        int declEnd = Math.Min(semDecl?.Declarations.FirstOrDefault()?.EndLine ?? primaryDecl?.EndLine ?? 0, declStart + 60);

        // 2. Primary source snippet.
        object? primarySource = null;
        if (declPath is not null && declStart > 0)
        {
            string? content = q.ContentByPath(declPath);
            if (content is not null)
            {
                var lines = content.Split('\n');
                var slice = lines.Skip(declStart - 1).Take(declEnd - declStart + 1)
                    .Select((l, i) => $"{declStart + i,5}| {l.TrimEnd('\r')}");
                primarySource = new { path = declPath, startLine = declStart, endLine = declEnd, source = string.Join("\n", slice) };
            }
        }

        // 3. Reference summary (indexed — fast).
        var (refTotal, _, _, refGroups) = q.ReferenceCandidates(name, 300, 1);

        // 4. Related tests.
        string? owner = declPath is not null
            ? q.ProjectsContaining(declPath).FirstOrDefault(p => !p.IsTest)?.Name
            : null;
        var tests = q.RelatedTests(name, owner, 4);

        // 5. Owner project edges.
        var edges = owner is not null ? q.ProjectGraph(owner, 1, "both").Take(14).ToList() : new();

        // 6. Siblings (types in the declaration file).
        var siblings = declPath is not null
            ? q.Outline(declPath).Where(s => s.Kind is "class" or "interface" or "struct" or "enum" && s.Name != name)
                .Select(s => new { s.Name, s.Kind, s.StartLine }).Take(10).ToList<object>()
            : new List<object>();

        var meta = Meta.From(_manager.Health(), semDecl is not null ? "exact" : "indexed", semDecl is not null ? "semantic" : "syntax");
        var omitted = new List<string>();

        object Build(bool dropSiblings, bool dropEdges, bool dropTests, bool dropSource) => new
        {
            name,
            summary = $"{name}: declared in {owner ?? "unknown project"}; {refTotal} candidate references across {refGroups.Count} projects; {tests.Count} related test groups.",
            symbol = semDecl is not null ? SemanticSymbolJson(semDecl) : null,
            declarations = indexedDecls.Select(SymbolJson),
            primarySource = dropSource ? null : primarySource,
            references = new
            {
                totalCandidates = refTotal,
                topProjects = refGroups.Take(6).Select(g => new
                {
                    project = GroupProject(g.Project),
                    orphaned = GroupOrphaned(g.Project), // was the magic "(no project)" string (bxw)
                    g.Count,
                    isTest = g.IsTestProject,
                }),
                confidence = "indexed",
            },
            relatedTests = dropTests ? null : new
            {
                confidence = "heuristic", // naming/project inference, not a compiler fact
                groups = tests.Select(g => new { project = g.TestProject, g.Reason, signal = g.Signal, g.MatchingFiles }),
            },
            // kind rides only on hintPathReference edges (bxw) — plain ProjectReferences omit it
            // (WhenWritingNull) so the orientation bundle stays compact; unusual coupling is the signal.
            ownerProjectEdges = dropEdges ? null : edges.Select(e => new
            {
                from = e.FromProject,
                to = e.ToProject,
                kind = e.Kind == "assembly" ? EdgeKind(e.Kind) : null,
            }),
            siblings = dropSiblings ? null : siblings,
            omittedBecauseBudget = omitted.Count > 0 ? omitted : null,
            meta,
        };

        // Deterministic budget trim: drop lowest-priority categories in order.
        string json = Json.Serialize(Build(false, false, false, false));
        foreach (var (drop, apply) in new (string, Func<string>)[]
                 {
                     ("siblings", () => Json.Serialize(Build(true, false, false, false))),
                     ("ownerProjectEdges", () => Json.Serialize(Build(true, true, false, false))),
                     ("relatedTests", () => Json.Serialize(Build(true, true, true, false))),
                     ("primarySource", () => Json.Serialize(Build(true, true, true, true))),
                 })
        {
            if (Json.Utf8Bytes(json) <= maxBytes) break;
            omitted.Add(drop);
            json = apply();
        }
        return json;
    }

    [McpServerTool(Name = "impact")]
    [Description("First-pass blast radius before changing a symbol: reference volume by project, dependent-project count, public API exposure, related tests, and deterministic risk notes.")]
    public string Impact(
        [Description("Symbol name to assess.")] string name,
        [Description("Optional containing type/namespace fragment.")] string? container = null)
    {
        if (NotReady() is { } notReady) return notReady;
        using var q = _manager.OpenQueries();

        var decls = q.SearchSymbols(name, "exact", null, 10, includeGenerated: true);
        if (container is { } c)
        {
            decls = decls.Where(h =>
                (h.Container?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Ns?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }
        // Prefer a COMPILED declaration for ownership — the 3tz resolution-gate parity this
        // indexed path never got (field 0.7.4: the ORPHANED Core copy of the flagship interface
        // sorted first, ProjectsContaining(orphanedPath) came back empty, owner stayed null, and
        // transitiveDependentProjects reported 0 on an interface referenced across 91 projects).
        var orphanedDecls = q.OrphanedPaths(decls.Select(d => d.FilePath).Distinct(StringComparer.Ordinal).ToList());
        var primary = decls.FirstOrDefault(d => !orphanedDecls.Contains(d.FilePath)) ?? decls.FirstOrDefault();
        string? owner = primary is not null
            ? q.ProjectsContaining(primary.FilePath).FirstOrDefault(p => !p.IsTest)?.Name
            : null;

        // Physical prod/test split (0ok): the old per-group sums double-counted files linked into
        // several projects, so impact could report more prod+test references than lines exist.
        var (refTotal, prodRefs, testRefs, refGroups) = q.ReferenceCandidates(name, 500, 0);
        int dependents = owner is not null ? q.DependentClosure(owner).Count : 0;
        // Direct-dependent provenance split (bxw, schema v10): a dependent wired ONLY via
        // <Reference>+HintPath (kind 'assembly' on every edge — same-name pair rows can mint two)
        // binds to the last-BUILT dll of the multi-staged build, and ProjectReference-aware
        // tooling (rename, IDE "find dependent projects") won't follow the edge. A dependent with
        // ANY real ProjectReference edge is excluded — that wiring does carry refactors.
        // Grouped by NAME (the graph is name-keyed; pair rows must not count twice).
        var directDependentGroups = owner is null
            ? new List<IGrouping<string, GraphEdge>>()
            : q.ProjectGraph(owner, 1, "upstream").GroupBy(e => e.FromProject, StringComparer.OrdinalIgnoreCase).ToList();
        int hintPathOnlyConsumers = directDependentGroups.Count(g => g.All(e => e.Kind == "assembly"));
        var tests = q.RelatedTests(name, owner, 6);
        bool isPublic = primary?.Accessibility == "public";
        bool isPartial = primary?.IsPartial ?? false;

        var risks = new List<string>();
        if (isPublic && dependents > 0) risks.Add($"Public symbol; {dependents} projects transitively depend on {owner}.");
        if (hintPathOnlyConsumers > 0) risks.Add(
            $"{hintPathOnlyConsumers} of {directDependentGroups.Count} direct dependents consume {owner} only via <Reference>/HintPath (multi-staged build) — they bind to the last-built dll and ProjectReference-aware refactor tooling won't follow those edges.");
        if (refGroups.Count > 10) risks.Add($"Referenced in {refGroups.Count} projects — wide blast radius.");
        if (testRefs == 0 && prodRefs > 0) risks.Add("No test references found — behavior changes are unguarded.");
        if (isPartial) risks.Add("Partial declarations — check all declaration files before editing.");
        if (decls.Count > 1) risks.Add($"{decls.Count} declarations share this name — verify you target the right one (use 'container').");
        if (risks.Count == 0) risks.Add("Narrow surface: few references and dependents detected.");

        return Json.Serialize(new
        {
            name,
            declaration = primary is null ? null : SymbolJson(primary),
            owningProject = owner,
            references = new
            {
                totalCandidates = refTotal,
                production = prodRefs,
                test = testRefs,
                projects = refGroups.Count,
                topProjects = refGroups.Take(8).Select(g => new
                {
                    project = GroupProject(g.Project),
                    orphaned = GroupOrphaned(g.Project), // was the magic "(no project)" string (bxw)
                    g.Count,
                }),
            },
            // Kind split only at depth 1 — transitive stays a single number: a mixed path
            // (projectRef hop then hintPathRef hop) has no honest single per-kind bucket.
            directDependentProjects = owner is null ? null : new
            {
                total = directDependentGroups.Count,
                viaProjectReference = directDependentGroups.Count - hintPathOnlyConsumers,
                viaHintPathOnly = hintPathOnlyConsumers,
            },
            transitiveDependentProjects = dependents,
            // ctx (field): the bare number next to the split READ as an oversight — make the
            // design decision visible in the response, but only when assembly wiring is actually
            // in the picture (all-projectReference graphs have nothing to explain).
            transitiveNote = dependents > 0 && directDependentGroups.Any(g => g.Any(e => e.Kind == "assembly"))
                ? "transitiveDependentProjects is a single count by design: transitive paths can mix projectReference and hintPathReference hops, so a per-kind transitive split would be dishonest — directDependentProjects carries the per-kind split at depth 1."
                : null,
            relatedTests = new
            {
                confidence = "heuristic", // naming/project inference, not a compiler fact
                groups = tests.Select(g => new { project = g.TestProject, g.Reason, signal = g.Signal }),
            },
            publicApi = isPublic,
            risks,
            note = "Reference counts are indexed candidates; run references(mode='semantic') for compiler-exact counts before risky changes.",
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
    }

    // ---------------------------------------------------------------- shared fallback

    private string IndexedReferencesFallback(string? name, string? reason)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Json.Serialize(new { error = "symbol_not_resolved", partialReason = reason });
        }
        using var q = _manager.OpenQueries();
        var (total, _, _, groups) = q.ReferenceCandidates(name, 300, 2);
        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(groups, (items, truncated) => new
        {
            name,
            partialReason = reason,
            note = "Semantic resolution unavailable — indexed whole-identifier candidates instead.",
            totalCandidates = total,
            groups = items.Select(g => new
            {
                // Structured orphan attribution here too (review F1): this fallback serves
                // callers/callees, and it was the ONE group emitter still shipping the magic
                // "(no project)" string after bxw replaced it everywhere else.
                project = GroupProject(g.Project),
                orphaned = GroupOrphaned(g.Project),
                isTest = g.IsTestProject,
                count = g.Count,
                samples = g.Samples.Select(s => new { path = s.FilePath, s.Line, text = s.LineText }),
            }),
            truncated,
            meta,
        });
    }
}
