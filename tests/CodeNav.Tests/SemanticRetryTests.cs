using System.Text.Json;

namespace CodeNav.Tests;

public sealed class SemanticRetryTests
{
    [Fact]
    public void ExactRetryWaitsForRefreshSweepConvergenceWithoutLooseningConfidence()
    {
        int calls = 0;

        JsonElement response = SemanticRetry.ParseExactWithRetry(() =>
        {
            calls++;
            return calls == 1
                ? """{"meta":{"confidence":"indexed","partialReason":"refresh_sweep_pending"}}"""
                : """{"meta":{"confidence":"exact"}}""";
        }, attempts: 1);

        Assert.Equal(2, calls);
        Assert.Equal("exact", response.GetProperty("meta").GetProperty("confidence").GetString());
    }

    [Fact]
    public void PlainRejectionConsumesExactlyTheOrdinaryAttemptBudget()
    {
        int calls = 0;

        Exception? failure = Record.Exception(() => SemanticRetry.ParseWithRetry(
            () =>
            {
                calls++;
                return """{"meta":{"confidence":"indexed","partialReason":"cluster_cold_load"}}""";
            },
            response => response.GetProperty("meta").GetProperty("confidence").GetString() == "exact",
            "exact response",
            attempts: 3,
            delay: _ => { }));

        Assert.NotNull(failure);
        Assert.Contains("response never satisfied", failure.Message);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void PermanentlyPendingRefreshStopsAfterTheConvergenceBound()
    {
        int calls = 0;
        int elapsedReads = 0;
        TimeSpan timeout = TimeSpan.FromSeconds(1);

        Exception? failure = Record.Exception(() => SemanticRetry.ParseWithRetry(
            () =>
            {
                calls++;
                return """{"meta":{"confidence":"indexed","partialReason":"refresh_sweep_pending"}}""";
            },
            response => response.GetProperty("meta").GetProperty("confidence").GetString() == "exact",
            "exact response",
            attempts: 3,
            refreshSweepConvergenceTimeout: timeout,
            delay: _ => { },
            convergenceElapsed: () => elapsedReads++ == 0 ? TimeSpan.Zero : timeout));

        Assert.NotNull(failure);
        Assert.Contains("response never satisfied", failure.Message);
        Assert.Equal(4, calls);
        Assert.Equal(4, elapsedReads);
    }
}
