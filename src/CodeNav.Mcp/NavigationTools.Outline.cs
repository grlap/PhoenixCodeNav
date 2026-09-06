using System.ComponentModel;
using CodeNav.Core.Indexing;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: outline and source_context — dispatch, C# syntactic outlines, and bounded live source reads, including ReadLinesUpTo.
/// Does not own: F# outline shaping (NavigationTools.FSharp.cs), search, or compiler-semantic navigation.
/// </summary>
public sealed partial class NavigationTools
{
    // ---------------------------------------------------------------- outline / source

    [McpServerTool(Name = "outline")]
    [Description("Syntactic map of a file (namespaces, types, members with line spans) without reading the body. A file_not_indexed response may include pathSuggestions with up to three ranked pinned-index paths plus total/truncated coverage. ALWAYS call this before reading a large file, then fetch only needed spans via source_context. For F# files, selectedParseContext and availableParseContexts identify only the .fsproj/target-framework parser options used for #if and syntax; they do not select assemblies, builds, reference resolution, or semantic workspaces.")]
    public string Outline(
        [Description("Workspace-relative file path (forward slashes).")] string path,
        [Description("1 = namespaces + types, 2 = + members (default), 3 = reserved (currently same as 2).")] int depth = 2)
    {
        if (NotReady() is { } notReady) return notReady;
        string normPath = NormalizePath(path);
        using var q = _manager.OpenQueries();
        FileHit? file = q.FileByPath(normPath);
        if (file is null)
        {
            PathSuggestionResult suggestions = q.SuggestFilePaths(normPath);
            return Json.WithListBudget(
                suggestions.Paths.ToList(),
                (suggestionPaths, suggestionsBudgetTruncated) => new
                {
                    error = "file_not_indexed",
                    path,
                    pathSuggestions = PathSuggestionsJson(
                        suggestions.Total,
                        suggestionPaths,
                        suggestionsBudgetTruncated),
                    meta = Meta.From(_manager.Health(), "indexed", "syntax"),
                });
        }
        if (file.Language == "fs" &&
            (Path.GetExtension(normPath).Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(normPath).Equals(".fsi", StringComparison.OrdinalIgnoreCase)))
        {
            return FSharpOutline(path, normPath, file, depth);
        }
        if (file.Language != "cs")
        {
            return UnsupportedLanguage(path, file.Language, "outline");
        }
        var rows = q.Outline(normPath);

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
        // Arity is part of the identity (szs): partial Foo and partial Foo<T> in the SAME file
        // are different types with different partial-file sets — one cache slot each.
        var partialCache = new Dictionary<(string Name, string? Ns, string Kind, string? Container, int Arity), List<string>>();

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
                var key = (s.Name, s.Ns, s.Kind, s.Container, s.Arity);
                if (!partialCache.TryGetValue(key, out var others))
                {
                    // Arity-matched (szs): without it, outline(FooOfT.cs) listed partial class Foo's
                    // files as Foo<T>'s "other halves" — navigation to the WRONG type's declarations.
                    others = q.PartialDeclarationFiles(s.Name, s.Ns, s.Kind, s.Container, normPath, s.Arity); // up to 11
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
                modifiers = s.Modifiers, // bt7: virtual/override/abstract/static/sealed..., omitted when none
                accessors = AccessorsJson(s.Accessors), // hu7: {get, set} only when an accessor differs
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
        bool generated = file.IsGenerated;

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
    [Description("Bounded live source read around one or more line spans (the bridge from navigation results to actual code). A file_not_found response may include pathSuggestions with up to three ranked pinned-index paths plus total/truncated coverage. Use canonical spans from outline/definition/search results instead of reading whole files; range is accepted as a compatibility alias when spans is omitted. Explicit whitespace, empty CSV items, and empty JSON arrays are bad_request.")]
    public string SourceContext(
        [Description("Workspace-relative file path. Optional when symbolId is given.")] string? path = null,
        [Description("Spans as 'start-end' or 'line'. Accepts CSV ('42-88,120') or a JSON-array encoded string ('[\"42-88\",\"120\"]'). Empty CSV items, empty JSON arrays, and whitespace-only values are rejected. Optional when symbolId is given (defaults to the symbol's declaration span).")] string spans = "",
        [Description("Extra context lines around each span (default 2).")] int contextLines = 2,
        [Description("Byte budget for returned source (default 8192, max 65536).")] int maxBytes = 8192,
        [Description("Show one symbol's source by handle instead of path+spans: 'idx:NNN' from a prior result. Overrides path/spans/range with the symbol's declaration span. Note: 'idx:' handles are index-local and change on reindex.")] string? symbolId = null,
        [Description("Compatibility alias for spans. Use only when spans is omitted; conflicting simultaneous values, whitespace-only values, empty CSV items, and empty JSON arrays return bad_request.")] string? range = null)
    {
        if (NotReady() is { } notReady) return notReady;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            path = hit!.FilePath;
            spans = $"{hit.StartLine}-{hit.EndLine}";
        }
        else if ((spans.Length > 0 && string.IsNullOrWhiteSpace(spans)) ||
                 (range is { Length: > 0 } && string.IsNullOrWhiteSpace(range)))
        {
            return StringListBadRequest("spans",
                "spans and range must not be whitespace-only.");
        }
        else if (!string.IsNullOrEmpty(range))
        {
            if (!string.IsNullOrEmpty(spans) && !string.Equals(spans, range, StringComparison.Ordinal))
            {
                return Json.Serialize(new
                {
                    error = "bad_request",
                    detail = "Provide only one of 'spans' or its compatibility alias 'range', or make them identical.",
                });
            }
            if (string.IsNullOrEmpty(spans)) spans = range;
        }
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(spans))
        {
            return Json.Serialize(new
            {
                error = "bad_request",
                detail = "Provide 'symbolId', or 'path' with 'spans' (compatibility alias: 'range').",
            });
        }
        path = NormalizePath(path);
        maxBytes = Math.Clamp(maxBytes, 256, Json.HardBudgetBytes);
        // Clamped BEFORE it enters line arithmetic: an absurd contextLines (int.MaxValue) would
        // overflow start/end math and defeat the bounded read below.
        contextLines = Math.Clamp(contextLines, 0, 500);

        // Parse the span specs BEFORE reading anything (gep): the requested spans bound how much
        // of the file we ever materialize. Unparsable specs are skipped. Zero/negative starts are
        // CLAMPED to line 1, not rejected — the old code accepted them ("0-10" rendered lines
        // 1..12) and 0-based callers are common; rejecting would be a silent-empty (review).
        if (!TryParseStringList(spans, "spans", out List<string>? spanSpecs, out string? spansError))
            return StringListBadRequest("spans", spansError);
        var ranges = new List<(int Start, int End)>();
        foreach (string spec in spanSpecs!)
        {
            var parts = spec.Split('-');
            if (!int.TryParse(parts[0], out int start)) continue;
            int end = parts.Length > 1 && int.TryParse(parts[1], out int e) ? e : start;
            start = Math.Max(1, start);
            end = Math.Max(1, end);
            if (end < start) continue; // inverted range — yields nothing
            ranges.Add((start, end));
        }
        // Long arithmetic: end near int.MaxValue plus context must saturate, not wrap negative.
        int maxNeededLine = 0;
        foreach (var r in ranges)
            maxNeededLine = (int)Math.Min(int.MaxValue, Math.Max(maxNeededLine, r.End + (long)contextLines));

        // Reject paths that escape the workspace root before touching the filesystem.
        if (!CodeNav.Core.WorkspacePaths.TryResolveInside(_manager.WorkspaceRoot, path, out string full))
        {
            return Json.Serialize(new { error = "path_outside_workspace", path, meta = Meta.From(_manager.Health(), "indexed", "text") });
        }

        string freshness = "live";
        // Live read is BOUNDED (gep): stream lines only up to the last requested line and stop —
        // never File.ReadAllText, which materialized entire files (a multi-hundred-MB artifact in
        // the workspace = one allocation spike per call) just to slice a few lines out.
        // Skip paths reaching outside via a symlink/junction (target or any ancestor) so an
        // in-workspace link cannot be followed to external content; fall through to the index.
        IReadOnlyList<string>? lines = null;
        PathSuggestionResult suggestions = new([], 0);
        if (File.Exists(full) && !CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(_manager.WorkspaceRoot, full))
        {
            lines = ReadLinesUpTo(full, maxNeededLine);
        }
        if (lines is null)
        {
            // Index fallback is keyed by relative path, so it can only ever return in-workspace
            // content — a contained path that is simply not on disk. This path still materializes
            // the stored content whole (no per-file size cap exists yet — rs7); the DoS-relevant
            // vector was the LIVE read of arbitrary on-disk files, which is now bounded above.
            using var q = _manager.OpenQueries();
            string? content = q.ContentByPath(path);
            if (content is not null) lines = content.Split('\n');
            else suggestions = q.SuggestFilePaths(path);
            freshness = "index";
        }
        if (lines is null)
        {
            return Json.WithListBudget(
                suggestions.Paths.ToList(),
                (suggestionPaths, suggestionsBudgetTruncated) => new
                {
                    error = "file_not_found",
                    path,
                    pathSuggestions = PathSuggestionsJson(
                        suggestions.Total,
                        suggestionPaths,
                        suggestionsBudgetTruncated),
                    meta = Meta.From(_manager.Health(), "indexed", "text"),
                });
        }

        (List<object> Spans, bool Truncated) BuildSpans(long rawBudget)
        {
            var spanResults = new List<object>();
            long budget = rawBudget;
            bool truncated = false;
            foreach (var (rawStart, rawEnd) in ranges)
            {
                int start = Math.Max(1, rawStart - contextLines);
                int end = Math.Min(lines.Count, (int)Math.Min(int.MaxValue, rawEnd + (long)contextLines));

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

    /// <summary>Streams at most <paramref name="maxLines"/> lines from a file, then stops reading
    /// (gep): the caller's spans bound the read, so a giant file costs only its requested prefix —
    /// never a whole-file materialization. Returns null on IO/access failure (caller falls back to
    /// index content). Line endings are normalized by ReadLine; the formatter's TrimEnd('\r') stays
    /// correct for both this path and the index path's Split('\n').</summary>
    private static List<string>? ReadLinesUpTo(string fullPath, int maxLines)
    {
        try
        {
            var list = new List<string>(Math.Min(maxLines, 4096));
            using var sr = new StreamReader(fullPath);
            string? line;
            while (list.Count < maxLines && (line = sr.ReadLine()) is not null) list.Add(line);
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
