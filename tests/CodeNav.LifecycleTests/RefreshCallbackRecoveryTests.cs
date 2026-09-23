using System.Reflection;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;

namespace CodeNav.Tests;

[Collection("Batch45 index follower isolation")]
public sealed class RefreshCallbackRecoveryTests
{
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
