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
    List<string>? ExplicitCompileItems, // workspace-relative EXACT paths; null => SDK-style project
    List<(string Assembly, string? HintPath)> AssemblyRefs, // legacy <Reference> items; hint paths workspace-relative
    string LoadStatus,                  // "parsed" | "failed:<reason>"
    // Compile-graph fidelity (3tz): a file must only count as compiled when the project REALLY
    // compiles it. Include globs (with their per-item Exclude=) cover legacy wildcard <Compile>
    // (previously skipped -> whole live projects looked orphaned) and SDK explicit <Compile Include>
    // (incl. linked ../ files); Remove globs cover unconditioned, project-level <Compile Remove>
    // (previously ignored -> excluded files looked compiled — the dead-twin bug). Condition
    // attributes on INCLUDES are deliberately ignored (over-inclusion is safe); a conditioned or
    // <Target>-scoped REMOVE is skipped (honoring it would falsely orphan live files).
    List<CompileGlob>? CompileIncludeGlobs = null,  // workspace-relative patterns (may contain * ? **)
    List<string>? CompileRemoveGlobs = null,        // workspace-relative patterns
    bool DefaultCompileItems = true);               // SDK **/*.cs default; false when EnableDefaultCompileItems=false (always false for legacy)

/// <summary>One wildcard/SDK &lt;Compile Include&gt; spec with its own Exclude= filters (Exclude only
/// prunes THAT item's wildcard expansion — the legacy exclusion idiom, since legacy MSBuild has no
/// &lt;Compile Remove&gt;).</summary>
public sealed record CompileGlob(string Include, List<string>? Excludes);

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

    // Legacy projects consume test frameworks as BINARY references (<Reference Include=
    // "nunit.framework"> + HintPath), not packages — the monolith's multi-stage idiom, third
    // appearance (field: HubServiceTests carries [TestFixture] types yet classified production
    // because NUnit never shows up in packageRefs). Simple-name markers checked against
    // AssemblyRefs; QualityTools is the classic MSTest v1 assembly.
    private static readonly string[] TestAssemblyMarkers =
    {
        "nunit.framework", "xunit", "xunit.core",
        "Microsoft.VisualStudio.QualityTools.UnitTestFramework",
        "Microsoft.VisualStudio.TestPlatform.TestFramework",
    };

    // Narrow, DOTTED naming conventions only — a fallback signal, never the classifier. Name
    // shapes are weak predictors (user counterexample: TestRoute.csproj is production routing,
    // not a unit test project), so anything test-framework-shaped must come from REFERENCES:
    // TestPackageMarkers (PackageReference/packages.config) or TestAssemblyMarkers (binary
    // <Reference> — how this monolith actually consumes NUnit). A no-dot "Tests" suffix was
    // considered for the HubServiceTests miss and deliberately rejected in favor of the
    // reference signal, which catches it via nunit.framework.
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
        var includeGlobs = new List<CompileGlob>();
        var removeGlobs = new List<string>();

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
                case "Compile":
                    // Compile-graph fidelity (3tz). Include: legacy exact paths keep the fast explicit
                    // list; wildcards (previously SKIPPED — orphaning whole legacy projects) and all
                    // SDK-side includes (incl. linked ../ files) become match globs. Remove: previously
                    // ignored, so an excluded-from-compilation file counted as compiled (the dead-twin
                    // bug). Condition attributes are ignored on purpose. MSBuild ';'-separated specs
                    // are split; Update= is metadata-only and irrelevant to compiled-ness.
                    if (include is not null)
                    {
                        // Per-item Exclude= prunes THIS include's wildcard expansion (the legacy
                        // exclusion idiom; pre-15 MSBuild has no <Compile Remove>).
                        List<string>? excludes = null;
                        if (item.Attribute("Exclude")?.Value is { } excl)
                        {
                            excludes = excl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(s => NormalizeRelative(projectDir, s)).ToList();
                            if (excludes.Count == 0) excludes = null;
                        }
                        foreach (var spec in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!isSdk && !spec.Contains('*') && !spec.Contains('?'))
                            {
                                compileItems!.Add(NormalizeRelative(projectDir, spec));
                            }
                            else
                            {
                                includeGlobs.Add(new CompileGlob(NormalizeRelative(projectDir, spec), excludes));
                            }
                        }
                    }
                    // A REMOVE is honored only when unconditioned and project-level: a Condition'd
                    // remove (multi-TFM idioms) or a <Target>-scoped remove would falsely ORPHAN live
                    // files — the one direction this graph must never err in (review 2a). Includes
                    // keep ignoring Conditions: over-inclusion is the safe direction.
                    if (item.Attribute("Remove")?.Value is { } remove && !IsConditionedOrTargetScoped(item))
                    {
                        foreach (var spec in remove.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            removeGlobs.Add(NormalizeRelative(projectDir, spec));
                        }
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

        bool defaultCompileItems = isSdk &&
            !string.Equals(Prop(root, "EnableDefaultCompileItems")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

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
                      packageRefs.Any(p => TestPackageMarkers.Any(m => p.Item1.Contains(m, StringComparison.OrdinalIgnoreCase))) ||
                      // Binary-referenced test frameworks (field: [TestFixture] projects whose
                      // NUnit arrives via <Reference>+HintPath, invisible to package markers).
                      // Exact-equality against the standard assembly names, PLUS a Contains on
                      // "nunit.framework" (user: their non-standard custom-resolve reference
                      // shape doesn't reduce to the bare simple name — substring is the contract).
                      assemblyRefs.Any(a =>
                          TestAssemblyMarkers.Any(m => a.Item1.Equals(m, StringComparison.OrdinalIgnoreCase)) ||
                          a.Item1.Contains("nunit.framework", StringComparison.OrdinalIgnoreCase));

        return new ParsedProject(relPath, name, style, guid, tfms, isTest,
            projectRefs.Distinct().ToList(), packageRefs, compileItems, assemblyRefs, "parsed",
            includeGlobs.Count > 0 ? includeGlobs : null,
            removeGlobs.Count > 0 ? removeGlobs : null,
            defaultCompileItems);
    }

    /// <summary>True when the element or any ancestor carries a Condition attribute or is a
    /// &lt;Target&gt; body — contexts where honoring a &lt;Compile Remove&gt; would falsely orphan
    /// live files (the remove may never execute, or only for some TFM).</summary>
    private static bool IsConditionedOrTargetScoped(XElement e) =>
        e.AncestorsAndSelf().Any(a => a.Attribute("Condition") is not null || a.Name.LocalName == "Target");

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
