using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.FSharp;
using Microsoft.Win32.SafeHandles;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace CodeNav.Core.Semantic;

public sealed partial class SemanticService
{

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
        string TargetFramework,
        string AssemblyName,
        string Fingerprint,
        bool[] SourceGenerated,
        string? PartialReason,
        bool IsTest);

    private sealed record PreparedFSharpSemanticNode(
        string Key,
        SemanticProjectInput Input,
        string ProjectPath,
        string TargetFramework,
        string AssemblyName,
        string Fingerprint,
        bool[] SourceGenerated,
        string? PartialReason,
        bool IsTest,
        string[] CompilerReferenceKeys,
        string[] DescendantKeys,
        List<FSharpBinaryReferenceSnapshot> BinaryReferences);

    private sealed class FSharpSemanticCaptureSession
    {
        public FSharpSemanticCaptureSession(int sourceFilesLimit, int sourceBytesLimit,
            long referenceBytesLimit)
        {
            BudgetPolicy = new(sourceFilesLimit, sourceBytesLimit, referenceBytesLimit,
                ShareFSharpSemanticBudgetsAcrossProjectClosure);
        }

        public FSharpSemanticClosureBudgetPolicy BudgetPolicy { get; }
        public List<PreparedFSharpSemanticNode> Nodes { get; } = [];
        public Dictionary<string, PreparedFSharpSemanticNode> Completed { get; } =
            new(WorkspacePaths.FileSystemPathComparer);
        public HashSet<string> Active { get; } =
            new(WorkspacePaths.FileSystemPathComparer);
        public List<FSharpBinaryReferenceSnapshot> BinaryReferences { get; } = [];
        public string? ReferenceSnapshotDirectory { get; set; }
    }

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

    private sealed record FSharpProjectReferenceTargetFrameworkSelection(
        string? TargetFramework,
        string CompatibilityTableRow,
        bool MultiTargetExactMatchOnly = false);

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

    internal static bool TrySelectFSharpProjectReferenceTargetFramework(
        string consumerTargetFramework,
        IReadOnlyList<string> availableTargetFrameworks,
        out string? selectedTargetFramework,
        out string compatibilityTableRow,
        out bool multiTargetExactMatchOnly)
    {
        FSharpProjectReferenceTargetFrameworkSelection selection =
            SelectFSharpProjectReferenceTargetFramework(consumerTargetFramework,
                availableTargetFrameworks);
        selectedTargetFramework = selection.TargetFramework;
        compatibilityTableRow = selection.CompatibilityTableRow;
        multiTargetExactMatchOnly = selection.MultiTargetExactMatchOnly;
        return selectedTargetFramework is not null;
    }

    private static FSharpProjectReferenceTargetFrameworkSelection
        SelectFSharpProjectReferenceTargetFramework(string consumerTargetFramework,
            IReadOnlyList<string> availableTargetFrameworks)
    {
        string consumer = consumerTargetFramework.Trim();
        string? exact = availableTargetFrameworks.FirstOrDefault(targetFramework =>
            targetFramework.Equals(consumer, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return new(exact, $"Exact target-framework match '{exact}'.");

        if (availableTargetFrameworks.Count == 0)
        {
            return new(null,
                "The referenced project exposes no evaluated target-framework context.");
        }

        if (availableTargetFrameworks.Count != 1)
        {
            return new(null,
                "Compatible selection is exact-match-only when the referenced project " +
                "exposes more than one evaluated target framework.",
                MultiTargetExactMatchOnly: true);
        }

        string child = availableTargetFrameworks[0];
        if (!TryParseNetStandardTargetFramework(child, out int childStandard))
        {
            return new(null,
                $"The Microsoft .NET Standard table has no compatibility row for referenced " +
                $"target framework '{child}'.");
        }

        bool compatible = IsNetStandardCompatibleConsumer(consumer, childStandard,
            out string tableRow);
        return new(compatible ? child : null, tableRow);
    }

    private static bool IsNetStandardCompatibleConsumer(string consumerTargetFramework,
        int standardVersion, out string tableRow)
    {
        string consumer = consumerTargetFramework.Trim().ToLowerInvariant();
        if (consumer.Contains('-') &&
            !consumer.StartsWith("net", StringComparison.Ordinal))
        {
            tableRow = $"Consumer '{consumerTargetFramework}' is not a recognized .NET " +
                       "implementation TFM in the Microsoft .NET Standard table.";
            return false;
        }

        if (TryParseNetStandardTargetFramework(consumer, out int consumerStandard))
        {
            bool compatible = consumerStandard >= standardVersion;
            tableRow = compatible
                ? $".NET Standard is cumulative: {FormatNetStandard(consumerStandard)} " +
                  $"includes {FormatNetStandard(standardVersion)}."
                : $".NET Standard is cumulative: {FormatNetStandard(consumerStandard)} " +
                  $"does not include the higher {FormatNetStandard(standardVersion)}.";
            return compatible;
        }

        if (TryParseNetCoreAppTargetFramework(consumer, out Version? coreVersion))
        {
            Version minimum = standardVersion <= 16
                ? new(1, 0)
                : standardVersion == 20
                    ? new(2, 0)
                    : new(3, 0);
            bool compatible = coreVersion! >= minimum;
            tableRow = compatible
                ? $".NET Core {coreVersion!.Major}.{coreVersion.Minor} implements " +
                  $"{FormatNetStandard(standardVersion)}."
                : $".NET Core {coreVersion!.Major}.{coreVersion.Minor} does not implement " +
                  $"{FormatNetStandard(standardVersion)}; the table starts at " +
                  $".NET Core {minimum.Major}.{minimum.Minor}.";
            return compatible;
        }

        if (TryParseModernNetTargetFramework(consumer, out Version? modernVersion))
        {
            tableRow = $".NET {modernVersion!.Major}.{modernVersion.Minor} implements " +
                       $"{FormatNetStandard(standardVersion)}.";
            return true;
        }

        if (TryParseNetFrameworkTargetFramework(consumer, out int frameworkVersion))
        {
            int? minimum = standardVersion switch
            {
                10 or 11 => 450,
                12 => 451,
                13 => 460,
                14 or 15 or 16 or 20 => 461,
                _ => null,
            };
            if (minimum is null)
            {
                tableRow = $".NET Framework does not implement " +
                           $"{FormatNetStandard(standardVersion)} (N/A in the Microsoft " +
                           ".NET Standard table).";
                return false;
            }
            bool compatible = frameworkVersion >= minimum.Value;
            tableRow = compatible
                ? $".NET Framework {FormatNetFramework(frameworkVersion)} implements " +
                  $"{FormatNetStandard(standardVersion)}."
                : $".NET Framework {FormatNetFramework(frameworkVersion)} does not implement " +
                  $"{FormatNetStandard(standardVersion)}; the table starts at .NET Framework " +
                  $"{FormatNetFramework(minimum.Value)}.";
            return compatible;
        }

        tableRow = $"Consumer '{consumerTargetFramework}' is not a recognized .NET, .NET Core, " +
                   ".NET Framework, or .NET Standard TFM in the Microsoft .NET Standard table.";
        return false;
    }

    private static bool TryParseNetStandardTargetFramework(string targetFramework,
        out int version)
    {
        version = 0;
        string normalized = targetFramework.Trim().ToLowerInvariant();
        if (!normalized.StartsWith("netstandard", StringComparison.Ordinal) ||
            normalized.Contains('-'))
            return false;
        version = normalized["netstandard".Length..] switch
        {
            "1.0" => 10,
            "1.1" => 11,
            "1.2" => 12,
            "1.3" => 13,
            "1.4" => 14,
            "1.5" => 15,
            "1.6" => 16,
            "2.0" => 20,
            "2.1" => 21,
            _ => 0,
        };
        return version != 0;
    }

    private static bool TryParseNetCoreAppTargetFramework(string targetFramework,
        out Version? version)
    {
        version = null;
        if (!targetFramework.StartsWith("netcoreapp", StringComparison.Ordinal) ||
            targetFramework.Contains('-'))
            return false;
        return Version.TryParse(targetFramework["netcoreapp".Length..], out version) &&
               version is { Major: >= 1 };
    }

    private static bool TryParseModernNetTargetFramework(string targetFramework,
        out Version? version)
    {
        version = null;
        string baseTargetFramework = targetFramework.Split('-', 2)[0];
        if (!baseTargetFramework.StartsWith("net", StringComparison.Ordinal) ||
            baseTargetFramework.StartsWith("netstandard", StringComparison.Ordinal) ||
            baseTargetFramework.StartsWith("netcoreapp", StringComparison.Ordinal))
            return false;
        return Version.TryParse(baseTargetFramework[3..], out version) &&
               version is { Major: >= 5 };
    }

    private static bool TryParseNetFrameworkTargetFramework(string targetFramework,
        out int version)
    {
        version = targetFramework switch
        {
            "net45" => 450,
            "net451" => 451,
            "net452" => 452,
            "net46" => 460,
            "net461" => 461,
            "net462" => 462,
            "net47" => 470,
            "net471" => 471,
            "net472" => 472,
            "net48" => 480,
            "net481" => 481,
            _ => 0,
        };
        return version != 0;
    }

    private static bool IsNetFrameworkTargetFramework(string targetFramework) =>
        TryParseNetFrameworkTargetFramework(targetFramework.Trim().ToLowerInvariant(), out _);

    private static string FormatNetStandard(int version) =>
        $"netstandard{version / 10}.{version % 10}";

    private static string FormatNetFramework(int version) => version switch
    {
        450 => "4.5",
        451 => "4.5.1",
        452 => "4.5.2",
        460 => "4.6",
        461 => "4.6.1",
        462 => "4.6.2",
        470 => "4.7",
        471 => "4.7.1",
        472 => "4.7.2",
        480 => "4.8",
        481 => "4.8.1",
        _ => version.ToString(),
    };

    private sealed record CapturedFSharpSemanticProject(
        SemanticProjectInput[] Projects,
        CapturedFSharpSemanticNode[] Nodes,
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
        public CapturedFSharpSemanticNode RootNode => Nodes[RootProjectIndex];
        public string[] SourceFiles => RootProject.SourceFiles;
        public string[] SourceTexts => RootProject.SourceTexts;
        public bool[] SourceGenerated => RootSourceGenerated;
    }

    private FSharpSemanticCaptureSession CreateFSharpSemanticCaptureSession() => new(
        FSharpSemanticSourceFilesLimitForTest ?? MaxFSharpSemanticSourceFiles,
        FSharpSemanticSourceBytesLimitForTest ?? MaxFSharpSemanticSourceBytes,
        FSharpSemanticReferenceBytesLimitForTest ?? MaxFSharpSemanticReferenceBytes);

    private FSharpSemanticOptionsSnapshot? EvaluateFSharpSemanticOptions(
        IndexQueries queries,
        ProjectRow owner,
        string targetFramework,
        ProjectFileParser.FSharpSemanticEvaluationBudget evaluationBudget,
        CancellationToken cancellationToken,
        out string? projectXml,
        out Dictionary<string, string> evaluatedAuthorityInputs)
    {
        projectXml = queries.ContentByPathBounded(owner.Path,
            IndexBuilder.MaxStructuralFileBytes, cancellationToken);
        var authorityInputs = new Dictionary<string, string>(
            WorkspacePaths.FileSystemPathComparer);
        evaluatedAuthorityInputs = authorityInputs;
        if (projectXml is null) return null;

        DirectoryBuildAuthorityPaths directoryBuild =
            queries.ApplicableDirectoryBuildAuthority(owner.Path);
        DirectoryPackagesAuthorityPath directoryPackages =
            queries.ApplicableDirectoryPackagesAuthority(owner.Path);
        return ProjectFileParser.ParseFSharpSemanticOptionsClosureSnapshot(
            evaluationBudget, owner.Path, projectXml, owner.Tfms, targetFramework,
            importPath =>
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
                    authorityInputs[imported.Path] = content;
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
            hasAmbiguousDirectoryPackagesAuthority: directoryPackages.PathAmbiguous,
            existsResolver: path => ResolveIndexedFSharpExists(queries, path));
    }

    internal static bool? ResolveIndexedFSharpExists(IndexQueries queries, string path)
    {
        if (!WorkspaceScanner.IsIndexedFilePath(path)) return null;
        // A row proves presence. Its absence cannot distinguish a missing path from a
        // link/non-regular input the no-follow scanner deliberately skipped, so it is
        // never promoted to a false compiler fact.
        return queries.FileByPathForHost(path) is not null ? true : null;
    }

    private CapturedFSharpSemanticProject? CaptureFSharpSemanticProject(
        string path,
        string? requestedProject,
        string? requestedTargetFramework,
        CancellationToken cancellationToken,
        out FSharpSemanticResult? failure)
    {
        using IndexReadSnapshot? snapshot = _manager.TryOpenReviewSnapshot(cancellationToken);
        if (snapshot is null)
        {
            failure = new(null, "index_snapshot_unavailable", null, []);
            return null;
        }

        return CaptureFSharpSemanticProject(snapshot, path, requestedProject,
            requestedTargetFramework, cancellationToken, out failure);
    }

    private CapturedFSharpSemanticProject? CaptureFSharpSemanticProject(
        IndexReadSnapshot snapshot,
        string path,
        string? requestedProject,
        string? requestedTargetFramework,
        CancellationToken cancellationToken,
        out FSharpSemanticResult? failure,
        FSharpSemanticCaptureSession? captureSession = null)
    {
        failure = null;

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
            contexts, cancellationToken, out failure, captureSession);
    }

    private CapturedFSharpSemanticProject? CaptureFSharpSemanticProjectContext(
        IndexReadSnapshot snapshot,
        ProjectRow owner,
        string targetFramework,
        CancellationToken cancellationToken,
        out FSharpSemanticResult? failure,
        FSharpSemanticCaptureSession captureSession)
    {
        var contexts = owner.Tfms.Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tfm => new FSharpTypeCheckContext(owner.Path, tfm))
            .OrderBy(context => context.TargetFramework.Equals(targetFramework,
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(context => context.TargetFramework, StringComparer.Ordinal)
            .ToList();
        FSharpTypeCheckContext? selected = contexts.FirstOrDefault(context =>
            context.TargetFramework.Equals(targetFramework, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            failure = new(null, "fsharp_type_check_context_not_found", null, contexts,
                Health: snapshot.Health);
            return null;
        }

        return CaptureFSharpSemanticClosure(snapshot.Queries, snapshot.Health, owner, null,
            selected, contexts, cancellationToken, out failure, captureSession);
    }

    private CapturedFSharpSemanticProject? CaptureFSharpSemanticClosure(
        IndexQueries queries,
        IndexHealth health,
        ProjectRow rootOwner,
        string? targetPath,
        FSharpTypeCheckContext selected,
        List<FSharpTypeCheckContext> contexts,
        CancellationToken cancellationToken,
        out FSharpSemanticResult? failure,
        FSharpSemanticCaptureSession? sharedSession = null)
    {
        failure = null;
        FSharpSemanticResult? capturedFailure = null;
        FSharpSemanticCaptureSession session = sharedSession ??
                                               CreateFSharpSemanticCaptureSession();
        bool ownsSession = sharedSession is null;
        bool ownershipTransferred = false;

        FSharpSemanticResult Failure(string error, string? nodeReasons = null,
            FSharpProjectReferenceFailure? projectReferenceFailure = null)
        {
            return new(null, error, selected, contexts,
                nodeReasons, Health: health,
                ProjectReferenceFailure: projectReferenceFailure);
        }

        PreparedFSharpSemanticNode? CaptureNode(ProjectRow owner, string nodeTargetFramework,
            string? requiredSource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = $"{owner.Path}\0{nodeTargetFramework.ToUpperInvariant()}";
            if (session.Completed.TryGetValue(key, out PreparedFSharpSemanticNode? completed))
            {
                if (requiredSource is not null && !completed.Input.SourceFiles.Contains(
                        WorkspaceAbsolutePath(requiredSource),
                        WorkspacePaths.FileSystemPathComparer))
                {
                    capturedFailure = Failure("fsharp_semantic_target_not_in_project",
                        completed.PartialReason);
                    return null;
                }
                return completed;
            }
            if (!session.Active.Add(key))
            {
                capturedFailure = Failure("fsharp_semantic_project_reference_cycle");
                return null;
            }

            try
            {
                var nodeReasons = new SortedSet<string>(StringComparer.Ordinal);
                FSharpSemanticResult NodeFailure(string error, string? reasons = null,
                    FSharpProjectReferenceFailure? projectReferenceFailure = null)
                {
                    AddPartialReasons(nodeReasons, reasons);
                    return Failure(error, JoinPartialReasons(nodeReasons),
                        projectReferenceFailure);
                }

                FSharpSemanticNodeBudgets nodeBudgets = session.BudgetPolicy.ForNode();
                if (!owner.Language.Equals("fs", StringComparison.OrdinalIgnoreCase))
                {
                    capturedFailure = NodeFailure("fsharp_semantic_project_references_unsupported");
                    return null;
                }
                if (owner.LoadStatus.StartsWith("failed:", StringComparison.OrdinalIgnoreCase))
                {
                    capturedFailure = NodeFailure("fsharp_semantic_project_reference_unavailable");
                    return null;
                }

                string[] availableTargetFrameworks = owner.Tfms.Split(';',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tfm => tfm, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!availableTargetFrameworks.Contains(nodeTargetFramework,
                        StringComparer.OrdinalIgnoreCase))
                {
                    capturedFailure = NodeFailure(
                        "fsharp_semantic_project_reference_target_framework_unavailable",
                        projectReferenceFailure: new(owner.Path,
                            availableTargetFrameworks.ToList(), nodeTargetFramework,
                            $"No evaluated context matches '{nodeTargetFramework}'."));
                    return null;
                }

                FSharpSemanticOptionsSnapshot? evaluatedOptions =
                    EvaluateFSharpSemanticOptions(queries, owner, nodeTargetFramework,
                        nodeBudgets.Evaluation, cancellationToken, out string? projectXml,
                        out Dictionary<string, string> evaluatedAuthorityInputs);
                if (evaluatedOptions is null || projectXml is null)
                {
                    capturedFailure = NodeFailure("fsharp_semantic_project_reference_unavailable");
                    return null;
                }
                FSharpSemanticOptionsSnapshot options = evaluatedOptions;
                AddPartialReasons(nodeReasons, options.PartialReason);
                if (options.Error is { } optionError)
                {
                    capturedFailure = NodeFailure(optionError, options.PartialReason);
                    return null;
                }
                if (requiredSource is not null && !options.SourceFiles.Contains(requiredSource,
                        WorkspacePaths.FileSystemPathComparer))
                {
                    capturedFailure = NodeFailure("fsharp_semantic_target_not_in_project",
                        options.PartialReason);
                    return null;
                }

                var fullSourcePaths = new List<string>(options.SourceFiles.Count);
                var sourceTexts = new List<string>(options.SourceFiles.Count);
                var sourceGenerated = new List<bool>(options.SourceFiles.Count);
                if (!nodeBudgets.Content.TryReserveSources(options.SourceFiles.Count, 0,
                        out string? sourceLimitError))
                {
                    capturedFailure = NodeFailure(sourceLimitError!, options.PartialReason);
                    return null;
                }
                foreach (string sourcePath in options.SourceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryWorkspaceAbsolutePath(sourcePath, out string? fullSourcePath) ||
                        queries.FileByPathForHost(sourcePath) is not { Language: "fs" } sourceFile ||
                        sourceFile.Size > IndexBuilder.MaxStructuralFileBytes)
                    {
                        capturedFailure = NodeFailure("fsharp_semantic_source_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    BeforeFSharpSemanticSourceReadForTest?.Invoke(sourceFile.Path);
                    string? text = queries.ContentByPathBounded(sourceFile.Path,
                        IndexBuilder.MaxStructuralFileBytes, cancellationToken);
                    if (text is null)
                    {
                        capturedFailure = NodeFailure("fsharp_semantic_source_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    int sourceBytes = System.Text.Encoding.UTF8.GetByteCount(text);
                    if (!nodeBudgets.Content.TryReserveSources(0, sourceBytes,
                            out sourceLimitError))
                    {
                        capturedFailure = NodeFailure(sourceLimitError!, options.PartialReason);
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
                        nodeTargetFramework, packageReferences, evaluatedAuthorityInputs,
                        cancellationToken, out FSharpPackageAssetsSnapshot? packageAssets,
                        out string? packageError))
                {
                    capturedFailure = NodeFailure(packageError!, options.PartialReason);
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
                    capturedFailure = NodeFailure("fsharp_semantic_reference_limit",
                        options.PartialReason);
                    return null;
                }

                if (options.AssemblyName.Length == 0 || options.AssemblyName.Length > 180 ||
                    options.AssemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    options.AssemblyName.Contains('/') || options.AssemblyName.Contains('\\'))
                {
                    capturedFailure = NodeFailure("fsharp_semantic_assembly_name_unavailable",
                        options.PartialReason);
                    return null;
                }
                var childNodes = new List<PreparedFSharpSemanticNode>(
                    options.ProjectReferences.Count);
                foreach (FSharpProjectReferenceSnapshot projectReference in
                         options.ProjectReferences)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProjectRow? child = queries.ProjectByPathForHost(
                        projectReference.ProjectPath);
                    if (child is null)
                    {
                        capturedFailure = NodeFailure(
                            "fsharp_semantic_project_reference_unavailable");
                        return null;
                    }
                    if (!child.Language.Equals("fs", StringComparison.OrdinalIgnoreCase))
                    {
                        capturedFailure = NodeFailure(
                            "fsharp_semantic_project_references_unsupported");
                        return null;
                    }
                    string[] childTargetFrameworks = child.Tfms.Split(';',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(targetFramework => targetFramework,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    FSharpProjectReferenceTargetFrameworkSelection childSelection =
                        SelectFSharpProjectReferenceTargetFramework(nodeTargetFramework,
                            childTargetFrameworks);
                    if (childSelection.TargetFramework is null)
                    {
                        capturedFailure = NodeFailure(
                            "fsharp_semantic_project_reference_target_framework_unavailable",
                            projectReferenceFailure: new(child.Path,
                                childTargetFrameworks.ToList(), nodeTargetFramework,
                                childSelection.CompatibilityTableRow,
                                childSelection.MultiTargetExactMatchOnly));
                        return null;
                    }
                    PreparedFSharpSemanticNode? childNode = CaptureNode(child,
                        childSelection.TargetFramework,
                        requiredSource: null);
                    if (childNode is null) return null;
                    AddPartialReasons(nodeReasons, childNode.PartialReason);
                    if (!childNodes.Any(existing => existing.Key.Equals(childNode.Key,
                            WorkspacePaths.FileSystemPathComparison)))
                        childNodes.Add(childNode);
                }
                var descendantKeys = new SortedSet<string>(
                    WorkspacePaths.FileSystemPathComparer);
                foreach (PreparedFSharpSemanticNode childNode in childNodes)
                {
                    descendantKeys.Add(childNode.Key);
                    descendantKeys.UnionWith(childNode.DescendantKeys);
                }
                string[] compilerReferenceKeys = options.ProjectReferencesTransitive
                    ? descendantKeys.ToArray()
                    : childNodes.Select(child => child.Key).ToArray();

                var referencePaths = new List<string>();
                var referenceIdentities = new List<string>();
                IReadOnlyList<string> frameworkReferences =
                    ReferenceAssemblyLocator.FrameworkReferencePaths(nodeTargetFramework,
                        out string? frameworkDirectory);
                if (frameworkDirectory is null || frameworkReferences.Count == 0)
                {
                    capturedFailure = NodeFailure("fsharp_framework_references_unavailable",
                        options.PartialReason, new(owner.Path,
                            availableTargetFrameworks.ToList(),
                            SelectedTargetFramework: nodeTargetFramework));
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
                        capturedFailure = NodeFailure("fsharp_semantic_reference_unresolved",
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
                        nodeTargetFramework, out bool exactTargetAsset);
                    if (fsharpCore is null)
                    {
                        capturedFailure = NodeFailure("fsharp_core_reference_unavailable",
                            options.PartialReason, new(owner.Path,
                                availableTargetFrameworks.ToList(),
                                SelectedTargetFramework: nodeTargetFramework));
                        return null;
                    }
                    referencePaths.Add(fsharpCore);
                    referenceIdentities.Add(ReferenceIdentity(fsharpCore));
                    nodeReasons.Add("fsharp_core_reference_defaulted");
                    if (!exactTargetAsset)
                        nodeReasons.Add("fsharp_core_reference_host_fallback");
                }
                if (options.HintPathReferences.Count > 0)
                    nodeReasons.Add("fsharp_binary_references_snapshotted");
                if (packageReferences.Count > 0)
                    nodeReasons.Add("fsharp_package_references_snapshotted");

                var nodeBinaryReferences = new List<FSharpBinaryReferenceSnapshot>();
                if (options.HintPathReferences.Count > 0 ||
                    resolvedPackageAssets.CompileAssets.Count > 0)
                {
                    session.ReferenceSnapshotDirectory ??= Directory.CreateTempSubdirectory(
                        "PhoenixCodeNav.FSharp.Reference.").FullName;
                }
                foreach (string hintPath in options.HintPathReferences)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FSharpBinaryReferenceSnapshot? binary = CaptureFSharpBinaryReference(
                        hintPath, session.ReferenceSnapshotDirectory!,
                        session.BinaryReferences.Count,
                        nodeBudgets.Content.RemainingReferenceBytes, cancellationToken,
                        out bool bytesExceeded);
                    if (binary is null)
                    {
                        capturedFailure = NodeFailure(bytesExceeded
                                ? "fsharp_semantic_reference_bytes_limit"
                                : "fsharp_semantic_reference_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    session.BinaryReferences.Add(binary);
                    nodeBinaryReferences.Add(binary);
                    if (!nodeBudgets.Content.TryReserveReferenceBytes(binary.Length))
                    {
                        capturedFailure = NodeFailure("fsharp_semantic_reference_bytes_limit",
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
                        packageAsset.PackageRoot, session.ReferenceSnapshotDirectory!,
                        session.BinaryReferences.Count,
                        nodeBudgets.Content.RemainingReferenceBytes,
                        cancellationToken, out bool bytesExceeded);
                    if (binary is null)
                    {
                        capturedFailure = NodeFailure(bytesExceeded
                                ? "fsharp_semantic_reference_bytes_limit"
                                : "fsharp_semantic_package_asset_unavailable",
                            options.PartialReason);
                        return null;
                    }
                    session.BinaryReferences.Add(binary);
                    nodeBinaryReferences.Add(binary);
                    if (!nodeBudgets.Content.TryReserveReferenceBytes(binary.Length))
                    {
                        capturedFailure = NodeFailure("fsharp_semantic_reference_bytes_limit",
                            options.PartialReason);
                        return null;
                    }
                    referencePaths.Add(binary.SnapshotFullPath);
                    referenceIdentities.Add(
                        $"{binary.SourceIdentity}|{binary.Length}|{binary.Sha256}");
                }
                if (resolvedPackageAssets.Identity.Length > 0)
                    referenceIdentities.Add(resolvedPackageAssets.Identity);
                foreach (PreparedFSharpSemanticNode child in childNodes)
                {
                    referenceIdentities.Add(
                        $"project:{child.ProjectPath}|{child.Fingerprint}");
                }

                referencePaths = referencePaths
                    .Distinct(WorkspacePaths.FileSystemPathComparer)
                    .OrderBy(reference => reference, WorkspacePaths.FileSystemPathComparer)
                    .ToList();
                string fingerprint = FSharpSemanticFingerprint(owner.Path,
                    nodeTargetFramework, projectXml, options.CommandLineArgs,
                    fullSourcePaths, sourceTexts, referenceIdentities,
                    options.ExistsDependencies);
                string outputPath = Path.Combine(Path.GetTempPath(),
                    "PhoenixCodeNav.FSharp", fingerprint, $"{options.AssemblyName}.dll");
                var commandLineArgs = new List<string>
                {
                    "--simpleresolution",
                    "--noframework",
                    "--target:library",
                    IsNetFrameworkTargetFramework(nodeTargetFramework)
                        ? "--targetprofile:mscorlib"
                        : "--targetprofile:netcore",
                    "--debug:portable",
                    "--optimize-",
                    $"--out:{outputPath}",
                };
                commandLineArgs.AddRange(options.CommandLineArgs);
                commandLineArgs.AddRange(compilerReferenceKeys.Select(referenceKey =>
                    $"-r:{session.Completed[referenceKey].Input.OutputFile}"));
                commandLineArgs.AddRange(referencePaths.Select(reference => $"-r:{reference}"));
                commandLineArgs.AddRange(fullSourcePaths);

                var input = new SemanticProjectInput(WorkspaceAbsolutePath(owner.Path),
                    fullSourcePaths.ToArray(), sourceTexts.ToArray(),
                    commandLineArgs.ToArray(), outputPath, []);
                var prepared = new PreparedFSharpSemanticNode(key, input, owner.Path,
                    nodeTargetFramework, options.AssemblyName, fingerprint,
                    sourceGenerated.ToArray(), JoinPartialReasons(nodeReasons), owner.IsTest,
                    compilerReferenceKeys, descendantKeys.ToArray(), nodeBinaryReferences);
                session.Nodes.Add(prepared);
                session.Completed[key] = prepared;
                FSharpSemanticProjectCapturedForTest?.Invoke(owner.Path,
                    nodeTargetFramework);
                return prepared;
            }
            finally
            {
                session.Active.Remove(key);
            }
        }

        try
        {
            PreparedFSharpSemanticNode? preparedRoot = CaptureNode(rootOwner,
                selected.TargetFramework,
                targetPath);
            if (preparedRoot is null)
            {
                failure = capturedFailure;
                return null;
            }
            var closureKeys = preparedRoot.DescendantKeys.ToHashSet(
                WorkspacePaths.FileSystemPathComparer);
            closureKeys.Add(preparedRoot.Key);
            PreparedFSharpSemanticNode[] preparedNodes = session.Nodes
                .Where(node => closureKeys.Contains(node.Key)).ToArray();
            bool assemblyConflict = preparedNodes
                .GroupBy(node => node.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Select(node => (node.ProjectPath, node.TargetFramework))
                    .Distinct().Skip(1).Any());
            var closureReasons = new SortedSet<string>(StringComparer.Ordinal);
            foreach (PreparedFSharpSemanticNode node in preparedNodes)
                AddPartialReasons(closureReasons, node.PartialReason);
            string? closurePartialReason = JoinPartialReasons(closureReasons);
            if (assemblyConflict)
            {
                failure = Failure("fsharp_project_options_conflict", closurePartialReason);
                return null;
            }

            var localIndices = preparedNodes.Select((node, index) => (node.Key, index))
                .ToDictionary(pair => pair.Key, pair => pair.index,
                    WorkspacePaths.FileSystemPathComparer);
            var nodes = new CapturedFSharpSemanticNode[preparedNodes.Length];
            for (int index = 0; index < preparedNodes.Length; index++)
            {
                PreparedFSharpSemanticNode prepared = preparedNodes[index];
                int[] compilerReferenceIndices = prepared.CompilerReferenceKeys
                    .Select(referenceKey => localIndices[referenceKey]).ToArray();
                var input = new SemanticProjectInput(prepared.Input.ProjectFileName,
                    prepared.Input.SourceFiles, prepared.Input.SourceTexts,
                    prepared.Input.CommandLineArgs, prepared.Input.OutputFile,
                    compilerReferenceIndices);
                nodes[index] = new(input, prepared.ProjectPath, prepared.TargetFramework,
                    prepared.AssemblyName, prepared.Fingerprint, prepared.SourceGenerated,
                    prepared.PartialReason, prepared.IsTest);
            }
            int rootProjectIndex = localIndices[preparedRoot.Key];
            CapturedFSharpSemanticNode root = nodes[rootProjectIndex];
            string targetFileName = targetPath is null ? "" : WorkspaceAbsolutePath(targetPath);
            List<FSharpBinaryReferenceSnapshot> binaryReferences = preparedNodes
                .SelectMany(node => node.BinaryReferences)
                .DistinctBy(reference => reference.SnapshotFullPath,
                    WorkspacePaths.FileSystemPathComparer).ToList();
            var captured = new CapturedFSharpSemanticProject(
                nodes.Select(node => node.Input).ToArray(), nodes.ToArray(),
                rootProjectIndex,
                root.SourceGenerated,
                nodes.SelectMany(node => node.Input.SourceFiles)
                    .Distinct(WorkspacePaths.FileSystemPathComparer).ToArray(),
                root.Fingerprint, targetFileName, selected, contexts, binaryReferences,
                session.ReferenceSnapshotDirectory, closurePartialReason, root.IsTest,
                health);
            ownershipTransferred = ownsSession;
            return captured;
        }
        finally
        {
            if (ownsSession && !ownershipTransferred)
                CleanupFSharpReferenceSnapshots(session.ReferenceSnapshotDirectory,
                    session.BinaryReferences);
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

    internal static string FSharpSemanticFingerprint(
        string projectPath,
        string targetFramework,
        string projectXml,
        IReadOnlyList<string> optionArgs,
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<string> sourceTexts,
        IReadOnlyList<string> referenceIdentities,
        IReadOnlyDictionary<string, bool> existsDependencies)
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
        foreach ((string path, bool exists) in existsDependencies.OrderBy(pair => pair.Key,
                     WorkspacePaths.FileSystemPathComparer))
            Add($"exists:{path}:{exists}");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
    }
}
