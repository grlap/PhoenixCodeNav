using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeNav.Core.Indexing;

public sealed record BaseTypeParseContext(
    string LanguageVersion,
    IReadOnlyList<string> PreprocessorSymbols);

public sealed record BaseTypeFact(
    string DeclarationOccurrence,
    int Ordinal,
    string RawTypeText,
    string LookupName,
    int SyntacticArity,
    string? QualifierText,
    string ResolutionKind,
    string? ScopeEvidence);

/// <summary>Extracts non-truncated base-list facts from one exact source snapshot and parse context.
/// Facts are syntactic leads; Roslyn compilation remains the identity authority.</summary>
public static class BaseTypeIndexer
{
    public static IReadOnlyList<BaseTypeFact> Parse(string source, BaseTypeParseContext context,
        CancellationToken cancellationToken = default)
    {
        LanguageVersion language = LanguageVersionFacts.TryParse(context.LanguageVersion, out LanguageVersion parsed)
            ? parsed : LanguageVersion.Latest;
        var options = new CSharpParseOptions(language, preprocessorSymbols: context.PreprocessorSymbols);
        SyntaxNode root = CSharpSyntaxTree.ParseText(source, options, cancellationToken: cancellationToken)
            .GetRoot(cancellationToken);
        var result = new List<BaseTypeFact>();
        int declarationOrdinal = 0;
        foreach (BaseTypeDeclarationSyntax declaration in root.DescendantNodes()
                     .OfType<BaseTypeDeclarationSyntax>().OrderBy(d => d.SpanStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentDeclaration = declarationOrdinal++;
            if (declaration.BaseList is null) continue;
            Dictionary<string, AliasFact> aliases = AliasesInScope(root, declaration);
            int baseOrdinal = 0;
            foreach (BaseTypeSyntax baseType in declaration.BaseList.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TypeSyntax syntax = baseType.Type;
                NameFact direct = DescribeName(syntax);
                string resolution = direct.Unresolved ? "unresolved" : "direct";
                string lookup = direct.Name;
                int arity = direct.Arity;
                string? qualifier = direct.Qualifier;
                string? scopeEvidence = null;
                if (syntax is IdentifierNameSyntax identifier && aliases.TryGetValue(identifier.Identifier.ValueText, out AliasFact? alias))
                {
                    if (alias.Unresolved)
                    {
                        resolution = "unresolved";
                        scopeEvidence = alias.Evidence;
                    }
                    else
                    {
                        resolution = "syntaxAlias";
                        lookup = alias.Target.Name;
                        arity = alias.Target.Arity;
                        qualifier = alias.Target.Qualifier;
                        scopeEvidence = alias.Evidence;
                    }
                }
                string occurrence = $"{declaration.SpanStart}:{declaration.Kind()}:{currentDeclaration}";
                result.Add(new BaseTypeFact(occurrence, baseOrdinal++, syntax.ToString(), lookup, arity,
                    qualifier, resolution, scopeEvidence));
            }
        }
        return result;
    }

    private static Dictionary<string, AliasFact> AliasesInScope(SyntaxNode root,
        BaseTypeDeclarationSyntax declaration)
    {
        var aliases = new Dictionary<string, AliasFact>(StringComparer.Ordinal);
        IEnumerable<UsingDirectiveSyntax> candidates = root is CompilationUnitSyntax unit
            ? unit.Usings
            : [];
        foreach (BaseNamespaceDeclarationSyntax ns in declaration.Ancestors()
                     .OfType<BaseNamespaceDeclarationSyntax>().Reverse())
            candidates = candidates.Concat(ns.Usings);

        foreach (UsingDirectiveSyntax directive in candidates)
        {
            if (directive.Alias is null || directive.Name is null) continue;
            string alias = directive.Alias.Name.Identifier.ValueText;
            NameFact target = DescribeName(directive.Name);
            bool unresolved = directive.GlobalKeyword.RawKind != 0 || target.Unresolved;
            aliases[alias] = new AliasFact(target, unresolved,
                $"using:{directive.SpanStart}:{directive.Name}");
        }
        return aliases;
    }

    private static NameFact DescribeName(TypeSyntax syntax) => syntax switch
    {
        GenericNameSyntax generic => new NameFact(generic.Identifier.ValueText,
            generic.TypeArgumentList.Arguments.Count, null, false),
        IdentifierNameSyntax identifier => new NameFact(identifier.Identifier.ValueText, 0, null, false),
        QualifiedNameSyntax qualified => WithQualifier(DescribeName(qualified.Right), qualified.Left.ToString()),
        AliasQualifiedNameSyntax aliasQualified => WithQualifier(DescribeName(aliasQualified.Name),
            aliasQualified.Alias.Identifier.ValueText + "::",
            !aliasQualified.Alias.Identifier.ValueText.Equals("global", StringComparison.Ordinal)),
        NullableTypeSyntax nullable => DescribeName(nullable.ElementType),
        _ => new NameFact(TerminalToken(syntax), 0, null, true),
    };

    private static NameFact WithQualifier(NameFact fact, string qualifier, bool unresolved = false) =>
        fact with { Qualifier = qualifier, Unresolved = fact.Unresolved || unresolved };

    private static string TerminalToken(TypeSyntax syntax)
    {
        SyntaxToken token = syntax.GetLastToken();
        return token.ValueText.Length > 0 ? token.ValueText : syntax.ToString();
    }

    private sealed record AliasFact(NameFact Target, bool Unresolved, string Evidence);
    private sealed record NameFact(string Name, int Arity, string? Qualifier, bool Unresolved);
}
