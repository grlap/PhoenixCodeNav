using System.Diagnostics;
using CodeNav.Core.Indexing;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// Coverage for git-aware refresh (PhoenixCodeNav-jrz): GitInfo CLI queries, the
/// branch-switch reconcile end-to-end, and the git-absent fallback. Each test builds its own
/// temp git repo (these need real git + branch manipulation).
/// </summary>
public class Batch5GitRefreshTests
{
    [Fact]
    public void GitInfoReportsHeadBranchAndDiff()
    {
        if (!GitInfo.GitAvailable) return; // no git on PATH — nothing to test
        string root = NewTemp("codenav-gitinfo");
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "one");
            GitInit(root);
            GitCommitAll(root, "first");

            string? c1 = GitInfo.HeadCommit(root);
            Assert.False(string.IsNullOrEmpty(c1));
            Assert.NotNull(GitInfo.ResolveGitDir(root));
            Assert.False(string.IsNullOrEmpty(GitInfo.HeadBranch(root))); // name varies (main/master)
            GitInfo.HeadSnapshot attached = GitInfo.HeadSnapshotEx(root);
            Assert.Equal("attached", attached.Status);
            Assert.Equal(c1, attached.Commit, ignoreCase: true);
            Assert.Equal(GitInfo.HeadBranch(root), attached.Branch);
            GitInfo.HeadSnapshot failed = GitInfo.HeadSnapshotEx(root,
                Path.Combine(root, "missing-git-executable"));
            Assert.Equal("unavailable", failed.Status);
            Assert.Null(failed.Commit);
            Assert.Null(failed.Branch);

            Git(root, "checkout -q -b feature");
            File.WriteAllText(Path.Combine(root, "a.txt"), "two");
            File.WriteAllText(Path.Combine(root, "b.txt"), "new");
            GitCommitAll(root, "second");

            string? c2 = GitInfo.HeadCommit(root);
            Assert.NotEqual(c1, c2);
            Assert.Equal("feature", GitInfo.HeadBranch(root));

            var changed = GitInfo.ChangedFiles(root, c1!, c2!);
            Assert.NotNull(changed);
            Assert.Contains("a.txt", changed!);
            Assert.Contains("b.txt", changed!);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BranchSwitchReconcilesTheIndex()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-gitswitch");
        string db = IndexBuilder.DefaultDbPath(root);
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 3, seed: 5);
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"), "namespace GX { class GitAlpha { } }");
            GitInit(root);
            GitCommitAll(root, "init");

            using var m = new IndexManager(root, db);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 20000), "index did not become queryable");
            Assert.True(WaitUntil(() => m.Health().IndexedCommit != null, 20000), "git baseline commit not recorded");

            using (var q = m.OpenQueries())
            {
                Assert.NotEmpty(q.SearchSymbols("GitAlpha", "exact", null, 5));
            }

            // Switch to a branch that changes the marker file.
            Assert.Equal(0, Git(root, "checkout -q -b feature").Code);
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"), "namespace GX { class GitBeta { } }");
            GitCommitAll(root, "beta");

            Assert.True(
                WaitUntil(() =>
                {
                    using var q = m.OpenQueries();
                    return q.SearchSymbols("GitBeta", "exact", null, 5).Count > 0;
                }, 20000),
                "index did not reflect the switched-to branch");

            using var q2 = m.OpenQueries();
            Assert.NotEmpty(q2.SearchSymbols("GitBeta", "exact", null, 5));
            Assert.Empty(q2.SearchSymbols("GitAlpha", "exact", null, 5));
            // The git reconcile path (not FSW) is what advances indexed_commit — so a match to
            // the new HEAD proves the git-aware path actually ran.
            Assert.True(WaitUntil(() =>
                string.Equals(m.Health().IndexedCommit, GitInfo.HeadCommit(root), StringComparison.OrdinalIgnoreCase),
                10000), "indexed_commit did not advance to the new HEAD");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DetachedHeadReconcileClearsCachedAndPersistedBranch()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-git-detached-branch");
        string db = IndexBuilder.DefaultDbPath(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace DetachedCase { class AttachedVersion { } }");
            GitInit(root);
            GitCommitAll(root, "attached");
            string attachedCommit = GitInfo.HeadCommit(root)!;

            using var manager = new IndexManager(root, db);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000),
                manager.Health().Error);
            Assert.True(WaitUntil(() =>
                    string.Equals(manager.Health().IndexedCommit, attachedCommit,
                        StringComparison.OrdinalIgnoreCase) &&
                    manager.Health().IndexedBranch is not null,
                20_000), "initial attached Git baseline was not published");
            string attachedBranch = manager.Health().IndexedBranch!;

            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace DetachedCase { class BranchVersion { } }");
            GitCommitAll(root, "branch");
            string branchCommit = GitInfo.HeadCommit(root)!;
            Assert.NotEqual(attachedCommit, branchCommit);
            Assert.True(WaitUntil(() =>
                    string.Equals(manager.Health().IndexedCommit, branchCommit,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(manager.Health().IndexedBranch, attachedBranch,
                        StringComparison.Ordinal),
                20_000), "attached branch baseline did not advance");

            var detach = Git(root,
                $"checkout -q --detach --force {attachedCommit}");
            Assert.True(detach.Code == 0, detach.Output);
            Assert.Null(GitInfo.HeadBranch(root));
            Assert.True(WaitUntil(() =>
                    string.Equals(manager.Health().IndexedCommit, attachedCommit,
                        StringComparison.OrdinalIgnoreCase),
                20_000), "detached HEAD commit was not reconciled");

            Assert.Null(manager.Health().IndexedBranch);
            using (var queries = manager.OpenQueries())
            {
                Assert.Single(queries.SearchSymbols(
                    "AttachedVersion", "exact", null, 2));
                Assert.Empty(queries.SearchSymbols(
                    "BranchVersion", "exact", null, 2));
            }

            using var followerReader = new IndexQueries(db);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(attachedCommit, metadata.IndexedCommit,
                ignoreCase: true);
            Assert.Null(metadata.IndexedBranch);
            IndexHealth followerHealth = IndexManager.FollowerHealthForTest(
                metadata, databaseBytes: 1, root, db);
            Assert.Equal("ready", followerHealth.State);
            Assert.Equal(attachedCommit, followerHealth.IndexedCommit,
                ignoreCase: true);
            Assert.Null(followerHealth.IndexedBranch);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SameCommitAttachmentChangesRefreshBranchMetadata()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-git-same-commit-branch");
        string db = IndexBuilder.DefaultDbPath(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace SameCommitCase { class Marker { } }");
            GitInit(root);
            GitCommitAll(root, "attached");
            string commit = GitInfo.HeadCommit(root)!;

            using var manager = new IndexManager(root, db);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000),
                manager.Health().Error);
            Assert.True(WaitUntil(() =>
                    string.Equals(manager.Health().IndexedCommit, commit,
                        StringComparison.OrdinalIgnoreCase) &&
                    manager.Health().IndexedBranch is not null,
                20_000), "initial attached Git baseline was not published");

            var detach = Git(root, "checkout -q --detach --force HEAD");
            Assert.True(detach.Code == 0, detach.Output);
            Assert.Equal(commit, GitInfo.HeadCommit(root), ignoreCase: true);
            Assert.Null(GitInfo.HeadBranch(root));
            GitInfo.HeadSnapshot detachedSnapshot = GitInfo.HeadSnapshotEx(root);
            Assert.Equal("detached", detachedSnapshot.Status);
            Assert.Equal(commit, detachedSnapshot.Commit, ignoreCase: true);
            Assert.Null(detachedSnapshot.Branch);
            Assert.True(WaitUntil(() => manager.Health().IndexedBranch is null, 20_000),
                "same-commit detachment did not clear indexed_branch");

            using (var followerReader = new IndexQueries(db))
            {
                IndexMetadataSnapshot detached = followerReader.ReadMetadata();
                Assert.Equal(commit, detached.IndexedCommit, ignoreCase: true);
                Assert.Null(detached.IndexedBranch);
            }

            var attach = Git(root, "checkout -q -b same-oid-alias");
            Assert.True(attach.Code == 0, attach.Output);
            Assert.Equal(commit, GitInfo.HeadCommit(root), ignoreCase: true);
            Assert.Equal("same-oid-alias", GitInfo.HeadBranch(root));
            Assert.True(WaitUntil(() =>
                    string.Equals(manager.Health().IndexedBranch, "same-oid-alias",
                        StringComparison.Ordinal),
                20_000), "same-commit reattachment did not publish indexed_branch");

            using var finalReader = new IndexQueries(db);
            IndexMetadataSnapshot attached = finalReader.ReadMetadata();
            Assert.Equal(commit, attached.IndexedCommit, ignoreCase: true);
            Assert.Equal("same-oid-alias", attached.IndexedBranch);
            IndexHealth followerHealth = IndexManager.FollowerHealthForTest(
                attached, databaseBytes: 1, root, db);
            Assert.Equal("same-oid-alias", followerHealth.IndexedBranch);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void UnavailableHeadSnapshotDoesNotEraseAttachedBranch()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-git-branch-unavailable");
        string db = IndexBuilder.DefaultDbPath(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace BranchUnavailableCase { class Marker { } }");
            GitInit(root);
            GitCommitAll(root, "attached");

            using var manager = new IndexManager(root, db);
            manager.Start();
            Assert.True(WaitUntil(() =>
                    manager.IsQueryable &&
                    manager.Health().IndexedBranch is not null,
                20_000), manager.Health().Error);
            string branch = manager.Health().IndexedBranch!;
            string commit = manager.Health().IndexedCommit!;

            manager.GitHeadSnapshotForTest = () =>
                new GitInfo.HeadSnapshot(null, null, "unavailable");
            manager.NotifyGitHeadChangedForTest();

            Assert.Equal(commit, manager.Health().IndexedCommit,
                ignoreCase: true);
            Assert.Equal(branch, manager.Health().IndexedBranch);
            using var followerReader = new IndexQueries(db);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(commit, metadata.IndexedCommit, ignoreCase: true);
            Assert.Equal(branch, metadata.IndexedBranch);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RestartReconcilesSameCommitDetachedMetadata()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-git-restart-detached");
        string db = IndexBuilder.DefaultDbPath(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace RestartDetachedCase { class Marker { } }");
            GitInit(root);
            GitCommitAll(root, "attached");
            string commit = GitInfo.HeadCommit(root)!;

            using (var attachedManager = new IndexManager(root, db))
            {
                attachedManager.Start();
                Assert.True(WaitUntil(() =>
                        string.Equals(attachedManager.Health().IndexedCommit, commit,
                            StringComparison.OrdinalIgnoreCase) &&
                        attachedManager.Health().IndexedBranch is not null,
                    20_000), "initial attached baseline was not published");
            }

            var detach = Git(root, "checkout -q --detach --force HEAD");
            Assert.True(detach.Code == 0, detach.Output);
            Assert.Equal(commit, GitInfo.HeadCommit(root), ignoreCase: true);

            using var restarted = new IndexManager(root, db);
            restarted.Start();
            Assert.True(WaitUntil(() =>
                    restarted.IsQueryable &&
                    string.Equals(restarted.Health().IndexedCommit, commit,
                        StringComparison.OrdinalIgnoreCase) &&
                    restarted.Health().IndexedBranch is null,
                20_000), "restart did not reconcile same-commit detached metadata");

            using var followerReader = new IndexQueries(db);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(commit, metadata.IndexedCommit, ignoreCase: true);
            Assert.Null(metadata.IndexedBranch);
            Assert.Null(IndexManager.FollowerHealthForTest(
                metadata, databaseBytes: 1, root, db).IndexedBranch);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task ReversedSameCommitAttachmentSignalsPublishLatestObservation()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-git-attachment-queue");
        string db = IndexBuilder.DefaultDbPath(root);
        using var releasePump = new ManualResetEventSlim(initialState: false);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace AttachmentQueueCase { class Marker { } }");
            GitInit(root);
            GitCommitAll(root, "attached");

            using var manager = new IndexManager(root, db);
            manager.Start();
            Assert.True(WaitUntil(() =>
                    manager.IsQueryable &&
                    manager.Health().IndexedBranch is not null,
                20_000), manager.Health().Error);
            string commit = manager.Health().IndexedCommit!;
            string branch = manager.Health().IndexedBranch!;

            using var pumpBlocked = new ManualResetEventSlim(initialState: false);
            int blockOnce = 0;
            manager.RefreshRequestDequeuedForTest = () =>
            {
                if (Interlocked.Exchange(ref blockOnce, 1) != 0) return;
                pumpBlocked.Set();
                releasePump.Wait(TimeSpan.FromSeconds(20));
            };
            Assert.True(manager.RequestRefreshForTest(
                Array.Empty<string>(), out Task blockingRefresh));
            Assert.True(pumpBlocked.Wait(TimeSpan.FromSeconds(10)),
                "refresh pump did not reach the deterministic blocker");

            manager.GitHeadSnapshotForTest = () =>
                new GitInfo.HeadSnapshot(commit, null, "detached");
            manager.NotifyGitHeadChangedForTest();
            manager.GitHeadSnapshotForTest = () =>
                new GitInfo.HeadSnapshot(commit, branch, "attached");
            manager.NotifyGitHeadChangedForTest();
            Assert.True(manager.RequestRefreshForTest(
                Array.Empty<string>(), out Task fifoBarrier));

            releasePump.Set();
            await blockingRefresh.WaitAsync(TimeSpan.FromSeconds(20));
            await fifoBarrier.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(commit, manager.Health().IndexedCommit,
                ignoreCase: true);
            Assert.Equal(branch, manager.Health().IndexedBranch);
            using var followerReader = new IndexQueries(db);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(commit, metadata.IndexedCommit, ignoreCase: true);
            Assert.Equal(branch, metadata.IndexedBranch);
            Assert.Equal(branch, IndexManager.FollowerHealthForTest(
                metadata, databaseBytes: 1, root, db).IndexedBranch);
        }
        finally
        {
            releasePump.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ConcurrentHeadCallbacksSerializeSnapshotAcquisition()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-git-observation-gate");
        string db = IndexBuilder.DefaultDbPath(root);
        using var releasePump = new ManualResetEventSlim(initialState: false);
        using var releaseFirstSnapshot = new ManualResetEventSlim(initialState: false);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace ObservationGateCase { class Marker { } }");
            GitInit(root);
            GitCommitAll(root, "attached");

            using var manager = new IndexManager(root, db);
            manager.Start();
            Assert.True(WaitUntil(() =>
                    manager.IsQueryable &&
                    manager.Health().IndexedBranch is not null,
                20_000), manager.Health().Error);
            string commit = manager.Health().IndexedCommit!;
            string branch = manager.Health().IndexedBranch!;

            using var pumpBlocked = new ManualResetEventSlim(initialState: false);
            int blockOnce = 0;
            manager.RefreshRequestDequeuedForTest = () =>
            {
                if (Interlocked.Exchange(ref blockOnce, 1) != 0) return;
                pumpBlocked.Set();
                releasePump.Wait(TimeSpan.FromSeconds(20));
            };
            Assert.True(manager.RequestRefreshForTest(
                Array.Empty<string>(), out Task blockingRefresh));
            Assert.True(pumpBlocked.Wait(TimeSpan.FromSeconds(10)),
                "refresh pump did not reach the deterministic blocker");

            using var firstSnapshotEntered = new ManualResetEventSlim(initialState: false);
            using var secondSnapshotEntered = new ManualResetEventSlim(initialState: false);
            int snapshotCalls = 0;
            manager.GitHeadSnapshotForTest = () =>
            {
                int call = Interlocked.Increment(ref snapshotCalls);
                if (call == 1)
                {
                    // Select the older detached observation, then hold its return. Without
                    // acquisition inside the observation gate, the newer attached callback
                    // returns first and is discarded as a duplicate of the published tuple.
                    firstSnapshotEntered.Set();
                    if (!releaseFirstSnapshot.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("first Git snapshot was not released");
                    return new GitInfo.HeadSnapshot(commit, null, "detached");
                }

                secondSnapshotEntered.Set();
                return new GitInfo.HeadSnapshot(commit, branch, "attached");
            };

            Task firstNotification = Task.Factory.StartNew(
                manager.NotifyGitHeadChangedForTest,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(firstSnapshotEntered.Wait(TimeSpan.FromSeconds(10)),
                "first callback did not begin snapshot acquisition");

            using var secondNotificationStarted =
                new ManualResetEventSlim(initialState: false);
            Task secondNotification = Task.Factory.StartNew(
                () =>
                {
                    secondNotificationStarted.Set();
                    manager.NotifyGitHeadChangedForTest();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(secondNotificationStarted.Wait(TimeSpan.FromSeconds(10)),
                "second callback thread did not start");
            bool snapshotAcquisitionOverlapped =
                secondSnapshotEntered.Wait(TimeSpan.FromSeconds(2));

            releaseFirstSnapshot.Set();
            await firstNotification.WaitAsync(TimeSpan.FromSeconds(20));
            await secondNotification.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(snapshotAcquisitionOverlapped,
                "a newer callback acquired Git HEAD while the older snapshot was in flight");
            Assert.True(manager.RequestRefreshForTest(
                Array.Empty<string>(), out Task fifoBarrier));

            releasePump.Set();
            await blockingRefresh.WaitAsync(TimeSpan.FromSeconds(20));
            await fifoBarrier.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(commit, manager.Health().IndexedCommit,
                ignoreCase: true);
            Assert.Equal(branch, manager.Health().IndexedBranch);
            using var followerReader = new IndexQueries(db);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(commit, metadata.IndexedCommit, ignoreCase: true);
            Assert.Equal(branch, metadata.IndexedBranch);
            Assert.Equal(branch, IndexManager.FollowerHealthForTest(
                metadata, databaseBytes: 1, root, db).IndexedBranch);
        }
        finally
        {
            releaseFirstSnapshot.Set();
            releasePump.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ReversedQueuedCommitSignalsRecomputePathsFromPublishedBaseline()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-git-commit-queue");
        string db = IndexBuilder.DefaultDbPath(root);
        using var releasePump = new ManualResetEventSlim(initialState: false);
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace CommitQueueCase { class VersionA { } }");
            GitInit(root);
            GitCommitAll(root, "version-a");
            string commitA = GitInfo.HeadCommit(root)!;
            string branchA = GitInfo.HeadBranch(root)!;

            Assert.Equal(0, Git(root, "checkout -q -b transient").Code);
            File.WriteAllText(Path.Combine(root, "GitMarker.cs"),
                "namespace CommitQueueCase { class VersionB { } }");
            GitCommitAll(root, "version-b");
            string commitB = GitInfo.HeadCommit(root)!;
            Assert.NotEqual(commitA, commitB);
            Assert.Equal(0, Git(root, $"checkout -q --force {branchA}").Code);

            using var manager = new IndexManager(root, db);
            manager.Start();
            Assert.True(WaitUntil(() =>
                    manager.IsQueryable &&
                    string.Equals(manager.Health().IndexedCommit, commitA,
                        StringComparison.OrdinalIgnoreCase),
                20_000), manager.Health().Error);

            using var pumpBlocked = new ManualResetEventSlim(initialState: false);
            int blockOnce = 0;
            manager.RefreshRequestDequeuedForTest = () =>
            {
                if (Interlocked.Exchange(ref blockOnce, 1) != 0) return;
                pumpBlocked.Set();
                releasePump.Wait(TimeSpan.FromSeconds(20));
            };
            Assert.True(manager.RequestRefreshForTest(
                Array.Empty<string>(), out Task blockingRefresh));
            Assert.True(pumpBlocked.Wait(TimeSpan.FromSeconds(10)),
                "refresh pump did not reach the deterministic blocker");

            Assert.Equal(0, Git(root, "checkout -q --force transient").Code);
            manager.NotifyGitHeadChangedForTest();
            Assert.Equal(0, Git(root, $"checkout -q --force {branchA}").Code);
            manager.NotifyGitHeadChangedForTest();
            Assert.True(manager.RequestRefreshForTest(
                Array.Empty<string>(), out Task fifoBarrier));

            releasePump.Set();
            await blockingRefresh.WaitAsync(TimeSpan.FromSeconds(20));
            await fifoBarrier.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal(commitA, manager.Health().IndexedCommit,
                ignoreCase: true);
            Assert.Equal(branchA, manager.Health().IndexedBranch);
            using (var queries = manager.OpenQueries())
            {
                Assert.Single(queries.SearchSymbols(
                    "VersionA", "exact", null, 2));
                Assert.Empty(queries.SearchSymbols(
                    "VersionB", "exact", null, 2));
            }
            using var followerReader = new IndexQueries(db);
            IndexMetadataSnapshot metadata = followerReader.ReadMetadata();
            Assert.Equal(commitA, metadata.IndexedCommit, ignoreCase: true);
            Assert.Equal(branchA, metadata.IndexedBranch);
            Assert.Equal(branchA, IndexManager.FollowerHealthForTest(
                metadata, databaseBytes: 1, root, db).IndexedBranch);
        }
        finally
        {
            releasePump.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public void PlainCommitOnSameBranchReconcilesTheIndex()
    {
        if (!GitInfo.GitAvailable) return;
        string root = NewTemp("codenav-gitcommit");
        string db = IndexBuilder.DefaultDbPath(root);
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 3, seed: 11);
            File.WriteAllText(Path.Combine(root, "CommitMarker.cs"), "namespace CX { class CommitAlpha { } }");
            GitInit(root);
            GitCommitAll(root, "init");

            using var m = new IndexManager(root, db);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 20000), "index did not become queryable");
            Assert.True(WaitUntil(() => m.Health().IndexedCommit != null, 20000), "git baseline commit not recorded");
            string? baseline = m.Health().IndexedCommit;

            using (var q = m.OpenQueries())
            {
                Assert.NotEmpty(q.SearchSymbols("CommitAlpha", "exact", null, 5));
            }

            // Commit on the SAME branch — no checkout. Only refs/heads/<branch> + logs/HEAD move,
            // so this exercises the logs/HEAD reflog watch specifically: a plain commit leaves the
            // top-level pointer files (HEAD, packed-refs, ...) untouched, so without that watch the
            // git reconcile path never runs and indexed_commit would stay at the baseline.
            File.WriteAllText(Path.Combine(root, "CommitMarker.cs"), "namespace CX { class CommitGamma { } }");
            GitCommitAll(root, "gamma");

            Assert.True(
                WaitUntil(() =>
                    string.Equals(m.Health().IndexedCommit, GitInfo.HeadCommit(root), StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(m.Health().IndexedCommit, baseline, StringComparison.OrdinalIgnoreCase),
                    20000),
                "indexed_commit did not advance to the new HEAD after a plain commit");

            using var q2 = m.OpenQueries();
            Assert.NotEmpty(q2.SearchSymbols("CommitGamma", "exact", null, 5));
            Assert.Empty(q2.SearchSymbols("CommitAlpha", "exact", null, 5));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void NonGitWorkspaceIndexesWithoutGitTracking()
    {
        string root = NewTemp("codenav-nogit");
        string db = IndexBuilder.DefaultDbPath(root);
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 3, seed: 9); // NOT a git repo
            Assert.Null(GitInfo.ResolveGitDir(root));

            using var m = new IndexManager(root, db);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 20000));
            Assert.Null(m.Health().IndexedCommit); // no git tracking, no crash

            using var q = m.OpenQueries();
            Assert.True(q.Overview().CsFiles > 0); // still indexed fine
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- helpers

    private static string NewTemp(string prefix) =>
        Path.GetFullPath(Directory.CreateTempSubdirectory(prefix).FullName);

    private static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(100);
        }
        return cond();
    }

    private static (int Code, string Output) Git(string dir, string args)
    {
        // Routed through the hang-proof runner (review: this helper had the exact pre-hotfix
        // ReadToEnd-before-WaitForExit shape AND runs index-refreshing commands like `add -A`,
        // which DO consult fsmonitor — on a dev machine with global core.fsmonitor=true the spawned
        // daemon would inherit the pipe and hang the entire suite).
        string? outp = GitInfo.RunProcess("git", dir,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);
        return outp is null ? (1, "") : (0, outp);
    }

    private static void GitInit(string dir)
    {
        Git(dir, "init -q");
        Git(dir, "config user.email test@example.com");
        Git(dir, "config user.name CodeNavTest");
        Git(dir, "config commit.gpgsign false");
    }

    private static void GitCommitAll(string dir, string message)
    {
        Git(dir, "add -A");
        Git(dir, $"commit -q -m \"{message}\"");
    }

    private static void Cleanup(string root)
    {
        TestWorkspaceCleanup.DeleteWorkspace(root);
    }
}
