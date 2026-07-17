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
            : Math.Max(0, result.Symbol.DeclarationCount - declarations.Count);
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
            "fsharp_semantic_package_references_unsupported" or
                "fsharp_semantic_project_references_unsupported" =>
                "This Stage 2A type-check context requires reference closure scheduled for Stage 2B.",
            "fsharp_semantic_compile_order_unavailable" =>
                "F# semantic checking requires literal, unconditional Compile items in compiler order.",
            "fsharp_semantic_items_conditioned" or "fsharp_semantic_import_unsupported" =>
                "This project requires MSBuild condition/import evaluation outside the bounded Stage 2A model.",
            "fsharp_semantic_reference_unresolved" or
                "fsharp_semantic_reference_unavailable" =>
                "A literal assembly reference for the selected project context could not be resolved safely.",
            "fsharp_semantic_reference_changed" =>
                "A literal assembly reference changed during FCS checking; the result was discarded.",
            "fsharp_semantic_reference_bytes_limit" =>
                "Literal assembly references exceed the bounded Stage 2A byte limit.",
            "fsharp_semantic_path_outside_workspace" =>
                "A literal Compile or HintPath item escapes the selected workspace.",
            "fsharp_framework_references_unavailable" =>
                "Exact target reference assemblies are unavailable for the selected target framework.",
            "fsharp_core_reference_unavailable" =>
                "A target-compatible FSharp.Core reference is unavailable for the selected target framework.",
            "fsharp_semantic_assembly_name_unavailable" =>
                "The selected project does not provide a safe literal assembly identity for FCS.",
            "unsupported_fsharp_file_kind" =>
                "F# semantic Stage 2A supports compile-owned .fs/.fsi files; .fsx remains text-only.",
            "fsharp_semantic_timeout" =>
                "FCS did not complete within the bounded deadline; retry with a larger timeoutMs.",
            "fsharp_symbol_not_resolved" =>
                "FCS found no symbol at this exact source position.",
            "fsharp_definition_not_in_selected_project" =>
                "The resolved symbol has no declaration in the selected physical F# project.",
            null => null,
            _ => "FCS could not produce a trustworthy semantic result for this project snapshot.",
        };
        var meta = Meta.From(result.Health ?? _manager.Health(), "indexed", "semantic");

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
            meta,
        });
    }
}
