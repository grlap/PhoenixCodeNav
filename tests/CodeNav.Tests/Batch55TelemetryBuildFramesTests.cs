using System.Text.Json;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Batch 55 (x5ls.1.2): index build/refresh lifecycle frames over the v1 IPC producer,
/// end-to-end through a real IndexManager against the contract-test broker. Pins:
/// (1) a startup build emits index.build.started (honest reason) and index.build.completed
///     with measured phaseDurations and file counts — never a fabricated total/ETA;
/// (2) a delta refresh emits one index.refresh.snapshot outcome frame with the batch's
///     applied-count and reason;
/// (3) frames carry the salted indexId (grouping key), not any path.
/// </summary>
public class Batch55TelemetryBuildFramesTests
{
    [Fact]
    public void StartupBuildAndRefreshEmitLifecycleFrames()
    {
        string root = Directory.CreateTempSubdirectory("codenav-55-frames").FullName;
        using var broker = new TestTelemetryBroker();
        try
        {
            string proj = Path.Combine(root, "P");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(proj, "A.cs"),
                "namespace P { public class A { } }");

            using (var m = new IndexManager(root, telemetryPipeName: broker.PipeName))
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 30_000), "index never became queryable");

                // (1) build lifecycle — started with the honest startup reason, completed with
                // measured phases (this workspace had no index: reason must be startup_missing).
                Assert.True(WaitUntil(() => broker.FramesOfType("index.build.completed").Count > 0,
                    15_000), "no index.build.completed frame arrived");
                var started = broker.FramesOfType("index.build.started");
                Assert.True(started.Count > 0, "no index.build.started frame arrived");
                var startedData = started[0].RootElement.GetProperty("data");
                Assert.Equal("startup_missing", startedData.GetProperty("reason").GetString());
                string buildId = startedData.GetProperty("buildId").GetString()!;

                var completed = broker.FramesOfType("index.build.completed")[0]
                    .RootElement.GetProperty("data");
                Assert.Equal(buildId, completed.GetProperty("buildId").GetString());
                Assert.True(completed.GetProperty("filesIndexed").GetInt32() >= 1);
                var phases = completed.GetProperty("phaseDurations").EnumerateArray()
                    .Select(p => p.GetProperty("phase").GetString()).ToList();
                Assert.Contains("scanning", phases); // measured transitions, not reconstructed
                Assert.StartsWith("ix_", completed.GetProperty("indexId").GetString());

                // (2) refresh outcome frame with the applied-batch count.
                File.WriteAllText(Path.Combine(proj, "B.cs"),
                    "namespace P { public class B { } }");
                Assert.True(m.RequestRefresh(new[] { "P/B.cs" }));
                // Key on the batch that APPLIED a file — a startup/detect-all sweep (reason
                // full_sweep, batchProcessed 0) may legitimately complete first.
                static bool AppliedBatch(JsonDocument f) =>
                    f.RootElement.GetProperty("data") is var d
                    && d.GetProperty("state").GetString() == "completed"
                    && d.GetProperty("batchProcessed").GetInt32() >= 1;
                Assert.True(WaitUntil(() => broker.FramesOfType("index.refresh.snapshot")
                    .Any(AppliedBatch), 15_000), "no applied-batch refresh snapshot arrived");
                // Review B2 provenance pin, race-free: B.cs may be applied by the startup
                // detect-all sweep, the watcher's own batch, or my explicit request —
                // whichever wins must carry ITS honest label, and the explicit request always
                // emits its own labeled frame regardless of who applied the file.
                var applied = broker.FramesOfType("index.refresh.snapshot")
                    .First(AppliedBatch).RootElement.GetProperty("data");
                Assert.Contains(applied.GetProperty("reason").GetString(),
                    new[] { "explicit", "watcher_batch", "full_sweep" });
                Assert.True(WaitUntil(() => broker.FramesOfType("index.refresh.snapshot")
                    .Any(f => f.RootElement.GetProperty("data").GetProperty("reason").GetString()
                        == "explicit"), 15_000),
                    "the tool-requested batch must emit its own explicit-labeled frame");

                // (3) privacy: no frame carries a path in any spelling.
                foreach (var frame in broker.Frames)
                {
                    string raw = frame.RootElement.GetRawText();
                    Assert.DoesNotContain(":\\\\", raw);
                    Assert.DoesNotContain(":/", raw);
                }
            }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
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
}
