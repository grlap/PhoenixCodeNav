using CodeNav.Core.Diagnostics;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

/// <summary>
/// Batch 51 (epuc.1): bounded semantic-operation telemetry. Pins the four contracts the
/// portal (x5ls) and the field's cold-start analysis depend on:
/// (1) a semantic operation emits one semanticOp JSONL record into
///     {workspace}/.codenav/telemetry/phoenix-{pid}-*.jsonl carrying ITS OWN per-call stage
///     split (ownerLoad — review F2: not some ambient last-load's stats);
/// (2) privacy — records carry no absolute paths (the portal spec forbids them; a drive-rooted
///     or UNC path in any record is a red);
/// (3) the in-memory ring is bounded (the portal reads it live; unbounded would leak);
/// (4) the file cap truncates honestly in-band and never kills Emit/ring (review F5).
/// </summary>
public class Batch51TelemetryTests
{
    [Fact]
    public void SemanticOperationEmitsBoundedPrivacySafeTelemetry()
    {
        string root = Directory.CreateTempSubdirectory("codenav-51-telemetry").FullName;
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
            File.WriteAllText(Path.Combine(proj, "Core.cs"),
                "namespace S { public class Core { public void Ping() { } } }");
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);
            using var m = new IndexManager(root, dbPath);
            var semantic = new SemanticService(m);
            try
            {
                m.Start();
                Assert.True(WaitUntil(() => m.IsQueryable, 30_000));
                if (!semantic.FrameworkRefsAvailable) return;

                // One cold semantic op (retry rides transients, per the n7ly family).
                _ = SemanticRetry.ParseExactWithRetry(() =>
                    new CodeNav.Mcp.NavigationTools(m, semantic)
                        .Definition(name: "Core", timeoutMs: 60000));

                // (1) the record reached the file (drainer is async — bounded wait, no sleep-only).
                string telemetryDir = Path.Combine(root, ".codenav", "telemetry");
                // Portal contract detail this test just proved the hard way: the writer holds
                // the file with FileShare.Read, so LIVE readers must request
                // FileShare.ReadWrite or Windows refuses them (File.ReadAllText does) —
                // see ReadShared below.
                Assert.True(WaitUntil(() =>
                    Directory.Exists(telemetryDir) &&
                    Directory.EnumerateFiles(telemetryDir, "phoenix-*.jsonl")
                        .Any(f => ReadShared(f).Contains("\"semanticOp\"")), 10_000),
                    "no semanticOp record reached the telemetry file");

                string content = ReadShared(
                    Directory.EnumerateFiles(telemetryDir, "phoenix-*.jsonl").First());
                Assert.Contains("\"tool\":\"definition\"", content);
                Assert.Contains("\"result\":\"exact\"", content);
                // Review F2: the split must be THIS op's own phase-1 load, not an ambient
                // last-load — the field name is the contract (ownerLoad, not load).
                Assert.Contains("\"ownerLoad\":", content);
                Assert.Contains("\"gateWaitMs\":", content);
                // Field regression (48s query invisible): the op's own load/query wall split
                // must ride every completed record — query is the dominant cost now.
                Assert.Contains("\"clusterLoadMs\":", content);
                Assert.Contains("\"queryMs\":", content);

                // (2) privacy: no drive-rooted path may appear in any record —
                // neither drive-letter (C:\\) nor UNC (\\\\server\\share) shaped.
                foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Assert.DoesNotContain(":\\\\", line);   // JSON-escaped C:\\ etc.
                    Assert.DoesNotContain("\\\\\\\\", line); // JSON-escaped \\ (UNC root)
                    Assert.DoesNotContain(root.Replace('\\', '/'), line);
                }
            }
            finally { semantic.Dispose(); m.Dispose(); }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public void RingIsBoundedAndEmitNeverThrows()
    {
        string root = Directory.CreateTempSubdirectory("codenav-51-ring").FullName;
        try
        {
            using var log = new TelemetryLog(root);
            for (int i = 0; i < 600; i++) log.Emit(new { e = "probe", i });
            Assert.True(log.Snapshot().Count <= 256, "ring must stay bounded");
            log.Emit(new { e = "still-alive" }); // after churn, Emit still never throws
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public void FileCapTruncatesHonestlyWhileRingKeepsRolling()
    {
        // Review F5: the 16 MiB cap was documented but unexercised — a broken cap means a
        // long-lived server writes an unbounded file into every indexed workspace.
        string root = Directory.CreateTempSubdirectory("codenav-51-cap").FullName;
        try
        {
            string dir = Path.Combine(root, ".codenav", "telemetry");
            long fileLenAtCap = 0;
            using (var log = new TelemetryLog(root))
            {
                log.FileCapBytes = 2_000; // test hook: shrink 16 MiB to something a test can cross
                for (int i = 0; i < 200; i++) log.Emit(new { e = "capProbe", i });
                Assert.True(WaitUntil(() =>
                    Directory.Exists(dir) &&
                    Directory.EnumerateFiles(dir, "phoenix-*.jsonl")
                        .Any(f => ReadShared(f).Contains("\"telemetry_truncated\"")), 10_000),
                    "cap crossing must be announced in-band as telemetry_truncated");

                string file = Directory.EnumerateFiles(dir, "phoenix-*.jsonl")
                    .First(f => ReadShared(f).Contains("\"telemetry_truncated\""));
                fileLenAtCap = new FileInfo(file).Length;

                // Past the cap: the file stops growing, but Emit/ring stay alive (the portal
                // still reads the ring even after the file honestly ends).
                for (int i = 0; i < 300; i++) log.Emit(new { e = "afterCap", i });
                Assert.True(log.Snapshot().Any(l => l.Contains("\"afterCap\"")),
                    "ring must keep rolling after the file cap");
                Assert.Equal(fileLenAtCap, new FileInfo(file).Length);
            }
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    [Fact]
    public async Task GateDeathStillPublishesGateOnlySplit()
    {
        // Review r2: a deadline dying while QUEUED for the workspace gate (cold workspace, two
        // parallel ops) is the primary gate-contention signal — the stats box must still carry
        // a gate-only split: gateWaitMs = whole wall, phases-never-entered = 0, and
        // loadedBefore ABSENT (null — the warm-set size is unreadable without the gate).
        string root = Directory.CreateTempSubdirectory("codenav-51-gate").FullName;
        try
        {
            using var ws = new SemanticWorkspace(root, Path.Combine(root, "index.db"));
            var box = new SemanticWorkspace.LoadStatsBox();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ws.EnsureLoadedAsync(new[] { "P" }, new CancellationToken(canceled: true),
                    statsBox: box));
            Assert.NotNull(box.Stats);
            Assert.Null(box.Stats!.LoadedBefore);   // unknown, never fabricated as 0
            Assert.Equal(1, box.Stats.Requested);
            Assert.Equal(0, box.Stats.FingerprintMs); // phase never entered
            Assert.Equal(0, box.Stats.ProjectLoadMs);
            Assert.Equal(0, box.Stats.Loaded);
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
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
