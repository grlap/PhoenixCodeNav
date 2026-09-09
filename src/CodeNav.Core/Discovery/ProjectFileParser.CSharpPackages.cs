using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodeNav.Core.Discovery;

public static partial class ProjectFileParser
{
    internal const int MaxCSharpCentralPackageProperties = 1024;
    internal const int MaxCSharpCentralPackagePropertyExpansions = 4096;
    internal const int MaxCSharpCentralPackagePropertyValueChars = 16 * 1024;
    internal const int MaxCSharpCentralPackageExpandedPropertyChars = 256 * 1024;

    private static readonly Regex CSharpCentralPackageId = new(
        @"^[A-Za-z0-9_][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    private static readonly Regex CSharpCentralPropertyName = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>Projects the standard unconditional CPM shape needed by the existing C# semantic
    /// package loader, including bounded simple local-property expansion. Authority comes from
    /// the pinned indexed Directory.Packages.props snapshot and unshadowed root SDK context.
    /// Shapes that require broader MSBuild
    /// evaluation retain the established unresolved-reference behavior instead of guessing a package
    /// version; once selected, a central version never falls back to another cache directory.</summary>
    internal static List<CSharpPackageReferenceSnapshot> EvaluateCSharpPackageReferencesSnapshot(
        byte[] projectBytes,
        IReadOnlyList<(string Package, string Version)> packageReferences,
        string? directoryPackagesXml,
        bool hasAmbiguousDirectoryPackagesAuthority,
        bool hasPotentialLateProjectPropertyAuthority = false,
        bool hasPotentialImportedSdkPropertyAuthority = false)
    {
        var direct = packageReferences.Select(reference =>
            new CSharpPackageReferenceSnapshot(reference.Package, reference.Version)).ToList();
        if (hasAmbiguousDirectoryPackagesAuthority || directoryPackagesXml is null)
            return direct;

        XDocument project;
        XDocument central;
        try
        {
            project = LoadSnapshotXml(projectBytes);
            central = LoadCSharpCentralPackageXml(directoryPackagesXml);
        }
        catch
        {
            return direct;
        }
        if (project.Root is null || central.Root is null ||
            central.Root.Attributes().Any(a => a.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)) ||
            central.Descendants().Any(e => NameEquals(e, "Sdk") || NameEquals(e, "Import")))
            return direct;

        // C# still admits only pinned unconditional XML. Item semantics below are shared with F#;
        // this adapter does not acquire imported property/condition authority on its caller's behalf.
        XElement[] items = new[] { central, project }.SelectMany(document =>
            document.Descendants().Where(e => BoundedMsBuildPackageEvaluator.IsPackageItem(e.Name.LocalName))).ToArray();
        if (items.Any(item => item.Parent is not { } group || !NameEquals(group, "ItemGroup") ||
                group.Parent != item.Document?.Root || HasCondition(item) || HasCondition(group) ||
                item.Elements().Any(HasCondition)))
            return direct;

        var flags = new Dictionary<string, BoundedMsBuildProperty>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement property in new[] { central, project }.SelectMany(document =>
                     document.Descendants().Where(e => NameEquals(e, "ManagePackageVersionsCentrally") ||
                         NameEquals(e, "CentralPackageVersionOverrideEnabled") ||
                         NameEquals(e, "RestoreEnableGlobalPackageReference"))))
        {
            if (property.Parent is not { } group || !NameEquals(group, "PropertyGroup") ||
                group.Parent != property.Document?.Root || HasCondition(property) || HasCondition(group) ||
                !bool.TryParse(property.Value.Trim(), out bool value))
                return direct;
            flags[property.Name.LocalName] = new(value.ToString(), true);
        }
        if (!flags.TryGetValue("ManagePackageVersionsCentrally", out var management) ||
            !management.Value.Equals("True", StringComparison.OrdinalIgnoreCase))
            return direct;

        bool usesProperties = items.Any(item =>
            item.Attributes().Any(a => (a.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase) ||
                a.Name.LocalName.Equals("VersionOverride", StringComparison.OrdinalIgnoreCase)) &&
                a.Value.Contains("$(", StringComparison.Ordinal)) ||
            item.Elements().Any(e => (NameEquals(e, "Version") || NameEquals(e, "VersionOverride")) &&
                e.Value.Contains("$(", StringComparison.Ordinal)));
        var expansionBudget = new CSharpCentralPropertyExpansionBudget();
        // SDK flags precede Directory.Build.props, which this C# adapter does not evaluate.
        // Withhold those seeds when imported authority may shadow them; explicit central
        // assignments still run afterward. Literal package versions do not need the seeds.
        // Unsupported SDK forms likewise confer no context. F# uses the same root reader
        // but evaluates early imports itself, so it can retain their actual assignments.
        BoundedMsBuildSdkContext sdkContext = default;
        if (!hasPotentialImportedSdkPropertyAuthority)
            _ = BoundedMsBuildSdkContext.TryRead(project.Root, CancellationToken.None, out sdkContext);
        Dictionary<string, BoundedMsBuildProperty> properties;
        var projectPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (usesProperties)
        {
            if (hasPotentialLateProjectPropertyAuthority ||
                project.Descendants().Any(e => NameEquals(e, "Import")) ||
                !TryEvaluateCSharpCentralProperties(central, expansionBudget, sdkContext, out properties))
                return direct;
            int projectPropertyCount = 0;
            foreach (XElement property in project.Descendants().Where(e => NameEquals(e, "PropertyGroup"))
                         .SelectMany(group => group.Elements()))
            {
                if (++projectPropertyCount > MaxCSharpCentralPackageProperties) return direct;
                projectPropertyNames.Add(property.Name.LocalName);
            }
        }
        else
        {
            properties = new(StringComparer.OrdinalIgnoreCase);
            sdkContext.SeedProperties(properties);
        }
        foreach (var flag in flags) properties[flag.Key] = flag.Value;

        bool Expand(string value, string document, out string expanded)
        {
            expanded = value;
            if (!usesProperties) return !value.Contains("$(", StringComparison.Ordinal);
            var referencedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return TryExpandCSharpCentralPropertyValue(value, properties, referencedProperties,
                       expansionBudget, out expanded) &&
                   !referencedProperties.Overlaps(projectPropertyNames);
        }
        static bool ExpandItems(string value, string document, out List<string> ids)
        {
            ids = [value.Trim()];
            return CSharpCentralPackageId.IsMatch(ids[0]);
        }
        static bool Unconditional(XElement element, string document, out bool process)
        {
            process = !HasCondition(element);
            return process;
        }
        var evaluator = new BoundedMsBuildPackageEvaluator(properties, Expand, ExpandItems,
            Unconditional, static () => true, CancellationToken.None);
        foreach (XElement item in items) evaluator.Add(item.Parent!, item, "");
        if (!evaluator.Evaluate()) return direct;

        var resolved = new List<CSharpPackageReferenceSnapshot>();
        foreach (var reference in evaluator.CompileReferences)
        {
            if (!TryNormalizeCSharpCentralPackageVersion(reference.RequestedVersion, out string version))
                return direct; // Exact-cache loading cannot select a version range or floating version.
            resolved.Add(new(reference.Id, version,
                reference.CentrallyManaged));
        }
        // packages.config inputs do not occur as PackageReference XML and retain their established path.
        var declaredIds = new HashSet<string>(items.Where(e => NameEquals(e, "PackageReference"))
            .Select(e => (AttributeValue(e, "Include") ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        var restoreIds = evaluator.RestoreReferences.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        resolved.AddRange(direct.Where(r => !declaredIds.Contains(r.Id) && !restoreIds.Contains(r.Id)));
        return resolved;
    }

    private static bool TryEvaluateCSharpCentralProperties(XDocument central,
        CSharpCentralPropertyExpansionBudget expansionBudget,
        BoundedMsBuildSdkContext sdkContext,
        out Dictionary<string, BoundedMsBuildProperty> properties)
    {
        properties = new Dictionary<string, BoundedMsBuildProperty>(
            StringComparer.OrdinalIgnoreCase);
        sdkContext.SeedProperties(properties);
        int propertyCount = 0;
        foreach (XElement group in central.Root!.Descendants().Where(element =>
                     NameEquals(element, "PropertyGroup")))
        {
            foreach (XElement property in group.Elements())
            {
                if (++propertyCount > MaxCSharpCentralPackageProperties)
                    return false;

                string name = property.Name.LocalName;
                if (!CSharpCentralPropertyName.IsMatch(name) ||
                    group.Parent != central.Root || HasCondition(group) ||
                    HasCondition(property) || property.HasElements)
                {
                    properties.Remove(name);
                    continue;
                }

                if (!TryExpandCSharpCentralPropertyValue(property.Value.Trim(), properties,
                        referencedProperties: null, expansionBudget, out string expanded))
                {
                    properties.Remove(name);
                    continue;
                }
                properties[name] = new BoundedMsBuildProperty(expanded, Complete: true);
            }
        }
        return true;
    }

    private static bool TryExpandCSharpCentralPropertyValue(string? value,
        IReadOnlyDictionary<string, BoundedMsBuildProperty> properties,
        HashSet<string>? referencedProperties,
        CSharpCentralPropertyExpansionBudget expansionBudget,
        out string expanded)
    {
        expanded = "";
        if (value is null) return false;
        if (!value.Contains("$(", StringComparison.Ordinal))
        {
            if (value.Length > MaxCSharpCentralPackagePropertyValueChars ||
                !expansionBudget.TryReserveExpandedValue(value.Length)) return false;
            expanded = value;
            return true;
        }

        var evaluator = new BoundedMsBuildExpressionEvaluator(
            properties,
            static (_, _) => new BoundedMsBuildExpansion(false, ""),
            static (_, _) => new BoundedMsBuildExistsResult(false, false),
            CancellationToken.None,
            MaxCSharpCentralPackagePropertyValueChars,
            // The enclosing C# projection still rejects every Condition before scalar expansion.
            maxConditionDepth: 0);
        return evaluator.TryExpandProperties(value, documentPath: "", selfProperty: null,
                   out expanded, out bool complete, out _,
                   allowPropertyStringFunctions: false,
                   // The legacy C# expander preserved these markers as opaque text; the final
                   // package-version grammar rejects them if they reach a consumed value.
                   preserveOpaqueItemAndMetadataReferences: true,
                   stopOnUnresolvedProperty: true,
                   tryReservePropertyExpansion: name =>
                   {
                       if (!expansionBudget.TryReservePropertyExpansion()) return false;
                       referencedProperties?.Add(name);
                       return true;
                   },
                   tryReserveExpandedValue: expansionBudget.TryReserveExpandedValue) &&
               complete;
    }

    private sealed class CSharpCentralPropertyExpansionBudget
    {
        private int _propertyExpansions;
        private int _expandedPropertyChars;

        public bool TryReservePropertyExpansion() =>
            ++_propertyExpansions <= MaxCSharpCentralPackagePropertyExpansions;

        public bool TryReserveExpandedValue(int length)
        {
            if (_expandedPropertyChars > MaxCSharpCentralPackageExpandedPropertyChars - length)
                return false;
            _expandedPropertyChars += length;
            return true;
        }
    }

    private static bool TryNormalizeCSharpCentralPackageVersion(string? version,
        out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(version) ||
            !BoundedMsBuildPackageEvaluator.IsSimpleVersion(version)) return false;

        string versionWithoutMetadata = version.Split('+', 2)[0];
        string[] releaseSplit = versionWithoutMetadata.Split('-', 2);
        string[] numberParts = releaseSplit[0].Split('.');
        var numbers = new List<ulong>(numberParts.Length);
        foreach (string part in numberParts)
        {
            if (!ulong.TryParse(part, out ulong number)) return false;
            numbers.Add(number);
        }
        while (numbers.Count < 3) numbers.Add(0);
        if (numbers.Count == 4 && numbers[3] == 0) numbers.RemoveAt(3);
        normalized = string.Join('.', numbers);
        if (releaseSplit.Length == 2)
            normalized += "-" + releaseSplit[1].ToLowerInvariant();
        return true;
    }

    private static XDocument LoadCSharpCentralPackageXml(string xml)
    {
        if (xml.Length > MaxSnapshotBytes)
            throw new InvalidDataException("central package XML exceeds the snapshot limit");
        using var input = new StringReader(xml);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxSnapshotBytes,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static bool NameEquals(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool HasCondition(XElement element) =>
        AttributeValue(element, "Condition") is not null;

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
}
