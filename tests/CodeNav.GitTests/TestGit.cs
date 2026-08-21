using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Owns: the shared, LOUD test-side git runner (n7ly). The suite's per-class `void Git(...)`
/// helpers discarded GitInfo.RunProcess's result, so a git spawn starved by full-suite load
/// (or killed by the wait timeout) became a SILENT no-op — the test's own setup broke and the
/// failure surfaced minutes later as an unrelated-looking red (Batch25's first-commit test
/// waited 60s for a commit that never existed). Spawn failures are transient under load, so:
/// bounded retry, then FAIL LOUDLY with the exact command — a named setup failure beats a
/// mystery downstream one. Git setup commands used by tests (init/config/add/commit -q) are
/// retry-safe in the common cases; residues a retry cannot heal (a killed run's stale
/// config.lock/index.lock, or a timed-out-but-completed commit whose retry says "nothing to
/// commit") still end in the LOUD red below — never a false green.
/// Deliberately does not own: wrappers that pass a custom gitExe (Batch43/44 assert their own
/// results and often count invocations, where a retry would break the count).
/// </summary>
internal static class TestGit
{
    internal static void Run(string dir, string args, int attempts = 3)
    {
        string? output = null;
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) Thread.Sleep(500);
            output = GitInfo.RunProcess("git", dir,
                "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);
            if (output is not null) return;
        }
        Assert.Fail($"test-side git failed after {attempts} attempts: git {args} (in {dir})");
    }
}
