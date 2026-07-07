using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Core.Indexing;

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
    string? AttrMarkers);       // ';' joined simple attribute names (for test detection etc.)

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

                        int ord = Add(symbols, text, member, parentOrdinal, kind, name, ns, container,
                            // A nested type's default follows its container: public inside an interface
                            // (memberDefault), private inside a class/struct/record; internal at top level.
                            sig, Access(typeDecl.Modifiers, defaultAcc: container is null ? "internal" : memberDefault),
                            partial, arity, attrs);

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
                            $"{Compact(method.ReturnType.ToString())} {method.Identifier.ValueText}{TypeParams(method)}{ParamSig(method.ParameterList)}",
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
                            $"{Compact(prop.Type.ToString())} {prop.Identifier.ValueText}",
                            Access(prop.Modifiers, memberDefault), false, 0, null);
                        break;
                    case IndexerDeclarationSyntax indexer:
                        Add(symbols, text, member, parentOrdinal, "indexer", "this[]", ns, container,
                            $"{Compact(indexer.Type.ToString())} this{ParamSig(indexer.ParameterList)}",
                            Access(indexer.Modifiers, memberDefault), false, 0, null);
                        break;
                    case EventDeclarationSyntax evt:
                        Add(symbols, text, member, parentOrdinal, "event", evt.Identifier.ValueText, ns, container,
                            $"event {Compact(evt.Type.ToString())} {evt.Identifier.ValueText}",
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
                        Add(symbols, text, member, parentOrdinal, "operator", $"operator {op.OperatorToken.ValueText}", ns, container,
                            $"{Compact(op.ReturnType.ToString())} operator {op.OperatorToken.ValueText}{ParamSig(op.ParameterList)}",
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

    private static IEnumerable<SyntaxNode> Members(SyntaxNode node) => node switch
    {
        CompilationUnitSyntax cu => cu.Members,
        BaseNamespaceDeclarationSyntax ns => ns.Members,
        EnumDeclarationSyntax e => e.Members,
        TypeDeclarationSyntax t => t.Members,
        _ => Enumerable.Empty<SyntaxNode>(),
    };

    private static int Add(
        List<SymbolRow> symbols, SourceText text, SyntaxNode node, int parentOrdinal,
        string kind, string name, string? ns, string? container, string signature,
        string accessibility, bool isPartial, int arity, string? attrs)
    {
        var span = text.Lines.GetLinePositionSpan(node.Span);
        int ordinal = symbols.Count;
        symbols.Add(new SymbolRow(
            ordinal, parentOrdinal, kind, name, ns, container,
            signature.Length > 400 ? signature[..400] : signature,
            accessibility,
            span.Start.Line + 1, span.End.Line + 1,
            isPartial, arity, attrs));
        return ordinal;
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
