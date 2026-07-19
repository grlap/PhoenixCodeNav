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
                    Interlocked.Increment(ref secondPathAttempts);
                    return Volatile.Read(ref recovered) == 0
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

            Assert.Equal(5, Volatile.Read(ref failedPathAttempts));
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
            using (var sparse = new FileStream(source, FileMode.Open, FileAccess.Write,
                       FileShare.ReadWrite))
            {
                sparse.SetLength((long)DeltaRefresher.MaxIndexedFileBytes + 1);
            }

            Assert.True(manager.RequestFullRebuild());
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
            Assert.Equal("ready", manager.State);
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
            Assert.Equal("ready", manager.State);
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
