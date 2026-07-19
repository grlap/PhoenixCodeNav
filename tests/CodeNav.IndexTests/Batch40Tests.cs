using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Batch 40 (v0.9.4) — the progress trio:
/// efa — an unreadable .cs capture or FAILED csproj parse once looked identical to a clean build;
///       filesSkipped/projectsFailed count the evidence, and unreadable source now fails closed;
/// z4c — 'refreshing' was a binary; pendingProcessed (monotonic applied-delta count) paired
///       with pendingChanges turns it into movement — both flat means stuck, not busy;
/// 0tn — filesPerSecond + estimatedRemainingMs derived from the indexing_files phase's OWN
///       clock, gated until measurable (&gt;=100 files over &gt;=1s) — labeled estimates; the
///       original no-fabricated-ETA stance survives as the gates.
/// </summary>
public class Batch40Tests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void UnreadableColdBuildFailsClosedAndCountsCaptureFailure()
    {
        string root = Directory.CreateTempSubdirectory("codenav-40-loss").FullName;
        try
        {
            WriteProgressWorkspace(root);
            // A malformed csproj: parse must fail and be COUNTED, not hidden.
            string badDir = Path.Combine(root, "Broken");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(Path.Combine(badDir, "Broken.csproj"), "<Project><UnclosedTag</Project>");
            // An exclusively-locked .cs file: the parallel reader gets IOException, records the
            // failed capture for progress, and refuses to publish a lossy cold index.
            string lockedPath = Path.Combine(root, "Lab", "Locked.cs");
            File.WriteAllText(lockedPath, "namespace Lab { public class LockedOut { } }");
            FileStream? padlock = null;
            if (OperatingSystem.IsWindows())
            {
                padlock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read,
                    FileShare.None);
            }
            else
            {
                File.SetUnixFileMode(lockedPath, UnixFileMode.None);
            }

            try
            {
                var bp = new BuildProgress();
                Assert.Throws<RefreshInputUnavailableException>(() =>
                    IndexBuilder.Build(root, IndexBuilder.DefaultDbPath(root), null, bp));
                var snap = bp.Snapshot();

                Assert.Equal(1, snap.FilesSkipped);
                Assert.Equal(1, snap.ProjectsFailed);
            }
            finally
            {
                padlock?.Dispose();
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(lockedPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ProgressJsonStaysSilentOnACleanBuildAndSpeaksOnALossyOne()
    {
        string root = Directory.CreateTempSubdirectory("codenav-40-shape").FullName;
        try
        {
            WriteProgressWorkspace(root);
            var bp = new BuildProgress();
            IndexBuilder.Build(root, IndexBuilder.DefaultDbPath(root), null, bp);
            var clean = bp.Snapshot();
            Assert.Equal(0, clean.FilesSkipped);
            Assert.Equal(0, clean.ProjectsFailed);

            // The MCP emitter: zero counters are OMITTED (silent-when-nothing-to-say); a lossy
            // snapshot carries them. Exercised through the shared ProgressJson via a synthetic
            // IndexHealth (internal access, same as prior batches' seam tests).
            string cleanJson = Json.Serialize(new { progress = NavigationTools.ProgressJsonForTest(Health(clean)) });
            Assert.DoesNotContain("filesSkipped", cleanJson);
            Assert.DoesNotContain("projectsFailed", cleanJson);
            Assert.DoesNotContain("estimatedRemainingMs", cleanJson); // build finished — no phase rate

            var lossy = clean with { FilesSkipped = 3, ProjectsFailed = 2 };
            string lossyJson = Json.Serialize(new { progress = NavigationTools.ProgressJsonForTest(Health(lossy)) });
            Assert.Contains("\"filesSkipped\":3", lossyJson);
            Assert.Contains("\"projectsFailed\":2", lossyJson);

            static IndexHealth Health(IndexProgress p) => new(
                "building", null, null, null, 0, null, 0, "w", "db", Progress: p);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ThroughputAndRemainingEstimateAppearOnlyOnceMeasurable()
    {
        var bp = new BuildProgress();
        bp.SetFilesTotal(1000);
        bp.SetPhase("indexing_files");

        // Below the file floor: nothing, however long the phase has run.
        for (int i = 0; i < 50; i++) bp.AddFileIndexed();
        Thread.Sleep(1100);
        var early = bp.Snapshot();
        Assert.Null(early.FilesPerSecond);
        Assert.Null(early.EstimatedRemainingMs);

        // Past both gates (>=100 files, >=1s of the phase clock): a measured rate and a
        // sane remaining estimate derived from it.
        for (int i = 0; i < 100; i++) bp.AddFileIndexed();
        var measured = bp.Snapshot();
        Assert.NotNull(measured.FilesPerSecond);
        Assert.True(measured.FilesPerSecond > 0);
        Assert.NotNull(measured.EstimatedRemainingMs);
        // 150 files over ~1.1s -> ~136/s -> ~850 left -> ~6.2s; assert loose sanity bounds.
        Assert.InRange(measured.EstimatedRemainingMs!.Value, 500, 120_000);

        // Outside the indexing_files phase the rate is meaningless — absent by design
        // (never extrapolated from another phase's clock).
        bp.SetPhase("finalizing");
        var final = bp.Snapshot();
        Assert.Null(final.FilesPerSecond);
        Assert.Null(final.EstimatedRemainingMs);
    }

    [Fact]
    public void PendingProcessedClimbsAsThePumpAppliesDeltas()
    {
        string root = Directory.CreateTempSubdirectory("codenav-40-pump").FullName;
        try
        {
            WriteProgressWorkspace(root);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 20000));
            long baseline = m.Health().PendingProcessed;

            // Apply a real change through the pump; the counter must climb by what was APPLIED.
            File.AppendAllText(Path.Combine(root, "Lab", "Alpha.cs"),
                "\nnamespace Lab { public class PumpToken40 { } }");
            m.RequestRefresh(new[] { "Lab/Alpha.cs" });
            Assert.True(WaitUntil(() => m.Health().PendingProcessed > baseline, 20000),
                "pendingProcessed did not climb after an applied delta");

            // Monotonic: a hash-identical re-request applies nothing and must not move it —
            // and can never DECREASE it.
            long afterFirst = m.Health().PendingProcessed;
            m.RequestRefresh(new[] { "Lab/Alpha.cs" });
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.SearchSymbols("PumpToken40", "exact", null, 2).Count > 0;
            }, 20000));
            Assert.True(m.Health().PendingProcessed >= afterFirst, "pendingProcessed went backwards");

            // The pair reaches the wire: capabilities carries pendingProcessed beside pendingChanges.
            var tools = new NavigationTools(m, new SemanticService(m));
            var index = Parse(tools.ServerCapabilities()).GetProperty("index");
            Assert.True(index.GetProperty("pendingProcessed").GetInt64() >= afterFirst);
            Assert.True(index.TryGetProperty("pendingChanges", out _));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task ExactRefreshCompletionHandlesWatcherWinningBeforeExplicitRequest()
    {
        string root = Directory.CreateTempSubdirectory("codenav-40-watcher-wins").FullName;
        try
        {
            WriteProgressWorkspace(root);
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            m.Start();
            Assert.True(WaitUntil(() => m.IsQueryable, 20000));

            // Drain startup work first, then let the watcher apply the edit before the helper
            // queues its explicit request. That request is hash-identical and therefore must
            // complete without relying on pendingProcessed to move.
            Assert.True(m.RequestRefreshForTest(Array.Empty<string>(), out Task startupBarrier));
            await startupBarrier.WaitAsync(TimeSpan.FromSeconds(20));
            File.AppendAllText(Path.Combine(root, "Lab", "Alpha.cs"),
                "\nnamespace Lab { public class WatcherWon40 { } }");
            Assert.True(WaitUntil(() =>
            {
                using var q = m.OpenQueries();
                return q.SearchSymbols("WatcherWon40", "exact", null, 2).Count > 0;
            }, 20000), "watcher did not index the edit before the explicit request");

            long processedAfterWatcher = m.Health().PendingProcessed;
            IndexManagerTestSupport.RefreshAndWait(m, new[] { "Lab/Alpha.cs" },
                q => q.SearchSymbols("WatcherWon40", "exact", null, 2).Count > 0,
                "hash-identical explicit request did not complete after the watcher won");
            Assert.Equal(processedAfterWatcher, m.Health().PendingProcessed);
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- fixture

    private static void WriteProgressWorkspace(string root)
    {
        string dir = Path.Combine(root, "Lab");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Lab.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Alpha.cs"),
            "namespace Lab { public class Alpha { public void Go() { } } }");
        File.WriteAllText(Path.Combine(dir, "Beta.cs"),
            "namespace Lab { public class Beta { } }");
    }

    private static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return cond();
    }

    private static void Cleanup(string root)
    {
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }
}
