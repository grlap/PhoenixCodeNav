using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Owns: the shared, loud test-side Git runner. The suite's per-class `void Git(...)`
/// helpers discarded GitInfo.RunProcess's result, so a git spawn starved by full-suite load
/// (or killed by the wait timeout) became a SILENT no-op — the test's own setup broke and the
/// failure surfaced minutes later as an unrelated-looking red. Test-side repository setup uses
/// the repository's established process-exit ceiling and fails loudly with the exact command.
/// It is deliberately not retried: killing Git can leave config.lock/index.lock behind, so a
/// retry cannot prove that the setup command completed correctly.
/// Deliberately does not own: wrappers that pass a custom gitExe (Batch43/44 assert their own
/// results and often count invocations, where a retry would break the count).
/// </summary>
internal static class TestGit
{
    // Shared with ProcessHeavyTestIsolation: this is the approved outer process-exit ceiling,
    // not a product Git timeout and not a second test-specific wall-clock guess.
    internal const int ProcessExitTimeoutMilliseconds = 130_000;

    internal static void Run(string dir, string args)
    {
        string? output = GitInfo.RunProcess("git", dir,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args,
            waitMs: ProcessExitTimeoutMilliseconds);
        Assert.True(output is not null,
            $"test-side git failed: git {args} (in {dir})");
    }
}
