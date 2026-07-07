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

        var (target, hint) = ResolveSemanticTarget(name, null, "class,interface,struct,record,enum", path, line, column);
        if (target is not { } t)
        {
            return Json.Serialize(new { error = "target_not_found_in_index", name });
        }
        var (result, coverage, reason) = _semantic
            .TypeHierarchyAsync(t.Path, t.Line, t.Column, hint, maxProjects, timeoutMs)
            .GetAwaiter().GetResult();
        if (result is null)
        {
            return Json.Serialize(new
            {
                error = "semantic_unavailable",
                partialReason = reason,
                hint = "Use 'implementations' for its indexed fallback, or search_symbol.",
                meta = Meta.From(_manager.Health(), "indexed", "semantic"),
            });
        }
        var down = result.DerivedOrImplementing;
        var meta1 = Meta.From(_manager.Health(), "exact", "semantic");
        return Json.WithListBudget(down, (items, truncated) => new
        {
            symbol = SemanticSymbolJson(result.Symbol),
            baseTypes = result.BaseTypes.Select(SemanticSymbolJson),
            interfaces = result.Interfaces.Select(SemanticSymbolJson),
            derivedOrImplementing = items.Select(SemanticSymbolJson),
            coverage = coverage is null ? null : CoverageJson(coverage),
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
                matchingFiles = g.MatchingFiles,
                samples = g.Samples.Select(s => new { path = s.FilePath, s.Line }),
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
        return Json.Serialize(new
        {
            fromProject,
            toProject,
            found = paths.Count > 0,
            paths = paths.Select(p => string.Join(" -> ", p)),
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
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
        var primaryDecl = indexedDecls.FirstOrDefault();
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
        var (refTotal, refGroups) = q.ReferenceCandidates(name, 300, 1);

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
                topProjects = refGroups.Take(6).Select(g => new { project = g.Project, g.Count, isTest = g.IsTestProject }),
                confidence = "indexed",
            },
            relatedTests = dropTests ? null : new
            {
                confidence = "heuristic", // naming/project inference, not a compiler fact
                groups = tests.Select(g => new { project = g.TestProject, g.Reason, g.MatchingFiles }),
            },
            ownerProjectEdges = dropEdges ? null : edges.Select(e => new { from = e.FromProject, to = e.ToProject }),
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
        var primary = decls.FirstOrDefault();
        string? owner = primary is not null
            ? q.ProjectsContaining(primary.FilePath).FirstOrDefault(p => !p.IsTest)?.Name
            : null;

        var (refTotal, refGroups) = q.ReferenceCandidates(name, 500, 0);
        int prodRefs = refGroups.Where(g => !g.IsTestProject).Sum(g => g.Count);
        int testRefs = refGroups.Where(g => g.IsTestProject).Sum(g => g.Count);
        int dependents = owner is not null ? q.DependentClosure(owner).Count : 0;
        var tests = q.RelatedTests(name, owner, 6);
        bool isPublic = primary?.Accessibility == "public";
        bool isPartial = primary?.IsPartial ?? false;

        var risks = new List<string>();
        if (isPublic && dependents > 0) risks.Add($"Public symbol; {dependents} projects transitively depend on {owner}.");
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
                topProjects = refGroups.Take(8).Select(g => new { project = g.Project, g.Count }),
            },
            transitiveDependentProjects = dependents,
            relatedTests = new
            {
                confidence = "heuristic", // naming/project inference, not a compiler fact
                groups = tests.Select(g => new { project = g.TestProject, g.Reason }),
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
        var (total, groups) = q.ReferenceCandidates(name, 300, 2);
        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(groups, (items, truncated) => new
        {
            name,
            partialReason = reason,
            note = "Semantic resolution unavailable — indexed whole-identifier candidates instead.",
            totalCandidates = total,
            groups = items.Select(g => new
            {
                project = g.Project,
                isTest = g.IsTestProject,
                count = g.Count,
                samples = g.Samples.Select(s => new { path = s.FilePath, s.Line, text = s.LineText }),
            }),
            truncated,
            meta,
        });
    }
}
