using System.Diagnostics;
using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public sealed class NestedWorktreeExclusionTests
{
    [Theory]
    [InlineData("vendor/.git", true)]
    [InlineData("bin/vendor/.git", false)]
    [InlineData("obj/vendor/.git", false)]
    [InlineData(".git/modules/vendor/.git", false)]
    public async Task RoutineGitMetadataDoesNotSweepOrInvalidateDirectoryKnowledge(string relative, bool directory)
    {
        string root = Directory.CreateTempSubdirectory("pcn-wt-meta").FullName;
        try
        {
            string entry = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            if (directory) Git(Path.GetDirectoryName(entry)!, "init", "-q");
            else File.WriteAllText(entry, "gitdir: unavailable");
            int sweeps = 0, batches = 0;
            using var watcher = new WorkspaceWatcher(root, _ => batches++, () => sweeps++);
            watcher.DisableNativeEventsForTest();
            await watcher.SeedCompletionForTest.WaitAsync(TimeSpan.FromSeconds(20));
            watcher.NotifyPathForTest(entry, WatcherChangeTypes.Changed);
            watcher.NotifyPathForTest(entry, WatcherChangeTypes.Created);
            if (!directory)
            {
                watcher.NotifyPathForTest(entry, WatcherChangeTypes.Deleted);
            }
            watcher.FlushForTest();
            Assert.Equal(0, sweeps);
            watcher.NotifyPathForTest(Path.Combine(Path.GetDirectoryName(entry)!, "LICENSE"), WatcherChangeTypes.Deleted);
            watcher.FlushForTest();
            Assert.Equal(0, sweeps); // Ignored metadata must not mark the directory unseeded.
            Assert.Equal(0, batches);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public void ScanAndBuildExcludeLinkedWorktreesButKeepOrdinaryDirectoriesAndSubmodules()
    {
        WithRepository((root, first, second) =>
        {
            string ordinary = Path.Combine(root, ".worktrees", "ordinary");
            Directory.CreateDirectory(ordinary);
            File.WriteAllText(Path.Combine(ordinary, "Ordinary.cs"), "class Ordinary { }");
            // A real submodule has a .git file too, but no linked-worktree commondir/backlink.
            Git(root, "-c", "protocol.file.allow=always", "submodule", "add", "-q", root, "vendor");
            var scan = WorkspaceScanner.Scan(root);
            Assert.Contains(scan.CsFiles, file => file.RelPath == "A.cs");
            Assert.Contains(scan.CsFiles, file => file.RelPath == ".worktrees/ordinary/Ordinary.cs");
            Assert.Contains(scan.CsFiles, file => file.RelPath == "vendor/A.cs");
            Assert.DoesNotContain(scan.CsFiles, file => file.RelPath.StartsWith(first + "/") || file.RelPath.StartsWith(second + "/"));

            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using (var store = new IndexStore(db, createNew: false))
            {
                Assert.DoesNotContain(store.AllFilesByPath().Keys, path => path.StartsWith(first + "/") || path.StartsWith(second + "/"));
                Assert.Contains("vendor/App.csproj", store.AllFilesByPath().Keys);
            }
            string child = Path.Combine(root, first);
            IndexBuilder.Build(child, IndexBuilder.DefaultDbPath(child));
            using var childStore = new IndexStore(IndexBuilder.DefaultDbPath(child), createNew: false);
            Assert.Contains("A.cs", childStore.AllFilesByPath().Keys);
            Assert.Contains("App.csproj", childStore.AllFilesByPath().Keys);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshRemovesPreviouslyIndexedWorktreeRowsAndCannotReintroduceThem(bool targeted)
    {
        WithRepository((root, first, second) =>
        {
            string pointer = Path.Combine(root, first, ".git");
            File.Move(pointer, pointer + ".saved");
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            File.Move(pointer + ".saved", pointer);
            using var store = new IndexStore(db, createNew: false);
            string[] nested = store.AllFilesByPath().Keys.Where(path => path.StartsWith(first + "/")).ToArray();
            Assert.Contains(first + "/A.cs", nested);
            Assert.Contains(first + "/App.fsproj", nested);
            Assert.Contains(first + "/A.fs", nested);
            var result = DeltaRefresher.Refresh(store, root, targeted ? nested : null);
            Assert.Equal(nested.Length, result.DeletedFiles);
            Assert.DoesNotContain(store.AllFilesByPath().Keys, path => path.StartsWith(first + "/"));
            Assert.DoesNotContain(store.FSharpParsingProjects(), project => project.Path.StartsWith(first + "/"));
            Assert.DoesNotContain(store.FSharpOwnership(), edge => edge.FilePath.StartsWith(first + "/"));
            var retry = DeltaRefresher.Refresh(store, root,
                [first + "/A.cs", first + "/App.csproj", first + "/A.fs", first + "/App.fsproj"]);
            Assert.Equal(0, retry.AddedFiles);
            Assert.Contains("A.cs", store.AllFilesByPath().Keys);
        });
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Changed)]
    public void WatcherSkipsNestedChangesButRecognizesRegistrationTransitions(WatcherChangeTypes transition)
    {
        WithRepository((root, first, second) =>
        {
            var batches = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var sweep = new ManualResetEventSlim();
            using var watcher = new WorkspaceWatcher(root,
                paths => { foreach (string path in paths) batches.Enqueue(path); }, () => sweep.Set());
            // Exercise the same handler synchronously: no negative assertion based on a sleep.
            watcher.NotifyPathForTest(Path.Combine(root, first, "A.cs"), WatcherChangeTypes.Changed);
            watcher.NotifyPathForTest(Path.Combine(root, second, "A.cs"), WatcherChangeTypes.Changed);
            watcher.FlushForTest();
            Assert.Empty(batches);
            Assert.False(sweep.IsSet);
            watcher.NotifyPathForTest(Path.Combine(root, "A.cs"), WatcherChangeTypes.Changed);
            watcher.FlushForTest();
            Assert.Contains("A.cs", batches);
            watcher.NotifyPathForTest(Path.Combine(root, first, ".git"), transition);
            watcher.FlushForTest();
            Assert.True(sweep.IsSet);
        });
    }

    [Theory]
    [InlineData("subtree", false)]
    [InlineData("subtree.cs", false)]
    [InlineData("subtree", true)]
    [InlineData("subtree.cs", true)]
    public void RemovingBoundaryThenMovingUntouchedDescendantRemovesIndexedRows(string directoryName, bool replaceWithDirectory)
    {
        WithRepository((root, first, second) =>
        {
            string child = Path.Combine(root, first);
            string descendant = Path.Combine(child, directoryName);
            Directory.CreateDirectory(descendant);
            File.WriteAllText(Path.Combine(descendant, "Nested.cs"), "class BoundaryRemovalSymbol { }");
            string relativeFile = first + "/" + directoryName + "/Nested.cs";
            string db = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, db);
            using var store = new IndexStore(db, createNew: false);
            int sweeps = 0;
            using var watcher = new WorkspaceWatcher(root,
                paths => DeltaRefresher.Refresh(store, root, paths),
                () => { DeltaRefresher.Refresh(store, root, null); sweeps++; });
            // Drive only directory notifications; native per-file events must not mask the bug.
            watcher.DisableNativeEventsForTest();
            watcher.SeedCompletionForTest.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            Assert.DoesNotContain(relativeFile, store.AllFilesByPath().Keys);
            string pointer = Path.Combine(child, ".git");
            File.Move(pointer, pointer + ".saved");
            // Recreate metadata before delivering the old deletion: native events can lag
            // the filesystem. No child-directory Created event is delivered to mask the gap.
            if (replaceWithDirectory) Git(child, "init", "-q");
            watcher.NotifyPathForTest(pointer, WatcherChangeTypes.Deleted);
            if (replaceWithDirectory) watcher.NotifyPathForTest(pointer, WatcherChangeTypes.Created);
            watcher.NotifyPathForTest(child, WatcherChangeTypes.Changed);
            watcher.FlushForTest();
            Assert.Equal(1, sweeps);
            Assert.Contains(relativeFile, store.AllFilesByPath().Keys);
            watcher.NotifyPathForTest(Path.Combine(root, first + "-neighbor", "LICENSE"), WatcherChangeTypes.Deleted);
            watcher.FlushForTest();
            Assert.Equal(1, sweeps); // Invalidation is segment-aware, not a raw string prefix.
            using (var queries = new IndexQueries(db))
                Assert.Single(queries.SearchSymbols("BoundaryRemovalSymbol", "exact", null, 5));

            string outside = Directory.CreateTempSubdirectory("pcn-wt-out").FullName;
            try
            {
                Directory.Move(descendant, Path.Combine(outside, directoryName));
                watcher.NotifyPathForTest(descendant, WatcherChangeTypes.Deleted);
                watcher.FlushForTest();
                Assert.Equal(2, sweeps);
                Assert.DoesNotContain(relativeFile, store.AllFilesByPath().Keys);
                using var queries = new IndexQueries(db);
                Assert.Empty(queries.SearchSymbols("BoundaryRemovalSymbol", "exact", null, 5));
                Assert.Contains("A.cs", store.AllFilesByPath().Keys);
            }
            finally { TestWorkspaceCleanup.DeleteWorkspace(outside); }
        });
    }

    private static void WithRepository(Action<string, string, string> test)
    {
        Assert.True(GitInfo.GitAvailable, "This regression requires Git.");
        string root = Directory.CreateTempSubdirectory("pcn-wt").FullName;
        const string first = ".worktrees/one", second = "reviews/two";
        try
        {
            Git(root, "init", "-q");
            Git(root, "config", "user.email", "test@example.invalid");
            Git(root, "config", "user.name", "Test");
            Git(root, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(root, "A.cs"), "class First { }");
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "A.fs"), "module A\nlet value = 1\n");
            File.WriteAllText(Path.Combine(root, "App.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"A.fs\" /></ItemGroup></Project>");
            Git(root, "add", ".");
            Git(root, "commit", "-qm", "first");
            Git(root, "worktree", "add", "-q", "--detach", first, "HEAD");
            File.WriteAllText(Path.Combine(root, "A.cs"), "class Second { }");
            Git(root, "add", "A.cs");
            Git(root, "commit", "-qm", "second");
            Git(root, "worktree", "add", "-q", "--detach", second, "HEAD");
            test(root, first, second);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private static void Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20000), "Git fixture command timed out");
        Assert.True(process.ExitCode == 0, stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
    }
}
