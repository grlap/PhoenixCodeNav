using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: regex search over indexed file content (search_text regex mode). Pre-narrows candidate files
/// via FTS on the pattern's required literals (RegexLiterals) when any exist, else does a bounded scan;
/// runs .NET Regex per line with a per-match timeout (ReDoS guard) and an overall wall-clock budget.
/// Does NOT own: token/graded text search (SearchTextGraded in IndexQueries.cs) or literal extraction
/// (RegexLiterals). Split out of IndexQueries.cs to keep the regex path isolated.
/// </summary>
public sealed partial class IndexQueries
{
    public RegexSearchResult SearchRegex(string pattern, TextFilter? filter, int maxCandidateFiles,
        int offset, int limit, int ctxBefore, int ctxAfter, int totalBudgetMs = 5000, int perMatchMs = 250)
    {
        if (pattern.Length > 4096) // bound compile cost — the match-timeout does not cover construction
            return new RegexSearchResult(new(), 0, 0, false, false, "regex too long (max 4096 chars)");
        Regex rx;
        try
        {
            // Per-match timeout is the ReDoS guard: a catastrophically-backtracking pattern on one line
            // aborts that match instead of hanging the server.
            rx = new Regex(pattern, RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(Math.Clamp(perMatchMs, 50, 2000)));
        }
        catch (ArgumentException ex)
        {
            return new RegexSearchResult(new(), 0, 0, false, false, $"invalid regex: {ex.Message}");
        }
        filter ??= new TextFilter();

        var literals = RegexLiterals.ExtractRequired(pattern);
        bool narrowed = literals.Count > 0;
        var (candidates, filesTotal) = RegexCandidates(literals, filter, maxCandidateFiles);

        var sw = Stopwatch.StartNew();
        var hits = new List<TextHit>();
        int scanned = 0;
        bool timedOut = false;
        foreach (var c in candidates)
        {
            if (sw.ElapsedMilliseconds > totalBudgetMs) { timedOut = true; break; } // overall budget
            string? content = ContentById(c.Id);
            if (content is null) continue;
            scanned++;
            var lines = content.Split('\n');
            if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1]; // drop trailing "" (see GradeFile)
            for (int i = 0; i < lines.Length; i++)
            {
                // Budget check PER LINE, not just per file — one huge file must not blow past the budget
                // (the per-match timeout bounds each line, not the total). ElapsedMilliseconds is ~ns.
                if (sw.ElapsedMilliseconds > totalBudgetMs) { timedOut = true; break; }
                bool m;
                try { m = rx.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { timedOut = true; break; }
                if (m)
                {
                    var (before, after) = ContextSlice(lines, i, ctxBefore, ctxAfter);
                    hits.Add(new TextHit(c.Path, i + 1, Snippet(lines[i]), c.Gen, "regex", null, before, after));
                }
            }
            if (timedOut) break;
        }

        int totalMatches = hits.Count;
        var page = hits.Skip(offset).Take(limit).ToList();
        return new RegexSearchResult(page, totalMatches, scanned, narrowed, timedOut, null,
            filesTotal, narrowed ? literals : null);
    }

    private (List<(long Id, string Path, bool Gen)> Candidates, int FilesTotal) RegexCandidates(
        List<string> literals, TextFilter filter, int max)
    {
        // Filter args are shared by the candidate query and the coverage COUNT; $lim is added only to
        // the candidate args so the COUNT binds exactly the parameters its SQL references.
        var args = new List<(string, object)>();
        var where = new System.Text.StringBuilder("WHERE 1=1");
        string join = "";
        if (!filter.IncludeGenerated) where.Append(" AND f.is_generated = 0");
        AppendPathFilter(where, args, filter.PathGlob, filter.ExcludePaths);
        if (filter.Lang is { } lang) { where.Append(" AND f.lang = $lang"); args.Add(("$lang", lang)); }
        // Honor project / scope(tests) via the compile graph, same as SearchTextGraded (bug: was silently ignored).
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

        if (literals.Count > 0)
        {
            args.Add(("$q", string.Join(" ", literals.Select(t => $"\"{t}\""))));
            // Coverage honesty: the TRUE count of in-scope candidate files — deliberately WITHOUT the
            // bm25 LIMIT window the candidate query uses. With the window, a common literal saturating
            // it plus a narrow path filter could yield filesTotal == filesScanned and falsely claim
            // full coverage while unscanned in-scope files exist (review finding). A COUNT needs no
            // ranking, so it can afford the full FTS match set.
            int total = (int)Query(
                $"""
                WITH m AS (SELECT rowid AS fid FROM fts_content WHERE fts_content MATCH $q)
                SELECT COUNT(DISTINCT f.id) FROM m
                JOIN files f ON f.id = m.fid
                {join}
                {where}
                """,
                r => r.GetInt64(0), args.ToArray())[0];
            args.Add(("$innerLim", Math.Clamp(max * 10, 2000, 20000)));
            args.Add(("$lim", max));
            var cands = Query(
                $"""
                WITH m AS MATERIALIZED (
                    SELECT rowid AS fid, bm25(fts_content) AS rank
                    FROM fts_content WHERE fts_content MATCH $q ORDER BY rank LIMIT $innerLim
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
            return (cands, total);
        }

        // No safely-extractable literal anchor: bounded scan over indexed source files.
        int scanTotal = (int)Query(
            $"""
            SELECT COUNT(DISTINCT f.id) FROM files f
            {join}
            {where} AND f.lang IN ('cs','csproj','sln','config')
            """,
            r => r.GetInt64(0), args.ToArray())[0];
        args.Add(("$lim", max));
        var scanned = Query(
            $"""
            SELECT f.id, f.path, f.is_generated FROM files f
            {join}
            {where} AND f.lang IN ('cs','csproj','sln','config')
            GROUP BY f.id
            ORDER BY f.is_generated, length(f.path)
            LIMIT $lim
            """,
            r => (Id: r.GetInt64(0), Path: r.GetString(1), Gen: r.GetBoolean(2)),
            args.ToArray());
        return (scanned, scanTotal);
    }
}

/// <summary>Result of a regex content search. Narrowed = FTS-pre-narrowed by required literals (vs a
/// full scan; Literals = the whole-token literals it narrowed on, null when scanning); TimedOut = the
/// per-match ReDoS guard or overall budget fired, so results are PARTIAL. FilesTotal = candidate files
/// in scope BEFORE the cap — FilesScanned &lt; FilesTotal means coverage was clipped (cap or timeout).</summary>
public sealed record RegexSearchResult(
    List<TextHit> Hits, int TotalMatches, int FilesScanned, bool Narrowed, bool TimedOut, string? Error,
    int FilesTotal = 0, IReadOnlyList<string>? Literals = null);
