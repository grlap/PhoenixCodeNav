using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.WorkspaceGen;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

/// <summary>
/// Bead two (field-requested, un-parked during the v5 monolith reindex): state:'building' was a
/// binary — callers (and the user watching a 2000-project rebuild) had no idea whether to wait.
/// Minimum honest cut: {phase, filesIndexed, filesTotal-once-known, elapsedMs} in
/// server_capabilities.index.progress AND inside every index_building error body. Monotonic
/// counters, no fabricated ETA/percent, absent when ready.
/// </summary>
public class Batch27BuildProgressTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task BuilderReportsPhasesAndMonotonicCounts()
    {
        string root = Directory.CreateTempSubdirectory("codenav-prog-core").FullName;
        try
        {
            WorkspaceGenerator.Generate(root, targetProjects: 12, seed: 3);
            int csFiles = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Length;

            var bp = new BuildProgress();
            var samples = new List<IndexProgress>();
            using var sampler = new CancellationTokenSource();
            var samplerTask = Task.Run(async () =>
            {
                while (!sampler.IsCancellationRequested)
                {
                    samples.Add(bp.Snapshot());
                    await Task.Delay(5);
                }
            });

            IndexBuilder.Build(root, IndexBuilder.DefaultDbPath(root), null, bp);
            sampler.Cancel();
            try { await samplerTask; } catch (OperationCanceledException) { }

            var final = bp.Snapshot();
            Assert.Equal("finalizing", final.Phase);
            Assert.Equal(csFiles, final.FilesTotal);
            Assert.Equal(csFiles, final.FilesIndexed); // every scanned file counted exactly once
            Assert.True(final.ElapsedMs >= 0);

            // Monotonicity across every observed snapshot — the no-going-backwards contract.
            for (int i = 1; i < samples.Count; i++)
            {
                Assert.True(samples[i].FilesIndexed >= samples[i - 1].FilesIndexed,
                    "filesIndexed moved backwards");
                Assert.True((samples[i].FilesTotal ?? -1) >= (samples[i - 1].FilesTotal ?? -1),
                    "filesTotal was retracted");
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BuildingStateSurfacesProgressInCapabilitiesAndErrors()
    {
        string root = Directory.CreateTempSubdirectory("codenav-prog-mcp").FullName;
        try
        {
            // Big enough that the building window is comfortably observable (~1s+): the first
            // cut at 40 projects built in ~250ms and the error probe missed the window.
            WorkspaceGenerator.Generate(root, targetProjects: 200, seed: 9);
            using var m = new IndexManager(root, IndexBuilder.DefaultDbPath(root));
            var tools = new NavigationTools(m, new SemanticService(m));
            m.Start();

            JsonElement? buildingCaps = null;
            JsonElement? buildingError = null;
            while (m.State is "missing" or "building")
            {
                if (m.State == "building")
                {
                    var err = Parse(tools.SearchSymbol("Anything"));
                    if (err.TryGetProperty("error", out var e) && e.GetString() == "index_building"
                        && err.TryGetProperty("progress", out var ep) && ep.ValueKind == JsonValueKind.Object)
                    {
                        buildingError ??= err;
                    }
                    var caps = Parse(tools.ServerCapabilities());
                    if (caps.GetProperty("index").TryGetProperty("progress", out var p)
                        && p.ValueKind == JsonValueKind.Object)
                    {
                        buildingCaps ??= caps;
                    }
                    if (buildingCaps is not null && buildingError is not null) break;
                }
                Thread.Sleep(10);
            }
            Assert.True(WaitUntil(() => m.IsQueryable, 30000), "index never became queryable");

            Assert.True(buildingCaps is not null,
                "no building-window capabilities sample captured — enlarge the fixture workspace");
            var prog = buildingCaps!.Value.GetProperty("index").GetProperty("progress");
            Assert.Contains(prog.GetProperty("phase").GetString(),
                new[] { "scanning", "parsing_projects", "indexing_files", "finalizing" });
            Assert.True(prog.GetProperty("filesIndexed").GetInt32() >= 0);
            Assert.True(prog.GetProperty("elapsedMs").GetInt64() >= 0);

            // The same struct rides in the tool error — no second poll needed (field ask).
            Assert.True(buildingError is not null,
                "no index_building error captured during the build window");
            var errProg = buildingError!.Value.GetProperty("progress");
            Assert.True(errProg.GetProperty("filesIndexed").GetInt32() >= 0);
            Assert.Contains(errProg.GetProperty("phase").GetString(),
                new[] { "scanning", "parsing_projects", "indexing_files", "finalizing" });

            // Ready => the field is GONE (fields absent when state == ready), not zeroed.
            var readyCaps = Parse(tools.ServerCapabilities());
            Assert.False(readyCaps.GetProperty("index").TryGetProperty("progress", out _),
                "progress must be omitted once ready");
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------- helpers

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
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }
}
