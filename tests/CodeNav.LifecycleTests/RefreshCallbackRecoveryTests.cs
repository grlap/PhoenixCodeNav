using System.Reflection;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;

namespace CodeNav.Tests;

[Collection("Batch45 index follower isolation")]
public sealed class RefreshCallbackRecoveryTests
{
    [Fact]
    public async Task PersistentHeadReadFailureExhaustsExistingBudgetAndManualSweepStillRecovers()
    {
        string root = Directory.CreateTempSubdirectory("pcn-head-budget").FullName;
        try
        {
            using var exhausted = new ManualResetEventSlim();
            using var manager = new IndexManager(root, log: line =>
            {
                if (line == "Git HEAD unresolvable after retries — waiting for the next git signal.")
                    exhausted.Set();
            });
            manager.Start();
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup");
            Assert.True(manager.RequestRefreshForTest([], out Task startup));
            await startup.WaitAsync(TimeSpan.FromSeconds(20));
            manager.GitHeadRetryDelayForTest = TimeSpan.FromMilliseconds(10);
            int reads = 0;
            manager.GitHeadSnapshotForTest = () =>
            {
                Interlocked.Increment(ref reads);
                throw new IOException("persistent HEAD read failure");
            };
            manager.NotifyGitHeadChangedForTest();
            Assert.True(exhausted.Wait(TimeSpan.FromSeconds(20)), "Retry budget did not terminate");
            // Drain the last timer invocation before inspecting its effects.
            await ((Timer)typeof(IndexManager).GetField("_gitHeadRetry",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!).DisposeAsync();
            Assert.Equal(6, Volatile.Read(ref reads)); // Initial read plus the existing five retries.
            Assert.Equal("stale", manager.State);
            Assert.Equal("refresh_callback_failed", manager.Health().RefreshIncompleteReason);
            Assert.False(manager.RefreshWorkerFailed);
            Assert.True(manager.RequestRefreshForTest([], out Task recovery));
            await recovery.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(manager.Health().RefreshIncompleteReason);
            Assert.Equal("ready", manager.State);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHeadReadRetriesWithoutAnotherSignalAndReconcilesResolvedHead(bool changeHead)
    {
        string root = Directory.CreateTempSubdirectory("pcn-head-retry").FullName;
        try
        {
            Git(root, "init", "-q");
            Git(root, "config", "user.email", "test@example.invalid");
            Git(root, "config", "user.name", "Test");
            Git(root, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(root, "A.cs"), "class Before { }");
            Git(root, "add", "A.cs");
            Git(root, "commit", "-qm", "before");
            using var manager = new IndexManager(root);
            manager.Start();
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup");
            Assert.True(manager.RequestRefreshForTest([], out Task startup));
            await startup.WaitAsync(TimeSpan.FromSeconds(20));
            foreach (string field in new[] { "_watcher", "_gitWatcher" })
                ((IDisposable?)typeof(IndexManager).GetField(field,
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager))?.Dispose();
            string? before = manager.Health().IndexedCommit;
            manager.GitHeadRetryDelayForTest = TimeSpan.FromMilliseconds(10);
            // Establish duplicate-suppression state before injecting a read failure.
            manager.NotifyGitHeadChangedForTest();
            if (changeHead) Git(root, "checkout", "-qb", "after-branch");
            File.WriteAllText(Path.Combine(root, "A.cs"), "class After { }");
            if (changeHead)
            {
                Git(root, "add", "A.cs");
                Git(root, "commit", "-qm", "after");
            }
            var expected = GitInfo.HeadSnapshotEx(root);
            Assert.Equal(changeHead, before != expected.Commit);
            int reads = 0;
            manager.GitHeadSnapshotForTest = () => Interlocked.Increment(ref reads) == 1
                ? throw new IOException("transient HEAD read") : GitInfo.HeadSnapshotEx(root);
            manager.NotifyGitHeadChangedForTest(); // The only Git notification after the checkout.
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready" &&
                manager.Health().IndexedCommit == expected.Commit &&
                manager.Health().IndexedBranch == expected.Branch, TimeSpan.FromSeconds(15)),
                "The bounded retry must publish HEAD without another Git event or manual refresh.");
            Assert.True(Volatile.Read(ref reads) >= 2);
            Assert.Null(manager.Health().RefreshIncompleteReason);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("After", "exact", null, 5));
            Assert.Empty(queries.SearchSymbols("Before", "exact", null, 5));
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private static void Git(string root, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20000), "Git fixture timed out");
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult() + output.GetAwaiter().GetResult());
    }

    [Fact]
    public async Task ExistingIncompleteReasonPrecedesCallbackUncertainty()
    {
        string root = Directory.CreateTempSubdirectory("pcn-callback").FullName;
        try
        {
            using var manager = new IndexManager(root);
            manager.Start();
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup");
            Assert.True(manager.RequestRefreshForTest([], out Task ready));
            await ready.WaitAsync(TimeSpan.FromSeconds(20));
            typeof(IndexManager).GetField("_refreshIncompleteReason", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, IndexManager.RefreshInputUnavailableCause);
            manager.GitHeadRetryDelayForTest = Timeout.InfiniteTimeSpan;
            manager.GitHeadSnapshotForTest = () => throw new IOException("HEAD dependency failed");
            manager.NotifyGitHeadChangedForTest();
            Assert.Equal(IndexManager.RefreshInputUnavailableCause, manager.Health().RefreshIncompleteReason);
            Assert.Equal("stale", manager.State);
            Assert.True(manager.RequestRefreshForTest([], out Task recovered));
            await recovered.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(manager.Health().RefreshIncompleteReason);
            Assert.Equal("ready", manager.State);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextRequestSweepsButCannotClearALaterCallbackFailure(bool failDuringSweep)
    {
        string root = Directory.CreateTempSubdirectory("pcn-callback").FullName;
        try
        {
            string file = Path.Combine(root, "A.cs");
            File.WriteAllText(file, "class Before { }");
            using var manager = new IndexManager(root);
            manager.Start();
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup");
            Assert.True(manager.RequestRefreshForTest([], out Task ready));
            await ready.WaitAsync(TimeSpan.FromSeconds(20));
            // Control event delivery: prove the narrow request itself must widen to find A.cs.
            ((IDisposable?)typeof(IndexManager).GetField("_watcher",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager))?.Dispose();
            File.WriteAllText(file, "class After { }");
            manager.GitHeadRetryDelayForTest = Timeout.InfiniteTimeSpan;
            manager.GitHeadSnapshotForTest = () => throw new IOException("HEAD dependency failed");
            manager.NotifyGitHeadChangedForTest();
            Assert.Equal("stale", manager.State);
            Assert.Equal("refresh_callback_failed", manager.Health().RefreshIncompleteReason);
            Assert.True(manager.IsQueryable);
            Assert.False(manager.RefreshWorkerFailed);
            Assert.Equal("indexed", Meta.From(manager.Health(), "exact", "semantic").Confidence);
            if (failDuringSweep)
                manager.ClearRefreshIncompleteBeforeCommitForTest = () =>
                {
                    manager.ClearRefreshIncompleteBeforeCommitForTest = null;
                    manager.NotifyGitHeadChangedForTest();
                };
            Assert.True(manager.RequestRefreshForTest([], out Task sweep));
            await sweep.WaitAsync(TimeSpan.FromSeconds(20));
            using (var queries = manager.OpenQueries())
            {
                Assert.Single(queries.SearchSymbols("After", "exact", null, 5));
                Assert.Empty(queries.SearchSymbols("Before", "exact", null, 5));
            }
            Assert.Equal(failDuringSweep ? "stale" : "ready", manager.State);
            if (failDuringSweep)
            {
                Assert.Equal("refresh_callback_failed", manager.Health().RefreshIncompleteReason);
                Assert.True(manager.RequestRefreshForTest([], out Task next));
                await next.WaitAsync(TimeSpan.FromSeconds(20));
            }
            Assert.Equal("ready", manager.State);
            Assert.Null(manager.Health().RefreshIncompleteReason);
            Assert.Equal("exact", Meta.From(manager.Health(), "exact", "semantic").Confidence);
            Assert.False(manager.RefreshWorkerFailed);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task CallbackFaultBeforeStartupIsRecoveredByStartupSweep()
    {
        string root = Directory.CreateTempSubdirectory("pcn-callback").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A { }");
            using var manager = new IndexManager(root);
            manager.GitHeadRetryDelayForTest = Timeout.InfiniteTimeSpan;
            manager.GitHeadSnapshotForTest = () => throw new IOException("HEAD dependency failed");
            manager.NotifyGitHeadChangedForTest();
            Assert.Equal("missing", manager.State); // No invented readable index before startup.
            manager.GitHeadSnapshotForTest = null;
            manager.Start();
            Assert.True(manager.RequestRefreshForTest([], out Task barrier));
            await barrier.WaitAsync(TimeSpan.FromSeconds(20));
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup recovery");
            Assert.Null(manager.Health().RefreshIncompleteReason);
            Assert.False(manager.RefreshWorkerFailed);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }
}
