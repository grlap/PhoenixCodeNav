using System.Collections.Concurrent;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

[Collection("Batch45 index follower isolation")]
public sealed class RefreshWorkerFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OuterFaultSealsQueueSurvivesBrokenLoggerAndRequiresNewManager(bool brokenLogger)
    {
        string root = Directory.CreateTempSubdirectory("pcn-pump").FullName;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logs = new ConcurrentQueue<string>();
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "class Before { }");
            string db = IndexBuilder.DefaultDbPath(root);
            using (var manager = new IndexManager(root, db, log: message =>
            {
                logs.Enqueue(message);
                if (brokenLogger && message.Contains("Refresh worker terminated"))
                    throw new IOException("broken log sink");
            }))
            {
                manager.Start();
                IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup");
                Assert.True(manager.RequestRefreshForTest([], out Task barrier));
                await barrier.WaitAsync(TimeSpan.FromSeconds(20));
                manager.RefreshRequestDequeuedForTest = () =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                    throw new IOException("injected outside delta try");
                };
                Assert.True(manager.RequestRefreshForTest(["A.cs"], out Task active));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
                Assert.True(manager.RequestRefreshForTest(["A.cs"], out Task pending));
                Assert.True(manager.RequestFullRebuild());
                release.Set();
                await Assert.ThrowsAsync<IOException>(() => active.WaitAsync(TimeSpan.FromSeconds(20)));
                await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
                await manager.RefreshWorkerCompletionForTest.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(manager.RefreshWorkerCompletionForTest.IsCompletedSuccessfully);
                Assert.True(manager.RefreshWorkerFailed);
                Assert.Equal("failed", manager.State);
                Assert.Equal(IndexManager.RefreshWorkerFailedCause, manager.Health().Error);
                Assert.False(manager.IsQueryable);
                Assert.Throws<IOException>(() => manager.OpenQueries());
                Assert.Null(manager.TryOpenReviewSnapshot());
                Assert.Equal(0, manager.QueuedRefreshCountForTest);
                Assert.Contains(logs, line => line.Contains("injected outside delta try"));

                manager.Start(); // The same manager cannot safely restart its mutation epoch.
                for (int i = 0; i < 100; i++)
                {
                    Assert.False(manager.RequestRefresh());
                    Assert.False(manager.RequestFullRebuild());
                }
                Assert.Equal(0, manager.QueuedRefreshCountForTest);
                using var semantic = new SemanticService(manager);
                var tools = new NavigationTools(manager, semantic);
                using (var capabilities = JsonDocument.Parse(tools.ServerCapabilities()))
                {
                    var index = capabilities.RootElement.GetProperty("index");
                    Assert.Equal("failed", index.GetProperty("state").GetString());
                    Assert.Equal(IndexManager.RefreshWorkerFailedCause, index.GetProperty("error").GetString());
                    foreach (string id in new[] { "nested-worktree-exclusion", "refresh-worker-failure", "refresh-worker-admission-refusal" })
                        Assert.Single(capabilities.RootElement.GetProperty("features").EnumerateArray(),
                            feature => feature.GetProperty("id").GetString() == id);
                }
                foreach (string force in new[] { "auto", "incremental", "full" })
                {
                    using var response = JsonDocument.Parse(tools.RefreshIndex(force: force));
                    var value = response.RootElement;
                    Assert.Equal(IndexManager.RefreshWorkerFailedCause, value.GetProperty("error").GetString());
                    Assert.False(value.GetProperty("queued").GetBoolean());
                    Assert.False(value.GetProperty("retryRecommended").GetBoolean());
                    Assert.Contains("Restart the shared daemon", value.GetProperty("detail").GetString());
                    Assert.DoesNotContain("injected", response.RootElement.GetRawText());
                }
            }
            using var restarted = new IndexManager(root, db);
            restarted.Start();
            IndexManagerTestSupport.WaitUntilReady(restarted, TimeSpan.FromSeconds(20), "restart");
            Assert.False(restarted.RefreshWorkerFailed);
            using var queries = restarted.OpenQueries();
            Assert.Single(queries.SearchSymbols("Before", "exact", null, 5));
        }
        finally
        {
            release.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandledDeltaFailureKeepsRecoveryButEscapingLogFailurePreservesDurableLatch(bool logThrows)
    {
        string root = Directory.CreateTempSubdirectory("pcn-delta").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "class Before { }");
            string db = IndexBuilder.DefaultDbPath(root);
            using var manager = new IndexManager(root, db, log: message =>
            {
                if (logThrows && message.StartsWith("Delta refresh failed:"))
                    throw new IOException("log failure outside delta handler");
            });
            manager.Start();
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup");
            Assert.True(manager.RequestRefreshForTest([], out Task barrier));
            await barrier.WaitAsync(TimeSpan.FromSeconds(20));
            manager.WorkspaceFileReaderForTest = (_, _, _) => throw new IOException("capture fault");
            Assert.True(manager.RequestRefreshForTest(["A.cs"], out Task request));
            if (logThrows)
            {
                await Assert.ThrowsAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(20)));
                await manager.RefreshWorkerCompletionForTest.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal("failed", manager.State);
                Assert.False(manager.IsQueryable);
                Assert.False(manager.RequestFullRebuild());
                var meta = Meta.From(manager.Health(), "exact", "semantic");
                string json = JsonSerializer.Serialize(meta);
                Assert.Contains("restart the daemon", json);
                Assert.DoesNotContain("call refresh_index to retry", json);
            }
            else
            {
                await request.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal("stale", manager.State);
                Assert.True(manager.IsQueryable);
                Assert.False(manager.RefreshWorkerFailed);
            }
            Assert.Equal(IndexManager.RefreshSweepPendingCause, manager.Health().RefreshIncompleteReason);
            using (var queries = new IndexQueries(db))
            {
                Assert.Equal(IndexManager.RefreshSweepPendingCause, queries.ReadMetadata().RefreshIncompleteReason);
                Assert.Single(queries.SearchSymbols("Before", "exact", null, 5));
            }
            if (!logThrows)
            {
                manager.WorkspaceFileReaderForTest = null;
                Assert.True(manager.RequestRefreshForTest(null, out Task recovered));
                await recovered.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal("ready", manager.State);
                Assert.Null(manager.Health().RefreshIncompleteReason);
            }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task FaultFromAwaitedFullRebuildTaskAlsoTerminatesWorker()
    {
        string root = Directory.CreateTempSubdirectory("pcn-full").FullName;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "class Before { }");
            string db = IndexBuilder.DefaultDbPath(root);
            using var manager = new IndexManager(root, db);
            manager.Start();
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup");
            Assert.True(manager.RequestRefreshForTest([], out Task barrier));
            await barrier.WaitAsync(TimeSpan.FromSeconds(20));
            manager.FullRebuildBeforeAnchoredDestinationOpenForTest = () =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                throw new IOException("awaited rebuild fault");
            };
            Assert.True(manager.RequestFullRebuild());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
            Assert.True(manager.RequestRefreshForTest([], out Task pending));
            release.Set();
            await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
            await manager.RefreshWorkerCompletionForTest.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(IndexManager.RefreshWorkerFailedCause, manager.Health().Error);
            Assert.False(manager.RequestFullRebuild());
            using var queries = new IndexQueries(db);
            Assert.Single(queries.SearchSymbols("Before", "exact", null, 5));
        }
        finally
        {
            release.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ShutdownRacingOuterFaultCompletesAndDoesNotLeakWriterOwnership()
    {
        string root = Directory.CreateTempSubdirectory("pcn-stop").FullName;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "class A { }");
            string db = IndexBuilder.DefaultDbPath(root);
            using (var manager = new IndexManager(root, db))
            {
                manager.Start();
                IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20), "startup");
                Assert.True(manager.RequestRefreshForTest([], out Task barrier));
                await barrier.WaitAsync(TimeSpan.FromSeconds(20));
                manager.RefreshRequestDequeuedForTest = () =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                    throw new IOException("shutdown race");
                };
                Assert.True(manager.RequestRefreshForTest([], out Task active));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
                Assert.True(manager.RequestRefreshForTest([], out Task pending));
                Task shutdown = manager.ShutdownAsync().AsTask();
                release.Set();
                await Assert.ThrowsAsync<IOException>(() => active.WaitAsync(TimeSpan.FromSeconds(20)));
                await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
                await shutdown.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(manager.RefreshWorkerFailed);
                Assert.Equal(0, manager.QueuedRefreshCountForTest);
            }
            using var next = new IndexManager(root, db);
            next.Start();
            IndexManagerTestSupport.WaitUntilReady(next, TimeSpan.FromSeconds(20), "new writer");
            await next.ShutdownAsync();
            Assert.False(next.RefreshWorkerFailed); // Normal channel completion is not failure.
        }
        finally
        {
            release.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task FaultBeforeStartupBarrierCannotBeHiddenByLateStartupPublication()
    {
        string root = Directory.CreateTempSubdirectory("pcn-early").FullName;
        using var startupEntered = new ManualResetEventSlim();
        using var startupRelease = new ManualResetEventSlim();
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "class Before { }");
            string db = IndexBuilder.DefaultDbPath(root);
            using var manager = new IndexManager(root, db)
            {
                StartupAfterLeaseAcquiredForTest = () =>
                {
                    startupEntered.Set();
                    Assert.True(startupRelease.Wait(TimeSpan.FromSeconds(20)));
                },
                RefreshRequestDequeuedForTest = () => throw new IOException("early fault"),
            };
            manager.Start();
            Assert.True(startupEntered.Wait(TimeSpan.FromSeconds(20)));
            Assert.True(manager.RequestRefreshForTest([], out Task active));
            await Assert.ThrowsAsync<IOException>(() => active.WaitAsync(TimeSpan.FromSeconds(20)));
            startupRelease.Set();
            await manager.ShutdownAsync();
            Assert.Equal("failed", manager.State);
            Assert.Equal(IndexManager.RefreshWorkerFailedCause, manager.Health().Error);
            Assert.Null(manager.Health().Progress);
            Assert.False(manager.RequestRefresh());
            Assert.False(manager.RequestFullRebuild());
        }
        finally
        {
            startupRelease.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }
}
