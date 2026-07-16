using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using System.Diagnostics;
using System.Text.Json;
using static CodeNav.Tests.Batch43Support;

namespace CodeNav.Tests;

/// <summary>
/// Owns: slice 2 of 2 of the Batch 43 (v0.11.1) git review-safety suite — a contiguous,
/// duration-balanced block of tests moved VERBATIM (xUnit parallelizes across classes but
/// runs one class serially).
/// Deliberately does not own: the shared helpers (Batch43Support.cs) or the sibling slice.
/// Split out of: Batch43GitReviewSafetyTests.cs (PhoenixCodeNav-6zdy).
/// </summary>
public class Batch43GitReviewSafetyTestsPart2
{
    [Fact]
    public void ReviewExcludesSubmoduleWorktreeDirtAndReportsCoverageWithoutExecutingChildFilter()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-submodule-root").FullName;
        string origin = Directory.CreateTempSubdirectory("codenav-y8e-submodule-origin").FullName;
        try
        {
            InitRepo(origin, gitExe);
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            File.WriteAllText(Path.Combine(origin, ".gitattributes"),
                "Child.cs filter=childsentinel\n");
            Git(origin, gitExe, "config filter.childsentinel.clean cat");
            Git(origin, gitExe, "config filter.childsentinel.required true");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m initial");

            InitRepo(root, gitExe);
            File.WriteAllText(Path.Combine(root, "Root.cs"), "class Root { }\n");
            Git(root, gitExe,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" Sub");
            File.WriteAllText(Path.Combine(root, ".gitattributes"),
                "Sub filter=parentsentinel\n");
            Git(root, gitExe, "config -f .gitmodules submodule.Sub.ignore all");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m parent");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            Git(root, gitExe, "config diff.ignoreSubmodules all");
            Git(root, gitExe, "config submodule.recurse true");
            Git(root, gitExe, "config diff.submodule diff");
            string tools = Path.Combine(root, "Sub", "helper-tools");
            Directory.CreateDirectory(tools);
            string marker = Path.Combine(tools, "child-filter-ran.txt");
            string helper = Path.Combine(tools,
                OperatingSystem.IsWindows() ? "filter.cmd" : "filter.sh");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(helper,
                    $"@echo off\r\n>\"{marker}\" echo ran\r\nexit /b 1\r\n");
            }
            else
            {
                File.WriteAllText(helper,
                    "#!/bin/sh\nprintf ran > \"$(dirname \"$0\")/child-filter-ran.txt\"\nexit 1\n");
                File.SetUnixFileMode(helper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Git(Path.Combine(root, "Sub"), gitExe,
                $"config filter.childsentinel.clean \"{helper.Replace('\\', '/')}\"");
            Git(Path.Combine(root, "Sub"), gitExe,
                "config filter.childsentinel.required true");
            Git(root, gitExe,
                $"config filter.parentsentinel.clean \"{helper.Replace('\\', '/')}\"");
            Git(root, gitExe, "config filter.parentsentinel.required true");
            File.SetLastWriteTimeUtc(Path.Combine(root, "Sub", "Child.cs"),
                DateTime.UtcNow.AddMinutes(2));

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");
            var result = review.Diff;
            var dirty = review.Dirty;
            var standaloneDirty = GitInfo.DirtyFiles(root, gitExe);
            var standaloneDetailed = GitInfo.DiffHunksDetailed(root, head, gitExe);
            var standaloneSimple = GitInfo.DiffHunks(root, head, gitExe);

            Assert.False(File.Exists(marker),
                "parent review executed a child repository clean filter");
            Assert.Equal("ok", result.Status);
            Assert.Empty(Assert.IsType<List<GitInfo.DiffFile>>(result.Files));
            Assert.DoesNotContain("Sub", Assert.IsType<List<string>>(dirty));
            var coverage = Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                review.ExcludedSubmoduleWorktrees);
            Assert.Equal(1, coverage.Count);
            Assert.Equal("Sub", Assert.Single(coverage.SamplePaths));
            Assert.Null(review.ExcludedUntrackedRepositories);
            var resultCoverage = Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                result.ExcludedSubmoduleWorktrees);
            Assert.Equal(coverage.Count, resultCoverage.Count);
            Assert.Equal(coverage.SamplePaths, resultCoverage.SamplePaths);
            Assert.Equal("ok", standaloneDetailed.Status);
            var standaloneCoverage = Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                standaloneDetailed.ExcludedSubmoduleWorktrees);
            Assert.Equal(coverage.Count, standaloneCoverage.Count);
            Assert.Equal(coverage.SamplePaths, standaloneCoverage.SamplePaths);
            Assert.Null(standaloneSimple); // simple API cannot silently discard partial coverage
            Assert.Null(standaloneDirty); // caller must take its honest full-sweep fallback
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void ReviewStillReportsAStagedGitlinkChangeWhileChildDirtIsExcluded()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-root").FullName;
        string origin = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-origin").FullName;
        try
        {
            InitRepo(origin, gitExe);
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m initial");

            InitRepo(root, gitExe);
            Git(root, gitExe,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" Sub");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m parent");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            File.AppendAllText(Path.Combine(origin, "Child.cs"), "// next\n");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m next");
            string next = GitOutput(origin, gitExe, "rev-parse HEAD").Trim();
            string child = Path.Combine(root, "Sub");
            Git(child, gitExe, "-c protocol.file.allow=always fetch -q origin");
            Git(child, gitExe, $"checkout -q {next}");
            Git(root, gitExe, "add Sub");

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            var link = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Sub");
            Assert.True(link.SubmoduleLink);
            Assert.Equal("Sub", Assert.Single(review.ChangedSubmoduleLinks));
            Assert.Contains("Sub", Assert.IsType<List<string>>(review.Dirty));
            Assert.Equal("Sub", Assert.Single(Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                review.ExcludedSubmoduleWorktrees).SamplePaths));
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void ReviewReportsADeletedGitlinkEvenAfterItLeavesTheCurrentIndex()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-delete-root").FullName;
        string origin = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-delete-origin").FullName;
        try
        {
            InitRepo(origin, gitExe);
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m initial");
            InitRepo(root, gitExe);
            Git(root, gitExe,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" Sub");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m parent");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            Git(root, gitExe, "rm -q -f Sub");
            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            var deleted = Assert.Single(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Sub");
            Assert.True(deleted.Deleted);
            Assert.True(deleted.SubmoduleLink);
            Assert.Equal("Sub", Assert.Single(review.ChangedSubmoduleLinks));
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void IndexOnlySubmoduleRemovalIsLayeredAndStillReportsExcludedChildCoverage()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-retained-root").FullName;
        string origin = Directory.CreateTempSubdirectory("codenav-y8e-gitlink-retained-origin").FullName;
        try
        {
            InitRepo(origin, gitExe);
            File.WriteAllText(Path.Combine(origin, "Child.cs"), "class Child { }\n");
            File.WriteAllText(Path.Combine(origin, ".gitattributes"),
                "Child.cs filter=childsentinel\n");
            Git(origin, gitExe, "config filter.childsentinel.clean cat");
            Git(origin, gitExe, "config filter.childsentinel.required true");
            Git(origin, gitExe, "add -A");
            Git(origin, gitExe, "commit -q -m initial");

            InitRepo(root, gitExe);
            Git(root, gitExe,
                $"-c protocol.file.allow=always submodule add -q \"{origin}\" Sub");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m parent");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();

            string child = Path.Combine(root, "Sub");
            string marker = Path.Combine(child, "child-filter-ran.txt");
            string helper = Path.Combine(child,
                OperatingSystem.IsWindows() ? "filter.cmd" : "filter.sh");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(helper,
                    $"@echo off\r\n>\"{marker}\" echo ran\r\nexit /b 1\r\n");
            }
            else
            {
                File.WriteAllText(helper,
                    "#!/bin/sh\nprintf ran > \"$(dirname \"$0\")/child-filter-ran.txt\"\nexit 1\n");
                File.SetUnixFileMode(helper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Git(child, gitExe,
                $"config filter.childsentinel.clean \"{helper.Replace('\\', '/')}\"");
            Git(child, gitExe, "config filter.childsentinel.required true");
            File.AppendAllText(Path.Combine(child, "Child.cs"), "// dirty\n");
            Git(root, gitExe, "rm --cached -q -f Sub");

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "layered_changes", r => r.Diff.Status ?? "<null>", "ReviewDiff status layered_changes");

            Assert.False(File.Exists(marker),
                "parent review executed a retained child repository clean filter");
            Assert.Equal("layered_changes", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.Equal("Sub", Assert.Single(review.ChangedSubmoduleLinks));
            var coverage = Assert.IsType<GitInfo.SubmoduleWorktreeCoverage>(
                review.ExcludedSubmoduleWorktrees);
            Assert.Equal(1, coverage.Count);
            Assert.Equal("Sub", Assert.Single(coverage.SamplePaths));
            Assert.Null(review.ExcludedUntrackedRepositories);
        }
        finally
        {
            Cleanup(root);
            Cleanup(origin);
        }
    }

    [Fact]
    public void DiffHunksReturnsAnEmptyListForACleanTree()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-clean").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Assert.Empty(Assert.IsType<List<GitInfo.DiffFile>>(
                GitInfo.DiffHunks(root, head, gitExe)));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DirtyFilesSupportsAnUnbornRepositoryInsideTheFilterSandbox()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-unborn-dirt").FullName;
        try
        {
            InitRepo(root, gitExe);
            Directory.CreateDirectory(Path.Combine(root, "New"));
            File.WriteAllText(Path.Combine(root, "New", "Source.cs"), "class Unborn { }\n");

            Assert.Contains("New/Source.cs",
                Assert.IsType<List<string>>(GitInfo.DirtyFiles(root, gitExe)));

            Git(root, gitExe, "add New/Source.cs");
            Assert.Contains("New/Source.cs",
                Assert.IsType<List<string>>(GitInfo.DirtyFiles(root, gitExe)));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FilterSandboxDoesNotDependOnTheRepositoriesRefStorageBackend()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-reftable").FullName;
        try
        {
            var initialized = GitInfo.RunProcessEx(gitExe, root,
                "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " +
                "init -q -b main --ref-format=reftable", waitMs: 20_000);
            if (initialized.Status != "ok") return; // Older Git builds do not expose reftable.
            Git(root, gitExe, "config user.email test@example.com");
            Git(root, gitExe, "config user.name CodeNavTest");
            Git(root, gitExe, "config commit.gpgsign false");
            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Initial { }\n");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Changed { }\n");

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Source.cs");
            Assert.Contains("Source.cs", Assert.IsType<List<string>>(review.Dirty));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FilterSandboxPreservesTheRepositoriesObjectFormat()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-sha256").FullName;
        try
        {
            var initialized = GitInfo.RunProcessEx(gitExe, root,
                "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " +
                "init -q -b main --object-format=sha256", waitMs: 20_000);
            if (initialized.Status != "ok") return; // Older Git builds may omit SHA-256 repos.
            Git(root, gitExe, "config user.email test@example.com");
            Git(root, gitExe, "config user.name CodeNavTest");
            Git(root, gitExe, "config commit.gpgsign false");
            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Initial256 { }\n");
            Git(root, gitExe, "add -A");
            Git(root, gitExe, "commit -q -m initial");
            string head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            Assert.Equal(64, head.Length);
            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Second256 { }\n");
            Git(root, gitExe, "add Source.cs");
            Git(root, gitExe, "commit -q -m second");
            string second = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            string fortyHex = head[..40];
            Git(root, gitExe, $"branch {fortyHex} {second}");

            Assert.Null(GitInfo.ResolveRef(root, fortyHex, gitExe));
            Assert.Equal(head, GitInfo.ResolveRef(root, head, gitExe));

            File.WriteAllText(Path.Combine(root, "Source.cs"), "class Changed256 { }\n");

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Source.cs");
            Assert.Contains("Source.cs", Assert.IsType<List<string>>(review.Dirty));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void FilterSandboxTreatsAMissingRepositoryFormatKeyAsLegacyVersionZero()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-implicit-format0").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, "config --local --unset core.repositoryformatversion");
            EditSource(root);

            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Source.cs");
            Assert.Contains("Source.cs", Assert.IsType<List<string>>(review.Dirty));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task UnixMetadataFifoIsRejectedWithoutWaitingForAWriter()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-fifo").FullName;
        string fifo = Path.Combine(root, "metadata");
        try
        {
            Assert.Equal(0, MakeFifo(fifo, 0x180)); // 0600
            Task<byte[]?> read = Task.Run(() =>
                GitInfo.ReadBoundedRegularFile(fifo, 1024, root));
            try
            {
                Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            catch (TimeoutException)
            {
                // Rescue the deliberately reintroduced buggy implementation: its blocking FIFO
                // reader is released by one writer byte, so a red test never leaks a stuck task.
                await Task.Run(() =>
                {
                    using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite);
                    writer.WriteByte(1);
                }).WaitAsync(TimeSpan.FromSeconds(2));
                await read.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Fail("repository metadata reads must not block on a FIFO");
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void GitConfigQuotingPreservesLiteralBackslashes()
    {
        Assert.Equal("\"/tmp/repo\\\\name/config\"",
            GitInfo.QuoteGitConfigValue("/tmp/repo\\name/config"));
    }

    [Fact]
    public void WindowsGitPathsRejectBackslashesBeforeFilesystemUse()
    {
        const string hostile = @"safe\..\outside.cs";
        Assert.Equal(!OperatingSystem.IsWindows(), GitInfo.IsSafeRelativeGitPath(hostile));
        Assert.True(GitInfo.IsSafeRelativeGitPath("safe/outside.cs"));
    }

    [Fact]
    public void MetadataSnapshotAcceptsOnlyBoundedRegularFiles()
    {
        string root = Directory.CreateTempSubdirectory("codenav-y8e-metadata-kind").FullName;
        try
        {
            string missing = Path.Combine(root, "missing");
            Assert.Empty(Assert.IsType<byte[]>(
                GitInfo.ReadBoundedRegularFile(missing, 4, root)));
            string directory = Directory.CreateDirectory(Path.Combine(root, "directory")).FullName;
            Assert.Null(GitInfo.ReadBoundedRegularFile(directory, 4, root));

            string regular = Path.Combine(root, "regular");
            File.WriteAllBytes(regular, [1, 2, 3, 4]);
            Assert.Equal(new byte[] { 1, 2, 3, 4 },
                GitInfo.ReadBoundedRegularFile(regular, 4, root));
            Assert.Null(GitInfo.ReadBoundedRegularFile(regular, 3, root));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void MetadataSnapshotRejectsAnAncestorDirectoryLink()
    {
        string root = Directory.CreateTempSubdirectory("codenav-y8e-metadata-root").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-y8e-metadata-outside").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "attributes"), "*.cs filter=outside\n");
            string linked = Path.Combine(root, "info");
            CreateDirectoryLink(linked, outside, root);

            Assert.Null(GitInfo.ReadBoundedRegularFile(
                Path.Combine(linked, "attributes"), 1024, root));
        }
        finally
        {
            try { Directory.Delete(Path.Combine(root, "info")); } catch { }
            Cleanup(root);
            Cleanup(outside);
        }
    }

    [Fact]
    public void MetadataSnapshotRejectsALinkInsideTheAllowedRootPath()
    {
        string root = Directory.CreateTempSubdirectory("codenav-y8e-anchor-root").FullName;
        string outside = Directory.CreateTempSubdirectory("codenav-y8e-anchor-outside").FullName;
        string linked = Path.Combine(root, "linked-common");
        try
        {
            string realAnchor = Directory.CreateDirectory(Path.Combine(outside, "repo")).FullName;
            string realInfo = Directory.CreateDirectory(Path.Combine(realAnchor, "info")).FullName;
            File.WriteAllText(Path.Combine(realInfo, "attributes"), "*.cs filter=outside\n");
            CreateDirectoryLink(linked, outside, root);
            string redirectedAnchor = Path.Combine(linked, "repo");

            Assert.Null(GitInfo.ReadBoundedRegularFile(
                Path.Combine(redirectedAnchor, "info", "attributes"), 1024,
                redirectedAnchor));
        }
        finally
        {
            try { Directory.Delete(linked); } catch { }
            Cleanup(root);
            Cleanup(outside);
        }
    }

    [Fact]
    public void MetadataSnapshotPreservesAFileSystemRootAnchor()
    {
        string volumeRoot = Assert.IsType<string>(
            Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath())));
        string missing = Path.Combine(volumeRoot,
            "codenav-definitely-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(Assert.IsType<byte[]>(
            GitInfo.ReadBoundedRegularFile(missing, 4, volumeRoot)));
    }

    [Fact]
    public void DirtyFilesExpandsOrdinaryDirectoriesButExcludesEmbeddedRepositoriesAtomically()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-qwn-core-nested").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Directory.CreateDirectory(Path.Combine(root, "Ordinary"));
            File.WriteAllText(Path.Combine(root, "Ordinary", "Fresh.cs"), "class Fresh { }\n");
            Assert.Contains("Ordinary/Fresh.cs",
                Assert.IsType<List<string>>(GitInfo.DirtyFiles(root, gitExe)));

            string nested = Path.Combine(root, "NestedRepo");
            Directory.CreateDirectory(nested);
            InitRepo(nested, gitExe);
            File.WriteAllText(Path.Combine(nested, "Child.cs"), "class Child { }\n");
            Git(nested, gitExe, "add -A");
            Git(nested, gitExe, "commit -q -m child");

            Assert.Null(GitInfo.DirtyFiles(root, gitExe));
            var review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");
            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains("Ordinary/Fresh.cs", Assert.IsType<List<string>>(review.Dirty));
            var coverage = Assert.IsType<GitInfo.UntrackedRepositoryCoverage>(
                review.ExcludedUntrackedRepositories);
            Assert.Equal(1, coverage.Count);
            Assert.Equal("NestedRepo", Assert.Single(coverage.SamplePaths));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DirtyFilesDoesNotCaptureASecondHugePatchJustToFindOnePath()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-huge-dirt").FullName;
        try
        {
            CreateRepo(root, gitExe);
            File.WriteAllText(Path.Combine(root, "Source.cs"),
                "class Huge { string Value = \"" + new string('x', 9 * 1024 * 1024) + "\"; }\n");

            var dirty = GitInfo.DirtyFiles(root, gitExe);

            Assert.Equal("Source.cs", Assert.Single(Assert.IsType<List<string>>(dirty)));
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    public void RealBatchWrappersResolveRefsAndReadExactSpecialPathBlobs(string extension)
    {
        if (!OperatingSystem.IsWindows()) return;
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-bhd-wrapper").FullName;
        try
        {
            string relativePath = "Lib/%TEMP% & bang! caret^ (x).cs";
            string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "initial");
            InitRepo(root, realGit);

            string wrapperDir = Path.Combine(root, "wrapper tools");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "wrapper tools/\n");
            Directory.CreateDirectory(wrapperDir);
            string wrapper = Path.Combine(wrapperDir, "git" + extension);
            File.WriteAllText(wrapper,
                $"@echo off\r\n>>\"%~dp0args.txt\" echo(%*\r\n\"{realGit}\" %*\r\n");

            string[] contents = { "", "plain", "one newline\n", "two newlines\n\n", "Zażółć 🐦\n\n" };
            string latest = "";
            for (int i = 0; i < contents.Length; i++)
            {
                File.WriteAllText(fullPath, contents[i]);
                Git(root, realGit, "add -A");
                Git(root, realGit, $"commit -q -m blob-{i}");
                latest = GitOutput(root, realGit, "rev-parse HEAD").Trim();
                Assert.Equal(contents[i], GitInfo.ShowFile(root, latest, relativePath, wrapper));
            }

            Git(root, realGit, "tag release-light");
            Git(root, realGit, "tag -a release-annotated -m tag");
            Assert.Equal(latest, GitInfo.ResolveRef(root, "HEAD", wrapper));
            Assert.Equal(latest, GitInfo.ResolveRef(root, "main", wrapper));
            Assert.Equal(latest, GitInfo.ResolveRef(root, "release-light", wrapper));
            Assert.Equal(latest, GitInfo.ResolveRef(root, "release-annotated", wrapper));
            Assert.Equal(latest, GitInfo.ResolveRef(root, latest, wrapper));
            Assert.Null(GitInfo.ResolveRef(root, "missing-ref", wrapper));
            Assert.Null(GitInfo.ShowFile(root, latest, "Lib/missing.cs", wrapper));

            string argsLog = File.ReadAllText(Path.Combine(wrapperDir, "args.txt"));
            Assert.Contains("cat-file --batch-check", argsLog);
            Assert.Contains("cat-file --batch", argsLog);
            Assert.DoesNotContain("%TEMP%", argsLog, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("release-annotated", argsLog, StringComparison.Ordinal);
            Assert.DoesNotContain("HEAD", argsLog, StringComparison.Ordinal);
            Assert.DoesNotContain(latest, argsLog, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ShowFileAcceptsQuotedAndLongSafePathsBecauseTheyTravelOnlyOnStdin()
    {
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-bhd-stdin-path").FullName;
        try
        {
            string head = CreateRepo(root, realGit);
            string expected = File.ReadAllText(Path.Combine(root, "Source.cs"));
            string wrapper;
            if (OperatingSystem.IsWindows())
            {
                wrapper = Path.Combine(root, "git-fixed.cmd");
                File.WriteAllText(wrapper,
                    $"@echo off\r\necho %*|findstr /c:\"rev-parse --show-prefix\" >nul && (echo. & exit /b 0)\r\necho %*|findstr /c:\"cat-file --batch-check\" >nul && (echo {head}:Source.cs|\"{realGit}\" -C \"{root}\" cat-file --batch-check & exit /b 0)\r\necho {head}:Source.cs|\"{realGit}\" -C \"{root}\" cat-file --batch\r\n");
            }
            else
            {
                wrapper = Path.Combine(root, "git-fixed.sh");
                File.WriteAllText(wrapper,
                    $"#!/bin/sh\ncase \"$*\" in *\"rev-parse --show-prefix\"*) printf '\\n'; exit 0;; *\"cat-file --batch-check\"*) printf '%s\\n' '{head}:Source.cs' | '{realGit}' -C '{root}' cat-file --batch-check; exit $?;; esac\nprintf '%s\\n' '{head}:Source.cs' | '{realGit}' -C '{root}' cat-file --batch\n");
                File.SetUnixFileMode(wrapper,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            string longPath = string.Join('/', Enumerable.Range(0, 6)
                .Select(i => new string((char)('a' + i), 90))) + "/Long.cs";

            Assert.Equal(expected, GitInfo.ShowFile(root, head, "Quoted\"Path.cs", wrapper));
            Assert.Equal(expected, GitInfo.ShowFile(root, head, longPath, wrapper));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDiffRejectsASamePathBinaryRewriteDuringSnapshotCapture()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-snapshot-race").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            string binary = Path.Combine(root, "Payload.bin");
            File.WriteAllBytes(binary, [0, 1, 2, 3, 4, 5, 6, 7]);
            Git(root, gitExe, "add Payload.bin");
            Git(root, gitExe, "commit -q -m binary-baseline");
            head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            File.WriteAllBytes(binary, [10, 11, 12, 13, 14, 15, 16, 17]);

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe,
                afterInitialSnapshot: () =>
                    File.WriteAllBytes(binary, [20, 21, 22, 23, 24, 25, 26, 27]));

            Assert.Equal("snapshot_changed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.Null(review.UntrackedFiles);
            Assert.Empty(review.ChangedSubmoduleLinks);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDiffRejectsAnABATransientDuringTheFirstCapture()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-snapshot-aba").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            string binary = Path.Combine(root, "Payload.bin");
            File.WriteAllBytes(binary, [0, 1, 2, 3, 4, 5, 6, 7]);
            Git(root, gitExe, "add Payload.bin");
            Git(root, gitExe, "commit -q -m binary-baseline");
            head = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            byte[] stateA = [10, 11, 12, 13, 14, 15, 16, 17];
            byte[] transientB = [20, 21, 22, 23, 24, 25, 26, 27];
            File.WriteAllBytes(binary, stateA);

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe,
                beforeFirstDiff: () => File.WriteAllBytes(binary, transientB),
                afterFirstDiff: () => File.WriteAllBytes(binary, stateA));

            Assert.Equal("snapshot_changed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.Null(review.UntrackedFiles);
            Assert.Empty(review.ChangedSubmoduleLinks);
            Assert.Equal(stateA, File.ReadAllBytes(binary));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ReviewDiffRejectsASamePathRewriteAfterTheSecondRawCapture()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-snapshot-final-gap").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            string source = Path.Combine(root, "Source.cs");
            File.WriteAllText(source, "class StateA { }\n");

            GitInfo.ReviewDiffResult review = GitInfo.ReviewDiff(root, head, gitExe,
                afterSecondDiff: () => File.WriteAllText(source, "class StateB { }\n"));

            Assert.Equal("snapshot_changed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
            Assert.Null(review.UntrackedFiles);
            Assert.Empty(review.ChangedSubmoduleLinks);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProcessRunnerRejectsMissingAndNonDirectoryWorkingDirectories()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-invalid-cwd").FullName;
        try
        {
            string missing = Path.Combine(root, "missing");
            string file = Path.Combine(root, "not-a-directory.txt");
            File.WriteAllText(file, "sentinel");

            Assert.Equal("spawn_failed",
                GitInfo.RunProcessEx(gitExe, missing, "--version").Status);
            Assert.Equal("spawn_failed",
                GitInfo.RunProcessEx(gitExe, file, "--version").Status);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ResolveRefValidatesHexRefsAbbreviationsAndFullObjectIdsThroughStdin()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-bhd-hex-refs").FullName;
        try
        {
            string first = CreateRepo(root, gitExe);
            const string hexBranch = "aaaaaaaa";
            const string hexTag = "bbbbbbbb";
            Git(root, gitExe, $"branch {hexBranch} {first}");
            Git(root, gitExe, $"tag -a {hexTag} {first} -m hex-tag");
            EditSource(root);
            Git(root, gitExe, "add Source.cs");
            Git(root, gitExe, "commit -q -m second");
            string second = GitOutput(root, gitExe, "rev-parse HEAD").Trim();
            string abbreviation = second[..12];
            string ambiguousHex = first[..12];
            Git(root, gitExe, $"branch {ambiguousHex} {second}");
            Git(root, gitExe, $"branch {first} {second}");
            string blob = GitOutput(root, gitExe, "rev-parse HEAD:Source.cs").Trim();

            Assert.Equal(first, GitInfo.ResolveRef(root, hexBranch, gitExe));
            Assert.Equal(first, GitInfo.ResolveRef(root, hexTag, gitExe));
            Assert.Equal(second, GitInfo.ResolveRef(root, abbreviation, gitExe));
            Assert.Null(GitInfo.ResolveRef(root, ambiguousHex, gitExe));
            Assert.Equal(first, GitInfo.ResolveRef(root, first, gitExe));
            Assert.Equal(second, GitInfo.ResolveRef(root, second, gitExe));
            Assert.Null(GitInfo.ResolveRef(root, new string('0', second.Length), gitExe));
            Assert.Null(GitInfo.ResolveRef(root, blob, gitExe));
            Assert.Null(GitInfo.ResolveRef(root, "cccccccccccc", gitExe));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RepositoryFormatParserIgnoresUnrelatedValuelessLocalKeys()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-valueless-config").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            File.AppendAllText(Path.Combine(root, ".git", "config"),
                "\n[review]\n\tvalueless\n");
            EditSource(root);

            GitInfo.ReviewDiffResult review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "ok", r => r.Diff.Status ?? "<null>", "ReviewDiff status ok");

            Assert.Equal("ok", review.Diff.Status);
            Assert.Contains(Assert.IsType<List<GitInfo.DiffFile>>(review.Diff.Files),
                file => file.Path == "Source.cs");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void RepositoryFormatParserRejectsAnInvalidRepositoryFormatVersion()
    {
        string? gitExe = FindRealGitExe();
        if (gitExe is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-invalid-format").FullName;
        try
        {
            string head = CreateRepo(root, gitExe);
            Git(root, gitExe, "config --local core.repositoryformatversion invalid");
            EditSource(root);

            GitInfo.ReviewDiffResult review = SemanticRetry.Until( // n7ly sweep: retries transient git; a deterministic wrong status stays red
                () => GitInfo.ReviewDiff(root, head, gitExe),
                r => r.Diff.Status == "config_failed", r => r.Diff.Status ?? "<null>", "ReviewDiff status config_failed");

            Assert.Equal("config_failed", review.Diff.Status);
            Assert.Null(review.Diff.Files);
            Assert.Null(review.Dirty);
        }
        finally { Cleanup(root); }
    }
}
