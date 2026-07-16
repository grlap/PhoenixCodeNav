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
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { /* git handles / windows locks */ }
    }
}
