using CodeNav.Core.Indexing;
using static CodeNav.Tests.Batch43Support;

namespace CodeNav.Tests;

/// <summary>Portable Git process-boundary contracts. This class deliberately stays outside the
/// platform-filtered Batch43 slices so the preflight and launcher assertions execute on macOS.</summary>
public class GitInvocationObserverTests
{
    [Fact]
    public void ReviewDiffReusesOneFilterSafetyPreflightAcrossSnapshotSandwich()
    {
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-y8e-one-preflight").FullName;
        try
        {
            string head = CreateRepo(root, realGit);
            Git(root, realGit, "config filter.media/lfs.clean command-that-must-not-run");
            EditSource(root);
            var invocations = new List<string>();

            GitInfo.ReviewDiffResult review;
            using (GitInfo.ObserveProcessInvocationsForTest(invocations.Add))
                review = GitInfo.ReviewDiff(root, head, realGit);

            Assert.True(review.Diff.Status == "ok",
                $"ReviewDiff status was {review.Diff.Status}; invocations: " +
                string.Join(" | ", invocations));
            Assert.NotNull(review.Dirty);
            Assert.Single(invocations, line => line.Contains(
                "config --includes --null --get-regexp filter[.]",
                StringComparison.Ordinal));
            Assert.Single(invocations, line => line.Contains(
                "ls-files -z --cached --stage", StringComparison.Ordinal));
            Assert.Single(invocations, line => line.Contains(
                "check-attr -z --stdin filter", StringComparison.Ordinal));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "diff --raw -z --patch", StringComparison.Ordinal)));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "diff --raw -z --numstat", StringComparison.Ordinal)));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "diff --cached --name-only -z", StringComparison.Ordinal)));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "ls-files -z --others --exclude-standard", StringComparison.Ordinal)));
            Assert.Equal(3, invocations.Count(line => line.Contains(
                "ls-files -z --unmerged", StringComparison.Ordinal)));
            Assert.DoesNotContain(invocations, line => line.Contains(
                "status --porcelain", StringComparison.Ordinal));
            Assert.DoesNotContain(invocations, line => line.Contains(
                "--path-format=absolute", StringComparison.Ordinal));
            Assert.Contains(invocations, line => line.Contains(
                "rev-parse --git-common-dir", StringComparison.Ordinal));
            Assert.All(invocations, line =>
                Assert.Contains("-c submodule.recurse=false", line, StringComparison.Ordinal));
            Assert.All(invocations, line =>
                Assert.Contains("-c protocol.allow=never", line, StringComparison.Ordinal));
            Assert.All(invocations, line =>
                Assert.Contains("-c diff.autoRefreshIndex=false", line, StringComparison.Ordinal));
            Assert.Contains(invocations, line => line.Contains(
                "diff --raw -z --patch", StringComparison.Ordinal) &&
                line.Contains("--ignore-submodules=dirty", StringComparison.Ordinal));
            Assert.Contains(invocations, line => line.Contains(
                "diff --cached --name-only -z", StringComparison.Ordinal) &&
                line.Contains("--ignore-submodules=dirty", StringComparison.Ordinal));
            Assert.Contains(invocations, line => line.Contains(
                "diff --raw -z --numstat", StringComparison.Ordinal) &&
                line.Contains("--ignore-submodules=dirty", StringComparison.Ordinal));
            Assert.DoesNotContain(invocations, line => line.Contains(
                "--ignore-submodules=none", StringComparison.Ordinal));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProcessObserverReportsOnlySuccessfulLaunchesAndCannotEscapeFailureContract()
    {
        string? realGit = FindRealGitExe();
        if (realGit is null) return;
        string root = Directory.CreateTempSubdirectory("codenav-git-process-observer").FullName;
        try
        {
            int missingObservations = 0;
            using (GitInfo.ObserveProcessInvocationsForTest(
                       _ => Interlocked.Increment(ref missingObservations)))
            {
                var missing = GitInfo.RunProcessEx(
                    Path.Combine(root, "missing-git-executable"), root, "--version");
                Assert.Equal("spawn_failed", missing.Status);
            }
            Assert.Equal(0, missingObservations);

            int throwingObservations = 0;
            using (GitInfo.ObserveProcessInvocationsForTest(_ =>
                   {
                       Interlocked.Increment(ref throwingObservations);
                       throw new InvalidOperationException("diagnostic observer failure");
                   }))
            {
                var observed = GitInfo.RunProcessEx(realGit, root, "--version");
                Assert.Equal("spawn_failed", observed.Status);
            }
            Assert.Equal(1, throwingObservations);

            var healthy = GitInfo.RunProcessEx(realGit, root, "--version");
            Assert.Equal("ok", healthy.Status);
            Assert.Contains("git version", healthy.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(root); }
    }
}
