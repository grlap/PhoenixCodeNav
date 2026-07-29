using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
    public void OnePhysicalWorkspaceMutexCoversEveryIndexPathAndOtherWorkspacesRemainIndependent()
    {
        string firstRoot = Directory.CreateTempSubdirectory("codenav-45-workspace-lock-a").FullName;
        string secondRoot = Directory.CreateTempSubdirectory("codenav-45-workspace-lock-b").FullName;
        string defaultDatabase = IndexBuilder.DefaultDbPath(firstRoot);
        string alternateDatabase = Path.Combine(firstRoot, ".alternate", "other.db");
        string independentDatabase = IndexBuilder.DefaultDbPath(secondRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(defaultDatabase)!);
        Directory.CreateDirectory(Path.GetDirectoryName(alternateDatabase)!);
        Directory.CreateDirectory(Path.GetDirectoryName(independentDatabase)!);
        try
        {
            Assert.True(IndexOwnershipLease.TryAcquire(firstRoot, defaultDatabase,
                out IndexOwnershipLease? owner));
            using (owner!)
            {
                Assert.Equal(IndexLeaseAcquireResult.Contended,
                    IndexOwnershipLease.TryAcquireDetailed(firstRoot, alternateDatabase,
                        anchoredIdentity: null, out IndexOwnershipLease? sameWorkspace));
                Assert.Null(sameWorkspace);

                Assert.True(IndexOwnershipLease.TryAcquire(secondRoot, independentDatabase,
                    out IndexOwnershipLease? independent));
                independent!.Dispose();
            }

            Assert.True(IndexOwnershipLease.TryAcquire(firstRoot, alternateDatabase,
                out IndexOwnershipLease? successor));
            successor!.Dispose();
        }
        finally
        {
            Cleanup(firstRoot);
            Cleanup(secondRoot);
        }
    }

    [Fact]
    public async Task BidirectionalWorkspaceAcquisitionFailsFastWithoutAWaitCycle()
    {
        string firstRoot = Directory.CreateTempSubdirectory(
            "codenav-45-no-cycle-a").FullName;
        string secondRoot = Directory.CreateTempSubdirectory(
            "codenav-45-no-cycle-b").FullName;
        string firstDatabase = IndexBuilder.DefaultDbPath(firstRoot);
        string secondDatabase = IndexBuilder.DefaultDbPath(secondRoot);
        try
        {
            Assert.True(IndexOwnershipLease.TryAcquire(firstRoot, firstDatabase,
                out IndexOwnershipLease? firstOwner));
            using (firstOwner!)
            {
                Assert.True(IndexOwnershipLease.TryAcquire(secondRoot, secondDatabase,
                    out IndexOwnershipLease? secondOwner));
                using (secondOwner!)
                {
                    var elapsed = Stopwatch.StartNew();
                    Task<IndexLeaseAcquireResult> firstToSecond = Task.Run(() =>
                        IndexOwnershipLease.TryAcquireDetailed(secondRoot, secondDatabase,
                            anchoredIdentity: null, out _));
                    Task<IndexLeaseAcquireResult> secondToFirst = Task.Run(() =>
                        IndexOwnershipLease.TryAcquireDetailed(firstRoot, firstDatabase,
                            anchoredIdentity: null, out _));

                    IndexLeaseAcquireResult[] results = await Task.WhenAll(
                        firstToSecond, secondToFirst).WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.All(results, result =>
                        Assert.Equal(IndexLeaseAcquireResult.Contended, result));
                    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2),
                        "cross-worktree acquisition blocked instead of failing fast");
                }
            }
        }
        finally
        {
            Cleanup(secondRoot);
            Cleanup(firstRoot);
        }
    }

    [Fact]
    public void DifferentWorkspacesCannotBecomeWritersForTheSameDatabaseDestination()
    {
        string firstRoot = Directory.CreateTempSubdirectory(
            "codenav-45-shared-destination-a").FullName;
        string secondRoot = Directory.CreateTempSubdirectory(
            "codenav-45-shared-destination-b").FullName;
        string sharedDatabase = IndexBuilder.DefaultDbPath(firstRoot);
        try
        {
            WriteWorkspace(firstRoot);
            WriteWorkspace(secondRoot);
            IndexBuilder.Build(firstRoot, sharedDatabase);

            using var first = new IndexManager(firstRoot, sharedDatabase);
            first.Start();
            Assert.True(WaitUntil(() => first.IsQueryable, 20_000), first.Health().Error);
            Assert.True(first.IsWriter);
            Assert.True(File.Exists(sharedDatabase + IndexDestinationClaim.Suffix));

            using var second = new IndexManager(secondRoot, sharedDatabase);
            second.Start();
            Assert.Equal("failed", second.State);
            Assert.Equal("unavailable", second.AccessMode);
            Assert.False(second.IsWriter);
            Assert.False(second.IsFollower);
            Assert.False(second.IsQueryable);
            Assert.Contains("different workspace", second.Health().Error ?? "",
                StringComparison.OrdinalIgnoreCase);
            Assert.True(first.IsQueryable, first.Health().Error);
            Assert.True(IndexOwnershipLease.SameWorkspaceIdentity(firstRoot,
                ReadMeta(sharedDatabase, "workspace_root")!));
        }
        finally
        {
            Cleanup(secondRoot);
            Cleanup(firstRoot);
        }
    }

    [Fact]
    public void SameWorkspaceWithDifferentDatabaseDoesNotAttachToTheWrongWriter()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-divergent-destination").FullName;
        string writerDatabase = IndexBuilder.DefaultDbPath(root);
        string differentDatabase = Path.Combine(root, ".alternate", "other.db");
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, writerDatabase);
            using var writer = new IndexManager(root, writerDatabase);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);

            using var contender = new IndexManager(root, differentDatabase);
            contender.Start();
            Assert.Equal("failed", contender.State);
            Assert.Equal("unavailable", contender.AccessMode);
            Assert.False(contender.IsWriter);
            Assert.False(contender.IsFollower);
            Assert.False(contender.IsQueryable);
            Assert.Contains("destination", contender.Health().Error ?? "",
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(differentDatabase));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void UnclaimedCurrentSchemaForeignDefaultDestinationFailsClosed()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-unclaimed-current").FullName;
        string foreignRoot = Directory.CreateTempSubdirectory(
            "codenav-45-unclaimed-current-owner").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            WriteWorkspace(foreignRoot);
            IndexBuilder.Build(foreignRoot, database);
            Assert.False(File.Exists(database + IndexDestinationClaim.Suffix));
            string foreignArtifact = Path.Combine(Path.GetDirectoryName(database)!,
                ".phoenix-stage-44444444444444444444444444444444.db");
            File.WriteAllText(foreignArtifact, "foreign-owner");

            using var manager = new IndexManager(root, database);
            manager.Start(forceRebuild: true);
            Assert.Equal("failed", manager.State);
            Assert.False(manager.IsQueryable);
            Assert.Contains("different workspace",
                manager.Health().Error ?? "",
                StringComparison.OrdinalIgnoreCase);
            Assert.True(IndexOwnershipLease.SameWorkspaceIdentity(foreignRoot,
                ReadMeta(database, "workspace_root")!));
            Assert.Equal("foreign-owner", File.ReadAllText(foreignArtifact));
        }
        finally
        {
            Cleanup(foreignRoot);
            Cleanup(root);
        }
    }

    [Fact]
    public void MovedWorkspaceCanForceRebuildAndRebindItsStoredRoot()
    {
        string oldRoot = Directory.CreateTempSubdirectory(
            "codenav-45-moved-old").FullName;
        string newRoot = oldRoot + "-renamed";
        try
        {
            WriteWorkspace(oldRoot);
            string oldDatabase = IndexBuilder.DefaultDbPath(oldRoot);
            IndexBuilder.Build(oldRoot, oldDatabase);
            Directory.Move(oldRoot, newRoot);
            string newDatabase = IndexBuilder.DefaultDbPath(newRoot);

            using (var ordinary = new IndexManager(newRoot, newDatabase))
            {
                ordinary.Start();
                Assert.Equal("failed", ordinary.State);
                Assert.Contains("force:'full'", ordinary.Health().Error ?? "",
                    StringComparison.OrdinalIgnoreCase);
            }

            using var manager = new IndexManager(newRoot, newDatabase);
            manager.Start(forceRebuild: true);
            Assert.True(WaitUntil(() =>
                (manager.IsQueryable && manager.State != "building") ||
                manager.State == "failed", 30_000));
            Assert.True(manager.IsQueryable, manager.Health().Error);
            Assert.True(manager.IsWriter);
            Assert.True(IndexOwnershipLease.SameWorkspaceIdentity(newRoot,
                ReadMeta(newDatabase, "workspace_root")!));
        }
        finally
        {
            Cleanup(newRoot);
            Cleanup(oldRoot);
        }
    }

    [Fact]
    public void ExplicitRebindRefusesAnUnverifiableStoredWorkspaceIdentity()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-rebind-current").FullName;
        string storedRoot = Directory.CreateTempSubdirectory(
            "codenav-45-rebind-inaccessible").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("workspace_root", storedRoot);

            string currentIdentity = IndexOwnershipLease.GetWorkspaceIdentity(root);
            IOException error = Assert.Throws<IOException>(() =>
                IndexBuilder.EnsureExistingDatabaseWorkspace(
                    root, database, allowMissingStoredRootRebind: true,
                    identityProbe: path =>
                        CodeNav.Core.WorkspacePaths.FullPathsEqual(path, storedRoot)
                            ? (WorkspaceIdentityProbeResult.Failed, null)
                            : (WorkspaceIdentityProbeResult.Found, currentIdentity)));

            Assert.Contains("could not be verified", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(storedRoot, ReadMeta(database, "workspace_root"));
        }
        finally
        {
            Cleanup(storedRoot);
            Cleanup(root);
        }
    }

    [Fact]
    public void StoredWorkspaceRootReplacedByAFileFailsClosedDuringExplicitRebind()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-rebind-current-file").FullName;
        string storedRoot = Directory.CreateTempSubdirectory(
            "codenav-45-rebind-file").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("workspace_root", storedRoot);
            Directory.Delete(storedRoot);
            File.WriteAllText(storedRoot, "not a workspace directory");

            IOException error = Assert.Throws<IOException>(() =>
                IndexBuilder.EnsureExistingDatabaseWorkspace(
                    root, database, allowMissingStoredRootRebind: true));

            Assert.Contains("could not be verified", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(storedRoot, ReadMeta(database, "workspace_root"));
        }
        finally
        {
            try { File.Delete(storedRoot); } catch { }
            Cleanup(storedRoot);
            Cleanup(root);
        }
    }

    [Fact]
    public void LegacyForeignOwnershipRequiresExplicitRecoveryAtTheWorkspaceLocalDefaultDestination()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-legacy-sibling").FullName;
        string foreignRoot = Directory.CreateTempSubdirectory(
            "codenav-45-legacy-seed").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            WriteWorkspace(foreignRoot);
            IndexBuilder.Build(foreignRoot, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("schema_version", "19");

            using (var ordinary = new IndexManager(root, database))
            {
                ordinary.Start();
                Assert.Equal("failed", ordinary.State);
                Assert.False(ordinary.IsQueryable);
                Assert.Contains("different workspace",
                    ordinary.Health().Error ?? "",
                    StringComparison.OrdinalIgnoreCase);
            }

            using var manager = new IndexManager(root, database);
            manager.Start(forceRebuild: true);
            Assert.True(WaitUntil(() => manager.IsQueryable ||
                manager.State == "failed", 30_000));
            Assert.True(manager.IsQueryable, manager.Health().Error);
            Assert.Equal(IndexBuilder.SchemaVersion,
                ReadMeta(database, "schema_version"));
            Assert.True(IndexOwnershipLease.SameWorkspaceIdentity(root,
                ReadMeta(database, "workspace_root")!));
        }
        finally
        {
            Cleanup(foreignRoot);
            Cleanup(root);
        }
    }

    [Fact]
    public void UnclaimedForeignCustomDestinationCannotBeReboundEvenWhenLegacyAndForced()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-foreign-custom-current").FullName;
        string foreignRoot = Directory.CreateTempSubdirectory(
            "codenav-45-foreign-custom-owner").FullName;
        string database = Path.Combine(root, ".alternate", "foreign.db");
        try
        {
            WriteWorkspace(root);
            WriteWorkspace(foreignRoot);
            IndexBuilder.Build(foreignRoot, database);
            using (var store = new IndexStore(database, createNew: false))
                store.SetMeta("schema_version", "19");

            using var manager = new IndexManager(root, database);
            manager.Start(forceRebuild: true);
            Assert.Equal("failed", manager.State);
            Assert.False(manager.IsQueryable);
            Assert.Contains("different workspace",
                manager.Health().Error ?? "",
                StringComparison.OrdinalIgnoreCase);
            Assert.True(IndexOwnershipLease.SameWorkspaceIdentity(foreignRoot,
                ReadMeta(database, "workspace_root")!));
        }
        finally
        {
            Cleanup(foreignRoot);
            Cleanup(root);
        }
    }

    [Fact]
    public void MalformedDestinationClaimFailsClosed()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-malformed-claim").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            File.WriteAllText(database + IndexDestinationClaim.Suffix,
                "not-a-valid-claim", Encoding.UTF8);

            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.Equal("failed", manager.State);
            Assert.Equal("unavailable", manager.AccessMode);
            Assert.False(manager.IsWriter);
            Assert.False(manager.IsFollower);
            Assert.Contains("ownership", manager.Health().Error ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(database + IndexDestinationClaim.Suffix); } catch { }
            Cleanup(root);
        }
    }

    [Fact]
    public void MissingFollowerClaimFailsClosedAndNextWriterRepairsIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-missing-claim").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        IndexOwnershipLease? owner = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            Assert.True(IndexOwnershipLease.TryAcquire(root, database, out owner));

            using (var follower = new IndexManager(root, database))
            {
                follower.Start();
                Assert.Equal("failed", follower.State);
                Assert.Equal("unavailable", follower.AccessMode);
                Assert.False(follower.IsFollower);
                Assert.False(follower.IsQueryable);
                Assert.Contains("verify", follower.Health().Error ?? "",
                    StringComparison.OrdinalIgnoreCase);
            }

            owner!.Dispose();
            owner = null;
            using var successor = new IndexManager(root, database);
            successor.Start();
            Assert.True(WaitUntil(() => successor.IsQueryable ||
                successor.State == "failed", 20_000));
            Assert.True(successor.IsQueryable, successor.Health().Error);
            Assert.True(successor.IsWriter);
            Assert.True(WaitUntil(() => IndexDestinationClaim.ReadState(root, database) ==
                IndexDestinationClaimState.Ready, 10_000));
        }
        finally
        {
            owner?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void ForceFullReportsWriterRequiredWhenRecoveryReattachesAsFollower()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-recovery-follower").FullName;
        string blockedDirectory = Path.Combine(root, "blocked-index");
        string database = Path.Combine(blockedDirectory, "index.db");
        try
        {
            WriteWorkspace(root);
            File.WriteAllText(blockedDirectory, "temporary destination blocker");
            using var recovering = new IndexManager(root, database);
            recovering.Start();
            Assert.Equal("failed", recovering.State);
            Assert.Equal("unavailable", recovering.AccessMode);

            File.Delete(blockedDirectory);
            Directory.CreateDirectory(blockedDirectory);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000),
                writer.Health().Error);

            using var semantic = new SemanticService(recovering);
            var tools = new NavigationTools(recovering, semantic);
            JsonElement response = Parse(tools.RefreshIndex(force: "full"));
            AssertWriterRequired(response);
            Assert.True(recovering.IsFollower);
            Assert.True(recovering.IsQueryable, recovering.Health().Error);
            Assert.True(writer.IsWriter);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SuccessorRecoversAStaleSameWorkspaceRebuildingClaim()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-stale-claim").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            File.WriteAllText(database + IndexDestinationClaim.Suffix,
                "B\n" + IndexOwnershipLease.GetWorkspaceIdentity(root) + "\n");

            using var successor = new IndexManager(root, database);
            successor.Start();
            Assert.True(WaitUntil(() => successor.IsQueryable ||
                successor.State == "failed", 20_000));
            Assert.True(successor.IsQueryable, successor.Health().Error);
            Assert.True(successor.IsWriter);
            Assert.True(WaitUntil(() => IndexDestinationClaim.ReadState(root, database) ==
                IndexDestinationClaimState.Ready, 10_000));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void TransientOwnershipProbeContentionDoesNotTurnSuccessorIntoFollower()
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
            Assert.Contains(capabilities.GetProperty("features").EnumerateArray(), feature =>
                feature.GetProperty("id").GetString() == "single-workspace-writer-mutex");
            Assert.Contains(capabilities.GetProperty("features").EnumerateArray(), feature =>
                feature.GetProperty("id").GetString() == "index-destination-claim");

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

            writer.FullRebuildPrivateStageCompletedForTest = () =>
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
            Assert.Equal(IndexDestinationClaimAcquireResult.Acquired,
                IndexDestinationClaim.TryAcquire(root, database,
                    out IndexDestinationClaim? destinationClaim));
            using var destination = destinationClaim!;
            destination.SetReady();
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
    public async Task ContendedFollowerHealthObservesARebuildingDestinationClaim()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-health-claim-race").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var captured = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            Assert.True(IndexOwnershipLease.TryAcquire(root, database,
                out IndexOwnershipLease? owner));
            using var ownership = owner!;
            Assert.Equal(IndexDestinationClaimAcquireResult.Acquired,
                IndexDestinationClaim.TryAcquire(root, database,
                    out IndexDestinationClaim? destinationClaim));
            using var destination = destinationClaim!;
            destination.SetReady();

            using var follower = new IndexManager(root, database);
            follower.Start();
            Assert.True(WaitUntil(() => follower.IsQueryable, 20_000),
                follower.Health().Error);

            int blocked = 0;
            follower.FollowerMetadataAfterPublishForTest = _ =>
            {
                if (Interlocked.Exchange(ref blocked, 1) != 0) return;
                captured.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            };
            await Task.Delay(300);
            Task<IndexHealth> first = Task.Run(() => follower.Health());
            Assert.True(captured.Wait(TimeSpan.FromSeconds(10)),
                "the first Health refresh did not reach its publication seam");

            destination.SetRebuilding();
            IndexHealth concurrent = follower.Health();
            Assert.Equal("failed", concurrent.State);
            Assert.False(follower.IsQueryable);

            release.Set();
            IndexHealth firstResult =
                await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("failed", firstResult.State);

            follower.FollowerMetadataAfterPublishForTest = null;
            destination.SetReady();
            Assert.True(WaitUntil(() => follower.IsQueryable, 10_000),
                follower.Health().Error);
        }
        finally
        {
            release.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public void RebuildClaimStopsNewFollowerReadsWhileExistingSnapshotsDrain()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory("codenav-45-review-drain").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var stageReady = new ManualResetEventSlim(false);
        using var releaseStageBuild = new ManualResetEventSlim(false);
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
            int activeAtBoundary = -1;
            writer.FullRebuildDestructiveBoundaryForTest = active =>
            {
                activeAtBoundary = active;
                boundary.Set();
            };
            writer.FullRebuildAfterTelemetryStartedForTest = () =>
                Assert.Equal(IndexDestinationClaimState.Ready,
                    IndexDestinationClaim.ReadState(root, database));
            writer.FullRebuildPrivateStageReadyForTest = _ =>
            {
                stageReady.Set();
                Assert.True(releaseStageBuild.Wait(TimeSpan.FromSeconds(15)));
            };
            writer.FullRebuildCompletedForTest = () => completed.Set();

            Assert.True(writer.RequestFullRebuild());
            Assert.True(stageReady.Wait(TimeSpan.FromSeconds(10)),
                "writer never opened its private rebuild stage");
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, database));
            Assert.True(writer.IsQueryable,
                "the writer stopped serving the prior publication during private staging");
            using (IndexQueries stagingWriterQuery = writer.OpenQueries())
                Assert.Single(stagingWriterQuery.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.True(follower.IsQueryable,
                "the follower stopped serving the prior publication during private staging");
            using (IndexQueries stagingFollowerQuery = follower.OpenQueries())
                Assert.Single(stagingFollowerQuery.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.False(boundary.IsSet,
                "the publication boundary ran before the private stage was released");
            releaseStageBuild.Set();
            Assert.True(boundary.Wait(TimeSpan.FromSeconds(10)),
                "writer never reached its local destructive boundary");
            Assert.Equal(0, activeAtBoundary);
            Assert.True(WaitUntil(() => writer.Health().Error?.Contains(
                    "waiting for existing index readers", StringComparison.OrdinalIgnoreCase) == true,
                10_000), "writer did not report the OS-level reader drain");
            Assert.False(completed.IsSet,
                "writer completed replacement while follower snapshots retained the old database");
            Assert.Equal(oldVersion, writer.Health().IndexVersion);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Assert.False(follower.IsQueryable,
                    "a follower remained queryable after rebuild intent was published");
                Assert.Equal("failed", follower.Health().State);
                Assert.Throws<IOException>(() => follower.OpenQueries());
                Assert.Null(follower.TryOpenReviewSnapshot());
                Thread.Sleep(10);
            }
            Assert.Single(snapshot.Queries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.Single(secondSnapshot.Queries.SearchSymbols("Alpha45", "exact", null, 2));

            snapshot.Dispose();
            snapshot = null;
            secondSnapshot.Dispose();
            secondSnapshot = null;
            Assert.True(completed.Wait(TimeSpan.FromSeconds(40)),
                "writer did not complete after the follower released its SQLite snapshots");
            Assert.True(WaitUntil(() => writer.IsQueryable &&
                writer.Health().IndexVersion != oldVersion, 20_000), writer.Health().Error);
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, database));

            using var semantic = new SemanticService(follower);
            var tools = new NavigationTools(follower, semantic);
            Assert.True(WaitUntil(() => HasSymbol(tools, "Alpha45"), 10_000),
                "follower could not query the replacement index after releasing its snapshot");
        }
        finally
        {
            releaseStageBuild.Set();
            snapshot?.Dispose();
            secondSnapshot?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void SupportedHostFullRebuildPublishesACompletePrivateStage()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-private-stage-publish").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var stageCompleted = new ManualResetEventSlim(false);
        using var releaseStage = new ManualResetEventSlim(false);
        using var waitingForWriterQuery = new ManualResetEventSlim(false);
        using var completed = new ManualResetEventSlim(false);
        IndexManager? follower = null;
        IndexQueries? heldWriterQuery = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            if (OperatingSystem.IsWindows())
            {
                follower = new IndexManager(root, database);
                follower.Start();
                Assert.True(WaitUntil(() => follower.IsQueryable, 20_000),
                    follower.Health().Error);
            }
            string oldVersion = writer.Health().IndexVersion!;

            writer.FullRebuildPrivateStageReadyForTest = _ =>
                File.WriteAllText(Path.Combine(root, "Beta.cs"),
                    "namespace Batch45; public class PrivateStageBeta45 { }");
            writer.FullRebuildPrivateStageCompletedForTest = () =>
            {
                stageCompleted.Set();
                Assert.True(releaseStage.Wait(TimeSpan.FromSeconds(15)));
            };
            writer.FullRebuildWaitingForLocalSnapshotsForTest =
                () => waitingForWriterQuery.Set();
            writer.FullRebuildCompletedForTest = () => completed.Set();
            heldWriterQuery = writer.OpenQueries();

            Assert.True(writer.RequestFullRebuild());
            Assert.True(stageCompleted.Wait(TimeSpan.FromSeconds(20)),
                "private rebuild never reached its publication boundary");
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, database));
            Assert.Equal(oldVersion, writer.Health().IndexVersion);
            Assert.True(writer.IsQueryable,
                "the writer stopped serving the old publication during private staging");
            Assert.Empty(heldWriterQuery.SearchSymbols(
                "PrivateStageBeta45", "exact", null, 2));
            if (follower is not null)
            {
                Assert.True(follower.IsQueryable,
                    "the old publication stopped serving before B was published");
                using IndexQueries oldQueries = follower.OpenQueries();
                Assert.Empty(oldQueries.SearchSymbols(
                    "PrivateStageBeta45", "exact", null, 2));
            }

            releaseStage.Set();
            Assert.True(waitingForWriterQuery.Wait(TimeSpan.FromSeconds(10)),
                "publication did not wait for an ordinary writer query");
            Assert.Equal(IndexDestinationClaimState.Rebuilding,
                IndexDestinationClaim.ReadState(root, database));
            Assert.False(completed.IsSet,
                "publication crossed an ordinary writer query lifetime");
            Assert.Equal(oldVersion, writer.Health().IndexVersion);
            Assert.Empty(heldWriterQuery.SearchSymbols(
                "PrivateStageBeta45", "exact", null, 2));
            if (follower is not null)
                Assert.False(follower.IsQueryable,
                    "the follower barged after staged publication entered B");

            heldWriterQuery.Dispose();
            heldWriterQuery = null;
            Assert.True(completed.Wait(TimeSpan.FromSeconds(40)),
                "private rebuild did not finish after the writer query drained");
            Assert.True(WaitUntil(() => writer.IsQueryable &&
                writer.Health().IndexVersion != oldVersion, 30_000), writer.Health().Error);
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, database));
            if (follower is not null)
            {
                Assert.True(WaitUntil(() =>
                {
                    try
                    {
                        using IndexQueries published = follower.OpenQueries();
                        return published.SearchSymbols(
                            "PrivateStageBeta45", "exact", null, 2).Count == 1;
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                }, 10_000), "follower did not observe the atomically published private stage");
            }
            using IndexQueries writerPublished = writer.OpenQueries();
            Assert.Single(writerPublished.SearchSymbols(
                "PrivateStageBeta45", "exact", null, 2));
        }
        finally
        {
            releaseStage.Set();
            heldWriterQuery?.Dispose();
            follower?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void LocalReaderDrainUsesThePublicationDeadline()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-private-local-timeout").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var completed = new ManualResetEventSlim(false);
        var logs = new ConcurrentQueue<string>();
        IndexQueries? heldWriterQuery = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database, logs.Enqueue)
            {
                FullRebuildPublicationTimeoutForTest = TimeSpan.FromMilliseconds(100),
                FullRebuildCompletedForTest = () => completed.Set(),
            };
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            string oldVersion = writer.Health().IndexVersion!;
            heldWriterQuery = writer.OpenQueries();

            Assert.True(writer.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)),
                "local-reader timeout did not return control to the refresh pump");
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            Assert.Equal(oldVersion, writer.Health().IndexVersion);
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, writer.DatabaseIoPath));
            Assert.Single(heldWriterQuery.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.Contains(logs, message => message.Contains(
                "timed out waiting for local index readers", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(database)!),
                path => Path.GetFileName(path).StartsWith(
                    ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally
        {
            heldWriterQuery?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void WriterQueryConstructionFailureReleasesItsPublicationLease()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-45-writer-query-construction-failure").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000),
                writer.Health().Error);
            writer.WriterQueryAfterRegistrationForTest = () =>
                throw new InvalidOperationException("decisive construction failure");

            Assert.Throws<InvalidOperationException>(() => writer.OpenQueries());
            Assert.Equal(0, writer.ActiveWriterQueriesForTest);

            writer.WriterQueryAfterRegistrationForTest = null;
            using IndexQueries queries = writer.OpenQueries();
            Assert.Single(queries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.Equal(1, writer.ActiveWriterQueriesForTest);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SupportedHostPrivateStageFailureKeepsThePriorPublication()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-private-stage-failure").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var completed = new ManualResetEventSlim(false);
        IndexHealth? completedHealth = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            string oldVersion = writer.Health().IndexVersion!;
            writer.FullRebuildPrivateStageReadyForTest = _ =>
                throw new InvalidOperationException("decisive staged-build failure");
            writer.FullRebuildCompletedForTest = () =>
            {
                completedHealth = writer.Health();
                completed.Set();
            };

            Assert.True(writer.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(10)),
                "failed private rebuild did not return to the pump");
            Assert.True(writer.IsQueryable, writer.Health().Error);
            Assert.Equal(oldVersion, writer.Health().IndexVersion);
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, database));
            Assert.NotNull(completedHealth);
            Assert.Contains("previous index remains available", completedHealth.Error);
            using (IndexQueries oldQueries = writer.OpenQueries())
                Assert.Single(oldQueries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(database)!),
                path => Path.GetFileName(path).StartsWith(
                    ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SupportedHostInstallEnvelopeFailureReopensThePriorPublication()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-private-install-failure").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var completed = new ManualResetEventSlim(false);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database);
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            string oldVersion = writer.Health().IndexVersion!;
            writer.FullRebuildBeforeStageInstallForTest = () =>
                throw new InvalidOperationException("decisive install-envelope failure");
            writer.FullRebuildCompletedForTest = () => completed.Set();

            Assert.True(writer.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)),
                "failed install envelope did not return to the pump");
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);
            Assert.Equal(oldVersion, writer.Health().IndexVersion);
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, writer.DatabaseIoPath));
            using (IndexQueries oldQueries = writer.OpenQueries())
                Assert.Single(oldQueries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(database)!),
                path => Path.GetFileName(path).StartsWith(
                    ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SupportedHostPostInstallFailureUsesTheStableFailedDiagnostic()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-private-post-install-failure").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var completed = new ManualResetEventSlim(false);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var writer = new IndexManager(root, database)
            {
                FullRebuildAfterStageInstallForTest = () =>
                    throw new InvalidOperationException("decisive post-install failure"),
                FullRebuildCompletedForTest = () => completed.Set(),
            };
            writer.Start();
            Assert.True(WaitUntil(() => writer.IsQueryable, 20_000), writer.Health().Error);

            Assert.True(writer.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)),
                "post-install failure did not return control to the refresh pump");
            Assert.Equal("failed", writer.State);
            Assert.False(writer.IsQueryable);
            IOException error = Assert.Throws<IOException>(() => writer.OpenQueries());
            Assert.Contains("full rebuild failed", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("being published", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(IndexDestinationClaimState.Rebuilding,
                IndexDestinationClaim.ReadState(root, writer.DatabaseIoPath));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SupportedHostStartupForceRebuildServesTheCompatiblePriorPublication()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-startup-private-readable").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var stageReady = new ManualResetEventSlim(false);
        using var releaseStage = new ManualResetEventSlim(false);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            string oldVersion = ReadMeta(database, "index_version")!;
            const string oldRefresh = "2001-02-03T04:05:06.0000000Z";
            const string oldCommit = "old-startup-publication-commit";
            const string oldBranch = "old-startup-publication-branch";
            WriteFollowerMetadata(database, oldRefresh, oldCommit, oldBranch);
            using var manager = new IndexManager(root, database)
            {
                FullRebuildPrivateStageReadyForTest = _ =>
                {
                    stageReady.Set();
                    Assert.True(releaseStage.Wait(TimeSpan.FromSeconds(15)));
                },
            };

            manager.Start(forceRebuild: true);
            Assert.True(stageReady.Wait(TimeSpan.FromSeconds(10)),
                "startup rebuild never opened its private stage");
            Assert.Equal("building", manager.State);
            Assert.True(manager.IsQueryable,
                "startup force rebuild hid the compatible prior publication");
            Assert.Equal(oldVersion, manager.Health().IndexVersion);
            Assert.Equal(oldRefresh, manager.Health().LastRefreshUtc);
            Assert.Equal(oldCommit, manager.Health().IndexedCommit);
            Assert.Equal(oldBranch, manager.Health().IndexedBranch);
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, manager.DatabaseIoPath));
            using (IndexQueries oldQueries = manager.OpenQueries())
                Assert.Single(oldQueries.SearchSymbols("Alpha45", "exact", null, 2));
            using (var semantic = new SemanticService(manager))
            {
                var tools = new NavigationTools(manager, semantic);
                JsonElement response = Parse(
                    tools.SearchSymbol("Alpha45", match: "exact"));
                JsonElement meta = response.GetProperty("meta");
                Assert.Equal("building",
                    meta.GetProperty("indexStatus").GetString());
                Assert.Equal(IndexManager.RefreshSweepPendingCause,
                    meta.GetProperty("partialReason").GetString());
                string statusNote =
                    meta.GetProperty("statusNote").GetString()!;
                Assert.Contains("previous index publication", statusNote);
                Assert.Contains("freshness convergence", statusNote);
            }

            releaseStage.Set();
            Assert.True(WaitUntil(() => manager.IsQueryable &&
                manager.Health().IndexVersion != oldVersion, 40_000), manager.Health().Error);
            Assert.NotEqual(oldRefresh, manager.Health().LastRefreshUtc);
            Assert.NotEqual(oldCommit, manager.Health().IndexedCommit);
            Assert.NotEqual(oldBranch, manager.Health().IndexedBranch);
        }
        finally
        {
            releaseStage.Set();
            Cleanup(root);
        }
    }

    [Fact]
    public void SupportedHostStartupStageFailureKeepsTheCompatiblePriorPublication()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-startup-private-failure").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            string oldVersion = ReadMeta(database, "index_version")!;
            using var manager = new IndexManager(root, database)
            {
                FullRebuildPrivateStageReadyForTest = _ =>
                    throw new InvalidOperationException("decisive startup stage failure"),
            };

            manager.Start(forceRebuild: true);
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000), manager.Health().Error);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, manager.DatabaseIoPath));
            using (IndexQueries oldQueries = manager.OpenQueries())
                Assert.Single(oldQueries.SearchSymbols("Alpha45", "exact", null, 2));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SupportedHostStartupRestoreFailureSettlesInFailedState()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-startup-restore-failure").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var manager = new IndexManager(root, database)
            {
                FullRebuildBeforeStageInstallForTest = () =>
                    throw new InvalidOperationException("decisive install failure"),
                StartupPriorPublicationRestoreForTest = () =>
                    throw new InvalidOperationException("decisive restore failure"),
            };

            manager.Start(forceRebuild: true);
            Assert.True(WaitUntil(() => manager.State == "failed", 30_000),
                manager.Health().Error);
            Assert.False(manager.IsQueryable);
            IOException error = Assert.Throws<IOException>(() => manager.OpenQueries());
            Assert.Contains("during index startup", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("being published", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void SupportedHostStartupRestoreDoesNotResurrectResourcesAfterDispose()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-startup-dispose-restore").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        IndexManager? manager = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            manager = new IndexManager(root, database)
            {
                DisposeWaitTimeoutForTest = TimeSpan.FromMilliseconds(10),
            };
            manager.FullRebuildBeforeStageInstallForTest = () =>
            {
                manager.Dispose();
                throw new InvalidOperationException("failure after concurrent dispose");
            };

            manager.Start(forceRebuild: true);
            Assert.True(WaitUntil(() => manager.State == "failed", 30_000),
                manager.Health().Error);
            Assert.False(manager.HasOwnedStoreForTest);
            Assert.False(manager.HasWorkspaceWatcherForTest);
        }
        finally
        {
            manager?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void SupportedHostPumpRestoreDoesNotResurrectResourcesAfterDispose()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-pump-dispose-restore").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var completed = new ManualResetEventSlim(false);
        IndexManager? manager = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            manager = new IndexManager(root, database)
            {
                DisposeWaitTimeoutForTest = TimeSpan.FromMilliseconds(10),
                FullRebuildCompletedForTest = () => completed.Set(),
            };
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000),
                manager.Health().Error);
            manager.FullRebuildBeforeStageInstallForTest = () =>
            {
                manager.Dispose();
                throw new InvalidOperationException("failure after concurrent dispose");
            };

            Assert.True(manager.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)),
                "disposed pump rebuild did not return");
            Assert.False(manager.HasOwnedStoreForTest);
            Assert.False(manager.HasWorkspaceWatcherForTest);
        }
        finally
        {
            manager?.Dispose();
            Cleanup(root);
        }
    }

    [Fact]
    public void LinuxStagedRebuildReadsThePinnedWorkspaceAndRejectsAReplacementRoot()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-linux-workspace-swap").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        using var completed = new ManualResetEventSlim(false);
        IndexManager? manager = null;
        bool moved = false;
        bool stagedPinnedSource = false;
        bool stagedLexicalMetadata = false;
        string? stagePath = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000),
                manager.Health().Error);
            string oldVersion = manager.Health().IndexVersion!;
            manager.FullRebuildPrivateStageReadyForTest = path =>
            {
                stagePath = path;
                Directory.Move(root, retainedRoot);
                moved = true;
                Directory.CreateDirectory(root);
                WriteWorkspace(root, "ReplacementWorkspaceBeta45");
            };
            manager.FullRebuildPrivateStageCompletedForTest = () =>
            {
                using var stageStore = new IndexStore(stagePath!, createNew: false);
                stagedLexicalMetadata = string.Equals(
                    Path.GetFullPath(root), stageStore.GetMeta("workspace_root"),
                    StringComparison.Ordinal);
                using var stageQueries = new IndexQueries(stagePath!,
                    pinReadSnapshot: false, pooling: false);
                stagedPinnedSource =
                    stageQueries.SearchSymbols("Alpha45", "exact", null, 2).Count == 1 &&
                    stageQueries.SearchSymbols(
                        "ReplacementWorkspaceBeta45", "exact", null, 2).Count == 0;
            };
            manager.FullRebuildCompletedForTest = () => completed.Set();

            Assert.True(manager.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)),
                "workspace-authority mismatch did not return to the refresh pump");
            Assert.True(stagedPinnedSource,
                "the private stage did not read through the retained workspace handle");
            Assert.True(stagedLexicalMetadata,
                "the private stage did not preserve the lexical workspace_root metadata");
            Assert.Equal("failed", manager.State);
            Assert.False(manager.IsQueryable);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);

            manager.Dispose();
            manager = null;
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            using var retainedQueries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(retainedQueries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.Empty(retainedQueries.SearchSymbols(
                "ReplacementWorkspaceBeta45", "exact", null, 2));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(retainedDatabase)!),
                path => Path.GetFileName(path).StartsWith(
                            ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally
        {
            manager?.Dispose();
            RestoreMovedWorkspace(root, retainedRoot, moved);
        }
    }

    [Fact]
    public void LinuxStartupRebuildReadsThePinnedWorkspaceAndRejectsAReplacementRoot()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-linux-startup-workspace-swap").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        IndexManager? manager = null;
        bool moved = false;
        bool stagedPinnedSource = false;
        bool stagedLexicalMetadata = false;
        string? stagePath = null;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            string oldVersion = ReadMeta(database, "index_version")!;
            manager = new IndexManager(root, database);
            manager.FullRebuildPrivateStageReadyForTest = path =>
            {
                stagePath = path;
                Directory.Move(root, retainedRoot);
                moved = true;
                Directory.CreateDirectory(root);
                WriteWorkspace(root, "ReplacementStartupBeta45");
            };
            manager.FullRebuildPrivateStageCompletedForTest = () =>
            {
                using var stageStore = new IndexStore(stagePath!, createNew: false);
                stagedLexicalMetadata = string.Equals(
                    Path.GetFullPath(root), stageStore.GetMeta("workspace_root"),
                    StringComparison.Ordinal);
                using var stageQueries = new IndexQueries(stagePath!,
                    pinReadSnapshot: false, pooling: false);
                stagedPinnedSource =
                    stageQueries.SearchSymbols("Alpha45", "exact", null, 2).Count == 1 &&
                    stageQueries.SearchSymbols(
                        "ReplacementStartupBeta45", "exact", null, 2).Count == 0;
            };

            manager.Start(forceRebuild: true);
            Assert.True(WaitUntil(() => manager.State == "failed", 30_000),
                manager.Health().Error);
            Assert.True(stagedPinnedSource,
                "the startup stage did not read through the retained workspace handle");
            Assert.True(stagedLexicalMetadata,
                "the startup stage did not preserve lexical workspace_root metadata");
            Assert.False(manager.IsQueryable);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);

            manager.Dispose();
            manager = null;
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            using var retainedQueries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(retainedQueries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.Empty(retainedQueries.SearchSymbols(
                "ReplacementStartupBeta45", "exact", null, 2));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(retainedDatabase)!),
                path => Path.GetFileName(path).StartsWith(
                            ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally
        {
            manager?.Dispose();
            RestoreMovedWorkspace(root, retainedRoot, moved);
        }
    }

    [Fact]
    public void LinuxPumpRebuildRejectsAWholeRootReplacementAfterStageInstall()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-linux-pump-post-install-root-swap").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        using var completed = new ManualResetEventSlim(false);
        IndexManager? manager = null;
        bool moved = false;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000),
                manager.Health().Error);
            string oldVersion = manager.Health().IndexVersion!;
            manager.FullRebuildAfterStageInstallForTest = () =>
            {
                Directory.Move(root, retainedRoot);
                moved = true;
                Directory.CreateDirectory(root);
                WriteWorkspace(root, "LatePumpReplacementBeta45");
                Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            };
            manager.FullRebuildCompletedForTest = () => completed.Set();

            Assert.True(manager.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(30)),
                "late pump root replacement did not return to the refresh pump");
            Assert.Equal("failed", manager.State);
            Assert.False(manager.IsQueryable);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);
            Assert.False(File.Exists(database));

            manager.Dispose();
            manager = null;
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            Assert.NotEqual(oldVersion, ReadMeta(retainedDatabase, "index_version"));
            using var retainedQueries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(retainedQueries.SearchSymbols(
                "Alpha45", "exact", null, 2));
            Assert.Empty(retainedQueries.SearchSymbols(
                "LatePumpReplacementBeta45", "exact", null, 2));
            AssertNoPublicationArtifacts(Path.GetDirectoryName(retainedDatabase)!);
            AssertNoPublicationArtifacts(Path.GetDirectoryName(database)!);
        }
        finally
        {
            manager?.Dispose();
            RestoreMovedWorkspace(root, retainedRoot, moved);
        }
    }

    [Fact]
    public void LinuxStartupRebuildRejectsAWholeRootReplacementAfterStageInstall()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-linux-startup-post-install-root-swap").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        IndexManager? manager = null;
        bool moved = false;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            string oldVersion = ReadMeta(database, "index_version")!;
            manager = new IndexManager(root, database)
            {
                FullRebuildAfterStageInstallForTest = () =>
                {
                    Directory.Move(root, retainedRoot);
                    moved = true;
                    Directory.CreateDirectory(root);
                    WriteWorkspace(root, "LateStartupReplacementBeta45");
                    Directory.CreateDirectory(Path.GetDirectoryName(database)!);
                },
            };

            manager.Start(forceRebuild: true);
            Assert.True(WaitUntil(() => manager.State == "failed", 30_000),
                manager.Health().Error);
            Assert.False(manager.IsQueryable);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);
            Assert.False(File.Exists(database));

            manager.Dispose();
            manager = null;
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            Assert.NotEqual(oldVersion, ReadMeta(retainedDatabase, "index_version"));
            using var retainedQueries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(retainedQueries.SearchSymbols(
                "Alpha45", "exact", null, 2));
            Assert.Empty(retainedQueries.SearchSymbols(
                "LateStartupReplacementBeta45", "exact", null, 2));
            AssertNoPublicationArtifacts(Path.GetDirectoryName(retainedDatabase)!);
            AssertNoPublicationArtifacts(Path.GetDirectoryName(database)!);
        }
        finally
        {
            manager?.Dispose();
            RestoreMovedWorkspace(root, retainedRoot, moved);
        }
    }

    [Fact]
    public void LinuxPumpRebuildFailsClosedWhenWorkspaceMovesBeforeAnchorOpen()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-linux-pump-pre-anchor-move").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        using var completed = new ManualResetEventSlim(false);
        IndexManager? manager = null;
        bool moved = false;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000),
                manager.Health().Error);
            string oldVersion = manager.Health().IndexVersion!;
            manager.FullRebuildBeforeAnchoredDestinationOpenForTest = () =>
            {
                Directory.Move(root, retainedRoot);
                moved = true;
            };
            manager.FullRebuildCompletedForTest = () => completed.Set();

            Assert.True(manager.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(20)),
                "pre-anchor workspace move did not return to the refresh pump");
            Assert.Equal("failed", manager.State);
            Assert.False(manager.IsQueryable);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);

            manager.Dispose();
            manager = null;
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            using var retainedQueries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(retainedQueries.SearchSymbols(
                "Alpha45", "exact", null, 2));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(retainedDatabase)!),
                path => Path.GetFileName(path).StartsWith(
                            ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally
        {
            manager?.Dispose();
            RestoreMovedWorkspace(root, retainedRoot, moved);
        }
    }

    [Fact]
    public void LinuxStartupRebuildFailsClosedWhenWorkspaceMovesBeforeAnchorOpen()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-linux-startup-pre-anchor-move").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        IndexManager? manager = null;
        bool moved = false;
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            string oldVersion = ReadMeta(database, "index_version")!;
            manager = new IndexManager(root, database);
            manager.FullRebuildBeforeAnchoredDestinationOpenForTest = () =>
            {
                Directory.Move(root, retainedRoot);
                moved = true;
            };

            manager.Start(forceRebuild: true);
            Assert.True(WaitUntil(() => manager.State == "failed", 20_000),
                manager.Health().Error);
            Assert.False(manager.IsQueryable);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);

            manager.Dispose();
            manager = null;
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            using var retainedQueries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(retainedQueries.SearchSymbols(
                "Alpha45", "exact", null, 2));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(retainedDatabase)!),
                path => Path.GetFileName(path).StartsWith(
                            ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally
        {
            manager?.Dispose();
            RestoreMovedWorkspace(root, retainedRoot, moved);
        }
    }

    [Fact]
    public void LinuxStagedRebuildRejectsAReplacementDestinationDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-linux-authority-swap").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        string indexDirectory = Path.GetDirectoryName(database)!;
        string retainedDirectory = indexDirectory + "-retained";
        using var completed = new ManualResetEventSlim(false);
        try
        {
            WriteWorkspace(root);
            IndexBuilder.Build(root, database);
            using var manager = new IndexManager(root, database);
            manager.Start();
            Assert.True(WaitUntil(() =>
                    manager.IsQueryable &&
                    IndexDestinationClaim.ReadState(root, manager.DatabaseIoPath) ==
                    IndexDestinationClaimState.Ready,
                20_000), manager.Health().Error);
            string oldVersion = manager.Health().IndexVersion!;
            manager.FullRebuildCompletedForTest = () => completed.Set();

            Directory.Move(indexDirectory, retainedDirectory);
            Directory.CreateDirectory(indexDirectory);

            Assert.True(manager.RequestFullRebuild());
            Assert.True(completed.Wait(TimeSpan.FromSeconds(20)),
                "authority-mismatched rebuild did not return to the pump");
            Assert.True(WaitUntil(() => manager.IsQueryable, 20_000), manager.Health().Error);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);
            Assert.Equal(IndexDestinationClaimState.Ready,
                IndexDestinationClaim.ReadState(root, manager.DatabaseIoPath));
            using (IndexQueries oldQueries = manager.OpenQueries())
                Assert.Single(oldQueries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.Empty(Directory.EnumerateFileSystemEntries(indexDirectory));
        }
        finally { Cleanup(root); }
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
    public void SuccessorStartupRebuildWaitsForFollowerDatabaseHandleWithoutReaderSidecar()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-startup-review-drain").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
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
                FullRebuildPrivateStageReadyForTest = _ =>
                {
                    Assert.Equal(IndexDestinationClaimState.Ready,
                        IndexDestinationClaim.ReadState(root, database));
                    Assert.True(follower!.IsQueryable,
                        "successor startup hid the old publication during private staging");
                    using IndexQueries oldQueries = follower.OpenQueries();
                    Assert.Single(oldQueries.SearchSymbols(
                        "Alpha45", "exact", null, 2));
                },
                FullRebuildDestructiveBoundaryForTest = _ => boundary.Set(),
                FullRebuildCompletedForTest = () => completed.Set(),
            };
            successor.Start(forceRebuild: true);
            Assert.True(boundary.Wait(TimeSpan.FromSeconds(10)),
                "successor startup never reached its local rebuild boundary");
            Assert.True(WaitUntil(() => successor.State == "building" &&
                successor.Health().Error?.Contains("waiting for existing index readers",
                    StringComparison.OrdinalIgnoreCase) == true, 5_000),
                successor.Health().Error);
            Assert.True(successor.IsWriter);
            Assert.False(completed.IsSet,
                "successor replaced the database while a follower retained the old handle");
            Assert.Single(snapshot.Queries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.False(File.Exists(database + ".readers"));

            snapshot.Dispose();
            snapshot = null;
            Assert.True(completed.Wait(TimeSpan.FromSeconds(40)),
                "successor did not rebuild after the surviving follower released its handle");
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
    public async Task DirectBuildWaitsForFollowerDatabaseHandleWithoutCoordinationSidecar()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-45-direct-build-review-drain").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
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

            Task<BuildResult> rebuild = Task.Run(() => IndexBuilder.Build(root, database));
            await Task.Delay(500);
            Assert.False(rebuild.IsCompleted,
                "direct build completed while the follower retained the old database handle");
            Assert.Single(snapshot.Queries.SearchSymbols("Alpha45", "exact", null, 2));
            Assert.False(File.Exists(database + ".readers"));

            snapshot.Dispose();
            snapshot = null;
            BuildResult rebuilt = await rebuild.WaitAsync(TimeSpan.FromSeconds(40));
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
                         error.GetString()!.Contains("waiting for existing index readers",
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
            Assert.Equal(IndexDestinationClaimAcquireResult.Acquired,
                IndexDestinationClaim.TryAcquire(root, database,
                    out IndexDestinationClaim? destinationClaim));
            using (owner!)
            using (destinationClaim!)
            using (var follower = new IndexManager(root, database))
            {
                if (scenario is "stale" or "workspace")
                    destinationClaim!.SetReady();
                follower.Start();
                Assert.True(WaitUntil(() => follower.State == "failed", 20_000),
                    $"{scenario}: expected follower refusal, got {follower.State}");
                Assert.False(follower.IsWriter);
                Assert.Equal("follower", follower.AccessMode);
                Assert.False(follower.IsQueryable);
                Assert.Contains("writer", follower.Health().Error ?? "",
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains("compatible index", follower.Health().Error ?? "",
                    StringComparison.OrdinalIgnoreCase);
                Assert.False(follower.RequestRefresh());
                Assert.False(follower.RequestFullRebuild());
                using var semantic = new SemanticService(follower);
                var tools = new NavigationTools(follower, semantic);
                JsonElement refresh = Parse(tools.RefreshIndex(force: "full"));
                JsonElement worktree = Parse(tools.IndexWorktree(
                    Path.Combine(root, "never-created-worktree")));
                AssertWriterRequired(refresh);
                AssertWriterRequired(worktree);
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
    public void WindowsFollowerKeepsServingCommittedStateAcrossWriterExitAndSuccessorRebuild()
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
            Assert.True(follower.IsQueryable, follower.Health().Error);
            Assert.True(HasSymbol(followerTools, "Beta45"),
                "the follower lost the last committed index after its writer exited");

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

    private static void WriteWorkspace(string root) =>
        WriteWorkspace(root, "Alpha45");

    private static void WriteWorkspace(string root, string className)
    {
        File.WriteAllText(Path.Combine(root, "Batch45.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Alpha.cs"),
            $"namespace Batch45;\n\npublic class {className}\n{{\n}}\n");
    }

    private static void RestoreMovedWorkspace(
        string root, string retainedRoot, bool moved)
    {
        TestWorkspaceCleanup.ClearIndexPools(root);
        TestWorkspaceCleanup.ClearIndexPools(retainedRoot);
        if (moved)
        {
            Cleanup(root);
            if (Directory.Exists(retainedRoot))
                Directory.Move(retainedRoot, root);
        }
        Cleanup(root);
        Cleanup(retainedRoot);
    }

    private static void AssertNoPublicationArtifacts(string indexDirectory)
    {
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(indexDirectory),
            path => Path.GetFileName(path).StartsWith(
                        ".phoenix-stage-", StringComparison.Ordinal) ||
                    Path.GetFileName(path).StartsWith(
                        ".phoenix-publish-", StringComparison.Ordinal));
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
