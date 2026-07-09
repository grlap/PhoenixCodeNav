using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

public sealed record SymbolHit(
    long Id, string Kind, string Name, string? Ns, string? Container, string Signature,
    string Accessibility, int StartLine, int EndLine, bool IsPartial, string? AttrMarkers,
    string FilePath, bool FileIsGenerated, long? ParentId, bool IsOrphaned = false,
    int Arity = 0,               // generic type-parameter count — Foo and Foo<T> are DIFFERENT types (szs)
    string? Modifiers = null);   // "static sealed abstract virtual override new readonly const" subset (bt7)

public sealed record FileHit(long Id, string Path, long Size, int LineCount, bool IsGenerated);

public sealed record TextHit(
    string FilePath, int Line, string LineText, bool IsGenerated,
    string MatchKind = "precise", IReadOnlyList<string>? Matched = null,
    IReadOnlyList<string>? Before = null, IReadOnlyList<string>? After = null);

/// <summary>
/// Graded text-search result. Hits are ordered precise-first (contiguous phrase before
/// scattered), then token-covering partials. Counts reflect the full candidate set before
/// paging. FilesMatchedAcrossLines are files where every query token occurs but never on a
/// single line (the file-level co-occurrence signal).
/// </summary>
public sealed record TextSearchResult(
    List<TextHit> Hits, int TotalPrecise, int TotalPartial, List<string> FilesMatchedAcrossLines);

public sealed record ProjectRow(long Id, string Path, string Name, string Style, string Tfms, bool IsTest, string LoadStatus);

public sealed record GraphEdge(string FromProject, string ToProject);

public sealed record ReferenceGroup(string Project, bool IsTestProject, int Count, List<TextHit> Samples);

public sealed record OverviewStats(
    long CsFiles, long TotalLines, long Symbols, long Projects, long LegacyProjects,
    long SdkProjects, long TestProjects, long Solutions, long GeneratedFiles, long OrphanedFiles,
    string TfmBreakdown, string? IndexVersion, string? IndexedAtUtc);

/// <summary>
/// Owns: read-side queries over the persisted index (own pooled connection, safe to
/// instantiate per operation). Does not own: writes (IndexStore) or result budgeting/
/// shaping for MCP responses (M2 tool layer).
/// </summary>
public sealed partial class IndexQueries : IDisposable
{
    private readonly SqliteConnection _conn;

    public IndexQueries(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
        _conn.Open();
    }

    // ---------------------------------------------------------------- find_file

    public List<FileHit> FindFiles(string nameOrGlob, int limit,
        IReadOnlyList<string>? excludePaths = null, int offset = 0)
    {
        // An empty include is "nothing to find" → no match (matching the pre-filter behavior).
        // Without this, AppendPathFilter would add no include predicate and the query would
        // collapse to WHERE 1=1 (a full-table listing), not the empty result callers expect.
        if (string.IsNullOrEmpty(nameOrGlob)) return new();
        // Include (nameOrGlob) and exclude share the same glob semantics as search_symbol via
        // AppendPathFilter — a bare name matches at any depth (root included), a '/'-glob anchors.
        var args = new List<(string, object)> { ("$lim", limit), ("$off", offset) };
        var where = new System.Text.StringBuilder("WHERE 1=1");
        AppendPathFilter(where, args, nameOrGlob, excludePaths);

        return Query(
            $"""
            SELECT f.id, f.path, f.size, f.line_count, f.is_generated FROM files f
            {where}
            ORDER BY f.is_generated, length(f.path), f.path LIMIT $lim OFFSET $off
            """,
            r => new FileHit(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetBoolean(4)),
            args.ToArray());
    }

    // ---------------------------------------------------------------- search_text

    public sealed record TextFilter(
        string? PathGlob = null,
        string? Project = null,
        bool IncludeGenerated = false,
        bool? TestsOnly = null,          // null = both, true = tests only, false = production only
        string? Lang = null,
        IReadOnlyList<string>? ExcludePaths = null);

    /// <summary>Convenience overload returning just the graded hits (auto partials mode).</summary>
    public List<TextHit> SearchText(string query, int limit, TextFilter? filter = null,
        int maxCandidateFiles = 200, int offset = 0)
        => SearchTextGraded(query, limit, filter, maxCandidateFiles, offset, "auto").Hits;

    /// <summary>
    /// Full-text search that grades each returned line: <c>precise</c> = the line contains
    /// ALL query tokens (contiguous phrase ranked before scattered), <c>partial</c> = the
    /// line covers only some tokens (token-covering, at most one line per otherwise-unmatched
    /// token). There is no silent single-token substitution. partialsMode: "auto" (default —
    /// partials only fill space precise did not), "never", or "always".
    /// </summary>
    public TextSearchResult SearchTextGraded(string query, int limit, TextFilter? filter,
        int maxCandidateFiles, int offset, string partialsMode, int ctxBefore = 0, int ctxAfter = 0)
    {
        string fts = FtsQuery(query);
        if (fts.Length == 0) return new TextSearchResult(new(), 0, 0, new());
        filter ??= new TextFilter();

        var args = new List<(string, object)> { ("$q", fts), ("$lim", maxCandidateFiles) };
        var where = new System.Text.StringBuilder("WHERE 1=1");
        string join = "";

        if (!filter.IncludeGenerated) where.Append(" AND f.is_generated = 0");
        // Shared path predicate — same glob semantics as search_symbol/find_file (bare
        // names match at any depth, workspace-root files included).
        AppendPathFilter(where, args, filter.PathGlob, filter.ExcludePaths);
        if (filter.Lang is { } lang)
        {
            where.Append(" AND f.lang = $lang");
            args.Add(("$lang", lang));
        }
        if (filter.Project is { } proj)
        {
            join = "JOIN compile_items ci ON ci.file_id = f.id JOIN projects p ON p.id = ci.project_id";
            where.Append(" AND p.name = $proj COLLATE NOCASE");
            args.Add(("$proj", proj));
        }
        else if (filter.TestsOnly is { } testsOnly)
        {
            join = "LEFT JOIN compile_items ci ON ci.file_id = f.id LEFT JOIN projects p ON p.id = ci.project_id";
            where.Append(testsOnly
                ? " AND (p.is_test = 1 OR f.has_test_attrs = 1)"
                : " AND COALESCE(p.is_test, 0) = 0 AND f.has_test_attrs = 0");
        }

        // bm25() is an FTS5 auxiliary function valid only directly over the FTS query;
        // MATERIALIZED prevents the planner from flattening it into the outer context.
        args.Add(("$innerLim", Math.Clamp(maxCandidateFiles * 10, 2000, 20000)));
        var candidates = Query(
            $"""
            WITH m AS MATERIALIZED (
                SELECT rowid AS fid, bm25(fts_content) AS rank
                FROM fts_content WHERE fts_content MATCH $q
                ORDER BY rank LIMIT $innerLim
            )
            SELECT f.id, f.path, f.is_generated FROM m
            JOIN files f ON f.id = m.fid
            {join}
            {where}
            GROUP BY f.id
            ORDER BY f.is_generated, MIN(m.rank)
            LIMIT $lim
            """,
            r => (Id: r.GetInt64(0), Path: r.GetString(1), Gen: r.GetBoolean(2)),
            args.ToArray());

        // Distinct query tokens, preserving order (case-insensitive).
        var distinctTokens = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tokenize(query))
        {
            if (seenTokens.Add(t)) distinctTokens.Add(t);
        }
        string rawQuery = query.Trim();

        var precise = new List<TextHit>();
        var partial = new List<TextHit>();
        var filesAcross = new List<string>();
        foreach (var c in candidates)
        {
            string? content = ContentById(c.Id);
            if (content is null) continue;
            var (filePrecise, filePartial) = GradeFile(c.Path, content, c.Gen, distinctTokens, rawQuery, ctxBefore, ctxAfter);
            precise.AddRange(filePrecise);
            if (filePrecise.Count == 0 && filePartial.Count > 0)
            {
                partial.AddRange(filePartial);
                filesAcross.Add(c.Path);
            }
        }

        bool includePartials = partialsMode switch
        {
            "never" => false,
            "always" => true,
            _ => precise.Count < offset + limit, // auto: only when precise did not fill through this page
        };
        var ordered = includePartials ? precise.Concat(partial).ToList() : precise;
        var page = ordered.Skip(offset).Take(limit).ToList();
        return new TextSearchResult(page, precise.Count, partial.Count, filesAcross);
    }

    /// <summary>
    /// Grades one file's lines against the query tokens. Returns precise lines (all tokens,
    /// contiguous phrase first) OR — only when the file has no precise line — token-covering
    /// partial lines (one per otherwise-unmatched token). A file contributes to exactly one tier.
    /// </summary>
    private static (List<TextHit> Precise, List<TextHit> Partial) GradeFile(
        string path, string content, bool gen, IReadOnlyList<string> tokens, string rawQuery,
        int ctxBefore, int ctxAfter)
    {
        var lines = content.Split('\n');
        // A newline-terminated file yields a trailing "" element; drop it so a context window near EOF
        // does not emit a spurious blank line. Every real line's index is unchanged (only the final "").
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        bool multi = tokens.Count > 1;
        var phrase = new List<TextHit>();
        var scattered = new List<TextHit>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool all = true;
            foreach (var t in tokens)
            {
                // Whole-token match (identifier-bounded), matching the FTS tokenizer that
                // selected this file — so 'Order' does NOT satisfy the token inside 'OrderId'.
                if (!ContainsWholeToken(line, t)) { all = false; break; }
            }
            if (!all) continue;
            bool isPhrase = multi && line.IndexOf(rawQuery, StringComparison.OrdinalIgnoreCase) >= 0;
            var (before, after) = ContextSlice(lines, i, ctxBefore, ctxAfter);
            (isPhrase ? phrase : scattered).Add(new TextHit(path, i + 1, Snippet(line), gen, "precise", null, before, after));
        }

        if (phrase.Count + scattered.Count > 0)
        {
            phrase.AddRange(scattered); // contiguous-phrase precise ranked before scattered precise
            return (phrase, new List<TextHit>());
        }

        // No precise line. For multi-token queries, surface where each token lives (one line
        // per otherwise-unmatched token) so the caller sees the co-occurrence spread.
        var partial = new List<TextHit>();
        if (multi)
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length && covered.Count < tokens.Count; i++)
            {
                string line = lines[i];
                List<string>? here = null;
                foreach (var t in tokens)
                {
                    if (!covered.Contains(t) && ContainsWholeToken(line, t))
                    {
                        (here ??= new()).Add(t);
                    }
                }
                if (here is not null)
                {
                    foreach (var t in here) covered.Add(t);
                    var (before, after) = ContextSlice(lines, i, ctxBefore, ctxAfter);
                    partial.Add(new TextHit(path, i + 1, Snippet(line), gen, "partial", here, before, after));
                }
            }
        }
        return (new List<TextHit>(), partial);
    }

    /// <summary>True if <paramref name="token"/> occurs in <paramref name="line"/> as a whole
    /// identifier token (bounded by non-identifier characters), case-insensitive — matching the
    /// FTS tokenizer so grading agrees with candidacy.</summary>
    private static bool ContainsWholeToken(string line, string token)
    {
        int idx = 0;
        while ((idx = line.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftOk = idx == 0 || !IsIdentChar(line[idx - 1]);
            int end = idx + token.Length;
            bool rightOk = end >= line.Length || !IsIdentChar(line[end]);
            if (leftOk && rightOk) return true;
            idx = end;
        }
        return false;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ---------------------------------------------------------------- symbols

    public List<SymbolHit> SearchSymbols(string query, string mode, IReadOnlyList<string>? kinds, int limit,
        bool includeGenerated = false, int offset = 0,
        string? pathGlob = null, IReadOnlyList<string>? excludePaths = null, string? ns = null)
    {
        string esc = EscapeLike(query);
        string pattern = mode switch
        {
            "exact" => esc,
            "prefix" => esc + "%",
            _ => "%" + esc + "%",
        };

        var args = new List<(string, object)>
        {
            ("$pat", pattern), ("$q", query), ("$pre", esc + "%"), ("$lim", limit), ("$off", offset),
        };
        var where = new System.Text.StringBuilder("WHERE s.name LIKE $pat ESCAPE '\\'");
        string kindFilter = KindFilter(kinds);
        if (kindFilter.Length > 0) where.Append(' ').Append(kindFilter);
        if (!includeGenerated) where.Append(" AND f.is_generated = 0");
        AppendPathFilter(where, args, pathGlob, excludePaths);
        if (ns is { Length: > 0 } nsFilter)
        {
            // Namespace subtree: the exact namespace OR anything nested under it.
            where.Append(" AND (s.ns = $ns COLLATE NOCASE OR s.ns LIKE $nsp ESCAPE '\\')");
            args.Add(("$ns", nsFilter));
            args.Add(("$nsp", EscapeLike(nsFilter) + ".%"));
        }

        return Query(
            $"""
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers
            FROM symbols s JOIN files f ON f.id = s.file_id
            {where}
            ORDER BY
              CASE WHEN s.name = $q COLLATE NOCASE THEN 0
                   WHEN s.name LIKE $pre ESCAPE '\' THEN 1 ELSE 2 END,
              f.is_generated, length(s.name), s.name, f.path
            LIMIT $lim OFFSET $off
            """,
            ReadSymbol,
            args.ToArray());
    }

    public List<SymbolHit> Outline(string filePath)
    {
        return Query(
            """
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE f.path = $p
            ORDER BY s.start_line, s.end_line DESC
            """,
            ReadSymbol, ("$p", filePath));
    }

    public List<SymbolHit> SymbolAt(string filePath, int line)
    {
        // Smallest containing symbol plus its ancestor chain, innermost first.
        var chain = new List<SymbolHit>();
        var innermost = InnermostSymbolAt(filePath, line);
        if (innermost is null) return chain;
        chain.Add(innermost);
        long? parent = innermost.ParentId;
        int guard = 0;
        while (parent is { } pid && guard++ < 16)
        {
            var next = Query(
                """
                SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                       s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE s.id = $id
                """,
                ReadSymbol,
                ("$id", pid));
            if (next.Count == 0) break;
            chain.Add(next[0]);
            parent = next[0].ParentId;
        }
        return chain;
    }

    /// <summary>Fetches a single symbol row by its indexed id — the backing of the <c>idx:NNN</c>
    /// symbolId handle. Null when no such row exists (ids are index-local and change on reindex).</summary>
    public SymbolHit? SymbolById(long id)
    {
        var hits = Query(
            """
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.id = $id
            """,
            ReadSymbol, ("$id", id));
        return hits.Count > 0 ? hits[0] : null;
    }

    /// <summary>Innermost symbol containing the line — the single-query flavor of
    /// <see cref="SymbolAt"/> (no ancestor walk), cheap enough to decorate search hits.</summary>
    public SymbolHit? InnermostSymbolAt(string filePath, int line)
    {
        var hits = Query(
            """
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE f.path = $p AND s.start_line <= $l AND s.end_line >= $l
            ORDER BY (s.end_line - s.start_line), s.start_line DESC
            LIMIT 1
            """,
            ReadSymbol,
            ("$p", filePath), ("$l", line));
        return hits.Count > 0 ? hits[0] : null;
    }

    /// <summary>Innermost enclosing symbol per (path, line), batched: search_text decorated up to a
    /// full page (~100 hits) with one InnermostSymbolAt point query EACH — this replaces the N+1 with
    /// chunked grouped queries and an in-memory innermost pick (smallest span, then latest start,
    /// mirroring InnermostSymbolAt's ORDER BY). Keys absent from the result had no enclosing symbol.</summary>
    public Dictionary<(string Path, int Line), SymbolHit> InnermostSymbolsAt(
        IReadOnlyCollection<(string Path, int Line)> keys)
    {
        var result = new Dictionary<(string, int), SymbolHit>();
        if (keys.Count == 0) return result;
        var best = new Dictionary<(string, int), (int Span, int Start)>();
        foreach (var chunk in keys.Distinct().Chunk(40))
        {
            var args = new List<(string, object)>();
            var clauses = new List<string>();
            for (int i = 0; i < chunk.Length; i++)
            {
                clauses.Add($"(f.path = $p{i} AND s.start_line <= $l{i} AND s.end_line >= $l{i})");
                args.Add(($"$p{i}", chunk[i].Path));
                args.Add(($"$l{i}", chunk[i].Line));
            }
            var rows = Query(
                $"""
                SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                       s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE {string.Join(" OR ", clauses)}
                """,
                ReadSymbol, args.ToArray());
            foreach (var sym in rows)
            {
                // One row can enclose several requested lines of the same file. Ordinal, not
                // IgnoreCase: the SQL matched f.path = $p with BINARY comparison, so a case-twin path
                // (possible on a case-sensitive FS) must not cross-attribute here (review finding).
                foreach (var (path, lineNo) in chunk)
                {
                    if (!string.Equals(path, sym.FilePath, StringComparison.Ordinal)) continue;
                    if (sym.StartLine > lineNo || sym.EndLine < lineNo) continue;
                    int span = sym.EndLine - sym.StartLine;
                    var key = (path, lineNo);
                    if (!best.TryGetValue(key, out var b) || span < b.Span || (span == b.Span && sym.StartLine > b.Start))
                    {
                        best[key] = (span, sym.StartLine);
                        result[key] = sym;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Other files containing a PARTIAL declaration of the same type identity
    /// (name + arity + kind + namespace + containing type) — the partial-type cross-links for an
    /// outline. is_partial=1 keeps an unrelated same-name non-partial type (legal in another
    /// project) out; the container match keeps same-name nested types apart; the arity match keeps
    /// generic-arity SIBLINGS apart — <c>partial class Foo</c> and <c>partial class Foo&lt;T&gt;</c>
    /// are different types whose partial halves live in different file sets (szs). Best-effort:
    /// identity does not include project. Returns up to 11 so the caller can detect (and mark)
    /// the &gt;10 case rather than silently capping.</summary>
    public List<string> PartialDeclarationFiles(string name, string? ns, string kind, string? container, string excludePath, int arity = 0)
    {
        return Query(
            """
            SELECT DISTINCT f.path
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.name = $n AND s.kind = $k AND s.is_partial = 1
              AND s.arity = $a
              AND COALESCE(s.ns, '') = COALESCE($ns, '')
              AND COALESCE(s.container, '') = COALESCE($c, '')
              AND f.path <> $p
            ORDER BY f.path
            LIMIT 11
            """,
            r => r.GetString(0),
            ("$n", name), ("$k", kind), ("$ns", (object?)ns ?? DBNull.Value),
            ("$c", (object?)container ?? DBNull.Value), ("$p", excludePath), ("$a", arity));
    }

    // ---------------------------------------------------------------- reference candidates

    /// <summary>Result of the indexed reference scan. TotalHits/ProdHits/TestHits are PHYSICAL
    /// line counts (each file counted once — a file linked into several projects is not repeated;
    /// it lands in ProdHits when ANY surviving owner is a production project, else TestHits), so
    /// ProdHits + TestHits == TotalHits always. Group counts are per-project ATTRIBUTIONS and can
    /// sum higher than TotalHits for linked files (0ok: the summary previously printed those
    /// attribution sums next to the physical total — "4 lines (8 production)").</summary>
    public sealed record ReferenceCandidateResult(int TotalHits, int ProdHits, int TestHits, List<ReferenceGroup> Groups);

    public ReferenceCandidateResult ReferenceCandidates(
        string symbolName, int maxCandidateFiles = 500, int samplesPerProject = 3,
        string? pathGlob = null, IReadOnlyList<string>? excludePaths = null, bool includeGenerated = true,
        bool includeTests = true)
    {
        var args = new List<(string, object)>
        {
            ("$q", $"\"{symbolName.Replace("\"", "")}\""), ("$lim", maxCandidateFiles),
        };
        // Same include/exclude glob semantics as search_symbol; lets references drop vendored
        // third-party candidate files precisely (counts reflect the filtered set).
        var where = new System.Text.StringBuilder("WHERE fts_content MATCH $q");
        AppendPathFilter(where, args, pathGlob, excludePaths);
        // Drop generated files from candidacy so COUNTS (not just samples) honor includeGenerated (bug wi3).
        if (!includeGenerated) where.Append(" AND f.is_generated = 0");
        var candidates = Query(
            $"""
            SELECT f.id, f.path, f.is_generated FROM fts_content
            JOIN files f ON f.id = fts_content.rowid
            {where}
            ORDER BY f.is_generated, bm25(fts_content)
            LIMIT $lim
            """,
            r => (Id: r.GetInt64(0), Path: r.GetString(1), Gen: r.GetBoolean(2)),
            args.ToArray());

        // Resolve project ownership for all candidate files in one query per batch.
        var fileProjects = FileProjects(candidates.Select(c => c.Id).ToList());

        int total = 0, prodTotal = 0, testTotal = 0;
        var groups = new Dictionary<string, (bool IsTest, int Count, List<TextHit> Samples)>();
        foreach (var c in candidates)
        {
            // includeTests filters OWNERS before counting (wu1): a file owned only by test
            // projects contributes nothing (its lines leave `total` too); a file shared between
            // production and test projects keeps its production attribution and is counted ONCE
            // in `total` — summing the per-project group counts instead would double-count files
            // legacy projects link into several compile sets (review-reproduced).
            var owners = fileProjects.TryGetValue(c.Id, out var list) ? list : new List<(string, bool)> { ("(no project)", false) };
            if (!includeTests) owners = owners.Where(o => !o.Item2).ToList();
            if (owners.Count == 0) continue;

            string? content = ContentById(c.Id);
            if (content is null) continue;
            var spans = LocateTokenLineSpans(content, symbolName);
            if (spans.Count == 0) continue;

            foreach (var (project, isTest) in owners)
            {
                if (!groups.TryGetValue(project, out var g)) g = (isTest, 0, new List<TextHit>());
                g.Count += spans.Count;
                foreach (var (ln, s, e) in spans.Take(Math.Max(0, samplesPerProject - g.Samples.Count)))
                {
                    g.Samples.Add(new TextHit(c.Path, ln, Snippet(content[s..e]), c.Gen));
                }
                groups[project] = g;
            }
            total += spans.Count;
            // Physical prod/test split, counted once per file like `total` (0ok): production when
            // ANY surviving owner is production ((no project) counts as production, matching the
            // group display), else test-only. Keeps prod + test == total in the summary.
            if (owners.Any(o => !o.Item2)) prodTotal += spans.Count;
            else testTotal += spans.Count;
        }

        var ordered = groups
            .Select(kv => new ReferenceGroup(kv.Key, kv.Value.IsTest, kv.Value.Count, kv.Value.Samples))
            .OrderByDescending(g => g.Count)
            .ToList();
        return new ReferenceCandidateResult(total, prodTotal, testTotal, ordered);
    }

    // ---------------------------------------------------------------- projects

    public ProjectRow? ProjectByName(string name)
    {
        var rows = Query(
            "SELECT id, path, name, style, tfms, is_test, load_status FROM projects WHERE name = $n COLLATE NOCASE LIMIT 1",
            ReadProject, ("$n", name));
        return rows.Count > 0 ? rows[0] : null;
    }

    public List<ProjectRow> ProjectsContaining(string filePath)
    {
        return Query(
            """
            SELECT p.id, p.path, p.name, p.style, p.tfms, p.is_test, p.load_status
            FROM compile_items ci
            JOIN files f ON f.id = ci.file_id
            JOIN projects p ON p.id = ci.project_id
            WHERE f.path = $p
            ORDER BY p.name
            """,
            ReadProject, ("$p", filePath));
    }

    public List<GraphEdge> ProjectGraph(string projectName, int depth, string direction)
    {
        // Depth-1 fast path: the semantic cluster load calls this once per project (TopoOrder +
        // per-project references), and the general path below loads the ENTIRE edge table each call —
        // O(edges) per project, O(projects x edges) per cluster. One indexed WHERE instead. Result-set
        // parity with the BFS below (review-hardened): an unknown direction returns EMPTY (the BFS's
        // guards match nothing), and 'both' emits downstream edges then upstream edges via UNION ALL —
        // the BFS's deterministic order, which callers truncate on (.Take / list budget).
        if (depth == 1)
        {
            const string select = """
                SELECT pf.name, pt.name FROM project_refs r
                JOIN projects pf ON pf.id = r.from_id
                JOIN projects pt ON pt.id = r.to_id
                """;
            string? sql = direction switch
            {
                "downstream" => $"{select} WHERE pf.name = $n COLLATE NOCASE",
                "upstream" => $"{select} WHERE pt.name = $n COLLATE NOCASE",
                "both" => $"{select} WHERE pf.name = $n COLLATE NOCASE UNION ALL {select} WHERE pt.name = $n COLLATE NOCASE",
                _ => null,
            };
            if (sql is null) return new List<GraphEdge>();
            return Query(sql, r => new GraphEdge(r.GetString(0), r.GetString(1)), ("$n", projectName));
        }

        var edges = Query(
            """
            SELECT pf.name, pt.name FROM project_refs r
            JOIN projects pf ON pf.id = r.from_id
            JOIN projects pt ON pt.id = r.to_id
            """,
            r => new GraphEdge(r.GetString(0), r.GetString(1)));

        var downstream = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); // project -> deps
        var upstream = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // project -> dependents
        foreach (var e in edges)
        {
            (downstream.TryGetValue(e.FromProject, out var d) ? d : downstream[e.FromProject] = new()).Add(e.ToProject);
            (upstream.TryGetValue(e.ToProject, out var u) ? u : upstream[e.ToProject] = new()).Add(e.FromProject);
        }

        var result = new List<GraphEdge>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectName };
        var frontier = new Queue<(string Node, int Depth)>();
        frontier.Enqueue((projectName, 0));

        while (frontier.Count > 0)
        {
            var (node, d) = frontier.Dequeue();
            if (d >= depth) continue;
            if (direction is "downstream" or "both" && downstream.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    result.Add(new GraphEdge(node, dep));
                    if (visited.Add(dep)) frontier.Enqueue((dep, d + 1));
                }
            }
            if (direction is "upstream" or "both" && upstream.TryGetValue(node, out var dependents))
            {
                foreach (var dependent in dependents)
                {
                    result.Add(new GraphEdge(dependent, node));
                    if (visited.Add(dependent)) frontier.Enqueue((dependent, d + 1));
                }
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- semantic-layer support

    /// <summary>Members named <paramref name="memberName"/> declared in one of the given types,
    /// identified by (namespace, container) PAIRS — so the <c>LIMIT</c> bounds only genuine matches
    /// (not every same-container-named type across all namespaces) and a hot member name can't get
    /// lost behind the cap. Empty on empty inputs.</summary>
    public List<SymbolHit> MembersNamedInTypes(string memberName, IReadOnlyCollection<(string Ns, string Container)> types, int limit)
    {
        if (string.IsNullOrEmpty(memberName) || types.Count == 0) return new();
        var args = new List<(string, object)> { ("$m", memberName), ("$lim", limit) };
        var clauses = new List<string>();
        int i = 0;
        foreach (var (ns, container) in types.Distinct())
        {
            if (string.IsNullOrEmpty(container)) continue;
            string n = $"$n{i}", c = $"$c{i}";
            i++;
            clauses.Add($"(COALESCE(s.ns, '') = {n} AND s.container = {c})");
            args.Add((n, ns ?? ""));
            args.Add((c, container));
        }
        if (clauses.Count == 0) return new();
        return Query(
            $"""
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.name = $m COLLATE NOCASE AND ({string.Join(" OR ", clauses)})
            ORDER BY f.is_generated, f.path
            LIMIT $lim
            """,
            ReadSymbol, args.ToArray());
    }

    /// <summary>Types whose base list textually mentions the given name (heuristic implementations).</summary>
    public List<SymbolHit> ImplementationCandidates(string name, int limit)
    {
        // An empty name would make the LIKE pattern collapse to '%: %' and match every type that has
        // any base list — never run that catch-all.
        if (string.IsNullOrEmpty(name)) return new();
        string esc = EscapeLike(name);
        // The LIKE is a SUBSTRING filter, so over-fetch and refine to types whose base list names the
        // interface as a WHOLE identifier token — otherwise 'IFoo' would also match 'IFooBar'.
        var candidates = Query(
            $"""
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.kind IN ('class','struct','record','record_struct')
              AND s.name <> $n
              AND s.signature LIKE $pat ESCAPE '\'
            ORDER BY f.is_generated, s.name
            LIMIT $lim
            """,
            ReadSymbol,
            ("$n", name), ("$pat", $"%: %{esc}%"), ("$lim", Math.Min(limit * 4, 2000)));
        return candidates.Where(h => ContainsWholeToken(h.Signature, name)).Take(limit).ToList();
    }

    /// <summary>Project name → is-test flag for the whole workspace (small).</summary>
    public Dictionary<string, bool> AllProjectTestFlags()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, isTest) in Query(
                     "SELECT name, is_test FROM projects",
                     r => (Name: r.GetString(0), IsTest: r.GetBoolean(1))))
        {
            map[name] = isTest;
        }
        return map;
    }

    /// <summary>File paths compiled by a project, ordered.</summary>
    public List<string> ProjectFiles(string projectName)
    {
        return Query(
            """
            SELECT f.path FROM compile_items ci
            JOIN projects p ON p.id = ci.project_id
            JOIN files f ON f.id = ci.file_id
            WHERE p.name = $n COLLATE NOCASE AND f.lang = 'cs'
            ORDER BY f.path
            """,
            r => r.GetString(0), ("$n", projectName));
    }

    /// <summary>Fingerprints for MANY projects in one grouped query. The warm-cache check in
    /// EnsureLoadedAsync ran ProjectFingerprint once per already-loaded project on EVERY semantic
    /// call — dozens to hundreds of point queries per references/implementations invocation (dz3).</summary>
    public Dictionary<string, (long FileCount, long HashSum)> ProjectFingerprints(IReadOnlyCollection<string> projectNames)
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        if (projectNames.Count == 0) return result;
        foreach (var chunk in projectNames.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(200))
        {
            var args = new List<(string, object)>();
            var ph = new List<string>();
            for (int i = 0; i < chunk.Length; i++)
            {
                ph.Add($"$n{i}");
                args.Add(($"$n{i}", chunk[i]));
            }
            foreach (var row in Query(
                $"""
                SELECT p.name, COUNT(*), TOTAL(f.hash) FROM compile_items ci
                JOIN projects p ON p.id = ci.project_id
                JOIN files f ON f.id = ci.file_id
                WHERE p.name COLLATE NOCASE IN ({string.Join(",", ph)}) AND f.lang = 'cs'
                GROUP BY p.name COLLATE NOCASE -- must union case-variant names exactly like the single
                                               -- query's '= $n COLLATE NOCASE', or the warm check and
                                               -- the load-time fingerprint permanently disagree and the
                                               -- project reloads on EVERY semantic call (review repro)
                """,
                r => (Name: r.GetString(0), Count: r.GetInt64(1), Sum: (long)r.GetDouble(2)), args.ToArray()))
            {
                result[row.Name] = (row.Count, row.Sum);
            }
        }
        return result; // names with no compiled files are simply absent => caller defaults to (0, 0)
    }

    /// <summary>Cheap change fingerprint for a project's compiled files.</summary>
    public (long FileCount, long HashSum) ProjectFingerprint(string projectName)
    {
        var rows = Query(
            """
            SELECT COUNT(*), TOTAL(f.hash) FROM compile_items ci
            JOIN projects p ON p.id = ci.project_id
            JOIN files f ON f.id = ci.file_id
            WHERE p.name = $n COLLATE NOCASE AND f.lang = 'cs'
            """,
            r => (Count: r.GetInt64(0), Sum: (long)r.GetDouble(1)), ("$n", projectName));
        return rows.Count > 0 ? (rows[0].Count, rows[0].Sum) : (0, 0);
    }

    /// <summary>
    /// Projects whose files textually contain the identifier (FTS whole-token candidates),
    /// ordered by match volume. This bounds which projects can possibly reference a symbol.
    /// </summary>
    public List<(string Project, int FileCount)> CandidateProjectsForName(string name, int maxFiles = 2000)
    {
        return Query(
            """
            WITH m AS MATERIALIZED (
                SELECT rowid AS fid FROM fts_content
                WHERE fts_content MATCH $q LIMIT $lim
            )
            SELECT p.name, COUNT(DISTINCT m.fid) FROM m
            JOIN compile_items ci ON ci.file_id = m.fid
            JOIN projects p ON p.id = ci.project_id
            GROUP BY p.name
            ORDER BY COUNT(DISTINCT m.fid) DESC, p.name
            """,
            r => (Project: r.GetString(0), FileCount: r.GetInt32(1)),
            ("$q", $"\"{name.Replace("\"", "")}\""), ("$lim", maxFiles));
    }

    /// <summary>All transitive dependency project names (downstream closure), targets included.</summary>
    public HashSet<string> DependencyClosure(IEnumerable<string> projectNames)
    {
        var edges = ProjectGraphEdges();
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(projectNames);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!closure.Add(node)) continue;
            if (edges.Downstream.TryGetValue(node, out var deps))
            {
                foreach (var d in deps) stack.Push(d);
            }
        }
        return closure;
    }

    /// <summary>All transitive dependent project names (upstream closure), target excluded.</summary>
    public HashSet<string> DependentClosure(string projectName)
    {
        var edges = ProjectGraphEdges();
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(projectName);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (edges.Upstream.TryGetValue(node, out var dependents))
            {
                foreach (var d in dependents)
                {
                    if (closure.Add(d)) stack.Push(d);
                }
            }
        }
        return closure;
    }

    /// <summary>Shortest dependency paths (project references) from one project to another.</summary>
    public List<List<string>> DependencyPaths(string fromProject, string toProject, int maxPaths = 3)
    {
        var (downstream, _) = ProjectGraphEdges();
        var results = new List<List<string>>();
        var queue = new Queue<List<string>>();
        queue.Enqueue(new List<string> { fromProject });
        var bestDepth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [fromProject] = 0 };
        int? shortest = null;

        while (queue.Count > 0 && results.Count < maxPaths)
        {
            var pathSoFar = queue.Dequeue();
            if (shortest is { } s && pathSoFar.Count > s) break; // only shortest-length paths
            string node = pathSoFar[^1];
            if (node.Equals(toProject, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(pathSoFar);
                shortest ??= pathSoFar.Count;
                continue;
            }
            if (!downstream.TryGetValue(node, out var deps)) continue;
            foreach (var dep in deps)
            {
                int depth = pathSoFar.Count;
                if (bestDepth.TryGetValue(dep, out int seen) && seen < depth) continue;
                bestDepth[dep] = depth;
                var next = new List<string>(pathSoFar) { dep };
                queue.Enqueue(next);
            }
        }
        return results;
    }

    public sealed record RelatedTestGroup(string TestProject, string Reason, int MatchingFiles, List<TextHit> Samples);

    /// <summary>
    /// Likely tests for a symbol: test files naming it (FTS), test classes following the
    /// {Name}Tests convention, and test projects referencing the owning project.
    /// </summary>
    public List<RelatedTestGroup> RelatedTests(string symbolName, string? owningProject, int maxGroups = 10)
    {
        var groups = new Dictionary<string, RelatedTestGroup>(StringComparer.OrdinalIgnoreCase);

        // 1. Test files that mention the symbol (strongest signal).
        var candidates = Query(
            """
            WITH m AS MATERIALIZED (
                SELECT rowid AS fid FROM fts_content WHERE fts_content MATCH $q LIMIT 2000
            )
            SELECT p.name, f.path, f.is_generated, COUNT(*) OVER (PARTITION BY p.name) FROM m
            JOIN files f ON f.id = m.fid
            JOIN compile_items ci ON ci.file_id = m.fid
            JOIN projects p ON p.id = ci.project_id
            WHERE p.is_test = 1
            ORDER BY p.name, f.path
            """,
            r => (Project: r.GetString(0), Path: r.GetString(1), Gen: r.GetBoolean(2), Count: r.GetInt32(3)),
            ("$q", $"\"{symbolName.Replace("\"", "")}\""));
        foreach (var c in candidates)
        {
            if (!groups.TryGetValue(c.Project, out var g))
            {
                groups[c.Project] = g = new RelatedTestGroup(c.Project, "references symbol name", c.Count, new List<TextHit>());
            }
            if (g.Samples.Count < 3) g.Samples.Add(new TextHit(c.Path, 1, "", c.Gen));
        }

        // 2. {Name}Tests naming convention.
        foreach (var hit in SearchSymbols(symbolName + "Tests", "exact", new[] { "class" }, 10, includeGenerated: false))
        {
            foreach (var owner in ProjectsContaining(hit.FilePath).Where(p => p.IsTest))
            {
                if (!groups.ContainsKey(owner.Name))
                {
                    groups[owner.Name] = new RelatedTestGroup(owner.Name, "naming convention", 1,
                        new List<TextHit> { new(hit.FilePath, hit.StartLine, hit.Signature, false) });
                }
            }
        }

        // 3. Test projects that reference the owning project (weakest signal).
        if (owningProject is not null)
        {
            var testFlags = AllProjectTestFlags();
            foreach (var dependent in DependentClosure(owningProject))
            {
                if (groups.Count >= maxGroups) break;
                if (testFlags.TryGetValue(dependent, out bool isTest) && isTest && !groups.ContainsKey(dependent))
                {
                    groups[dependent] = new RelatedTestGroup(dependent, "references owning project", 0, new List<TextHit>());
                }
            }
        }

        return groups.Values
            .OrderBy(g => g.Reason switch { "references symbol name" => 0, "naming convention" => 1, _ => 2 })
            .ThenByDescending(g => g.MatchingFiles)
            .Take(maxGroups)
            .ToList();
    }

    private (Dictionary<string, List<string>> Downstream, Dictionary<string, List<string>> Upstream) ProjectGraphEdges()
    {
        var edges = Query(
            """
            SELECT pf.name, pt.name FROM project_refs r
            JOIN projects pf ON pf.id = r.from_id
            JOIN projects pt ON pt.id = r.to_id
            """,
            r => (From: r.GetString(0), To: r.GetString(1)));
        var down = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var up = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
        {
            (down.TryGetValue(e.From, out var d) ? d : down[e.From] = new()).Add(e.To);
            (up.TryGetValue(e.To, out var u) ? u : up[e.To] = new()).Add(e.From);
        }
        return (down, up);
    }

    // ---------------------------------------------------------------- misc

    /// <summary>Of the given workspace-relative paths, the subset in NO project's compile set — the
    /// "is this file really compiled?" signal (the compile graph grep lacks). Since 3tz the graph
    /// expands &lt;Compile Include&gt; wildcard globs (legacy wildcard projects are owned), honors
    /// &lt;Compile Remove&gt; (an excluded-from-compilation file is correctly orphaned — the dead-twin
    /// case) and EnableDefaultCompileItems. Remaining best-effort gaps: shared projects
    /// (.shproj/.projitems), Directory.Build.props/.targets compile globs, and Condition attributes
    /// (ignored — over-inclusive). A project that fails to PARSE glob-attributes its whole subtree,
    /// so dead code under it will NOT appear. Still a syntactic signal, not a compiler fact — never
    /// hide results on it; absence is not absolute proof a file compiles.</summary>
    public HashSet<string> OrphanedPaths(IReadOnlyCollection<string> paths)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (paths.Count == 0) return result;
        foreach (var chunk in paths.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            var args = new List<(string, object)>();
            var placeholders = new List<string>();
            for (int i = 0; i < chunk.Length; i++)
            {
                string p = $"$p{i}";
                placeholders.Add(p);
                args.Add((p, chunk[i]));
            }
            foreach (var path in Query(
                $"""
                SELECT f.path FROM files f
                WHERE f.path IN ({string.Join(",", placeholders)})
                  AND NOT EXISTS (SELECT 1 FROM compile_items ci WHERE ci.file_id = f.id)
                """,
                r => r.GetString(0), args.ToArray()))
            {
                result.Add(path);
            }
        }
        return result;
    }

    /// <summary>Workspace-relative paths of files flagged generated (is_generated=1), for callers that
    /// must exclude generated code from results (case-insensitive, matching the index's path handling).</summary>
    public HashSet<string> GeneratedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Query("SELECT path FROM files WHERE is_generated = 1", r => r.GetString(0)))
            set.Add(p);
        return set;
    }

    public string? ContentByPath(string filePath)
    {
        var rows = Query(
            "SELECT c.content FROM file_contents c JOIN files f ON f.id = c.file_id WHERE f.path = $p",
            r => r.GetString(0), ("$p", filePath));
        return rows.Count > 0 ? rows[0] : null;
    }

    public OverviewStats Overview()
    {
        long Scalar(string sql)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }

        string tfms = string.Join(", ", Query(
            "SELECT tfms || ' x' || COUNT(*) FROM projects GROUP BY tfms ORDER BY COUNT(*) DESC LIMIT 6",
            r => r.GetString(0)));

        string? version = MetaValue("index_version");
        string? at = MetaValue("indexed_at_utc");

        return new OverviewStats(
            CsFiles: Scalar("SELECT COUNT(*) FROM files WHERE lang='cs'"),
            TotalLines: Scalar("SELECT COALESCE(SUM(line_count),0) FROM files WHERE lang='cs'"),
            Symbols: Scalar("SELECT COUNT(*) FROM symbols"),
            Projects: Scalar("SELECT COUNT(*) FROM projects"),
            LegacyProjects: Scalar("SELECT COUNT(*) FROM projects WHERE style='legacy'"),
            SdkProjects: Scalar("SELECT COUNT(*) FROM projects WHERE style='sdk'"),
            TestProjects: Scalar("SELECT COUNT(*) FROM projects WHERE is_test=1"),
            Solutions: Scalar("SELECT COUNT(*) FROM solutions"),
            GeneratedFiles: Scalar("SELECT COUNT(*) FROM files WHERE is_generated=1"),
            // Indexed .cs files in no project's compile set — the "really compiled?" count (see
            // OrphanedPaths for the exact semantics and remaining shared/props/Condition gaps).
            OrphanedFiles: Scalar("SELECT COUNT(*) FROM files WHERE lang='cs' AND NOT EXISTS (SELECT 1 FROM compile_items ci WHERE ci.file_id = files.id)"),
            TfmBreakdown: tfms,
            IndexVersion: version,
            IndexedAtUtc: at);
    }

    private string? MetaValue(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    // ---------------------------------------------------------------- internals

    private Dictionary<long, List<(string Project, bool IsTest)>> FileProjects(List<long> fileIds)
    {
        var map = new Dictionary<long, List<(string, bool)>>();
        if (fileIds.Count == 0) return map;
        // Chunk to keep parameter counts sane.
        foreach (var chunk in fileIds.Chunk(400))
        {
            string inList = string.Join(",", chunk);
            var rows = Query(
                $"""
                SELECT ci.file_id, p.name, p.is_test FROM compile_items ci
                JOIN projects p ON p.id = ci.project_id
                WHERE ci.file_id IN ({inList})
                """,
                r => (FileId: r.GetInt64(0), Name: r.GetString(1), IsTest: r.GetBoolean(2)));
            foreach (var row in rows)
            {
                (map.TryGetValue(row.FileId, out var list) ? list : map[row.FileId] = new()).Add((row.Name, row.IsTest));
            }
        }
        return map;
    }

    private string? ContentById(long fileId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM file_contents WHERE file_id = $id";
        cmd.Parameters.AddWithValue("$id", fileId);
        return cmd.ExecuteScalar() as string;
    }

    private static SymbolHit ReadSymbol(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetInt32(8),
        r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.GetString(11), r.GetBoolean(12),
        r.FieldCount > 13 && !r.IsDBNull(13) ? r.GetInt64(13) : null,
        Arity: r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetInt32(14) : 0,
        Modifiers: r.FieldCount > 15 && !r.IsDBNull(15) ? r.GetString(15) : null);

    private static ProjectRow ReadProject(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetBoolean(5), r.GetString(6));

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var list = new List<T>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    private static string KindFilter(IReadOnlyList<string>? kinds)
    {
        if (kinds is null || kinds.Count == 0) return "";
        // Whitelisted kind tokens only — safe to inline.
        var safe = kinds.Where(k => k.All(c => char.IsLetter(c) || c == '_')).Select(k => $"'{k}'");
        return $"AND s.kind IN ({string.Join(",", safe)})";
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string GlobToLike(string glob)
    {
        var sb = new System.Text.StringBuilder(glob.Length + 8);
        foreach (char c in glob)
        {
            switch (c)
            {
                case '*': sb.Append('%'); break;
                case '?': sb.Append('_'); break;
                case '%': sb.Append("\\%"); break;
                case '_': sb.Append("\\_"); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }
        }
        // No %%->% collapse: '%%' matches identically to '%' in LIKE (so '**' stays correct),
        // and collapsing is escaping-blind — it would merge a wildcard onto an escaped literal '\%'.
        return sb.ToString();
    }

    /// <summary>Appends include (<paramref name="pathGlob"/>) and exclude (<paramref name="excludePaths"/>)
    /// path predicates onto a WHERE builder over <c>f.path</c>. A glob containing '/' is matched against
    /// the workspace-relative path as-is; a bare glob (no '/') is matched starting at any path-segment
    /// boundary, root included — note '*'/'?' cross '/' in LIKE, so a wildcarded bare glob can span
    /// directory segments, not just the file name. The bare form is OR-ed in (and De Morgan'd for
    /// exclude) the way FindFiles does. Each exclude glob is a whole AND-ed <c>NOT LIKE</c> (a path is
    /// dropped if it matches ANY exclude). Binds $incPath/$incBare for the include and $ex{n}p/$ex{n}b
    /// per exclude, so a single query must not rebind those.</summary>
    private static void AppendPathFilter(System.Text.StringBuilder where, List<(string, object)> args,
        string? pathGlob, IReadOnlyList<string>? excludePaths)
    {
        if (pathGlob is { Length: > 0 } inc)
        {
            // A leading "**/" means "at any depth, INCLUDING zero" — so root-level files must match.
            // GlobToLike alone turns it into "%%/..." which requires a leading segment, silently
            // excluding root files (bug 6yk); match the remainder both at root and at any depth, the
            // same OR form the bare-glob (no-'/') case uses.
            if (inc.StartsWith("**/", StringComparison.Ordinal))
            {
                string rest = GlobToLike(inc[3..]);
                where.Append(" AND (f.path LIKE $incPath ESCAPE '\\' OR f.path LIKE $incBare ESCAPE '\\')");
                args.Add(("$incPath", $"%/{rest}"));
                args.Add(("$incBare", rest));
            }
            else if (inc.Contains('/'))
            {
                where.Append(" AND f.path LIKE $incPath ESCAPE '\\'");
                args.Add(("$incPath", GlobToLike(inc)));
            }
            else
            {
                string like = GlobToLike(inc);
                where.Append(" AND (f.path LIKE $incPath ESCAPE '\\' OR f.path LIKE $incBare ESCAPE '\\')");
                args.Add(("$incPath", $"%/{like}"));
                args.Add(("$incBare", like));
            }
        }
        if (excludePaths is null) return;
        int n = 0;
        foreach (var raw in excludePaths)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            string pPath = $"$ex{n}p", pBare = $"$ex{n}b"; // distinct binds per exclude
            n++;
            // Symmetric to the include side (bug 6yk mirror): a leading "**/" exclude must also drop
            // root-level matches, or e.g. "**/gen/**" fails to exclude a root-level "gen/...".
            if (raw.StartsWith("**/", StringComparison.Ordinal))
            {
                string rest = GlobToLike(raw[3..]);
                where.Append($" AND f.path NOT LIKE {pPath} ESCAPE '\\' AND f.path NOT LIKE {pBare} ESCAPE '\\'");
                args.Add((pPath, $"%/{rest}"));
                args.Add((pBare, rest));
            }
            else if (raw.Contains('/'))
            {
                where.Append($" AND f.path NOT LIKE {pPath} ESCAPE '\\'");
                args.Add((pPath, GlobToLike(raw)));
            }
            else
            {
                string like = GlobToLike(raw);
                where.Append($" AND f.path NOT LIKE {pPath} ESCAPE '\\' AND f.path NOT LIKE {pBare} ESCAPE '\\'");
                args.Add((pPath, $"%/{like}"));
                args.Add((pBare, like));
            }
        }
    }

    // ---------------------------------------------------------------- vendor / first-party

    // Directory names that mark checked-in third-party or generated source that IS in the index.
    // Matched as a whole path segment (case-insensitive), at any depth. Deliberately conservative —
    // path-based, not a namespace heuristic — and surfaced (not silently applied) so callers can see
    // them. Intentionally disjoint from WorkspaceScanner.DefaultExcludedDirs (bin/obj/packages/
    // node_modules/...): those are never indexed at all, so listing them here would be dead weight —
    // this set is exactly the vendored/generated dirs that survive the scan and become noise.
    private static readonly string[] VendorSegments =
    {
        "3rdparty", "thirdparty", "third-party", "third_party",
        "vendor", "vendored", "external", "externals", "generated",
    };

    /// <summary>Exclude globs for the checked-in vendor/generated directories actually present in
    /// the index (e.g. "3rdparty/**", "src/vendor/**") — the repo_overview discovery hint. The
    /// vendor-segment test is pushed into SQL so ALL paths are considered (not a sampled window),
    /// yet only vendor paths come back to walk. Ordinal-sorted; the 50-cap bounds only pathological
    /// output. Empty when none are present. (firstPartyOnly does NOT use this — it excludes vendor
    /// segments directly via <see cref="VendorExcludeGlobs"/>, so it never depends on this scan.)</summary>
    public List<string> SuggestedExcludes()
    {
        // Whole-segment match per marker: at the path start ("marker/…") or after a slash
        // ("…/marker/…"). EscapeLike neutralizes the '_' in markers like third_party (LIKE wildcard).
        var clauses = new List<string>();
        var args = new List<(string, object)>();
        for (int i = 0; i < VendorSegments.Length; i++)
        {
            string esc = EscapeLike(VendorSegments[i]);
            string a = $"$va{i}", b = $"$vb{i}";
            clauses.Add($"f.path LIKE {a} ESCAPE '\\' OR f.path LIKE {b} ESCAPE '\\'");
            args.Add((a, esc + "/%"));    // marker at path start
            args.Add((b, $"%/{esc}/%"));  // marker after a slash
        }
        var dirs = Query(
            $"SELECT f.path FROM files f WHERE {string.Join(" OR ", clauses)} ORDER BY f.path",
            r => r.GetString(0), args.ToArray());

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in dirs)
        {
            var segs = path.Split('/');
            for (int i = 0; i < segs.Length - 1; i++) // exclude the file name (last segment)
            {
                if (Array.Exists(VendorSegments, v => string.Equals(v, segs[i], StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(string.Join('/', segs.Take(i + 1)) + "/**");
                    break; // shallowest vendor dir on this path is enough
                }
            }
            if (found.Count >= 50) break; // bound the set
        }
        return found.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whole-segment exclude globs for the known vendor/generated directory names, at any
    /// depth — two per marker: "<c>name/**</c>" (workspace root) and "<c>**/name/**</c>" (nested).
    /// Powers firstPartyOnly: a complete, scan-free exclusion that matches exactly the paths
    /// <see cref="IsVendorPath"/> flags as noise, so the two signals never diverge.</summary>
    public static IReadOnlyList<string> VendorExcludeGlobs()
    {
        var globs = new List<string>(VendorSegments.Length * 2);
        foreach (var seg in VendorSegments)
        {
            globs.Add(seg + "/**");         // marker directory at the workspace root
            globs.Add("**/" + seg + "/**"); // marker directory at any depth
        }
        return globs;
    }

    /// <summary>True if any directory segment of the path (excluding the file name) is a known
    /// vendor/generated marker — the per-hit "noise" signal. Segment-exact, case-insensitive,
    /// at any depth. Best-effort and purely path-based, matching <see cref="VendorSegments"/>.</summary>
    public static bool IsVendorPath(string path)
    {
        var segs = path.Split('/');
        for (int i = 0; i < segs.Length - 1; i++) // skip the file name (last segment)
        {
            foreach (var v in VendorSegments)
            {
                if (string.Equals(v, segs[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>Splits a query into index tokens (letter/digit/underscore runs), matching the FTS tokenizer.</summary>
    public static List<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (char c in query)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Builds an FTS5 query: each token quoted, implicit AND.</summary>
    public static string FtsQuery(string query) =>
        string.Join(" ", Tokenize(query).Select(t => $"\"{t}\""));

    /// <summary>Lines (1-based) where the name occurs as a whole identifier token, with each line's
    /// char span in <paramref name="content"/> — one entry per matching line, first match wins.
    /// Single pass with NO per-line allocation: the old Split('\n') materialized every line of every
    /// candidate file (~2x the content's allocation, up to 2000 files per references call); snippets
    /// are now substringed lazily by the caller for sampled lines only. '\n'/'\r' are not identifier
    /// chars, so boundary checks against the raw content match the old per-line semantics exactly.</summary>
    private static List<(int Line, int Start, int End)> LocateTokenLineSpans(string content, string name)
    {
        var result = new List<(int, int, int)>();
        // A name containing '\n' could never match a Split('\n') line in the old implementation — and
        // here a match STARTING with '\n' would make idx = lineEnd a no-progress infinite loop
        // (review-reproduced via references(name:"\nGuard"), which FTS does not reject). Reject it.
        if (name.Length == 0 || name.Contains('\n')) return result;
        int line = 1, lineStart = 0;
        int idx = 0;
        while ((idx = content.IndexOf(name, idx, StringComparison.Ordinal)) >= 0)
        {
            // Advance the line cursor to the match (IndexOf positions are non-decreasing).
            while (true)
            {
                int nl = content.IndexOf('\n', lineStart);
                if (nl < 0 || nl >= idx) break;
                lineStart = nl + 1;
                line++;
            }
            bool leftOk = idx == 0 || !IsIdentChar(content[idx - 1]);
            int end = idx + name.Length;
            bool rightOk = end >= content.Length || !IsIdentChar(content[end]);
            if (leftOk && rightOk)
            {
                int lineEnd = content.IndexOf('\n', idx);
                if (lineEnd < 0) lineEnd = content.Length;
                result.Add((line, lineStart, lineEnd));
                idx = lineEnd; // once per line: skip the rest of this line
            }
            else
            {
                idx = end;
            }
        }
        return result;
    }

    private static string Snippet(string line)
    {
        string t = line.TrimEnd('\r').TrimEnd();
        return t.Length <= 240 ? t : t[..240] + "…";
    }

    /// <summary>Lines immediately before/after a 0-based hit line (grep -B/-A), snippet-trimmed and
    /// clamped to file bounds. Returns (null, null) — omitted from the response — when no context is
    /// requested or at a file edge. Each side is byte-bounded (nearest lines first) so a single
    /// context-heavy hit — e.g. multi-byte/CJK comment blocks — cannot breach the response hard-byte
    /// budget, which floors at one item and so cannot shed a lone hit's context.</summary>
    internal static (IReadOnlyList<string>? Before, IReadOnlyList<string>? After) ContextSlice(
        string[] lines, int lineIdx, int before, int after)
    {
        const int bytesPerSide = 4 * 1024;
        List<string>? b = null, a = null;
        int start = Math.Max(0, lineIdx - before);
        if (before > 0 && start < lineIdx)
        {
            b = new List<string>();
            int bytes = 0;
            for (int j = lineIdx - 1; j >= start && bytes < bytesPerSide; j--) // nearest-first; truncation drops the farthest
            {
                string s = Snippet(lines[j]);
                bytes += System.Text.Encoding.UTF8.GetByteCount(s);
                b.Insert(0, s);
            }
        }
        int end = Math.Min(lines.Length - 1, lineIdx + after);
        if (after > 0 && end > lineIdx)
        {
            a = new List<string>();
            int bytes = 0;
            for (int j = lineIdx + 1; j <= end && bytes < bytesPerSide; j++)
            {
                string s = Snippet(lines[j]);
                bytes += System.Text.Encoding.UTF8.GetByteCount(s);
                a.Add(s);
            }
        }
        return (b, a);
    }

    public void Dispose() => _conn.Dispose();
}
