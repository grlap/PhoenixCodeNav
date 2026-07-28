using CodeNav.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 68 (lf4p): file persistence client-assigns ids and uses cached raw SQLite statements
/// for every exact batch size 1..32. These tests pin the statement-width distribution, all file
/// facets, rollback gaps, and reset/reuse after a constraint failure.
/// </summary>
public sealed class Batch68RawFileBatchingTests
{
    [Fact]
    public void ExactRawFileBatchesPreserveRowsFacetsAndConsecutiveIds()
    {
        string root = Directory.CreateTempSubdirectory("codenav-68-file-batches").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = new IndexStore(dbPath, createNew: true, privateStaging: true);
            using (SqliteTransaction tx = store.BeginTransaction())
            {
                for (int size = 1; size <= 32; size++)
                    store.InsertFiles(tx, Rows(size, $"width-{size}"));
                store.InsertFiles(tx, Rows(65, "chunked"));
                tx.Commit();
            }

            for (int size = 1; size <= 32; size++)
            {
                long expected = size switch
                {
                    1 => 2,  // width-1 plus the 65-row remainder
                    32 => 3, // width-32 plus two chunks from the 65-row batch
                    _ => 1,
                };
                Assert.Equal(expected, store.FileInsertExecutionCountForTest(size));
            }

            using SqliteConnection reader = store.OpenReader();
            Assert.Equal(593, Scalar(reader, "SELECT COUNT(*) FROM files"));
            Assert.Equal(593, Scalar(reader, "SELECT MAX(id) FROM files"));
            Assert.Equal(593, Scalar(reader, "SELECT COUNT(DISTINCT id) FROM files"));

            using SqliteCommand facets = reader.CreateCommand();
            facets.CommandText = """
                SELECT size, mtime_ticks, hash, lang, line_count, is_generated, has_test_attrs
                FROM files
                WHERE path='chunked/檔案-64.cs'
                """;
            using SqliteDataReader row = facets.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal(65, row.GetInt64(0));
            Assert.Equal(1064, row.GetInt64(1));
            Assert.Equal(unchecked((long)(ulong.MaxValue - 64)), row.GetInt64(2));
            Assert.Equal("cs", row.GetString(3));
            Assert.Equal(65, row.GetInt32(4));
            Assert.Equal(1, row.GetInt32(5));
            Assert.Equal(1, row.GetInt32(6));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void FailedAndRolledBackRawFileBatchesResetAndOnlyLeaveIdGaps()
    {
        string root = Directory.CreateTempSubdirectory("codenav-68-file-reset").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = new IndexStore(dbPath, createNew: true, privateStaging: true);
            using (SqliteTransaction seed = store.BeginTransaction())
            {
                Assert.Equal(1, store.InsertFile(seed, "occupied.cs", 1, 1, 1, "cs", 1,
                    false, false));
                seed.Commit();
            }

            using (SqliteTransaction collision = store.BeginTransaction())
            {
                SqliteException error = Assert.Throws<SqliteException>(() =>
                    store.InsertFiles(collision,
                    [
                        Row("occupied.cs", 2),
                        Row("never-written.cs", 3),
                    ]));
                Assert.Equal(19, error.SqliteErrorCode);
                collision.Rollback();
            }

            using (SqliteTransaction rolledBack = store.BeginTransaction())
            {
                Assert.Equal(4, store.InsertFiles(rolledBack,
                [
                    Row("rolled-back-a.cs", 4),
                    Row("rolled-back-b.cs", 5),
                ]));
                rolledBack.Rollback();
            }

            using (SqliteTransaction retry = store.BeginTransaction())
            {
                Assert.Equal(6, store.InsertFiles(retry,
                [
                    Row("kept-a.cs", 6),
                    Row("kept-b.cs", 7),
                ]));
                retry.Commit();
            }

            using SqliteConnection reader = store.OpenReader();
            Assert.Equal(3, Scalar(reader, "SELECT COUNT(*) FROM files"));
            Assert.Equal(7, Scalar(reader, "SELECT MAX(id) FROM files"));
            Assert.Equal(1, Scalar(reader,
                "SELECT COUNT(*) FROM files WHERE id=6 AND path='kept-a.cs'"));
            Assert.Equal(1, Scalar(reader,
                "SELECT COUNT(*) FROM files WHERE id=7 AND path='kept-b.cs'"));
            Assert.Equal(1, store.FileInsertExecutionCountForTest(1));
            Assert.Equal(2, store.FileInsertExecutionCountForTest(2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void RawFileBatchesKeepManagedAllocationBelowPerRowParameterChurn()
    {
        string root = Directory.CreateTempSubdirectory("codenav-68-file-allocation").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = new IndexStore(dbPath, createNew: true, privateStaging: true);
            using (SqliteTransaction warmup = store.BeginTransaction())
            {
                store.InsertFiles(warmup, Rows(32, "warmup"));
                warmup.Commit();
            }

            List<BulkFileRow> rows = Rows(4096, "measured");
            using SqliteTransaction measured = store.BeginTransaction();
            long before = GC.GetAllocatedBytesForCurrentThread();
            store.InsertFiles(measured, rows);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            measured.Commit();

            Assert.True(allocated < 1_000_000,
                $"Raw insertion allocated {allocated:N0} managed bytes for 4,096 files.");
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static List<BulkFileRow> Rows(int count, string prefix)
    {
        var rows = new List<BulkFileRow>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new BulkFileRow(
                $"{prefix}/檔案-{i}.cs",
                i + 1,
                1000 + i,
                ulong.MaxValue - (ulong)i,
                "cs",
                i + 1,
                IsGenerated: i % 2 == 0,
                HasTestAttrs: i % 3 == 1));
        }
        return rows;
    }

    private static BulkFileRow Row(string path, int value) =>
        new(path, value, value, (ulong)value, "cs", value,
            IsGenerated: false, HasTestAttrs: false);

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
