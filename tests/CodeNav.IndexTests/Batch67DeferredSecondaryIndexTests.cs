using CodeNav.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Batch 67 (lf4p.2): complete cold builds load rows before creating query-facing secondary
/// indexes. The final schema is unchanged, while direct IndexStore construction stays eager so
/// callers outside IndexBuilder cannot accidentally publish an incomplete database.
/// </summary>
public sealed class Batch67DeferredSecondaryIndexTests
{
    private static readonly string[] ExpectedIndexes =
    [
        "idx_compile_items_file",
        "idx_files_path_nocase",
        "idx_package_refs_project",
        "idx_project_refs_to",
        "idx_projects_name",
        "idx_symbols_file",
        "idx_symbols_kind",
        "idx_symbols_name",
        "idx_type_base_edges_file",
    ];

    [Fact]
    public void BulkBuildLoadsRowsBeforeCreatingTheCompleteSecondaryIndexSet()
    {
        string root = Directory.CreateTempSubdirectory("codenav-67-deferred-indexes").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = IndexStore.CreateForBulkBuild(dbPath);
            using (SqliteTransaction load = store.BeginTransaction())
            {
                long fileId = store.InsertFile(load, "P/A.cs", 10, 1, 1, "cs", 2,
                    isGenerated: false, hasTestAttrs: false);
                store.InsertSymbols(load, fileId,
                [
                    new SymbolRow(0, -1, "class", "A", "P", null, "class A", "public",
                        1, 2, false, 0, null,
                        BaseTypes: [new BaseTypeIdentity("Base", 0)]),
                ]);
                load.Commit();
            }

            Assert.Empty(IndexNames(store));
            Assert.Null(store.GetMeta("schema_version"));

            using (SqliteTransaction finalize = store.BeginTransaction())
            {
                store.CompleteBulkLoad(finalize);
                Assert.Equal(ExpectedIndexes, IndexNames(finalize.Connection!, finalize));
                finalize.Commit();
            }

            Assert.Equal(ExpectedIndexes, IndexNames(store));
            Assert.Equal(1, Scalar(store, "SELECT COUNT(*) FROM symbols WHERE name='A'"));
            Assert.Equal(1, Scalar(store,
                "SELECT COUNT(*) FROM type_base_edges WHERE base_name='Base'"));

            var timings = store.BulkSchemaTimingsMs;
            Assert.True(timings.Schema > 0);
            Assert.True(timings.FileIndexes + timings.ProjectGraphIndexes +
                        timings.SymbolIndexes + timings.BaseEdgeIndexes > 0);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void DirectCreateNewStoreStillExposesAnEagerCompleteSchema()
    {
        string root = Directory.CreateTempSubdirectory("codenav-67-eager-indexes").FullName;
        try
        {
            string dbPath = Path.Combine(root, ".codenav", "index.db");
            using var store = new IndexStore(dbPath, createNew: true, privateStaging: true);

            Assert.Equal(ExpectedIndexes, IndexNames(store));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void IndexBuilderPublishesIndexesAndReportsTheirBuildCost()
    {
        string root = Directory.CreateTempSubdirectory("codenav-67-builder-indexes").FullName;
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
            File.WriteAllText(Path.Combine(project, "A.cs"),
                "namespace P; public class A : Base { } public class Base { }");

            var progress = new List<string>();
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath, progress.Add);

            string writerSplit = Assert.Single(progress,
                line => line.StartsWith("Writer split (lf4p):", StringComparison.Ordinal));
            Assert.Contains("schema ", writerSplit, StringComparison.Ordinal);
            Assert.Contains("secondary-indexes ", writerSplit, StringComparison.Ordinal);
            Assert.Contains("project-graph ", writerSplit, StringComparison.Ordinal);
            Assert.Contains("projects ", writerSplit, StringComparison.Ordinal);
            Assert.Contains("compile-items ", writerSplit, StringComparison.Ordinal);

            using var store = new IndexStore(dbPath, createNew: false);
            Assert.Equal(ExpectedIndexes, IndexNames(store));
            Assert.Equal(IndexBuilder.SchemaVersion, store.GetMeta("schema_version"));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static string[] IndexNames(IndexStore store)
    {
        using SqliteConnection reader = store.OpenReader();
        return IndexNames(reader, tx: null);
    }

    private static string[] IndexNames(SqliteConnection connection, SqliteTransaction? tx)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type='index' AND name LIKE 'idx_%'
            ORDER BY name
            """;
        using SqliteDataReader rows = command.ExecuteReader();
        var names = new List<string>();
        while (rows.Read()) names.Add(rows.GetString(0));
        return names.ToArray();
    }

    private static long Scalar(IndexStore store, string sql)
    {
        using SqliteConnection reader = store.OpenReader();
        using SqliteCommand command = reader.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
