using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

/// <summary>
/// Batch 54 (pv1k): per-file open→read→decode uses the cold-start runtime's process-wide
/// bounded source lane, so one large project can fan out without multiplying the worker cap by
/// the number of concurrently prepared projects.
/// Pins the two properties the parallelization must not cost:
/// (1) the disk-miss fallback still serves the INDEXED text (that path runs single-threaded
///     against SQLite after the fan-out — a worker touching the shared connection would
///     corrupt or throw, and dropping the fallback would silently lose documents);
/// (2) document order stays deterministic and equal to the index file order (results are
///     index-addressed, not completion-ordered).
/// </summary>
public class Batch54ParallelLoadTests
{
    [Fact]
    public async Task DiskMissFallsBackToIndexedTextAndOrderIsDeterministic()
    {
        string root = Directory.CreateTempSubdirectory("codenav-54-parallel").FullName;
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
            // Several files so the parallel fan-out actually engages.
            for (int i = 0; i < 6; i++)
            {
                File.WriteAllText(Path.Combine(proj, $"F{i}.cs"),
                    $"namespace P {{ public class C{i} {{ }} }}");
            }
            string dbPath = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, dbPath);

            // Vanish ONE file from disk after indexing — its text must come from the index.
            File.Delete(Path.Combine(proj, "F3.cs"));

            using var ws = new SemanticWorkspace(root, dbPath,
                semanticInputBudgetBytes: 64 * 1024 * 1024,
                preparationConcurrency: 2);
            int activeReads = 0;
            int maximumReads = 0;
            var bothReadsEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ws.TestOnlyBeforeSourceCaptureAsync = async (_, _, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref activeReads);
                int observed;
                while ((observed = Volatile.Read(ref maximumReads)) < current &&
                       Interlocked.CompareExchange(ref maximumReads, current, observed) != observed)
                {
                }
                if (current >= 2) bothReadsEntered.TrySetResult(true);
                await bothReadsEntered.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref activeReads);
            };
            using var firstLoad = await ws.EnsureLoadedAsync(
                new[] { "P" }, CancellationToken.None);
            var (solution, coverage) = firstLoad;
            Assert.Equal(1, coverage.LoadedProjects);

            var project = Assert.Single(solution.Projects);
            var docNames = project.Documents.Select(d => d.Name).ToList();
            Assert.Contains("F3.cs", docNames); // fallback kept the document
            var f3 = project.Documents.First(d => d.Name == "F3.cs");
            string text = (await f3.GetTextAsync()).ToString();
            Assert.Contains("class C3", text); // indexed content, not empty/garbage

            // Determinism (review r2: the first cut computed `expected` and never compared
            // CONTENT — a tautology that completion-ordered accumulation could pass):
            // the index serves files ORDER BY path, so document order must BE the ordinal
            // path order, not merely stable across loads.
            var expected = Enumerable.Range(0, 6).Select(i => $"F{i}.cs").ToList();
            Assert.Equal(expected, docNames); // exact order AND nothing lost/duplicated
            Assert.True(maximumReads >= 2,
                $"one-project source capture stayed serial (max={maximumReads})");
            using var ws2 = new SemanticWorkspace(root, dbPath);
            using var secondLoad = await ws2.EnsureLoadedAsync(
                new[] { "P" }, CancellationToken.None);
            var solution2 = secondLoad.Solution;
            var docNames2 = Assert.Single(solution2.Projects)
                .Documents.Select(d => d.Name).ToList();
            Assert.Equal(expected, docNames2); // and identical on a fresh load
        }
        finally { TestWorkspaceCleanup.DeleteWorkspace(root); }
    }
}
