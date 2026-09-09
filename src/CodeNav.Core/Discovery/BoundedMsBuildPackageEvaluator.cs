using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeNav.Core.Discovery;

internal sealed record BoundedMsBuildPackageReference(
    string Id, string? RequestedVersion, bool CentrallyManaged, bool IncludeCompileAssets);

/// <summary>Shared package-item semantics. Callers own document/condition authority and budgets;
/// package operations run after property evaluation, and references consume final central versions.</summary>
internal sealed class BoundedMsBuildPackageEvaluator
{
    internal delegate bool Expand(string value, string documentPath, out string expanded);
    internal delegate bool ExpandItems(string value, string documentPath, out List<string> items);
    internal delegate bool Condition(XElement element, string documentPath, out bool process);

    private static readonly Regex PackageId = new(
        @"^[A-Za-z0-9_][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex SimplePackageVersion = new(
        @"^[0-9]+(?:\.[0-9]+){0,3}(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex FloatingPackageVersion = new(
        @"^[0-9]+(?:\.[0-9]+){0,2}\.\*$", RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, BoundedMsBuildProperty> _properties;
    private readonly Expand _expand;
    private readonly ExpandItems _expandItems;
    private readonly Condition _shouldProcess;
    private readonly Func<bool> _reserveItem;
    private readonly CancellationToken _cancellationToken;
    private readonly List<(XElement Group, XElement Item, string Document, ExpandItems ExpandItems)> _items = [];
    private readonly Dictionary<string, string?> _packageReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packageVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _centralReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _globalReferences = new(StringComparer.OrdinalIgnoreCase);
    private string? _error;

    public BoundedMsBuildPackageEvaluator(
        IReadOnlyDictionary<string, BoundedMsBuildProperty> properties, Expand expand,
        ExpandItems expandItems, Condition shouldProcess, Func<bool> reserveItem,
        CancellationToken cancellationToken)
    {
        _properties = properties;
        _expand = (string value, string document, out string result) =>
        {
            bool ok = expand(value, document, out result);
            if (!ok) _error ??= "package_reference_unresolved";
            return ok;
        };
        _expandItems = WithItemExpansionFailure(expandItems);
        _shouldProcess = (XElement element, string document, out bool process) =>
        {
            bool ok = shouldProcess(element, document, out process);
            if (!ok) _error ??= "condition_unsupported";
            return ok;
        };
        _reserveItem = reserveItem;
        _cancellationToken = cancellationToken;
    }

    public string? Error => _error;
    public IEnumerable<BoundedMsBuildPackageReference> RestoreReferences => _packageReferences.Select(reference =>
        new BoundedMsBuildPackageReference(reference.Key, reference.Value,
            _centralReferences.Contains(reference.Key), !_globalReferences.Contains(reference.Key)));
    public IEnumerable<BoundedMsBuildPackageReference> CompileReferences =>
        RestoreReferences.Where(reference => reference.IncludeCompileAssets);
    public static bool IsPackageItem(string name) =>
        name.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GlobalPackageReference", StringComparison.OrdinalIgnoreCase);
    public static bool IsSimpleVersion(string version) => SimplePackageVersion.IsMatch(version);

    public void Add(XElement group, XElement item, string document, ExpandItems? expandItems = null) =>
        _items.Add((group, item, document, expandItems is null ? _expandItems : WithItemExpansionFailure(expandItems)));

    private ExpandItems WithItemExpansionFailure(ExpandItems expandItems) =>
        (string value, string document, out List<string> result) =>
        {
            bool ok = expandItems(value, document, out result);
            if (!ok) _error ??= "package_reference_unresolved";
            return ok;
        };
    private void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();

    public bool Evaluate()
    {
        var active = new List<(XElement Item, string Document, ExpandItems ExpandItems)>();
        foreach (var entry in _items)
        {
            CheckCancellation();
            if (!_shouldProcess(entry.Group, entry.Document, out bool group)) return false;
            if (!group) continue;
            if (!_shouldProcess(entry.Item, entry.Document, out bool process)) return false;
            if (process) active.Add((entry.Item, entry.Document, entry.ExpandItems));
        }
        foreach (var entry in active.Where(entry =>
                     entry.Item.Name.LocalName.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase)))
        {
            CheckCancellation();
            ProcessPackageVersion(entry.Item, entry.Document, entry.ExpandItems);
            if (_error is not null) return false;
        }
        foreach (var entry in active.Where(entry =>
                     entry.Item.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase)))
        {
            CheckCancellation();
            ProcessPackageReference(entry.Item, entry.Document, entry.ExpandItems);
            if (_error is not null) return false;
        }
        var globals = active.Where(entry =>
            entry.Item.Name.LocalName.Equals("GlobalPackageReference", StringComparison.OrdinalIgnoreCase)).ToList();
        if (globals.Count > 0 && IsCentralPackageManagementEnabled() &&
            TryGetBooleanProperty("RestoreEnableGlobalPackageReference", defaultValue: true))
        {
            foreach (var entry in globals)
            {
                CheckCancellation();
                // NuGet.targets projects global items after ordinary package items with
                // IncludeAssets=Runtime;Build;Native;contentFiles;Analyzers and PrivateAssets=All.
                // Compile is absent from that fixed mask. We model this fenced projection only,
                // not arbitrary asset filtering; PrivateAssets alone never excludes own compilation.
                if (entry.Item.Attributes().Any(a => a.Name.LocalName is not
                        ("Include" or "Version" or "Condition" or "Label")) ||
                    entry.Item.Elements().Any(e => !e.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase)))
                {
                    _error = "central_package_management_unsupported";
                    return false;
                }
                var version = new XElement(entry.Item) { Name = "PackageVersion" };
                ProcessPackageVersion(version, entry.Document, entry.ExpandItems);
                if (_error is not null) return false;
                if (!entry.ExpandItems(AttributeValue(entry.Item, "Include") ?? "", entry.Document,
                        out List<string> ids)) return false;
                foreach (string id in ids)
                {
                    CheckCancellation();
                    if (_packageReferences.ContainsKey(id))
                    {
                        _error = "central_package_management_unsupported";
                        return false;
                    }
                    _globalReferences.Add(id);
                    _centralReferences.Add(id);
                    _packageReferences[id] = _packageVersions[id];
                }
            }
        }
        // NuGet consumes final item sets. A transient reference may be removed or receive an
        // override later; it must not require a central version before those mutations finish.
        // Global collisions above are rejected first, so their projected versions can never
        // supply an otherwise unresolved ordinary reference with the same identity.
        foreach (string id in _packageReferences.Keys.ToArray())
        {
            CheckCancellation();
            if (_packageReferences[id] is not null) continue;
            if (!_centralReferences.Contains(id) || !_packageVersions.TryGetValue(id, out string? version))
            {
                _error = "package_reference_unresolved";
                return false;
            }
            _packageReferences[id] = version;
        }
        return _error is null;
    }

    private void ProcessPackageReference(XElement item, string documentPath, ExpandItems expandItems)
    {
        string? rawInclude = AttributeValue(item, "Include")?.Trim();
        string? rawUpdate = AttributeValue(item, "Update")?.Trim();
        string? rawRemove = AttributeValue(item, "Remove")?.Trim();
        int operations = (rawInclude is null ? 0 : 1) + (rawUpdate is null ? 0 : 1) +
                         (rawRemove is null ? 0 : 1);
        if (operations != 1 || AttributeValue(item, "Exclude") is not null)
        {
            _error = "package_reference_unresolved";
            return;
        }
        string raw = rawInclude ?? rawUpdate ?? rawRemove!;
        if (!expandItems(raw, documentPath, out List<string> specs))
        {
            _error ??= "package_reference_unresolved";
            return;
        }
        if (specs.Count == 0)
        {
            _error = "package_reference_unresolved";
            return;
        }

        var ids = new List<string>(specs.Count);
        foreach (string spec in specs)
        {
            CheckCancellation();
            if (!_reserveItem())
            {
                _error = "item_list_limit";
                return;
            }
            string id = spec.Trim();
            if (!IsValidPackageId(id))
            {
                _error = "package_reference_unresolved";
                return;
            }
            ids.Add(id);
        }
        if (rawUpdate is not null && ids.All(id =>
                !_packageReferences.ContainsKey(id)))
            return;
        if (rawRemove is null &&
            !PackageReferenceCompilerMetadataIsSupported(item, documentPath))
            return;

        string? requestedVersion = null;
        bool versionOverrideUsed = false;
        if (rawRemove is null && !TryGetPackageVersion(item, documentPath,
                allowVersionOverride: true,
                failureCause: "package_reference_unresolved",
                out requestedVersion,
                out versionOverrideUsed))
            return;
        if (requestedVersion is not null &&
            !IsSupportedPackageVersionExpression(requestedVersion))
        {
            _error = "package_reference_unresolved";
            return;
        }

        bool centrallyManaged = IsCentralPackageManagementEnabled();
        if (_error is not null) return;
        if (centrallyManaged && requestedVersion is not null &&
            !versionOverrideUsed)
        {
            _error = "package_reference_unresolved";
            return;
        }
        bool versionOverrideEnabled = !versionOverrideUsed ||
                                      IsCentralPackageVersionOverrideEnabled();
        if (_error is not null) return;
        if (versionOverrideUsed &&
            (!centrallyManaged || !versionOverrideEnabled ||
             requestedVersion is null ||
             !SimplePackageVersion.IsMatch(requestedVersion)))
        {
            _error = "package_reference_unresolved";
            return;
        }

        foreach (string id in ids)
        {
            CheckCancellation();
            if (rawRemove is not null)
            {
                _packageReferences.Remove(id);
                _centralReferences.Remove(id);
            }
            else if (rawUpdate is not null)
            {
                if (!_packageReferences.ContainsKey(id)) continue;
                if (requestedVersion is null) continue;
                _packageReferences[id] = requestedVersion;
                if (centrallyManaged) _centralReferences.Add(id);
            }
            else
            {
                _packageReferences[id] = requestedVersion;
                if (centrallyManaged) _centralReferences.Add(id);
            }
        }
    }

    private bool PackageReferenceCompilerMetadataIsSupported(XElement item,
        string documentPath)
    {
        static bool ChangesCompilerReferenceShape(string name) =>
            name.Equals("ExcludeAssets", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("IncludeAssets", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Aliases", StringComparison.OrdinalIgnoreCase);

        if (item.Attributes().Any(attribute =>
                ChangesCompilerReferenceShape(attribute.Name.LocalName)))
        {
            _error = "package_reference_metadata_unsupported";
            return false;
        }

        foreach (XElement metadata in item.Elements().Where(element =>
                     ChangesCompilerReferenceShape(element.Name.LocalName)))
        {
            if (!_shouldProcess(metadata, documentPath, out bool process)) return false;
            if (!process) continue;
            _error = "package_reference_metadata_unsupported";
            return false;
        }
        return true;
    }

    private void ProcessPackageVersion(XElement item, string documentPath, ExpandItems expandItems)
    {
        string? rawInclude = AttributeValue(item, "Include")?.Trim();
        string? rawUpdate = AttributeValue(item, "Update")?.Trim();
        string? rawRemove = AttributeValue(item, "Remove")?.Trim();
        int operations = (rawInclude is null ? 0 : 1) + (rawUpdate is null ? 0 : 1) +
                         (rawRemove is null ? 0 : 1);
        if (operations != 1 || AttributeValue(item, "Exclude") is not null)
        {
            _error = "central_package_management_unsupported";
            return;
        }

        string raw = rawInclude ?? rawUpdate ?? rawRemove!;
        if (!expandItems(raw, documentPath, out List<string> specs) || specs.Count == 0)
        {
            _error ??= "central_package_management_unsupported";
            return;
        }

        string? version = null;
        if (rawRemove is null && !TryGetPackageVersion(item, documentPath,
                allowVersionOverride: false,
                failureCause: "central_package_management_unsupported",
                out version, out _))
            return;
        if (rawInclude is not null && version is null)
        {
            _error = "central_package_management_unsupported";
            return;
        }
        if (version is not null && !SimplePackageVersion.IsMatch(version))
        {
            _error = "central_package_management_unsupported";
            return;
        }

        foreach (string spec in specs)
        {
            CheckCancellation();
            if (!_reserveItem())
            {
                _error = "item_list_limit";
                return;
            }
            string id = spec.Trim();
            if (!IsValidPackageId(id))
            {
                _error = "central_package_management_unsupported";
                return;
            }
            if (rawRemove is not null)
                _packageVersions.Remove(id);
            else if (rawInclude is not null && _packageVersions.ContainsKey(id))
            {
                _error = "central_package_management_unsupported";
                return;
            }
            else if (rawUpdate is not null && !_packageVersions.ContainsKey(id))
            {
                continue;
            }
            else if (version is not null)
                _packageVersions[id] = version;
            else if (!_packageVersions.ContainsKey(id))
            {
                _error = "central_package_management_unsupported";
                return;
            }
        }
    }

    private bool TryGetPackageVersion(XElement item, string documentPath,
        bool allowVersionOverride, string failureCause, out string? requestedVersion,
        out bool versionOverrideUsed)
    {
        requestedVersion = null;
        versionOverrideUsed = false;
        var values = new List<string>();
        bool foundVersionOverride = false;
        foreach (string attributeName in allowVersionOverride
                     ? new[] { "VersionOverride", "Version" }
                     : new[] { "Version" })
        {
            if (AttributeValue(item, attributeName) is { } attributeValue)
            {
                values.Add(attributeValue);
                foundVersionOverride |= attributeName.Equals("VersionOverride",
                    StringComparison.OrdinalIgnoreCase);
            }
            foreach (XElement metadata in item.Elements().Where(element =>
                         element.Name.LocalName.Equals(attributeName,
                             StringComparison.OrdinalIgnoreCase)))
            {
                if (!_shouldProcess(metadata, documentPath, out bool process)) return false;
                if (process)
                {
                    values.Add(metadata.Value);
                    foundVersionOverride |= attributeName.Equals("VersionOverride",
                        StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        if (values.Count > 1)
        {
            _error = failureCause;
            return false;
        }
        if (values.Count == 0) return true;
        versionOverrideUsed = foundVersionOverride;
        if (!_expand(values[0].Trim(), documentPath, out string expanded))
        {
            _error = failureCause;
            return false;
        }
        if (expanded.Length == 0)
        {
            _error = failureCause;
            return false;
        }
        requestedVersion = expanded;
        return true;
    }

    private bool IsCentralPackageManagementEnabled() =>
        TryGetBooleanProperty("ManagePackageVersionsCentrally", defaultValue: false);

    private bool IsCentralPackageVersionOverrideEnabled() =>
        TryGetBooleanProperty("CentralPackageVersionOverrideEnabled", defaultValue: true);

    private bool TryGetBooleanProperty(string name, bool defaultValue)
    {
        if (!_properties.TryGetValue(name, out BoundedMsBuildProperty property))
            return defaultValue;
        if (!property.Complete || !bool.TryParse(property.Value.Trim(), out bool value))
        {
            _error = "central_package_management_unsupported";
            return false;
        }
        return value;
    }

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool IsValidPackageId(string id) =>
        PackageId.IsMatch(id);

    private static bool IsSupportedPackageVersionExpression(string expression)
    {
        string value = expression.Trim();
        if (SimplePackageVersion.IsMatch(value) ||
            FloatingPackageVersion.IsMatch(value))
            return true;
        if (value.Length >= 3 && value[0] == '[' && value[^1] == ']' &&
            !value.Contains(','))
            return SimplePackageVersion.IsMatch(value[1..^1].Trim());
        if (value.Length < 3 || value[0] is not ('[' or '(') ||
            value[^1] is not (']' or ')'))
            return false;
        string body = value[1..^1];
        int comma = body.IndexOf(',');
        if (comma < 0 || comma != body.LastIndexOf(',')) return false;
        string lower = body[..comma].Trim();
        string upper = body[(comma + 1)..].Trim();
        if (lower.Length == 0 && value[0] != '(' ||
            upper.Length == 0 && value[^1] != ')' ||
            lower.Length == 0 && upper.Length == 0)
            return false;
        return (lower.Length == 0 || SimplePackageVersion.IsMatch(lower)) &&
               (upper.Length == 0 || SimplePackageVersion.IsMatch(upper));
    }

}
