namespace CodeNav.Core.Discovery;

/// <summary>Lexical MSBuild path values from captured caller authority, never the process CWD.
/// Document values are resolved at each expression, so assignments capture their defining file.</summary>
internal sealed class BoundedMsBuildProjectContext
{
    private static readonly HashSet<string> RootProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "MSBuildProjectDirectory", "MSBuildProjectDirectoryNoRoot", "MSBuildProjectFullPath",
        "MSBuildProjectFile", "MSBuildProjectName", "MSBuildProjectExtension",
    };
    private static readonly HashSet<string> DocumentProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "MSBuildThisFileDirectory", "MSBuildThisFileDirectoryNoRoot", "MSBuildThisFileFullPath",
        "MSBuildThisFile", "MSBuildThisFileName", "MSBuildThisFileExtension",
    };
    // Known host/toolchain inputs are not optional user properties. No values are inferred from
    // Phoenix's runtime or environment; explicit project assignments retain normal authority.
    private static readonly HashSet<string> UnprovidedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "MSBuildAssemblyVersion", "MSBuildBinPath", "MSBuildDisableFeaturesFromVersion",
        "MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildExtensionsPath64",
        "MSBuildFileVersion", "MSBuildFrameworkToolsPath", "MSBuildFrameworkToolsPath32",
        "MSBuildFrameworkToolsPath64", "MSBuildInteractive", "MSBuildLastTaskResult",
        "MSBuildNodeCount", "MSBuildOverrideTasksPath", "MSBuildProgramFiles32",
        "MSBuildProjectDefaultTargets", "MSBuildRuntimeType", "MSBuildStartupDirectory",
        "MSBuildToolsPath", "MSBuildToolsPath32", "MSBuildToolsPath64", "MSBuildToolsVersion",
        "MSBuildSDKsPath", "MSBuildSemanticVersion", "MSBuildUserExtensionsPath", "MSBuildVersion",
        "OS", "FrameworkSDKRoot", "RoslynTargetsPath", "SDK35ToolsPath", "SDK40ToolsPath",
        "VsInstallRoot", "WindowsSDK80Path",
    };

    private readonly string? _projectPath;
    private readonly string? _workspaceRoot;

    internal BoundedMsBuildProjectContext(string? projectPath, string? workspaceRoot = null)
    {
        _projectPath = string.IsNullOrEmpty(projectPath) ? null : HostPath(projectPath);
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot)) return;
        try { _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    internal static bool IsRootReservedProperty(string name) => RootProperties.Contains(name);
    internal static bool IsDocumentReservedProperty(string name) => DocumentProperties.Contains(name);
    internal static bool IsReservedProperty(string name) =>
        IsRootReservedProperty(name) || IsDocumentReservedProperty(name);
    internal static bool IsKnownProvidedProperty(string name) =>
        IsReservedProperty(name) || UnprovidedProperties.Contains(name);

    internal static void SeedProperties(string? projectPath,
        IDictionary<string, BoundedMsBuildProperty> properties) =>
        new BoundedMsBuildProjectContext(projectPath).SeedProperties(properties);

    internal void SeedProperties(IDictionary<string, BoundedMsBuildProperty> properties)
    {
        // Only root values belong in the shared bag or the global invariant proof.
        foreach (string name in RootProperties)
            if (Resolve(name, null) is { } value) properties[name] = value;
    }

    internal BoundedMsBuildProperty? Resolve(string name, string? documentPath)
    {
        bool rootProperty = IsRootReservedProperty(name);
        if (!rootProperty && !IsDocumentReservedProperty(name)) return null;
        string? path = rootProperty ? _projectPath : string.IsNullOrEmpty(documentPath) ? null : HostPath(documentPath);
        if (path is null) return null;
        string suffix = name[(rootProperty ? "MSBuildProject".Length : "MSBuildThisFile".Length)..];
        // File/name/extension do not require absolute-root authority.
        string? value = suffix.ToUpperInvariant() switch
        {
            "" when !rootProperty => Path.GetFileName(path),
            "FILE" when rootProperty => Path.GetFileName(path),
            "NAME" => Path.GetFileNameWithoutExtension(path),
            "EXTENSION" => Path.GetExtension(path),
            _ => null,
        };
        if (value is not null) return BoundedMsBuildProperty.Native(value);
        if (!TryGetFullPath(path, out string fullPath)) return null;
        if (suffix.Equals("FullPath", StringComparison.OrdinalIgnoreCase)) return BoundedMsBuildProperty.Native(fullPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        if (suffix.Equals("DirectoryNoRoot", StringComparison.OrdinalIgnoreCase))
            directory = directory[Path.GetPathRoot(directory)!.Length..];
        // Document directories always end in a separator, even when removing the host root
        // leaves nothing. ProjectDirectoryNoRoot, in contrast, is empty at the host root.
        if (!rootProperty && !Path.EndsInDirectorySeparator(directory)) directory += Path.DirectorySeparatorChar;
        return BoundedMsBuildProperty.Native(directory);
    }

    internal bool TryMakeWorkspaceRelative(string path, out string relative)
    {
        relative = "";
        if (_workspaceRoot is null || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            relative = WorkspacePaths.ToGitPath(Path.GetRelativePath(_workspaceRoot, Path.GetFullPath(path)));
            return relative != "." && relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) &&
                   !Path.IsPathRooted(relative) && !relative.Contains(':');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private bool TryGetFullPath(string path, out string fullPath)
    {
        fullPath = "";
        if (_workspaceRoot is null) return false;
        try
        {
            // Explicit base prevents relative paths and drive-relative forms from consulting CWD.
            if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)) return false;
            fullPath = Path.GetFullPath(path, _workspaceRoot);
            return TryMakeWorkspaceRelative(fullPath, out _);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string HostPath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar);
}
