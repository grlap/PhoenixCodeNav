using System.Diagnostics;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Owns: thin, failure-tolerant queries to the git CLI for the working tree at a workspace
/// root — resolved git dir, current HEAD commit/branch, and the changed-file set between two
/// commits. Every call returns null on any failure (git absent, not a repo, timeout, error)
/// so callers fall back to full-sweep / FSW-only behavior. Uses the git CLI, not libgit2, to
/// avoid a native dependency. Does not own: watching (GitWatcher) or scheduling (IndexManager).
/// </summary>
public static class GitInfo
{
    // Resolve git's absolute path once, searching PATH ONLY (never the process current
    // directory), then spawn that path — so a git.exe planted in a workspace we navigate can
    // never be run in place of the real git via the Windows executable search order (which
    // searches the cwd before PATH for a bare name). CODENAV_GIT_EXE overrides the lookup.
    private static readonly Lazy<string?> GitExe = new(ResolveGitExe);

    private static readonly Lazy<bool> Available = new(() => Run(".", "--version") is not null);

    /// <summary>True if a git executable was resolved (found on PATH, or via CODENAV_GIT_EXE).</summary>
    public static bool GitAvailable => Available.Value;

    /// <summary>Absolute git path resolved from CODENAV_GIT_EXE or PATH, or null when git is not
    /// installed — never the bare name "git", which Windows would resolve through the
    /// cwd-inclusive executable search order (the exact hole the absolute path closes).</summary>
    private static string? ResolveGitExe() => ResolveGitExeFrom(
        Environment.GetEnvironmentVariable("CODENAV_GIT_EXE"),
        Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Pure resolution core, split out for tests (the Lazy above resolves once per
    /// process, so env-mutation tests can't exercise it). h99: Windows also accepts git.cmd /
    /// git.bat — scoop shims and corporate wrappers ship git as a batch launcher, and skipping
    /// them silently disabled git-aware refresh on those machines. Search order mirrors
    /// PATHEXT precedence: dir-by-dir, .exe before .cmd before .bat within a dir.</summary>
    internal static string? ResolveGitExeFrom(string? overridePath, string? pathVar)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        string[] names = OperatingSystem.IsWindows()
            ? new[] { "git.exe", "git.cmd", "git.bat" }
            : new[] { "git" };
        foreach (var entry in (pathVar ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string dir = entry.Trim('"');
            foreach (var name in names)
            {
                try
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry — skip */ }
            }
        }
        return null; // git absent: GitAvailable resolves false, git-aware refresh stays off
    }

    /// <summary>Maps a resolved git launcher to a spawnable (exe, args) pair. A .cmd/.bat wrapper
    /// cannot be started directly with UseShellExecute=false (CreateProcess rejects non-PE files),
    /// so it is run through cmd.exe: /d skips AutoRun (no injected user shell config), /s preserves
    /// the outer quotes so a wrapper path WITH SPACES survives, /c runs-and-exits. The hang-proof
    /// runner's Kill(entireProcessTree) covers the extra cmd.exe layer. The args tail is
    /// shell-interpreted by cmd on this path, so it must stay shell-inert: it is built from our
    /// own literals plus commit ids that IsHexCommit VALIDATED (HeadCommitEx gates what gets
    /// stored; ChangedFiles gates what gets interpolated) — never free-form caller input. Route
    /// any future ref/branch/path argument through validation before it reaches an args string.</summary>
    internal static (string Exe, string Args) Invocation(string gitExe, string args) =>
        gitExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || gitExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /s /c \"\"{gitExe}\" {args}\"")
            : (gitExe, args);

    /// <summary>Absolute path to the resolved git metadata dir — handles the worktree/submodule
    /// case where <c>.git</c> is a file — or null if the root is not inside a git work tree.</summary>
    public static string? ResolveGitDir(string workspaceRoot)
    {
        string? outp = Run(workspaceRoot, "rev-parse --absolute-git-dir")?.Trim();
        return !string.IsNullOrEmpty(outp) && Directory.Exists(outp) ? outp : null;
    }

    /// <summary>Current HEAD commit SHA, or null on failure/absent.</summary>
    public static string? HeadCommit(string workspaceRoot) => HeadCommitEx(workspaceRoot).Value;

    /// <summary>HEAD commit WITH an honest status — field feedback: a silent null made
    /// "why is headCommit empty?" undiagnosable (git absent? not a repo? or the hang guard fired?).
    /// Status: "ok" | "unavailable" (git absent / not a repo / error) | "timed_out" (guard fired).</summary>
    public static (string? Value, string Status) HeadCommitEx(string workspaceRoot)
    {
        if (GitExe.Value is not { } gitExe) return (null, "unavailable");
        var (exe, args) = Invocation(gitExe, GitArgs("rev-parse HEAD"));
        var r = RunProcessEx(exe, workspaceRoot, args);
        if (r.Status is "timed_out" or "drain_timed_out") return (null, "timed_out");
        string? sha = r.Status == "ok" && !r.Truncated ? r.Output?.Trim() : null;
        // ENFORCED, not assumed (review): this value is stored and later interpolated into git
        // command lines (ChangedFiles) which, on .cmd/.bat-wrapper machines, pass through cmd.exe —
        // where a metacharacter would split into a second command. Real rev-parse output is a hex
        // SHA; anything else (garbled wrapper, hook noise on stdout) is rejected as unavailable.
        if (sha is not null && !IsHexCommit(sha)) sha = null;
        return string.IsNullOrEmpty(sha) ? (null, "unavailable") : (sha, "ok");
    }

    /// <summary>True for a plausible commit id: 4-64 ASCII hex chars. The gate that keeps every
    /// value we later interpolate into a git args string shell-inert on wrapper machines.</summary>
    internal static bool IsHexCommit(string s)
    {
        if (s.Length is < 4 or > 64) return false;
        foreach (char c in s)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary>Current branch name, or null when detached or on failure.</summary>
    public static string? HeadBranch(string workspaceRoot)
    {
        string? outp = Run(workspaceRoot, "rev-parse --abbrev-ref HEAD")?.Trim();
        return string.IsNullOrEmpty(outp) || outp == "HEAD" ? null : outp;
    }

    /// <summary>
    /// Workspace-relative paths (forward slashes) that differ between two commits, scoped to
    /// the workspace subtree. Returns null if the diff cannot be computed (unrelated histories,
    /// shallow clone, error) — the signal for the caller to fall back to a full sweep.
    /// Renames are reported as delete + add.
    /// </summary>
    public static List<string>? ChangedFiles(string workspaceRoot, string fromCommit, string toCommit)
    {
        // Both ids are interpolated into the args string below — hex-gate them (review): the
        // stored indexed_commit is normally our own validated rev-parse output, but this makes
        // the shell-inertness property hold even against a tampered meta table or future callers.
        if (string.IsNullOrWhiteSpace(fromCommit) || string.IsNullOrWhiteSpace(toCommit)) return null;
        if (!IsHexCommit(fromCommit) || !IsHexCommit(toCommit)) return null; // caller full-sweeps
        // A TRUNCATED diff must fail this call (null => the caller full-sweeps): acting on a partial
        // changed-file list would silently skip refreshing real changes.
        string? outp = Run(workspaceRoot, $"diff --name-only --no-renames --relative {fromCommit} {toCommit}");
        if (outp is null) return null;
        var list = new List<string>();
        foreach (var line in outp.Split('\n'))
        {
            string p = line.Trim().Replace('\\', '/');
            if (p.Length > 0) list.Add(p);
        }
        return list;
    }

    private static string? Run(string cwd, string args)
    {
        if (GitExe.Value is not { } gitExe) return null; // git not resolved — feature off
        var (exe, wrapped) = Invocation(gitExe, GitArgs(args));
        var r = RunProcessEx(exe, cwd, wrapped);
        return r.Status == "ok" && !r.Truncated ? r.Output : null;
    }

    // -c core.fsmonitor=false: on fsmonitor/Scalar-enabled monoliths git auto-spawns a background
    // daemon; our read-only queries never need it, and the daemon INHERITING our redirected pipes is
    // exactly the hang RunProcessEx guards against. core.useBuiltinFSMonitor covers the Scalar-era
    // microsoft/git 2.33-2.36 builds, which gate the experimental daemon on THAT key. Unknown/unused
    // -c keys are harmless on every git in the wild.
    private static string GitArgs(string args) =>
        "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args;

    /// <summary>Hang-proof process runner (field bug: repo_overview froze inside
    /// StandardOutput.ReadToEnd on a work monolith). The old shape — synchronous ReadToEnd BEFORE
    /// WaitForExit — could block FOREVER, making its own timeout unreachable: (a) sequential sync
    /// reads deadlock when stderr fills the pipe first; (b) git can spawn a background daemon
    /// (fsmonitor--daemon) that inherits the redirected stdout handle, so the pipe never reaches
    /// EOF even after the command itself exits. Now: both streams read ASYNC, WaitForExit(timeout)
    /// runs first (it deliberately does not wait for drain), then a bounded drain-grace — a
    /// lingering pipe-holder degrades to null ("git unavailable") instead of hanging the server.
    /// Stdin is redirected and closed so no child can inherit or consume the MCP stdio channel.</summary>
    internal static string? RunProcess(string exe, string cwd, string args, int waitMs = 10000, int drainMs = 2000)
    {
        var r = RunProcessEx(exe, cwd, args, waitMs, drainMs);
        return r.Status == "ok" && !r.Truncated ? r.Output : null;
    }

    /// <summary>Outcome of a bounded process run. Status: "ok" | "spawn_failed" | "timed_out"
    /// (WaitForExit guard) | "drain_timed_out" (a pipe-holder outlived the exit) | "exit_nonzero".
    /// Truncated: stdout exceeded the cap (output is the capped prefix) — field feedback: bounded
    /// TIME is not enough, a runaway subprocess can also produce megabytes.</summary>
    internal sealed record ProcessResult(string? Output, string Status, bool Truncated);

    internal static ProcessResult RunProcessEx(string exe, string cwd, string args,
        int waitMs = 10000, int drainMs = 2000, int maxOutputChars = 8 * 1024 * 1024)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";   // read-only queries must not take index locks
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";  // never wait on interactive input
            using var p = Process.Start(psi);
            if (p is null) return new ProcessResult(null, "spawn_failed", false);
            p.StandardInput.Close();
            // Dedicated background reader THREADS, not Task continuations: under a saturated thread
            // pool (heavy parallel load) async drains can miss a small grace window and spuriously
            // report failure — a pool-independent thread drains the instant the pipe closes. Reads
            // are BOUNDED: past the cap we keep draining (so the child never blocks on a full pipe)
            // but discard, marking Truncated.
            string stdout = "";
            bool truncated = false;
            var outReader = new Thread(() =>
            {
                try { (stdout, truncated) = ReadBounded(p.StandardOutput, maxOutputChars); }
                catch { /* pipe torn down */ }
            }) { IsBackground = true };
            var errReader = new Thread(() =>
            {
                try { _ = ReadBounded(p.StandardError, 64 * 1024); } // drained, capped, discarded
                catch { /* pipe torn down */ }
            }) { IsBackground = true };
            outReader.Start();
            errReader.Start();
            if (!p.WaitForExit(waitMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                CutReadEnds(p);
                return new ProcessResult(null, "timed_out", false);
            }
            if (!outReader.Join(drainMs) || !errReader.Join(drainMs))
            {
                // A grandchild still holds the pipe. Degrade — but first reap what we can and CUT
                // OUR READ ENDS: disposing the base streams aborts the blocked reads in ~20ms
                // (review-measured; the readers' catches swallow the ObjectDisposedException).
                // Without this, every degraded call leaked 2 blocked threads + 2 pipe handles for
                // the foreign holder's lifetime — a slow leak on a long-running server.
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                CutReadEnds(p);
                return new ProcessResult(null, "drain_timed_out", false);
            }
            return p.ExitCode == 0
                ? new ProcessResult(stdout, "ok", truncated)
                : new ProcessResult(null, "exit_nonzero", truncated);
        }
        catch
        {
            return new ProcessResult(null, "spawn_failed", false); // exe missing, spawn failure, etc.
        }
    }

    private static void CutReadEnds(Process p)
    {
        try { p.StandardOutput.BaseStream.Dispose(); } catch { /* aborting a blocked read */ }
        try { p.StandardError.BaseStream.Dispose(); } catch { /* aborting a blocked read */ }
    }

    /// <summary>Reads to EOF with a character cap: appends up to <paramref name="maxChars"/>, then
    /// keeps DRAINING (discarding) so the child never blocks on a full pipe. Returns the capped text
    /// and whether anything was discarded.</summary>
    private static (string Text, bool Truncated) ReadBounded(StreamReader reader, int maxChars)
    {
        var sb = new System.Text.StringBuilder();
        var buf = new char[8192];
        bool truncated = false;
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            int room = maxChars - sb.Length;
            if (room >= n)
            {
                sb.Append(buf, 0, n);
            }
            else
            {
                if (room > 0) sb.Append(buf, 0, room);
                truncated = true;
            }
        }
        return (sb.ToString(), truncated);
    }
}
