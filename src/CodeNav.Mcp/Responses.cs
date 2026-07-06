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
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Serializes build(items, truncated); if the result exceeds the hard budget the
    /// list is shrunk (and truncated=true) until it fits.
    /// </summary>
    public static string WithListBudget<T>(List<T> items, Func<List<T>, bool, object> build, int? maxBytes = null)
    {
        int cap = Math.Min(maxBytes ?? HardBudgetBytes, HardBudgetBytes);
        bool truncated = false;
        string json = Serialize(build(items, truncated));
        while (json.Length > cap && items.Count > 1)
        {
            items.RemoveRange(items.Count / 2, items.Count - items.Count / 2);
            truncated = true;
            json = Serialize(build(items, truncated));
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
    string NavigationLayer)
{
    public static Meta From(IndexHealth h, string confidence, string layer)
    {
        // Pending watcher changes mean results may lag the working tree.
        string status = h.State == "ready" && h.PendingChanges > 0 ? "stale" : h.State;
        return new Meta(status, h.IndexVersion, h.IndexedAtUtc, h.LastRefreshUtc, h.PendingChanges, confidence, layer);
    }
}
