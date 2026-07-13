using Microsoft.Data.Sqlite;

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
            SqliteConnection.ClearAllPools();
            foreach (var sidecar in new[]
                     { dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
            {
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }

        _write = Open();
        try
        {
            if (createNew) AfterOpenBeforeCreateSchemaForTest?.Invoke(_dbPath);
            if (createNew) CreateSchema();
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

    private SqliteConnection Open()
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
            // connection on failure so its OS handle is released (ClearAllPools can then free it,
            // letting a stale/corrupt-index rebuild delete the file rather than fail on a lock).
            Exec(conn, _privateStaging
                ? "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;"
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

            -- v15: physical-project / compilation-variant identity. The legacy project-level
            -- tables remain compatibility projections for nonsemantic tools during migration.
            CREATE TABLE project_variants(
              id INTEGER PRIMARY KEY,
              project_id INTEGER NOT NULL,
              dimension_key TEXT NOT NULL,
              stable_variant_key TEXT NOT NULL UNIQUE,
              target_framework TEXT NOT NULL,
              configuration TEXT NOT NULL,
              platform TEXT NOT NULL,
              assembly_name TEXT NOT NULL,
              target_name TEXT NOT NULL,
              target_ext TEXT NOT NULL,
              evaluation_complete INTEGER NOT NULL,
              UNIQUE(project_id, dimension_key)
            );
            CREATE INDEX idx_project_variants_project ON project_variants(project_id, dimension_key);

            CREATE TABLE parse_contexts(
              id INTEGER PRIMARY KEY,
              context_key TEXT NOT NULL UNIQUE,
              language_version TEXT NOT NULL,
              preprocessor_symbols TEXT NOT NULL,
              is_complete INTEGER NOT NULL
            );
            CREATE TABLE variant_parse_contexts(
              variant_id INTEGER NOT NULL PRIMARY KEY,
              parse_context_id INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX idx_variant_parse_contexts_context ON variant_parse_contexts(parse_context_id, variant_id);

            CREATE TABLE project_outputs(
              id INTEGER PRIMARY KEY,
              variant_id INTEGER NOT NULL,
              output_path TEXT NOT NULL,
              target_path TEXT NOT NULL,
              condition TEXT,
              evidence TEXT NOT NULL,
              UNIQUE(variant_id, target_path, condition)
            );
            CREATE INDEX idx_project_outputs_target ON project_outputs(target_path, variant_id);

            CREATE TABLE variant_compile_items(
              variant_id INTEGER NOT NULL,
              file_id INTEGER NOT NULL,
              evaluation_status TEXT NOT NULL,
              PRIMARY KEY(variant_id, file_id)
            ) WITHOUT ROWID;
            CREATE INDEX idx_variant_compile_items_file ON variant_compile_items(file_id, variant_id);

            CREATE TABLE variant_structural_inputs(
              variant_id INTEGER NOT NULL,
              file_id INTEGER NOT NULL,
              input_kind TEXT NOT NULL,
              evaluation_status TEXT NOT NULL,
              PRIMARY KEY(variant_id, file_id, input_kind)
            ) WITHOUT ROWID;
            CREATE INDEX idx_variant_structural_inputs_file ON variant_structural_inputs(file_id, variant_id);

            CREATE TABLE variant_fact_coverage(
              variant_id INTEGER PRIMARY KEY,
              parse_context_complete INTEGER NOT NULL,
              compile_ownership_complete INTEGER NOT NULL,
              reference_graph_complete INTEGER NOT NULL,
              reasons TEXT
            );

            CREATE TABLE project_reference_facts(
              id INTEGER PRIMARY KEY,
              from_variant_id INTEGER NOT NULL,
              include_path TEXT NOT NULL,
              requested_framework TEXT,
              condition TEXT,
              evaluation_status TEXT NOT NULL
            );
            CREATE INDEX idx_project_reference_facts_from ON project_reference_facts(from_variant_id, id);

            CREATE TABLE assembly_reference_facts(
              id INTEGER PRIMARY KEY,
              from_variant_id INTEGER NOT NULL,
              include_name TEXT NOT NULL,
              hint_path TEXT,
              condition TEXT,
              evaluation_status TEXT NOT NULL
            );
            CREATE INDEX idx_assembly_reference_facts_from ON assembly_reference_facts(from_variant_id, id);

            CREATE TABLE variant_package_refs(
              id INTEGER PRIMARY KEY,
              variant_id INTEGER NOT NULL,
              package TEXT NOT NULL,
              version TEXT NOT NULL,
              condition TEXT,
              evaluation_status TEXT NOT NULL
            );
            CREATE INDEX idx_variant_package_refs_variant ON variant_package_refs(variant_id, id);

            CREATE TABLE reference_resolutions(
              id INTEGER PRIMARY KEY,
              reference_fact_kind TEXT NOT NULL,
              reference_fact_id INTEGER NOT NULL,
              from_variant_id INTEGER NOT NULL,
              status TEXT NOT NULL,
              is_complete INTEGER NOT NULL,
              selected_candidate_id INTEGER
            );
            CREATE INDEX idx_reference_resolutions_from ON reference_resolutions(from_variant_id, id);

            CREATE TABLE reference_resolution_candidates(
              id INTEGER PRIMARY KEY,
              resolution_id INTEGER NOT NULL,
              target_variant_id INTEGER,
              binary_path TEXT,
              provenance TEXT NOT NULL,
              compatibility TEXT NOT NULL,
              matched_output_path TEXT,
              rank INTEGER NOT NULL
            );
            CREATE INDEX idx_reference_candidates_resolution ON reference_resolution_candidates(resolution_id, id);
            CREATE INDEX idx_reference_candidates_target ON reference_resolution_candidates(target_variant_id, resolution_id);

            CREATE TABLE resolved_reference_edges(
              resolution_id INTEGER NOT NULL,
              from_variant_id INTEGER NOT NULL,
              target_variant_id INTEGER NOT NULL,
              provenance TEXT NOT NULL,
              PRIMARY KEY(from_variant_id, target_variant_id, resolution_id)
            ) WITHOUT ROWID;
            CREATE INDEX idx_resolved_reference_edges_to ON resolved_reference_edges(target_variant_id, from_variant_id);
            CREATE INDEX idx_resolved_reference_edges_from ON resolved_reference_edges(from_variant_id, target_variant_id);

            CREATE TABLE symbol_base_types(
              id INTEGER PRIMARY KEY,
              parse_context_id INTEGER NOT NULL,
              file_id INTEGER NOT NULL,
              declaration_occurrence TEXT NOT NULL,
              ordinal INTEGER NOT NULL,
              raw_type_text TEXT NOT NULL,
              lookup_name TEXT NOT NULL,
              syntactic_arity INTEGER NOT NULL,
              qualifier_text TEXT,
              resolution_kind TEXT NOT NULL,
              scope_evidence TEXT,
              UNIQUE(parse_context_id, file_id, declaration_occurrence, ordinal)
            );
            CREATE INDEX idx_symbol_base_lookup ON symbol_base_types(lookup_name COLLATE BINARY, syntactic_arity, parse_context_id, file_id, id);
            CREATE INDEX idx_symbol_base_unresolved ON symbol_base_types(resolution_kind, parse_context_id, file_id, id);
            CREATE INDEX idx_symbol_base_file ON symbol_base_types(file_id, parse_context_id, id);

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
            VALUES($p, $s, $m, $h, $l, $lc, $g, $t)
            RETURNING id;
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
        // RETURNING makes this ONE statement per row (was INSERT + SELECT last_insert_rowid() —
        // two prepared-statement steps per symbol, ~1.14M steps on a 570k-symbol build).
        cmd.CommandText = """
            INSERT INTO symbols(file_id, parent_id, kind, name, ns, container, signature,
                                accessibility, start_line, end_line, is_partial, arity, attr_markers, modifiers, accessors, declaration_key)
            VALUES($f, $p, $k, $n, $ns, $c, $sig, $acc, $sl, $el, $part, $ar, $attr, $mods, $accs, $decl)
            RETURNING id;
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
        var pMods = cmd.Parameters.Add("$mods", SqliteType.Text);
        var pAccs = cmd.Parameters.Add("$accs", SqliteType.Text);
        var pDecl = cmd.Parameters.Add("$decl", SqliteType.Text);

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
            pMods.Value = (object?)row.Modifiers ?? DBNull.Value;
            pAccs.Value = (object?)row.Accessors ?? DBNull.Value;
            pDecl.Value = row.DeclarationKey ?? "";
            ordinalToId[row.OrdinalInFile] = (long)cmd.ExecuteScalar()!;
        }
    }

    public long InsertProject(SqliteTransaction tx, Discovery.ParsedProject p)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO projects(path, dir, name, style, guid, tfms, is_test, load_status, compile_globs)
            VALUES($p, $d, $n, $st, $g, $tf, $t, $ls, $cg);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", p.RelPath);
        cmd.Parameters.AddWithValue("$d",
            WorkspacePaths.ToGitPath(Path.GetDirectoryName(p.RelPath) ?? ""));
        cmd.Parameters.AddWithValue("$n", p.Name);
        cmd.Parameters.AddWithValue("$st", p.Style);
        cmd.Parameters.AddWithValue("$g", (object?)p.Guid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tf", p.TargetFrameworks);
        cmd.Parameters.AddWithValue("$t", p.IsTest ? 1 : 0);
        cmd.Parameters.AddWithValue("$ls", p.LoadStatus);
        // Include/Remove globs — or an SDK project that OPTS OUT of default items (review 5a: its dir
        // must not act as an incremental glob root) — make ownership non-derivable from the dir
        // prefix alone; the incremental added-file attribution falls back to the full rebuild.
        cmd.Parameters.AddWithValue("$cg",
            p.CompileIncludeGlobs is { Count: > 0 } || p.CompileRemoveGlobs is { Count: > 0 } ||
            (p.Style == "sdk" && !p.DefaultCompileItems) ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    public long UpsertProjectVariant(SqliteTransaction tx, long projectId, string dimensionKey,
        string stableVariantKey, string targetFramework, string configuration, string platform,
        string assemblyName, string targetName, string targetExt, bool evaluationComplete)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO project_variants(project_id, dimension_key, stable_variant_key,
              target_framework, configuration, platform, assembly_name, target_name, target_ext,
              evaluation_complete)
            VALUES($p,$d,$s,$tf,$c,$pl,$a,$tn,$te,$ok)
            ON CONFLICT(project_id, dimension_key) DO UPDATE SET
              stable_variant_key=excluded.stable_variant_key,
              target_framework=excluded.target_framework,
              configuration=excluded.configuration,
              platform=excluded.platform,
              assembly_name=excluded.assembly_name,
              target_name=excluded.target_name,
              target_ext=excluded.target_ext,
              evaluation_complete=excluded.evaluation_complete
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$d", dimensionKey);
        cmd.Parameters.AddWithValue("$s", stableVariantKey);
        cmd.Parameters.AddWithValue("$tf", targetFramework);
        cmd.Parameters.AddWithValue("$c", configuration);
        cmd.Parameters.AddWithValue("$pl", platform);
        cmd.Parameters.AddWithValue("$a", assemblyName);
        cmd.Parameters.AddWithValue("$tn", targetName);
        cmd.Parameters.AddWithValue("$te", targetExt);
        cmd.Parameters.AddWithValue("$ok", evaluationComplete ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    public long UpsertParseContext(SqliteTransaction tx, string contextKey, string languageVersion,
        IEnumerable<string> preprocessorSymbols, bool isComplete)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO parse_contexts(context_key, language_version, preprocessor_symbols, is_complete)
            VALUES($k,$l,$s,$ok)
            ON CONFLICT(context_key) DO UPDATE SET
              language_version=excluded.language_version,
              preprocessor_symbols=excluded.preprocessor_symbols,
              is_complete=excluded.is_complete
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$k", contextKey);
        cmd.Parameters.AddWithValue("$l", languageVersion);
        cmd.Parameters.AddWithValue("$s", string.Join(';', preprocessorSymbols
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)));
        cmd.Parameters.AddWithValue("$ok", isComplete ? 1 : 0);
        return (long)cmd.ExecuteScalar()!;
    }

    public void SetVariantParseContext(SqliteTransaction tx, long variantId, long parseContextId) =>
        ExecTx(tx, "INSERT OR REPLACE INTO variant_parse_contexts(variant_id, parse_context_id) VALUES($v,$c)",
            ("$v", variantId), ("$c", parseContextId));

    public void InsertVariantOutput(SqliteTransaction tx, long variantId, string outputPath,
        string targetPath, string? condition, string evidence) =>
        ExecTx(tx, "INSERT OR IGNORE INTO project_outputs(variant_id, output_path, target_path, condition, evidence) VALUES($v,$o,$t,$c,$e)",
            ("$v", variantId), ("$o", outputPath), ("$t", targetPath),
            ("$c", (object?)condition ?? DBNull.Value), ("$e", evidence));

    public void InsertVariantCompileItem(SqliteTransaction tx, long variantId, long fileId,
        string evaluationStatus) =>
        ExecTx(tx, "INSERT OR REPLACE INTO variant_compile_items(variant_id, file_id, evaluation_status) VALUES($v,$f,$s)",
            ("$v", variantId), ("$f", fileId), ("$s", evaluationStatus));

    public void CopyProjectCompileItemsToVariant(SqliteTransaction tx, long projectId, long variantId,
        string evaluationStatus) =>
        ExecTx(tx, "INSERT OR REPLACE INTO variant_compile_items(variant_id,file_id,evaluation_status) " +
            "SELECT $v,file_id,$s FROM compile_items WHERE project_id=$p",
            ("$v", variantId), ("$s", evaluationStatus), ("$p", projectId));

    public void InsertVariantCompileItemsForProject(SqliteTransaction tx, long projectId, long fileId,
        string evaluationStatus) =>
        ExecTx(tx, "INSERT OR REPLACE INTO variant_compile_items(variant_id,file_id,evaluation_status) " +
            "SELECT id,$f,$s FROM project_variants WHERE project_id=$p",
            ("$f", fileId), ("$s", evaluationStatus), ("$p", projectId));

    public void InsertVariantStructuralInput(SqliteTransaction tx, long variantId, long fileId,
        string inputKind, string evaluationStatus) =>
        ExecTx(tx, "INSERT OR REPLACE INTO variant_structural_inputs(variant_id, file_id, input_kind, evaluation_status) VALUES($v,$f,$k,$s)",
            ("$v", variantId), ("$f", fileId), ("$k", inputKind), ("$s", evaluationStatus));

    public void SetVariantFactCoverage(SqliteTransaction tx, long variantId, bool parseComplete,
        bool ownershipComplete, bool graphComplete, IEnumerable<string> reasons) =>
        ExecTx(tx, "INSERT OR REPLACE INTO variant_fact_coverage(variant_id, parse_context_complete, compile_ownership_complete, reference_graph_complete, reasons) VALUES($v,$p,$o,$g,$r)",
            ("$v", variantId), ("$p", parseComplete ? 1 : 0), ("$o", ownershipComplete ? 1 : 0),
            ("$g", graphComplete ? 1 : 0), ("$r", string.Join(';', reasons.Distinct(StringComparer.Ordinal))));

    public long InsertProjectReferenceFact(SqliteTransaction tx, long fromVariantId, string includePath,
        string? requestedFramework, string? condition, string evaluationStatus) =>
        InsertReturningId(tx,
            "INSERT INTO project_reference_facts(from_variant_id, include_path, requested_framework, condition, evaluation_status) VALUES($v,$i,$tf,$c,$s) RETURNING id",
            ("$v", fromVariantId), ("$i", includePath), ("$tf", (object?)requestedFramework ?? DBNull.Value),
            ("$c", (object?)condition ?? DBNull.Value), ("$s", evaluationStatus));

    public long InsertAssemblyReferenceFact(SqliteTransaction tx, long fromVariantId, string includeName,
        string? hintPath, string? condition, string evaluationStatus) =>
        InsertReturningId(tx,
            "INSERT INTO assembly_reference_facts(from_variant_id, include_name, hint_path, condition, evaluation_status) VALUES($v,$i,$h,$c,$s) RETURNING id",
            ("$v", fromVariantId), ("$i", includeName), ("$h", (object?)hintPath ?? DBNull.Value),
            ("$c", (object?)condition ?? DBNull.Value), ("$s", evaluationStatus));

    public void InsertVariantPackageReference(SqliteTransaction tx, long variantId, string package,
        string version, string? condition, string evaluationStatus) =>
        ExecTx(tx, "INSERT INTO variant_package_refs(variant_id, package, version, condition, evaluation_status) VALUES($v,$p,$ver,$c,$s)",
            ("$v", variantId), ("$p", package), ("$ver", version),
            ("$c", (object?)condition ?? DBNull.Value), ("$s", evaluationStatus));

    public long InsertReferenceResolution(SqliteTransaction tx, string factKind, long factId,
        long fromVariantId, string status, bool isComplete) =>
        InsertReturningId(tx,
            "INSERT INTO reference_resolutions(reference_fact_kind, reference_fact_id, from_variant_id, status, is_complete) VALUES($k,$f,$v,$s,$ok) RETURNING id",
            ("$k", factKind), ("$f", factId), ("$v", fromVariantId), ("$s", status),
            ("$ok", isComplete ? 1 : 0));

    public long InsertReferenceResolutionCandidate(SqliteTransaction tx, long resolutionId,
        long? targetVariantId, string? binaryPath, string provenance, string compatibility,
        string? matchedOutputPath, int rank) =>
        InsertReturningId(tx,
            "INSERT INTO reference_resolution_candidates(resolution_id, target_variant_id, binary_path, provenance, compatibility, matched_output_path, rank) VALUES($r,$t,$b,$p,$c,$m,$rank) RETURNING id",
            ("$r", resolutionId), ("$t", (object?)targetVariantId ?? DBNull.Value),
            ("$b", (object?)binaryPath ?? DBNull.Value), ("$p", provenance), ("$c", compatibility),
            ("$m", (object?)matchedOutputPath ?? DBNull.Value), ("$rank", rank));

    public void SelectReferenceResolutionCandidate(SqliteTransaction tx, long resolutionId,
        long candidateId, long fromVariantId, long targetVariantId, string provenance)
    {
        ExecTx(tx, "UPDATE reference_resolutions SET selected_candidate_id=$c, status='resolved' WHERE id=$r",
            ("$c", candidateId), ("$r", resolutionId));
        ExecTx(tx, "INSERT OR IGNORE INTO resolved_reference_edges(resolution_id, from_variant_id, target_variant_id, provenance) VALUES($r,$f,$t,$p)",
            ("$r", resolutionId), ("$f", fromVariantId), ("$t", targetVariantId), ("$p", provenance));
    }

    public void InsertBaseTypeFact(SqliteTransaction tx, long parseContextId, long fileId,
        string declarationOccurrence, int ordinal, string rawTypeText, string lookupName,
        int syntacticArity, string? qualifierText, string resolutionKind, string? scopeEvidence) =>
        ExecTx(tx, "INSERT OR REPLACE INTO symbol_base_types(parse_context_id, file_id, declaration_occurrence, ordinal, raw_type_text, lookup_name, syntactic_arity, qualifier_text, resolution_kind, scope_evidence) VALUES($c,$f,$d,$o,$raw,$n,$a,$q,$r,$s)",
            ("$c", parseContextId), ("$f", fileId), ("$d", declarationOccurrence), ("$o", ordinal),
            ("$raw", rawTypeText), ("$n", lookupName), ("$a", syntacticArity),
            ("$q", (object?)qualifierText ?? DBNull.Value), ("$r", resolutionKind),
            ("$s", (object?)scopeEvidence ?? DBNull.Value));

    public void DeleteBaseTypeFactsForFile(SqliteTransaction tx, long fileId) =>
        ExecTx(tx, "DELETE FROM symbol_base_types WHERE file_id=$f", ("$f", fileId));

    public void ClearVariantChildren(SqliteTransaction tx, long variantId)
    {
        ExecTx(tx, "DELETE FROM resolved_reference_edges WHERE from_variant_id=$v OR target_variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM reference_resolution_candidates WHERE resolution_id IN (SELECT id FROM reference_resolutions WHERE from_variant_id=$v)", ("$v", variantId));
        ExecTx(tx, "DELETE FROM reference_resolutions WHERE from_variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM project_reference_facts WHERE from_variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM assembly_reference_facts WHERE from_variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM variant_package_refs WHERE variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM project_outputs WHERE variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM variant_compile_items WHERE variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM variant_structural_inputs WHERE variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM variant_fact_coverage WHERE variant_id=$v", ("$v", variantId));
        ExecTx(tx, "DELETE FROM variant_parse_contexts WHERE variant_id=$v", ("$v", variantId));
    }

    private long InsertReturningId(SqliteTransaction tx, string sql, params (string, object)[] args)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
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
        // NAME-level uniformity (review): a same-AssemblyName pair is ONE assembly to every
        // name-keyed reader — but per-row classification could differ (a referenced twin fails
        // the leaf guard; parse-time R1/R2 can diverge between twins), and AllProjectTestFlags'
        // last-row-wins collapse made the NAME's answer depend on scan order vs which twin
        // carried the incoming edge (review-reproduced: identical workspaces, opposite
        // classification). If ANY row of a name classifies as test, every row of that name does.
        // Single pass is a fixed point: propagation is same-name-only, so the eligible-name set
        // cannot grow (review-verified: a second call returns 0).
        using var uniform = _write.CreateCommand();
        uniform.Transaction = tx;
        uniform.CommandText = """
            UPDATE projects SET is_test = 1
            WHERE is_test = 0
              AND name COLLATE NOCASE IN (SELECT name FROM projects WHERE is_test = 1)
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

    public List<long> GetVariantCompileItemIdsForWrite(SqliteTransaction tx, long variantId)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT file_id FROM variant_compile_items WHERE variant_id=$v ORDER BY file_id";
        cmd.Parameters.AddWithValue("$v", variantId);
        var result = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetInt64(0));
        return result;
    }

    public List<(long Id, string LanguageVersion, List<string> Symbols)> ParseContextsForFileForWrite(
        SqliteTransaction tx, long fileId)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT DISTINCT pc.id,pc.language_version,pc.preprocessor_symbols
            FROM variant_compile_items vci
            JOIN variant_parse_contexts vpc ON vpc.variant_id=vci.variant_id
            JOIN parse_contexts pc ON pc.id=vpc.parse_context_id
            WHERE vci.file_id=$f
            ORDER BY pc.id
            """;
        cmd.Parameters.AddWithValue("$f", fileId);
        var result = new List<(long, string, List<string>)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()));
        }
        return result;
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
        ExecTx(tx, "DELETE FROM symbol_base_types WHERE file_id=$id", ("$id", fileId));
        ExecTx(tx, "DELETE FROM variant_compile_items WHERE file_id=$id", ("$id", fileId));
        ExecTx(tx, "DELETE FROM variant_structural_inputs WHERE file_id=$id", ("$id", fileId));
        ExecTx(tx, "DELETE FROM compile_items WHERE file_id=$id", ("$id", fileId));
        ExecTx(tx, "DELETE FROM files WHERE id=$id", ("$id", fileId));
    }

    public void ClearProjectData(SqliteTransaction tx)
    {
        foreach (var table in new[]
                 {
                     "resolved_reference_edges", "reference_resolution_candidates", "reference_resolutions",
                     "project_reference_facts", "assembly_reference_facts", "variant_package_refs",
                     "variant_structural_inputs", "variant_fact_coverage", "variant_compile_items",
                     "project_outputs", "variant_parse_contexts", "project_variants", "parse_contexts",
                     "symbol_base_types", "solution_projects", "solutions", "compile_items",
                     "package_refs", "project_refs", "projects"
                 })
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
    public bool HasNonTrivialCompileShapes()
    {
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM projects WHERE style = 'legacy' OR compile_globs = 1)";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    /// <summary>(id, csproj relPath) of projects that own files by their DIRECTORY prefix alone —
    /// SDK-style or failed-parse, with no Include/Remove globs. Used for the incremental attribution
    /// of a single added .cs file in trivially-shaped workspaces (zki).</summary>
    public List<(long Id, string RelPath)> GlobRootProjects()
    {
        var list = new List<(long, string)>();
        using var cmd = _write.CreateCommand();
        cmd.CommandText = "SELECT id, path FROM projects WHERE style != 'legacy' AND compile_globs = 0";
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
        _write.Dispose();
    }
}
