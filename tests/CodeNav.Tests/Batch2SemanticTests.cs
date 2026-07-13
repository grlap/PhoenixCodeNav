using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

/// <summary>
/// Regression coverage for review batch 2: PhoenixCodeNav-hja (reload must not orphan
/// dependents) and PhoenixCodeNav-797 (resolve + search must share one snapshot).
/// Uses its own IndexFixture instance (separate temp workspace + db).
/// </summary>
public class Batch2SemanticTests : IClassFixture<IndexFixture>, IDisposable
{
    private readonly IndexFixture _fx;
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;

    public Batch2SemanticTests(IndexFixture fx)
    {
        _fx = fx;
        _manager = new IndexManager(_fx.Root, _fx.DbPath);
        _manager.Start();
        for (int i = 0; i < 100 && !_manager.IsQueryable; i++) Thread.Sleep(50);
        _semantic = new SemanticService(_manager);
    }

    public void Dispose()
    {
        _semantic.Dispose();
        _manager.Dispose();
    }

    [Fact]
    public void SkipWhenNoFrameworkRefs()
    {
        // Guards the meaningful tests below: they need real compilations.
        Assert.True(_semantic.FrameworkRefsAvailable);
    }

    /// <summary>
    /// The hja/797 scenario: run references() for a widely-used symbol, edit its declaring
    /// file (forcing an owner reload on the next call), then run references() again. The
    /// cross-project usages must NOT silently vanish (the pre-fix bug returned them dropped
    /// with confidence 'exact' / partial:false), and resolution must stay consistent.
    /// </summary>
    /// <summary>n7ly: service-level twin of SemanticRetry — the transient degrade classes
    /// (cluster_cold_load / index_snapshot_unavailable / timeout under suite load) are
    /// retryable per their own documented contract; a deterministic failure keeps its final
    /// reason and the caller's assert names it.</summary>
    private async Task<(SemanticReferences? Result, string? FailReason)> ReferencesWithRetry(
        string path, int line, int attempts = 3)
    {
        (SemanticReferences?, string?) last = default;
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) await Task.Delay(250);
            last = await _semantic.ReferencesAsync(
                path, line, null, "Guard", maxProjects: 40, samplesPerGroup: 1, timeoutMs: 90000);
            if (last.Item1 is not null) return last;
        }
        return last;
    }

    [Fact]
    public async Task ReferencesSurviveOwnerReloadWithoutDroppingDependents()
    {
        using var q = _manager.OpenQueries();
        var guard = q.SearchSymbols("Guard", "exact", new[] { "class" }, 1).Single();
        Assert.Equal("Acme.Platform.Common", guard.Ns);

        var (before, r1) = await ReferencesWithRetry(guard.FilePath, guard.StartLine); // n7ly
        Assert.True(before is not null, $"first references failed: {r1}");
        int beforeCross = before!.Groups.Count(g => !g.Project.Equals("Acme.Platform.Common", StringComparison.OrdinalIgnoreCase));
        Assert.True(beforeCross > 0, "expected Guard to be referenced from dependent projects");
        int beforeTotal = before.TotalLocations;

        string full = Path.Combine(_fx.Root, guard.FilePath.Replace('/', Path.DirectorySeparatorChar));
        string original = File.ReadAllText(full);
        // Append a member -> file hash changes -> Platform.Common reloads on next call.
        File.WriteAllText(full, original.Replace(
            "public static void NotNull",
            "public static void ReloadMarkerNoop() { }\n\n        public static void NotNull"));
        try
        {
            using (var store = new IndexStore(_fx.DbPath, createNew: false))
            {
                DeltaRefresher.Refresh(store, _fx.Root, new[] { guard.FilePath });
            }

            var (after, r2) = await ReferencesWithRetry(guard.FilePath, guard.StartLine); // n7ly
            Assert.True(after is not null, $"second references failed: {r2}");

            int afterCross = after!.Groups.Count(g => !g.Project.Equals("Acme.Platform.Common", StringComparison.OrdinalIgnoreCase));
            Assert.True(afterCross >= beforeCross,
                $"cross-project references dropped after owner reload: {beforeCross} -> {afterCross} " +
                "(hja: dependents orphaned by a fresh ProjectId)");
            Assert.True(after.TotalLocations >= beforeTotal,
                $"total exact references dropped after reload: {beforeTotal} -> {after.TotalLocations}");
        }
        finally
        {
            File.WriteAllText(full, original);
            using var store = new IndexStore(_fx.DbPath, createNew: false);
            DeltaRefresher.Refresh(store, _fx.Root, new[] { guard.FilePath });
        }
    }

    /// <summary>
    /// Directly asserts the SemanticWorkspace invariant: after a dependency reloads, a
    /// previously-loaded dependent still sees the dependency's types (reference not dangling).
    /// </summary>
    [Fact]
    public async Task DependentStillSeesReloadedDependencyTypes()
    {
        // Find a non-test project that references Platform.Common.
        string dependent;
        using (var q = _manager.OpenQueries())
        {
            dependent = q.ProjectGraph("Acme.Platform.Common", 1, "upstream")
                .Select(e => e.FromProject)
                .First(p => !p.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase));
        }

        var workspace = new SemanticWorkspace(_fx.Root, _fx.DbPath);
        try
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Acme.Platform.Common", dependent };
            var (sol1, _) = await workspace.EnsureLoadedAsync(set, CancellationToken.None);
            Assert.True(await DependentSeesGuard(sol1, dependent), "dependent should see Guard before reload");

            // Change Platform.Common's fingerprint via delta refresh, then re-ensure.
            using var q0 = _manager.OpenQueries();
            var guard = q0.SearchSymbols("Guard", "exact", new[] { "class" }, 1).Single();
            string full = Path.Combine(_fx.Root, guard.FilePath.Replace('/', Path.DirectorySeparatorChar));
            string original = File.ReadAllText(full);
            File.WriteAllText(full, original.Replace("public static void NotNull",
                "public static void ReloadMarker2() { }\n\n        public static void NotNull"));
            try
            {
                using (var store = new IndexStore(_fx.DbPath, createNew: false))
                {
                    DeltaRefresher.Refresh(store, _fx.Root, new[] { guard.FilePath });
                }
                var (sol2, _) = await workspace.EnsureLoadedAsync(set, CancellationToken.None);
                Assert.True(await DependentSeesGuard(sol2, dependent),
                    "dependent lost visibility of Guard after Platform.Common reload (hja)");
            }
            finally
            {
                File.WriteAllText(full, original);
                using var store = new IndexStore(_fx.DbPath, createNew: false);
                DeltaRefresher.Refresh(store, _fx.Root, new[] { guard.FilePath });
            }
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static async Task<bool> DependentSeesGuard(Microsoft.CodeAnalysis.Solution solution, string dependent)
    {
        var project = solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Name, dependent, StringComparison.OrdinalIgnoreCase));
        if (project is null) return false;
        var compilation = await project.GetCompilationAsync();
        // Resolvable only if the project reference to Platform.Common is intact.
        return compilation?.GetTypeByMetadataName("Acme.Platform.Common.Guard") is not null;
    }
}
