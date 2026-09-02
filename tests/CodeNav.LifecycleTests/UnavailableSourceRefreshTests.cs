using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

[Collection("Batch45 index follower isolation")]
public sealed class UnavailableSourceRefreshTests
{
    [Fact]
    public void RefusedSymbolicLinkHasDefinitelyNonRegularDisposition()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-symlink").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Target.cs"),
                "namespace LinkCase; public sealed class Target { }");
            File.CreateSymbolicLink(Path.Combine(root, "Linked.cs"), "Target.cs");

            GitInfo.WorkspaceFileReadResult result =
                GitInfo.ReadBoundedWorkspaceFileResult(root, "Linked.cs", 1024);

            Assert.Equal(GitInfo.WorkspaceFileReadDisposition.DefinitelyNonRegular,
                result.Disposition);
            Assert.Null(result.Bytes);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ExhaustedUnavailableRollsBackEarlierFilesInBatch()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-rollback").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string firstPath = "First.cs";
            const string unavailablePath = "Unavailable.cs";
            File.WriteAllText(Path.Combine(root, firstPath),
                "namespace RollbackCase; public sealed class FirstBefore { }");
            File.WriteAllText(Path.Combine(root, unavailablePath),
                "namespace RollbackCase; public sealed class UnavailableBefore { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] replacement = Encoding.UTF8.GetBytes(
                "namespace RollbackCase; public sealed class FirstAfter { }");
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (gitPath.Equals(firstPath, StringComparison.Ordinal))
                    return new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, replacement);
                if (gitPath.Equals(unavailablePath, StringComparison.Ordinal))
                    return new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null);
                return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                    maxBytes);
            };

            Assert.True(manager.RequestRefreshForTest([firstPath, unavailablePath],
                out Task refreshCompleted));
            await refreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("stale", manager.State);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("FirstBefore", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols("FirstAfter", "exact", null, 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task OversizedBatchRollsBackEarlierRowsBeforePublishingLatch()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-oversized-atomic").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string firstPath = "First.cs";
            const string oversizedPath = "Oversized.cs";
            File.WriteAllText(Path.Combine(root, firstPath),
                "namespace AtomicOversize; public sealed class FirstBefore { }");
            File.WriteAllText(Path.Combine(root, oversizedPath),
                "namespace AtomicOversize; public sealed class OversizedBefore { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] replacement = Encoding.UTF8.GetBytes(
                "namespace AtomicOversize; public sealed class FirstAfter { }");
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (gitPath.Equals(firstPath, StringComparison.Ordinal))
                {
                    return new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, replacement);
                }
                if (gitPath.Equals(oversizedPath, StringComparison.Ordinal))
                {
                    return new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Oversized, null);
                }
                return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                    maxBytes);
            };
            using var beforeLatch = new ManualResetEventSlim();
            using var continueLatch = new ManualResetEventSlim();
            manager.RefreshInputFailureBeforeLatchForTest = () =>
            {
                beforeLatch.Set();
                Assert.True(continueLatch.Wait(TimeSpan.FromSeconds(10)));
            };

            Assert.True(manager.RequestRefreshForTest([firstPath, oversizedPath],
                out Task refreshCompleted));
            Assert.True(beforeLatch.Wait(TimeSpan.FromSeconds(10)),
                "refresh never reached the post-rollback, pre-latch boundary");
            try
            {
                using var interleavedFollower = new IndexQueries(database);
                IndexMetadataSnapshot interleavedMetadata =
                    interleavedFollower.ReadMetadata();
                Assert.Equal(IndexManager.RefreshSweepPendingCause,
                    interleavedMetadata.RefreshIncompleteReason);
                Assert.Single(interleavedFollower.SearchSymbols(
                    "FirstBefore", "exact", null, 2));
                Assert.Empty(interleavedFollower.SearchSymbols(
                    "FirstAfter", "exact", null, 2));
            }
            finally
            {
                continueLatch.Set();
            }
            await refreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("stale", manager.State);
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                manager.Health().RefreshIncompleteReason);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("FirstBefore", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols("FirstAfter", "exact", null, 2));
            using var followerReader = new IndexQueries(database);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                metadata.RefreshIncompleteReason);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task StructuralSecondCaptureUsesTypedUnavailableRecovery()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-structural-recapture").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string projectPath = "App.csproj";
            string projectFile = Path.Combine(root, projectPath);
            File.WriteAllText(projectFile,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "App.cs"),
                "namespace StructuralCapture; public sealed class App { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] replacement = Encoding.UTF8.GetBytes(
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net10.0</TargetFramework>" +
                "<AssemblyName>AfterRecapture</AssemblyName>" +
                "</PropertyGroup></Project>");
            int projectReads = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (!gitPath.Equals(projectPath, StringComparison.Ordinal))
                {
                    return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);
                }

                return Interlocked.Increment(ref projectReads) % 2 == 1
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, replacement)
                    : new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null);
            };

            Assert.True(manager.RequestRefreshForTest([projectPath],
                out Task refreshCompleted));
            await refreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(8, Volatile.Read(ref projectReads));
            Assert.Equal("stale", manager.State);
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);
            using var queries = manager.OpenQueries();
            Assert.DoesNotContain("AfterRecapture",
                queries.ContentByPath(projectPath) ?? "", StringComparison.Ordinal);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task IncompleteGitRefreshDoesNotAdvanceIndexedCommit()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-git-baseline").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Changed.cs";
            const string oldCommit = "1111111111111111111111111111111111111111";
            const string newCommit = "2222222222222222222222222222222222222222";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace BaselineCase; public sealed class Before { }");
            IndexBuilder.Build(root, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("indexed_commit", oldCommit);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(oldCommit, manager.Health().IndexedCommit);

            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
                gitPath.Equals(relativePath, StringComparison.Ordinal)
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Oversized, null)
                    : GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);

            Assert.True(manager.RequestGitRefreshForTest([relativePath], newCommit,
                out Task refreshCompleted));
            await refreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("stale", manager.State);
            Assert.Equal(oldCommit, manager.Health().IndexedCommit);
            using var persisted = new IndexStore(database, createNew: false);
            Assert.Equal(oldCommit, persisted.GetMeta("indexed_commit"));
            using var followerReader = new IndexQueries(database);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                metadata.RefreshIncompleteReason);
            Assert.Equal([relativePath], metadata.RefreshIncompletePaths);
            IndexHealth followerHealth = IndexManager.FollowerHealthForTest(metadata,
                databaseBytes: 1, root, database);
            Assert.Equal("stale", followerHealth.State);
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                followerHealth.RefreshIncompleteReason);
            Assert.Equal([relativePath], followerHealth.RefreshIncompletePaths);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task AutonomousRecoveryPublishesLatestGitBaseline()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-git-autonomous-recovery").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Changed.cs";
            const string oldCommit = "1111111111111111111111111111111111111111";
            const string failedRequestCommit = "2222222222222222222222222222222222222222";
            const string latestHeadCommit = "3333333333333333333333333333333333333333";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace AutonomousBaseline; public sealed class Before { }");
            IndexBuilder.Build(root, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("indexed_commit", oldCommit);

            using var manager = new IndexManager(root, database);
            manager.RefreshRecoverySweepDelayForTest =
                _ => TimeSpan.FromMilliseconds(25);
            int headResolutionAttempts = 0;
            manager.GitHeadSnapshotForTest = () =>
                Interlocked.Increment(ref headResolutionAttempts) == 1
                    ? new GitInfo.HeadSnapshot(null, null, "unavailable")
                    : new GitInfo.HeadSnapshot(
                        latestHeadCommit, "recovery-branch", "attached");
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(oldCommit, manager.Health().IndexedCommit);

            byte[] replacement = Encoding.UTF8.GetBytes(
                "namespace AutonomousBaseline; public sealed class After { }");
            int inputAvailable = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (!gitPath.Equals(relativePath, StringComparison.Ordinal))
                    return GitInfo.ReadBoundedWorkspaceFileResult(
                        workspaceRoot, gitPath, maxBytes);
                return Volatile.Read(ref inputAvailable) == 0
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                    : new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, replacement);
            };

            Assert.True(manager.RequestGitRefreshForTest(
                [relativePath], failedRequestCommit, out Task failedRefreshCompleted));
            await failedRefreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);
            Assert.Equal(oldCommit, manager.Health().IndexedCommit);
            using (var persistedBeforeRecovery =
                   new IndexStore(database, createNew: false))
            {
                Assert.Equal(oldCommit,
                    persistedBeforeRecovery.GetMeta("indexed_commit"));
            }

            Volatile.Write(ref inputAvailable, 1);

            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(10)),
                "autonomous recovery did not converge after Git input became readable");
            Assert.True(Volatile.Read(ref headResolutionAttempts) >= 2,
                "recovery did not retry after HEAD was temporarily unavailable");
            Assert.Equal(latestHeadCommit, manager.Health().IndexedCommit);
            Assert.Equal("recovery-branch", manager.Health().IndexedBranch);
            Assert.Null(manager.Health().RefreshIncompleteReason);
            using var queries = manager.OpenQueries();
            Assert.Empty(queries.SearchSymbols("Before", "exact", null, 2));
            Assert.Single(queries.SearchSymbols("After", "exact", null, 2));
            using (var persistedAfterRecovery =
                   new IndexStore(database, createNew: false))
            {
                Assert.Equal(latestHeadCommit,
                    persistedAfterRecovery.GetMeta("indexed_commit"));
                Assert.Equal("recovery-branch",
                    persistedAfterRecovery.GetMeta("indexed_branch"));
            }
            using var followerReader = new IndexQueries(database);
            IndexMetadataSnapshot recoveredMetadata = followerReader.ReadMetadata();
            Assert.Equal(latestHeadCommit, recoveredMetadata.IndexedCommit);
            Assert.Null(recoveredMetadata.RefreshIncompleteReason);
            IndexHealth followerHealth = IndexManager.FollowerHealthForTest(
                recoveredMetadata, databaseBytes: 1, root, database);
            Assert.Equal("ready", followerHealth.State);
            Assert.Equal(latestHeadCommit, followerHealth.IndexedCommit);
            Assert.Equal("recovery-branch", followerHealth.IndexedBranch);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RecoverySnapshotQueuesBehindEarlierGitObservation()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-git-recovery-order").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var releaseRecovery = new ManualResetEventSlim(initialState: false);
        try
        {
            const string relativePath = "Changed.cs";
            const string oldCommit = "1111111111111111111111111111111111111111";
            const string failedRequestCommit =
                "2222222222222222222222222222222222222222";
            const string earlierObservedCommit =
                "3333333333333333333333333333333333333333";
            const string recoveredHeadCommit =
                "4444444444444444444444444444444444444444";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace RecoveryOrder; public sealed class Before { }");
            IndexBuilder.Build(root, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("indexed_commit", oldCommit);

            using var manager = new IndexManager(root, database);
            int recoverySchedules = 0;
            manager.RefreshRecoverySweepDelayForTest = _ =>
                Interlocked.Increment(ref recoverySchedules) == 1
                    ? TimeSpan.FromMilliseconds(25)
                    : TimeSpan.FromMinutes(5);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] recoveredBytes = Encoding.UTF8.GetBytes(
                "namespace RecoveryOrder; public sealed class RecoveredHead { }");
            int inputAvailable = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (!gitPath.Equals(relativePath, StringComparison.Ordinal))
                    return GitInfo.ReadBoundedWorkspaceFileResult(
                        workspaceRoot, gitPath, maxBytes);
                return Volatile.Read(ref inputAvailable) == 0
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                    : new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, recoveredBytes);
            };

            int headSnapshots = 0;
            manager.GitHeadSnapshotForTest = () =>
                Interlocked.Increment(ref headSnapshots) == 1
                    ? new GitInfo.HeadSnapshot(
                        earlierObservedCommit, "earlier-branch", "attached")
                    : new GitInfo.HeadSnapshot(
                        recoveredHeadCommit, "recovered-branch", "attached");

            using var recoveryDequeued =
                new ManualResetEventSlim(initialState: false);
            int blockOnce = 0;
            manager.RefreshRequestDequeuedForTest = () =>
            {
                if (!string.Equals(manager.Health().RefreshIncompleteReason,
                        IndexManager.RefreshInputUnavailableCause,
                        StringComparison.Ordinal) ||
                    Interlocked.Exchange(ref blockOnce, 1) != 0)
                    return;
                recoveryDequeued.Set();
                if (!releaseRecovery.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("recovery request was not released");
            };

            Assert.True(manager.RequestGitRefreshForTest(
                [relativePath], failedRequestCommit, out Task failedRefresh));
            await failedRefresh.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);
            Assert.Equal(oldCommit, manager.Health().IndexedCommit);
            Assert.True(recoveryDequeued.Wait(TimeSpan.FromSeconds(10)),
                "timer-initiated recovery did not reach the deterministic blocker");

            Volatile.Write(ref inputAvailable, 1);
            // Queue C while timer recovery R is active. R then samples newer D. D must be
            // appended behind C instead of being applied immediately and overwritten afterward.
            manager.NotifyGitHeadChangedForTest();
            releaseRecovery.Set();

            Assert.True(SpinWait.SpinUntil(() =>
                {
                    IndexHealth health = manager.Health();
                    return health.State == "ready" &&
                           string.Equals(health.IndexedCommit, recoveredHeadCommit,
                               StringComparison.Ordinal) &&
                           string.Equals(health.IndexedBranch, "recovered-branch",
                               StringComparison.Ordinal) &&
                           health.RefreshIncompleteReason is null;
                },
                    TimeSpan.FromSeconds(10)),
                "ordered timer recovery did not finish publishing after the earlier Git observation");

            Assert.Equal(2, Volatile.Read(ref headSnapshots));
            Assert.Equal(1, Volatile.Read(ref recoverySchedules));
            Assert.Equal(recoveredHeadCommit, manager.Health().IndexedCommit);
            Assert.Equal("recovered-branch", manager.Health().IndexedBranch);
            Assert.Null(manager.Health().RefreshIncompleteReason);
            using (var queries = manager.OpenQueries())
            {
                Assert.Empty(queries.SearchSymbols("Before", "exact", null, 2));
                Assert.Single(queries.SearchSymbols(
                    "RecoveredHead", "exact", null, 2));
            }

            using var persisted = new IndexStore(database, createNew: false);
            Assert.Equal(recoveredHeadCommit,
                persisted.GetMeta("indexed_commit"));
            Assert.Equal("recovered-branch",
                persisted.GetMeta("indexed_branch"));
            using var followerReader = new IndexQueries(database);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(recoveredHeadCommit, metadata.IndexedCommit);
            Assert.Equal("recovered-branch", metadata.IndexedBranch);
            IndexHealth followerHealth = IndexManager.FollowerHealthForTest(
                metadata, databaseBytes: 1, root, database);
            Assert.Equal(recoveredHeadCommit, followerHealth.IndexedCommit);
            Assert.Equal("recovered-branch", followerHealth.IndexedBranch);
        }
        finally
        {
            releaseRecovery.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task UnavailableRecoveryHeadForcesOlderGitObservationToRevalidate()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-git-recovery-unavailable-order").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var releaseRecovery = new ManualResetEventSlim(initialState: false);
        using var releaseNextRecovery = new ManualResetEventSlim(initialState: false);
        try
        {
            const string relativePath = "Changed.cs";
            const string oldCommit = "1111111111111111111111111111111111111111";
            const string failedRequestCommit =
                "2222222222222222222222222222222222222222";
            const string earlierObservedCommit =
                "3333333333333333333333333333333333333333";
            const string laterObservedCommit =
                "4444444444444444444444444444444444444444";
            const string intermediateHeadCommit =
                "5555555555555555555555555555555555555555";
            const string recoveredHeadCommit =
                "6666666666666666666666666666666666666666";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace RecoveryUnavailableOrder; public sealed class Before { }");
            IndexBuilder.Build(root, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("indexed_commit", oldCommit);

            using var manager = new IndexManager(root, database);
            int recoverySchedules = 0;
            manager.RefreshRecoverySweepDelayForTest = _ =>
                Interlocked.Increment(ref recoverySchedules) == 1
                    ? TimeSpan.FromMilliseconds(25)
                    : TimeSpan.FromMilliseconds(250);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] recoveredBytes = Encoding.UTF8.GetBytes(
                "namespace RecoveryUnavailableOrder; " +
                "public sealed class RecoveredHead { }");
            int inputAvailable = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (!gitPath.Equals(relativePath, StringComparison.Ordinal))
                    return GitInfo.ReadBoundedWorkspaceFileResult(
                        workspaceRoot, gitPath, maxBytes);
                return Volatile.Read(ref inputAvailable) == 0
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                    : new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, recoveredBytes);
            };

            using var recoveryDequeued =
                new ManualResetEventSlim(initialState: false);
            using var nextRecoveryDequeued =
                new ManualResetEventSlim(initialState: false);
            int blockPhase = 0;
            manager.RefreshRequestDequeuedForTest = () =>
            {
                if (!string.Equals(manager.Health().RefreshIncompleteReason,
                        IndexManager.RefreshInputUnavailableCause,
                        StringComparison.Ordinal))
                    return;
                if (Interlocked.CompareExchange(ref blockPhase, 1, 0) == 0)
                {
                    recoveryDequeued.Set();
                    if (!releaseRecovery.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("recovery request was not released");
                    return;
                }
                if (string.Equals(manager.Health().IndexedCommit,
                        intermediateHeadCommit, StringComparison.Ordinal) &&
                    Interlocked.CompareExchange(ref blockPhase, 2, 1) == 1)
                {
                    nextRecoveryDequeued.Set();
                    if (!releaseNextRecovery.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException(
                            "next recovery request was not released");
                }
            };

            Assert.True(manager.RequestGitRefreshForTest(
                [relativePath], failedRequestCommit, out Task failedRefresh));
            await failedRefresh.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);
            Assert.True(recoveryDequeued.Wait(TimeSpan.FromSeconds(10)),
                "timer-initiated recovery did not reach the deterministic blocker");

            int headSnapshots = 0;
            manager.GitHeadSnapshotForTest = () =>
            {
                int attempt = Interlocked.Increment(ref headSnapshots);
                return attempt switch
                {
                    1 => new GitInfo.HeadSnapshot(
                        earlierObservedCommit, "earlier-branch", "attached"),
                    2 => new GitInfo.HeadSnapshot(
                        laterObservedCommit, "later-branch", "attached"),
                    3 => new GitInfo.HeadSnapshot(null, null, "unavailable"),
                    4 => new GitInfo.HeadSnapshot(
                        intermediateHeadCommit, "intermediate-branch", "attached"),
                    5 => new GitInfo.HeadSnapshot(null, null, "unavailable"),
                    _ => new GitInfo.HeadSnapshot(
                        recoveredHeadCommit, "recovered-branch", "attached"),
                };
            };

            Volatile.Write(ref inputAvailable, 1);
            // Queue C and E while timer recovery R is active. R then fails to resolve HEAD.
            // C re-resolves D, but E observes a newer unavailable generation before D executes.
            // D may commit rows and metadata, but cannot clear the latch; the next timer publishes F.
            manager.NotifyGitHeadChangedForTest();
            manager.NotifyGitHeadChangedForTest();
            releaseRecovery.Set();

            Assert.True(nextRecoveryDequeued.Wait(TimeSpan.FromSeconds(10)),
                "the paced retry after the newer unavailable generation did not run");
            Assert.Equal("stale", manager.State);
            Assert.Equal(intermediateHeadCommit, manager.Health().IndexedCommit);
            Assert.Equal("intermediate-branch", manager.Health().IndexedBranch);
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);
            using (var intermediateStore =
                   new IndexStore(database, createNew: false))
            {
                Assert.Equal(intermediateHeadCommit,
                    intermediateStore.GetMeta("indexed_commit"));
                Assert.Equal("intermediate-branch",
                    intermediateStore.GetMeta("indexed_branch"));
                Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                    intermediateStore.GetMeta(IndexManager.RefreshIncompleteReasonMeta));
            }
            releaseNextRecovery.Set();

            Assert.True(SpinWait.SpinUntil(
                    () => string.Equals(manager.Health().IndexedCommit,
                              recoveredHeadCommit, StringComparison.Ordinal) &&
                          string.Equals(manager.State, "ready",
                              StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10)),
                "the older queued Git observation published without revalidating HEAD");

            Assert.Equal(6, Volatile.Read(ref headSnapshots));
            Assert.Equal(4, Volatile.Read(ref recoverySchedules));
            Assert.Equal("ready", manager.State);
            Assert.Equal(recoveredHeadCommit, manager.Health().IndexedCommit);
            Assert.Equal("recovered-branch", manager.Health().IndexedBranch);
            Assert.Null(manager.Health().RefreshIncompleteReason);
            using (var queries = manager.OpenQueries())
            {
                Assert.Empty(queries.SearchSymbols("Before", "exact", null, 2));
                Assert.Single(queries.SearchSymbols(
                    "RecoveredHead", "exact", null, 2));
            }

            using var persisted = new IndexStore(database, createNew: false);
            Assert.Equal(recoveredHeadCommit,
                persisted.GetMeta("indexed_commit"));
            Assert.Equal("recovered-branch",
                persisted.GetMeta("indexed_branch"));
            using var followerReader = new IndexQueries(database);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(recoveredHeadCommit, metadata.IndexedCommit);
            Assert.Equal("recovered-branch", metadata.IndexedBranch);
            Assert.Null(metadata.RefreshIncompleteReason);
            IndexHealth followerHealth = IndexManager.FollowerHealthForTest(
                metadata, databaseBytes: 1, root, database);
            Assert.Equal("ready", followerHealth.State);
            Assert.Equal(recoveredHeadCommit, followerHealth.IndexedCommit);
            Assert.Equal("recovered-branch", followerHealth.IndexedBranch);
            Assert.Null(followerHealth.RefreshIncompleteReason);
        }
        finally
        {
            releaseRecovery.Set();
            releaseNextRecovery.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task FullRebuildRetiresOrderedRecoveryFromPreviousDatabase()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-rebuild-retires-ordered-recovery").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var releaseRecovery = new ManualResetEventSlim(initialState: false);
        using var recoveryDequeued = new ManualResetEventSlim(initialState: false);
        using var postBuildSweepDequeued = new ManualResetEventSlim(initialState: false);
        using var releasePostBuildSweep = new ManualResetEventSlim(initialState: false);
        try
        {
            const string relativePath = "Changed.cs";
            const string oldCommit = "1111111111111111111111111111111111111111";
            const string failedRequestCommit =
                "2222222222222222222222222222222222222222";
            const string obsoleteRecoveryCommit =
                "3333333333333333333333333333333333333333";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace RebuildRecovery; public sealed class Current { }");
            IndexBuilder.Build(root, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("indexed_commit", oldCommit);

            using var manager = new IndexManager(root, database);
            manager.RefreshRecoverySweepDelayForTest = _ => TimeSpan.FromMinutes(5);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            int inputAvailable = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (!gitPath.Equals(relativePath, StringComparison.Ordinal))
                    return GitInfo.ReadBoundedWorkspaceFileResult(
                        workspaceRoot, gitPath, maxBytes);
                return Volatile.Read(ref inputAvailable) == 0
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                    : GitInfo.ReadBoundedWorkspaceFileResult(
                        workspaceRoot, gitPath, maxBytes);
            };

            Assert.True(manager.RequestGitRefreshForTest(
                [relativePath], failedRequestCommit, out Task failedRefresh));
            await failedRefresh.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);

            manager.GitHeadSnapshotForTest = () => new GitInfo.HeadSnapshot(
                obsoleteRecoveryCommit, "obsolete-branch", "attached");
            int blockRecoveryOnce = 0;
            int blockPostBuildSweepOnce = 0;
            int rebuildCompleted = 0;
            int afterRebuildDequeues = 0;
            Task orderedRecoveryCompleted = Task.CompletedTask;
            manager.FullRebuildCompletedForTest = () =>
                Volatile.Write(ref rebuildCompleted, 1);
            manager.RefreshRequestDequeuedForTest = () =>
            {
                if (Volatile.Read(ref rebuildCompleted) != 0)
                {
                    Interlocked.Increment(ref afterRebuildDequeues);
                    // With retirement, D completes before the ordinary dequeue hook. Without it,
                    // D reaches this hook first; let it run so the metadata assertions below prove
                    // that such publication would corrupt the replacement.
                    if (orderedRecoveryCompleted.IsCompleted &&
                        Interlocked.Exchange(ref blockPostBuildSweepOnce, 1) == 0)
                    {
                        postBuildSweepDequeued.Set();
                        if (!releasePostBuildSweep.Wait(TimeSpan.FromSeconds(20)))
                            throw new TimeoutException(
                                "post-build sweep was not released");
                    }
                    return;
                }
                if (string.Equals(manager.Health().RefreshIncompleteReason,
                        IndexManager.RefreshInputUnavailableCause,
                        StringComparison.Ordinal) &&
                    Interlocked.Exchange(ref blockRecoveryOnce, 1) == 0)
                {
                    recoveryDequeued.Set();
                    if (!releaseRecovery.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("recovery request was not released");
                }
            };

            Volatile.Write(ref inputAvailable, 1);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out orderedRecoveryCompleted));
            Assert.True(recoveryDequeued.Wait(TimeSpan.FromSeconds(10)),
                "recovery request did not reach the deterministic blocker");

            // R is active. Queue rebuild F, then let R sample obsolete D and append it:
            // the pump order is F -> D -> the post-build convergence sweep.
            Assert.True(manager.RequestFullRebuild());
            releaseRecovery.Set();

            await orderedRecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(postBuildSweepDequeued.Wait(TimeSpan.FromSeconds(10)),
                "post-build sweep did not queue behind the retired recovery publication");

            Assert.True(Volatile.Read(ref afterRebuildDequeues) >= 1);
            Assert.Equal("stale", manager.State);
            Assert.Equal(IndexManager.RefreshSweepPendingCause,
                manager.Health().RefreshIncompleteReason);
            Assert.NotEqual(obsoleteRecoveryCommit, manager.Health().IndexedCommit);
            Assert.NotEqual("obsolete-branch", manager.Health().IndexedBranch);
            using (var replacementReader = new IndexQueries(database))
            {
                IndexMetadataSnapshot replacementMetadata =
                    replacementReader.ReadMetadata();
                Assert.Equal(IndexManager.RefreshSweepPendingCause,
                    replacementMetadata.RefreshIncompleteReason);
                Assert.NotEqual(obsoleteRecoveryCommit,
                    replacementMetadata.IndexedCommit);
                Assert.NotEqual("obsolete-branch",
                    replacementMetadata.IndexedBranch);
                IndexHealth followerHealth = IndexManager.FollowerHealthForTest(
                    replacementMetadata, databaseBytes: 1, root, database);
                Assert.Equal("stale", followerHealth.State);
                Assert.Equal(IndexManager.RefreshSweepPendingCause,
                    followerHealth.RefreshIncompleteReason);
            }

            manager.RefreshRequestDequeuedForTest = null;
            releasePostBuildSweep.Set();
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task queueDrained));
            await queueDrained.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("ready", manager.State);
            Assert.NotEqual(obsoleteRecoveryCommit, manager.Health().IndexedCommit);
        }
        finally
        {
            releaseRecovery.Set();
            releasePostBuildSweep.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RetriedRequestStaysAheadOfLaterQueuedRefresh()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-order").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string firstPath = "First.cs";
            const string secondPath = "Second.cs";
            File.WriteAllText(Path.Combine(root, firstPath),
                "namespace QueueCase; public sealed class FirstBefore { }");
            File.WriteAllText(Path.Combine(root, secondPath),
                "namespace QueueCase; public sealed class SecondBefore { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] firstReplacement = Encoding.UTF8.GetBytes(
                "namespace QueueCase; public sealed class FirstAfter { }");
            byte[] secondReplacement = Encoding.UTF8.GetBytes(
                "namespace QueueCase; public sealed class SecondAfter { }");
            var readOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
            int firstAttempts = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (gitPath.Equals(firstPath, StringComparison.Ordinal))
                {
                    readOrder.Enqueue(firstPath);
                    return Interlocked.Increment(ref firstAttempts) == 1
                        ? new GitInfo.WorkspaceFileReadResult(
                            GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                        : new GitInfo.WorkspaceFileReadResult(
                            GitInfo.WorkspaceFileReadDisposition.Success, firstReplacement);
                }
                if (gitPath.Equals(secondPath, StringComparison.Ordinal))
                {
                    readOrder.Enqueue(secondPath);
                    return new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, secondReplacement);
                }
                return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                    maxBytes);
            };

            Assert.True(manager.RequestRefreshForTest([firstPath],
                out Task firstCompleted));
            Assert.True(manager.RequestRefreshForTest([secondPath],
                out Task secondCompleted));
            await Task.WhenAll(firstCompleted, secondCompleted)
                .WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal([firstPath, firstPath, secondPath], readOrder.ToArray());
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("FirstAfter", "exact", null, 2));
            Assert.Single(queries.SearchSymbols("SecondAfter", "exact", null, 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ExhaustedCaptureLatchForcesNextRequestToDetectAll()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-recovery").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string failedPath = "Failed.cs";
            const string secondUnavailablePath = "ZSecondUnavailable.cs";
            const string unrelatedPath = "Unrelated.cs";
            File.WriteAllText(Path.Combine(root, failedPath),
                "namespace RecoveryCase; public sealed class BeforeRecovery { }");
            File.WriteAllText(Path.Combine(root, unrelatedPath),
                "namespace RecoveryCase; public sealed class Unrelated { }");
            File.WriteAllText(Path.Combine(root, secondUnavailablePath),
                "namespace RecoveryCase; public sealed class SecondUnavailable { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.RefreshRecoverySweepDelayForTest =
                _ => TimeSpan.FromHours(1);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] replacement = Encoding.UTF8.GetBytes(
                "namespace RecoveryCase; public sealed class AfterRecovery { }");
            int failedPathAttempts = 0;
            int secondPathAttempts = 0;
            int recovered = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (gitPath.Equals(secondUnavailablePath, StringComparison.Ordinal))
                {
                    int attempt = Interlocked.Increment(ref secondPathAttempts);
                    return Volatile.Read(ref recovered) == 0 || attempt <= 3
                        ? new GitInfo.WorkspaceFileReadResult(
                            GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                        : GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                            maxBytes);
                }
                if (!gitPath.Equals(failedPath, StringComparison.Ordinal))
                    return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);
                Interlocked.Increment(ref failedPathAttempts);
                return Volatile.Read(ref recovered) == 0
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                    : new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, replacement);
            };

            Assert.True(manager.RequestRefreshForTest([failedPath, secondUnavailablePath],
                out Task failedRefreshCompleted));
            await failedRefreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(4, Volatile.Read(ref failedPathAttempts));
            Assert.Equal(0, Volatile.Read(ref secondPathAttempts));
            Assert.Equal("stale", manager.State);
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().Error);
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);
            Assert.Equal([failedPath], manager.Health().RefreshIncompletePaths);
            Assert.Equal(1, manager.Health().RefreshIncompletePathCount);
            Assert.True(manager.Health().RefreshIncompletePathCountIsLowerBound);

            Volatile.Write(ref recovered, 1);
            Assert.True(manager.RequestRefreshForTest([unrelatedPath],
                out Task recoveryCompleted));
            await recoveryCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.InRange(Volatile.Read(ref failedPathAttempts), 5, 8);
            Assert.Equal(4, Volatile.Read(ref secondPathAttempts));
            Assert.Equal("ready", manager.State);
            Assert.Null(manager.Health().Error);
            Assert.Null(manager.Health().RefreshIncompleteReason);
            Assert.Null(manager.Health().RefreshIncompletePaths);
            Assert.Equal(0, manager.Health().RefreshIncompletePathCount);
            Assert.False(manager.Health().RefreshIncompletePathCountIsLowerBound);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("AfterRecovery", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols("BeforeRecovery", "exact", null, 2));
            using var followerReader = new IndexQueries(database);
            IndexMetadataSnapshot recoveredMetadata = followerReader.ReadMetadata();
            Assert.Null(recoveredMetadata.RefreshIncompleteReason);
            Assert.Null(recoveredMetadata.RefreshIncompletePaths);
            Assert.Equal(0, recoveredMetadata.RefreshIncompletePathCount);
            Assert.False(recoveredMetadata.RefreshIncompletePathCountIsLowerBound);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ExhaustedProjectCaptureRecoversCoBatchedSourceWithoutAnotherEvent()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-project-capture-recovery").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string sourcePath = "App.cs";
            const string projectPath = "App.csproj";
            string projectFile = Path.Combine(root, projectPath);
            File.WriteAllText(Path.Combine(root, sourcePath),
                "namespace ProjectRecovery; public sealed class BeforeRecovery { }");
            File.WriteAllText(projectFile,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net10.0</TargetFramework>" +
                "<AssemblyName>BeforeProjectRecovery</AssemblyName>" +
                "</PropertyGroup></Project>");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.RefreshRecoverySweepDelayForTest =
                _ => TimeSpan.FromMilliseconds(25);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] sourceReplacement = Encoding.UTF8.GetBytes(
                "namespace ProjectRecovery; public sealed class AfterRecovery { }");
            byte[] projectReplacement = Encoding.UTF8.GetBytes(
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                "<TargetFramework>net10.0</TargetFramework>" +
                "<AssemblyName>AfterProjectRecovery</AssemblyName>" +
                "</PropertyGroup></Project>");

            int projectAvailable = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (gitPath.Equals(sourcePath, StringComparison.Ordinal))
                {
                    return new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, sourceReplacement);
                }
                if (gitPath.Equals(projectPath, StringComparison.Ordinal))
                {
                    return Volatile.Read(ref projectAvailable) == 0
                        ? new GitInfo.WorkspaceFileReadResult(
                            GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                        : new GitInfo.WorkspaceFileReadResult(
                            GitInfo.WorkspaceFileReadDisposition.Success, projectReplacement);
                }
                return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath, maxBytes);
            };

            Assert.True(manager.RequestRefreshForTest([sourcePath, projectPath],
                out Task failedRefreshCompleted));
            await failedRefreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);
            using (var staleQueries = manager.OpenQueries())
            {
                Assert.Single(staleQueries.SearchSymbols(
                    "BeforeRecovery", "exact", null, 2));
                Assert.Empty(staleQueries.SearchSymbols(
                    "AfterRecovery", "exact", null, 2));
                Assert.NotNull(staleQueries.ProjectByName("BeforeProjectRecovery"));
                Assert.Null(staleQueries.ProjectByName("AfterProjectRecovery"));
            }

            Volatile.Write(ref projectAvailable, 1);

            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(10)),
                "the stale writer did not retry after project input became readable");
            using var recoveredQueries = manager.OpenQueries();
            Assert.Empty(recoveredQueries.SearchSymbols(
                "BeforeRecovery", "exact", null, 2));
            Assert.Single(recoveredQueries.SearchSymbols(
                "AfterRecovery", "exact", null, 2));
            Assert.Null(recoveredQueries.ProjectByName("BeforeProjectRecovery"));
            Assert.NotNull(recoveredQueries.ProjectByName("AfterProjectRecovery"));
            Assert.Null(manager.Health().Error);
            Assert.Null(manager.Health().RefreshIncompleteReason);
            using var followerReader = new IndexQueries(database);
            IndexMetadataSnapshot recoveredMetadata = followerReader.ReadMetadata();
            Assert.Null(recoveredMetadata.RefreshIncompleteReason);
            Assert.Null(recoveredMetadata.RefreshIncompletePaths);
            Assert.Equal(0, recoveredMetadata.RefreshIncompletePathCount);
            Assert.False(recoveredMetadata.RefreshIncompletePathCountIsLowerBound);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RecoverySweepBackoffEscalatesAndSkipsInlineCaptureRetries()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-recovery-backoff").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string unavailablePath = "Unavailable.cs";
            File.WriteAllText(Path.Combine(root, unavailablePath),
                "namespace RecoveryBackoff; public sealed class BeforeRecovery { }");
            IndexBuilder.Build(root, database);

            var logs = new ConcurrentQueue<string>();
            var scheduledLevels = new ConcurrentQueue<int>();
            using var manager = new IndexManager(root, database, logs.Enqueue);
            manager.RefreshRecoverySweepDelayForTest = level =>
            {
                scheduledLevels.Enqueue(level);
                return level < 3
                    ? TimeSpan.FromMilliseconds(10)
                    : TimeSpan.FromHours(1);
            };
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            int unavailableAttempts = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (gitPath.Equals(unavailablePath, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref unavailableAttempts);
                    return new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null);
                }
                return GitInfo.ReadBoundedWorkspaceFileResult(
                    workspaceRoot, gitPath, maxBytes);
            };

            Assert.True(manager.RequestRefreshForTest([unavailablePath],
                out Task failedRefreshCompleted));
            await failedRefreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(SpinWait.SpinUntil(() => scheduledLevels.Count >= 4,
                TimeSpan.FromSeconds(5)), "recovery backoff did not reach its ceiling");

            Assert.Equal([0, 1, 2, 3], scheduledLevels.Take(4).ToArray());
            Assert.Equal(7, Volatile.Read(ref unavailableAttempts));
            Assert.Single(logs, message => message.Contains(
                "recovery is now paced at one complete sweep every",
                StringComparison.Ordinal));
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RecoverySweepRetriesAfterDurableLatchClearFailsOnce()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-capture-recovery-latch-clear").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Changed.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace RecoveryLatchClear; public sealed class Before { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.RefreshRecoverySweepDelayForTest =
                _ => TimeSpan.FromMilliseconds(25);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] replacement = Encoding.UTF8.GetBytes(
                "namespace RecoveryLatchClear; public sealed class After { }");
            int inputAvailable = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (!gitPath.Equals(relativePath, StringComparison.Ordinal))
                    return GitInfo.ReadBoundedWorkspaceFileResult(
                        workspaceRoot, gitPath, maxBytes);
                return Volatile.Read(ref inputAvailable) == 0
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                    : new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, replacement);
            };
            int clearAttempts = 0;
            manager.ClearRefreshIncompleteBeforeCommitForTest = () =>
            {
                if (Interlocked.Increment(ref clearAttempts) == 1)
                    throw new IOException("injected one-time metadata clear failure");
            };

            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task failedRefreshCompleted));
            await failedRefreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(IndexManager.RefreshInputUnavailableCause,
                manager.Health().RefreshIncompleteReason);

            Volatile.Write(ref inputAvailable, 1);

            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(10)),
                "recovery did not retry after the durable latch clear failed once");
            Assert.Equal(2, Volatile.Read(ref clearAttempts));
            Assert.Null(manager.Health().Error);
            Assert.Null(manager.Health().RefreshIncompleteReason);
            using var queries = manager.OpenQueries();
            Assert.Empty(queries.SearchSymbols("Before", "exact", null, 2));
            Assert.Single(queries.SearchSymbols("After", "exact", null, 2));
            using var followerReader = new IndexQueries(database);
            IndexMetadataSnapshot recoveredMetadata = followerReader.ReadMetadata();
            Assert.Null(recoveredMetadata.RefreshIncompleteReason);
            Assert.Null(recoveredMetadata.RefreshIncompletePaths);
            Assert.Equal(0, recoveredMetadata.RefreshIncompletePathCount);
            Assert.False(recoveredMetadata.RefreshIncompletePathCountIsLowerBound);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task OversizedSourceDoesNotRetryOrPublishReady()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-oversized-refresh").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Oversized.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace OversizeCase; public sealed class RetainedBeforeOversize { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            int sourceReadAttempts = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (!gitPath.Equals(relativePath, StringComparison.Ordinal))
                    return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);
                Interlocked.Increment(ref sourceReadAttempts);
                return new GitInfo.WorkspaceFileReadResult(
                    GitInfo.WorkspaceFileReadDisposition.Oversized, null);
            };

            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task refreshCompleted));
            await refreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(1, Volatile.Read(ref sourceReadAttempts));
            Assert.Equal("stale", manager.State);
            Assert.Equal("refresh_input_oversized", manager.Health().Error);
            Assert.Equal([relativePath], manager.Health().RefreshIncompletePaths);
            Assert.True(manager.Health().RefreshIncompletePathCountIsLowerBound);
            Assert.True(manager.IsQueryable);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("RetainedBeforeOversize", "exact", null, 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ColdBuildWithOversizedRegularSourceNeverBecomesQueryable()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-oversized-cold-build").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            using (var sparse = new FileStream(Path.Combine(root, "Oversized.cs"),
                       FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                sparse.SetLength((long)DeltaRefresher.MaxIndexedFileBytes + 1);
            }

            using var manager = new IndexManager(root, database);
            manager.Start();

            Assert.True(SpinWait.SpinUntil(() => manager.State == "failed",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.False(manager.IsQueryable);
            Assert.Contains(nameof(RefreshInputOversizedException),
                manager.Health().Error, StringComparison.Ordinal);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void FullRebuildWithOversizedRegularSourceNeverPublishesReady()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-oversized-full-build").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            string source = Path.Combine(root, "Oversized.cs");
            File.WriteAllText(source,
                "namespace FullOversize; public sealed class Before { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            string oldVersion = manager.Health().IndexVersion!;
            using var rebuildCompleted = new ManualResetEventSlim(false);
            manager.FullRebuildCompletedForTest = () => rebuildCompleted.Set();
            using (var sparse = new FileStream(source, FileMode.Open, FileAccess.Write,
                       FileShare.ReadWrite))
            {
                sparse.SetLength((long)DeltaRefresher.MaxIndexedFileBytes + 1);
            }

            Assert.True(manager.RequestFullRebuild());
            Assert.True(rebuildCompleted.Wait(TimeSpan.FromSeconds(30)),
                "oversized full rebuild did not return control to the refresh pump");
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                Assert.True(manager.IsQueryable, manager.Health().Error);
                Assert.Equal(oldVersion, manager.Health().IndexVersion);
                Assert.NotEqual("failed", manager.State);
                string? error = manager.Health().Error;
                Assert.True(
                    error?.Contains("previous index remains available",
                        StringComparison.Ordinal) == true ||
                    string.Equals(error, "refresh_input_oversized",
                        StringComparison.Ordinal),
                    $"unexpected restored-publication error: {error}");
                using IndexQueries queries = manager.OpenQueries();
                Assert.Single(queries.SearchSymbols("Before", "exact", null, 2));
            }
            else
            {
                Assert.Equal("failed", manager.State);
                Assert.False(manager.IsQueryable);
                Assert.Contains(nameof(RefreshInputOversizedException),
                    manager.Health().Error, StringComparison.Ordinal);
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task LatchPersistenceFailureLeavesFollowerVisibleSweepMarker()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-latch-persist-failure").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Blocked.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace PersistFailure; public sealed class Retained { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
                gitPath.Equals(relativePath, StringComparison.Ordinal)
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Oversized, null)
                    : GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);
            manager.RefreshIncompleteBeforeCommitForTest = reason =>
            {
                if (reason == IndexManager.RefreshInputOversizedCause)
                    throw new IOException("injected specific-latch persistence failure");
            };

            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task failedRefresh));
            await failedRefresh.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("stale", manager.State);
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                manager.Health().RefreshIncompleteReason);
            using var reader = new IndexQueries(database);
            IndexMetadataSnapshot metadata = reader.ReadMetadata();
            Assert.Equal(IndexManager.RefreshSweepPendingCause,
                metadata.RefreshIncompleteReason);
            IndexHealth follower = IndexManager.FollowerHealthForTest(metadata, 1,
                root, database);
            Assert.Equal("stale", follower.State);
            Assert.Equal(IndexManager.RefreshSweepPendingCause,
                follower.RefreshIncompleteReason);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task FailedInitialSweepMarkerIsRetriedBeforeNextRequestReadsSource()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-initial-marker-retry").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var secondMarkerBeforeCommit = new ManualResetEventSlim();
        using var releaseSecondMarker = new ManualResetEventSlim();
        try
        {
            const string relativePath = "Changed.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace MarkerRetry; public sealed class Current { }");
            IndexBuilder.Build(root, database);
            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);

            int markerAttempts = 0;
            int captureAttempts = 0;
            string? markerObservedByCapture = null;
            manager.RefreshIncompleteBeforeCommitForTest = reason =>
            {
                if (reason != IndexManager.RefreshSweepPendingCause) return;
                int attempt = Interlocked.Increment(ref markerAttempts);
                if (attempt == 1)
                    throw new IOException("injected initial marker failure");
                if (attempt == 2)
                {
                    secondMarkerBeforeCommit.Set();
                    Assert.True(releaseSecondMarker.Wait(TimeSpan.FromSeconds(15)));
                }
            };
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                Interlocked.Increment(ref captureAttempts);
                using var followerReader = new IndexQueries(database);
                markerObservedByCapture = followerReader.ReadMetadata()
                    .RefreshIncompleteReason;
                return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                    maxBytes);
            };

            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task firstRequest));
            await firstRequest.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, Volatile.Read(ref markerAttempts));
            Assert.Equal(0, Volatile.Read(ref captureAttempts));
            using (var followerBeforeRetry = new IndexQueries(database))
                Assert.Null(followerBeforeRetry.ReadMetadata().RefreshIncompleteReason);
            IndexHealth failedMarkerHealth = manager.Health();
            Assert.Equal("stale", failedMarkerHealth.State);
            Assert.Equal(IndexManager.RefreshSweepPendingCause,
                failedMarkerHealth.RefreshIncompleteReason);
            Meta failedMarkerMeta = Meta.From(failedMarkerHealth, "exact", "semantic");
            Assert.Equal("indexed", failedMarkerMeta.Confidence);
            Assert.Contains("refresh_index", failedMarkerMeta.StatusNote);
            Assert.DoesNotContain("no additional refresh request",
                failedMarkerMeta.StatusNote);

            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task secondRequest));
            Assert.True(secondMarkerBeforeCommit.Wait(TimeSpan.FromSeconds(10)),
                "second request did not retry the durable sweep marker");
            Assert.Equal(0, Volatile.Read(ref captureAttempts));
            releaseSecondMarker.Set();
            await secondRequest.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(Volatile.Read(ref captureAttempts) > 0);
            Assert.Equal(IndexManager.RefreshSweepPendingCause,
                markerObservedByCapture);
            Assert.Equal("ready", manager.State);
        }
        finally
        {
            releaseSecondMarker.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ColdStartupDoesNotPublishReadyBeforePostBuildSweep()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-cold-sweep-gate").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var sweepDequeued = new ManualResetEventSlim();
        using var releaseSweep = new ManualResetEventSlim();
        try
        {
            const string relativePath = "ChangedDuringBuildGap.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace BuildGap; public sealed class BeforeSweep { }");
            using var manager = new IndexManager(root, database)
            {
                RefreshRequestDequeuedForTest = () =>
                {
                    sweepDequeued.Set();
                    Assert.True(releaseSweep.Wait(TimeSpan.FromSeconds(15)));
                },
            };
            manager.Start();
            Assert.True(sweepDequeued.Wait(TimeSpan.FromSeconds(20)),
                "post-build sweep was not dequeued");

            Assert.Equal("stale", manager.State);
            Assert.Equal(IndexManager.RefreshSweepPendingCause,
                manager.Health().RefreshIncompleteReason);
            Assert.NotEqual("ready", manager.State);
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace BuildGap; public sealed class AfterSweep { }");

            manager.RefreshRequestDequeuedForTest = null;
            releaseSweep.Set();
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task sweepAndBarrierDrained));
            await sweepAndBarrierDrained.WaitAsync(TimeSpan.FromSeconds(20));
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20),
                "cold-start post-build sweep did not converge after the watcher refresh");
            Assert.Null(manager.Health().RefreshIncompleteReason);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("AfterSweep", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols("BeforeSweep", "exact", null, 2));
        }
        finally
        {
            releaseSweep.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task FullRebuildDoesNotPublishReadyBeforePostBuildSweep()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-full-sweep-gate").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var sweepDequeued = new ManualResetEventSlim();
        using var releaseSweep = new ManualResetEventSlim();
        try
        {
            const string relativePath = "ChangedDuringFullBuildGap.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace FullBuildGap; public sealed class BeforeSweep { }");
            IndexBuilder.Build(root, database);
            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);

            int dequeued = 0;
            manager.RefreshRequestDequeuedForTest = () =>
            {
                if (Interlocked.Increment(ref dequeued) != 2) return;
                sweepDequeued.Set();
                Assert.True(releaseSweep.Wait(TimeSpan.FromSeconds(15)));
            };
            Assert.True(manager.RequestFullRebuild());
            Assert.True(sweepDequeued.Wait(TimeSpan.FromSeconds(30)),
                "post-rebuild sweep was not dequeued");

            Assert.Equal("stale", manager.State);
            Assert.Equal(IndexManager.RefreshSweepPendingCause,
                manager.Health().RefreshIncompleteReason);
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace FullBuildGap; public sealed class AfterSweep { }");

            manager.RefreshRequestDequeuedForTest = null;
            releaseSweep.Set();
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task sweepAndBarrierDrained));
            await sweepAndBarrierDrained.WaitAsync(TimeSpan.FromSeconds(20));
            IndexManagerTestSupport.WaitUntilReady(manager, TimeSpan.FromSeconds(20),
                "full-rebuild post-build sweep did not converge after the watcher refresh");
            Assert.Null(manager.Health().RefreshIncompleteReason);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("AfterSweep", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols("BeforeSweep", "exact", null, 2));
        }
        finally
        {
            releaseSweep.Set();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ColdBuildSkipsMissingSourceAndStopsAfterFirstUnavailableFailure()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-build-capture-stop").FullName;
        try
        {
            int fileCount = Math.Max(256, Environment.ProcessorCount * 16);
            for (int i = 0; i < fileCount; i++)
                File.WriteAllText(Path.Combine(root, $"Source{i:D4}.cs"),
                    $"namespace BuildStop; public sealed class Source{i:D4} {{ }}");

            int attempts = 0;
            int retainedFailures = 0;
            string? retainedPath = null;
            var hooks = new BuildCaptureTestHooks((workspaceRoot, gitPath, maxBytes) =>
            {
                Interlocked.Increment(ref attempts);
                if (gitPath == "Source0000.cs")
                {
                    return new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Missing, null);
                }
                return new GitInfo.WorkspaceFileReadResult(
                    GitInfo.WorkspaceFileReadDisposition.Unavailable, null);
            }, path =>
            {
                retainedPath = path;
                Interlocked.Increment(ref retainedFailures);
            });

            RefreshInputUnavailableException failure =
                Assert.Throws<RefreshInputUnavailableException>(() =>
                IndexBuilder.BuildWithSourceBatchSizeForTest(root, 1,
                    buildCaptureTestHooks: hooks));
            Assert.InRange(Volatile.Read(ref attempts), 1, fileCount - 1);
            Assert.Equal(1, Volatile.Read(ref retainedFailures));
            Assert.Equal(retainedPath, failure.Path);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void FSharpColdBuildStopsAfterRetainingFirstUnavailableFailure()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-build-capture-stop").FullName;
        try
        {
            const int fileCount = 256;
            for (int i = 0; i < fileCount; i++)
                File.WriteAllText(Path.Combine(root, $"Source{i:D4}.fs"),
                    $"module Source{i:D4}\nlet value = {i}\n");

            int attempts = 0;
            int retainedFailures = 0;
            string? retainedPath = null;
            var hooks = new BuildCaptureTestHooks((_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return new GitInfo.WorkspaceFileReadResult(
                    GitInfo.WorkspaceFileReadDisposition.Unavailable, null);
            }, path =>
            {
                retainedPath = path;
                Interlocked.Increment(ref retainedFailures);
            });

            RefreshInputUnavailableException failure =
                Assert.Throws<RefreshInputUnavailableException>(() =>
                    IndexBuilder.BuildWithSourceBatchSizeForTest(root, 1,
                        buildCaptureTestHooks: hooks));

            Assert.InRange(Volatile.Read(ref attempts), 1, fileCount - 1);
            Assert.Equal(1, Volatile.Read(ref retainedFailures));
            Assert.Equal(retainedPath, failure.Path);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ColdBuildSkipsSourceThatDisappearsAfterScan()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-build-missing-source").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string missingPath = "Missing.cs";
            const string retainedPath = "Retained.cs";
            File.WriteAllText(Path.Combine(root, missingPath),
                "namespace MissingBuild; public sealed class MustNotAppear { }");
            File.WriteAllText(Path.Combine(root, retainedPath),
                "namespace MissingBuild; public sealed class MustAppear { }");
            var hooks = new BuildCaptureTestHooks((workspaceRoot, gitPath, maxBytes) =>
                gitPath == missingPath
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Missing, null)
                    : GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes));

            BuildResult result = IndexBuilder.BuildWithSourceBatchSizeForTest(root, 1,
                buildCaptureTestHooks: hooks);

            Assert.Equal(1, result.CsFiles);
            using var queries = new IndexQueries(database);
            Assert.Empty(queries.SearchSymbols("MustNotAppear", "exact", null, 2));
            Assert.Single(queries.SearchSymbols("MustAppear", "exact", null, 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task LatchClearFailureKeepsSuccessfulRefreshQueryableAsStale()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-latch-clear-failure").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Changed.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace ClearFailure; public sealed class Before { }");
            IndexBuilder.Build(root, database);
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();

            using var manager = new IndexManager(root, database, logs.Enqueue);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
                gitPath.Equals(relativePath, StringComparison.Ordinal)
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Oversized, null)
                    : GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);
            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task failedRefresh));
            await failedRefresh.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("stale", manager.State);

            byte[] replacement = Encoding.UTF8.GetBytes(
                "namespace ClearFailure; public sealed class After { }");
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
                gitPath.Equals(relativePath, StringComparison.Ordinal)
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, replacement)
                    : GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);
            manager.ClearRefreshIncompleteBeforeCommitForTest = () =>
                throw new IOException("injected metadata clear failure");
            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task successfulRefresh));
            await successfulRefresh.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("stale", manager.State);
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                manager.Health().RefreshIncompleteReason);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("After", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols("Before", "exact", null, 2));
            Assert.Contains(logs, log => log.Contains(
                "Could not clear incomplete-source refresh state", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, log => log.Contains(
                "Delta refresh failed", StringComparison.Ordinal));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task StaleWriterCanPinReviewSnapshotWithIncompleteHealth()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-stale-review-snapshot").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Stale.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace StaleSnapshot; public sealed class Retained { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);
            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
                gitPath.Equals(relativePath, StringComparison.Ordinal)
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Oversized, null)
                    : GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);
            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task refreshCompleted));
            await refreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            using IndexReadSnapshot? snapshot = manager.TryOpenReviewSnapshot();
            Assert.NotNull(snapshot);
            Assert.Equal("stale", snapshot.Health.State);
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                snapshot.Health.RefreshIncompleteReason);
            Assert.Single(snapshot.Queries.SearchSymbols("Retained", "exact", null, 2));

            var tools = new NavigationTools(manager, new SemanticService(manager));
            using JsonDocument review = JsonDocument.Parse(
                tools.ReviewPack(paths: relativePath));
            JsonElement response = review.RootElement;
            Assert.False(response.TryGetProperty("error", out _));
            JsonElement meta = response.GetProperty("meta");
            Assert.Equal("stale", meta.GetProperty("indexStatus").GetString());
            Assert.Equal("indexed", meta.GetProperty("confidence").GetString());
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                meta.GetProperty("partialReason").GetString());
            Assert.True(meta.GetProperty("incompleteSourcePathCountLowerBound")
                .GetBoolean());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task StaleFollowerCanPinReviewSnapshotWithIncompleteHealth()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory(
            "codenav-stale-follower-snapshot").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Follower.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace StaleFollower; public sealed class Retained { }");
            IndexBuilder.Build(root, database);

            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(SpinWait.SpinUntil(() => writer.State == "ready",
                TimeSpan.FromSeconds(20)), writer.Health().Error);
            using var follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(SpinWait.SpinUntil(() => follower.State == "ready",
                TimeSpan.FromSeconds(20)), follower.Health().Error);
            Assert.True(writer.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            writer.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
                gitPath.Equals(relativePath, StringComparison.Ordinal)
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Oversized, null)
                    : GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);
            Assert.True(writer.RequestRefreshForTest([relativePath],
                out Task refreshCompleted));
            await refreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(SpinWait.SpinUntil(() => follower.Health().State == "stale",
                TimeSpan.FromSeconds(20)), follower.Health().Error);

            using IndexReadSnapshot? snapshot = follower.TryOpenReviewSnapshot();
            Assert.NotNull(snapshot);
            Assert.Equal("stale", snapshot.Health.State);
            Assert.Equal(IndexManager.RefreshInputOversizedCause,
                snapshot.Health.RefreshIncompleteReason);
            Assert.True(snapshot.Health.RefreshIncompletePathCountIsLowerBound);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void OversizedRegularSourceHasPersistentDisposition()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-oversized-source").FullName;
        try
        {
            const string relativePath = "Oversized.cs";
            using (var sparse = new FileStream(Path.Combine(root, relativePath),
                       FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                sparse.SetLength((long)DeltaRefresher.MaxIndexedFileBytes + 1);
            }

            GitInfo.WorkspaceFileReadResult result =
                GitInfo.ReadBoundedWorkspaceFileResult(root, relativePath,
                    DeltaRefresher.MaxIndexedFileBytes);

            Assert.Equal(GitInfo.WorkspaceFileReadDisposition.Oversized,
                result.Disposition);
            Assert.Null(result.Bytes);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task TransientUnavailableSourceIsRetriedBeforeRefreshCompletes()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-unavailable-source").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            const string relativePath = "Transient.cs";
            File.WriteAllText(Path.Combine(root, relativePath),
                "namespace RetryCase; public sealed class BeforeRetry { }");
            IndexBuilder.Build(root, database);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(SpinWait.SpinUntil(() => manager.State == "ready",
                TimeSpan.FromSeconds(20)), manager.Health().Error);

            Assert.True(manager.RequestRefreshForTest(Array.Empty<string>(),
                out Task startupQueueDrained));
            await startupQueueDrained.WaitAsync(TimeSpan.FromSeconds(20));

            byte[] replacement = Encoding.UTF8.GetBytes(
                "namespace RetryCase; public sealed class AfterRetry { }");
            int sourceReadAttempts = 0;
            manager.WorkspaceFileReaderForTest = (workspaceRoot, gitPath, maxBytes) =>
            {
                if (!gitPath.Equals(relativePath, StringComparison.Ordinal))
                    return GitInfo.ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath,
                        maxBytes);

                return Interlocked.Increment(ref sourceReadAttempts) == 1
                    ? new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Unavailable, null)
                    : new GitInfo.WorkspaceFileReadResult(
                        GitInfo.WorkspaceFileReadDisposition.Success, replacement);
            };

            Assert.True(manager.RequestRefreshForTest([relativePath],
                out Task refreshCompleted));
            await refreshCompleted.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(2, Volatile.Read(ref sourceReadAttempts));
            Assert.Equal("ready", manager.State);
            using var queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols("AfterRetry", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols("BeforeRetry", "exact", null, 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }
}
