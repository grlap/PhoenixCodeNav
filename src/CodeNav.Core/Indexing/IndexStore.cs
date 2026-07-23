using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: the persisted SQLite index — schema, bulk writes (single writer), and the
/// read API used by tools and benchmarks. WAL mode allows concurrent readers.
/// Does not own: parsing (SyntaxIndexer/ProjectFileParser) or orchestration (IndexBuilder).
/// </summary>
public sealed class IndexStore : IDisposable
{
    internal static Action<string>? AfterOpenBeforeCreateSchemaForTest { get; set; }
    private readonly string _dbPath;
    private readonly SqliteConnection _write;
    private readonly bool _privateStaging;

    public IndexStore(string dbPath, bool createNew, bool privateStaging = false)
    {
        _dbPath = dbPath;
        _privateStaging = privateStaging;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        if (createNew)
        {
            // Sidecars cleaned UNCONDITIONALLY (review F3): a crash between a main-db delete and
            // its sidecar deletes leaves an orphaned -wal that a fresh build would otherwise
            // replay stale content from. Main-file check stays only for the main delete.
            // kae: scoped — only THIS database's pooled reader handles can block these deletes.
            IndexQueries.ClearPoolsFor(dbPath);
            foreach (var sidecar in new[]
                     { dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        _write = Open(coldBuild: createNew);
        try
        {
            if (createNew) AfterOpenBeforeCreateSchemaForTest?.Invoke(_dbPath);
            if (createNew) CreateSchema();
            InitializeSymbolIdCounter();
        }
        catch
        {
            // A constructor that fails after opening SQLite has no caller-visible instance to
            // dispose. Close the writer here so an outer ownership lease can unwind safely.
            _write.Dispose();
            throw;
        }
    }

    public string DbPath => _dbPath;

    // ---------------------------------------------------------------- lf4p writer timing
    // Plain (unsynchronized) tick counters: every mutation happens on the single writer
    // thread that owns _write — the same invariant the store itself already relies on.
    // Read via WriterTimingsMs after the build completes.
    private long _tFileRows, _tContentRows, _tFtsRows, _tSymbolRows, _tBaseEdgeRows,
                 _tFtsOptimize, _tAnalyze, _tCheckpoint;

    /// <summary>lf4p: where the single writer's time went, in milliseconds — the measurement
    /// that decides whether FTS tokenization (the second-writer split candidate) or plain
    /// b-tree work dominates the build's critical path. Statement-scoped stopwatches cost
    /// tens of nanoseconds against millisecond-scale SQLite work.</summary>
    public (double FileRows, double ContentRows, double FtsRows, double SymbolRows,
            double BaseEdgeRows,
            double FtsOptimize, double Analyze, double Checkpoint) WriterTimingsMs =>
        (ToMs(_tFileRows), ToMs(_tContentRows), ToMs(_tFtsRows), ToMs(_tSymbolRows),
         ToMs(_tBaseEdgeRows), ToMs(_tFtsOptimize), ToMs(_tAnalyze), ToMs(_tCheckpoint));

    private static double ToMs(long ticks) =>
        ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>lf4p (owner-directed config pass): cold builds keep WAL semantics (mid-build
    /// followers may probe/read concurrently — MEMORY journal would turn those reads into BUSY
    /// failures) but get a GB-class page-cache cap and wal_autocheckpoint=0, so b-tree/FTS pages
    /// churn in RAM and no mid-build checkpoint stalls the single writer; Optimize() flushes the
    /// WAL and restores the steady-state autocheckpoint before the index is published. Staging
    /// databases are private by construction and stay on MEMORY/OFF (now with the same big
    /// cache). Live (createNew:false) connections are unchanged. cache_size is a lazily-filled
    /// per-connection LIMIT, not an allocation.</summary>
    private SqliteConnection Open(bool coldBuild)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            // The store already retains its write connection for its full lifetime. Pooling adds
            // no hot-path benefit and could retain native DB/WAL handles after the ownership lease
            // is released by a direct build or manager shutdown.
            Pooling = false,
        };
        var conn = new SqliteConnection(connectionString.ToString());
        try
        {
            conn.Open();
            // First op that touches the header — throws on a corrupt/non-SQLite file. Dispose the
            // connection on failure so its OS handle is released (this connection is unpooled, so
            // disposal alone frees the handle, letting a stale/corrupt-index rebuild delete the
            // file rather than fail on a lock; kae: pool clearing is scoped and reader-only now).
            Exec(conn, _privateStaging
                ? "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-1048576;"
                : coldBuild
                    ? "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-1048576; PRAGMA wal_autocheckpoint=0;"
                    : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;");
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Opens a read connection (SQLite WAL supports many readers alongside the writer).</summary>
    public SqliteConnection OpenReader() => Open(coldBuild: false);

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
            CREATE INDEX idx_files_path_nocase ON files(path COLLATE NOCASE);

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
              lang TEXT NOT NULL,
              guid TEXT,
              tfms TEXT NOT NULL,
              is_test INTEGER NOT NULL,
              load_status TEXT NOT NULL,
              compile_globs INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX idx_projects_name ON projects(name COLLATE NOCASE);

            CREATE TABLE project_refs(
              from_id INTEGER NOT NULL,
              to_id INTEGER NOT NULL,
              kind TEXT NOT NULL DEFAULT 'project',  -- v10 (bxw): 'project' (<ProjectReference>) | 'assembly' (recovered <Reference>+HintPath edge)
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
              attr_markers TEXT,
              modifiers TEXT,  -- v4 (bt7): space-joined static/sealed/abstract/virtual/override/new/readonly/const
              accessors TEXT,  -- v9 (hu7): "get=public;set=private", only when an accessor differs
              declaration_key TEXT NOT NULL -- v11: overload/interface identity separate from display signature
            );
            CREATE INDEX idx_symbols_file ON symbols(file_id, start_line);
            CREATE INDEX idx_symbols_name ON symbols(name COLLATE NOCASE);
            CREATE INDEX idx_symbols_kind ON symbols(kind, name COLLATE NOCASE);

            CREATE TABLE type_base_edges(
              base_name TEXT COLLATE NOCASE NOT NULL,
              base_arity INTEGER NOT NULL CHECK(base_arity >= 0),
              derived_symbol_id INTEGER NOT NULL,
              file_id INTEGER NOT NULL,
              PRIMARY KEY(base_name, base_arity, derived_symbol_id)
            ) WITHOUT ROWID;
            CREATE INDEX idx_type_base_edges_file ON type_base_edges(file_id);
            """);
    }

    // ================================================================ write API

    public SqliteTransaction BeginTransaction() => _write.BeginTransaction();

    private SqliteCommand? _fileInsertCmd; // lf4p: cached like the symbol inserts

    public long InsertFile(SqliteTransaction tx, string path, long size, long mtimeTicks, ulong hash,
        string lang, int lineCount, bool isGenerated, bool hasTestAttrs)
    {
        // lf4p: one store-cached prepared command instead of CreateCommand + 8 AddWithValue +
        // re-prepare per file (17k executions per roslyn-scale build). RETURNING stays: unlike
        // symbols, the caller consumes the id immediately and file rows are one-per-call.
        // Timer covers the WHOLE method now — the execute-only scope it had first under-
        // reported this bucket relative to the others.
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_fileInsertCmd is null)
        {
            _fileInsertCmd = _write.CreateCommand();
            _fileInsertCmd.CommandText = """
                INSERT INTO files(path, size, mtime_ticks, hash, lang, line_count, is_generated, has_test_attrs)
                VALUES($p, $s, $m, $h, $l, $lc, $g, $t)
                RETURNING id;
                """;
            _fileInsertCmd.Parameters.Add("$p", SqliteType.Text);
            _fileInsertCmd.Parameters.Add("$s", SqliteType.Integer);
            _fileInsertCmd.Parameters.Add("$m", SqliteType.Integer);
            _fileInsertCmd.Parameters.Add("$h", SqliteType.Integer);
            _fileInsertCmd.Parameters.Add("$l", SqliteType.Text);
            _fileInsertCmd.Parameters.Add("$lc", SqliteType.Integer);
            _fileInsertCmd.Parameters.Add("$g", SqliteType.Integer);
            _fileInsertCmd.Parameters.Add("$t", SqliteType.Integer);
        }
        _fileInsertCmd.Transaction = tx;
        _fileInsertCmd.Parameters[0].Value = path;
        _fileInsertCmd.Parameters[1].Value = size;
        _fileInsertCmd.Parameters[2].Value = mtimeTicks;
        _fileInsertCmd.Parameters[3].Value = unchecked((long)hash);
        _fileInsertCmd.Parameters[4].Value = lang;
        _fileInsertCmd.Parameters[5].Value = lineCount;
        _fileInsertCmd.Parameters[6].Value = isGenerated ? 1 : 0;
        _fileInsertCmd.Parameters[7].Value = hasTestAttrs ? 1 : 0;
        long id = (long)_fileInsertCmd.ExecuteScalar()!;
        _tFileRows += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return id;
    }

    public void InsertContent(SqliteTransaction tx, long fileId, string content)
    {
        // lf4p: the raw row and the FTS row were one batched command; they are executed (and
        // timed) SEPARATELY now because "is the writer FTS-tokenization-bound?" is exactly the
        // second-writer-split question. The extra command round-trip is µs-scale against the
        // ms-scale tokenization it isolates.
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using (var raw = _write.CreateCommand())
        {
            raw.Transaction = tx;
            raw.CommandText = "INSERT INTO file_contents(file_id, content) VALUES($id, $c);";
            raw.Parameters.AddWithValue("$id", fileId);
            raw.Parameters.AddWithValue("$c", content);
            raw.ExecuteNonQuery();
        }
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        _tContentRows += t1 - t0;
        using (var fts = _write.CreateCommand())
        {
            fts.Transaction = tx;
            fts.CommandText = "INSERT INTO fts_content(rowid, content) VALUES($id, $c);";
            fts.Parameters.AddWithValue("$id", fileId);
            fts.Parameters.AddWithValue("$c", content);
            fts.ExecuteNonQuery();
        }
        _tFtsRows += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
    }

    // ---------------------------------------------------------------- lf4p symbol batching
    // The RETURNING-per-row predecessor cost ~43µs/symbol in per-execute round-trips
    // (measured: 15.2s of a 26.9s roslyn cold build for 354k symbols — 61% of the writer).
    // Two changes remove it:
    //   1. ids are CLIENT-ASSIGNED — the single-writer invariant makes "read committed MAX(id)
    //      once AT STORE OPEN, hand out monotonically" safe: rollbacks and deletes leave gaps,
    //      never collisions (see InitializeSymbolIdCounter for why open-time/committed-state
    //      matters). Parent wiring is resolved BEFORE binding from the assigned ids, so
    //      RETURNING has nothing left to do.
    //   2. rows go through store-cached raw SQLite statements for every exact size 1..32.
    //      Exact-size remainders reduce the runtime-scale corpus from ~355k executions to ~50k.
    //      Binding is ordinal through SQLitePCLRaw: Microsoft.Data.Sqlite resolves every named
    //      parameter again on every execution, which made wide exact-size commands slower despite
    //      the lower call count and allocated ~1.66 GB for the measured 919k-symbol workload.
    // lf4p A/B (roslyn, 354k symbols): 32 → 9.0-9.4s; 128 → 11.0s. 128 lost because this
    // constant is ALSO the eligibility threshold — files with 32..127 symbols fell back to
    // the single-row path entirely. 32 chunks the fat generated files AND keeps the band
    // eligible; a size cascade could recover a little more, parked until numbers demand it.
    private const int SymbolChunkRows = 32;
    private const int SymbolColumns = 17;
    private long _nextSymbolId = -1;
    private readonly sqlite3_stmt?[] _symbolInsertStatements =
        new sqlite3_stmt?[SymbolChunkRows + 1];
    private readonly long[] _symbolInsertExecutions =
        new long[SymbolChunkRows + 1];
    private SqliteCommand? _baseEdgeInsertCmd;

    /// <summary>Review (lf4p): runs at store OPEN, outside any transaction — the first version
    /// read MAX(id) lazily UNDER the caller's tx, which observes that tx's own uncommitted
    /// DELETEs (delta refresh deletes a file's symbols then reinserts in one tx). A rollback
    /// then restored the deleted rows while the counter stayed BELOW the committed max — the
    /// next refresh collided on UNIQUE symbols.id and its whole batch was dropped. Reading
    /// committed state at open makes rollbacks strictly gap-producing, never colliding.</summary>
    private void InitializeSymbolIdCounter()
    {
        using var q = _write.CreateCommand();
        q.CommandText = "SELECT COALESCE(MAX(id), 0) + 1 FROM symbols;";
        _nextSymbolId = (long)q.ExecuteScalar()!;
    }

    private long ReserveSymbolIds(int count)
    {
        long first = _nextSymbolId;
        _nextSymbolId += count;
        return first;
    }

    internal long SymbolInsertExecutionCountForTest(int rowsPerStatement) =>
        rowsPerStatement is >= 1 and <= SymbolChunkRows
            ? _symbolInsertExecutions[rowsPerStatement]
            : throw new ArgumentOutOfRangeException(nameof(rowsPerStatement));

    private sqlite3_stmt BuildSymbolInsert(int rowsPerStatement)
    {
        var sb = new System.Text.StringBuilder(
            "INSERT INTO symbols(id, file_id, parent_id, kind, name, ns, container, signature, " +
            "accessibility, start_line, end_line, is_partial, arity, attr_markers, modifiers, accessors, declaration_key) VALUES ");
        for (int r = 0; r < rowsPerStatement; r++)
        {
            sb.Append(r > 0 ? ",(" : "(");
            for (int c = 0; c < SymbolColumns; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append('?');
            }
            sb.Append(')');
        }
        int rc = raw.sqlite3_prepare_v2(_write.Handle, sb.ToString(),
            out sqlite3_stmt statement);
        if (rc != raw.SQLITE_OK)
            throw RawSqliteException("prepare symbol insert", rc);
        return statement;
    }

    private SqliteException RawSqliteException(
        string operation, int primaryCode, int? parameter = null)
    {
        int extendedCode = raw.sqlite3_extended_errcode(_write.Handle);
        string detail = raw.sqlite3_errmsg(_write.Handle).utf8_to_string();
        string parameterSuffix = parameter.HasValue
            ? $", parameter {parameter.Value}"
            : "";
        return new SqliteException(
            $"{operation} failed (SQLite {primaryCode}/{extendedCode}{parameterSuffix}): {detail}",
            primaryCode, extendedCode);
    }

    private void BindRawInt64(sqlite3_stmt statement, int parameter, long value)
    {
        int rc = raw.sqlite3_bind_int64(statement, parameter, value);
        if (rc != raw.SQLITE_OK)
            throw RawSqliteException("bind symbol integer", rc, parameter);
    }

    private void BindRawText(sqlite3_stmt statement, int parameter, string? value)
    {
        int rc = value is null
            ? raw.sqlite3_bind_null(statement, parameter)
            : raw.sqlite3_bind_text(statement, parameter, value);
        if (rc != raw.SQLITE_OK)
            throw RawSqliteException("bind symbol text", rc, parameter);
    }

    private void BindSymbolRow(sqlite3_stmt statement, int slot, long id, long fileId,
        SymbolRow row, long[] ordinalToId)
    {
        int parameter = slot * SymbolColumns + 1;
        BindRawInt64(statement, parameter++, id);
        BindRawInt64(statement, parameter++, fileId);
        if (row.ParentOrdinal >= 0)
        {
            BindRawInt64(statement, parameter++, ordinalToId[row.ParentOrdinal]);
        }
        else
        {
            int rc = raw.sqlite3_bind_null(statement, parameter++);
            if (rc != raw.SQLITE_OK)
                throw RawSqliteException("bind symbol parent", rc, parameter - 1);
        }
        BindRawText(statement, parameter++, row.Kind);
        BindRawText(statement, parameter++, row.Name);
        BindRawText(statement, parameter++, row.Namespace);
        BindRawText(statement, parameter++, row.Container);
        BindRawText(statement, parameter++, row.Signature);
        BindRawText(statement, parameter++, row.Accessibility);
        BindRawInt64(statement, parameter++, row.StartLine);
        BindRawInt64(statement, parameter++, row.EndLine);
        BindRawInt64(statement, parameter++, row.IsPartial ? 1 : 0);
        BindRawInt64(statement, parameter++, row.Arity);
        BindRawText(statement, parameter++, row.AttrMarkers);
        BindRawText(statement, parameter++, row.Modifiers);
        BindRawText(statement, parameter++, row.Accessors);
        BindRawText(statement, parameter, row.DeclarationKey ?? "");
    }

    private void ExecuteRawSymbolInsert(sqlite3_stmt statement)
    {
        int step = raw.sqlite3_step(statement);
        // Every parameter is rebound before the next step, so reset is sufficient; clearing all
        // 544 bindings would add another native call per parameter without changing semantics.
        int reset = raw.sqlite3_reset(statement);
        if (step != raw.SQLITE_DONE)
            throw RawSqliteException("execute symbol insert", step);
        if (reset != raw.SQLITE_OK)
            throw RawSqliteException("reset symbol insert", reset);
    }

    public void InsertSymbols(SqliteTransaction tx, long fileId, List<SymbolRow> rows)
    {
        if (rows.Count == 0) return;
        if (!ReferenceEquals(tx.Connection, _write))
            throw new InvalidOperationException(
                "Symbol inserts require this IndexStore's active write transaction.");
        long tSym0 = System.Diagnostics.Stopwatch.GetTimestamp(); // lf4p
        long firstId = ReserveSymbolIds(rows.Count);

        // Ordinals are dense 0..N-1 (the array below is exactly the old code's shape); the
        // identity mapping id = firstId + ordinal resolves every parent before any insert.
        var ordinalToId = new long[rows.Count];
        for (int o = 0; o < ordinalToId.Length; o++) ordinalToId[o] = firstId + o;

        int done = 0;
        while (done < rows.Count)
        {
            int statementRows = Math.Min(SymbolChunkRows, rows.Count - done);
            sqlite3_stmt statement =
                _symbolInsertStatements[statementRows] ??=
                    BuildSymbolInsert(statementRows);
            for (int r = 0; r < statementRows; r++)
            {
                var row = rows[done + r];
                BindSymbolRow(statement, r, ordinalToId[row.OrdinalInFile],
                    fileId, row, ordinalToId);
            }
            ExecuteRawSymbolInsert(statement);
            _symbolInsertExecutions[statementRows]++;
            done += statementRows;
        }
        long tEdges = System.Diagnostics.Stopwatch.GetTimestamp();
        _tSymbolRows += tEdges - tSym0; // lf4p

        foreach (SymbolRow row in rows)
        {
            if (row.BaseTypes is not { Count: > 0 }) continue;
            _baseEdgeInsertCmd ??= BuildBaseEdgeInsert();
            _baseEdgeInsertCmd.Transaction = tx;
            foreach (BaseTypeIdentity baseType in row.BaseTypes)
            {
                _baseEdgeInsertCmd.Parameters[0].Value = baseType.Name;
                _baseEdgeInsertCmd.Parameters[1].Value = baseType.Arity;
                _baseEdgeInsertCmd.Parameters[2].Value = ordinalToId[row.OrdinalInFile];
                _baseEdgeInsertCmd.Parameters[3].Value = fileId;
                _baseEdgeInsertCmd.ExecuteNonQuery();
            }
        }
        _tBaseEdgeRows += System.Diagnostics.Stopwatch.GetTimestamp() - tEdges;
    }

    private SqliteCommand BuildBaseEdgeInsert()
    {
        var cmd = _write.CreateCommand();
        cmd.CommandText = """
            INSERT INTO type_base_edges(base_name, base_arity, derived_symbol_id, file_id)
            VALUES($name, $arity, $symbol, $file)
            """;
        cmd.Parameters.Add("$name", SqliteType.Text);
        cmd.Parameters.Add("$arity", SqliteType.Integer);
        cmd.Parameters.Add("$symbol", SqliteType.Integer);
        cmd.Parameters.Add("$file", SqliteType.Integer);
        return cmd;
    }

    public long InsertProject(SqliteTransaction tx, Discovery.ParsedProject p)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projects(path, dir, name, style, lang, guid, tfms, is_test, load_status, compile_globs)
            VALUES($p, $d, $n, $st, $lang, $g, $tf, $t, $ls, $cg);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", p.RelPath);
        cmd.Parameters.AddWithValue("$d",
            WorkspacePaths.ToGitPath(Path.GetDirectoryName(p.RelPath) ?? ""));
        cmd.Parameters.AddWithValue("$n", p.Name);
        cmd.Parameters.AddWithValue("$st", p.Style);
        cmd.Parameters.AddWithValue("$lang", p.Language);
        cmd.Parameters.AddWithValue("$g", (object?)p.Guid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tf", p.TargetFrameworks);
        cmd.Parameters.AddWithValue("$t", p.IsTest ? 1 : 0);
        cmd.Parameters.AddWithValue("$ls", p.LoadStatus);
        // Include/Remove globs — or any project that does not authoritatively opt into default
        // items (review 5a: its dir must not act as an incremental glob root) — make ownership
        // non-derivable from the dir prefix alone; added-file attribution falls back to a rebuild.
        cmd.Parameters.AddWithValue("$cg",
            p.CompileIncludeGlobs is { Count: > 0 } || p.CompileRemoveGlobs is { Count: > 0 } ||
            !p.DefaultCompileItems ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>fgq: transactionally consistent snapshot of a (possibly LIVE) index into a
    /// fresh single-file db at <paramref name="targetDbPath"/>. VACUUM INTO runs as a WAL read
    /// transaction — the writing pump never pauses, the copy can never be torn (the trap a
    /// plain file copy of a live WAL db walks into), and the output is compacted (the field's
    /// 1.1GB db carries FTS bloat). This is the worktree SEED primitive: everything stored is
    /// workspace-RELATIVE, so the file is valid under any root, and the embedded
    /// indexed_commit drives the git reconcile at the destination. Refuses to clobber an
    /// existing target — callers delete explicitly.</summary>
    public static void SnapshotTo(string sourceDbPath, string targetDbPath)
    {
        if (File.Exists(targetDbPath)) throw new IOException($"snapshot target already exists: {targetDbPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetDbPath))!);
        SnapshotCore(sourceDbPath, targetDbPath);
    }

    /// <summary>Writes into an already-reserved empty regular file whose exact handle is held by
    /// the caller. Anchored worktree staging uses this to close the target-leaf race.</summary>
    internal static void SnapshotToReserved(string sourceDbPath, string targetDbPath)
    {
        if (new FileInfo(targetDbPath).Length != 0)
            throw new IOException("reserved snapshot target is not empty");
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = targetDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        source.Open();
        target.Open();
        using (var mode = target.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF;";
            mode.ExecuteNonQuery();
        }
        source.BackupDatabase(target);
        using (var mode = target.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode=MEMORY;";
            mode.ExecuteNonQuery();
        }
    }

    private static void SnapshotCore(string sourceDbPath, string targetDbPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $target";
        cmd.Parameters.AddWithValue("$target", targetDbPath);
        cmd.CommandTimeout = 600; // ~seconds for a 1.1GB db on SSD; generous ceiling, not a hang
        cmd.ExecuteNonQuery();
    }

    /// <summary>Edge provenance (bxw): kind 'project' = an explicit &lt;ProjectReference&gt;;
    /// 'assembly' = a recovered &lt;Reference&gt;+HintPath edge. INSERT OR IGNORE on PK(from,to)
    /// gives FIRST-WRITER precedence — both graph builders insert relpath ProjectReferences
    /// BEFORE AssemblyRefEdges runs, so a pair connected both ways records 'project'.</summary>
    public void InsertProjectRef(SqliteTransaction tx, long fromId, long toId, string kind = "project") =>
        ExecTx(tx, "INSERT OR IGNORE INTO project_refs(from_id, to_id, kind) VALUES($a, $b, $k)",
            ("$a", fromId), ("$b", toId), ("$k", kind));

    /// <summary>R3 of the isTest rules (field: custom assembly resolution injects test-framework
    /// references OUTSIDE the csproj, so parse-time signals R1/R2 can be structurally blind):
    /// promote a project to is_test when its COMPILED set contains a test-attributed file
    /// ([TestFixture]/[Fact]/... — files.has_test_attrs, indexed from source) AND the project is
    /// a graph LEAF (no incoming project_refs — nothing depends on a test assembly). The leaf
    /// guard keeps a SINGLE production lib with one stray attributed file production (user:
    /// mixed projects exist; name shapes like TestRoute must never classify) — for same-name
    /// PAIRS the name-uniformity pass below wins instead (review-assessed: the pair-shaped
    /// protection was already illusory pre-v8; a NAME-level leaf check is the future fix if a
    /// production pair with a stray fixture ever surfaces in the field). Runs AFTER compile-item
    /// attribution and ref insertion; returns rows flipped by BOTH passes for the build log.</summary>
    public int PromoteTestProjectsByCompiledAttributes(SqliteTransaction tx)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE projects SET is_test = 1
            WHERE is_test = 0
              AND EXISTS (SELECT 1 FROM compile_items ci JOIN files f ON f.id = ci.file_id
                          WHERE ci.project_id = projects.id AND f.has_test_attrs = 1)
              AND NOT EXISTS (SELECT 1 FROM project_refs r WHERE r.to_id = projects.id)
            """;
        int promoted = cmd.ExecuteNonQuery();
        // NAME+LANGUAGE uniformity (review): same-AssemblyName C# twins form one Roslyn assembly,
        // but an F# project with the same logical name is a distinct physical compiler boundary.
        // Per-row classification can differ when a referenced twin fails the leaf guard or
        // parse-time R1/R2 diverges, so propagate within one language only. Crossing languages
        // would let a test F# row mark a production C# row as test and make includeTests:false
        // suppress supported semantic results. One pass is a fixed point because propagation is
        // confined to the existing (name, lang) class.
        using var uniform = _write.CreateCommand();
        uniform.Transaction = tx;
        uniform.CommandText = """
            UPDATE projects SET is_test = 1
            WHERE is_test = 0
              AND EXISTS (
                  SELECT 1 FROM projects test
                  WHERE test.is_test = 1
                    AND test.lang = projects.lang
                    AND test.name = projects.name COLLATE NOCASE
              )
            """;
        promoted += uniform.ExecuteNonQuery();
        return promoted;
    }

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

    public Dictionary<string, StoredFile> AllFilesByPath(SqliteTransaction? tx = null)
    {
        var map = new Dictionary<string, StoredFile>(WorkspacePaths.FileSystemPathComparer);
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
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

    /// <summary>Deletes the normalized base edges before their symbol rows in the same
    /// transaction. Symbol ids can be reused only after this helper removes the old edges, so a
    /// refreshed declaration can never inherit another declaration's base identities.</summary>
    public void DeleteSymbolsForFile(SqliteTransaction tx, long fileId)
    {
        ExecTx(tx, "DELETE FROM type_base_edges WHERE file_id=$id", ("$id", fileId));
        ExecTx(tx, "DELETE FROM symbols WHERE file_id=$id", ("$id", fileId));
    }

    public void DeleteFileCascade(SqliteTransaction tx, long fileId, string? oldContent)
    {
        if (oldContent is not null)
        {
            ExecTx(tx, "INSERT INTO fts_content(fts_content, rowid, content) VALUES('delete', $id, $old)",
                ("$id", fileId), ("$old", oldContent));
        }
        ExecTx(tx, "DELETE FROM file_contents WHERE file_id=$id", ("$id", fileId));
        DeleteSymbolsForFile(tx, fileId);
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

    public List<(long Id, string Path, string Lang)> FileIdPathLang(
        SqliteTransaction? tx = null)
    {
        var list = new List<(long, string, string)>();
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, path, lang FROM files";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>True when any project's ownership is NOT derivable from the dir prefix alone —
    /// legacy explicit-&lt;Compile&gt; lists (which can claim a re-added file without a csproj change)
    /// or any Include/Remove compile globs. Gates the incremental added-file attribution: such
    /// shapes are re-read only by the full rebuild.</summary>
    public bool HasNonTrivialCompileShapes(string language)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM projects WHERE lang = $lang AND (style = 'legacy' OR compile_globs = 1))";
        cmd.Parameters.AddWithValue("$lang", language);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>(id, project relPath) of projects that authoritatively own implicit source files by
    /// their directory prefix alone. Used for incremental added-file attribution in trivially-shaped
    /// workspaces (zki); failed/disabled F# shapes are excluded by compile_globs=1.</summary>
    public List<(long Id, string RelPath)> GlobRootProjects(string language)
    {
        var list = new List<(long, string)>();
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT id, path FROM projects WHERE lang = $lang AND style != 'legacy' AND compile_globs = 0";
        cmd.Parameters.AddWithValue("$lang", language);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    public void SetMeta(string key, string value) =>
        Exec(_write, "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=$v",
            ("$k", key), ("$v", value));

    internal void SetMeta(SqliteTransaction tx, string key, string value) =>
        ExecTx(tx,
            "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=$v",
            ("$k", key), ("$v", value));

    public void DeleteMeta(string key) =>
        Exec(_write, "DELETE FROM meta WHERE key=$k", ("$k", key));

    internal void DeleteMeta(SqliteTransaction tx, string key) =>
        ExecTx(tx, "DELETE FROM meta WHERE key=$k", ("$k", key));

    public string? GetMeta(string key)
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Optimize()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); // lf4p
        Exec(_write, "INSERT INTO fts_content(fts_content) VALUES('optimize');");
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        _tFtsOptimize += t1 - t0;
        Exec(_write, "ANALYZE; PRAGMA optimize;");
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        _tAnalyze += t2 - t1;
        // lf4p: cold builds run with wal_autocheckpoint=0 (no mid-build checkpoint stalls) —
        // flush the accumulated WAL into the main file here so (a) the dbBytes measured after
        // the build describe the REAL database, and (b) the live open that follows is not
        // handed a build-sized -wal to replay. The autocheckpoint restore is belt-and-braces:
        // this cold-build connection does not outlive the build (the manager reopens fresh),
        // but the pragma must not be relied on to die with it. Harmless no-op for MEMORY-
        // journal staging databases and for live connections already at the default.
        Exec(_write, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA wal_autocheckpoint=1000;");
        _tCheckpoint += System.Diagnostics.Stopwatch.GetTimestamp() - t2; // lf4p
    }

    /// <summary>Flushes committed WAL content into the main database before an anchored atomic
    /// install moves that single database file into a worktree destination.</summary>
    internal void CheckpointForAtomicInstall() =>
        Exec(_write, "PRAGMA wal_checkpoint(TRUNCATE);");

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
        foreach (sqlite3_stmt? statement in _symbolInsertStatements)
        {
            if (statement is not null) raw.sqlite3_finalize(statement);
        }
        _baseEdgeInsertCmd?.Dispose();
        _fileInsertCmd?.Dispose();
        _write.Dispose();
    }
}
