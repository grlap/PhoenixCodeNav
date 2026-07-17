using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Mcp;

public sealed partial class NavigationTools
{
    internal const int MaxFSharpOutlineParseContexts = 64;

    private string FSharpOutline(string path, string normalizedPath, FileHit file, int depth)
    {
        FSharpOutlineResult result = _semantic.FSharpOutline(normalizedPath);
        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        if (result.Error is { } error)
        {
            string detail = error switch
            {
                "fsharp_project_not_found" =>
                    "F# outline requires the file to be a compile item of an indexed .fsproj.",
                "fsharp_project_options_unavailable" =>
                    "The indexed owning .fsproj is unavailable or cannot provide parser options.",
                "fsharp_project_options_conflict" =>
                    "Multiple owning .fsproj files provide different F# parser options.",
                "file_too_large" =>
                    "The indexed F# source exceeds the structural parse limit.",
                "file_content_unavailable" =>
                    "The indexed F# source content is unavailable for parsing.",
                "fsharp_parse_failed" =>
                    "FCS reported syntax errors; no partial outline was returned.",
                _ => "FCS could not produce an outline for this file.",
            };
            return Json.Serialize(new
            {
                error,
                operation = "outline",
                path,
                detail,
                fileBytes = result.FileBytes,
                maxBytes = result.MaxBytes,
                meta,
            });
        }

        object Node(FSharpOutlineItem item, bool includeMembers)
        {
            List<object>? members = null;
            if (includeMembers && item.Members.Count > 0)
                members = item.Members.Select(member => Node(member, includeMembers: true)).ToList();

            return new
            {
                item.Name,
                item.Kind,
                item.Signature,
                item.Accessibility,
                modifiers = item.Modifiers,
                accessors = item.Accessors,
                item.StartLine,
                item.EndLine,
                isPartial = (bool?)null,
                partialFiles = (object?)null,
                partialFilesTruncated = (bool?)null,
                attributes = (object?)null,
                members,
            };
        }

        object? selectedParseContext = result.SelectedProject is null
            ? null
            : new
            {
                project = result.SelectedProject,
                targetFramework = result.SelectedTargetFramework,
            };
        var allAvailableParseContexts = result.AvailableParseContexts?
            .Select(context => (object)new
            {
                project = context.Project,
                targetFramework = context.TargetFramework,
            })
            .ToList() ?? [];
        int availableParseContextsTotal = allAvailableParseContexts.Count;
        var availableParseContexts = allAvailableParseContexts
            .Take(MaxFSharpOutlineParseContexts)
            .ToList();
        bool availableParseContextsLimitTruncated =
            availableParseContextsTotal > availableParseContexts.Count;

        string BuildNested(bool includeMembers, bool truncated) => Json.Serialize(new
        {
            path,
            isGenerated = file.IsGenerated,
            symbols = result.Symbols.Select(symbol => Node(symbol, includeMembers)).ToList(),
            truncated,
            partial = result.PartialReason is not null ? true : (bool?)null,
            partialReason = result.PartialReason,
            selectedParseContext,
            availableParseContexts,
            availableParseContextsTotal,
            availableParseContextsReturned = availableParseContexts.Count,
            availableParseContextsTruncated = availableParseContextsLimitTruncated,
            meta,
        });

        string nested = BuildNested(includeMembers: depth >= 2, truncated: false);
        if (Json.Utf8Bytes(nested) <= Json.HardBudgetBytes) return nested;

        if (depth >= 2)
        {
            string rootsOnly = BuildNested(includeMembers: false, truncated: true);
            if (Json.Utf8Bytes(rootsOnly) <= Json.HardBudgetBytes) return rootsOnly;
        }

        var flat = result.Symbols
            .Select(symbol => (object)new
            {
                symbol.Name,
                symbol.Kind,
                symbol.StartLine,
                symbol.EndLine,
            })
            .ToList();
        return Json.WithAuxiliaryListBudget(flat, availableParseContexts,
            (items, _, parseContexts, parseContextsByteTruncated) => new
        {
            path,
            isGenerated = file.IsGenerated,
            symbols = items,
            truncated = true,
            partial = result.PartialReason is not null ? true : (bool?)null,
            partialReason = result.PartialReason,
            selectedParseContext,
            availableParseContexts = parseContexts,
            availableParseContextsTotal,
            availableParseContextsReturned = parseContexts.Count,
            availableParseContextsTruncated =
                availableParseContextsLimitTruncated || parseContextsByteTruncated,
            note = "File has too many declarations for a full outline; showing bounded top-level declarations.",
            meta,
        });
    }
}
