using System.Xml.Linq;
using System.Xml;

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
    bool DefaultCompileItems = true,                // SDK **/*.cs default; false when EnableDefaultCompileItems=false (always false for legacy)
    bool CompileOwnershipComplete = true,           // false for bounded review shapes with imports the raw XML cannot evaluate
    List<CompileMembershipOperation>? CompileOperations = null,
    // Literal project-root items from the standard SDK assembly-info generation path only.
    // SemanticWorkspace materializes these as generated assembly attributes so friend projects
    // bind internal source symbols exactly. Unsupported MSBuild evaluation shapes fail closed.
    List<string>? InternalsVisibleTo = null,
    // True only when this project file itself closes every authority boundary that can alter the
    // generated IVT attribute. SemanticWorkspace additionally checks applicable Directory.Build
    // files from the pinned index before allowing compiler-exact claims.
    bool InternalsVisibleToAuthorityComplete = true);

/// <summary>One wildcard/SDK &lt;Compile Include&gt; spec with its own Exclude= filters (Exclude only
/// prunes THAT item's wildcard expansion — the legacy exclusion idiom, since legacy MSBuild has no
/// &lt;Compile Remove&gt;).</summary>
public sealed record CompileGlob(string Include, List<string>? Excludes);
public sealed record CompileMembershipOperation(bool Include, string Pattern,
    List<string>? Excludes = null);

/// <summary>
/// Owns: reading a single .csproj (legacy or SDK style) into a ParsedProject without
/// MSBuild evaluation — raw XML facts only, confidence "indexed" not "exact".
/// Does not own: file discovery, storage, or semantic project loading (M3).
/// </summary>
public static class ProjectFileParser
{
    private const int MaxSnapshotBytes = 16 * 1024 * 1024;

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
        "nunit.framework", "xunit", "xunit.core", "xunit.assert",
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

        XDocument? packagesDoc = null;
        string packagesPath = Path.Combine(Path.GetDirectoryName(full)!, "packages.config");
        if (!IsSdkProject(doc.Root!) && File.Exists(packagesPath))
        {
            try { packagesDoc = XDocument.Load(packagesPath); }
            catch { /* packages.config remains optional/partial */ }
        }
        return ParseDocuments(relPath, doc, packagesDoc);
    }

    /// <summary>Parses a project from the exact bounded, no-follow byte snapshots captured by the
    /// indexer. The optional packages.config bytes participate in the same parse epoch.</summary>
    public static ParsedProject ParseSnapshot(string relPath, byte[] projectBytes,
        byte[]? packagesConfigBytes = null)
    {
        string name = Path.GetFileNameWithoutExtension(relPath);
        try
        {
            XDocument project = LoadSnapshotXml(projectBytes);
            XDocument? packages = null;
            if (packagesConfigBytes is not null)
            {
                try { packages = LoadSnapshotXml(packagesConfigBytes); }
                catch { /* packages.config remains optional/partial */ }
            }
            return ParseDocuments(relPath, project, packages);
        }
        catch (Exception ex)
        {
            return new ParsedProject(relPath, name, "unknown", null, "",
                LooksLikeTestName(name), [], [], null, [], $"failed:{ex.GetType().Name}");
        }
    }

    private static XDocument LoadSnapshotXml(byte[] bytes)
    {
        if (bytes.Length > MaxSnapshotBytes)
            throw new InvalidDataException("project XML exceeds the snapshot limit");
        using var stream = new MemoryStream(bytes, writable: false);
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxSnapshotBytes,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static ParsedProject ParseDocuments(string relPath, XDocument doc,
        XDocument? packagesConfig)
    {
        string name = Path.GetFileNameWithoutExtension(relPath);
        string projectDir = WorkspacePaths.ToGitPath(Path.GetDirectoryName(relPath) ?? "");

        var root = doc.Root!;
        bool isSdk = IsSdkProject(root);
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

        var internalsVisibleTo = ParseGeneratedInternalsVisibleTo(root, isSdk);
        bool internalsVisibleToAuthorityComplete = internalsVisibleTo is null ||
            HasClosedInternalsVisibleToAuthority(root, isSdk, packageRefs);

        bool defaultCompileItems = isSdk &&
            !string.Equals(Prop(root, "EnableDefaultCompileItems")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

        // packages.config for legacy package refs. Snapshot callers supply the exact bounded bytes
        // captured alongside the project, so graph facts cannot come from a later filesystem epoch.
        if (!isSdk && packagesConfig is not null)
        {
            try
            {
                foreach (var pkg in packagesConfig.Descendants().Where(e =>
                             e.Name.LocalName == "package"))
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
            defaultCompileItems,
            InternalsVisibleTo: internalsVisibleTo,
            InternalsVisibleToAuthorityComplete: internalsVisibleToAuthorityComplete);
    }

    /// <summary>Parses only the compile-item shape from an already bounded, no-follow project-file
    /// snapshot. Review fallback uses this instead of reopening every project and packages.config.</summary>
    public static ParsedProject ParseCompileShape(string relPath, byte[] xmlBytes)
    {
        string name = Path.GetFileNameWithoutExtension(relPath);
        string projectDir = WorkspacePaths.ToGitPath(Path.GetDirectoryName(relPath) ?? "");
        XDocument doc;
        try
        {
            using var stream = new MemoryStream(xmlBytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 512 * 1024,
            });
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception ex)
        {
            return new ParsedProject(relPath, name, "unknown", null, "", false,
                [], [], null, [], $"failed:{ex.GetType().Name}");
        }

        XElement? root = doc.Root;
        if (root is null)
        {
            return new ParsedProject(relPath, name, "unknown", null, "", false,
                [], [], null, [], "failed:missing_root");
        }
        bool isSdk = IsSdkProject(root);
        List<string>? compileItems = isSdk ? null : [];
        var includeGlobs = new List<CompileGlob>();
        var removeGlobs = new List<string>();
        var compileOperations = new List<CompileMembershipOperation>();
        bool hasUnevaluatedCompileSpec = false;
        foreach (XElement item in root.Descendants().Where(element =>
                     element.Name.LocalName == "Compile"))
        {
            if (item.Parent?.Name.LocalName != "ItemGroup" || item.Parent.Parent != root)
            {
                hasUnevaluatedCompileSpec = true;
                continue;
            }
            hasUnevaluatedCompileSpec |= item.AncestorsAndSelf().Any(ancestor =>
                ancestor.Attribute("Condition") is not null ||
                ancestor.Name.LocalName is "Target" or "Choose" or "When" or "Otherwise");
            if (item.Attribute("Include")?.Value is { } include)
            {
                hasUnevaluatedCompileSpec |= ContainsMsBuildExpression(include);
                List<string>? excludes = null;
                if (item.Attribute("Exclude")?.Value is { } exclude)
                {
                    excludes = [];
                    foreach (string spec in exclude.Split(';',
                                 StringSplitOptions.RemoveEmptyEntries |
                                 StringSplitOptions.TrimEntries))
                    {
                        if (TryNormalizeCompileSpec(projectDir, spec, out string normalized))
                            excludes.Add(normalized);
                        else
                            hasUnevaluatedCompileSpec = true;
                    }
                }
                if (item.Attribute("Exclude")?.Value is { } excludeValue)
                    hasUnevaluatedCompileSpec |= ContainsMsBuildExpression(excludeValue);
                if (excludes is { Count: 0 }) excludes = null;
                foreach (string spec in include.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                                           StringSplitOptions.TrimEntries))
                {
                    if (!TryNormalizeCompileSpec(projectDir, spec, out string normalizedSpec))
                    {
                        hasUnevaluatedCompileSpec = true;
                        continue;
                    }
                    compileOperations.Add(new CompileMembershipOperation(true, normalizedSpec,
                        excludes));
                    if (!isSdk && !spec.Contains('*') && !spec.Contains('?'))
                        compileItems!.Add(normalizedSpec);
                    else
                        includeGlobs.Add(new CompileGlob(normalizedSpec, excludes));
                }
            }
            if (item.Attribute("Remove")?.Value is { } removeValue)
                hasUnevaluatedCompileSpec |= ContainsMsBuildExpression(removeValue);
            if (item.Attribute("Remove")?.Value is { } remove &&
                !IsConditionedOrTargetScoped(item))
            {
                foreach (string spec in remove.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                                          StringSplitOptions.TrimEntries))
                {
                    if (!TryNormalizeCompileSpec(projectDir, spec, out string normalizedSpec))
                    {
                        hasUnevaluatedCompileSpec = true;
                        continue;
                    }
                    removeGlobs.Add(normalizedSpec);
                    compileOperations.Add(new CompileMembershipOperation(false, normalizedSpec));
                }
            }
        }
        List<XElement> defaultCompileProperties = root.Descendants().Where(element =>
            element.Name.LocalName == "EnableDefaultCompileItems").ToList();
        string? defaultCompileValue = defaultCompileProperties.FirstOrDefault()?.Value.Trim();
        bool recognizedDefaultCompileValue = defaultCompileValue is null ||
            defaultCompileValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            defaultCompileValue.Equals("false", StringComparison.OrdinalIgnoreCase);
        bool defaultCompileItems = isSdk && (defaultCompileValue is null ||
            defaultCompileValue.Equals("true", StringComparison.OrdinalIgnoreCase));
        bool defaultCompileEvaluationComplete = defaultCompileProperties.Count <= 1 &&
            recognizedDefaultCompileValue &&
            defaultCompileProperties.All(property =>
                !ContainsMsBuildExpression(property.Value) &&
                !property.AncestorsAndSelf().Any(ancestor =>
                    ancestor.Attribute("Condition") is not null));
        bool hasUnevaluatedDefaultMembership = root.Descendants()
            .Any(element => IsUnevaluatedCompileMembershipProperty(element.Name.LocalName));
        bool hasPackageBuildImports = root.Descendants().Any(element =>
            element.Name.LocalName == "PackageReference");
        string? rootSdk = root.Attribute("Sdk")?.Value;
        bool knownRootSdk = rootSdk is null || rootSdk
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(sdk => sdk.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) ||
                        sdk.StartsWith("Microsoft.NET.Sdk/", StringComparison.OrdinalIgnoreCase));
        bool compileOwnershipComplete = knownRootSdk && defaultCompileEvaluationComplete &&
            !hasUnevaluatedCompileSpec && !hasUnevaluatedDefaultMembership &&
            !hasPackageBuildImports &&
            !root.Elements().Any(element => element.Name.LocalName == "Sdk") &&
            !root.Descendants().Any(element => element.Name.LocalName == "Import");
        return new ParsedProject(relPath, name, isSdk ? "sdk" : "legacy", null, "", false,
            [], [], compileItems, [], "parsed",
            includeGlobs.Count > 0 ? includeGlobs : null,
            removeGlobs.Count > 0 ? removeGlobs : null, defaultCompileItems,
            compileOwnershipComplete, compileOperations);
    }

    private static bool IsSdkProject(XElement root) =>
        root.Attribute("Sdk") is not null ||
        root.Elements().Any(element => element.Name.LocalName == "Sdk") ||
        root.Descendants().Any(element => element.Name.LocalName == "Import" &&
            element.Attribute("Sdk") is not null);

    /// <summary>
    /// Models the narrow SDK target contract that converts InternalsVisibleTo items into assembly
    /// attributes. Operations that cannot be modeled locally return no grants. Plain unsupported
    /// Includes are also omitted rather than granting access Phoenix cannot prove from this file.
    /// </summary>
    private const int MaxInternalsVisibleToOperations = 4096;

    private static bool HasClosedInternalsVisibleToAuthority(XElement root, bool isSdk,
        IReadOnlyCollection<(string Package, string Version)> packageRefs)
    {
        // This is deliberately not an import evaluator. A single standard root SDK plus local XML
        // is the only closed shape. Explicit/custom SDK imports and package build assets remain
        // useful as modeled candidates, but their responses must disclose project_model_unproven.
        string[] rootSdks = (root.Attribute("Sdk")?.Value ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool standardRootSdk = rootSdks.Length == 1 &&
            (rootSdks[0].Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) ||
             rootSdks[0].StartsWith("Microsoft.NET.Sdk/", StringComparison.OrdinalIgnoreCase));
        return isSdk && standardRootSdk && packageRefs.Count == 0 &&
               !root.Elements().Any(element => element.Name.LocalName == "Sdk") &&
               !root.Descendants().Any(element => element.Name.LocalName == "Import");
    }

    private static List<string>? ParseGeneratedInternalsVisibleTo(XElement root, bool isSdk)
    {
        // A legacy project can use an item with this name for arbitrary custom targets. Require
        // the implicit SDK import shape that owns Microsoft.NET.GenerateAssemblyInfo.targets.
        bool hasRootSdk = root.Attribute("Sdk") is not null ||
                          root.Elements().Any(element => element.Name.LocalName == "Sdk");
        if (!isSdk || !hasRootSdk)
        {
            return null;
        }

        var assemblyInfo = EvaluateSdkBooleanProperty(root, "GenerateAssemblyInfo");
        var ivtAttributes = EvaluateSdkBooleanProperty(root,
            "GenerateInternalsVisibleToAttributes");
        if (!assemblyInfo.Complete || !ivtAttributes.Complete ||
            HasNonEmptyOrUnevaluatedProjectProperty(root, "PublicKey") ||
            HasNonEmptyOrUnevaluatedProjectProperty(root, "AssemblyOriginatorKeyFile") ||
            HasEnabledOrUnevaluatedBooleanProjectProperty(root, "SignAssembly") ||
            HasEnabledOrUnevaluatedBooleanProjectProperty(root, "PublicSign") ||
            HasInternalsVisibleToItemDefinitionKey(root))
        {
            return null;
        }
        if (!assemblyInfo.Enabled || !ivtAttributes.Enabled)
        {
            return null; // the SDK target is disabled in this project.
        }

        var friends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int operations = 0;
        foreach (XElement item in root.Elements()
                     .Where(element => element.Name.LocalName == "ItemGroup")
                     .SelectMany(group => group.Elements()
                         .Where(element => MsBuildNameEquals(
                             element.Name.LocalName, "InternalsVisibleTo"))))
        {
            if (++operations > MaxInternalsVisibleToOperations) return null;
            string? include = item.Attribute("Include")?.Value;
            string? remove = item.Attribute("Remove")?.Value;
            string? update = item.Attribute("Update")?.Value;
            int operationCount = (include is null ? 0 : 1) + (remove is null ? 0 : 1) +
                                 (update is null ? 0 : 1);
            if (operationCount != 1) return null;

            bool conditioned = IsConditionedOrTargetScoped(item);
            if (conditioned)
            {
                // A conditional Include can safely be omitted. A conditional Remove/Update may
                // revoke/key a grant that we would otherwise synthesize, so the model is unknown.
                if (remove is not null || update is not null) return null;
                continue;
            }

            bool hasKeyMetadata = HasItemMetadata(item, "Key") ||
                                  HasItemMetadata(item, "PublicKey");
            if (include is not null)
            {
                // Keyed IVTs are valid build inputs, but Phoenix cannot prove the consuming
                // project's signing identity. Omitting the one item is the safe direction.
                if (hasKeyMetadata) continue;
                HashSet<string>? excluded = ParseLiteralAssemblyNames(
                    item.Attribute("Exclude")?.Value, allowMissing: true);
                if (excluded is null && item.Attribute("Exclude") is not null) continue;

                HashSet<string>? included = ParseLiteralAssemblyNames(include, allowMissing: false);
                if (included is null) continue;
                foreach (string friend in included)
                {
                    if (excluded?.Contains(friend) == true) continue;
                    friends.Add(friend);
                }
                continue;
            }

            string operation = remove ?? update!;
            HashSet<string>? affected = ParseLiteralAssemblyNames(operation, allowMissing: false);
            if (affected is null) return null;
            if (remove is not null || hasKeyMetadata)
            {
                friends.ExceptWith(affected);
            }
        }

        // Target/Choose-scoped Includes are safe to omit. A hidden Remove/Update is not: it can
        // invalidate a direct item above for a configuration we did not evaluate.
        foreach (XElement nested in root.Descendants().Where(element =>
                     MsBuildNameEquals(element.Name.LocalName, "InternalsVisibleTo") &&
                     element.Parent?.Name.LocalName != "ItemGroup" ||
                     MsBuildNameEquals(element.Name.LocalName, "InternalsVisibleTo") &&
                     element.Parent?.Parent != root))
        {
            if (nested.Attribute("Remove") is not null || nested.Attribute("Update") is not null)
                return null;
        }

        return friends.Count > 0
            ? friends.OrderBy(friend => friend, StringComparer.OrdinalIgnoreCase).ToList()
            : null;
    }

    private static (bool Enabled, bool Complete) EvaluateSdkBooleanProperty(
        XElement root, string propertyName)
    {
        bool enabled = true; // Microsoft.NET.GenerateAssemblyInfo.targets default.
        foreach (XElement property in root.Descendants().Where(element =>
                     MsBuildNameEquals(element.Name.LocalName, propertyName) &&
                     element.Parent?.Name.LocalName == "PropertyGroup"))
        {
            bool direct = property.Parent?.Name.LocalName == "PropertyGroup" &&
                          property.Parent.Parent == root;
            string value = property.Value.Trim();
            if (!direct || IsConditionedOrTargetScoped(property) ||
                ContainsMsBuildExpression(value) || !bool.TryParse(value, out enabled))
            {
                return (false, false);
            }
        }
        return (enabled, true);
    }

    private static bool HasNonEmptyOrUnevaluatedProjectProperty(XElement root, string propertyName)
    {
        foreach (XElement property in root.Descendants().Where(element =>
                     MsBuildNameEquals(element.Name.LocalName, propertyName) &&
                     element.Parent?.Name.LocalName == "PropertyGroup"))
        {
            bool direct = property.Parent?.Name.LocalName == "PropertyGroup" &&
                          property.Parent.Parent == root;
            string value = property.Value.Trim();
            if (!direct || IsConditionedOrTargetScoped(property) ||
                ContainsMsBuildExpression(value) || value.Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasEnabledOrUnevaluatedBooleanProjectProperty(
        XElement root, string propertyName)
    {
        foreach (XElement property in root.Descendants().Where(element =>
                     MsBuildNameEquals(element.Name.LocalName, propertyName) &&
                     element.Parent?.Name.LocalName == "PropertyGroup"))
        {
            bool direct = property.Parent?.Parent == root;
            string value = property.Value.Trim();
            if (!direct || IsConditionedOrTargetScoped(property) ||
                ContainsMsBuildExpression(value) || !bool.TryParse(value, out bool enabled) ||
                enabled)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasInternalsVisibleToItemDefinitionKey(XElement root) =>
        root.Descendants().Any(item =>
            MsBuildNameEquals(item.Name.LocalName, "InternalsVisibleTo") &&
            item.Ancestors().Any(ancestor => ancestor.Name.LocalName == "ItemDefinitionGroup") &&
            (HasItemMetadata(item, "Key") || HasItemMetadata(item, "PublicKey")));

    private static bool HasItemMetadata(XElement item, string name) =>
        item.Attributes().Any(attribute => MsBuildNameEquals(attribute.Name.LocalName, name)) ||
        item.Elements().Any(element => MsBuildNameEquals(element.Name.LocalName, name));

    private static bool MsBuildNameEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static HashSet<string>? ParseLiteralAssemblyNames(string? value, bool allowMissing)
    {
        if (value is null) return allowMissing
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string spec in value.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ContainsMsBuildExpression(spec) || spec.IndexOfAny(['*', '?', ',']) >= 0)
                return null;
            result.Add(spec);
        }
        return result.Count > 0 ? result : null;
    }

    private static bool ContainsMsBuildExpression(string value) =>
        value.Contains("$(", StringComparison.Ordinal) ||
        value.Contains("@(", StringComparison.Ordinal) ||
        value.Contains("%(", StringComparison.Ordinal);

    private static bool TryNormalizeCompileSpec(string projectDir, string spec,
        out string normalized)
    {
        normalized = "";
        string portable = spec.Replace('\\', '/');
        if (portable.StartsWith('/') ||
            (portable.Length >= 2 && char.IsLetter(portable[0]) && portable[1] == ':'))
            return false;
        var parts = projectDir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string part in portable.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return false;
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }
        normalized = string.Join('/', parts);
        return normalized.Length > 0;
    }

    private static bool IsUnevaluatedCompileMembershipProperty(string name) =>
        name is "EnableDefaultItems" or "DefaultItemExcludes" or
            "DefaultItemExcludesInProjectFolder" or "DefaultExcludesInProjectFolder" or
            "BaseOutputPath" or "BaseIntermediateOutputPath" or "OutputPath" or
            "IntermediateOutputPath" or "MSBuildProjectExtensionsPath" or
            "ArtifactsPath" or "UseArtifactsOutput" or "DefaultLanguageSourceExtension" or
            "MSBuildExtensionsPath" or
            "MSBuildUserExtensionsPath" or "MSBuildToolsVersion" or
            "CustomBeforeMicrosoftCommonProps" or "CustomAfterMicrosoftCommonProps" or
            "CustomBeforeMicrosoftCommonTargets" or "CustomAfterMicrosoftCommonTargets" or
            "CustomBeforeMicrosoftCommonCrossTargetingTargets" or
            "CustomAfterMicrosoftCommonCrossTargetingTargets" or
            "CustomBeforeMicrosoftCSharpTargets" or "CustomAfterMicrosoftCSharpTargets" ||
        name.StartsWith("ImportByWildcard", StringComparison.Ordinal) ||
        name.StartsWith("ImportUserLocationsByWildcard", StringComparison.Ordinal);

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
        // projectDir is already in the canonical Git/index domain. Only the MSBuild-authored
        // include accepts either slash style; rewriting the combined string would reinterpret a
        // legal literal backslash in a Unix project-directory name as a separator.
        var parts = projectDir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var part in include.Replace('\\', '/').Split('/',
                     StringSplitOptions.RemoveEmptyEntries))
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
