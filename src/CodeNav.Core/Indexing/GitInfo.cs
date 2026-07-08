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
    private static string? ResolveGitExe()
    {
        string? overridePath = Environment.GetEnvironmentVariable("CODENAV_GIT_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        string[] names = OperatingSystem.IsWindows() ? new[] { "git.exe" } : new[] { "git" };
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

    /// <summary>Absolute path to the resolved git metadata dir — handles the worktree/submodule
    /// case where <c>.git</c> is a file — or null if the root is not inside a git work tree.</summary>
    public static string? ResolveGitDir(string workspaceRoot)
    {
        string? outp = Run(workspaceRoot, "rev-parse --absolute-git-dir")?.Trim();
        return !string.IsNullOrEmpty(outp) && Directory.Exists(outp) ? outp : null;
    }

    /// <summary>Current HEAD commit SHA, or null on failure/absent.</summary>
    public static string? HeadCommit(string workspaceRoot)
    {
        string? outp = Run(workspaceRoot, "rev-parse HEAD")?.Trim();
        return string.IsNullOrEmpty(outp) ? null : outp;
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
        if (string.IsNullOrWhiteSpace(fromCommit) || string.IsNullOrWhiteSpace(toCommit)) return null;
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
        // -c core.fsmonitor=false: on fsmonitor/Scalar-enabled monoliths git auto-spawns a
        // background daemon; our read-only queries never need it, and the daemon INHERITING our
        // redirected pipes is exactly the hang fixed below. Unknown/unused -c keys are harmless.
        return RunProcess(gitExe, cwd, "-c core.fsmonitor=false " + args);
    }

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
            if (p is null) return null;
            p.StandardInput.Close();
            // Dedicated background reader THREADS, not Task continuations: under a saturated thread
            // pool (heavy parallel load) async drains can miss a small grace window and spuriously
            // report null — a pool-independent thread drains the instant the pipe closes.
            string stdout = "";
            var outReader = new Thread(() => { try { stdout = p.StandardOutput.ReadToEnd(); } catch { /* pipe torn down */ } }) { IsBackground = true };
            var errReader = new Thread(() => { try { p.StandardError.ReadToEnd(); } catch { /* pipe torn down */ } }) { IsBackground = true };
            outReader.Start();
            errReader.Start();
            if (!p.WaitForExit(waitMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            if (!outReader.Join(drainMs) || !errReader.Join(drainMs))
            {
                return null; // a grandchild still holds the pipe — degrade, never hang
            }
            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null; // exe missing, spawn failure, etc.
        }
    }
}
