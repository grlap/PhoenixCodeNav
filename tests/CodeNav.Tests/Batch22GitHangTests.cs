using System.Diagnostics;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Field bug: repo_overview froze inside StandardOutput.ReadToEnd running `git rev-parse HEAD` on a
/// work monolith — git exits instantly (console fine) but a spawned background daemon (fsmonitor)
/// inherits the redirected stdout handle, the pipe never reaches EOF, and the old synchronous
/// ReadToEnd-before-WaitForExit blocked forever with its own timeout unreachable. This reproduces
/// the exact shape with cmd.exe: the parent exits immediately while a grandchild keeps the inherited
/// pipe open — the runner must degrade to null within its drain grace, never hang.
/// </summary>
public class Batch22GitHangTests
{
    [Fact]
    public void RunnerDegradesWhenAGrandchildHoldsThePipe()
    {
        if (!OperatingSystem.IsWindows()) return; // cmd.exe reproduction is Windows-shaped

        var sw = Stopwatch.StartNew();
        // start /b: the ping grandchild inherits the redirected stdout and holds it ~20s;
        // the cmd parent exits immediately — exactly the fsmonitor--daemon shape.
        string? result = GitInfo.RunProcess(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Path.GetTempPath(),
            "/c \"start /b ping -n 20 127.0.0.1 & exit /b 0\"",
            waitMs: 5000, drainMs: 1500);
        sw.Stop();

        Assert.Null(result); // degraded, not hung
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"runner took {sw.Elapsed.TotalSeconds:n1}s — the pipe-holding grandchild hang is back");
    }

    [Fact]
    public void RunnerStillReturnsOutputForNormalCommands()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? result = GitInfo.RunProcess(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Path.GetTempPath(), "/c echo hello");
        Assert.Equal("hello", result?.Trim());
    }

    // Field feedback: bounded TIME is not enough — a runaway subprocess can produce megabytes.
    // Past the cap the runner keeps DRAINING (the child never blocks on a full pipe) but discards,
    // marking Truncated; the string? wrapper treats truncation as failure (callers must not act on
    // a partial changed-file list — they full-sweep instead).
    [Fact]
    public void RunnerCapsOutputAndMarksTruncated()
    {
        if (!OperatingSystem.IsWindows()) return;
        string cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var r = GitInfo.RunProcessEx(cmd, Path.GetTempPath(),
            "/c \"for /l %i in (1,1,500) do @echo 0123456789012345678901234567890123456789\"",
            maxOutputChars: 1000);
        Assert.Equal("ok", r.Status);
        Assert.True(r.Truncated, "output beyond the cap must mark Truncated");
        Assert.Equal(1000, r.Output!.Length);
    }

    // Field feedback: the guard firing must be DIAGNOSABLE, not a silent null — "why is headCommit
    // empty?" needs an answer. A never-exiting process reports status "timed_out".
    [Fact]
    public void RunnerReportsTimedOutStatus()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = GitInfo.RunProcessEx("ping", Path.GetTempPath(), "-n 30 127.0.0.1", waitMs: 800, drainMs: 500);
        sw.Stop();
        Assert.Equal("timed_out", r.Status);
        Assert.Null(r.Output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"timed_out path took {sw.Elapsed.TotalSeconds:n1}s");
    }
}
