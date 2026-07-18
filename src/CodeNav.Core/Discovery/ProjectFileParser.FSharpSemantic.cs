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

    private sealed record FSharpSemanticEvaluation(
        List<string> SourceFiles,
        List<string> CommandLineArgs,
        List<string> HintPathReferences,
        List<string> BareReferences,
        string AssemblyName,
        string? PartialReason = null,
        string? Error = null);

    private readonly record struct FSharpSemanticProperty(string Value, bool Complete);

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

        private static readonly Regex ExistsCondition = new(
            @"^Exists\s*\(\s*(?:'(?<path>[^'\r\n]*)'|""(?<path>[^""\r\n]*)"")\s*\)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly string _projectPath;
        private readonly string _projectDir;
        private readonly string _selectedTargetFramework;
        private readonly string[] _targetFrameworks;
        private readonly Func<string, string?>? _importResolver;
        private readonly Func<string, long?>? _importSizeResolver;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<string, FSharpSemanticProperty> _properties =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _sources = [];
        private readonly HashSet<string> _sourceSet =
            new(WorkspacePaths.FileSystemPathComparer);
        private readonly List<string> _hintPaths = [];
        private readonly List<string> _bareReferences = [];
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
        private int _evaluationDepth;
        private bool _semanticItemPhaseStarted;
        private string? _error;

        public FSharpSemanticProjectEvaluator(
            string projectPath,
            string selectedTargetFramework,
            string[] targetFrameworks,
            Func<string, string?>? importResolver,
            Func<string, long?>? importSizeResolver,
            CancellationToken cancellationToken)
        {
            _projectPath = WorkspacePaths.ToGitPath(projectPath).TrimStart('/');
            _projectDir = WorkspacePaths.ToGitPath(
                Path.GetDirectoryName(_projectPath) ?? "");
            _selectedTargetFramework = selectedTargetFramework;
            _targetFrameworks = targetFrameworks;
            _importResolver = importResolver;
            _importSizeResolver = importSizeResolver;
            _cancellationToken = cancellationToken;
            _properties["TargetFramework"] = new(selectedTargetFramework, true);
        }

        public FSharpSemanticEvaluation Evaluate(XElement root)
        {
            CheckCancellation();
            if (root.Name.LocalName != "Project")
                return Failure("fsharp_project_options_unavailable");

            if (!ValidateSdkAuthority(root, allowStandardSdk: true))
                return Failure("fsharp_semantic_sdk_unsupported");

            ProcessContainer(root, _projectPath, imported: false, depth: 0);
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
                _hintPaths.Distinct(WorkspacePaths.FileSystemPathComparer).ToList(),
                _bareReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                assemblyName, parsing.PartialReason);
        }

        private void CheckCancellation() =>
            _cancellationToken.ThrowIfCancellationRequested();

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
                _hintPaths.Distinct(WorkspacePaths.FileSystemPathComparer).ToList(),
                _bareReferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                assemblyName ?? Path.GetFileNameWithoutExtension(_projectPath),
                _partialReasons.Count == 0 ? null : string.Join(';', _partialReasons), error);

        private void ProcessContainer(XElement container, string documentPath,
            bool imported, int depth)
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

                foreach (XElement child in container.Elements())
                {
                    CheckCancellation();
                    if (_error is not null) return;
                    switch (child.Name.LocalName)
                    {
                        case "PropertyGroup":
                            ProcessPropertyGroup(child, documentPath);
                            break;
                        case "ItemGroup":
                            ProcessItemGroup(child, documentPath, imported);
                            break;
                        case "Import":
                            ProcessImport(child, documentPath, depth);
                            break;
                        case "ImportGroup":
                            ProcessImportGroup(child, documentPath, depth);
                            break;
                        case "Choose":
                            ProcessChoose(child, documentPath, imported, depth);
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

        private void ProcessPropertyGroup(XElement group, string documentPath)
        {
            CheckCancellation();
            if (!ShouldProcess(group, documentPath, out bool process) || !process) return;
            if (_semanticItemPhaseStarted && group.Elements().Any())
            {
                _error = "fsharp_semantic_evaluation_order_unsupported";
                return;
            }

            foreach (XElement property in group.Elements())
            {
                CheckCancellation();
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

        private void ProcessItemGroup(XElement group, string documentPath, bool imported)
        {
            CheckCancellation();
            bool hasSemanticItems = false;
            foreach (XElement item in group.Elements())
            {
                CheckCancellation();
                if (!IsSemanticItemName(item.Name.LocalName)) continue;
                hasSemanticItems = true;
                break;
            }
            if (!hasSemanticItems) return;
            if (!ShouldProcess(group, documentPath, out bool process)) return;
            if (!process)
            {
                if (ConditionMayDependOnProperties(group)) _semanticItemPhaseStarted = true;
                return;
            }

            foreach (XElement item in group.Elements())
            {
                CheckCancellation();
                if (item.Name.LocalName is not ("Compile" or "Reference" or
                    "ProjectReference" or "PackageReference")) continue;
                if (!ShouldProcess(item, documentPath, out process)) return;
                if (!process)
                {
                    if (ConditionMayDependOnProperties(item)) _semanticItemPhaseStarted = true;
                    continue;
                }
                _semanticItemPhaseStarted = true;
                if (imported)
                {
                    _error = item.Name.LocalName switch
                    {
                        "ProjectReference" =>
                            "fsharp_semantic_project_references_unsupported",
                        "PackageReference" =>
                            "fsharp_semantic_package_references_unsupported",
                        _ => "fsharp_semantic_import_items_unsupported",
                    };
                    return;
                }

                switch (item.Name.LocalName)
                {
                    case "Compile":
                        ProcessCompile(item);
                        break;
                    case "Reference":
                        ProcessReference(item, documentPath);
                        break;
                    case "ProjectReference":
                        _error = "fsharp_semantic_project_references_unsupported";
                        break;
                    case "PackageReference":
                        _error = "fsharp_semantic_package_references_unsupported";
                        break;
                }
                if (_error is not null) return;
            }
        }

        private static bool ConditionMayDependOnProperties(XElement element) =>
            element.Attribute("Condition")?.Value.Contains("$(", StringComparison.Ordinal) == true;

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
            string rawInclude = item.Attribute("Include")?.Value.Trim() ?? "";
            if (!TryExpandProperties(rawInclude, null,
                    out string include, out bool includeComplete)) return;
            string simpleName = include.Split(',')[0].Trim();
            if (!includeComplete || simpleName.Length == 0)
            {
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
                _bareReferences.Add(simpleName);
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
            _hintPaths.Add(hintPath);
        }

        private void ProcessImportGroup(XElement group, string documentPath, int depth)
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
                ProcessImport(child, documentPath, depth);
                if (_error is not null) return;
            }
        }

        private void ProcessImport(XElement import, string documentPath, int depth)
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
            if (!ShouldProcess(import, documentPath, out bool process) || !process) return;

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
            if (!importPath.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
            {
                _error = "fsharp_semantic_import_unsupported";
                return;
            }
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
                if (!TryResolveImport(importPath, out string? content)) return;
                CheckCancellation();
                if (content is null)
                {
                    _error = "fsharp_semantic_import_unavailable";
                    return;
                }
                if (!_importRoots.TryGetValue(importPath, out XElement? importedRoot) &&
                    (!TryLoadProjectXml(content, MaxFSharpSemanticImportBytes,
                         _cancellationToken,
                         out importedRoot) || importedRoot is null))
                {
                    _error = "fsharp_semantic_import_unavailable";
                    return;
                }
                _importRoots[importPath] = importedRoot;
                CheckCancellation();
                // Imported .props are data-only in this projection. Any SDK declaration would
                // introduce resolver-controlled implicit props/targets at a nested authority
                // boundary, even when it names the otherwise allowlisted root SDK.
                if (!ValidateSdkAuthority(importedRoot, allowStandardSdk: false))
                {
                    _error = "fsharp_semantic_sdk_unsupported";
                    return;
                }
                ProcessContainer(importedRoot, importPath, imported: true, depth + 1);
            }
            finally
            {
                _activeImports.Remove(importPath);
            }
        }

        private void ProcessChoose(XElement choose, string documentPath,
            bool imported, int depth)
        {
            CheckCancellation();
            bool hasSemanticItemPhaseFacts = ContainsSemanticChooseItemPhaseFacts(choose);
            if (!ShouldProcess(choose, documentPath, out bool process)) return;
            if (!process)
            {
                if (hasSemanticItemPhaseFacts && ConditionMayDependOnProperties(choose))
                    _semanticItemPhaseStarted = true;
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
                ProcessContainer(branch, documentPath, imported, depth);
                if (_error is null && hasSemanticItemPhaseFacts)
                    _semanticItemPhaseStarted = true;
                return;
            }
            if (otherwise is not null)
                ProcessContainer(otherwise, documentPath, imported, depth);
            if (_error is null && hasSemanticItemPhaseFacts)
                _semanticItemPhaseStarted = true;
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
            out string output, out bool complete)
        {
            CheckCancellation();
            output = "";
            complete = false;
            if (input.Length > MaxFSharpSemanticPropertyValueChars ||
                ContainsUnsupportedExpansion(input))
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
            complete = allComplete && !ContainsMsBuildExpression(output);
            return true;
        }

        private bool ContainsUnsupportedExpansion(string input)
        {
            CheckCancellation();
            if (input.Contains("@(", StringComparison.Ordinal) ||
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

        private bool ContainsSemanticChooseItemPhaseFacts(XElement choose)
        {
            foreach (XElement element in choose.Descendants())
            {
                CheckCancellation();
                if (IsSemanticItemName(element.Name.LocalName) ||
                    element.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase) &&
                    ContainsSemanticTargetFacts(element))
                    return true;
            }
            return false;
        }

        private bool ContainsSemanticTargetFacts(XElement target)
        {
            foreach (XElement element in target.Descendants())
            {
                CheckCancellation();
                if (element.Name.LocalName.Equals("Fsc", StringComparison.OrdinalIgnoreCase) ||
                    IsSemanticItemName(element.Name.LocalName) ||
                    IsSemanticPropertyName(element.Name.LocalName) ||
                    element.Name.LocalName.Equals("Output", StringComparison.OrdinalIgnoreCase) &&
                    (IsSemanticPropertyName(element.Attribute("PropertyName")?.Value) ||
                     IsSemanticItemName(element.Attribute("ItemName")?.Value)))
                    return true;
            }
            return false;
        }

        private static bool IsSemanticPropertyName(string? name) => name is not null &&
            (name.Equals("DefineConstants", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("LangVersion", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("OtherFlags", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("FscAdditionalArgs", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("AssemblyName", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("DisableImplicitFrameworkDefines", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase));

        private static bool IsSemanticItemName(string? name) => name is not null &&
            (name.Equals("Compile", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("Reference", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("PackageReference", StringComparison.OrdinalIgnoreCase));

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
