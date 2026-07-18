using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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

    // These variables can redirect Git away from the requested workspace or splice attacker-
    // selected object/index storage into it. Ambient values are always removed; the filter
    // sandbox later reinstates only paths discovered from and contained by the requested repo.
    private static readonly string[] GitRepositorySelectionEnvironmentVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_COMMON_DIR",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_QUARANTINE_PATH",
        "GIT_SHALLOW_FILE",
        "GIT_GRAFT_FILE",
        "GIT_REPLACE_REF_BASE",
        "GIT_NAMESPACE",
        "GIT_PREFIX",
        "GIT_SUPER_PREFIX",
        "GIT_INTERNAL_SUPER_PREFIX",
        "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM",
        "GIT_IMPLICIT_WORK_TREE",
    ];

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
        string? configured = CanonicalExistingFile(overridePath);
        if (configured is not null) return configured;

        string[] names = OperatingSystem.IsWindows()
            ? new[] { "git.exe", "git.cmd", "git.bat" }
            : new[] { "git" };
        foreach (string entry in (pathVar ?? "").Split(Path.PathSeparator))
        {
            string dir = entry.Trim();
            if (dir.Length >= 2 && dir[0] == '"' && dir[^1] == '"')
                dir = dir[1..^1];
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) continue;
            try { dir = Path.GetFullPath(dir); }
            catch { continue; }
            foreach (var name in names)
            {
                try
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        candidate = Path.GetFullPath(candidate);
                        if (IsSafeResolvedLauncher(candidate)) return candidate;
                    }
                }
                catch { /* malformed PATH entry — skip */ }
            }
        }
        return null; // git absent: GitAvailable resolves false, git-aware refresh stays off
    }

    private static string? CanonicalExistingFile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            string candidate = value.Trim();
            if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
                candidate = candidate[1..^1];
            if (!Path.IsPathFullyQualified(candidate)) return null;
            candidate = Path.GetFullPath(candidate);
            return File.Exists(candidate) && IsSafeResolvedLauncher(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeResolvedLauncher(string path) =>
        !OperatingSystem.IsWindows() ||
        (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
         !path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) ||
        !path.Contains('%');

    /// <summary>Maps a resolved git launcher to a spawnable (exe, args) pair. A .cmd/.bat wrapper
    /// cannot be started directly with UseShellExecute=false (CreateProcess rejects non-PE files),
    /// so it is run through cmd.exe: /d skips AutoRun, /s preserves outer quotes for launcher paths
    /// with spaces, /v:off disables delayed expansion, and /c runs-and-exits. The hang-proof runner
    /// covers the extra cmd.exe layer. Args must remain shell-inert: dynamic refs and paths use
    /// git's stdin batch protocol; the few commit ids still present in args are hex-gated.</summary>
    internal static (string Exe, string Args) Invocation(string gitExe, string args)
    {
        if (string.IsNullOrWhiteSpace(gitExe) || !Path.IsPathFullyQualified(gitExe))
            throw new ArgumentException("Git launcher must be an absolute path", nameof(gitExe));
        gitExe = Path.GetFullPath(gitExe);
        if (!IsSafeResolvedLauncher(gitExe))
            throw new ArgumentException("Batch launcher paths containing '%' are unsafe", nameof(gitExe));

        if (!OperatingSystem.IsWindows() ||
            (!gitExe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) &&
             !gitExe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return (gitExe, args);
        }

        string systemDirectory = Environment.SystemDirectory;
        if (!Path.IsPathFullyQualified(systemDirectory))
            throw new InvalidOperationException("Windows system directory is not fully qualified");
        string commandInterpreter = Path.GetFullPath(Path.Combine(systemDirectory, "cmd.exe"));
        return (commandInterpreter, $"/d /v:off /s /c \"\"{gitExe}\" {args}\"");
    }

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
    public static (string? Value, string Status) HeadCommitEx(string workspaceRoot) =>
        HeadCommitEx(workspaceRoot, GitExe.Value);

    internal static (string? Value, string Status) HeadCommitEx(string workspaceRoot,
        string? gitExe, IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        if (gitExe is null) return (null, "unavailable");
        var environment = CopyEnvironmentOverrides(environmentOverrides);
        ClearGitRepositorySelection(environment);
        ProcessResult r = RunWithExecutableResult(gitExe, workspaceRoot, "rev-parse HEAD",
            environmentOverrides: environment);
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
    /// Workspace-relative Git paths ('/' separators; literal Unix backslashes preserved) that
    /// differ between two commits, scoped to the workspace subtree. Returns null if the diff
    /// cannot be computed (unrelated histories,
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
        // -z disables C-style path quoting and makes every legal newline/backslash an ordinary
        // filename byte. Git paths already use '/', so never reinterpret a literal Unix '\\'.
        string? outp = Run(workspaceRoot,
            $"diff --name-only -z --no-renames --relative {fromCommit} {toCommit} --");
        return outp is null ? null : ParseNulPathList(outp);
    }

    internal static List<string>? ParseNulPathList(string output)
    {
        if (output.Length == 0) return [];
        // A non-empty -z stream always terminates its final path. Missing termination is malformed
        // or truncated; callers must full-sweep rather than trusting a partial changed-file list.
        if (output[^1] != '\0') return null;
        string[] paths = output.Split('\0');
        var result = new List<string>(paths.Length - 1);
        for (int i = 0; i < paths.Length - 1; i++)
        {
            if (paths[i].Length == 0) return null;
            result.Add(paths[i]);
        }
        return result;
    }

    /// <summary>One linked/main worktree of the repository containing the root. Branch is null
    /// when detached. Path is absolute with Git '/' separators; literal Unix backslashes and
    /// newlines remain filename characters.</summary>
    public sealed record Worktree(string Path, string? Head, string? Branch);

    /// <summary>All worktrees of the repo containing <paramref name="workspaceRoot"/> (the main
    /// checkout is first, per git), or null on any failure. READ-ONLY — phoenix never creates or
    /// removes worktrees (decided with the user); this is the enumeration the review system's
    /// per-worktree index tools validate targets against, so an arbitrary filesystem path can
    /// never be smuggled into an index-write operation. Porcelain format is stable by contract;
    /// no caller input is interpolated into the args (shell-inert on wrapper machines).</summary>
    public static List<Worktree>? Worktrees(string workspaceRoot)
    {
        string? outp = Run(workspaceRoot, "worktree list --porcelain -z");
        return outp is null ? null : ParseWorktreePorcelainZ(outp);
    }

    internal static List<Worktree>? ParseWorktreePorcelainZ(string output) =>
        ParseWorktreePorcelainZ(output, Path.DirectorySeparatorChar);

    internal static List<Worktree>? ParseWorktreePorcelainZ(string output,
        char directorySeparator)
    {
        if (output.Length == 0) return [];
        var result = new List<Worktree>();
        int offset = 0;
        while (offset < output.Length)
        {
            if (!TryReadNulField(output, ref offset, out string first) ||
                !first.StartsWith("worktree ", StringComparison.Ordinal))
            {
                return null;
            }
            string rawPath = first["worktree ".Length..];
            if (rawPath.Length == 0) return null;
            string path = WorkspacePaths.ToGitPath(rawPath, directorySeparator);
            if (!IsFullyQualifiedWorktreePath(path, directorySeparator)) return null;
            string? head = null, branch = null;
            bool sawHead = false, sawBranch = false, sawDetached = false, sawBare = false;
            bool sawLocked = false, sawPrunable = false, terminated = false;
            while (offset < output.Length)
            {
                if (!TryReadNulField(output, ref offset, out string field)) return null;
                if (field.Length == 0)
                {
                    terminated = true;
                    break;
                }
                if (field.StartsWith("worktree ", StringComparison.Ordinal)) return null;
                if (field.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    if (sawHead) return null;
                    sawHead = true;
                    string sha = field["HEAD ".Length..];
                    if (sha.Length is not (40 or 64) || !IsHexCommit(sha)) return null;
                    head = sha;
                }
                else if (field.StartsWith("branch ", StringComparison.Ordinal))
                {
                    if (sawBranch) return null;
                    sawBranch = true;
                    branch = field["branch ".Length..];
                    const string prefix = "refs/heads/";
                    if (!branch.StartsWith(prefix, StringComparison.Ordinal) ||
                        branch.Length == prefix.Length)
                    {
                        return null;
                    }
                    branch = branch[prefix.Length..];
                }
                else if (field == "detached")
                {
                    if (sawDetached) return null;
                    sawDetached = true;
                }
                else if (field == "bare")
                {
                    if (sawBare) return null;
                    sawBare = true;
                }
                else if (field == "locked" ||
                         (field.StartsWith("locked ", StringComparison.Ordinal) &&
                          field.Length > "locked ".Length))
                {
                    if (sawLocked) return null;
                    sawLocked = true;
                }
                else if (field == "prunable" ||
                         (field.StartsWith("prunable ", StringComparison.Ordinal) &&
                          field.Length > "prunable ".Length))
                {
                    if (sawPrunable) return null;
                    sawPrunable = true;
                }
                // Future porcelain attributes carry no path identity needed here. Unknown labels
                // are ignored for forward compatibility; every currently documented state label
                // is parsed above and cross-validated below.
            }
            if (!terminated) return null;
            // A record is either the bare repository, or a checked-out HEAD with exactly one
            // branch/detached state. Accepting a path-only record made malformed output look like a
            // genuine headless worktree and weakened the target-validation boundary.
            if (sawBare
                ? sawHead || sawBranch || sawDetached
                : !sawHead || sawBranch == sawDetached)
            {
                return null;
            }
            result.Add(new Worktree(path, head, branch));
        }
        return result;
    }

    private static bool IsFullyQualifiedWorktreePath(string path, char directorySeparator)
    {
        if (directorySeparator == '/') return path.StartsWith('/');
        if (directorySeparator != '\\') return false;
        try { return Path.IsPathFullyQualified(path.Replace('/', '\\')); }
        catch { return false; }
    }

    private static bool TryReadNulField(string output, ref int offset, out string field)
    {
        field = "";
        if ((uint)offset >= (uint)output.Length) return false;
        int nul = output.IndexOf('\0', offset);
        if (nul < 0) return false;
        field = output[offset..nul];
        offset = nul + 1;
        return true;
    }

    /// <summary>Workspace-root-relative paths (forward slashes) with UNCOMMITTED differences in the
    /// working tree at <paramref name="workspaceRoot"/> — staged, unstaged, and untracked — or
    /// null on failure (the caller full-sweeps). Independent NUL-separated staged, unstaged,
    /// unmerged, and untracked manifests preserve exact path bytes without relying on HEAD refs
    /// inside the filter sandbox. This is the second half of the worktree reconcile: ChangedFiles
    /// covers commit movement, this covers final-worktree dirt.</summary>
    public static List<string>? DirtyFiles(string workspaceRoot) =>
        DirtyFiles(workspaceRoot, GitExe.Value);

    private sealed record DirtyFilesResult(string Status, List<string>? Files,
        List<string>? ExcludedUntrackedRepositories = null,
        List<string>? UntrackedFiles = null,
        List<string>? ExcludedLinkedPaths = null,
        byte[]? SnapshotDigest = null);

    internal static List<string>? DirtyFiles(string workspaceRoot, string? gitExe,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        using var safety = FilterNeutralEnvironment(
            workspaceRoot, gitExe, environmentOverrides);
        if (safety is null) return null;
        if (safety.UnsafeFilteredPaths.Count > 0) return null;
        // Standalone callers (worktree reconcile) have no response envelope in which to disclose
        // excluded child-worktree coverage. Force their documented full-sweep fallback instead of
        // claiming a complete dirt manifest for a repository containing gitlinks.
        if (safety.ExcludedSubmoduleWorktrees is not null) return null;
        DirtyFilesResult result = DirtyFilesWithSafety(workspaceRoot, gitExe, safety);
        if (result.ExcludedUntrackedRepositories is { Count: > 0 }) return null;
        return result.Status is "ok" or "unmerged" or "layered_changes"
            ? result.Files
            : null;
    }

    private static DirtyFilesResult DirtyFilesWithSafety(string workspaceRoot, string? gitExe,
        GitSafetyEnvironment safety)
    {
        string stagedArgs = safety.IsUnborn
            ? "diff --cached --name-only -z --no-renames --no-ext-diff --no-textconv " +
              "--ignore-submodules=dirty --relative --"
            : "diff --cached --name-only -z --no-renames --no-ext-diff --no-textconv " +
              $"--ignore-submodules=dirty --relative {safety.HeadOid} --";
        List<string>? staged = RunNulPathCommand(
            workspaceRoot, gitExe, stagedArgs, safety.Environment);
        List<string>? unmerged = RunUnmergedPathCommand(
            workspaceRoot, gitExe, safety.Environment);
        List<string>? unstaged = unmerged is { Count: > 0 }
            ? RunNulPathCommand(workspaceRoot, gitExe,
                "diff --name-only -z --no-renames --no-ext-diff --no-textconv " +
                "--ignore-submodules=dirty --relative --", safety.Environment)
            : RunWorktreeDiffPaths(workspaceRoot, gitExe, safety.Environment);
        List<string>? untracked = RunNulPathCommand(workspaceRoot, gitExe,
            "ls-files -z --others --exclude-standard", safety.Environment,
            allowTrailingDirectory: true);
        if (staged is null || unstaged is null || untracked is null || unmerged is null)
            return new DirtyFilesResult("failed", null);

        byte[]? snapshotDigest = BuildDirtySnapshotDigest(staged, unstaged, unmerged, untracked);
        if (snapshotDigest is null) return new DirtyFilesResult("failed", null);

        var stagedKeys = staged.ToHashSet(StringComparer.Ordinal);
        var unstagedKeys = unstaged.ToHashSet(StringComparer.Ordinal);
        var untrackedKeys = untracked.Select(LayerPathKey).ToHashSet(StringComparer.Ordinal);
        bool layered = stagedKeys.Overlaps(unstagedKeys) || stagedKeys.Overlaps(untrackedKeys);

        var excluded = untracked.Where(path => path.EndsWith('/'))
            .Select(LayerPathKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var excludedLinkedPaths = new List<string>();
        var ordinaryUntracked = untracked.Where(path => !path.EndsWith('/'))
            .Where(path =>
            {
                if (!WorkspacePaths.TryResolveGitPathInside(workspaceRoot, path,
                        out string fullPath) ||
                    WorkspacePaths.EscapesViaReparsePoint(workspaceRoot, fullPath))
                {
                    excludedLinkedPaths.Add(path);
                    return false;
                }
                return true;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var files = new HashSet<string>(staged, StringComparer.Ordinal);
        files.UnionWith(unstaged);
        files.UnionWith(unmerged);
        files.UnionWith(ordinaryUntracked);
        var list = files.OrderBy(path => path, StringComparer.Ordinal).ToList();
        return unmerged.Count > 0
            ? new DirtyFilesResult("unmerged", list, excluded, ordinaryUntracked,
                excludedLinkedPaths, snapshotDigest)
            : layered
                ? new DirtyFilesResult("layered_changes", list, excluded, ordinaryUntracked,
                    excludedLinkedPaths, snapshotDigest)
                : new DirtyFilesResult("ok", list, excluded, ordinaryUntracked,
                    excludedLinkedPaths, snapshotDigest);
    }

    private static byte[]? BuildDirtySnapshotDigest(IReadOnlyList<string> staged,
        IReadOnlyList<string> unstaged, IReadOnlyList<string> unmerged,
        IReadOnlyList<string> untracked)
    {
        try
        {
            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] separator = [0];
            void Add(string value)
            {
                digest.AppendData(Encoding.UTF8.GetBytes(value));
                digest.AppendData(separator);
            }
            void AddComponent(string name, IReadOnlyList<string> paths)
            {
                Add(name);
                foreach (string path in paths.OrderBy(path => path, StringComparer.Ordinal))
                    Add(path);
            }

            AddComponent("staged", staged);
            AddComponent("unstaged", unstaged);
            AddComponent("unmerged", unmerged);
            AddComponent("untracked", untracked);
            return digest.GetHashAndReset();
        }
        catch
        {
            return null;
        }
    }

    private static string LayerPathKey(string path) => path.EndsWith('/') ? path[..^1] : path;

    private static List<string>? RunWorktreeDiffPaths(string workspaceRoot, string? gitExe,
        IReadOnlyDictionary<string, string?> environment)
    {
        string? output = RunWithExecutable(gitExe, workspaceRoot,
            "diff --raw -z --numstat --no-renames --no-ext-diff --no-textconv " +
            "--ignore-submodules=dirty --relative --", environmentOverrides: environment);
        return ParseRawNumstatPaths(output);
    }

    private static List<string>? ParseRawNumstatPaths(string? output)
    {
        if (output is null || (output.Length > 0 && output[^1] != '\0')) return null;
        string[] records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>();
        var statOnlyEligible = new HashSet<string>(StringComparer.Ordinal);
        var numstatPaths = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        while (index < records.Length && records[index].StartsWith(':'))
        {
            string header = records[index++];
            if (header.Any(c => c > 0x7f) || index >= records.Length ||
                !TryParseRawHeader(Encoding.ASCII.GetBytes(header), out char status,
                    out _, out _, out bool sameMode) ||
                !IsSafeRelativeGitPath(records[index]))
            {
                return null;
            }
            string path = records[index++];
            paths.Add(path);
            if (status == 'M' && sameMode) statOnlyEligible.Add(path);
        }

        var rawPaths = paths.ToHashSet(StringComparer.Ordinal);
        for (; index < records.Length; index++)
        {
            string record = records[index];
            int firstTab = record.IndexOf('\t');
            int secondTab = firstTab < 0 ? -1 : record.IndexOf('\t', firstTab + 1);
            if (firstTab <= 0 || secondTab <= firstTab + 1 || secondTab + 1 >= record.Length)
                return null;
            string added = record[..firstTab];
            string deleted = record[(firstTab + 1)..secondTab];
            string path = record[(secondTab + 1)..];
            bool validCount(string value) => value == "-" || value.All(char.IsAsciiDigit);
            if (!validCount(added) || !validCount(deleted) ||
                !IsSafeRelativeGitPath(path) || !rawPaths.Contains(path))
            {
                return null;
            }
            numstatPaths.Add(path);
        }
        // A same-mode M record with no matching numstat entry is Git's stat-only dirt shape
        // when diff.autoRefreshIndex=false. Real text and binary changes always contribute a
        // numstat record (numeric counts or "-\t-"); retain every other raw-only shape.
        return paths.Where(path => !(statOnlyEligible.Contains(path) && !numstatPaths.Contains(path)))
            .ToList();
    }

    private static List<string>? RunNulPathCommand(string workspaceRoot, string? gitExe,
        string args, IReadOnlyDictionary<string, string?> environment,
        bool allowTrailingDirectory = false)
    {
        string? output = RunWithExecutable(gitExe, workspaceRoot, args,
            environmentOverrides: environment);
        if (output is null || (output.Length > 0 && output[^1] != '\0')) return null;
        var paths = new List<string>();
        foreach (string path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string validationPath = allowTrailingDirectory && path.EndsWith('/')
                ? path[..^1]
                : path;
            if (!IsSafeRelativeGitPath(validationPath)) return null;
            paths.Add(path);
        }
        return paths;
    }

    private static List<string>? RunUnmergedPathCommand(string workspaceRoot, string? gitExe,
        IReadOnlyDictionary<string, string?> environment)
    {
        string? output = RunWithExecutable(gitExe, workspaceRoot,
            "ls-files -z --unmerged", environmentOverrides: environment);
        if (output is null || (output.Length > 0 && output[^1] != '\0')) return null;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] record = Encoding.UTF8.GetBytes(entry);
            if (!TryParseIndexEntry(record, out string? path, out _, out _, out _,
                    requireFilterPath: true) || path is null)
            {
                return null;
            }
            paths.Add(path);
        }
        return paths.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Resolve a REF NAME (branch/tag/"HEAD"/merge-base output) to a full commit sha,
    /// or null. The input uses a strict ref charset and travels through cat-file's stdin protocol,
    /// never a wrapper command line; the output is hex-gated like every stored commit id. Callers
    /// may also pass an abbreviated or full raw object id; every candidate is resolved and
    /// commit-peeled by Git rather than trusted by its spelling.</summary>
    public static string? ResolveRef(string workspaceRoot, string refName) =>
        ResolveRef(workspaceRoot, refName, GitExe.Value);

    /// <summary>Explicit-executable core used by real .cmd/.bat wrapper tests without mutating the
    /// process-wide lazy Git resolution. The dynamic revision expression is sent on stdin so cmd
    /// never parses the peel operator.</summary>
    internal static string? ResolveRef(string workspaceRoot, string refName, string? gitExe)
    {
        if (gitExe is null || !IsSafeRefName(refName)) return null;
        bool hexadecimal = refName.Length is >= 4 and <= 64 &&
                           refName.All(char.IsAsciiHexDigit);

        string? ResolveCandidate(string candidate)
        {
            string? output = RunWithExecutable(gitExe, workspaceRoot,
                "cat-file --batch-check", $"{candidate}^{{commit}}\n");
            return TryParseBatchHeader(output, out string oid, out string type, out _,
                       out int contentOffset) && type == "commit" &&
                   contentOffset == output!.Length
                ? oid
                : null;
        }

        List<string?>? ResolveCandidateBatch(IReadOnlyList<string> candidates)
        {
            if (candidates.Count == 0) return [];
            string input = string.Concat(candidates.Select(candidate =>
                candidate + "^{commit}\n"));
            string? output = RunWithExecutable(gitExe, workspaceRoot,
                "cat-file --batch-check", input);
            if (output is null || !output.EndsWith('\n')) return null;
            string[] lines = output.Split('\n');
            if (lines.Length != candidates.Count + 1 || lines[^1].Length != 0) return null;
            var result = new List<string?>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                string line = lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i];
                string request = candidates[i] + "^{commit}";
                if (line.Equals(request + " missing", StringComparison.Ordinal))
                {
                    result.Add(null);
                    continue;
                }
                string framed = line + "\n";
                if (!TryParseBatchHeader(framed, out string oid, out string type, out _,
                        out int contentOffset) || type != "commit" ||
                    contentOffset != framed.Length)
                {
                    return null;
                }
                result.Add(oid);
            }
            return result;
        }

        if (!hexadecimal) return ResolveCandidate(refName);

        string? objectFormatOutput = RunWithExecutable(gitExe, workspaceRoot,
            "rev-parse --show-object-format");
        int fullObjectIdLength = objectFormatOutput?.Trim() switch
        {
            "sha1" => 40,
            "sha256" => 64,
            _ => 0,
        };
        if (fullObjectIdLength == 0) return null;

        // Git itself gives a repository-width full object id precedence over a ref with the same
        // spelling. Keep this lookup on cat-file stdin so full SHA values never cross a batch
        // wrapper command line; a full blob/tree remains invalid after the commit peel.
        if (refName.Length == fullObjectIdLength) return ResolveCandidate(refName);

        // `cat-file <hex>` follows ref-name precedence, so it cannot reveal that an exact hex
        // branch shadows a different abbreviated object. `--disambiguate` is safe here because
        // the operand is already a 4-64 character hexadecimal allowlist, and its output is then
        // full-OID gated and commit-peeled through cat-file's stdin protocol.
        string? objectOutput = RunWithExecutable(gitExe, workspaceRoot,
            $"rev-parse --disambiguate={refName}");
        if (objectOutput is null) return null;
        var objectIds = new List<string>();
        foreach (string line in objectOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string oid = line.EndsWith('\r') ? line[..^1] : line;
            if (!IsFullObjectId(oid)) return null;
            objectIds.Add(oid);
        }

        // Short hexadecimal names can simultaneously be a branch, tag, and object abbreviation.
        // Resolve every exact namespace and every disambiguated object, then refuse distinct
        // commit targets instead of silently reviewing a different base than the caller intended.
        var candidates = new List<string>
        {
            "refs/heads/" + refName,
            "refs/tags/" + refName,
        };
        candidates.AddRange(objectIds);
        List<string?>? resolved = ResolveCandidateBatch(candidates);
        if (resolved is null) return null;
        string[] distinct = resolved.Where(oid => oid is not null).Select(oid => oid!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    /// <summary>Strict allowlist for ref names we interpolate: alnum plus / - _ . (covers
    /// branches, tags, remotes/origin/x, HEAD). Rejects empty, leading '-', '..', and length
    /// abuse. Deliberately NARROWER than git's own ref grammar — an exotic-but-legal ref can
    /// be resolved by the caller to a sha instead.</summary>
    internal static bool IsSafeRefName(string s)
    {
        if (s.Length is 0 or > 200 || s[0] == '-' || s.Contains("..", StringComparison.Ordinal)) return false;
        foreach (char c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('/' or '-' or '_' or '.')) return false;
        }
        return true;
    }

    /// <summary>One changed file in a working-tree diff: NEW-side line ranges that changed
    /// (1-based, inclusive; a pure-deletion hunk contributes a 1-line anchor range at its
    /// position so the surrounding symbol still counts as touched). Deleted is true when the
    /// file no longer exists on the new side.</summary>
    public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount);

    public sealed record DiffFile(string Path, bool Deleted, List<(int Start, int End)> Ranges,
        bool SubmoduleLink = false, bool RemovedSubmoduleLink = false)
    {
        public List<DiffHunk> Hunks { get; init; } = [];
        public char Status { get; init; }
        public string? OldMode { get; init; }
        public string? NewMode { get; init; }
        public string? OldObjectId { get; init; }
        public string? NewObjectId { get; init; }
        public string? MovedFromPath { get; init; }
        public string? MovedToPath { get; init; }
    }

    /// <summary>A diff result with an explicit failure reason. Review callers must not turn a
    /// malformed patch or an unresolved merge into a generic empty change set.</summary>
    public sealed record DiffHunksResult(string Status, List<DiffFile>? Files,
        SubmoduleWorktreeCoverage? ExcludedSubmoduleWorktrees = null)
    {
        /// <summary>SHA-256 of the exact bounded raw diff bytes. ReviewDiff compares two captures
        /// so modes, object ids, symlink payloads, gitlinks, paths, and patch bytes share one epoch.</summary>
        internal byte[]? RawCaptureDigest { get; init; }
    }

    /// <summary>The review tool's diff and dirt manifests computed under one safety snapshot, so
    /// large repositories are not scanned twice for active content filters.</summary>
    public sealed record SubmoduleWorktreeCoverage(int Count, List<string> SamplePaths,
        bool SamplesTruncated);

    public sealed record UntrackedRepositoryCoverage(int Count, List<string> SamplePaths,
        bool SamplesTruncated);

    public sealed record UntrackedLinkCoverage(int Count, List<string> SamplePaths,
        bool SamplesTruncated);

    public sealed record ReviewDiffResult(DiffHunksResult Diff, List<string>? Dirty,
        SubmoduleWorktreeCoverage? ExcludedSubmoduleWorktrees,
        List<string> ChangedSubmoduleLinks,
        UntrackedRepositoryCoverage? ExcludedUntrackedRepositories = null,
        UntrackedLinkCoverage? ExcludedUntrackedLinks = null)
    {
        /// <summary>True ordinary untracked files only. Dirty remains the compatibility union of
        /// staged, unstaged, unmerged, and untracked paths for reconcile callers.</summary>
        public List<string>? UntrackedFiles { get; init; }
    }

    /// <summary>Hunk-level diff of the WORKING TREE against <paramref name="fromCommit"/>
    /// (staged + unstaged in one pass; untracked files are invisible to diff — callers union
    /// DirtyFiles for those). Null on any failure (the caller degrades honestly). A helper-free
    /// safety preflight precedes the `-U0` diff; hunk headers then provide exact change ranges and
    /// paths arrive as raw UTF-8 (quotepath off).</summary>
    public static List<DiffFile>? DiffHunks(string workspaceRoot, string fromCommit) =>
        CompleteFilesOrNull(DiffHunksDetailed(workspaceRoot, fromCommit));

    public static DiffHunksResult DiffHunksDetailed(string workspaceRoot, string fromCommit) =>
        DiffHunksDetailed(workspaceRoot, fromCommit, GitExe.Value);

    public static ReviewDiffResult ReviewDiff(string workspaceRoot, string fromCommit) =>
        ReviewDiff(workspaceRoot, fromCommit, GitExe.Value);

    internal static ReviewDiffResult ReviewDiff(string workspaceRoot, string fromCommit,
        string? gitExe, IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        Action? afterInitialSnapshot = null,
        Action? beforeFirstDiff = null,
        Action? afterFirstDiff = null,
        Action? afterSecondDiff = null,
        Action? afterUntrackedMoveRead = null)
    {
        if (!IsHexCommit(fromCommit))
            return new ReviewDiffResult(new DiffHunksResult("invalid_commit", null), null, null, []);
        using var safety = FilterNeutralEnvironment(workspaceRoot, gitExe, environmentOverrides);
        if (safety is null)
            return new ReviewDiffResult(new DiffHunksResult("config_failed", null), null, null, []);
        if (safety.UnsafeFilteredPaths.Count > 0)
            return new ReviewDiffResult(new DiffHunksResult("filter_unsafe", null), null,
                safety.ExcludedSubmoduleWorktrees, []);
        // Three complete captures must agree: typed status provenance is sampled before each of
        // the first two raw patches and once more before the final raw patch. Comparing the exact
        // raw diff digest binds
        // modes, object ids, symlink payloads, gitlinks, and tracked file bytes without performing
        // potentially blocking in-process file reads. The Git subprocesses retain their existing
        // output and wall-clock bounds. A transient A->B->A during the first capture is caught by
        // the later raw captures. The final raw patch closes the second-diff-to-status content gap;
        // an identical final payload is intentionally equivalent to no drift.
        DirtyFilesResult before = DirtyFilesWithSafety(workspaceRoot, gitExe, safety);
        if (before.Status == "failed" || before.SnapshotDigest is null)
            return new ReviewDiffResult(new DiffHunksResult("status_failed", null), null,
                safety.ExcludedSubmoduleWorktrees, []);
        try
        {
            beforeFirstDiff?.Invoke();
        }
        catch
        {
            return new ReviewDiffResult(new DiffHunksResult("snapshot_changed", null), null,
                safety.ExcludedSubmoduleWorktrees, []);
        }
        DiffHunksResult firstDiff = DiffHunksWithSafety(workspaceRoot, fromCommit, gitExe, safety);
        if (firstDiff.Status != "ok")
            return new ReviewDiffResult(firstDiff with
            {
                ExcludedSubmoduleWorktrees = safety.ExcludedSubmoduleWorktrees,
            }, null, safety.ExcludedSubmoduleWorktrees, []);
        try
        {
            afterFirstDiff?.Invoke();
            afterInitialSnapshot?.Invoke();
        }
        catch
        {
            return new ReviewDiffResult(new DiffHunksResult("snapshot_changed", null), null,
                safety.ExcludedSubmoduleWorktrees, []);
        }

        DirtyFilesResult dirty = DirtyFilesWithSafety(workspaceRoot, gitExe, safety);
        if (dirty.Status == "failed" || dirty.SnapshotDigest is null)
            return new ReviewDiffResult(new DiffHunksResult("status_failed", null,
                    safety.ExcludedSubmoduleWorktrees), null,
                safety.ExcludedSubmoduleWorktrees, []);
        if (!string.Equals(before.Status, dirty.Status, StringComparison.Ordinal) ||
            !before.SnapshotDigest.AsSpan().SequenceEqual(dirty.SnapshotDigest))
        {
            return new ReviewDiffResult(new DiffHunksResult("snapshot_changed", null,
                    safety.ExcludedSubmoduleWorktrees), null,
                safety.ExcludedSubmoduleWorktrees, []);
        }

        DiffHunksResult diff = DiffHunksWithSafety(workspaceRoot, fromCommit, gitExe, safety);
        if (diff.Status != "ok")
            return new ReviewDiffResult(diff with
            {
                ExcludedSubmoduleWorktrees = safety.ExcludedSubmoduleWorktrees,
            }, null, safety.ExcludedSubmoduleWorktrees, []);
        try
        {
            afterSecondDiff?.Invoke();
        }
        catch
        {
            return new ReviewDiffResult(new DiffHunksResult("snapshot_changed", null), null,
                safety.ExcludedSubmoduleWorktrees, []);
        }
        DirtyFilesResult after = DirtyFilesWithSafety(workspaceRoot, gitExe, safety);
        if (after.Status == "failed" || after.SnapshotDigest is null)
            return new ReviewDiffResult(new DiffHunksResult("status_failed", null,
                    safety.ExcludedSubmoduleWorktrees), null,
                safety.ExcludedSubmoduleWorktrees, []);
        DiffHunksResult finalDiff = DiffHunksWithSafety(workspaceRoot, fromCommit, gitExe, safety);
        if (finalDiff.Status != "ok")
            return new ReviewDiffResult(finalDiff with
            {
                ExcludedSubmoduleWorktrees = safety.ExcludedSubmoduleWorktrees,
            }, null, safety.ExcludedSubmoduleWorktrees, []);
        if (!string.Equals(dirty.Status, after.Status, StringComparison.Ordinal) ||
            !dirty.SnapshotDigest.AsSpan().SequenceEqual(after.SnapshotDigest) ||
            firstDiff.RawCaptureDigest is null || diff.RawCaptureDigest is null ||
            finalDiff.RawCaptureDigest is null ||
            !firstDiff.RawCaptureDigest.AsSpan().SequenceEqual(diff.RawCaptureDigest) ||
            !diff.RawCaptureDigest.AsSpan().SequenceEqual(finalDiff.RawCaptureDigest))
        {
            return new ReviewDiffResult(new DiffHunksResult("snapshot_changed", null,
                    safety.ExcludedSubmoduleWorktrees), null,
                safety.ExcludedSubmoduleWorktrees, []);
        }
        diff = finalDiff;

        SubmoduleWorktreeCoverage? coverage = IncludeRetainedRemovedSubmodules(
            workspaceRoot, safety.ExcludedSubmoduleWorktrees, diff.Files);
        diff = diff with { ExcludedSubmoduleWorktrees = coverage };
        dirty = after;
        var changedSubmoduleLinks = diff.Files?
            .Where(file => file.SubmoduleLink)
            .Select(file => file.Path)
            .ToList() ?? [];
        UntrackedRepositoryCoverage? untrackedCoverage = BuildUntrackedRepositoryCoverage(
            dirty.ExcludedUntrackedRepositories, diff.Files);
        UntrackedLinkCoverage? untrackedLinkCoverage = BuildUntrackedLinkCoverage(
            dirty.ExcludedLinkedPaths);
        if (dirty.Status is "unmerged" or "layered_changes")
        {
            diff = new DiffHunksResult(dirty.Status, null, coverage);
            return new ReviewDiffResult(diff, null, coverage,
                changedSubmoduleLinks, untrackedCoverage, untrackedLinkCoverage);
        }
        if (dirty.Status == "ok" && diff.Files is not null && dirty.UntrackedFiles is not null)
        {
            CorrelateUntrackedExactMoves(workspaceRoot, diff.Files, dirty.UntrackedFiles,
                afterUntrackedMoveRead);
        }
        return new ReviewDiffResult(diff, dirty.Status == "ok" ? dirty.Files : null,
            coverage, changedSubmoduleLinks, untrackedCoverage, untrackedLinkCoverage)
        {
            UntrackedFiles = dirty.Status == "ok" ? dirty.UntrackedFiles : null,
        };
    }

    private static void CorrelateUntrackedExactMoves(string workspaceRoot, List<DiffFile> files,
        IReadOnlyList<string> untrackedFiles, Action? afterUntrackedMoveRead = null)
    {
        const int maxCandidates = 64;
        const int maxCandidateBytes = 8 * 1024 * 1024;
        const int maxAggregateBytes = 32 * 1024 * 1024;
        var deletedByOid = files
            .Where(file => file.Status == 'D' && file.MovedToPath is null &&
                           IsReviewableCSharpPath(file.Path) &&
                           IsRegularGitMode(file.OldMode) && IsFullObjectId(file.OldObjectId))
            .GroupBy(file => file.OldObjectId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        if (deletedByOid.Count == 0) return;

        var candidates = new List<(string Path, byte[] Content)>();
        int retainedBytes = 0;
        int attempted = 0;
        foreach (string path in untrackedFiles.Where(IsReviewableCSharpPath)
                     .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (attempted++ >= maxCandidates || retainedBytes >= maxAggregateBytes) break;
            if (!WorkspacePaths.TryResolveGitPathInside(workspaceRoot, path, out string fullPath))
                continue;
            int remaining = maxAggregateBytes - retainedBytes;
            byte[]? content = ReadExistingBoundedRegularFile(fullPath,
                Math.Min(maxCandidateBytes, remaining), workspaceRoot);
            if (content is null) continue;
            candidates.Add((path, content));
            retainedBytes += content.Length;
        }
        if (candidates.Count == 0) return;
        // The bytes above were obtained through anchored component-by-component no-follow opens.
        // Never ask a later Git process to reopen repository-controlled paths: a regular file can
        // otherwise be swapped for a symlink/junction between validation and hash-object. Raw-byte
        // equality is deliberately conservative; a CRLF-only relocation may remain uncorrelated.
        try { afterUntrackedMoveRead?.Invoke(); }
        catch { return; }
        var additionsByOid = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        try
        {
            bool needsSha1 = deletedByOid.Keys.Any(oid => oid.Length == 40);
            bool needsSha256 = deletedByOid.Keys.Any(oid => oid.Length == 64);
            foreach (var candidate in candidates)
            {
                if (needsSha1)
                    AddCandidateOid(GitBlobObjectId(candidate.Content, HashAlgorithmName.SHA1));
                if (needsSha256)
                    AddCandidateOid(GitBlobObjectId(candidate.Content, HashAlgorithmName.SHA256));
                void AddCandidateOid(string oid)
                {
                    if (!additionsByOid.TryGetValue(oid, out List<string>? paths))
                        additionsByOid[oid] = paths = [];
                    paths.Add(candidate.Path);
                }
            }
        }
        catch { return; }
        foreach (var (oid, deleted) in deletedByOid)
        {
            if (deleted.Count != 1 || !additionsByOid.TryGetValue(oid,
                    out List<string>? additions) || additions.Count != 1)
            {
                continue;
            }
            int index = files.FindIndex(file =>
                string.Equals(file.Path, deleted[0].Path, StringComparison.Ordinal));
            if (index >= 0) files[index] = files[index] with { MovedToPath = additions[0] };
        }
    }

    private static string GitBlobObjectId(byte[] content, HashAlgorithmName algorithm)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {content.Length}\0"));
        hash.AppendData(content);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static List<DiffFile>? DiffHunks(string workspaceRoot, string fromCommit, string? gitExe,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null) =>
        CompleteFilesOrNull(DiffHunksDetailed(
            workspaceRoot, fromCommit, gitExe, environmentOverrides));

    internal static DiffHunksResult DiffHunksDetailed(string workspaceRoot, string fromCommit,
        string? gitExe, IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        if (!IsHexCommit(fromCommit)) return new DiffHunksResult("invalid_commit", null);
        using var safety = FilterNeutralEnvironment(
            workspaceRoot, gitExe, environmentOverrides);
        if (safety is null) return new DiffHunksResult("config_failed", null);
        if (safety.UnsafeFilteredPaths.Count > 0)
            return new DiffHunksResult("filter_unsafe", null);
        DiffHunksResult diff = DiffHunksWithSafety(workspaceRoot, fromCommit, gitExe, safety);
        SubmoduleWorktreeCoverage? coverage = IncludeRetainedRemovedSubmodules(
            workspaceRoot, safety.ExcludedSubmoduleWorktrees, diff.Files);
        diff = diff with
        {
            ExcludedSubmoduleWorktrees = coverage,
        };
        if (diff.Status != "ok") return diff;
        DirtyFilesResult dirty = DirtyFilesWithSafety(workspaceRoot, gitExe, safety);
        return dirty.Status == "ok"
            ? diff
            : new DiffHunksResult(dirty.Status == "failed" ? "status_failed" : dirty.Status,
                null, coverage);
    }

    private static List<DiffFile>? CompleteFilesOrNull(DiffHunksResult result) =>
        result.ExcludedSubmoduleWorktrees is null ? result.Files : null;

    private static SubmoduleWorktreeCoverage? IncludeRetainedRemovedSubmodules(
        string workspaceRoot, SubmoduleWorktreeCoverage? current,
        IEnumerable<DiffFile>? files)
    {
        if (files is null) return current;
        var retained = files
            .Where(file => file.RemovedSubmoduleLink &&
                           IsRetainedSubmoduleDirectory(workspaceRoot, file.Path))
            .Select(file => file.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (retained.Count == 0) return current;

        var coverage = new SubmoduleCoverageAccumulator();
        if (current is not null) coverage.Include(current);
        foreach (string path in retained) coverage.Add(path);
        return coverage.Build();
    }

    private static bool IsRetainedSubmoduleDirectory(string workspaceRoot, string path)
    {
        try
        {
            string root = Path.GetFullPath(workspaceRoot);
            string candidate = Path.GetFullPath(Path.Combine(root,
                path.Replace('/', Path.DirectorySeparatorChar)));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            string prefix = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, comparison) && Directory.Exists(candidate);
        }
        catch
        {
            return false;
        }
    }

    private static UntrackedRepositoryCoverage? BuildUntrackedRepositoryCoverage(
        IEnumerable<string>? paths, IEnumerable<DiffFile>? files)
    {
        if (paths is null) return null;
        var gitlinks = files?
            .Where(file => file.SubmoduleLink || file.RemovedSubmoduleLink)
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var distinct = paths.Where(path => !gitlinks.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0) return null;

        const int maxPaths = 8;
        const int maxJsonBytes = 512;
        var sample = new List<string>();
        int sampleBytes = 0;
        foreach (string path in distinct)
        {
            int bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(path).Length + 1;
            if (sample.Count >= maxPaths || bytes > maxJsonBytes - sampleBytes) continue;
            sample.Add(path);
            sampleBytes += bytes;
        }
        return new UntrackedRepositoryCoverage(distinct.Count, sample,
            sample.Count < distinct.Count);
    }

    private static UntrackedLinkCoverage? BuildUntrackedLinkCoverage(IEnumerable<string>? paths)
    {
        if (paths is null) return null;
        var distinct = paths.Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) return null;
        const int maxPaths = 8;
        const int maxJsonBytes = 512;
        var sample = new List<string>();
        int sampleBytes = 0;
        foreach (string path in distinct)
        {
            int bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(path).Length + 1;
            if (sample.Count >= maxPaths || bytes > maxJsonBytes - sampleBytes) continue;
            sample.Add(path);
            sampleBytes += bytes;
        }
        return new UntrackedLinkCoverage(distinct.Count, sample,
            sample.Count < distinct.Count);
    }

    private static DiffHunksResult DiffHunksWithSafety(string workspaceRoot, string fromCommit,
        string? gitExe, GitSafetyEnvironment safety)
    {
        var process = RunWithExecutableResult(gitExe, workspaceRoot,
            "diff --raw -z --patch --unified=0 --abbrev=64 --no-ext-diff --no-textconv --no-renames --no-color " +
            "--src-prefix=a/ --dst-prefix=b/ --no-indent-heuristic --diff-algorithm=myers " +
            "--inter-hunk-context=0 --submodule=short --ignore-submodules=dirty -O/dev/null " +
            $"--relative {fromCommit} --", environmentOverrides: safety.Environment,
            captureBytes: true);
        if (process.Status != "ok" || process.Truncated)
            return new DiffHunksResult("process_failed", null);
        DiffHunksResult parsed = ParseDiffOutput(process.OutputBytes.Span);
        return parsed with
        {
            RawCaptureDigest = SHA256.HashData(process.OutputBytes.Span),
        };
    }

    private enum DiffParseState
    {
        OutsideSection,
        SectionMetadata,
        SawOldHeader,
        ReadyForHunks,
        InHunk,
    }

    /// <summary>Parses the byte-safe `--raw -z --patch` dialect. The NUL-delimited raw records are
    /// authoritative for changed paths and statuses; the textual patch only refines line ranges.
    /// Hunk bodies stay bytes so a legacy-encoded source file cannot poison otherwise structural
    /// Git output.</summary>
    internal static DiffHunksResult ParseDiffOutput(byte[] output) =>
        ParseDiffOutput(output.AsSpan());

    private static DiffHunksResult ParseDiffOutput(ReadOnlySpan<byte> output)
    {
        if (output.Length == 0) return new DiffHunksResult("ok", new List<DiffFile>());

        var manifest = new Dictionary<string, DiffFile>(StringComparer.Ordinal);
        var manifestStatuses = new Dictionary<string, char>(StringComparer.Ordinal);
        var statOnlyEligible = new HashSet<string>(StringComparer.Ordinal);
        var textSectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int offset = 0;
        bool sawRawRecord = false;
        bool sawUnmerged = false;
        while (offset < output.Length && output[offset] == (byte)':')
        {
            sawRawRecord = true;
            int headerNulRelative = output[offset..].IndexOf((byte)0);
            if (headerNulRelative < 0)
                return new DiffHunksResult("malformed", null);
            int headerEnd = offset + headerNulRelative;
            if (!TryParseRawHeader(output.Slice(offset, headerEnd - offset), out char status,
                    out bool isSubmodule, out bool removedSubmodule, out bool sameMode,
                    out string oldObjectId, out string newObjectId,
                    out string oldMode, out string newMode))
                return new DiffHunksResult("malformed", null);

            offset = headerEnd + 1;
            int pathNulRelative = output[offset..].IndexOf((byte)0);
            if (pathNulRelative < 0 ||
                !TryDecodeGitPath(output.Slice(offset, pathNulRelative), out string? path) ||
                path is null)
            {
                return new DiffHunksResult("malformed", null);
            }
            offset += pathNulRelative + 1;
            if (!manifest.TryAdd(path,
                    new DiffFile(path, Deleted: status == 'D', new List<(int, int)>(),
                        SubmoduleLink: isSubmodule,
                        RemovedSubmoduleLink: removedSubmodule)
                    {
                        Status = status,
                        OldMode = oldMode,
                        NewMode = newMode,
                        OldObjectId = oldObjectId,
                        NewObjectId = newObjectId,
                    }))
            {
                return new DiffHunksResult("malformed", null);
            }
            manifestStatuses.Add(path, status);
            if (status == 'M' && sameMode) statOnlyEligible.Add(path);
            if (status == 'U') sawUnmerged = true;
        }

        if (!sawRawRecord || offset >= output.Length || output[offset] != 0)
            return new DiffHunksResult("malformed", null);
        offset++; // mandatory empty raw record separating the NUL manifest from the patch
        if (sawUnmerged) return new DiffHunksResult("unmerged", null);
        CorrelateExactMoves(manifest);
        if (offset == output.Length)
            return SuccessfulDiff(manifest, textSectionCounts, statOnlyEligible);

        var expectedDiffHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in manifest.Keys)
        {
            expectedDiffHeaders.Add($"diff --git a/{path} b/{path}", path);
        }

        ReadOnlySpan<byte> patch = output[offset..];
        if (patch.Length == 0 || patch[^1] != (byte)'\n')
            return new DiffHunksResult("malformed", null);

        DiffParseState state = DiffParseState.OutsideSection;
        string? oldPath = null;
        DiffFile? current = null;
        int oldRemaining = 0;
        int newRemaining = 0;
        bool sawChangeInHunk = false;
        bool markerEligible = false;
        bool sectionHasIndex = false;
        bool sectionHasNewFileMode = false;
        bool sectionHasDeletedFileMode = false;
        bool sectionHasOldMode = false;
        bool sectionHasNewMode = false;
        bool sectionHasBinaryNotice = false;
        bool sawSection = false;
        char currentStatus = '\0';
        string? sectionPath = null;

        bool SectionComplete() => state switch
        {
            DiffParseState.OutsideSection => true,
            DiffParseState.SectionMetadata => sectionHasBinaryNotice ||
                (sectionHasIndex && (sectionHasNewFileMode || sectionHasDeletedFileMode)) ||
                (sectionHasOldMode && sectionHasNewMode),
            DiffParseState.InHunk => oldRemaining == 0 && newRemaining == 0 && sawChangeInHunk,
            _ => false,
        };

        int lineOffset = 0;
        while (lineOffset < patch.Length)
        {
            int lfRelative = patch[lineOffset..].IndexOf((byte)'\n');
            if (lfRelative < 0) return new DiffHunksResult("malformed", null);
            ReadOnlySpan<byte> line = patch.Slice(lineOffset, lfRelative);
            lineOffset += lfRelative + 1;
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];

            if (StartsWithAscii(line, "diff --git "))
            {
                if (!SectionComplete() || !TryResolveDiffHeader(line, expectedDiffHeaders,
                        manifestStatuses, out string resolvedSectionPath, out currentStatus))
                    return new DiffHunksResult("malformed", null);
                sectionPath = resolvedSectionPath;
                int sectionCount = textSectionCounts.GetValueOrDefault(sectionPath) + 1;
                if (sectionCount > (currentStatus == 'T' ? 2 : 1))
                    return new DiffHunksResult("malformed", null);
                textSectionCounts[sectionPath] = sectionCount;
                sawSection = true;
                state = DiffParseState.SectionMetadata;
                oldPath = null;
                current = null;
                sectionHasIndex = sectionHasNewFileMode = sectionHasDeletedFileMode = false;
                sectionHasOldMode = sectionHasNewMode = sectionHasBinaryNotice = false;
                continue;
            }

            if (state == DiffParseState.OutsideSection)
                return new DiffHunksResult("malformed", null);

            if (state == DiffParseState.SectionMetadata)
            {
                if (StartsWithAscii(line, "--- "))
                {
                    if (!TryParseDiffSide(line[4..], "a/", out oldPath))
                        return new DiffHunksResult("malformed", null);
                    state = DiffParseState.SawOldHeader;
                    continue;
                }
                if (!IsDiffMetadata(line)) return new DiffHunksResult("malformed", null);
                sectionHasIndex |= StartsWithAscii(line, "index ");
                sectionHasNewFileMode |= StartsWithAscii(line, "new file mode ");
                sectionHasDeletedFileMode |= StartsWithAscii(line, "deleted file mode ");
                sectionHasOldMode |= StartsWithAscii(line, "old mode ");
                sectionHasNewMode |= StartsWithAscii(line, "new mode ");
                sectionHasBinaryNotice |= StartsWithAscii(line, "Binary files ");
                continue;
            }

            if (state == DiffParseState.SawOldHeader)
            {
                if (!StartsWithAscii(line, "+++ ") ||
                    !TryParseDiffSide(line[4..], "b/", out string? newPath))
                {
                    return new DiffHunksResult("malformed", null);
                }
                if (oldPath is null && newPath is null)
                    return new DiffHunksResult("malformed", null);
                if (oldPath is not null && newPath is not null &&
                    !string.Equals(oldPath, newPath, StringComparison.Ordinal))
                {
                    return new DiffHunksResult("malformed", null);
                }

                string path = newPath ?? oldPath!;
                if (!string.Equals(path, sectionPath, StringComparison.Ordinal) ||
                    !manifest.TryGetValue(path, out current) ||
                    !manifestStatuses.TryGetValue(path, out currentStatus) ||
                    (currentStatus != 'T' && current.Deleted != (newPath is null)))
                {
                    return new DiffHunksResult("malformed", null);
                }
                state = DiffParseState.ReadyForHunks;
                continue;
            }

            if (StartsWithAscii(line, "@@ "))
            {
                if (state == DiffParseState.InHunk &&
                    (oldRemaining != 0 || newRemaining != 0 || !sawChangeInHunk))
                    return new DiffHunksResult("malformed", null);
                if (current is null || !TryParseHunkHeader(line,
                        out int oldStart, out int oldCount, out int newStart, out int newCount))
                {
                    return new DiffHunksResult("malformed", null);
                }

                oldRemaining = oldCount;
                newRemaining = newCount;
                sawChangeInHunk = false;
                markerEligible = false;
                int anchorStart = Math.Max(1, newStart);
                int anchorCount = Math.Max(1, newCount);
                int anchorEnd;
                try
                {
                    // Both intervals originate in untrusted Git output. Even though only the
                    // post-change anchor is returned today, retain no overflowed old-side range in
                    // DiffHunk: former-symbol consumers rely on these values being honest.
                    _ = checked(Math.Max(1, oldStart) + Math.Max(1, oldCount) - 1);
                    anchorEnd = checked(anchorStart + anchorCount - 1);
                }
                catch (OverflowException) { return new DiffHunksResult("malformed", null); }
                // Type changes can be rendered as a delete section plus an add section. They are
                // reviewed at whole-file granularity; still parse both sections to fail closed on
                // malformed counts without manufacturing misleading line precision.
                current.Hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount));
                if (currentStatus != 'T') current.Ranges.Add((anchorStart, anchorEnd));
                state = DiffParseState.InHunk;
                continue;
            }

            if (state != DiffParseState.InHunk)
                return new DiffHunksResult("malformed", null);
            // Git localizes the prose after this structural prefix. Only the prefix is grammar.
            if (line.Length >= 2 && line[0] == (byte)'\\' && line[1] == (byte)' ')
            {
                if (!markerEligible) return new DiffHunksResult("malformed", null);
                markerEligible = false;
                continue;
            }
            if (line.Length == 0) return new DiffHunksResult("malformed", null);
            markerEligible = false;

            switch (line[0])
            {
                case (byte)'-':
                    oldRemaining--;
                    sawChangeInHunk = true;
                    markerEligible = true;
                    break;
                case (byte)'+':
                    newRemaining--;
                    sawChangeInHunk = true;
                    markerEligible = true;
                    break;
                default:
                    return new DiffHunksResult("malformed", null);
            }
            if (oldRemaining < 0 || newRemaining < 0)
                return new DiffHunksResult("malformed", null);
        }

        if (!sawSection || !SectionComplete()) return new DiffHunksResult("malformed", null);
        return SuccessfulDiff(manifest, textSectionCounts, statOnlyEligible);
    }

    private static DiffHunksResult SuccessfulDiff(Dictionary<string, DiffFile> manifest,
        IReadOnlyDictionary<string, int> textSectionCounts,
        IReadOnlySet<string> statOnlyEligible)
    {
        foreach (string path in manifest.Keys.ToList())
        {
            if (textSectionCounts.ContainsKey(path)) continue;
            // With diff.autoRefreshIndex=false, Git can report a same-mode M raw record solely
            // because cached stat metadata is stale, while emitting no patch because bytes match.
            // Dropping that structurally exact shape keeps review read-only without inventing a
            // whole-file change. Every other raw-only shape remains malformed/fail-closed.
            if (!statOnlyEligible.Contains(path)) return new DiffHunksResult("malformed", null);
            manifest.Remove(path);
        }
        var files = manifest.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return new DiffHunksResult("ok", files);
    }

    private static bool TryParseRawHeader(ReadOnlySpan<byte> header, out char status,
        out bool isSubmodule, out bool removedSubmodule, out bool sameMode) =>
        TryParseRawHeader(header, out status, out isSubmodule, out removedSubmodule,
            out sameMode, out _, out _, out _, out _);

    private static bool TryParseRawHeader(ReadOnlySpan<byte> header, out char status,
        out bool isSubmodule, out bool removedSubmodule, out bool sameMode,
        out string oldObjectId, out string newObjectId,
        out string oldMode, out string newMode)
    {
        status = '\0';
        isSubmodule = false;
        removedSubmodule = false;
        sameMode = false;
        oldObjectId = "";
        newObjectId = "";
        oldMode = "";
        newMode = "";
        if (header.Length < 2 || header[0] != (byte)':') return false;
        string[] fields;
        try { fields = Encoding.ASCII.GetString(header[1..]).Split(' '); }
        catch { return false; }
        if (fields.Length != 5 || fields.Any(string.IsNullOrEmpty) ||
            !IsGitMode(fields[0]) || !IsGitMode(fields[1]) ||
            !IsAsciiHex(fields[2]) || !IsAsciiHex(fields[3]) || fields[4].Length != 1)
        {
            return false;
        }
        status = fields[4][0];
        oldObjectId = fields[2];
        newObjectId = fields[3];
        oldMode = fields[0];
        newMode = fields[1];
        isSubmodule = fields[0] == "160000" || fields[1] == "160000";
        removedSubmodule = fields[0] == "160000" && fields[1] != "160000";
        sameMode = fields[0] == fields[1];
        return status is 'A' or 'M' or 'D' or 'T' or 'U';
    }

    private static void CorrelateExactMoves(Dictionary<string, DiffFile> manifest)
    {
        var deletedByOid = manifest.Values
            .Where(file => file.Status == 'D' && IsReviewableCSharpPath(file.Path) &&
                           IsRegularGitMode(file.OldMode) &&
                           IsFullObjectId(file.OldObjectId))
            .GroupBy(file => file.OldObjectId!, StringComparer.Ordinal);
        var addedByOid = manifest.Values
            .Where(file => file.Status == 'A' && IsReviewableCSharpPath(file.Path) &&
                           IsRegularGitMode(file.NewMode) &&
                           IsFullObjectId(file.NewObjectId))
            .GroupBy(file => file.NewObjectId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        foreach (var deletedGroup in deletedByOid)
        {
            List<DiffFile> deleted = deletedGroup.ToList();
            if (deleted.Count != 1 || !addedByOid.TryGetValue(deletedGroup.Key,
                    out List<DiffFile>? added) || added.Count != 1)
            {
                continue;
            }
            DiffFile oldFile = deleted[0];
            DiffFile newFile = added[0];
            manifest[oldFile.Path] = oldFile with { MovedToPath = newFile.Path };
            manifest[newFile.Path] = newFile with { MovedFromPath = oldFile.Path };
        }
    }

    private static bool IsReviewableCSharpPath(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsRegularGitMode(string? mode) => mode is "100644" or "100755";

    private static bool IsFullObjectId(string? oid) =>
        oid is { Length: 40 or 64 } && oid.All(char.IsAsciiHexDigit) &&
        !oid.All(c => c == '0');

    private static bool IsGitMode(string value) =>
        value.Length == 6 && value.All(c => c is >= '0' and <= '7');

    private static bool IsAsciiHex(string value) =>
        value.Length is >= 4 and <= 64 && value.All(char.IsAsciiHexDigit);

    private static bool TryDecodeGitPath(ReadOnlySpan<byte> bytes, out string? path)
    {
        path = null;
        if (!TryDecodeUtf8(bytes, out string? decoded) || decoded is null ||
            !IsSafeRelativeGitPath(decoded))
        {
            return false;
        }
        path = decoded;
        return true;
    }

    internal static bool IsSafeRelativeGitPath(string path) =>
        IsSafeRelativeGitPath(path, OperatingSystem.IsWindows());

    internal static bool IsSafeRelativeGitPath(string path, bool backslashIsSeparator)
    {
        if (path.Length is 0 or > 32768 || path.Any(char.IsControl) ||
            path.StartsWith('/') ||
            (backslashIsSeparator && path.Contains('\\')))
        {
            return false;
        }
        foreach (string segment in path.Split('/'))
        {
            if (segment is "" or "." or "..") return false;
        }
        return true;
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string? value)
    {
        try
        {
            value = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            value = null;
            return false;
        }
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> value, string prefix)
    {
        if (value.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (prefix[i] > 0x7f || value[i] != (byte)prefix[i]) return false;
        }
        return true;
    }

    private static bool IsDiffMetadata(ReadOnlySpan<byte> line)
    {
        if (!TryDecodeUtf8(line, out string? text) || text is null) return false;
        return text.StartsWith("index ", StringComparison.Ordinal) ||
               text.StartsWith("new file mode ", StringComparison.Ordinal) ||
               text.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
               text.StartsWith("old mode ", StringComparison.Ordinal) ||
               text.StartsWith("new mode ", StringComparison.Ordinal) ||
               (text.StartsWith("Binary files ", StringComparison.Ordinal) &&
                text.EndsWith(" differ", StringComparison.Ordinal));
    }

    private static bool TryParseDiffSide(ReadOnlySpan<byte> raw, string prefix, out string? path)
    {
        if (raw.Length > 0 && raw[0] == (byte)'"')
        {
            if (!TryDecodeGitQuotedToken(raw, out string? quoted, out int consumed) ||
                quoted is null ||
                (consumed != raw.Length &&
                 !(consumed + 1 == raw.Length && raw[consumed] == (byte)'\t')))
            {
                path = null;
                return false;
            }
            return TryParseDiffSide(quoted, prefix, out path);
        }
        if (raw.Length > 0 && raw[^1] == (byte)'\t') raw = raw[..^1];
        if (!TryDecodeUtf8(raw, out string? side) || side is null)
        {
            path = null;
            return false;
        }
        return TryParseDiffSide(side, prefix, out path);
    }

    private static bool TryResolveDiffHeader(ReadOnlySpan<byte> line,
        IReadOnlyDictionary<string, string> expectedHeaders,
        IReadOnlyDictionary<string, char> manifestStatuses,
        out string path, out char status)
    {
        path = "";
        status = '\0';
        if (TryDecodeUtf8(line, out string? exact) && exact is not null &&
            expectedHeaders.TryGetValue(exact, out string? exactPath) && exactPath is not null &&
            manifestStatuses.TryGetValue(exactPath, out status))
        {
            path = exactPath;
            return true;
        }

        const string prefix = "diff --git ";
        if (!StartsWithAscii(line, prefix)) return false;
        ReadOnlySpan<byte> rest = line[prefix.Length..];
        if (!TryDecodeGitQuotedToken(rest, out string? oldSide, out int oldConsumed) ||
            oldSide is null || oldConsumed >= rest.Length || rest[oldConsumed] != (byte)' ')
        {
            return false;
        }
        rest = rest[(oldConsumed + 1)..];
        if (!TryDecodeGitQuotedToken(rest, out string? newSide, out int newConsumed) ||
            newSide is null || newConsumed != rest.Length ||
            !TryParseDiffSide(oldSide, "a/", out string? oldPath) || oldPath is null ||
            !TryParseDiffSide(newSide, "b/", out string? newPath) || newPath is null ||
            !string.Equals(oldPath, newPath, StringComparison.Ordinal) ||
            !manifestStatuses.TryGetValue(oldPath, out status))
        {
            return false;
        }
        path = oldPath;
        return true;
    }

    private static bool TryDecodeGitQuotedToken(ReadOnlySpan<byte> raw, out string? value,
        out int consumed)
    {
        value = null;
        consumed = 0;
        if (raw.Length < 2 || raw[0] != (byte)'"' || raw.Length > MaxGitPathBytes * 2)
            return false;
        var decoded = new byte[Math.Min(raw.Length, MaxGitPathBytes)];
        int written = 0;
        for (int i = 1; i < raw.Length; i++)
        {
            byte current = raw[i];
            if (current == (byte)'"')
            {
                consumed = i + 1;
                return TryDecodeUtf8(decoded.AsSpan(0, written), out value) && value is not null;
            }
            if (written >= decoded.Length) return false;
            if (current != (byte)'\\')
            {
                decoded[written++] = current;
                continue;
            }
            if (++i >= raw.Length) return false;
            byte escaped = raw[i];
            byte translated = escaped switch
            {
                (byte)'a' => 0x07,
                (byte)'b' => 0x08,
                (byte)'t' => 0x09,
                (byte)'n' => 0x0a,
                (byte)'v' => 0x0b,
                (byte)'f' => 0x0c,
                (byte)'r' => 0x0d,
                (byte)'"' => (byte)'"',
                (byte)'\\' => (byte)'\\',
                _ => 0xff,
            };
            if (translated != 0xff)
            {
                decoded[written++] = translated;
                continue;
            }
            if (escaped is < (byte)'0' or > (byte)'7') return false;
            int octal = escaped - (byte)'0';
            int digits = 1;
            while (digits < 3 && i + 1 < raw.Length &&
                   raw[i + 1] is >= (byte)'0' and <= (byte)'7')
            {
                octal = (octal * 8) + raw[++i] - (byte)'0';
                digits++;
            }
            if (octal > byte.MaxValue) return false;
            decoded[written++] = (byte)octal;
        }
        return false;
    }

    private static bool TryParseHunkHeader(ReadOnlySpan<byte> line, out int oldStart,
        out int oldCount, out int newStart, out int newCount)
    {
        oldStart = oldCount = newStart = newCount = 0;
        int close = IndexOfAscii(line, " @@", 4);
        if (close < 0) return false;
        if (close + 3 < line.Length && line[close + 3] != (byte)' ') return false;
        ReadOnlySpan<byte> grammar = line[..(close + 3)];
        foreach (byte value in grammar)
        {
            if (value >= 0x80) return false;
        }
        return TryParseHunkHeader(Encoding.ASCII.GetString(grammar),
            out oldStart, out oldCount, out newStart, out newCount);
    }

    private static int IndexOfAscii(ReadOnlySpan<byte> value, string needle, int start)
    {
        for (int i = Math.Max(0, start); i <= value.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (value[i + j] != (byte)needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static bool TryParseDiffSide(string raw, string prefix, out string? path)
    {
        string side = StripHeaderTerminator(raw);
        if (side == "/dev/null")
        {
            path = null;
            return true;
        }
        if (!side.StartsWith(prefix, StringComparison.Ordinal) || side.Length == prefix.Length ||
            side.Any(char.IsControl))
        {
            path = null;
            return false;
        }
        path = side[prefix.Length..];
        return true;
    }

    private static bool TryParseHunkHeader(string line, out int oldStart, out int oldCount,
        out int newStart, out int newCount)
    {
        oldStart = oldCount = newStart = newCount = 0;
        if (!line.StartsWith("@@ -", StringComparison.Ordinal)) return false;
        int oldEnd = line.IndexOf(' ', 4);
        if (oldEnd < 0 || oldEnd + 2 >= line.Length || line[oldEnd + 1] != '+') return false;
        int newEnd = line.IndexOf(" @@", oldEnd + 2, StringComparison.Ordinal);
        if (newEnd < 0 || (newEnd + 3 < line.Length && line[newEnd + 3] != ' ')) return false;
        return TryParseHunkSpan(line[4..oldEnd], out oldStart, out oldCount) &&
               TryParseHunkSpan(line[(oldEnd + 2)..newEnd], out newStart, out newCount);
    }

    private static bool TryParseHunkSpan(string span, out int start, out int count)
    {
        start = count = 0;
        int comma = span.IndexOf(',');
        string startText = comma < 0 ? span : span[..comma];
        string countText = comma < 0 ? "1" : span[(comma + 1)..];
        if (!int.TryParse(startText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out start) ||
            !int.TryParse(countText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out count))
        {
            return false;
        }
        return start >= 0 && count >= 0 && (count == 0 || start > 0);
    }

    /// <summary>Git appends a TAB terminator to diff header paths that CONTAIN SPACES (the GNU
    /// timestamp-separator convention). Left in place, the "path" carries a trailing '\t'
    /// (review, reproduced): ShowFile's control-char guard then rejected it — deletion honesty
    /// silently lost — and the ghost entry double-counted the file while the tab-free dirt
    /// union re-added it at the wrong granularity. Strip exactly one trailing tab.</summary>
    private static string StripHeaderTerminator(string s) =>
        s.EndsWith('\t') ? s[..^1] : s;

    /// <summary>Owns the bounded preflight plus a private Git common-dir whose highest-precedence
    /// attributes make content filters unselectable. `--no-ext-diff` and `--no-textconv` do not
    /// disable clean/process filters; without this boundary a supposedly read-only worktree
    /// comparison can execute an arbitrary helper.</summary>
    private sealed class GitSafetyEnvironment : IDisposable
    {
        private readonly string _temporaryRoot;
        private readonly List<FileStream> _guards;
        private int _disposed;

        public GitSafetyEnvironment(Dictionary<string, string?> environment,
            HashSet<string> unsafeFilteredPaths,
            SubmoduleWorktreeCoverage? excludedSubmoduleWorktrees,
            string? headOid, bool isUnborn, string temporaryRoot, List<FileStream> guards)
        {
            Environment = environment;
            UnsafeFilteredPaths = unsafeFilteredPaths;
            ExcludedSubmoduleWorktrees = excludedSubmoduleWorktrees;
            HeadOid = headOid;
            IsUnborn = isUnborn;
            _temporaryRoot = temporaryRoot;
            _guards = guards;
        }

        public Dictionary<string, string?> Environment { get; }
        public HashSet<string> UnsafeFilteredPaths { get; }
        public SubmoduleWorktreeCoverage? ExcludedSubmoduleWorktrees { get; }
        public string? HeadOid { get; }
        public bool IsUnborn { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (FileStream guard in _guards)
            {
                try { guard.Dispose(); } catch { /* best-effort temporary cleanup */ }
            }
            try { Directory.Delete(_temporaryRoot, recursive: true); }
            catch { /* inert OS-temp metadata may be reclaimed later */ }
        }
    }

    private sealed record GitRepositoryLayout(string GitDir, string CommonDir,
        string ObjectDirectory, string IndexFile, string ConfigFile,
        string InfoAttributesFile, string InfoExcludeFile, string SparseCheckoutFile,
        int RepositoryFormatVersion, Dictionary<string, string> RepositoryExtensions,
        string TopLevel);

    private static GitSafetyEnvironment? FilterNeutralEnvironment(string workspaceRoot,
        string? gitExe, IReadOnlyDictionary<string, string?>? baseOverrides)
    {
        if (gitExe is null) return null;
        var discoveryEnvironment = CopyEnvironmentOverrides(baseOverrides);
        ClearGitRepositorySelection(discoveryEnvironment);
        // GIT_CONFIG replaces normal repository config for `git config` but not for every Git
        // command. Letting it survive discovery could hide the filters the subsequent diff sees.
        discoveryEnvironment["GIT_CONFIG"] = null;
        string? listed = RunWithExecutable(gitExe, workspaceRoot,
            "-c filter.codenav-safety-sentinel.required=false " +
            "config --includes --null --get-regexp filter[.]",
            environmentOverrides: discoveryEnvironment);
        if (listed is null || (listed.Length > 0 && listed[^1] != '\0')) return null;

        var drivers = new HashSet<string>(StringComparer.Ordinal);
        var executableDrivers = new HashSet<string>(StringComparer.Ordinal);
        var effectiveCommands = new Dictionary<string, (string? Clean, string? Process)>(
            StringComparer.Ordinal);
        if (listed.Length > 0)
        {
            string[] entries = listed.Split('\0');
            for (int i = 0; i < entries.Length - 1; i++)
            {
                string entry = entries[i];
                int lineFeed = entry.IndexOf('\n');
                if (lineFeed <= 0) return null;
                string key = entry[..lineFeed];
                string value = entry[(lineFeed + 1)..];
                if (!TryGetFilterDriver(key, out string? driver, out bool executable)) continue;
                if (driver is null || !IsSafeFilterDriver(driver)) return null;
                drivers.Add(driver);
                if (executable)
                {
                    var commands = effectiveCommands.GetValueOrDefault(driver);
                    if (key.EndsWith(".clean", StringComparison.OrdinalIgnoreCase))
                        commands.Clean = value;
                    else
                        commands.Process = value;
                    effectiveCommands[driver] = commands;
                }
                if (drivers.Count > 256) return null;
            }
        }
        foreach (var (driver, commands) in effectiveCommands)
        {
            if (!string.IsNullOrEmpty(commands.Clean) ||
                !string.IsNullOrEmpty(commands.Process))
            {
                executableDrivers.Add(driver);
            }
        }

        var neutral = CopyEnvironmentOverrides(baseOverrides);
        ClearGitRepositorySelection(neutral);
        neutral["GIT_CONFIG"] = null;
        neutral["GIT_CONFIG_PARAMETERS"] = null;
        int count = drivers.Count * 3;
        neutral["GIT_CONFIG_COUNT"] = count.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        int index = 0;
        int environmentChars = 0;
        foreach (string driver in drivers.OrderBy(x => x, StringComparer.Ordinal))
        {
            foreach (var (suffix, value) in new[]
                     {
                         ("clean", ""), ("process", ""), ("required", "false"),
                     })
            {
                string key = $"filter.{driver}.{suffix}";
                environmentChars += key.Length + value.Length;
                if (environmentChars > 24 * 1024) return null;
                neutral[$"GIT_CONFIG_KEY_{index}"] = key;
                neutral[$"GIT_CONFIG_VALUE_{index}"] = value;
                index++;
            }
        }
        TrackedSafetyScan? scan = DiscoverTrackedSafety(
            workspaceRoot, gitExe, neutral, executableDrivers);
        if (scan is null) return null;
        return TryCreateFilterSandbox(workspaceRoot, gitExe, neutral, scan);
    }

    /// <summary>The preflight above detects filters active at the snapshot, but it cannot make a
    /// later process immune to a newly-added driver. Run every worktree-reading command with a
    /// private common-dir whose highest-precedence info/attributes ends in `* !filter`. Git then
    /// cannot select any clean/process driver, regardless of config or attribute races, while all
    /// non-filter attributes and repository config continue to describe the real worktree.</summary>
    private static GitSafetyEnvironment? TryCreateFilterSandbox(string workspaceRoot,
        string gitExe, Dictionary<string, string?> neutralEnvironment, TrackedSafetyScan scan)
    {
        GitRepositoryLayout? layout = ResolveGitRepositoryLayout(
            workspaceRoot, gitExe, neutralEnvironment);
        if (layout is null) return null;

        string? headOutput = RunWithExecutable(gitExe, workspaceRoot,
            "rev-parse --verify HEAD", environmentOverrides: neutralEnvironment)?.Trim();
        string? headOid = headOutput is not null && IsHexCommit(headOutput) ? headOutput : null;
        bool isUnborn = false;
        if (headOid is null)
        {
            string? symbolic = RunWithExecutable(gitExe, workspaceRoot,
                "symbolic-ref -q HEAD", environmentOverrides: neutralEnvironment)?.Trim();
            isUnborn = symbolic is not null && symbolic.StartsWith("refs/", StringComparison.Ordinal) &&
                       IsSafeRefName(symbolic);
            if (!isUnborn) return null;
        }

        string? temporaryRoot = null;
        var guards = new List<FileStream>();
        try
        {
            temporaryRoot = Directory.CreateTempSubdirectory(
                "PhoenixCodeNav-git-safety-").FullName;
            string info = Directory.CreateDirectory(Path.Combine(temporaryRoot, "info")).FullName;
            Directory.CreateDirectory(Path.Combine(temporaryRoot, "objects"));
            Directory.CreateDirectory(Path.Combine(temporaryRoot, "refs"));

            var configBuilder = new StringBuilder();
            configBuilder.Append("[include]\n\tpath = ")
                .Append(QuoteGitConfigValue(layout.ConfigFile)).Append('\n')
                .Append("[core]\n\trepositoryFormatVersion = ")
                .Append(layout.RepositoryFormatVersion)
                .Append('\n');
            if (layout.RepositoryExtensions.Count > 0)
            {
                configBuilder.Append("[extensions]\n");
                foreach (var (name, configuredValue) in layout.RepositoryExtensions
                             .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string value = name.Equals("refStorage", StringComparison.OrdinalIgnoreCase)
                        ? "files"
                        : configuredValue;
                    configBuilder.Append('\t').Append(name).Append(" = ")
                        .Append(QuoteGitConfigValue(value)).Append('\n');
                }
            }
            guards.Add(CreateGuardedFile(Path.Combine(temporaryRoot, "config"),
                Encoding.UTF8.GetBytes(configBuilder.ToString())));

            byte[]? attributes = ReadBoundedRegularFile(layout.InfoAttributesFile,
                1024 * 1024, layout.CommonDir);
            if (attributes is null) throw new IOException("unsafe or oversized info/attributes");
            using (var content = new MemoryStream(attributes.Length + 32))
            {
                content.Write(attributes);
                if (attributes.Length > 0 && attributes[^1] != (byte)'\n') content.WriteByte((byte)'\n');
                content.Write("* !filter\n"u8);
                guards.Add(CreateGuardedFile(Path.Combine(info, "attributes"), content.ToArray()));
            }

            foreach (var (source, destination, limit, anchor) in new[]
                     {
                         (layout.InfoExcludeFile, Path.Combine(info, "exclude"),
                             4 * 1024 * 1024, layout.CommonDir),
                         (layout.SparseCheckoutFile, Path.Combine(info, "sparse-checkout"),
                             4 * 1024 * 1024, layout.GitDir),
                     })
            {
                byte[]? bytes = ReadBoundedRegularFile(source, limit, anchor);
                if (bytes is null) throw new IOException("unsafe or oversized Git metadata");
                if (bytes.Length > 0) guards.Add(CreateGuardedFile(destination, bytes));
            }

            var runtime = CopyEnvironmentOverrides(neutralEnvironment);
            runtime["GIT_DIR"] = layout.GitDir;
            runtime["GIT_COMMON_DIR"] = temporaryRoot;
            runtime["GIT_OBJECT_DIRECTORY"] = layout.ObjectDirectory;
            runtime["GIT_INDEX_FILE"] = layout.IndexFile;
            runtime["GIT_WORK_TREE"] = layout.TopLevel;
            return new GitSafetyEnvironment(runtime, scan.UnsafeFilteredPaths,
                scan.ExcludedSubmoduleWorktrees, headOid, isUnborn, temporaryRoot, guards);
        }
        catch
        {
            foreach (FileStream guard in guards)
            {
                try { guard.Dispose(); } catch { }
            }
            if (temporaryRoot is not null)
            {
                try { Directory.Delete(temporaryRoot, recursive: true); } catch { }
            }
            return null;
        }
    }

    private static GitRepositoryLayout? ResolveGitRepositoryLayout(string workspaceRoot,
        string gitExe, IReadOnlyDictionary<string, string?> environment)
    {
        string? gitDir = ResolveAbsoluteGitPath(workspaceRoot, gitExe,
            "rev-parse --absolute-git-dir", environment);
        string? commonDir = ResolveAbsoluteGitPath(workspaceRoot, gitExe,
            "rev-parse --path-format=absolute --git-common-dir", environment);
        string? objects = ResolveAbsoluteGitPath(workspaceRoot, gitExe,
            "rev-parse --path-format=absolute --git-path objects", environment);
        string? index = ResolveAbsoluteGitPath(workspaceRoot, gitExe,
            "rev-parse --path-format=absolute --git-path index", environment,
            requireExisting: false);
        string? sparse = ResolveAbsoluteGitPath(workspaceRoot, gitExe,
            "rev-parse --path-format=absolute --git-path info/sparse-checkout", environment,
            requireExisting: false);
        string? topLevel = ResolveAbsoluteGitPath(workspaceRoot, gitExe,
            "rev-parse --path-format=absolute --show-toplevel", environment);
        string commonConfig = Path.Combine(commonDir ?? "", "config");
        var format = commonDir is null
            ? null
            : ReadRepositoryFormat(workspaceRoot, gitExe, environment, commonConfig);
        if (gitDir is null || commonDir is null || objects is null || index is null ||
            sparse is null || topLevel is null || format is null ||
            !IsSameOrDescendantPath(Path.GetFullPath(workspaceRoot), topLevel))
            return null;
        return new GitRepositoryLayout(gitDir, commonDir, objects, index,
            Path.Combine(commonDir, "config"), Path.Combine(commonDir, "info", "attributes"),
            Path.Combine(commonDir, "info", "exclude"), sparse,
            format.Value.Version, format.Value.Extensions, topLevel);
    }

    private static bool IsSameOrDescendantPath(string candidate, string parent)
    {
        try
        {
            string fullCandidate = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullParent = Path.GetFullPath(parent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(fullCandidate, fullParent, comparison) ||
                   fullCandidate.StartsWith(fullParent + Path.DirectorySeparatorChar,
                       comparison);
        }
        catch
        {
            return false;
        }
    }

    private static (int Version, Dictionary<string, string> Extensions)? ReadRepositoryFormat(
        string workspaceRoot, string gitExe, IReadOnlyDictionary<string, string?> environment,
        string commonConfigFile)
    {
        string? listed = RunWithExecutable(gitExe, workspaceRoot,
            "config --local --no-includes --null --show-origin --list",
            environmentOverrides: environment);
        if (listed is null || (listed.Length > 0 && listed[^1] != '\0')) return null;
        string[] records = listed.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length == 0) return (0, new(StringComparer.OrdinalIgnoreCase));
        if (records.Length % 2 != 0) return null;
        var entries = new List<(string Origin, string Key, string? Value)>();
        for (int i = 0; i < records.Length; i += 2)
        {
            string origin = records[i];
            string entry = records[i + 1];
            int lineFeed = entry.IndexOf('\n');
            if (!origin.StartsWith("file:", StringComparison.Ordinal) || lineFeed == 0 ||
                entry.Length == 0)
            {
                return null;
            }
            entries.Add(lineFeed < 0
                ? (origin, entry, null)
                : (origin, entry[..lineFeed], entry[(lineFeed + 1)..]));
        }
        List<(string Origin, string Key, string? Value)> commonEntries = entries
            .Where(entry => GitConfigOriginMatches(entry.Origin, commonConfigFile, workspaceRoot))
            .ToList();
        if (commonEntries.Count == 0) return (0, new(StringComparer.OrdinalIgnoreCase));

        int? version = null;
        var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, key, value) in commonEntries)
        {
            if (key.Equals("core.repositoryformatversion", StringComparison.OrdinalIgnoreCase))
            {
                if (value is null || !int.TryParse(value,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out int parsed) ||
                    parsed is < 0 or > 1)
                {
                    return null;
                }
                version = parsed;
            }
            else if (key.StartsWith("extensions.", StringComparison.OrdinalIgnoreCase))
            {
                string name = key["extensions.".Length..];
                if (value is null || name.Length is 0 or > 100 || value.Length > 4096 ||
                    !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ||
                    value.Any(char.IsControl) || extensions.Count >= 32)
                {
                    return null;
                }
                extensions[name] = value;
            }
        }
        // Git's setup.c accepts a missing repositoryFormatVersion as the legacy v0 format and
        // does not interpret extension keys in that missing-version case.
        if (version is null) return (0, new(StringComparer.OrdinalIgnoreCase));
        return (version.Value, extensions);
    }

    private static bool GitConfigOriginMatches(string origin, string expectedFile,
        string workspaceRoot)
    {
        try
        {
            string raw = origin["file:".Length..];
            if (raw.Length == 0 || raw.Any(char.IsControl)) return false;
            string actual = Path.IsPathFullyQualified(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(workspaceRoot, raw));
            string expected = Path.GetFullPath(expectedFile);
            return string.Equals(actual, expected, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveAbsoluteGitPath(string workspaceRoot, string gitExe,
        string args, IReadOnlyDictionary<string, string?> environment,
        bool requireExisting = true)
    {
        string? output = RunWithExecutable(gitExe, workspaceRoot, args,
            environmentOverrides: environment)?.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(output) || output.Any(char.IsControl)) return null;
        try
        {
            string path = Path.IsPathFullyQualified(output)
                ? Path.GetFullPath(output)
                : Path.GetFullPath(Path.Combine(workspaceRoot, output));
            if (requireExisting && !Directory.Exists(path)) return null;
            return path;
        }
        catch
        {
            return null;
        }
    }

    internal static string QuoteGitConfigValue(string value)
    {
        if (value.Any(char.IsControl)) throw new IOException("invalid Git metadata path");
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Missing metadata is an empty snapshot. Existing files must be bounded regular
    /// files; never dereference a repository-controlled link while constructing the sandbox.</summary>
    public static byte[]? ReadBoundedRegularFile(string path, int maxBytes,
        string allowedRoot) =>
        ReadBoundedRegularFileCore(path, maxBytes, allowedRoot, missingAsEmpty: true);

    /// <summary>Reads one workspace-relative Git path without following links or blocking on
    /// special files. Linux worktree reconciliation may pass a pinned /proc/&lt;pid&gt;/fd directory;
    /// in that case traversal starts directly from the held descriptor rather than rejecting the
    /// trusted proc descriptor link as repository-controlled metadata.</summary>
    internal enum WorkspaceFileReadDisposition
    {
        Success,
        Missing,
        DefinitelyNonRegular,
        Unavailable,
    }

    internal readonly record struct WorkspaceFileReadResult(
        WorkspaceFileReadDisposition Disposition, byte[]? Bytes);

    public static byte[]? ReadBoundedWorkspaceFile(string workspaceRoot, string gitPath,
        int maxBytes) => ReadBoundedWorkspaceFileResult(workspaceRoot, gitPath, maxBytes).Bytes;

    internal static WorkspaceFileReadResult ReadBoundedWorkspaceFileResult(
        string workspaceRoot, string gitPath, int maxBytes)
    {
        if (!WorkspacePaths.TryResolveGitPathInside(workspaceRoot, gitPath,
                out string fullPath))
            return new(WorkspaceFileReadDisposition.Unavailable, null);
        if (OperatingSystem.IsLinux() &&
            TryParseOwnProcDirectoryFd(workspaceRoot, out int directoryFd))
        {
            return ReadBoundedUnixFileAt(directoryFd, gitPath, maxBytes);
        }
        byte[]? bytes = ReadBoundedRegularFileCore(fullPath, maxBytes, workspaceRoot,
            missingAsEmpty: false);
        if (bytes is not null)
            return new(WorkspaceFileReadDisposition.Success, bytes);
        return new(ClassifyUnreadableWorkspacePath(workspaceRoot, fullPath), null);
    }

    private static bool TryParseOwnProcDirectoryFd(string root, out int descriptor)
    {
        descriptor = -1;
        string prefix = $"/proc/{Environment.ProcessId}/fd/";
        return root.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(root.AsSpan(prefix.Length), out descriptor) && descriptor >= 0;
    }

    private static WorkspaceFileReadDisposition ClassifyUnreadableWorkspacePath(
        string workspaceRoot,
        string fullPath)
    {
        try
        {
            if (WorkspacePaths.EscapesViaReparsePoint(workspaceRoot, fullPath))
                return WorkspaceFileReadDisposition.DefinitelyNonRegular;
            if (OperatingSystem.IsWindows())
            {
                FileAttributes attributes = File.GetAttributes(fullPath);
                return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device |
                                      FileAttributes.Directory)) != 0
                    ? WorkspaceFileReadDisposition.DefinitelyNonRegular
                    : WorkspaceFileReadDisposition.Unavailable;
            }
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (LStatUnix(fullPath, out UnixFileStatus status) == 0)
                    return (status.Mode & UnixFileTypeMask) != UnixRegularFile
                        ? WorkspaceFileReadDisposition.DefinitelyNonRegular
                        : WorkspaceFileReadDisposition.Unavailable;
                return Marshal.GetLastPInvokeError() == UnixNoSuchFile
                    ? WorkspaceFileReadDisposition.Missing
                    : WorkspaceFileReadDisposition.Unavailable;
            }
        }
        catch (FileNotFoundException)
        {
            return WorkspaceFileReadDisposition.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return WorkspaceFileReadDisposition.Missing;
        }
        catch { }
        return WorkspaceFileReadDisposition.Unavailable;
    }

    private static WorkspaceFileReadResult ReadBoundedUnixFileAt(
        int rootDescriptor, string gitPath,
        int maxBytes)
    {
        string[] segments = gitPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            return new(WorkspaceFileReadDisposition.Unavailable, null);
        var openedDirectories = new List<SafeFileHandle>();
        using var root = new SafeFileHandle((IntPtr)rootDescriptor, ownsHandle: false);
        SafeFileHandle current = root;
        try
        {
            int directoryFlags = UnixOpenNonBlockingLinux | UnixOpenNoFollowLinux |
                UnixOpenNoControllingTerminalLinux | UnixOpenCloseOnExecLinux |
                UnixOpenDirectoryLinux;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                int descriptor = OpenAtUnix(current, segments[i], directoryFlags);
                if (descriptor < 0)
                    return new(Marshal.GetLastPInvokeError() == UnixNoSuchFile
                        ? WorkspaceFileReadDisposition.Missing
                        : WorkspaceFileReadDisposition.Unavailable, null);
                var next = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                openedDirectories.Add(next);
                if (!IsUnixFileType(next, UnixDirectory, out _))
                    return new(WorkspaceFileReadDisposition.Unavailable, null);
                current = next;
            }

            if (TryStatxTypeAt(current.DangerousGetHandle().ToInt32(), segments[^1],
                    out int namedType) && namedType != UnixRegularFile)
            {
                return new(WorkspaceFileReadDisposition.DefinitelyNonRegular, null);
            }
            int fileFlags = UnixOpenNonBlockingLinux | UnixOpenNoFollowLinux |
                UnixOpenNoControllingTerminalLinux | UnixOpenCloseOnExecLinux;
            int fileDescriptor = OpenAtUnix(current, segments[^1], fileFlags);
            if (fileDescriptor < 0)
                return new(Marshal.GetLastPInvokeError() == UnixNoSuchFile
                    ? WorkspaceFileReadDisposition.Missing
                    : WorkspaceFileReadDisposition.Unavailable, null);
            using var file = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
            if (!IsUnixFileType(file, UnixRegularFile, out long length))
            {
                return new(WorkspaceFileReadDisposition.DefinitelyNonRegular, null);
            }
            if (length < 0 || length > maxBytes || length > int.MaxValue ||
                SetUnixNonBlocking(file, 0) != 0)
                return new(WorkspaceFileReadDisposition.Unavailable, null);
            byte[]? bytes = ReadBoundedHandle(file, length);
            return bytes is null
                ? new(WorkspaceFileReadDisposition.Unavailable, null)
                : new(WorkspaceFileReadDisposition.Success, bytes);
        }
        finally
        {
            for (int i = openedDirectories.Count - 1; i >= 0; i--)
                openedDirectories[i].Dispose();
        }
    }

    private static byte[]? ReadExistingBoundedRegularFile(string path, int maxBytes,
        string allowedRoot) =>
        ReadBoundedRegularFileCore(path, maxBytes, allowedRoot, missingAsEmpty: false);

    private static byte[]? ReadBoundedRegularFileCore(string path, int maxBytes,
        string allowedRoot, bool missingAsEmpty)
    {
        try
        {
            if (!TryBuildMetadataTraversal(path, allowedRoot, out string volumeRoot,
                    out string[] anchorSegments, out string[] targetSegments))
            {
                return null;
            }
            if (!OperatingSystem.IsWindows())
            {
                int fileFlags = OperatingSystem.IsLinux()
                    ? UnixOpenNonBlockingLinux | UnixOpenNoFollowLinux |
                      UnixOpenNoControllingTerminalLinux | UnixOpenCloseOnExecLinux
                    : OperatingSystem.IsMacOS()
                        ? UnixOpenNonBlockingBsd | UnixOpenNoFollowBsd |
                          UnixOpenNoControllingTerminalMac | UnixOpenCloseOnExecMac
                        : -1;
                int directoryFlag = OperatingSystem.IsLinux()
                    ? UnixOpenDirectoryLinux
                    : OperatingSystem.IsMacOS() ? UnixOpenDirectoryMac : 0;
                if (fileFlags < 0 || directoryFlag == 0) return null;
                return ReadBoundedUnixFile(volumeRoot, anchorSegments, targetSegments,
                    maxBytes, fileFlags, fileFlags | directoryFlag, missingAsEmpty);
            }

            return ReadBoundedWindowsFile(volumeRoot, anchorSegments, targetSegments, maxBytes,
                missingAsEmpty);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryBuildMetadataTraversal(string path, string allowedRoot,
        out string volumeRoot, out string[] anchorSegments, out string[] targetSegments)
    {
        volumeRoot = "";
        anchorSegments = [];
        targetSegments = [];
        string anchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        volumeRoot = Path.GetPathRoot(anchor) ?? "";
        if (volumeRoot.Length == 0 ||
            !TryRelativePathSegments(volumeRoot, anchor, allowEmpty: true, out anchorSegments) ||
            !TryRelativePathSegments(anchor, Path.GetFullPath(path), allowEmpty: false,
                out targetSegments))
        {
            return false;
        }
        return true;
    }

    private static bool TryRelativePathSegments(string root, string target, bool allowEmpty,
        out string[] segments)
    {
        segments = [];
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        target = Path.GetFullPath(target);
        string relative = Path.GetRelativePath(root, target);
        if (relative == ".") return allowEmpty;
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }
        segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
    }

    private static byte[]? ReadBoundedUnixFile(string volumeRoot, string[] anchorSegments,
        string[] targetSegments, int maxBytes, int fileFlags, int directoryFlags,
        bool missingAsEmpty)
    {
        int rootDescriptor = OpenUnix(volumeRoot, directoryFlags, 0);
        if (rootDescriptor < 0) return null;
        using var rootHandle = new SafeFileHandle((IntPtr)rootDescriptor, ownsHandle: true);
        if (!IsUnixFileType(rootHandle, UnixDirectory, out _)) return null;

        SafeFileHandle current = rootHandle;
        var openedDirectories = new List<SafeFileHandle>();
        try
        {
            int directoryCount = anchorSegments.Length + targetSegments.Length - 1;
            for (int i = 0; i < directoryCount; i++)
            {
                bool isAnchor = i < anchorSegments.Length;
                string segment = isAnchor
                    ? anchorSegments[i]
                    : targetSegments[i - anchorSegments.Length];
                int descriptor = OpenAtUnix(current, segment, directoryFlags);
                if (descriptor < 0)
                    return !isAnchor && missingAsEmpty &&
                           Marshal.GetLastPInvokeError() == UnixNoSuchFile ? [] : null;
                var next = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                openedDirectories.Add(next);
                if (!IsUnixFileType(next, UnixDirectory, out _)) return null;
                current = next;
            }

            int fileDescriptor = OpenAtUnix(current, targetSegments[^1], fileFlags);
            if (fileDescriptor < 0)
                return missingAsEmpty && Marshal.GetLastPInvokeError() == UnixNoSuchFile
                    ? [] : null;
            using var file = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
            if (!IsUnixFileType(file, UnixRegularFile, out long length) ||
                length < 0 || length > maxBytes || length > int.MaxValue)
            {
                return null;
            }
            if (SetUnixNonBlocking(file, 0) != 0) return null;
            return ReadBoundedHandle(file, length);
        }
        finally
        {
            for (int i = openedDirectories.Count - 1; i >= 0; i--)
                openedDirectories[i].Dispose();
        }
    }

    private static bool TryStatxTypeAt(int directoryDescriptor, string leaf, out int fileType)
    {
        fileType = 0;
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (int offset = 0; offset < 256; offset += sizeof(long))
                Marshal.WriteInt64(buffer, offset, 0);
            // STATX_TYPE (0x1) owns the S_IFMT classification consumed below; STATX_MODE (0x2)
            // owns permissions. Require both so the type bits are guaranteed filled (0ce1).
            const uint statxTypeAndMode = 0x00000001 | 0x00000002;
            if (StatxUnix(directoryDescriptor, leaf, 0x100, statxTypeAndMode, buffer) != 0)
                return false;
            uint mask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            if ((mask & statxTypeAndMode) != statxTypeAndMode) return false;
            ushort mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
            fileType = mode & UnixFileTypeMask;
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsUnixFileType(SafeFileHandle handle, int expectedType, out long length)
    {
        length = 0;
        if (FStatUnix(handle, out UnixFileStatus status) != 0 ||
            (status.Mode & UnixFileTypeMask) != expectedType)
        {
            return false;
        }
        length = status.Size;
        return true;
    }

    private static int OpenAtUnix(SafeFileHandle directory, string segment, int flags)
    {
        bool added = false;
        try
        {
            directory.DangerousAddRef(ref added);
            return OpenAtUnixNative(directory.DangerousGetHandle().ToInt32(), segment, flags, 0);
        }
        finally
        {
            if (added) directory.DangerousRelease();
        }
    }

    private static byte[]? ReadBoundedWindowsFile(string volumeRoot, string[] anchorSegments,
        string[] targetSegments, int maxBytes, bool missingAsEmpty)
    {
        using SafeFileHandle root = OpenWindowsMetadataFile(volumeRoot,
            WindowsReadAttributes | WindowsTraverse | WindowsSynchronize,
            WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
            IntPtr.Zero, WindowsOpenExisting,
            WindowsOpenReparsePoint | WindowsBackupSemantics, IntPtr.Zero);
        if (root.IsInvalid) return null;
        FileAttributes rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & FileAttributes.Directory) == 0 ||
            (rootAttributes & RejectedWindowsAttributes) != 0)
        {
            return null;
        }

        SafeFileHandle current = root;
        var openedDirectories = new List<SafeFileHandle>();
        try
        {
            int directoryCount = anchorSegments.Length + targetSegments.Length - 1;
            for (int i = 0; i < directoryCount; i++)
            {
                bool isAnchor = i < anchorSegments.Length;
                string segment = isAnchor
                    ? anchorSegments[i]
                    : targetSegments[i - anchorSegments.Length];
                SafeFileHandle? next = OpenWindowsRelative(current, segment, true,
                    out bool missing);
                if (next is null) return !isAnchor && missingAsEmpty && missing ? [] : null;
                openedDirectories.Add(next);
                FileAttributes attributes = File.GetAttributes(next);
                if ((attributes & FileAttributes.Directory) == 0 ||
                    (attributes & RejectedWindowsAttributes) != 0)
                {
                    return null;
                }
                current = next;
            }

            using SafeFileHandle? file = OpenWindowsRelative(current, targetSegments[^1],
                false, out bool finalMissing);
            if (file is null) return missingAsEmpty && finalMissing ? [] : null;
            FileAttributes fileAttributes = File.GetAttributes(file);
            if ((fileAttributes & (FileAttributes.Directory | RejectedWindowsAttributes)) != 0)
                return null;
            long length = RandomAccess.GetLength(file);
            if (length < 0 || length > maxBytes || length > int.MaxValue) return null;
            return ReadBoundedHandle(file, length);
        }
        finally
        {
            for (int i = openedDirectories.Count - 1; i >= 0; i--)
                openedDirectories[i].Dispose();
        }
    }

    private static SafeFileHandle? OpenWindowsRelative(SafeFileHandle directory,
        string segment, bool directoryEntry, out bool missing)
    {
        missing = false;
        IntPtr text = IntPtr.Zero;
        IntPtr namePointer = IntPtr.Zero;
        bool added = false;
        try
        {
            if (segment.Length > (ushort.MaxValue / sizeof(char)) - 1) return null;
            text = Marshal.StringToHGlobalUni(segment);
            var name = new WindowsUnicodeString
            {
                Length = checked((ushort)(segment.Length * sizeof(char))),
                MaximumLength = checked((ushort)((segment.Length + 1) * sizeof(char))),
                Buffer = text,
            };
            namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsUnicodeString>());
            Marshal.StructureToPtr(name, namePointer, fDeleteOld: false);
            directory.DangerousAddRef(ref added);
            var attributes = new WindowsObjectAttributes
            {
                Length = Marshal.SizeOf<WindowsObjectAttributes>(),
                RootDirectory = directory.DangerousGetHandle(),
                ObjectName = namePointer,
                Attributes = WindowsObjectCaseInsensitive,
            };
            uint access = WindowsReadAttributes | WindowsSynchronize |
                          (directoryEntry ? WindowsTraverse : WindowsReadData);
            uint options = WindowsOpenReparsePoint | WindowsSynchronousIoNonAlert |
                           (directoryEntry ? WindowsDirectoryFile : WindowsNonDirectoryFile);
            int status = OpenWindowsRelativeNative(out IntPtr rawHandle, access, ref attributes,
                out _, IntPtr.Zero, 0, WindowsShareRead | WindowsShareWrite | WindowsShareDelete,
                WindowsNtOpen, options, IntPtr.Zero, 0);
            if (status < 0)
            {
                uint value = unchecked((uint)status);
                missing = value is WindowsStatusNoSuchFile or WindowsStatusObjectNameNotFound or
                    WindowsStatusObjectPathNotFound;
                return null;
            }
            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            if (added) directory.DangerousRelease();
            if (namePointer != IntPtr.Zero) Marshal.FreeHGlobal(namePointer);
            if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
        }
    }

    private static byte[]? ReadBoundedHandle(SafeFileHandle handle, long length)
    {
        var bytes = new byte[(int)length];
        int read = 0;
        while (read < bytes.Length)
        {
            int count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
            if (count <= 0) return null;
            read += count;
        }
        Span<byte> extra = stackalloc byte[1];
        return RandomAccess.Read(handle, extra, length) == 0 ? bytes : null;
    }

    private const int UnixNoSuchFile = 2;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixDirectory = 0x4000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixOpenNonBlockingLinux = 0x800;
    private const int UnixOpenNoFollowLinux = 0x20000;
    private const int UnixOpenNoControllingTerminalLinux = 0x100;
    private const int UnixOpenCloseOnExecLinux = 0x80000;
    private const int UnixOpenDirectoryLinux = 0x10000;
    private const int UnixOpenNonBlockingBsd = 0x4;
    private const int UnixOpenNoFollowBsd = 0x100;
    private const int UnixOpenNoControllingTerminalMac = 0x20000;
    private const int UnixOpenCloseOnExecMac = 0x1000000;
    private const int UnixOpenDirectoryMac = 0x100000;

    private static bool IsUnixSymbolicLinkError(int error) =>
        OperatingSystem.IsLinux() ? error == 40 : OperatingSystem.IsMacOS() && error == 62;

    private const uint WindowsReadData = 0x1;
    private const uint WindowsTraverse = 0x20;
    private const uint WindowsReadAttributes = 0x80;
    private const uint WindowsSynchronize = 0x00100000;
    private const uint WindowsShareRead = 0x1;
    private const uint WindowsShareWrite = 0x2;
    private const uint WindowsShareDelete = 0x4;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsDirectoryFile = 0x1;
    private const uint WindowsSynchronousIoNonAlert = 0x20;
    private const uint WindowsNonDirectoryFile = 0x40;
    private const uint WindowsNtOpen = 1;
    private const uint WindowsObjectCaseInsensitive = 0x40;
    private const uint WindowsStatusNoSuchFile = 0xC000000F;
    private const uint WindowsStatusObjectNameNotFound = 0xC0000034;
    private const uint WindowsStatusObjectPathNotFound = 0xC000003A;
    private const FileAttributes RejectedWindowsAttributes =
        FileAttributes.ReparsePoint | FileAttributes.Device;

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct UnixFileStatus
    {
        [FieldOffset(0)] public int Flags;
        [FieldOffset(4)] public int Mode;
        [FieldOffset(8)] public uint Uid;
        [FieldOffset(12)] public uint Gid;
        [FieldOffset(16)] public long Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsIoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtUnixNative(int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxUnix(int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask,
        IntPtr status);

    [DllImport("System.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int FStatUnix(SafeFileHandle descriptor, out UnixFileStatus status);

    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int LStatUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixFileStatus status);

    [DllImport("System.Native", EntryPoint = "SystemNative_FcntlSetIsNonBlocking",
        SetLastError = true)]
    private static extern int SetUnixNonBlocking(SafeFileHandle descriptor, int isNonBlocking);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle OpenWindowsMetadataFile(string path,
        uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static extern int OpenWindowsRelativeNative(out IntPtr fileHandle,
        uint desiredAccess, ref WindowsObjectAttributes objectAttributes,
        out WindowsIoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes,
        uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer,
        uint eaLength);

    private static FileStream CreateGuardedFile(string path, byte[] content)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read, bufferSize: 4096, FileOptions.WriteThrough);
        try
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private sealed record TrackedSafetyScan(HashSet<string> UnsafeFilteredPaths,
        SubmoduleWorktreeCoverage? ExcludedSubmoduleWorktrees);

    /// <summary>Content filters can be non-idempotent, so comparing raw worktree bytes with the
    /// canonical blob after disabling helpers can otherwise be falsely clean. Stream the index
    /// directly into check-attr: no complete path manifest or attribute response is retained, so
    /// monorepo size cannot trip the generic 8 MiB process-output cap. Mode-160000 entries are
    /// collected at the same time so callers can disclose that child worktree dirt is excluded.</summary>
    private static TrackedSafetyScan? DiscoverTrackedSafety(string workspaceRoot,
        string gitExe, IReadOnlyDictionary<string, string?> neutralEnvironment,
        IReadOnlySet<string> executableDrivers)
    {
        Process? index = null;
        Process? attributes = null;
        try
        {
            var environment = StableGitEnvironment(neutralEnvironment);
            if (executableDrivers.Count > 0)
            {
                var (attrExe, attrArgs) = Invocation(gitExe,
                    GitArgs("check-attr -z --stdin filter"));
                attributes = Process.Start(CreateProcessStartInfo(
                    attrExe, workspaceRoot, attrArgs, environment));
                if (attributes is null) return null;
            }

            var (indexExe, indexArgs) = Invocation(gitExe,
                GitArgs("ls-files -z --cached --stage"));
            index = Process.Start(CreateProcessStartInfo(
                indexExe, workspaceRoot, indexArgs, environment));
            if (index is null)
            {
                if (attributes is not null) KillAndCut(attributes);
                return null;
            }
            index.StandardInput.Close();

            var unsafePaths = new HashSet<string>(StringComparer.Ordinal);
            var submodules = new SubmoduleCoverageAccumulator();
            (bool Valid, long Records, byte[] Digest) indexScan = default;
            (bool Valid, long Records, byte[] Digest) attributeScan = (true, 0, []);
            Exception? indexFailure = null;
            Exception? attributeFailure = null;

            var indexReader = new Thread(() =>
            {
                try
                {
                    indexScan = PumpTrackedIndex(
                        index.StandardOutput.BaseStream,
                        attributes?.StandardInput.BaseStream,
                        submodules.Add);
                }
                catch (Exception ex) { indexFailure = ex; }
                finally
                {
                    try { attributes?.StandardInput.Close(); } catch { /* pipe torn down */ }
                }
            })
            { IsBackground = true };
            Thread? attributeReader = null;
            if (attributes is not null)
            {
                attributeReader = new Thread(() =>
                {
                    try
                    {
                        attributeScan = ReadFilterAttributes(
                            attributes.StandardOutput.BaseStream, executableDrivers, unsafePaths);
                    }
                    catch (Exception ex) { attributeFailure = ex; }
                })
                { IsBackground = true };
            }
            var indexError = DrainThread(index.StandardError.BaseStream);
            Thread? attributeError = attributes is null
                ? null
                : DrainThread(attributes.StandardError.BaseStream);

            indexReader.Start();
            attributeReader?.Start();
            indexError.Start();
            attributeError?.Start();

            const int waitMs = 30000;
            const int drainMs = 3000;
            var waitClock = Stopwatch.StartNew();
            bool WaitWithinBudget(Process process)
            {
                int remaining = Math.Max(0, waitMs - (int)waitClock.ElapsedMilliseconds);
                return process.WaitForExit(remaining);
            }

            bool exited = WaitWithinBudget(index) &&
                          (attributes is null || WaitWithinBudget(attributes));
            if (!exited)
            {
                KillAndCut(index);
                if (attributes is not null) KillAndCut(attributes);
                return null;
            }

            var drainClock = Stopwatch.StartNew();
            bool JoinWithinBudget(Thread? thread)
            {
                if (thread is null) return true;
                int remaining = Math.Max(0, drainMs - (int)drainClock.ElapsedMilliseconds);
                return thread.Join(remaining);
            }

            bool drained = JoinWithinBudget(indexReader) &&
                           JoinWithinBudget(attributeReader) &&
                           JoinWithinBudget(indexError) &&
                           JoinWithinBudget(attributeError);
            if (!drained)
            {
                KillAndCut(index);
                if (attributes is not null) KillAndCut(attributes);
                return null;
            }

            if (index.ExitCode != 0 || (attributes is not null && attributes.ExitCode != 0) ||
                indexFailure is not null || attributeFailure is not null ||
                !indexScan.Valid || !attributeScan.Valid ||
                (attributes is not null &&
                 (indexScan.Records != attributeScan.Records ||
                  !indexScan.Digest.AsSpan().SequenceEqual(attributeScan.Digest))))
            {
                return null;
            }
            return new TrackedSafetyScan(unsafePaths, submodules.Build());
        }
        catch
        {
            if (index is not null) KillAndCut(index);
            if (attributes is not null) KillAndCut(attributes);
            return null;
        }
        finally
        {
            index?.Dispose();
            attributes?.Dispose();
        }
    }

    private const int MaxGitPathBytes = 128 * 1024 + 256;

    private static readonly byte[] NullPathTerminator = [0];

    private sealed class SubmoduleCoverageAccumulator
    {
        private const int MaxPaths = 8;
        private const int MaxUtf8Bytes = 512;
        private readonly List<string> _samplePaths = [];
        private int _sampleBytes;
        public int Count { get; private set; }

        public void Include(SubmoduleWorktreeCoverage coverage)
        {
            Count += coverage.Count;
            foreach (string path in coverage.SamplePaths) AddSample(path);
        }

        public void Add(string path)
        {
            Count++;
            AddSample(path);
        }

        private void AddSample(string path)
        {
            if (_samplePaths.Count >= MaxPaths) return;
            int bytes = Encoding.UTF8.GetByteCount(path) + 2;
            if (bytes > MaxUtf8Bytes - _sampleBytes) return;
            _samplePaths.Add(path);
            _sampleBytes += bytes;
        }

        public SubmoduleWorktreeCoverage? Build() => Count == 0
            ? null
            : new SubmoduleWorktreeCoverage(Count, _samplePaths,
                _samplePaths.Count < Count);
    }

    internal static (bool Valid, long Records, byte[] Digest) PumpTrackedIndex(Stream source,
        Stream? attributeInput, Action<string>? onSubmodule = null)
    {
        var read = new byte[8192];
        var record = new byte[MaxGitPathBytes];
        int length = 0;
        bool overflow = false;
        bool valid = true;
        bool canWrite = attributeInput is not null;
        long records = 0;
        using IncrementalHash? digest = attributeInput is null
            ? null
            : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        bool requireEveryPath = attributeInput is not null;

        int count;
        while ((count = source.Read(read, 0, read.Length)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                byte value = read[i];
                if (value != 0)
                {
                    if (length < record.Length) record[length++] = value;
                    else overflow = true;
                    continue;
                }

                if (overflow || length == 0 ||
                    !TryParseIndexEntry(record.AsSpan(0, length), out string? path,
                        out int pathOffset, out bool isSubmodule, out bool filterableBlob,
                        requireEveryPath))
                {
                    valid = false;
                }
                else
                {
                    if (isSubmodule) onSubmodule?.Invoke(path!);
                    // Gitlink entries are commit OIDs, never worktree blobs: Git does not run
                    // clean/process conversion on them. Every other tracked mode is checked,
                    // including symlinks: a symlink replaced by a regular worktree file (or a
                    // core.symlinks=false representation) can enter Git's check-in conversion.
                    if (attributeInput is null)
                    {
                        records++;
                    }
                    else if (filterableBlob)
                    {
                        records++;
                        digest!.AppendData(record, pathOffset, length - pathOffset);
                        digest.AppendData(NullPathTerminator);
                        try
                        {
                            if (canWrite)
                            {
                                attributeInput.Write(record, pathOffset, length - pathOffset);
                                attributeInput.WriteByte(0);
                            }
                        }
                        catch
                        {
                            valid = false;
                            canWrite = false;
                        }
                    }
                }
                length = 0;
                overflow = false;
            }
        }
        if (length != 0 || overflow) valid = false;
        return (valid, records, digest?.GetHashAndReset() ?? []);
    }

    private static bool TryParseIndexEntry(ReadOnlySpan<byte> record, out string? path,
        out int pathOffset, out bool isSubmodule, out bool filterableBlob,
        bool requireFilterPath)
    {
        path = null;
        pathOffset = 0;
        isSubmodule = false;
        filterableBlob = false;
        int tab = record.IndexOf((byte)'\t');
        if (tab < 12 || tab + 1 >= record.Length) return false;
        for (int i = 0; i < 6; i++)
        {
            if (record[i] is < (byte)'0' or > (byte)'7') return false;
        }
        if (record[6] != (byte)' ') return false;
        int oidEndRelative = record[7..tab].IndexOf((byte)' ');
        if (oidEndRelative < 4) return false;
        int oidEnd = 7 + oidEndRelative;
        int oidLength = oidEnd - 7;
        if (oidLength > 64 || oidEnd + 2 != tab ||
            record[oidEnd + 1] is < (byte)'0' or > (byte)'3')
        {
            return false;
        }
        for (int i = 7; i < oidEnd; i++)
        {
            byte value = record[i];
            bool hex = value is >= (byte)'0' and <= (byte)'9' or
                >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
            if (!hex) return false;
        }
        bool isGitlink = record[..6].SequenceEqual("160000"u8);
        isSubmodule = isGitlink && record[oidEnd + 1] == (byte)'0';
        filterableBlob = record[..6].SequenceEqual("100644"u8) ||
                         record[..6].SequenceEqual("100755"u8) ||
                         record[..6].SequenceEqual("120000"u8);
        if ((requireFilterPath && (filterableBlob || isGitlink)) || isSubmodule)
        {
            ReadOnlySpan<byte> pathBytes = record[(tab + 1)..];
            if (!TryDecodeUtf8(pathBytes, out path) || path is null ||
                !IsSafeRelativeGitPath(path))
            {
                return false;
            }
        }
        pathOffset = tab + 1;
        return true;
    }

    internal static (bool Valid, long Records, byte[] Digest) ReadFilterAttributes(Stream source,
        IReadOnlySet<string> executableDrivers, HashSet<string> unsafePaths)
    {
        var read = new byte[8192];
        var field = new byte[MaxGitPathBytes];
        int length = 0;
        int fieldIndex = 0;
        bool overflow = false;
        bool valid = true;
        long records = 0;
        string? path = null;
        string? attribute = null;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        int count;
        while ((count = source.Read(read, 0, read.Length)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                byte value = read[i];
                if (value != 0)
                {
                    if (length < field.Length) field[length++] = value;
                    else overflow = true;
                    continue;
                }

                string? decoded = null;
                if (overflow || !TryDecodeUtf8(field.AsSpan(0, length), out decoded) ||
                    decoded is null)
                {
                    valid = false;
                }
                else if (fieldIndex == 0)
                {
                    path = decoded;
                    if (!IsSafeRelativeGitPath(path)) valid = false;
                    digest.AppendData(field, 0, length);
                    digest.AppendData(NullPathTerminator);
                }
                else if (fieldIndex == 1)
                {
                    attribute = decoded;
                    if (attribute != "filter") valid = false;
                }
                else
                {
                    if (decoded.Length == 0 || decoded.Any(char.IsControl) ||
                        path is null || attribute != "filter")
                    {
                        valid = false;
                    }
                    else if (executableDrivers.Contains(decoded) && unsafePaths.Count < 64)
                    {
                        unsafePaths.Add(path);
                    }
                    records++;
                    path = attribute = null;
                }

                fieldIndex = (fieldIndex + 1) % 3;
                length = 0;
                overflow = false;
            }
        }
        if (length != 0 || overflow || fieldIndex != 0) valid = false;
        return (valid, records, digest.GetHashAndReset());
    }

    private static Thread DrainThread(Stream stream) => new(() =>
    {
        try { DrainToEof(stream); }
        catch { /* pipe torn down */ }
    })
    { IsBackground = true };

    private static void KillAndCut(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        CutPipes(process);
    }

    private static Dictionary<string, string?> CopyEnvironmentOverrides(
        IReadOnlyDictionary<string, string?>? source)
    {
        var copy = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (source is not null)
        {
            foreach (var (key, value) in source) copy[key] = value;
        }
        return copy;
    }

    private static void ClearGitRepositorySelection(Dictionary<string, string?> environment)
    {
        foreach (string variable in GitRepositorySelectionEnvironmentVariables)
            environment[variable] = null;
    }

    private static bool TryGetFilterDriver(string key, out string? driver, out bool executable)
    {
        driver = null;
        executable = false;
        const string prefix = "filter.";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string suffix in new[] { ".clean", ".process", ".required" })
        {
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            int length = key.Length - prefix.Length - suffix.Length;
            driver = length > 0 ? key.Substring(prefix.Length, length) : null;
            executable = suffix is ".clean" or ".process";
            return true;
        }
        return false;
    }

    private static bool IsSafeFilterDriver(string value)
    {
        if (value.Length is 0 or > 200) return false;
        return !value.Any(char.IsControl);
    }

    private static string? Run(string cwd, string args, string? standardInput = null) =>
        RunWithExecutable(GitExe.Value, cwd, args, standardInput);

    private static string? RunWithExecutable(string? gitExe, string cwd, string args,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        var r = RunWithExecutableResult(gitExe, cwd, args, standardInput,
            environmentOverrides);
        return r.Status == "ok" && !r.Truncated ? r.Output : null;
    }

    private static ProcessResult RunWithExecutableResult(string? gitExe, string cwd, string args,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        bool captureBytes = false,
        int maxOutputChars = 8 * 1024 * 1024,
        int waitMs = 10000,
        int drainMs = 2000)
    {
        if (gitExe is null) return new ProcessResult(null, "spawn_failed", false);
        var environment = StableGitEnvironment(environmentOverrides);
        string exe;
        string wrapped;
        try { (exe, wrapped) = Invocation(gitExe, GitArgs(args)); }
        catch { return new ProcessResult(null, "spawn_failed", false); }
        return RunProcessEx(exe, cwd, wrapped, standardInput: standardInput,
            environmentOverrides: environment, captureBytes: captureBytes,
            maxOutputChars: maxOutputChars, waitMs: waitMs, drainMs: drainMs);
    }

    private static Dictionary<string, string?> StableGitEnvironment(
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        var environment = CopyEnvironmentOverrides(environmentOverrides);
        // ProcessStartInfo begins with the parent environment. Add explicit removals for every
        // repository-selection variable the caller did not intentionally supply. Filter safety
        // discovery force-clears even supplied values; its validated runtime sandbox then supplies
        // the only accepted GIT_DIR / worktree / object / index locations.
        foreach (string variable in GitRepositorySelectionEnvironmentVariables)
        {
            if (!environment.ContainsKey(variable)) environment[variable] = null;
        }
        // Git's human diagnostics and no-final-newline marker are localized. Force the stable
        // machine locale after caller overrides so behavior cannot vary by host or test wrapper.
        environment["LC_ALL"] = "C";
        environment["LANG"] = "C";
        environment["LANGUAGE"] = "C";
        environment["GIT_OPTIONAL_LOCKS"] = "0";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_NO_LAZY_FETCH"] = "1";
        // GIT_ALLOW_PROTOCOL is a highest-precedence absolute allowlist. A single empty
        // component denies every transport even when hostile repository config sets
        // protocol.ext.allow=always or protocol.file.allow=always (which overrides the generic
        // protocol.allow=never fallback below on older Git versions).
        environment["GIT_ALLOW_PROTOCOL"] = ":";
        // Partial/promisor clones otherwise lazy-fetch missing objects during diff/cat-file,
        // launching transports, credential helpers, or configured remote helpers from a command
        // that is required to stay local and bounded. Missing objects fail honestly instead.
        return environment;
    }

    /// <summary>Content of workspace-relative <paramref name="relPath"/> at
    /// <paramref name="commit"/>, or null. For DELETED-file review honesty: the post-change
    /// index cannot know removed symbols; the base blob re-parsed in memory can. The path is
    /// translated to Git's tree-root domain before it travels through cat-file's stdin protocol.</summary>
    public static string? ShowFile(string workspaceRoot, string commit, string relPath) =>
        ShowFile(workspaceRoot, commit, relPath, GitExe.Value);

    public static string? ShowFile(string workspaceRoot, string commit, string relPath,
        int maxBlobBytes) =>
        ShowFile(workspaceRoot, commit, relPath, GitExe.Value, maxBlobBytes, 4000);

    public static string? ShowFile(string workspaceRoot, string commit, string relPath,
        int maxBlobBytes, int timeoutMs) =>
        ShowFile(workspaceRoot, commit, relPath, GitExe.Value, maxBlobBytes, timeoutMs);

    /// <summary>Reads a blob through cat-file's stdin protocol. Dynamic paths never appear in
    /// the command line, so a batch wrapper cannot expand percent variables or metacharacters.</summary>
    internal static string? ShowFile(string workspaceRoot, string commit, string relPath, string? gitExe)
        => ShowFile(workspaceRoot, commit, relPath, gitExe, 8 * 1024 * 1024, 4000);

    internal static string? ShowFile(string workspaceRoot, string commit, string relPath,
        string? gitExe, int maxBlobBytes) =>
        ShowFile(workspaceRoot, commit, relPath, gitExe, maxBlobBytes, 4000);

    internal static string? ShowFile(string workspaceRoot, string commit, string relPath,
        string? gitExe, int maxBlobBytes, int timeoutMs)
    {
        if (!IsHexCommit(commit)) return null;
        if (!IsSafeRelativeGitPath(relPath)) return null;
        if (gitExe is null || maxBlobBytes <= 0 || timeoutMs < 4) return null;
        // Git's :./path revision syntax resolves from the process prefix (cwd inside the
        // worktree), while :path always resolves from the repository root. This keeps the
        // public path domain workspace-relative without a second discovery process or a
        // root/subtree same-name collision.
        string treePath = "./" + relPath;
        string request = $"{commit}:{treePath}\n";
        var deadline = Stopwatch.StartNew();
        (int Wait, int Drain)? RemainingProcessBudget()
        {
            int remaining = timeoutMs - (int)Math.Min(int.MaxValue,
                deadline.ElapsedMilliseconds);
            if (remaining < 4) return null;
            int drain = Math.Max(1, remaining / 4);
            return (remaining - drain, drain);
        }
        // cat-file --batch streams a requested blob even when the consumer has already reached
        // its cap. Size-gate with batch-check first so an oversized local object cannot turn a
        // bounded review into a full-object drain or repeated process timeouts.
        if (RemainingProcessBudget() is not { } checkBudget) return null;
        var check = RunWithExecutableResult(gitExe, workspaceRoot,
            "cat-file --batch-check", request, maxOutputChars: 1024,
            waitMs: checkBudget.Wait, drainMs: checkBudget.Drain);
        if (check.Status != "ok" || check.Truncated ||
            !TryParseBatchHeader(check.Output, out _, out string type, out long size,
                out int contentOffset) || type != "blob" || contentOffset != check.Output!.Length ||
            size > maxBlobBytes)
        {
            return null;
        }
        // captureBytes mode interprets this runner parameter as a byte cap. Reserve a bounded
        // allowance for the cat-file header and trailing newline in addition to the blob itself.
        int captureBytes = (int)Math.Min(int.MaxValue, (long)maxBlobBytes + 512);
        if (RemainingProcessBudget() is not { } contentBudget) return null;
        var process = RunWithExecutableResult(gitExe, workspaceRoot, "cat-file --batch",
            request, captureBytes: true, maxOutputChars: captureBytes,
            waitMs: contentBudget.Wait, drainMs: contentBudget.Drain);
        return process.Status == "ok" && !process.Truncated
            ? ParseBatchBlob(process.OutputBytes.Span, maxBlobBytes)
            : null;
    }

    internal static string? ParseBatchBlob(string? output) =>
        output is null ? null : ParseBatchBlob(Encoding.UTF8.GetBytes(output));

    internal static string? ParseBatchBlob(byte[]? output) =>
        output is null ? null : ParseBatchBlob(output.AsSpan());

    private static string? ParseBatchBlob(ReadOnlySpan<byte> output) =>
        ParseBatchBlob(output, int.MaxValue);

    private static string? ParseBatchBlob(ReadOnlySpan<byte> output, int maxBlobBytes)
    {
        int lf = output.IndexOf((byte)'\n');
        if (lf < 0) return null;
        ReadOnlySpan<byte> headerBytes = output[..lf];
        if (headerBytes.Length > 0 && headerBytes[^1] == (byte)'\r') headerBytes = headerBytes[..^1];
        foreach (byte value in headerBytes)
        {
            if (value >= 0x80) return null;
        }
        string[] fields = Encoding.ASCII.GetString(headerBytes)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || !IsHexCommit(fields[0]) || fields[1] != "blob" ||
            !long.TryParse(fields[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long size) ||
            size < 0 || size > int.MaxValue || size > maxBlobBytes)
        {
            return null;
        }
        int contentOffset = lf + 1;
        if ((long)contentOffset + size + 1 != output.Length || output[^1] != (byte)'\n')
        {
            return null;
        }
        // Source blobs in older .NET repositories are not universally UTF-8. Framing and the
        // batch header remain strict bytes; content is deliberately replacement-decoded so
        // deletion review can still recover surrounding syntax instead of losing the file.
        return new UTF8Encoding(false, false).GetString(output.Slice(contentOffset, (int)size));
    }

    private static bool TryParseBatchHeader(string? output, out string oid, out string type,
        out long size, out int contentOffset)
    {
        oid = type = "";
        size = 0;
        contentOffset = 0;
        if (output is null) return false;
        int lf = output.IndexOf('\n');
        if (lf < 0) return false;
        string header = output[..lf].TrimEnd('\r');
        string[] fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || !IsHexCommit(fields[0]) ||
            !long.TryParse(fields[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out size) || size < 0)
        {
            return false;
        }
        oid = fields[0];
        type = fields[1];
        contentOffset = lf + 1;
        return true;
    }

    // -c core.fsmonitor=false: on fsmonitor/Scalar-enabled monoliths git auto-spawns a background
    // daemon; our read-only queries never need it, and the daemon INHERITING our redirected pipes is
    // exactly the hang RunProcessEx guards against. core.useBuiltinFSMonitor covers the Scalar-era
    // microsoft/git 2.33-2.36 builds, which gate the experimental daemon on THAT key. Unknown/unused
    // -c keys are harmless on every git in the wild.
    // -c core.quotepath=false: with quotepath (git's default) non-ASCII path BYTES are
    // octal-escaped and double-quoted in diff/dirt output — the UTF-8 pipe decoding above
    // would then hand us "L\303\244b.cs" literals instead of paths. Disabled, git writes raw
    // UTF-8 bytes, which the explicit UTF-8 pipe encoding decodes exactly (review F1 pair).
    // -c submodule.recurse=false plus explicit --ignore-submodules=dirty keeps parent review from
    // entering child worktrees, where child-local clean/process filters could execute. Gitlink
    // pointer changes remain visible and ReviewDiff reports the excluded child-worktree coverage.
    // -c protocol.allow=never prevents any repository-controlled URL/protocol configuration from
    // turning these local inspection commands into transport or helper execution.
    // -c diff.autoRefreshIndex=false is required for the review-only contract: Git's automatic
    // refresh rewrites .git/index even with GIT_OPTIONAL_LOCKS=0. The raw parser recognizes and
    // drops only the structurally exact same-mode M/no-patch stat-cache shape instead.
    private static string GitArgs(string args) =>
        "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false -c core.quotepath=false " +
        "-c submodule.recurse=false -c protocol.allow=never -c diff.autoRefreshIndex=false " +
        "-c diff.orderFile=/dev/null " + args;

    /// <summary>Hang-proof process runner (field bug: repo_overview froze inside
    /// StandardOutput.ReadToEnd on a work monolith). The old shape — synchronous ReadToEnd BEFORE
    /// WaitForExit — could block FOREVER, making its own timeout unreachable: (a) sequential sync
    /// reads deadlock when stderr fills the pipe first; (b) git can spawn a background daemon
    /// (fsmonitor--daemon) that inherits the redirected stdout handle, so the pipe never reaches
    /// EOF even after the command itself exits. Now: both streams read ASYNC, WaitForExit(timeout)
    /// runs first (it deliberately does not wait for drain), then a bounded drain-grace — a
    /// lingering pipe-holder degrades to null ("git unavailable") instead of hanging the server.
    /// Stdin is redirected and either populated explicitly or closed, so no child can inherit or
    /// consume the MCP stdio channel.</summary>
    internal static string? RunProcess(string exe, string cwd, string args, int waitMs = 10000, int drainMs = 2000)
    {
        var r = RunProcessEx(exe, cwd, args, waitMs, drainMs);
        return r.Status == "ok" && !r.Truncated ? r.Output : null;
    }

    /// <summary>Outcome of a bounded process run. Status: "ok" | "spawn_failed" | "timed_out"
    /// (WaitForExit guard) | "drain_timed_out" | "input_failed" | "output_failed" |
    /// "exit_nonzero".
    /// Truncated: stdout exceeded the cap (output is the capped prefix) — field feedback: bounded
    /// TIME is not enough, a runaway subprocess can also produce megabytes.</summary>
    internal sealed record ProcessResult(string? Output, string Status, bool Truncated,
        ReadOnlyMemory<byte> OutputBytes = default);

    private static ProcessStartInfo CreateProcessStartInfo(string exe, string cwd, string args,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            throw new DirectoryNotFoundException("Process working directory is unavailable.");
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = Path.GetFullPath(cwd),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
            StandardInputEncoding = new UTF8Encoding(false, true),
        };
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                if (value is null) psi.Environment.Remove(name);
                else psi.Environment[name] = value;
            }
        }
        psi.Environment.Remove("GIT_DIFF_OPTS");
        return psi;
    }

    internal static ProcessResult RunProcessEx(string exe, string cwd, string args,
        int waitMs = 10000, int drainMs = 2000, int maxOutputChars = 8 * 1024 * 1024,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        bool captureBytes = false)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            return new ProcessResult(null, "spawn_failed", false);
        Process? child = null;
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = Path.GetFullPath(cwd),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Git emits paths as UTF-8 BYTES; without an explicit encoding, .NET decodes
                // redirected output with the host CONSOLE CODEPAGE (CP437/CP850 on most Windows
                // boxes), mangling every non-ASCII path (review, reproduced: 'Ünïcode Dirt.cs'
                // came back as '├£n├»code…', File.Exists false — and the worktree reconcile,
                // which has NO watcher backstop, silently LOST the file while reporting
                // success). UTF-8 on both pipes; pairs with core.quotepath=false in GitArgs.
                StandardOutputEncoding = new System.Text.UTF8Encoding(false, true),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false, true),
                StandardInputEncoding = new System.Text.UTF8Encoding(false, true),
            };
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";   // read-only queries must not take index locks
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";  // never wait on interactive input
            if (environmentOverrides is not null)
            {
                foreach (var (name, value) in environmentOverrides)
                {
                    if (value is null) psi.Environment.Remove(name);
                    else psi.Environment[name] = value;
                }
            }
            // Git documents GIT_DIFF_OPTS as overriding --unified on the command line. Letting an
            // inherited value broaden hunks would make review symbol selection host-dependent.
            psi.Environment.Remove("GIT_DIFF_OPTS");
            child = Process.Start(psi);
            if (child is null) return new ProcessResult(null, "spawn_failed", false);
            Process p = child;
            // Dedicated background reader THREADS, not Task continuations: under a saturated thread
            // pool (heavy parallel load) async drains can miss a small grace window and spuriously
            // report failure — a pool-independent thread drains the instant the pipe closes. Reads
            // are BOUNDED: past the cap we keep draining (so the child never blocks on a full pipe)
            // but discard, marking Truncated.
            string stdout = "";
            ReadOnlyMemory<byte> stdoutBytes = default;
            bool truncated = false;
            Exception? stdoutFailure = null;
            bool stdoutValidUtf8 = true;
            var outReader = new Thread(() =>
            {
                try
                {
                    if (captureBytes)
                        (stdoutBytes, truncated) = ReadBytesBounded(
                            p.StandardOutput.BaseStream, maxOutputChars);
                    else
                        (stdout, truncated, stdoutValidUtf8) = ReadUtf8Bounded(
                            p.StandardOutput.BaseStream, maxOutputChars);
                }
                catch (Exception ex) { stdoutFailure = ex; }
            })
            { IsBackground = true };
            var errReader = new Thread(() =>
            {
                try { DrainToEof(p.StandardError.BaseStream); }
                catch { /* pipe torn down */ }
            })
            { IsBackground = true };
            outReader.Start();
            errReader.Start();

            Exception? stdinFailure = null;
            Thread? inWriter = null;
            if (standardInput is null)
            {
                p.StandardInput.Close();
            }
            else
            {
                inWriter = new Thread(() =>
                {
                    try { p.StandardInput.Write(standardInput); }
                    catch (Exception ex) { stdinFailure = ex; }
                    finally
                    {
                        try { p.StandardInput.Close(); } catch { /* pipe torn down */ }
                    }
                })
                { IsBackground = true };
                inWriter.Start();
            }

            if (!p.WaitForExit(waitMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                CutPipes(p);
                return new ProcessResult(null, "timed_out", false);
            }
            var drainClock = Stopwatch.StartNew();
            bool JoinWithinGrace(Thread? thread)
            {
                if (thread is null) return true;
                int remaining = Math.Max(0, drainMs - (int)drainClock.ElapsedMilliseconds);
                return thread.Join(remaining);
            }
            bool inputDone = JoinWithinGrace(inWriter);
            bool outputDone = JoinWithinGrace(outReader);
            bool errorDone = JoinWithinGrace(errReader);
            if (!inputDone || !outputDone || !errorDone)
            {
                // A grandchild still holds the pipe. Degrade — but first reap what we can and CUT
                // OUR READ ENDS: disposing the base streams aborts the blocked reads in ~20ms
                // (review-measured; the readers' catches swallow the ObjectDisposedException).
                // Without this, every degraded call leaked 2 blocked threads + 2 pipe handles for
                // the foreign holder's lifetime — a slow leak on a long-running server.
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                CutPipes(p);
                return new ProcessResult(null, "drain_timed_out", false);
            }
            if (stdinFailure is not null) return new ProcessResult(null, "input_failed", false);
            if (stdoutFailure is not null) return new ProcessResult(null, "output_failed", false);
            if (!captureBytes && !stdoutValidUtf8)
                return new ProcessResult(null, "output_failed", truncated);
            return p.ExitCode == 0
                ? new ProcessResult(captureBytes ? null : stdout, "ok", truncated, stdoutBytes)
                : new ProcessResult(null, "exit_nonzero", truncated);
        }
        catch
        {
            if (child is not null)
            {
                try { child.Kill(entireProcessTree: true); } catch { /* best effort */ }
                CutPipes(child);
            }
            return new ProcessResult(null, "spawn_failed", false); // exe missing, spawn failure, etc.
        }
        finally
        {
            child?.Dispose();
        }
    }

    private static void CutPipes(Process p)
    {
        try { p.StandardInput.BaseStream.Dispose(); } catch { /* aborting a blocked write */ }
        try { p.StandardOutput.BaseStream.Dispose(); } catch { /* aborting a blocked read */ }
        try { p.StandardError.BaseStream.Dispose(); } catch { /* aborting a blocked read */ }
    }

    /// <summary>Incrementally decodes strict UTF-8 while retaining at most maxChars. The decoder
    /// continues through EOF after the cap, and a malformed sequence switches to raw draining, so
    /// neither large nor invalid output can strand the child behind a full pipe.</summary>
    internal static (string Text, bool Truncated, bool ValidUtf8) ReadUtf8Bounded(
        Stream stream, int maxChars)
    {
        maxChars = Math.Max(0, maxChars);
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[4096];
        var kept = new StringBuilder(Math.Min(maxChars, 64 * 1024));
        bool truncated = false;
        bool valid = true;

        void Retain(ReadOnlySpan<char> decoded)
        {
            int take = Math.Min(maxChars - kept.Length, decoded.Length);
            if (take > 0 && take < decoded.Length &&
                char.IsHighSurrogate(decoded[take - 1]) && char.IsLowSurrogate(decoded[take]))
            {
                take--;
            }
            if (take > 0) kept.Append(decoded[..take]);
            if (take < decoded.Length) truncated = true;
        }

        int read;
        while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
        {
            if (!valid) continue;
            int byteOffset = 0;
            while (byteOffset < read)
            {
                try
                {
                    decoder.Convert(bytes, byteOffset, read - byteOffset,
                        chars, 0, chars.Length, flush: false,
                        out int bytesUsed, out int charsUsed, out _);
                    if (charsUsed > 0) Retain(chars.AsSpan(0, charsUsed));
                    byteOffset += bytesUsed;
                    if (bytesUsed == 0 && charsUsed == 0)
                    {
                        valid = false;
                        break;
                    }
                }
                catch (DecoderFallbackException)
                {
                    valid = false;
                    break;
                }
            }
        }

        if (valid)
        {
            try
            {
                decoder.Convert(Array.Empty<byte>(), 0, 0, chars, 0, chars.Length, flush: true,
                    out _, out int charsUsed, out _);
                if (charsUsed > 0) Retain(chars.AsSpan(0, charsUsed));
            }
            catch (DecoderFallbackException)
            {
                valid = false;
            }
        }
        return (kept.ToString(), truncated, valid);
    }

    private static (ReadOnlyMemory<byte> Bytes, bool Truncated) ReadBytesBounded(
        Stream stream, int maxBytes)
    {
        maxBytes = Math.Max(0, maxBytes);
        using var kept = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[8192];
        bool truncated = false;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int room = maxBytes - (int)kept.Length;
            int take = Math.Min(room, read);
            if (take > 0) kept.Write(buffer, 0, take);
            if (take != read) truncated = true;
        }
        if (!kept.TryGetBuffer(out ArraySegment<byte> segment) || segment.Array is null)
            return (ReadOnlyMemory<byte>.Empty, truncated);
        return (new ReadOnlyMemory<byte>(segment.Array, segment.Offset, (int)kept.Length), truncated);
    }

    private static void DrainToEof(Stream stream)
    {
        var buffer = new byte[8192];
        while (stream.Read(buffer, 0, buffer.Length) > 0) { }
    }
}
