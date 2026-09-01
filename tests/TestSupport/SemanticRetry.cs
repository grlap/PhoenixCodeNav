using System.Diagnostics;
using System.Text.Json;

namespace CodeNav.Tests;

/// <summary>
/// Owns: bounded retry for semantic-layer test calls (n7ly family). Under full-suite CPU load
/// the semantic path can transiently degrade (cluster_cold_load / index_snapshot_unavailable /
/// semantic_timeout / refresh_sweep_pending) and auto mode honestly falls back to
/// indexed/heuristic shapes; tests that
/// assert the SEMANTIC shape then die on missing properties or 'indexed' confidence — the
/// suite's dominant rotating one-off family. The documented recovery for those transient
/// reasons IS an immediate retry, so: retry until the caller's predicate accepts the response,
/// and when it never does, fail with the LAST RAW RESPONSE embedded so the red names the
/// degrade that fired instead of a bare KeyNotFoundException.
/// NOT a confidence-based skip: every substantive assertion still runs against the accepted
/// response, and a DETERMINISTIC wrong shape (a pinned regression resurfacing) fails every
/// attempt and stays red — only transient degrades are ridden out.
/// Deliberately does not own: tolerance for wrong-but-stable answers or general watcher/timing
/// waits (WaitUntil owns those). It does own bounded convergence polling when the response itself
/// explicitly reports refresh_sweep_pending.
/// </summary>
internal static class SemanticRetry
{
    private static readonly TimeSpan DefaultRefreshSweepConvergenceTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);

    internal static JsonElement ParseWithRetry(Func<string> call, Func<JsonElement, bool> accept,
        string expectation, int attempts = 3,
        TimeSpan? refreshSweepConvergenceTimeout = null,
        Action<TimeSpan>? delay = null,
        Func<TimeSpan>? convergenceElapsed = null)
    {
        string last = "";
        int ordinaryAttempts = 0;
        int totalCalls = 0;
        var convergenceWait = Stopwatch.StartNew();
        TimeSpan convergenceTimeout = refreshSweepConvergenceTimeout ??
            DefaultRefreshSweepConvergenceTimeout;
        delay ??= Thread.Sleep;
        convergenceElapsed ??= () => convergenceWait.Elapsed;

        while (ordinaryAttempts < attempts)
        {
            if (totalCalls > 0) delay(DefaultRetryDelay);
            totalCalls++;
            last = call();
            using JsonDocument document = JsonDocument.Parse(last);
            JsonElement parsed = document.RootElement.Clone();
            if (accept(parsed)) return parsed;

            // A manager can legitimately re-enter stale after its initial ready state while a
            // watcher convergence sweep is pending. Poll only that explicit lifecycle state;
            // every other rejected response still consumes the caller's ordinary retry budget.
            if (IsRefreshSweepPending(parsed) &&
                convergenceElapsed() < convergenceTimeout)
                continue;

            ordinaryAttempts++;
        }

        Assert.Fail($"response never satisfied '{expectation}' in {ordinaryAttempts} attempts " +
                    $"({totalCalls} total calls including refresh-sweep convergence) — " +
                    $"last response: {last}");
        return default; // unreachable
    }

    /// <summary>Generic form for non-JSON transients (e.g. a git invocation inside ReviewDiff
    /// starved by suite load): retry until accepted; on exhaustion fail with the last state
    /// described. Deterministic wrong states fail every attempt and stay red.</summary>
    internal static T Until<T>(Func<T> call, Func<T, bool> accept, Func<T, string> describe,
        string expectation, int attempts = 3)
    {
        T last = default!;
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) Thread.Sleep(250);
            last = call();
            if (accept(last)) return last;
        }
        Assert.Fail($"state never satisfied '{expectation}' in {attempts} attempts — last: {describe(last)}");
        return default!; // unreachable
    }

    /// <summary>Async counterpart for typed semantic-service calls. The caller owns the
    /// acceptance predicate and final assertions; this helper owns only bounded retry timing so
    /// every typed test uses the same policy.</summary>
    internal static async Task<T> UntilAsync<T>(Func<Task<T>> call, Func<T, bool> accept,
        int attempts = 3)
    {
        T last = default!;
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) await Task.Delay(250);
            last = await call();
            if (accept(last)) return last;
        }
        return last;
    }

    internal static bool IsDocumentedTransient(string? reason) =>
        reason is "index_snapshot_unavailable" or "cluster_cold_load";

    internal static JsonElement ParseExactWithRetry(Func<string> call, int attempts = 3) =>
        ParseWithRetry(call, IsExact, "meta.confidence == 'exact'", attempts);

    private static bool IsExact(JsonElement response) =>
        response.TryGetProperty("meta", out JsonElement meta) &&
        meta.TryGetProperty("confidence", out JsonElement confidence) &&
        confidence.GetString() == "exact";

    private static bool IsRefreshSweepPending(JsonElement response) =>
        response.TryGetProperty("meta", out JsonElement meta) &&
        meta.TryGetProperty("partialReason", out JsonElement reason) &&
        reason.GetString() == "refresh_sweep_pending";
}
