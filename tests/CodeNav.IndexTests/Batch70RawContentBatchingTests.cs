using System.Text.RegularExpressions;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 70 (lf4p.6/.10/.11/.13): cold C# content rows reuse the file batch's contiguous ids and
/// cached raw exact-size statements; FTS rebuilds once from complete external content. The C#
/// producer handoff remains bounded without async ThreadPool starvation, and cold parsing starts
/// the largest files first to overlap long Roslyn parses with ordinary work. Live/eager callers
/// retain transactional content+FTS insertion.
/// </summary>
public sealed class Batch70RawContentBatchingTests
{
    [Fact]
    public void ExactRawContentBatchesPreserveUnicodeAttributionAndFtsRows()
    {
        string root = Directory.CreateTempSubdirectory("codenav-70-content-batches").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = new IndexStore(dbPath, createNew: true, privateStaging: true);
            using (SqliteTransaction tx = store.BeginTransaction())
            {
                for (int size = 1; size <= 32; size++)
                {
                    long firstId = store.InsertFiles(tx, FileRows(size, $"width-{size}"));
                    store.InsertContents(tx, firstId, Contents(size, $"width{size}"));
                }

                long chunkedFirst = store.InsertFiles(tx, FileRows(65, "chunked"));
                store.InsertContents(tx, chunkedFirst, Contents(65, "chunked"));
                tx.Commit();
            }

            for (int size = 1; size <= 32; size++)
            {
                long expected = size switch
                {
                    1 => 2,
                    32 => 3,
                    _ => 1,
                };
                Assert.Equal(expected, store.ContentBatchInsertExecutionCountForTest(size));
                Assert.Equal(expected, store.FtsBatchInsertExecutionCountForTest(size));
            }

            using SqliteConnection reader = store.OpenReader();
            Assert.Equal(593, Scalar(reader, "SELECT COUNT(*) FROM file_contents"));
            Assert.Equal(593, Scalar(reader, "SELECT COUNT(*) FROM fts_content"));

            using SqliteCommand attributed = reader.CreateCommand();
            attributed.CommandText = """
                SELECT fc.content
                FROM files f
                JOIN file_contents fc ON fc.file_id=f.id
                WHERE f.path='chunked/檔案-64.cs'
                """;
            Assert.Equal("chunked64 漢字 rocket-🚀", attributed.ExecuteScalar());

            using SqliteCommand fts = reader.CreateCommand();
            fts.CommandText = """
                SELECT COUNT(*)
                FROM fts_content
                WHERE rowid=(SELECT id FROM files WHERE path='chunked/檔案-64.cs')
                  AND fts_content MATCH 'chunked64'
                """;
            Assert.Equal(1L, fts.ExecuteScalar());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void FailedRawContentBatchResetsBothCachedStatementsForReuse()
    {
        string root = Directory.CreateTempSubdirectory("codenav-70-content-reset").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = new IndexStore(dbPath, createNew: true, privateStaging: true);
            long firstId;
            using (SqliteTransaction seed = store.BeginTransaction())
            {
                firstId = store.InsertFiles(seed, FileRows(4, "files"));
                store.InsertContents(seed, firstId, ["seed-one", "seed-two"]);
                seed.Commit();
            }

            using (SqliteTransaction collision = store.BeginTransaction())
            {
                SqliteException error = Assert.Throws<SqliteException>(() =>
                    store.InsertContents(collision, firstId, ["duplicate-one", "duplicate-two"]));
                Assert.Equal(19, error.SqliteErrorCode);
                collision.Rollback();
            }

            using (SqliteTransaction retry = store.BeginTransaction())
            {
                store.InsertContents(retry, firstId + 2, ["kept-三", "kept-four"]);
                retry.Commit();
            }

            using SqliteConnection reader = store.OpenReader();
            Assert.Equal(4, Scalar(reader, "SELECT COUNT(*) FROM file_contents"));
            Assert.Equal(4, Scalar(reader, "SELECT COUNT(*) FROM fts_content"));
            Assert.Equal(2, store.ContentBatchInsertExecutionCountForTest(2));
            Assert.Equal(2, store.FtsBatchInsertExecutionCountForTest(2));
            Assert.Equal(2, Scalar(reader,
                "SELECT COUNT(*) FROM fts_content WHERE fts_content MATCH 'kept'"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void IndexBuilderUsesMultiRowContentAndFtsStatementsWithRelationalParity()
    {
        string root = Directory.CreateTempSubdirectory("codenav-70-builder-content").FullName;
        try
        {
            string project = Path.Combine(root, "P");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            const int sourceCount = 257;
            for (int i = 0; i < sourceCount; i++)
            {
                File.WriteAllText(Path.Combine(project, $"C{i:D3}.cs"),
                    $"namespace P; public class C{i:D3} {{ public string Text => \"batchneedle{i:D3}\"; }}");
            }

            var progress = new List<string>();
            IndexBuilder.BuildWithSourceBatchSizeForTest(root, sourceCount, progress.Add);

            string writerSplit = Assert.Single(progress,
                line => line.StartsWith("Writer split (lf4p):", StringComparison.Ordinal));
            long contentStatements = StatementCount(writerSplit, "content");
            long ftsStatements = StatementCount(writerSplit, "fts");
            long fileStatements = FileStatementCount(writerSplit);
            Assert.InRange(contentStatements, 1, sourceCount - 1);
            Assert.Equal(1, ftsStatements);
            Assert.InRange(fileStatements, 1, sourceCount - 1);

            string dbPath = IndexBuilder.DefaultDbPath(root);
            using var store = new IndexStore(dbPath, createNew: false);
            using SqliteConnection reader = store.OpenReader();
            Assert.Equal(sourceCount, Scalar(reader, """
                SELECT COUNT(*)
                FROM files f
                JOIN file_contents fc ON fc.file_id=f.id
                WHERE f.lang='cs' AND fc.content LIKE '%batchneedle%'
                """));
            Assert.Equal(sourceCount, Scalar(reader, """
                SELECT COUNT(*)
                FROM symbols s
                JOIN files f ON f.id=s.file_id
                WHERE f.lang='cs' AND s.kind='class'
                """));
            Assert.Equal(sourceCount, Scalar(reader, """
                SELECT COUNT(*)
                FROM compile_items ci
                JOIN files f ON f.id=ci.file_id
                WHERE f.lang='cs'
                """));
            Assert.Equal(1, Scalar(reader,
                "SELECT COUNT(*) FROM fts_content WHERE fts_content MATCH 'batchneedle256'"));
            Assert.Equal(sourceCount, Scalar(reader,
                "SELECT COUNT(*) FROM fts_content WHERE fts_content MATCH '\"public class\"'"));
            using (SqliteCommand integrity = reader.CreateCommand())
            {
                integrity.CommandText =
                    "INSERT INTO fts_content(fts_content, rank) VALUES('integrity-check', 1)";
                integrity.ExecuteNonQuery();
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void DeferredFtsRebuildFailureCanBeRepairedAndRetried()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-70-deferred-fts-retry").FullName;
        try
        {
            string dbPath = Path.Combine(root, "bulk.db");
            using var store = IndexStore.CreateForBulkBuild(dbPath);
            using (SqliteTransaction tx = store.BeginTransaction())
            {
                long firstId = store.InsertFiles(tx, FileRows(2, "deferred"));
                store.InsertContents(tx, firstId,
                    ["alpha public class one", "beta public class two"]);
                Assert.Equal(0, store.FtsInsertStatementCount);

                using (SqliteCommand drop = tx.Connection!.CreateCommand())
                {
                    drop.Transaction = tx;
                    drop.CommandText = "DROP TABLE fts_content";
                    drop.ExecuteNonQuery();
                }
                Assert.Throws<SqliteException>(() => store.CompleteBulkLoad(tx));
                Assert.Equal(0, store.FtsInsertStatementCount);

                using (SqliteCommand recreate = tx.Connection!.CreateCommand())
                {
                    recreate.Transaction = tx;
                    recreate.CommandText = """
                        CREATE VIRTUAL TABLE fts_content USING fts5(
                          content,
                          content='file_contents',
                          content_rowid='file_id',
                          tokenize="unicode61 tokenchars '_'"
                        )
                        """;
                    recreate.ExecuteNonQuery();
                }
                store.CompleteBulkLoad(tx);
                tx.Commit();
            }

            Assert.Equal(1, store.FtsInsertStatementCount);
            using SqliteConnection reader = store.OpenReader();
            Assert.Equal(2, Scalar(reader,
                "SELECT COUNT(*) FROM fts_content WHERE fts_content MATCH '\"public class\"'"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ColdBuildTelemetryCountsSingleRowNonCSharpContentStatements()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-70-non-csharp-content-telemetry").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Notes.md"),
                "markdown-telemetry-needle");
            File.WriteAllText(Path.Combine(root, "Schema.sql"),
                "select 'sql-telemetry-needle';");
            var progress = new List<string>();

            BuildResult result = IndexBuilder.BuildWithSourceBatchSizeForTest(
                root, 32, progress.Add);

            Assert.Equal(2, result.OtherFiles);
            string writerSplit = Assert.Single(progress,
                line => line.StartsWith("Writer split (lf4p):",
                    StringComparison.Ordinal));
            Assert.Equal(2, StatementCount(writerSplit, "content"));
            Assert.Equal(1, StatementCount(writerSplit, "fts"));
            using var queries = new IndexQueries(IndexBuilder.DefaultDbPath(root));
            Assert.Single(queries.SearchText("markdown-telemetry-needle", 2));
            Assert.Single(queries.SearchText("sql-telemetry-needle", 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ColdBuildCompletesAfterItsBoundedProducerQueueSaturates()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-70-bounded-producer").FullName;
        try
        {
            const int sourceCount = 257;
            for (int i = 0; i < sourceCount; i++)
            {
                File.WriteAllText(Path.Combine(root, $"Queue{i:D3}.cs"),
                    $"namespace QueueBuild; public sealed class Queue{i:D3} {{ public string Text => \"queue{i:D3}\"; }}");
            }

            int saturated = 0;
            var hooks = new BuildCaptureTestHooks(
                (workspaceRoot, gitPath, maxBytes) =>
                    GitInfo.ReadBoundedWorkspaceFileResult(
                        workspaceRoot, gitPath, maxBytes),
                CSharpQueueCapacity: 1,
                CSharpQueueSaturated: () => Interlocked.Increment(ref saturated));

            BuildResult result = IndexBuilder.BuildWithSourceBatchSizeForTest(
                root, sourceCount, buildCaptureTestHooks: hooks);

            Assert.Equal(sourceCount, result.CsFiles);
            Assert.True(Volatile.Read(ref saturated) > 0,
                "The capacity-1 producer queue never exercised bounded backpressure.");
            using var queries = new IndexQueries(IndexBuilder.DefaultDbPath(root));
            Assert.Single(queries.SearchSymbols("Queue256", "exact", null, 2));
            Assert.Single(queries.SearchText("queue256", 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ColdBuildPrioritizesLargestCSharpFilesWithAnOrdinalPathTieBreak()
    {
        ScannedFile[] input =
        [
            new("small.cs", 100, 1),
            new("zeta.cs", 900, 2),
            new("largest.cs", 1000, 3),
            new("Alpha.cs", 900, 4),
        ];

        string[] scheduled = IndexBuilder
            .PrioritizeCSharpFilesForColdBuild(input)
            .Select(static file => file.RelPath)
            .ToArray();

        Assert.Equal(["largest.cs", "Alpha.cs", "zeta.cs", "small.cs"], scheduled);
        Assert.Equal(["small.cs", "zeta.cs", "largest.cs", "Alpha.cs"],
            input.Select(static file => file.RelPath));
    }

    private static long StatementCount(string writerSplit, string phase)
    {
        Match match = Regex.Match(writerSplit,
            $@"\b{Regex.Escape(phase)} [0-9.]+s \(([0-9,]+) cold-build statements\)");
        Assert.True(match.Success, $"Missing {phase} statement telemetry: {writerSplit}");
        return long.Parse(match.Groups[1].Value.Replace(",", "", StringComparison.Ordinal));
    }

    private static long FileStatementCount(string writerSplit)
    {
        Match match = Regex.Match(writerSplit,
            @"\bfiles [0-9.]+s \(([0-9,]+) statements\)");
        Assert.True(match.Success, $"Missing file statement telemetry: {writerSplit}");
        return long.Parse(match.Groups[1].Value.Replace(",", "", StringComparison.Ordinal));
    }

    private static List<BulkFileRow> FileRows(int count, string prefix)
    {
        var rows = new List<BulkFileRow>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new BulkFileRow(
                $"{prefix}/檔案-{i}.cs",
                i + 1,
                1000 + i,
                (ulong)(i + 1),
                "cs",
                i + 1,
                IsGenerated: false,
                HasTestAttrs: false));
        }
        return rows;
    }

    private static List<string> Contents(int count, string prefix)
    {
        var contents = new List<string>(count);
        for (int i = 0; i < count; i++)
            contents.Add($"{prefix}{i} 漢字 rocket-🚀");
        return contents;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
