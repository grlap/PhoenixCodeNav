using System.Text;
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

    private static readonly Regex CSharpCentralPackageVersion = new(
        @"^[0-9]+(?:\.[0-9]+){0,3}(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);

    /// <summary>Projects the standard unconditional CPM shape needed by the existing C# semantic
    /// package loader, including bounded simple local-property expansion. Authority comes only from
    /// the pinned indexed Directory.Packages.props snapshot. Shapes that require broader MSBuild
    /// evaluation retain the established unresolved-reference behavior instead of guessing a package
    /// version; once selected, a central version never falls back to another cache directory.</summary>
    internal static List<CSharpPackageReferenceSnapshot> EvaluateCSharpPackageReferencesSnapshot(
        byte[] projectBytes,
        IReadOnlyList<(string Package, string Version)> packageReferences,
        string? directoryPackagesXml,
        bool hasAmbiguousDirectoryPackagesAuthority,
        bool hasPotentialLateProjectPropertyAuthority = false)
    {
        var direct = packageReferences.Select(reference =>
            new CSharpPackageReferenceSnapshot(reference.Package, reference.Version)).ToList();
        if (direct.All(reference => !string.IsNullOrWhiteSpace(reference.Version)))
            return direct;
        if (hasAmbiguousDirectoryPackagesAuthority)
            return direct;
        if (directoryPackagesXml is null)
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
        if (project.Root is null || central.Root is null)
            return direct;
        if (central.Root.Attributes().Any(attribute =>
                attribute.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)) ||
            central.Root.Descendants().Any(element => NameEquals(element, "Sdk")))
        {
            return direct;
        }
        if (project.Root.Descendants().Any(element =>
                NameEquals(element, "PackageReference") &&
                (element.Parent is not { } group || !NameEquals(group, "ItemGroup") ||
                 group.Parent != project.Root || HasCondition(element) || HasCondition(group) ||
                 AttributeValue(element, "VersionOverride") is not null ||
                 element.Elements().Any(child => NameEquals(child, "VersionOverride")))))
        {
            return direct;
        }

        if (central.Root.Descendants().Any(element =>
                NameEquals(element, "Import") || NameEquals(element, "GlobalPackageReference")))
        {
            return direct;
        }

        bool centrallyManaged = false;
        foreach (XDocument document in new[] { central, project })
        {
            foreach (XElement property in document.Descendants().Where(element =>
                         NameEquals(element, "ManagePackageVersionsCentrally")))
            {
                if (property.Parent is not { } group || !NameEquals(group, "PropertyGroup") ||
                    group.Parent != document.Root || HasCondition(property) ||
                    HasCondition(group) ||
                    !bool.TryParse(property.Value.Trim(), out centrallyManaged))
                {
                    return direct;
                }
            }
        }
        if (!centrallyManaged)
            return direct;

        var centralVersions = new List<(string Id, string RawVersion)>();
        var centralVersionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement item in central.Descendants().Where(element =>
                     NameEquals(element, "PackageVersion")))
        {
            if (item.Parent is not { } group || !NameEquals(group, "ItemGroup") ||
                group.Parent != central.Root || HasCondition(item) || HasCondition(group) ||
                AttributeValue(item, "Update") is not null ||
                AttributeValue(item, "Remove") is not null)
            {
                return direct;
            }

            string? id = AttributeValue(item, "Include")?.Trim();
            string? attributeVersion = AttributeValue(item, "Version")?.Trim();
            string[] childVersions = item.Elements().Where(element =>
                    NameEquals(element, "Version"))
                .Select(element => element.Value.Trim()).ToArray();
            if (string.IsNullOrWhiteSpace(id) || !CSharpCentralPackageId.IsMatch(id) ||
                attributeVersion is not null && childVersions.Length > 0 ||
                childVersions.Length > 1)
            {
                return direct;
            }

            string version = attributeVersion ?? childVersions.SingleOrDefault() ?? "";
            if (!centralVersionIds.Add(id)) return direct;
            centralVersions.Add((id, version.Trim()));
        }

        bool usesProperties = centralVersions.Any(entry =>
            entry.RawVersion.Contains("$(", StringComparison.Ordinal));
        int propertyExpansions = 0;
        int expandedPropertyChars = 0;
        Dictionary<string, string> properties;
        var projectPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (usesProperties)
        {
            if (hasPotentialLateProjectPropertyAuthority ||
                project.Root.Descendants().Any(element => NameEquals(element, "Import")) ||
                !TryEvaluateCSharpCentralProperties(central,
                    ref propertyExpansions, ref expandedPropertyChars, out properties))
            {
                return direct;
            }

            int projectPropertyCount = 0;
            foreach (XElement group in project.Root.Descendants().Where(element =>
                         NameEquals(element, "PropertyGroup")))
            {
                foreach (XElement property in group.Elements())
                {
                    if (++projectPropertyCount > MaxCSharpCentralPackageProperties)
                        return direct;
                    projectPropertyNames.Add(property.Name.LocalName);
                }
            }
        }
        else
        {
            properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string rawVersion) in centralVersions)
        {
            var referencedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string expandedVersion = rawVersion;
            if (usesProperties && !TryExpandCSharpCentralPropertyValue(rawVersion, properties,
                    referencedProperties, ref propertyExpansions, ref expandedPropertyChars,
                    out expandedVersion))
            {
                return direct;
            }
            if (referencedProperties.Overlaps(projectPropertyNames) ||
                !TryNormalizeCSharpCentralPackageVersion(expandedVersion,
                    out string normalizedVersion) ||
                !versions.TryAdd(id, normalizedVersion))
            {
                return direct;
            }
        }

        var resolved = new List<CSharpPackageReferenceSnapshot>(direct.Count);
        foreach (CSharpPackageReferenceSnapshot reference in direct)
        {
            if (!string.IsNullOrWhiteSpace(reference.Version))
            {
                resolved.Add(reference);
                continue;
            }
            if (!versions.TryGetValue(reference.Id, out string? version))
                return direct;
            resolved.Add(reference with { Version = version, CentrallyManaged = true });
        }
        return resolved;
    }

    private static bool TryEvaluateCSharpCentralProperties(XDocument central,
        ref int propertyExpansions, ref int expandedPropertyChars,
        out Dictionary<string, string> properties)
    {
        properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                        referencedProperties: null, ref propertyExpansions,
                        ref expandedPropertyChars, out string expanded))
                {
                    properties.Remove(name);
                    continue;
                }
                properties[name] = expanded;
            }
        }
        return true;
    }

    private static bool TryExpandCSharpCentralPropertyValue(string? value,
        IReadOnlyDictionary<string, string> properties,
        HashSet<string>? referencedProperties,
        ref int propertyExpansions,
        ref int expandedPropertyChars,
        out string expanded)
    {
        expanded = "";
        if (value is null || value.Length > MaxCSharpCentralPackagePropertyValueChars)
            return false;

        var builder = new StringBuilder(value.Length);
        int offset = 0;
        while (offset < value.Length)
        {
            int expression = value.IndexOf("$(", offset, StringComparison.Ordinal);
            if (expression < 0)
            {
                builder.Append(value, offset, value.Length - offset);
                break;
            }
            builder.Append(value, offset, expression - offset);
            int close = value.IndexOf(')', expression + 2);
            if (close < 0) return false;

            string name = value[(expression + 2)..close];
            if (!CSharpCentralPropertyName.IsMatch(name) ||
                !properties.TryGetValue(name, out string? replacement) ||
                ++propertyExpansions > MaxCSharpCentralPackagePropertyExpansions)
            {
                return false;
            }
            referencedProperties?.Add(name);
            builder.Append(replacement);
            if (builder.Length > MaxCSharpCentralPackagePropertyValueChars)
                return false;
            offset = close + 1;
        }

        if (builder.Length > MaxCSharpCentralPackagePropertyValueChars ||
            expandedPropertyChars > MaxCSharpCentralPackageExpandedPropertyChars - builder.Length)
        {
            return false;
        }
        expandedPropertyChars += builder.Length;
        expanded = builder.ToString();
        return true;
    }

    private static bool TryNormalizeCSharpCentralPackageVersion(string? version,
        out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(version) ||
            !CSharpCentralPackageVersion.IsMatch(version)) return false;

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
