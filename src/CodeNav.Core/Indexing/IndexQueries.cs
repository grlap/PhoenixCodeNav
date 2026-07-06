using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

public sealed record SymbolHit(
    long Id, string Kind, string Name, string? Ns, string? Container, string Signature,
    string Accessibility, int StartLine, int EndLine, bool IsPartial, string? AttrMarkers,
    string FilePath, bool FileIsGenerated, long? ParentId);

public sealed record FileHit(long Id, string Path, long Size, int LineCount, bool IsGenerated);

public sealed record TextHit(string FilePath, int Line, string LineText, bool IsGenerated);

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

    public List<FileHit> FindFiles(string nameOrGlob, int limit, int offset = 0)
    {
        bool pathQuery = nameOrGlob.Contains('/');
        string like = GlobToLike(nameOrGlob);
        string pattern = pathQuery ? like : $"%/{like}";

        var hits = Query(
            """
            SELECT id, path, size, line_count, is_generated FROM files
            WHERE path LIKE $p ESCAPE '\' OR path LIKE $bare ESCAPE '\'
            ORDER BY is_generated, length(path), path LIMIT $lim OFFSET $off
            """,
            r => new FileHit(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetBoolean(4)),
            ("$p", pattern), ("$bare", like), ("$lim", limit), ("$off", offset));
        return hits;
    }

    // ---------------------------------------------------------------- search_text

    public sealed record TextFilter(
        string? PathGlob = null,
        string? Project = null,
        bool IncludeGenerated = false,
        bool? TestsOnly = null,          // null = both, true = tests only, false = production only
        string? Lang = null);

    public List<TextHit> SearchText(string query, int limit, TextFilter? filter = null,
        int maxCandidateFiles = 200, int offset = 0)
    {
        string fts = FtsQuery(query);
        if (fts.Length == 0) return new();
        filter ??= new TextFilter();

        var args = new List<(string, object)> { ("$q", fts), ("$lim", maxCandidateFiles) };
        var where = new System.Text.StringBuilder("WHERE 1=1");
        string join = "";

        if (!filter.IncludeGenerated) where.Append(" AND f.is_generated = 0");
        if (filter.PathGlob is { } glob)
        {
            where.Append(" AND f.path LIKE $glob ESCAPE '\\'");
            string like = GlobToLike(glob);
            args.Add(("$glob", glob.Contains('/') ? like : $"%/{like}"));
        }
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

        var results = new List<TextHit>(Math.Min(limit, 64));
        int skipped = 0;
        foreach (var c in candidates)
        {
            string? content = ContentById(c.Id);
            if (content is null) continue;
            foreach (int lineNo in LocateLines(content, query, out var lines))
            {
                if (skipped < offset) { skipped++; continue; }
                results.Add(new TextHit(c.Path, lineNo, Snippet(lines[lineNo - 1]), c.Gen));
                if (results.Count >= limit) return results;
            }
        }
        return results;
    }

    // ---------------------------------------------------------------- symbols

    public List<SymbolHit> SearchSymbols(string query, string mode, IReadOnlyList<string>? kinds, int limit,
        bool includeGenerated = false, int offset = 0)
    {
        string esc = EscapeLike(query);
        string pattern = mode switch
        {
            "exact" => esc,
            "prefix" => esc + "%",
            _ => "%" + esc + "%",
        };
        string kindFilter = KindFilter(kinds);
        string genFilter = includeGenerated ? "" : "AND f.is_generated = 0";

        return Query(
            $"""
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.name LIKE $pat ESCAPE '\' {kindFilter} {genFilter}
            ORDER BY
              CASE WHEN s.name = $q COLLATE NOCASE THEN 0
                   WHEN s.name LIKE $pre ESCAPE '\' THEN 1 ELSE 2 END,
              f.is_generated, length(s.name), s.name, f.path
            LIMIT $lim OFFSET $off
            """,
            ReadSymbol,
            ("$pat", pattern), ("$q", query), ("$pre", esc + "%"), ("$lim", limit), ("$off", offset));
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
        var innermost = Query(
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

        if (innermost.Count == 0) return chain;
        chain.Add(innermost[0]);
        long? parent = innermost[0].ParentId;
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

    // ---------------------------------------------------------------- reference candidates

    public (int TotalHits, List<ReferenceGroup> Groups) ReferenceCandidates(
        string symbolName, int maxCandidateFiles = 500, int samplesPerProject = 3)
    {
        var candidates = Query(
            """
            SELECT f.id, f.path, f.is_generated FROM fts_content
            JOIN files f ON f.id = fts_content.rowid
            WHERE fts_content MATCH $q
            ORDER BY f.is_generated, bm25(fts_content)
            LIMIT $lim
            """,
            r => (Id: r.GetInt64(0), Path: r.GetString(1), Gen: r.GetBoolean(2)),
            ("$q", $"\"{symbolName.Replace("\"", "")}\""), ("$lim", maxCandidateFiles));

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
        // "**/" globs collapse naturally because % crosses '/' in LIKE.
        return sb.ToString().Replace("%%", "%");
    }

    /// <summary>Builds an FTS5 query: each token quoted, implicit AND.</summary>
    public static string FtsQuery(string query)
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
        return string.Join(" ", tokens.Select(t => $"\"{t}\""));
    }

    /// <summary>Lines (1-based) containing the raw query case-insensitively; falls back to first token.</summary>
    private static List<int> LocateLines(string content, string query, out string[] lines)
    {
        lines = content.Split('\n');
        var result = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) result.Add(i + 1);
        }
        if (result.Count > 0) return result;

        string fts = FtsQuery(query);
        string firstToken = fts.Length > 0 ? fts.Split(' ')[0].Trim('"') : query;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(firstToken, StringComparison.OrdinalIgnoreCase)) result.Add(i + 1);
        }
        return result;
    }

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
