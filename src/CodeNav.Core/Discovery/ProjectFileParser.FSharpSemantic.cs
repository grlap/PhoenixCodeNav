using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodeNav.Core.Discovery;

public static partial class ProjectFileParser
{
    internal const int MaxFSharpSemanticImportDepth = 8;
    internal const int MaxFSharpSemanticImportFiles = 32;
    internal const int MaxFSharpSemanticImportOccurrences = 64;
    internal const int MaxFSharpSemanticImportBytes = 4 * 1024 * 1024;
    internal const int MaxFSharpSemanticProperties = 512;
    internal const int MaxFSharpSemanticPropertyValueChars = 16 * 1024;
    internal const int MaxFSharpSemanticConditionChars = 4 * 1024;
    internal const int MaxFSharpSemanticConditionDepth = 32;
    internal const int MaxFSharpSemanticEvaluationDepth = 64;
    internal const int MaxFSharpSemanticItemListEntries = 1024;
    internal const int MaxFSharpSemanticDependencyNodes = 1024;

    private sealed record FSharpSemanticEvaluation(
        List<string> SourceFiles,
        List<string> CommandLineArgs,
        List<string> HintPathReferences,
        List<string> BareReferences,
        List<FSharpPackageReferenceSnapshot> PackageReferences,
        string AssemblyName,
        string? PartialReason = null,
        string? Error = null);

    private readonly record struct FSharpSemanticProperty(string Value, bool Complete);
    private readonly record struct FSharpSemanticReference(
        string ItemSpec, string SimpleName, string? HintPath);

    private enum FSharpSemanticDocumentRole
    {
        Project,
        ExplicitImport,
        DirectoryPackagesProps,
        DirectoryBuildProps,
        DirectoryBuildTargets,
    }

    /// <summary>
    /// A deliberately small MSBuild evaluation projection. It evaluates only the ordered property,
    /// import, condition, compile-item, and reference facts needed by Stage 2A.1. It never loads
    /// MSBuild, executes a target/task, restores a package, or treats a solution as authority.
    /// </summary>
    private sealed class FSharpSemanticProjectEvaluator
    {
        private static readonly Regex PropertyReference = new(
            @"\$\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\)",
            RegexOptions.CultureInvariant);

        private static readonly Regex ItemReference = new(
            @"^@\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\)$",
            RegexOptions.CultureInvariant);

        private static readonly Regex ItemReferenceOccurrence = new(
            @"@\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\)",
            RegexOptions.CultureInvariant);

        private static readonly Regex SimplePackageVersion = new(
            @"^[0-9]+(?:\.[0-9]+){0,3}(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant);

        private static readonly Regex FloatingPackageVersion = new(
            @"^[0-9]+(?:\.[0-9]+){0,2}\.\*$",
            RegexOptions.CultureInvariant);

        private static readonly Regex ExistsCondition = new(
            @"^Exists\s*\(\s*(?:'(?<path>[^'\r\n]*)'|""(?<path>[^""\r\n]*)"")\s*\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly string _projectPath;
        private readonly string _projectDir;
        private readonly string _selectedTargetFramework;
        private readonly string[] _targetFrameworks;
        private readonly Func<string, string?>? _importResolver;
        private readonly Func<string, long?>? _importSizeResolver;
        private readonly string? _directoryBuildPropsPath;
        private readonly string? _directoryBuildTargetsPath;
        private readonly string? _directoryPackagesPropsPath;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<string, FSharpSemanticProperty> _properties =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _sources = [];
        private readonly HashSet<string> _sourceSet =
            new(WorkspacePaths.FileSystemPathComparer);
        private readonly List<FSharpSemanticReference> _references = [];
        private readonly Dictionary<string, string?> _packageReferences =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _packageVersions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _itemLists =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _incompleteItemListErrors =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directoryReferenceProperties =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directoryReferenceItemLists =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _referenceInputConsumedProperties =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _activeImports =
            new(WorkspacePaths.FileSystemPathComparer);
        private readonly Dictionary<string, string?> _importSnapshots =
            new(WorkspacePaths.FileSystemPathComparer);
        private readonly Dictionary<string, XElement> _importRoots =
            new(WorkspacePaths.FileSystemPathComparer);
        private readonly SortedSet<string> _partialReasons = new(StringComparer.Ordinal);

        private int _importFiles;
        private int _importOccurrences;
        private long _importBytes;
        private int _propertyAssignments;
        private int _itemListEntries;
        private int _evaluationDepth;
        private bool _semanticItemPhaseStarted;
        private bool _directSemanticItemPhaseStarted;
        private string? _error;

        public FSharpSemanticProjectEvaluator(
            string projectPath,
            string selectedTargetFramework,
            string[] targetFrameworks,
            Func<string, string?>? importResolver,
            Func<string, long?>? importSizeResolver,
            string? directoryPackagesPropsPath,
            string? directoryBuildPropsPath,
            string? directoryBuildTargetsPath,
            CancellationToken cancellationToken)
        {
            _projectPath = WorkspacePaths.ToGitPath(projectPath).TrimStart('/');
            _projectDir = WorkspacePaths.ToGitPath(
                Path.GetDirectoryName(_projectPath) ?? "");
            _selectedTargetFramework = selectedTargetFramework;
            _targetFrameworks = targetFrameworks;
            _importResolver = importResolver;
            _importSizeResolver = importSizeResolver;
            _directoryPackagesPropsPath = NormalizeOptionalWorkspacePath(
                directoryPackagesPropsPath);
            _directoryBuildPropsPath = NormalizeOptionalWorkspacePath(directoryBuildPropsPath);
            _directoryBuildTargetsPath = NormalizeOptionalWorkspacePath(directoryBuildTargetsPath);
            _cancellationToken = cancellationToken;
            _properties["TargetFramework"] = new(selectedTargetFramework, true);
        }

        private static string? NormalizeOptionalWorkspacePath(string? path) =>
            string.IsNullOrWhiteSpace(path)
                ? null
                : WorkspacePaths.ToGitPath(path).TrimStart('/');

        public FSharpSemanticEvaluation Evaluate(XElement root)
        {
            CheckCancellation();
            if (root.Name.LocalName != "Project")
                return Failure("fsharp_project_options_unavailable");

            if (!ValidateSdkAuthority(root, allowStandardSdk: true))
                return Failure("fsharp_semantic_sdk_unsupported");
            RegisterDirectoryBuildDependencies(root);
            if (_error is not null) return Failure(_error);
            if (_directoryBuildTargetsPath is not null &&
                !TryResolveImportRoot(_directoryBuildTargetsPath, out _))
                return Failure(_error ?? "fsharp_semantic_import_unavailable");

            if (_directoryBuildPropsPath is not null)
                ProcessResolvedImport(_directoryBuildPropsPath,
                    FSharpSemanticDocumentRole.DirectoryBuildProps, depth: 0);
            if (_error is null && _directoryPackagesPropsPath is not null)
                ProcessResolvedImport(_directoryPackagesPropsPath,
                    FSharpSemanticDocumentRole.DirectoryPackagesProps, depth: 0);
            if (_error is null)
                ProcessContainer(root, _projectPath,
                    FSharpSemanticDocumentRole.Project, depth: 0);
            if (_error is null && _directoryBuildTargetsPath is not null)
                ProcessResolvedImport(_directoryBuildTargetsPath,
                    FSharpSemanticDocumentRole.DirectoryBuildTargets, depth: 0);
            CheckCancellation();
            if (_error is not null) return Failure(_error);

            string assemblyName = Path.GetFileNameWithoutExtension(_projectPath);
            if (_properties.TryGetValue("AssemblyName", out FSharpSemanticProperty assembly))
            {
                if (!assembly.Complete || string.IsNullOrWhiteSpace(assembly.Value))
                    return Failure("fsharp_semantic_assembly_name_unavailable");
                assemblyName = assembly.Value.Trim();
            }

            if (_properties.TryGetValue("EnableDefaultCompileItems",
                    out FSharpSemanticProperty defaultItems) &&
                (!defaultItems.Complete ||
                 !defaultItems.Value.Trim().Equals("false",
                     StringComparison.OrdinalIgnoreCase)))
            {
                return Failure("fsharp_semantic_compile_order_unavailable", assemblyName);
            }
            if (_sources.Count == 0)
                return Failure("fsharp_semantic_compile_order_unavailable", assemblyName);

            if (!TryCompilerProperty("DefineConstants", out string defines) ||
                !TryCompilerProperty("LangVersion", out string languageVersion) ||
                !TryCompilerProperty("OtherFlags", out string otherFlags) ||
                !TryCompilerProperty("FscAdditionalArgs", out string additionalArgs) ||
                !TryCompilerProperty("DisableImplicitFrameworkDefines",
                    out string disableImplicitText))
            {
                return Failure("fsharp_semantic_property_unresolved", assemblyName);
            }

            bool disableImplicitFrameworkDefines = false;
            if (disableImplicitText.Length > 0 &&
                !bool.TryParse(disableImplicitText, out disableImplicitFrameworkDefines))
            {
                return Failure("fsharp_semantic_property_unresolved", assemblyName);
            }

            FSharpParsingOptionsSnapshot parsing = BuildFSharpParsingOptionsSnapshot(
                _targetFrameworks, _selectedTargetFramework, defines,
                languageVersion.Length == 0 ? null : languageVersion,
                otherFlags, additionalArgs, disableImplicitFrameworkDefines,
                _partialReasons);
            CheckCancellation();
            if (parsing.Error is not null)
                return Failure(parsing.Error, assemblyName);

            return new(_sources, parsing.CommandLineArgs,
                _references.Where(reference => reference.HintPath is not null)
                    .Select(reference => reference.HintPath!)
                    .Distinct(WorkspacePaths.FileSystemPathComparer).ToList(),
                _references.Where(reference => reference.HintPath is null)
                    .Select(reference => reference.SimpleName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                _packageReferences.OrderBy(reference => reference.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(reference => new FSharpPackageReferenceSnapshot(
                        reference.Key, reference.Value)).ToList(),
                assemblyName, parsing.PartialReason);
        }

        private void CheckCancellation() =>
            _cancellationToken.ThrowIfCancellationRequested();

        private static bool IsDirectoryBuildRole(FSharpSemanticDocumentRole role) =>
            role is FSharpSemanticDocumentRole.DirectoryBuildProps or
                FSharpSemanticDocumentRole.DirectoryBuildTargets;

        private static bool IsDirectoryPropsAuthorityRole(
            FSharpSemanticDocumentRole role) =>
            role is FSharpSemanticDocumentRole.DirectoryPackagesProps or
                FSharpSemanticDocumentRole.DirectoryBuildProps or
                FSharpSemanticDocumentRole.DirectoryBuildTargets;

        private bool ValidateSdkAuthority(XElement root, bool allowStandardSdk)
        {
            CheckCancellation();
            XAttribute[] sdkAttributes = root.Attributes()
                .Where(attribute => attribute.Name.LocalName.Equals("Sdk",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (sdkAttributes.Length > 1) return false;
            if (sdkAttributes.Length == 1)
            {
                string sdk = sdkAttributes[0].Value.Trim();
                const string knownSdk = "Microsoft.NET.Sdk";
                if (!allowStandardSdk ||
                    !sdk.Equals(knownSdk, StringComparison.OrdinalIgnoreCase)) return false;
                _partialReasons.Add("fsharp_semantic_sdk_implicit_authority");
            }

            // Child SDK declarations can add implicit props/targets from arbitrary resolvers.
            // Stage 2A.1 does not resolve that authority, even for a familiar SDK name.
            foreach (XElement element in root.Elements())
            {
                CheckCancellation();
                if (element.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private FSharpSemanticEvaluation Failure(string error, string? assemblyName = null) =>
            new([], [],
                _references.Where(reference => reference.HintPath is not null)
                    .Select(reference => reference.HintPath!)
                    .Distinct(WorkspacePaths.FileSystemPathComparer).ToList(),
                _references.Where(reference => reference.HintPath is null)
                    .Select(reference => reference.SimpleName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                _packageReferences.OrderBy(reference => reference.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(reference => new FSharpPackageReferenceSnapshot(
                        reference.Key, reference.Value)).ToList(),
                assemblyName ?? Path.GetFileNameWithoutExtension(_projectPath),
                _partialReasons.Count == 0 ? null : string.Join(';', _partialReasons), error);

        private void ProcessContainer(XElement container, string documentPath,
            FSharpSemanticDocumentRole role, int depth)
        {
            CheckCancellation();
            if (_error is not null) return;
            if (++_evaluationDepth > MaxFSharpSemanticEvaluationDepth)
            {
                _evaluationDepth--;
                _error = "fsharp_semantic_evaluation_depth_limit";
                return;
            }
            try
            {
                if (!ShouldProcess(container, documentPath, out bool process)) return;
                if (!process) return;
                if (HasCompilerSchedulingProjectAttribute(container))
                {
                    _error = "fsharp_semantic_target_evaluation_unsupported";
                    return;
                }

                foreach (XElement child in container.Elements())
                {
                    CheckCancellation();
                    if (_error is not null) return;
                    switch (child.Name.LocalName)
                    {
                        case "PropertyGroup":
                            ProcessPropertyGroup(child, documentPath, role);
                            break;
                        case "ItemGroup":
                            ProcessItemGroup(child, documentPath, role);
                            break;
                        case "Import":
                            ProcessImport(child, documentPath, role, depth);
                            break;
                        case "ImportGroup":
                            ProcessImportGroup(child, documentPath, role, depth);
                            break;
                        case "Choose":
                            ProcessChoose(child, documentPath, role, depth);
                            break;
                        case "Target":
                            if (!ContainsSemanticTargetFacts(child)) break;
                            if (!ShouldProcess(child, documentPath, out bool processTarget)) return;
                            if (processTarget)
                                _error = "fsharp_semantic_target_evaluation_unsupported";
                            break;
                        case "ItemDefinitionGroup":
                            if (!child.Descendants().Any(element =>
                                    IsSemanticItemName(element.Name.LocalName))) break;
                            if (!ShouldProcess(child, documentPath,
                                    out bool processDefinitions)) return;
                            if (processDefinitions)
                                _error = "fsharp_semantic_item_definition_unsupported";
                            break;
                            // SDK declarations, UsingTask registrations, ProjectExtensions, and
                            // unrelated item kinds do not contribute facts to this bounded projection.
                            // They are never executed.
                    }
                }
            }
            finally
            {
                _evaluationDepth--;
            }
        }

        private void ProcessPropertyGroup(XElement group, string documentPath,
            FSharpSemanticDocumentRole role)
        {
            CheckCancellation();
            bool filterToReferenceInputs =
                role == FSharpSemanticDocumentRole.DirectoryBuildTargets;
            bool hasReferenceInputProperties = group.Elements().Any(property =>
                    IsSemanticPropertyName(property.Name.LocalName) ||
                    _directoryReferenceProperties.Contains(property.Name.LocalName));
            bool hasCompilerSchedulingProperties = group.Elements().Any(property =>
                IsCompilerSchedulingPropertyName(property.Name.LocalName));
            if (filterToReferenceInputs && !hasReferenceInputProperties &&
                !hasCompilerSchedulingProperties)
                return;
            if (!ShouldProcess(group, documentPath, out bool process) || !process) return;

            foreach (XElement schedulingProperty in group.Elements().Where(property =>
                         IsCompilerSchedulingPropertyName(property.Name.LocalName)))
            {
                CheckCancellation();
                if (!ShouldProcess(schedulingProperty, documentPath, out process)) return;
                if (!process) continue;
                _error = "fsharp_semantic_target_evaluation_unsupported";
                return;
            }
            if (filterToReferenceInputs && !hasReferenceInputProperties) return;

            if (_semanticItemPhaseStarted && group.Elements().Any(property =>
                    _directSemanticItemPhaseStarted ||
                    _referenceInputConsumedProperties.Contains(property.Name.LocalName)))
            {
                _error = "fsharp_semantic_evaluation_order_unsupported";
                return;
            }

            foreach (XElement property in group.Elements())
            {
                CheckCancellation();
                if (filterToReferenceInputs &&
                    !IsSemanticPropertyName(property.Name.LocalName) &&
                    !_directoryReferenceProperties.Contains(property.Name.LocalName))
                    continue;
                if (!ShouldProcess(property, documentPath, out process)) return;
                if (!process) continue;
                if (property.HasElements || ++_propertyAssignments > MaxFSharpSemanticProperties)
                {
                    _error = property.HasElements
                        ? "fsharp_semantic_property_unsupported"
                        : "fsharp_semantic_property_limit";
                    return;
                }

                string name = property.Name.LocalName;
                string raw = property.Value.Trim();
                if (raw.Length > MaxFSharpSemanticPropertyValueChars)
                {
                    _error = "fsharp_semantic_property_value_limit";
                    return;
                }
                if (!TryExpandProperties(raw, name,
                        out string value, out bool complete))
                    return;
                if (value.Length > MaxFSharpSemanticPropertyValueChars)
                {
                    _error = "fsharp_semantic_property_value_limit";
                    return;
                }

                // The caller-selected physical TFM is the one global property in this projection.
                // A multi-target project cannot overwrite it while evaluating one selected context.
                if (name.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase)) continue;
                _properties[name] = new(value, complete);
            }
        }

        private void ProcessItemGroup(XElement group, string documentPath,
            FSharpSemanticDocumentRole role)
        {
            CheckCancellation();
            bool hasSemanticItems = group.Elements().Any(item =>
                IsSemanticItemName(item.Name.LocalName));
            bool hasNonPackageVersionSemanticItems = group.Elements().Any(item =>
                IsSemanticItemName(item.Name.LocalName) &&
                !item.Name.LocalName.Equals("PackageVersion",
                    StringComparison.OrdinalIgnoreCase));
            bool groupProcess = true;
            if (hasSemanticItems)
            {
                if (!ShouldProcess(group, documentPath, out groupProcess)) return;
                if (!groupProcess && hasNonPackageVersionSemanticItems &&
                    ConditionMayDependOnProperties(group))
                {
                    _semanticItemPhaseStarted = true;
                    _directSemanticItemPhaseStarted = true;
                }
            }

            foreach (XElement item in group.Elements())
            {
                CheckCancellation();
                string itemName = item.Name.LocalName;
                if (!IsSemanticItemName(itemName))
                {
                    ProcessReferenceInputItem(group, item, documentPath);
                    continue;
                }
                if (!groupProcess) continue;
                if (!ShouldProcess(item, documentPath, out bool process)) return;
                if (!process)
                {
                    if (!itemName.Equals("PackageVersion",
                            StringComparison.OrdinalIgnoreCase) &&
                        ConditionMayDependOnProperties(item))
                    {
                        _semanticItemPhaseStarted = true;
                        _directSemanticItemPhaseStarted = true;
                    }
                    continue;
                }
                if (itemName.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase))
                {
                    ProcessPackageVersion(item, documentPath);
                    if (_error is not null) return;
                    continue;
                }
                _semanticItemPhaseStarted = true;
                _directSemanticItemPhaseStarted = true;
                if (role == FSharpSemanticDocumentRole.DirectoryPackagesProps)
                {
                    _error = "fsharp_semantic_central_package_management_unsupported";
                    return;
                }
                if (role == FSharpSemanticDocumentRole.ExplicitImport)
                {
                    _error = itemName.Equals("ProjectReference",
                        StringComparison.OrdinalIgnoreCase)
                        ? "fsharp_semantic_project_references_unsupported"
                        : itemName.Equals("PackageReference",
                            StringComparison.OrdinalIgnoreCase)
                            ? null
                            : "fsharp_semantic_import_items_unsupported";
                    if (_error is not null) return;
                }
                if (IsDirectoryBuildRole(role) &&
                    itemName.Equals("Compile", StringComparison.OrdinalIgnoreCase))
                {
                    _error = "fsharp_semantic_directory_build_unsupported";
                    return;
                }

                if (itemName.Equals("Compile", StringComparison.OrdinalIgnoreCase))
                {
                    ProcessCompile(item);
                }
                else if (itemName.Equals("Reference", StringComparison.OrdinalIgnoreCase))
                {
                    ProcessReference(item, documentPath);
                }
                else if (itemName.Equals("ProjectReference",
                             StringComparison.OrdinalIgnoreCase))
                {
                    _error = "fsharp_semantic_project_references_unsupported";
                }
                else if (itemName.Equals("PackageReference",
                             StringComparison.OrdinalIgnoreCase))
                {
                    ProcessPackageReference(item, documentPath);
                }
                else if (itemName.Equals("GlobalPackageReference",
                             StringComparison.OrdinalIgnoreCase))
                {
                    _error = "fsharp_semantic_central_package_management_unsupported";
                }
                else
                {
                    _error = "fsharp_semantic_reference_unresolved";
                }
                if (_error is not null) return;
            }
        }

        private void ProcessPackageReference(XElement item, string documentPath)
        {
            string? rawInclude = item.Attribute("Include")?.Value.Trim();
            string? rawUpdate = item.Attribute("Update")?.Value.Trim();
            string? rawRemove = item.Attribute("Remove")?.Value.Trim();
            int operations = (rawInclude is null ? 0 : 1) + (rawUpdate is null ? 0 : 1) +
                             (rawRemove is null ? 0 : 1);
            if (operations != 1 || item.Attribute("Exclude") is not null)
            {
                _error = "fsharp_semantic_package_reference_unresolved";
                return;
            }
            string raw = rawInclude ?? rawUpdate ?? rawRemove!;
            if (!TryExpandItemSpecs(raw, out List<string> specs))
            {
                _error ??= "fsharp_semantic_package_reference_unresolved";
                return;
            }
            if (specs.Count == 0)
            {
                _error = "fsharp_semantic_package_reference_unresolved";
                return;
            }

            var ids = new List<string>(specs.Count);
            foreach (string spec in specs)
            {
                if (++_itemListEntries > MaxFSharpSemanticItemListEntries)
                {
                    _error = "fsharp_semantic_item_list_limit";
                    return;
                }
                string id = spec.Trim();
                if (!IsValidPackageId(id))
                {
                    _error = "fsharp_semantic_package_reference_unresolved";
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
                    failureCause: "fsharp_semantic_package_reference_unresolved",
                    out requestedVersion,
                    out versionOverrideUsed))
                return;
            if (requestedVersion is not null &&
                !IsSupportedPackageVersionExpression(requestedVersion))
            {
                _error = "fsharp_semantic_package_reference_unresolved";
                return;
            }

            bool centrallyManaged = IsCentralPackageManagementEnabled();
            if (_error is not null) return;
            if (centrallyManaged && requestedVersion is not null &&
                !versionOverrideUsed)
            {
                _error = "fsharp_semantic_package_reference_unresolved";
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
                _error = "fsharp_semantic_package_reference_unresolved";
                return;
            }

            foreach (string id in ids)
            {
                if (rawRemove is not null)
                    _packageReferences.Remove(id);
                else if (rawUpdate is not null)
                {
                    if (!_packageReferences.ContainsKey(id)) continue;
                    if (requestedVersion is null) continue;
                    _packageReferences[id] = requestedVersion;
                }
                else
                {
                    string? effectiveVersion = requestedVersion;
                    if (effectiveVersion is null &&
                        (!centrallyManaged || !_packageVersions.TryGetValue(id,
                            out effectiveVersion)))
                    {
                        _error = "fsharp_semantic_package_reference_unresolved";
                        return;
                    }
                    _packageReferences[id] = effectiveVersion;
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
                _error = "fsharp_semantic_package_reference_metadata_unsupported";
                return false;
            }

            foreach (XElement metadata in item.Elements().Where(element =>
                         ChangesCompilerReferenceShape(element.Name.LocalName)))
            {
                if (!ShouldProcess(metadata, documentPath, out bool process)) return false;
                if (!process) continue;
                _error = "fsharp_semantic_package_reference_metadata_unsupported";
                return false;
            }
            return true;
        }

        private void ProcessPackageVersion(XElement item, string documentPath)
        {
            string? rawInclude = item.Attribute("Include")?.Value.Trim();
            string? rawUpdate = item.Attribute("Update")?.Value.Trim();
            string? rawRemove = item.Attribute("Remove")?.Value.Trim();
            int operations = (rawInclude is null ? 0 : 1) + (rawUpdate is null ? 0 : 1) +
                             (rawRemove is null ? 0 : 1);
            if (operations != 1 || item.Attribute("Exclude") is not null)
            {
                _error = "fsharp_semantic_central_package_management_unsupported";
                return;
            }

            string raw = rawInclude ?? rawUpdate ?? rawRemove!;
            if (!TryExpandItemSpecs(raw, out List<string> specs) || specs.Count == 0)
            {
                _error ??= "fsharp_semantic_central_package_management_unsupported";
                return;
            }

            string? version = null;
            if (rawRemove is null && !TryGetPackageVersion(item, documentPath,
                    allowVersionOverride: false,
                    failureCause: "fsharp_semantic_central_package_management_unsupported",
                    out version, out _))
                return;
            if (rawInclude is not null && version is null)
            {
                _error = "fsharp_semantic_central_package_management_unsupported";
                return;
            }
            if (version is not null && !SimplePackageVersion.IsMatch(version))
            {
                _error = "fsharp_semantic_central_package_management_unsupported";
                return;
            }

            foreach (string spec in specs)
            {
                if (++_itemListEntries > MaxFSharpSemanticItemListEntries)
                {
                    _error = "fsharp_semantic_item_list_limit";
                    return;
                }
                string id = spec.Trim();
                if (!IsValidPackageId(id))
                {
                    _error = "fsharp_semantic_central_package_management_unsupported";
                    return;
                }
                if (rawRemove is not null)
                    _packageVersions.Remove(id);
                else if (rawInclude is not null && _packageVersions.ContainsKey(id))
                {
                    _error = "fsharp_semantic_central_package_management_unsupported";
                    return;
                }
                else if (rawUpdate is not null && !_packageVersions.ContainsKey(id))
                {
                    _error = "fsharp_semantic_central_package_management_unsupported";
                    return;
                }
                else if (version is not null)
                    _packageVersions[id] = version;
                else if (!_packageVersions.ContainsKey(id))
                {
                    _error = "fsharp_semantic_central_package_management_unsupported";
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
                if (item.Attribute(attributeName)?.Value is { } attributeValue)
                {
                    values.Add(attributeValue);
                    foundVersionOverride |= attributeName.Equals("VersionOverride",
                        StringComparison.OrdinalIgnoreCase);
                }
                foreach (XElement metadata in item.Elements().Where(element =>
                             element.Name.LocalName.Equals(attributeName,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    if (!ShouldProcess(metadata, documentPath, out bool process)) return false;
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
            if (!TryExpandProperties(values[0].Trim(), null,
                    out string expanded, out bool complete))
                return false;
            if (!complete || expanded.Length == 0)
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
            if (!_properties.TryGetValue(name, out FSharpSemanticProperty property))
                return defaultValue;
            if (!property.Complete || !bool.TryParse(property.Value.Trim(), out bool value))
            {
                _error = "fsharp_semantic_central_package_management_unsupported";
                return false;
            }
            return value;
        }

        private static bool IsValidPackageId(string id) =>
            id.Length > 0 && !id.Contains('/') && !id.Contains('\\');

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

        private void ProcessReferenceInputItem(XElement group, XElement item,
            string documentPath)
        {
            string name = item.Name.LocalName;
            if (!_directoryReferenceItemLists.Contains(name)) return;
            if (item.Attribute("Include") is null && item.Attribute("Remove") is null) return;
            // MSBuild evaluates all properties before any items. Even a helper item whose
            // condition is currently false consumes that final property state, so a later
            // property assignment cannot be projected document-sequentially without divergence.
            RegisterReferenceInputPropertyDependencies(item, group);
            _semanticItemPhaseStarted = true;

            string? priorError = _error;
            _error = null;
            try
            {
                if (!ShouldProcess(group, documentPath, out bool processGroup)) return;
                if (!processGroup)
                {
                    _itemLists.TryAdd(name, []);
                    return;
                }
                if (!ShouldProcess(item, documentPath, out bool processItem)) return;
                if (!processItem)
                {
                    _itemLists.TryAdd(name, []);
                    return;
                }
                XAttribute? include = item.Attribute("Include");
                XAttribute? remove = item.Attribute("Remove");
                if (item.HasElements || include is not null && remove is not null ||
                    item.Attribute("Update") is not null ||
                    item.Attribute("Exclude") is not null ||
                    item.Attributes().Any(attribute => attribute.Name.LocalName is not
                        ("Include" or "Remove" or "Condition")))
                {
                    _error = "fsharp_semantic_reference_unresolved";
                    return;
                }

                _itemLists.TryAdd(name, []);

                if (remove?.Value is { } rawRemove)
                {
                    if (!TryExpandItemSpecs(rawRemove, out List<string> removeSpecs)) return;
                    if (_itemLists.TryGetValue(name, out List<string>? current))
                    {
                        current.RemoveAll(existing => removeSpecs.Contains(existing,
                            StringComparer.OrdinalIgnoreCase));
                    }
                }
                if (include?.Value is not { } rawInclude) return;
                if (!TryExpandItemSpecs(rawInclude, out List<string> includeSpecs)) return;
                List<string> list = _itemLists.GetValueOrDefault(name) ?? [];
                if (!_itemLists.ContainsKey(name)) _itemLists[name] = list;
                foreach (string spec in includeSpecs)
                {
                    if (++_itemListEntries > MaxFSharpSemanticItemListEntries)
                    {
                        _error = "fsharp_semantic_item_list_limit";
                        return;
                    }
                    list.Add(spec);
                }
            }
            finally
            {
                if (_error is not null) _incompleteItemListErrors[name] = _error;
                _error = priorError;
            }
        }

        private static bool ConditionMayDependOnProperties(XElement element) =>
            element.Attribute("Condition")?.Value.Contains("$(", StringComparison.Ordinal) == true;

        private void RegisterReferenceInputPropertyDependencies(XElement item, XElement boundary)
        {
            void Add(string? expression)
            {
                if (string.IsNullOrEmpty(expression)) return;
                foreach (Match match in PropertyReference.Matches(expression))
                    _referenceInputConsumedProperties.Add(match.Groups["name"].Value);
            }

            foreach (XAttribute attribute in item.Attributes()) Add(attribute.Value);
            XElement? ancestor = item.Parent;
            while (ancestor is not null)
            {
                Add(ancestor.Attribute("Condition")?.Value);
                if (ReferenceEquals(ancestor, boundary)) break;
                ancestor = ancestor.Parent;
            }
        }

        private void ProcessCompile(XElement item)
        {
            string? raw = item.Attribute("Include")?.Value.Trim();
            if (raw is null || item.Attribute("Remove") is not null ||
                item.Attribute("Update") is not null || item.Attribute("Exclude") is not null)
            {
                _error = "fsharp_semantic_compile_order_unavailable";
                return;
            }
            if (!TryExpandProperties(raw, null,
                    out string include, out bool complete)) return;
            if (!complete)
            {
                _error = "fsharp_semantic_compile_order_unavailable";
                return;
            }

            foreach (string spec in include.Split(';',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                CheckCancellation();
                if (spec.Contains('*') || spec.Contains('?'))
                {
                    _error = "fsharp_semantic_compile_order_unavailable";
                    return;
                }
                if (!TryNormalizeSemanticRelative(_projectDir, spec, out string source))
                {
                    _error = "fsharp_semantic_path_outside_workspace";
                    return;
                }
                if (!source.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) &&
                    !source.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase))
                {
                    _error = "fsharp_semantic_compile_item_unsupported";
                    return;
                }
                if (_sourceSet.Add(source)) _sources.Add(source);
            }
        }

        private void ProcessReference(XElement item, string documentPath)
        {
            string? rawInclude = item.Attribute("Include")?.Value.Trim();
            string? rawRemove = item.Attribute("Remove")?.Value.Trim();
            if (item.Attribute("Update") is not null || item.Attribute("Exclude") is not null ||
                (rawInclude is null) == (rawRemove is null))
            {
                _error = "fsharp_semantic_reference_unresolved";
                return;
            }

            if (rawRemove is not null)
            {
                if (item.HasElements || !TryExpandItemSpecs(rawRemove,
                        out List<string> removeSpecs))
                {
                    _error ??= "fsharp_semantic_reference_unresolved";
                    return;
                }
                _references.RemoveAll(reference => removeSpecs.Any(remove =>
                    remove.Equals(reference.ItemSpec, StringComparison.OrdinalIgnoreCase) ||
                    remove.Equals(reference.SimpleName, StringComparison.OrdinalIgnoreCase)));
                return;
            }

            if (!TryExpandItemSpecs(rawInclude!, out List<string> includes))
            {
                _error ??= "fsharp_semantic_reference_unresolved";
                return;
            }
            if (includes.Count == 0)
            {
                if (ItemReferenceOccurrence.IsMatch(rawInclude!)) return;
                _error = "fsharp_semantic_reference_unresolved";
                return;
            }

            var activeHints = new List<XElement>();
            foreach (XElement hint in item.Elements().Where(element =>
                         element.Name.LocalName == "HintPath"))
            {
                CheckCancellation();
                if (!ShouldProcess(hint, documentPath, out bool process)) return;
                if (process) activeHints.Add(hint);
            }
            if (activeHints.Count == 0)
            {
                foreach (string include in includes)
                {
                    string simpleName = include.Split(',')[0].Trim();
                    if (simpleName.Length == 0)
                    {
                        _error = "fsharp_semantic_reference_unresolved";
                        return;
                    }
                    _references.Add(new(include, simpleName, null));
                }
                return;
            }
            if (activeHints.Count != 1 || activeHints[0].HasElements)
            {
                _error = "fsharp_semantic_reference_unresolved";
                return;
            }

            string rawHint = activeHints[0].Value.Trim();
            if (!TryExpandProperties(rawHint, null,
                    out string value, out bool complete)) return;
            if (!complete || value.Length == 0)
            {
                _error = "fsharp_semantic_reference_unresolved";
                return;
            }
            if (!TryNormalizeSemanticRelative(_projectDir, value, out string hintPath))
            {
                _error = "fsharp_semantic_path_outside_workspace";
                return;
            }
            foreach (string include in includes)
            {
                string simpleName = include.Split(',')[0].Trim();
                if (simpleName.Length == 0)
                {
                    _error = "fsharp_semantic_reference_unresolved";
                    return;
                }
                _references.Add(new(include, simpleName, hintPath));
            }
        }

        private bool TryExpandItemSpecs(string raw, out List<string> specs)
        {
            specs = [];
            if (!TryExpandProperties(raw.Trim(), null,
                    out string expanded, out bool complete, allowItemReferences: true))
                return false;
            if (!complete)
            {
                _error = "fsharp_semantic_reference_unresolved";
                return false;
            }

            foreach (string token in expanded.Split(';',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                CheckCancellation();
                if (token.Contains('*') || token.Contains('?'))
                {
                    _error = "fsharp_semantic_reference_unresolved";
                    return false;
                }
                Match itemReference = ItemReference.Match(token);
                if (!itemReference.Success)
                {
                    if (token.Contains("@(", StringComparison.Ordinal))
                    {
                        _error = "fsharp_semantic_reference_unresolved";
                        return false;
                    }
                    specs.Add(token);
                    continue;
                }

                string name = itemReference.Groups["name"].Value;
                if (_incompleteItemListErrors.TryGetValue(name, out string? itemListError))
                {
                    _error = itemListError;
                    return false;
                }
                if (!_itemLists.TryGetValue(name, out List<string>? itemSpecs))
                {
                    _error = "fsharp_semantic_reference_unresolved";
                    return false;
                }
                specs.AddRange(itemSpecs);
            }
            return true;
        }

        private void ProcessImportGroup(XElement group, string documentPath,
            FSharpSemanticDocumentRole role, int depth)
        {
            CheckCancellation();
            if (!ShouldProcess(group, documentPath, out bool process) || !process) return;
            foreach (XElement child in group.Elements())
            {
                CheckCancellation();
                if (child.Name.LocalName != "Import")
                {
                    _error = "fsharp_semantic_import_unsupported";
                    return;
                }
                ProcessImport(child, documentPath, role, depth);
                if (_error is not null) return;
            }
        }

        private void ProcessImport(XElement import, string documentPath,
            FSharpSemanticDocumentRole role, int depth)
        {
            CheckCancellation();
            if (import.Attributes().Any(attribute => attribute.Name.LocalName.Equals("Sdk",
                    StringComparison.OrdinalIgnoreCase)))
            {
                _error = "fsharp_semantic_sdk_unsupported";
                return;
            }

            string rawProject = import.Attribute("Project")?.Value.Trim() ?? "";
            if (rawProject.Length == 0)
            {
                _error = "fsharp_semantic_import_unsupported";
                return;
            }
            // A recognized toolchain import contributes no project-specific facts to this
            // projection. Its existence guard is therefore immaterial and is not an invitation to
            // resolve ambient MSBuildToolsPath/MSBuildBinPath values.
            if (!rawProject.Equals("$(FSharpTargetsPath)",
                    StringComparison.OrdinalIgnoreCase) &&
                AcceptKnownFSharpSemanticImport(rawProject))
                return;
            string? deferredConditionError = null;
            if (!ShouldProcess(import, documentPath, out bool process))
            {
                if (!IsDirectoryPropsAuthorityRole(role)) return;
                deferredConditionError = _error ??
                                         "fsharp_semantic_condition_unsupported";
                _error = null;
            }
            else if (!process)
            {
                return;
            }

            if (rawProject.Equals("$(FSharpTargetsPath)",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!_properties.TryGetValue("FSharpTargetsPath",
                        out FSharpSemanticProperty configuredTargets))
                {
                    // The conventional legacy placeholder is itself a recognized terminal. An
                    // explicit project override is inspected below so a local targets file cannot
                    // masquerade as compiler infrastructure.
                    AcceptKnownFSharpSemanticImport(rawProject);
                    return;
                }
                if (AcceptKnownFSharpSemanticImport(configuredTargets.Value)) return;
                if (!configuredTargets.Complete)
                {
                    _error = "fsharp_semantic_import_unsupported";
                    return;
                }
                rawProject = configuredTargets.Value;
            }

            // These imports are compiler/toolchain infrastructure. Stage 2A supplies its own
            // bounded framework/compiler inputs and never opens or executes the imported targets.
            if (AcceptKnownFSharpSemanticImport(rawProject)) return;

            if (!TryExpandProperties(rawProject, null,
                    out string expandedProject, out bool complete)) return;
            if (AcceptKnownFSharpSemanticImport(expandedProject)) return;
            if (!complete || expandedProject.Contains('*') || expandedProject.Contains('?') ||
                expandedProject.Contains(';'))
            {
                _error = "fsharp_semantic_import_unsupported";
                return;
            }

            string documentDir = WorkspacePaths.ToGitPath(
                Path.GetDirectoryName(documentPath) ?? "");
            if (!TryNormalizeSemanticRelative(documentDir, expandedProject,
                    out string importPath))
            {
                _error = "fsharp_semantic_import_path_outside_workspace";
                return;
            }
            bool supportedProps = importPath.EndsWith(".props",
                StringComparison.OrdinalIgnoreCase);
            bool supportedDirectoryTargets = IsDirectoryBuildRole(role) &&
                importPath.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);
            if (!supportedProps && !supportedDirectoryTargets)
            {
                _error = "fsharp_semantic_import_unsupported";
                return;
            }
            if (deferredConditionError is not null)
            {
                if (TryResolveImportRoot(importPath, out XElement? deferredRoot) &&
                    deferredRoot is not null &&
                    !ContainsDirectoryBuildSemanticFacts(deferredRoot))
                {
                    _error = null;
                    return;
                }
                _error = deferredConditionError;
                return;
            }
            ProcessResolvedImport(importPath,
                IsDirectoryPropsAuthorityRole(role)
                    ? role
                    : FSharpSemanticDocumentRole.ExplicitImport,
                depth);
        }

        private void ProcessResolvedImport(string importPath,
            FSharpSemanticDocumentRole role, int depth)
        {
            if (++_importOccurrences > MaxFSharpSemanticImportOccurrences)
            {
                _error = "fsharp_semantic_import_occurrence_limit";
                return;
            }
            if (depth >= MaxFSharpSemanticImportDepth)
            {
                _error = "fsharp_semantic_import_depth_limit";
                return;
            }
            if (!_activeImports.Add(importPath))
            {
                _error = "fsharp_semantic_import_cycle";
                return;
            }

            try
            {
                if (!TryResolveImportRoot(importPath, out XElement? importedRoot) ||
                    importedRoot is null) return;
                // Imported project inputs are data-only in this projection. Any SDK declaration would
                // introduce resolver-controlled implicit props/targets at a nested authority
                // boundary, even when it names the otherwise allowlisted root SDK.
                if (!ValidateSdkAuthority(importedRoot, allowStandardSdk: false))
                {
                    _error = "fsharp_semantic_sdk_unsupported";
                    return;
                }
                ProcessContainer(importedRoot, importPath, role, depth + 1);
            }
            finally
            {
                _activeImports.Remove(importPath);
            }
        }

        private bool TryResolveImportRoot(string importPath, out XElement? importedRoot)
        {
            CheckCancellation();
            if (_importRoots.TryGetValue(importPath, out importedRoot)) return true;
            if (!TryResolveImport(importPath, out string? content)) return false;
            CheckCancellation();
            if (content is null || !TryLoadProjectXml(content,
                    MaxFSharpSemanticImportBytes, _cancellationToken, out importedRoot) ||
                importedRoot is null)
            {
                _error = "fsharp_semantic_import_unavailable";
                return false;
            }
            _importRoots[importPath] = importedRoot;
            RegisterDirectoryBuildDependencies(importedRoot);
            return true;
        }

        private void RegisterDirectoryBuildDependencies(XElement root)
        {
            XElement[] elements = root.Descendants().ToArray();
            ILookup<string, XElement> elementsByName = elements.ToLookup(
                element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase);
            var pendingNames = new Queue<string>();
            var scheduledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Schedule(string name)
            {
                if (scheduledNames.Add(name)) pendingNames.Enqueue(name);
            }

            bool AddProperty(string name)
            {
                bool added = _directoryReferenceProperties.Add(name);
                Schedule(name);
                if (added && _directoryReferenceProperties.Count +
                    _directoryReferenceItemLists.Count > MaxFSharpSemanticDependencyNodes)
                {
                    _error = "fsharp_semantic_dependency_limit";
                    return false;
                }
                return true;
            }

            bool AddItemList(string name)
            {
                bool added = _directoryReferenceItemLists.Add(name);
                Schedule(name);
                if (added && _directoryReferenceProperties.Count +
                    _directoryReferenceItemLists.Count > MaxFSharpSemanticDependencyNodes)
                {
                    _error = "fsharp_semantic_dependency_limit";
                    return false;
                }
                return true;
            }

            void AddProperties(string? expression)
            {
                if (_error is not null || string.IsNullOrEmpty(expression)) return;
                foreach (Match match in PropertyReference.Matches(expression))
                {
                    if (!AddProperty(match.Groups["name"].Value)) return;
                }
            }

            void AddItemLists(string? expression)
            {
                if (_error is not null || string.IsNullOrEmpty(expression)) return;
                foreach (Match match in ItemReferenceOccurrence.Matches(expression))
                {
                    if (!AddItemList(match.Groups["name"].Value)) return;
                }
            }

            void AddElementInputs(XElement element)
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    AddProperties(attribute.Value);
                    if (_error is not null) return;
                    AddItemLists(attribute.Value);
                    if (_error is not null) return;
                }
                if (!element.HasElements)
                {
                    AddProperties(element.Value);
                    if (_error is not null) return;
                    AddItemLists(element.Value);
                    if (_error is not null) return;
                }
                foreach (XElement ancestor in element.Ancestors())
                {
                    if (ancestor == root) break;
                    AddProperties(ancestor.Attribute("Condition")?.Value);
                    if (_error is not null) return;
                    AddItemLists(ancestor.Attribute("Condition")?.Value);
                    if (_error is not null) return;
                }
            }

            foreach (string name in _directoryReferenceProperties) Schedule(name);
            foreach (string name in _directoryReferenceItemLists) Schedule(name);

            foreach (XElement element in elements)
            {
                CheckCancellation();
                if (IsSemanticItemName(element.Name.LocalName) ||
                    element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase))
                    AddElementInputs(element);
                if (IsSemanticPropertyName(element.Name.LocalName))
                    AddProperty(element.Name.LocalName);
                if (_error is not null) return;
            }

            while (pendingNames.TryDequeue(out string? name))
            {
                CheckCancellation();
                foreach (XElement element in elementsByName[name])
                {
                    CheckCancellation();
                    AddElementInputs(element);
                    if (_error is not null) return;
                }
            }
        }

        private bool ContainsDirectoryBuildSemanticFacts(XElement root)
        {
            if (HasCompilerSchedulingProjectAttribute(root)) return true;
            foreach (XElement element in root.Descendants())
            {
                CheckCancellation();
                string name = element.Name.LocalName;
                if (IsSemanticItemName(name) || IsSemanticPropertyName(name) ||
                    IsCompilerSchedulingPropertyName(name) ||
                    _directoryReferenceItemLists.Contains(name) ||
                    _directoryReferenceProperties.Contains(name) ||
                    name.Equals("Import", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Fsc", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Target", StringComparison.OrdinalIgnoreCase) &&
                    ContainsSemanticTargetFacts(element))
                    return true;
            }
            return false;
        }

        private void ProcessChoose(XElement choose, string documentPath,
            FSharpSemanticDocumentRole role, int depth)
        {
            CheckCancellation();
            bool hasSemanticItemPhaseFacts =
                ContainsSemanticChooseItemPhaseFacts(choose, out bool hasDirectSemanticFacts);
            if (!ShouldProcess(choose, documentPath, out bool process)) return;
            if (!process)
            {
                if (hasSemanticItemPhaseFacts && ConditionMayDependOnProperties(choose))
                {
                    _semanticItemPhaseStarted = true;
                    _directSemanticItemPhaseStarted |= hasDirectSemanticFacts;
                }
                return;
            }
            XElement? otherwise = null;
            foreach (XElement branch in choose.Elements())
            {
                CheckCancellation();
                if (branch.Name.LocalName == "Otherwise")
                {
                    if (otherwise is not null)
                    {
                        _error = "fsharp_semantic_condition_unsupported";
                        return;
                    }
                    otherwise = branch;
                    continue;
                }
                if (branch.Name.LocalName != "When" ||
                    branch.Attribute("Condition") is null)
                {
                    _error = "fsharp_semantic_condition_unsupported";
                    return;
                }
                if (!ShouldProcess(branch, documentPath, out bool selected)) return;
                if (!selected) continue;
                ProcessContainer(branch, documentPath, role, depth);
                if (_error is null && hasSemanticItemPhaseFacts)
                {
                    _semanticItemPhaseStarted = true;
                    _directSemanticItemPhaseStarted |= hasDirectSemanticFacts;
                }
                return;
            }
            if (otherwise is not null)
                ProcessContainer(otherwise, documentPath, role, depth);
            if (_error is null && hasSemanticItemPhaseFacts)
            {
                _semanticItemPhaseStarted = true;
                _directSemanticItemPhaseStarted |= hasDirectSemanticFacts;
            }
        }

        private bool AcceptKnownFSharpSemanticImport(string project)
        {
            if (!IsKnownFSharpSemanticImport(project)) return false;
            _partialReasons.Add("fsharp_semantic_toolchain_implicit_authority");
            return true;
        }

        private bool ShouldProcess(XElement element, string documentPath, out bool process)
        {
            process = false;
            XAttribute? condition = element.Attribute("Condition");
            if (condition is null)
            {
                process = true;
                return true;
            }
            if (condition.Value.Length > MaxFSharpSemanticConditionChars)
            {
                _error = "fsharp_semantic_condition_limit";
                return false;
            }
            string? unsetSelfProperty = IsCanonicalUnsetSelfCondition(element,
                condition.Value) ? element.Name.LocalName : null;
            if (!TryEvaluateCondition(condition.Value, documentPath, out process,
                    unsetSelfProperty))
            {
                _error ??= "fsharp_semantic_condition_unsupported";
                return false;
            }
            return true;
        }

        private bool TryEvaluateCondition(string condition, string documentPath, out bool result,
            string? unsetSelfProperty = null, int depth = 0)
        {
            CheckCancellation();
            result = false;
            if (depth > MaxFSharpSemanticConditionDepth)
            {
                _error = "fsharp_semantic_condition_depth_limit";
                return false;
            }
            if (!TryExpandProperties(condition, unsetSelfProperty,
                    out string expanded, out bool complete))
                return false;
            if (!complete)
            {
                _error = "fsharp_semantic_condition_property_unresolved";
                return false;
            }
            expanded = expanded.Trim();
            if (expanded.Length == 0) return false;
            if (!HasBalancedConditionDelimiters(expanded)) return false;
            if (depth == 0 && !ValidateConditionSyntax(expanded, depth)) return false;

            if (TrySplitLogical(expanded, "Or", out string left, out string right))
            {
                if (!TryEvaluateCondition(left, documentPath, out bool leftResult,
                        unsetSelfProperty, depth + 1))
                    return false;
                if (leftResult)
                {
                    result = true;
                    return true;
                }
                return TryEvaluateCondition(right, documentPath, out result,
                    unsetSelfProperty, depth + 1);
            }
            if (TrySplitLogical(expanded, "And", out left, out right))
            {
                if (!TryEvaluateCondition(left, documentPath, out bool leftResult,
                        unsetSelfProperty, depth + 1))
                    return false;
                if (!leftResult)
                {
                    result = false;
                    return true;
                }
                return TryEvaluateCondition(right, documentPath, out result,
                    unsetSelfProperty, depth + 1);
            }

            if (HasWrappingParentheses(expanded))
                return TryEvaluateCondition(expanded[1..^1], documentPath, out result,
                    unsetSelfProperty, depth + 1);
            if (expanded[0] == '!')
            {
                if (!TryEvaluateCondition(expanded[1..], documentPath, out bool inner,
                        unsetSelfProperty, depth + 1))
                    return false;
                result = !inner;
                return true;
            }

            Match exists = ExistsCondition.Match(expanded);
            if (exists.Success)
            {
                string rawPath = exists.Groups["path"].Value;
                string documentDir = WorkspacePaths.ToGitPath(
                    Path.GetDirectoryName(documentPath) ?? "");
                if (!TryNormalizeSemanticRelative(documentDir, rawPath, out string path) ||
                    !path.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!TryResolveImport(path, out string? content)) return false;
                result = content is not null;
                return true;
            }

            if (TryParseConditionOperand(expanded, out string scalar) &&
                bool.TryParse(scalar, out result)) return true;
            if (!TryFindComparison(expanded, out left, out string op, out right)) return false;
            if (!TryParseConditionOperand(left, out left) ||
                !TryParseConditionOperand(right, out right)) return false;
            switch (op)
            {
                case "==":
                    result = left.Equals(right, StringComparison.OrdinalIgnoreCase);
                    return true;
                case "!=":
                    result = !left.Equals(right, StringComparison.OrdinalIgnoreCase);
                    return true;
                default:
                    if (!TryCompareOrdered(left, right, out int comparison)) return false;
                    result = op switch
                    {
                        ">" => comparison > 0,
                        ">=" => comparison >= 0,
                        "<" => comparison < 0,
                        "<=" => comparison <= 0,
                        _ => false,
                    };
                    return true;
            }
        }

        private bool ValidateConditionSyntax(string expression, int depth)
        {
            CheckCancellation();
            if (depth > MaxFSharpSemanticConditionDepth)
            {
                _error = "fsharp_semantic_condition_depth_limit";
                return false;
            }

            expression = expression.Trim();
            if (expression.Length == 0 || !HasBalancedConditionDelimiters(expression))
                return false;
            if (TrySplitLogical(expression, "Or", out string left, out string right) ||
                TrySplitLogical(expression, "And", out left, out right))
                return ValidateConditionSyntax(left, depth + 1) &&
                       ValidateConditionSyntax(right, depth + 1);
            if (HasWrappingParentheses(expression))
                return ValidateConditionSyntax(expression[1..^1], depth + 1);
            if (expression[0] == '!')
                return ValidateConditionSyntax(expression[1..], depth + 1);
            if (ExistsCondition.IsMatch(expression)) return true;
            if (TryParseConditionOperand(expression, out string scalar) &&
                bool.TryParse(scalar, out _)) return true;
            return TryFindComparison(expression, out left, out _, out right) &&
                   TryParseConditionOperand(left, out _) &&
                   TryParseConditionOperand(right, out _);
        }

        private bool TryExpandProperties(string input, string? selfProperty,
            out string output, out bool complete, bool allowItemReferences = false)
        {
            CheckCancellation();
            output = "";
            complete = false;
            if (input.Length > MaxFSharpSemanticPropertyValueChars ||
                ContainsUnsupportedExpansion(input, allowItemReferences))
            {
                _error = "fsharp_semantic_property_function_unsupported";
                return false;
            }

            var builder = new StringBuilder(input.Length);
            int cursor = 0;
            bool allComplete = true;
            foreach (Match match in PropertyReference.Matches(input))
            {
                CheckCancellation();
                builder.Append(input, cursor, match.Index - cursor);
                string name = match.Groups["name"].Value;
                if (_properties.TryGetValue(name, out FSharpSemanticProperty property))
                {
                    builder.Append(property.Value);
                    allComplete &= property.Complete;
                }
                else if (name.Equals(selfProperty, StringComparison.OrdinalIgnoreCase))
                {
                    // Unset properties are empty only for a property's own value and the narrowly
                    // recognized self-default condition. Other unknown condition properties may be
                    // ambient/global build inputs, so treating them as proven empty would be false.
                }
                else
                {
                    builder.Append(match.Value);
                    allComplete = false;
                }
                cursor = match.Index + match.Length;
                if (builder.Length > MaxFSharpSemanticPropertyValueChars)
                {
                    _error = "fsharp_semantic_property_value_limit";
                    return false;
                }
            }
            builder.Append(input, cursor, input.Length - cursor);
            if (builder.Length > MaxFSharpSemanticPropertyValueChars)
            {
                _error = "fsharp_semantic_property_value_limit";
                return false;
            }
            output = builder.ToString();
            complete = allComplete &&
                       !output.Contains("$(", StringComparison.Ordinal) &&
                       !output.Contains("%(", StringComparison.Ordinal) &&
                       (allowItemReferences ||
                        !output.Contains("@(", StringComparison.Ordinal));
            return true;
        }

        private bool ContainsUnsupportedExpansion(string input, bool allowItemReferences)
        {
            CheckCancellation();
            if (!allowItemReferences && input.Contains("@(", StringComparison.Ordinal) ||
                input.Contains("%(", StringComparison.Ordinal))
                return true;

            MatchCollection simpleProperties = PropertyReference.Matches(input);
            int simpleIndex = 0;
            int cursor = 0;
            while ((cursor = input.IndexOf("$(", cursor, StringComparison.Ordinal)) >= 0)
            {
                CheckCancellation();
                while (simpleIndex < simpleProperties.Count &&
                       simpleProperties[simpleIndex].Index < cursor)
                    simpleIndex++;
                if (simpleIndex >= simpleProperties.Count ||
                    simpleProperties[simpleIndex].Index != cursor)
                    return true;
                cursor += simpleProperties[simpleIndex].Length;
                simpleIndex++;
            }
            return false;
        }

        private static bool IsCanonicalUnsetSelfCondition(XElement element,
            string condition)
        {
            if (element.Parent?.Name.LocalName != "PropertyGroup") return false;
            MatchCollection references = PropertyReference.Matches(condition);
            if (references.Count != 1 ||
                !references[0].Groups["name"].Value.Equals(element.Name.LocalName,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            string withoutReference = condition.Remove(references[0].Index,
                references[0].Length);
            string normalized = string.Concat(withoutReference.Where(character =>
                !char.IsWhiteSpace(character))).Replace('"', '\'');
            return normalized == "''==''";
        }

        private bool TryCompilerProperty(string name, out string value)
        {
            value = "";
            if (!_properties.TryGetValue(name, out FSharpSemanticProperty property)) return true;
            if (!property.Complete) return false;
            value = property.Value.Trim();
            return true;
        }

        private bool TryResolveImport(string path, out string? content)
        {
            CheckCancellation();
            if (_importSnapshots.TryGetValue(path, out content)) return true;
            if (++_importFiles > MaxFSharpSemanticImportFiles)
            {
                _error = "fsharp_semantic_import_count_limit";
                content = null;
                return false;
            }

            long? indexedBytes = _importSizeResolver?.Invoke(path);
            CheckCancellation();
            if (indexedBytes is < 0 or > MaxFSharpSemanticImportBytes)
            {
                _error = indexedBytes < 0
                    ? "fsharp_semantic_import_unavailable"
                    : "fsharp_semantic_import_bytes_limit";
                content = null;
                return false;
            }

            content = _importResolver?.Invoke(path);
            CheckCancellation();
            if (content is not null)
            {
                long bytes;
                try
                {
                    int actualBytes = Encoding.UTF8.GetByteCount(content);
                    bytes = Math.Max(actualBytes, indexedBytes ?? 0L);
                    _importBytes = checked(_importBytes + bytes);
                }
                catch (OverflowException)
                {
                    _error = "fsharp_semantic_import_bytes_limit";
                    content = null;
                    return false;
                }
                if (bytes > MaxFSharpSemanticImportBytes ||
                    _importBytes > MaxFSharpSemanticImportBytes)
                {
                    _error = "fsharp_semantic_import_bytes_limit";
                    content = null;
                    return false;
                }
            }
            _importSnapshots[path] = content;
            return true;
        }

        private static bool TryLoadProjectXml(string content, int maxCharacters,
            CancellationToken cancellationToken,
            out XElement? root)
        {
            root = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = new StringReader(content);
                using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = maxCharacters,
                });
                root = XDocument.Load(reader, LoadOptions.None).Root;
                cancellationToken.ThrowIfCancellationRequested();
                return root is not null && root.Name.LocalName == "Project";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private bool ContainsSemanticChooseItemPhaseFacts(
            XElement choose, out bool hasDirectSemanticFacts)
        {
            hasDirectSemanticFacts = false;
            bool hasReferenceInputFacts = false;
            foreach (XElement element in choose.Descendants())
            {
                CheckCancellation();
                if (IsSemanticItemName(element.Name.LocalName) ||
                    element.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase) &&
                    ContainsSemanticTargetFacts(element))
                {
                    hasDirectSemanticFacts = true;
                    return true;
                }
                if (_directoryReferenceItemLists.Contains(element.Name.LocalName) &&
                    (element.Attribute("Include") is not null ||
                     element.Attribute("Remove") is not null))
                {
                    RegisterReferenceInputPropertyDependencies(element, choose);
                    hasReferenceInputFacts = true;
                }
            }
            return hasReferenceInputFacts;
        }

        private bool ContainsSemanticTargetFacts(XElement target)
        {
            bool replacesCompilerTarget = IsCompilerTargetName(target.Attribute("Name")?.Value);
            bool hooksCompilerTarget = replacesCompilerTarget ||
                ContainsCompilerTargetName(target.Attribute("BeforeTargets")?.Value) ||
                ContainsCompilerTargetName(target.Attribute("AfterTargets")?.Value);
            if (replacesCompilerTarget) return true;

            foreach (XElement element in target.Descendants())
            {
                CheckCancellation();
                if (element.Name.LocalName.Equals("Fsc", StringComparison.OrdinalIgnoreCase) ||
                    IsSemanticItemName(element.Name.LocalName) ||
                    IsSemanticPropertyName(element.Name.LocalName) ||
                    IsCompilerSchedulingPropertyName(element.Name.LocalName) ||
                    element.Name.LocalName.Equals("Output", StringComparison.OrdinalIgnoreCase) &&
                    (IsSemanticPropertyName(element.Attribute("PropertyName")?.Value) ||
                     IsCompilerSchedulingPropertyName(
                         element.Attribute("PropertyName")?.Value) ||
                     IsSemanticItemName(element.Attribute("ItemName")?.Value)))
                    return true;
            }
            if (!hooksCompilerTarget) return false;

            // A compiler-hooked target can rewrite indexed source or HintPath bytes without
            // declaring a semantic MSBuild item. Only diagnostics are proven side-effect-free;
            // every other task/container remains outside this non-executing projection.
            return target.Elements().Any(element => element.Name.LocalName is not
                ("Message" or "Warning" or "Error"));
        }

        private static bool ContainsCompilerTargetName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.Contains("$(", StringComparison.Ordinal)) return true;
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                    StringSplitOptions.TrimEntries)
                .Any(IsCompilerTargetName);
        }

        private static bool IsCompilerTargetName(string? name) => name is not null &&
            (name.Equals("Compile", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("CoreCompile", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("Fsc", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("BeforeCompile", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("AfterCompile", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("BeforeCoreCompile", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("AfterCoreCompile", StringComparison.OrdinalIgnoreCase));

        private static bool IsCompilerSchedulingPropertyName(string? name) =>
            name?.EndsWith("DependsOn", StringComparison.OrdinalIgnoreCase) == true;

        private static bool HasCompilerSchedulingProjectAttribute(XElement element) =>
            element.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(element.Attribute("InitialTargets")?.Value);

        private static bool IsSemanticPropertyName(string? name) => name is not null &&
            (name.Equals("DefineConstants", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("LangVersion", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("OtherFlags", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("FscAdditionalArgs", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("AssemblyName", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("DisableImplicitFrameworkDefines", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("ManagePackageVersionsCentrally",
                 StringComparison.OrdinalIgnoreCase) ||
             name.Equals("CentralPackageVersionOverrideEnabled",
                 StringComparison.OrdinalIgnoreCase));

        private static bool IsSemanticItemName(string? name) => name is not null &&
            (name.Equals("Compile", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("Reference", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("GlobalPackageReference", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("ReferencePath", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("ReferencePathWithRefAssemblies", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("ResolvedCompileFileDefinitions", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("_ResolvedProjectReferencePaths", StringComparison.OrdinalIgnoreCase));

        private bool TrySplitLogical(string expression, string word,
            out string left, out string right)
        {
            CheckCancellation();
            left = right = "";
            char quote = '\0';
            int depth = 0;
            for (int i = 0; i <= expression.Length - word.Length; i++)
            {
                CheckCancellation();
                char ch = expression[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch is '\'' or '"')
                {
                    quote = ch;
                    continue;
                }
                if (ch == '(') { depth++; continue; }
                if (ch == ')') { depth--; continue; }
                if (depth != 0 ||
                    !expression.AsSpan(i, word.Length).Equals(word,
                        StringComparison.OrdinalIgnoreCase)) continue;
                bool before = i == 0 || char.IsWhiteSpace(expression[i - 1]) ||
                              expression[i - 1] == ')';
                int afterIndex = i + word.Length;
                bool after = afterIndex == expression.Length ||
                             char.IsWhiteSpace(expression[afterIndex]) ||
                             expression[afterIndex] == '(';
                if (!before || !after) continue;
                left = expression[..i];
                right = expression[afterIndex..];
                return left.Trim().Length > 0 && right.Trim().Length > 0;
            }
            return false;
        }

        private bool HasBalancedConditionDelimiters(string expression)
        {
            CheckCancellation();
            char quote = '\0';
            int depth = 0;
            foreach (char ch in expression)
            {
                CheckCancellation();
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch is '\'' or '"')
                {
                    quote = ch;
                    continue;
                }
                if (ch == '(')
                {
                    depth++;
                    continue;
                }
                if (ch == ')')
                {
                    if (depth == 0) return false;
                    depth--;
                }
            }
            return quote == '\0' && depth == 0;
        }

        private bool HasWrappingParentheses(string expression)
        {
            CheckCancellation();
            if (expression.Length < 2 || expression[0] != '(' || expression[^1] != ')')
                return false;
            char quote = '\0';
            int depth = 0;
            for (int i = 0; i < expression.Length; i++)
            {
                CheckCancellation();
                char ch = expression[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch is '\'' or '"') { quote = ch; continue; }
                if (ch == '(') depth++;
                else if (ch == ')' && --depth == 0 && i != expression.Length - 1)
                    return false;
            }
            return depth == 0;
        }

        private bool TryFindComparison(string expression, out string left,
            out string op, out string right)
        {
            CheckCancellation();
            left = op = right = "";
            char quote = '\0';
            int depth = 0;
            for (int i = 0; i < expression.Length; i++)
            {
                CheckCancellation();
                char ch = expression[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch is '\'' or '"') { quote = ch; continue; }
                if (ch == '(') { depth++; continue; }
                if (ch == ')') { depth--; continue; }
                if (depth != 0) continue;
                foreach (string candidate in new[] { "==", "!=", ">=", "<=", ">", "<" })
                {
                    if (!expression.AsSpan(i).StartsWith(candidate,
                            StringComparison.Ordinal)) continue;
                    left = expression[..i];
                    op = candidate;
                    right = expression[(i + candidate.Length)..];
                    return left.Trim().Length > 0 && right.Trim().Length > 0;
                }
            }
            return false;
        }

        private bool TryParseConditionOperand(string operand, out string value)
        {
            CheckCancellation();
            value = "";
            operand = operand.Trim();
            if (operand.Length == 0) return false;
            if (operand[0] is '\'' or '"')
            {
                char quote = operand[0];
                if (operand.Length < 2 || operand[^1] != quote) return false;
                string inner = operand[1..^1];
                if (inner.Contains(quote)) return false;
                value = inner;
                return true;
            }
            if (operand.Any(ch => char.IsWhiteSpace(ch) || ch is '\'' or '"' or
                                  '(' or ')' or '=' or '<' or '>' or '!'))
                return false;
            value = operand;
            return true;
        }

        private static bool TryCompareOrdered(string left, string right, out int comparison)
        {
            comparison = 0;
            if (Version.TryParse(left, out Version? leftVersion) &&
                Version.TryParse(right, out Version? rightVersion))
            {
                comparison = leftVersion.CompareTo(rightVersion);
                return true;
            }
            if (decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture,
                    out decimal leftNumber) &&
                decimal.TryParse(right, NumberStyles.Number, CultureInfo.InvariantCulture,
                    out decimal rightNumber))
            {
                comparison = leftNumber.CompareTo(rightNumber);
                return true;
            }
            return false;
        }

    }
}
