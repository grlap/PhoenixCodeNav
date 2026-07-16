using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Owns: the shared test-workspace teardown — scoped SQLite pool release (kae) followed by a
/// best-effort recursive delete. Replaces the per-class SqliteConnection.ClearAllPools() calls,
/// which were process-GLOBAL: under parallel classes one test's cleanup could invalidate a
/// concurrently running test's pooled reader at the rent boundary (rqek —
/// ObjectDisposedException on the SQLitePCL handle mid-query). Clearing is scoped to the index
/// databases that actually live under the workspace being deleted, so sibling tests can no
/// longer interfere through the pool, by construction.
/// Deliberately does not own: assertions — cleanup stays best-effort exactly like the bodies it
/// replaced (watchers and in-flight non-SQLite handles can still hold a temp dir briefly);
/// Batch49PoolScopingTests owns the deterministic canary that proves the locks it IS
/// responsible for are gone.
/// </summary>
internal static class TestWorkspaceCleanup
{
    /// <summary>Clear the pooled reader handles of every index database under root. Reparse
    /// points are skipped (tests create junctions; following one could walk out of the temp
    /// root or loop) and enumeration races with concurrent deletes are tolerated.</summary>
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
            };
            foreach (string db in Directory.EnumerateFiles(root, "*.db", options))
            {
                IndexQueries.ClearPoolsFor(db);
            }
        }
        catch (IOException) { /* enumeration raced a concurrent delete; nothing left to clear */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>Scoped pool release + best-effort recursive delete of a test workspace.</summary>
    internal static void DeleteWorkspace(string root)
    {
        ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); }
        catch { /* windows locks (watchers, in-flight handles); temp dirs are best-effort */ }
    }
}
