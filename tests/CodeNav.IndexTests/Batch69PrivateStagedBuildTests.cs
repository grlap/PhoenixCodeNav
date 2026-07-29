using CodeNav.Core.Indexing;

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
}
