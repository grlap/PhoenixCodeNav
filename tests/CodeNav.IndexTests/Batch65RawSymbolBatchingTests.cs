using CodeNav.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 65 (lf4p): symbol persistence uses cached raw SQLite statements for every exact
/// batch size 1..32. The regression pin is deliberately structural: it exercises every
/// statement width, a multi-chunk file, parent-id wiring, nullable/text facets, and base edges.
/// Restoring the managed named-parameter path or the old 32+single remainder policy makes the
/// execution histogram fail even when the final row count happens to stay correct.
/// </summary>
public class Batch65RawSymbolBatchingTests
{
    [Fact]
    public void ExactRawBatchesPreserveRowsParentsFacetsAndBaseEdges()
    {
        string root = Directory.CreateTempSubdirectory("codenav-65-raw-symbols").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = new IndexStore(dbPath, createNew: true, privateStaging: true);
            using (SqliteTransaction tx = store.BeginTransaction())
            {
                for (int size = 1; size <= 32; size++)
                    InsertFileWithSymbols(store, tx, size);
                InsertFileWithSymbols(store, tx, 65);
                tx.Commit();
            }

            for (int size = 1; size <= 32; size++)
            {
                long expected = size switch
                {
                    1 => 2,  // the one-row file plus the 65-row remainder
                    32 => 3, // the 32-row file plus two chunks from the 65-row file
                    _ => 1,
                };
                Assert.Equal(expected, store.SymbolInsertExecutionCountForTest(size));
            }

            using SqliteConnection reader = store.OpenReader();
            Assert.Equal(593, Scalar(reader, "SELECT COUNT(*) FROM symbols"));
            Assert.Equal(33, Scalar(reader,
                "SELECT COUNT(*) FROM symbols WHERE parent_id IS NULL"));
            Assert.Equal(560, Scalar(reader, """
                SELECT COUNT(*)
                FROM symbols child
                JOIN symbols parent ON parent.id = child.parent_id
                WHERE parent.file_id = child.file_id
                  AND parent.name LIKE 'Root_%'
                """));
            Assert.Equal(33, Scalar(reader, """
                SELECT COUNT(*)
                FROM type_base_edges edge
                JOIN symbols derived ON derived.id = edge.derived_symbol_id
                WHERE edge.file_id = derived.file_id
                  AND edge.base_name = 'Base'
                  AND edge.base_arity = 1
                """));

            using (SqliteCommand facets = reader.CreateCommand())
            {
                facets.CommandText = """
                    SELECT ns, container, attr_markers, modifiers, accessors, declaration_key
                    FROM symbols
                    WHERE name = 'M_60_65'
                    """;
                using SqliteDataReader row = facets.ExecuteReader();
                Assert.True(row.Read());
                Assert.True(row.IsDBNull(0));
                Assert.Equal("Root_65", row.GetString(1));
                Assert.Equal("Fact", row.GetString(2));
                Assert.Equal("static", row.GetString(3));
                Assert.Equal("get=public;set=private", row.GetString(4));
                Assert.Equal("", row.GetString(5));
            }

            using (SqliteCommand identity = reader.CreateCommand())
            {
                identity.CommandText = """
                    SELECT ns, declaration_key
                    FROM symbols
                    WHERE name = 'M_61_65'
                    """;
                using SqliteDataReader row = identity.ExecuteReader();
                Assert.True(row.Read());
                Assert.Equal("N", row.GetString(0));
                Assert.Equal("D61", row.GetString(1));
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void FailedRawStepIsResetBeforeTheStatementIsReused()
    {
        string root = Directory.CreateTempSubdirectory("codenav-65-raw-reset").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = new IndexStore(dbPath, createNew: true, privateStaging: true);
            long firstFileId;
            long secondFileId;
            using (SqliteTransaction seed = store.BeginTransaction())
            {
                firstFileId = store.InsertFile(seed, "P/First.cs", 1, 1, 1, "cs", 1,
                    false, false);
                secondFileId = store.InsertFile(seed, "P/Second.cs", 1, 1, 2, "cs", 1,
                    false, false);
                using SqliteCommand occupyNextId = seed.Connection!.CreateCommand();
                occupyNextId.Transaction = seed;
                occupyNextId.CommandText = """
                    INSERT INTO symbols(
                      id,file_id,parent_id,kind,name,ns,container,signature,accessibility,
                      start_line,end_line,is_partial,arity,attr_markers,modifiers,accessors,
                      declaration_key)
                    VALUES(1,$file,NULL,'class','Occupied',NULL,NULL,'Occupied','public',
                           1,1,0,0,NULL,NULL,NULL,'Occupied')
                    """;
                occupyNextId.Parameters.AddWithValue("$file", firstFileId);
                occupyNextId.ExecuteNonQuery();
                seed.Commit();
            }

            var replacement = new List<SymbolRow>
            {
                new(0, -1, "class", "Replacement", null, null, "Replacement", "public",
                    1, 1, false, 0, null),
            };
            using (SqliteTransaction collision = store.BeginTransaction())
            {
                SqliteException error = Assert.Throws<SqliteException>(() =>
                    store.InsertSymbols(collision, secondFileId, replacement));
                Assert.Equal(19, error.SqliteErrorCode);
            }

            using (SqliteTransaction retry = store.BeginTransaction())
            {
                store.InsertSymbols(retry, secondFileId, replacement);
                retry.Commit();
            }

            using SqliteConnection reader = store.OpenReader();
            Assert.Equal(1, Scalar(reader,
                "SELECT COUNT(*) FROM symbols WHERE name='Replacement' AND id=2"));
            Assert.Equal(1, store.SymbolInsertExecutionCountForTest(1));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static void InsertFileWithSymbols(
        IndexStore store, SqliteTransaction tx, int size)
    {
        long fileId = store.InsertFile(
            tx,
            $"P/F{size}.cs",
            size: size,
            mtimeTicks: size,
            hash: (ulong)size,
            lang: "cs",
            lineCount: size,
            isGenerated: false,
            hasTestAttrs: false);
        var rows = new List<SymbolRow>(size);
        string rootName = $"Root_{size}";
        for (int ordinal = 0; ordinal < size; ordinal++)
        {
            bool root = ordinal == 0;
            rows.Add(new SymbolRow(
                OrdinalInFile: ordinal,
                ParentOrdinal: root ? -1 : 0,
                Kind: root ? "class" : "method",
                Name: root ? rootName : $"M_{ordinal}_{size}",
                Namespace: root || ordinal % 2 == 0 ? null : "N",
                Container: root ? null : rootName,
                Signature: root ? rootName : $"void M_{ordinal}_{size}()",
                Accessibility: "public",
                StartLine: ordinal + 1,
                EndLine: ordinal + 1,
                IsPartial: root,
                Arity: root ? 1 : 0,
                AttrMarkers: !root && ordinal % 3 == 0 ? "Fact" : null,
                Modifiers: !root && ordinal % 4 == 0 ? "static" : null,
                Accessors: !root && ordinal % 5 == 0
                    ? "get=public;set=private"
                    : null,
                DeclarationKey: !root && ordinal % 2 == 1 ? $"D{ordinal}" : null,
                BaseTypes: ordinal == size - 1
                    ? new[] { new BaseTypeIdentity("Base", 1) }
                    : null));
        }
        store.InsertSymbols(tx, fileId, rows);
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
