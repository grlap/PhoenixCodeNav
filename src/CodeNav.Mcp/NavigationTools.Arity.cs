using CodeNav.Core.Indexing;

namespace CodeNav.Mcp;

public sealed partial class NavigationTools
{
    private sealed record ArityTargetSelection(
        string? Name,
        string? Path,
        int Line,
        int Column,
        int? Arity,
        bool AllowHeuristicFallback);

    /// <summary>
    /// Resolves only the generic-arity part of a tool target. Project ownership, target
    /// frameworks, assembly names, and semantic cluster construction deliberately remain
    /// outside this helper: those are not part of generic symbol identity selection.
    /// </summary>
    private (ArityTargetSelection? Selection, string? Error) ResolveArityTarget(
        string? name,
        string? path,
        int line,
        int column,
        int? arity,
        string? symbolId,
        bool typeOnly)
    {
        IReadOnlyList<string> hierarchyKinds =
            ["class", "interface", "struct", "record", "enum"];
        IReadOnlyList<string>? preferredKinds = typeOnly ? hierarchyKinds : TypeKinds;

        if (arity is < 0)
        {
            return (null, Json.Serialize(new
            {
                error = "bad_request",
                detail = "arity must be zero or greater.",
                meta = Meta.From(_manager.Health(), "indexed", "syntax"),
            }));
        }

        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return (null, error);
            if (typeOnly && !hierarchyKinds.Contains(hit!.Kind, StringComparer.Ordinal))
            {
                return (null, Json.Serialize(new
                {
                    error = "bad_request",
                    detail = "type_hierarchy symbolId must identify a type declaration.",
                    meta = Meta.From(_manager.Health(), "indexed", "syntax"),
                }));
            }

            return (new ArityTargetSelection(
                hit!.Name, hit.FilePath, hit.StartLine, 0, hit.Arity,
                AllowHeuristicFallback: true), null);
        }

        if (name is null && (path is null || line <= 0))
        {
            return (null, Json.Serialize(new
            {
                error = "bad_request",
                detail = "Provide 'symbolId', 'name', or 'path'+'line'.",
                meta = Meta.From(_manager.Health(), "indexed", "syntax"),
            }));
        }

        // A source position remains semantic authority. When it is also an indexed declaration
        // line, carry its arity into Roslyn and the syntax fallback. A usage line with a mixed-
        // arity name remains eligible for exact semantic resolution, but its name-only fallback
        // is withheld because that fallback could merge distinct generic identities.
        if (path is not null && line > 0)
        {
            using var positionQueries = _manager.OpenQueries();
            string normalizedPath = NormalizePath(path);
            List<SymbolHit> lineHits = positionQueries.SymbolsStartingAt(normalizedPath, line);
            if (name is { Length: > 0 })
            {
                lineHits = lineHits
                    .Where(hit => string.Equals(hit.Name, name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (preferredKinds is not null)
                {
                    List<SymbolHit> preferred = lineHits
                        .Where(hit => preferredKinds.Contains(hit.Kind, StringComparer.Ordinal))
                        .ToList();
                    if (preferred.Count > 0) lineHits = preferred;
                }
            }
            else if (lineHits.Count == 1)
            {
                name = lineHits[0].Name;
            }

            List<int> positionArities = lineHits.Select(hit => hit.Arity).Distinct().Order().ToList();
            if (arity is null && positionArities.Count == 1)
                arity = positionArities[0];

            bool allowHeuristicFallback = arity is not null;
            if (!allowHeuristicFallback && name is { Length: > 0 })
            {
                List<int> globalArities = positionQueries.SymbolArities(name, preferredKinds);
                if (!typeOnly && globalArities.Count == 0)
                    globalArities = positionQueries.SymbolArities(name);
                if (globalArities.Count == 1)
                {
                    arity = globalArities[0];
                    allowHeuristicFallback = true;
                }
                else if (globalArities.Count == 0)
                {
                    // No indexed mixed-arity evidence can contaminate the old name fallback.
                    allowHeuristicFallback = true;
                }
            }

            return (new ArityTargetSelection(
                name, normalizedPath, line, column, arity, allowHeuristicFallback), null);
        }

        using var queries = _manager.OpenQueries();
        IReadOnlyList<string>? candidateKinds = preferredKinds;
        List<int> availableArities = queries.SymbolArities(name!, candidateKinds);

        // implementations also supports interface members. Preserve that behavior only when
        // no type with the requested name exists; types retain the existing precedence.
        if (!typeOnly && availableArities.Count == 0)
        {
            candidateKinds = null;
            availableArities = queries.SymbolArities(name!);
        }

        if (arity is { } requestedArity)
        {
            if (availableArities.Count > 0 && !availableArities.Contains(requestedArity))
            {
                return (null, Json.Serialize(new
                {
                    error = "symbol_not_found",
                    name,
                    arity = requestedArity,
                    availableArities,
                    detail = "No exact-name declaration has the requested generic arity.",
                    meta = Meta.From(_manager.Health(), "indexed", "syntax"),
                }));
            }
        }
        else if (availableArities.Count > 1)
        {
            return (null, ArityAmbiguityResponse(name!, candidateKinds, availableArities, queries));
        }
        else if (availableArities.Count == 1)
        {
            arity = availableArities[0];
        }

        return (new ArityTargetSelection(
            name, path, line, column, arity, AllowHeuristicFallback: true), null);
    }

    private string ArityAmbiguityResponse(
        string name,
        IReadOnlyList<string>? kinds,
        IReadOnlyList<int> availableArities,
        IndexQueries queries)
    {
        const int maxRepresentatives = 64;
        var representatives = new List<SymbolHit>();
        foreach (int candidateArity in availableArities.Take(maxRepresentatives))
        {
            SymbolHit? representative = SelectArityRepresentative(
                queries, name, kinds, candidateArity);
            if (representative is not null) representatives.Add(representative);
        }

        return Json.WithListBudget(representatives, (items, byteTruncated) => new
        {
            error = "symbol_ambiguous",
            name,
            detail = "The exact name resolves to multiple generic arities. Pass arity, path+line, or a candidate symbolId.",
            candidates = items.Select(SymbolJson),
            candidateCount = availableArities.Count,
            candidatesTruncated = byteTruncated || representatives.Count < availableArities.Count
                ? true
                : (bool?)null,
            meta = Meta.From(_manager.Health(), "indexed", "syntax"),
        });
    }

    private static SymbolHit? SelectArityRepresentative(
        IndexQueries queries,
        string name,
        IReadOnlyList<string>? kinds,
        int arity)
    {
        List<SymbolHit> hits = queries.SearchSymbols(
            name, "exact", kinds, 20, includeGenerated: true, arity: arity);
        if (hits.Count == 0) return null;

        var orphaned = queries.OrphanedPaths(
            hits.Select(hit => hit.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        if (orphaned.Count > 0)
        {
            List<SymbolHit> live = hits.Where(hit => !orphaned.Contains(hit.FilePath)).ToList();
            if (live.Count > 0) hits = live;
        }

        return hits.OrderBy(hit => hit.Kind switch
        {
            "interface" => 0,
            "class" or "struct" or "record" or "record_struct" => 1,
            _ => 2,
        }).FirstOrDefault();
    }
}
