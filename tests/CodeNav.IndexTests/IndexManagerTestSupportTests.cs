using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public class IndexManagerTestSupportTests
{
    [Fact]
    public void WaitUntilReadyDoesNotReturnBeforeQueryabilityIsPublished()
    {
        var readyHealth = new IndexHealth(
            "ready", null, null, null, 0, null, 0, "workspace", "index.db");
        Queue<IndexManagerTestSupport.ReadinessSnapshot> observations = new(
        [
            new(false, readyHealth),
            new(true, readyHealth),
        ]);

        IndexManagerTestSupport.WaitUntilReady(
            () => observations.Dequeue(),
            TimeSpan.FromSeconds(1),
            "queryability must be published",
            pollInterval: TimeSpan.Zero);

        Assert.Empty(observations);
    }
}
