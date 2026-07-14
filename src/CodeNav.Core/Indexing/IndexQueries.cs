using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CodeNav.Core.Indexing;

public sealed record SymbolHit(
    long Id, string Kind, string Name, string? Ns, string? Container, string Signature,
    string Accessibility, int StartLine, int EndLine, bool IsPartial, string? AttrMarkers,
    string FilePath, bool FileIsGenerated, long? ParentId, bool IsOrphaned = false,
    int Arity = 0,               // generic type-parameter count — Foo and Foo<T> are DIFFERENT types (szs)
    string? Modifiers = null,    // "static sealed abstract virtual override new readonly const" subset (bt7)
    string? Accessors = null,    // "get=public;set=private" only when an accessor differs (hu7)
    string? DeclarationKey = null);

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

public sealed record GraphEdge(string FromProject, string ToProject,
    string Kind = "project"); // 'project' | 'assembly' — edge provenance (bxw, schema v10)

public sealed record ReferenceGroup(string Project, bool IsTestProject, int Count, List<TextHit> Samples);

public sealed record OverviewStats(
    long CsFiles, long TotalLines, long Symbols, long Projects, long LegacyProjects,
    long SdkProjects, long TestProjects, long Solutions, long GeneratedFiles, long OrphanedFiles,
    string TfmBreakdown, string? IndexVersion, string? IndexedAtUtc);

internal sealed record IndexMetadataSnapshot(
    string? SchemaVersion,
    string? WorkspaceRoot,
    string? IndexVersion,
    string? IndexedAtUtc,
    string? LastRefreshUtc,
    string? IndexedCommit,
    string? IndexedBranch);

/// <summary>
/// Owns: read-side queries over the persisted index (one read-only connection, pooled for the
/// writer process and deliberately nonpooled for followers). Does not own: writes (IndexStore) or result budgeting/
/// shaping for MCP responses (M2 tool layer).
/// </summary>
public sealed partial class IndexQueries : IDisposable
{
    private const int CandidateDiscoveryBatchSize = 512;
    private const int DeclarationOffsetParseLimit = 128;
    private const int DeclarationOffsetPerFileCharLimit = 512 * 1024;
    private const int DeclarationOffsetCumulativeCharLimit = 4 * 1024 * 1024;
    private const int DeclarationOffsetPerFileByteLimit = 2 * 1024 * 1024;
    private const int DeclarationOffsetCumulativeByteLimit = 8 * 1024 * 1024;
    private readonly SqliteConnection _conn;
    private readonly SqliteTransaction? _readSnapshot;
    private readonly Action<string>? _afterQueryForTest;
    private readonly Dictionary<(string Path, string Name),
        (byte[] ContentHash, List<(int Start, int End)> Offsets)>
        _declarationOffsets = new();
    private int _declarationOffsetParses;
    private int _declarationOffsetChars;
    private int _declarationOffsetBytes;

    public IndexQueries(string dbPath) : this(dbPath, pinReadSnapshot: false)
    {
    }

    /// <summary>Single source of truth for the read connection string (kae). The pooled-variant
    /// enumeration in <see cref="ClearPoolsFor"/> derives from this same builder, so a new
    /// variant cannot silently escape scoped pool clearing.
    /// DataSource is canonicalized with Path.GetFullPath because pools are keyed by the EXACT
    /// connection string: a reader opened via one spelling of a path (test-composed backslashes)
    /// and a clear issued via another (git-reported forward slashes for the same worktree db)
    /// would otherwise address DIFFERENT pools — the parked handle survives and the next atomic
    /// install fails with 'the staged index could not be atomically installed' (caught by
    /// Batch41's worktree seed/reconcile test when kae first scoped the clears). Same-file paths
    /// that differ only by character CASE, 8.3 short names, or directory-link aliases are NOT
    /// unified — no current caller produces those (every spelling derives from one composed
    /// string, and the product refuses reparse-point index destinations).</summary>
    internal static string ReadConnectionString(string dbPath, bool pinReadSnapshot, bool pooling)
    {
        // Shared-cache table locks would prevent the WAL writer from refreshing tables while a
        // long-lived review read transaction is open. Use a private pager cache for that one
        // snapshot; ordinary short queries retain the pooled shared-cache behavior.
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(dbPath),
            Mode = SqliteOpenMode.ReadOnly,
            // A follower lives in a different process from the writer. Its idle pooled native
            // handle cannot be cleared by the writer before a destructive rebuild on Windows,
            // so follower reads use private, genuinely short-lived connections.
            Pooling = pooling,
            Cache = pinReadSnapshot || !pooling
                ? SqliteCacheMode.Private
                : SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>Releases THIS database's idle pooled reader handles without touching any other
    /// database's pool (kae). The process-global SqliteConnection.ClearAllPools() it replaces
    /// could invalidate a SIBLING database's pooled connection at the rent boundary — observed
    /// as ObjectDisposedException on the SQLitePCL handle mid-query under parallel tests (rqek).
    /// Pools are keyed by connection string, so clearing both pooled read variants of this path
    /// is complete: the writer, meta probes, snapshot copies, and follower reads are
    /// Pooling=false by design and own no pool entries.</summary>
    public static void ClearPoolsFor(string dbPath)
    {
        foreach (bool pinReadSnapshot in new[] { false, true })
        {
            using var poolKeyCarrier = new SqliteConnection(
                ReadConnectionString(dbPath, pinReadSnapshot, pooling: true));
            SqliteConnection.ClearPool(poolKeyCarrier);
        }
    }

    internal IndexQueries(string dbPath, bool pinReadSnapshot,
        Action<string>? afterQueryForTest = null, bool pooling = true)
    {
        _conn = new SqliteConnection(ReadConnectionString(dbPath, pinReadSnapshot, pooling));
        _afterQueryForTest = afterQueryForTest;
        try
        {
            _conn.Open();
            if (pinReadSnapshot)
            {
                // A deferred WAL read transaction allows the refresh writer to keep committing while
                // every query on this connection continues to see one immutable database snapshot.
                // SQLite does not pin that snapshot until the first read, so establish it here before
                // IndexManager validates the surrounding refresh epoch.
                _readSnapshot = _conn.BeginTransaction(deferred: true);
                using var pin = CreateCommand();
                pin.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
                _ = pin.ExecuteScalar();
            }
        }
        catch
        {
            // A failed constructor cannot be disposed by its caller. Release both the partially
            // pinned transaction and connection before any outer ownership lease can unwind.
            try { _readSnapshot?.Dispose(); }
            finally { _conn.Dispose(); }
            throw;
        }
    }

    /// <summary>Reads the persisted index identity through this connection (and therefore through
    /// the same pinned transaction when this is a review snapshot). Followers use this instead of
    /// a writer-process cache, which cannot observe another process's refresh epoch.</summary>
    internal IndexMetadataSnapshot ReadMetadata()
    {
        using var cmd = CreateCommand();
        cmd.CommandText =
            "SELECT key, value FROM meta WHERE key IN " +
            "('schema_version','workspace_root','index_version','indexed_at_utc'," +
            "'last_refresh_utc','indexed_commit','indexed_branch')";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        }
        _afterQueryForTest?.Invoke(cmd.CommandText);
        return new IndexMetadataSnapshot(
            values.GetValueOrDefault("schema_version"),
            values.GetValueOrDefault("workspace_root"),
            values.GetValueOrDefault("index_version"),
            values.GetValueOrDefault("indexed_at_utc"),
            values.GetValueOrDefault("last_refresh_utc"),
            values.GetValueOrDefault("indexed_commit"),
            values.GetValueOrDefault("indexed_branch"));
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
        string? pathGlob = null, IReadOnlyList<string>? excludePaths = null, string? ns = null,
        int? arity = null)
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
        if (arity is { } requestedArity)
        {
            where.Append(" AND s.arity = $arity");
            args.Add(("$arity", requestedArity));
        }
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
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
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

    /// <summary>Distinct generic arities for exact-name declarations. This is syntax-index
    /// authority, not FTS evidence; callers use it to refuse a bare name that would merge
    /// <c>Foo</c>, <c>Foo&lt;T&gt;</c>, and <c>Foo&lt;T1,T2&gt;</c>.</summary>
    public List<int> SymbolArities(string name, IReadOnlyList<string>? kinds = null)
    {
        if (string.IsNullOrEmpty(name)) return new();
        string kindFilter = KindFilter(kinds);
        return Query(
            $"""
            SELECT DISTINCT s.arity
            FROM symbols s
            WHERE s.name = $n COLLATE NOCASE {kindFilter}
            ORDER BY s.arity
            """,
            r => r.GetInt32(0),
            ("$n", name));
    }

    /// <summary>Declarations whose indexed start line exactly matches a source position.
    /// Unlike <see cref="SymbolAt"/>, this returns sibling declarations on the same line so
    /// callers can distinguish same-simple-name generic arities without guessing.</summary>
    public List<SymbolHit> SymbolsStartingAt(string filePath, int line)
    {
        return Query(
            """
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE f.path = $p AND s.start_line = $l
            ORDER BY s.id
            """,
            ReadSymbol,
            ("$p", filePath), ("$l", line));
    }

    public List<SymbolHit> Outline(string filePath)
    {
        return Query(
            """
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE f.path = $p
            ORDER BY s.start_line, s.end_line DESC
            """,
            ReadSymbol, ("$p", filePath));
    }

    /// <summary>91u: symbols in one file whose span intersects ANY of the given 1-based
    /// inclusive line ranges — the server-side hunk-to-symbol mapping for review_pack (the
    /// index already stores spans, so this is one query instead of N symbol_at calls).
    /// Returns EVERY intersecting symbol (types and members); the caller applies the
    /// innermost policy via ParentId. Ranges beyond 64 are IGNORED by this method (SQL
    /// parameter economy) — callers MUST substitute a whole-file range before calling when
    /// their set exceeds 64, as review_pack does (review F2: the old wording claimed a
    /// fallback nobody implemented, and tail hunks were silently dropped).</summary>
    public List<SymbolHit> SymbolsIntersecting(string filePath, IReadOnlyList<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0) return new();
        var args = new List<(string, object)> { ("$p", filePath) };
        var predicates = new List<string>();
        int i = 0;
        foreach (var (start, end) in ranges.Take(64))
        {
            predicates.Add($"(s.start_line <= $e{i} AND s.end_line >= $s{i})");
            args.Add(($"$s{i}", start));
            args.Add(($"$e{i}", end));
            i++;
        }
        return Query(
            $"""
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE f.path = $p AND ({string.Join(" OR ", predicates)})
            ORDER BY s.start_line, s.end_line DESC
            """,
            ReadSymbol, args.ToArray());
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
                       s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
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
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
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
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
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
                       s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
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

    /// <summary>Group key for candidate files in NO project's compile set (orphaned copies).
    /// A dictionary key internally — the MCP layer translates it to a structured row
    /// (project: null, orphaned: true) instead of shipping a magic display string (bxw).</summary>
    public const string NoProjectGroup = "(no project)";

    /// <summary>Result of the indexed reference scan. TotalHits/ProdHits/TestHits are PHYSICAL
    /// line counts (each file counted once — a file linked into several projects is not repeated;
    /// it lands in ProdHits when ANY surviving owner is a production project, else TestHits), so
    /// ProdHits + TestHits == TotalHits always. Group counts are per-project ATTRIBUTIONS and can
    /// sum higher than TotalHits for linked files (0ok: the summary previously printed those
    /// attribution sums next to the physical total — "4 lines (8 production)").</summary>
    public sealed record ReferenceCandidateResult(int TotalHits, int ProdHits, int TestHits,
        List<ReferenceGroup> Groups)
    {
        public bool CandidateFilesTruncated { get; init; }
        public int CandidateFilesScanned { get; init; }
        public int CandidateFilesAtLeast { get; init; }
        public int CandidateFileLimit { get; init; }
        public bool DeclarationExclusionBudgetHit { get; init; }
        public bool DeclarationExclusionApplied { get; init; }
        public int DeclarationFilesParsed { get; init; }
        public int DeclarationFileParseLimit { get; init; }
        public int DeclarationCharsParsed { get; init; }
        public int DeclarationCharLimit { get; init; }
        public int DeclarationPerFileCharLimit { get; init; }
        public int DeclarationBytesParsed { get; init; }
        public int DeclarationByteLimit { get; init; }
        public int DeclarationPerFileByteLimit { get; init; }
    }

    public ReferenceCandidateResult ReferenceCandidates(
        string symbolName, int maxCandidateFiles = 500, int samplesPerProject = 3,
        string? pathGlob = null, IReadOnlyList<string>? excludePaths = null, bool includeGenerated = true,
        bool includeTests = true,
        IReadOnlyList<(string Path, int StartLine, int EndLine)>? excludeSpans = null,
        IReadOnlyList<(string Path, int StartOffset, int EndOffset)>? excludeOffsets = null,
        bool excludeDeclarations = false)
    {
        int boundedCandidateFiles = Math.Max(0, maxCandidateFiles);
        int queryCandidateFiles = boundedCandidateFiles == int.MaxValue
            ? int.MaxValue
            : boundedCandidateFiles + 1;
        var args = new List<(string, object)>
        {
            ("$q", $"\"{symbolName.Replace("\"", "")}\""), ("$lim", queryCandidateFiles),
        };
        // Same include/exclude glob semantics as search_symbol; lets references drop vendored
        // third-party candidate files precisely (counts reflect the filtered set).
        var where = new System.Text.StringBuilder("WHERE fts_content MATCH $q");
        AppendPathFilter(where, args, pathGlob, excludePaths);
        // Drop generated files from candidacy so COUNTS (not just samples) honor includeGenerated (bug wi3).
        if (!includeGenerated) where.Append(" AND f.is_generated = 0");
        var candidates = Query(
            $"""
            SELECT f.id, f.path, f.is_generated, f.size FROM fts_content
            JOIN files f ON f.id = fts_content.rowid
            {where}
            ORDER BY f.is_generated, bm25(fts_content)
            LIMIT $lim
            """,
            r => (Id: r.GetInt64(0), Path: r.GetString(1), Gen: r.GetBoolean(2),
                Size: r.GetInt64(3)),
            args.ToArray());
        bool candidateFilesTruncated = candidates.Count > boundedCandidateFiles;
        int candidateFilesAtLeast = candidateFilesTruncated
            ? boundedCandidateFiles + 1
            : candidates.Count;
        if (candidateFilesTruncated)
            candidates = candidates.Take(boundedCandidateFiles).ToList();
        int candidateFilesScanned = 0;
        bool declarationExclusionBudgetHit = false;
        var excludedSpanMap = excludeSpans?
            .GroupBy(span => span.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(span => (span.StartLine, span.EndLine)).ToList(),
                StringComparer.Ordinal);
        var excludedOffsetMap = excludeOffsets?
            .GroupBy(span => span.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(span => (span.StartOffset, span.EndOffset)).ToList(),
                StringComparer.Ordinal);

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
            var owners = fileProjects.TryGetValue(c.Id, out var list) ? list : new List<(string, bool)> { (NoProjectGroup, false) };
            if (!includeTests) owners = owners.Where(o => !o.Item2).ToList();
            if (owners.Count == 0) continue;

            bool declarationCandidate = excludeDeclarations &&
                                        c.Path.EndsWith(".cs",
                                            StringComparison.OrdinalIgnoreCase);
            if (declarationCandidate && c.Size > DeclarationOffsetPerFileByteLimit)
            {
                declarationExclusionBudgetHit = true;
                break;
            }
            string? content = declarationCandidate
                ? ContentByIdBounded(c.Id, DeclarationOffsetPerFileCharLimit)
                : ContentById(c.Id);
            if (content is null)
            {
                if (declarationCandidate)
                {
                    declarationExclusionBudgetHit = true;
                    break;
                }
                continue;
            }
            List<(int StartOffset, int EndOffset)>? excludedOffsetsForFile = null;
            excludedOffsetMap?.TryGetValue(c.Path, out excludedOffsetsForFile);
            IReadOnlyList<(int StartOffset, int EndOffset)>? effectiveExcludedOffsets =
                excludedOffsetsForFile;
            if (declarationCandidate)
            {
                byte[] contentHash = SHA256.HashData(
                    MemoryMarshal.AsBytes(content.AsSpan()));
                var cacheKey = (c.Path, symbolName);
                if (!_declarationOffsets.TryGetValue(cacheKey, out var cachedDeclarations) ||
                    !cachedDeclarations.ContentHash.AsSpan().SequenceEqual(contentHash))
                {
                    int contentBytes = System.Text.Encoding.UTF8.GetByteCount(content);
                    if (_declarationOffsetParses >= DeclarationOffsetParseLimit ||
                        content.Length > DeclarationOffsetPerFileCharLimit ||
                        content.Length > DeclarationOffsetCumulativeCharLimit -
                        _declarationOffsetChars ||
                        contentBytes > DeclarationOffsetPerFileByteLimit ||
                        contentBytes > DeclarationOffsetCumulativeByteLimit -
                        _declarationOffsetBytes)
                    {
                        declarationExclusionBudgetHit = true;
                        break;
                    }
                    cachedDeclarations = (contentHash,
                        SyntaxIndexer.DeclarationIdentifierOffsets(content, symbolName));
                    _declarationOffsets[cacheKey] = cachedDeclarations;
                    _declarationOffsetParses++;
                    _declarationOffsetChars += content.Length;
                    _declarationOffsetBytes += contentBytes;
                }
                if (cachedDeclarations.Offsets.Count > 0)
                {
                    effectiveExcludedOffsets = excludedOffsetsForFile is null
                        ? cachedDeclarations.Offsets
                        : excludedOffsetsForFile.Concat(cachedDeclarations.Offsets).ToList();
                }
            }
            candidateFilesScanned++;
            var spans = LocateTokenLineSpans(content, symbolName, effectiveExcludedOffsets);
            if (excludedSpanMap is not null && excludedSpanMap.TryGetValue(c.Path,
                    out List<(int StartLine, int EndLine)>? excludedRanges))
            {
                spans = spans.Where(span => !excludedRanges.Any(range =>
                        range.StartLine <= span.Line && span.Line <= range.EndLine))
                    .ToList();
            }
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
        return new ReferenceCandidateResult(total, prodTotal, testTotal, ordered)
        {
            // These are independent coverage causes. The candidate-file cap describes only the
            // SQL result-set limit; declaration exclusion has its own explicit budget flag below.
            // Conflating them made review.reference_candidates_cap fire for a parser/content
            // budget and gave one response two contradictory explanations for the same shortfall.
            CandidateFilesTruncated = candidateFilesTruncated,
            CandidateFilesScanned = candidateFilesScanned,
            CandidateFilesAtLeast = candidateFilesAtLeast,
            CandidateFileLimit = boundedCandidateFiles,
            DeclarationExclusionBudgetHit = declarationExclusionBudgetHit,
            DeclarationExclusionApplied = excludeDeclarations,
            DeclarationFilesParsed = _declarationOffsetParses,
            DeclarationFileParseLimit = DeclarationOffsetParseLimit,
            DeclarationCharsParsed = _declarationOffsetChars,
            DeclarationCharLimit = DeclarationOffsetCumulativeCharLimit,
            DeclarationPerFileCharLimit = DeclarationOffsetPerFileCharLimit,
            DeclarationBytesParsed = _declarationOffsetBytes,
            DeclarationByteLimit = DeclarationOffsetCumulativeByteLimit,
            DeclarationPerFileByteLimit = DeclarationOffsetPerFileByteLimit,
        };
    }

    // ---------------------------------------------------------------- projects

    public ProjectRow? ProjectByName(string name)
    {
        var rows = Query(
            "SELECT id, path, name, style, tfms, is_test, load_status FROM projects WHERE name = $n COLLATE NOCASE LIMIT 1",
            ReadProject, ("$n", name));
        return rows.Count > 0 ? rows[0] : null;
    }

    public List<ProjectRow> AllProjects() => Query(
        "SELECT id, path, name, style, tfms, is_test, load_status FROM projects ORDER BY path, name",
        ReadProject);

    public List<ProjectRow> AllProjects(int limit) => Query(
        "SELECT id, path, name, style, tfms, is_test, load_status FROM projects " +
        "ORDER BY path, name LIMIT $lim",
        ReadProject, ("$lim", Math.Max(0, limit)));

    /// <summary>Current declarations matching source identity while deliberately ignoring the
    /// signature. Type base-list/signature edits do not change declaration identity, and generated
    /// declarations remain eligible. The caller supplies a bound and can probe one extra row when
    /// it needs truncation honesty.</summary>
    public List<SymbolHit> SymbolsByDeclarationIdentity(string kind, string name, string? ns,
        string? container, int arity, int limit)
    {
        return Query(
            """
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path,
                   f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.kind = $kind COLLATE BINARY AND s.name = $name COLLATE BINARY
              AND ((s.ns IS NULL AND $ns IS NULL) OR s.ns = $ns COLLATE BINARY)
              AND ((s.container IS NULL AND $container IS NULL) OR
                   s.container = $container COLLATE BINARY)
              AND s.arity = $arity
            ORDER BY f.path, s.start_line
            LIMIT $limit
            """,
            ReadSymbol, ("$kind", kind), ("$name", name), ("$ns", ns ?? (object)DBNull.Value),
            ("$container", container ?? (object)DBNull.Value), ("$arity", arity),
            ("$limit", Math.Clamp(limit, 1, 10_000)));
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
                SELECT pf.name, pt.name, r.kind FROM project_refs r
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
            return Query(sql, r => new GraphEdge(r.GetString(0), r.GetString(1), r.GetString(2)), ("$n", projectName));
        }

        var edges = Query(
            """
            SELECT pf.name, pt.name, r.kind FROM project_refs r
            JOIN projects pf ON pf.id = r.from_id
            JOIN projects pt ON pt.id = r.to_id
            """,
            r => new GraphEdge(r.GetString(0), r.GetString(1), r.GetString(2)));

        var downstream = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); // project -> deps
        var upstream = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);   // project -> dependents
        // Edge provenance survives the BFS (bxw): result edges are reconstructed from the
        // adjacency maps, so kind must be re-attached from the loaded edge set. The BFS is
        // NAME-keyed by construction (depth 1 is row-granular: a same-AssemblyName pair can
        // surface one edge per ROW, each with its own kind) — so a mixed-kind name pair takes
        // the NAME-LEVEL policy answer here, same as EdgeKindMap: 'project' wins (any real
        // ProjectReference row means the coupling carries refactors; review F2 — plain
        // last-writer rode the JOIN's planner-dependent row order and could flip a recovered
        // coupling invisible at the tool's default depth). Keys are case-folded (review F4:
        // the adjacency maps and the depth-1 SQL are case-insensitive, and the BFS root node
        // carries the CALLER's casing — an unfolded lookup missed and defaulted first hops).
        var kinds = new Dictionary<(string, string), string>();
        foreach (var e in edges)
        {
            (downstream.TryGetValue(e.FromProject, out var d) ? d : downstream[e.FromProject] = new()).Add(e.ToProject);
            (upstream.TryGetValue(e.ToProject, out var u) ? u : upstream[e.ToProject] = new()).Add(e.FromProject);
            var key = (e.FromProject.ToLowerInvariant(), e.ToProject.ToLowerInvariant());
            if (!kinds.TryGetValue(key, out var existing) || existing != "project")
            {
                kinds[key] = e.Kind;
            }
        }
        string KindOf(string from, string to) =>
            kinds.TryGetValue((from.ToLowerInvariant(), to.ToLowerInvariant()), out var k) ? k : "project";

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
                    result.Add(new GraphEdge(node, dep, KindOf(node, dep)));
                    if (visited.Add(dep)) frontier.Enqueue((dep, d + 1));
                }
            }
            if (direction is "upstream" or "both" && upstream.TryGetValue(node, out var dependents))
            {
                foreach (var dependent in dependents)
                {
                    result.Add(new GraphEdge(dependent, node, KindOf(dependent, node)));
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
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.name = $m COLLATE NOCASE AND ({string.Join(" OR ", clauses)})
            ORDER BY f.is_generated, f.path
            LIMIT $lim
            """,
            ReadSymbol, args.ToArray());
    }

    /// <summary>Types whose base list textually mentions the given name (heuristic implementations).</summary>
    public List<SymbolHit> ImplementationCandidates(string name, int limit, int? targetArity = null)
    {
        // An empty name would make the LIKE pattern collapse to '%: %' and match every type that has
        // any base list — never run that catch-all.
        if (string.IsNullOrEmpty(name) || limit <= 0) return new();
        string esc = EscapeLike(name);
        // The LIKE is a SUBSTRING filter, so over-fetch and refine to types whose base list names the
        // interface as a WHOLE identifier token — otherwise 'IFoo' would also match 'IFooBar'.
        List<SymbolHit> QueryPage(int pageLimit, int offset) => Query(
            """
            SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                   s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.kind IN ('class','struct','record','record_struct')
              AND s.name <> $n
              AND s.signature LIKE $pat ESCAPE '\'
            ORDER BY f.is_generated, s.name, f.path, s.id
            LIMIT $lim OFFSET $off
            """,
            ReadSymbol,
            ("$n", name), ("$pat", $"%: %{esc}%"), ("$lim", pageLimit), ("$off", offset));

        if (targetArity is null)
        {
            return QueryPage(Math.Min(limit * 4, 2000), 0)
                .Where(h => BaseListContainsIdentity(h.Signature, name, targetArity))
                .Take(limit)
                .ToList();
        }

        // Arity filtering happens after the broad SQL text prefilter. Page until enough exact
        // syntax matches are found (or the candidate set is exhausted), so earlier rows for IFoo
        // cannot hide every IFoo<T> row behind the prefilter cap.
        int batchSize = Math.Clamp(limit * 4, 64, 512);
        int offset = 0;
        var matches = new List<SymbolHit>(limit);
        while (matches.Count < limit)
        {
            List<SymbolHit> page = QueryPage(batchSize, offset);
            foreach (SymbolHit hit in page)
            {
                if (BaseListContainsIdentity(hit.Signature, name, targetArity))
                {
                    matches.Add(hit);
                    if (matches.Count == limit) break;
                }
            }
            if (page.Count < batchSize) break;
            offset += page.Count;
        }
        return matches;
    }

    /// <summary>Checks the right-most type name in each syntax-derived base-list entry.
    /// The SQL LIKE above remains a broad candidate prefilter; generic arity is decided from
    /// parsed C# syntax so <c>ICache</c> and <c>ICache&lt;T&gt;</c> never merge in a fallback.</summary>
    private static bool BaseListContainsIdentity(string signature, string name, int? targetArity)
    {
        if (!ContainsWholeToken(signature, name)) return false;
        if (targetArity is null) return true;

        int marker = signature.IndexOf(" : ", StringComparison.Ordinal);
        if (marker < 0 || marker + 3 >= signature.Length) return false;

        CompilationUnitSyntax unit = SyntaxFactory.ParseCompilationUnit(
            $"class __PhoenixArityProbe : {signature[(marker + 3)..]} {{ }}");
        BaseListSyntax? baseList = unit.Members.OfType<ClassDeclarationSyntax>()
            .FirstOrDefault()?.BaseList;
        if (baseList is null) return false;

        foreach (BaseTypeSyntax baseType in baseList.Types)
        {
            SimpleNameSyntax? simpleName = baseType.Type switch
            {
                QualifiedNameSyntax qualified => qualified.Right,
                AliasQualifiedNameSyntax aliased => aliased.Name,
                SimpleNameSyntax simple => simple,
                _ => baseType.Type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().LastOrDefault(),
            };
            if (simpleName is null ||
                !string.Equals(simpleName.Identifier.ValueText, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int arity = simpleName is GenericNameSyntax generic
                ? generic.TypeArgumentList.Arguments.Count
                : 0;
            if (arity == targetArity.Value) return true;
        }
        return false;
    }

    /// <summary>jj1q: the full SYNTACTIC subtype closure of a type — every type whose base
    /// list transitively reaches <paramref name="rootName"/> (A→B→C→D chains of any depth;
    /// interfaces included so class-via-derived-interface implementers stay reachable).
    /// Whole-token + arity-checked per hop. Callers run a SEMANTIC verification pass over the
    /// result (same-name collisions across namespaces enter the closure by design and must be
    /// pruned there — syntax narrows, semantics verifies). <paramref name="capped"/> reports
    /// an aborted walk on pathological fan-out: callers MUST fall back to the exhaustive
    /// compiler search then — a truncated closure must never ship as an exact answer.</summary>
    public List<SymbolHit> TransitiveImplementationClosure(
        string rootName, int? rootArity, out bool capped,
        int maxTypes = 2000, CancellationToken cancellationToken = default)
    {
        capped = false;
        var results = new List<SymbolHit>();
        if (string.IsNullOrEmpty(rootName)) return results;
        var seenDeclarations = new HashSet<string>(StringComparer.Ordinal);
        var visitedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = new Queue<(string Name, int? Arity)>();
        frontier.Enqueue((rootName, rootArity));
        visitedNames.Add($"{rootName}`{rootArity?.ToString() ?? "?"}");
        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, arity) = frontier.Dequeue();
            foreach (var hit in BaseListMentions(name, arity, maxTypes + 1, cancellationToken))
            {
                string key = hit.DeclarationKey
                    ?? $"{hit.Ns}|{hit.Container}|{hit.Name}|{hit.Arity}|{hit.FilePath}|{hit.StartLine}";
                if (!seenDeclarations.Add(key)) continue;
                if (results.Count >= maxTypes)
                {
                    capped = true;
                    return results;
                }
                results.Add(hit);
                // Partial declarations of one type share a name — visitedNames keeps the
                // frontier from re-walking a name, while every declaration is still returned.
                if (visitedNames.Add($"{hit.Name}`{hit.Arity}"))
                {
                    frontier.Enqueue((hit.Name, hit.Arity));
                }
            }
        }
        return results;
    }

    /// <summary>All type declarations (interfaces INCLUDED — unlike the heuristic
    /// ImplementationCandidates, which serves a tool that only lists instantiable-ish kinds)
    /// whose base list names <paramref name="name"/> as a whole identifier token with the
    /// given arity. Pages until exhausted or <paramref name="cap"/>.</summary>
    private List<SymbolHit> BaseListMentions(string name, int? targetArity, int cap,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(name) || cap <= 0) return new();
        string esc = EscapeLike(name);
        var matches = new List<SymbolHit>();
        int offset = 0;
        const int page = 512;
        while (matches.Count < cap)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = Query(
                """
                SELECT s.id, s.kind, s.name, s.ns, s.container, s.signature, s.accessibility,
                       s.start_line, s.end_line, s.is_partial, s.attr_markers, f.path, f.is_generated, s.parent_id, s.arity, s.modifiers, s.accessors, s.declaration_key
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE s.kind IN ('class','struct','record','record_struct','interface')
                  AND s.name <> $n
                  AND s.signature LIKE $pat ESCAPE '\'
                ORDER BY s.id
                LIMIT $lim OFFSET $off
                """,
                ReadSymbol,
                ("$n", name), ("$pat", $"%: %{esc}%"), ("$lim", page), ("$off", offset));
            foreach (SymbolHit hit in rows)
            {
                if (BaseListContainsIdentity(hit.Signature, name, targetArity))
                {
                    matches.Add(hit);
                    if (matches.Count == cap) break;
                }
            }
            if (rows.Count < page) break;
            offset += rows.Count;
        }
        return matches;
    }

    /// <summary>
    /// Every project containing a type whose base list names <paramref name="name"/> as a whole
    /// identifier token. Semantic cluster discovery must enumerate projects before applying its
    /// caller-selected project budget; a symbol-hit cap here would silently make maxProjects lie.
    /// </summary>
    public List<string> ImplementationCandidateProjects(
        string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name)) return new();
        string esc = EscapeLike(name);
        var projects = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long afterSymbolId = 0;
        using var ownedSnapshot = _readSnapshot is null ? _conn.BeginTransaction(deferred: true) : null;
        SqliteTransaction snapshot = ownedSnapshot ?? _readSnapshot!;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = QueryCoreCancellable(snapshot,
                """
                SELECT DISTINCT c.id, c.signature, p.name
                FROM (
                    SELECT s.id, s.file_id, s.signature
                    FROM symbols s
                    WHERE s.kind IN ('class','struct','record','record_struct')
                      AND s.name <> $n
                      AND s.signature LIKE $pat ESCAPE '\'
                      AND s.id > $after
                    ORDER BY s.id
                    LIMIT $batch
                ) c
                LEFT JOIN compile_items ci ON ci.file_id = c.file_id
                LEFT JOIN projects p ON p.id = ci.project_id
                ORDER BY c.id, p.name COLLATE NOCASE
                """,
                r => (Id: r.GetInt64(0), Signature: r.GetString(1),
                    Project: r.IsDBNull(2) ? null : r.GetString(2)),
                cancellationToken,
                ("$n", name), ("$pat", $"%: %{esc}%"), ("$after", afterSymbolId),
                ("$batch", CandidateDiscoveryBatchSize));
            if (batch.Count == 0) break;
            afterSymbolId = batch[^1].Id;
            foreach (var candidate in batch)
            {
                if (candidate.Project is not null &&
                    ContainsWholeToken(candidate.Signature, name) &&
                    seen.Add(candidate.Project))
                {
                    projects.Add(candidate.Project);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (batch.Select(candidate => candidate.Id).Distinct().Count() < CandidateDiscoveryBatchSize)
                break;
        }
        return projects;
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

    /// <summary>File paths compiled by a project, ordered. DISTINCT is load-bearing (field P1,
    /// 0.7.2): monoliths carry same-AssemblyName csproj PAIRS (net-old/net-new multi-targets both
    /// named X) — the name join matches BOTH project rows, so without DISTINCT every shared file
    /// came back twice, the semantic workspace created duplicate documents in one adhoc project,
    /// and every reference site in those files was counted twice within its group.</summary>
    public List<string> ProjectFiles(string projectName)
    {
        return Query(
            """
            SELECT DISTINCT f.path FROM compile_items ci
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
    /// Every project whose files textually contain the identifier (FTS whole-token candidates),
    /// ordered by match volume. Project selection is bounded later by the caller's maxProjects;
    /// truncating matching files before this GROUP BY silently hid projects in large repositories.
    /// </summary>
    public List<(string Project, int FileCount)> CandidateProjectsForName(
        string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name)) return new();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long afterFileId = 0;
        using var ownedSnapshot = _readSnapshot is null ? _conn.BeginTransaction(deferred: true) : null;
        SqliteTransaction snapshot = ownedSnapshot ?? _readSnapshot!;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = QueryCoreCancellable(snapshot,
                """
                SELECT DISTINCT m.fid, p.name
                FROM (
                    SELECT rowid AS fid
                    FROM fts_content
                    WHERE fts_content MATCH $q AND rowid > $after
                    ORDER BY rowid
                    LIMIT $batch
                ) m
                LEFT JOIN compile_items ci ON ci.file_id = m.fid
                LEFT JOIN projects p ON p.id = ci.project_id
                ORDER BY m.fid, p.name COLLATE NOCASE
                """,
                r => (FileId: r.GetInt64(0), Project: r.IsDBNull(1) ? null : r.GetString(1)),
                cancellationToken,
                ("$q", $"\"{name.Replace("\"", "")}\""), ("$after", afterFileId),
                ("$batch", CandidateDiscoveryBatchSize));
            if (batch.Count == 0) break;
            afterFileId = batch[^1].FileId;
            long currentFileId = -1;
            var projectsForFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in batch)
            {
                if (match.FileId != currentFileId)
                {
                    currentFileId = match.FileId;
                    projectsForFile.Clear();
                }
                if (match.Project is not null && projectsForFile.Add(match.Project))
                    counts[match.Project] = counts.GetValueOrDefault(match.Project) + 1;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (batch.Select(match => match.FileId).Distinct().Count() < CandidateDiscoveryBatchSize)
                break;
        }
        return counts
            .Select(entry => (Project: entry.Key, FileCount: entry.Value))
            .OrderByDescending(entry => entry.FileCount)
            .ThenBy(entry => entry.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    /// <summary>Edge-kind lookup keyed by (fromName, toName), case-insensitive keys folded to
    /// lower — annotates dependency_path hops with provenance (bxw): 'project' vs 'assembly'.
    /// A pair connected via both twin rows keeps 'project' precedence (first-writer insert).</summary>
    public Dictionary<(string From, string To), string> EdgeKindMap()
    {
        var map = new Dictionary<(string, string), string>();
        foreach (var (from, to, kind) in Query(
            """
            SELECT pf.name, pt.name, r.kind FROM project_refs r
            JOIN projects pf ON pf.id = r.from_id
            JOIN projects pt ON pt.id = r.to_id
            """,
            r => (From: r.GetString(0), To: r.GetString(1), Kind: r.GetString(2))))
        {
            var key = (from.ToLowerInvariant(), to.ToLowerInvariant());
            // Same-name pair rows can carry both kinds for one NAME-level edge — 'project' wins
            // (an explicit ProjectReference is the stronger provenance claim). The conditional is
            // load-bearing: plain last-writer would ride the JOIN's planner-dependent row order.
            if (!map.TryGetValue(key, out var existing) || existing != "project")
            {
                map[key] = kind;
            }
        }
        return map;
    }

    /// <summary>Shortest dependency paths (project references) from one project to another.</summary>
    public List<List<string>> DependencyPaths(string fromProject, string toProject, int maxPaths = 3)
    {
        // 46p: the old implementation was a FIFO BFS of materialized path copies — on a wide
        // lattice it enumerated EVERY equal-length partial path before the first result could
        // stop it (~width^layers: a 17-layer 3-wide synthetic graph took 69 seconds and GB-scale
        // allocations). Two phases instead, no partial-path materialization:
        //   1. Reverse BFS from the TARGET gives distTo[n] = hops from n to the target — O(V+E).
        //   2. DFS from the source following ONLY strictly-descending edges
        //      (distTo[dep] == distTo[node] - 1): every step provably lies on a shortest path,
        //      so there are no dead ends and enumeration costs O(maxPaths × pathLength).
        // Contract preserved: shortest-length paths only, up to maxPaths, name lists,
        // fromProject == toProject yields the single-node path. One deliberate change: a
        // same-AssemblyName pair's duplicate name-level edges previously emitted DUPLICATE
        // identical paths — neighbors are now deduped per node, so paths are distinct.
        var (downstream, upstream) = ProjectGraphEdges();
        var results = new List<List<string>>();

        var distTo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [toProject] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(toProject);
        while (queue.Count > 0)
        {
            string node = queue.Dequeue();
            if (!upstream.TryGetValue(node, out var preds)) continue;
            foreach (var pred in preds)
            {
                if (distTo.ContainsKey(pred)) continue;
                distTo[pred] = distTo[node] + 1;
                queue.Enqueue(pred);
            }
        }
        if (!distTo.ContainsKey(fromProject)) return results; // unreachable (or unknown) — found:false

        var path = new List<string> { fromProject };
        Walk(fromProject);
        return results;

        void Walk(string node)
        {
            if (results.Count >= maxPaths) return;
            if (distTo[node] == 0) // the target (covers fromProject == toProject as [from])
            {
                results.Add(new List<string>(path));
                return;
            }
            if (!downstream.TryGetValue(node, out var deps)) return; // cannot happen: distTo > 0 implies an edge
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // pair-row dup edges → one path
            foreach (var dep in deps)
            {
                if (results.Count >= maxPaths) return;
                if (!distTo.TryGetValue(dep, out int d) || d != distTo[node] - 1) continue; // off the shortest DAG
                if (!seen.Add(dep)) continue;
                path.Add(dep);
                Walk(dep);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    /// <summary>Signal (49k) grades the STRONGEST usage shape found in the group's sampled
    /// mention lines: "callSite" ('Name(' — invocation or construction), "typeUsage"
    /// ('new Name' / ': Name' / 'Name&lt;' / '&lt;Name' / 'Name.'), "nameMention" (comments,
    /// strings, usings — the field's complaint: a test whose only link is a string literal
    /// ranked identically to one that CALLS the symbol). Null for the non-mention reasons
    /// (naming convention / project reference), whose Reason already carries the tier — and
    /// for a mention group whose ordinal line scan missed the FTS match (case/tokenizer
    /// nuances), so ABSENCE of Signal is "ungraded", never a graded nameMention.
    /// Text-shape heuristics on purpose — related_tests is a heuristic-confidence tool.</summary>
    public sealed record RelatedTestGroup(string TestProject, string Reason, int MatchingFiles, List<TextHit> Samples,
        string? Signal = null);

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
            SELECT p.name, f.path, f.is_generated, f.id, COUNT(*) OVER (PARTITION BY p.name) FROM m
            JOIN files f ON f.id = m.fid
            JOIN compile_items ci ON ci.file_id = m.fid
            JOIN projects p ON p.id = ci.project_id
            WHERE p.is_test = 1
            ORDER BY p.name, f.path
            """,
            r => (Project: r.GetString(0), Path: r.GetString(1), Gen: r.GetBoolean(2), Id: r.GetInt64(3), Count: r.GetInt32(4)),
            ("$q", $"\"{symbolName.Replace("\"", "")}\""));
        foreach (var c in candidates)
        {
            if (!groups.TryGetValue(c.Project, out var g))
            {
                groups[c.Project] = g = new RelatedTestGroup(c.Project, "references symbol name", c.Count, new List<TextHit>());
            }
            if (g.Samples.Count >= 3) continue;
            // 49k: locate the mention LINES (samples used to ship a placeholder line 1 with
            // empty text) and grade the strongest usage shape — the group keeps its max tier
            // across the sampled files. Bounded: content is fetched only for the <=3 sample
            // files per group, and <=20 located lines are graded per file.
            string? content = ContentById(c.Id);
            var spans = content is null
                ? new List<(int Line, int Start, int End)>()
                : LocateTokenLineSpans(content, symbolName);
            if (spans.Count == 0)
            {
                // FTS matched but the whole-token line scan did not (tokenizer nuances) —
                // keep the file visible the old way rather than dropping the evidence.
                g.Samples.Add(new TextHit(c.Path, 1, "", c.Gen));
                continue;
            }
            foreach (var (ln, s, e) in spans.Take(3 - g.Samples.Count))
            {
                g.Samples.Add(new TextHit(c.Path, ln, Snippet(content![s..e]), c.Gen));
            }
            int tier = SignalTierOf(g.Signal);
            foreach (var (_, s, e) in spans.Take(20))
            {
                tier = Math.Max(tier, UsageTier(content!, s, e, symbolName));
                if (tier >= 3) break;
            }
            groups[c.Project] = g with { Signal = SignalName(tier) }; // Samples list is shared
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

    public FileHit? FileByPath(string filePath)
    {
        var rows = Query(
            "SELECT id, path, size, line_count, is_generated FROM files WHERE path = $p",
            r => new FileHit(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3),
                r.GetBoolean(4)), ("$p", filePath));
        return rows.Count > 0 ? rows[0] : null;
    }

    public string? ContentByPathBounded(string filePath, int maxChars)
    {
        if (maxChars < 0) return null;
        var rows = Query(
            "SELECT CASE WHEN length(c.content) <= $max THEN c.content END " +
            "FROM file_contents c JOIN files f ON f.id = c.file_id WHERE f.path = $p",
            r => r.IsDBNull(0) ? null : r.GetString(0), ("$p", filePath), ("$max", maxChars));
        return rows.Count > 0 ? rows[0] : null;
    }

    public OverviewStats Overview()
    {
        long Scalar(string sql)
        {
            using var cmd = CreateCommand();
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
        using var cmd = CreateCommand();
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
        using var cmd = CreateCommand();
        cmd.CommandText = "SELECT content FROM file_contents WHERE file_id = $id";
        cmd.Parameters.AddWithValue("$id", fileId);
        return cmd.ExecuteScalar() as string;
    }

    private string? ContentByIdBounded(long fileId, int maxChars)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN length(content) <= $max THEN content END " +
                          "FROM file_contents WHERE file_id = $id";
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.Parameters.AddWithValue("$max", maxChars);
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
        Modifiers: r.FieldCount > 15 && !r.IsDBNull(15) ? r.GetString(15) : null,
        Accessors: r.FieldCount > 16 && !r.IsDBNull(16) ? r.GetString(16) : null,
        DeclarationKey: r.FieldCount > 17 && !r.IsDBNull(17) ? r.GetString(17) : null);

    private static ProjectRow ReadProject(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetBoolean(5), r.GetString(6));

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object)[] args) =>
        QueryCore(_readSnapshot, sql, map, args);

    private List<T> QueryCore<T>(SqliteTransaction? transaction, string sql,
        Func<SqliteDataReader, T> map, params (string, object)[] args)
        => QueryCoreCancellable(transaction, sql, map, CancellationToken.None, args);

    private List<T> QueryCoreCancellable<T>(SqliteTransaction? transaction, string sql,
        Func<SqliteDataReader, T> map, CancellationToken cancellationToken,
        params (string, object)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var list = new List<T>();
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state =>
            {
                try { ((SqliteCommand)state!).Cancel(); } catch { }
            }, cmd);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(map(reader));
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _afterQueryForTest?.Invoke(sql);
        return list;
    }

    private SqliteCommand CreateCommand()
    {
        SqliteCommand cmd = _conn.CreateCommand();
        cmd.Transaction = _readSnapshot;
        return cmd;
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
    private static List<(int Line, int Start, int End)> LocateTokenLineSpans(string content,
        string name, IReadOnlyList<(int StartOffset, int EndOffset)>? excludedOffsets = null)
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
            bool excluded = excludedOffsets?.Any(range =>
                range.StartOffset <= idx && end <= range.EndOffset) == true;
            if (leftOk && rightOk && !excluded)
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

    /// <summary>49k: grade ONE line slice by the strongest whole-token usage shape of the name
    /// in it — 3 'Name(' (invocation/construction: something EXECUTES), 2 type usage
    /// ('new Name' / ': Name' / 'Name&lt;' / '&lt;Name' via preceding '&lt;' / 'Name.'), 1 bare
    /// mention (string literals, comments, usings). Text shapes on purpose: related_tests is a
    /// heuristic tool, and the tier is a lead-strength label, never a compiler fact.</summary>
    private static int UsageTier(string content, int lineStart, int lineEnd, string name)
    {
        int best = 1;
        int idx = lineStart;
        while (best < 3 && idx < lineEnd
               && (idx = content.IndexOf(name, idx, StringComparison.Ordinal)) >= 0 && idx < lineEnd)
        {
            int end = idx + name.Length;
            bool word = (idx == 0 || !IsIdentChar(content[idx - 1]))
                        && (end >= content.Length || !IsIdentChar(content[end]));
            if (!word) { idx = end; continue; }

            int tier = 1;
            int i = end;
            while (i < lineEnd && content[i] is ' ' or '\t') i++;
            if (i < lineEnd && content[i] == '(') tier = 3;
            else if (i < lineEnd && content[i] is '<' or '.') tier = 2;
            else
            {
                int j = idx - 1;
                while (j >= lineStart && content[j] is ' ' or '\t') j--;
                // '.' before = a QUALIFIED reference (Ns.Name / obj.Name) — at least type/member
                // usage even when the after-char is neutral (e.g. List<LibNs.Zeta> ends in '>').
                if (j >= lineStart && content[j] is ':' or '<' or '.') tier = 2;
                else if (j - 2 >= lineStart && content[j] == 'w' && content[j - 1] == 'e' && content[j - 2] == 'n'
                         && (j - 3 < lineStart || !IsIdentChar(content[j - 3]))) tier = 2; // 'new Name'
            }
            if (tier > best) best = tier;
            idx = end;
        }
        return best;
    }

    private static string SignalName(int tier) => tier switch { 3 => "callSite", 2 => "typeUsage", _ => "nameMention" };

    /// <summary>1ly: single-token SPELLING suggestions — distinct symbol names within Damerau
    /// edit distance 1 of the token (one substitution, adjacent transposition, insertion, or
    /// deletion; case-insensitive), anchored on the case-folded FIRST character so the scan
    /// rides idx_symbols_name (NOCASE) instead of walking the table. Ordered by declaration
    /// count (most-declared first) so the caller probes the likeliest names. The caller PROBES
    /// every candidate before suggesting — nothing unverified reaches the wire. Accepted
    /// limitations, documented rather than papered over: first-character typos are missed (a
    /// full-alphabet fan-out or table scan is not worth a heuristic suggestion), and tokens
    /// under 4 chars are refused (their ED-1 neighborhoods are noise).</summary>
    public List<string> NearMissSymbolNames(string token, int maxCandidates = 3)
    {
        if (token.Length < 4 || token.Length > 128) return new();
        char first = token[0];
        if (first >= char.MaxValue) return new();
        var rows = Query(
            """
            SELECT name, COUNT(*) FROM symbols
            WHERE name >= $lo COLLATE NOCASE AND name < $hi COLLATE NOCASE
              AND LENGTH(name) BETWEEN $l1 AND $l2
            GROUP BY name
            LIMIT 20000
            """,
            r => (Name: r.GetString(0), Count: r.GetInt32(1)),
            ("$lo", first.ToString()), ("$hi", ((char)(first + 1)).ToString()),
            ("$l1", token.Length - 1), ("$l2", token.Length + 1));
        return rows
            .Where(r => WithinDamerauDistance1(token, r.Name))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase) // case-twins probe identically (FTS is nocase)
            .Take(maxCandidates)
            .ToList();
    }

    /// <summary>Damerau-Levenshtein distance EXACTLY 1, case-insensitive: one substitution,
    /// one adjacent transposition, or one insertion/deletion. Identical-ignoring-case returns
    /// false — that is not a suggestion, and FTS is case-insensitive anyway.</summary>
    private static bool WithinDamerauDistance1(string a, string b)
    {
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > 1) return false;
        static char F(char c) => char.ToLowerInvariant(c);
        if (la == lb)
        {
            int i = 0;
            while (i < la && F(a[i]) == F(b[i])) i++;
            if (i == la) return false; // identical (nocase)
            // One substitution: the rest matches.
            bool substitution = true;
            for (int k = i + 1; k < la; k++) { if (F(a[k]) != F(b[k])) { substitution = false; break; } }
            if (substitution) return true;
            // One adjacent transposition: swapped pair, then the rest matches.
            if (i + 1 < la && F(a[i]) == F(b[i + 1]) && F(a[i + 1]) == F(b[i]))
            {
                for (int k = i + 2; k < la; k++) { if (F(a[k]) != F(b[k])) return false; }
                return true;
            }
            return false;
        }
        // Lengths differ by one: one insertion/deletion — walk with a single skip in the longer.
        string s = la < lb ? a : b, l = la < lb ? b : a;
        int si = 0, li = 0;
        bool skipped = false;
        while (si < s.Length && li < l.Length)
        {
            if (F(s[si]) == F(l[li])) { si++; li++; continue; }
            if (skipped) return false;
            skipped = true;
            li++; // skip one char of the longer
        }
        return true; // trailing longer char (if any) is the single skip
    }

    private static int SignalTierOf(string? signal) => signal switch { "callSite" => 3, "typeUsage" => 2, "nameMention" => 1, _ => 0 };

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

    public void Dispose()
    {
        _readSnapshot?.Dispose();
        _conn.Dispose();
    }
}
