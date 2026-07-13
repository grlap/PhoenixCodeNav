using System.Diagnostics;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeNav.Tests;

[CollectionDefinition("Batch45 index follower isolation", DisableParallelization = true)]
public sealed class Batch45IndexFollowerCollection;

/// <summary>
/// Batch 45 (smgs) - one Phoenix writer owns index mutation while additional Phoenix
/// processes attach to the same committed SQLite WAL state as explicit read-only followers.
/// The process regression is Windows-only because that is the supported deployment target and
/// because foreign pooled file handles are most likely to break the writer's destructive rebuild.
/// </summary>
[Collection("Batch45 index follower isolation")]
public sealed class Batch45IndexFollowerTests
{
    [Fact]
    public void TransientLivenessProbeContentionDoesNotTurnSuccessorIntoFollower()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-45-probe-race").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        IndexOwnershipLease? transientProbe = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            Assert.True(IndexOwnershipLease.TryAcquire(root, database, out transientProbe));
            int contentions = 0;
            using var successor = new IndexManager(root, database)
            {
                StartupAfterLeaseContentionForTest = () =>
                {
                    Interlocked.Increment(ref contentions);
                    transientProbe?.Dispose();
                    transientProbe = null;
                },
            };

            successor.Start();

            Assert.Equal(1, Volatile.Read(ref contentions));
            Assert.True(WaitUntil(() => successor.IsQueryable || successor.State == "failed",
                20_000));
            Assert.True(successor.IsQueryable, successor.Health().Error);
            Assert.True(successor.IsWriter);
            Assert.Equal("writer", successor.AccessMode);
        }
        finally
        {
            transientProbe?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void ContendingManagerBecomesQueryableFollowerAndRejectsEveryMutationPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-45-follower").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        IndexManager? follower = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);

            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            Assert.True(writer.IsWriter);
            Assert.Equal("writer", writer.AccessMode);
            Assert.Equal("writer", writer.Health().AccessMode);

            follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable || follower.State == "failed", 20_000),
                "the contending manager never attached or failed");
            Assert.True(follower.IsQueryable, follower.Health().Error);
            Assert.False(follower.IsWriter);
            Assert.Equal("follower", follower.AccessMode);
            Assert.Equal("follower", follower.Health().AccessMode);

            using var semantic = new SemanticService(follower);
            var tools = new NavigationTools(follower, semantic);
            JsonElement capabilities = Parse(tools.ServerCapabilities());
            Assert.Equal("follower",
                capabilities.GetProperty("index").GetProperty("mode").GetString());
            Assert.False(capabilities.GetProperty("index")
                .GetProperty("pendingChangesKnown").GetBoolean());
            Assert.Contains(capabilities.GetProperty("features").EnumerateArray(), feature =>
                feature.GetProperty("id").GetString() == "index-read-followers");

            JsonElement search = Parse(tools.SearchSymbol("Alpha45", match: "exact"));
            Assert.Single(search.GetProperty("symbols").EnumerateArray());
            Assert.Equal("follower",
                search.GetProperty("meta").GetProperty("indexMode").GetString());
            string statusNote = search.GetProperty("meta").GetProperty("statusNote").GetString()!;
            Assert.Contains("index-backed evidence reflects committed writer state", statusNote);
            Assert.Contains("live source, Git, and semantic evidence may be newer", statusNote);

            JsonElement definition = Parse(tools.Definition(
                name: "Alpha45", mode: "auto", timeoutMs: 30_000));
            Assert.False(definition.TryGetProperty("error", out _), definition.ToString());
            Assert.Equal("follower",
                definition.GetProperty("meta").GetProperty("indexMode").GetString());

            AssertWriterRequired(Parse(tools.RefreshIndex()));
            AssertWriterRequired(Parse(tools.RefreshIndex(force: "incremental")));
            AssertWriterRequired(Parse(tools.RefreshIndex(force: "full")));

            string sibling = Path.Combine(Path.GetDirectoryName(root)!,
                Path.GetFileName(root) + "-unowned-worktree");
            AssertWriterRequired(Parse(tools.IndexWorktree(sibling)));
            Assert.False(File.Exists(IndexBuilder.DefaultDbPath(sibling)));

            Assert.False(follower.RequestRefresh());
            Assert.False(follower.RequestFullRebuild());
            Assert.Equal("index_writer_required",
                follower.EnsureWorktreeIndex(sibling, "auto", _ => { }).Action);

            follower.Dispose();
            follower = null;
            Assert.True(IndexOwnershipLease.IsHeld(root, database),
                "disposing a follower must not release the writer's ownership lease");
            Assert.True(writer.IsQueryable);
            using var writerQuery = writer.OpenQueries();
            Assert.Single(writerQuery.SearchSymbols("Alpha45", "exact", null, 2));
        }
        finally
        {
            follower?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void FollowerProvenanceDistinguishesCommittedIndexFromLiveWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-45-provenance").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var boundary = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            using var follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable, 20_000), follower.Health().Error);

            writer.FullRebuildDestructiveBoundaryForTest = _ =>
            {
                boundary.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            };
            string oldVersion = writer.Health().IndexVersion!;
            Assert.True(writer.RequestFullRebuild());
            Assert.True(boundary.Wait(TimeSpan.FromSeconds(10)),
                "writer did not reach the blocked rebuild boundary");

            File.WriteAllText(Path.Combine(root, "Beta.cs"),
                "namespace Batch45; public class Beta45 { }");
            using var semantic = new SemanticService(follower);
            var tools = new NavigationTools(follower, semantic);

            JsonElement indexed = Parse(tools.SearchSymbol("Beta45", match: "exact"));
            Assert.Empty(indexed.GetProperty("symbols").EnumerateArray());
            Assert.Equal("follower",
                indexed.GetProperty("meta").GetProperty("indexMode").GetString());

            JsonElement live = Parse(tools.SourceContext("Beta.cs", "1", contextLines: 0));
            Assert.Equal("live", live.GetProperty("freshness").GetString());
            Assert.Contains("Beta45", live.GetProperty("spans")[0]
                .GetProperty("source").GetString());
            Assert.Equal("follower",
                live.GetProperty("meta").GetProperty("indexMode").GetString());
            string statusNote = live.GetProperty("meta").GetProperty("statusNote").GetString()!;
            Assert.Contains("index-backed evidence reflects committed writer state", statusNote);
            Assert.Contains("live source, Git, and semantic evidence may be newer", statusNote);

            JsonElement capabilities = Parse(tools.ServerCapabilities());
            Assert.False(capabilities.GetProperty("index")
                .GetProperty("pendingChangesKnown").GetBoolean());

            release.Set();
            Assert.True(WaitUntil(() => writer.State == "failed" ||
                (writer.IsQueryable && writer.Health().IndexVersion != oldVersion), 40_000),
                "writer did not finish the released rebuild");
            Assert.True(writer.IsQueryable, writer.Health().Error);
            Assert.True(WaitUntil(() => HasSymbol(tools, "Beta45"), 10_000),
                "follower did not observe Beta45 after the committed rebuild");
        }
        finally
        {
            release.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task FollowerMetadataPublicationIsAtomicAndCaptureOrdered()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-45-metadata-order").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var captured = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var secondStarted = new ManualResetEventSlim(false);
        using var secondReachedGate = new ManualResetEventSlim(false);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            Assert.True(IndexOwnershipLease.TryAcquire(root, database,
                out IndexOwnershipLease? owner));
            using var ownership = owner!;
            using var follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable, 20_000), follower.Health().Error);

            WriteFollowerMetadata(database, "2026-07-11T01:00:00.0000000Z",
                "commit-a", "branch-a");
            using (follower.OpenQueries()) { }
            Assert.Equal("commit-a", follower.FollowerMetadataForTest?.IndexedCommit);
            Assert.Equal("branch-a", follower.FollowerMetadataForTest?.IndexedBranch);

            int blocked = 0;
            follower.FollowerMetadataBeforePublishForTest = metadata =>
            {
                if (metadata.IndexedCommit != "commit-a" ||
                    Interlocked.Exchange(ref blocked, 1) != 0)
                    return;
                captured.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            };
            follower.FollowerMetadataBeforeGateForTest = () =>
            {
                if (captured.IsSet) secondReachedGate.Set();
            };

            Task first = Task.Run(() =>
            {
                using var query = follower.OpenQueries();
            });
            Assert.True(captured.Wait(TimeSpan.FromSeconds(10)),
                "older follower metadata was not captured at the publication seam");

            WriteFollowerMetadata(database, "2026-07-11T02:00:00.0000000Z",
                "commit-b", "branch-b");
            Task second = Task.Run(() =>
            {
                secondStarted.Set();
                using var query = follower.OpenQueries();
            });
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(secondReachedGate.Wait(TimeSpan.FromSeconds(5)),
                "newer follower metadata request never reached the publication gate");
            await Task.Delay(100);
            Assert.False(second.IsCompleted,
                "a newer metadata capture bypassed the serialized publication gate");

            IndexMetadataSnapshot? whileBlocked = follower.FollowerMetadataForTest;
            Assert.Equal("commit-a", whileBlocked?.IndexedCommit);
            Assert.Equal("branch-a", whileBlocked?.IndexedBranch);
            Assert.Equal("2026-07-11T01:00:00.0000000Z", whileBlocked?.LastRefreshUtc);

            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            follower.FollowerMetadataBeforePublishForTest = null;
            follower.FollowerMetadataBeforeGateForTest = null;

            IndexMetadataSnapshot? published = follower.FollowerMetadataForTest;
            Assert.Equal("commit-b", published?.IndexedCommit);
            Assert.Equal("branch-b", published?.IndexedBranch);
            Assert.Equal("2026-07-11T02:00:00.0000000Z", published?.LastRefreshUtc);
            IndexHealth health = follower.Health();
            Assert.Equal(published?.IndexedCommit, health.IndexedCommit);
            Assert.Equal(published?.IndexedBranch, health.IndexedBranch);
            Assert.Equal(published?.LastRefreshUtc, health.LastRefreshUtc);
        }
        finally
        {
            release.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public void FollowerReviewSnapshotDrainsBeforeWriterReplacesDatabase()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-45-review-drain").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var waiting = new ManualResetEventSlim(false);
        using var boundary = new ManualResetEventSlim(false);
        using var completed = new ManualResetEventSlim(false);
        IndexReadSnapshot? snapshot = null;
        IndexReadSnapshot? secondSnapshot = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            using var follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable, 20_000), follower.Health().Error);

            snapshot = follower.TryOpenReviewSnapshot();
            Assert.NotNull(snapshot);
            secondSnapshot = follower.TryOpenReviewSnapshot();
            Assert.NotNull(secondSnapshot);
            string oldVersion = writer.Health().IndexVersion!;
            writer.FullRebuildReviewWaitTimeoutForTest = TimeSpan.FromMilliseconds(100);
            int activeAtBoundary = -1;
            writer.FullRebuildWaitingForReviewSnapshotsForTest = () => waiting.Set();
            writer.FullRebuildDestructiveBoundaryForTest = active =>
            {
                activeAtBoundary = active;
                boundary.Set();
            };
            writer.FullRebuildCompletedForTest = () => completed.Set();

            Assert.True(writer.RequestFullRebuild());
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)),
                "writer never observed the follower's cross-manager review slot");
            Assert.True(WaitUntil(() => writer.Health().Error?.Contains(
                    "waiting for active index readers", StringComparison.OrdinalIgnoreCase) == true, 5_000),
                "writer did not report the active-reader wait");
            Assert.False(boundary.IsSet,
                "writer crossed the destructive boundary while a follower snapshot was active");
            Assert.Equal("ready", writer.State);
            Assert.True(writer.IsQueryable, writer.Health().Error);
            Assert.Equal(oldVersion, writer.Health().IndexVersion);

            snapshot.Dispose();
            snapshot = null;
            secondSnapshot.Dispose();
            secondSnapshot = null;
            Assert.True(boundary.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, activeAtBoundary);
            Assert.True(completed.Wait(TimeSpan.FromSeconds(40)),
                "writer did not complete after the follower released its review slot");
            Assert.True(WaitUntil(() => writer.IsQueryable &&
                writer.Health().IndexVersion != oldVersion, 20_000), writer.Health().Error);

            using var semantic = new SemanticService(follower);
            var tools = new NavigationTools(follower, semantic);
            Assert.True(WaitUntil(() => HasSymbol(tools, "Alpha45"), 10_000),
                "follower could not query the replacement index after releasing its snapshot");
        }
        finally
        {
            snapshot?.Dispose();
            secondSnapshot?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void FullRebuildQueuedDuringStartupWaitsForStartupBuild()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-startup-serialization").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var startupEntered = new ManualResetEventSlim(false);
        using var releaseStartup = new ManualResetEventSlim(false);
        using var requestDequeued = new ManualResetEventSlim(false);
        using var requestPassedStartup = new ManualResetEventSlim(false);
        using var destructiveBoundary = new ManualResetEventSlim(false);
        using var rebuildCompleted = new ManualResetEventSlim(false);
        try
        {
            WriteWorkspace(root);
            using var manager = new IndexManager(root, database)
            {
                StartupAfterLeaseAcquiredForTest = () =>
                {
                    startupEntered.Set();
                    Assert.True(releaseStartup.Wait(TimeSpan.FromSeconds(15)));
                },
                RefreshRequestDequeuedForTest = () => requestDequeued.Set(),
                RefreshRequestPassedStartupBarrierForTest = () =>
                    requestPassedStartup.Set(),
                FullRebuildDestructiveBoundaryForTest = _ => destructiveBoundary.Set(),
                FullRebuildCompletedForTest = () => rebuildCompleted.Set(),
            };
            manager.Start(forceRebuild: true);
            Assert.True(startupEntered.Wait(TimeSpan.FromSeconds(10)));

            Assert.True(manager.RequestFullRebuild());
            Assert.True(requestDequeued.Wait(TimeSpan.FromSeconds(10)),
                "refresh pump did not dequeue the full rebuild requested during startup");
            Assert.False(requestPassedStartup.Wait(TimeSpan.FromMilliseconds(250)),
                "refresh pump passed the startup barrier before startup completed");
            Assert.False(destructiveBoundary.IsSet,
                "refresh pump crossed the destructive boundary before startup completed");

            releaseStartup.Set();
            Assert.True(rebuildCompleted.Wait(TimeSpan.FromSeconds(40)),
                "queued full rebuild did not run after startup completed");
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000), manager.Health().Error);
        }
        finally
        {
            releaseStartup.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public void SuccessorStartupRebuildDefersWhileFollowerReviewSnapshotIsActive()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-startup-review-drain").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var waiting = new ManualResetEventSlim(false);
        using var boundary = new ManualResetEventSlim(false);
        using var completed = new ManualResetEventSlim(false);
        IndexManager? writer = null;
        IndexManager? follower = null;
        IndexManager? successor = null;
        IndexReadSnapshot? snapshot = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            string oldVersion = writer.Health().IndexVersion!;

            follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable, 20_000),
                follower.Health().Error);
            snapshot = follower.TryOpenReviewSnapshot();
            Assert.NotNull(snapshot);

            writer.Dispose();
            writer = null;
            Assert.True(WaitUntil(() => !IndexOwnershipLease.IsHeld(root, database), 10_000),
                "original writer lease remained held during successor startup");

            successor = new IndexManager(root, database)
            {
                FullRebuildReviewWaitTimeoutForTest = TimeSpan.FromMilliseconds(100),
                FullRebuildWaitingForReviewSnapshotsForTest = () => waiting.Set(),
                FullRebuildDestructiveBoundaryForTest = _ => boundary.Set(),
                FullRebuildCompletedForTest = () => completed.Set(),
            };
            successor.Start(forceRebuild: true);
            Assert.True(WaitUntil(() => waiting.IsSet || boundary.IsSet, 10_000),
                "successor startup reached neither reader coordination nor its rebuild boundary");
            Assert.True(waiting.IsSet,
                "successor startup bypassed the surviving follower review slot");
            Assert.True(WaitUntil(() => successor.State == "building" &&
                successor.Health().Error?.Contains("waiting for active index readers",
                    StringComparison.OrdinalIgnoreCase) == true, 5_000),
                successor.Health().Error);
            Assert.True(successor.IsWriter);
            Assert.False(boundary.IsSet,
                "successor startup crossed the destructive boundary with an active reader");
            Assert.Equal(oldVersion, ReadMeta(database, "index_version"));

            snapshot.Dispose();
            snapshot = null;
            Assert.True(boundary.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(completed.Wait(TimeSpan.FromSeconds(40)),
                "successor did not rebuild after the surviving follower released its snapshot");
            Assert.True(WaitUntil(() => successor.IsQueryable &&
                successor.Health().IndexVersion != oldVersion, 20_000),
                successor.Health().Error);

            using var semantic = new SemanticService(follower);
            var tools = new NavigationTools(follower, semantic);
            Assert.True(WaitUntil(() => HasSymbol(tools, "Alpha45"), 10_000),
                "surviving follower could not query the successor's replacement index");
        }
        finally
        {
            snapshot?.Dispose();
            successor?.Dispose();
            follower?.Dispose();
            writer?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void DirectBuildDefersWhileFollowerReviewSnapshotIsActive()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-direct-build-review-drain").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var waiting = new ManualResetEventSlim(false);
        IndexManager? writer = null;
        IndexManager? follower = null;
        IndexReadSnapshot? snapshot = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            string oldVersion = writer.Health().IndexVersion!;

            follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable, 20_000),
                follower.Health().Error);
            snapshot = follower.TryOpenReviewSnapshot();
            Assert.NotNull(snapshot);

            writer.Dispose();
            writer = null;
            Assert.True(WaitUntil(() => !IndexOwnershipLease.IsHeld(root, database), 10_000));

            IOException deferred = Assert.Throws<IOException>(() =>
                IndexBuilder.BuildWithReviewWaitForTest(root, database,
                    TimeSpan.FromMilliseconds(100), () => waiting.Set()));
            Assert.True(waiting.IsSet,
                "direct build never observed the surviving follower review slot");
            Assert.Contains("review snapshot", deferred.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(oldVersion, ReadMeta(database, "index_version"));

            snapshot.Dispose();
            snapshot = null;
            BuildResult rebuilt = IndexBuilder.BuildWithReviewWaitForTest(root, database,
                TimeSpan.FromSeconds(5));
            Assert.True(rebuilt.CsFiles > 0);
            Assert.NotEqual(oldVersion, ReadMeta(database, "index_version"));
        }
        finally
        {
            snapshot?.Dispose();
            follower?.Dispose();
            writer?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task WindowsForeignFollowerSnapshotDoesNotFailWriterRebuild()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-foreign-review-drain").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        IndexReadSnapshot? snapshot = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            await using McpClient writer = await StartPhoenixClientAsync(root, database);
            JsonElement initial = await WaitForWriterCapabilitiesAsync(writer,
                index => index.GetProperty("state").GetString() == "ready", 20_000);
            string oldVersion = initial.GetProperty("indexVersion").GetString()!;

            using var follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable, 20_000), follower.Health().Error);
            snapshot = follower.TryOpenReviewSnapshot();
            Assert.NotNull(snapshot);

            JsonElement queued = await CallJsonAsync(writer, "refresh_index",
                new Dictionary<string, object?> { ["force"] = "full" });
            Assert.True(queued.GetProperty("queued").GetBoolean());

            JsonElement held = await WaitForWriterCapabilitiesAsync(writer,
                index => index.TryGetProperty("error", out JsonElement error) &&
                         error.ValueKind == JsonValueKind.String &&
                         error.GetString()!.Contains("waiting for active index readers",
                              StringComparison.OrdinalIgnoreCase),
                45_000);
            Assert.NotEqual("failed", held.GetProperty("state").GetString());
            Assert.Equal("writer", held.GetProperty("mode").GetString());
            Assert.Equal(oldVersion, held.GetProperty("indexVersion").GetString());

            snapshot.Dispose();
            snapshot = null;
            JsonElement rebuilt = await WaitForWriterCapabilitiesAsync(writer,
                index => index.GetProperty("state").GetString() == "ready" &&
                         index.GetProperty("indexVersion").GetString() != oldVersion,
                40_000);
            Assert.NotEqual(oldVersion, rebuilt.GetProperty("indexVersion").GetString());

            using var semantic = new SemanticService(follower);
            var tools = new NavigationTools(follower, semantic);
            Assert.True(WaitUntil(() => HasSymbol(tools, "Alpha45"), 10_000),
                "foreign follower could not query the replacement index");
        }
        finally
        {
            snapshot?.Dispose();
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("stale")]
    [InlineData("workspace")]
    public void FollowerRefusesUnusableIndexWithoutCreatingOrRepairingIt(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-45-refusal").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        byte[]? originalBytes = null;
        try
        {
            WriteWorkspace(root);
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            switch (scenario)
            {
                case "missing":
                    break;
                case "corrupt":
                    originalBytes = "not a sqlite database"u8.ToArray();
                    File.WriteAllBytes(database, originalBytes);
                    break;
                case "stale":
                    IndexBuilder.Build(root, database);
                    using (var store = new IndexStore(database, createNew: false))
                        store.SetMeta("schema_version", "0");
                    break;
                case "workspace":
                    IndexBuilder.Build(root, database);
                    using (var store = new IndexStore(database, createNew: false))
                        store.SetMeta("workspace_root", Path.Combine(root, "different-workspace"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }

            Assert.True(IndexOwnershipLease.TryAcquire(root, database,
                out IndexOwnershipLease? owner));
            using (owner!)
            using (var follower = new IndexManager(root, database))
            {
                follower.Start();
                Assert.True(WaitUntil(() => follower.State == "failed", 20_000),
                    $"{scenario}: expected follower refusal, got {follower.State}");
                Assert.False(follower.IsWriter);
                Assert.Equal(scenario is "missing" or "corrupt" ? "unavailable" : "follower",
                    follower.AccessMode);
                Assert.False(follower.IsQueryable);
                if (follower.IsFollower)
                {
                    Assert.Contains("writer", follower.Health().Error ?? "",
                        StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("compatible index", follower.Health().Error ?? "",
                        StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    Assert.Contains("coordination", follower.Health().Error ?? "",
                        StringComparison.OrdinalIgnoreCase);
                }
                Assert.False(follower.RequestRefresh());
                Assert.False(follower.RequestFullRebuild());
                using var semantic = new SemanticService(follower);
                var tools = new NavigationTools(follower, semantic);
                JsonElement refresh = Parse(tools.RefreshIndex(force: "full"));
                JsonElement worktree = Parse(tools.IndexWorktree(
                    Path.Combine(root, "never-created-worktree")));
                if (follower.IsFollower)
                {
                    AssertWriterRequired(refresh);
                    AssertWriterRequired(worktree);
                }
                else
                {
                    Assert.Equal("index_unavailable", refresh.GetProperty("error").GetString());
                    Assert.Equal("index_unavailable", worktree.GetProperty("error").GetString());
                }
            }

            switch (scenario)
            {
                case "missing":
                    Assert.False(File.Exists(database));
                    break;
                case "corrupt":
                    Assert.Equal(originalBytes, File.ReadAllBytes(database));
                    break;
                case "stale":
                    Assert.Equal("0", ReadMeta(database, "schema_version"));
                    break;
                case "workspace":
                    Assert.Equal(Path.Combine(root, "different-workspace"),
                        ReadMeta(database, "workspace_root"));
                    break;
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WindowsForeignWriterAllowsFollowerAndSuccessorRebuildWhileFollowerLives()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-45-process-follower").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        Process? child = null;
        Task<string>? childStdout = null;
        Task<string>? childStderr = null;
        IndexManager? follower = null;
        IndexManager? successor = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);

            child = StartPhoenixProcess(root, database);
            childStdout = child.StandardOutput.ReadToEndAsync();
            childStderr = child.StandardError.ReadToEndAsync();
            Assert.True(WaitUntil(() => child.HasExited ||
                IndexOwnershipLease.IsHeld(root, database), 20_000),
                "child Phoenix never acquired the writer lease");
            Assert.False(child.HasExited,
                $"child Phoenix exited before owning the index: {CompletedText(childStderr)}");
            Assert.True(WaitUntil(() => File.Exists(database + "-wal"), 10_000),
                "child Phoenix never opened the committed WAL index");

            follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable || follower.State == "failed", 20_000));
            Assert.True(follower.IsQueryable, follower.Health().Error);
            Assert.False(follower.IsWriter);
            Assert.Equal("follower", follower.AccessMode);
            using var followerSemantic = new SemanticService(follower);
            var followerTools = new NavigationTools(follower, followerSemantic);
            Assert.True(HasSymbol(followerTools, "Alpha45"));

            File.WriteAllText(Path.Combine(root, "Beta.cs"),
                "namespace Batch45 { public class Beta45 { } }");
            Assert.True(WaitUntil(() => HasSymbol(followerTools, "Beta45"), 20_000),
                "the follower never observed the writer's committed WAL refresh");
            string oldVersion = follower.Health().IndexVersion!;

            // EOF is a graceful stdio-server shutdown. The existing Batch41 crash regression
            // separately pins abandoned-mutex recovery after Kill(entireProcessTree:true).
            child.StandardInput.Close();
            Assert.True(child.WaitForExit(10_000),
                $"child Phoenix did not stop after stdin EOF: {CompletedText(childStderr)}");
            Assert.Equal(0, child.ExitCode);
            Assert.True(WaitUntil(() => !IndexOwnershipLease.IsHeld(root, database), 10_000),
                "the writer lease remained held after graceful owner shutdown");
            Assert.True(WaitUntil(() => !follower.IsQueryable, 5_000),
                "the follower stayed ready after its writer exited");
            Assert.Contains("writer is no longer running", follower.Health().Error ?? "",
                StringComparison.OrdinalIgnoreCase);

            successor = new IndexManager(root, database);
            successor.Start();
            Assert.True(WaitUntil(() => successor.IsQueryable || successor.State == "failed", 20_000));
            Assert.True(successor.IsQueryable, successor.Health().Error);
            Assert.True(successor.IsWriter);
            Assert.Equal("writer", successor.AccessMode);
            Assert.True(successor.RequestFullRebuild());
            Assert.True(WaitUntil(() => successor.State == "failed" ||
                (successor.IsQueryable && successor.Health().IndexVersion != oldVersion), 40_000),
                "the successor writer never completed its full rebuild");
            Assert.True(successor.IsQueryable, successor.Health().Error);
            Assert.NotEqual(oldVersion, successor.Health().IndexVersion);

            Assert.True(WaitUntil(() => HasSymbol(followerTools, "Beta45"), 10_000),
                "the live follower could not query the successor writer's replacement index");
            JsonElement capabilities = Parse(followerTools.ServerCapabilities());
            JsonElement index = capabilities.GetProperty("index");
            Assert.Equal("follower", index.GetProperty("mode").GetString());
            Assert.Equal(successor.Health().IndexVersion,
                index.GetProperty("indexVersion").GetString());
        }
        finally
        {
            successor?.Dispose();
            follower?.Dispose();
            if (child is { HasExited: false })
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                try { child.WaitForExit(10_000); } catch { }
            }
            child?.Dispose();
            GC.KeepAlive(childStdout);
            GC.KeepAlive(childStderr);
            Cleanup(root);
        }
    }

    private static void AssertWriterRequired(JsonElement response)
    {
        Assert.Equal("index_writer_required", response.GetProperty("error").GetString());
        Assert.False(response.TryGetProperty("queued", out _));
        Assert.False(response.TryGetProperty("action", out _));
    }

    private static bool HasSymbol(NavigationTools tools, string symbol)
    {
        try
        {
            JsonElement response = Parse(tools.SearchSymbol(symbol, match: "exact"));
            return response.TryGetProperty("symbols", out JsonElement symbols) &&
                   symbols.GetArrayLength() == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static void WriteFollowerMetadata(string database, string refreshedAtUtc,
        string commit, string branch)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach ((string key, string value) in new[]
                 {
                     ("last_refresh_utc", refreshedAtUtc),
                     ("indexed_commit", commit),
                     ("indexed_branch", branch),
                 })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO meta(key,value) VALUES($key,$value) " +
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static string? ReadMeta(string database, string key)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static Process StartPhoenixProcess(string workspaceRoot, string dbPath)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string testsAssembly = typeof(Batch45IndexFollowerTests).Assembly.Location;
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(testsAssembly, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(testsAssembly, ".deps.json"));
        start.ArgumentList.Add(typeof(NavigationTools).Assembly.Location);
        start.ArgumentList.Add("--workspace-root");
        start.ArgumentList.Add(workspaceRoot);
        start.ArgumentList.Add("--index-db");
        start.ArgumentList.Add(dbPath);
        return Process.Start(start) ??
            throw new InvalidOperationException("could not start child Phoenix process");
    }

    private static async Task<McpClient> StartPhoenixClientAsync(
        string workspaceRoot, string dbPath)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string testsAssembly = typeof(Batch45IndexFollowerTests).Assembly.Location;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Batch45 foreign Phoenix writer",
            Command = dotnet,
            Arguments = new[]
            {
                "exec",
                "--runtimeconfig",
                Path.ChangeExtension(testsAssembly, ".runtimeconfig.json"),
                "--depsfile",
                Path.ChangeExtension(testsAssembly, ".deps.json"),
                typeof(NavigationTools).Assembly.Location,
                "--workspace-root",
                workspaceRoot,
                "--index-db",
                dbPath,
            },
        });
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonElement> CallJsonAsync(McpClient client, string tool,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        CallToolResult result = await client.CallToolAsync(tool,
            arguments ?? new Dictionary<string, object?>());
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return Parse(text.Text);
    }

    private static async Task<JsonElement> WaitForWriterCapabilitiesAsync(McpClient client,
        Func<JsonElement, bool> condition, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        JsonElement index = default;
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            index = (await CallJsonAsync(client, "server_capabilities")).GetProperty("index");
            if (condition(index)) return index;
            await Task.Delay(100);
        }
        index = (await CallJsonAsync(client, "server_capabilities")).GetProperty("index");
        Assert.True(condition(index), $"writer capabilities did not reach the expected state: {index}");
        return index;
    }

    private static string CompletedText(Task<string>? text) =>
        text is { IsCompletedSuccessfully: true } ? text.Result : "(no stderr available)";

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static void WriteWorkspace(string root)
    {
        File.WriteAllText(Path.Combine(root, "Batch45.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Alpha.cs"),
            "namespace Batch45;\n\npublic class Alpha45\n{\n}\n");
    }

    private static void Cleanup(string root)
    {
        TestWorkspaceCleanup.ClearIndexPools(root);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (!Directory.Exists(root)) return;
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 19 &&
                                       ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }

        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
