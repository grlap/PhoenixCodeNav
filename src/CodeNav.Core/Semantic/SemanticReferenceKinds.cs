using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeNav.Core.Semantic;

/// <summary>
/// Owns: classifying a compiler-exact reference location by HOW the symbol is used — call,
/// construction, typeMention, attribute, nameof, xmldoc (cref), usingDirective, baseList, typeof —
/// so `references` can report kind buckets and filter. Field evidence: "500 references" is often
/// ~480 xmldoc/comment mentions and 20 real calls; without kinds a caller re-reads every sample.
/// Syntax-ancestry based; used on the semantic (exact) path only — the indexed fallback has no
/// syntax to classify and stays honestly unclassified.
/// Does not own: finding the references (SemanticService.ReferencesAsync) or response shaping.
/// </summary>
public static class SemanticReferenceKinds
{
    /// <summary>All kind labels this classifier can produce (for docs/tests).</summary>
    public static readonly string[] All =
    {
        "call", "construction", "typeMention", "attribute", "nameof", "xmldoc",
        "usingDirective", "baseList", "typeof", "other",
    };

    public static string Classify(SyntaxNode root, int position, bool symbolIsType)
    {
        var token = root.FindToken(position, findInsideTrivia: true); // cref names live inside doc trivia
        for (SyntaxNode? n = token.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case DocumentationCommentTriviaSyntax: return "xmldoc";
                case AttributeSyntax: return "attribute";
                case UsingDirectiveSyntax: return "usingDirective";
                case BaseListSyntax: return "baseList";
                case TypeOfExpressionSyntax: return "typeof";
                case InvocationExpressionSyntax nameofInv
                    when nameofInv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }:
                    return "nameof";
                case InvocationExpressionSyntax inv:
                    // The invoked name (left of the parens) is a call; a reference inside the ARGUMENT
                    // list is not — its own classification (typeof, construction, ...) was already
                    // checked on the way up, so fall through to the boundary check below.
                    if (inv.Expression.Span.Contains(token.Span)) return "call";
                    break;
                case ConstructorInitializerSyntax: return "call"; // : base(...) / : this(...) — real executions
                case ImplicitObjectCreationExpressionSyntax: return "construction"; // target-typed new() — the token IS 'new'
                case ObjectCreationExpressionSyntax oc:
                    if (oc.Type.Span.Contains(token.Span)) return "construction";
                    break;
            }
            // Stop at statement/member boundaries: outer contexts (an enclosing call, the containing
            // method) say nothing about how THIS token uses the symbol.
            if (n is StatementSyntax or MemberDeclarationSyntax) break;
        }
        return symbolIsType ? "typeMention" : "other"; // e.g. a method group passed as a delegate
    }
}
