using System.Text.Json;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Markdown and SQL participate only in the indexed-text layer. These tests pin cold-build,
/// detect-all, and targeted-refresh parity without implying syntax or compiler semantics.
/// </summary>
public class MarkdownSqlTextIndexingTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("README.md")]
    [InlineData("Database/Schema.SQL")]
    public void WatcherRecognizesMarkdownAndSqlInputs(string path)
        => Assert.True(WorkspaceWatcher.IsWatchedFile(path));

    [Fact]
    public void ColdBuildIndexesMarkdownAndSqlAsTextOnlyLanguages()
    {
        string root = Directory.CreateTempSubdirectory("codenav-md-sql-cold").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            Directory.CreateDirectory(Path.Combine(root, "database"));
            File.WriteAllText(Path.Combine(root, "docs", "Guide.MD"),
                "# Guide\nPhoenixMarkdownIndexMarker describes the deployment.\n");
            File.WriteAllText(Path.Combine(root, "database", "schema.sql"),
                "CREATE TABLE PhoenixSqlIndexMarker (Id INTEGER PRIMARY KEY);\n");

            ScanResult scan = WorkspaceScanner.Scan(root);
            Assert.Equal("docs/Guide.MD", Assert.Single(scan.MarkdownFiles).RelPath);
            Assert.Equal("database/schema.sql", Assert.Single(scan.SqlFiles).RelPath);

            var liveProgress = new BuildProgress();
            var progress = new List<string>();
            BuildResult build = IndexBuilder.BuildWithSourceBatchSizeForTest(
                root,
                sourceWriteBatchSize: 1,
                progress: progress.Add,
                liveProgress: liveProgress);
            Assert.Equal(2, build.OtherFiles);
            IndexProgress completed = liveProgress.Snapshot();
            Assert.Equal("finalizing", completed.Phase);
            Assert.Equal(2, completed.FilesTotal);
            Assert.Equal(2, completed.FilesIndexed);
            Assert.True(completed.BytesRead > 0);
            Assert.Contains(progress, message => message.Contains(
                "2/2 Markdown/SQL files in 2 text writer batches",
                StringComparison.Ordinal));

            using var queries = new IndexQueries(IndexBuilder.DefaultDbPath(root));
            FileHit markdown = Assert.Single(queries.FindFiles("*.md", 10));
            FileHit sql = Assert.Single(queries.FindFiles("*.sql", 10));
            Assert.Equal("md", markdown.Language);
            Assert.Equal("sql", sql.Language);

            Assert.Single(queries.SearchText(
                "PhoenixMarkdownIndexMarker",
                10,
                new IndexQueries.TextFilter(Lang: "md")));
            Assert.Empty(queries.SearchText(
                "PhoenixMarkdownIndexMarker",
                10,
                new IndexQueries.TextFilter(Lang: "sql")));
            Assert.Single(queries.SearchText(
                "PhoenixSqlIndexMarker",
                10,
                new IndexQueries.TextFilter(Lang: "sql")));

            RegexSearchResult regex = queries.SearchRegex(
                "CREATE\\s+TABLE",
                new IndexQueries.TextFilter(Lang: "sql"),
                maxCandidateFiles: 20,
                offset: 0,
                limit: 10,
                ctxBefore: 0,
                ctxAfter: 0);
            Assert.Equal("database/schema.sql", Assert.Single(regex.Hits).FilePath);

            Assert.Contains(
                "PhoenixMarkdownIndexMarker",
                queries.ContentByPath("docs/Guide.MD"));
            Assert.Contains(
                "PhoenixSqlIndexMarker",
                queries.ContentByPath("database/schema.sql"));
            OverviewStats overview = queries.Overview();
            Assert.Equal(1, overview.MarkdownFiles);
            Assert.Equal(1, overview.SqlFiles);
            Assert.Equal(0, overview.Symbols);

            using var manager = new IndexManager(root, IndexBuilder.DefaultDbPath(root));
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);
            JsonElement markdownSymbols = Parse(tools.SearchSymbol(
                "Anything",
                pathGlob: "docs/**"));
            Assert.Equal("unsupported_language",
                markdownSymbols.GetProperty("error").GetString());
            Assert.Equal("md", markdownSymbols.GetProperty("language").GetString());
            JsonElement sqlSymbols = Parse(tools.SearchSymbol(
                "Anything",
                pathGlob: "database/**"));
            Assert.Equal("unsupported_language",
                sqlSymbols.GetProperty("error").GetString());
            Assert.Equal("sql", sqlSymbols.GetProperty("language").GetString());
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FilteredTokenCandidatesCannotBeCrowdedOutBeforeTheLanguageFilter()
    {
        string root = Directory.CreateTempSubdirectory("codenav-md-filter-window").FullName;
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var store = new IndexStore(dbPath, createNew: true, privateStaging: true))
            {
                using var tx = store.BeginTransaction();
                const string content = "PhoenixFilteredWindowMarker\n";
                for (int i = 0; i < 2001; i++)
                {
                    long id = store.InsertFile(
                        tx,
                        $"src/Crowd{i:D4}.cs",
                        content.Length,
                        mtimeTicks: 1,
                        hash: (ulong)(i + 1),
                        lang: "cs",
                        lineCount: 1,
                        isGenerated: false,
                        hasTestAttrs: false);
                    store.InsertContent(tx, id, content);
                }
                long markdownId = store.InsertFile(
                    tx,
                    "docs/Target.md",
                    content.Length,
                    mtimeTicks: 1,
                    hash: 10_000,
                    lang: "md",
                    lineCount: 1,
                    isGenerated: false,
                    hasTestAttrs: false);
                store.InsertContent(tx, markdownId, content);
                tx.Commit();
            }

            using var queries = new IndexQueries(dbPath);
            TextSearchResult result = queries.SearchTextGraded(
                "PhoenixFilteredWindowMarker",
                limit: 5,
                filter: new IndexQueries.TextFilter(Lang: "md"),
                maxCandidateFiles: 1,
                offset: 0,
                partialsMode: "never");
            Assert.Equal("docs/Target.md", Assert.Single(result.Hits).FilePath);
            Assert.Equal(1, result.CandidateFilesScanned);
            Assert.Equal(1, result.CandidateFilesAtLeast);
            Assert.False(result.CandidateFilesTruncated);
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TokenCandidateCapIsObservableInTheMcpEnvelope()
    {
        string root = Directory.CreateTempSubdirectory("codenav-md-cap").FullName;
        try
        {
            string docs = Path.Combine(root, "docs");
            Directory.CreateDirectory(docs);
            for (int i = 0; i < 301; i++)
            {
                File.WriteAllText(
                    Path.Combine(docs, $"Cap{i:D3}.md"),
                    $"PhoenixMarkdownCandidateCapMarker {i}\n");
            }
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var manager = new IndexManager(root, dbPath);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.IsQueryable, 20_000));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(manager, semantic);
            JsonElement response = Parse(tools.SearchText(
                "PhoenixMarkdownCandidateCapMarker",
                lang: "md",
                limit: 5));
            Assert.Equal(300, response.GetProperty("filesScanned").GetInt32());
            Assert.False(response.TryGetProperty("filesTotal", out _));
            Assert.Equal(301, response.GetProperty("filesAtLeast").GetInt32());
            Assert.True(response.GetProperty("budgetHit").GetBoolean());
            Assert.True(response.GetProperty("countsAreLowerBounds").GetBoolean());
            Assert.True(response.GetProperty("partial").GetBoolean());
            Assert.Equal("candidate_file_cap",
                response.GetProperty("partialReason").GetString());
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ScannerExcludesBeadsMarkdownAndSqlBookkeeping()
    {
        string root = Directory.CreateTempSubdirectory("codenav-md-beads").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".beads"));
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, ".beads", "README.md"), "tracker notes\n");
            File.WriteAllText(Path.Combine(root, ".beads", "state.sql"), "select 1;\n");
            File.WriteAllText(Path.Combine(root, "docs", "Public.md"), "public docs\n");

            ScanResult scan = WorkspaceScanner.Scan(root);
            Assert.Equal("docs/Public.md", Assert.Single(scan.MarkdownFiles).RelPath);
            Assert.Empty(scan.SqlFiles);
            Assert.True(WorkspaceScanner.IsExcludedPath(".beads/README.md"));
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeltaRefreshCreatesUpdatesDeletesAndDetectsMarkdownAndSql()
    {
        string root = Directory.CreateTempSubdirectory("codenav-md-sql-delta").FullName;
        try
        {
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var store = new IndexStore(dbPath, createNew: false);

            const string markdownPath = "Notes.md";
            const string sqlPath = "db/query.sql";
            Directory.CreateDirectory(Path.Combine(root, "db"));
            File.WriteAllText(Path.Combine(root, markdownPath), "PhoenixMarkdownDeltaOld\n");
            File.WriteAllText(Path.Combine(root, "db", "query.sql"), "SELECT 'PhoenixSqlDeltaOld';\n");

            RefreshResult refresh =
                DeltaRefresher.Refresh(store, root, new[] { markdownPath, sqlPath });
            Assert.Equal(2, refresh.AddedFiles);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Equal("md", queries.FileByPath(markdownPath)!.Language);
                Assert.Equal("sql", queries.FileByPath(sqlPath)!.Language);
                Assert.Single(queries.SearchText("PhoenixMarkdownDeltaOld", 5));
                Assert.Single(queries.SearchText("PhoenixSqlDeltaOld", 5));
            }

            File.WriteAllText(Path.Combine(root, markdownPath), "PhoenixMarkdownDeltaNew\n");
            File.WriteAllText(Path.Combine(root, "db", "query.sql"), "SELECT 'PhoenixSqlDeltaNew';\n");
            refresh = DeltaRefresher.Refresh(store, root, new[] { markdownPath, sqlPath });
            Assert.Equal(2, refresh.ChangedFiles);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Empty(queries.SearchText("PhoenixMarkdownDeltaOld", 5));
                Assert.Empty(queries.SearchText("PhoenixSqlDeltaOld", 5));
                Assert.Single(queries.SearchText("PhoenixMarkdownDeltaNew", 5));
                Assert.Single(queries.SearchText("PhoenixSqlDeltaNew", 5));
            }

            File.Delete(Path.Combine(root, markdownPath));
            File.Delete(Path.Combine(root, "db", "query.sql"));
            refresh = DeltaRefresher.Refresh(store, root, new[] { markdownPath, sqlPath });
            Assert.Equal(2, refresh.DeletedFiles);
            using (var queries = new IndexQueries(dbPath))
            {
                Assert.Null(queries.FileByPath(markdownPath));
                Assert.Null(queries.FileByPath(sqlPath));
            }

            File.WriteAllText(Path.Combine(root, markdownPath), "PhoenixMarkdownSweepMarker\n");
            File.WriteAllText(Path.Combine(root, "db", "query.sql"), "SELECT 'PhoenixSqlSweepMarker';\n");
            refresh = DeltaRefresher.Refresh(store, root, changedRelPaths: null);
            Assert.Equal(2, refresh.AddedFiles);
            using var finalQueries = new IndexQueries(dbPath);
            Assert.Single(finalQueries.SearchText("PhoenixMarkdownSweepMarker", 5));
            Assert.Single(finalQueries.SearchText("PhoenixSqlSweepMarker", 5));
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
