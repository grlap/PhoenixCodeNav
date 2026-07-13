using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Batch 49 (kae/rqek): scoped SQLite pool clearing.
/// kae  — replace process-global SqliteConnection.ClearAllPools() with per-database
///        IndexQueries.ClearPoolsFor: pools are keyed by connection string, and the only pooled
///        connections in the product are IndexQueries' two read variants, so clearing those two
///        strings for ONE path is complete for that database and invisible to every other.
/// rqek — the global clear was observed invalidating a SIBLING database's pooled connection at
///        the rent boundary under parallel tests (ObjectDisposedException on the SQLitePCL
///        handle mid-query in Batch39). Scoping makes that interference structurally
///        impossible, not merely less likely.
/// The lock-based observables below are deterministic: an idle pooled read handle holds the db
/// file open on Windows, so File.Delete throws while the pool retains it and succeeds once the
/// pool for THAT database is cleared. No race needs to be reproduced.
/// </summary>
public class Batch49PoolScopingTests
{
    /// <summary>Build a minimal indexed workspace and park one idle pooled read handle for it
    /// (open a pooled IndexQueries, run a query, dispose — the native handle returns to the
    /// pool instead of closing).</summary>
    private static string BuildWorkspaceWithParkedPooledReader(string tag)
    {
        string root = Directory.CreateTempSubdirectory($"codenav-49-{tag}").FullName;
        string proj = Path.Combine(root, "Lib");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(proj, "Thing.cs"),
            "namespace Lib { public class Thing { public void Act() { } } }");
        string dbPath = IndexBuilder.DefaultDbPath(root);
        IndexBuilder.Build(root, dbPath);
        using (var q = new IndexQueries(dbPath))
        {
            _ = q.SearchSymbols("Thing", "exact", null, 1);
        }
        return root;
    }

    [Fact]
    public void ClearPoolsForIsScopedToItsOwnDatabase()
    {
        // The lock-based observable encodes WINDOWS sharing semantics (SQLite's win32 VFS opens
        // without FILE_SHARE_DELETE, so deleting a pooled-open db throws). On Linux, unlinking
        // an open file succeeds — the scope assertion is unobservable this way, so skip, per
        // the suite's platform-guard convention.
        if (!OperatingSystem.IsWindows()) return;
        string rootA = BuildWorkspaceWithParkedPooledReader("a");
        string rootB = BuildWorkspaceWithParkedPooledReader("b");
        string dbA = IndexBuilder.DefaultDbPath(rootA);
        string dbB = IndexBuilder.DefaultDbPath(rootB);
        try
        {
            // Clearing A must NOT release B's parked handle: B's file stays locked, so this
            // delete throws. (Reintroduction proof: swap ClearPoolsFor's body back to
            // ClearAllPools and this assertion goes red — B's handle would be freed too.)
            IndexQueries.ClearPoolsFor(dbA);
            Assert.ThrowsAny<IOException>(() => File.Delete(dbB));

            // Clearing B itself releases exactly that handle: the same delete now succeeds.
            IndexQueries.ClearPoolsFor(dbB);
            File.Delete(dbB); // must not throw
            Assert.False(File.Exists(dbB));

            // And A's own clear (already done) released A's handle: A deletes cleanly too.
            File.Delete(dbA);
            Assert.False(File.Exists(dbA));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(rootA);
            TestWorkspaceCleanup.DeleteWorkspace(rootB);
        }
    }

    [Fact]
    public void ClearPoolsForCoversThePinnedReviewSnapshotVariant()
    {
        // The review snapshot pins a deferred read transaction on a PRIVATE-cache pooled
        // connection — a distinct connection string, hence a distinct pool. ClearPoolsFor must
        // enumerate that variant too, or review-heavy tests would leak exactly the handle class
        // that motivated kae.
        string root = BuildWorkspaceWithParkedPooledReader("pin");
        string dbPath = IndexBuilder.DefaultDbPath(root);
        try
        {
            using (var pinned = new IndexQueries(dbPath, pinReadSnapshot: true))
            {
                _ = pinned.ReadMetadata();
            }
            IndexQueries.ClearPoolsFor(dbPath);
            File.Delete(dbPath); // both variants cleared -> no surviving native handle
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void ClearPoolsForUnifiesPathSpellings()
    {
        // Pools are keyed by the EXACT connection string. A reader parked via one spelling
        // (forward slashes — how git reports worktree paths) must be cleared by a clear issued
        // via another (backslashes — how tests and Path.Combine spell the same file), or the
        // surviving handle blocks the next atomic install ("the staged index could not be
        // atomically installed" — the kae regression Batch41's seed/reconcile test caught).
        // ReadConnectionString canonicalizes DataSource with Path.GetFullPath for BOTH sides.
        string root = BuildWorkspaceWithParkedPooledReader("spell");
        string dbPath = IndexBuilder.DefaultDbPath(root);
        try
        {
            using (var slashed = new IndexQueries(dbPath.Replace('\\', '/')))
            {
                _ = slashed.SearchSymbols("Thing", "exact", null, 1);
            }
            IndexQueries.ClearPoolsFor(dbPath); // canonical spelling clears BOTH parked readers
            File.Delete(dbPath); // must not throw — no surviving handle under any spelling
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void WorkspaceCleanupReleasesEveryIndexLockUnderTheRoot()
    {
        // The canary the old Cleanups never had: every per-class Cleanup swallows delete
        // failures ("windows locks"), so a missed pool variant would leak temp dirs silently
        // forever. This asserts the delete actually happens after a scoped clear.
        string root = BuildWorkspaceWithParkedPooledReader("canary");
        TestWorkspaceCleanup.ClearIndexPools(root);
        Directory.Delete(root, recursive: true); // must not throw
        Assert.False(Directory.Exists(root));
    }
}
