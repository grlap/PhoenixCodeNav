using System.Collections.Immutable;
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

    internal sealed class FSharpSemanticEvaluationBudget
    {
        private int _importFiles;
        private int _importOccurrences;
        private long _importBytes;
        private int _propertyAssignments;
        private int _itemListEntries;

        public bool TryReserveImportFile() =>
            ++_importFiles <= MaxFSharpSemanticImportFiles;

        public bool TryReserveImportOccurrence() =>
            ++_importOccurrences <= MaxFSharpSemanticImportOccurrences;

        public bool TryReserveImportBytes(long bytes)
        {
            if (bytes < 0 || bytes > MaxFSharpSemanticImportBytes) return false;
            try
            {
                _importBytes = checked(_importBytes + bytes);
            }
            catch (OverflowException)
            {
                return false;
            }
            return _importBytes <= MaxFSharpSemanticImportBytes;
        }

        public bool TryReservePropertyAssignment() =>
            ++_propertyAssignments <= MaxFSharpSemanticProperties;

        public bool TryReserveItemListEntry() =>
            ++_itemListEntries <= MaxFSharpSemanticItemListEntries;
    }

    private sealed record FSharpSemanticEvaluation(
        List<string> SourceFiles,
        List<string> CommandLineArgs,
        List<string> HintPathReferences,
        List<string> BareReferences,
        List<FSharpPackageReferenceSnapshot> PackageReferences,
        List<FSharpProjectReferenceSnapshot> ProjectReferences,
        string AssemblyName,
        bool ProjectReferencesTransitive,
        IReadOnlyDictionary<string, bool> ExistsDependencies,
        string? PartialReason = null,
        string? Error = null);

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

    private readonly record struct FSharpChooseState(
        bool HasSemanticItemPhaseFacts,
        bool HasDirectSemanticFacts);

    /// <summary>
    /// A deliberately small MSBuild evaluation projection. It evaluates only the ordered property,
    /// import, condition, compile-item, and reference facts needed by Stage 2A.1. It never loads
    /// MSBuild, executes a target/task, restores a package, or treats a solution as authority.
    /// </summary>
    private sealed class FSharpSemanticProjectEvaluator :
        BoundedMsBuildProjectEvaluator<FSharpSemanticDocumentRole, FSharpChooseState>
    {
        private static readonly Regex MakeRelativeProjectToThisFile = new(
            @"^\$\(\s*\[MSBuild\]::MakeRelative\(\s*" +
            @"(?:'\$\(MSBuildProjectDirectory\)'|""\$\(MSBuildProjectDirectory\)""|\$\(MSBuildProjectDirectory\))" +
            @"\s*,\s*" +
            @"(?:'\$\(MSBuildThisFileDirectory\)'|""\$\(MSBuildThisFileDirectory\)""|\$\(MSBuildThisFileDirectory\))" +
            @"\s*\)\s*\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ItemReference = new(
            @"^@\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\)$",
            RegexOptions.CultureInvariant);

        private static readonly Regex ItemReferenceOccurrence = new(
            @"@\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\)",
            RegexOptions.CultureInvariant);

        private readonly string _projectPath;
        private readonly string _projectDir;
        private readonly string _selectedTargetFramework;
        private readonly string[] _targetFrameworks;
        private readonly Func<string, string?>? _importResolver;
        private readonly Func<string, long?>? _importSizeResolver;
        private readonly Func<string, bool?>? _existsResolver;
        private readonly string? _directoryBuildPropsPath;
        private readonly string? _directoryBuildTargetsPath;
        private readonly string? _directoryPackagesPropsPath;
        private readonly CancellationToken _cancellationToken;
        private readonly FSharpSemanticEvaluationBudget _budget;
        private readonly Dictionary<string, BoundedMsBuildProperty> _properties =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly BoundedMsBuildExpressionEvaluator _expressions;
        private readonly List<string> _sources = [];
        private readonly HashSet<string> _sourceSet =
            new(WorkspacePaths.FileSystemPathComparer);
        private readonly List<FSharpSemanticReference> _references = [];
        private readonly BoundedMsBuildPackageEvaluator _packages;
        private readonly List<FSharpProjectReferenceSnapshot> _projectReferences = [];
        private readonly HashSet<string> _projectReferenceSet =
            new(WorkspacePaths.FileSystemPathComparer);
        private ImmutableDictionary<string, ImmutableList<string>> _itemLists =
            ImmutableDictionary.Create<string, ImmutableList<string>>(StringComparer.OrdinalIgnoreCase);
        private ImmutableDictionary<string, string> _incompleteItemListErrors =
            ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase);
        // A positional snapshot contains helper state only. Properties are deliberately absent:
        // deferred package expressions must still consume the final evaluated property state.
        private readonly record struct ItemListSnapshot(
            ImmutableDictionary<string, ImmutableList<string>> Items,
            ImmutableDictionary<string, string> Errors);
        private readonly HashSet<string> _directoryReferenceProperties =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directoryReferenceItemLists =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _referenceInputConsumedProperties =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> _importSnapshots =
            new(WorkspacePaths.FileSystemPathComparer);
        private readonly Dictionary<string, XElement> _importRoots =
            new(WorkspacePaths.FileSystemPathComparer);
        private readonly SortedSet<string> _partialReasons = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _existsDependencies =
            new(WorkspacePaths.FileSystemPathComparer);

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
            CancellationToken cancellationToken,
            FSharpSemanticEvaluationBudget budget,
            Func<string, bool?>? existsResolver)
            : base(cancellationToken, MaxFSharpSemanticEvaluationDepth,
                MaxFSharpSemanticImportDepth)
        {
            _projectPath = WorkspacePaths.ToGitPath(projectPath).TrimStart('/');
            _projectDir = WorkspacePaths.ToGitPath(
                Path.GetDirectoryName(_projectPath) ?? "");
            _selectedTargetFramework = selectedTargetFramework;
            _targetFrameworks = targetFrameworks;
            _importResolver = importResolver;
            _importSizeResolver = importSizeResolver;
            _existsResolver = existsResolver;
            _directoryPackagesPropsPath = NormalizeOptionalWorkspacePath(
                directoryPackagesPropsPath);
            _directoryBuildPropsPath = NormalizeOptionalWorkspacePath(directoryBuildPropsPath);
            _directoryBuildTargetsPath = NormalizeOptionalWorkspacePath(directoryBuildTargetsPath);
            _cancellationToken = cancellationToken;
            _budget = budget;
            _properties["TargetFramework"] = new(selectedTargetFramework, true);
            // Phoenix's initial analysis context, available even to early props. These are
            // mutable defaults, not detected IDE state or immutable MSBuild global properties.
            _properties["Configuration"] = new("Debug", true);
            _properties["Platform"] = new("AnyCPU", true);
            _partialReasons.Add("fsharp_semantic_default_context_assumed");
            _expressions = new BoundedMsBuildExpressionEvaluator(
                _properties,
                (input, documentPath) => TryExpandSupportedPropertyFunction(
                    input, documentPath, out string output)
                    ? new BoundedMsBuildExpansion(true, output)
                    : new BoundedMsBuildExpansion(false, ""),
                EvaluateExists,
                cancellationToken,
                MaxFSharpSemanticPropertyValueChars,
                MaxFSharpSemanticConditionDepth);
            _packages = new(_properties,
                (string value, string document, out string expanded) =>
                    TryExpandProperties(value, document, null, out expanded, out bool complete) && complete,
                TryExpandItemSpecs, ShouldProcess, _budget.TryReserveItemListEntry, cancellationToken);
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

            if (!ValidateSdkAuthority(root, allowStandardSdk: true, out BoundedMsBuildSdkContext sdkContext))
                return Failure("fsharp_semantic_sdk_unsupported");
            sdkContext.SeedProperties(_properties);
            bool isSdkStyle = sdkContext.UsesMicrosoftNetSdk;
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
            {
                ProcessContainer(root, _projectPath,
                    FSharpSemanticDocumentRole.Project, depth: 0);
            }
            if (_error is null && _directoryBuildTargetsPath is not null)
                ProcessResolvedImport(_directoryBuildTargetsPath,
                    FSharpSemanticDocumentRole.DirectoryBuildTargets, depth: 0);
            CheckCancellation();
            if (_error is not null) return Failure(_error);

            if (!_packages.Evaluate())
                return Failure(_error ?? "fsharp_semantic_" + (_packages.Error ?? "package_reference_unresolved"));

            string assemblyName = Path.GetFileNameWithoutExtension(_projectPath);
            if (_properties.TryGetValue("AssemblyName", out BoundedMsBuildProperty assembly))
            {
                if (!assembly.Complete || string.IsNullOrWhiteSpace(assembly.Value))
                    return Failure("fsharp_semantic_assembly_name_unavailable");
                assemblyName = assembly.Value.Trim();
            }

            if (_properties.TryGetValue("EnableDefaultCompileItems",
                    out BoundedMsBuildProperty defaultItems) &&
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

            bool projectReferencesTransitive = false;
            if (isSdkStyle)
            {
                if (!TryCompilerProperty("DisableTransitiveProjectReferences",
                        out string disableTransitiveText))
                    return Failure("fsharp_semantic_property_unresolved", assemblyName);
                bool disableTransitiveProjectReferences = false;
                if (disableTransitiveText.Length > 0 &&
                    !bool.TryParse(disableTransitiveText,
                        out disableTransitiveProjectReferences))
                    return Failure("fsharp_semantic_property_unresolved", assemblyName);
                projectReferencesTransitive = !disableTransitiveProjectReferences;
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
                _packages.RestoreReferences.OrderBy(reference => reference.Id,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(reference => new FSharpPackageReferenceSnapshot(
                        reference.Id, reference.RequestedVersion, reference.IncludeCompileAssets)).ToList(),
                _projectReferences,
                assemblyName, projectReferencesTransitive, _existsDependencies,
                parsing.PartialReason);
        }

        private void CheckCancellation() =>
            _cancellationToken.ThrowIfCancellationRequested();

        protected override bool HasEvaluationError => _error is not null;

        protected override void SetEvaluationError(string cause) =>
            _error = "fsharp_semantic_" + cause;

        protected override bool ShouldProcessElement(XElement element, string documentPath,
            out bool process) => ShouldProcess(element, documentPath, out process);

        protected override bool ValidateContainer(XElement container)
        {
            if (!HasCompilerSchedulingProjectAttribute(container)) return true;
            _error = "fsharp_semantic_target_evaluation_unsupported";
            return false;
        }

        protected override void ProcessPropertyGroupElement(XElement group, string documentPath,
            FSharpSemanticDocumentRole role) => ProcessPropertyGroup(group, documentPath, role);

        protected override void ProcessItemGroupElement(XElement group, string documentPath,
            FSharpSemanticDocumentRole role) => ProcessItemGroup(group, documentPath, role);

        protected override void ProcessImportElement(XElement import, string documentPath,
            FSharpSemanticDocumentRole role, int depth) =>
            ProcessImport(import, documentPath, role, depth);

        protected override void ProcessOtherElement(XElement element, string documentPath,
            FSharpSemanticDocumentRole role, int depth)
        {
            switch (element.Name.LocalName)
            {
                case "Target":
                    if (!ContainsSemanticTargetFacts(element)) return;
                    if (!ShouldProcess(element, documentPath, out bool processTarget)) return;
                    if (processTarget)
                        _error = "fsharp_semantic_target_evaluation_unsupported";
                    return;
                case "ItemDefinitionGroup":
                    if (!element.Descendants().Any(candidate =>
                            IsSemanticItemName(candidate.Name.LocalName))) return;
                    if (!ShouldProcess(element, documentPath,
                            out bool processDefinitions)) return;
                    if (processDefinitions)
                        _error = "fsharp_semantic_item_definition_unsupported";
                    return;
            }
        }

        protected override bool TryReserveImportOccurrence() =>
            _budget.TryReserveImportOccurrence();

        protected override bool TryResolveImportRootCore(string importPath,
            out XElement? root) => TryResolveImportRoot(importPath, out root);

        protected override bool ValidateImportedRoot(XElement root)
        {
            if (ValidateSdkAuthority(root, allowStandardSdk: false, out _)) return true;
            _error = "fsharp_semantic_sdk_unsupported";
            return false;
        }

        protected override FSharpChooseState CaptureChooseState(XElement choose)
        {
            bool hasFacts = ContainsSemanticChooseItemPhaseFacts(choose,
                out bool hasDirectFacts);
            return new(hasFacts, hasDirectFacts);
        }

        protected override void OnChooseSkipped(XElement choose, FSharpChooseState state)
        {
            if (!state.HasSemanticItemPhaseFacts || !ConditionMayDependOnProperties(choose)) return;
            _semanticItemPhaseStarted = true;
            _directSemanticItemPhaseStarted |= state.HasDirectSemanticFacts;
        }

        protected override void OnChooseCompleted(XElement choose, FSharpChooseState state)
        {
            if (!state.HasSemanticItemPhaseFacts) return;
            _semanticItemPhaseStarted = true;
            _directSemanticItemPhaseStarted |= state.HasDirectSemanticFacts;
        }

        private static bool IsDirectoryBuildRole(FSharpSemanticDocumentRole role) =>
            role is FSharpSemanticDocumentRole.DirectoryBuildProps or
                FSharpSemanticDocumentRole.DirectoryBuildTargets;

        private static bool IsDirectoryPropsAuthorityRole(
            FSharpSemanticDocumentRole role) =>
            role is FSharpSemanticDocumentRole.DirectoryPackagesProps or
                FSharpSemanticDocumentRole.DirectoryBuildProps or
                FSharpSemanticDocumentRole.DirectoryBuildTargets;

        private bool ValidateSdkAuthority(XElement root, bool allowStandardSdk,
            out BoundedMsBuildSdkContext sdkContext)
        {
            if (!BoundedMsBuildSdkContext.TryRead(root, _cancellationToken, out sdkContext)) return false;
            if (sdkContext.UsesMicrosoftNetSdk)
            {
                if (!allowStandardSdk) return false;
                _partialReasons.Add("fsharp_semantic_sdk_implicit_authority");
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
                _packages.RestoreReferences.OrderBy(reference => reference.Id,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(reference => new FSharpPackageReferenceSnapshot(
                        reference.Id, reference.RequestedVersion, reference.IncludeCompileAssets)).ToList(),
                _projectReferences,
                assemblyName ?? Path.GetFileNameWithoutExtension(_projectPath),
                false, _existsDependencies,
                _partialReasons.Count == 0 ? null : string.Join(';', _partialReasons), error);

        private void ProcessContainer(XElement container, string documentPath,
            FSharpSemanticDocumentRole role, int depth) =>
            ProcessEvaluationContainer(container, documentPath, role, depth);

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
                if (property.HasElements || !_budget.TryReservePropertyAssignment())
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
                if (!TryExpandProperties(raw, documentPath, name,
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
                IsSemanticItemName(item.Name.LocalName) &&
                !BoundedMsBuildPackageEvaluator.IsPackageItem(item.Name.LocalName));
            bool groupProcess = true;
            if (hasSemanticItems)
            {
                if (!ShouldProcess(group, documentPath, out groupProcess)) return;
                if (!groupProcess &&
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
                if (BoundedMsBuildPackageEvaluator.IsPackageItem(itemName))
                {
                    // Capture at the item's position, including within this ItemGroup. Persistent
                    // collections share unchanged state instead of copying every helper per item.
                    var snapshot = new ItemListSnapshot(_itemLists, _incompleteItemListErrors);
                    _packages.Add(group, item, documentPath,
                        (string raw, string document, out List<string> specs) =>
                            TryExpandItemSpecs(raw, document, snapshot, out specs));
                    continue;
                }
                if (!IsSemanticItemName(itemName))
                {
                    ProcessReferenceInputItem(group, item, documentPath);
                    continue;
                }
                if (!groupProcess) continue;
                if (!ShouldProcess(item, documentPath, out bool process)) return;
                if (!process)
                {
                    if (ConditionMayDependOnProperties(item))
                    {
                        _semanticItemPhaseStarted = true;
                        _directSemanticItemPhaseStarted = true;
                    }
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
                    ProcessCompile(item, documentPath);
                }
                else if (itemName.Equals("Reference", StringComparison.OrdinalIgnoreCase))
                {
                    ProcessReference(item, documentPath);
                }
                else if (itemName.Equals("ProjectReference",
                             StringComparison.OrdinalIgnoreCase))
                {
                    ProcessProjectReference(item, documentPath);
                }
                else
                {
                    _error = "fsharp_semantic_reference_unresolved";
                }
                if (_error is not null) return;
            }
        }

        private void ProcessProjectReference(XElement item, string documentPath)
        {
            string? rawInclude = item.Attribute("Include")?.Value.Trim();
            if (rawInclude is null || item.Attribute("Update") is not null ||
                item.Attribute("Remove") is not null || item.Attribute("Exclude") is not null ||
                item.Attributes().Any(attribute =>
                    !attribute.Name.LocalName.Equals("Include", StringComparison.OrdinalIgnoreCase) &&
                    !attribute.Name.LocalName.Equals("Condition", StringComparison.OrdinalIgnoreCase) &&
                    !attribute.Name.LocalName.Equals("Label", StringComparison.OrdinalIgnoreCase) &&
                    !attribute.Name.LocalName.Equals("ReferenceOutputAssembly",
                        StringComparison.OrdinalIgnoreCase)))
            {
                _error = "fsharp_semantic_project_reference_metadata_unsupported";
                return;
            }

            bool referenceOutputAssembly = true;
            var activeReferenceOutputAssembly = new List<string>();
            XAttribute? outputAttribute = item.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("ReferenceOutputAssembly",
                    StringComparison.OrdinalIgnoreCase));
            if (outputAttribute is not null)
                activeReferenceOutputAssembly.Add(outputAttribute.Value);

            foreach (XElement metadata in item.Elements())
            {
                CheckCancellation();
                if (metadata.HasElements || metadata.Attributes().Any(attribute =>
                        !attribute.Name.LocalName.Equals("Condition",
                            StringComparison.OrdinalIgnoreCase) &&
                        !attribute.Name.LocalName.Equals("Label",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    _error = "fsharp_semantic_project_reference_metadata_unsupported";
                    return;
                }
                if (!ShouldProcess(metadata, documentPath, out bool process)) return;
                if (!process) continue;
                string name = metadata.Name.LocalName;
                if (name.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Project", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!name.Equals("ReferenceOutputAssembly",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _error = "fsharp_semantic_project_reference_metadata_unsupported";
                    return;
                }
                activeReferenceOutputAssembly.Add(metadata.Value);
            }

            if (activeReferenceOutputAssembly.Count > 1)
            {
                _error = "fsharp_semantic_project_reference_metadata_unsupported";
                return;
            }
            if (activeReferenceOutputAssembly.Count == 1 &&
                (!TryExpandProperties(activeReferenceOutputAssembly[0].Trim(), documentPath, null,
                     out string expanded, out bool complete) || !complete ||
                 !bool.TryParse(expanded, out referenceOutputAssembly)))
            {
                _error = "fsharp_semantic_project_reference_metadata_unsupported";
                return;
            }
            if (!referenceOutputAssembly) return;

            if (!TryExpandItemSpecs(rawInclude, documentPath, out List<string> specs))
            {
                if (_error == "fsharp_semantic_reference_unresolved")
                    _error = "fsharp_semantic_project_reference_metadata_unsupported";
                return;
            }
            if (specs.Count == 0)
            {
                _error = "fsharp_semantic_project_reference_unavailable";
                return;
            }
            foreach (string spec in specs)
            {
                CheckCancellation();
                if (!_budget.TryReserveItemListEntry())
                {
                    _error = "fsharp_semantic_item_list_limit";
                    return;
                }
                if (!TryNormalizeSemanticRelative(_projectDir, spec,
                        out string projectPath))
                {
                    _error = "fsharp_semantic_path_outside_workspace";
                    return;
                }
                if (!projectPath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) &&
                    !projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    _error = "fsharp_semantic_project_references_unsupported";
                    return;
                }
                if (_projectReferenceSet.Add(projectPath))
                    _projectReferences.Add(new(projectPath));
            }
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
                    EnsureItemList(name);
                    return;
                }
                if (!ShouldProcess(item, documentPath, out bool processItem)) return;
                if (!processItem)
                {
                    EnsureItemList(name);
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

                EnsureItemList(name);

                if (remove?.Value is { } rawRemove)
                {
                    if (!TryExpandItemSpecs(rawRemove, documentPath,
                            out List<string> removeSpecs)) return;
                    if (_itemLists.TryGetValue(name, out ImmutableList<string>? current))
                    {
                        _itemLists = _itemLists.SetItem(name, current.RemoveAll(existing =>
                            removeSpecs.Contains(existing, StringComparer.OrdinalIgnoreCase)));
                    }
                }
                if (include?.Value is not { } rawInclude) return;
                if (!TryExpandItemSpecs(rawInclude, documentPath,
                        out List<string> includeSpecs)) return;
                var list = _itemLists[name].ToBuilder();
                foreach (string spec in includeSpecs)
                {
                    if (!_budget.TryReserveItemListEntry())
                    {
                        _error = "fsharp_semantic_item_list_limit";
                        return;
                    }
                    list.Add(spec);
                }
                _itemLists = _itemLists.SetItem(name, list.ToImmutable());
            }
            finally
            {
                if (_error is not null)
                    _incompleteItemListErrors = _incompleteItemListErrors.SetItem(name, _error);
                _error = priorError;
            }
        }

        private void EnsureItemList(string name)
        {
            if (!_itemLists.ContainsKey(name)) _itemLists = _itemLists.Add(name, []);
        }

        private static bool ConditionMayDependOnProperties(XElement element) =>
            element.Attribute("Condition")?.Value.Contains("$(", StringComparison.Ordinal) == true;

        private void RegisterReferenceInputPropertyDependencies(XElement item, XElement boundary)
        {
            void Add(string? expression)
            {
                if (string.IsNullOrEmpty(expression)) return;
                foreach (string name in
                         BoundedMsBuildExpressionEvaluator.ReferencedPropertyNames(expression))
                    _referenceInputConsumedProperties.Add(name);
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

        private void ProcessCompile(XElement item, string documentPath)
        {
            string? raw = item.Attribute("Include")?.Value.Trim();
            if (raw is null || item.Attribute("Remove") is not null ||
                item.Attribute("Update") is not null || item.Attribute("Exclude") is not null)
            {
                _error = "fsharp_semantic_compile_order_unavailable";
                return;
            }
            if (!TryExpandProperties(raw, documentPath, null,
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
                if (item.HasElements || !TryExpandItemSpecs(rawRemove, documentPath,
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

            if (!TryExpandItemSpecs(rawInclude!, documentPath, out List<string> includes))
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
            if (!TryExpandProperties(rawHint, documentPath, null,
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

        private bool TryExpandItemSpecs(string raw, string documentPath,
            out List<string> specs) => TryExpandItemSpecs(raw, documentPath,
            new ItemListSnapshot(_itemLists, _incompleteItemListErrors), out specs);

        private bool TryExpandItemSpecs(string raw, string documentPath,
            ItemListSnapshot snapshot, out List<string> specs)
        {
            specs = [];
            if (!TryExpandProperties(raw.Trim(), documentPath, null,
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
                if (snapshot.Errors.TryGetValue(name, out string? itemListError))
                {
                    _error = itemListError;
                    return false;
                }
                if (!snapshot.Items.TryGetValue(name, out ImmutableList<string>? itemSpecs))
                {
                    _error = "fsharp_semantic_reference_unresolved";
                    return false;
                }
                specs.AddRange(itemSpecs);
            }
            return true;
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
                        out BoundedMsBuildProperty configuredTargets))
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

            if (!TryExpandProperties(rawProject, documentPath, null,
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
            FSharpSemanticDocumentRole role, int depth) =>
            ProcessResolvedEvaluationImport(importPath, role, depth);

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
                foreach (string name in
                         BoundedMsBuildExpressionEvaluator.ReferencedPropertyNames(expression))
                {
                    if (!AddProperty(name)) return;
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
            bool evaluated = _expressions.TryEvaluateCondition(condition, documentPath,
                out result, out string? error, unsetSelfProperty, depth);
            if (!evaluated && error is not null)
                _error ??= FSharpExpressionError(error);
            return evaluated;
        }

        private BoundedMsBuildExistsResult EvaluateExists(string documentPath, string rawPath)
        {
            // MSBuild evaluates relative Exists operands from the owning project directory,
            // including conditions in imported documents (unlike Import Project paths).
            if (!TryNormalizeSemanticRelative(_projectDir, rawPath, out string path))
                return new(false, false);
            // Preserve the existing import authority and its existing budgets for .props probes.
            // Generic indexed-file probes below add no limit: the condition/expression budgets
            // already bound how many distinct paths one evaluation can ask about.
            if (path.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveImport(path, out string? content))
                    return new(false, false, _error);
                bool exists = content is not null;
                _existsDependencies[path] = exists;
                return new(true, exists);
            }
            if (_existsDependencies.TryGetValue(path, out bool recorded))
                return new(true, recorded);
            if (_existsResolver is not null)
            {
                bool? indexed = _existsResolver(path);
                if (!indexed.HasValue) return new(false, false);
                _existsDependencies[path] = indexed.Value;
                return new(true, indexed.Value);
            }
            return new(false, false);
        }

        private static string FSharpExpressionError(string error) =>
            error.StartsWith("fsharp_", StringComparison.Ordinal)
                ? error
                : "fsharp_semantic_" + error;

        private bool TryExpandProperties(string input, string documentPath, string? selfProperty,
            out string output, out bool complete, bool allowItemReferences = false)
        {
            bool expanded = _expressions.TryExpandProperties(input, documentPath, selfProperty,
                out output, out complete, out string? error, allowItemReferences);
            if (!expanded && error is not null)
                _error ??= FSharpExpressionError(error);
            return expanded;
        }

        private bool TryExpandSupportedPropertyFunction(string input, string documentPath,
            out string output)
        {
            output = "";
            if (!MakeRelativeProjectToThisFile.IsMatch(input)) return false;

            // Both operands are reserved directory properties whose resolved targets are already
            // workspace-contained. Parent segments in the returned value are expected: consumers
            // resolve them from the project directory back to the current imported file's directory.
            string thisFileDirectory = WorkspacePaths.ToGitPath(
                Path.GetDirectoryName(documentPath) ?? "").Trim('/');
            output = MakeRelativeDirectory(_projectDir, thisFileDirectory);
            return true;
        }

        private static string MakeRelativeDirectory(string baseDirectory,
            string targetDirectory)
        {
            string[] baseParts = baseDirectory.Split('/',
                StringSplitOptions.RemoveEmptyEntries);
            string[] targetParts = targetDirectory.Split('/',
                StringSplitOptions.RemoveEmptyEntries);
            int common = 0;
            // These are canonical workspace-relative identities captured from the same index.
            StringComparer pathComparer = WorkspacePaths.FileSystemPathComparer;
            while (common < baseParts.Length && common < targetParts.Length &&
                   pathComparer.Equals(baseParts[common], targetParts[common]))
                common++;

            if (common == baseParts.Length && common == targetParts.Length) return ".";
            var relative = new List<string>(baseParts.Length - common +
                                            targetParts.Length - common);
            for (int index = common; index < baseParts.Length; index++) relative.Add("..");
            for (int index = common; index < targetParts.Length; index++)
                relative.Add(targetParts[index]);
            // MSBuildThisFileDirectory is directory-valued and carries a trailing separator;
            // MakeRelative preserves that separator and emits the host-native spelling.
            char separator = Path.DirectorySeparatorChar;
            return string.Join(separator, relative) + separator;
        }

        private bool IsCanonicalUnsetSelfCondition(XElement element,
            string condition)
        {
            return element.Parent?.Name.LocalName == "PropertyGroup" &&
                   _expressions.IsSelfDefaultCondition(condition, element.Name.LocalName);
        }

        private bool TryCompilerProperty(string name, out string value)
        {
            value = "";
            if (!_properties.TryGetValue(name, out BoundedMsBuildProperty property)) return true;
            if (!property.Complete) return false;
            value = property.Value.Trim();
            return true;
        }

        private bool TryResolveImport(string path, out string? content)
        {
            CheckCancellation();
            if (_importSnapshots.TryGetValue(path, out content)) return true;
            if (!_budget.TryReserveImportFile())
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
                int actualBytes = Encoding.UTF8.GetByteCount(content);
                long bytes = Math.Max(actualBytes, indexedBytes ?? 0L);
                if (!_budget.TryReserveImportBytes(bytes))
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
             name.Equals("DisableTransitiveProjectReferences",
                 StringComparison.OrdinalIgnoreCase) ||
             name.Equals("EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("ManagePackageVersionsCentrally",
                 StringComparison.OrdinalIgnoreCase) ||
             name.Equals("RestoreEnableGlobalPackageReference", StringComparison.OrdinalIgnoreCase) ||
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

    }
}
