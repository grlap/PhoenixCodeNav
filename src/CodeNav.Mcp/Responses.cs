using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: JSON serialization policy, response budgets (8KB soft / 64KB hard), and the
/// index-metadata envelope every tool response carries.
/// Does not own: tool logic (NavigationTools) or index queries (CodeNav.Core).
/// </summary>
internal static class Json
{
    // Phoenix's response-size policy, not an MCP transport limit. The larger hard ceiling keeps
    // bounded project/TFM context metadata useful without making ordinary responses less compact.
    public const int SoftBudgetBytes = 8 * 1024;
    public const int HardBudgetBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // The default encoder escapes '<', '&', ''' and EVERY non-ASCII char as 6-byte
        // \uXXXX sequences — source code with non-English comments inflated up to 6x past
        // the byte budgets (review finding). This is JSON-over-stdio, not HTML embedding,
        // so relaxed escaping is safe and keeps wire size ≈ raw size.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>UTF-8 byte length of a serialized payload. The budgets are a wire-BYTE
    /// contract; with the relaxed encoder a non-ASCII char is 1 UTF-16 unit but up to 4 UTF-8
    /// bytes, so <c>string.Length</c> would under-count. Every budget check must go through this.</summary>
    public static int Utf8Bytes(string json) => System.Text.Encoding.UTF8.GetByteCount(json);

    /// <summary>Returns a valid-Unicode prefix whose UTF-8 representation is at most
    /// <paramref name="maxBytes"/> bytes. Capability health fields use this for values whose
    /// producer is outside the response-size contract (workspace paths and failure text).</summary>
    public static string Utf8Prefix(string value, int maxBytes, out bool truncated)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (Utf8Bytes(value) <= maxBytes)
        {
            truncated = false;
            return value;
        }

        var result = new StringBuilder(Math.Min(value.Length, maxBytes));
        int retainedBytes = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (retainedBytes + rune.Utf8SequenceLength > maxBytes) break;
            result.Append(rune.ToString());
            retainedBytes += rune.Utf8SequenceLength;
        }
        truncated = true;
        return result.ToString();
    }

    /// <summary>Returns the longest rune-safe prefix whose serialized JSON string (including
    /// quotes and escapes) fits <paramref name="maxJsonBytes"/>. Raw UTF-8 bounds are insufficient
    /// for hostile control characters because one input byte can become a six-byte \uXXXX escape.</summary>
    public static string JsonStringPrefix(string value, int maxJsonBytes, out bool truncated)
    {
        if (maxJsonBytes < 2) throw new ArgumentOutOfRangeException(nameof(maxJsonBytes));
        int originalBytes = Utf8Bytes(value);
        if (Utf8Bytes(Serialize(value)) <= maxJsonBytes)
        {
            truncated = false;
            return value;
        }

        int low = 0;
        int high = originalBytes;
        string best = "";
        while (low <= high)
        {
            int candidateBytes = low + ((high - low) / 2);
            string candidate = Utf8Prefix(value, candidateBytes, out _);
            if (Utf8Bytes(Serialize(candidate)) <= maxJsonBytes)
            {
                best = candidate;
                low = candidateBytes + 1;
            }
            else
            {
                high = candidateBytes - 1;
            }
        }
        truncated = Utf8Bytes(best) < originalBytes;
        return best;
    }

    /// <summary>Budgets one reflected string against the complete serialized envelope. The
    /// preferred raw-byte cap remains observable through the builder's truncated argument, while
    /// a binary search shrinks further when JSON escaping or fixed metadata requires it.</summary>
    public static string WithStringBudget(string value, int preferredRawBytes,
        Func<string, bool, object> build, int? maxBytes = null)
    {
        int cap = Math.Min(maxBytes ?? HardBudgetBytes, HardBudgetBytes);
        int originalBytes = Utf8Bytes(value);
        int low = 0;
        int high = Math.Min(Math.Max(0, preferredRawBytes), originalBytes);
        string emptyJson = Serialize(build("", originalBytes > 0));
        if (Utf8Bytes(emptyJson) > cap)
            throw new InvalidOperationException("Fixed string envelope exceeds the response budget.");
        string bestJson = emptyJson;

        while (low <= high)
        {
            int candidateBytes = low + ((high - low) / 2);
            string candidate = Utf8Prefix(value, candidateBytes, out _);
            bool truncated = Utf8Bytes(candidate) < originalBytes;
            string json = Serialize(build(candidate, truncated));
            if (Utf8Bytes(json) <= cap)
            {
                bestJson = json;
                low = candidateBytes + 1;
            }
            else
            {
                high = candidateBytes - 1;
            }
        }
        return bestJson;
    }

    /// <summary>Hard-bounds the capability envelope while retaining every feature id. Longest
    /// non-review summaries are removed first, deterministically; review summaries are retained
    /// preferentially because they are the deploy-verification surface for the safety contract.
    /// The root-level coverage fields make any compaction explicit.</summary>
    public static string WithCapabilitiesBudget(object envelope)
    {
        string json = Serialize(envelope);
        if (Utf8Bytes(json) <= HardBudgetBytes) return json;

        JsonObject root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Capability envelope did not serialize as an object.");
        JsonArray features = root["features"]?.AsArray()
            ?? throw new InvalidOperationException("Capability envelope has no feature manifest.");
        var summaries = features
            .Select(node => node?.AsObject())
            .Where(feature => feature is not null && feature["summary"] is not null)
            .Select(feature => new
            {
                Feature = feature!,
                Id = feature!["id"]?.GetValue<string>() ?? "",
                Bytes = Utf8Bytes(feature!["summary"]?.GetValue<string>() ?? ""),
            })
            // Keep review safety summaries available for ordinary deployments whenever possible.
            .OrderBy(item => item.Id.StartsWith("review-", StringComparison.Ordinal) ||
                             item.Id.Equals("review-pack", StringComparison.Ordinal) ? 1 : 0)
            .ThenByDescending(item => item.Bytes)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();

        int summariesReturned = summaries.Count;
        foreach (var item in summaries)
        {
            item.Feature.Remove("summary");
            summariesReturned--;
            root["featuresCompacted"] = true;
            root["featureSummariesReturned"] = summariesReturned;
            json = root.ToJsonString(Options);
            if (Utf8Bytes(json) <= HardBudgetBytes) return json;
        }

        // Defensive last resort for future static capability growth. Dynamic health strings are
        // already bounded by NavigationTools, so this path is not expected for the current
        // manifest. Preserve the feature ids and the core build/index identity while dropping
        // optional explanatory sections in a stable order.
        root["responseCompacted"] = true;
        foreach (string property in new[]
                 {
                     "semantic", "confidenceModel", "tools", "navigationLayers", "languages",
                 })
        {
            root.Remove(property);
            json = root.ToJsonString(Options);
            if (Utf8Bytes(json) <= HardBudgetBytes) return json;
        }

        // Every feature object now contains only its singular id. With today's finite manifest
        // this is far below the cap; fail closed during development if that invariant ever changes.
        if (Utf8Bytes(json) > HardBudgetBytes)
            throw new InvalidOperationException("Feature ids alone exceed the capability hard budget.");
        return json;
    }

    /// <summary>
    /// Serializes build(items, truncated); if the result exceeds the hard budget the
    /// list is shrunk (and truncated=true) until it fits. Operates on a private COPY so it is
    /// idempotent — callers that invoke it repeatedly (e.g. the body-shrink retry loop) get a
    /// full-list shrink each time with an honest truncated flag, not a progressively gutted list.
    /// </summary>
    public static string WithListBudget<T>(List<T> items, Func<List<T>, bool, object> build, int? maxBytes = null)
    {
        int cap = Math.Min(maxBytes ?? HardBudgetBytes, HardBudgetBytes);
        var work = new List<T>(items);
        bool truncated = false;
        string json = Serialize(build(work, truncated));
        // A single oversized item must be droppable too. Stopping at one made callers such as
        // worktrees violate the advertised hard envelope when the only Git worktree path was
        // itself larger than the response budget.
        while (Utf8Bytes(json) > cap && work.Count > 0)
        {
            int keep = work.Count / 2;
            work.RemoveRange(keep, work.Count - keep);
            truncated = true;
            json = Serialize(build(work, truncated));
        }
        return json;
    }

    /// <summary>Budgets a primary result list plus a diagnostic auxiliary list. The auxiliary
    /// list is first reduced to a useful sample, preserving primary answers; if the fixed envelope
    /// still cannot fit, the primary list and finally the sample are reduced to zero. Counts live
    /// outside this helper so callers can report complete totals even when samples are omitted.</summary>
    public static string WithAuxiliaryListBudget<T, TAux>(
        List<T> items,
        List<TAux> auxiliary,
        Func<List<T>, bool, List<TAux>, bool, object> build,
        int? maxBytes = null)
    {
        const int auxiliarySampleItems = 16;
        int cap = Math.Min(maxBytes ?? HardBudgetBytes, HardBudgetBytes);
        var work = new List<T>(items);
        var auxWork = auxiliary.Take(auxiliarySampleItems).ToList();
        bool truncated = false;
        bool auxiliaryTruncated = auxiliary.Count > auxWork.Count;
        string json = Serialize(build(work, truncated, auxWork, auxiliaryTruncated));

        while (Utf8Bytes(json) > cap && work.Count > 0)
        {
            int keep = work.Count / 2;
            work.RemoveRange(keep, work.Count - keep);
            truncated = true;
            json = Serialize(build(work, truncated, auxWork, auxiliaryTruncated));
        }
        while (Utf8Bytes(json) > cap && auxWork.Count > 0)
        {
            int keep = auxWork.Count / 2;
            auxWork.RemoveRange(keep, auxWork.Count - keep);
            auxiliaryTruncated = true;
            json = Serialize(build(work, truncated, auxWork, auxiliaryTruncated));
        }
        return json;
    }

    /// <summary>Budgets a primary result list plus two independent diagnostic lists. Diagnostic
    /// samples are reduced first so primary answers survive whenever possible; every reduction is
    /// reported through its own truncation flag.</summary>
    public static string WithAuxiliaryListsBudget<T, TAux, TSecondary>(
        List<T> items,
        List<TAux> auxiliary,
        List<TSecondary> secondary,
        Func<List<T>, bool, List<TAux>, bool, List<TSecondary>, bool, object> build,
        int? maxBytes = null)
    {
        const int auxiliarySampleItems = 16;
        const int secondarySampleItems = 20;
        int cap = Math.Min(maxBytes ?? HardBudgetBytes, HardBudgetBytes);
        var work = new List<T>(items);
        var auxWork = auxiliary.Take(auxiliarySampleItems).ToList();
        var secondaryWork = secondary.Take(secondarySampleItems).ToList();
        bool truncated = false;
        bool auxiliaryTruncated = auxiliary.Count > auxWork.Count;
        bool secondaryTruncated = secondary.Count > secondaryWork.Count;
        string json = Serialize(build(work, truncated, auxWork, auxiliaryTruncated,
            secondaryWork, secondaryTruncated));

        while (Utf8Bytes(json) > cap && secondaryWork.Count > 0)
        {
            int keep = secondaryWork.Count / 2;
            secondaryWork.RemoveRange(keep, secondaryWork.Count - keep);
            secondaryTruncated = true;
            json = Serialize(build(work, truncated, auxWork, auxiliaryTruncated,
                secondaryWork, secondaryTruncated));
        }
        while (Utf8Bytes(json) > cap && auxWork.Count > 0)
        {
            int keep = auxWork.Count / 2;
            auxWork.RemoveRange(keep, auxWork.Count - keep);
            auxiliaryTruncated = true;
            json = Serialize(build(work, truncated, auxWork, auxiliaryTruncated,
                secondaryWork, secondaryTruncated));
        }
        while (Utf8Bytes(json) > cap && work.Count > 0)
        {
            int keep = work.Count / 2;
            work.RemoveRange(keep, work.Count - keep);
            truncated = true;
            json = Serialize(build(work, truncated, auxWork, auxiliaryTruncated,
                secondaryWork, secondaryTruncated));
        }
        return json;
    }
}

internal sealed record Meta(
    string IndexStatus,
    string? IndexVersion,
    string? IndexedAtUtc,
    string? LastRefreshUtc,
    int PendingChanges,
    string Confidence,
    string NavigationLayer,
    string? ConfidenceNote = null,
    string? StatusNote = null,
    string? Build = null,
    string? IndexSchema = null, // field (asked twice): key on schema per-response, no capabilities call
    string IndexMode = "writer")
{
    public static Meta From(IndexHealth h, string confidence, string layer)
    {
        // Pending watcher changes mean results may lag the working tree.
        string status = h.State == "ready" && h.PendingChanges > 0 ? "stale" : h.State;
        // 47t (field: "repeated on every response — after the first it's noise"): the indexed-tier
        // explainer is GONE from per-response meta — the tier meanings live in
        // server_capabilities.confidenceModel, read once. Only heuristic keeps its warning: it
        // changes how much to trust THIS specific result.
        string? note = confidence == "heuristic"
            ? "naming/text inference — verify before relying on it"
            : null;
        // 9z4 (field: couldn't tell whether 'refreshing' meant "results may be wrong" or "background
        // catch-up, results fine"): one line of meaning, only when the status needs it.
        string? statusNote = h.AccessMode == IndexManager.FollowerAccessMode
            ? "read-only follower — index-backed evidence reflects committed writer state; live source, Git, and semantic evidence may be newer; this process cannot observe the writer's pending queue"
            : status switch
            {
                "refreshing" => "background non-blocking refresh — results reflect the index as of lastRefreshUtc/indexedAtUtc",
                "stale" => $"watcher changes pending ({h.PendingChanges}) — results may lag the working tree slightly",
                _ => null,
            };
        // ddp (field: "I can programmatically check what's deployed — make it inline"): every
        // response self-identifies its build, ~20 bytes. indexSchema likewise (asked twice) —
        // schema bumps force reindexes, and a caller watching for one shouldn't need a second call.
        return new Meta(status, h.IndexVersion, h.IndexedAtUtc, h.LastRefreshUtc, h.PendingChanges,
            confidence, layer, note, statusNote, BuildInfo.Stamp, BuildInfo.IndexSchema,
            h.AccessMode);
    }
}
