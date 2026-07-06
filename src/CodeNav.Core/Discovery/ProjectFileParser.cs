using System.Xml.Linq;

namespace CodeNav.Core.Discovery;

public sealed record ParsedProject(
    string RelPath,
    string Name,
    string Style,                       // "legacy" | "sdk"
    string? Guid,
    string TargetFrameworks,            // ';' joined, may be ""
    bool IsTest,
    List<string> ProjectRefRelPaths,    // workspace-relative, forward slashes
    List<(string Package, string Version)> PackageRefs,
    List<string>? ExplicitCompileItems, // workspace-relative; null => SDK globbing
    List<(string Assembly, string? HintPath)> AssemblyRefs, // legacy <Reference> items; hint paths workspace-relative
    string LoadStatus);                 // "parsed" | "failed:<reason>"

/// <summary>
/// Owns: reading a single .csproj (legacy or SDK style) into a ParsedProject without
/// MSBuild evaluation — raw XML facts only, confidence "indexed" not "exact".
/// Does not own: file discovery, storage, or semantic project loading (M3).
/// </summary>
public static class ProjectFileParser
{
    private static readonly string[] TestPackageMarkers =
    {
        "xunit", "nunit", "mstest.testframework", "microsoft.net.test.sdk",
    };

    private static readonly string[] TestNameSuffixes =
    {
        ".Tests", ".Test", ".UnitTests", ".IntegrationTests", ".Specs",
    };

    public static ParsedProject Parse(string workspaceRoot, string relPath)
    {
        string full = Path.Combine(workspaceRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        string name = Path.GetFileNameWithoutExtension(relPath);
        string projectDir = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";

        XDocument doc;
        try
        {
            doc = XDocument.Load(full);
        }
        catch (Exception ex)
        {
            return new ParsedProject(relPath, name, "unknown", null, "", LooksLikeTestName(name),
                new(), new(), null, new(), $"failed:{ex.GetType().Name}");
        }

        var root = doc.Root!;
        bool isSdk = root.Attribute("Sdk") is not null ||
                     root.Elements().Any(e => e.Name.LocalName == "Sdk") ||
                     root.Descendants().Any(e => e.Name.LocalName == "Import" &&
                         (e.Attribute("Sdk") is not null));
        string style = isSdk ? "sdk" : "legacy";

        string? guid = Prop(root, "ProjectGuid")?.Trim('{', '}');
        string? assemblyName = Prop(root, "AssemblyName");
        if (!string.IsNullOrWhiteSpace(assemblyName) && !assemblyName.Contains('$')) name = assemblyName;

        string tfms = Prop(root, "TargetFrameworks") ?? Prop(root, "TargetFramework") ?? "";
        if (tfms.Length == 0)
        {
            string? legacyTf = Prop(root, "TargetFrameworkVersion"); // e.g. v4.7.2
            if (legacyTf is not null)
            {
                tfms = "net" + legacyTf.TrimStart('v').Replace(".", "");
            }
        }

        var projectRefs = new List<string>();
        var packageRefs = new List<(string, string)>();
        var assemblyRefs = new List<(string, string?)>();
        List<string>? compileItems = isSdk ? null : new List<string>();

        foreach (var item in root.Descendants().Where(e => e.Name.LocalName is "ProjectReference" or "PackageReference" or "Compile" or "Reference"))
        {
            string? include = item.Attribute("Include")?.Value;
            switch (item.Name.LocalName)
            {
                case "ProjectReference" when include is not null:
                    projectRefs.Add(NormalizeRelative(projectDir, include));
                    break;
                case "PackageReference" when include is not null:
                    string version = item.Attribute("Version")?.Value
                        ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                        ?? "";
                    packageRefs.Add((include, version));
                    break;
                case "Compile" when include is not null && !isSdk:
                    // Legacy projects list sources explicitly; globs are rare but possible.
                    if (!include.Contains('*'))
                    {
                        compileItems!.Add(NormalizeRelative(projectDir, include));
                    }
                    break;
                case "Reference" when include is not null:
                    // "Newtonsoft.Json, Version=..., Culture=..." → simple name.
                    string simpleName = include.Split(',')[0].Trim();
                    string? hint = item.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
                    assemblyRefs.Add((simpleName, hint is null ? null : NormalizeRelative(projectDir, hint)));
                    break;
            }
        }

        // packages.config for legacy package refs.
        string packagesConfig = Path.Combine(Path.GetDirectoryName(full)!, "packages.config");
        if (!isSdk && File.Exists(packagesConfig))
        {
            try
            {
                var pkgDoc = XDocument.Load(packagesConfig);
                foreach (var pkg in pkgDoc.Descendants().Where(e => e.Name.LocalName == "package"))
                {
                    string? id = pkg.Attribute("id")?.Value;
                    if (id is not null) packageRefs.Add((id, pkg.Attribute("version")?.Value ?? ""));
                }
            }
            catch
            {
                // packages.config unreadable — package refs stay partial.
            }
        }

        bool isTest = LooksLikeTestName(name) ||
                      packageRefs.Any(p => TestPackageMarkers.Any(m => p.Item1.Contains(m, StringComparison.OrdinalIgnoreCase)));

        return new ParsedProject(relPath, name, style, guid, tfms, isTest,
            projectRefs.Distinct().ToList(), packageRefs, compileItems, assemblyRefs, "parsed");
    }

    private static bool LooksLikeTestName(string name) =>
        TestNameSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static string? Prop(XElement root, string localName) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    /// <summary>Resolves an MSBuild relative include against the project dir into a workspace-relative path.</summary>
    internal static string NormalizeRelative(string projectDir, string include)
    {
        string combined = projectDir.Length == 0 ? include : $"{projectDir}/{include}";
        var parts = new List<string>();
        foreach (var part in combined.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
