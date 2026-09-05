using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;

namespace CodeNav.Mcp;

public sealed partial class NavigationTools
{
    internal const int MaxFSharpOutlineParseContexts = 64;
    internal const int MaxFSharpTypeCheckContexts = 64;

    private string FSharpOutline(string path, string normalizedPath, FileHit file, int depth)
    {
        FSharpOutlineResult result = _semantic.FSharpOutline(normalizedPath);
        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        if (result.Error is { } error)
        {
            string detail = error switch
            {
                "fsharp_project_not_found" =>
                    "F# outline requires the file to be a compile item of an indexed .fsproj.",
                "fsharp_project_options_unavailable" =>
                    "The indexed owning .fsproj is unavailable or cannot provide parser options.",
                "fsharp_project_options_conflict" =>
                    "Multiple owning .fsproj files provide different F# parser options.",
                "file_too_large" =>
                    "The indexed F# source exceeds the structural parse limit.",
                "file_content_unavailable" =>
                    "The indexed F# source content is unavailable for parsing.",
                "fsharp_parse_failed" =>
                    "FCS reported syntax errors; no partial outline was returned.",
                _ => "FCS could not produce an outline for this file.",
            };
            return Json.Serialize(new
            {
                error,
                operation = "outline",
                path,
                detail,
                fileBytes = result.FileBytes,
                maxBytes = result.MaxBytes,
                meta,
            });
        }

        object Node(FSharpOutlineItem item, bool includeMembers)
        {
            List<object>? members = null;
            if (includeMembers && item.Members.Count > 0)
                members = item.Members.Select(member => Node(member, includeMembers: true)).ToList();

            return new
            {
                item.Name,
                item.Kind,
                item.Signature,
                item.Accessibility,
                modifiers = item.Modifiers,
                accessors = item.Accessors,
                item.StartLine,
                item.EndLine,
                isPartial = (bool?)null,
                partialFiles = (object?)null,
                partialFilesTruncated = (bool?)null,
                attributes = (object?)null,
                members,
            };
        }

        object? selectedParseContext = result.SelectedProject is null
            ? null
            : new
            {
                project = result.SelectedProject,
                targetFramework = result.SelectedTargetFramework,
            };
        var allAvailableParseContexts = result.AvailableParseContexts?
            .Select(context => (object)new
            {
                project = context.Project,
                targetFramework = context.TargetFramework,
            })
            .ToList() ?? [];
        int availableParseContextsTotal = allAvailableParseContexts.Count;
        var availableParseContexts = allAvailableParseContexts
            .Take(MaxFSharpOutlineParseContexts)
            .ToList();
        bool availableParseContextsLimitTruncated =
            availableParseContextsTotal > availableParseContexts.Count;

        string BuildNested(bool includeMembers, bool truncated) => Json.Serialize(new
        {
            path,
            isGenerated = file.IsGenerated,
            symbols = result.Symbols.Select(symbol => Node(symbol, includeMembers)).ToList(),
            truncated,
            partial = result.PartialReason is not null ? true : (bool?)null,
            partialReason = result.PartialReason,
            selectedParseContext,
            availableParseContexts,
            availableParseContextsTotal,
            availableParseContextsReturned = availableParseContexts.Count,
            availableParseContextsTruncated = availableParseContextsLimitTruncated,
            meta,
        });

        string nested = BuildNested(includeMembers: depth >= 2, truncated: false);
        if (Json.Utf8Bytes(nested) <= Json.HardBudgetBytes) return nested;

        if (depth >= 2)
        {
            string rootsOnly = BuildNested(includeMembers: false, truncated: true);
            if (Json.Utf8Bytes(rootsOnly) <= Json.HardBudgetBytes) return rootsOnly;
        }

        var flat = result.Symbols
            .Select(symbol => (object)new
            {
                symbol.Name,
                symbol.Kind,
                symbol.StartLine,
                symbol.EndLine,
            })
            .ToList();
        return Json.WithAuxiliaryListBudget(flat, availableParseContexts,
            (items, _, parseContexts, parseContextsByteTruncated) => new
            {
                path,
                isGenerated = file.IsGenerated,
                symbols = items,
                truncated = true,
                partial = result.PartialReason is not null ? true : (bool?)null,
                partialReason = result.PartialReason,
                selectedParseContext,
                availableParseContexts = parseContexts,
                availableParseContextsTotal,
                availableParseContextsReturned = parseContexts.Count,
                availableParseContextsTruncated =
                availableParseContextsLimitTruncated || parseContextsByteTruncated,
                note = "File has too many declarations for a full outline; showing bounded top-level declarations.",
                meta,
            });
    }

    private string FSharpSymbolAt(string path, int line, int column,
        string? projectPath, string? targetFramework, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        FSharpSemanticResult result = _semantic.FSharpSymbolAtAsync(path, line, column,
                projectPath, targetFramework, timeoutMs)
            .GetAwaiter().GetResult();
        return ShapeFSharpSemanticResult("symbol_at", path, line, column, result,
            Math.Clamp(timeoutMs, 500, 60_000), stopwatch.ElapsedMilliseconds);
    }

    private string FSharpDefinition(string path, int line, int column,
        string? projectPath, string? targetFramework, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        FSharpSemanticResult result = _semantic.FSharpSymbolAtAsync(path, line, column,
                projectPath, targetFramework, timeoutMs)
            .GetAwaiter().GetResult();
        if (result.Symbol is { Declarations.Count: 0 } && result.Error is null)
            result = result with { Error = "fsharp_definition_not_in_selected_project" };
        return ShapeFSharpSemanticResult("definition", path, line, column, result,
            Math.Clamp(timeoutMs, 500, 60_000), stopwatch.ElapsedMilliseconds);
    }

    internal static string FSharpSemanticConfidence(string? partialReason)
    {
        if (string.IsNullOrEmpty(partialReason)) return "exact";

        foreach (string reason in partialReason.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (reason)
            {
                // These disclose input provenance or result scope; they neither remove
                // selected-context authority nor substitute a target-incompatible input.
                case "fsharp_semantic_sdk_implicit_authority":
                case "fsharp_semantic_toolchain_implicit_authority":
                case "fsharp_core_reference_defaulted":
                case "fsharp_binary_references_snapshotted":
                case "fsharp_package_references_snapshotted":
                case "fsharp_references_workspace_dependents_not_scanned":
                    continue;
                // Closed by design: known authority loss and every future unclassified cause
                // remain conservative until deliberately admitted above.
                default:
                    return "indexed";
            }
        }

        return "exact";
    }

    private string FSharpReferences(string path, int line, int column,
        string? projectPath, string? targetFramework, bool includeTests,
        bool includeGenerated, int samplesPerGroup, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int deadlineMs = Math.Clamp(timeoutMs, 500, SemanticNavigationDeadlineMaxMs);
        FSharpReferencesResult result = _semantic.FSharpReferencesAsync(
                path, line, column, projectPath, targetFramework, includeTests,
                includeGenerated, Math.Clamp(samplesPerGroup, 0, 10), deadlineMs)
            .GetAwaiter().GetResult();
        return ShapeFSharpReferencesResult(path, line, column, result, deadlineMs,
            stopwatch.ElapsedMilliseconds);
    }

    private string ShapeFSharpReferencesResult(string path, int line, int column,
        FSharpReferencesResult result, int deadlineMs, long elapsedMs)
    {
        var selected = result.SelectedContext is null
            ? null
            : new
            {
                project = result.SelectedContext.Project,
                targetFramework = result.SelectedContext.TargetFramework,
            };
        var allContexts = result.AvailableContexts.Select(context => (object)new
        {
            project = context.Project,
            targetFramework = context.TargetFramework,
        }).ToList();
        int contextTotal = allContexts.Count;
        var diagnostics = result.Diagnostics ?? [];
        bool diagnosticLimitTruncated = result.DiagnosticCount > diagnostics.Count;
        bool succeeded = result.Error is null && result.Symbol is not null &&
                         result.TotalReferences is not null;
        string? detail = result.Error switch
        {
            "fsharp_semantic_position_invalid" =>
                "F# semantic positions require line >= 1 and column >= 0 (0 means line-only).",
            "fsharp_type_check_context_required" =>
                "Select one physical F# project and target framework using projectPath + targetFramework.",
            "fsharp_type_check_context_not_found" =>
                "The requested projectPath + targetFramework is not an owning type-check context for this file.",
            "fsharp_semantic_column_required" =>
                "More than one F# symbol occurs on this line; provide a 1-based column.",
            "fsharp_semantic_line_only_source_limit" =>
                "Line-only F# lookup is disabled for this source size; provide a 1-based column.",
            "fsharp_semantic_project_references_unsupported" =>
                "This F# closure reaches a non-F# ProjectReference. FCS cannot type-check that source project, and Phoenix never substitutes a potentially stale last-built project DLL.",
            "fsharp_semantic_project_reference_metadata_unsupported" =>
                "An active ProjectReference changes compiler closure through metadata or item operations outside the bounded F# evaluator.",
            "fsharp_semantic_project_reference_unavailable" =>
                "An active ProjectReference is missing, unindexed, unreadable, or not an evaluable physical project snapshot.",
            "fsharp_semantic_project_reference_target_framework_unavailable" =>
                FSharpProjectReferenceTargetFrameworkDetail(result.ProjectReferenceFailure),
            "fsharp_framework_references_unavailable" =>
                FSharpFrameworkReferencesUnavailableDetail(result.ProjectReferenceFailure),
            "fsharp_core_reference_unavailable" =>
                FSharpCoreReferenceUnavailableDetail(result.ProjectReferenceFailure),
            "fsharp_semantic_project_reference_cycle" =>
                "The active F# ProjectReference closure contains a cycle; Phoenix stopped instead of constructing recursive FCS options.",
            "fsharp_project_options_conflict" =>
                "Two distinct physical project/TFM contexts in the F# closure produce the same assembly identity.",
            "fsharp_semantic_reference_changed" =>
                "A captured binary or package reference changed during FCS checking; the result was discarded.",
            "fsharp_semantic_timeout" =>
                "FCS did not complete within the bounded deadline; retry with a larger timeoutMs.",
            "fsharp_symbol_not_resolved" =>
                "FCS found no symbol at this exact source position.",
            null => null,
            _ => "FCS could not produce trustworthy same-project reference evidence for this project snapshot.",
        };
        var meta = FSharpSemanticMeta(result.Health, result.Error, result.PartialReason);
        object? symbol = result.Symbol is null ? null : new
        {
            name = result.Symbol.Name,
            fullName = result.Symbol.FullName,
            kind = result.Symbol.Kind,
            container = result.Symbol.Container,
            @namespace = result.Symbol.Namespace,
            assembly = result.Symbol.Assembly,
            accessibility = result.Symbol.Accessibility,
            use = new
            {
                path = result.Symbol.Use.Path,
                startLine = result.Symbol.Use.StartLine,
                startColumn = result.Symbol.Use.StartColumn,
                endLine = result.Symbol.Use.EndLine,
                endColumn = result.Symbol.Use.EndColumn,
            },
        };

        string shaped = Json.WithAuxiliaryListsBudget(result.Samples, allContexts, diagnostics,
            (shownSamples, samplesTruncated, shownContexts, contextsTruncated,
                shownDiagnostics, diagnosticsTruncated) => new
                {
                    error = result.Error,
                    operation = "references",
                    path,
                    line,
                    column = column > 0 ? column : (int?)null,
                    found = result.Error == "fsharp_symbol_not_resolved"
                    ? false
                    : succeeded
                        ? true
                        : (bool?)null,
                    symbol,
                    summary = succeeded
                    ? $"Exactly {result.TotalReferences} compiler-bound non-definition references in the selected physical F# project; the workspace total is a lower bound because dependent projects were not scanned."
                    : null,
                    totalReferences = succeeded ? result.TotalReferences : null,
                    totalIsLowerBound = succeeded ? true : (bool?)null,
                    groupBy = succeeded ? "project" : null,
                    groups = succeeded && result.SelectedContext is not null
                    ? new[]
                    {
                        new
                        {
                            project = result.SelectedContext.Project,
                            targetFramework = result.SelectedContext.TargetFramework,
                            isTest = result.SelectedProjectIsTest,
                            count = result.TotalReferences!.Value,
                            samples = shownSamples.Select(sample => new
                            {
                                path = sample.Path,
                                line = sample.Line,
                                startColumn = sample.StartColumn,
                                endLine = sample.EndLine,
                                endColumn = sample.EndColumn,
                                text = sample.LineText,
                            }),
                        },
                    }
                    : null,
                    sampleCoverage = succeeded && samplesTruncated
                    ? new
                    {
                        selected = result.Samples.Count,
                        returned = shownSamples.Count,
                        complete = false,
                    }
                    : null,
                    coverage = succeeded
                    ? new
                    {
                        scope = "selected_physical_project",
                        workspaceDependentsScanned = 0,
                        workspaceComplete = false,
                    }
                    : null,
                    partial = succeeded || result.PartialReason is not null ? true : (bool?)null,
                    partialReason = result.PartialReason,
                    detail,
                    selectedFSharpTypeCheckContext = selected,
                    availableFSharpTypeCheckContexts = shownContexts,
                    fsharpTypeCheckContextsTotal = contextTotal,
                    fsharpTypeCheckContextsReturned = shownContexts.Count,
                    fsharpTypeCheckContextsTruncated = contextsTruncated ? true : (bool?)null,
                    diagnosticCount = result.DiagnosticCount > 0
                    ? result.DiagnosticCount
                    : (int?)null,
                    diagnostics = shownDiagnostics.Count > 0
                    ? shownDiagnostics.Select(diagnostic => new
                    {
                        severity = diagnostic.Severity,
                        code = diagnostic.Code,
                        message = diagnostic.Message,
                        path = diagnostic.Path,
                        startLine = diagnostic.StartLine,
                        startColumn = diagnostic.StartColumn,
                        endLine = diagnostic.EndLine,
                        endColumn = diagnostic.EndColumn,
                    })
                    : null,
                    diagnosticsTruncated = diagnosticLimitTruncated || diagnosticsTruncated
                    ? true
                    : (bool?)null,
                    timing = new
                    {
                        deadlineMs,
                        elapsedMs,
                    },
                    meta,
                }, maxBytes: TestOnlyReferencesResponseMaxBytes,
            auxiliarySampleItems: MaxFSharpTypeCheckContexts);
        if (Json.Utf8Bytes(shaped) <= Json.HardBudgetBytes) return shaped;

        return Json.WithStringBudget(path, 4096, (boundedPath, pathTruncated) => new
        {
            error = "fsharp_semantic_response_too_large",
            operation = "references",
            path = boundedPath,
            pathTruncated = pathTruncated ? true : (bool?)null,
            line,
            column = column > 0 ? column : (int?)null,
            detail = "The FCS reference identity exceeds the 64 KiB response budget; use a narrower source position.",
            timing = new
            {
                deadlineMs,
                elapsedMs,
            },
            meta = FSharpSemanticMeta(result.Health, "fsharp_semantic_response_too_large",
                result.PartialReason),
        });
    }

    private Meta FSharpSemanticMeta(IndexHealth? health, string? error = null,
        string? partialReason = null)
    {
        string confidence = error is null
            ? FSharpSemanticConfidence(partialReason)
            : "indexed";
        return Meta.From(health ?? _manager.Health(), confidence, "semantic");
    }

    private string ShapeFSharpSemanticResult(string operation, string path, int line, int column,
        FSharpSemanticResult result, int deadlineMs, long elapsedMs)
    {
        var selected = result.SelectedContext is null
            ? null
            : new
            {
                project = result.SelectedContext.Project,
                targetFramework = result.SelectedContext.TargetFramework,
            };
        var allContexts = result.AvailableContexts.Select(context => (object)new
        {
            project = context.Project,
            targetFramework = context.TargetFramework,
        }).ToList();
        int contextTotal = allContexts.Count;
        var contexts = allContexts.Take(MaxFSharpTypeCheckContexts).ToList();
        bool contextLimitTruncated = contextTotal > contexts.Count;
        var declarations = result.Symbol?.Declarations ?? [];
        int declarationsOutsideSelectedProject = result.Symbol is null
            ? 0
            : result.Symbol.DeclarationsOutsideSelectedProjectCount;
        var diagnostics = result.Diagnostics ?? [];
        bool diagnosticLimitTruncated = result.DiagnosticCount > diagnostics.Count;
        string? error = result.Error;
        string? detail = error switch
        {
            "fsharp_type_check_context_required" =>
                "Select one physical F# project and target framework using projectPath + targetFramework.",
            "fsharp_type_check_context_not_found" =>
                "The requested projectPath + targetFramework is not an owning type-check context for this file.",
            "fsharp_semantic_column_required" =>
                "More than one F# symbol occurs on this line; provide a 1-based column.",
            "fsharp_semantic_line_only_source_limit" =>
                "Line-only F# lookup is disabled for this source size; provide a 1-based column.",
            "fsharp_semantic_position_invalid" =>
                "F# semantic positions require line >= 1 and column >= 0 (0 means line-only).",
            "fsharp_semantic_project_references_unsupported" =>
                "This F# closure reaches a non-F# ProjectReference. FCS cannot type-check that source project, and Phoenix never substitutes a potentially stale last-built project DLL.",
            "fsharp_semantic_project_reference_metadata_unsupported" =>
                "An active ProjectReference changes compiler closure through metadata or item operations outside the bounded F# evaluator.",
            "fsharp_semantic_project_reference_unavailable" =>
                "An active ProjectReference is missing, unindexed, unreadable, or not an evaluable physical project snapshot.",
            "fsharp_semantic_project_reference_target_framework_unavailable" =>
                FSharpProjectReferenceTargetFrameworkDetail(result.ProjectReferenceFailure),
            "fsharp_semantic_project_reference_cycle" =>
                "The active F# ProjectReference closure contains a cycle; Phoenix stopped instead of constructing recursive FCS options.",
            "fsharp_project_options_conflict" =>
                "Two distinct physical project/TFM contexts in the F# closure produce the same assembly identity.",
            "fsharp_semantic_package_reference_unresolved" =>
                "An active PackageReference identity could not be resolved by the bounded project evaluator.",
            "fsharp_semantic_package_reference_metadata_unsupported" =>
                "An active PackageReference uses compile-asset or alias metadata that the bounded F# evaluator cannot model safely.",
            "fsharp_semantic_central_package_management_unsupported" =>
                "Directory.Packages.props uses central package authority outside the bounded property, condition, import, and PackageVersion projection.",
            "fsharp_semantic_package_assets_unavailable" =>
                "The selected project has PackageReference items but no safe, readable target-specific project.assets.json snapshot.",
            "fsharp_semantic_package_assets_stale" =>
                "The restored package assets do not match the indexed project snapshot and selected target framework.",
            "fsharp_semantic_package_asset_unavailable" =>
                "A target-specific package compile asset is missing, unsafe, unreadable, or not a managed assembly.",
            "fsharp_semantic_compile_order_unavailable" =>
                "F# semantic checking requires deterministic literal Compile membership in compiler order; wildcards, defaults, exclusions, and unevaluated membership are unsupported.",
            "fsharp_semantic_items_conditioned" =>
                "This project uses conditioned semantic items outside the bounded F# semantic project model.",
            "fsharp_semantic_import_unsupported" =>
                "The bounded F# semantic evaluator accepts literal workspace-local .props imports, bounded Directory.Build reference-only .targets chains, and recognized compiler target imports.",
            "fsharp_semantic_sdk_unsupported" =>
                "The selected project uses SDK authority outside the bounded F# semantic project model.",
            "fsharp_semantic_directory_build_unsupported" =>
                "An applicable Directory.Build file changes F# inputs outside the bounded property/condition/reference projection.",
            "fsharp_semantic_directory_build_ambiguous" =>
                "Applicable Directory.Build authority is ambiguous under the Windows host-case policy; F# semantic checking stopped instead of selecting a different indexed file.",
            "fsharp_semantic_directory_packages_ambiguous" =>
                "Applicable Directory.Packages.props authority is ambiguous under the Windows host-case policy; F# semantic checking stopped instead of selecting a different indexed file.",
            "fsharp_semantic_import_items_unsupported" =>
                "An imported .props file contributes an active Compile, Reference, or other unsupported semantic item. Imported PackageReference and PackageVersion authority is evaluated; ProjectReference closure reports its own unsupported cause.",
            "fsharp_semantic_import_path_outside_workspace" =>
                "A project import escapes the selected workspace and was not opened.",
            "fsharp_semantic_import_unavailable" =>
                "A required literal workspace project import is missing, unindexed, unreadable, or invalid XML.",
            "fsharp_semantic_import_cycle" =>
                "The selected F# ProjectReference closure contains a cycle of bounded workspace project imports.",
            "fsharp_semantic_import_count_limit" =>
                "The selected F# ProjectReference closure exceeds the bounded number of inspected workspace project imports.",
            "fsharp_semantic_import_occurrence_limit" =>
                "The selected F# ProjectReference closure exceeds the bounded number of active workspace project-import occurrences.",
            "fsharp_semantic_import_depth_limit" =>
                "A project in the selected F# ProjectReference closure exceeds the bounded workspace project-import depth.",
            "fsharp_semantic_import_bytes_limit" =>
                "The selected F# ProjectReference closure's workspace project imports/inputs exceed the bounded aggregate UTF-8 byte limit.",
            "fsharp_semantic_item_list_limit" =>
                "Project/package reference input lists exceed the bounded F# semantic item count.",
            "fsharp_semantic_dependency_limit" =>
                "Project/package reference dependencies exceed the bounded F# semantic graph size.",
            "fsharp_semantic_condition_unsupported" =>
                "The selected project uses an MSBuild condition outside the bounded F# semantic evaluator grammar.",
            "fsharp_semantic_condition_limit" =>
                "An MSBuild condition exceeds the bounded F# semantic evaluator character limit.",
            "fsharp_semantic_condition_depth_limit" =>
                "An MSBuild condition exceeds the bounded F# semantic evaluator expression depth.",
            "fsharp_semantic_condition_property_unresolved" =>
                "An MSBuild condition depends on an unresolved property that may be supplied by the build environment.",
            "fsharp_semantic_evaluation_depth_limit" =>
                "A project in the selected F# ProjectReference closure exceeds the bounded F# semantic evaluator nesting depth.",
            "fsharp_semantic_evaluation_order_unsupported" =>
                "A property assignment appears after semantic items; the bounded F# semantic evaluator cannot reproduce MSBuild's property-before-item evaluation phases for this project.",
            "fsharp_semantic_property_function_unsupported" =>
                "The selected project uses an MSBuild property function or item transform; the bounded F# semantic evaluator expands simple properties only.",
            "fsharp_semantic_property_unsupported" or
                "fsharp_semantic_property_unresolved" =>
                "A compiler-affecting project property could not be resolved by the bounded F# semantic evaluator.",
            "fsharp_semantic_property_limit" =>
                "The selected F# ProjectReference closure exceeds the bounded number of property assignments.",
            "fsharp_semantic_property_value_limit" =>
                "A project property value exceeds the bounded F# semantic evaluator character limit.",
            "fsharp_semantic_target_evaluation_unsupported" =>
                "An active MSBuild Target mutates F# semantic inputs; the bounded F# semantic evaluator never executes targets or tasks.",
            "fsharp_semantic_item_definition_unsupported" =>
                "An active ItemDefinitionGroup changes F# semantic items outside the bounded F# semantic evaluator.",
            "fsharp_semantic_reference_unresolved" or
                "fsharp_semantic_reference_unavailable" =>
                "A literal assembly reference for the selected project context could not be resolved safely.",
            "fsharp_semantic_reference_changed" =>
                "A literal assembly reference changed during FCS checking; the result was discarded.",
            "fsharp_semantic_reference_bytes_limit" =>
                "Literal assembly references exceed the bounded F# semantic evaluator byte limit.",
            "fsharp_semantic_path_outside_workspace" =>
                "A literal Compile or HintPath item escapes the selected workspace.",
            "fsharp_framework_references_unavailable" =>
                FSharpFrameworkReferencesUnavailableDetail(result.ProjectReferenceFailure),
            "fsharp_core_reference_unavailable" =>
                FSharpCoreReferenceUnavailableDetail(result.ProjectReferenceFailure),
            "fsharp_semantic_assembly_name_unavailable" =>
                "The selected project does not provide a safe literal assembly identity for FCS.",
            "unsupported_fsharp_file_kind" =>
                "The bounded F# semantic model supports compile-owned .fs/.fsi files; .fsx remains text-only.",
            "fsharp_semantic_timeout" =>
                "FCS did not complete within the bounded deadline; retry with a larger timeoutMs.",
            "fsharp_symbol_not_resolved" =>
                "FCS found no symbol at this exact source position.",
            "fsharp_definition_not_in_selected_project" =>
                "The resolved symbol has no declaration in the selected physical F# project or its captured F# ProjectReference closure.",
            null => null,
            _ => "FCS could not produce a trustworthy semantic result for this project snapshot.",
        };
        var meta = FSharpSemanticMeta(result.Health, result.Error, result.PartialReason);

        object? symbol = result.Symbol is null ? null : new
        {
            name = result.Symbol.Name,
            fullName = result.Symbol.FullName,
            kind = result.Symbol.Kind,
            container = result.Symbol.Container,
            @namespace = result.Symbol.Namespace,
            assembly = result.Symbol.Assembly,
            accessibility = result.Symbol.Accessibility,
            use = new
            {
                path = result.Symbol.Use.Path,
                startLine = result.Symbol.Use.StartLine,
                startColumn = result.Symbol.Use.StartColumn,
                endLine = result.Symbol.Use.EndLine,
                endColumn = result.Symbol.Use.EndColumn,
            },
        };

        string shaped = Json.WithAuxiliaryListsBudget(declarations, contexts, diagnostics,
            (shownDeclarations, declarationsTruncated, shownContexts, contextsByteTruncated,
                shownDiagnostics, diagnosticsTruncated) => new
                {
                    error,
                    operation,
                    path,
                    line,
                    column = column > 0 ? column : (int?)null,
                    found = error is null ? result.Symbol is not null : (bool?)null,
                    symbol,
                    declarations = shownDeclarations.Select(declaration => new
                    {
                        declaration.Role,
                        declaration.Path,
                        declaration.StartLine,
                        declaration.StartColumn,
                        declaration.EndLine,
                        declaration.EndColumn,
                    }),
                    declarationsTotal = result.Symbol?.DeclarationCount,
                    declarationsOutsideSelectedProjectCount = result.Symbol is null
                    ? (int?)null
                    : declarationsOutsideSelectedProject > 0
                        ? declarationsOutsideSelectedProject
                        : (int?)null,
                    declarationsFromProjectReferenceClosureCount = result.Symbol is null
                    ? (int?)null
                    : result.Symbol.DeclarationsFromProjectReferenceClosureCount > 0
                        ? result.Symbol.DeclarationsFromProjectReferenceClosureCount
                        : (int?)null,
                    declarationsTruncated = declarationsTruncated ? true : (bool?)null,
                    partial = result.PartialReason is not null ? true : (bool?)null,
                    partialReason = result.PartialReason,
                    detail,
                    selectedFSharpTypeCheckContext = selected,
                    availableFSharpTypeCheckContexts = shownContexts,
                    fsharpTypeCheckContextsTotal = contextTotal,
                    fsharpTypeCheckContextsReturned = shownContexts.Count,
                    fsharpTypeCheckContextsTruncated =
                    contextLimitTruncated || contextsByteTruncated,
                    diagnosticCount = result.DiagnosticCount > 0
                    ? result.DiagnosticCount
                    : (int?)null,
                    diagnostics = shownDiagnostics.Count > 0
                    ? shownDiagnostics.Select(diagnostic => new
                    {
                        severity = diagnostic.Severity,
                        code = diagnostic.Code,
                        message = diagnostic.Message,
                        path = diagnostic.Path,
                        startLine = diagnostic.StartLine,
                        startColumn = diagnostic.StartColumn,
                        endLine = diagnostic.EndLine,
                        endColumn = diagnostic.EndColumn,
                    })
                    : null,
                    diagnosticsTruncated = diagnosticLimitTruncated || diagnosticsTruncated
                    ? true
                    : (bool?)null,
                    limit = result.LimitActual is not null && result.LimitMaximum is not null
                    ? new { actual = result.LimitActual, maximum = result.LimitMaximum, unit = "characters" }
                    : null,
                    timing = new
                    {
                        deadlineMs,
                        elapsedMs,
                    },
                    meta,
                }, auxiliarySampleItems: MaxFSharpTypeCheckContexts);
        if (Json.Utf8Bytes(shaped) <= Json.HardBudgetBytes) return shaped;

        // Lists can be reduced to zero, but FCS-derived symbol names are fixed members of the
        // normal envelope and F# permits very long quoted identifiers. Fail closed with a bounded
        // diagnostic instead of violating the process-wide hard response contract.
        var fallbackMeta = Meta.From(result.Health ?? _manager.Health(), "indexed", "semantic");
        return Json.WithStringBudget(path, 4096, (boundedPath, pathTruncated) => new
        {
            error = "fsharp_semantic_response_too_large",
            operation,
            path = boundedPath,
            pathTruncated = pathTruncated ? true : (bool?)null,
            line,
            column = column > 0 ? column : (int?)null,
            detail = "The FCS semantic result exceeds the 64 KiB response budget; use a narrower source position or textual navigation.",
            timing = new
            {
                deadlineMs,
                elapsedMs,
            },
            meta = fallbackMeta,
        });
    }

    private static string FSharpProjectReferenceTargetFrameworkDetail(
        FSharpProjectReferenceFailure? failure)
    {
        string project = failure?.Project ?? "the referenced F# project";
        string consumer = failure?.ConsumerTargetFramework ?? "unknown";
        string available = failure?.AvailableTargetFrameworks.Count > 0
            ? string.Join(", ", failure.AvailableTargetFrameworks)
            : "none";
        string rule = failure?.CompatibilityTableRow ??
                      "No Microsoft .NET Standard compatibility row was available.";
        string selection = failure?.MultiTargetExactMatchOnly == true
            ? " Multi-target referenced projects remain exact-match-only in v0.12.84."
            : "";
        return $"Referenced project '{project}' has no target-framework context for consumer " +
               $"'{consumer}'. Available child TFMs: {available}.{selection} Table row " +
               $"consulted: {rule} Source: Microsoft .NET Standard support table, " +
               "https://learn.microsoft.com/dotnet/standard/net-standard. The table notes " +
               "that NuGet treats .NET Framework 4.6.1 as applicable to .NET Standard " +
               "1.5 through 2.0 while recommending .NET Framework 4.7.2 or later for " +
               "runtime compatibility; Phoenix applies the compile-applicability row.";
    }

    private static string FSharpFrameworkReferencesUnavailableDetail(
        FSharpProjectReferenceFailure? failure) =>
        IsUnmaterializedNetStandard1(failure)
            ? FSharpNetStandard1CompileInputsDetail(failure)
            : IsPlatformQualifiedTargetFramework(failure)
                ? $"Exact platform reference assemblies are unavailable for target framework " +
                  $"'{failure?.SelectedTargetFramework}'. Phoenix failed closed and did not " +
                  "substitute the base .NET reference pack."
            : "Exact target reference assemblies are unavailable for the selected target framework.";

    private static string FSharpCoreReferenceUnavailableDetail(
        FSharpProjectReferenceFailure? failure) =>
        IsUnmaterializedNetStandard1(failure)
            ? FSharpNetStandard1CompileInputsDetail(failure)
            : "A target-compatible FSharp.Core reference is unavailable for the selected target framework.";

    private static bool IsUnmaterializedNetStandard1(
        FSharpProjectReferenceFailure? failure) =>
        failure?.SelectedTargetFramework?.StartsWith("netstandard1.",
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPlatformQualifiedTargetFramework(
        FSharpProjectReferenceFailure? failure) =>
        failure?.SelectedTargetFramework?.Contains('-', StringComparison.Ordinal) == true;

    private static string FSharpNetStandard1CompileInputsDetail(
        FSharpProjectReferenceFailure? failure) =>
        $"F# project '{failure?.Project ?? "the referenced F# project"}' selected " +
        $"target framework '{failure?.SelectedTargetFramework ?? "netstandard1.x"}', but " +
        "this build does not materialize netstandard1.x compile inputs from the granular " +
        "autoReferenced NETStandard.Library closure in project.assets.json. Phoenix failed " +
        "closed and did not substitute the wider netstandard2.0 reference pack.";
}
