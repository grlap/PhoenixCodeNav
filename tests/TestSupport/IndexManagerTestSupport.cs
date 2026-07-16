using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

internal static class IndexManagerTestSupport
{
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

        bool completed = SpinWait.SpinUntil(() =>
        {
            if (manager.State != "ready") return false;

            using var queries = manager.OpenQueries();
            return isVisible(queries);
        }, TimeSpan.FromSeconds(20));

        Assert.True(completed, because);
    }
}
