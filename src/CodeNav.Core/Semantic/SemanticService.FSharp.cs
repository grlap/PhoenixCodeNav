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

public sealed record FSharpDependentCoverageEntry(
    string Project,
    string Reason);

public sealed record FSharpReferencesCoverage(
    int? DependentsTotal,
    int DependentsScanned,
    int DependentsExcluded,
    int DependentsFailed,
    int? DependentsPending,
    bool WorkspaceComplete,
    List<FSharpDependentCoverageEntry> Excluded,
    List<FSharpDependentCoverageEntry> Failed,
    int PotentialConsumers = 0,
    int PotentialConsumersEvaluated = 0,
    int PotentialConsumersUnevaluated = 0,
    List<FSharpDependentCoverageEntry>? DiscoveryFailed = null,
    string? DeclaringProject = null,
    string? DeclaringProjectStatus = null,
    string? DeclaringProjectReason = null,
    List<string>? DeclaringProjects = null);

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
    bool Complete,
    bool QuotationBodiesExcluded,
    bool TraitCallsUnresolved);

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
    internal Action<string>? BeforeFSharpImplementationTraversalForTest { get; set; }
    internal Action<string, string>? FSharpSemanticProjectCapturedForTest { get; set; }
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
        List<FSharpDependentCoverageEntry> Failed);

    private sealed record FSharpReferenceProjectScan(
        FSharpReferenceGroup Group,
        bool Complete,
        bool Inactive,
        bool Filtered,
        int DiagnosticCount,
        List<FSharpSemanticDiagnostic> Diagnostics,
        string? PartialReason);

    private sealed record FSharpImplementationProjectScan(
        FSharpImplementationGroup Group,
        bool Complete,
        bool Inactive,
        int DiagnosticCount,
        List<FSharpSemanticDiagnostic> Diagnostics,
        string? PartialReason,
        bool QuotationBodiesExcluded);

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
        int timeoutMs)
    {
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
            await _fsharpSemanticGate.WaitAsync(cts.Token).ConfigureAwait(false);
            entered = true;
            snapshot = _manager.TryOpenReviewSnapshot(cts.Token);
            if (snapshot is null)
                return new(null, null, [], "index_snapshot_unavailable", null, []);
            captured = CaptureFSharpSemanticProject(snapshot, path, projectPath, targetFramework,
                cts.Token, out FSharpSemanticResult? failure, captureSession);
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
                            out FSharpSemanticResult? projectCaptureFailure, captureSession);
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
                            MaxFSharpSemanticLineOnlySourceChars, cts.Token)
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
                    false, [], []);
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
                candidateSetKnown = discoveryFailed.Count == 0;
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
            bool workspaceComplete = candidateSetKnown && !deadlineExhausted &&
                                     declaringProjectComplete && failed.Count == 0 &&
                                     pending == 0 && !incompleteExcluded;
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
                excluded.Count, failed.Count, pending, workspaceComplete, excluded, failed,
                potentialConsumers, potentialConsumersEvaluated,
                Math.Max(0, potentialConsumers - potentialConsumersEvaluated),
                discoveryFailed, declaringProject, declaringProjectStatus,
                declaringProjectReason);
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
                    null, 0, 0, 0, null, false, [], []);
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
                candidateSetKnown = discoveryFailed.Count == 0;
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
                declaringProjects);
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
        int timeoutMs)
    {
        if (line < 1 || column <= 0)
            return new(null, null, null, [], "fsharp_semantic_position_invalid", null, []);

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
                return new(null, null, null, [], "index_snapshot_unavailable", null, []);
            captured = CaptureFSharpSemanticProject(snapshot, path, projectPath,
                targetFramework, cts.Token, out FSharpSemanticResult? captureFailure,
                captureSession);
            if (captured is null)
                return FSharpCallersFailure(captureFailure!);
            FSharpSemanticSnapshotCapturedForTest?.Invoke();

            SemanticCallGraphCheckResult check = await SemanticResolver.ResolveCallersAsync(
                captured.Projects, captured.RootProjectIndex, captured.RootProjectIndex,
                captured.Fingerprint, captured.BinaryReferences.Count == 0,
                captured.TargetFileName, line, column,
                BeforeFSharpImplementationTraversalForTest!, cts.Token).ConfigureAwait(false);
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
                    null, 0, 0, 0, null, false, [], []);
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
                            captureSession);
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
                            cts.Token).ConfigureAwait(false);
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
                candidateSetKnown = discoveryFailed.Count == 0;
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
            bool workspaceComplete = candidateSetKnown && !deadlineExhausted &&
                                     declaringProjectComplete && failed.Count == 0 &&
                                     pending == 0 && !incompleteExcluded &&
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
                workspaceComplete, excluded, failed,
                potentialConsumers, potentialConsumersEvaluated,
                Math.Max(0, potentialConsumers - potentialConsumersEvaluated),
                discoveryFailed, declaringProject, declaringProjectStatus,
                declaringProjectReason,
                declaringProject is null ? [] : [declaringProject]);
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
        int timeoutMs)
    {
        if (line < 1 || column <= 0)
            return new(null, null, null, [], "fsharp_semantic_position_invalid", null, []);

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
                return new(null, null, null, [], "index_snapshot_unavailable", null, []);
            captured = CaptureFSharpSemanticProject(snapshot, path, projectPath,
                targetFramework, cts.Token, out FSharpSemanticResult? captureFailure,
                captureSession);
            if (captured is null)
                return FSharpCalleesFailure(captureFailure!);
            FSharpSemanticSnapshotCapturedForTest?.Invoke();

            SemanticCallGraphCheckResult check = await SemanticResolver.ResolveCalleesAsync(
                captured.Projects, captured.RootProjectIndex, captured.RootProjectIndex,
                captured.Fingerprint, captured.BinaryReferences.Count == 0,
                captured.TargetFileName, line, column,
                BeforeFSharpImplementationTraversalForTest!, cts.Token).ConfigureAwait(false);
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
                check.QuotationBodiesExcluded, check.TraitCallsUnresolved);
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

    private static FSharpImplementationsResult FSharpImplementationsFailure(
        FSharpSemanticResult failure) =>
        new(null, null, [], failure.Error, failure.SelectedContext,
            failure.AvailableContexts, PartialReason: failure.PartialReason,
            DiagnosticCount: failure.DiagnosticCount,
            Diagnostics: failure.Diagnostics, Health: failure.Health,
            ProjectReferenceFailure: failure.ProjectReferenceFailure);

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

    private static FSharpReferencesResult FSharpReferencesFailure(FSharpSemanticResult failure) =>
        new(null, null, [], failure.Error, failure.SelectedContext,
            failure.AvailableContexts, PartialReason: failure.PartialReason,
            DiagnosticCount: failure.DiagnosticCount, Diagnostics: failure.Diagnostics,
            Health: failure.Health, ProjectReferenceFailure: failure.ProjectReferenceFailure);

    private FSharpSemanticCaptureSession CreateFSharpSemanticCaptureSession() => new(
        FSharpSemanticSourceFilesLimitForTest ?? MaxFSharpSemanticSourceFiles,
        FSharpSemanticSourceBytesLimitForTest ?? MaxFSharpSemanticSourceBytes,
        FSharpSemanticReferenceBytesLimitForTest ?? MaxFSharpSemanticReferenceBytes);

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
                    foreach (string targetFramework in targetFrameworks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FSharpSemanticOptionsSnapshot? options =
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
            potentialConsumersEvaluated, discoveryFailed);
    }

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
            hasAmbiguousDirectoryPackagesAuthority: directoryPackages.PathAmbiguous);
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
                    fullSourcePaths, sourceTexts, referenceIdentities);
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
