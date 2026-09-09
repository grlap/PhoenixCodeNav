namespace CodeNav.Core.Discovery;

/// <summary>Reserved properties derived from the root project, not the current imported document.</summary>
internal static class BoundedMsBuildProjectContext
{
    internal static bool IsReservedProperty(string name) =>
        name.Equals("MSBuildProjectExtension", StringComparison.OrdinalIgnoreCase);

    internal static void SeedProperties(string? projectPath,
        IDictionary<string, BoundedMsBuildProperty> properties)
    {
        // Missing caller authority is not a reason to guess a language or extension.
        if (string.IsNullOrEmpty(projectPath)) return;
        properties["MSBuildProjectExtension"] = new(Path.GetExtension(projectPath), Complete: true);
    }
}
