using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Core.Indexing;

public readonly record struct BaseTypeIdentity(string Name, int Arity);

public sealed record SymbolRow(
    int OrdinalInFile,
    int ParentOrdinal,          // -1 for top-level
    string Kind,                // namespace|class|interface|struct|record|record_struct|enum|delegate|method|constructor|property|field|event|enum_member|indexer|operator
    string Name,
    string? Namespace,
    string? Container,          // outer type chain within the namespace, '.' joined
    string Signature,
    string Accessibility,
    int StartLine,              // 1-based inclusive
    int EndLine,                // 1-based inclusive
    bool IsPartial,
    int Arity,
    string? AttrMarkers,        // ';' joined simple attribute names (for test detection etc.)
    string? Modifiers = null,   // space-joined inheritance/lifetime modifiers (static sealed abstract
                                // virtual override new), null when none — bt7: in deep hierarchies a
                                // caller needs "override" vs "virtual" to pick the right site
    string? Accessors = null,   // "get=public;set=private" — ONLY when an accessor's accessibility
                                // differs from the member's own (hu7, field twice-asked: the private
                                // on a setter was invisible); null when uniform or accessor-less
    string? DeclarationKey = null,
    IReadOnlyList<BaseTypeIdentity>? BaseTypes = null); // v18: direct syntax heads before signature truncation

public sealed record ParsedCsFile(
    string RelPath,
    string Content,
    int LineCount,
    bool LooksGenerated,
    bool HasTestAttributes,
    List<SymbolRow> Symbols);

/// <summary>
/// Owns: turning one C# file into symbol/outline rows using Roslyn syntax-only parsing
/// (no project system, no compilation — "indexed" confidence facts).
/// Does not own: file IO policy, storage, or semantic binding (M3).
/// </summary>
public static class SyntaxIndexer
{
    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Latest, DocumentationMode.Parse);

    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource",
        "TestMethod", "TestFixture", "TestClass", "SetUp", "TearDown",
    };

    public static ParsedCsFile Parse(string relPath, string content)
    {
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(content), ParseOptions);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var text = tree.GetText();

        var symbols = new List<SymbolRow>();
        bool hasTestAttrs = false;

        // memberDefault = the default accessibility for MEMBERS of the current container:
        // "public" inside an interface, "private" inside a class/struct/record (unused at the top
        // level / in a namespace, where only types live and each computes its own default).
        void Walk(SyntaxNode node, int parentOrdinal, string? ns, string? container, string memberDefault)
        {
            foreach (var member in Members(node))
            {
                switch (member)
                {
                    case BaseNamespaceDeclarationSyntax nsDecl:
                        {
                            string name = nsDecl.Name.ToString();
                            string full = ns is null ? name : $"{ns}.{name}";
                            int ord = Add(symbols, text, member, parentOrdinal, "namespace", full, ns, null,
                                $"namespace {full}", "public", isPartial: false, arity: 0, attrs: null);
                            Walk(nsDecl, ord, full, null, memberDefault);
                            break;
                        }
                    case BaseTypeDeclarationSyntax typeDecl:
                        {
                            string kind = typeDecl switch
                            {
                                ClassDeclarationSyntax => "class",
                                InterfaceDeclarationSyntax => "interface",
                                StructDeclarationSyntax => "struct",
                                RecordDeclarationSyntax r =>
                                    r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record_struct" : "record",
                                EnumDeclarationSyntax => "enum",
                                _ => "type",
                            };
                            int arity = (typeDecl as TypeDeclarationSyntax)?.TypeParameterList?.Parameters.Count ?? 0;
                            bool partial = typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword);
                            string name = typeDecl.Identifier.ValueText;
                            string? attrs = AttrMarkers(typeDecl.AttributeLists, ref hasTestAttrs);
                            string baseList = typeDecl.BaseList is { } bl ? $" : {Compact(bl.Types.ToString())}" : "";
                            string sig = $"{kind} {name}{TypeParams(typeDecl)}{baseList}";
                            IReadOnlyList<BaseTypeIdentity>? baseTypes =
                                BaseTypeIdentities(typeDecl.BaseList);

                            int ord = Add(symbols, text, member, parentOrdinal, kind, name, ns, container,
                                // A nested type's default follows its container: public inside an interface
                                // (memberDefault), private inside a class/struct/record; internal at top level.
                                sig, Access(typeDecl.Modifiers, defaultAcc: container is null ? "internal" : memberDefault),
                                partial, arity, attrs, baseTypes);

                            string childContainer = container is null ? name : $"{container}.{name}";
                            // Interface members are implicitly public; other type members default to private.
                            string childMemberDefault = kind == "interface" ? "public" : "private";
                            Walk(typeDecl, ord, ns, childContainer, childMemberDefault);
                            break;
                        }
                    case DelegateDeclarationSyntax del:
                        Add(symbols, text, member, parentOrdinal, "delegate", del.Identifier.ValueText, ns, container,
                            $"delegate {Compact(del.ReturnType.ToString())} {del.Identifier.ValueText}{ParamSig(del.ParameterList)}",
                            Access(del.Modifiers, container is null ? "internal" : memberDefault), false,
                            del.TypeParameterList?.Parameters.Count ?? 0, null);
                        break;
                    case EnumMemberDeclarationSyntax enumMember:
                        Add(symbols, text, member, parentOrdinal, "enum_member", enumMember.Identifier.ValueText, ns, container,
                            enumMember.Identifier.ValueText, "public", false, 0, null);
                        break;
                    case MethodDeclarationSyntax method:
                        {
                            string? attrs = AttrMarkers(method.AttributeLists, ref hasTestAttrs);
                            Add(symbols, text, member, parentOrdinal, "method", method.Identifier.ValueText, ns, container,
                                $"{Compact(method.ReturnType.ToString())} {ExplicitInterface(method.ExplicitInterfaceSpecifier)}{method.Identifier.ValueText}{TypeParams(method)}{ParamSig(method.ParameterList)}",
                                Access(method.Modifiers, memberDefault),
                                method.Modifiers.Any(SyntaxKind.PartialKeyword),
                                method.TypeParameterList?.Parameters.Count ?? 0, attrs);
                            break;
                        }
                    case ConstructorDeclarationSyntax ctor:
                        Add(symbols, text, member, parentOrdinal, "constructor", ctor.Identifier.ValueText, ns, container,
                            $"{ctor.Identifier.ValueText}{ParamSig(ctor.ParameterList)}",
                            Access(ctor.Modifiers, memberDefault), false, 0, null);
                        break;
                    case PropertyDeclarationSyntax prop:
                        Add(symbols, text, member, parentOrdinal, "property", prop.Identifier.ValueText, ns, container,
                            $"{Compact(prop.Type.ToString())} {ExplicitInterface(prop.ExplicitInterfaceSpecifier)}{prop.Identifier.ValueText}",
                            Access(prop.Modifiers, memberDefault), false, 0, null);
                        break;
                    case IndexerDeclarationSyntax indexer:
                        Add(symbols, text, member, parentOrdinal, "indexer", "this[]", ns, container,
                            $"{Compact(indexer.Type.ToString())} {ExplicitInterface(indexer.ExplicitInterfaceSpecifier)}this{ParamSig(indexer.ParameterList)}",
                            Access(indexer.Modifiers, memberDefault), false, 0, null);
                        break;
                    case EventDeclarationSyntax evt:
                        Add(symbols, text, member, parentOrdinal, "event", evt.Identifier.ValueText, ns, container,
                            $"event {Compact(evt.Type.ToString())} {ExplicitInterface(evt.ExplicitInterfaceSpecifier)}{evt.Identifier.ValueText}",
                            Access(evt.Modifiers, memberDefault), false, 0, null);
                        break;
                    case EventFieldDeclarationSyntax evtField:
                        foreach (var v in evtField.Declaration.Variables)
                        {
                            Add(symbols, text, member, parentOrdinal, "event", v.Identifier.ValueText, ns, container,
                                $"event {Compact(evtField.Declaration.Type.ToString())} {v.Identifier.ValueText}",
                                Access(evtField.Modifiers, memberDefault), false, 0, null);
                        }
                        break;
                    case FieldDeclarationSyntax field:
                        foreach (var v in field.Declaration.Variables)
                        {
                            Add(symbols, text, member, parentOrdinal, "field", v.Identifier.ValueText, ns, container,
                                $"{Compact(field.Declaration.Type.ToString())} {v.Identifier.ValueText}",
                                Access(field.Modifiers, memberDefault), false, 0, null);
                        }
                        break;
                    case OperatorDeclarationSyntax op:
                        string operatorName = OperatorName(op);
                        Add(symbols, text, member, parentOrdinal, "operator", operatorName, ns, container,
                            $"{Compact(op.ReturnType.ToString())} {ExplicitInterface(op.ExplicitInterfaceSpecifier)}{operatorName}{ParamSig(op.ParameterList)}",
                            Access(op.Modifiers, "public"), false, 0, null);
                        break;
                }
            }
        }

        Walk(root, -1, null, null, "private");

        int lineCount = text.Lines.Count;
        bool looksGenerated = FileClassifier.LooksGenerated(relPath, content);

        return new ParsedCsFile(relPath, content, lineCount, looksGenerated, hasTestAttrs, symbols);
    }

    /// <summary>Exact 1-based line ranges occupied by namespace names (not their bodies).
    /// Namespace SymbolRows intentionally span the whole declaration for ownership queries; review
    /// classification needs the narrower name region so a using/directive inside the body is not
    /// mislabeled as a namespace edit.</summary>
    public static List<(int Start, int End)> NamespaceNameLineRanges(string content)
    {
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(content), ParseOptions);
        var text = tree.GetText();
        return tree.GetRoot().DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Where(declaration => NamespaceNameOccupiesDeclarationOnlyLines(declaration, text))
            .Select(declaration => text.Lines.GetLinePositionSpan(declaration.Name.Span))
            .Select(span => (span.Start.Line + 1, span.End.Line + 1))
            .ToList();
    }

    private static bool NamespaceNameOccupiesDeclarationOnlyLines(
        BaseNamespaceDeclarationSyntax declaration, SourceText text)
    {
        if (declaration.Name.DescendantTrivia(descendIntoTrivia: true).Any(trivia =>
                !trivia.IsKind(SyntaxKind.WhitespaceTrivia) &&
                !trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            // A line hunk touching `A /* marker */ . B` may have changed the comment rather
            // than the namespace identifier. Treat any non-layout trivia inside the name as
            // file-level evidence instead of claiming a namespace-name-only edit.
            return false;
        }
        var position = text.Lines.GetLinePositionSpan(declaration.Name.Span);
        TextLine firstLine = text.Lines[position.Start.Line];
        TextLine lastLine = text.Lines[position.End.Line];
        int nameStart = declaration.Name.SpanStart - firstLine.Start;
        int nameEnd = declaration.Name.Span.End - lastLine.Start;
        string prefix = firstLine.ToString()[..nameStart].Trim();
        string suffix = lastLine.ToString()[nameEnd..].Trim();
        // Mixed-content lines are conservatively file-level: Git's line hunk does not reveal
        // which column changed, so `namespace N { /* edited */ }` cannot honestly be called a
        // namespace-name edit. Apply the same endpoint checks to multiline qualified names.
        return prefix == "namespace" && suffix is "" or ";" or "{";
    }

    /// <summary>Exact absolute character offsets of indexed declaration identifiers matching
    /// <paramref name="name"/>. Reference-candidate review excludes these tokens, not their whole
    /// symbol bodies, so a stale call inside a replacement method remains visible.</summary>
    public static IReadOnlyDictionary<string, List<(int Start, int End)>>
        DeclarationIdentifierOffsetMap(string content)
    {
        var result = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);
        VisitDeclarationIdentifiers(content, (declarationName, start, end) =>
        {
            if (!result.TryGetValue(declarationName, out List<(int Start, int End)>? offsets))
            {
                offsets = [];
                result[declarationName] = offsets;
            }
            offsets.Add((start, end));
        });
        return result;
    }

    public static List<(int Start, int End)> DeclarationIdentifierOffsets(string content,
        string name)
    {
        var result = new List<(int Start, int End)>();
        VisitDeclarationIdentifiers(content, (declarationName, start, end) =>
        {
            if (string.Equals(declarationName, name, StringComparison.Ordinal))
                result.Add((start, end));
        });
        return result;
    }

    private static void VisitDeclarationIdentifiers(string content,
        Action<string, int, int> visit)
    {
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(content), ParseOptions);
        void VisitToken(SyntaxToken token)
        {
            if (!token.IsKind(SyntaxKind.None) && token.ValueText.Length > 0)
                visit(token.ValueText, token.SpanStart, token.Span.End);
        }
        foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
        {
            if (node is BaseNamespaceDeclarationSyntax namespaceDeclaration)
            {
                foreach (SyntaxToken token in namespaceDeclaration.Name.DescendantTokens()
                             .Where(token => token.IsKind(SyntaxKind.IdentifierToken)))
                {
                    VisitToken(token);
                }
                continue;
            }
            if (node is OperatorDeclarationSyntax operatorDeclaration)
            {
                visit(OperatorName(operatorDeclaration),
                    operatorDeclaration.OperatorKeyword.SpanStart,
                    operatorDeclaration.OperatorToken.Span.End);
                continue;
            }
            SyntaxToken? identifier = node switch
            {
                BaseTypeDeclarationSyntax declaration => declaration.Identifier,
                DelegateDeclarationSyntax declaration => declaration.Identifier,
                MethodDeclarationSyntax declaration => declaration.Identifier,
                ConstructorDeclarationSyntax declaration => declaration.Identifier,
                DestructorDeclarationSyntax declaration => declaration.Identifier,
                PropertyDeclarationSyntax declaration => declaration.Identifier,
                EventDeclarationSyntax declaration => declaration.Identifier,
                EnumMemberDeclarationSyntax declaration => declaration.Identifier,
                LocalFunctionStatementSyntax declaration => declaration.Identifier,
                ParameterSyntax declaration => declaration.Identifier,
                TypeParameterSyntax declaration => declaration.Identifier,
                VariableDeclaratorSyntax declaration => declaration.Identifier,
                ForEachStatementSyntax declaration => declaration.Identifier,
                CatchDeclarationSyntax declaration => declaration.Identifier,
                SingleVariableDesignationSyntax declaration => declaration.Identifier,
                LabeledStatementSyntax declaration => declaration.Identifier,
                FromClauseSyntax declaration => declaration.Identifier,
                LetClauseSyntax declaration => declaration.Identifier,
                JoinClauseSyntax declaration => declaration.Identifier,
                JoinIntoClauseSyntax declaration => declaration.Identifier,
                QueryContinuationSyntax declaration => declaration.Identifier,
                ExternAliasDirectiveSyntax declaration => declaration.Identifier,
                NameEqualsSyntax declaration when declaration.Parent is UsingDirectiveSyntax =>
                    declaration.Name.Identifier,
                _ => null,
            };
            if (identifier is { } identifierToken) VisitToken(identifierToken);
        }
    }

    private static IEnumerable<SyntaxNode> Members(SyntaxNode node) => node switch
    {
        CompilationUnitSyntax cu => cu.Members,
        BaseNamespaceDeclarationSyntax ns => ns.Members,
        EnumDeclarationSyntax e => e.Members,
        TypeDeclarationSyntax t => t.Members,
        _ => Enumerable.Empty<SyntaxNode>(),
    };

    /// <summary>Persists the right-most simple identity of every direct base-list entry. The
    /// closure intentionally over-includes same-named types across namespaces and lets Roslyn
    /// verify them later, so qualification is not identity here. Extraction happens from the
    /// original syntax before the display signature's 400-character cap; type arguments and
    /// where-clause constraints are deliberately not separate edges.</summary>
    private static IReadOnlyList<BaseTypeIdentity>? BaseTypeIdentities(BaseListSyntax? baseList)
    {
        if (baseList is null) return null;
        var result = new List<BaseTypeIdentity>(baseList.Types.Count);
        var seen = new HashSet<BaseTypeIdentity>(BaseTypeIdentityComparer.Instance);
        foreach (BaseTypeSyntax baseType in baseList.Types)
        {
            if (BaseTypeHead(baseType.Type) is not { } identity || !seen.Add(identity)) continue;
            result.Add(identity);
        }
        return result.Count == 0 ? null : result;
    }

    internal static BaseTypeIdentity? BaseTypeHead(TypeSyntax type)
    {
        SimpleNameSyntax? simpleName = type switch
        {
            QualifiedNameSyntax qualified => qualified.Right,
            AliasQualifiedNameSyntax aliased => aliased.Name,
            SimpleNameSyntax simple => simple,
            _ => type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().LastOrDefault(),
        };
        if (simpleName is null) return null;
        return new BaseTypeIdentity(
            simpleName.Identifier.ValueText,
            simpleName is GenericNameSyntax generic
                ? generic.TypeArgumentList.Arguments.Count
                : 0);
    }

    private sealed class BaseTypeIdentityComparer : IEqualityComparer<BaseTypeIdentity>
    {
        public static BaseTypeIdentityComparer Instance { get; } = new();

        public bool Equals(BaseTypeIdentity x, BaseTypeIdentity y) =>
            x.Arity == y.Arity &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(BaseTypeIdentity value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name), value.Arity);
    }

    private static int Add(
        List<SymbolRow> symbols, SourceText text, SyntaxNode node, int parentOrdinal,
        string kind, string name, string? ns, string? container, string signature,
        string accessibility, bool isPartial, int arity, string? attrs,
        IReadOnlyList<BaseTypeIdentity>? baseTypes = null)
    {
        var span = text.Lines.GetLinePositionSpan(node.Span);
        int ordinal = symbols.Count;
        // Extracted HERE from the declaration node (bt7) so every member/type kind gets them
        // uniformly — namespaces are MemberDeclarationSyntax too but carry no modifiers.
        string? mods = node is MemberDeclarationSyntax md ? Mods(md.Modifiers) : null;
        string? accessors = AccessorSplit(node, accessibility);
        symbols.Add(new SymbolRow(
            ordinal, parentOrdinal, kind, name, ns, container,
            signature.Length > 400 ? signature[..400] : signature,
            accessibility,
            span.Start.Line + 1, span.End.Line + 1,
            isPartial, arity, attrs, mods, accessors,
            DeclarationKey(node, kind, name, arity), baseTypes));
        return ordinal;
    }

    /// <summary>Per-accessor accessibility split, e.g. <c>get=public;set=private</c> — emitted
    /// ONLY when at least one accessor's accessibility differs from the member's own (hu7, field
    /// twice-asked: <c>{ get; private set; }</c> showed a bare "public" and the private setter
    /// was invisible). All accessors are listed when any differs (explicit beats implicit);
    /// null when uniform or accessor-less (expression-bodied members). Covers properties,
    /// indexers, and events with explicit add/remove.</summary>
    private static string? AccessorSplit(SyntaxNode node, string memberAccessibility)
    {
        if (node is not BasePropertyDeclarationSyntax bp || bp.AccessorList is null) return null;
        List<string>? parts = null;
        bool anyDiffers = false;
        foreach (var acc in bp.AccessorList.Accessors)
        {
            string a = Access(acc.Modifiers, memberAccessibility);
            if (!string.Equals(a, memberAccessibility, StringComparison.Ordinal)) anyDiffers = true;
            (parts ??= new()).Add($"{acc.Keyword.ValueText}={a}");
        }
        return anyDiffers ? string.Join(';', parts!) : null;
    }

    /// <summary>Space-joined inheritance/lifetime modifiers in canonical order, null when none.
    /// Deliberately excludes accessibility (its own column), partial (its own flag), and
    /// body-implementation details (async/unsafe/extern) that don't help pick an override site.</summary>
    private static string? Mods(SyntaxTokenList modifiers)
    {
        if (modifiers.Count == 0) return null;
        List<string>? found = null;
        void Take(SyntaxKind kind, string text)
        {
            if (modifiers.Any(kind)) (found ??= new()).Add(text);
        }
        Take(SyntaxKind.StaticKeyword, "static");
        Take(SyntaxKind.SealedKeyword, "sealed");
        Take(SyntaxKind.AbstractKeyword, "abstract");
        Take(SyntaxKind.VirtualKeyword, "virtual");
        Take(SyntaxKind.OverrideKeyword, "override");
        Take(SyntaxKind.NewKeyword, "new"); // member hiding — an override-site trap worth surfacing
        Take(SyntaxKind.ReadOnlyKeyword, "readonly");
        Take(SyntaxKind.ConstKeyword, "const");
        return found is null ? null : string.Join(' ', found);
    }

    private static string? AttrMarkers(SyntaxList<AttributeListSyntax> lists, ref bool hasTestAttrs)
    {
        if (lists.Count == 0) return null;
        List<string>? names = null;
        foreach (var list in lists)
        {
            foreach (var attr in list.Attributes)
            {
                string simple = attr.Name switch
                {
                    SimpleNameSyntax s => s.Identifier.ValueText,
                    QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                    _ => attr.Name.ToString(),
                };
                if (simple.EndsWith("Attribute", StringComparison.Ordinal)) simple = simple[..^"Attribute".Length];
                (names ??= new()).Add(simple);
                if (TestAttributeNames.Contains(simple)) hasTestAttrs = true;
            }
        }
        return names is null ? null : string.Join(';', names.Distinct());
    }

    private static string Access(SyntaxTokenList modifiers, string defaultAcc)
    {
        bool isPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        bool isPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);
        bool isProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        bool isInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        if (isPublic) return "public";
        if (isProtected && isInternal) return "protected internal";
        if (isPrivate && isProtected) return "private protected";
        if (isProtected) return "protected";
        if (isInternal) return "internal";
        if (isPrivate) return "private";
        return defaultAcc;
    }

    private static string TypeParams(SyntaxNode node)
    {
        var list = node switch
        {
            TypeDeclarationSyntax t => t.TypeParameterList,
            MethodDeclarationSyntax m => m.TypeParameterList,
            _ => null,
        };
        return list is null ? "" : $"<{string.Join(", ", list.Parameters.Select(p => p.Identifier.ValueText))}>";
    }

    private static string ExplicitInterface(ExplicitInterfaceSpecifierSyntax? specifier) =>
        specifier is null ? "" : Compact(specifier.Name.ToString()) + ".";

    private static string OperatorName(OperatorDeclarationSyntax declaration)
    {
        string checkedPart = declaration.CheckedKeyword.IsKind(SyntaxKind.CheckedKeyword)
            ? "checked "
            : "";
        return $"operator {checkedPart}{declaration.OperatorToken.ValueText}";
    }

    private static string ParamSig(BaseParameterListSyntax? list)
    {
        if (list is null || list.Parameters.Count == 0) return "()";
        var parts = list.Parameters.Select(p =>
        {
            // Keep parameter modifiers (ref/out/in/params/this/scoped) — they are part of the
            // signature and often the overload disambiguator.
            string mods = p.Modifiers.Count > 0 ? Compact(p.Modifiers.ToString()) + " " : "";
            return Compact($"{mods}{p.Type} {p.Identifier.ValueText}").Trim();
        });
        return $"({string.Join(", ", parts)})";
    }

    /// <summary>Stable declaration identity, separate from the display signature: return/base/member
    /// types and parameter names may change without deleting a declaration, while overload parameter
    /// types/ref-kinds and explicit-interface qualifiers must remain distinct.</summary>
    private static string DeclarationKey(SyntaxNode node, string kind, string name, int arity)
    {
        IReadOnlyDictionary<string, string> typeParameters = TypeParameterReplacements(node);
        ExplicitInterfaceSpecifierSyntax? explicitInterface = node switch
        {
            MethodDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            PropertyDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            IndexerDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            EventDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            OperatorDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            ConversionOperatorDeclarationSyntax value => value.ExplicitInterfaceSpecifier,
            _ => null,
        };
        BaseParameterListSyntax? parameters = node switch
        {
            BaseMethodDeclarationSyntax value => value.ParameterList,
            IndexerDeclarationSyntax value => value.ParameterList,
            DelegateDeclarationSyntax value => value.ParameterList,
            _ => null,
        };
        string parameterKey = parameters is null
            ? ""
            : string.Join(',', parameters.Parameters.Select(parameter =>
            {
                string modifiers = string.Join(' ', parameter.Modifiers
                    .Where(modifier => modifier.IsKind(SyntaxKind.RefKeyword) ||
                                       modifier.IsKind(SyntaxKind.OutKeyword) ||
                                       modifier.IsKind(SyntaxKind.InKeyword))
                    .Select(modifier => modifier.ValueText));
                string type = parameter.Type is null
                    ? ""
                    : CanonicalSyntax(parameter.Type, typeParameters);
                return modifiers.Length == 0 ? type : modifiers + " " + type;
            }));
        return string.Join('\u001e', kind,
            explicitInterface is null
                ? ""
                : CanonicalSyntax(explicitInterface.Name, typeParameters),
            name, arity.ToString(System.Globalization.CultureInfo.InvariantCulture), parameterKey);
    }

    private static IReadOnlyDictionary<string, string> TypeParameterReplacements(SyntaxNode node)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        int depth = 0;
        foreach (TypeDeclarationSyntax type in node.Ancestors()
                     .OfType<TypeDeclarationSyntax>().Reverse())
        {
            if (type.TypeParameterList is { } parameters)
            {
                for (int i = 0; i < parameters.Parameters.Count; i++)
                    replacements[parameters.Parameters[i].Identifier.ValueText] =
                        $"$type{depth}_{i}";
            }
            depth++;
        }
        SeparatedSyntaxList<TypeParameterSyntax>? ownParameters = node switch
        {
            MethodDeclarationSyntax method => method.TypeParameterList?.Parameters,
            DelegateDeclarationSyntax declaration => declaration.TypeParameterList?.Parameters,
            _ => null,
        };
        if (ownParameters is { } own)
        {
            for (int i = 0; i < own.Count; i++)
                replacements[own[i].Identifier.ValueText] = $"$method_{i}";
        }
        return replacements;
    }

    private static string CanonicalSyntax(SyntaxNode node,
        IReadOnlyDictionary<string, string> replacements) =>
        string.Join('\u001d', node.DescendantTokens()
            // Tuple element names are source-level labels, like parameter names: renaming
            // `(int left, string right)` to `(int x, string y)` does not replace the method.
            // Filter only the TupleElementSyntax.Identifier token; the element Type subtree,
            // punctuation, and nested tuple structure remain in the canonical key.
            .Where(token => token.Parent is not TupleElementSyntax tupleElement ||
                            tupleElement.Identifier != token)
            .Select(token =>
            token.IsKind(SyntaxKind.IdentifierToken) &&
            !IsQualifiedNameComponent(token) &&
            replacements.TryGetValue(token.ValueText, out string? replacement)
                ? replacement
                : token.ValueText));

    private static bool IsQualifiedNameComponent(SyntaxToken token) =>
        token.Parent is SimpleNameSyntax simpleName &&
        simpleName.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax;

    /// <summary>Collapses newlines/duplicate whitespace so signatures stay single-line.</summary>
    private static string Compact(string s)
    {
        if (!s.Contains('\n') && !s.Contains("  ")) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        bool lastWs = false;
        foreach (char c in s)
        {
            bool ws = char.IsWhiteSpace(c);
            if (ws && lastWs) continue;
            sb.Append(ws ? ' ' : c);
            lastWs = ws;
        }
        return sb.ToString();
    }
}
