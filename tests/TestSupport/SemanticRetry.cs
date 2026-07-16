using System.Text.Json;

namespace CodeNav.Tests;

/// <summary>
/// Owns: bounded retry for semantic-layer test calls (n7ly family). Under full-suite CPU load
/// the semantic path can transiently degrade (cluster_cold_load / index_snapshot_unavailable /
/// semantic_timeout) and auto mode honestly falls back to indexed/heuristic shapes; tests that
/// assert the SEMANTIC shape then die on missing properties or 'indexed' confidence — the
/// suite's dominant rotating one-off family. The documented recovery for those transient
/// reasons IS an immediate retry, so: retry until the caller's predicate accepts the response,
/// and when it never does, fail with the LAST RAW RESPONSE embedded so the red names the
/// degrade that fired instead of a bare KeyNotFoundException.
/// NOT a confidence-based skip: every substantive assertion still runs against the accepted
/// response, and a DETERMINISTIC wrong shape (a pinned regression resurfacing) fails every
/// attempt and stays red — only transient degrades are ridden out.
/// Deliberately does not own: tolerance for wrong-but-stable answers, or watcher/timing waits
/// (WaitUntil owns those).
/// </summary>
internal static class SemanticRetry
{
    internal static JsonElement ParseWithRetry(Func<string> call, Func<JsonElement, bool> accept,
        string expectation, int attempts = 3)
    {
        string last = "";
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) Thread.Sleep(250);
            last = call();
            var parsed = JsonDocument.Parse(last).RootElement;
            if (accept(parsed)) return parsed;
        }
        Assert.Fail($"response never satisfied '{expectation}' in {attempts} attempts — " +
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

    internal static JsonElement ParseExactWithRetry(Func<string> call, int attempts = 3) =>
        ParseWithRetry(call,
            j => j.TryGetProperty("meta", out var meta) &&
                 meta.TryGetProperty("confidence", out var confidence) &&
                 confidence.GetString() == "exact",
            "meta.confidence == 'exact'", attempts);
}
