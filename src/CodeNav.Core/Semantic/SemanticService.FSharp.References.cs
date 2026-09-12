using CodeNav.Core.Indexing;
using CodeNav.FSharp;

namespace CodeNav.Core.Semantic;

public sealed record FSharpReferenceSample(
    string Path,
    int Line,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string LineText);

public sealed record FSharpReferenceGroup(
    string Project,
    List<string> TargetFrameworksScanned,
    bool IsTest,
    int Count,
    List<FSharpReferenceSample> Samples,
    string Status = "scanned",
    string? Reason = null);

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
    FSharpProjectReferenceFailure? ProjectReferenceFailure = null,
    List<FSharpReferenceGroup>? Groups = null,
    FSharpReferencesCoverage? Coverage = null);

public sealed partial class SemanticService
{

    private sealed record FSharpReferenceProjectScan(
        FSharpReferenceGroup Group,
        bool Complete,
        bool Inactive,
        bool Filtered,
        int DiagnosticCount,
        List<FSharpSemanticDiagnostic> Diagnostics,
        string? PartialReason);

    /// <summary>
    /// Enumerates compiler-bound non-definition uses inside one selected physical F# project and
    /// target framework, then scans every proven workspace-dependent F# context under the same
    /// request snapshot and deadline. Counts remain independent from bounded response samples.
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
        int timeoutMs, FSharpSemanticTimingBox? timing = null)
    {
        timing ??= new();
        if (line < 1 || column < 0)
            return new(null, null, [], "fsharp_semantic_position_invalid", null, []);
        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120_000));
        var captureSession = CreateFSharpSemanticCaptureSession();
        CapturedFSharpSemanticProject? captured = null;
        IndexReadSnapshot? snapshot = null;
        bool entered = false;
        bool rootReady = false;
        try
        {
            using (timing.Admission())
                await _fsharpSemanticGate.WaitAsync(cts.Token).ConfigureAwait(false);
            entered = true;
            using IDisposable captureTiming = timing.Capture();
            snapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (snapshot is null)
                return new(null, null, [], "index_snapshot_unavailable", null, []);
            captured = CaptureFSharpSemanticProject(snapshot, path, projectPath, targetFramework,
                cts.Token, out FSharpSemanticResult? failure, captureSession);
            if (captured is null) return FSharpReferencesFailure(failure!);
            captureTiming.Dispose();
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
                timing, cts.Token).ConfigureAwait(false);
            FSharpSemanticCheckCompletedForTest?.Invoke(check.Error);
            rootReady = check.Symbol is not null;

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

            int sampleLimit = Math.Clamp(samplesPerGroup, 0, 10);

            static List<(CodeNav.FSharp.SemanticLocation Location, int SourceIndex)>
                AcceptedReferences(CapturedFSharpSemanticProject project,
                    SemanticCheckResult resolved, bool allowTests, bool allowGenerated)
            {
                var sourceIndex = project.SourceFiles
                    .Select((source, index) => (source, index))
                    .ToDictionary(pair => pair.source, pair => pair.index,
                        WorkspacePaths.FileSystemPathComparer);
                var accepted = new List<(CodeNav.FSharp.SemanticLocation, int)>();
                if (!allowTests && project.SelectedProjectIsTest) return accepted;
                foreach (CodeNav.FSharp.SemanticLocation reference in resolved.References)
                {
                    string fullPath = Path.GetFullPath(reference.FileName);
                    if (!sourceIndex.TryGetValue(fullPath, out int index)) continue;
                    if (!allowGenerated && project.SourceGenerated[index]) continue;
                    accepted.Add((reference, index));
                }
                return accepted;
            }

            FSharpReferenceSample Sample(CapturedFSharpSemanticProject project,
                CodeNav.FSharp.SemanticLocation reference, int index)
            {
                Microsoft.CodeAnalysis.Text.SourceText text =
                    Microsoft.CodeAnalysis.Text.SourceText.From(project.SourceTexts[index]);
                int lineIndex = reference.StartLine - 1;
                string lineText = (uint)lineIndex < (uint)text.Lines.Count
                    ? Truncate(text.Lines[lineIndex].ToString().Trim())
                    : "";
                return new(
                    ToRelPath(reference.FileName),
                    reference.StartLine,
                    reference.StartColumn + 1,
                    reference.EndLine,
                    reference.EndColumn + 1,
                    lineText);
            }

            static bool HasGeneratedFilteredReferences(
                CapturedFSharpSemanticProject project, SemanticCheckResult resolved)
            {
                if (resolved.References.Length == 0) return false;
                var sourceIndex = project.SourceFiles
                    .Select((source, index) => (source, index))
                    .ToDictionary(pair => pair.source, pair => pair.index,
                        WorkspacePaths.FileSystemPathComparer);
                return resolved.References.Any(reference =>
                {
                    string fullPath = Path.GetFullPath(reference.FileName);
                    return sourceIndex.TryGetValue(fullPath, out int index) &&
                           project.SourceGenerated[index];
                });
            }

            FSharpReferenceDefinition? definition = FindFSharpReferenceDefinition(captured, check);
            var countedSites = new HashSet<string>(StringComparer.Ordinal);

            static string SiteKey(CodeNav.FSharp.SemanticLocation location)
            {
                string fullPath = Path.GetFullPath(location.FileName);
                return $"{WorkspacePaths.ToGitPath(fullPath)}\0" +
                       $"{location.StartLine}\0{location.StartColumn}\0" +
                       $"{location.EndLine}\0{location.EndColumn}";
            }

            async Task<FSharpReferenceProjectScan> ScanProjectAsync(ProjectRow project,
                IReadOnlyList<string> targetFrameworks, bool requireDeclaringProject)
            {
                if (!includeTests && project.IsTest)
                {
                    return new(new(project.Path, [], true, 0, [], "filtered",
                        "test_project"), true, false, true, 0, [], null);
                }

                if (targetFrameworks.Count == 0)
                {
                    const string reason = "fsharp_type_check_context_unavailable";
                    return new(new(project.Path, [], project.IsTest, 0, [], "failed", reason),
                        false, false, false, 0, [], null);
                }

                var applicableTargetFrameworks = new List<string>();
                var sites = new Dictionary<string,
                    (CapturedFSharpSemanticProject Project,
                        CodeNav.FSharp.SemanticLocation Location, int SourceIndex)>(
                    StringComparer.Ordinal);
                var failures = new List<string>();
                int projectDiagnosticCount = 0;
                var projectDiagnostics = new List<FSharpSemanticDiagnostic>();
                string? projectPartialReason = null;
                bool generatedReferencesFiltered = false;

                foreach (string projectTargetFramework in targetFrameworks)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    CapturedFSharpSemanticProject? projectCapture =
                        CaptureFSharpSemanticProjectContext(snapshot, project,
                            projectTargetFramework, cts.Token,
                            out FSharpSemanticResult? projectCaptureFailure, captureSession, timing);
                    if (projectCapture is null)
                    {
                        failures.Add(projectCaptureFailure?.Error ??
                            "fsharp_semantic_snapshot_failed");
                        continue;
                    }
                    int lookupProjectIndex = Array.FindIndex(projectCapture.Nodes, node =>
                        node.ProjectPath.Equals(definition!.ProjectPath,
                            WorkspacePaths.FileSystemPathComparison) &&
                        node.AssemblyName.Equals(definition.AssemblyName,
                            StringComparison.OrdinalIgnoreCase));
                    if (lookupProjectIndex < 0)
                    {
                        if (requireDeclaringProject)
                            failures.Add("fsharp_workspace_declaring_project_not_in_closure");
                        continue;
                    }

                    SemanticCheckResult projectCheck = await
                        SemanticResolver.ResolveReferencesForProjectAsync(
                            projectCapture.Projects, projectCapture.RootProjectIndex,
                            lookupProjectIndex, projectCapture.Fingerprint,
                            projectCapture.BinaryReferences.Count == 0,
                            definition.FullPath, definition.Line, definition.Column,
                            MaxFSharpSemanticLineOnlySourceChars, timing, cts.Token)
                        .ConfigureAwait(false);
                    FSharpSemanticCheckCompletedForTest?.Invoke(projectCheck.Error);
                    if (projectCheck.Symbol is null || projectCheck.Error is not null ||
                        !string.Equals(projectCheck.Symbol.FullName, check.Symbol.FullName,
                            StringComparison.Ordinal) ||
                        !string.Equals(projectCheck.Symbol.Assembly, check.Symbol.Assembly,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add(projectCheck.Error ??
                            "fsharp_semantic_symbol_identity_changed");
                        continue;
                    }
                    if (!VerifyFSharpBinaryReferences(projectCapture.BinaryReferences,
                            cts.Token))
                    {
                        failures.Add("fsharp_semantic_reference_changed");
                        continue;
                    }
                    applicableTargetFrameworks.Add(projectTargetFramework);

                    if (projectCapture.PartialReason is not null)
                        projectPartialReason = AppendPartialReason(projectPartialReason,
                            projectCapture.PartialReason);
                    if (projectCheck.ErrorDiagnosticCount > 0)
                    {
                        projectPartialReason = AppendPartialReason(projectPartialReason,
                            "fsharp_semantic_diagnostics_present");
                    }
                    projectDiagnosticCount += projectCheck.DiagnosticCount;
                    var projectSourcePaths = projectCapture.ClosureSourceFiles.ToHashSet(
                        WorkspacePaths.FileSystemPathComparer);
                    projectDiagnostics.AddRange(projectCheck.Diagnostics.Select(value =>
                        MapFSharpDiagnostic(value, projectSourcePaths)));
                    generatedReferencesFiltered |= !includeGenerated &&
                                                   HasGeneratedFilteredReferences(
                                                       projectCapture, projectCheck);
                    foreach (var accepted in AcceptedReferences(projectCapture, projectCheck,
                                 includeTests, includeGenerated))
                    {
                        string key = SiteKey(accepted.Location);
                        if (countedSites.Contains(key)) continue;
                        sites.TryAdd(key, (projectCapture, accepted.Location,
                            accepted.SourceIndex));
                    }
                }

                string? failure = failures.FirstOrDefault();
                if (applicableTargetFrameworks.Count == 0 && failure is null)
                {
                    const string reason = "inactive_project_reference";
                    return new(new(project.Path, [], project.IsTest, 0, [], "excluded", reason),
                        true, true, false, projectDiagnosticCount, projectDiagnostics,
                        projectPartialReason);
                }

                List<(CapturedFSharpSemanticProject Project,
                    CodeNav.FSharp.SemanticLocation Location, int SourceIndex)> orderedSites =
                    sites.Values.OrderBy(value => value.Location.FileName,
                            WorkspacePaths.FileSystemPathComparer)
                        .ThenBy(value => value.Location.StartLine)
                        .ThenBy(value => value.Location.StartColumn)
                        .ThenBy(value => value.Location.EndLine)
                        .ThenBy(value => value.Location.EndColumn)
                        .ToList();
                string status = failure is not null
                    ? applicableTargetFrameworks.Count == 0 ? "failed" : "partial"
                    : orderedSites.Count == 0 && generatedReferencesFiltered
                        ? "filtered"
                        : "scanned";
                string? reasonValue = failure ?? (status == "filtered"
                    ? "generated_files"
                    : null);
                countedSites.UnionWith(sites.Keys);
                return new(new(project.Path, applicableTargetFrameworks, project.IsTest,
                        orderedSites.Count, orderedSites.Take(sampleLimit).Select(value =>
                            Sample(value.Project, value.Location, value.SourceIndex)).ToList(),
                        status, reasonValue),
                    failure is null, false, status == "filtered",
                    projectDiagnosticCount, projectDiagnostics, projectPartialReason);
            }

            List<(CodeNav.FSharp.SemanticLocation Location, int SourceIndex)> rootAccepted =
                AcceptedReferences(captured, check, includeTests, includeGenerated)
                    .Where(reference => countedSites.Add(SiteKey(reference.Location)))
                    .ToList();
            bool rootGeneratedFiltered = !includeGenerated && rootAccepted.Count == 0 &&
                                         HasGeneratedFilteredReferences(captured, check);
            bool rootTestFiltered = !includeTests && captured.SelectedProjectIsTest;
            var groups = new List<FSharpReferenceGroup>
            {
                new(captured.SelectedContext!.Project,
                    [captured.SelectedContext.TargetFramework], captured.SelectedProjectIsTest,
                    rootAccepted.Count, rootAccepted.Take(sampleLimit)
                        .Select(reference => Sample(captured, reference.Location,
                            reference.SourceIndex)).ToList(),
                    rootTestFiltered || rootGeneratedFiltered ? "filtered" : "scanned",
                    rootTestFiltered ? "test_project" : rootGeneratedFiltered
                        ? "generated_files"
                        : null),
            };
            int totalReferences = rootAccepted.Count;
            if (definition is null)
            {
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_dependents_not_scanned");
                var externalCoverage = new FSharpReferencesCoverage(null, 0, 0, 0, null,
                    false, [], [], ApproximateModel: SelectedFSharpProjectModel == ProjectModelMode.Simple);
                return new(symbol, totalReferences, groups.SelectMany(group => group.Samples).ToList(),
                    null, captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason, check.DiagnosticCount,
                    diagnostics, captured.Health, Groups: groups, Coverage: externalCoverage);
            }

            var candidates = new List<FSharpDependentCandidate>();
            var excluded = new List<FSharpDependentCoverageEntry>();
            var failed = new List<FSharpDependentCoverageEntry>();
            var discoveryFailed = new List<FSharpDependentCoverageEntry>();
            int scanned = 0;
            int additionalDiagnosticCount = 0;
            int potentialConsumers = 0;
            int potentialConsumersEvaluated = 0;
            bool deadlineExhausted = false;
            bool candidateSetKnown = false;
            bool declaringProjectComplete = true;
            string? declaringProject = null;
            string? declaringProjectStatus = null;
            string? declaringProjectReason = null;

            try
            {
                if (!definition.ProjectPath.Equals(captured.SelectedContext!.Project,
                        WorkspacePaths.FileSystemPathComparison))
                {
                    declaringProject = definition.ProjectPath;
                    ProjectRow? definingProject = snapshot.Queries.ProjectByPathForHost(
                        definition.ProjectPath);
                    if (definingProject is null)
                    {
                        const string reason =
                            "fsharp_semantic_project_reference_unavailable";
                        declaringProjectComplete = false;
                        declaringProjectStatus = "failed";
                        declaringProjectReason = reason;
                        groups.Add(new(definition.ProjectPath, [], false, 0, [],
                            declaringProjectStatus, reason));
                    }
                    else
                    {
                        FSharpReferenceProjectScan declaringScan = await ScanProjectAsync(
                            definingProject, [definition.TargetFramework],
                            requireDeclaringProject: true).ConfigureAwait(false);
                        groups.Add(declaringScan.Group);
                        totalReferences += declaringScan.Group.Count;
                        declaringProjectComplete = declaringScan.Complete &&
                                                   !declaringScan.Inactive;
                        declaringProjectStatus = declaringScan.Group.Status;
                        declaringProjectReason = declaringScan.Group.Reason;
                        if (declaringScan.PartialReason is not null)
                            partialReason = AppendPartialReason(partialReason,
                                declaringScan.PartialReason);
                        if (declaringScan.DiagnosticCount > 0)
                        {
                            additionalDiagnosticCount += declaringScan.DiagnosticCount;
                            diagnostics.AddRange(declaringScan.Diagnostics);
                        }
                    }
                }

                FSharpDependentDiscovery discovery = DiscoverFSharpDependentCandidates(
                    snapshot.Queries, definition, captured.SelectedContext.Project, cts.Token);
                candidates = discovery.Candidates;
                potentialConsumers = discovery.PotentialConsumers;
                potentialConsumersEvaluated = discovery.PotentialConsumersEvaluated;
                discoveryFailed = discovery.Failed;
                candidateSetKnown = discovery.CandidateSetKnown;
                foreach (FSharpDependentCandidate candidate in candidates)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (candidate.BinaryCoupled)
                    {
                        excluded.Add(new(candidate.Project.Path, "binary_reference"));
                        groups.Add(new(candidate.Project.Path, [], candidate.Project.IsTest,
                            0, [], "excluded", "binary_reference"));
                        continue;
                    }
                    if (candidate.UnsupportedLanguage ||
                        !candidate.Project.Language.Equals("fs", StringComparison.OrdinalIgnoreCase))
                    {
                        excluded.Add(new(candidate.Project.Path, "unsupported_language"));
                        groups.Add(new(candidate.Project.Path, [], candidate.Project.IsTest,
                            0, [], "excluded", "unsupported_language"));
                        continue;
                    }

                    if (!includeTests && candidate.Project.IsTest)
                    {
                        excluded.Add(new(candidate.Project.Path, "test_project"));
                        groups.Add(new(candidate.Project.Path, [], true, 0, [],
                            "filtered", "test_project"));
                        continue;
                    }

                    string[] targetFrameworks = candidate.Project.Tfms.Split(';',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                    FSharpReferenceProjectScan dependentScan = await ScanProjectAsync(
                        candidate.Project, targetFrameworks,
                        requireDeclaringProject: false).ConfigureAwait(false);
                    groups.Add(dependentScan.Group);
                    totalReferences += dependentScan.Group.Count;
                    if (dependentScan.Inactive)
                    {
                        excluded.Add(new(candidate.Project.Path, "inactive_project_reference"));
                        continue;
                    }

                    if (dependentScan.Complete) scanned++;
                    else failed.Add(new(candidate.Project.Path,
                        dependentScan.Group.Reason ?? "fsharp_semantic_failed"));
                    if (dependentScan.PartialReason is not null)
                        partialReason = AppendPartialReason(partialReason,
                            dependentScan.PartialReason);
                    if (dependentScan.DiagnosticCount > 0)
                    {
                        additionalDiagnosticCount += dependentScan.DiagnosticCount;
                        diagnostics.AddRange(dependentScan.Diagnostics);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                deadlineExhausted = true;
            }

            int? pending = candidateSetKnown
                ? Math.Max(0, candidates.Count - scanned - excluded.Count - failed.Count)
                : null;
            bool incompleteExcluded = excluded.Any(entry =>
                !entry.Reason.Equals("inactive_project_reference", StringComparison.Ordinal) &&
                !entry.Reason.Equals("test_project", StringComparison.Ordinal));
            bool scansComplete = discoveryFailed.Count == 0 &&
                                 potentialConsumersEvaluated == potentialConsumers && !deadlineExhausted &&
                                     declaringProjectComplete && failed.Count == 0 &&
                                     candidates.Count == scanned + excluded.Count + failed.Count && !incompleteExcluded;
            if (deadlineExhausted)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_deadline");
            if (discoveryFailed.Count > 0)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_dependent_discovery_incomplete");
            if (!declaringProjectComplete)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_declaring_project_failed");
            if (failed.Count > 0)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_dependent_failed");
            if (excluded.Any(entry => entry.Reason == "unsupported_language"))
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_unsupported_boundary");
            if (excluded.Any(entry => entry.Reason == "binary_reference"))
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_binary_dependents_not_scanned");
            var coverage = new FSharpReferencesCoverage(candidateSetKnown ? candidates.Count : null,
                scanned,
                excluded.Count, failed.Count, pending, ScansComplete: scansComplete, Excluded: excluded, Failed: failed,
                potentialConsumers, potentialConsumersEvaluated,
                Math.Max(0, potentialConsumers - potentialConsumersEvaluated),
                discoveryFailed, declaringProject, declaringProjectStatus,
                declaringProjectReason, ApproximateModel: SelectedFSharpProjectModel == ProjectModelMode.Simple);
            int diagnosticCount = check.DiagnosticCount + additionalDiagnosticCount;
            List<FSharpReferenceSample> samples = groups.SelectMany(group => group.Samples).ToList();
            return new(symbol, totalReferences, samples, null,
                captured.SelectedContext, captured.AvailableContexts,
                captured.SelectedProjectIsTest, partialReason, diagnosticCount,
                diagnostics, captured.Health, Groups: groups, Coverage: coverage);
        }
        catch (OperationCanceledException)
        {
            if (rootReady && captured is not null)
            {
                string reason = AppendPartialReason(captured.PartialReason,
                    "fsharp_workspace_deadline")!;
                return new(null, null, [], "fsharp_semantic_timeout", captured.SelectedContext,
                    captured.AvailableContexts, captured.SelectedProjectIsTest, reason,
                    Health: captured.Health);
            }
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
            CleanupFSharpReferenceSnapshots(captureSession.ReferenceSnapshotDirectory,
                captureSession.BinaryReferences);
            snapshot?.Dispose();
            if (entered) _fsharpSemanticGate.Release();
        }
    }

    private static FSharpReferencesResult FSharpReferencesFailure(FSharpSemanticResult failure) =>
        new(null, null, [], failure.Error, failure.SelectedContext,
            failure.AvailableContexts, PartialReason: failure.PartialReason,
            DiagnosticCount: failure.DiagnosticCount, Diagnostics: failure.Diagnostics,
            Health: failure.Health, ProjectReferenceFailure: failure.ProjectReferenceFailure);
}
