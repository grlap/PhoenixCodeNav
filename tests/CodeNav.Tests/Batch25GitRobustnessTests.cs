using System.Diagnostics;
using CodeNav.Core.Indexing;
using CodeNav.WorkspaceGen;

namespace CodeNav.Tests;

/// <summary>
/// Batch 25 — git robustness pair:
///  - h99: git shipped as a .cmd/.bat wrapper (scoop shims, corporate launchers) was invisible to
///    resolution AND unspawnable (CreateProcess rejects non-PE files) — git-aware refresh silently
///    off. Resolution now accepts git.cmd/git.bat; Invocation() wraps them in `cmd /d /s /c`.
///  - wll: a freshly initialized repo has no .git/logs yet, so the reflog watch was never attached
///    and never re-attempted — every plain commit after the first went unseen. The top-level watch
///    now re-attaches on the logs/ directory's creation.
/// </summary>
public class Batch25GitRobustnessTests
{
    // ------------------------------------------------------------------ h99: resolution

    [Fact]
    public void ResolutionAcceptsCmdAndBatWrappers()
    {
        if (!OperatingSystem.IsWindows()) return; // wrapper story is Windows-shaped
        string root = Directory.CreateTempSubdirectory("codenav-h99").FullName;
        try
        {
            string exeDir = Path.Combine(root, "exe");
            string cmdDir = Path.Combine(root, "cmd");
            string batDir = Path.Combine(root, "bat");
            Directory.CreateDirectory(exeDir);
            Directory.CreateDirectory(cmdDir);
            Directory.CreateDirectory(batDir);
            File.WriteAllText(Path.Combine(exeDir, "git.exe"), "");
            File.WriteAllText(Path.Combine(cmdDir, "git.cmd"), "@echo off");
            File.WriteAllText(Path.Combine(batDir, "git.bat"), "@echo off");

            // A dir shipping only a .cmd (the scoop-shim shape) now resolves.
            Assert.Equal(Path.Combine(cmdDir, "git.cmd"), GitInfo.ResolveGitExeFrom(null, cmdDir));
            Assert.Equal(Path.Combine(batDir, "git.bat"), GitInfo.ResolveGitExeFrom(null, batDir));
            // .exe still wins within a dir (PATHEXT precedence)...
            File.WriteAllText(Path.Combine(cmdDir, "git.exe"), "");
            Assert.Equal(Path.Combine(cmdDir, "git.exe"), GitInfo.ResolveGitExeFrom(null, cmdDir));
            // ...and dir order beats extension preference (first PATH entry with any git wins).
            Assert.Equal(Path.Combine(batDir, "git.bat"),
                GitInfo.ResolveGitExeFrom(null, batDir + Path.PathSeparator + exeDir));
            // Override beats PATH; missing override falls through.
            Assert.Equal(Path.Combine(exeDir, "git.exe"),
                GitInfo.ResolveGitExeFrom(Path.Combine(exeDir, "git.exe"), cmdDir));
            Assert.Equal(Path.Combine(cmdDir, "git.exe"),
                GitInfo.ResolveGitExeFrom(Path.Combine(root, "nope.exe"), cmdDir));
            string nonCanonical = Path.Combine(cmdDir, "..", "cmd", "git.exe");
            Assert.Equal(Path.GetFullPath(Path.Combine(cmdDir, "git.exe")),
                GitInfo.ResolveGitExeFrom(nonCanonical, null));
            Assert.Null(GitInfo.ResolveGitExeFrom(null, root)); // no git anywhere
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void ResolutionRejectsRelativeOverrideAndPathEntries()
    {
        string root = Directory.CreateTempSubdirectory("codenav-git-resolution").FullName;
        try
        {
            string bin = Directory.CreateDirectory(Path.Combine(root, "hostile-bin")).FullName;
            string name = OperatingSystem.IsWindows() ? "git.exe" : "git";
            string absolute = Path.Combine(bin, name);
            File.WriteAllText(absolute, "");
            string relativeFile = Path.GetRelativePath(Environment.CurrentDirectory, absolute);
            string relativeDirectory = Path.GetRelativePath(Environment.CurrentDirectory, bin);
            Assert.False(Path.IsPathFullyQualified(relativeFile));
            Assert.False(Path.IsPathFullyQualified(relativeDirectory));

            Assert.Null(GitInfo.ResolveGitExeFrom(relativeFile, null));
            Assert.Null(GitInfo.ResolveGitExeFrom(null, relativeDirectory));
            Assert.Equal(Path.GetFullPath(absolute),
                GitInfo.ResolveGitExeFrom(null, Path.PathSeparator + bin));
            if (OperatingSystem.IsWindows())
            {
                string expanding = Directory.CreateDirectory(
                    Path.Combine(root, "%USERNAME%", "bin")).FullName;
                string wrapper = Path.Combine(expanding, "git.cmd");
                File.WriteAllText(wrapper, "@echo unsafe\r\n");
                Assert.Null(GitInfo.ResolveGitExeFrom(wrapper, null));
                Assert.Null(GitInfo.ResolveGitExeFrom(null, expanding));
                Assert.Throws<ArgumentException>(() => GitInfo.Invocation(wrapper, "status"));
            }
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ------------------------------------------------------------------ h99: spawning

    [Fact]
    public void CmdWrapperActuallySpawnsThroughInvocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("codenav-h99run").FullName;
        try
        {
            // Fake wrapper that proves both the spawn AND the argument pass-through. A dir WITH A
            // SPACE exercises the /s outer-quote handling — the real-world wrapper-path shape.
            string spaced = Path.Combine(root, "git tools");
            Directory.CreateDirectory(spaced);
            string wrapper = Path.Combine(spaced, "git.cmd");
            File.WriteAllText(wrapper, "@echo fake-git %*\r\n");

            string trustedCmd = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            string hostileCmd = Path.Combine(root, "cmd.exe");
            File.WriteAllText(hostileCmd, "workspace-planted command interpreter");
            string? priorComSpec = Environment.GetEnvironmentVariable("ComSpec");
            (string exe, string args) invocation;
            try
            {
                Environment.SetEnvironmentVariable("ComSpec", hostileCmd);
                invocation = GitInfo.Invocation(wrapper, "rev-parse HEAD");
            }
            finally
            {
                Environment.SetEnvironmentVariable("ComSpec", priorComSpec);
            }
            var (exe, args) = invocation;
            Assert.Equal(trustedCmd, exe, ignoreCase: true);
            Assert.True(Path.IsPathFullyQualified(exe));
            Assert.NotEqual(hostileCmd, exe);

            var r = GitInfo.RunProcessEx(exe, root, args);
            Assert.Equal("ok", r.Status); // direct spawn of a .cmd would be spawn_failed
            Assert.Contains("fake-git", r.Output);
            Assert.Contains("rev-parse HEAD", r.Output);

            // A real executable passes through untouched — no cmd.exe layer for git.exe.
            var (exe2, args2) = GitInfo.Invocation(@"C:\somewhere\git.exe", "status");
            Assert.Equal(@"C:\somewhere\git.exe", exe2);
            Assert.Equal("status", args2);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ------------------------------------------------------------------ wll: watcher unit

    [Fact]
    public async Task LogsWatchAttachesWhenTheReflogDirIsBorn()
    {
        string gitDir = Directory.CreateTempSubdirectory("codenav-wll").FullName;
        int fires = 0;
        using (var w = new GitWatcher(gitDir, () => Interlocked.Increment(ref fires)))
        {
            // Simulate the first commit: git creates logs/ and appends logs/HEAD.
            string logs = Path.Combine(gitDir, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "HEAD"), "0000 1111 first\n");
            Assert.True(await WaitAsync(() => Volatile.Read(ref fires) >= 1, 5000),
                "logs/ creation did not signal (re-attach hook missing — wll)");

            // The DECISIVE part: a subsequent plain commit only appends logs/HEAD. Without the
            // late attach nobody watches that file and this append is silent forever.
            int before = Volatile.Read(ref fires);
            await Task.Delay(600); // let the first debounce window fully drain
            File.AppendAllText(Path.Combine(logs, "HEAD"), "1111 2222 second\n");
            Assert.True(await WaitAsync(() => Volatile.Read(ref fires) > before, 5000),
                "append to logs/HEAD went unseen — the late-attached reflog watch is not live");
        }
    }

    // ------------------------------------------------------------------ wll: end-to-end

    [Fact]
    public void FirstCommitOnACommitlessRepoAdvancesTheIndex()
    {
        if (!GitInfo.GitAvailable) return;
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-wll-e2e").FullName);
        string db = IndexBuilder.DefaultDbPath(root);
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 3, seed: 7);
            File.WriteAllText(Path.Combine(root, "FirstMarker.cs"), "namespace W { class FirstAlpha { } }");
            // git init only — NO commit: .git/logs does not exist when the manager starts.
            Git(root, "init -q");
            Git(root, "config user.email test@example.com");
            Git(root, "config user.name CodeNavTest");
            Git(root, "config commit.gpgsign false");

            using var m = new IndexManager(root, db);
            m.Start();
            Assert.True(WaitUntil(() => m.Health().State == "ready", 30000),
                $"index did not become ready: {m.Health().Error}");
            Assert.Null(m.Health().IndexedCommit); // commit-less: nothing to record yet

            // The FIRST commit creates .git/logs — the wll re-attach turns it into a signal.
            Git(root, "add -A");
            Git(root, "commit -q -m first");
            string? firstCommit = GitInfo.HeadCommit(root);
            Assert.NotNull(firstCommit);

            Assert.True(
                WaitUntil(() => string.Equals(
                    m.Health().IndexedCommit, firstCommit, StringComparison.OrdinalIgnoreCase),
                    30000),
                $"indexed_commit did not pick up the repo's FIRST commit (wll): " +
                $"expected={firstCommit}, actual={m.Health().IndexedCommit}, " +
                $"state={m.Health().State}, error={m.Health().Error}");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* git/windows locks */ }
        }
    }

    // ------------------------------------------------------------------ h99: shell-inertness

    // Review hardening: commit ids get interpolated into git args strings, and on .cmd/.bat
    // wrapper machines that tail is cmd.exe-interpreted — a metacharacter would split into a
    // second command. The hex gate makes the safety property enforced, not assumed.
    [Theory]
    [InlineData("f87895f", true)]
    [InlineData("F87895F1AB2C", true)]
    [InlineData("f87895f1ab2cf87895f1ab2cf87895f1ab2cf87895f1ab2cf87895f1ab2cabcd", true)] // 64 (sha256 repos)
    [InlineData("abc", false)]                    // too short to be a commit id
    [InlineData("deadbeef & calc.exe", false)]    // the reviewer's injection shape
    [InlineData("HEAD", false)]                   // symbolic refs are not hex
    [InlineData("f87895f\"", false)]
    public void CommitIdsAreHexGated(string candidate, bool ok) =>
        Assert.Equal(ok, GitInfo.IsHexCommit(candidate));

    [Fact]
    public void ChangedFilesRefusesNonHexCommitIds()
    {
        // No git invocation should ever happen for these — null (full-sweep signal) regardless
        // of whether a repo exists at the path.
        Assert.Null(GitInfo.ChangedFiles(Path.GetTempPath(), "deadbeef & calc.exe", "f87895f1"));
        Assert.Null(GitInfo.ChangedFiles(Path.GetTempPath(), "f87895f1", "HEAD~1"));
    }

    // ------------------------------------------------------------------ helpers

    private static void Git(string dir, string args) =>
        GitInfo.RunProcess("git", dir,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);

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

    private static async Task<bool> WaitAsync(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }
}
