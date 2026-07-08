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
}
