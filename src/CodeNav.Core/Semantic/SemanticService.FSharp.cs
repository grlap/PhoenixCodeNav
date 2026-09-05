using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.FSharp;
using Microsoft.Win32.SafeHandles;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

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

public sealed record FSharpTypeCheckContext(string Project, string TargetFramework);

public sealed record FSharpSemanticRange(
    string Role,
    string Path,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

public sealed record FSharpSemanticDiagnostic(
    string Severity,
    string Code,
    string Message,
    string? Path,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn);

public sealed record FSharpProjectReferenceFailure(
    string Project,
    List<string> AvailableTargetFrameworks);

public sealed record FSharpSemanticSymbolInfo(
    string Name,
    string? FullName,
    string Kind,
    string? Container,
    string? Namespace,
    string? Assembly,
    string? Accessibility,
    FSharpSemanticRange Use,
    int DeclarationCount,
    List<FSharpSemanticRange> Declarations,
    int DeclarationsOutsideSelectedProjectCount = 0,
    int DeclarationsFromProjectReferenceClosureCount = 0);

public sealed record FSharpSemanticResult(
    FSharpSemanticSymbolInfo? Symbol,
    string? Error,
    FSharpTypeCheckContext? SelectedContext,
    List<FSharpTypeCheckContext> AvailableContexts,
    string? PartialReason = null,
    int DiagnosticCount = 0,
    List<FSharpSemanticDiagnostic>? Diagnostics = null,
    IndexHealth? Health = null,
    int? LimitActual = null,
    int? LimitMaximum = null,
    FSharpProjectReferenceFailure? ProjectReferenceFailure = null);

public sealed record FSharpReferenceSample(
    string Path,
    int Line,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string LineText);

public sealed record FSharpReferencesResult(
    FSharpSemanticSymbolInfo? Symbol,
    int? TotalReferences,
    List<FSharpReferenceSample> Samples,
    string? Error,
    FSharpTypeCheckContext? SelectedContext,
    List<FSharpTypeCheckContext> AvailableContexts,
    bool SelectedProjectIsTest = false,
    string? PartialReason = null,
    int DiagnosticCount = 0,
    List<FSharpSemanticDiagnostic>? Diagnostics = null,
    IndexHealth? Health = null,
    FSharpProjectReferenceFailure? ProjectReferenceFailure = null);

public sealed partial class SemanticService
{
    private sealed record FSharpOwnerOptions(
        ProjectRow Owner,
        FSharpParsingOptionsSnapshot Options);

    // Match the existing structural-input ceiling used by the C# parser. F# text indexing may
    // retain much larger files, but an on-demand compiler parse is a different cost profile.
    public const int MaxFSharpOutlineBytes = IndexBuilder.MaxStructuralFileBytes;
    public const int MaxFSharpSemanticSourceFiles = 256;
    public const int MaxFSharpSemanticSourceBytes = 16 * 1024 * 1024;
    public const int MaxFSharpSemanticLineOnlySourceChars = 256 * 1024;
    public const int MaxFSharpSemanticHintPaths = 64;
    public const long MaxFSharpSemanticReferenceBytes = 256L * 1024 * 1024;
    private readonly SemaphoreSlim _fsharpSemanticGate = new(1, 1);
    internal Action? FSharpSemanticSnapshotCapturedForTest { get; set; }
    internal Action<string?>? FSharpSemanticCheckCompletedForTest { get; set; }
    internal Action<string>? BeforeFSharpReferenceOpenForTest { get; set; }
    internal Action<string>? BeforeFSharpSemanticSourceReadForTest { get; set; }
    internal Action<string>? BeforeFSharpPackageRootProbeForTest { get; set; }
    internal Action<string>? FSharpReferenceSnapshotCreatedForTest { get; set; }
    internal int? FSharpSemanticSourceFilesLimitForTest { get; set; }
    internal int? FSharpSemanticSourceBytesLimitForTest { get; set; }
    internal long? FSharpSemanticReferenceBytesLimitForTest { get; set; }

    private sealed record FSharpBinaryReferenceSnapshot(
        string SourceIdentity,
        string OriginalFullPath,
        string AllowedRootFullPath,
        string SnapshotFullPath,
        long Length,
        string Sha256);

    private sealed record CapturedFSharpSemanticNode(
        SemanticProjectInput Input,
        string ProjectPath,
        string Fingerprint,
        bool[] SourceGenerated,
        string? PartialReason,
        bool IsTest,
        int[] DescendantProjectIndices);

    private const bool ShareFSharpSemanticBudgetsAcrossProjectClosure = true;

    private sealed class FSharpSemanticClosureBudget(
        int sourceFilesLimit,
        int sourceBytesLimit,
        long referenceBytesLimit)
    {
        private int _sourceFiles;
        private int _sourceBytes;
        private int _referenceInputs;
        private long _referenceBytes;

        public bool TryReserveSources(int files, int bytes, out string? error)
        {
            error = null;
            if (files < 0 || files > sourceFilesLimit - _sourceFiles)
            {
                error = "fsharp_semantic_source_limit";
                return false;
            }
            if (bytes < 0 || bytes > sourceBytesLimit - _sourceBytes)
            {
                error = "fsharp_semantic_source_bytes_limit";
                return false;
            }
            _sourceFiles += files;
            _sourceBytes += bytes;
            return true;
        }

        public bool TryReserveReferenceInputs(int count)
        {
            if (count < 0 || count > MaxFSharpSemanticHintPaths - _referenceInputs)
                return false;
            _referenceInputs += count;
            return true;
        }

        public long RemainingReferenceBytes => Math.Max(0, referenceBytesLimit - _referenceBytes);

        public bool TryReserveReferenceBytes(long bytes)
        {
            if (bytes <= 0 || bytes > RemainingReferenceBytes) return false;
            _referenceBytes += bytes;
            return true;
        }
    }

    private sealed record FSharpSemanticNodeBudgets(
        FSharpSemanticClosureBudget Content,
        ProjectFileParser.FSharpSemanticEvaluationBudget Evaluation);

    private sealed class FSharpSemanticClosureBudgetPolicy
    {
        private readonly int _sourceFilesLimit;
        private readonly int _sourceBytesLimit;
        private readonly long _referenceBytesLimit;
        private readonly FSharpSemanticNodeBudgets? _shared;

        public FSharpSemanticClosureBudgetPolicy(int sourceFilesLimit,
            int sourceBytesLimit, long referenceBytesLimit,
            bool shareAcrossClosure)
        {
            _sourceFilesLimit = sourceFilesLimit;
            _sourceBytesLimit = sourceBytesLimit;
            _referenceBytesLimit = referenceBytesLimit;
            _shared = shareAcrossClosure ? CreateNodeBudgets() : null;
        }

        public FSharpSemanticNodeBudgets ForNode() =>
            _shared ?? CreateNodeBudgets();

        private FSharpSemanticNodeBudgets CreateNodeBudgets() => new(
            new FSharpSemanticClosureBudget(_sourceFilesLimit, _sourceBytesLimit,
                _referenceBytesLimit),
            new ProjectFileParser.FSharpSemanticEvaluationBudget());
    }

    private sealed record CapturedFSharpSemanticProject(
        SemanticProjectInput[] Projects,
        int RootProjectIndex,
        bool[] RootSourceGenerated,
        string[] ClosureSourceFiles,
        string Fingerprint,
        string TargetFileName,
        FSharpTypeCheckContext SelectedContext,
        List<FSharpTypeCheckContext> AvailableContexts,
        List<FSharpBinaryReferenceSnapshot> BinaryReferences,
        string? ReferenceSnapshotDirectory,
        string? PartialReason,
        bool SelectedProjectIsTest,
        IndexHealth Health)
    {
        public SemanticProjectInput RootProject => Projects[RootProjectIndex];
        public string[] SourceFiles => RootProject.SourceFiles;
        public string[] SourceTexts => RootProject.SourceTexts;
        public bool[] SourceGenerated => RootSourceGenerated;
    }

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

    /// <summary>
    /// Enumerates compiler-bound non-definition uses inside one selected physical F# project and
    /// target framework. The exact selected-project count is returned independently from the
    /// bounded samples; workspace dependents are deliberately left to the cross-project stage.
    /// </summary>
    public async Task<FSharpReferencesResult> FSharpReferencesAsync(
        string path,
        int line,
        int column,
        string? projectPath,
        string? targetFramework,
        bool includeTests,
        bool includeGenerated,
        int samplesPerGroup,
        int timeoutMs)
    {
        if (line < 1 || column < 0)
            return new(null, null, [], "fsharp_semantic_position_invalid", null, []);
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120_000));
        CapturedFSharpSemanticProject? captured = null;
        bool entered = false;
        try
        {
            await _fsharpSemanticGate.WaitAsync(cts.Token).ConfigureAwait(false);
            entered = true;
            captured = CaptureFSharpSemanticProject(path, projectPath, targetFramework,
                cts.Token, out FSharpSemanticResult? failure);
            if (captured is null) return FSharpReferencesFailure(failure!);
            FSharpSemanticSnapshotCapturedForTest?.Invoke();

            SemanticCheckResult check = await SemanticResolver.ResolveReferencesAsync(
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

            if (!VerifyFSharpBinaryReferences(captured.BinaryReferences, cts.Token))
            {
                return new(null, null, [], "fsharp_semantic_reference_changed",
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason, check.DiagnosticCount,
                    diagnostics, captured.Health);
            }

            if (check.Symbol is null)
            {
                return new(null, null, [], check.Error ?? "fsharp_semantic_failed",
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason, check.DiagnosticCount,
                    diagnostics, captured.Health);
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

            var sourceIndex = captured.SourceFiles
                .Select((source, index) => (source, index))
                .ToDictionary(pair => pair.source, pair => pair.index,
                    WorkspacePaths.FileSystemPathComparer);
            var accepted = new List<(CodeNav.FSharp.SemanticLocation Location, int SourceIndex)>();
            if (includeTests || !captured.SelectedProjectIsTest)
            {
                foreach (CodeNav.FSharp.SemanticLocation reference in check.References)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string fullPath = Path.GetFullPath(reference.FileName);
                    if (!sourceIndex.TryGetValue(fullPath, out int index)) continue;
                    if (!includeGenerated && captured.SourceGenerated[index]) continue;
                    accepted.Add((reference, index));
                }
            }

            int sampleLimit = Math.Clamp(samplesPerGroup, 0, 10);
            var samples = new List<FSharpReferenceSample>(Math.Min(sampleLimit, accepted.Count));
            foreach ((CodeNav.FSharp.SemanticLocation reference, int index) in
                     accepted.Take(sampleLimit))
            {
                Microsoft.CodeAnalysis.Text.SourceText text =
                    Microsoft.CodeAnalysis.Text.SourceText.From(captured.SourceTexts[index]);
                int lineIndex = reference.StartLine - 1;
                if ((uint)lineIndex >= (uint)text.Lines.Count) continue;
                samples.Add(new(
                    ToRelPath(reference.FileName),
                    reference.StartLine,
                    reference.StartColumn + 1,
                    reference.EndLine,
                    reference.EndColumn + 1,
                    Truncate(text.Lines[lineIndex].ToString().Trim())));
            }

            partialReason = AppendPartialReason(partialReason,
                "fsharp_references_workspace_dependents_not_scanned");
            return new(symbol, accepted.Count, samples, null,
                captured.SelectedContext, captured.AvailableContexts,
                captured.SelectedProjectIsTest, partialReason, check.DiagnosticCount,
                diagnostics, captured.Health);
        }
        catch (OperationCanceledException)
        {
            return new(null, null, [], "fsharp_semantic_timeout", captured?.SelectedContext,
                captured?.AvailableContexts ?? [], captured?.SelectedProjectIsTest ?? false,
                captured?.PartialReason, Health: captured?.Health);
        }
        catch (Exception ex)
        {
            _log($"F# references request failed: {ex.GetType().Name}");
            return new(null, null, [], captured is null
                    ? "fsharp_semantic_snapshot_failed"
                    : "fsharp_semantic_failed",
                captured?.SelectedContext, captured?.AvailableContexts ?? [],
                captured?.SelectedProjectIsTest ?? false, captured?.PartialReason,
                Health: captured?.Health);
        }
        finally
        {
            if (captured is not null) CleanupFSharpReferenceSnapshots(captured);
            if (entered) _fsharpSemanticGate.Release();
        }
    }

    private static FSharpReferencesResult FSharpReferencesFailure(FSharpSemanticResult failure) =>
        new(null, null, [], failure.Error, failure.SelectedContext,
            failure.AvailableContexts, PartialReason: failure.PartialReason,
            DiagnosticCount: failure.DiagnosticCount, Diagnostics: failure.Diagnostics,
            Health: failure.Health, ProjectReferenceFailure: failure.ProjectReferenceFailure);

    private CapturedFSharpSemanticProject? CaptureFSharpSemanticProject(
        string path,
        string? requestedProject,
        string? requestedTargetFramework,
        CancellationToken cancellationToken,
        out FSharpSemanticResult? failure)
    {
        failure = null;
        using IndexReadSnapshot? snapshot = _manager.TryOpenReviewSnapshot(cancellationToken);
        if (snapshot is null)
        {
            failure = new(null, "index_snapshot_unavailable", null, []);
            return null;
        }

        IndexQueries queries = snapshot.Queries;
        FileHit? target = queries.FileByPath(path);
        if (target is not { Language: "fs" })
        {
            failure = new(null, "unsupported_language", null, [], Health: snapshot.Health);
            return null;
        }
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase))
        {
            failure = new(null, "unsupported_fsharp_file_kind", null, [], Health: snapshot.Health);
            return null;
        }

        List<ProjectRow> owners = queries.ProjectsContaining(path)
            .Where(project => project.Language == "fs")
            .OrderBy(project => project.Path, StringComparer.Ordinal)
            .ToList();
        var contexts = owners
            .SelectMany(owner => owner.Tfms.Split(';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(tfm => new FSharpTypeCheckContext(owner.Path, tfm)))
            .OrderBy(context => context.Project, StringComparer.Ordinal)
            .ThenBy(context => context.TargetFramework, StringComparer.Ordinal)
            .ToList();
        if (contexts.Count == 0)
        {
            failure = new(null, "fsharp_type_check_context_unavailable", null, contexts,
                Health: snapshot.Health);
            return null;
        }

        bool hasRequestedProject = !string.IsNullOrWhiteSpace(requestedProject);
        bool hasRequestedTarget = !string.IsNullOrWhiteSpace(requestedTargetFramework);
        if (hasRequestedProject != hasRequestedTarget)
        {
            failure = new(null, "fsharp_type_check_context_required", null, contexts,
                Health: snapshot.Health);
            return null;
        }

        string? normalizedProject = !hasRequestedProject
            ? null
            : WorkspacePaths.ToGitPath(requestedProject!.Trim()).TrimStart('/');
        List<FSharpTypeCheckContext> matches = contexts.Where(context =>
                (normalizedProject is null || context.Project.Equals(normalizedProject,
                    WorkspacePaths.FileSystemPathComparison)) &&
                (!hasRequestedTarget ||
                 context.TargetFramework.Equals(requestedTargetFramework!.Trim(),
                     StringComparison.OrdinalIgnoreCase)))
            .ToList();
        bool implicitSelection = !hasRequestedProject;
        if ((implicitSelection && contexts.Count != 1) || matches.Count != 1)
        {
            string error = matches.Count == 0 && !implicitSelection
                ? "fsharp_type_check_context_not_found"
                : "fsharp_type_check_context_required";
            failure = new(null, error, null, contexts, Health: snapshot.Health);
            return null;
        }

        FSharpTypeCheckContext selected = matches[0];
        contexts = contexts
            .OrderBy(context => context.Equals(selected) ? 0 : 1)
            .ThenBy(context => context.Project, StringComparer.Ordinal)
            .ThenBy(context => context.TargetFramework, StringComparer.Ordinal)
            .ToList();
        ProjectRow owner = owners.Single(project => project.Path.Equals(selected.Project,
            WorkspacePaths.FileSystemPathComparison));
        return CaptureFSharpSemanticClosure(queries, snapshot.Health, owner, path, selected,
            contexts, cancellationToken, out failure);
    }

    private CapturedFSharpSemanticProject? CaptureFSharpSemanticClosure(
        IndexQueries queries,
        IndexHealth health,
        ProjectRow rootOwner,
        string targetPath,
        FSharpTypeCheckContext selected,
        List<FSharpTypeCheckContext> contexts,
        CancellationToken cancellationToken,
        out FSharpSemanticResult? failure)
    {
        failure = null;
        FSharpSemanticResult? capturedFailure = null;
        var nodes = new List<CapturedFSharpSemanticNode>();
        var completed = new Dictionary<string, int>(WorkspacePaths.FileSystemPathComparer);
        var active = new HashSet<string>(WorkspacePaths.FileSystemPathComparer);
        var assemblyOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var binaryReferences = new List<FSharpBinaryReferenceSnapshot>();
        var partialReasons = new SortedSet<string>(StringComparer.Ordinal);
        string? referenceSnapshotDirectory = null;
        bool ownershipTransferred = false;
        // Greg's pending aggregate-vs-per-node ruling is intentionally one switch for every
        // existing source, reference, import, property, and item-list counter in the closure.
        var budgetPolicy = new FSharpSemanticClosureBudgetPolicy(
            FSharpSemanticSourceFilesLimitForTest ?? MaxFSharpSemanticSourceFiles,
            FSharpSemanticSourceBytesLimitForTest ?? MaxFSharpSemanticSourceBytes,
            FSharpSemanticReferenceBytesLimitForTest ?? MaxFSharpSemanticReferenceBytes,
            ShareFSharpSemanticBudgetsAcrossProjectClosure);

        FSharpSemanticResult Failure(string error, string? nodeReasons = null,
            FSharpProjectReferenceFailure? projectReferenceFailure = null)
        {
            AddPartialReasons(partialReasons, nodeReasons);
            return new(null, error, selected, contexts,
                JoinPartialReasons(partialReasons), Health: health,
                ProjectReferenceFailure: projectReferenceFailure);
        }

        int? CaptureNode(ProjectRow owner, string? requiredSource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = $"{owner.Path}\0{selected.TargetFramework}";
            if (completed.TryGetValue(key, out int completedIndex)) return completedIndex;
            if (!active.Add(key))
            {
                capturedFailure = Failure("fsharp_semantic_project_reference_cycle");
                return null;
            }

            try
            {
                FSharpSemanticNodeBudgets nodeBudgets = budgetPolicy.ForNode();
                if (!owner.Language.Equals("fs", StringComparison.OrdinalIgnoreCase))
                {
                    capturedFailure = Failure("fsharp_semantic_project_references_unsupported");
                    return null;
                }
                if (owner.LoadStatus.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
                {
                    capturedFailure = Failure("fsharp_semantic_project_reference_unavailable");
                    return null;
                }

                string[] availableTargetFrameworks = owner.Tfms.Split(';',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tfm => tfm, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!availableTargetFrameworks.Contains(selected.TargetFramework,
                        StringComparer.OrdinalIgnoreCase))
                {
                    capturedFailure = Failure(
                        "fsharp_semantic_project_reference_target_framework_unavailable",
                        projectReferenceFailure: new(owner.Path,
                            availableTargetFrameworks.ToList()));
                    return null;
                }

                string? projectXml = queries.ContentByPathBounded(owner.Path,
                    IndexBuilder.MaxStructuralFileBytes, cancellationToken);
                if (projectXml is null)
                {
                    capturedFailure = Failure("fsharp_semantic_project_reference_unavailable");
                    return null;
                }

                DirectoryBuildAuthorityPaths directoryBuild =
                    queries.ApplicableDirectoryBuildAuthority(owner.Path);
                DirectoryPackagesAuthorityPath directoryPackages =
                    queries.ApplicableDirectoryPackagesAuthority(owner.Path);
                var evaluatedAuthorityInputs = new Dictionary<string, string>(
                    WorkspacePaths.FileSystemPathComparer);
                FSharpSemanticOptionsSnapshot options =
                    ProjectFileParser.ParseFSharpSemanticOptionsClosureSnapshot(
                        nodeBudgets.Evaluation, owner.Path, projectXml,
                        owner.Tfms, selected.TargetFramework, importPath =>
                        {
                            FileHit? imported = ResolveIndexedFSharpImport(queries, importPath);
                            string? content = imported is { Language: "config" } &&
                                              imported.Size <=
                                              ProjectFileParser.MaxFSharpSemanticImportBytes
                                ? queries.ContentByPathBounded(imported.Path,
                                    ProjectFileParser.MaxFSharpSemanticImportBytes,
                                    cancellationToken)
                                : null;
                            if (imported is not null && content is not null)
                                evaluatedAuthorityInputs[imported.Path] = content;
                            return content;
                        }, importPath =>
                        {
                            FileHit? imported = ResolveIndexedFSharpImport(queries, importPath);
                            return imported is { Language: "config" } ? imported.Size : null;
                        }, directoryPackagesPropsPath: directoryPackages.Path,
                        directoryBuildPropsPath: directoryBuild.PropsPath,
                        directoryBuildTargetsPath: directoryBuild.TargetsPath,
                        cancellationToken: cancellationToken,
                        hasAmbiguousDirectoryBuildAuthority: directoryBuild.HasAmbiguity,
                        hasAmbiguousDirectoryPackagesAuthority: directoryPackages.PathAmbiguous);
                AddPartialReasons(partialReasons, options.PartialReason);
                if (options.Error is { } optionError)
                {
                    capturedFailure = Failure(optionError, options.PartialReason);
                    return null;
                }
                if (requiredSource is not null && !options.SourceFiles.Contains(requiredSource,
                        WorkspacePaths.FileSystemPathComparer))
                {
                    capturedFailure = Failure("fsharp_semantic_target_not_in_project",
                        options.PartialReason);
                    return null;
                }

                var fullSourcePaths = new List<string>(options.SourceFiles.Count);
                var sourceTexts = new List<string>(options.SourceFiles.Count);
                var sourceGenerated = new List<bool>(options.SourceFiles.Count);
                if (!nodeBudgets.Content.TryReserveSources(options.SourceFiles.Count, 0,
                        out string? sourceLimitError))
                {
                    capturedFailure = Failure(sourceLimitError!, options.PartialReason);
                    return null;
                }
                foreach (string sourcePath in options.SourceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryWorkspaceAbsolutePath(sourcePath, out string? fullSourcePath) ||
                        queries.FileByPathForHost(sourcePath) is not { Language: "fs" } sourceFile ||
                        sourceFile.Size > IndexBuilder.MaxStructuralFileBytes)
                    {
                        capturedFailure = Failure("fsharp_semantic_source_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    BeforeFSharpSemanticSourceReadForTest?.Invoke(sourceFile.Path);
                    string? text = queries.ContentByPathBounded(sourceFile.Path,
                        IndexBuilder.MaxStructuralFileBytes, cancellationToken);
                    if (text is null)
                    {
                        capturedFailure = Failure("fsharp_semantic_source_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    int sourceBytes = System.Text.Encoding.UTF8.GetByteCount(text);
                    if (!nodeBudgets.Content.TryReserveSources(0, sourceBytes,
                            out sourceLimitError))
                    {
                        capturedFailure = Failure(sourceLimitError!, options.PartialReason);
                        return null;
                    }
                    fullSourcePaths.Add(fullSourcePath!);
                    sourceTexts.Add(text);
                    sourceGenerated.Add(sourceFile.IsGenerated);
                }

                List<string> bareReferences = options.BareReferences ?? [];
                List<FSharpPackageReferenceSnapshot> packageReferences =
                    options.PackageReferences ?? [];
                if (!TryResolveFSharpPackageAssets(owner.Path, projectXml,
                        selected.TargetFramework, packageReferences, evaluatedAuthorityInputs,
                        cancellationToken, out FSharpPackageAssetsSnapshot? packageAssets,
                        out string? packageError))
                {
                    capturedFailure = Failure(packageError!, options.PartialReason);
                    return null;
                }
                FSharpPackageAssetsSnapshot resolvedPackageAssets = packageAssets!;
                int referenceInputCount;
                try
                {
                    referenceInputCount = checked(options.HintPathReferences.Count +
                        bareReferences.Count + resolvedPackageAssets.CompileAssets.Count);
                }
                catch (OverflowException)
                {
                    referenceInputCount = int.MaxValue;
                }
                if (!nodeBudgets.Content.TryReserveReferenceInputs(referenceInputCount))
                {
                    capturedFailure = Failure("fsharp_semantic_reference_limit",
                        options.PartialReason);
                    return null;
                }

                if (options.AssemblyName.Length == 0 || options.AssemblyName.Length > 180 ||
                    options.AssemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    options.AssemblyName.Contains('/') || options.AssemblyName.Contains('\\'))
                {
                    capturedFailure = Failure("fsharp_semantic_assembly_name_unavailable",
                        options.PartialReason);
                    return null;
                }
                if (assemblyOwners.TryGetValue(options.AssemblyName,
                        out string? existingAssemblyOwner) &&
                    !existingAssemblyOwner.Equals(owner.Path,
                        WorkspacePaths.FileSystemPathComparison))
                {
                    capturedFailure = Failure("fsharp_project_options_conflict", options.PartialReason);
                    return null;
                }
                assemblyOwners[options.AssemblyName] = owner.Path;

                var childIndices = new List<int>(options.ProjectReferences.Count);
                foreach (FSharpProjectReferenceSnapshot projectReference in
                         options.ProjectReferences)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProjectRow? child = queries.ProjectByPathForHost(
                        projectReference.ProjectPath);
                    if (child is null)
                    {
                        capturedFailure = Failure("fsharp_semantic_project_reference_unavailable");
                        return null;
                    }
                    if (!child.Language.Equals("fs", StringComparison.OrdinalIgnoreCase))
                    {
                        capturedFailure = Failure("fsharp_semantic_project_references_unsupported");
                        return null;
                    }
                    int? childIndex = CaptureNode(child, requiredSource: null);
                    if (childIndex is null) return null;
                    if (!childIndices.Contains(childIndex.Value))
                        childIndices.Add(childIndex.Value);
                }
                var descendantIndices = new SortedSet<int>();
                foreach (int childIndex in childIndices)
                {
                    descendantIndices.Add(childIndex);
                    descendantIndices.UnionWith(nodes[childIndex].DescendantProjectIndices);
                }
                int[] compilerReferenceIndices = options.ProjectReferencesTransitive
                    ? descendantIndices.ToArray()
                    : childIndices.Order().ToArray();

                var referencePaths = new List<string>();
                var referenceIdentities = new List<string>();
                IReadOnlyList<string> frameworkReferences =
                    ReferenceAssemblyLocator.FrameworkReferencePaths(selected.TargetFramework,
                        out string? frameworkDirectory);
                if (frameworkDirectory is null || frameworkReferences.Count == 0)
                {
                    capturedFailure = Failure("fsharp_framework_references_unavailable",
                        options.PartialReason);
                    return null;
                }
                var frameworkAssemblyNames = frameworkReferences
                    .Select(Path.GetFileNameWithoutExtension)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string bareReference in bareReferences)
                {
                    if (!bareReference.Equals("FSharp.Core",
                            StringComparison.OrdinalIgnoreCase) &&
                        !frameworkAssemblyNames.Contains(bareReference))
                    {
                        capturedFailure = Failure("fsharp_semantic_reference_unresolved",
                            options.PartialReason);
                        return null;
                    }
                }
                referencePaths.AddRange(frameworkReferences);
                referenceIdentities.AddRange(frameworkReferences.Select(ReferenceIdentity));

                bool hasExplicitFSharpCore = options.HintPathReferences.Any(reference =>
                                                 Path.GetFileName(reference).Equals(
                                                     "FSharp.Core.dll",
                                                     StringComparison.OrdinalIgnoreCase)) ||
                                             resolvedPackageAssets.CompileAssets.Any(reference =>
                                                 Path.GetFileName(reference.FullPath).Equals(
                                                     "FSharp.Core.dll",
                                                     StringComparison.OrdinalIgnoreCase));
                if (!hasExplicitFSharpCore)
                {
                    string? fsharpCore = ReferenceAssemblyLocator.FSharpCoreReferencePath(
                        selected.TargetFramework, out bool exactTargetAsset);
                    if (fsharpCore is null)
                    {
                        capturedFailure = Failure("fsharp_core_reference_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    referencePaths.Add(fsharpCore);
                    referenceIdentities.Add(ReferenceIdentity(fsharpCore));
                    partialReasons.Add("fsharp_core_reference_defaulted");
                    if (!exactTargetAsset)
                        partialReasons.Add("fsharp_core_reference_host_fallback");
                }
                if (options.HintPathReferences.Count > 0)
                    partialReasons.Add("fsharp_binary_references_snapshotted");
                if (packageReferences.Count > 0)
                    partialReasons.Add("fsharp_package_references_snapshotted");

                if (options.HintPathReferences.Count > 0 ||
                    resolvedPackageAssets.CompileAssets.Count > 0)
                {
                    referenceSnapshotDirectory ??= Directory.CreateTempSubdirectory(
                        "PhoenixCodeNav.FSharp.Reference.").FullName;
                }
                foreach (string hintPath in options.HintPathReferences)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FSharpBinaryReferenceSnapshot? binary = CaptureFSharpBinaryReference(
                        hintPath, referenceSnapshotDirectory!, binaryReferences.Count,
                        nodeBudgets.Content.RemainingReferenceBytes, cancellationToken,
                        out bool bytesExceeded);
                    if (binary is null)
                    {
                        capturedFailure = Failure(bytesExceeded
                                ? "fsharp_semantic_reference_bytes_limit"
                                : "fsharp_semantic_reference_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    binaryReferences.Add(binary);
                    if (!nodeBudgets.Content.TryReserveReferenceBytes(binary.Length))
                    {
                        capturedFailure = Failure("fsharp_semantic_reference_bytes_limit",
                            options.PartialReason);
                        return null;
                    }
                    referencePaths.Add(binary.SnapshotFullPath);
                    referenceIdentities.Add(
                        $"{binary.SourceIdentity}|{binary.Length}|{binary.Sha256}");
                }
                foreach (FSharpPackageCompileAsset packageAsset in
                         resolvedPackageAssets.CompileAssets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FSharpBinaryReferenceSnapshot? binary = CaptureFSharpBinaryReference(
                        packageAsset.SourceIdentity, packageAsset.FullPath,
                        packageAsset.PackageRoot, referenceSnapshotDirectory!,
                        binaryReferences.Count, nodeBudgets.Content.RemainingReferenceBytes,
                        cancellationToken, out bool bytesExceeded);
                    if (binary is null)
                    {
                        capturedFailure = Failure(bytesExceeded
                                ? "fsharp_semantic_reference_bytes_limit"
                                : "fsharp_semantic_package_asset_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    binaryReferences.Add(binary);
                    if (!nodeBudgets.Content.TryReserveReferenceBytes(binary.Length))
                    {
                        capturedFailure = Failure("fsharp_semantic_reference_bytes_limit",
                            options.PartialReason);
                        return null;
                    }
                    referencePaths.Add(binary.SnapshotFullPath);
                    referenceIdentities.Add(
                        $"{binary.SourceIdentity}|{binary.Length}|{binary.Sha256}");
                }
                if (resolvedPackageAssets.Identity.Length > 0)
                    referenceIdentities.Add(resolvedPackageAssets.Identity);
                foreach (int childIndex in childIndices)
                {
                    CapturedFSharpSemanticNode child = nodes[childIndex];
                    referenceIdentities.Add(
                        $"project:{child.ProjectPath}|{child.Fingerprint}");
                }

                referencePaths = referencePaths
                    .Distinct(WorkspacePaths.FileSystemPathComparer)
                    .OrderBy(reference => reference, WorkspacePaths.FileSystemPathComparer)
                    .ToList();
                string fingerprint = FSharpSemanticFingerprint(owner.Path,
                    selected.TargetFramework, projectXml, options.CommandLineArgs,
                    fullSourcePaths, sourceTexts, referenceIdentities);
                string outputPath = Path.Combine(Path.GetTempPath(),
                    "PhoenixCodeNav.FSharp", fingerprint, $"{options.AssemblyName}.dll");
                var commandLineArgs = new List<string>
                {
                    "--simpleresolution",
                    "--noframework",
                    "--target:library",
                    selected.TargetFramework.Equals("net472",
                        StringComparison.OrdinalIgnoreCase)
                        ? "--targetprofile:mscorlib"
                        : "--targetprofile:netcore",
                    "--debug:portable",
                    "--optimize-",
                    $"--out:{outputPath}",
                };
                commandLineArgs.AddRange(options.CommandLineArgs);
                commandLineArgs.AddRange(compilerReferenceIndices.Select(childIndex =>
                    $"-r:{nodes[childIndex].Input.OutputFile}"));
                commandLineArgs.AddRange(referencePaths.Select(reference => $"-r:{reference}"));
                commandLineArgs.AddRange(fullSourcePaths);

                var input = new SemanticProjectInput(WorkspaceAbsolutePath(owner.Path),
                    fullSourcePaths.ToArray(), sourceTexts.ToArray(),
                    commandLineArgs.ToArray(), outputPath, compilerReferenceIndices);
                int nodeIndex = nodes.Count;
                nodes.Add(new(input, owner.Path, fingerprint, sourceGenerated.ToArray(),
                    options.PartialReason, owner.IsTest, descendantIndices.ToArray()));
                completed[key] = nodeIndex;
                return nodeIndex;
            }
            finally
            {
                active.Remove(key);
            }
        }

        try
        {
            int? rootProjectIndex = CaptureNode(rootOwner, targetPath);
            if (rootProjectIndex is null)
            {
                failure = capturedFailure;
                return null;
            }
            CapturedFSharpSemanticNode root = nodes[rootProjectIndex.Value];
            string targetFileName = WorkspaceAbsolutePath(targetPath);
            var captured = new CapturedFSharpSemanticProject(
                nodes.Select(node => node.Input).ToArray(), rootProjectIndex.Value,
                root.SourceGenerated,
                nodes.SelectMany(node => node.Input.SourceFiles)
                    .Distinct(WorkspacePaths.FileSystemPathComparer).ToArray(),
                root.Fingerprint, targetFileName, selected, contexts, binaryReferences,
                referenceSnapshotDirectory, JoinPartialReasons(partialReasons), root.IsTest,
                health);
            ownershipTransferred = true;
            return captured;
        }
        finally
        {
            if (!ownershipTransferred)
                CleanupFSharpReferenceSnapshots(referenceSnapshotDirectory, binaryReferences);
        }
    }

    private FileHit? ResolveIndexedFSharpImport(IndexQueries queries, string importPath)
    {
        return queries.FileByPathForHost(importPath);
    }

    private bool TryWorkspaceAbsolutePath(string relativePath, out string? fullPath,
        bool rejectReparsePoints = false)
    {
        try
        {
            string root = Path.GetFullPath(_manager.WorkspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            {
                fullPath = null;
                return false;
            }
            if (rejectReparsePoints)
            {
                string cursor = root;
                string relative = Path.GetRelativePath(root, candidate);
                foreach (string part in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    cursor = Path.Combine(cursor, part);
                    if (!File.Exists(cursor) && !Directory.Exists(cursor)) continue;
                    if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    {
                        fullPath = null;
                        return false;
                    }
                }
            }
            fullPath = candidate;
            return true;
        }
        catch
        {
            fullPath = null;
            return false;
        }
    }

    private string WorkspaceAbsolutePath(string relativePath) =>
        TryWorkspaceAbsolutePath(relativePath, out string? fullPath)
            ? fullPath!
            : throw new InvalidDataException("workspace-relative path escaped the workspace");

    internal static bool TryAccumulateFSharpSemanticReferenceBytes(ref long totalBytes,
        long nextLength)
    {
        if (totalBytes < 0 || nextLength <= 0 ||
            totalBytes > MaxFSharpSemanticReferenceBytes ||
            nextLength > MaxFSharpSemanticReferenceBytes - totalBytes)
            return false;
        totalBytes += nextLength;
        return true;
    }

    internal static bool TryCopyFSharpReferenceStream(Stream source, Stream destination,
        long expectedLength, long maximumLength, CancellationToken cancellationToken,
        out long copiedLength, out string? sha256, out bool lengthLimitExceeded)
    {
        copiedLength = 0;
        sha256 = null;
        lengthLimitExceeded = false;
        if (expectedLength <= 0 || maximumLength <= 0 || expectedLength > maximumLength)
        {
            lengthLimitExceeded = expectedLength > maximumLength;
            return false;
        }

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read > maximumLength - copiedLength)
            {
                lengthLimitExceeded = true;
                return false;
            }
            if (read > expectedLength - copiedLength) return false;
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            copiedLength += read;
        }

        if (copiedLength != expectedLength || source.Length != expectedLength) return false;
        sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return true;
    }

    private static string ReferenceIdentity(string path)
    {
        var info = new FileInfo(path);
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private FSharpBinaryReferenceSnapshot? CaptureFSharpBinaryReference(
        string relativePath,
        string snapshotDirectory,
        int snapshotIndex,
        long maximumLength,
        CancellationToken cancellationToken,
        out bool lengthLimitExceeded)
    {
        if (!TryWorkspaceAbsolutePath(relativePath, out string? fullPath,
                rejectReparsePoints: true))
        {
            lengthLimitExceeded = false;
            return null;
        }
        string workspaceRoot = Path.GetFullPath(_manager.WorkspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return CaptureFSharpBinaryReference(relativePath, fullPath!, workspaceRoot,
            snapshotDirectory, snapshotIndex, maximumLength, cancellationToken,
            out lengthLimitExceeded);
    }

    private FSharpBinaryReferenceSnapshot? CaptureFSharpBinaryReference(
        string sourceIdentity,
        string fullPath,
        string allowedRoot,
        string snapshotDirectory,
        int snapshotIndex,
        long maximumLength,
        CancellationToken cancellationToken,
        out bool lengthLimitExceeded)
    {
        lengthLimitExceeded = false;
        string? snapshotPath = null;
        try
        {
            if (!TryOpenVerifiedReference(sourceIdentity, fullPath, allowedRoot,
                    ".dll", out FileStream? stream))
                return null;
            using (FileStream verifiedStream = stream!)
            {
                long expectedLength = verifiedStream.Length;
                if (expectedLength <= 0) return null;
                if (expectedLength > maximumLength)
                {
                    lengthLimitExceeded = true;
                    return null;
                }
                using (var pe = new System.Reflection.PortableExecutable.PEReader(verifiedStream,
                           System.Reflection.PortableExecutable.PEStreamOptions.LeaveOpen))
                {
                    if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly) return null;
                }
                verifiedStream.Position = 0;
                snapshotPath = Path.Combine(snapshotDirectory,
                    $"{snapshotIndex:D3}-{Path.GetFileName(fullPath)}");
                using var destination = new FileStream(snapshotPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read, 64 * 1024,
                    FileOptions.SequentialScan | FileOptions.WriteThrough);
                FSharpReferenceSnapshotCreatedForTest?.Invoke(snapshotPath);
                if (!TryCopyFSharpReferenceStream(verifiedStream, destination,
                        expectedLength, maximumLength, cancellationToken,
                        out long copiedLength, out string? sha256,
                        out bool copyLengthLimitExceeded))
                {
                    lengthLimitExceeded = copyLengthLimitExceeded;
                    throw new InvalidDataException("reference changed during capture");
                }
                destination.Flush(flushToDisk: true);
                return new(sourceIdentity, Path.GetFullPath(fullPath),
                    Path.GetFullPath(allowedRoot), snapshotPath, copiedLength, sha256!);
            }
        }
        catch (OperationCanceledException)
        {
            if (snapshotPath is not null)
            {
                try { File.Delete(snapshotPath); } catch { }
            }
            throw;
        }
        catch
        {
            if (snapshotPath is not null)
            {
                try { File.Delete(snapshotPath); } catch { }
            }
            return null;
        }
    }

    private bool VerifyFSharpBinaryReferences(
        IReadOnlyList<FSharpBinaryReferenceSnapshot> references,
        CancellationToken cancellationToken)
    {
        foreach (FSharpBinaryReferenceSnapshot expected in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryOpenVerifiedReference(expected.SourceIdentity,
                    expected.OriginalFullPath, expected.AllowedRootFullPath, ".dll",
                    out FileStream? stream))
                return false;
            try
            {
                using (FileStream verifiedStream = stream!)
                {
                    if (verifiedStream.Length != expected.Length ||
                        !HashFSharpReference(verifiedStream, cancellationToken).Equals(
                            expected.Sha256, StringComparison.Ordinal))
                        return false;
                }
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
        return true;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private bool TryOpenVerifiedReference(string sourceIdentity, string fullPath,
        string allowedRoot, string requiredExtension, out FileStream? stream)
    {
        stream = null;
        if (!TryNormalizeContainedPath(fullPath, allowedRoot, out string? normalizedPath) ||
            !File.Exists(normalizedPath) ||
            !Path.GetExtension(normalizedPath).Equals(requiredExtension,
                StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            BeforeFSharpReferenceOpenForTest?.Invoke(sourceIdentity);
            stream = new FileStream(normalizedPath!, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            bool openedPathMatches = OpenedHandleMatchesPath(stream.SafeFileHandle,
                normalizedPath!);
            if (!openedPathMatches)
            {
                stream.Dispose();
                stream = null;
                return false;
            }
            return true;
        }
        catch
        {
            stream?.Dispose();
            stream = null;
            return false;
        }
    }

    private static bool TryNormalizeContainedPath(string path, string allowedRoot,
        out string? normalizedPath)
    {
        normalizedPath = null;
        try
        {
            string root = Path.GetFullPath(allowedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(path);
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                return false;

            string? volumeRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(volumeRoot)) return false;
            string cursor = volumeRoot;
            string relative = Path.GetRelativePath(volumeRoot, candidate);
            foreach (string part in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, part);
                if (!File.Exists(cursor) && !Directory.Exists(cursor)) return false;
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            normalizedPath = candidate;
            return true;
        }
        catch
        {
            normalizedPath = null;
            return false;
        }
    }

    private static bool TryGetFinalPath(SafeFileHandle handle, out string? path)
    {
        path = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var buffer = new System.Text.StringBuilder(32_768);
                uint length = GetFinalPathNameByHandle(handle, buffer,
                    (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return false;
                string value = buffer.ToString();
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    value = @"\\" + value[8..];
                else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    value = value[4..];
                path = Path.GetFullPath(value);
                return true;
            }
            if (OperatingSystem.IsLinux())
            {
                string descriptor = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
                FileSystemInfo? target = new FileInfo(descriptor)
                    .ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) return false;
                path = Path.GetFullPath(target.FullName);
                return true;
            }
            // Reference semantics fail closed on platforms where an opened handle cannot be
            // resolved authoritatively.
            return false;
        }
        catch
        {
            path = null;
            return false;
        }
    }

    private static bool OpenedHandleMatchesPath(SafeFileHandle handle, string expectedPath)
    {
        if (!OperatingSystem.IsMacOS())
            return TryGetFinalPath(handle, out string? openedPath) &&
                   Path.GetFullPath(openedPath!).Equals(expectedPath, PathComparison);

        const int bufferSize = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int length = proc_pidfdinfo(Environment.ProcessId,
                handle.DangerousGetHandle().ToInt32(), 2, buffer, bufferSize);
            if (length <= 0 || length > bufferSize) return false;
            byte[] actual = new byte[length];
            Marshal.Copy(buffer, actual, 0, length);
            return MacOsVnodePathMatches(actual, expectedPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool MacOsVnodePathMatches(ReadOnlySpan<byte> vnodeFdInfoWithPath,
        string expectedPath)
    {
        // vnode_fdinfowithpath ends with vnode_info_path.vip_path[MAXPATHLEN]. Decode that
        // single ABI field; scanning the whole structure would accept an attacker-controlled
        // longer path that merely contains the expected absolute path as a suffix.
        const int maxPathLength = 1024;
        if (vnodeFdInfoWithPath.Length < maxPathLength) return false;
        ReadOnlySpan<byte> pathField = vnodeFdInfoWithPath[^maxPathLength..];
        int terminator = pathField.IndexOf((byte)0);
        if (terminator <= 0) return false;
        try
        {
            byte[] expected = System.Text.Encoding.UTF8.GetBytes(
                Path.GetFullPath(expectedPath));
            return pathField[..terminator].SequenceEqual(expected);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,
        System.Text.StringBuilder path, uint pathLength, uint flags);

    [DllImport("/usr/lib/libproc.dylib", SetLastError = true)]
    private static extern int proc_pidfdinfo(int processId, int descriptor,
        int flavor, IntPtr buffer, int bufferSize);

    private void CleanupFSharpReferenceSnapshots(
        CapturedFSharpSemanticProject captured) =>
        CleanupFSharpReferenceSnapshots(captured.ReferenceSnapshotDirectory,
            captured.BinaryReferences);

    private void CleanupFSharpReferenceSnapshots(string? directory,
        IEnumerable<FSharpBinaryReferenceSnapshot> references)
    {
        int failures = 0;
        foreach (FSharpBinaryReferenceSnapshot reference in references)
        {
            try { File.Delete(reference.SnapshotFullPath); } catch { failures++; }
        }
        if (directory is not null)
        {
            try
            {
                string expectedParent = Path.GetFullPath(Path.GetTempPath())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(directory);
                if (full.StartsWith(expectedParent + Path.DirectorySeparatorChar, PathComparison) &&
                    Path.GetFileName(full).StartsWith("PhoenixCodeNav.FSharp.Reference.",
                        StringComparison.Ordinal))
                    Directory.Delete(full, recursive: false);
            }
            catch { failures++; }
        }
        if (failures > 0)
            _log($"F# reference snapshot cleanup incomplete: {failures} item(s)");
    }

    private static string HashFSharpReference(Stream stream,
        CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FSharpSemanticFingerprint(
        string projectPath,
        string targetFramework,
        string projectXml,
        IReadOnlyList<string> optionArgs,
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> sourceTexts,
        IReadOnlyList<string> referenceIdentities)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        Add(projectPath);
        Add(targetFramework);
        Add(projectXml);
        foreach (string arg in optionArgs) Add(arg);
        for (int index = 0; index < sourcePaths.Count; index++)
        {
            Add(sourcePaths[index]);
            Add(sourceTexts[index]);
        }
        foreach (string identity in referenceIdentities.OrderBy(value => value,
                     WorkspacePaths.FileSystemPathComparer))
            Add(identity);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
    }

    private FSharpSemanticDiagnostic MapFSharpDiagnostic(
        CodeNav.FSharp.SemanticDiagnostic diagnostic,
        HashSet<string> sourcePaths)
    {
        string? mappedPath = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(diagnostic.FileName))
            {
                string full = Path.GetFullPath(diagnostic.FileName);
                if (sourcePaths.Contains(full)) mappedPath = ToRelPath(full);
            }
        }
        catch { }
        return new(
            diagnostic.Severity,
            diagnostic.Code,
            SanitizeFSharpDiagnosticMessage(diagnostic.Message),
            mappedPath,
            mappedPath is null ? null : diagnostic.StartLine,
            mappedPath is null ? null : diagnostic.StartColumn + 1,
            mappedPath is null ? null : diagnostic.EndLine,
            mappedPath is null ? null : diagnostic.EndColumn + 1);
    }

    private string SanitizeFSharpDiagnosticMessage(string message)
    {
        string sanitized = message;
        string[] privateRoots =
        [
            _manager.WorkspaceRoot,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.GetTempPath(),
        ];
        foreach (string root in privateRoots.Where(root => !string.IsNullOrWhiteSpace(root))
                     .Distinct(WorkspacePaths.FileSystemPathComparer)
                     .OrderByDescending(root => root.Length))
        {
            sanitized = sanitized.Replace(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                "<path>", PathComparison);
        }
        // Messages are already capped by the F# adapter. This final conservative pass catches
        // absolute paths outside the known roots without exposing host-specific directories.
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized,
            @"(?i)(?:[a-z]:[\\/]|/(?:home|Users|tmp|var|opt|usr)/)[^\r\n,;\""']+",
            "<path>");
        return sanitized.Length <= 320 ? sanitized : sanitized[..320];
    }

    private static string AppendPartialReason(string? reasons, string reason)
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);
        AddPartialReasons(all, reasons);
        all.Add(reason);
        return JoinPartialReasons(all)!;
    }

    private static string? JoinPartialReasons(SortedSet<string> reasons) =>
        reasons.Count == 0 ? null : string.Join(';', reasons);

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

    private static void AddPartialReasons(SortedSet<string> destination, string? reasons)
    {
        if (reasons is not { Length: > 0 }) return;
        foreach (string reason in reasons.Split(';', StringSplitOptions.RemoveEmptyEntries))
            destination.Add(reason);
    }
}
