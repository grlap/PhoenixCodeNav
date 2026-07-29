using CodeNav.Core.Indexing;
using System.Runtime.InteropServices;

namespace CodeNav.Tests;

[CollectionDefinition("Batch69 staged build isolation", DisableParallelization = true)]
public sealed class Batch69StagedBuildCollection;

/// <summary>
/// Batch 69 (lf4p.3): a cold build can populate and finalize an already-reserved private
/// database inode without touching the live destination. Anchored publication tests live in the
/// lifecycle suite; this portable contract pins the complete staged database itself.
/// </summary>
[Collection("Batch69 staged build isolation")]
public sealed class Batch69PrivateStagedBuildTests
{
    [Fact]
    public void SupportedHostDirectBuilderAcceptsItsInstalledStageIdentity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-builder-installed-stage").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root, "InstalledStageAlpha69");

            BuildResult result = IndexBuilder.Build(root, database);

            Assert.Equal(1, result.CsFiles);
            using var queries = new IndexQueries(database,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(queries.SearchSymbols(
                "InstalledStageAlpha69", "exact", null, 2));
            AssertNoPublicationArtifacts(Path.GetDirectoryName(database)!);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void SupportedHostDirectBuilderReapsCrashArtifactsAndPreservesUnrelatedFiles()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-reap-crash-artifacts").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root, "CrashArtifactAlpha69");
            IndexBuilder.Build(root, database);
            string indexDirectory = Path.GetDirectoryName(database)!;
            string[] abandoned = WriteAbandonedPublicationArtifacts(indexDirectory);
            string unrelated = Path.Combine(indexDirectory,
                ".phoenix-stage-not-a-guid.db");
            File.WriteAllText(unrelated, "keep");

            var progress = new List<string>();
            IndexBuilder.Build(root, database, progress.Add);

            Assert.All(abandoned, path => Assert.False(File.Exists(path), path));
            Assert.True(File.Exists(unrelated));
            Assert.Contains(progress, line => line.Contains(
                $"Removed {abandoned.Length} abandoned index publication artifact(s)",
                StringComparison.Ordinal));
            using var queries = new IndexQueries(database,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(queries.SearchSymbols(
                "CrashArtifactAlpha69", "exact", null, 2));
            File.Delete(unrelated);
            AssertNoPublicationArtifacts(indexDirectory);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void SupportedHostManagerStartupReapsCrashArtifactsWithoutForcingARebuild()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-startup-reaps-crash-artifacts").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root, "StartupReapAlpha69");
            IndexBuilder.Build(root, database);
            string oldVersion;
            using (var prior = new IndexStore(database, createNew: false))
                oldVersion = prior.GetMeta("index_version")!;
            string indexDirectory = Path.GetDirectoryName(database)!;
            string[] abandoned = WriteAbandonedPublicationArtifacts(indexDirectory);

            using var manager = new IndexManager(root, database);
            manager.Start();

            Assert.All(abandoned, path => Assert.False(File.Exists(path), path));
            Assert.True(SpinWait.SpinUntil(
                () => manager.IsQueryable, TimeSpan.FromSeconds(20)),
                manager.Health().Error);
            Assert.Equal(oldVersion, manager.Health().IndexVersion);
            Assert.NotEqual("building", manager.Health().State);
            using IndexQueries queries = manager.OpenQueries();
            Assert.Single(queries.SearchSymbols(
                "StartupReapAlpha69", "exact", null, 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Theory]
    [InlineData(".phoenix-stage-0123456789abcdef0123456789abcdef.db", true)]
    [InlineData(".phoenix-stage-0123456789ABCDEF0123456789ABCDEF.db-wal", true)]
    [InlineData(".phoenix-stage-0123456789abcdef0123456789abcdef.db-shm", true)]
    [InlineData(".phoenix-stage-0123456789abcdef0123456789abcdef.db-journal", true)]
    [InlineData(".phoenix-publish-fedcba9876543210fedcba9876543210.db", true)]
    [InlineData(".phoenix-publish-fedcba9876543210fedcba9876543210.db-wal", true)]
    [InlineData(".phoenix-stage-not-a-guid.db", false)]
    [InlineData(".phoenix-stage-0123456789abcdef0123456789abcde.db", false)]
    [InlineData(".phoenix-stage-0123456789abcdef0123456789abcdef0.db", false)]
    [InlineData(".phoenix-stage-0123456789abcdef0123456789abcdeg.db", false)]
    [InlineData(".phoenix-stage-0123456789abcdef0123456789abcdef", false)]
    [InlineData("index.db", false)]
    public void PublicationArtifactNameClassificationIsExactAndPortable(
        string name, bool expected)
    {
        Assert.Equal(expected,
            AnchoredIndexDestination.IsPrivatePublicationArtifactName(name));
    }

    [Fact]
    public void PublicationArtifactSelectionIsCountAndTimeBoundBeforeHandleOpening()
    {
        string[] boundary = Enumerable.Range(
                0, AnchoredIndexDestination.MaxAbandonedPublicationArtifacts)
            .Select(i => $".phoenix-stage-{i:x32}.db")
            .ToArray();

        Assert.True(AnchoredIndexDestination.TrySelectPrivatePublicationArtifactNames(
            boundary,
            AnchoredIndexDestination.MaxAbandonedPublicationArtifacts,
            TimeSpan.FromMinutes(1),
            out List<string> selected,
            out PublicationArtifactReapFailure failure,
            out int observedCandidates));
        Assert.Equal(boundary, selected);
        Assert.Equal(PublicationArtifactReapFailure.None, failure);
        Assert.Equal(boundary.Length, observedCandidates);

        Assert.False(AnchoredIndexDestination.TrySelectPrivatePublicationArtifactNames(
            boundary.Append(
                ".phoenix-publish-ffffffffffffffffffffffffffffffff.db"),
            AnchoredIndexDestination.MaxAbandonedPublicationArtifacts,
            TimeSpan.FromMinutes(1),
            out selected,
            out failure,
            out observedCandidates));
        Assert.Equal(AnchoredIndexDestination.MaxAbandonedPublicationArtifacts,
            selected.Count);
        Assert.Equal(PublicationArtifactReapFailure.CandidateLimitExceeded, failure);
        Assert.Equal(AnchoredIndexDestination.MaxAbandonedPublicationArtifacts + 1,
            observedCandidates);
        string countDetail =
            AnchoredIndexDestination.DescribePublicationArtifactReapFailure(
                failure, observedCandidates);
        Assert.Contains("256-artifact cap", countDetail, StringComparison.Ordinal);
        Assert.Contains("257 candidates", countDetail, StringComparison.Ordinal);

        int elapsedChecks = 0;
        Assert.False(AnchoredIndexDestination.TrySelectPrivatePublicationArtifactNames(
            boundary.Take(2),
            AnchoredIndexDestination.MaxAbandonedPublicationArtifacts,
            TimeSpan.FromSeconds(1),
            out selected,
            out failure,
            out observedCandidates,
            elapsedForTest: () => elapsedChecks++ == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(2)));
        Assert.Single(selected);
        Assert.Equal(PublicationArtifactReapFailure.ScanBudgetExceeded, failure);
        Assert.Equal(1, observedCandidates);
        string timeDetail =
            AnchoredIndexDestination.DescribePublicationArtifactReapFailure(
                failure, observedCandidates);
        Assert.Contains("5-second budget", timeDetail, StringComparison.Ordinal);
        Assert.Contains("1 candidate", timeDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedHostReaperRequiresTheCompleteHardLinkSet()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-reap-hardlinks").FullName;
        string external = Directory.CreateTempSubdirectory(
            "codenav-69-reap-hardlinks-external").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root, "HardLinkAlpha69");
            IndexBuilder.Build(root, database);
            string indexDirectory = Path.GetDirectoryName(database)!;

            string internalStage = Path.Combine(indexDirectory,
                ".phoenix-stage-11111111111111111111111111111111.db");
            string internalPublish = Path.Combine(indexDirectory,
                ".phoenix-publish-22222222222222222222222222222222.db");
            File.WriteAllText(internalStage, "internal");
            CreateHardLinkForTest(internalPublish, internalStage);

            IndexBuilder.Build(root, database);

            Assert.False(File.Exists(internalStage));
            Assert.False(File.Exists(internalPublish));

            string externalFile = Path.Combine(external, "outside.db");
            string incompleteSet = Path.Combine(indexDirectory,
                ".phoenix-stage-33333333333333333333333333333333.db");
            File.WriteAllText(externalFile, "external-owner");
            CreateHardLinkForTest(incompleteSet, externalFile);

            IOException refused = Assert.Throws<IOException>(
                () => IndexBuilder.Build(root, database));

            Assert.Contains("could not be cleaned safely", refused.Message,
                StringComparison.Ordinal);
            Assert.True(File.Exists(incompleteSet));
            Assert.Equal("external-owner", File.ReadAllText(externalFile));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(external);
        }
    }

    [Fact]
    public void SupportedHostContendedWriterCannotReapTheActiveClaimedStage()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-active-stage-refusal").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        try
        {
            WriteWorkspace(root, "ActiveStageAlpha69");
            IndexBuilder.Build(root, database);
            Assert.True(AnchoredIndexDestination.TryOpen(
                root, root, database, createIndexDirectory: false,
                out AnchoredIndexDestination? destination));
            using (destination!)
            {
                Assert.True(destination!.TryGetLeaseIdentity(
                    out IndexLeaseIdentity? leaseIdentity));
                Assert.True(IndexOwnershipLease.TryAcquire(
                    root, database, leaseIdentity,
                    out IndexOwnershipLease? ownershipLease));
                using (ownershipLease!)
                {
                    Assert.Equal(IndexDestinationClaimAcquireResult.Acquired,
                        IndexDestinationClaim.TryAcquire(
                            root, destination.DatabaseAuthorityPath,
                            out IndexDestinationClaim? destinationClaim));
                    using (destinationClaim!)
                    {
                        _ = destination.CreateStagePath();
                        string indexDirectory = Path.GetDirectoryName(database)!;
                        string activeStage = Assert.Single(
                            Directory.EnumerateFiles(
                                indexDirectory, ".phoenix-stage-*.db"),
                            path => !path.EndsWith("-wal", StringComparison.Ordinal) &&
                                    !path.EndsWith("-shm", StringComparison.Ordinal) &&
                                    !path.EndsWith("-journal", StringComparison.Ordinal));

                        using (var contender = new IndexManager(root, database))
                            contender.Start(forceRebuild: true);

                        Assert.True(File.Exists(activeStage));
                        Assert.True(File.Exists(activeStage + "-wal"));
                        Assert.True(File.Exists(activeStage + "-shm"));
                        Assert.True(File.Exists(activeStage + "-journal"));
                    }
                }
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ReservedPrivateStageReceivesACompleteQueryableColdBuild()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-69-private-stage").FullName;
        try
        {
            string project = Path.Combine(root, "P");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(project, "A.cs"),
                "namespace P; public class PrivateStageAlpha69 { }");

            string indexDirectory = Path.Combine(root, ".codenav");
            Directory.CreateDirectory(indexDirectory);
            string stagePath = Path.Combine(indexDirectory,
                ".phoenix-stage-contract.db");
            using (FileStream reservation = new(stagePath, FileMode.CreateNew,
                       FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                Assert.Equal(0, reservation.Length);
            }

            BuildResult result = IndexBuilder.BuildOwned(root, stagePath,
                reservedPrivateStage: true);

            Assert.Equal(1, result.CsFiles);
            Assert.True(new FileInfo(stagePath).Length > 0);
            Assert.False(File.Exists(stagePath + "-wal"));
            Assert.False(File.Exists(stagePath + "-shm"));
            Assert.False(File.Exists(stagePath + "-journal"));
            using var store = new IndexStore(stagePath, createNew: false);
            Assert.Equal(IndexBuilder.SchemaVersion, store.GetMeta("schema_version"));
            using var queries = new IndexQueries(stagePath,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(queries.SearchSymbols(
                "PrivateStageAlpha69", "exact", null, 2));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void LinuxDirectBuilderRejectsAReplacementDestinationDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-builder-authority-swap").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        string indexDirectory = Path.GetDirectoryName(database)!;
        string retainedDirectory = indexDirectory + "-retained";
        try
        {
            string project = Path.Combine(root, "P");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(project, "A.cs"),
                "namespace P; public class DirectBuilderAlpha69 { }");
            IndexBuilder.Build(root, database);

            IndexBuilder.BeforeAnchoredDestinationOpenForTest = () =>
            {
                Directory.Move(indexDirectory, retainedDirectory);
                Directory.CreateDirectory(indexDirectory);
            };
            IOException error = Assert.Throws<IOException>(
                () => IndexBuilder.Build(root, database));

            Assert.Contains("differs from the retained index authority", error.Message,
                StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(indexDirectory));
            string retainedDatabase = Path.Combine(retainedDirectory,
                Path.GetFileName(database));
            using var queries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(queries.SearchSymbols(
                "DirectBuilderAlpha69", "exact", null, 2));
        }
        finally
        {
            IndexBuilder.BeforeAnchoredDestinationOpenForTest = null;
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void LinuxDirectBuilderReadsThePinnedWorkspaceAndRejectsAReplacementRoot()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-builder-workspace-swap").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        bool moved = false;
        try
        {
            WriteWorkspace(root, "PinnedWorkspaceAlpha69");
            IndexBuilder.Build(root, database);

            IndexBuilder.AnchoredStageReadyForTest = _ =>
            {
                Directory.Move(root, retainedRoot);
                moved = true;
                Directory.CreateDirectory(root);
                WriteWorkspace(root, "ReplacementWorkspaceBeta69");
            };
            IndexBuilder.AnchoredStageCompletedForTest = stagePath =>
            {
                using var stageStore = new IndexStore(stagePath, createNew: false);
                Assert.Equal(Path.GetFullPath(root),
                    stageStore.GetMeta("workspace_root"));
                using var stageQueries = new IndexQueries(stagePath,
                    pinReadSnapshot: false, pooling: false);
                Assert.Single(stageQueries.SearchSymbols(
                    "PinnedWorkspaceAlpha69", "exact", null, 2));
                Assert.Empty(stageQueries.SearchSymbols(
                    "ReplacementWorkspaceBeta69", "exact", null, 2));
            };

            IOException error = Assert.Throws<IOException>(
                () => IndexBuilder.Build(root, database));

            Assert.Contains("workspace differs from the retained workspace authority",
                error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(database));
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            using var queries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(queries.SearchSymbols(
                "PinnedWorkspaceAlpha69", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols(
                "ReplacementWorkspaceBeta69", "exact", null, 2));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(retainedDatabase)!),
                path => Path.GetFileName(path).StartsWith(
                            ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally
        {
            IndexBuilder.AnchoredStageReadyForTest = null;
            IndexBuilder.AnchoredStageCompletedForTest = null;
            TestWorkspaceCleanup.ClearIndexPools(root);
            TestWorkspaceCleanup.ClearIndexPools(retainedRoot);
            if (moved)
            {
                TestWorkspaceCleanup.DeleteWorkspace(root);
                if (Directory.Exists(retainedRoot))
                    Directory.Move(retainedRoot, root);
            }
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(retainedRoot);
        }
    }

    [Fact]
    public void LinuxDirectBuilderRejectsAWholeRootReplacementAfterStageInstall()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-builder-post-install-root-swap").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        bool moved = false;
        try
        {
            WriteWorkspace(root, "PostInstallAlpha69");
            IndexBuilder.Build(root, database);
            string oldVersion;
            using (var oldStore = new IndexStore(database, createNew: false))
                oldVersion = oldStore.GetMeta("index_version")!;
            IndexBuilder.AnchoredStageInstalledForTest = () =>
            {
                Directory.Move(root, retainedRoot);
                moved = true;
                Directory.CreateDirectory(root);
                WriteWorkspace(root, "PostInstallReplacementBeta69");
                Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            };

            IOException error = Assert.Throws<IOException>(
                () => IndexBuilder.Build(root, database));

            Assert.Contains("live index destination differs", error.Message,
                StringComparison.Ordinal);
            Assert.False(File.Exists(database));
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            using var retainedStore = new IndexStore(retainedDatabase, createNew: false);
            Assert.NotEqual(oldVersion, retainedStore.GetMeta("index_version"));
            using var queries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(queries.SearchSymbols(
                "PostInstallAlpha69", "exact", null, 2));
            Assert.Empty(queries.SearchSymbols(
                "PostInstallReplacementBeta69", "exact", null, 2));
            AssertNoPublicationArtifacts(Path.GetDirectoryName(retainedDatabase)!);
            AssertNoPublicationArtifacts(Path.GetDirectoryName(database)!);
        }
        finally
        {
            IndexBuilder.AnchoredStageInstalledForTest = null;
            TestWorkspaceCleanup.ClearIndexPools(root);
            TestWorkspaceCleanup.ClearIndexPools(retainedRoot);
            if (moved)
            {
                TestWorkspaceCleanup.DeleteWorkspace(root);
                if (Directory.Exists(retainedRoot))
                    Directory.Move(retainedRoot, root);
            }
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(retainedRoot);
        }
    }

    [Fact]
    public void LinuxDirectBuilderFailsClosedWhenTheWorkspaceDisappearsBeforeAnchorOpen()
    {
        if (!OperatingSystem.IsLinux()) return;

        string root = Directory.CreateTempSubdirectory(
            "codenav-69-builder-pre-anchor-move").FullName;
        string retainedRoot = root + "-retained";
        string database = IndexBuilder.DefaultDbPath(root);
        bool moved = false;
        try
        {
            WriteWorkspace(root, "PreAnchorAlpha69");
            IndexBuilder.Build(root, database);
            IndexBuilder.BeforeAnchoredDestinationOpenForTest = () =>
            {
                Directory.Move(root, retainedRoot);
                moved = true;
            };

            IOException error = Assert.Throws<IOException>(
                () => IndexBuilder.Build(root, database));

            Assert.Contains("workspace differs from the retained workspace authority",
                error.Message, StringComparison.Ordinal);
            string retainedDatabase = IndexBuilder.DefaultDbPath(retainedRoot);
            using var queries = new IndexQueries(retainedDatabase,
                pinReadSnapshot: false, pooling: false);
            Assert.Single(queries.SearchSymbols(
                "PreAnchorAlpha69", "exact", null, 2));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(
                    Path.GetDirectoryName(retainedDatabase)!),
                path => Path.GetFileName(path).StartsWith(
                            ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
        }
        finally
        {
            IndexBuilder.BeforeAnchoredDestinationOpenForTest = null;
            TestWorkspaceCleanup.ClearIndexPools(root);
            TestWorkspaceCleanup.ClearIndexPools(retainedRoot);
            if (moved && Directory.Exists(retainedRoot))
                Directory.Move(retainedRoot, root);
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(retainedRoot);
        }
    }

    private static void WriteWorkspace(string root, string className)
    {
        string project = Path.Combine(root, "P");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "P.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(project, "A.cs"),
            $"namespace P; public class {className} {{ }}");
    }

    private static void AssertNoPublicationArtifacts(string indexDirectory)
    {
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(indexDirectory),
            path => Path.GetFileName(path).StartsWith(
                        ".phoenix-stage-", StringComparison.Ordinal) ||
                        Path.GetFileName(path).StartsWith(
                            ".phoenix-publish-", StringComparison.Ordinal));
    }

    private static string[] WriteAbandonedPublicationArtifacts(string indexDirectory)
    {
        string stage = Path.Combine(indexDirectory,
            $".phoenix-stage-{Guid.NewGuid():N}.db");
        string publish = Path.Combine(indexDirectory,
            $".phoenix-publish-{Guid.NewGuid():N}.db");
        string[] paths =
        [
            stage,
            stage + "-wal",
            stage + "-shm",
            stage + "-journal",
            publish,
        ];
        foreach (string path in paths) File.WriteAllText(path, "crash");
        return paths;
    }

    private static void CreateHardLinkForTest(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
            Assert.True(CreateHardLinkW(linkPath, existingPath, IntPtr.Zero));
        else
            Assert.Equal(0, link(existingPath, linkPath));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName,
        string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);
}
