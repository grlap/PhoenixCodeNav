using CodeNav.Core.Indexing;
using CodeNav.FSharp;

namespace CodeNav.Core.Semantic;

public sealed record FSharpImplementationInfo(
    FSharpSemanticSymbolInfo Symbol,
    string ImplementationKind,
    bool IsAbstract,
    string? Via,
    string Project,
    List<string> TargetFrameworksScanned);

public sealed record FSharpImplementationGroup(
    string Project,
    List<string> TargetFrameworksScanned,
    bool IsTest,
    int Count,
    string Status = "scanned",
    string? Reason = null);

public sealed record FSharpImplementationsResult(
    FSharpSemanticSymbolInfo? Symbol,
    int? TotalImplementations,
    List<FSharpImplementationInfo> Implementations,
    string? Error,
    FSharpTypeCheckContext? SelectedContext,
    List<FSharpTypeCheckContext> AvailableContexts,
    bool SelectedProjectIsTest = false,
    string? PartialReason = null,
    int DiagnosticCount = 0,
    List<FSharpSemanticDiagnostic>? Diagnostics = null,
    IndexHealth? Health = null,
    FSharpProjectReferenceFailure? ProjectReferenceFailure = null,
    List<FSharpImplementationGroup>? Groups = null,
    FSharpReferencesCoverage? Coverage = null,
    List<string>? ResolvedFromOverride = null,
    bool QuotationBodiesExcluded = false);

public sealed partial class SemanticService
{

    private sealed record FSharpImplementationProjectScan(
        FSharpImplementationGroup Group,
        bool Complete,
        bool Inactive,
        int DiagnosticCount,
        List<FSharpSemanticDiagnostic> Diagnostics,
        string? PartialReason,
        bool QuotationBodiesExcluded);

    private sealed class FSharpImplementationAccumulator(
        CapturedFSharpSemanticProject captured,
        CodeNav.FSharp.SemanticImplementation implementation,
        string project,
        string targetFramework)
    {
        public CapturedFSharpSemanticProject Captured { get; } = captured;
        public CodeNav.FSharp.SemanticImplementation Implementation { get; } = implementation;
        public string Project { get; } = project;
        public SortedSet<string> TargetFrameworks { get; } =
            new(StringComparer.OrdinalIgnoreCase) { targetFramework };
    }

    /// <summary>
    /// Finds compiler-proven F# interface/type/member implementations in one pinned workspace
    /// snapshot. The selected closure, distinct declaring projects, and every proven source
    /// dependent share one capture session and one request-wide physical declaration set.
    /// </summary>
    public async Task<FSharpImplementationsResult> FSharpImplementationsAsync(
        string path,
        int line,
        int column,
        string? projectPath,
        string? targetFramework,
        bool includeTests,
        bool includeGenerated,
        int timeoutMs)
    {
        if (line < 1 || column <= 0)
            return new(null, null, [], "fsharp_semantic_position_invalid", null, []);

        using var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 500, 120_000));
        var captureSession = CreateFSharpSemanticCaptureSession();
        CapturedFSharpSemanticProject? captured = null;
        IndexReadSnapshot? snapshot = null;
        bool entered = false;
        try
        {
            await _fsharpSemanticGate.WaitAsync(cts.Token).ConfigureAwait(false);
            entered = true;
            snapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (snapshot is null)
                return new(null, null, [], "index_snapshot_unavailable", null, []);
            captured = CaptureFSharpSemanticProject(snapshot, path, projectPath,
                targetFramework, cts.Token, out FSharpSemanticResult? captureFailure,
                captureSession);
            if (captured is null)
                return FSharpImplementationsFailure(captureFailure!);
            FSharpSemanticSnapshotCapturedForTest?.Invoke();

            SemanticImplementationsCheckResult check = await
                SemanticResolver.ResolveImplementationsAsync(
                    captured.Projects, captured.RootProjectIndex,
                    captured.RootProjectIndex, captured.Fingerprint,
                    captured.BinaryReferences.Count == 0, captured.TargetFileName,
                    line, column, BeforeFSharpImplementationTraversalForTest!,
                    cts.Token).ConfigureAwait(false);
            FSharpSemanticCheckCompletedForTest?.Invoke(check.Error);
            var rootSourcePaths = captured.SourceFiles.ToHashSet(
                WorkspacePaths.FileSystemPathComparer);
            var closureSourcePaths = captured.ClosureSourceFiles.ToHashSet(
                WorkspacePaths.FileSystemPathComparer);
            List<FSharpSemanticDiagnostic> diagnostics = check.Diagnostics
                .Select(value => MapFSharpDiagnostic(value, closureSourcePaths))
                .ToList();
            string? partialReason = check.ErrorDiagnosticCount > 0
                ? AppendPartialReason(captured.PartialReason,
                    "fsharp_semantic_diagnostics_present")
                : captured.PartialReason;

            if (!VerifyFSharpBinaryReferences(captured.BinaryReferences, cts.Token))
            {
                return new(null, null, [], "fsharp_semantic_reference_changed",
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason,
                    check.DiagnosticCount, diagnostics, captured.Health);
            }
            if (check.Symbol is null || check.Error is not null)
            {
                return new(null, null, [], check.Error ?? "fsharp_semantic_failed",
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReason,
                    check.DiagnosticCount, diagnostics, captured.Health,
                    ResolvedFromOverride: check.ResolvedFromOverride.ToList(),
                    QuotationBodiesExcluded: check.QuotationBodiesExcluded);
            }

            FSharpSemanticRange MapRange(CodeNav.FSharp.SemanticLocation location) => new(
                location.Role,
                ToRelPath(location.FileName),
                location.StartLine,
                location.StartColumn + 1,
                location.EndLine,
                location.EndColumn + 1);

            var ownerPathSets = new Dictionary<CapturedFSharpSemanticProject,
                (HashSet<string> Root, HashSet<string> Closure)>(
                ReferenceEqualityComparer.Instance);

            FSharpSemanticSymbolInfo MapSymbol(
                CodeNav.FSharp.SemanticSymbol value,
                CapturedFSharpSemanticProject owner)
            {
                if (!ownerPathSets.TryGetValue(owner, out var pathSets))
                {
                    pathSets = (
                        owner.SourceFiles.ToHashSet(WorkspacePaths.FileSystemPathComparer),
                        owner.ClosureSourceFiles.ToHashSet(
                            WorkspacePaths.FileSystemPathComparer));
                    ownerPathSets[owner] = pathSets;
                }
                List<FSharpSemanticRange> declarations = value.Declarations
                    .Where(location => pathSets.Closure.Contains(
                        Path.GetFullPath(location.FileName)))
                    .Select(MapRange)
                    .ToList();
                int outside = Math.Max(0, value.Declarations.Length - declarations.Count);
                int closure = value.Declarations.Count(location =>
                {
                    string declarationPath = Path.GetFullPath(location.FileName);
                    return pathSets.Closure.Contains(declarationPath) &&
                           !pathSets.Root.Contains(declarationPath);
                });
                return new(value.Name, value.FullName, value.Kind, value.Container,
                    value.Namespace, value.Assembly, value.Accessibility,
                    MapRange(value.UseLocation), value.Declarations.Length,
                    declarations, outside, closure);
            }

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
                check.Symbol.Declarations
                    .Where(location => closureSourcePaths.Contains(
                        Path.GetFullPath(location.FileName)))
                    .Select(MapRange)
                    .ToList(),
                Math.Max(0, check.Symbol.Declarations.Count(location =>
                    !closureSourcePaths.Contains(Path.GetFullPath(location.FileName)))),
                check.Symbol.Declarations.Count(location =>
                {
                    string declarationPath = Path.GetFullPath(location.FileName);
                    return closureSourcePaths.Contains(declarationPath) &&
                           !rootSourcePaths.Contains(declarationPath);
                }));

            var implementations = new Dictionary<string, FSharpImplementationAccumulator>(
                StringComparer.Ordinal);
            bool quotationBodiesExcluded = check.QuotationBodiesExcluded;

            string SiteKey(CodeNav.FSharp.SemanticImplementation implementation)
            {
                CodeNav.FSharp.SemanticLocation location = implementation.Symbol.UseLocation;
                return $"{WorkspacePaths.ToGitPath(Path.GetFullPath(location.FileName))}\0" +
                       $"{location.StartLine}\0{location.StartColumn}\0" +
                       $"{location.EndLine}\0{location.EndColumn}\0" +
                       implementation.ImplementationKind;
            }

            (int Added, bool GeneratedFiltered, bool TestFiltered) AcceptImplementations(
                CapturedFSharpSemanticProject owner,
                SemanticImplementationsCheckResult ownerCheck,
                Dictionary<string, FSharpImplementationAccumulator> destination)
            {
                int before = destination.Count;
                bool generatedWasFiltered = false;
                bool testWasFiltered = false;
                foreach (CodeNav.FSharp.SemanticImplementation implementation in
                         ownerCheck.Implementations)
                {
                    if (implementation.ProjectIndex < 0 ||
                        implementation.ProjectIndex >= owner.Nodes.Length)
                        continue;
                    CapturedFSharpSemanticNode node = owner.Nodes[implementation.ProjectIndex];
                    string fullPath = Path.GetFullPath(
                        implementation.Symbol.UseLocation.FileName);
                    int sourceIndex = Array.FindIndex(node.Input.SourceFiles, source =>
                        source.Equals(fullPath, WorkspacePaths.FileSystemPathComparison));
                    if (sourceIndex < 0) continue;
                    if (!includeTests && node.IsTest)
                    {
                        testWasFiltered = true;
                        continue;
                    }
                    if (!includeGenerated && node.SourceGenerated[sourceIndex])
                    {
                        generatedWasFiltered = true;
                        continue;
                    }

                    string key = SiteKey(implementation);
                    if (destination.TryGetValue(key, out var existing))
                    {
                        existing.TargetFrameworks.Add(node.TargetFramework);
                        continue;
                    }
                    destination[key] = new(owner, implementation,
                        node.ProjectPath, node.TargetFramework);
                }
                return (destination.Count - before, generatedWasFiltered,
                    testWasFiltered);
            }

            void MergeImplementations(
                Dictionary<string, FSharpImplementationAccumulator> source)
            {
                foreach ((string key, FSharpImplementationAccumulator value) in source)
                {
                    if (implementations.TryGetValue(key, out var existing))
                    {
                        foreach (string framework in value.TargetFrameworks)
                            existing.TargetFrameworks.Add(framework);
                    }
                    else
                    {
                        implementations[key] = value;
                    }
                }
            }

            var rootAccepted = AcceptImplementations(captured, check, implementations);
            bool rootFiltered = rootAccepted.Added == 0 &&
                                (rootAccepted.TestFiltered || rootAccepted.GeneratedFiltered);
            var groups = new List<FSharpImplementationGroup>
            {
                new(captured.SelectedContext.Project,
                    [captured.SelectedContext.TargetFramework],
                    captured.SelectedProjectIsTest, rootAccepted.Added,
                    rootFiltered
                        ? "filtered"
                        : check.QuotationBodiesExcluded
                            ? "partial"
                        : "scanned",
                    rootFiltered && rootAccepted.TestFiltered
                        ? "test_project"
                        : rootFiltered && rootAccepted.GeneratedFiltered
                            ? "generated_files"
                            : check.QuotationBodiesExcluded
                                ? "quotation_bodies"
                            : null),
            };

            int additionalDiagnosticCount = 0;
            List<FSharpReferenceDefinition> definitions = check.Targets
                .Select(target => FindFSharpReferenceDefinition(captured, target))
                .Where(definition => definition is not null)
                .Select(definition => definition!)
                .DistinctBy(definition =>
                    $"{WorkspacePaths.ToGitPath(definition.ProjectPath)}\0" +
                    $"{definition.TargetFramework}\0" +
                    $"{WorkspacePaths.ToGitPath(definition.FullPath)}\0" +
                    $"{definition.Line}\0{definition.Column}", StringComparer.Ordinal)
                .ToList();
            if (definitions.Count == 0)
            {
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_dependents_not_scanned");
                var externalCoverage = new FSharpReferencesCoverage(
                    null, 0, 0, 0, null, false, [], [],
                    ApproximateModel: SelectedFSharpProjectModel == FSharpProjectModel.Simple);
                return BuildResult(externalCoverage, partialReason);
            }

            async Task<FSharpImplementationProjectScan> ScanProjectAsync(
                ProjectRow project,
                IReadOnlyList<string> targetFrameworks,
                bool requireDeclaringProject)
            {
                if (!includeTests && project.IsTest)
                {
                    return new(new(project.Path, [], true, 0, "filtered", "test_project"),
                        true, false, 0, [], null, false);
                }

                var applicableTargetFrameworks = new SortedSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                var failures = new List<string>();
                var projectDiagnostics = new List<FSharpSemanticDiagnostic>();
                int projectDiagnosticCount = 0;
                string? projectPartialReason = null;
                bool projectQuotationBodiesExcluded = false;
                bool projectGeneratedFiltered = false;
                int before = implementations.Count;
                bool anyTargetInClosure = false;
                foreach (string projectTargetFramework in targetFrameworks)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    CapturedFSharpSemanticProject? projectCapture =
                        CaptureFSharpSemanticProjectContext(snapshot, project,
                            projectTargetFramework, cts.Token,
                            out FSharpSemanticResult? projectCaptureFailure, captureSession);
                    if (projectCapture is null)
                    {
                        failures.Add(projectCaptureFailure?.Error ??
                                     "fsharp_semantic_snapshot_failed");
                        continue;
                    }

                    bool targetResolvedForFramework = false;
                    var stagedImplementations = new Dictionary<string,
                        FSharpImplementationAccumulator>(StringComparer.Ordinal);
                    bool stagedGeneratedFiltered = false;
                    bool stagedQuotationBodiesExcluded = false;
                    string? stagedPartialReason = null;
                    int stagedDiagnosticCount = 0;
                    var stagedDiagnostics = new List<FSharpSemanticDiagnostic>();
                    foreach (FSharpReferenceDefinition definition in definitions)
                    {
                        int lookupProjectIndex = Array.FindIndex(projectCapture.Nodes, node =>
                            node.ProjectPath.Equals(definition.ProjectPath,
                                WorkspacePaths.FileSystemPathComparison) &&
                            node.AssemblyName.Equals(definition.AssemblyName,
                                StringComparison.OrdinalIgnoreCase));
                        if (lookupProjectIndex < 0) continue;
                        anyTargetInClosure = true;
                        SemanticImplementationsCheckResult projectCheck = await
                            SemanticResolver.ResolveImplementationsAsync(
                                projectCapture.Projects,
                                projectCapture.RootProjectIndex,
                                lookupProjectIndex,
                                projectCapture.Fingerprint,
                                projectCapture.BinaryReferences.Count == 0,
                                definition.FullPath,
                                definition.Line,
                                definition.Column,
                                BeforeFSharpImplementationTraversalForTest!,
                                cts.Token).ConfigureAwait(false);
                        FSharpSemanticCheckCompletedForTest?.Invoke(projectCheck.Error);
                        if (projectCheck.Symbol is null || projectCheck.Error is not null)
                        {
                            failures.Add(projectCheck.Error ??
                                         "fsharp_semantic_symbol_identity_changed");
                            continue;
                        }
                        targetResolvedForFramework = true;
                        var accepted = AcceptImplementations(projectCapture, projectCheck,
                            stagedImplementations);
                        stagedGeneratedFiltered |= accepted.GeneratedFiltered;
                        stagedQuotationBodiesExcluded |=
                            projectCheck.QuotationBodiesExcluded;
                        if (projectCheck.ErrorDiagnosticCount > 0)
                            stagedPartialReason = AppendPartialReason(stagedPartialReason,
                                "fsharp_semantic_diagnostics_present");
                        stagedDiagnosticCount += projectCheck.DiagnosticCount;
                        var projectSourcePaths = projectCapture.ClosureSourceFiles.ToHashSet(
                            WorkspacePaths.FileSystemPathComparer);
                        stagedDiagnostics.AddRange(projectCheck.Diagnostics.Select(value =>
                            MapFSharpDiagnostic(value, projectSourcePaths)));
                    }

                    if (targetResolvedForFramework)
                    {
                        if (!VerifyFSharpBinaryReferences(projectCapture.BinaryReferences,
                                cts.Token))
                        {
                            failures.Add("fsharp_semantic_reference_changed");
                            continue;
                        }
                        MergeImplementations(stagedImplementations);
                        projectGeneratedFiltered |= stagedGeneratedFiltered;
                        projectQuotationBodiesExcluded |=
                            stagedQuotationBodiesExcluded;
                        if (stagedPartialReason is not null)
                            projectPartialReason = AppendPartialReason(projectPartialReason,
                                stagedPartialReason);
                        projectDiagnosticCount += stagedDiagnosticCount;
                        projectDiagnostics.AddRange(stagedDiagnostics);
                        applicableTargetFrameworks.Add(projectTargetFramework);
                        if (projectCapture.PartialReason is not null)
                            projectPartialReason = AppendPartialReason(projectPartialReason,
                                projectCapture.PartialReason);
                    }
                }

                string? failure = failures.FirstOrDefault();
                if (!anyTargetInClosure && failure is null)
                {
                    string reason = requireDeclaringProject
                        ? "fsharp_workspace_declaring_project_not_in_closure"
                        : "inactive_project_reference";
                    return new(new(project.Path, [], project.IsTest, 0,
                            requireDeclaringProject ? "failed" : "excluded", reason),
                        !requireDeclaringProject, !requireDeclaringProject,
                        projectDiagnosticCount, projectDiagnostics,
                        projectPartialReason, false);
                }

                int added = implementations.Count - before;
                string status = failure is not null
                    ? applicableTargetFrameworks.Count == 0 ? "failed" : "partial"
                    : projectQuotationBodiesExcluded
                        ? "partial"
                        : added == 0 && projectGeneratedFiltered
                            ? "filtered"
                            : "scanned";
                string? reasonValue = failure ?? (projectQuotationBodiesExcluded
                    ? "quotation_bodies"
                    : status == "filtered" ? "generated_files" : null);
                return new(new(project.Path, applicableTargetFrameworks.ToList(),
                        project.IsTest, added, status, reasonValue),
                    failure is null && !projectQuotationBodiesExcluded,
                    false, projectDiagnosticCount, projectDiagnostics,
                    projectPartialReason, projectQuotationBodiesExcluded);
            }

            var candidates = new List<FSharpDependentCandidate>();
            var excluded = new List<FSharpDependentCoverageEntry>();
            var failed = new List<FSharpDependentCoverageEntry>();
            var discoveryFailed = new List<FSharpDependentCoverageEntry>();
            var declaringProjects = definitions
                .Select(definition => definition.ProjectPath)
                .Where(project => !project.Equals(captured.SelectedContext.Project,
                    WorkspacePaths.FileSystemPathComparison))
                .Distinct(WorkspacePaths.FileSystemPathComparer)
                .OrderBy(project => project, StringComparer.Ordinal)
                .ToList();
            int scanned = 0;
            int potentialConsumers = 0;
            int potentialConsumersEvaluated = 0;
            bool deadlineExhausted = false;
            bool candidateSetKnown = false;
            bool declaringProjectsComplete = true;
            string? declaringProjectStatus = null;
            string? declaringProjectReason = null;

            static int DeclaringStatusRank(string? status) => status switch
            {
                "failed" => 3,
                "partial" => 2,
                "filtered" => 1,
                "scanned" => 0,
                _ => -1,
            };

            void ObserveDeclaringStatus(string status, string? reason)
            {
                if (DeclaringStatusRank(status) <
                    DeclaringStatusRank(declaringProjectStatus)) return;
                declaringProjectStatus = status;
                declaringProjectReason = reason;
            }

            try
            {
                foreach (string definingPath in declaringProjects)
                {
                    ProjectRow? definingProject = snapshot.Queries.ProjectByPathForHost(
                        definingPath);
                    if (definingProject is null)
                    {
                        const string reason =
                            "fsharp_semantic_project_reference_unavailable";
                        declaringProjectsComplete = false;
                        ObserveDeclaringStatus("failed", reason);
                        groups.Add(new(definingPath, [], false, 0, "failed", reason));
                        continue;
                    }
                    string[] definingFrameworks = definitions
                        .Where(definition => definition.ProjectPath.Equals(definingPath,
                            WorkspacePaths.FileSystemPathComparison))
                        .Select(definition => definition.TargetFramework)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    FSharpImplementationProjectScan declaringScan = await ScanProjectAsync(
                        definingProject, definingFrameworks,
                        requireDeclaringProject: true).ConfigureAwait(false);
                    groups.Add(declaringScan.Group);
                    declaringProjectsComplete &= declaringScan.Complete;
                    ObserveDeclaringStatus(declaringScan.Group.Status,
                        declaringScan.Group.Reason);
                    quotationBodiesExcluded |= declaringScan.QuotationBodiesExcluded;
                    if (declaringScan.PartialReason is not null)
                        partialReason = AppendPartialReason(partialReason,
                            declaringScan.PartialReason);
                    additionalDiagnosticCount += declaringScan.DiagnosticCount;
                    diagnostics.AddRange(declaringScan.Diagnostics);
                }

                FSharpDependentDiscovery discovery = DiscoverFSharpDependentCandidates(
                    snapshot.Queries, definitions, captured.SelectedContext.Project,
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

                    string[] candidateFrameworks = candidate.Project.Tfms.Split(';',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();
                    FSharpImplementationProjectScan dependentScan = await ScanProjectAsync(
                        candidate.Project, candidateFrameworks,
                        requireDeclaringProject: false).ConfigureAwait(false);
                    groups.Add(dependentScan.Group);
                    quotationBodiesExcluded |= dependentScan.QuotationBodiesExcluded;
                    if (dependentScan.Inactive)
                    {
                        excluded.Add(new(candidate.Project.Path,
                            "inactive_project_reference"));
                        continue;
                    }
                    if (dependentScan.Complete) scanned++;
                    else failed.Add(new(candidate.Project.Path,
                        dependentScan.Group.Reason ?? "fsharp_semantic_failed"));
                    if (dependentScan.PartialReason is not null)
                        partialReason = AppendPartialReason(partialReason,
                            dependentScan.PartialReason);
                    additionalDiagnosticCount += dependentScan.DiagnosticCount;
                    diagnostics.AddRange(dependentScan.Diagnostics);
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
            bool workspaceComplete = candidateSetKnown && !deadlineExhausted &&
                                     declaringProjectsComplete && failed.Count == 0 &&
                                     pending == 0 && !incompleteExcluded &&
                                     !quotationBodiesExcluded;
            if (deadlineExhausted)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_deadline");
            if (discoveryFailed.Count > 0)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_dependent_discovery_incomplete");
            if (!declaringProjectsComplete)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_declaring_project_failed");
            if (failed.Count > 0)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_dependent_failed");
            if (quotationBodiesExcluded)
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_quotation_bodies_excluded");
            if (excluded.Any(entry => entry.Reason == "unsupported_language"))
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_unsupported_boundary");
            if (excluded.Any(entry => entry.Reason == "binary_reference"))
                partialReason = AppendPartialReason(partialReason,
                    "fsharp_workspace_binary_dependents_not_scanned");

            var coverage = new FSharpReferencesCoverage(
                candidateSetKnown ? candidates.Count : null,
                scanned, excluded.Count, failed.Count, pending,
                workspaceComplete, excluded, failed,
                potentialConsumers, potentialConsumersEvaluated,
                Math.Max(0, potentialConsumers - potentialConsumersEvaluated),
                discoveryFailed,
                declaringProjects.FirstOrDefault(),
                declaringProjectStatus,
                declaringProjectReason,
                declaringProjects, ApproximateModel: SelectedFSharpProjectModel == FSharpProjectModel.Simple);
            return BuildResult(coverage, partialReason);

            FSharpImplementationsResult BuildResult(
                FSharpReferencesCoverage coverageValue,
                string? partialReasonValue)
            {
                List<FSharpImplementationInfo> items = implementations.Values
                    .Select(value => new FSharpImplementationInfo(
                        MapSymbol(value.Implementation.Symbol, value.Captured),
                        value.Implementation.ImplementationKind,
                        value.Implementation.IsAbstract,
                        string.IsNullOrEmpty(value.Implementation.Via)
                            ? null
                            : value.Implementation.Via,
                        value.Project,
                        value.TargetFrameworks.OrderBy(item => item,
                            StringComparer.Ordinal).ToList()))
                    .OrderBy(value => value.IsAbstract ? 1 : 0)
                    .ThenBy(value => value.Symbol.Use.Path, StringComparer.Ordinal)
                    .ThenBy(value => value.Symbol.Use.StartLine)
                    .ThenBy(value => value.Symbol.Use.StartColumn)
                    .ThenBy(value => value.ImplementationKind, StringComparer.Ordinal)
                    .ToList();
                return new(symbol, items.Count, items, null,
                    captured.SelectedContext, captured.AvailableContexts,
                    captured.SelectedProjectIsTest, partialReasonValue,
                    check.DiagnosticCount + additionalDiagnosticCount,
                    diagnostics, captured.Health,
                    Groups: groups,
                    Coverage: coverageValue,
                    ResolvedFromOverride: check.ResolvedFromOverride.ToList(),
                    QuotationBodiesExcluded: quotationBodiesExcluded);
            }
        }
        catch (OperationCanceledException)
        {
            string? reason = captured is null
                ? null
                : AppendPartialReason(captured.PartialReason,
                    "fsharp_workspace_deadline");
            return new(null, null, [], "fsharp_semantic_timeout",
                captured?.SelectedContext, captured?.AvailableContexts ?? [],
                captured?.SelectedProjectIsTest ?? false, reason,
                Health: captured?.Health);
        }
        catch (Exception ex)
        {
            _log($"F# implementations request failed: {ex.GetType().Name}");
            return new(null, null, [], captured is null
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

    private static FSharpImplementationsResult FSharpImplementationsFailure(
        FSharpSemanticResult failure) =>
        new(null, null, [], failure.Error, failure.SelectedContext,
            failure.AvailableContexts, PartialReason: failure.PartialReason,
            DiagnosticCount: failure.DiagnosticCount,
            Diagnostics: failure.Diagnostics, Health: failure.Health,
            ProjectReferenceFailure: failure.ProjectReferenceFailure);
}
