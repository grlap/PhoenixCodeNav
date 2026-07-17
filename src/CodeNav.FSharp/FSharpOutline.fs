namespace CodeNav.FSharp

open System
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

[<Sealed>]
type OutlineItem(
    name: string,
    kind: string,
    signature: string,
    accessibility: string,
    startLine: int,
    endLine: int,
    modifiers: string,
    accessors: string,
    members: OutlineItem array
) =
    member _.Name = name
    member _.Kind = kind
    member _.Signature = signature
    member _.Accessibility = accessibility
    member _.StartLine = startLine
    member _.EndLine = endLine
    member _.Modifiers = modifiers
    member _.Accessors = accessors
    member _.Members = members

[<Sealed>]
type OutlineParseResult(symbols: OutlineItem array, error: string) =
    member _.Symbols = symbols
    member _.Error = error

module private Outline =
    let checker = lazy (FSharpChecker.Create(projectCacheSize = 4))
    let nullString: string = Unchecked.defaultof<string>

    let accessValue = function
        | SynAccess.Public _ -> "public"
        | SynAccess.Internal _ -> "internal"
        | SynAccess.Private _ -> "private"

    let accessNameWith fallback = function
        | Some access -> accessValue access
        | None -> fallback

    let accessName access = accessNameWith "public" access

    let valAccessFacts = function
        | SynValSigAccess.Single access -> accessName access, nullString
        | SynValSigAccess.GetSet(access, getterAccess, setterAccess) ->
            let memberAccess = accessName access
            let getter = accessNameWith memberAccess getterAccess
            let setter = accessNameWith memberAccess setterAccess
            let accessors =
                if getter = memberAccess && setter = memberAccess then nullString
                else $"get={getter};set={setter}"
            memberAccess, accessors

    let lines (range: range) =
        let startLine = max 1 range.StartLine
        startLine, max startLine range.EndLine

    let sourceLines (source: string) =
        source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')

    let sourceSlice (content: string array) (range: range) cutAtEquals firstLineOnly =
        let originalStart = range.StartLine - 1
        let rangeEnd = min (range.EndLine - 1) (content.Length - 1)
        let startIndex =
            if firstLineOnly && originalStart >= 0 then
                [ originalStart .. rangeEnd ]
                |> List.tryFind (fun index ->
                    let trimmed = content[index].TrimStart()
                    trimmed.Length > 0 &&
                    not (trimmed.StartsWith("[<", StringComparison.Ordinal)))
                |> Option.defaultValue originalStart
            else originalStart
        let endIndex = if firstLineOnly then startIndex else range.EndLine - 1
        if startIndex < 0 || startIndex >= content.Length || endIndex < startIndex then
            nullString
        else
            let boundedEnd = min endIndex (content.Length - 1)
            let text =
                if startIndex = boundedEnd then
                    let line = content[startIndex]
                    let startColumn = if firstLineOnly then 0 else min range.StartColumn line.Length
                    let requestedEnd = if firstLineOnly then line.Length else range.EndColumn
                    let endColumn = max startColumn (min requestedEnd line.Length)
                    line.Substring(startColumn, endColumn - startColumn)
                else
                    [
                        let first = content[startIndex]
                        let startColumn = min range.StartColumn first.Length
                        yield first[startColumn..]
                        for index in (startIndex + 1) .. (boundedEnd - 1) do
                            yield content[index]
                        let last = content[boundedEnd]
                        let endColumn = min range.EndColumn last.Length
                        yield if endColumn = 0 then "" else last[.. endColumn - 1]
                    ]
                    |> String.concat " "

            let normalized = Regex.Replace(text, "\\s+", " ").Trim()
            let withoutBody =
                if cutAtEquals then
                    let separator = normalized.IndexOf(" = ", StringComparison.Ordinal)
                    if separator >= 0 then normalized[.. separator - 1].TrimEnd()
                    elif normalized.EndsWith(" =", StringComparison.Ordinal) then
                        normalized[.. normalized.Length - 3].TrimEnd()
                    else normalized
                else normalized
            if withoutBody.Length = 0 then nullString
            elif withoutBody.Length <= 400 then withoutBody
            else withoutBody[..399]

    let modifiers (values: string list) =
        let value =
            values
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> List.distinct
            |> String.concat " "
        if value.Length = 0 then nullString else value

    let flagModifiers (flags: SynMemberFlags) =
        [
            if not flags.IsInstance then "static"
            if flags.IsDispatchSlot then "abstract"
            if flags.IsOverrideOrExplicitImpl then "override"
            if flags.IsFinal then "sealed"
        ]

    let item content name kind signatureRange cutAtEquals firstLineOnly accessibility modifierText accessorText members =
        let startLine, endLine = lines signatureRange
        OutlineItem(
            name,
            kind,
            sourceSlice content signatureRange cutAtEquals firstLineOnly,
            accessibility,
            startLine,
            endLine,
            modifierText,
            accessorText,
            members
        )

    let identName (SynIdent(ident, _)) = ident.idText

    let longName (idents: Ident list) =
        idents |> List.map (fun ident -> ident.idText) |> String.concat "."

    let argPatsHaveArguments = function
        | SynArgPats.Pats patterns -> not (List.isEmpty patterns)
        | SynArgPats.NamePatPairs(pairs, _, _) -> not (List.isEmpty pairs)

    let rec bindingHead = function
        | SynPat.Named(SynIdent(ident, _), _, accessibility, _) ->
            Some(ident.idText, accessibility, false)
        | SynPat.LongIdent(SynLongIdent(identifiers, _, _), _, _, arguments, accessibility, _) ->
            identifiers
            |> List.tryLast
            |> Option.map (fun ident -> ident.idText, accessibility, argPatsHaveArguments arguments)
        | SynPat.InstanceMember(_, memberIdentifier, _, accessibility, _) ->
            Some(memberIdentifier.idText, accessibility, false)
        | SynPat.Typed(pattern, _, _)
        | SynPat.Attrib(pattern, _, _)
        | SynPat.Paren(pattern, _) -> bindingHead pattern
        | SynPat.As(pattern, _, _) -> bindingHead pattern
        | _ -> None

    let memberKind (flags: SynMemberFlags) =
        match flags.MemberKind with
        | SynMemberKind.ClassConstructor
        | SynMemberKind.Constructor -> "constructor"
        | SynMemberKind.PropertyGet
        | SynMemberKind.PropertySet
        | SynMemberKind.PropertyGetSet -> "property"
        | SynMemberKind.Member -> "method"

    let bindingAccess defaultAccess (SynBinding(accessibility, _, _, _, _, _, _, headPattern, _, _, _, _, _)) =
        match bindingHead headPattern with
        | Some(_, Some headAccess, _) -> accessValue headAccess
        | _ -> accessNameWith defaultAccess accessibility

    let mapBinding content defaultAccess forcedKind extraModifiers
        (SynBinding(accessibility, _, isInline, isMutable, _, _, SynValData(memberFlags, _, _),
                    headPattern, _, _, range, _, _)) =
        match bindingHead headPattern with
        | None -> None
        | Some(name, headAccess, hasArguments) ->
            let kind =
                match forcedKind, memberFlags with
                | Some value, _ -> value
                | None, Some flags when flags.MemberKind.IsMember && not hasArguments -> "property"
                | None, Some flags -> memberKind flags
                | None, None -> if hasArguments then "function" else "value"
            let access =
                match headAccess with
                | Some value -> accessValue value
                | None -> accessNameWith defaultAccess accessibility
            let flagValues = memberFlags |> Option.map flagModifiers |> Option.defaultValue []
            let modifierText =
                modifiers [
                    yield! extraModifiers
                    yield! flagValues
                    if isInline then "inline"
                    if isMutable then "mutable"
                ]
            Some(item content name kind range true false access modifierText nullString Array.empty)

    let mapField content (SynField(_, isStatic, identifier, _, isMutable, _, accessibility, range, _)) =
        identifier
        |> Option.map (fun ident ->
            let modifierText = modifiers [ if isStatic then "static"; if isMutable then "mutable" ]
            item content ident.idText "field" range false false (accessName accessibility)
                modifierText nullString Array.empty)

    let mapUnionCase content (SynUnionCase(_, identifier, _, _, accessibility, range, _)) =
        item content (identName identifier) "unionCase" range false false
            (accessName accessibility) nullString nullString Array.empty

    let mapEnumCase content (SynEnumCase(_, identifier, _, _, range, _)) =
        item content (identName identifier) "enumMember" range false false
            "public" nullString nullString Array.empty

    let valHasArguments (SynValInfo(curriedArguments, _)) =
        curriedArguments |> List.exists (List.isEmpty >> not)

    let mapVal content defaultKind flags
        (SynValSig(_, identifier, _, _, arity, isInline, isMutable, _, accessibility, _, range, _)) =
        let access, accessors = valAccessFacts accessibility
        let kind =
            match flags with
            | Some memberFlags -> memberKind memberFlags
            | None -> if defaultKind = "value" && valHasArguments arity then "function" else defaultKind
        let modifierText =
            modifiers [
                match flags with
                | Some memberFlags -> yield! flagModifiers memberFlags
                | None -> ()
                if isInline then "inline"
                if isMutable then "mutable"
            ]
        item content (identName identifier) kind range false false access modifierText accessors Array.empty

    let implementationTypeKind = function
        | SynTypeDefnRepr.ObjectModel(SynTypeDefnKind.Class, _, _) -> "class"
        | SynTypeDefnRepr.ObjectModel(SynTypeDefnKind.Interface, _, _) -> "interface"
        | SynTypeDefnRepr.ObjectModel(SynTypeDefnKind.Struct, _, _) -> "struct"
        | SynTypeDefnRepr.ObjectModel(SynTypeDefnKind.Delegate _, _, _) -> "delegate"
        | SynTypeDefnRepr.ObjectModel _ -> "class"
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Union _, _) -> "union"
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Enum _, _) -> "enum"
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Record _, _) -> "record"
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.General(SynTypeDefnKind.Class, _, _, _, _, _, _, _), _) -> "class"
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.General(SynTypeDefnKind.Interface, _, _, _, _, _, _, _), _) -> "interface"
        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.General(SynTypeDefnKind.Struct, _, _, _, _, _, _, _), _) -> "struct"
        | SynTypeDefnRepr.Simple _ -> "type"
        | SynTypeDefnRepr.Exception _ -> "exception"

    let signatureTypeKind = function
        | SynTypeDefnSigRepr.ObjectModel(SynTypeDefnKind.Class, _, _) -> "class"
        | SynTypeDefnSigRepr.ObjectModel(SynTypeDefnKind.Interface, _, _) -> "interface"
        | SynTypeDefnSigRepr.ObjectModel(SynTypeDefnKind.Struct, _, _) -> "struct"
        | SynTypeDefnSigRepr.ObjectModel(SynTypeDefnKind.Delegate _, _, _) -> "delegate"
        | SynTypeDefnSigRepr.ObjectModel _ -> "class"
        | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.Union _, _) -> "union"
        | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.Enum _, _) -> "enum"
        | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.Record _, _) -> "record"
        | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.General(SynTypeDefnKind.Class, _, _, _, _, _, _, _), _) -> "class"
        | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.General(SynTypeDefnKind.Interface, _, _, _, _, _, _, _), _) -> "interface"
        | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.General(SynTypeDefnKind.Struct, _, _, _, _, _, _, _), _) -> "struct"
        | SynTypeDefnSigRepr.Simple _ -> "type"
        | SynTypeDefnSigRepr.Exception _ -> "exception"

    let distinctItems (items: OutlineItem list) =
        items
        |> List.distinctBy (fun entry -> entry.Name, entry.StartLine, entry.Kind, entry.Signature)
        |> List.sortBy (fun entry -> entry.StartLine, entry.Name)
        |> List.toArray

    let mapGetSet content getter setter range =
        let first = getter |> Option.orElse setter
        first
        |> Option.bind (fun binding ->
            mapBinding content "public" (Some "property") [] binding
            |> Option.map (fun mapped ->
                let memberAccess = mapped.Accessibility
                let getterAccess = getter |> Option.map (bindingAccess memberAccess) |> Option.defaultValue memberAccess
                let setterAccess = setter |> Option.map (bindingAccess memberAccess) |> Option.defaultValue memberAccess
                let accessorText =
                    if getterAccess = memberAccess && setterAccess = memberAccess then nullString
                    else $"get={getterAccess};set={setterAccess}"
                let startLine, endLine = lines range
                OutlineItem(
                    mapped.Name,
                    "property",
                    sourceSlice content range true false,
                    memberAccess,
                    startLine,
                    endLine,
                    mapped.Modifiers,
                    accessorText,
                    Array.empty)))

    let rec mapImplementationMember content = function
        | SynMemberDefn.Member(binding, _) -> mapBinding content "public" None [] binding
        | SynMemberDefn.GetSetMember(getter, setter, range, _) -> mapGetSet content getter setter range
        | SynMemberDefn.AbstractSlot(value, flags, _, _) -> Some(mapVal content "method" (Some flags) value)
        | SynMemberDefn.ValField(field, _) -> mapField content field
        | SynMemberDefn.NestedType(nestedType, _, _) -> Some(mapImplementationType content nestedType)
        | SynMemberDefn.AutoProperty(_, isStatic, identifier, _, _, flags, _, _, accessibility, _, range, _) ->
            let access, accessors = valAccessFacts accessibility
            let modifierText = modifiers [ if isStatic then "static"; yield! flagModifiers flags ]
            Some(item content identifier.idText "property" range true false access modifierText accessors Array.empty)
        | SynMemberDefn.Open _
        | SynMemberDefn.ImplicitCtor _
        | SynMemberDefn.ImplicitInherit _
        | SynMemberDefn.LetBindings _
        | SynMemberDefn.Interface _
        | SynMemberDefn.Inherit _ -> None

    and mapImplementationType content (SynTypeDefn(typeInfo, representation, members, _, range, _)) =
        let (SynComponentInfo(_, _, _, identifiers, _, _, accessibility, _)) = typeInfo
        let representationMembers =
            match representation with
            | SynTypeDefnRepr.ObjectModel(_, typeMembers, _) ->
                typeMembers |> List.choose (mapImplementationMember content)
            | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Union(_, cases, _), _) ->
                cases |> List.map (mapUnionCase content)
            | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Enum(cases, _), _) ->
                cases |> List.map (mapEnumCase content)
            | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Record(_, fields, _), _) ->
                fields |> List.choose (mapField content)
            | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.General(_, _, slots, fields, _, _, _, _), _) ->
                [
                    yield! slots |> List.map (fun (value, flags) -> mapVal content "method" (Some flags) value)
                    yield! fields |> List.choose (mapField content)
                ]
            | _ -> []
        let allMembers =
            [
                yield! representationMembers
                yield! members |> List.choose (mapImplementationMember content)
            ]
            |> distinctItems
        item content (longName identifiers) (implementationTypeKind representation) range true true
            (accessName accessibility) nullString nullString allMembers

    let exceptionNameAndAccess (SynExceptionDefnRepr(_, caseName, _, _, accessibility, _)) =
        let (SynUnionCase(_, identifier, _, _, caseAccess, _, _)) = caseName
        identName identifier,
        match caseAccess with
        | Some access -> accessValue access
        | None -> accessName accessibility

    let mapImplementationException content (SynExceptionDefn(representation, _, members, range)) =
        let name, accessibility = exceptionNameAndAccess representation
        let nested = members |> List.choose (mapImplementationMember content) |> distinctItems
        item content name "exception" range false true accessibility nullString nullString nested

    let rec mapImplementationDeclaration content = function
        | SynModuleDecl.ModuleAbbrev(identifier, _, range) ->
            [ item content identifier.idText "module" range true true "public" nullString nullString Array.empty ]
        | SynModuleDecl.NestedModule(moduleInfo, _, declarations, _, range, _) ->
            let (SynComponentInfo(_, _, _, identifiers, _, _, accessibility, _)) = moduleInfo
            let nested = declarations |> List.collect (mapImplementationDeclaration content) |> distinctItems
            [ item content (longName identifiers) "module" range true true
                  (accessName accessibility) nullString nullString nested ]
        | SynModuleDecl.Let(_, bindings, _, _) ->
            bindings |> List.choose (mapBinding content "public" None [])
        | SynModuleDecl.Types(types, _) -> types |> List.map (mapImplementationType content)
        | SynModuleDecl.Exception(exceptionDefinition, _) ->
            [ mapImplementationException content exceptionDefinition ]
        | SynModuleDecl.NamespaceFragment fragment -> mapImplementationContainer content fragment
        | SynModuleDecl.Expr _
        | SynModuleDecl.Open _
        | SynModuleDecl.Attributes _
        | SynModuleDecl.HashDirective _ -> []

    and mapImplementationContainer content
        (SynModuleOrNamespace(identifiers, _, kind, declarations, _, _, accessibility, range, _)) =
        let nested = declarations |> List.collect (mapImplementationDeclaration content) |> distinctItems
        match kind with
        | SynModuleOrNamespaceKind.NamedModule ->
            [ item content (longName identifiers) "module" range true true
                  (accessName accessibility) nullString nullString nested ]
        | SynModuleOrNamespaceKind.DeclaredNamespace ->
            [ item content (longName identifiers) "namespace" range false true
                  (accessName accessibility) nullString nullString nested ]
        | SynModuleOrNamespaceKind.AnonModule
        | SynModuleOrNamespaceKind.GlobalNamespace -> List.ofArray nested

    let mapImplementation content (input: ParsedImplFileInput) =
        input.Contents
        |> List.collect (mapImplementationContainer content)
        |> distinctItems

    let rec mapSignatureMember content = function
        | SynMemberSig.Member(value, flags, _, _) -> Some(mapVal content "method" (Some flags) value)
        | SynMemberSig.ValField(field, _) -> mapField content field
        | SynMemberSig.NestedType(nestedType, _) -> Some(mapSignatureType content nestedType)
        | SynMemberSig.Interface _
        | SynMemberSig.Inherit _ -> None

    and mapSignatureType content (SynTypeDefnSig(typeInfo, representation, members, range, _)) =
        let (SynComponentInfo(_, _, _, identifiers, _, _, accessibility, _)) = typeInfo
        let representationMembers =
            match representation with
            | SynTypeDefnSigRepr.ObjectModel(_, memberSignatures, _) ->
                memberSignatures |> List.choose (mapSignatureMember content)
            | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.Union(_, cases, _), _) ->
                cases |> List.map (mapUnionCase content)
            | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.Enum(cases, _), _) ->
                cases |> List.map (mapEnumCase content)
            | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.Record(_, fields, _), _) ->
                fields |> List.choose (mapField content)
            | SynTypeDefnSigRepr.Simple(SynTypeDefnSimpleRepr.General(_, _, slots, fields, _, _, _, _), _) ->
                [
                    yield! slots |> List.map (fun (value, flags) -> mapVal content "method" (Some flags) value)
                    yield! fields |> List.choose (mapField content)
                ]
            | _ -> []
        let allMembers =
            [
                yield! representationMembers
                yield! members |> List.choose (mapSignatureMember content)
            ]
            |> distinctItems
        item content (longName identifiers) (signatureTypeKind representation) range true true
            (accessName accessibility) nullString nullString allMembers

    let mapSignatureException content (SynExceptionSig(representation, _, members, range)) =
        let name, accessibility = exceptionNameAndAccess representation
        let nested = members |> List.choose (mapSignatureMember content) |> distinctItems
        item content name "exception" range false true accessibility nullString nullString nested

    let rec mapSignatureDeclaration content = function
        | SynModuleSigDecl.ModuleAbbrev(identifier, _, range) ->
            [ item content identifier.idText "module" range true true "public" nullString nullString Array.empty ]
        | SynModuleSigDecl.NestedModule(moduleInfo, _, declarations, range, _) ->
            let (SynComponentInfo(_, _, _, identifiers, _, _, accessibility, _)) = moduleInfo
            let nested = declarations |> List.collect (mapSignatureDeclaration content) |> distinctItems
            [ item content (longName identifiers) "module" range true true
                  (accessName accessibility) nullString nullString nested ]
        | SynModuleSigDecl.Val(value, _) -> [ mapVal content "value" None value ]
        | SynModuleSigDecl.Types(types, _) -> types |> List.map (mapSignatureType content)
        | SynModuleSigDecl.Exception(exceptionSignature, _) ->
            [ mapSignatureException content exceptionSignature ]
        | SynModuleSigDecl.NamespaceFragment fragment -> mapSignatureContainer content fragment
        | SynModuleSigDecl.Open _
        | SynModuleSigDecl.HashDirective _ -> []

    and mapSignatureContainer content
        (SynModuleOrNamespaceSig(identifiers, _, kind, declarations, _, _, accessibility, range, _)) =
        let nested = declarations |> List.collect (mapSignatureDeclaration content) |> distinctItems
        match kind with
        | SynModuleOrNamespaceKind.NamedModule ->
            [ item content (longName identifiers) "module" range true true
                  (accessName accessibility) nullString nullString nested ]
        | SynModuleOrNamespaceKind.DeclaredNamespace ->
            [ item content (longName identifiers) "namespace" range false true
                  (accessName accessibility) nullString nullString nested ]
        | SynModuleOrNamespaceKind.AnonModule
        | SynModuleOrNamespaceKind.GlobalNamespace -> List.ofArray nested

    let mapSignature content (input: ParsedSigFileInput) =
        input.Contents
        |> List.collect (mapSignatureContainer content)
        |> distinctItems

    let parse fileName source (commandLineArgs: string array) =
        async {
            let checker = checker.Value
            let parsingOptions, _ =
                checker.GetParsingOptionsFromCommandLineArgs(
                    [ fileName ], commandLineArgs |> Array.toList)
            let! parsed = checker.ParseFile(fileName, SourceText.ofString source, parsingOptions)

            if parsed.ParseHadErrors then
                return OutlineParseResult(Array.empty, "fsharp_parse_failed")
            else
                let content = sourceLines source
                match parsed.ParseTree with
                | ParsedInput.ImplFile implementation ->
                    return OutlineParseResult(mapImplementation content implementation, nullString)
                | ParsedInput.SigFile signature ->
                    return OutlineParseResult(mapSignature content signature, nullString)
        }

[<AbstractClass; Sealed>]
type OutlineParser private () =
    static member Parse(fileName: string, source: string, commandLineArgs: string array) =
        Outline.parse fileName source commandLineArgs |> Async.RunSynchronously
