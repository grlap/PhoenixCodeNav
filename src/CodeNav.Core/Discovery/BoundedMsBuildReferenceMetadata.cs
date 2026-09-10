namespace CodeNav.Core.Discovery;

/// <summary>
/// MSBuild reference metadata roles, independent of the consuming compiler. Copy-local
/// controls output copying, not whether the compiler receives the reference. Structural
/// project discovery retains build edges and does not perform compiler-metadata evaluation.
/// </summary>
internal static class BoundedMsBuildReferenceMetadata
{
    public static bool IsCopyLocal(string name) =>
        name.Equals("Private", StringComparison.OrdinalIgnoreCase);

    public static bool IsProjectIdentityOnly(string name) =>
        name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Project", StringComparison.OrdinalIgnoreCase);

    public static bool ControlsCompilerInclusion(string name) =>
        name.Equals("ReferenceOutputAssembly", StringComparison.OrdinalIgnoreCase);
}
