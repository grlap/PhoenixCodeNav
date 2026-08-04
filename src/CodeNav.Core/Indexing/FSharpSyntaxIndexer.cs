using System.Text;
using CodeNav.Core.Discovery;
using CodeNav.FSharp;

namespace CodeNav.Core.Indexing;

public sealed record ParsedFSharpFile(
    string RelPath,
    string Content,
    int LineCount,
    bool LooksGenerated,
    List<SymbolRow> Symbols,
    int ParseContextCount,
    int FailedParseContextCount,
    int OptionProjectCount,
    int FailedOptionProjectCount,
    int PartialOptionProjectCount,
    IReadOnlyList<string> OptionPartialReasons)
{
    internal FSharpIndexCoverage Coverage => new(
        ParseContextCount,
        FailedParseContextCount,
        OptionProjectCount,
        FailedOptionProjectCount,
        PartialOptionProjectCount,
        OptionPartialReasons);
}

internal sealed record FSharpIndexCoverage(
    int ParseContextCount,
    int FailedParseContextCount,
    int OptionProjectCount,
    int FailedOptionProjectCount,
    int PartialOptionProjectCount,
    IReadOnlyList<string> OptionPartialReasons);

internal sealed record FSharpParsingContextSelection(
    IReadOnlyList<string[]> Contexts,
    int ProjectCount,
    int FailedProjects,
    int PartialProjects,
    IReadOnlyList<string> PartialReasons)
{
    internal static FSharpParsingContextSelection Unowned { get; } =
        new([Array.Empty<string>()], 0, 0, 0, []);
}

/// <summary>
/// Owns: turning one F# implementation/signature file into the declaration rows shared by
/// <c>search_symbol</c> and indexed outlines. FCS parsing remains isolated in CodeNav.FSharp;
/// this adapter only normalizes its declaration tree into the language-neutral index schema.
/// </summary>
public static class FSharpSyntaxIndexer
{
    // Keep the diagnostic seam inside the installing test's execution context. A process-wide
    // delegate can observe unrelated xUnit classes while they build F# indexes in parallel and can
    // make one test's callback fail another test's parse.
    private static readonly AsyncLocal<Action<string>?> BeforeParseForTestSlot = new();
    internal static Action<string>? BeforeParseForTest
    {
        get => BeforeParseForTestSlot.Value;
        set => BeforeParseForTestSlot.Value = value;
    }

    internal static ParsedFSharpFile Parse(
        string relPath,
        string content,
        FSharpParsingContextSelection? contextSelection = null)
    {
        BeforeParseForTest?.Invoke(relPath);
        int lineCount = content.Count(character => character == '\n') + 1;
        bool generated = FileClassifier.LooksGenerated(relPath, content);
        string extension = Path.GetExtension(relPath);
        if (!extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase))
        {
            return new(relPath, content, lineCount, generated, [], 0, 0,
                0, 0, 0, []);
        }

        contextSelection ??= FSharpParsingContextSelection.Unowned;
        IReadOnlyList<string[]> contexts = contextSelection.Contexts;
        var declarations = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        int failedParseContexts = 0;
        foreach (string[] context in contexts)
        {
            OutlineParseResult parsed = OutlineParser.Parse(relPath, content, context);
            if (parsed.Error is not null)
            {
                failedParseContexts++;
                continue;
            }
            AddDeclarations(parsed.Symbols, declarations);
        }

        Declaration[] ordered = declarations.Values
            .OrderBy(declaration => declaration.StartLine)
            .ThenBy(declaration => declaration.Depth)
            .ThenByDescending(declaration => declaration.EndLine)
            .ThenBy(declaration => declaration.Name, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Kind, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Signature, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Key, StringComparer.Ordinal)
            .ToArray();
        var ordinals = new Dictionary<string, int>(ordered.Length, StringComparer.Ordinal);
        for (int ordinal = 0; ordinal < ordered.Length; ordinal++)
            ordinals[ordered[ordinal].Key] = ordinal;

        var symbols = new List<SymbolRow>(ordered.Length);
        for (int ordinal = 0; ordinal < ordered.Length; ordinal++)
        {
            Declaration declaration = ordered[ordinal];
            int parentOrdinal = declaration.ParentKey is not null &&
                                ordinals.TryGetValue(declaration.ParentKey, out int parent)
                ? parent
                : -1;
            symbols.Add(new SymbolRow(
                ordinal,
                parentOrdinal,
                declaration.Kind,
                declaration.Name,
                declaration.Namespace,
                declaration.Container,
                declaration.Signature,
                declaration.Accessibility,
                declaration.StartLine,
                declaration.EndLine,
                IsPartial: false,
                declaration.Arity,
                AttrMarkers: null,
                declaration.Modifiers,
                declaration.Accessors,
                declaration.Key));
        }

        return new(relPath, content, lineCount, generated, symbols,
            contexts.Count, failedParseContexts,
            contextSelection.ProjectCount,
            contextSelection.FailedProjects,
            contextSelection.PartialProjects,
            contextSelection.PartialReasons);
    }

    internal static FSharpParsingContextSelection ParsingContexts(
        IEnumerable<(string ProjectPath, string TargetFrameworks, string ProjectXml)> owners)
    {
        FSharpParsingContextSelection[] selections = owners
            .GroupBy(owner => owner.ProjectPath, WorkspacePaths.FileSystemPathComparer)
            .Select(group => group.First())
            .OrderBy(owner => owner.ProjectPath, StringComparer.Ordinal)
            .Select(owner => ParsingContextsForProject(
                owner.ProjectPath, owner.TargetFrameworks, owner.ProjectXml))
            .ToArray();
        return CombineParsingContexts(selections, unowned: selections.Length == 0);
    }

    internal static FSharpParsingContextSelection ParsingContextsForProject(
        string projectPath, string targetFrameworks, string projectXml)
    {
        var contexts = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        bool partial = false;
        bool failed = false;

        FSharpParsingOptionsSnapshot initial =
            ProjectFileParser.ParseFSharpParsingOptionsSnapshot(
                projectPath, projectXml, targetFrameworks);
        if (initial.Error is not null)
        {
            reasons.Add(initial.Error);
            failed = true;
        }
        else
        {
            IReadOnlyList<string?> targetFrameworksToParse =
                initial.AvailableTargetFrameworks is { Count: > 1 } available
                    ? available.Select(value => (string?)value).ToArray()
                    : new string?[] { null };
            foreach (string? targetFramework in targetFrameworksToParse)
            {
                FSharpParsingOptionsSnapshot selected = targetFramework is null
                    ? initial
                    : ProjectFileParser.ParseFSharpParsingOptionsSnapshot(
                        projectPath, projectXml, targetFrameworks,
                        targetFramework);
                if (selected.Error is not null)
                {
                    reasons.Add(selected.Error);
                    partial = true;
                    continue;
                }
                AddReasons(selected.PartialReason, reasons);
                partial |= selected.PartialReason is not null;
                string[] args = selected.CommandLineArgs.ToArray();
                contexts.TryAdd(ContextKey(args), args);
            }
            failed = contexts.Count == 0;
        }

        return new FSharpParsingContextSelection(
            contexts.Values.OrderBy(ContextKey, StringComparer.Ordinal).ToList(),
            ProjectCount: 1,
            FailedProjects: failed ? 1 : 0,
            PartialProjects: !failed && partial ? 1 : 0,
            reasons.ToArray());
    }

    internal static FSharpParsingContextSelection CombineParsingContexts(
        IEnumerable<FSharpParsingContextSelection> selections,
        bool unowned = false)
    {
        FSharpParsingContextSelection[] materialized = selections.ToArray();
        if (materialized.Length == 0)
            return unowned ? FSharpParsingContextSelection.Unowned : new([], 0, 0, 0, []);

        var contexts = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        foreach (FSharpParsingContextSelection selection in materialized)
        {
            foreach (string[] context in selection.Contexts)
                contexts.TryAdd(ContextKey(context), context);
            foreach (string reason in selection.PartialReasons)
                reasons.Add(reason);
        }
        return new FSharpParsingContextSelection(
            contexts.Values.OrderBy(ContextKey, StringComparer.Ordinal).ToList(),
            materialized.Sum(selection => selection.ProjectCount),
            materialized.Sum(selection => selection.FailedProjects),
            materialized.Sum(selection => selection.PartialProjects),
            reasons.ToArray());
    }

    private static void AddReasons(string? joinedReasons, SortedSet<string> destination)
    {
        if (joinedReasons is null) return;
        foreach (string reason in joinedReasons.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            destination.Add(reason);
    }

    private static string ContextKey(string[] arguments) =>
        string.Join('\u001f', arguments);

    private static void AddDeclarations(
        IReadOnlyList<OutlineItem> roots,
        Dictionary<string, Declaration> declarations)
    {
        var pending = new Stack<PendingDeclaration>();
        for (int index = roots.Count - 1; index >= 0; index--)
            pending.Push(new(roots[index], null, null, null, 0));

        while (pending.Count > 0)
        {
            PendingDeclaration pendingDeclaration = pending.Pop();
            OutlineItem item = pendingDeclaration.Item;
            string kind = NormalizeKind(item.Kind);
            string signature = item.Signature ?? string.Empty;
            string key = DeclarationKey(pendingDeclaration.ParentKey, kind, item.Name,
                item.StartLine, signature);
            var declaration = new Declaration(
                key,
                pendingDeclaration.ParentKey,
                pendingDeclaration.Depth,
                kind,
                item.Name,
                pendingDeclaration.Namespace,
                pendingDeclaration.Container,
                signature,
                item.Accessibility ?? "public",
                item.StartLine,
                item.EndLine,
                GenericArity(item.Name, signature),
                EmptyToNull(item.Modifiers),
                EmptyToNull(item.Accessors));
            if (declarations.TryGetValue(key, out Declaration? existing))
            {
                declarations[key] = existing with
                {
                    EndLine = Math.Max(existing.EndLine, declaration.EndLine),
                };
            }
            else
            {
                declarations.Add(key, declaration);
            }

            string? childNamespace = pendingDeclaration.Namespace;
            string? childContainer = pendingDeclaration.Container;
            if (kind is "namespace" or "module")
            {
                childNamespace = Qualify(pendingDeclaration.Namespace, item.Name);
                childContainer = null;
            }
            else if (IsTypeLike(kind))
            {
                childContainer = Qualify(pendingDeclaration.Container, item.Name);
            }

            for (int index = item.Members.Length - 1; index >= 0; index--)
            {
                pending.Push(new(item.Members[index], key, childNamespace,
                    childContainer, pendingDeclaration.Depth + 1));
            }
        }
    }

    private static string NormalizeKind(string kind) => kind switch
    {
        "unionCase" => "union_case",
        "enumMember" => "enum_member",
        _ => kind,
    };

    private static bool IsTypeLike(string kind) => kind is
        "class" or "interface" or "struct" or "record" or "union" or "enum" or
        "delegate" or "type" or "exception";

    private static string? Qualify(string? prefix, string name)
    {
        if (string.IsNullOrEmpty(prefix)) return name;
        if (name.Equals(prefix, StringComparison.Ordinal) ||
            name.StartsWith(prefix + ".", StringComparison.Ordinal))
        {
            return name;
        }
        return prefix + "." + name;
    }

    private static string DeclarationKey(string? parentKey, string kind, string name,
        int startLine, string signature)
    {
        var key = new StringBuilder((parentKey?.Length ?? 0) + signature.Length + name.Length + 48);
        if (parentKey is not null) key.Append(parentKey).Append('/');
        key.Append(kind).Append(':').Append(name).Append('@').Append(startLine)
            .Append(':').Append(signature);
        return key.ToString();
    }

    internal static int GenericArity(string name, string signature)
    {
        string simpleName = name[(name.LastIndexOf('.') + 1)..];
        if (simpleName.Length == 0) return 0;
        int open = -1;
        for (int searchFrom = 0; searchFrom < signature.Length;)
        {
            int nameIndex = signature.IndexOf(simpleName, searchFrom,
                StringComparison.Ordinal);
            if (nameIndex < 0) break;
            int nameEnd = nameIndex + simpleName.Length;
            bool leftBoundary = nameIndex == 0 || !IsIdentifierCharacter(signature[nameIndex - 1]);
            bool rightBoundary = nameEnd == signature.Length ||
                                 !IsIdentifierCharacter(signature[nameEnd]);
            int candidateOpen = nameEnd;
            while (candidateOpen < signature.Length &&
                   char.IsWhiteSpace(signature[candidateOpen]))
            {
                candidateOpen++;
            }
            if (leftBoundary && rightBoundary && candidateOpen < signature.Length &&
                signature[candidateOpen] == '<')
            {
                open = candidateOpen;
                break;
            }
            searchFrom = nameEnd;
        }
        if (open < 0) return 0;

        int depth = 0;
        int arity = 1;
        for (int index = open + 1; index < signature.Length; index++)
        {
            switch (signature[index])
            {
                case '<': depth++; break;
                case '>' when index > 0 && signature[index - 1] == '-': break;
                case '>' when depth == 0: return arity;
                case '>': depth--; break;
                case ',' when depth == 0: arity++; break;
            }
        }
        return 0;
    }

    internal static long EstimatedRetainedSymbolBytes(IReadOnlyList<SymbolRow> symbols)
    {
        const long rowAndListSlotBytes = 160;
        long total = 0;
        foreach (SymbolRow symbol in symbols)
        {
            total = SaturatingAdd(total, rowAndListSlotBytes);
            total = SaturatingAdd(total, StringBytes(symbol.Kind));
            total = SaturatingAdd(total, StringBytes(symbol.Name));
            total = SaturatingAdd(total, StringBytes(symbol.Namespace));
            total = SaturatingAdd(total, StringBytes(symbol.Container));
            total = SaturatingAdd(total, StringBytes(symbol.Signature));
            total = SaturatingAdd(total, StringBytes(symbol.Accessibility));
            total = SaturatingAdd(total, StringBytes(symbol.AttrMarkers));
            total = SaturatingAdd(total, StringBytes(symbol.Modifiers));
            total = SaturatingAdd(total, StringBytes(symbol.Accessors));
            total = SaturatingAdd(total, StringBytes(symbol.DeclarationKey));
        }
        return total;

        static long StringBytes(string? value) => value is null
            ? 0
            : 24L + (long)value.Length * sizeof(char);
        static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '\'';

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private sealed record PendingDeclaration(
        OutlineItem Item,
        string? ParentKey,
        string? Namespace,
        string? Container,
        int Depth);

    private sealed record Declaration(
        string Key,
        string? ParentKey,
        int Depth,
        string Kind,
        string Name,
        string? Namespace,
        string? Container,
        string Signature,
        string Accessibility,
        int StartLine,
        int EndLine,
        int Arity,
        string? Modifiers,
        string? Accessors);
}
