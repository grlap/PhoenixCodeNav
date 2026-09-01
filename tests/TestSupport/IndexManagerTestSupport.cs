using System.Diagnostics;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

internal static class IndexManagerTestSupport
{
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(50);

    internal readonly record struct ReadinessSnapshot(bool IsQueryable, IndexHealth Health);

    /// <summary>
    /// Waits for startup freshness convergence. IsQueryable deliberately includes the stale
    /// state, but exact semantic assertions require the startup sweep to clear its incomplete
    /// reason and publish the ready state.
    /// </summary>
    internal static void WaitUntilReady(
        IndexManager manager,
        TimeSpan timeout,
        string because) =>
        WaitUntilReady(
            () => new ReadinessSnapshot(manager.IsQueryable, manager.Health()),
            timeout,
            because);

    internal static void WaitUntilReady(
        Func<ReadinessSnapshot> observe,
        TimeSpan timeout,
        string because,
        TimeSpan? pollInterval = null)
    {
        TimeSpan interval = pollInterval ?? ReadyPollInterval;
        var wait = Stopwatch.StartNew();
        ReadinessSnapshot snapshot;
        bool completed;

        do
        {
            snapshot = observe();
            completed = snapshot.IsQueryable &&
                snapshot.Health.State == "ready" &&
                snapshot.Health.RefreshIncompleteReason is null;
            if (completed || wait.Elapsed >= timeout) break;
            if (interval > TimeSpan.Zero) Thread.Sleep(interval);
        } while (true);

        Assert.True(completed,
            $"{because}: isQueryable={snapshot.IsQueryable}, " +
            $"state={snapshot.Health.State}, " +
            $"refreshIncompleteReason={snapshot.Health.RefreshIncompleteReason}, " +
            $"error={snapshot.Health.Error}");
    }

    /// <summary>
    /// Routes test mutations through the manager's single-writer refresh pump and waits for both
    /// request completion and the decisive indexed state. Calling DeltaRefresher directly while a
    /// live manager owns the database can race its startup sweep or watcher.
    /// </summary>
    internal static void RefreshAndWait(
        IndexManager manager,
        IReadOnlyCollection<string> paths,
        Func<IndexQueries, bool> isVisible,
        string because)
    {
        Assert.True(manager.RequestRefreshForTest(paths, out Task requestCompleted),
            "manager rejected the test refresh request");
        Assert.True(requestCompleted.Wait(TimeSpan.FromSeconds(20)),
            "manager did not complete the exact test refresh request");

        var wait = Stopwatch.StartNew();
        bool completed = false;
        do
        {
            if (manager.State == "ready" && manager.IsQueryable)
            {
                using var queries = manager.OpenQueries();
                completed = isVisible(queries);
            }

            if (completed || wait.Elapsed >= TimeSpan.FromSeconds(20)) break;
            Thread.Sleep(ReadyPollInterval);
        } while (true);

        Assert.True(completed, because);
    }
}
