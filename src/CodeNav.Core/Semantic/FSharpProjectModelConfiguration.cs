namespace CodeNav.Core.Semantic;

internal static class FSharpProjectModelConfiguration
{
    internal static ProjectModelMode Select(ProjectModelMode? requested, Action<string>? log = null)
    {
        if (requested is { } value) return value;
        string? configured = Environment.GetEnvironmentVariable("PHOENIX_FSHARP_PROJECT_MODEL")?.Trim();
        if (string.Equals(configured, "evaluated", StringComparison.OrdinalIgnoreCase))
            return ProjectModelMode.Evaluated;
        if (!string.IsNullOrEmpty(configured) &&
            !configured.Equals("simple", StringComparison.OrdinalIgnoreCase))
            log?.Invoke("Unrecognized PHOENIX_FSHARP_PROJECT_MODEL; using simple. Expected simple or evaluated.");
        return ProjectModelMode.Simple;
    }
}
