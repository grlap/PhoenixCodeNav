using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.FSharp;

namespace CodeNav.Core.Semantic;

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
    List<string> AvailableTargetFrameworks,
    string? ConsumerTargetFramework = null,
    string? CompatibilityTableRow = null,
    bool MultiTargetExactMatchOnly = false,
    string? SelectedTargetFramework = null);

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

public sealed record FSharpDependentCoverageEntry(
    string Project,
    string Reason);

public sealed record FSharpReferencesCoverage(
    int? DependentsTotal,
    int DependentsScanned,
    int DependentsExcluded,
    int DependentsFailed,
    int? DependentsPending,
    bool ScansComplete,
    List<FSharpDependentCoverageEntry> Excluded,
    List<FSharpDependentCoverageEntry> Failed,
    int PotentialConsumers = 0,
    int PotentialConsumersEvaluated = 0,
    int PotentialConsumersUnevaluated = 0,
    List<FSharpDependentCoverageEntry>? DiscoveryFailed = null,
    string? DeclaringProject = null,
    string? DeclaringProjectStatus = null,
    string? DeclaringProjectReason = null,
    List<string>? DeclaringProjects = null,
    bool ApproximateModel = false)
{
    // Completing a scan of approximate inputs cannot prove real-workspace completeness.
    public bool WorkspaceComplete => ScansComplete && !ApproximateModel;
}

public sealed partial class SemanticService
{
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
    internal Action<string>? BeforeFSharpImplementationTraversalForTest { get; set; }
    internal Action<string, string>? FSharpSemanticProjectCapturedForTest { get; set; }
    internal Action<string>? BeforeFSharpPackageRootProbeForTest { get; set; }
    internal Action<string>? FSharpReferenceSnapshotCreatedForTest { get; set; }
    internal int? FSharpSemanticSourceFilesLimitForTest { get; set; }
    internal int? FSharpSemanticSourceBytesLimitForTest { get; set; }
    internal long? FSharpSemanticReferenceBytesLimitForTest { get; set; }

    private sealed record FSharpReferenceDefinition(
        string ProjectPath,
        string TargetFramework,
        string AssemblyName,
        string FullPath,
        int Line,
        int Column);

    private sealed record FSharpDependentCandidate(
        ProjectRow Project,
        int Distance,
        bool BinaryCoupled,
        bool UnsupportedLanguage);

    private sealed record FSharpDependentDiscovery(
        List<FSharpDependentCandidate> Candidates,
        int PotentialConsumers,
        int PotentialConsumersEvaluated,
        List<FSharpDependentCoverageEntry> Failed,
        bool ApproximateModel)
    {
        public bool CandidateSetKnown => !ApproximateModel && Failed.Count == 0;
    }

    private static FSharpReferenceDefinition? FindFSharpReferenceDefinition(
        CapturedFSharpSemanticProject captured,
        SemanticCheckResult check) =>
        FindFSharpReferenceDefinition(captured, check.Symbol);

    private static FSharpReferenceDefinition? FindFSharpReferenceDefinition(
        CapturedFSharpSemanticProject captured,
        CodeNav.FSharp.SemanticSymbol? symbol)
    {
        if (symbol is null || string.IsNullOrWhiteSpace(symbol.Assembly)) return null;
        var matches = new List<(int NodeIndex, CodeNav.FSharp.SemanticLocation Location)>();
        foreach (CodeNav.FSharp.SemanticLocation declaration in symbol.Declarations)
        {
            string fullPath = Path.GetFullPath(declaration.FileName);
            for (int index = 0; index < captured.Nodes.Length; index++)
            {
                CapturedFSharpSemanticNode node = captured.Nodes[index];
                if (!node.AssemblyName.Equals(symbol.Assembly,
                        StringComparison.OrdinalIgnoreCase) ||
                    !node.Input.SourceFiles.Contains(fullPath,
                        WorkspacePaths.FileSystemPathComparer)) continue;
                matches.Add((index, declaration));
            }
        }

        var contexts = matches.Select(match => captured.Nodes[match.NodeIndex])
            .Select(node => (node.ProjectPath, node.TargetFramework))
            .Distinct()
            .ToList();
        if (contexts.Count != 1) return null;
        CapturedFSharpSemanticNode definingNode = captured.Nodes[matches[0].NodeIndex];
        CodeNav.FSharp.SemanticLocation selected = matches
            .Where(match => match.NodeIndex == matches[0].NodeIndex)
            .Select(match => match.Location)
            .OrderBy(location => location.Role switch
            {
                "implementation" => 0,
                "signature" => 1,
                _ => 2,
            })
            .ThenBy(location => location.FileName, WorkspacePaths.FileSystemPathComparer)
            .ThenBy(location => location.StartLine)
            .ThenBy(location => location.StartColumn)
            .First();
        return new(definingNode.ProjectPath, definingNode.TargetFramework,
            definingNode.AssemblyName,
            Path.GetFullPath(selected.FileName),
            selected.StartLine, selected.StartColumn + 1);
    }

    private FSharpDependentDiscovery DiscoverFSharpDependentCandidates(
        IndexQueries queries,
        FSharpReferenceDefinition definition,
        string selectedProjectPath,
        CancellationToken cancellationToken) =>
        DiscoverFSharpDependentCandidates(queries, [definition], selectedProjectPath,
            cancellationToken);

    private FSharpDependentDiscovery DiscoverFSharpDependentCandidates(
        IndexQueries queries,
        IReadOnlyList<FSharpReferenceDefinition> definitions,
        string selectedProjectPath,
        CancellationToken cancellationToken)
    {
        List<SemanticProjectEdge> persistedEdges =
            queries.FSharpWorkspaceReferenceEdges(cancellationToken);
        List<ProjectRow> allProjects = queries.AllProjects(cancellationToken);
        List<ProjectRow> potentialConsumers = allProjects
            .Where(project => project.Language.Equals("fs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(project => project.Path, StringComparer.Ordinal)
            .ToList();
        var discoveryFailed = new List<FSharpDependentCoverageEntry>();
        int potentialConsumersEvaluated = 0;
        var edgeByIdentity = new Dictionary<string, SemanticProjectEdge>(
            WorkspacePaths.FileSystemPathComparer);

        void AddEdge(SemanticProjectEdge edge)
        {
            string identity = $"{WorkspacePaths.ToGitPath(edge.FromPath)}\0" +
                              $"{WorkspacePaths.ToGitPath(edge.ToPath)}\0{edge.Kind}";
            edgeByIdentity.TryAdd(identity, edge);
        }

        foreach (SemanticProjectEdge edge in persistedEdges) AddEdge(edge);
        foreach (ProjectRow project in potentialConsumers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? failureReason = null;
            if (project.LoadStatus.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
            {
                failureReason = "fsharp_semantic_project_reference_unavailable";
            }
            else
            {
                string[] targetFrameworks = project.Tfms.Split(';',
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                if (targetFrameworks.Length == 0)
                {
                    failureReason = "fsharp_type_check_context_unavailable";
                }
                else
                {
                    FSharpSemanticOptionsSnapshot? simpleReferences = null;
                    if (SelectedFSharpProjectModel == ProjectModelMode.Simple)
                    {
                        string? xml = queries.ContentByPathBounded(project.Path,
                            IndexBuilder.MaxStructuralFileBytes, cancellationToken);
                        if (xml is not null)
                            simpleReferences = SimpleProjectModelBuilder.BuildFSharpReferences(
                                project.Path, xml, cancellationToken);
                    }
                    foreach (string targetFramework in SelectedFSharpProjectModel == ProjectModelMode.Simple
                                 ? targetFrameworks.Take(1) : targetFrameworks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FSharpSemanticOptionsSnapshot? options =
                            SelectedFSharpProjectModel == ProjectModelMode.Simple ? simpleReferences :
                            EvaluateFSharpSemanticOptions(queries, project, targetFramework,
                                new ProjectFileParser.FSharpSemanticEvaluationBudget(),
                                cancellationToken, out _, out _);
                        if (options is null || options.Error is not null)
                        {
                            failureReason ??= options?.Error ??
                                              "fsharp_semantic_project_reference_unavailable";
                            continue;
                        }

                        foreach (FSharpProjectReferenceSnapshot reference in
                                 options.ProjectReferences)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ProjectRow? target = queries.ProjectByPathForHost(
                                reference.ProjectPath);
                            if (target is null)
                            {
                                failureReason ??=
                                    "fsharp_semantic_project_reference_unavailable";
                                continue;
                            }
                            AddEdge(new(project.Id, project.Path, project.Name,
                                project.Language, target.Id, target.Path, target.Name,
                                target.Language));
                        }
                    }
                }
            }

            if (failureReason is null) potentialConsumersEvaluated++;
            else discoveryFailed.Add(new(project.Path, failureReason));
        }

        List<SemanticProjectEdge> edges = edgeByIdentity.Values.ToList();
        Dictionary<string, List<SemanticProjectEdge>> byTargetPath = edges
            .GroupBy(edge => edge.ToPath, WorkspacePaths.FileSystemPathComparer)
            .ToDictionary(group => group.Key, group => group.ToList(),
                WorkspacePaths.FileSystemPathComparer);
        List<SemanticProjectEdge> assemblySeedEdges = edges.Where(edge =>
                edge.Kind.Equals("assembly", StringComparison.OrdinalIgnoreCase) &&
                definitions.Any(definition => edge.ToProject.Equals(
                    definition.AssemblyName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var frontier = new Queue<(string Path, int Distance, bool Binary, bool Unsupported)>();
        foreach (FSharpReferenceDefinition definition in definitions)
            frontier.Enqueue((definition.ProjectPath, 0, false, false));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var best = new Dictionary<string, (int Rank, int Distance)>(
            WorkspacePaths.FileSystemPathComparer);
        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = frontier.Dequeue();
            string stateKey = $"{WorkspacePaths.ToGitPath(current.Path)}\0{current.Binary}\0{current.Unsupported}";
            if (!visited.Add(stateKey)) continue;
            IEnumerable<SemanticProjectEdge> incoming =
                byTargetPath.GetValueOrDefault(current.Path) ?? [];
            if (current.Distance == 0) incoming = incoming.Concat(assemblySeedEdges);
            foreach (SemanticProjectEdge edge in incoming
                         .OrderBy(edge => edge.FromPath, StringComparer.Ordinal)
                         .ThenBy(edge => edge.Kind, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool binary = current.Binary || edge.Kind.Equals("assembly",
                    StringComparison.OrdinalIgnoreCase);
                bool unsupported = current.Unsupported ||
                                   !edge.FromLanguage.Equals("fs",
                                       StringComparison.OrdinalIgnoreCase) ||
                                   !edge.ToLanguage.Equals("fs",
                                       StringComparison.OrdinalIgnoreCase);
                int rank = !binary && !unsupported ? 0 : binary ? 1 : 2;
                int distance = current.Distance + 1;
                if (!best.TryGetValue(edge.FromPath, out var prior) || rank < prior.Rank ||
                    rank == prior.Rank && distance < prior.Distance)
                {
                    best[edge.FromPath] = (rank, distance);
                }
                frontier.Enqueue((edge.FromPath, distance, binary, unsupported));
            }
        }

        List<FSharpDependentCandidate> candidates = best
            .Where(pair => !definitions.Any(definition => pair.Key.Equals(
                               definition.ProjectPath,
                               WorkspacePaths.FileSystemPathComparison)) &&
                           !pair.Key.Equals(selectedProjectPath,
                               WorkspacePaths.FileSystemPathComparison))
            .Select(pair => (Project: queries.ProjectByPathForHost(pair.Key), pair.Value))
            .Where(pair => pair.Project is not null)
            .Select(pair => new FSharpDependentCandidate(pair.Project!, pair.Value.Distance,
                pair.Value.Rank == 1, pair.Value.Rank == 2))
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Project.Path, StringComparer.Ordinal)
            .ToList();
        return new(candidates, potentialConsumers.Count,
            potentialConsumersEvaluated, discoveryFailed,
            SelectedFSharpProjectModel == ProjectModelMode.Simple);
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

    private static void AddPartialReasons(SortedSet<string> destination, string? reasons)
    {
        if (reasons is not { Length: > 0 }) return;
        foreach (string reason in reasons.Split(';', StringSplitOptions.RemoveEmptyEntries))
            destination.Add(reason);
    }
}
