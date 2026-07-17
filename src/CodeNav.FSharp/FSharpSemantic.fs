#nowarn "57"
#nowarn "3261"

namespace CodeNav.FSharp

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

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
    declarations: SemanticLocation array
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
    diagnostics: SemanticDiagnostic array
) =
    member _.Symbol = symbol
    member _.Error = error
    member _.DiagnosticCount = diagnosticCount
    member _.ErrorDiagnosticCount = errorDiagnosticCount
    member _.Diagnostics = diagnostics

module private Semantic =
    let nullString: string = Unchecked.defaultof<string>
    let nullSymbol: SemanticSymbol = Unchecked.defaultof<SemanticSymbol>
    let maxCachedProjects = 4
    let cacheGate = obj()
    let mutable accessClock = 0L
    let pathComparer =
        if OperatingSystem.IsWindows() then StringComparer.OrdinalIgnoreCase else StringComparer.Ordinal

    type Runtime =
        {
            Checker: FSharpChecker
            mutable LastAccess: int64
        }

    let runtimes = Dictionary<string, Runtime>(StringComparer.Ordinal)

    let sourceKey (fileName: string) =
        try
            Path.GetFullPath(fileName).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
        with _ ->
            fileName.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)

    let pathIdentityKey fileName =
        let key = sourceKey fileName
        if OperatingSystem.IsWindows() then key.ToUpperInvariant() else key

    let createRuntime (sourceFiles: string array) (sourceTexts: string array) =
        let sources = Dictionary<string, ISourceText>(pathComparer)
        for index in 0 .. sourceFiles.Length - 1 do
            sources[sourceKey sourceFiles[index]] <- SourceText.ofString sourceTexts[index]

        let documentSource =
            DocumentSource.Custom(fun fileName ->
                async {
                    match sources.TryGetValue(sourceKey fileName) with
                    | true, source -> return Some source
                    | _ -> return None
                })

        {
            Checker =
                FSharpChecker.Create(
                    projectCacheSize = 1,
                    keepAssemblyContents = false,
                    keepAllBackgroundResolutions = true,
                    documentSource = documentSource
                )
            LastAccess = 0L
        }

    let runtime fingerprint sourceFiles sourceTexts cacheRuntime =
        if not cacheRuntime then
            createRuntime sourceFiles sourceTexts
        else
            lock cacheGate (fun () ->
                accessClock <- accessClock + 1L
                match runtimes.TryGetValue(fingerprint) with
                | true, existing ->
                    existing.LastAccess <- accessClock
                    existing
                | _ ->
                    let created = createRuntime sourceFiles sourceTexts
                    created.LastAccess <- accessClock
                    if runtimes.Count >= maxCachedProjects then
                        let oldest =
                            runtimes
                            |> Seq.minBy (fun pair -> pair.Value.LastAccess)
                        runtimes.Remove(oldest.Key) |> ignore
                    runtimes[fingerprint] <- created
                    created)

    let safeString getter =
        try
            let value = getter ()
            if String.IsNullOrEmpty(value) then nullString else value
        with _ ->
            nullString

    let accessibility (symbol: FSharpSymbol) =
        try
            if symbol.Accessibility.IsPublic then "public"
            elif symbol.Accessibility.IsPrivate then "private"
            elif symbol.Accessibility.IsInternal then "internal"
            elif symbol.Accessibility.IsProtected then "protected"
            else nullString
        with _ ->
            nullString

    let kind (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpEntity as entity ->
            if entity.IsNamespace then "namespace"
            elif entity.IsFSharpModule then "module"
            elif entity.IsInterface then "interface"
            elif entity.IsEnum then "enum"
            elif entity.IsFSharpRecord then "record"
            elif entity.IsFSharpUnion then "union"
            elif entity.IsDelegate then "delegate"
            elif entity.IsClass then "class"
            else "type"
        | :? FSharpMemberOrFunctionOrValue as value ->
            if value.IsConstructor then "constructor"
            elif value.IsProperty then "property"
            elif value.IsEvent then "event"
            elif value.IsMethod then "method"
            elif value.IsFunction then "function"
            else "value"
        | :? FSharpField -> "field"
        | :? FSharpUnionCase -> "unionCase"
        | :? FSharpActivePatternCase -> "activePatternCase"
        | :? FSharpParameter -> "parameter"
        | :? FSharpGenericParameter -> "typeParameter"
        | _ -> "symbol"

    let declaringEntity (symbol: FSharpSymbol) =
        try
            match symbol with
            | :? FSharpEntity as entity -> entity.DeclaringEntity
            | :? FSharpMemberOrFunctionOrValue as value -> value.DeclaringEntity
            | :? FSharpField as field -> field.DeclaringEntity
            | :? FSharpUnionCase as unionCase -> Some unionCase.DeclaringEntity
            | _ -> None
        with _ ->
            None

    let container symbol =
        declaringEntity symbol
        |> Option.map (fun entity -> safeString (fun () -> entity.FullName))
        |> Option.defaultValue nullString

    let namespaceName (symbol: FSharpSymbol) =
        match declaringEntity symbol with
        | Some entity -> entity.Namespace |> Option.defaultValue nullString
        | None ->
            match symbol with
            | :? FSharpEntity as entity -> entity.Namespace |> Option.defaultValue nullString
            | _ -> nullString

    let location role (range: range) =
        SemanticLocation(
            role,
            range.FileName,
            max 1 range.StartLine,
            max 0 range.StartColumn,
            max 1 range.EndLine,
            max 0 range.EndColumn
        )

    let declarationLocations (symbol: FSharpSymbol) =
        let ranges = ResizeArray<string * range>()
        let add role getter =
            try
                getter () |> Option.iter (fun range -> ranges.Add(role, range))
            with _ ->
                ()
        add "implementation" (fun () -> symbol.ImplementationLocation)
        add "signature" (fun () -> symbol.SignatureLocation)
        add "declaration" (fun () -> symbol.DeclarationLocation)
        ranges
        |> Seq.distinctBy (fun (_, range) ->
            pathIdentityKey range.FileName, range.StartLine, range.StartColumn, range.EndLine, range.EndColumn)
        |> Seq.map (fun (role, range) -> location role range)
        |> Seq.toArray

    let diagnosticSeverity (diagnostic: FSharpDiagnostic) =
        diagnostic.Severity.ToString().ToLowerInvariant()

    let boundedDiagnostics (diagnostics: seq<FSharpDiagnostic>) =
        diagnostics
        |> Seq.sortBy (fun diagnostic -> if diagnosticSeverity diagnostic = "error" then 0 else 1)
        |> Seq.truncate 8
        |> Seq.map (fun diagnostic ->
            let text = diagnostic.Message
            let message = if text.Length <= 320 then text else text.Substring(0, 320)
            let range = diagnostic.Range
            SemanticDiagnostic(
                diagnosticSeverity diagnostic,
                sprintf "FS%04d" diagnostic.ErrorNumber,
                message,
                range.FileName,
                range.StartLine,
                range.StartColumn,
                range.EndLine,
                range.EndColumn
            ))
        |> Seq.toArray

    let checkResult symbol error (diagnostics: seq<FSharpDiagnostic>) =
        let all = diagnostics |> Seq.toArray
        let errorCount = all |> Array.sumBy (fun diagnostic ->
            if diagnosticSeverity diagnostic = "error" then 1 else 0)
        SemanticCheckResult(symbol, error, all.Length, errorCount, boundedDiagnostics all)

    let diagnosticKey (diagnostic: FSharpDiagnostic) =
        let range = diagnostic.Range
        diagnosticSeverity diagnostic,
        diagnostic.ErrorNumber,
        diagnostic.Message,
        pathIdentityKey range.FileName,
        range.StartLine,
        range.StartColumn,
        range.EndLine,
        range.EndColumn

    let mergeDiagnostics (projectDiagnostics: seq<FSharpDiagnostic>)
        (fileDiagnostics: seq<FSharpDiagnostic>) =
        Seq.append projectDiagnostics fileDiagnostics
        |> Seq.distinctBy diagnosticKey
        |> Seq.toArray

    let containsPosition line column (range: range) =
        if line < range.StartLine || line > range.EndLine then false
        elif column <= 0 then true
        else
            let zeroBasedColumn = column - 1
            let afterStart = line > range.StartLine || zeroBasedColumn >= range.StartColumn
            let beforeEnd = line < range.EndLine || zeroBasedColumn < range.EndColumn
            afterStart && beforeEnd

    let rangeScore (symbolUse: FSharpSymbolUse) =
        let range = symbolUse.Range
        let lineSpan = max 0 (range.EndLine - range.StartLine)
        let columnSpan =
            if lineSpan = 0 then max 0 (range.EndColumn - range.StartColumn)
            else Int32.MaxValue / 2
        lineSpan, columnSpan, range.StartColumn, safeString (fun () -> symbolUse.Symbol.FullName)

    let resolve
        projectFileName
        (sourceFiles: string array)
        (sourceTexts: string array)
        (commandLineArgs: string array)
        fingerprint
        cacheRuntime
        targetFileName
        line
        column
        maxLineOnlySourceChars
        =
        async {
            if sourceFiles.Length = 0 || sourceFiles.Length <> sourceTexts.Length then
                return checkResult nullSymbol "fsharp_semantic_snapshot_invalid" Array.empty
            else
                let runtime = runtime fingerprint sourceFiles sourceTexts cacheRuntime
                let checker = runtime.Checker
                let options =
                    checker.GetProjectOptionsFromCommandLineArgs(
                        projectFileName,
                        commandLineArgs,
                        loadedTimeStamp = DateTime.UnixEpoch,
                        isEditing = false,
                        isInteractive = false
                    )
                let! checkedProject = checker.ParseAndCheckProject(options, userOpName = "PhoenixCodeNav.symbol_at")
                if checkedProject.HasCriticalErrors then
                    return
                        checkResult nullSymbol "fsharp_semantic_check_failed" checkedProject.Diagnostics
                else
                    let targetIndex =
                        sourceFiles
                        |> Array.tryFindIndex (fun fileName ->
                            pathComparer.Equals(fileName, targetFileName))
                    match targetIndex with
                    | None ->
                        return
                            checkResult nullSymbol "fsharp_semantic_target_not_in_project" checkedProject.Diagnostics
                    | Some targetIndex ->
                        let! _, answer =
                            checker.ParseAndCheckFileInProject(
                                targetFileName,
                                0,
                                SourceText.ofString sourceTexts[targetIndex],
                                options,
                                userOpName = "PhoenixCodeNav.symbol_at"
                            )
                        match answer with
                        | FSharpCheckFileAnswer.Aborted ->
                            return
                                checkResult nullSymbol "fsharp_semantic_check_aborted" checkedProject.Diagnostics
                        | FSharpCheckFileAnswer.Succeeded checkedFile when
                            not checkedFile.HasFullTypeCheckInfo ->
                            let diagnostics = mergeDiagnostics checkedProject.Diagnostics checkedFile.Diagnostics
                            return
                                checkResult nullSymbol "fsharp_semantic_check_incomplete" diagnostics
                        | FSharpCheckFileAnswer.Succeeded checkedFile ->
                            let diagnostics = mergeDiagnostics checkedProject.Diagnostics checkedFile.Diagnostics
                            let sourceText = SourceText.ofString sourceTexts[targetIndex]
                            let candidates =
                                if column > 0 && line >= 1 && line <= sourceText.GetLineCount() then
                                    let lineText = sourceText.GetLineString(line - 1)
                                    let cursor = column - 1
                                    if cursor < 0 || cursor > lineText.Length then
                                        Array.empty
                                    else
                                        match QuickParse.GetCompleteIdentifierIsland true lineText cursor with
                                        | Some (identifier, endColumn, _) ->
                                            let names = identifier.Split('.') |> Array.toList
                                            match checkedFile.GetSymbolUseAtLocation(line, endColumn, lineText, names) with
                                            | Some symbolUse -> [| symbolUse |]
                                            | None -> Array.empty
                                        | None -> Array.empty
                                elif column <= 0 && sourceTexts[targetIndex].Length <= maxLineOnlySourceChars then
                                    checkedFile.GetAllUsesOfAllSymbolsInFile()
                                    |> Seq.filter (fun symbolUse -> containsPosition line column symbolUse.Range)
                                    |> Seq.sortBy rangeScore
                                    |> Seq.truncate 2
                                    |> Seq.toArray
                                else
                                    Array.empty
                            if candidates.Length = 0 then
                                let error =
                                    if column <= 0 && sourceTexts[targetIndex].Length > maxLineOnlySourceChars then
                                        "fsharp_semantic_line_only_source_limit"
                                    else
                                        "fsharp_symbol_not_resolved"
                                return
                                    checkResult nullSymbol error diagnostics
                            elif column <= 0 && candidates.Length > 1 then
                                return
                                    checkResult nullSymbol "fsharp_semantic_column_required" diagnostics
                            else
                                let symbolUse = candidates[0]
                                let symbol = symbolUse.Symbol
                                let mapped =
                                    SemanticSymbol(
                                        safeString (fun () -> symbol.DisplayName),
                                        safeString (fun () -> symbol.FullName),
                                        kind symbol,
                                        container symbol,
                                        namespaceName symbol,
                                        safeString (fun () -> symbol.Assembly.SimpleName),
                                        accessibility symbol,
                                        location "use" symbolUse.Range,
                                        declarationLocations symbol
                                    )
                                return
                                    checkResult mapped nullString diagnostics
        }

[<AbstractClass; Sealed>]
type SemanticResolver private () =
    static member ResolveAsync(
        projectFileName: string,
        sourceFiles: string array,
        sourceTexts: string array,
        commandLineArgs: string array,
        fingerprint: string,
        cacheRuntime: bool,
        targetFileName: string,
        line: int,
        column: int,
        maxLineOnlySourceChars: int,
        cancellationToken: CancellationToken
    ) : Task<SemanticCheckResult> =
        Semantic.resolve projectFileName sourceFiles sourceTexts commandLineArgs fingerprint cacheRuntime
            targetFileName line column maxLineOnlySourceChars
        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
