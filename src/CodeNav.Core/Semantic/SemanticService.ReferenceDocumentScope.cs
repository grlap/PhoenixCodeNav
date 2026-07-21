using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.Core.Semantic;

public sealed partial class SemanticService
{
    private const string DocumentScopeCandidateSource = "leasedSolutionText";
    private const int ReferenceScopeFastStringLimitChars = 40_000;
    private const int ReferenceScopeCacheEntryLimit = 64;
    private static readonly int ReferenceScopeScanConcurrency =
        Math.Min(8, Math.Max(1, Environment.ProcessorCount));
    private static readonly SemaphoreSlim ReferenceScopeScanSlots =
        new(ReferenceScopeScanConcurrency, ReferenceScopeScanConcurrency);
    private readonly ConditionalWeakTable<Solution, ReferenceDocumentScopeCache>
        _referenceDocumentScopeCache = new();

    private sealed class ReferenceDocumentScopeCache
    {
        private readonly ConcurrentDictionary<string, ReferenceDocumentScope> _entries =
            new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _insertionOrder = new();

        public int Count => _entries.Count;

        public bool TryGet(string key, out ReferenceDocumentScope scope)
            => _entries.TryGetValue(key, out scope!);

        public void Add(string key, ReferenceDocumentScope scope)
        {
            if (!_entries.TryAdd(key, scope)) return;
            _insertionOrder.Enqueue(key);
            while (_entries.Count > ReferenceScopeCacheEntryLimit &&
                   _insertionOrder.TryDequeue(out string? oldest))
                _entries.TryRemove(oldest, out _);
        }
    }

    private static readonly HashSet<string> ImplicitReferencePatternNames = new(
        StringComparer.Ordinal)
    {
        "GetEnumerator",
        "GetAsyncEnumerator",
        "MoveNext",
        "MoveNextAsync",
        "Current",
        "Dispose",
        "DisposeAsync",
        "GetAwaiter",
        "GetResult",
        "IsCompleted",
        "OnCompleted",
        "UnsafeOnCompleted",
        "Add",
        "Deconstruct",
        "GetPinnableReference",
        "Slice",
        "Length",
        "Count",
        "Select",
        "SelectMany",
        "Where",
        "Join",
        "GroupJoin",
        "OrderBy",
        "OrderByDescending",
        "ThenBy",
        "ThenByDescending",
        "GroupBy",
        "Cast",
    };

    internal sealed record ReferenceDocumentScopeStats(
        string Mode,
        string Reason,
        string CandidateSource,
        double TotalMs,
        bool CacheHit,
        int? SolutionDocuments,
        int? CandidateDocuments,
        int? ScopedDocuments,
        int AliasWidenedProjects,
        int TransformedIncludedDocuments);

    internal sealed class ReferenceDocumentScopeStatsBox
    {
        public ReferenceDocumentScopeStats? Stats { get; internal set; }
    }

    internal sealed record ReferenceDocumentScope(
        IImmutableSet<Document>? Documents,
        ReferenceDocumentScopeStats Stats);

    private sealed record DocumentProbe(
        Document Document,
        bool Candidate,
        bool ContainsValueTextTransformation);

    /// <summary>
    /// Produces a conservative document superset from the exact immutable Solution later passed
    /// to SymbolFinder. This deliberately does not use committed FTS as the source of truth: a
    /// follower cannot observe the writer's pending queue, and the live source snapshot may move
    /// after an index snapshot is pinned. Texts are already resident after compilation preparation,
    /// so this pass is cheap relative to Roslyn's per-document reference binding while remaining
    /// exact for live edits, linked documents, pathless documents, and follower mode.
    /// </summary>
    internal Task<ReferenceDocumentScope> PlanReferenceDocumentScopeAsync(
        ISymbol symbol, Solution solution, CancellationToken cancellationToken)
        => PlanReferenceDocumentScopeAsync(symbol, solution,
            new ReferenceDocumentScopeStatsBox(), cancellationToken);

    internal async Task<ReferenceDocumentScope> PlanReferenceDocumentScopeAsync(
        ISymbol symbol, Solution solution, ReferenceDocumentScopeStatsBox statsBox,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statsBox);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int? observedSolutionDocuments = null;
        int? observedCandidateDocuments = null;
        int? observedScopedDocuments = null;
        int observedAliasWidenedProjects = 0;
        int observedTransformedIncludedDocuments = 0;

        ReferenceDocumentScope Full(string reason, int? solutionDocuments = null,
            int? candidateDocuments = null, int aliasWidenedProjects = 0,
            int transformedIncludedDocuments = 0)
        {
            var stats = new ReferenceDocumentScopeStats(
                Mode: "fullSolution",
                Reason: reason,
                CandidateSource: DocumentScopeCandidateSource,
                TotalMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                CacheHit: false,
                SolutionDocuments: solutionDocuments,
                CandidateDocuments: candidateDocuments,
                ScopedDocuments: solutionDocuments,
                AliasWidenedProjects: aliasWidenedProjects,
                TransformedIncludedDocuments: transformedIncludedDocuments);
            statsBox.Stats = stats;
            return new ReferenceDocumentScope(null, stats);
        }

        if (TestOnlyForceFullSolutionReferences)
            return Full("forced_full_solution");

        ISymbol? scopeSymbol = NormalizeReferenceScopeSymbol(symbol);
        if (scopeSymbol is null || !TryGetReferenceCandidateNames(scopeSymbol,
                out ImmutableArray<string> candidateNames))
            return Full("ineligible_kind");

        string cacheKey = string.Join("\u001f", candidateNames.Order(StringComparer.Ordinal));
        ReferenceDocumentScopeCache solutionCache =
            _referenceDocumentScopeCache.GetValue(solution, static _ => new());
        if (solutionCache.TryGet(cacheKey, out ReferenceDocumentScope cached))
        {
            ReferenceDocumentScopeStats cachedStats = cached.Stats with
            {
                TotalMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                CacheHit = true,
            };
            statsBox.Stats = cachedStats;
            return cached with { Stats = cachedStats };
        }

        if (solution.Projects.Any(project => project.Language != LanguageNames.CSharp))
            return Full("unsupported_language");

        try
        {
            var allDocuments = ImmutableArray.CreateBuilder<Document>();
            var projectDocuments = new Dictionary<ProjectId, ImmutableArray<Document>>();
            foreach (Project project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var documents = ImmutableArray.CreateBuilder<Document>();
                documents.AddRange(project.Documents);
                documents.AddRange(await project.GetSourceGeneratedDocumentsAsync(
                    cancellationToken).ConfigureAwait(false));
                ImmutableArray<Document> projectSet = documents.ToImmutable();
                projectDocuments[project.Id] = projectSet;
                allDocuments.AddRange(projectSet);
            }

            ImmutableArray<Document> solutionDocuments = allDocuments.ToImmutable();
            observedSolutionDocuments = solutionDocuments.Length;
            if (solutionDocuments.IsEmpty)
                return Full("no_documents", solutionDocuments: 0, candidateDocuments: 0);

            var probes = new DocumentProbe[solutionDocuments.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, solutionDocuments.Length),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = ReferenceScopeScanConcurrency,
                }, async (index, token) =>
            {
                await ReferenceScopeScanSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    Document document = solutionDocuments[index];
                    SourceText text = document.TryGetText(out SourceText? residentText)
                        ? residentText
                        : await document.GetTextAsync(token).ConfigureAwait(false);
                    // StringText returns its backing string without allocation and lets the runtime
                    // use its vectorized ordinal search. LargeText would flatten its chunks, so keep
                    // the allocation-free SourceText scan for large files.
                    string? fastText = text.Length <= ReferenceScopeFastStringLimitChars
                        ? text.ToString()
                        : null;
                    // Roslyn compares token ValueText, not only raw bytes. Retain documents with
                    // complete C# escapes (including SuppressMessage.Target), numeric XML entities,
                    // or Format scalars stripped by the lexer. Bare "\\U" is not enough: it would
                    // retain common C:\\Users path strings and destroy the reduction.
                    bool containsValueTextTransformation =
                        ContainsValueTextTransformation(text, fastText);
                    bool candidate = containsValueTextTransformation ||
                        candidateNames.Any(name => ContainsIdentifier(text, name, fastText));
                    probes[index] = new DocumentProbe(document, candidate,
                        containsValueTextTransformation);
                }
                finally
                {
                    ReferenceScopeScanSlots.Release();
                }
            }).ConfigureAwait(false);

            var candidateDocuments = probes.Where(probe => probe.Candidate)
                .Select(probe => probe.Document).ToImmutableHashSet();
            observedCandidateDocuments = candidateDocuments.Count;
            observedTransformedIncludedDocuments = probes.Count(probe =>
                probe.Candidate && probe.ContainsValueTextTransformation);
            if (candidateDocuments.Count == 0)
            {
                ReferenceDocumentScope full = Full("no_candidates", solutionDocuments.Length,
                    candidateDocuments: 0);
                solutionCache.Add(cacheKey, full);
                return full;
            }

            var scopedDocuments = candidateDocuments.ToBuilder();
            var widenedProjects = new HashSet<ProjectId>();
            foreach (Document document in candidateDocuments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (root is not CompilationUnitSyntax compilationUnit ||
                    !compilationUnit.Usings.Any(usingDirective =>
                        usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) &&
                        usingDirective.Alias is not null))
                    continue;

                if (widenedProjects.Add(document.Project.Id) &&
                    projectDocuments.TryGetValue(document.Project.Id, out var documents))
                    scopedDocuments.UnionWith(documents);
            }

            int scopedCount = scopedDocuments.Count;
            observedScopedDocuments = scopedCount;
            observedAliasWidenedProjects = widenedProjects.Count;
            if (scopedCount >= solutionDocuments.Length)
            {
                ReferenceDocumentScope full = Full("no_reduction", solutionDocuments.Length,
                    candidateDocuments.Count, widenedProjects.Count,
                    observedTransformedIncludedDocuments);
                solutionCache.Add(cacheKey, full);
                return full;
            }

            var scopedStats = new ReferenceDocumentScopeStats(
                    Mode: "documentScoped",
                    Reason: "eligible",
                    CandidateSource: DocumentScopeCandidateSource,
                    TotalMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    CacheHit: false,
                    SolutionDocuments: solutionDocuments.Length,
                    CandidateDocuments: candidateDocuments.Count,
                    ScopedDocuments: scopedCount,
                    AliasWidenedProjects: widenedProjects.Count,
                    TransformedIncludedDocuments: observedTransformedIncludedDocuments);
            statsBox.Stats = scopedStats;
            var scoped = new ReferenceDocumentScope(scopedDocuments.ToImmutable(), scopedStats);
            solutionCache.Add(cacheKey, scoped);
            return scoped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            statsBox.Stats = new ReferenceDocumentScopeStats(
                Mode: "notCompleted",
                Reason: "cancelled",
                CandidateSource: DocumentScopeCandidateSource,
                TotalMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                CacheHit: false,
                SolutionDocuments: observedSolutionDocuments,
                CandidateDocuments: observedCandidateDocuments,
                ScopedDocuments: observedScopedDocuments,
                AliasWidenedProjects: observedAliasWidenedProjects,
                TransformedIncludedDocuments: observedTransformedIncludedDocuments);
            throw;
        }
        catch (Exception ex)
        {
            // Narrowing is only a performance optimization. Any planning uncertainty restores
            // the existing full-solution authority without weakening confidence or coverage.
            _log($"Semantic reference document scoping fell back: {ex.Message}");
            return Full("planning_error", observedSolutionDocuments,
                observedCandidateDocuments, observedAliasWidenedProjects,
                observedTransformedIncludedDocuments);
        }
    }

    private static ISymbol? NormalizeReferenceScopeSymbol(ISymbol symbol)
    {
        if (symbol is IAliasSymbol alias)
        {
            if (alias.Target is not INamedTypeSymbol { IsTupleType: false } target)
                return null;
            symbol = target;
        }

        if (symbol is IMethodSymbol
            {
                MethodKind: MethodKind.ReducedExtension,
                ReducedFrom: { } reduced,
            })
            symbol = reduced;

        return symbol.OriginalDefinition;
    }

    private static bool TryGetReferenceCandidateNames(ISymbol symbol,
        out ImmutableArray<string> candidateNames)
    {
        bool eligible = symbol switch
        {
            INamedTypeSymbol type => type.CanBeReferencedByName &&
                (type.TypeKind == TypeKind.Interface ||
                 type is { TypeKind: TypeKind.Class, IsStatic: true }),
            IMethodSymbol method => method.CanBeReferencedByName &&
                method.MethodKind == MethodKind.Ordinary &&
                method.ExplicitInterfaceImplementations.IsEmpty &&
                method.AssociatedSymbol is null &&
                !ImplicitReferencePatternNames.Contains(method.Name) &&
                !IsCollectionBuilderFactory(method),
            IFieldSymbol field => field.CanBeReferencedByName && field.AssociatedSymbol is null,
            IEventSymbol @event => @event.CanBeReferencedByName &&
                @event.ExplicitInterfaceImplementations.IsEmpty,
            IPropertySymbol property => property.CanBeReferencedByName && !property.IsIndexer &&
                property.ExplicitInterfaceImplementations.IsEmpty &&
                !ImplicitReferencePatternNames.Contains(property.Name),
            _ => false,
        };

        if (!eligible)
        {
            candidateNames = [];
            return false;
        }

        var names = ImmutableArray.CreateBuilder<string>();
        if (!string.IsNullOrEmpty(symbol.Name)) names.Add(symbol.Name);
        if (symbol is INamedTypeSymbol && symbol.Name.EndsWith("Attribute",
                StringComparison.Ordinal) && symbol.Name.Length > "Attribute".Length)
            names.Add(symbol.Name[..^"Attribute".Length]);
        if (symbol is IPropertySymbol propertySymbol)
        {
            if (!string.IsNullOrEmpty(propertySymbol.GetMethod?.Name))
                names.Add(propertySymbol.GetMethod.Name);
            if (!string.IsNullOrEmpty(propertySymbol.SetMethod?.Name))
                names.Add(propertySymbol.SetMethod.Name);
        }
        candidateNames = names.Distinct(StringComparer.Ordinal).ToImmutableArray();
        return !candidateNames.IsEmpty;
    }

    internal int TestOnlyReferenceDocumentScopeCacheCount(Solution solution)
        => _referenceDocumentScopeCache.TryGetValue(solution, out var cache) ? cache.Count : 0;

    private static bool IsCollectionBuilderFactory(IMethodSymbol method)
    {
        if (method.ReturnType is not INamedTypeSymbol returnType) return false;
        foreach (AttributeData attribute in returnType.GetAttributes())
        {
            INamedTypeSymbol? attributeClass = attribute.AttributeClass;
            if (attributeClass?.Name != "CollectionBuilderAttribute" ||
                attributeClass.ContainingNamespace?.ToDisplayString() !=
                    "System.Runtime.CompilerServices" ||
                attribute.ConstructorArguments.Length < 2 ||
                attribute.ConstructorArguments[0].Value is not ITypeSymbol builderType ||
                attribute.ConstructorArguments[1].Value is not string methodName)
                continue;

            if (string.Equals(methodName, method.Name, StringComparison.Ordinal) &&
                SymbolEqualityComparer.Default.Equals(method.ContainingType.OriginalDefinition,
                    builderType.OriginalDefinition))
                return true;
        }
        return false;
    }

    private static bool ContainsIdentifier(SourceText text, string value,
        string? fastText = null)
    {
        if (value.Length == 0 || value.Length > text.Length) return false;
        if (fastText is not null)
        {
            int searchFrom = 0;
            while (searchFrom <= fastText.Length - value.Length)
            {
                int start = fastText.IndexOf(value, searchFrom, StringComparison.Ordinal);
                if (start < 0) return false;
                if (HasIdentifierBoundaries(fastText, start, value.Length)) return true;
                searchFrom = start + 1;
            }
            return false;
        }

        int lastStart = text.Length - value.Length;
        for (int start = 0; start <= lastStart; start++)
        {
            int offset = 0;
            while (offset < value.Length && text[start + offset] == value[offset]) offset++;
            if (offset == value.Length && HasIdentifierBoundaries(text, start, value.Length))
                return true;
        }
        return false;
    }

    private static bool HasIdentifierBoundaries(string text, int start, int length)
        => (start == 0 || !SyntaxFacts.IsIdentifierPartCharacter(text[start - 1])) &&
           (start + length == text.Length ||
            !SyntaxFacts.IsIdentifierPartCharacter(text[start + length]));

    private static bool HasIdentifierBoundaries(SourceText text, int start, int length)
        => (start == 0 || !SyntaxFacts.IsIdentifierPartCharacter(text[start - 1])) &&
           (start + length == text.Length ||
            !SyntaxFacts.IsIdentifierPartCharacter(text[start + length]));

    private static bool ContainsValueTextTransformation(SourceText text, string? fastText)
    {
        for (int start = 0; start < text.Length; start++)
        {
            char current = fastText is null ? text[start] : fastText[start];
            if (char.GetUnicodeCategory(current) == UnicodeCategory.Format)
                return true;
            if (char.IsHighSurrogate(current) && start + 1 < text.Length)
            {
                char low = fastText is null ? text[start + 1] : fastText[start + 1];
                if (Rune.TryCreate(current, low, out Rune scalar) &&
                    Rune.GetUnicodeCategory(scalar) == UnicodeCategory.Format)
                    return true;
            }

            if (current == '&' && ContainsNumericXmlEntity(text, fastText, start))
                return true;
            if (current != '\\' || start + 2 >= text.Length) continue;

            char kind = fastText is null ? text[start + 1] : fastText[start + 1];
            int minimumDigits = kind is 'u' or 'U' or 'x' ? 1 : 0;
            int maximumDigits = kind == 'u' ? 4 : kind == 'U' ? 8 : kind == 'x' ? 4 : 0;
            if (minimumDigits == 0) continue;

            int availableDigits = 0;
            while (availableDigits < maximumDigits &&
                   start + 2 + availableDigits < text.Length)
            {
                char digit = fastText is null
                    ? text[start + 2 + availableDigits]
                    : fastText[start + 2 + availableDigits];
                if (!IsHexDigit(digit)) break;
                availableDigits++;
            }

            // \u and \U have fixed widths. \x consumes one through four hex digits.
            if (kind == 'x' ? availableDigits >= 1 : availableDigits == maximumDigits)
                return true;
        }
        return false;
    }

    private static bool ContainsNumericXmlEntity(SourceText text, string? fastText, int start)
    {
        if (start + 3 >= text.Length ||
            (fastText is null ? text[start + 1] : fastText[start + 1]) != '#')
            return false;

        int cursor = start + 2;
        char first = fastText is null ? text[cursor] : fastText[cursor];
        bool hexadecimal = first is 'x' or 'X';
        if (hexadecimal) cursor++;
        int digits = 0;
        while (cursor < text.Length)
        {
            char value = fastText is null ? text[cursor] : fastText[cursor];
            bool digit = hexadecimal ? IsHexDigit(value) : value is >= '0' and <= '9';
            if (!digit) break;
            digits++;
            cursor++;
        }
        return digits > 0 && cursor < text.Length &&
               (fastText is null ? text[cursor] : fastText[cursor]) == ';';
    }

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
