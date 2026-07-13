using System.Diagnostics;
using CodeNav.Core;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

[CollectionDefinition("Watcher timing isolation", DisableParallelization = true)]
public sealed class WatcherTimingIsolationCollection { }

/// <summary>
/// Regression coverage for review batch 3: PhoenixCodeNav-mkf (directory renames must
/// trigger a sweep), 6d2 (watcher Dispose is race-safe), eot (reparse-point exclusion),
/// mz6 (IndexManager Dispose is race-safe).
/// </summary>
[Collection("Watcher timing isolation")]
public class WatcherTests
{
    [Fact]
    public void DirectoryRenameTriggersSweepNotSilentDrop()
    {
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-watch").FullName);
        try
        {
            string sub = Path.Combine(root, "Feature");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "Thing.cs"), "class Thing {}");

            int batches = 0, sweeps = 0;
            using (new WorkspaceWatcher(root,
                       _ => Interlocked.Increment(ref batches),
                       () => Interlocked.Increment(ref sweeps)))
            {
                Thread.Sleep(300); // let the watcher settle
                Directory.Move(sub, Path.Combine(root, "Renamed"));

                var sw = Stopwatch.StartNew();
                while (Volatile.Read(ref sweeps) == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(50);
                }
            }

            Assert.True(sweeps > 0,
                "directory rename must escalate to a detect-all sweep (mkf: was silently dropped)");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DottedNameDirectoryDeleteTriggersSweep()
    {
        // .NET project folders are dotted (Acme.Payments); the old extension heuristic
        // misclassified them as files and dropped the delete. They must sweep.
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-dotted").FullName);
        try
        {
            string proj = Path.Combine(root, "Acme.Payments");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "Service.cs"), "class Service {}");

            int sweeps = 0;
            using (new WorkspaceWatcher(root, _ => { }, () => Interlocked.Increment(ref sweeps)))
            {
                Thread.Sleep(600); // let the background known-dirs seed + FSW settle
                Directory.Delete(proj, recursive: true);

                var sw = Stopwatch.StartNew();
                while (Volatile.Read(ref sweeps) == 0 && sw.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(50);
                }
            }
            Assert.True(sweeps > 0, "deleting a dotted project directory must trigger a sweep");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExtensionlessFileDeleteDoesNotTriggerSweep()
    {
        // Deleting LICENSE/Dockerfile (no extension, not a known dir) must NOT force a sweep.
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-lic").FullName);
        try
        {
            string license = Path.Combine(root, "LICENSE");
            File.WriteAllText(license, "MIT");

            int sweeps = 0;
            using (new WorkspaceWatcher(root, _ => { }, () => Interlocked.Increment(ref sweeps)))
            {
                Thread.Sleep(600);
                File.Delete(license);
                Thread.Sleep(1500); // well past the 600ms debounce
            }
            Assert.Equal(0, Volatile.Read(ref sweeps));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AddingAFileDoesNotTriggerSweep()
    {
        // A directory 'Changed' (mtime bump when a child is added) must not escalate to a
        // full sweep — the child's own event covers it.
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-add").FullName);
        try
        {
            string lib = Path.Combine(root, "Lib");
            Directory.CreateDirectory(lib);

            int sweeps = 0, batches = 0;
            using (new WorkspaceWatcher(root,
                       _ => Interlocked.Increment(ref batches),
                       () => Interlocked.Increment(ref sweeps)))
            {
                Thread.Sleep(600);
                File.WriteAllText(Path.Combine(lib, "New.cs"), "class New {}");
                Thread.Sleep(1500);
            }
            Assert.Equal(0, Volatile.Read(ref sweeps));
            Assert.True(Volatile.Read(ref batches) > 0, "the added .cs file should still produce a per-file batch");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DisposeIsSafeUnderInFlightEvents()
    {
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-watch2").FullName);
        try
        {
            // Repeatedly create a watcher, generate a burst of events, and dispose racing
            // the debounce timer. A disposed-timer ArmDebounce would throw on a threadpool
            // thread and crash the run.
            for (int i = 0; i < 8; i++)
            {
                var watcher = new WorkspaceWatcher(root, _ => { }, () => { });
                for (int j = 0; j < 25; j++)
                {
                    File.WriteAllText(Path.Combine(root, $"f{i}_{j}.cs"), "x");
                }
                watcher.Dispose();
                watcher.Dispose(); // idempotent
            }
            Thread.Sleep(200); // give any stray threadpool callbacks time to (not) throw
            Assert.True(true);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

public class ReparsePointTests
{
    [Fact]
    public void IsReparsePointFalseForNormalAndAbsentPaths()
    {
        string f = Path.GetTempFileName();
        try
        {
            Assert.False(WorkspacePaths.IsReparsePoint(f));
        }
        finally
        {
            File.Delete(f);
        }
        Assert.False(WorkspacePaths.IsReparsePoint(Path.Combine(Path.GetTempPath(), "codenav-absent-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void EscapesViaReparsePointFalseForCleanTree()
    {
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-clean").FullName);
        try
        {
            string sub = Path.Combine(root, "a", "b");
            Directory.CreateDirectory(sub);
            string file = Path.Combine(sub, "x.cs");
            File.WriteAllText(file, "class X {}");
            Assert.False(WorkspacePaths.EscapesViaReparsePoint(root, file));
            // Not-yet-created leaf under a clean tree is fine too.
            Assert.False(WorkspacePaths.EscapesViaReparsePoint(root, Path.Combine(sub, "new.cs")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RefreshIndexSkipsContentReachedThroughAJunction()
    {
        // Junctions (mklink /J) don't require elevation on Windows; skip gracefully elsewhere.
        string root = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-junc").FullName);
        string outside = Path.GetFullPath(Directory.CreateTempSubdirectory("codenav-ext").FullName);
        string dbPath = IndexBuilder.DefaultDbPath(root);
        try
        {
            // Minimal indexable workspace so the index/db exist.
            File.WriteAllText(Path.Combine(root, "Real.cs"), "namespace R { class Real {} }");
            IndexBuilder.Build(root, dbPath);

            File.WriteAllText(Path.Combine(outside, "secret.cs"), "namespace S { class JunctionSecretMarker {} }");
            string linkDir = Path.Combine(root, "linked");
            if (!TryCreateJunction(linkDir, outside))
            {
                return; // junction creation unavailable in this environment — inconclusive, skip
            }

            try
            {
                string rel = "linked/secret.cs";
                using (var store = new IndexStore(dbPath, createNew: false))
                {
                    var result = DeltaRefresher.Refresh(store, root, new[] { rel });
                    Assert.Equal(0, result.AddedFiles);
                }
                using var q = new IndexQueries(dbPath);
                Assert.Empty(q.SearchSymbols("JunctionSecretMarker", "exact", null, 5));
            }
            finally
            {
                try { Directory.Delete(linkDir); } catch { } // remove the junction, not its target
            }
        }
        finally
        {
            // kae review: the pooled reader was parked on ROOT's db — clear root before its
            // delete ('outside' holds no db; its clear is a harmless no-op kept for symmetry).
            TestWorkspaceCleanup.ClearIndexPools(root);
            TestWorkspaceCleanup.ClearIndexPools(outside);
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}

public class IndexManagerLifecycleTests : IClassFixture<IndexFixture>
{
    private readonly IndexFixture _fx;

    public IndexManagerLifecycleTests(IndexFixture fx) => _fx = fx;

    [Fact]
    public void DisposeRacingStartupDoesNotThrow()
    {
        // Dispose immediately after Start, racing the background open-store task.
        for (int i = 0; i < 6; i++)
        {
            var mgr = new IndexManager(_fx.Root, _fx.DbPath);
            mgr.Start();
            mgr.Dispose();
        }
        Assert.True(true); // reaching here without an exception/crash is the assertion
    }

    [Fact]
    public void DisposeAfterReadyWaitsForPumpAndCleansUp()
    {
        var mgr = new IndexManager(_fx.Root, _fx.DbPath);
        mgr.Start();
        for (int i = 0; i < 100 && !mgr.IsQueryable; i++) Thread.Sleep(50);
        Assert.True(mgr.IsQueryable);

        mgr.RequestRefresh();      // give the pump in-flight work
        mgr.Dispose();             // must settle the pump before disposing the store
        Assert.True(true);
    }
}
