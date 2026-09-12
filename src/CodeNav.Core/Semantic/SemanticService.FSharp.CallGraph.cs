using CodeNav.Core.Indexing;
using CodeNav.FSharp;

namespace CodeNav.Core.Semantic;

public sealed record FSharpCallSiteInfo(
    string Path,
    int Line,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string LineText,
    string CallKind,
    string Project,
    List<string> TargetFrameworksScanned);

public sealed record FSharpCallerInfo(
    FSharpSemanticSymbolInfo Caller,
    List<FSharpCallSiteInfo> CallSites);

public sealed record FSharpCalleeInfo(
    FSharpSemanticSymbolInfo Callee,
    List<FSharpCallSiteInfo> CallSites);

public sealed record FSharpCallGraphGroup(
    string Project,
    List<string> TargetFrameworksScanned,
    bool IsTest,
    int Count,
    string Status = "scanned",
    string? Reason = null);

public sealed record FSharpCallersResult(
    FSharpSemanticSymbolInfo? Symbol,
    int? TotalCallers,
    int? TotalCallSites,
    List<FSharpCallerInfo> Callers,
    string? Error,
    FSharpTypeCheckContext? SelectedContext,
    List<FSharpTypeCheckContext> AvailableContexts,
    bool SelectedProjectIsTest = false,
    string? PartialReason = null,
    int DiagnosticCount = 0,
    List<FSharpSemanticDiagnostic>? Diagnostics = null,
    IndexHealth? Health = null,
    FSharpProjectReferenceFailure? ProjectReferenceFailure = null,
    List<FSharpCallGraphGroup>? Groups = null,
    FSharpReferencesCoverage? Coverage = null,
    List<string>? DispatchSlots = null,
    bool QuotationBodiesExcluded = false,
    bool TraitCallsUnresolved = false);

public sealed record FSharpCalleesCoverage(
    bool ScanComplete,
    bool QuotationBodiesExcluded,
    bool TraitCallsUnresolved,
    bool ApproximateModel = false)
{
    public bool Complete => ScanComplete && !ApproximateModel;
}

public sealed record FSharpCalleesResult(
    FSharpSemanticSymbolInfo? Symbol,
    int? TotalCallees,
    int? TotalCallSites,
    List<FSharpCalleeInfo> Callees,
    string? Error,
    FSharpTypeCheckContext? SelectedContext,
    List<FSharpTypeCheckContext> AvailableContexts,
    bool SelectedProjectIsTest = false,
    string? PartialReason = null,
    int DiagnosticCount = 0,
    List<FSharpSemanticDiagnostic>? Diagnostics = null,
    IndexHealth? Health = null,
    FSharpProjectReferenceFailure? ProjectReferenceFailure = null,
    List<FSharpCallGraphGroup>? Groups = null,
    FSharpCalleesCoverage? Coverage = null);

public sealed partial class SemanticService
{

    private sealed class FSharpCallAccumulator(
        CapturedFSharpSemanticProject captured,
        CodeNav.FSharp.SemanticCall call,
        string project,
        string targetFramework)
    {
        public CapturedFSharpSemanticProject Captured { get; } = captured;
        public CodeNav.FSharp.SemanticCall Call { get; } = call;
        public string Project { get; } = project;
        public SortedSet<string> TargetFrameworks { get; } =
            new(StringComparer.OrdinalIgnoreCase) { targetFramework };
    }

    private sealed record FSharpCallProjectScan(
        FSharpCallGraphGroup Group,
        bool Complete,
        bool Inactive,
        int DiagnosticCount,
        List<FSharpSemanticDiagnostic> Diagnostics,
        string? PartialReason,
        bool QuotationBodiesExcluded,
        bool TraitCallsUnresolved);

    /// <summary>
    /// Finds compiler-proven F# callers in the selected closure, every distinct declaring
    /// project, and every proven source-ProjectReference workspace dependent. Per-context call
    /// evidence is admitted only after the captured binary-reference authority is reverified.
    /// </summary>
    public async Task<FSharpCallersResult> FSharpCallersAsync(
        string path,
        int line,
        int column,
        string? projectPath,
        string? targetFramework,
        bool includeTests,
        bool includeGenerated,
        int timeoutMs, FSharpSemanticTimingBox? timing = null)
    {
        timing ??= new();
        if (line < 1 || column <= 0)
            return new(null, null, null, [], "fsharp_semantic_position_invalid", null, []);

        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120_000));
        var captureSession = CreateFSharpSemanticCaptureSession();
        CapturedFSharpSemanticProject? captured = null;
        IndexReadSnapshot? snapshot = null;
        bool entered = false;
        try
        {
            using (timing.Admission())
                await _fsharpSemanticGate.WaitAsync(cts.Token).ConfigureAwait(false);
            entered = true;
            using IDisposable captureTiming = timing.Capture();
            snapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (snapshot is null)
                return new(null, null, null, [], "index_snapshot_unavailable", null, []);
            captured = CaptureFSharpSemanticProject(snapshot, path, projectPath,
                targetFramework, cts.Token, out FSharpSemanticResult? captureFailure,
                captureSession);
            if (captured is null)
                return FSharpCallersFailure(captureFailure!);
            captureTiming.Dispose();
            FSharpSemanticSnapshotCapturedForTest?.Invoke();

            SemanticCallGraphCheckResult check = await SemanticResolver.ResolveCallersAsync(
                captured.Projects, captured.RootProjectIndex, captured.RootProjectIndex,
                captured.Fingerprint, captured.BinaryReferences.Count == 0,
                captured.TargetFileName, line, column,
                BeforeFSharpImplementationTraversalForTest!, timing, cts.Token).ConfigureAwait(false);
            FSharpSemanticCheckCompletedForTest?.Invoke(check.Error);
            var closurePaths = captured.ClosureSourceFiles.ToHashSet(
                WorkspacePaths.FileSystemPathComparer);
            List<FSharpSemanticDiagnostic> diagnostics = check.Diagnostics
                .Select(value => MapFSharpDiagnostic(value, closurePaths))
                .ToList();
            string? partialReason = check.ErrorDiagnosticCount > 0
                ? AppendPartialReason(captured.PartialReason,
                    "fsharp_semantic_diagnostics_present")
                : captured.PartialReason;

            if (!VerifyFSharpBinaryReferences(captured.BinaryReferences, cts.Token))
            {
                return new(null, null, null, [], "fsharp_semantic_reference_changed",
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason,
                    check.DiagnosticCount, diagnostics, captured.Health);
            }
            if (check.Symbol is null || check.Error is not null)
            {
                return new(null, null, null, [], check.Error ?? "fsharp_semantic_failed",
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason,
                    check.DiagnosticCount, diagnostics, captured.Health,
                    DispatchSlots: check.DispatchSlots.ToList(),
                    QuotationBodiesExcluded: check.QuotationBodiesExcluded,
                    TraitCallsUnresolved: check.TraitCallsUnresolved);
            }

            var calls = new Dictionary<string, FSharpCallAccumulator>(StringComparer.Ordinal);
            var dispatchSlots = new SortedSet<string>(check.DispatchSlots,
                StringComparer.Ordinal);
            bool quotationBodiesExcluded = check.QuotationBodiesExcluded;
            bool traitCallsUnresolved = check.TraitCallsUnresolved;

            (int Added, bool GeneratedFiltered, bool TestFiltered) AcceptCalls(
                CapturedFSharpSemanticProject owner,
                SemanticCallGraphCheckResult ownerCheck,
                Dictionary<string, FSharpCallAccumulator> destination)
            {
                int before = destination.Count;
                bool generatedWasFiltered = false;
                bool testWasFiltered = false;
                foreach (CodeNav.FSharp.SemanticCall call in ownerCheck.Calls)
                {
                    if (!TryLocateFSharpCallSite(owner, call,
                            out CapturedFSharpSemanticNode? node, out int sourceIndex))
                        continue;
                    if (!includeTests && node!.IsTest)
                    {
                        testWasFiltered = true;
                        continue;
                    }
                    if (!includeGenerated && node!.SourceGenerated[sourceIndex])
                    {
                        generatedWasFiltered = true;
                        continue;
                    }
                    string key = FSharpCallSiteKey(call);
                    if (destination.TryGetValue(key, out FSharpCallAccumulator? existing))
                    {
                        existing.TargetFrameworks.Add(node!.TargetFramework);
                    }
                    else
                    {
                        destination[key] = new(owner, call, node!.ProjectPath,
                            node.TargetFramework);
                    }
                }
                return (destination.Count - before, generatedWasFiltered,
                    testWasFiltered);
            }

            void MergeCalls(Dictionary<string, FSharpCallAccumulator> source)
            {
                foreach ((string key, FSharpCallAccumulator value) in source)
                {
                    if (calls.TryGetValue(key, out FSharpCallAccumulator? existing))
                    {
                        foreach (string framework in value.TargetFrameworks)
                            existing.TargetFrameworks.Add(framework);
                    }
                    else
                    {
                        calls[key] = value;
                    }
                }
            }

            var rootAccepted = AcceptCalls(captured, check, calls);
            bool rootFiltered = rootAccepted.Added == 0 &&
                                (rootAccepted.TestFiltered || rootAccepted.GeneratedFiltered);
            string rootStatus = rootFiltered
                ? "filtered"
                : check.DeadlineExhausted || check.QuotationBodiesExcluded ||
                  check.TraitCallsUnresolved
                    ? "partial"
                    : "scanned";
            string? rootReason = rootFiltered && rootAccepted.TestFiltered
                ? "test_project"
                : rootFiltered && rootAccepted.GeneratedFiltered
                    ? "generated_files"
                    : check.DeadlineExhausted
                        ? "deadline"
                        : check.TraitCallsUnresolved
                            ? "trait_calls"
                            : check.QuotationBodiesExcluded
                                ? "quotation_bodies"
                                : null;
            var groups = new List<FSharpCallGraphGroup>
            {
                new(captured.SelectedContext.Project,
                    [captured.SelectedContext.TargetFramework],
                    captured.SelectedProjectIsTest, rootAccepted.Added,
                    rootStatus, rootReason),
            };
            int additionalDiagnosticCount = 0;

            FSharpReferenceDefinition? definition =
                FindFSharpReferenceDefinition(captured, check.Symbol);
            if (definition is null)
            {
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_dependents_not_scanned");
                var externalCoverage = new FSharpReferencesCoverage(
                    null, 0, 0, 0, null, false, [], [],
                    ApproximateModel: SelectedFSharpProjectModel == ProjectModelMode.Simple);
                return BuildResult(externalCoverage, partialReason);
            }

            async Task<FSharpCallProjectScan> ScanProjectAsync(
                ProjectRow project,
                IReadOnlyList<string> targetFrameworks,
                bool requireDeclaringProject)
            {
                if (!includeTests && project.IsTest)
                {
                    return new(new(project.Path, [], true, 0, "filtered", "test_project"),
                        true, false, 0, [], null, false, false);
                }

                var applicable = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var failures = new List<string>();
                var projectDiagnostics = new List<FSharpSemanticDiagnostic>();
                int projectDiagnosticCount = 0;
                string? projectPartialReason = null;
                bool projectQuotationBodiesExcluded = false;
                bool projectTraitCallsUnresolved = false;
                bool projectGeneratedFiltered = false;
                bool anyTargetInClosure = false;
                int before = calls.Count;
                foreach (string framework in targetFrameworks)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    CapturedFSharpSemanticProject? projectCapture =
                        CaptureFSharpSemanticProjectContext(snapshot, project, framework,
                            cts.Token, out FSharpSemanticResult? captureFailure,
                            captureSession, timing);
                    if (projectCapture is null)
                    {
                        failures.Add(captureFailure?.Error ??
                                     "fsharp_semantic_snapshot_failed");
                        continue;
                    }
                    int lookupProjectIndex = Array.FindIndex(projectCapture.Nodes, node =>
                        node.ProjectPath.Equals(definition.ProjectPath,
                            WorkspacePaths.FileSystemPathComparison) &&
                        node.AssemblyName.Equals(definition.AssemblyName,
                            StringComparison.OrdinalIgnoreCase));
                    if (lookupProjectIndex < 0) continue;
                    anyTargetInClosure = true;

                    SemanticCallGraphCheckResult projectCheck = await
                        SemanticResolver.ResolveCallersAsync(projectCapture.Projects,
                            projectCapture.RootProjectIndex, lookupProjectIndex,
                            projectCapture.Fingerprint,
                            projectCapture.BinaryReferences.Count == 0,
                            definition.FullPath, definition.Line, definition.Column,
                            BeforeFSharpImplementationTraversalForTest!,
                            timing, cts.Token).ConfigureAwait(false);
                    FSharpSemanticCheckCompletedForTest?.Invoke(projectCheck.Error);
                    if (projectCheck.Symbol is null || projectCheck.Error is not null)
                    {
                        failures.Add(projectCheck.Error ??
                                     "fsharp_semantic_symbol_identity_changed");
                        continue;
                    }
                    var staged = new Dictionary<string, FSharpCallAccumulator>(
                        StringComparer.Ordinal);
                    var accepted = AcceptCalls(projectCapture, projectCheck, staged);
                    if (!VerifyFSharpBinaryReferences(projectCapture.BinaryReferences,
                            cts.Token))
                    {
                        failures.Add("fsharp_semantic_reference_changed");
                        continue;
                    }

                    MergeCalls(staged);
                    projectGeneratedFiltered |= accepted.GeneratedFiltered;
                    projectQuotationBodiesExcluded |= projectCheck.QuotationBodiesExcluded;
                    projectTraitCallsUnresolved |= projectCheck.TraitCallsUnresolved;
                    foreach (string slot in projectCheck.DispatchSlots)
                        dispatchSlots.Add(slot);
                    if (projectCheck.ErrorDiagnosticCount > 0)
                        projectPartialReason = AppendPartialReason(projectPartialReason,
                            "fsharp_semantic_diagnostics_present");
                    if (projectCapture.PartialReason is not null)
                        projectPartialReason = AppendPartialReason(projectPartialReason,
                            projectCapture.PartialReason);
                    var paths = projectCapture.ClosureSourceFiles.ToHashSet(
                        WorkspacePaths.FileSystemPathComparer);
                    projectDiagnostics.AddRange(projectCheck.Diagnostics.Select(value =>
                        MapFSharpDiagnostic(value, paths)));
                    projectDiagnosticCount += projectCheck.DiagnosticCount;
                    applicable.Add(framework);
                    if (projectCheck.DeadlineExhausted)
                        failures.Add("fsharp_workspace_deadline");
                }

                if (!anyTargetInClosure && failures.Count == 0)
                {
                    string reason = requireDeclaringProject
                        ? "fsharp_workspace_declaring_project_not_in_closure"
                        : "inactive_project_reference";
                    return new(new(project.Path, [], project.IsTest, 0,
                            requireDeclaringProject ? "failed" : "excluded", reason),
                        !requireDeclaringProject, !requireDeclaringProject,
                        projectDiagnosticCount, projectDiagnostics,
                        projectPartialReason, false, false);
                }

                int added = calls.Count - before;
                string? failure = failures.FirstOrDefault();
                string status = failure is not null
                    ? applicable.Count == 0 ? "failed" : "partial"
                    : projectQuotationBodiesExcluded || projectTraitCallsUnresolved
                        ? "partial"
                        : added == 0 && projectGeneratedFiltered
                            ? "filtered"
                            : "scanned";
                string? reasonValue = failure ?? (projectTraitCallsUnresolved
                    ? "trait_calls"
                    : projectQuotationBodiesExcluded
                        ? "quotation_bodies"
                        : status == "filtered" ? "generated_files" : null);
                return new(new(project.Path, applicable.ToList(), project.IsTest,
                        added, status, reasonValue),
                    failure is null && !projectQuotationBodiesExcluded &&
                    !projectTraitCallsUnresolved,
                    false, projectDiagnosticCount, projectDiagnostics,
                    projectPartialReason, projectQuotationBodiesExcluded,
                    projectTraitCallsUnresolved);
            }

            var candidates = new List<FSharpDependentCandidate>();
            var excluded = new List<FSharpDependentCoverageEntry>();
            var failed = new List<FSharpDependentCoverageEntry>();
            var discoveryFailed = new List<FSharpDependentCoverageEntry>();
            int scanned = 0;
            int potentialConsumers = 0;
            int potentialConsumersEvaluated = 0;
            bool deadlineExhausted = check.DeadlineExhausted;
            bool candidateSetKnown = false;
            bool declaringProjectComplete = true;
            string? declaringProjectStatus = null;
            string? declaringProjectReason = null;
            string? declaringProject = definition.ProjectPath.Equals(
                captured.SelectedContext.Project,
                WorkspacePaths.FileSystemPathComparison)
                ? null
                : definition.ProjectPath;
            try
            {
                if (declaringProject is not null)
                {
                    ProjectRow? row = snapshot.Queries.ProjectByPathForHost(
                        declaringProject);
                    if (row is null)
                    {
                        declaringProjectComplete = false;
                        declaringProjectStatus = "failed";
                        declaringProjectReason =
                            "fsharp_semantic_project_reference_unavailable";
                        groups.Add(new(declaringProject, [], false, 0, "failed",
                            declaringProjectReason));
                    }
                    else
                    {
                        FSharpCallProjectScan declaringScan = await ScanProjectAsync(row,
                            [definition.TargetFramework], true).ConfigureAwait(false);
                        groups.Add(declaringScan.Group);
                        declaringProjectComplete = declaringScan.Complete;
                        declaringProjectStatus = declaringScan.Group.Status;
                        declaringProjectReason = declaringScan.Group.Reason;
                        quotationBodiesExcluded |=
                            declaringScan.QuotationBodiesExcluded;
                        traitCallsUnresolved |= declaringScan.TraitCallsUnresolved;
                        additionalDiagnosticCount += declaringScan.DiagnosticCount;
                        diagnostics.AddRange(declaringScan.Diagnostics);
                        if (declaringScan.PartialReason is not null)
                            partialReason = AppendPartialReason(partialReason,
                                declaringScan.PartialReason);
                    }
                }

                FSharpDependentDiscovery discovery = DiscoverFSharpDependentCandidates(
                    snapshot.Queries, definition, captured.SelectedContext.Project,
                    cts.Token);
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
                            0, "excluded", "binary_reference"));
                        continue;
                    }
                    if (candidate.UnsupportedLanguage ||
                        !candidate.Project.Language.Equals("fs",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        excluded.Add(new(candidate.Project.Path, "unsupported_language"));
                        groups.Add(new(candidate.Project.Path, [], candidate.Project.IsTest,
                            0, "excluded", "unsupported_language"));
                        continue;
                    }
                    if (!includeTests && candidate.Project.IsTest)
                    {
                        excluded.Add(new(candidate.Project.Path, "test_project"));
                        groups.Add(new(candidate.Project.Path, [], true, 0,
                            "filtered", "test_project"));
                        continue;
                    }
                    string[] frameworks = candidate.Project.Tfms.Split(';',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                    FSharpCallProjectScan dependentScan = await ScanProjectAsync(
                        candidate.Project, frameworks, false).ConfigureAwait(false);
                    groups.Add(dependentScan.Group);
                    quotationBodiesExcluded |= dependentScan.QuotationBodiesExcluded;
                    traitCallsUnresolved |= dependentScan.TraitCallsUnresolved;
                    if (dependentScan.Inactive)
                    {
                        excluded.Add(new(candidate.Project.Path,
                            "inactive_project_reference"));
                        continue;
                    }
                    if (dependentScan.Complete) scanned++;
                    else failed.Add(new(candidate.Project.Path,
                        dependentScan.Group.Reason ?? "fsharp_semantic_failed"));
                    additionalDiagnosticCount += dependentScan.DiagnosticCount;
                    diagnostics.AddRange(dependentScan.Diagnostics);
                    if (dependentScan.PartialReason is not null)
                        partialReason = AppendPartialReason(partialReason,
                            dependentScan.PartialReason);
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
                                     candidates.Count == scanned + excluded.Count + failed.Count && !incompleteExcluded &&
                                     !quotationBodiesExcluded && !traitCallsUnresolved;
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
            if (quotationBodiesExcluded)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_quotation_bodies_excluded");
            if (traitCallsUnresolved)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_trait_calls_unresolved");
            if (excluded.Any(entry => entry.Reason == "unsupported_language"))
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_unsupported_boundary");
            if (excluded.Any(entry => entry.Reason == "binary_reference"))
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_binary_dependents_not_scanned");

            var coverage = new FSharpReferencesCoverage(
                candidateSetKnown ? candidates.Count : null,
                scanned, excluded.Count, failed.Count, pending,
                ScansComplete: scansComplete, Excluded: excluded, Failed: failed,
                potentialConsumers, potentialConsumersEvaluated,
                Math.Max(0, potentialConsumers - potentialConsumersEvaluated),
                discoveryFailed, declaringProject, declaringProjectStatus,
                declaringProjectReason,
                declaringProject is null ? [] : [declaringProject],
                ApproximateModel: SelectedFSharpProjectModel == ProjectModelMode.Simple);
            return BuildResult(coverage, partialReason);

            FSharpCallersResult BuildResult(FSharpReferencesCoverage coverageValue,
                string? partialReasonValue)
            {
                List<FSharpCallerInfo> callerItems = calls.Values
                    .GroupBy(value => FSharpCallSymbolKey(value.Call.Caller),
                        StringComparer.Ordinal)
                    .Select(group => new FSharpCallerInfo(
                        MapFSharpCallSymbol(group.First().Call.Caller,
                            group.First().Captured),
                        group.Select(MapFSharpCallSite)
                            .OrderBy(site => site.Path, StringComparer.Ordinal)
                            .ThenBy(site => site.Line)
                            .ThenBy(site => site.StartColumn)
                            .ThenBy(site => site.CallKind, StringComparer.Ordinal)
                            .ToList()))
                    .OrderBy(value => value.Caller.Use.Path, StringComparer.Ordinal)
                    .ThenBy(value => value.Caller.Use.StartLine)
                    .ThenBy(value => value.Caller.Use.StartColumn)
                    .ThenBy(value => value.Caller.FullName, StringComparer.Ordinal)
                    .ToList();
                return new(MapFSharpCallSymbol(check.Symbol, captured),
                    callerItems.Count, calls.Count, callerItems, null,
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReasonValue,
                    check.DiagnosticCount + additionalDiagnosticCount,
                    diagnostics, captured.Health, Groups: groups,
                    Coverage: coverageValue, DispatchSlots: dispatchSlots.ToList(),
                    QuotationBodiesExcluded: quotationBodiesExcluded,
                    TraitCallsUnresolved: traitCallsUnresolved);
            }
        }
        catch (OperationCanceledException)
        {
            string? reason = captured is null
                ? null
                : AppendPartialReason(captured.PartialReason,
                    "fsharp_workspace_deadline");
            return new(null, null, null, [], "fsharp_semantic_timeout",
                captured?.SelectedContext, captured?.AvailableContexts ?? [],
                captured?.SelectedProjectIsTest ?? false, reason,
                Health: captured?.Health);
        }
        catch (Exception ex)
        {
            _log($"F# callers request failed: {ex.GetType().Name}");
            return new(null, null, null, [], captured is null
                    ? "fsharp_semantic_snapshot_failed"
                    : "fsharp_semantic_failed",
                captured?.SelectedContext, captured?.AvailableContexts ?? [],
                captured?.SelectedProjectIsTest ?? false,
                captured?.PartialReason, Health: captured?.Health);
        }
        finally
        {
            CleanupFSharpReferenceSnapshots(captureSession.ReferenceSnapshotDirectory,
                captureSession.BinaryReferences);
            snapshot?.Dispose();
            if (entered) _fsharpSemanticGate.Release();
        }
    }

    /// <summary>
    /// Finds compiler-proven callees in the innermost F# function, member, constructor, object
    /// expression override, or module initializer body containing the requested position.
    /// Target identities may come from the selected source-ProjectReference closure, but the
    /// operation never scans workspace dependents.
    /// </summary>
    public async Task<FSharpCalleesResult> FSharpCalleesAsync(
        string path,
        int line,
        int column,
        string? projectPath,
        string? targetFramework,
        bool includeTests,
        bool includeGenerated,
        int timeoutMs, FSharpSemanticTimingBox? timing = null)
    {
        timing ??= new();
        if (line < 1 || column <= 0)
            return new(null, null, null, [], "fsharp_semantic_position_invalid", null, []);

        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120_000));
        var captureSession = CreateFSharpSemanticCaptureSession();
        CapturedFSharpSemanticProject? captured = null;
        IndexReadSnapshot? snapshot = null;
        bool entered = false;
        try
        {
            using (timing.Admission())
                await _fsharpSemanticGate.WaitAsync(cts.Token).ConfigureAwait(false);
            entered = true;
            using IDisposable captureTiming = timing.Capture();
            snapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (snapshot is null)
                return new(null, null, null, [], "index_snapshot_unavailable", null, []);
            captured = CaptureFSharpSemanticProject(snapshot, path, projectPath,
                targetFramework, cts.Token, out FSharpSemanticResult? captureFailure,
                captureSession);
            if (captured is null)
                return FSharpCalleesFailure(captureFailure!);
            captureTiming.Dispose();
            FSharpSemanticSnapshotCapturedForTest?.Invoke();

            SemanticCallGraphCheckResult check = await SemanticResolver.ResolveCalleesAsync(
                captured.Projects, captured.RootProjectIndex, captured.RootProjectIndex,
                captured.Fingerprint, captured.BinaryReferences.Count == 0,
                captured.TargetFileName, line, column,
                BeforeFSharpImplementationTraversalForTest!, timing, cts.Token).ConfigureAwait(false);
            FSharpSemanticCheckCompletedForTest?.Invoke(check.Error);
            var closurePaths = captured.ClosureSourceFiles.ToHashSet(
                WorkspacePaths.FileSystemPathComparer);
            List<FSharpSemanticDiagnostic> diagnostics = check.Diagnostics
                .Select(value => MapFSharpDiagnostic(value, closurePaths))
                .ToList();
            string? partialReason = check.ErrorDiagnosticCount > 0
                ? AppendPartialReason(captured.PartialReason,
                    "fsharp_semantic_diagnostics_present")
                : captured.PartialReason;

            if (!VerifyFSharpBinaryReferences(captured.BinaryReferences, cts.Token))
            {
                return new(null, null, null, [], "fsharp_semantic_reference_changed",
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason,
                    check.DiagnosticCount, diagnostics, captured.Health);
            }
            if (check.Symbol is null || check.Error is not null)
            {
                return new(null, null, null, [], check.Error ?? "fsharp_semantic_failed",
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason,
                    check.DiagnosticCount, diagnostics, captured.Health);
            }

            var accepted = new Dictionary<string, FSharpCallAccumulator>(
                StringComparer.Ordinal);
            bool generatedFiltered = false;
            bool testFiltered = false;
            foreach (CodeNav.FSharp.SemanticCall call in check.Calls)
            {
                if (!TryLocateFSharpCallSite(captured, call,
                        out CapturedFSharpSemanticNode? node, out int sourceIndex))
                    continue;
                if (!includeTests && node!.IsTest)
                {
                    testFiltered = true;
                    continue;
                }
                if (!includeGenerated && node!.SourceGenerated[sourceIndex])
                {
                    generatedFiltered = true;
                    continue;
                }

                string key = FSharpCallSiteKey(call) + "\0" +
                             FSharpCallSymbolKey(call.Callee);
                if (accepted.TryGetValue(key, out FSharpCallAccumulator? existing))
                {
                    existing.TargetFrameworks.Add(node!.TargetFramework);
                }
                else
                {
                    accepted[key] = new(captured, call, node!.ProjectPath,
                        node.TargetFramework);
                }
            }

            bool filtered = accepted.Count == 0 && (testFiltered || generatedFiltered);
            bool complete = !check.DeadlineExhausted &&
                            !check.QuotationBodiesExcluded &&
                            !check.TraitCallsUnresolved;
            if (check.DeadlineExhausted)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_deadline");
            if (check.QuotationBodiesExcluded)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_quotation_bodies_excluded");
            if (check.TraitCallsUnresolved)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_trait_calls_unresolved");

            string status = filtered
                ? "filtered"
                : complete ? "scanned" : "partial";
            string? reason = filtered && testFiltered
                ? "test_project"
                : filtered && generatedFiltered
                    ? "generated_files"
                    : check.DeadlineExhausted
                        ? "deadline"
                        : check.TraitCallsUnresolved
                            ? "trait_calls"
                            : check.QuotationBodiesExcluded
                                ? "quotation_bodies"
                                : null;
            var groups = new List<FSharpCallGraphGroup>
            {
                new(captured.SelectedContext.Project,
                    [captured.SelectedContext.TargetFramework],
                    captured.SelectedProjectIsTest, accepted.Count, status, reason),
            };
            List<FSharpCalleeInfo> callees = accepted.Values
                .GroupBy(value => FSharpCallSymbolKey(value.Call.Callee),
                    StringComparer.Ordinal)
                .Select(group => new FSharpCalleeInfo(
                    MapFSharpCallSymbol(group.First().Call.Callee,
                        group.First().Captured),
                    group.Select(MapFSharpCallSite)
                        .OrderBy(site => site.Path, StringComparer.Ordinal)
                        .ThenBy(site => site.Line)
                        .ThenBy(site => site.StartColumn)
                        .ThenBy(site => site.CallKind, StringComparer.Ordinal)
                        .ToList()))
                .OrderBy(value => value.Callee.FullName, StringComparer.Ordinal)
                .ThenBy(value => value.Callee.Name, StringComparer.Ordinal)
                .ThenBy(value => value.Callee.Use.Path, StringComparer.Ordinal)
                .ThenBy(value => value.Callee.Use.StartLine)
                .ToList();
            var coverage = new FSharpCalleesCoverage(complete,
                check.QuotationBodiesExcluded, check.TraitCallsUnresolved,
                ApproximateModel: SelectedFSharpProjectModel == ProjectModelMode.Simple);
            return new(MapFSharpCallSymbol(check.Symbol, captured), callees.Count,
                accepted.Count, callees, null, captured.SelectedContext,
                captured.AvailableContexts, captured.SelectedProjectIsTest,
                partialReason, check.DiagnosticCount, diagnostics, captured.Health,
                Groups: groups, Coverage: coverage);
        }
        catch (OperationCanceledException)
        {
            string? reason = captured is null
                ? null
                : AppendPartialReason(captured.PartialReason,
                    "fsharp_workspace_deadline");
            return new(null, null, null, [], "fsharp_semantic_timeout",
                captured?.SelectedContext, captured?.AvailableContexts ?? [],
                captured?.SelectedProjectIsTest ?? false, reason,
                Health: captured?.Health);
        }
        catch (Exception ex)
        {
            _log($"F# callees request failed: {ex.GetType().Name}");
            return new(null, null, null, [], captured is null
                    ? "fsharp_semantic_snapshot_failed"
                    : "fsharp_semantic_failed",
                captured?.SelectedContext, captured?.AvailableContexts ?? [],
                captured?.SelectedProjectIsTest ?? false,
                captured?.PartialReason, Health: captured?.Health);
        }
        finally
        {
            CleanupFSharpReferenceSnapshots(captureSession.ReferenceSnapshotDirectory,
                captureSession.BinaryReferences);
            snapshot?.Dispose();
            if (entered) _fsharpSemanticGate.Release();
        }
    }

    private static FSharpCallersResult FSharpCallersFailure(
        FSharpSemanticResult failure) =>
        new(null, null, null, [], failure.Error, failure.SelectedContext,
            failure.AvailableContexts, PartialReason: failure.PartialReason,
            DiagnosticCount: failure.DiagnosticCount,
            Diagnostics: failure.Diagnostics, Health: failure.Health,
            ProjectReferenceFailure: failure.ProjectReferenceFailure);

    private static FSharpCalleesResult FSharpCalleesFailure(
        FSharpSemanticResult failure) =>
        new(null, null, null, [], failure.Error, failure.SelectedContext,
            failure.AvailableContexts, PartialReason: failure.PartialReason,
            DiagnosticCount: failure.DiagnosticCount,
            Diagnostics: failure.Diagnostics, Health: failure.Health,
            ProjectReferenceFailure: failure.ProjectReferenceFailure);

    private FSharpSemanticRange MapFSharpCallRange(
        CodeNav.FSharp.SemanticLocation location) => new(
        location.Role,
        ToRelPath(location.FileName),
        location.StartLine,
        location.StartColumn + 1,
        location.EndLine,
        location.EndColumn + 1);

    private FSharpSemanticSymbolInfo MapFSharpCallSymbol(
        CodeNav.FSharp.SemanticSymbol value,
        CapturedFSharpSemanticProject owner)
    {
        var root = owner.SourceFiles.ToHashSet(WorkspacePaths.FileSystemPathComparer);
        var closure = owner.ClosureSourceFiles.ToHashSet(
            WorkspacePaths.FileSystemPathComparer);
        List<FSharpSemanticRange> declarations = value.Declarations
            .Where(location => closure.Contains(Path.GetFullPath(location.FileName)))
            .Select(MapFSharpCallRange)
            .ToList();
        int outside = Math.Max(0, value.Declarations.Length - declarations.Count);
        int fromClosure = value.Declarations.Count(location =>
        {
            string fullPath = Path.GetFullPath(location.FileName);
            return closure.Contains(fullPath) && !root.Contains(fullPath);
        });
        return new(value.Name, value.FullName, value.Kind, value.Container,
            value.Namespace, value.Assembly, value.Accessibility,
            MapFSharpCallRange(value.UseLocation), value.Declarations.Length,
            declarations, outside, fromClosure);
    }

    private static bool TryLocateFSharpCallSite(
        CapturedFSharpSemanticProject owner,
        CodeNav.FSharp.SemanticCall call,
        out CapturedFSharpSemanticNode? node,
        out int sourceIndex)
    {
        node = null;
        sourceIndex = -1;
        if (call.ProjectIndex < 0 || call.ProjectIndex >= owner.Nodes.Length)
            return false;
        node = owner.Nodes[call.ProjectIndex];
        string fullPath = Path.GetFullPath(call.Site.FileName);
        sourceIndex = Array.FindIndex(node.Input.SourceFiles, source =>
            source.Equals(fullPath, WorkspacePaths.FileSystemPathComparison));
        return sourceIndex >= 0;
    }

    private FSharpCallSiteInfo MapFSharpCallSite(FSharpCallAccumulator value)
    {
        _ = TryLocateFSharpCallSite(value.Captured, value.Call,
            out CapturedFSharpSemanticNode? node, out int sourceIndex);
        string lineText = "";
        if (node is not null && sourceIndex >= 0)
        {
            Microsoft.CodeAnalysis.Text.SourceText source =
                Microsoft.CodeAnalysis.Text.SourceText.From(
                    node.Input.SourceTexts[sourceIndex]);
            if (value.Call.Site.StartLine >= 1 &&
                value.Call.Site.StartLine <= source.Lines.Count)
            {
                lineText = Truncate(source.Lines[value.Call.Site.StartLine - 1]
                    .ToString().Trim());
            }
        }
        return new(ToRelPath(value.Call.Site.FileName),
            value.Call.Site.StartLine, value.Call.Site.StartColumn + 1,
            value.Call.Site.EndLine, value.Call.Site.EndColumn + 1,
            lineText, value.Call.CallKind, value.Project,
            value.TargetFrameworks.OrderBy(item => item, StringComparer.Ordinal)
                .ToList());
    }

    private static string FSharpCallSiteKey(CodeNav.FSharp.SemanticCall call) =>
        $"{WorkspacePaths.ToGitPath(Path.GetFullPath(call.Site.FileName))}\0" +
        $"{call.Site.StartLine}\0{call.Site.StartColumn}\0" +
        $"{call.Site.EndLine}\0{call.Site.EndColumn}\0{call.CallKind}";

    private static string FSharpCallSymbolKey(CodeNav.FSharp.SemanticSymbol symbol)
    {
        string declarations = string.Join("|", symbol.Declarations
            .OrderBy(location => location.FileName, WorkspacePaths.FileSystemPathComparer)
            .ThenBy(location => location.StartLine)
            .ThenBy(location => location.StartColumn)
            .Select(location =>
                $"{WorkspacePaths.ToGitPath(Path.GetFullPath(location.FileName))}:" +
                $"{location.StartLine}:{location.StartColumn}:" +
                $"{location.EndLine}:{location.EndColumn}"));
        return $"{symbol.Assembly}\0{symbol.Kind}\0{symbol.FullName}\0" +
               $"{symbol.Identity}\0{declarations}";
    }
}
