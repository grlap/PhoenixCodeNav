using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

public sealed record SymbolHit(
    long Id, string Kind, string Name, string? Ns, string? Container, string Signature,
    string Accessibility, int StartLine, int EndLine, bool IsPartial, string? AttrMarkers,
    string FilePath, bool FileIsGenerated, long? ParentId);

public sealed record FileHit(long Id, string Path, long Size, int LineCount, bool IsGenerated);

public sealed record TextHit(
    string FilePath, int Line, string LineText, bool IsGenerated,
    string MatchKind = "precise", IReadOnlyList<string>? Matched = null);

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
    long SdkProjects, long TestProjects, long Solutions, long GeneratedFiles,
    string TfmBreakdown, string? IndexVersion, string? IndexedAtUtc);

/// <summary>
/// Owns: read-side queries over the persisted index (own pooled connection, safe to
/// instantiate per operation). Does not own: writes (IndexStore) or result budgeting/
/// shaping for MCP responses (M2 tool layer).
/// </summary>
public sealed class IndexQueries : IDisposable
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
        int maxCandidateFiles, int offset, string partialsMode)
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
            var (filePrecise, filePartial) = GradeFile(c.Path, content, c.Gen, distinctTokens, rawQuery);
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
        string path, string content, bool gen, IReadOnlyList<string> tokens, string rawQuery)
    {
        var lines = content.Split('\n');
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
            (isPhrase ? phrase : scattered).Add(new TextHit(path, i + 1, Snippet(line), gen, "precise"));
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
                    partial.Add(new TextHit(path, i + 1, Snippet(line), gen, "partial", here));
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
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id
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
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id
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
                       s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id
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

    /// <summary>Innermost symbol containing the line — the single-query flavor of
    /// <see cref="SymbolAt"/> (no ancestor walk), cheap enough to decorate search hits.</summary>
    public SymbolHit? InnermostSymbolAt(string filePath, int line)
    {
        var hits = Query(
            """
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE f.path = $p AND s.start_line <= $l AND s.end_line >= $l
            ORDER BY (s.end_line - s.start_line), s.start_line DESC
            LIMIT 1
            """,
            ReadSymbol,
            ("$p", filePath), ("$l", line));
        return hits.Count > 0 ? hits[0] : null;
    }

    /// <summary>Other files containing a PARTIAL declaration of the same type identity
    /// (name + kind + namespace + containing type) — the partial-type cross-links for an
    /// outline. is_partial=1 keeps an unrelated same-name non-partial type (legal in another
    /// project) out; the container match keeps same-name nested types apart. Best-effort:
    /// identity does not include project or generic arity. Returns up to 11 so the caller can
    /// detect (and mark) the &gt;10 case rather than silently capping.</summary>
    public List<string> PartialDeclarationFiles(string name, string? ns, string kind, string? container, string excludePath)
    {
        return Query(
            """
            SELECT DISTINCT f.path
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.name = $n AND s.kind = $k AND s.is_partial = 1
              AND COALESCE(s.ns, '') = COALESCE($ns, '')
              AND COALESCE(s.container, '') = COALESCE($c, '')
              AND f.path <> $p
            ORDER BY f.path
            LIMIT 11
            """,
            r => r.GetString(0),
            ("$n", name), ("$k", kind), ("$ns", (object?)ns ?? DBNull.Value),
            ("$c", (object?)container ?? DBNull.Value), ("$p", excludePath));
    }

    // ---------------------------------------------------------------- reference candidates

    public (int TotalHits, List<ReferenceGroup> Groups) ReferenceCandidates(
        string symbolName, int maxCandidateFiles = 500, int samplesPerProject = 3,
        string? pathGlob = null, IReadOnlyList<string>? excludePaths = null)
    {
        var args = new List<(string, object)>
        {
            ("$q", $"\"{symbolName.Replace("\"", "")}\""), ("$lim", maxCandidateFiles),
        };
        // Same include/exclude glob semantics as search_symbol; lets references drop vendored
        // third-party candidate files precisely (counts reflect the filtered set).
        var where = new System.Text.StringBuilder("WHERE fts_content MATCH $q");
        AppendPathFilter(where, args, pathGlob, excludePaths);
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

        int total = 0;
        var groups = new Dictionary<string, (bool IsTest, int Count, List<TextHit> Samples)>();
        foreach (var c in candidates)
        {
            string? content = ContentById(c.Id);
            if (content is null) continue;
            var lineNos = LocateTokenLines(content, symbolName, out var lines);
            if (lineNos.Count == 0) continue;

            var owners = fileProjects.TryGetValue(c.Id, out var list) ? list : new List<(string, bool)> { ("(no project)", false) };
            foreach (var (project, isTest) in owners)
            {
                if (!groups.TryGetValue(project, out var g)) g = (isTest, 0, new List<TextHit>());
                g.Count += lineNos.Count;
                foreach (int ln in lineNos.Take(Math.Max(0, samplesPerProject - g.Samples.Count)))
                {
                    g.Samples.Add(new TextHit(c.Path, ln, Snippet(lines[ln - 1]), c.Gen));
                }
                groups[project] = g;
            }
            total += lineNos.Count;
        }

        var ordered = groups
            .Select(kv => new ReferenceGroup(kv.Key, kv.Value.IsTest, kv.Value.Count, kv.Value.Samples))
            .OrderByDescending(g => g.Count)
            .ToList();
        return (total, ordered);
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

    /// <summary>Types whose base list textually mentions the given name (heuristic implementations).</summary>
    public List<SymbolHit> ImplementationCandidates(string name, int limit)
    {
        string esc = EscapeLike(name);
        return Query(
            $"""
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.kind IN ('class','struct','record','record_struct')
              AND s.name <> $n
              AND s.signature LIKE $pat ESCAPE '\'
            ORDER BY f.is_generated, s.name
            LIMIT $lim
            """,
            ReadSymbol,
            ("$n", name), ("$pat", $"%: %{esc}%"), ("$lim", limit));
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
        r.FieldCount > 13 && !r.IsDBNull(13) ? r.GetInt64(13) : null);

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
            string like = GlobToLike(inc);
            if (inc.Contains('/'))
            {
                where.Append(" AND f.path LIKE $incPath ESCAPE '\\'");
                args.Add(("$incPath", like));
            }
            else
            {
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
            string like = GlobToLike(raw);
            string pPath = $"$ex{n}p", pBare = $"$ex{n}b"; // distinct binds per exclude
            n++;
            if (raw.Contains('/'))
            {
                where.Append($" AND f.path NOT LIKE {pPath} ESCAPE '\\'");
                args.Add((pPath, like));
            }
            else
            {
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

    /// <summary>Lines (1-based) where the name occurs as a whole identifier token.</summary>
    private static List<int> LocateTokenLines(string content, string name, out string[] lines)
    {
        lines = content.Split('\n');
        var result = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int idx = 0;
            while ((idx = line.IndexOf(name, idx, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = idx == 0 || !IsIdentChar(line[idx - 1]);
                int end = idx + name.Length;
                bool rightOk = end >= line.Length || !IsIdentChar(line[end]);
                if (leftOk && rightOk)
                {
                    result.Add(i + 1);
                    break;
                }
                idx = end;
            }
        }
        return result;

        static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    private static string Snippet(string line)
    {
        string t = line.TrimEnd('\r').TrimEnd();
        return t.Length <= 240 ? t : t[..240] + "…";
    }

    public void Dispose() => _conn.Dispose();
}
