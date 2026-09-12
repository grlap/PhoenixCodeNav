#nowarn "57"
#nowarn "3261"

namespace CodeNav.FSharp

/// FCS-owned spans only; the caller owns the per-operation collector and snapshot.
type SemanticPhase =
    | Setup = 0
    | ProjectParseAndCheck = 1
    | FileParseAndCheck = 2

type ISemanticTiming =
    abstract StartPhase: SemanticPhase -> System.IDisposable

[<Sealed>]
type SemanticLocation(
    role: string,
    fileName: string,
    startLine: int,
    startColumn: int,
    endLine: int,
    endColumn: int
) =
    member _.Role = role
    member _.FileName = fileName
    member _.StartLine = startLine
    member _.StartColumn = startColumn
    member _.EndLine = endLine
    member _.EndColumn = endColumn

[<Sealed>]
type SemanticSymbol(
    name: string,
    fullName: string,
    kind: string,
    container: string,
    namespaceName: string,
    assemblyName: string,
    accessibility: string,
    useLocation: SemanticLocation,
    declarations: SemanticLocation array,
    identity: string
) =
    member _.Name = name
    member _.FullName = fullName
    member _.Kind = kind
    member _.Container = container
    member _.Namespace = namespaceName
    member _.Assembly = assemblyName
    member _.Accessibility = accessibility
    member _.UseLocation = useLocation
    member _.Declarations = declarations
    member _.Identity = identity

[<Sealed>]
type SemanticDiagnostic(
    severity: string,
    code: string,
    message: string,
    fileName: string,
    startLine: int,
    startColumn: int,
    endLine: int,
    endColumn: int
) =
    member _.Severity = severity
    member _.Code = code
    member _.Message = message
    member _.FileName = fileName
    member _.StartLine = startLine
    member _.StartColumn = startColumn
    member _.EndLine = endLine
    member _.EndColumn = endColumn

[<Sealed>]
type SemanticCheckResult(
    symbol: SemanticSymbol,
    error: string,
    diagnosticCount: int,
    errorDiagnosticCount: int,
    diagnostics: SemanticDiagnostic array,
    references: SemanticLocation array
) =
    member _.Symbol = symbol
    member _.Error = error
    member _.DiagnosticCount = diagnosticCount
    member _.ErrorDiagnosticCount = errorDiagnosticCount
    member _.Diagnostics = diagnostics
    member _.References = references

[<Sealed>]
type SemanticImplementation(
    symbol: SemanticSymbol,
    implementationKind: string,
    isAbstract: bool,
    via: string,
    projectIndex: int
) =
    member _.Symbol = symbol
    member _.ImplementationKind = implementationKind
    member _.IsAbstract = isAbstract
    member _.Via = via
    member _.ProjectIndex = projectIndex

[<Sealed>]
type SemanticImplementationsCheckResult(
    symbol: SemanticSymbol,
    targets: SemanticSymbol array,
    implementations: SemanticImplementation array,
    error: string,
    diagnosticCount: int,
    errorDiagnosticCount: int,
    diagnostics: SemanticDiagnostic array,
    resolvedFromOverride: string array,
    quotationBodiesExcluded: bool
) =
    member _.Symbol = symbol
    member _.Targets = targets
    member _.Implementations = implementations
    member _.Error = error
    member _.DiagnosticCount = diagnosticCount
    member _.ErrorDiagnosticCount = errorDiagnosticCount
    member _.Diagnostics = diagnostics
    member _.ResolvedFromOverride = resolvedFromOverride
    member _.QuotationBodiesExcluded = quotationBodiesExcluded

[<Sealed>]
type SemanticCall(
    caller: SemanticSymbol,
    callee: SemanticSymbol,
    site: SemanticLocation,
    callKind: string,
    projectIndex: int
) =
    member _.Caller = caller
    member _.Callee = callee
    member _.Site = site
    member _.CallKind = callKind
    member _.ProjectIndex = projectIndex

[<Sealed>]
type SemanticCallGraphCheckResult(
    symbol: SemanticSymbol,
    calls: SemanticCall array,
    error: string,
    diagnosticCount: int,
    errorDiagnosticCount: int,
    diagnostics: SemanticDiagnostic array,
    dispatchSlots: string array,
    quotationBodiesExcluded: bool,
    traitCallsUnresolved: bool,
    deadlineExhausted: bool
) =
    member _.Symbol = symbol
    member _.Calls = calls
    member _.Error = error
    member _.DiagnosticCount = diagnosticCount
    member _.ErrorDiagnosticCount = errorDiagnosticCount
    member _.Diagnostics = diagnostics
    member _.DispatchSlots = dispatchSlots
    member _.QuotationBodiesExcluded = quotationBodiesExcluded
    member _.TraitCallsUnresolved = traitCallsUnresolved
    member _.DeadlineExhausted = deadlineExhausted

[<Sealed>]
type SemanticProjectInput(
    projectFileName: string,
    sourceFiles: string array,
    sourceTexts: string array,
    commandLineArgs: string array,
    outputFile: string,
    referencedProjectIndices: int array
) =
    member _.ProjectFileName = projectFileName
    member _.SourceFiles = sourceFiles
    member _.SourceTexts = sourceTexts
    member _.CommandLineArgs = commandLineArgs
    member _.OutputFile = outputFile
    member _.ReferencedProjectIndices = referencedProjectIndices
