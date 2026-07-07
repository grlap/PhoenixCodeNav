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
    string? ConfidenceNote = null)
{
    public static Meta From(IndexHealth h, string confidence, string layer)
    {
        // Pending watcher changes mean results may lag the working tree.
        string status = h.State == "ready" && h.PendingChanges > 0 ? "stale" : h.State;
        // Surface what the label means in the payload itself (feedback: priming alone is
        // not enough). Kept terse — this rides on every non-exact response.
        string? note = confidence switch
        {
            "indexed" => "index/syntax-backed, not compiler-verified",
            "heuristic" => "naming/text inference — verify before relying on it",
            _ => null,
        };
        return new Meta(status, h.IndexVersion, h.IndexedAtUtc, h.LastRefreshUtc, h.PendingChanges, confidence, layer, note);
    }
}
