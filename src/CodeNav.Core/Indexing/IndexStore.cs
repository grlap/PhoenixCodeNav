using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: the persisted SQLite index — schema, bulk writes (single writer), and the
/// read API used by tools and benchmarks. WAL mode allows concurrent readers.
/// Does not own: parsing (SyntaxIndexer/ProjectFileParser) or orchestration (IndexBuilder).
/// </summary>
public sealed class IndexStore : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _write;

    public IndexStore(string dbPath, bool createNew)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        if (createNew && File.Exists(dbPath))
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
            foreach (var sidecar in new[] { dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        }

        _write = Open();
        if (createNew) CreateSchema();
    }

    public string DbPath => _dbPath;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        try
        {
            conn.Open();
            // First op that touches the header — throws on a corrupt/non-SQLite file. Dispose the
            // connection on failure so its OS handle is released (ClearAllPools can then free it,
            // letting a stale/corrupt-index rebuild delete the file rather than fail on a lock).
            Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;");
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Opens a read connection (SQLite WAL supports many readers alongside the writer).</summary>
    public SqliteConnection OpenReader() => Open();

    private void CreateSchema()
    {
        Exec(_write, """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);

            CREATE TABLE files(
              id INTEGER PRIMARY KEY,
              path TEXT NOT NULL UNIQUE,
              size INTEGER NOT NULL,
              mtime_ticks INTEGER NOT NULL,
              hash INTEGER NOT NULL,
              lang TEXT NOT NULL,
              line_count INTEGER NOT NULL DEFAULT 0,
              is_generated INTEGER NOT NULL DEFAULT 0,
              has_test_attrs INTEGER NOT NULL DEFAULT 0,
              stale INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE file_contents(
              file_id INTEGER PRIMARY KEY,
              content TEXT NOT NULL
            );

            CREATE VIRTUAL TABLE fts_content USING fts5(
              content,
              content='file_contents',
              content_rowid='file_id',
              tokenize="unicode61 tokenchars '_'"
            );

            CREATE TABLE projects(
              id INTEGER PRIMARY KEY,
              path TEXT NOT NULL UNIQUE,
              dir TEXT NOT NULL,
              name TEXT NOT NULL,
              style TEXT NOT NULL,
              guid TEXT,
              tfms TEXT NOT NULL,
              is_test INTEGER NOT NULL,
              load_status TEXT NOT NULL
            );
            CREATE INDEX idx_projects_name ON projects(name COLLATE NOCASE);

            CREATE TABLE project_refs(
              from_id INTEGER NOT NULL,
              to_id INTEGER NOT NULL,
              PRIMARY KEY(from_id, to_id)
            ) WITHOUT ROWID;
            CREATE INDEX idx_project_refs_to ON project_refs(to_id);

            CREATE TABLE package_refs(
              project_id INTEGER NOT NULL,
              package TEXT NOT NULL,
              version TEXT NOT NULL
            );
            CREATE INDEX idx_package_refs_project ON package_refs(project_id);

            CREATE TABLE compile_items(
              project_id INTEGER NOT NULL,
              file_id INTEGER NOT NULL,
              PRIMARY KEY(project_id, file_id)
            ) WITHOUT ROWID;
            CREATE INDEX idx_compile_items_file ON compile_items(file_id);

            CREATE TABLE solutions(
              id INTEGER PRIMARY KEY,
              path TEXT NOT NULL UNIQUE,
              name TEXT NOT NULL
            );
            CREATE TABLE solution_projects(
              solution_id INTEGER NOT NULL,
              project_id INTEGER NOT NULL,
              PRIMARY KEY(solution_id, project_id)
            ) WITHOUT ROWID;

            CREATE TABLE symbols(
              id INTEGER PRIMARY KEY,
              file_id INTEGER NOT NULL,
              parent_id INTEGER,
              kind TEXT NOT NULL,
              name TEXT NOT NULL,
              ns TEXT,
              container TEXT,
              signature TEXT NOT NULL,
              accessibility TEXT NOT NULL,
              start_line INTEGER NOT NULL,
              end_line INTEGER NOT NULL,
              is_partial INTEGER NOT NULL,
              arity INTEGER NOT NULL,
              attr_markers TEXT
            );
            CREATE INDEX idx_symbols_file ON symbols(file_id, start_line);
            CREATE INDEX idx_symbols_name ON symbols(name COLLATE NOCASE);
            CREATE INDEX idx_symbols_kind ON symbols(kind, name COLLATE NOCASE);
            """);
    }

    // ================================================================ write API

    public SqliteTransaction BeginTransaction() => _write.BeginTransaction();

    public long InsertFile(SqliteTransaction tx, string path, long size, long mtimeTicks, ulong hash,
        string lang, int lineCount, bool isGenerated, bool hasTestAttrs)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO files(path, size, mtime_ticks, hash, lang, line_count, is_generated, has_test_attrs)
            VALUES($p, $s, $m, $h, $l, $lc, $g, $t);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$s", size);
        cmd.Parameters.AddWithValue("$m", mtimeTicks);
        cmd.Parameters.AddWithValue("$h", unchecked((long)hash));
        cmd.Parameters.AddWithValue("$l", lang);
        cmd.Parameters.AddWithValue("$lc", lineCount);
        cmd.Parameters.AddWithValue("$g", isGenerated ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", hasTestAttrs ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    public void InsertContent(SqliteTransaction tx, long fileId, string content)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO file_contents(file_id, content) VALUES($id, $c);" +
                          "INSERT INTO fts_content(rowid, content) VALUES($id, $c);";
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.ExecuteNonQuery();
    }

    public void InsertSymbols(SqliteTransaction tx, long fileId, List<SymbolRow> rows)
    {
        if (rows.Count == 0) return;
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO symbols(file_id, parent_id, kind, name, ns, container, signature,
                                accessibility, start_line, end_line, is_partial, arity, attr_markers)
            VALUES($f, $p, $k, $n, $ns, $c, $sig, $acc, $sl, $el, $part, $ar, $attr);
            SELECT last_insert_rowid();
            """;
        var pF = cmd.Parameters.Add("$f", SqliteType.Integer);
        var pP = cmd.Parameters.Add("$p", SqliteType.Integer);
        var pK = cmd.Parameters.Add("$k", SqliteType.Text);
        var pN = cmd.Parameters.Add("$n", SqliteType.Text);
        var pNs = cmd.Parameters.Add("$ns", SqliteType.Text);
        var pC = cmd.Parameters.Add("$c", SqliteType.Text);
        var pSig = cmd.Parameters.Add("$sig", SqliteType.Text);
        var pAcc = cmd.Parameters.Add("$acc", SqliteType.Text);
        var pSl = cmd.Parameters.Add("$sl", SqliteType.Integer);
        var pEl = cmd.Parameters.Add("$el", SqliteType.Integer);
        var pPart = cmd.Parameters.Add("$part", SqliteType.Integer);
        var pAr = cmd.Parameters.Add("$ar", SqliteType.Integer);
        var pAttr = cmd.Parameters.Add("$attr", SqliteType.Text);

        var ordinalToId = new long[rows.Count];
        foreach (var row in rows)
        {
            pF.Value = fileId;
            pP.Value = row.ParentOrdinal >= 0 ? ordinalToId[row.ParentOrdinal] : DBNull.Value;
            pK.Value = row.Kind;
            pN.Value = row.Name;
            pNs.Value = (object?)row.Namespace ?? DBNull.Value;
            pC.Value = (object?)row.Container ?? DBNull.Value;
            pSig.Value = row.Signature;
            pAcc.Value = row.Accessibility;
            pSl.Value = row.StartLine;
            pEl.Value = row.EndLine;
            pPart.Value = row.IsPartial ? 1 : 0;
            pAr.Value = row.Arity;
            pAttr.Value = (object?)row.AttrMarkers ?? DBNull.Value;
            ordinalToId[row.OrdinalInFile] = (long)cmd.ExecuteScalar()!;
        }
    }

    public long InsertProject(SqliteTransaction tx, Discovery.ParsedProject p)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projects(path, dir, name, style, guid, tfms, is_test, load_status)
            VALUES($p, $d, $n, $st, $g, $tf, $t, $ls);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", p.RelPath);
        cmd.Parameters.AddWithValue("$d", Path.GetDirectoryName(p.RelPath)?.Replace('\\', '/') ?? "");
        cmd.Parameters.AddWithValue("$n", p.Name);
        cmd.Parameters.AddWithValue("$st", p.Style);
        cmd.Parameters.AddWithValue("$g", (object?)p.Guid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tf", p.TargetFrameworks);
        cmd.Parameters.AddWithValue("$t", p.IsTest ? 1 : 0);
        cmd.Parameters.AddWithValue("$ls", p.LoadStatus);
        return (long)cmd.ExecuteScalar()!;
    }

    public void InsertProjectRef(SqliteTransaction tx, long fromId, long toId) =>
        ExecTx(tx, "INSERT OR IGNORE INTO project_refs(from_id, to_id) VALUES($a, $b)", ("$a", fromId), ("$b", toId));

    public void InsertPackageRef(SqliteTransaction tx, long projectId, string package, string version) =>
        ExecTx(tx, "INSERT INTO package_refs(project_id, package, version) VALUES($a, $b, $c)",
            ("$a", projectId), ("$b", package), ("$c", version));

    public void InsertCompileItem(SqliteTransaction tx, long projectId, long fileId) =>
        ExecTx(tx, "INSERT OR IGNORE INTO compile_items(project_id, file_id) VALUES($a, $b)", ("$a", projectId), ("$b", fileId));

    public long InsertSolution(SqliteTransaction tx, string path, string name)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO solutions(path, name) VALUES($p, $n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$n", name);
        return (long)cmd.ExecuteScalar()!;
    }

    public void InsertSolutionProject(SqliteTransaction tx, long solutionId, long projectId) =>
        ExecTx(tx, "INSERT OR IGNORE INTO solution_projects(solution_id, project_id) VALUES($a, $b)",
            ("$a", solutionId), ("$b", projectId));

    // ================================================================ delta refresh API

    public sealed record StoredFile(long Id, string Path, long Hash, string Lang);

    public Dictionary<string, StoredFile> AllFilesByPath()
    {
        var map = new Dictionary<string, StoredFile>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT id, path, hash, lang FROM files";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var f = new StoredFile(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3));
            map[f.Path] = f;
        }
        return map;
    }

    public string? GetContentForWrite(long fileId)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT content FROM file_contents WHERE file_id = $id";
        cmd.Parameters.AddWithValue("$id", fileId);
        return cmd.ExecuteScalar() as string;
    }

    public void UpdateFileRow(SqliteTransaction tx, long fileId, long size, long mtimeTicks, ulong hash,
        int lineCount, bool isGenerated, bool hasTestAttrs) =>
        ExecTx(tx, """
            UPDATE files SET size=$s, mtime_ticks=$m, hash=$h, line_count=$lc,
                             is_generated=$g, has_test_attrs=$t, stale=0
            WHERE id=$id
            """,
            ("$s", size), ("$m", mtimeTicks), ("$h", unchecked((long)hash)),
            ("$lc", lineCount), ("$g", isGenerated ? 1 : 0), ("$t", hasTestAttrs ? 1 : 0), ("$id", fileId));

    public void ReplaceContent(SqliteTransaction tx, long fileId, string oldContent, string newContent)
    {
        ExecTx(tx, "INSERT INTO fts_content(fts_content, rowid, content) VALUES('delete', $id, $old)",
            ("$id", fileId), ("$old", oldContent));
        ExecTx(tx, "UPDATE file_contents SET content=$c WHERE file_id=$id", ("$c", newContent), ("$id", fileId));
        ExecTx(tx, "INSERT INTO fts_content(rowid, content) VALUES($id, $c)", ("$id", fileId), ("$c", newContent));
    }

    public void DeleteSymbolsForFile(SqliteTransaction tx, long fileId) =>
        ExecTx(tx, "DELETE FROM symbols WHERE file_id=$id", ("$id", fileId));

    public void DeleteFileCascade(SqliteTransaction tx, long fileId, string? oldContent)
    {
        if (oldContent is not null)
        {
            ExecTx(tx, "INSERT INTO fts_content(fts_content, rowid, content) VALUES('delete', $id, $old)",
                ("$id", fileId), ("$old", oldContent));
        }
        ExecTx(tx, "DELETE FROM file_contents WHERE file_id=$id", ("$id", fileId));
        ExecTx(tx, "DELETE FROM symbols WHERE file_id=$id", ("$id", fileId));
        ExecTx(tx, "DELETE FROM compile_items WHERE file_id=$id", ("$id", fileId));
        ExecTx(tx, "DELETE FROM files WHERE id=$id", ("$id", fileId));
    }

    public void ClearProjectData(SqliteTransaction tx)
    {
        foreach (var table in new[] { "solution_projects", "solutions", "compile_items", "package_refs", "project_refs", "projects" })
        {
            ExecTx(tx, $"DELETE FROM {table}");
        }
    }

    public List<(long Id, string Path, string Lang)> FileIdPathLang()
    {
        var list = new List<(long, string, string)>();
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT id, path, lang FROM files";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public void SetMeta(string key, string value) =>
        Exec(_write, "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=$v",
            ("$k", key), ("$v", value));

    public string? GetMeta(string key)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Optimize()
    {
        Exec(_write, "INSERT INTO fts_content(fts_content) VALUES('optimize');");
        Exec(_write, "ANALYZE; PRAGMA optimize;");
    }

    // ================================================================ helpers

    private static void Exec(SqliteConnection conn, string sql, params (string, object)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    private void ExecTx(SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _write.Dispose();
    }
}
