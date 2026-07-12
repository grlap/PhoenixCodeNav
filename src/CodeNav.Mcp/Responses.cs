using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: JSON serialization policy, response budgets (8KB soft / 24KB hard), and the
/// index-metadata envelope every tool response carries.
/// Does not own: tool logic (NavigationTools) or index queries (CodeNav.Core).
/// </summary>
internal static class Json
{
    // Hard cap kept under the brief's ~32KB so the JSON-RPC envelope + string
    // escaping cannot push the wire message past it.
    public const int SoftBudgetBytes = 8 * 1024;
    public const int HardBudgetBytes = 24 * 1024;

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
