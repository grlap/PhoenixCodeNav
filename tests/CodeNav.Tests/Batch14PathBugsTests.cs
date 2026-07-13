using CodeNav.Core;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// Path/glob correctness bugs. 6yk: a leading "**/" (any depth, INCLUDING zero) must match root-level
/// files — GlobToLike turned it into "%%/..." which required a leading segment, silently excluding
/// root files. 9h3: refresh_index must normalize the platform separator to the stored forward-slash
/// form, or it refreshes paths as new rows. Backslash is a separator only on Windows; Unix preserves
/// it as a legal filename character.
/// </summary>
public class Batch14PathBugsTests
{
    [Fact]
    public void DoubleStarSlashGlobMatchesRootLevelFiles()
    {
        string root = Directory.CreateTempSubdirectory("codenav-6yk").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 2, seed: 5);
            File.WriteAllText(Path.Combine(root, "RootMarker.cs"), "namespace R { class RootMarkerType { } }");
            string nest = Path.Combine(root, "deep", "nest");
            Directory.CreateDirectory(nest);
            File.WriteAllText(Path.Combine(nest, "NestMarker.cs"), "namespace N { class NestMarkerType { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var q = new IndexQueries(dbPath);
            var all = q.FindFiles("**/*.cs", 5000).Select(f => f.Path).ToList();
            Assert.Contains("RootMarker.cs", all);            // bug 6yk: root file was excluded by %%/
            Assert.Contains("deep/nest/NestMarker.cs", all);  // depth still matches
            Assert.Contains("RootMarker.cs", q.FindFiles("**/RootMarker.cs", 10).Select(f => f.Path));
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }

    // 6yk mirror on the EXCLUDE side: a leading "**/" exclude must drop root-level matches too, or a
    // caller-supplied excludePath="**/gen/**" silently leaks a root-level "gen/..." directory.
    [Fact]
    public void DoubleStarSlashExcludeDropsRootLevelDir()
    {
        string root = Directory.CreateTempSubdirectory("codenav-6yk-ex").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 2, seed: 6);
            Directory.CreateDirectory(Path.Combine(root, "gen"));
            File.WriteAllText(Path.Combine(root, "gen", "GenMarker.cs"), "namespace G { class GenMarkerType { } }");
            Directory.CreateDirectory(Path.Combine(root, "sub", "gen"));
            File.WriteAllText(Path.Combine(root, "sub", "gen", "NestedGen.cs"), "namespace G2 { class NestedGenType { } }");

            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            using var q = new IndexQueries(dbPath);
            var unfiltered = q.FindFiles("**/*.cs", 5000).Select(f => f.Path).ToList();
            Assert.Contains("gen/GenMarker.cs", unfiltered);        // sanity: indexed
            Assert.Contains("sub/gen/NestedGen.cs", unfiltered);

            var filtered = q.FindFiles("**/*.cs", 5000, new[] { "**/gen/**" }).Select(f => f.Path).ToList();
            Assert.DoesNotContain("gen/GenMarker.cs", filtered);       // root-level dir excluded (was leaking)
            Assert.DoesNotContain("sub/gen/NestedGen.cs", filtered);   // nested still excluded
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }

    [Fact]
    public void RefreshIndexWithPlatformPathCreatesNoDuplicateRow()
    {
        string root = Directory.CreateTempSubdirectory("codenav-9h3").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 3, seed: 9);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            var manager = new IndexManager(root, dbPath);
            try
            {
                manager.Start();
                for (int i = 0; i < 100 && !manager.IsQueryable; i++) Thread.Sleep(50);
                Assert.True(manager.IsQueryable);

                string forward;
                long before;
                using (var q = manager.OpenQueries())
                {
                    var guard = q.FindFiles("Guard.cs", 1);
                    if (guard.Count == 0) return; // fixture guard
                    forward = guard[0].Path; // an indexed .cs, forward slashes
                    before = q.Overview().CsFiles;
                }

                var tools = new NavigationTools(manager, new CodeNav.Core.Semantic.SemanticService(manager));
                string platformPath = forward.Replace('/', Path.DirectorySeparatorChar);
                tools.RefreshIndex(paths: platformPath);

                for (int i = 0; i < 200 && manager.Health().PendingChanges > 0; i++) Thread.Sleep(25);
                Thread.Sleep(300); // let the refresh write commit

                long after = manager.OpenQueries().Overview().CsFiles;
                Assert.Equal(before, after); // normalized -> matched the existing row; bug 9h3 would add one
            }
            finally { manager.Dispose(); }
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(root);
            try { Directory.Delete(root, recursive: true); } catch { /* leave temp on Windows lock */ }
        }
    }

    [Fact]
    public void PortablePathDomainHelpersPreserveCanonicalUnixBackslashes()
    {
        const string canonicalProjectDir = "Folder\\Literal";
        Assert.Equal("Folder\\Literal/Child.cs",
            ProjectFileParser.NormalizeRelative(canonicalProjectDir, "Child.cs"));
        Assert.Equal("Folder\\Literal/Sub/Child.cs",
            ProjectFileParser.NormalizeRelative(canonicalProjectDir, "Sub\\Child.cs"));

        Assert.Equal("Sub/Nested",
            SolutionParser.PortableDirectoryName("Sub\\Nested/All.sln"));
        Assert.Equal("Folder/Literal\\Name.cs",
            WorkspacePaths.Normalize("Folder/Literal\\Name.cs", '/'));
        Assert.Equal("/tmp/Twin\\Review",
            WorkspacePaths.NormalizeFullForComparison("/tmp/Twin\\Review/", '/'));
    }

    [Fact]
    public void SqliteDataSourceTreatsConnectionStringDelimitersAsLiteralPathText()
    {
        string root = Directory.CreateTempSubdirectory("codenav-14-sqlite-path").FullName;
        string nonce = Guid.NewGuid().ToString("N");
        string redirectedBuild = Path.GetFullPath($"codenav-redirect-build-{nonce}.db");
        string redirectedCopy = Path.GetFullPath($"codenav-redirect-copy-{nonce}.db");
        string dbPath = Path.Combine(root,
            $"index=literal;Data Source={Path.GetFileName(redirectedBuild)}");
        string copyPath = Path.Combine(root,
            $"copy=literal;Data Source={Path.GetFileName(redirectedCopy)}");
        try
        {
            File.WriteAllText(Path.Combine(root, "Literal.cs"),
                "namespace LiteralPath { public class DelimiterPath14 { } }");

            IndexBuilder.Build(root, dbPath);

            Assert.True(File.Exists(dbPath));
            Assert.False(File.Exists(redirectedBuild));
            using (var queries = new IndexQueries(dbPath))
                Assert.Single(queries.SearchSymbols("DelimiterPath14", "exact", null, 2));

            using (File.Create(copyPath)) { }
            IndexStore.SnapshotToReserved(dbPath, copyPath);

            Assert.True(new FileInfo(copyPath).Length > 0);
            Assert.False(File.Exists(redirectedCopy));
            using var copiedQueries = new IndexQueries(copyPath);
            Assert.Single(copiedQueries.SearchSymbols("DelimiterPath14", "exact", null, 2));
        }
        finally
        {
            // kae review: the pooled readers live on dbPath and copyPath UNDER ROOT — clear
            // root before deleting it. The redirected paths are asserted to never exist, so
            // they own no pools; their file deletes below stay as just-in-case cleanup.
            TestWorkspaceCleanup.ClearIndexPools(root);
            foreach (string path in new[] { redirectedBuild, redirectedCopy })
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(path + "-wal"); } catch { }
                try { File.Delete(path + "-shm"); } catch { }
                try { File.Delete(path + "-journal"); } catch { }
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("a/b/C.cs", "a/**/*.cs", true)]
    [InlineData("a/C.cs", "a/**/*.cs", true)]
    [InlineData("a/b/C.cs", "a/*.cs", false)]
    [InlineData("a/Cx.cs", "a/C?.cs", true)]
    [InlineData("a/C.CS", "a/c.cs", true)]
    public void BoundedMsBuildGlobPreservesNormalWildcardSemantics(string path, string pattern,
        bool expected)
    {
        var budget = new GlobMatchBudget(segmentLimit: 64, operationLimit: 10_000,
            timeLimit: TimeSpan.FromSeconds(5));

        GlobMatchOutcome outcome = MsBuildGlob.Match(path, pattern, ignoreCase: true, budget);

        Assert.Equal(expected ? GlobMatchOutcome.Matched : GlobMatchOutcome.NotMatched, outcome);
        Assert.False(budget.IsExhausted);
    }

    [Fact]
    public void BoundedMsBuildGlobMemoizesConsecutiveAndInterleavedDoubleStars()
    {
        string matchingPath = string.Join('/', Enumerable.Repeat("a", 64)) + "/end.cs";
        string consecutive = string.Join('/', Enumerable.Repeat("**", 64)) + "/end.cs";
        var consecutiveBudget = new GlobMatchBudget(segmentLimit: 256,
            operationLimit: 100_000, timeLimit: TimeSpan.FromSeconds(5));
        Assert.Equal(GlobMatchOutcome.Matched,
            MsBuildGlob.Match(matchingPath, consecutive, ignoreCase: false, consecutiveBudget));
        Assert.False(consecutiveBudget.IsExhausted);

        string interleaved = string.Join('/', Enumerable.Range(0, 32)
            .SelectMany(_ => new[] { "**", "a" })) + "/**/missing.cs";
        var interleavedBudget = new GlobMatchBudget(segmentLimit: 256,
            operationLimit: 100_000, timeLimit: TimeSpan.FromSeconds(5));
        Assert.Equal(GlobMatchOutcome.NotMatched,
            MsBuildGlob.Match(matchingPath, interleaved, ignoreCase: false,
                interleavedBudget));
        Assert.False(interleavedBudget.IsExhausted);
        Assert.InRange(interleavedBudget.Operations, 1, 100_000);
    }

    [Fact]
    public void BoundedMsBuildGlobReportsStickyCumulativeOperationExhaustion()
    {
        var budget = new GlobMatchBudget(segmentLimit: 256, operationLimit: 500,
            timeLimit: TimeSpan.FromSeconds(5));
        Assert.Equal(GlobMatchOutcome.Matched,
            MsBuildGlob.Match("a/first.cs", "a/*.cs", ignoreCase: false, budget));

        string path = string.Join('/', Enumerable.Repeat("a", 64)) + "/end.cs";
        string pattern = string.Join('/', Enumerable.Range(0, 32)
            .SelectMany(_ => new[] { "**", "a" })) + "/**/missing.cs";
        Assert.Equal(GlobMatchOutcome.BudgetExhausted,
            MsBuildGlob.Match(path, pattern, ignoreCase: false, budget));
        Assert.True(budget.IsExhausted);
        Assert.InRange(budget.Operations, 1, budget.OperationLimit);

        Assert.Equal(GlobMatchOutcome.BudgetExhausted,
            MsBuildGlob.Match("a/second.cs", "**/*.cs", ignoreCase: false, budget));
    }

    [Fact]
    public void BoundedMsBuildGlobReportsSegmentAndDeadlineExhaustion()
    {
        var segmentBudget = new GlobMatchBudget(segmentLimit: 3, operationLimit: 10_000,
            timeLimit: TimeSpan.FromSeconds(5));
        Assert.Equal(GlobMatchOutcome.BudgetExhausted,
            MsBuildGlob.Match("a/b/c/file.cs", "**/*.cs", ignoreCase: false, segmentBudget));
        Assert.True(segmentBudget.IsExhausted);

        var expiredBudget = new GlobMatchBudget(segmentLimit: 64, operationLimit: 10_000,
            timeLimit: TimeSpan.Zero);
        Assert.Equal(GlobMatchOutcome.BudgetExhausted,
            MsBuildGlob.Match("a/file.cs", "**/*.cs", ignoreCase: false, expiredBudget));
        Assert.True(expiredBudget.IsExhausted);
    }

    [Fact]
    public void BoundedMsBuildGlobFreezesElapsedStateBetweenCallerCheckpoints()
    {
        var budget = new GlobMatchBudget(segmentLimit: 64, operationLimit: 10_000,
            timeLimit: TimeSpan.FromMilliseconds(250));
        Assert.Equal(GlobMatchOutcome.Matched,
            MsBuildGlob.Match("a/file.cs", "**/*.cs", ignoreCase: false, budget));
        long elapsedAfterMatch = budget.ElapsedMilliseconds;

        Thread.Sleep(300);

        Assert.False(budget.IsExhausted);
        Assert.Equal(elapsedAfterMatch, budget.ElapsedMilliseconds);
        Assert.False(budget.TryContinue());
        Assert.True(budget.IsExhausted);
        Assert.True(budget.ElapsedMilliseconds >= 250);
    }
}
