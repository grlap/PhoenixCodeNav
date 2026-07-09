using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeNav.Core.Indexing;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: JSON serialization policy, response budgets (8KB soft / 32KB hard), and the
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
        while (Utf8Bytes(json) > cap && work.Count > 1)
        {
            work.RemoveRange(work.Count / 2, work.Count - work.Count / 2);
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
    string? IndexSchema = null) // field (asked twice): key on schema per-response, no capabilities call
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
        string? statusNote = status switch
        {
            "refreshing" => "background non-blocking refresh — results reflect the index as of lastRefreshUtc/indexedAtUtc",
            "stale" => $"watcher changes pending ({h.PendingChanges}) — results may lag the working tree slightly",
            _ => null,
        };
        // ddp (field: "I can programmatically check what's deployed — make it inline"): every
        // response self-identifies its build, ~20 bytes. indexSchema likewise (asked twice) —
        // schema bumps force reindexes, and a caller watching for one shouldn't need a second call.
        return new Meta(status, h.IndexVersion, h.IndexedAtUtc, h.LastRefreshUtc, h.PendingChanges,
            confidence, layer, note, statusNote, BuildInfo.Stamp, BuildInfo.IndexSchema);
    }
}
