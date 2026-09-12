using System.Text.Json;
using CodeNav.Core.Semantic;

namespace CodeNav.Mcp;

public sealed partial class NavigationTools
{
    private string CompleteFSharpOperation(string tool, string response,
        SemanticService.FSharpSemanticTiming? snapshot)
    {
        // Inspect only the selected, budgeted envelope. Shaping callbacks may run many
        // times and may replace a successful result with a response-too-large error.
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        string? error = root.TryGetProperty("error", out JsonElement value)
            ? value.GetString() : null;
        bool unresolved = error == "fsharp_symbol_not_resolved" ||
            (error is null && root.TryGetProperty("found", out value) && value.ValueKind == JsonValueKind.False);
        string? reason = error ?? (unresolved ? "fsharp_symbol_not_resolved" : null) ?? (root.TryGetProperty("partialReason", out value)
            ? value.GetString() : null);
        string outcome = unresolved ? "unresolved" : error is not null ? "error"
            : root.GetProperty("meta").GetProperty("confidence").GetString() != "exact"
                ? "degraded"
                : root.TryGetProperty("partial", out value) && value.ValueKind == JsonValueKind.True
                    ? "partial" : "exact";
        _semantic.EmitFSharpTelemetry(tool, outcome, reason, snapshot);
        return response;
    }
}
