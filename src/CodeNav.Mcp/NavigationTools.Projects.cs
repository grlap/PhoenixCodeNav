using System.ComponentModel;
using CodeNav.Core.Indexing;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: project_graph and projects_containing graph/ownership response shaping.
/// Does not own: project discovery, index construction, or shared response primitives.
/// </summary>
public sealed partial class NavigationTools
{
    // ---------------------------------------------------------------- projects

    [McpServerTool(Name = "project_graph")]
    [Description("Project dependency edges around a project (upstream = dependents, downstream = dependencies). Use to understand ownership and blast radius before changes. Graph answers are assembly-name-keyed; exact project-file selectors still identify physical index rows.")]
    public string ProjectGraph(
        [Description("Project name, AssemblyName metadata, or exact workspace-relative .csproj/.fsproj path.")] string project,
        [Description("Traversal depth (default 2, max 5).")] int depth = 2,
        [Description("'upstream'/'dependents', 'downstream'/'dependencies', or 'both' (default). Responses echo the canonical value.")] string direction = "both")
    {
        if (NotReady() is { } notReady) return notReady;
        string normalizedDirection = string.IsNullOrWhiteSpace(direction)
            ? "both"
            : direction.Trim().ToLowerInvariant();
        direction = normalizedDirection switch
        {
            "dependencies" => "downstream",
            "dependents" => "upstream",
            "upstream" or "downstream" or "both" => normalizedDirection,
            _ => "invalid",
        };
        if (direction == "invalid")
        {
            return Json.Serialize(new
            {
                error = "bad_request",
                field = "direction",
                validValues = new[] { "upstream", "downstream", "both", "dependencies", "dependents" },
                detail = "direction must name a canonical direction or its agent-facing alias.",
                meta = Meta.From(_manager.Health(), "indexed", "text"),
            });
        }
        depth = Math.Clamp(depth, 1, 5);
        using var q = _manager.OpenQueries();
        (ResolvedProjectSelector? projectSelection, string? selectorError) =
            ResolveProjectSelector(q, project, "project", "project_graph");
        if (selectorError is not null) return selectorError;
        ProjectRow root = projectSelection!.Project;
        var edges = q.ProjectGraph(root!.Name, depth, direction);
        var nodes = edges.SelectMany(e => new[] { e.FromProject, e.ToProject })
            .Append(root.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Dictionary<string, string> projectLanguages = q.ProjectLanguages(nodes);
        string LanguageOf(string name) => projectLanguages.TryGetValue(name, out string? language)
            ? language
            : "unknown";

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithStringBudget(project, Json.HardBudgetBytes,
            (boundedSelector, selectorTruncated) =>
                System.Text.Json.Nodes.JsonNode.Parse(Json.WithDiagnosticListBudget(
                    edges, projectSelection.ShadowedMatches,
                    (items, truncated, shadowedMatches, shadowedMatchesTruncated) => new
                    {
                        root = new { root.Name, root.Path, root.Style, language = LanguageOf(root.Name), root.Tfms, root.IsTest },
                        projectSelector = boundedSelector,
                        projectSelectorTruncated = selectorTruncated ? true : (bool?)null,
                        projectSelectorBytes = selectorTruncated ? Json.Utf8Bytes(project) : (int?)null,
                        projectSelectorResolution = ProjectSelectorResolutionJson(
                            projectSelection.ShadowedMatches.Count, shadowedMatches,
                            shadowedMatchesTruncated),
                        direction,
                        depth,
                        nodeCount = nodes.Count,
                        // Edge provenance (bxw, schema v10): 'projectReference' = a real <ProjectReference>;
                        // 'hintPathReference' = recovered from <Reference>+HintPath / bare Include (the
                        // multi-staged build). kind was previously HARDCODED 'projectReference' for every
                        // edge — presenting binary couplings as source-graph ones.
                        edges = items.Select(e => new
                        {
                            from = e.FromProject,
                            fromLanguage = LanguageOf(e.FromProject),
                            to = e.ToProject,
                            toLanguage = LanguageOf(e.ToProject),
                            kind = EdgeKind(e.Kind),
                        }),
                        truncated,
                        meta,
                    }, TestOnlyProjectSelectorResponseMaxBytes))!,
            TestOnlyProjectSelectorResponseMaxBytes);
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
            projects = projects.Select(p => new
            {
                p.Name,
                p.Path,
                p.Style,
                language = p.Language,
                p.Tfms,
                p.IsTest,
            }),
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
    }
}
