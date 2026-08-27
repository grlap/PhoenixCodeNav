using System.Diagnostics;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Owns: the shared test-workspace teardown — scoped SQLite pool release (kae) followed by a
/// bounded no-follow recursive delete. Replaces the per-class SqliteConnection.ClearAllPools() calls,
/// which were process-GLOBAL: under parallel classes one test's cleanup could invalidate a
/// concurrently running test's pooled reader at the rent boundary (rqek —
/// ObjectDisposedException on the SQLitePCL handle mid-query). Clearing is scoped to the index
/// databases that actually live under the workspace being deleted, so sibling tests can no
/// longer interfere through the pool, by construction.
/// Deliberately does not own: assertions — cleanup stays best-effort because watchers and
/// in-flight non-SQLite handles can still hold a temp dir briefly. It does own clearing the
/// read-only attribute Git gives loose objects on Windows; otherwise every successful Git
/// fixture leaves its .git/objects tree behind. Batch49PoolScopingTests owns deterministic
/// canaries for both the pooled-handle and read-only-object cases.
/// </summary>
internal static class TestWorkspaceCleanup
{
    private const int MaxTraversalDepth = 256;

    /// <summary>Create a directory reparse point for containment canaries. Prefer the managed
    /// symlink API, then fall back to a Windows junction so ordinary non-elevated Windows test
    /// hosts exercise the same no-follow cleanup branch without requiring Developer Mode.</summary>
    internal static bool TryCreateDirectoryLink(string link, string target, out string? failure,
        bool forceWindowsJunctionFallback = false)
    {
        string? symlinkFailureMessage = null;
        if (!forceWindowsJunctionFallback)
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
                bool created = new DirectoryInfo(link).LinkTarget is not null;
                failure = created ? null : "managed directory-link creation returned no link target";
                return created;
            }
            catch (Exception ex) when (OperatingSystem.IsWindows())
            {
                symlinkFailureMessage = ex.Message;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }
        }
        else if (!OperatingSystem.IsWindows())
        {
            failure = "the forced junction fallback is available only on Windows";
            return false;
        }
        else
        {
            symlinkFailureMessage = "managed symbolic-link creation was deliberately bypassed";
        }

        try
        {
            if (!IsSafeJunctionArgument(link) || !IsSafeJunctionArgument(target))
            {
                failure = $"symbolic link failed ({symlinkFailureMessage}); junction paths " +
                    "must be absolute and contain no cmd.exe metacharacters";
                return false;
            }
            using Process process = Process.Start(new ProcessStartInfo
            {
                // Resolve the platform shell absolutely and strictly validate the only two
                // interpolated arguments. cmd.exe consumes the outer quote pair and the inner
                // plain quotes; backslash-escaped quotes would be passed to mklink literally.
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /s /c \"mklink /J \"{link}\" \"{target}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
            })!;
            Task<string> stderrRead = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                failure = $"symbolic link failed ({symlinkFailureMessage}); junction creation timed out";
                return false;
            }

            string stderr = stderrRead.GetAwaiter().GetResult().Trim();
            if (process.ExitCode == 0 && new DirectoryInfo(link).LinkTarget is not null)
            {
                failure = null;
                return true;
            }

            failure = $"symbolic link failed ({symlinkFailureMessage}); junction creation " +
                $"exited {process.ExitCode}" +
                (string.IsNullOrWhiteSpace(stderr) ? "" : $": {stderr}");
            return false;
        }
        catch (Exception junctionFailure)
        {
            failure = $"symbolic link failed ({symlinkFailureMessage}); junction failed " +
                $"({junctionFailure.Message})";
            return false;
        }
    }

    private static bool IsSafeJunctionArgument(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        ReadOnlySpan<char> forbidden = ['\"', '\r', '\n', '&', '|', '<', '>', '^', '%', '!', '(', ')'];
        return path.AsSpan().IndexOfAny(forbidden) < 0;
    }

    /// <summary>Clear the pooled reader handles of every index database under root. Test
    /// fixtures use .db, .sqlite, and .sqlite3 leaves; all three must be covered. Reparse points
    /// are skipped (tests create junctions; following one could walk out of the temp root or
    /// loop) and enumeration races with concurrent deletes are tolerated.</summary>
    internal static void ClearIndexPools(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            foreach (string pattern in new[] { "*.db", "*.sqlite", "*.sqlite3" })
            {
                foreach (string db in Directory.EnumerateFiles(root, pattern, options))
                    IndexQueries.ClearPoolsFor(db);
            }
        }
        catch (IOException) { /* enumeration raced a concurrent delete; nothing left to clear */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>Scoped pool release plus bounded, no-follow deletion of a test workspace.
    /// Read-only entries are normalized before removal because Windows Git loose objects use
    /// that attribute and Directory.Delete(recursive: true) refuses to remove them.</summary>
    internal static void DeleteWorkspace(string root)
    {
        ClearIndexPools(root);
        Exception? finalFailure = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (!Directory.Exists(root) && !File.Exists(root))
                return;

            try
            {
                DeleteTreeNoFollow(root, depth: 0);
                return;
            }
            catch (Exception ex)
            {
                finalFailure = ex;
                if (ex is not IOException and not UnauthorizedAccessException)
                    break;
                if (attempt < 39)
                    Thread.Sleep(25);
            }
        }

        Console.Error.WriteLine(
            $"Test cleanup could not remove '{root}' after bounded retries: " +
            $"{finalFailure?.GetType().Name}: {finalFailure?.Message}");
    }

    private static void DeleteTreeNoFollow(string path, int depth)
    {
        if (depth > MaxTraversalDepth)
            throw new IOException(
                $"Test cleanup traversal exceeded {MaxTraversalDepth} levels at '{path}'.");

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteEntry(path, attributes);
            return;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            DeleteEntry(path, attributes);
            return;
        }

        // Snapshot children before removal. Deleting while a lazy directory enumeration is
        // active can skip entries on Windows and force a complete retry of a large Git tree.
        foreach (string child in Directory.GetFileSystemEntries(path))
            DeleteTreeNoFollow(child, depth + 1);

        ClearReadOnly(path, attributes);
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteEntry(string path, FileAttributes attributes)
    {
        // Attribute mutation APIs can resolve a symlink/junction to its target. Delete the
        // reparse entry itself without trying to normalize attributes outside the workspace.
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            ClearReadOnly(path, attributes);
        if ((attributes & FileAttributes.Directory) != 0)
            Directory.Delete(path, recursive: false);
        else
            File.Delete(path);
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}
