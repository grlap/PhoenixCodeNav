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

module private Semantic =
    let measure (timing: ISemanticTiming) phase work =
        async {
            use span = timing.StartPhase(phase)
            return! work ()
        }

    let nullString: string = Unchecked.defaultof<string>
    let nullSymbol: SemanticSymbol = Unchecked.defaultof<SemanticSymbol>
    let maxCachedProjects = 4
    let cacheGate = obj()
    let assemblyContentsCacheGate = obj()
    let mutable accessClock = 0L
    let mutable assemblyContentsAccessClock = 0L
    let pathComparer =
        if OperatingSystem.IsWindows() then StringComparer.OrdinalIgnoreCase else StringComparer.Ordinal

    type Runtime =
        {
            Checker: FSharpChecker
            mutable LastAccess: int64
        }

    type CheckedAssemblyContents =
        {
            Checker: FSharpChecker
            OptionsByIndex: FSharpProjectOptions array
            Projects: FSharpCheckProjectResults array
            Diagnostics: FSharpDiagnostic array
            HasCriticalErrors: bool
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

    let expressionRangeKey (value: range) =
        pathIdentityKey value.FileName + "\u0000" +
        string value.StartLine + "\u0000" + string value.StartColumn + "\u0000" +
        string value.EndLine + "\u0000" + string value.EndColumn

    let createRuntime keepAssemblyContents (sourceFiles: string array) (sourceTexts: string array) =
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
                    keepAssemblyContents = keepAssemblyContents,
                    keepAllBackgroundResolutions = true,
                    documentSource = documentSource
                )
            LastAccess = 0L
        }

    let assemblyContentsRuntimes = Dictionary<string, Runtime>(StringComparer.Ordinal)

    let runtime fingerprint sourceFiles sourceTexts cacheRuntime =
        if not cacheRuntime then
            createRuntime false sourceFiles sourceTexts
        else
            lock cacheGate (fun () ->
                accessClock <- accessClock + 1L
                match runtimes.TryGetValue(fingerprint) with
                | true, existing ->
                    existing.LastAccess <- accessClock
                    existing
                | _ ->
                    let created = createRuntime false sourceFiles sourceTexts
                    created.LastAccess <- accessClock
                    if runtimes.Count >= maxCachedProjects then
                        let oldest =
                            runtimes
                            |> Seq.minBy (fun pair -> pair.Value.LastAccess)
                        runtimes.Remove(oldest.Key) |> ignore
                    runtimes[fingerprint] <- created
                    created)

    let assemblyContentsRuntime fingerprint sourceFiles sourceTexts cacheRuntime =
        if not cacheRuntime then
            createRuntime true sourceFiles sourceTexts
        else
            lock assemblyContentsCacheGate (fun () ->
                assemblyContentsAccessClock <- assemblyContentsAccessClock + 1L
                match assemblyContentsRuntimes.TryGetValue(fingerprint) with
                | true, existing ->
                    existing.LastAccess <- assemblyContentsAccessClock
                    existing
                | _ ->
                    let created = createRuntime true sourceFiles sourceTexts
                    created.LastAccess <- assemblyContentsAccessClock
                    if assemblyContentsRuntimes.Count >= maxCachedProjects then
                        let oldest =
                            assemblyContentsRuntimes
                            |> Seq.minBy (fun pair -> pair.Value.LastAccess)
                        assemblyContentsRuntimes.Remove(oldest.Key) |> ignore
                    assemblyContentsRuntimes[fingerprint] <- created
                    created)

    let checkAssemblyContents (timing: ISemanticTiming)
        (projects: SemanticProjectInput array)
        fingerprint
        cacheRuntime
        operationName
        (cancellationToken: CancellationToken) =
        async {
            use setup = timing.StartPhase(SemanticPhase.Setup)
            let sourceFiles = projects |> Array.collect (fun project -> project.SourceFiles)
            let sourceTexts = projects |> Array.collect (fun project -> project.SourceTexts)
            let runtime = assemblyContentsRuntime fingerprint sourceFiles sourceTexts cacheRuntime
            let checker = runtime.Checker
            let optionsByIndex = Array.zeroCreate<FSharpProjectOptions> projects.Length
            for index in 0 .. projects.Length - 1 do
                cancellationToken.ThrowIfCancellationRequested()
                let project = projects[index]
                let baseOptions =
                    checker.GetProjectOptionsFromCommandLineArgs(
                        project.ProjectFileName,
                        project.CommandLineArgs,
                        loadedTimeStamp = DateTime.UnixEpoch,
                        isEditing = false,
                        isInteractive = false
                    )
                let referencedProjects =
                    project.ReferencedProjectIndices
                    |> Array.map (fun child ->
                        FSharpReferencedProject.FSharpReference(
                            projects[child].OutputFile,
                            optionsByIndex[child]
                        ))
                optionsByIndex[index] <-
                    { baseOptions with ReferencedProjects = referencedProjects }

            setup.Dispose()

            let checkedProjects = Array.zeroCreate<FSharpCheckProjectResults> projects.Length
            let projectDiagnostics = ResizeArray<FSharpDiagnostic>()
            let mutable hasCriticalErrors = false
            for index in 0 .. projects.Length - 1 do
                cancellationToken.ThrowIfCancellationRequested()
                let! checkedProject =
                    measure timing SemanticPhase.ProjectParseAndCheck (fun () -> checker.ParseAndCheckProject(
                        optionsByIndex[index],
                        userOpName = operationName
                    ))
                checkedProjects[index] <- checkedProject
                projectDiagnostics.AddRange(checkedProject.Diagnostics)
                hasCriticalErrors <- hasCriticalErrors || checkedProject.HasCriticalErrors
            let allProjectDiagnostics =
                projectDiagnostics
                |> Seq.distinctBy (fun diagnostic ->
                    let range = diagnostic.Range
                    diagnostic.Severity.ToString(),
                    diagnostic.ErrorNumber,
                    diagnostic.Message,
                    pathIdentityKey range.FileName,
                    range.StartLine,
                    range.StartColumn,
                    range.EndLine,
                    range.EndColumn)
                |> Seq.toArray
            return
                {
                    Checker = checker
                    OptionsByIndex = optionsByIndex
                    Projects = checkedProjects
                    Diagnostics = allProjectDiagnostics
                    HasCriticalErrors = hasCriticalErrors
                }
        }

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
        SemanticCheckResult(symbol, error, all.Length, errorCount, boundedDiagnostics all, Array.empty)

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

    let implementationResult symbol targets implementations error diagnostics resolvedFromOverride quotationsExcluded =
        let all = diagnostics |> Seq.toArray
        let errorCount = all |> Array.sumBy (fun diagnostic ->
            if diagnosticSeverity diagnostic = "error" then 1 else 0)
        SemanticImplementationsCheckResult(
            symbol,
            targets,
            implementations,
            error,
            all.Length,
            errorCount,
            boundedDiagnostics all,
            resolvedFromOverride,
            quotationsExcluded
        )

    let callGraphResult symbol calls error diagnostics dispatchSlots quotationsExcluded traitCallsUnresolved deadlineExhausted =
        let all = diagnostics |> Seq.toArray
        let errorCount = all |> Array.sumBy (fun diagnostic ->
            if diagnosticSeverity diagnostic = "error" then 1 else 0)
        SemanticCallGraphCheckResult(
            symbol,
            calls,
            error,
            all.Length,
            errorCount,
            boundedDiagnostics all,
            dispatchSlots,
            quotationsExcluded,
            traitCallsUnresolved,
            deadlineExhausted
        )

    let symbolIdentity (symbol: FSharpSymbol) =
        let fullName = safeString (fun () -> symbol.FullName)
        let assemblyName = safeString (fun () -> symbol.Assembly.SimpleName)
        let signature =
            match symbol with
            | :? FSharpMemberOrFunctionOrValue as value ->
                safeString (fun () ->
                    value.CompiledName + "\u0000" +
                    value.FullType.Format(FSharpDisplayContext.Empty))
            | _ -> safeString (fun () -> symbol.ToString())
        String.concat "\u0000" [
            if isNull assemblyName then "" else assemblyName
            kind symbol
            if isNull fullName then "" else fullName
            if isNull signature then "" else signature
        ]

    let mappedSymbol (useRange: range) (symbol: FSharpSymbol) =
        SemanticSymbol(
            safeString (fun () -> symbol.DisplayName),
            safeString (fun () -> symbol.FullName),
            kind symbol,
            container symbol,
            namespaceName symbol,
            safeString (fun () -> symbol.Assembly.SimpleName),
            accessibility symbol,
            location "use" useRange,
            declarationLocations symbol,
            symbolIdentity symbol
        )

    let sameEntity (left: FSharpEntity) (right: FSharpEntity) =
        try
            (left :> FSharpSymbol).IsEffectivelySameAs(right :> FSharpSymbol)
        with _ ->
            false

    let rec stripAbbreviation (value: FSharpType) =
        try
            if value.IsAbbreviation then stripAbbreviation value.AbbreviatedType else value
        with _ ->
            value

    let typeDefinition (value: FSharpType) =
        try
            let normalized = stripAbbreviation value
            if normalized.HasTypeDefinition then Some normalized.TypeDefinition else None
        with _ ->
            None

    let rec sameType (left: FSharpType) (right: FSharpType) =
        try
            let normalizedLeft = stripAbbreviation left
            let normalizedRight = stripAbbreviation right
            if normalizedLeft.Equals(normalizedRight) then true
            elif normalizedLeft.IsGenericParameter && normalizedRight.IsGenericParameter then
                String.Equals(
                    normalizedLeft.GenericParameter.Name,
                    normalizedRight.GenericParameter.Name,
                    StringComparison.Ordinal)
            elif normalizedLeft.HasTypeDefinition && normalizedRight.HasTypeDefinition &&
                 sameEntity normalizedLeft.TypeDefinition normalizedRight.TypeDefinition then
                let leftArguments = normalizedLeft.GenericArguments |> Seq.toArray
                let rightArguments = normalizedRight.GenericArguments |> Seq.toArray
                leftArguments.Length = rightArguments.Length &&
                Array.forall2 sameType leftArguments rightArguments
            elif normalizedLeft.IsFunctionType = normalizedRight.IsFunctionType &&
                 normalizedLeft.IsTupleType = normalizedRight.IsTupleType &&
                 normalizedLeft.IsStructTupleType = normalizedRight.IsStructTupleType then
                let leftArguments = normalizedLeft.GenericArguments |> Seq.toArray
                let rightArguments = normalizedRight.GenericArguments |> Seq.toArray
                leftArguments.Length > 0 &&
                leftArguments.Length = rightArguments.Length &&
                Array.forall2 sameType leftArguments rightArguments
            else false
        with _ ->
            false

    let typeTargetsEntity (target: FSharpEntity) (value: FSharpType) =
        typeDefinition value |> Option.exists (sameEntity target)

    let typeDisplay (value: FSharpType) =
        match typeDefinition value with
        | Some entity -> safeString (fun () -> entity.DisplayName)
        | None -> safeString (fun () -> value.Format(FSharpDisplayContext.Empty))

    let entityDisplay (entity: FSharpEntity) = safeString (fun () -> entity.DisplayName)

    let entityIsAbstract (entity: FSharpEntity) =
        try entity.IsInterface || entity.IsAbstractClass with _ -> false

    let entityTypes (signature: FSharpAssemblySignature) =
        let rec walk (entities: seq<FSharpEntity>) =
            seq {
                for entity in entities do
                    yield entity
                    let nested =
                        try entity.NestedEntities :> seq<FSharpEntity>
                        with _ -> Seq.empty
                    yield! walk nested
            }
        walk signature.Entities

    let overrideMemberAtPosition
        (signature: FSharpAssemblySignature)
        targetFileName
        line
        column =
        entityTypes signature
        |> Seq.collect (fun entity -> entity.MembersFunctionsAndValues)
        |> Seq.filter (fun memberValue ->
            try
                memberValue.IsOverrideOrExplicitInterfaceImplementation &&
                pathComparer.Equals(memberValue.DeclarationLocation.FileName, targetFileName) &&
                containsPosition line column memberValue.DeclarationLocation
            with _ ->
                false)
        |> Seq.sortBy (fun memberValue ->
            let declaration = memberValue.DeclarationLocation
            declaration.EndLine - declaration.StartLine,
            declaration.EndColumn - declaration.StartColumn,
            declaration.StartColumn)
        |> Seq.tryHead

    let rec typeOrBaseTargetsEntity (target: FSharpEntity) (value: FSharpType) =
        if typeTargetsEntity target value then true
        else
            try
                value.BaseType |> Option.exists (typeOrBaseTargetsEntity target)
            with _ ->
                false

    let typeOrInterfacesTargetEntity (target: FSharpEntity) (value: FSharpType) =
        if typeTargetsEntity target value then true
        else
            try
                value.AllInterfaces |> Seq.exists (typeTargetsEntity target)
            with _ ->
                false

    let entityRelation (target: FSharpEntity) (candidate: FSharpEntity) =
        if sameEntity target candidate then false, nullString
        elif target.IsInterface then
            let declared =
                try candidate.DeclaredInterfaces |> Seq.toArray
                with _ -> Array.empty
            if declared |> Array.exists (typeTargetsEntity target) then
                true, nullString
            else
                let declaredVia =
                    declared
                    |> Array.tryFind (fun value -> typeOrInterfacesTargetEntity target value)
                match declaredVia with
                | Some value -> true, typeDisplay value
                | None ->
                    let baseVia =
                        try candidate.BaseType
                        with _ -> None
                    match baseVia with
                    | Some value when typeOrInterfacesTargetEntity target value ->
                        true, typeDisplay value
                    | _ ->
                        let inherited =
                            try candidate.AllInterfaces |> Seq.exists (typeTargetsEntity target)
                            with _ -> false
                        inherited, nullString
        else
            let baseType =
                try candidate.BaseType
                with _ -> None
            match baseType with
            | Some value when typeTargetsEntity target value -> true, nullString
            | Some value when typeOrBaseTargetsEntity target value -> true, typeDisplay value
            | _ -> false, nullString

    let instantiateForDeclaringType
        (target: FSharpMemberOrFunctionOrValue)
        (signature: FSharpAbstractSignature)
        (value: FSharpType) =
        try
            match target.DeclaringEntity, typeDefinition signature.DeclaringType with
            | Some owner, Some declaring when sameEntity owner declaring ->
                let parameters = owner.GenericParameters |> Seq.toList
                let arguments = signature.DeclaringType.GenericArguments |> Seq.toList
                if parameters.Length = arguments.Length then
                    value.Instantiate(List.zip parameters arguments)
                else value
            | _ -> value
        with _ ->
            value

    let memberTypeMatchesSignature
        (target: FSharpMemberOrFunctionOrValue)
        (signature: FSharpAbstractSignature)
        (targetType: FSharpType)
        (signatureType: FSharpType) =
        sameType targetType signatureType ||
        sameType (instantiateForDeclaringType target signature targetType) signatureType

    let memberMatchesSignature
        (target: FSharpMemberOrFunctionOrValue)
        (signature: FSharpAbstractSignature) =
        try
            let declaringMatches =
                match target.DeclaringEntity, typeDefinition signature.DeclaringType with
                | Some owner, Some declaring -> sameEntity owner declaring
                | _ -> false
            let nameMatches =
                String.Equals(target.CompiledName, signature.Name, StringComparison.Ordinal) ||
                String.Equals(target.LogicalName, signature.Name, StringComparison.Ordinal)
            let targetGroups = target.CurriedParameterGroups |> Seq.map Seq.toArray |> Seq.toArray
            let signatureGroups = signature.AbstractArguments |> Seq.map Seq.toArray |> Seq.toArray
            let argumentsMatch =
                targetGroups.Length = signatureGroups.Length &&
                Array.forall2 (fun (targetGroup: FSharpParameter array) (signatureGroup: FSharpAbstractParameter array) ->
                    targetGroup.Length = signatureGroup.Length &&
                    Array.forall2 (fun (targetParameter: FSharpParameter) (signatureParameter: FSharpAbstractParameter) ->
                        memberTypeMatchesSignature target signature
                            targetParameter.Type signatureParameter.Type)
                        targetGroup signatureGroup)
                    targetGroups signatureGroups
            let targetMethodGenericCount =
                let declaringGenericCount =
                    match target.DeclaringEntity with
                    | Some owner -> owner.GenericParameters.Count
                    | None -> 0
                max 0 (target.GenericParameters.Count - declaringGenericCount)
            let genericMatches =
                targetMethodGenericCount = signature.MethodGenericParameters.Count
            let returnMatches =
                memberTypeMatchesSignature target signature
                    target.ReturnParameter.Type signature.AbstractReturnType
            declaringMatches && nameMatches && argumentsMatch && genericMatches && returnMatches
        with _ ->
            false

    let signaturesMatch (left: FSharpAbstractSignature) (right: FSharpAbstractSignature) =
        try
            let declaringMatches =
                match typeDefinition left.DeclaringType, typeDefinition right.DeclaringType with
                | Some leftEntity, Some rightEntity -> sameEntity leftEntity rightEntity
                | _ -> false
            let leftGroups = left.AbstractArguments |> Seq.map Seq.toArray |> Seq.toArray
            let rightGroups = right.AbstractArguments |> Seq.map Seq.toArray |> Seq.toArray
            declaringMatches &&
            String.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            left.MethodGenericParameters.Count = right.MethodGenericParameters.Count &&
            leftGroups.Length = rightGroups.Length &&
            Array.forall2 (fun (leftGroup: FSharpAbstractParameter array) (rightGroup: FSharpAbstractParameter array) ->
                leftGroup.Length = rightGroup.Length &&
                Array.forall2 (fun (leftParameter: FSharpAbstractParameter) (rightParameter: FSharpAbstractParameter) ->
                    sameType leftParameter.Type rightParameter.Type) leftGroup rightGroup)
                leftGroups rightGroups &&
            sameType left.AbstractReturnType right.AbstractReturnType
        with _ ->
            false

    type TargetSlot =
        | MemberSlot of FSharpMemberOrFunctionOrValue
        | AbstractSlot of FSharpAbstractSignature

    let slotMatchesSignature target signature =
        match target with
        | MemberSlot memberValue -> memberMatchesSignature memberValue signature
        | AbstractSlot abstractSignature -> signaturesMatch abstractSignature signature

    let memberVariants (value: FSharpMemberOrFunctionOrValue) =
        seq {
            yield value
            try if value.HasGetterMethod then yield value.GetterMethod with _ -> ()
            try if value.HasSetterMethod then yield value.SetterMethod with _ -> ()
            try if value.IsEvent then yield value.EventAddMethod with _ -> ()
            try if value.IsEvent then yield value.EventRemoveMethod with _ -> ()
        }
        |> Seq.distinctBy (fun memberValue ->
            safeString (fun () -> memberValue.CompiledName),
            safeString (fun () -> memberValue.FullName))
        |> Seq.toArray

    let propertyAccessorAtPosition
        (contents: FSharpAssemblyContents)
        targetFileName
        line
        column
        (propertyValue: FSharpMemberOrFunctionOrValue) =
        let variants = memberVariants propertyValue
        let candidates = ResizeArray<FSharpMemberOrFunctionOrValue * range>()
        let addCandidate (expression: FSharpExpr)
            (calledMember: FSharpMemberOrFunctionOrValue) =
            if pathComparer.Equals(expression.Range.FileName, targetFileName) &&
               containsPosition line column expression.Range &&
               variants |> Array.exists (fun variant ->
                   try
                       (variant :> FSharpSymbol).IsEffectivelySameAs(
                           calledMember :> FSharpSymbol)
                   with _ -> false) then
                candidates.Add(calledMember, expression.Range)
        let rec visitExpression (expression: FSharpExpr) =
            match expression with
            | FSharpExprPatterns.Quote _ -> ()
            | FSharpExprPatterns.Call(_, calledMember, _, _, _)
            | FSharpExprPatterns.CallWithWitnesses(_, calledMember, _, _, _, _) ->
                addCandidate expression calledMember
                expression.ImmediateSubExpressions |> Seq.iter visitExpression
            | _ -> expression.ImmediateSubExpressions |> Seq.iter visitExpression
        let rec visitDeclaration declaration =
            match declaration with
            | FSharpImplementationFileDeclaration.Entity(_, declarations) ->
                declarations |> Seq.iter visitDeclaration
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(_, _, body)
            | FSharpImplementationFileDeclaration.InitAction body ->
                visitExpression body
        for implementationFile in contents.ImplementationFiles do
            implementationFile.Declarations |> Seq.iter visitDeclaration
        candidates
        |> Seq.sortBy (fun (_, expressionRange) ->
            expressionRange.EndLine - expressionRange.StartLine,
            expressionRange.EndColumn - expressionRange.StartColumn,
            expressionRange.StartLine,
            expressionRange.StartColumn)
        |> Seq.tryHead
        |> Option.map fst

    let constructorAtPosition
        (contents: FSharpAssemblyContents)
        targetFileName
        line
        column
        (targetEntity: FSharpEntity) =
        let candidates = ResizeArray<FSharpMemberOrFunctionOrValue * range>()
        let rec visitExpression (expression: FSharpExpr) =
            match expression with
            | FSharpExprPatterns.Quote _ -> ()
            | FSharpExprPatterns.NewObject(constructorValue, _, _) ->
                let matches =
                    match constructorValue.DeclaringEntity with
                    | Some declaringEntity ->
                        try
                            (declaringEntity :> FSharpSymbol).IsEffectivelySameAs(
                                targetEntity :> FSharpSymbol)
                        with _ -> false
                    | None -> false
                if matches &&
                   pathComparer.Equals(expression.Range.FileName, targetFileName) &&
                   containsPosition line column expression.Range then
                    candidates.Add(constructorValue, expression.Range)
                expression.ImmediateSubExpressions |> Seq.iter visitExpression
            | _ -> expression.ImmediateSubExpressions |> Seq.iter visitExpression
        let rec visitDeclaration declaration =
            match declaration with
            | FSharpImplementationFileDeclaration.Entity(_, declarations) ->
                declarations |> Seq.iter visitDeclaration
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(_, _, body)
            | FSharpImplementationFileDeclaration.InitAction body ->
                visitExpression body
        for implementationFile in contents.ImplementationFiles do
            implementationFile.Declarations |> Seq.iter visitDeclaration
        candidates
        |> Seq.sortBy (fun (_, expressionRange) ->
            expressionRange.EndLine - expressionRange.StartLine,
            expressionRange.EndColumn - expressionRange.StartColumn,
            expressionRange.StartLine,
            expressionRange.StartColumn)
        |> Seq.tryHead
        |> Option.map fst

    let findMemberForSignature (signature: FSharpAbstractSignature) =
        try
            match typeDefinition signature.DeclaringType with
            | None -> None
            | Some entity ->
                entity.MembersFunctionsAndValues
                |> Seq.collect memberVariants
                |> Seq.tryFind (fun memberValue -> memberMatchesSignature memberValue signature)
        with _ ->
            None

    let slotDisplay (signature: FSharpAbstractSignature) =
        let declaring =
            match typeDefinition signature.DeclaringType with
            | Some entity -> entityDisplay entity
            | None -> typeDisplay signature.DeclaringType
        if String.IsNullOrEmpty(declaring) then signature.Name
        else declaring + "." + signature.Name

    let implementationSymbolForObjectExpression
        (targetName: string)
        (assemblyName: string)
        (expressionRange: range) =
        let declaration = location "implementation" expressionRange
        SemanticSymbol(
            targetName,
            nullString,
            "objectExpression",
            nullString,
            nullString,
            assemblyName,
            nullString,
            declaration,
            [| declaration |],
            assemblyName + "\u0000objectExpression\u0000" +
            expressionRangeKey expressionRange
        )

    let resolveImplementations (timing: ISemanticTiming)
        (projects: SemanticProjectInput array)
        rootProjectIndex
        lookupProjectIndex
        fingerprint
        cacheRuntime
        targetFileName
        line
        column
        (implementationTraversalBoundary: Action<string>)
        =
        async {
            let! cancellationToken = Async.CancellationToken
            if projects.Length = 0 || rootProjectIndex < 0 ||
               rootProjectIndex >= projects.Length || lookupProjectIndex < 0 ||
               lookupProjectIndex >= projects.Length || column <= 0 ||
               projects |> Array.exists (fun project ->
                   project.SourceFiles.Length = 0 ||
                   project.SourceFiles.Length <> project.SourceTexts.Length) ||
               projects |> Array.mapi (fun index project ->
                   project.ReferencedProjectIndices |> Array.exists (fun child ->
                       child < 0 || child >= index)) |> Array.exists id then
                return
                    implementationResult nullSymbol Array.empty Array.empty
                        "fsharp_semantic_snapshot_invalid" Array.empty Array.empty false
            else
                let lookupProject = projects[lookupProjectIndex]
                let! closure =
                    checkAssemblyContents timing projects fingerprint cacheRuntime
                        "PhoenixCodeNav.implementations" cancellationToken
                let checker = closure.Checker
                let optionsByIndex = closure.OptionsByIndex
                let checkedProjects = closure.Projects
                let allProjectDiagnostics = closure.Diagnostics
                if closure.HasCriticalErrors then
                    return
                        implementationResult nullSymbol Array.empty Array.empty
                            "fsharp_semantic_check_failed" allProjectDiagnostics Array.empty false
                else
                    let targetIndex =
                        lookupProject.SourceFiles
                        |> Array.tryFindIndex (fun fileName ->
                            pathComparer.Equals(fileName, targetFileName))
                    match targetIndex with
                    | None ->
                        return
                            implementationResult nullSymbol Array.empty Array.empty
                                "fsharp_semantic_target_not_in_project" allProjectDiagnostics Array.empty false
                    | Some targetIndex ->
                        let! _, answer =
                            measure timing SemanticPhase.FileParseAndCheck (fun () -> checker.ParseAndCheckFileInProject(
                                targetFileName,
                                0,
                                SourceText.ofString lookupProject.SourceTexts[targetIndex],
                                optionsByIndex[lookupProjectIndex],
                                userOpName = "PhoenixCodeNav.implementations"
                            ))
                        match answer with
                        | FSharpCheckFileAnswer.Aborted ->
                            return
                                implementationResult nullSymbol Array.empty Array.empty
                                    "fsharp_semantic_check_aborted" allProjectDiagnostics Array.empty false
                        | FSharpCheckFileAnswer.Succeeded checkedFile when
                            not checkedFile.HasFullTypeCheckInfo ->
                            let diagnostics = mergeDiagnostics allProjectDiagnostics checkedFile.Diagnostics
                            return
                                implementationResult nullSymbol Array.empty Array.empty
                                    "fsharp_semantic_check_incomplete" diagnostics Array.empty false
                        | FSharpCheckFileAnswer.Succeeded checkedFile ->
                            let diagnostics = mergeDiagnostics allProjectDiagnostics checkedFile.Diagnostics
                            let sourceText = SourceText.ofString lookupProject.SourceTexts[targetIndex]
                            if line < 1 || line > sourceText.GetLineCount() then
                                return
                                    implementationResult nullSymbol Array.empty Array.empty
                                        "fsharp_semantic_position_invalid" diagnostics Array.empty false
                            else
                                let lineText = sourceText.GetLineString(line - 1)
                                let cursor = column - 1
                                let symbolUse =
                                    if cursor < 0 || cursor > lineText.Length then None
                                    else
                                        match QuickParse.GetCompleteIdentifierIsland true lineText cursor with
                                        | Some (identifier, endColumn, _) ->
                                            let names = identifier.Split('.') |> Array.toList
                                            checkedFile.GetSymbolUseAtLocation(line, endColumn, lineText, names)
                                        | None -> None
                                match symbolUse with
                                | None ->
                                    return
                                        implementationResult nullSymbol Array.empty Array.empty
                                            "fsharp_symbol_not_resolved" diagnostics Array.empty false
                                | Some symbolUse ->
                                    let requestedFSharpSymbol =
                                        match symbolUse.Symbol with
                                        | :? FSharpMemberOrFunctionOrValue as memberValue when
                                            not memberValue.IsOverrideOrExplicitInterfaceImplementation ->
                                            match overrideMemberAtPosition
                                                checkedProjects[lookupProjectIndex].AssemblySignature
                                                targetFileName line column with
                                            | Some overrideMember -> overrideMember :> FSharpSymbol
                                            | None -> symbolUse.Symbol
                                        | _ -> symbolUse.Symbol
                                    let requestedSymbol = mappedSymbol symbolUse.Range requestedFSharpSymbol
                                    let mutable targetEntity: FSharpEntity option = None
                                    let mutable targetSlots: TargetSlot array = Array.empty
                                    let mutable targetSymbols: SemanticSymbol array = Array.empty
                                    let mutable resolvedFromOverride: string array = Array.empty
                                    let mutable targetName = requestedSymbol.Name
                                    let mutable targetError: string = nullString
                                    match requestedFSharpSymbol with
                                    | :? FSharpEntity as entity when
                                        not entity.IsNamespace && not entity.IsFSharpModule ->
                                        targetEntity <- Some entity
                                        targetSymbols <- [| requestedSymbol |]
                                        targetName <- entityDisplay entity
                                    | :? FSharpMemberOrFunctionOrValue as memberValue when
                                        memberValue.IsOverrideOrExplicitInterfaceImplementation ->
                                        let signatures =
                                            memberValue.ImplementedAbstractSignatures |> Seq.toArray
                                        let members = signatures |> Array.map findMemberForSignature
                                        if signatures.Length = 0 || members |> Array.exists Option.isNone then
                                            targetError <- "fsharp_implementations_slot_unresolved"
                                        else
                                            targetSlots <- signatures |> Array.map AbstractSlot
                                            targetSymbols <-
                                                members
                                                |> Array.choose id
                                                |> Array.map (fun value ->
                                                    mappedSymbol symbolUse.Range (value :> FSharpSymbol))
                                            resolvedFromOverride <-
                                                signatures |> Array.map slotDisplay |> Array.distinct
                                            targetName <- resolvedFromOverride[0]
                                    | :? FSharpMemberOrFunctionOrValue as memberValue when
                                        memberValue.IsDispatchSlot ->
                                        targetSlots <- memberVariants memberValue |> Array.map MemberSlot
                                        targetSymbols <- [| requestedSymbol |]
                                        targetName <- requestedSymbol.Name
                                    | _ ->
                                        targetError <- "unsupported_symbol_kind"

                                    if not (String.IsNullOrEmpty(targetError)) then
                                        return
                                            implementationResult requestedSymbol targetSymbols Array.empty
                                                targetError diagnostics resolvedFromOverride false
                                    else
                                        let hits = ResizeArray<SemanticImplementation>()
                                        let hitKeys = HashSet<string>(StringComparer.Ordinal)
                                        let mutable quotationsExcluded = false
                                        let addHit projectIndex implementationKind isAbstract viaValue
                                            (symbol: SemanticSymbol) =
                                            let declaration = symbol.UseLocation
                                            let key =
                                                pathIdentityKey declaration.FileName + "\u0000" +
                                                string declaration.StartLine + "\u0000" +
                                                string declaration.StartColumn + "\u0000" +
                                                string declaration.EndLine + "\u0000" +
                                                string declaration.EndColumn + "\u0000" +
                                                implementationKind
                                            if hitKeys.Add(key) then
                                                hits.Add(SemanticImplementation(
                                                    symbol, implementationKind, isAbstract, viaValue,
                                                    projectIndex))

                                        for projectIndex in 0 .. checkedProjects.Length - 1 do
                                            cancellationToken.ThrowIfCancellationRequested()
                                            if not (isNull implementationTraversalBoundary) then
                                                implementationTraversalBoundary.Invoke(
                                                    projects[projectIndex].ProjectFileName)
                                            cancellationToken.ThrowIfCancellationRequested()
                                            let checkedProject = checkedProjects[projectIndex]
                                            let addEntityCandidate (entity: FSharpEntity) =
                                                cancellationToken.ThrowIfCancellationRequested()
                                                match targetEntity with
                                                | Some target ->
                                                    let implementationKind =
                                                        if target.IsInterface then "interfaceImplementation"
                                                        else "derivedType"
                                                    let matches, viaValue = entityRelation target entity
                                                    if matches then
                                                        let declaration = entity.DeclarationLocation
                                                        addHit projectIndex implementationKind
                                                            (entityIsAbstract entity) viaValue
                                                            (mappedSymbol declaration
                                                                (entity :> FSharpSymbol))
                                                | None -> ()

                                            let addMemberCandidate
                                                (candidate: FSharpMemberOrFunctionOrValue) =
                                                cancellationToken.ThrowIfCancellationRequested()
                                                match targetEntity with
                                                | Some _ -> ()
                                                | None ->
                                                    if candidate.IsOverrideOrExplicitInterfaceImplementation &&
                                                       candidate.ImplementedAbstractSignatures
                                                       |> Seq.exists (fun signature ->
                                                           targetSlots |> Array.exists (fun target ->
                                                               slotMatchesSignature target signature)) then
                                                        let declaration = candidate.DeclarationLocation
                                                        let isAbstract =
                                                            match candidate.DeclaringEntity with
                                                            | Some owner -> entityIsAbstract owner
                                                            | None -> false
                                                        addHit projectIndex "override" isAbstract nullString
                                                            (mappedSymbol declaration
                                                                (candidate :> FSharpSymbol))

                                            let assemblyName =
                                                Path.GetFileNameWithoutExtension(projects[projectIndex].OutputFile)
                                            let rec visitExpression (expression: FSharpExpr) =
                                                cancellationToken.ThrowIfCancellationRequested()
                                                match expression with
                                                | FSharpExprPatterns.Quote _ ->
                                                    quotationsExcluded <- true
                                                | FSharpExprPatterns.ObjectExpr(
                                                    baseType, _baseCall, overrides, interfaceImplementations) ->
                                                    let objectMatches =
                                                        match targetEntity with
                                                        | Some target when target.IsInterface ->
                                                            seq {
                                                                yield baseType
                                                                yield! interfaceImplementations |> Seq.map fst
                                                            }
                                                            |> Seq.exists (typeOrInterfacesTargetEntity target)
                                                        | Some target -> typeOrBaseTargetsEntity target baseType
                                                        | None ->
                                                            seq {
                                                                yield! overrides
                                                                yield! interfaceImplementations |> Seq.collect snd
                                                            }
                                                            |> Seq.exists (fun overrideValue ->
                                                                targetSlots |> Array.exists (fun target ->
                                                                    slotMatchesSignature target overrideValue.Signature))
                                                    if objectMatches then
                                                        addHit projectIndex "objectExpression" false nullString
                                                            (implementationSymbolForObjectExpression
                                                                targetName assemblyName expression.Range)
                                                    expression.ImmediateSubExpressions
                                                    |> Seq.iter visitExpression
                                                | _ ->
                                                    expression.ImmediateSubExpressions
                                                    |> Seq.iter visitExpression

                                            let rec visitDeclaration declaration =
                                                cancellationToken.ThrowIfCancellationRequested()
                                                match declaration with
                                                | FSharpImplementationFileDeclaration.Entity(entity, declarations) ->
                                                    addEntityCandidate entity
                                                    declarations |> Seq.iter visitDeclaration
                                                | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(
                                                    memberValue, _, body) ->
                                                    addMemberCandidate memberValue
                                                    visitExpression body
                                                | FSharpImplementationFileDeclaration.InitAction body ->
                                                    visitExpression body
                                            for implementationFile in
                                                checkedProject.AssemblyContents.ImplementationFiles do
                                                cancellationToken.ThrowIfCancellationRequested()
                                                implementationFile.Declarations
                                                |> Seq.iter visitDeclaration

                                        cancellationToken.ThrowIfCancellationRequested()
                                        let ordered =
                                            hits
                                            |> Seq.sortBy (fun hit ->
                                                (if hit.IsAbstract then 1 else 0),
                                                pathIdentityKey hit.Symbol.UseLocation.FileName,
                                                hit.Symbol.UseLocation.StartLine,
                                                hit.Symbol.UseLocation.StartColumn,
                                                hit.ImplementationKind)
                                            |> Seq.toArray
                                        return
                                            implementationResult requestedSymbol targetSymbols ordered nullString
                                                diagnostics resolvedFromOverride quotationsExcluded
        }

    let sameSymbol (left: FSharpSymbol) (right: FSharpSymbol) =
        try left.IsEffectivelySameAs(right)
        with _ -> false

    let rangeContainsRange (outerRange: range) (innerRange: range) =
        pathComparer.Equals(outerRange.FileName, innerRange.FileName) &&
        (innerRange.StartLine > outerRange.StartLine ||
         innerRange.StartLine = outerRange.StartLine &&
         innerRange.StartColumn >= outerRange.StartColumn) &&
        (innerRange.EndLine < outerRange.EndLine ||
         innerRange.EndLine = outerRange.EndLine &&
         innerRange.EndColumn <= outerRange.EndColumn)

    let sameRange (left: range) (right: range) =
        pathComparer.Equals(left.FileName, right.FileName) &&
        left.StartLine = right.StartLine &&
        left.StartColumn = right.StartColumn &&
        left.EndLine = right.EndLine &&
        left.EndColumn = right.EndColumn

    let memberIsOperator (value: FSharpMemberOrFunctionOrValue) =
        try value.CompiledName.StartsWith("op_", StringComparison.Ordinal)
        with _ -> false

    let pipeFunctionArgument (value: FSharpMemberOrFunctionOrValue)
        (arguments: FSharpExpr array) =
        let fullName = safeString (fun () -> value.FullName)
        match fullName with
        | "Microsoft.FSharp.Core.Operators.(|>)"
        | "Microsoft.FSharp.Core.Operators.(||>)"
        | "Microsoft.FSharp.Core.Operators.(|||>)" when arguments.Length > 0 ->
            Some arguments[arguments.Length - 1]
        | "Microsoft.FSharp.Core.Operators.(<|)"
        | "Microsoft.FSharp.Core.Operators.(<||)"
        | "Microsoft.FSharp.Core.Operators.(<|||)" when arguments.Length > 0 ->
            Some arguments[0]
        | _ -> None

    let isPipeMember (value: FSharpMemberOrFunctionOrValue) =
        pipeFunctionArgument value [| Unchecked.defaultof<FSharpExpr> |]
        |> Option.isSome

    let useMatchesMember (symbolUse: FSharpSymbolUse)
        (memberValue: FSharpMemberOrFunctionOrValue) =
        try
            match symbolUse.Symbol with
            | :? FSharpMemberOrFunctionOrValue as usedMember ->
                sameSymbol (usedMember :> FSharpSymbol) (memberValue :> FSharpSymbol) ||
                (usedMember.IsProperty &&
                 ((usedMember.HasGetterMethod &&
                   sameSymbol (usedMember.GetterMethod :> FSharpSymbol)
                       (memberValue :> FSharpSymbol)) ||
                  (usedMember.HasSetterMethod &&
                   sameSymbol (usedMember.SetterMethod :> FSharpSymbol)
                       (memberValue :> FSharpSymbol))))
            | _ -> false
        with _ -> false

    let tryUseForMember (uses: FSharpSymbolUse array)
        (memberValue: FSharpMemberOrFunctionOrValue) (expressionRange: range) =
        uses
        |> Array.filter (fun symbolUse ->
            not symbolUse.IsFromDefinition &&
            rangeContainsRange expressionRange symbolUse.Range &&
            useMatchesMember symbolUse memberValue)
        |> Array.sortBy (fun symbolUse ->
            symbolUse.Range.StartLine,
            symbolUse.Range.StartColumn,
            symbolUse.Range.EndLine - symbolUse.Range.StartLine,
            symbolUse.Range.EndColumn - symbolUse.Range.StartColumn)
        |> Array.tryHead

    let useMatchesConstructor (symbolUse: FSharpSymbolUse)
        (memberValue: FSharpMemberOrFunctionOrValue) =
        try
            useMatchesMember symbolUse memberValue ||
            (match symbolUse.Symbol, memberValue.DeclaringEntity with
             | (:? FSharpEntity as usedEntity), Some declaringEntity ->
                 sameSymbol (usedEntity :> FSharpSymbol) (declaringEntity :> FSharpSymbol)
             | _ -> false)
        with _ -> false

    let tryUseForConstructor (uses: FSharpSymbolUse array)
        (memberValue: FSharpMemberOrFunctionOrValue) (expressionRange: range) =
        uses
        |> Array.filter (fun symbolUse ->
            not symbolUse.IsFromDefinition &&
            rangeContainsRange expressionRange symbolUse.Range &&
            useMatchesConstructor symbolUse memberValue)
        |> Array.sortBy (fun symbolUse ->
            symbolUse.Range.StartLine,
            symbolUse.Range.StartColumn,
            symbolUse.Range.EndLine - symbolUse.Range.StartLine,
            symbolUse.Range.EndColumn - symbolUse.Range.StartColumn)
        |> Array.tryHead

    let tryActivePatternUse (uses: FSharpSymbolUse array) (expressionRange: range) =
        uses
        |> Array.filter (fun symbolUse ->
            not symbolUse.IsFromDefinition && symbolUse.IsFromPattern &&
            symbolUse.Symbol :? FSharpActivePatternCase &&
            rangeContainsRange expressionRange symbolUse.Range)
        |> Array.sortBy (fun symbolUse ->
            symbolUse.Range.StartLine,
            symbolUse.Range.StartColumn,
            symbolUse.Range.EndLine - symbolUse.Range.StartLine,
            symbolUse.Range.EndColumn - symbolUse.Range.StartColumn)
        |> Array.tryHead

    let targetMemberSupported (value: FSharpMemberOrFunctionOrValue) =
        try
            value.IsFunction || value.IsMethod || value.IsConstructor ||
            value.IsProperty || value.IsActivePattern || memberIsOperator value
        with _ -> false

    let moduleInitializerSymbol projectIndex (projects: SemanticProjectInput array)
        (expressionRange: range) =
        let assemblyName = Path.GetFileNameWithoutExtension(projects[projectIndex].OutputFile)
        let declaration = location "declaration" expressionRange
        SemanticSymbol(
            "<module initialization>",
            assemblyName + ".<module initialization>",
            "moduleInitializer",
            nullString,
            nullString,
            assemblyName,
            nullString,
            declaration,
            [| declaration |],
            assemblyName + "\u0000moduleInitializer\u0000" +
            expressionRangeKey expressionRange
        )

    let initializerSymbol projectIndex (projects: SemanticProjectInput array)
        (owner: FSharpEntity option) (expressionRange: range) =
        match owner with
        | Some entity when not entity.IsFSharpModule && not entity.IsNamespace ->
            let constructor =
                try
                    entity.MembersFunctionsAndValues
                    |> Seq.tryFind (fun value -> value.IsConstructor)
                with _ -> None
            match constructor with
            | Some value -> mappedSymbol value.DeclarationLocation (value :> FSharpSymbol)
            | None -> moduleInitializerSymbol projectIndex projects expressionRange
        | _ -> moduleInitializerSymbol projectIndex projects expressionRange

    let objectOverrideSymbol assemblyName (value: FSharpObjectExprOverride) =
        let declaration = location "implementation" value.Body.Range
        let fullName = slotDisplay value.Signature
        SemanticSymbol(
            value.Signature.Name,
            fullName,
            "objectExpressionOverride",
            nullString,
            nullString,
            assemblyName,
            nullString,
            declaration,
            [| declaration |],
            assemblyName + "\u0000objectExpressionOverride\u0000" + fullName +
            "\u0000" + expressionRangeKey value.Body.Range
        )

    let collectPipeOperandRanges (body: FSharpExpr) =
        let ranges = HashSet<string>(StringComparer.Ordinal)
        let rec visit (expression: FSharpExpr) =
            match expression with
            | FSharpExprPatterns.Quote _ -> ()
            | FSharpExprPatterns.Call(_, memberValue, _, _, arguments) ->
                let argumentArray = arguments |> Seq.toArray
                match pipeFunctionArgument memberValue argumentArray with
                | Some functionArgument ->
                    ranges.Add(expressionRangeKey functionArgument.Range) |> ignore
                | None -> ()
                expression.ImmediateSubExpressions |> Seq.iter visit
            | FSharpExprPatterns.CallWithWitnesses(
                _, memberValue, _, _, _, arguments) ->
                let argumentArray = arguments |> Seq.toArray
                match pipeFunctionArgument memberValue argumentArray with
                | Some functionArgument ->
                    ranges.Add(expressionRangeKey functionArgument.Range) |> ignore
                | None -> ()
                expression.ImmediateSubExpressions |> Seq.iter visit
            | _ -> expression.ImmediateSubExpressions |> Seq.iter visit
        visit body
        ranges

    let classifyMemberCall
        (memberValue: FSharpMemberOrFunctionOrValue)
        (expressionRange: range)
        (symbolUse: FSharpSymbolUse)
        (ancestors: FSharpExpr list)
        (pipelineOperands: HashSet<string>) =
        if symbolUse.IsFromComputationExpression then "computationExpression"
        elif not (targetMemberSupported memberValue) then "computationExpression"
        elif memberValue.IsActivePattern || symbolUse.Symbol :? FSharpActivePatternCase then
            "activePattern"
        elif memberIsOperator memberValue then "operator"
        else
            let insideEtaLambda =
                ancestors
                |> List.tryHead
                |> Option.exists (fun ancestor ->
                    match ancestor with
                    | FSharpExprPatterns.Lambda _ -> true
                    | _ -> false)
            let insidePipeline =
                ancestors
                |> List.exists (fun ancestor ->
                    pipelineOperands.Contains(expressionRangeKey ancestor.Range))
            if insidePipeline then "pipelineApplication"
            elif insideEtaLambda && sameRange expressionRange symbolUse.Range then
                "firstClassReference"
            elif insideEtaLambda then "partialApplication"
            else "directApplication"

    let resolveCallers (timing: ISemanticTiming)
        (projects: SemanticProjectInput array)
        rootProjectIndex
        lookupProjectIndex
        fingerprint
        cacheRuntime
        targetFileName
        line
        column
        (traversalBoundary: Action<string>) =
        async {
            let! cancellationToken = Async.CancellationToken
            if projects.Length = 0 || rootProjectIndex < 0 ||
               rootProjectIndex >= projects.Length || lookupProjectIndex < 0 ||
               lookupProjectIndex >= projects.Length || column <= 0 ||
               projects |> Array.exists (fun project ->
                   project.SourceFiles.Length = 0 ||
                   project.SourceFiles.Length <> project.SourceTexts.Length) ||
               projects |> Array.mapi (fun index project ->
                   project.ReferencedProjectIndices |> Array.exists (fun child ->
                       child < 0 || child >= index)) |> Array.exists id then
                return callGraphResult nullSymbol Array.empty
                    "fsharp_semantic_snapshot_invalid" Array.empty Array.empty false false false
            else
                let lookupProject = projects[lookupProjectIndex]
                let! closure =
                    checkAssemblyContents timing projects fingerprint cacheRuntime
                        "PhoenixCodeNav.callers" cancellationToken
                if closure.HasCriticalErrors then
                    return callGraphResult nullSymbol Array.empty
                        "fsharp_semantic_check_failed" closure.Diagnostics Array.empty false false false
                else
                    let targetIndex =
                        lookupProject.SourceFiles
                        |> Array.tryFindIndex (fun fileName ->
                            pathComparer.Equals(fileName, targetFileName))
                    match targetIndex with
                    | None ->
                        return callGraphResult nullSymbol Array.empty
                            "fsharp_semantic_target_not_in_project" closure.Diagnostics
                            Array.empty false false false
                    | Some targetIndex ->
                        let! _, answer =
                            measure timing SemanticPhase.FileParseAndCheck (fun () -> closure.Checker.ParseAndCheckFileInProject(
                                targetFileName,
                                0,
                                SourceText.ofString lookupProject.SourceTexts[targetIndex],
                                closure.OptionsByIndex[lookupProjectIndex],
                                userOpName = "PhoenixCodeNav.callers"
                            ))
                        match answer with
                        | FSharpCheckFileAnswer.Aborted ->
                            return callGraphResult nullSymbol Array.empty
                                "fsharp_semantic_check_aborted" closure.Diagnostics
                                Array.empty false false false
                        | FSharpCheckFileAnswer.Succeeded checkedFile when
                            not checkedFile.HasFullTypeCheckInfo ->
                            return callGraphResult nullSymbol Array.empty
                                "fsharp_semantic_check_incomplete"
                                (mergeDiagnostics closure.Diagnostics checkedFile.Diagnostics)
                                Array.empty false false false
                        | FSharpCheckFileAnswer.Succeeded checkedFile ->
                            let diagnostics =
                                mergeDiagnostics closure.Diagnostics checkedFile.Diagnostics
                            let sourceText = SourceText.ofString lookupProject.SourceTexts[targetIndex]
                            if line < 1 || line > sourceText.GetLineCount() then
                                return callGraphResult nullSymbol Array.empty
                                    "fsharp_semantic_position_invalid" diagnostics
                                    Array.empty false false false
                            else
                                let lineText = sourceText.GetLineString(line - 1)
                                let cursor = column - 1
                                let symbolUse =
                                    if cursor < 0 || cursor > lineText.Length then None
                                    else
                                        let identifierUse =
                                            match QuickParse.GetCompleteIdentifierIsland true lineText cursor with
                                            | Some(identifier, endColumn, _) ->
                                                checkedFile.GetSymbolUseAtLocation(
                                                    line, endColumn, lineText,
                                                    identifier.Split('.') |> Array.toList)
                                            | None -> None
                                        match identifierUse with
                                        | Some _ -> identifierUse
                                        | None ->
                                            checkedFile.GetAllUsesOfAllSymbolsInFile()
                                            |> Seq.filter (fun useValue ->
                                                containsPosition line column useValue.Range)
                                            |> Seq.sortBy rangeScore
                                            |> Seq.tryHead
                                match symbolUse with
                                | None ->
                                    return callGraphResult nullSymbol Array.empty
                                        "fsharp_symbol_not_resolved" diagnostics
                                        Array.empty false false false
                                | Some symbolUse ->
                                    let requestedBeforeAccessor =
                                        match symbolUse.Symbol with
                                        | :? FSharpMemberOrFunctionOrValue as memberValue when
                                            not memberValue.IsOverrideOrExplicitInterfaceImplementation ->
                                            match overrideMemberAtPosition
                                                closure.Projects[lookupProjectIndex].AssemblySignature
                                                targetFileName line column with
                                            | Some overrideMember -> overrideMember :> FSharpSymbol
                                            | None -> symbolUse.Symbol
                                        | _ -> symbolUse.Symbol
                                    let requested =
                                        match requestedBeforeAccessor with
                                        | :? FSharpEntity as entity when
                                            not entity.IsNamespace && not entity.IsFSharpModule ->
                                            match constructorAtPosition
                                                closure.Projects[lookupProjectIndex].AssemblyContents
                                                targetFileName line column entity with
                                            | Some constructorValue ->
                                                constructorValue :> FSharpSymbol
                                            | None -> requestedBeforeAccessor
                                        | :? FSharpMemberOrFunctionOrValue as memberValue when
                                            memberValue.IsProperty ->
                                            match propertyAccessorAtPosition
                                                closure.Projects[lookupProjectIndex].AssemblyContents
                                                targetFileName line column memberValue with
                                            | Some accessor -> accessor :> FSharpSymbol
                                            | None -> requestedBeforeAccessor
                                        | _ -> requestedBeforeAccessor
                                    let requestedMember =
                                        match requested with
                                        | :? FSharpMemberOrFunctionOrValue as value when
                                            targetMemberSupported value -> Some value
                                        | _ -> None
                                    let requestedUnionCase =
                                        match requested with
                                        | :? FSharpUnionCase as value -> Some value
                                        | _ -> None
                                    let requestedActivePatternCase =
                                        match requested with
                                        | :? FSharpActivePatternCase as value -> Some value
                                        | _ -> None
                                    if requestedMember.IsNone && requestedUnionCase.IsNone &&
                                       requestedActivePatternCase.IsNone then
                                        return callGraphResult
                                            (mappedSymbol symbolUse.Range requested)
                                            Array.empty "unsupported_symbol_kind" diagnostics
                                            Array.empty false false false
                                    else
                                        let mappedTarget = mappedSymbol symbolUse.Range requested
                                        let dispatchSlots =
                                            match requestedMember with
                                            | Some value when
                                                value.IsOverrideOrExplicitInterfaceImplementation ->
                                                value.ImplementedAbstractSignatures
                                                |> Seq.map slotDisplay
                                                |> Seq.distinct
                                                |> Seq.toArray
                                            | _ -> Array.empty
                                        let targetMembers =
                                            requestedMember
                                            |> Option.map memberVariants
                                            |> Option.defaultValue Array.empty
                                        let requestedIsOverride =
                                            requestedMember
                                            |> Option.exists (fun value ->
                                                value.IsOverrideOrExplicitInterfaceImplementation)
                                        let calledMemberMatches (calledMember: FSharpMemberOrFunctionOrValue) =
                                            targetMembers
                                            |> Array.exists (fun target ->
                                                try
                                                    (sameSymbol (target :> FSharpSymbol)
                                                         (calledMember :> FSharpSymbol) ||
                                                     (target.IsConstructor &&
                                                      calledMember.IsConstructor &&
                                                      sameRange target.DeclarationLocation
                                                          calledMember.DeclarationLocation)) &&
                                                    (not requestedIsOverride ||
                                                     sameRange target.DeclarationLocation
                                                         calledMember.DeclarationLocation)
                                                with _ -> false)
                                        let calls = ResizeArray<SemanticCall>()
                                        let usedSites = HashSet<string>(StringComparer.Ordinal)
                                        let mutable quotationsExcluded = false
                                        let mutable traitCallsUnresolved = false
                                        let mutable deadlineExhausted = false

                                        try
                                            for projectIndex in 0 .. closure.Projects.Length - 1 do
                                                cancellationToken.ThrowIfCancellationRequested()
                                                if not (isNull traversalBoundary) then
                                                    traversalBoundary.Invoke(
                                                        projects[projectIndex].ProjectFileName)
                                                cancellationToken.ThrowIfCancellationRequested()
                                                let targetUses =
                                                    match requestedMember with
                                                    | Some value when value.IsConstructor ->
                                                        closure.Projects[projectIndex]
                                                            .GetAllUsesOfAllSymbols(
                                                                cancellationToken = cancellationToken)
                                                        |> Array.filter (fun useValue ->
                                                            useMatchesConstructor useValue value)
                                                    | Some value when
                                                        value.CompiledName.StartsWith(
                                                            "get_", StringComparison.Ordinal) ||
                                                        value.CompiledName.StartsWith(
                                                            "set_", StringComparison.Ordinal) ->
                                                        closure.Projects[projectIndex]
                                                            .GetAllUsesOfAllSymbols(
                                                                cancellationToken = cancellationToken)
                                                        |> Array.filter (fun useValue ->
                                                            useMatchesMember useValue value)
                                                    | _ ->
                                                        closure.Projects[projectIndex].GetUsesOfSymbol(
                                                            requested,
                                                            cancellationToken = cancellationToken)
                                                    |> Array.filter (fun useValue ->
                                                        not useValue.IsFromDefinition &&
                                                        not useValue.IsFromType &&
                                                        not useValue.IsFromAttribute &&
                                                        not useValue.IsFromOpenStatement)
                                                let tryTakeUse (expressionRange: range) predicate =
                                                    targetUses
                                                    |> Array.filter (fun useValue ->
                                                        predicate useValue &&
                                                        rangeContainsRange expressionRange useValue.Range &&
                                                        not (usedSites.Contains(
                                                            expressionRangeKey useValue.Range)))
                                                    |> Array.sortBy (fun useValue ->
                                                        useValue.Range.StartLine,
                                                        useValue.Range.StartColumn,
                                                        useValue.Range.EndLine,
                                                        useValue.Range.EndColumn)
                                                    |> Array.tryHead

                                                let addCallAt caller callKind (callRange: range) =
                                                    let key = expressionRangeKey callRange
                                                    if usedSites.Add(key) then
                                                        calls.Add(SemanticCall(
                                                            caller,
                                                            mappedTarget,
                                                            location "call" callRange,
                                                            callKind,
                                                            projectIndex))

                                                let addCall caller callKind (useValue: FSharpSymbolUse) =
                                                    addCallAt caller callKind useValue.Range

                                                let assemblyName =
                                                    Path.GetFileNameWithoutExtension(
                                                        projects[projectIndex].OutputFile)

                                                let rec visitExpression caller ancestors
                                                    (pipelineOperands: HashSet<string>)
                                                    (expression: FSharpExpr) =
                                                    cancellationToken.ThrowIfCancellationRequested()
                                                    let visitChildren () =
                                                        expression.ImmediateSubExpressions
                                                        |> Seq.iter (visitExpression caller
                                                            (expression :: ancestors) pipelineOperands)
                                                    match expression with
                                                    | FSharpExprPatterns.Quote _ ->
                                                        quotationsExcluded <- true
                                                    | FSharpExprPatterns.TraitCall _ ->
                                                        traitCallsUnresolved <- true
                                                        visitChildren ()
                                                    | FSharpExprPatterns.Let(
                                                        (boundValue, boundExpression, _), continuation) ->
                                                        let boundCaller =
                                                            if targetMemberSupported boundValue then
                                                                mappedSymbol boundValue.DeclarationLocation
                                                                    (boundValue :> FSharpSymbol)
                                                            else caller
                                                        visitExpression boundCaller
                                                            (expression :: ancestors) pipelineOperands
                                                            boundExpression
                                                        visitExpression caller (expression :: ancestors)
                                                            pipelineOperands continuation
                                                    | FSharpExprPatterns.LetRec(bindings, continuation) ->
                                                        for boundValue, boundExpression, _ in bindings do
                                                            let boundCaller =
                                                                if targetMemberSupported boundValue then
                                                                    mappedSymbol boundValue.DeclarationLocation
                                                                        (boundValue :> FSharpSymbol)
                                                                else caller
                                                            visitExpression boundCaller
                                                                (expression :: ancestors) pipelineOperands
                                                                boundExpression
                                                        visitExpression caller (expression :: ancestors)
                                                            pipelineOperands continuation
                                                    | FSharpExprPatterns.ObjectExpr(
                                                        _, baseCall, overrides, interfaceImplementations) ->
                                                        visitExpression caller (expression :: ancestors)
                                                            pipelineOperands baseCall
                                                        for overrideValue in overrides do
                                                            visitExpression
                                                                (objectOverrideSymbol assemblyName overrideValue)
                                                                (expression :: ancestors) pipelineOperands
                                                                overrideValue.Body
                                                        for _, interfaceOverrides in interfaceImplementations do
                                                            for overrideValue in interfaceOverrides do
                                                                visitExpression
                                                                    (objectOverrideSymbol assemblyName overrideValue)
                                                                    (expression :: ancestors) pipelineOperands
                                                                    overrideValue.Body
                                                    | FSharpExprPatterns.Call(
                                                        _, calledMember, _, _, _) ->
                                                        let matches = calledMemberMatches calledMember
                                                        let activeMatch =
                                                            requestedActivePatternCase.IsSome &&
                                                            calledMember.IsActivePattern
                                                        if matches || activeMatch then
                                                            match tryTakeUse expression.Range (fun useValue ->
                                                                if activeMatch then
                                                                    useValue.Symbol :? FSharpActivePatternCase
                                                                else true) with
                                                            | Some useValue ->
                                                                addCall caller
                                                                    (classifyMemberCall calledMember
                                                                        expression.Range useValue ancestors
                                                                        pipelineOperands)
                                                                    useValue
                                                            | None -> ()
                                                        visitChildren ()
                                                    | FSharpExprPatterns.CallWithWitnesses(
                                                        _, calledMember, _, _, _, _) ->
                                                        let matches = calledMemberMatches calledMember
                                                        if matches then
                                                            match tryTakeUse expression.Range (fun _ -> true) with
                                                            | Some useValue ->
                                                                addCall caller
                                                                    (classifyMemberCall calledMember
                                                                        expression.Range useValue ancestors
                                                                        pipelineOperands)
                                                                    useValue
                                                            | None -> ()
                                                        visitChildren ()
                                                    | FSharpExprPatterns.NewObject(calledMember, _, _) ->
                                                        let matches = calledMemberMatches calledMember
                                                        if matches then
                                                            match tryTakeUse expression.Range (fun _ -> true) with
                                                            | Some useValue ->
                                                                addCall caller "construction" useValue
                                                            | None ->
                                                                addCallAt caller "construction" expression.Range
                                                        visitChildren ()
                                                    | FSharpExprPatterns.NewUnionCase(_, unionCase, _) ->
                                                        let matches = requestedUnionCase |> Option.exists (fun target ->
                                                            sameSymbol (target :> FSharpSymbol)
                                                                (unionCase :> FSharpSymbol))
                                                        if matches then
                                                            match tryTakeUse expression.Range (fun useValue ->
                                                                not useValue.IsFromPattern) with
                                                            | Some useValue ->
                                                                addCall caller "unionCaseConstruction" useValue
                                                            | None -> ()
                                                        visitChildren ()
                                                    | FSharpExprPatterns.Application(
                                                        functionValue, _, _) ->
                                                        match functionValue, requestedMember with
                                                        | FSharpExprPatterns.Value calledValue,
                                                          Some _ when calledMemberMatches calledValue ->
                                                            match tryTakeUse functionValue.Range
                                                                (fun _ -> true) with
                                                            | Some useValue ->
                                                                addCall caller "indirectApplication"
                                                                    useValue
                                                            | None -> ()
                                                        | _ -> ()
                                                        visitChildren ()
                                                    | FSharpExprPatterns.Value calledValue when
                                                        requestedMember.IsSome &&
                                                        calledMemberMatches calledValue ->
                                                        match tryTakeUse expression.Range
                                                            (fun _ -> true) with
                                                        | Some useValue ->
                                                            let insidePipeline =
                                                                pipelineOperands.Contains(
                                                                    expressionRangeKey expression.Range) ||
                                                                (ancestors |> List.exists (fun ancestor ->
                                                                    pipelineOperands.Contains(
                                                                        expressionRangeKey ancestor.Range)))
                                                            let callKind =
                                                                if insidePipeline then
                                                                    "pipelineApplication"
                                                                else "firstClassReference"
                                                            addCall caller callKind useValue
                                                        | None -> ()
                                                        visitChildren ()
                                                    | _ -> visitChildren ()

                                                let rec visitDeclaration ownerEntity declaration =
                                                    cancellationToken.ThrowIfCancellationRequested()
                                                    match declaration with
                                                    | FSharpImplementationFileDeclaration.Entity(
                                                        entity, declarations) ->
                                                        declarations
                                                        |> Seq.iter (visitDeclaration (Some entity))
                                                    | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(
                                                        memberValue, _, body) ->
                                                        let caller =
                                                            mappedSymbol memberValue.DeclarationLocation
                                                                (memberValue :> FSharpSymbol)
                                                        let pipelineOperands = collectPipeOperandRanges body
                                                        visitExpression caller [] pipelineOperands body
                                                    | FSharpImplementationFileDeclaration.InitAction body ->
                                                        let caller =
                                                            initializerSymbol projectIndex projects ownerEntity
                                                                body.Range
                                                        let pipelineOperands = collectPipeOperandRanges body
                                                        visitExpression caller [] pipelineOperands body

                                                for implementationFile in
                                                    closure.Projects[projectIndex].AssemblyContents.ImplementationFiles do
                                                    implementationFile.Declarations
                                                    |> Seq.iter (visitDeclaration None)
                                        with :? OperationCanceledException ->
                                            deadlineExhausted <- true

                                        let ordered =
                                            calls
                                            |> Seq.sortBy (fun call ->
                                                pathIdentityKey call.Site.FileName,
                                                call.Site.StartLine,
                                                call.Site.StartColumn,
                                                call.CallKind,
                                                call.Caller.FullName)
                                            |> Seq.toArray
                                        return callGraphResult mappedTarget ordered nullString diagnostics
                                            dispatchSlots quotationsExcluded traitCallsUnresolved
                                            deadlineExhausted
        }

    let resolveCallees (timing: ISemanticTiming)
        (projects: SemanticProjectInput array)
        rootProjectIndex
        lookupProjectIndex
        fingerprint
        cacheRuntime
        targetFileName
        line
        column
        (traversalBoundary: Action<string>) =
        async {
            let! cancellationToken = Async.CancellationToken
            if projects.Length = 0 || rootProjectIndex < 0 ||
               rootProjectIndex >= projects.Length || lookupProjectIndex < 0 ||
               lookupProjectIndex >= projects.Length || column <= 0 ||
               projects |> Array.exists (fun project ->
                   project.SourceFiles.Length = 0 ||
                   project.SourceFiles.Length <> project.SourceTexts.Length) ||
               projects |> Array.mapi (fun index project ->
                   project.ReferencedProjectIndices |> Array.exists (fun child ->
                       child < 0 || child >= index)) |> Array.exists id then
                return callGraphResult nullSymbol Array.empty
                    "fsharp_semantic_snapshot_invalid" Array.empty Array.empty false false false
            else
                let! closure =
                    checkAssemblyContents timing projects fingerprint cacheRuntime
                        "PhoenixCodeNav.callees" cancellationToken
                if closure.HasCriticalErrors then
                    return callGraphResult nullSymbol Array.empty
                        "fsharp_semantic_check_failed" closure.Diagnostics Array.empty false false false
                else
                    let checkedProject = closure.Projects[lookupProjectIndex]
                    let candidates = ResizeArray<SemanticSymbol * FSharpExpr * int>()
                    let assemblyName =
                        Path.GetFileNameWithoutExtension(
                            projects[lookupProjectIndex].OutputFile)
                    let rec expressionContainsPosition (expression: FSharpExpr) =
                        containsPosition line column expression.Range ||
                        (match expression with
                         | FSharpExprPatterns.Quote _ -> false
                         | _ ->
                             expression.ImmediateSubExpressions
                             |> Seq.exists expressionContainsPosition)
                    let addCandidate depth symbol (body: FSharpExpr) declarationRange =
                        if pathComparer.Equals(body.Range.FileName, targetFileName) &&
                           (expressionContainsPosition body ||
                            containsPosition line column declarationRange) then
                            candidates.Add(symbol, body, depth)
                    let rec findNestedBodies depth (expression: FSharpExpr) =
                        cancellationToken.ThrowIfCancellationRequested()
                        match expression with
                        | FSharpExprPatterns.Quote _ -> ()
                        | FSharpExprPatterns.Let(
                            (boundValue, boundExpression, _), continuation) ->
                            if targetMemberSupported boundValue then
                                addCandidate (depth + 1)
                                    (mappedSymbol boundValue.DeclarationLocation
                                        (boundValue :> FSharpSymbol))
                                    boundExpression boundValue.DeclarationLocation
                            findNestedBodies (depth + 1) boundExpression
                            findNestedBodies depth continuation
                        | FSharpExprPatterns.LetRec(bindings, continuation) ->
                            for boundValue, boundExpression, _ in bindings do
                                if targetMemberSupported boundValue then
                                    addCandidate (depth + 1)
                                        (mappedSymbol boundValue.DeclarationLocation
                                            (boundValue :> FSharpSymbol))
                                        boundExpression boundValue.DeclarationLocation
                                findNestedBodies (depth + 1) boundExpression
                            findNestedBodies depth continuation
                        | FSharpExprPatterns.ObjectExpr(
                            _, baseCall, overrides, interfaceImplementations) ->
                            findNestedBodies depth baseCall
                            for overrideValue in overrides do
                                addCandidate (depth + 1)
                                    (objectOverrideSymbol assemblyName overrideValue)
                                    overrideValue.Body overrideValue.Body.Range
                                findNestedBodies (depth + 1) overrideValue.Body
                            for _, interfaceOverrides in interfaceImplementations do
                                for overrideValue in interfaceOverrides do
                                    addCandidate (depth + 1)
                                        (objectOverrideSymbol assemblyName overrideValue)
                                        overrideValue.Body overrideValue.Body.Range
                                    findNestedBodies (depth + 1) overrideValue.Body
                        | _ ->
                            expression.ImmediateSubExpressions
                            |> Seq.iter (findNestedBodies depth)
                    let rec findDeclaration ownerEntity declaration =
                        cancellationToken.ThrowIfCancellationRequested()
                        match declaration with
                        | FSharpImplementationFileDeclaration.Entity(entity, declarations) ->
                            declarations |> Seq.iter (findDeclaration (Some entity))
                        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(
                            memberValue, _, body) ->
                            addCandidate 0
                                (mappedSymbol memberValue.DeclarationLocation
                                    (memberValue :> FSharpSymbol))
                                body memberValue.DeclarationLocation
                            findNestedBodies 0 body
                        | FSharpImplementationFileDeclaration.InitAction body ->
                            addCandidate 0
                                (initializerSymbol lookupProjectIndex projects ownerEntity body.Range)
                                body body.Range
                            findNestedBodies 0 body
                    for implementationFile in checkedProject.AssemblyContents.ImplementationFiles do
                        implementationFile.Declarations |> Seq.iter (findDeclaration None)
                    let selected =
                        candidates
                        |> Seq.sortBy (fun (_, body, depth) ->
                            -depth,
                            body.Range.EndLine - body.Range.StartLine,
                            body.Range.EndColumn - body.Range.StartColumn,
                            body.Range.StartLine,
                            body.Range.StartColumn)
                        |> Seq.tryHead
                    match selected with
                    | None ->
                        return callGraphResult nullSymbol Array.empty
                            "fsharp_callee_body_not_found" closure.Diagnostics
                            Array.empty false false false
                    | Some(caller, body, _) ->
                        let calls = ResizeArray<SemanticCall>()
                        let usedSites = HashSet<string>(StringComparer.Ordinal)
                        let mutable quotationsExcluded = false
                        let mutable traitCallsUnresolved = false
                        let mutable deadlineExhausted = false
                        try
                            if not (isNull traversalBoundary) then
                                traversalBoundary.Invoke(
                                    projects[lookupProjectIndex].ProjectFileName)
                            cancellationToken.ThrowIfCancellationRequested()
                            let uses =
                                checkedProject.GetAllUsesOfAllSymbols(
                                    cancellationToken = cancellationToken)
                                |> Array.filter (fun useValue ->
                                    not useValue.IsFromDefinition &&
                                    pathComparer.Equals(useValue.Range.FileName, targetFileName))
                            let pipelineOperands = collectPipeOperandRanges body
                            let addCallAt callee callKind (callRange: range) =
                                let key = expressionRangeKey callRange + "\u0000" + callKind
                                if usedSites.Add(key) then
                                    calls.Add(SemanticCall(
                                        caller,
                                        mappedSymbol callRange callee,
                                        location "call" callRange,
                                        callKind,
                                        lookupProjectIndex))

                            let addCall callee callKind (symbolUse: FSharpSymbolUse) =
                                addCallAt callee callKind symbolUse.Range
                            let rec visit ancestors (expression: FSharpExpr) =
                                cancellationToken.ThrowIfCancellationRequested()
                                let visitChildren () =
                                    expression.ImmediateSubExpressions
                                    |> Seq.iter (visit (expression :: ancestors))
                                match expression with
                                | FSharpExprPatterns.Quote _ -> quotationsExcluded <- true
                                | FSharpExprPatterns.TraitCall _ ->
                                    traitCallsUnresolved <- true
                                    visitChildren ()
                                | FSharpExprPatterns.Let(
                                    (boundValue, boundExpression, _), continuation) ->
                                    if not (targetMemberSupported boundValue) then
                                        visit (expression :: ancestors) boundExpression
                                    visit (expression :: ancestors) continuation
                                | FSharpExprPatterns.LetRec(bindings, continuation) ->
                                    for boundValue, boundExpression, _ in bindings do
                                        if not (targetMemberSupported boundValue) then
                                            visit (expression :: ancestors) boundExpression
                                    visit (expression :: ancestors) continuation
                                | FSharpExprPatterns.ObjectExpr(
                                    _, baseCall, _, _) ->
                                    visit (expression :: ancestors) baseCall
                                | FSharpExprPatterns.Call(_, memberValue, _, _, _) ->
                                    if not (isPipeMember memberValue) then
                                        let matchedUse =
                                            match tryUseForMember uses memberValue expression.Range with
                                            | Some useValue -> Some useValue
                                            | None when memberValue.IsActivePattern ->
                                                tryActivePatternUse uses expression.Range
                                            | None -> None
                                        match matchedUse with
                                        | Some useValue ->
                                            let callKind =
                                                classifyMemberCall memberValue expression.Range
                                                    useValue ancestors pipelineOperands
                                            if callKind <> "firstClassReference" then
                                                addCall useValue.Symbol callKind useValue
                                        | None -> ()
                                    visitChildren ()
                                | FSharpExprPatterns.CallWithWitnesses(
                                    _, memberValue, _, _, _, _) ->
                                    if not (isPipeMember memberValue) then
                                        match tryUseForMember uses memberValue expression.Range with
                                        | Some useValue ->
                                            let callKind =
                                                classifyMemberCall memberValue expression.Range
                                                    useValue ancestors pipelineOperands
                                            if callKind <> "firstClassReference" then
                                                addCall useValue.Symbol callKind useValue
                                        | None -> ()
                                    visitChildren ()
                                | FSharpExprPatterns.NewObject(memberValue, _, _) ->
                                    match tryUseForConstructor uses memberValue expression.Range with
                                    | Some useValue ->
                                        addCall (memberValue :> FSharpSymbol) "construction" useValue
                                    | None ->
                                        addCallAt (memberValue :> FSharpSymbol) "construction"
                                            expression.Range
                                    visitChildren ()
                                | FSharpExprPatterns.NewUnionCase(_, unionCase, _) ->
                                    let matchedUse =
                                        uses
                                        |> Array.filter (fun useValue ->
                                            not useValue.IsFromPattern &&
                                            rangeContainsRange expression.Range useValue.Range &&
                                            sameSymbol useValue.Symbol (unionCase :> FSharpSymbol))
                                        |> Array.tryHead
                                    match matchedUse with
                                    | Some useValue ->
                                        addCall useValue.Symbol "unionCaseConstruction" useValue
                                    | None -> ()
                                    visitChildren ()
                                | FSharpExprPatterns.Application(functionValue, _, _) ->
                                    match functionValue with
                                    | FSharpExprPatterns.Value value ->
                                        let matchedUse =
                                            uses
                                            |> Array.filter (fun useValue ->
                                                rangeContainsRange functionValue.Range useValue.Range &&
                                                sameSymbol useValue.Symbol (value :> FSharpSymbol))
                                            |> Array.tryHead
                                        match matchedUse with
                                        | Some useValue ->
                                            addCall useValue.Symbol "indirectApplication" useValue
                                        | None -> ()
                                    | _ -> ()
                                    visitChildren ()
                                | FSharpExprPatterns.Value value when
                                    pipelineOperands.Contains(
                                        expressionRangeKey expression.Range) ->
                                    let matchedUse =
                                        uses
                                        |> Array.filter (fun useValue ->
                                            not useValue.IsFromDefinition &&
                                            rangeContainsRange expression.Range useValue.Range &&
                                            sameSymbol useValue.Symbol (value :> FSharpSymbol))
                                        |> Array.tryHead
                                    match matchedUse with
                                    | Some useValue ->
                                        addCall useValue.Symbol "pipelineApplication" useValue
                                    | None -> ()
                                    visitChildren ()
                                | _ -> visitChildren ()
                            visit [] body
                        with :? OperationCanceledException ->
                            deadlineExhausted <- true
                        let ordered =
                            calls
                            |> Seq.sortBy (fun call ->
                                pathIdentityKey call.Site.FileName,
                                call.Site.StartLine,
                                call.Site.StartColumn,
                                call.CallKind,
                                call.Callee.FullName)
                            |> Seq.toArray
                        return callGraphResult caller ordered nullString closure.Diagnostics
                            Array.empty quotationsExcluded traitCallsUnresolved deadlineExhausted
        }

    let resolve (timing: ISemanticTiming)
        (projects: SemanticProjectInput array)
        rootProjectIndex
        lookupProjectIndex
        fingerprint
        cacheRuntime
        targetFileName
        line
        column
        maxLineOnlySourceChars
        includeReferences
        =
        async {
            let! cancellationToken = Async.CancellationToken
            if projects.Length = 0 || rootProjectIndex < 0 ||
               rootProjectIndex >= projects.Length || lookupProjectIndex < 0 ||
               lookupProjectIndex >= projects.Length ||
               projects |> Array.exists (fun project ->
                   project.SourceFiles.Length = 0 ||
                   project.SourceFiles.Length <> project.SourceTexts.Length) ||
               projects |> Array.mapi (fun index project ->
                   project.ReferencedProjectIndices |> Array.exists (fun child ->
                       child < 0 || child >= index)) |> Array.exists id then
                return checkResult nullSymbol "fsharp_semantic_snapshot_invalid" Array.empty
            else
                let lookupProject = projects[lookupProjectIndex]
                use setup = timing.StartPhase(SemanticPhase.Setup)
                let sourceFiles = projects |> Array.collect (fun project -> project.SourceFiles)
                let sourceTexts = projects |> Array.collect (fun project -> project.SourceTexts)
                let runtime = runtime fingerprint sourceFiles sourceTexts cacheRuntime
                let checker = runtime.Checker
                let optionsByIndex = Array.zeroCreate<FSharpProjectOptions> projects.Length
                for index in 0 .. projects.Length - 1 do
                    let project = projects[index]
                    let baseOptions =
                        checker.GetProjectOptionsFromCommandLineArgs(
                            project.ProjectFileName,
                            project.CommandLineArgs,
                            loadedTimeStamp = DateTime.UnixEpoch,
                            isEditing = false,
                            isInteractive = false
                        )
                    let referencedProjects =
                        project.ReferencedProjectIndices
                        |> Array.map (fun child ->
                            FSharpReferencedProject.FSharpReference(
                                projects[child].OutputFile,
                                optionsByIndex[child]
                            ))
                    optionsByIndex[index] <-
                        { baseOptions with ReferencedProjects = referencedProjects }
                let operationName =
                    if includeReferences then "PhoenixCodeNav.references" else "PhoenixCodeNav.symbol_at"
                setup.Dispose()
                let checkedProjects = Array.zeroCreate projects.Length
                let projectDiagnostics = ResizeArray<FSharpDiagnostic>()
                let mutable hasCriticalErrors = false
                for index in 0 .. projects.Length - 1 do
                    let! checkedProject =
                        measure timing SemanticPhase.ProjectParseAndCheck (fun () -> checker.ParseAndCheckProject(optionsByIndex[index], userOpName = operationName))
                    checkedProjects[index] <- checkedProject
                    projectDiagnostics.AddRange(checkedProject.Diagnostics)
                    hasCriticalErrors <- hasCriticalErrors || checkedProject.HasCriticalErrors
                let allProjectDiagnostics =
                    projectDiagnostics
                    |> Seq.distinctBy diagnosticKey
                    |> Seq.toArray
                let checkedProject = checkedProjects[rootProjectIndex]
                if hasCriticalErrors then
                    return
                        checkResult nullSymbol "fsharp_semantic_check_failed" allProjectDiagnostics
                else
                    let targetIndex =
                        lookupProject.SourceFiles
                        |> Array.tryFindIndex (fun fileName ->
                            pathComparer.Equals(fileName, targetFileName))
                    match targetIndex with
                    | None ->
                        return
                            checkResult nullSymbol "fsharp_semantic_target_not_in_project" allProjectDiagnostics
                    | Some targetIndex ->
                        let! _, answer =
                            measure timing SemanticPhase.FileParseAndCheck (fun () -> checker.ParseAndCheckFileInProject(
                                targetFileName,
                                0,
                                SourceText.ofString lookupProject.SourceTexts[targetIndex],
                                optionsByIndex[lookupProjectIndex],
                                userOpName = operationName
                            ))
                        match answer with
                        | FSharpCheckFileAnswer.Aborted ->
                            return
                                checkResult nullSymbol "fsharp_semantic_check_aborted" allProjectDiagnostics
                        | FSharpCheckFileAnswer.Succeeded checkedFile when
                            not checkedFile.HasFullTypeCheckInfo ->
                            let diagnostics = mergeDiagnostics allProjectDiagnostics checkedFile.Diagnostics
                            return
                                checkResult nullSymbol "fsharp_semantic_check_incomplete" diagnostics
                        | FSharpCheckFileAnswer.Succeeded checkedFile ->
                            let diagnostics = mergeDiagnostics allProjectDiagnostics checkedFile.Diagnostics
                            let sourceText = SourceText.ofString lookupProject.SourceTexts[targetIndex]
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
                                elif column <= 0 && lookupProject.SourceTexts[targetIndex].Length <= maxLineOnlySourceChars then
                                    checkedFile.GetAllUsesOfAllSymbolsInFile()
                                    |> Seq.filter (fun symbolUse -> containsPosition line column symbolUse.Range)
                                    |> Seq.sortBy rangeScore
                                    |> Seq.truncate 2
                                    |> Seq.toArray
                                else
                                    Array.empty
                            if candidates.Length = 0 then
                                let error =
                                    if column <= 0 && lookupProject.SourceTexts[targetIndex].Length > maxLineOnlySourceChars then
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
                                        declarationLocations symbol,
                                        symbolIdentity symbol
                                    )
                                let references =
                                    if includeReferences then
                                        checkedProject.GetUsesOfSymbol(
                                            symbol,
                                            cancellationToken = cancellationToken
                                        )
                                        |> Seq.filter (fun symbolUse -> not symbolUse.IsFromDefinition)
                                        |> Seq.distinctBy (fun symbolUse ->
                                            let range = symbolUse.Range
                                            pathIdentityKey range.FileName,
                                            range.StartLine,
                                            range.StartColumn,
                                            range.EndLine,
                                            range.EndColumn)
                                        |> Seq.sortBy (fun symbolUse ->
                                            let range = symbolUse.Range
                                            pathIdentityKey range.FileName,
                                            range.StartLine,
                                            range.StartColumn,
                                            range.EndLine,
                                            range.EndColumn)
                                        |> Seq.map (fun symbolUse -> location "reference" symbolUse.Range)
                                        |> Seq.toArray
                                    else
                                        Array.empty
                                let all = diagnostics |> Seq.toArray
                                let errorCount = all |> Array.sumBy (fun diagnostic ->
                                    if diagnosticSeverity diagnostic = "error" then 1 else 0)
                                return
                                    SemanticCheckResult(
                                        mapped,
                                        nullString,
                                        all.Length,
                                        errorCount,
                                        boundedDiagnostics all,
                                        references
                                    )
        }

[<AbstractClass; Sealed>]
type SemanticResolver private () =
    static member ResolveAsync(
        projects: SemanticProjectInput array,
        rootProjectIndex: int,
        fingerprint: string,
        cacheRuntime: bool,
        targetFileName: string,
        line: int,
        column: int,
        maxLineOnlySourceChars: int,
        timing: ISemanticTiming,
        cancellationToken: CancellationToken
    ) : Task<SemanticCheckResult> =
        Semantic.resolve timing projects rootProjectIndex rootProjectIndex fingerprint cacheRuntime
            targetFileName line column maxLineOnlySourceChars false
        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

    static member ResolveReferencesAsync(
        projects: SemanticProjectInput array,
        rootProjectIndex: int,
        fingerprint: string,
        cacheRuntime: bool,
        targetFileName: string,
        line: int,
        column: int,
        maxLineOnlySourceChars: int,
        timing: ISemanticTiming,
        cancellationToken: CancellationToken
    ) : Task<SemanticCheckResult> =
        Semantic.resolve timing projects rootProjectIndex rootProjectIndex fingerprint cacheRuntime
            targetFileName line column maxLineOnlySourceChars true
        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

    static member ResolveReferencesForProjectAsync(
        projects: SemanticProjectInput array,
        rootProjectIndex: int,
        lookupProjectIndex: int,
        fingerprint: string,
        cacheRuntime: bool,
        targetFileName: string,
        line: int,
        column: int,
        maxLineOnlySourceChars: int,
        timing: ISemanticTiming,
        cancellationToken: CancellationToken
    ) : Task<SemanticCheckResult> =
        Semantic.resolve timing projects rootProjectIndex lookupProjectIndex fingerprint cacheRuntime
            targetFileName line column maxLineOnlySourceChars true
        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

    static member ResolveImplementationsAsync(
        projects: SemanticProjectInput array,
        rootProjectIndex: int,
        lookupProjectIndex: int,
        fingerprint: string,
        cacheRuntime: bool,
        targetFileName: string,
        line: int,
        column: int,
        implementationTraversalBoundary: Action<string>,
        timing: ISemanticTiming,
        cancellationToken: CancellationToken
    ) : Task<SemanticImplementationsCheckResult> =
        Semantic.resolveImplementations timing projects rootProjectIndex lookupProjectIndex
            fingerprint cacheRuntime targetFileName line column implementationTraversalBoundary
        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

    static member ResolveCallersAsync(
        projects: SemanticProjectInput array,
        rootProjectIndex: int,
        lookupProjectIndex: int,
        fingerprint: string,
        cacheRuntime: bool,
        targetFileName: string,
        line: int,
        column: int,
        traversalBoundary: Action<string>,
        timing: ISemanticTiming,
        cancellationToken: CancellationToken
    ) : Task<SemanticCallGraphCheckResult> =
        Semantic.resolveCallers timing projects rootProjectIndex lookupProjectIndex
            fingerprint cacheRuntime targetFileName line column traversalBoundary
        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)

    static member ResolveCalleesAsync(
        projects: SemanticProjectInput array,
        rootProjectIndex: int,
        lookupProjectIndex: int,
        fingerprint: string,
        cacheRuntime: bool,
        targetFileName: string,
        line: int,
        column: int,
        traversalBoundary: Action<string>,
        timing: ISemanticTiming,
        cancellationToken: CancellationToken
    ) : Task<SemanticCallGraphCheckResult> =
        Semantic.resolveCallees timing projects rootProjectIndex lookupProjectIndex
            fingerprint cacheRuntime targetFileName line column traversalBoundary
        |> fun work -> Async.StartAsTask(work, cancellationToken = cancellationToken)
