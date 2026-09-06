using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.FSharp;

namespace CodeNav.Core.Semantic;

public sealed record FSharpOutlineItem(
    string Name,
    string Kind,
    string? Signature,
    string Accessibility,
    int StartLine,
    int EndLine,
    string? Modifiers,
    string? Accessors,
    List<FSharpOutlineItem> Members);

public sealed record FSharpOutlineParseContext(
    string Project,
    string? TargetFramework);

public sealed record FSharpOutlineResult(
    List<FSharpOutlineItem> Symbols,
    string? Error = null,
    long? FileBytes = null,
    long? MaxBytes = null,
    string? PartialReason = null,
    string? SelectedProject = null,
    string? SelectedTargetFramework = null,
    List<FSharpOutlineParseContext>? AvailableParseContexts = null);

public sealed partial class SemanticService
{
    private sealed record FSharpOwnerOptions(
        ProjectRow Owner,
        FSharpParsingOptionsSnapshot Options);

    // Match the existing structural-input ceiling used by the C# parser. F# text indexing may
    // retain much larger files, but an on-demand compiler parse is a different cost profile.
    public const int MaxFSharpOutlineBytes = IndexBuilder.MaxStructuralFileBytes;

    /// <summary>
    /// Returns an FCS-derived declaration outline for an indexed, project-owned .fs/.fsi file.
    /// This is intentionally syntax-only: project type checking belongs to later F# stages.
    /// </summary>
    public FSharpOutlineResult FSharpOutline(string path)
    {
        using var queries = _manager.OpenQueries();
        FileHit? file = queries.FileByPath(path);
        if (file is not { Language: "fs" })
            return new([], "unsupported_language");

        string extension = Path.GetExtension(path);
        if (!extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase))
        {
            return new([], "unsupported_fsharp_file_kind");
        }

        List<ProjectRow> owners = queries.ProjectsContaining(path)
            .Where(project => project.Language == "fs")
            .OrderBy(project => project.Path, StringComparer.Ordinal)
            .ToList();
        if (owners.Count == 0)
            return new([], "fsharp_project_not_found");

        var ownerOptions = new List<FSharpOwnerOptions>(owners.Count);
        foreach (ProjectRow owner in owners)
        {
            string? projectXml = queries.ContentByPathBounded(
                owner.Path, IndexBuilder.MaxStructuralFileBytes);
            if (projectXml is null)
                return new([], "fsharp_project_options_unavailable");

            FSharpParsingOptionsSnapshot options =
                ProjectFileParser.ParseFSharpParsingOptionsSnapshot(
                    owner.Path, projectXml, owner.Tfms);
            if (options.Error is { } optionError)
                return new([], optionError);

            ownerOptions.Add(new(owner, options));
        }

        var availableParseContexts = new List<FSharpOutlineParseContext>();
        foreach (FSharpOwnerOptions owner in ownerOptions)
        {
            if (owner.Options.AvailableTargetFrameworks is { Count: > 0 } frameworks)
            {
                availableParseContexts.AddRange(frameworks.Select(framework =>
                    new FSharpOutlineParseContext(owner.Owner.Path, framework)));
            }
            else
            {
                availableParseContexts.Add(new(owner.Owner.Path, null));
            }
        }

        FSharpOwnerOptions? pairedBase = SelectPairedBaseProject(ownerOptions);
        FSharpOwnerOptions selected = pairedBase ?? ownerOptions[0];
        availableParseContexts = availableParseContexts
            .OrderBy(context => context.Project.Equals(selected.Owner.Path,
                WorkspacePaths.FileSystemPathComparison) ? 0 : 1)
            .ToList();
        var partialReasons = new SortedSet<string>(StringComparer.Ordinal);
        if (pairedBase is null)
        {
            foreach (FSharpOwnerOptions owner in ownerOptions)
            {
                if (!selected.Options.CommandLineArgs.SequenceEqual(
                        owner.Options.CommandLineArgs, StringComparer.Ordinal))
                {
                    return new([], "fsharp_project_options_conflict");
                }
                AddPartialReasons(partialReasons, owner.Options.PartialReason);
            }
        }
        else
        {
            AddPartialReasons(partialReasons, selected.Options.PartialReason);
            partialReasons.Add("fsharp_alternate_parse_contexts");
        }

        if (file.Size > MaxFSharpOutlineBytes)
            return new([], "file_too_large", file.Size, MaxFSharpOutlineBytes);

        string? source = queries.ContentByPathBounded(path, MaxFSharpOutlineBytes);
        if (source is null)
            return new([], "file_content_unavailable", file.Size, MaxFSharpOutlineBytes);

        try
        {
            string fileName = Path.GetFullPath(Path.Combine(
                _manager.WorkspaceRoot,
                path.Replace('/', Path.DirectorySeparatorChar)));
            OutlineParseResult parsed = OutlineParser.Parse(
                fileName, source, selected.Options.CommandLineArgs.ToArray());
            if (parsed.Error is { } error)
                return new([], error);

            var symbols = parsed.Symbols
                .Select(MapOutlineItem)
                .OrderBy(item => item.StartLine)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            return new(symbols, PartialReason: partialReasons.Count == 0
                ? null
                : string.Join(';', partialReasons),
                SelectedProject: selected.Owner.Path,
                SelectedTargetFramework: selected.Options.SelectedTargetFramework,
                AvailableParseContexts: availableParseContexts);
        }
        catch (Exception ex)
        {
            _log($"F# outline failed: {ex.GetType().Name}");
            return new([], "fsharp_outline_failed");
        }
    }

    /// <summary>
    /// Resolves one F# symbol against a selected physical project + TFM type-check environment.
    /// Admission is bounded before every source byte and project option is captured from one pinned
    /// index epoch; SQLite is released before invoking FCS. Restored package compile assets and the
    /// exact-TFM F# ProjectReference closure under the evaluated MSBuild transitivity policy are
    /// captured immutably. Non-F# dependencies
    /// fail closed rather than borrowing a potentially stale last-built project binary.
    /// </summary>
    public async Task<FSharpSemanticResult> FSharpSymbolAtAsync(
        string path,
        int line,
        int column,
        string? projectPath,
        string? targetFramework,
        int timeoutMs)
    {
        if (line < 1 || column < 0)
            return new(null, "fsharp_semantic_position_invalid", null, []);
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 60_000));
        CapturedFSharpSemanticProject? captured = null;
        bool entered = false;
        try
        {
            // Admission precedes all source/reference capture so waiting requests cannot each retain
            // a maximum-sized snapshot while the single FCS worker is busy.
            await _fsharpSemanticGate.WaitAsync(cts.Token).ConfigureAwait(false);
            entered = true;
            captured = CaptureFSharpSemanticProject(path, projectPath, targetFramework,
                cts.Token, out FSharpSemanticResult? failure);
            if (captured is null) return failure!;
            // Capture owns a short SQLite read snapshot. Invoke the deterministic test seam only
            // after Capture has returned and its using scope has released that snapshot.
            FSharpSemanticSnapshotCapturedForTest?.Invoke();

            SemanticCheckResult check = await SemanticResolver.ResolveAsync(
                captured.Projects,
                captured.RootProjectIndex,
                captured.Fingerprint,
                captured.BinaryReferences.Count == 0,
                captured.TargetFileName,
                line,
                column,
                MaxFSharpSemanticLineOnlySourceChars,
                cts.Token).ConfigureAwait(false);
            FSharpSemanticCheckCompletedForTest?.Invoke(check.Error);

            var rootSourcePaths = captured.SourceFiles.ToHashSet(
                WorkspacePaths.FileSystemPathComparer);
            var sourcePaths = captured.ClosureSourceFiles.ToHashSet(
                WorkspacePaths.FileSystemPathComparer);
            List<FSharpSemanticDiagnostic> diagnostics = check.Diagnostics
                .Select(diagnostic => MapFSharpDiagnostic(diagnostic, sourcePaths))
                .ToList();
            string? partialReason = check.ErrorDiagnosticCount > 0
                ? AppendPartialReason(captured.PartialReason,
                    "fsharp_semantic_diagnostics_present")
                : captured.PartialReason;
            bool lineOnlyLimit = check.Error == "fsharp_semantic_line_only_source_limit";
            int? limitActual = lineOnlyLimit
                ? captured.SourceTexts[Array.FindIndex(captured.SourceFiles, source =>
                    source.Equals(captured.TargetFileName,
                        WorkspacePaths.FileSystemPathComparison))].Length
                : null;
            int? limitMaximum = lineOnlyLimit ? MaxFSharpSemanticLineOnlySourceChars : null;

            if (!VerifyFSharpBinaryReferences(captured.BinaryReferences, cts.Token))
            {
                return new(null, "fsharp_semantic_reference_changed",
                    captured.SelectedContext, captured.AvailableContexts,
                    partialReason, check.DiagnosticCount, diagnostics, captured.Health);
            }

            if (check.Symbol is null)
            {
                string? error = check.Error == "fsharp_symbol_not_resolved"
                    ? null
                    : check.Error ?? "fsharp_semantic_failed";
                return new(null, error,
                    captured.SelectedContext, captured.AvailableContexts,
                    partialReason, check.DiagnosticCount, diagnostics, captured.Health,
                    limitActual, limitMaximum);
            }

            FSharpSemanticRange MapRange(CodeNav.FSharp.SemanticLocation location) => new(
                location.Role,
                ToRelPath(location.FileName),
                location.StartLine,
                location.StartColumn + 1,
                location.EndLine,
                location.EndColumn + 1);
            var declarations = check.Symbol.Declarations
                .Where(location => sourcePaths.Contains(Path.GetFullPath(location.FileName)))
                .Select(MapRange)
                .ToList();
            int declarationsOutsideSelectedProject = Math.Max(0,
                check.Symbol.Declarations.Length - declarations.Count);
            int declarationsFromProjectReferenceClosure = check.Symbol.Declarations.Count(
                location =>
                {
                    string declarationPath = Path.GetFullPath(location.FileName);
                    return sourcePaths.Contains(declarationPath) &&
                           !rootSourcePaths.Contains(declarationPath);
                });
            var symbol = new FSharpSemanticSymbolInfo(
                check.Symbol.Name,
                check.Symbol.FullName,
                check.Symbol.Kind,
                check.Symbol.Container,
                check.Symbol.Namespace,
                check.Symbol.Assembly,
                check.Symbol.Accessibility,
                MapRange(check.Symbol.UseLocation),
                check.Symbol.Declarations.Length,
                declarations,
                declarationsOutsideSelectedProject,
                declarationsFromProjectReferenceClosure);
            return new(symbol, null, captured.SelectedContext, captured.AvailableContexts,
                partialReason, check.DiagnosticCount, diagnostics, captured.Health);
        }
        catch (OperationCanceledException)
        {
            return new(null, "fsharp_semantic_timeout", captured?.SelectedContext,
                captured?.AvailableContexts ?? [], captured?.PartialReason,
                Health: captured?.Health);
        }
        catch (Exception ex)
        {
            _log($"F# semantic request failed: {ex.GetType().Name}");
            return new(null, captured is null
                    ? "fsharp_semantic_snapshot_failed"
                    : "fsharp_semantic_failed",
                captured?.SelectedContext, captured?.AvailableContexts ?? [],
                captured?.PartialReason, Health: captured?.Health);
        }
        finally
        {
            if (captured is not null) CleanupFSharpReferenceSnapshots(captured);
            if (entered) _fsharpSemanticGate.Release();
        }
    }

    private static FSharpOutlineItem MapOutlineItem(OutlineItem item) =>
        new(
            item.Name,
            item.Kind,
            item.Signature,
            item.Accessibility,
            item.StartLine,
            item.EndLine,
            item.Modifiers,
            item.Accessors,
            item.Members.Select(MapOutlineItem).ToList());

    private static FSharpOwnerOptions? SelectPairedBaseProject(
        List<FSharpOwnerOptions> owners)
    {
        // Migration convention, syntax selection only: the old single-target project remains the
        // build/process baseline while the exact .Net companion dual-compiles the same source for
        // binary comparison and the new runtime. Do not collapse their graph or reference facts.
        if (owners.Count != 2) return null;

        foreach (FSharpOwnerOptions candidate in owners)
        {
            if (!candidate.Owner.Style.Equals("legacy", StringComparison.OrdinalIgnoreCase) ||
                candidate.Options.AvailableTargetFrameworks is not { Count: 1 } baseFrameworks)
            {
                continue;
            }

            FSharpOwnerOptions companion = ReferenceEquals(candidate, owners[0])
                ? owners[1]
                : owners[0];
            if (!companion.Owner.Style.Equals("sdk", StringComparison.OrdinalIgnoreCase) ||
                companion.Options.AvailableTargetFrameworks is not { Count: > 1 } companionFrameworks ||
                !IsExactNetCompanion(candidate.Owner.Path, companion.Owner.Path) ||
                !companionFrameworks.Contains(baseFrameworks[0],
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool IsExactNetCompanion(string baseProject, string companionProject)
    {
        string normalizedBase = baseProject.Replace('\\', '/');
        string normalizedCompanion = companionProject.Replace('\\', '/');
        string baseDirectory = normalizedBase.Contains('/')
            ? normalizedBase[..normalizedBase.LastIndexOf('/')]
            : "";
        string companionDirectory = normalizedCompanion.Contains('/')
            ? normalizedCompanion[..normalizedCompanion.LastIndexOf('/')]
            : "";
        if (!baseDirectory.Equals(companionDirectory, WorkspacePaths.FileSystemPathComparison))
            return false;

        string baseName = Path.GetFileNameWithoutExtension(normalizedBase);
        string companionName = Path.GetFileNameWithoutExtension(normalizedCompanion);
        return companionName.Equals($"{baseName}.Net", WorkspacePaths.FileSystemPathComparison);
    }
}
