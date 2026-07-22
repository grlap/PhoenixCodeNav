using System.Buffers;
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
    internal const int ReferenceScopeFastStringLimitChars = 40_000;
    internal const int ReferenceScopeScanBufferChars = 32 * 1024;
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
        int? ScopedProjects,
        int? DocumentsInScopedProjects,
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
        bool ContainsValueTextTransformation,
        bool MayContainGlobalAlias);

    internal readonly record struct ReferenceTextProbe(
        bool Candidate,
        bool ContainsValueTextTransformation,
        bool MayContainGlobalAlias = false);

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
        int? observedScopedProjects = null;
        int? observedDocumentsInScopedProjects = null;
        int observedAliasWidenedProjects = 0;
        int observedTransformedIncludedDocuments = 0;

        ReferenceDocumentScope Full(string reason, int? solutionDocuments = null,
            int? candidateDocuments = null, int aliasWidenedProjects = 0,
            int transformedIncludedDocuments = 0, int? scopedProjects = null,
            int? documentsInScopedProjects = null)
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
                ScopedProjects: scopedProjects,
                DocumentsInScopedProjects: documentsInScopedProjects,
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
                return Full("no_documents", solutionDocuments: 0, candidateDocuments: 0,
                    scopedProjects: 0, documentsInScopedProjects: 0);

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
                    ReferenceTextProbe probe = ProbeReferenceText(text, candidateNames,
                        fastText, token, ReferenceScopeScanBufferChars);
                    probes[index] = new DocumentProbe(document, probe.Candidate,
                        probe.ContainsValueTextTransformation, probe.MayContainGlobalAlias);
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
                    candidateDocuments: 0, scopedProjects: projectDocuments.Count,
                    documentsInScopedProjects: solutionDocuments.Length);
                solutionCache.Add(cacheKey, full);
                return full;
            }

            var scopedDocuments = candidateDocuments.ToBuilder();
            var widenedProjects = new HashSet<ProjectId>();
            foreach (DocumentProbe probe in probes)
            {
                if (!probe.Candidate || !probe.MayContainGlobalAlias) continue;
                cancellationToken.ThrowIfCancellationRequested();
                Document document = probe.Document;
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
            ProjectId[] scopedProjectIds = scopedDocuments.Select(document => document.Project.Id)
                .Distinct().ToArray();
            observedScopedProjects = scopedProjectIds.Length;
            observedDocumentsInScopedProjects = scopedProjectIds.Sum(projectId =>
                projectDocuments.GetValueOrDefault(projectId).Length);
            if (scopedCount >= solutionDocuments.Length)
            {
                ReferenceDocumentScope full = Full("no_reduction", solutionDocuments.Length,
                    candidateDocuments.Count, widenedProjects.Count,
                    observedTransformedIncludedDocuments, projectDocuments.Count,
                    solutionDocuments.Length);
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
                    ScopedProjects: observedScopedProjects,
                    DocumentsInScopedProjects: observedDocumentsInScopedProjects,
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
                ScopedProjects: observedScopedProjects,
                DocumentsInScopedProjects: observedDocumentsInScopedProjects,
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
                observedTransformedIncludedDocuments, observedScopedProjects,
                observedDocumentsInScopedProjects);
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

    private static ReferenceTextProbe ProbeReferenceText(SourceText text,
        ImmutableArray<string> candidateNames, string? fastText,
        CancellationToken cancellationToken, int bufferChars)
    {
        if (fastText is not null)
            return ProbeReferenceString(fastText, candidateNames);

        return ProbeReferenceTextBuffered(text, candidateNames, cancellationToken,
            bufferChars);
    }

    internal static ReferenceTextProbe TestOnlyProbeReferenceText(SourceText text,
        IEnumerable<string> candidateNames, bool forceBuffered, int bufferChars,
        CancellationToken cancellationToken = default)
    {
        ImmutableArray<string> names = candidateNames.ToImmutableArray();
        if (forceBuffered)
            return ProbeReferenceTextBuffered(text, names, cancellationToken, bufferChars);

        // Keep the original scalar implementation as an independent exactness oracle for the
        // vectorized string and buffered SourceText paths.
        string oracleText = text.ToString();
        bool transformed = ContainsValueTextTransformation(oracleText);
        bool candidate = transformed || names.Any(name =>
            ContainsIdentifier(oracleText, name));
        return new ReferenceTextProbe(candidate, transformed,
            MayContainGlobalAlias(oracleText));
    }

    internal static ReferenceTextProbe TestOnlyProbeReferenceString(SourceText text,
        IEnumerable<string> candidateNames)
        => ProbeReferenceString(text.ToString(), candidateNames.ToImmutableArray());

    private static ReferenceTextProbe ProbeReferenceString(string text,
        ImmutableArray<string> candidateNames)
    {
        var transformation = new ValueTextTransformationScanner();
        bool transformed = transformation.Scan(text.AsSpan());
        bool candidate = transformed || candidateNames.Any(name =>
            ContainsIdentifier(text, name));
        return new ReferenceTextProbe(candidate, transformed,
            MayContainGlobalAlias(text));
    }

    // The contextual keywords must be raw token spellings: escaped or Format-interleaved
    // identifiers have the same ValueText but are not parsed as global/using keywords. The raw
    // substring test is therefore conservative (comments and strings cause harmless parsing),
    // while the syntax root below remains the authority for actual alias directives.
    private static bool MayContainGlobalAlias(string text)
        => text.Contains("global", StringComparison.Ordinal) &&
           text.Contains("using", StringComparison.Ordinal) &&
           text.Contains('=');

    private static ReferenceTextProbe ProbeReferenceTextBuffered(SourceText text,
        ImmutableArray<string> candidateNames, CancellationToken cancellationToken,
        int bufferChars)
    {
        bufferChars = Math.Max(8, bufferChars);
        int maxNameLength = candidateNames.IsDefaultOrEmpty
            ? 0
            : candidateNames.Max(name => name.Length);
        // One character of left context plus enough right context for candidate identifiers
        // and global-alias prefilter terms make every match available to its owning core.
        // Starts belong to exactly one core, so overlapping context never double-counts.
        bool bufferedIdentifiers = (long)maxNameLength + 1 < bufferChars;
        int rightContext = Math.Max(bufferedIdentifiers ? maxNameLength : 0,
            "global".Length);
        int coreChars = bufferChars - rightContext - 1;
        bool candidate = false;
        if (!bufferedIdentifiers)
        {
            foreach (string name in candidateNames)
            {
                if (!ContainsIdentifierScalar(text, name, cancellationToken)) continue;
                candidate = true;
                break;
            }
        }
        bool containsGlobal = false;
        bool containsUsing = false;
        bool containsEquals = false;
        bool transformed = false;
        var transformation = new ValueTextTransformationScanner();
        char[] buffer = ArrayPool<char>.Shared.Rent(bufferChars);
        try
        {
            for (int coreStart = 0; coreStart < text.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int coreLength = Math.Min(coreChars, text.Length - coreStart);
                int prefixLength = coreStart == 0 ? 0 : 1;
                int copyStart = coreStart - prefixLength;
                int copyEnd = (int)Math.Min(text.Length,
                    (long)coreStart + coreLength + rightContext);
                int copyLength = copyEnd - copyStart;
                text.CopyTo(copyStart, buffer, 0, copyLength);

                int localCoreStart = prefixLength;
                ReadOnlySpan<char> core = buffer.AsSpan(localCoreStart, coreLength);
                if (!transformed && transformation.Scan(core))
                {
                    transformed = true;
                    candidate = true;
                }

                ReadOnlySpan<char> window = buffer.AsSpan(0, copyLength);
                int localCoreEnd = localCoreStart + coreLength;
                if (!containsGlobal)
                    containsGlobal = ContainsRawText(window, localCoreStart, localCoreEnd,
                        "global");
                if (!containsUsing)
                    containsUsing = ContainsRawText(window, localCoreStart, localCoreEnd,
                        "using");
                if (!containsEquals)
                    containsEquals = core.Contains('=');

                if (!candidate && bufferedIdentifiers)
                {
                    foreach (string name in candidateNames)
                    {
                        if (!ContainsIdentifier(window, copyStart, localCoreStart,
                                localCoreEnd, text.Length, name))
                            continue;
                        candidate = true;
                        break;
                    }
                }

                coreStart += coreLength;
            }

            return new ReferenceTextProbe(candidate, transformed,
                containsGlobal && containsUsing && containsEquals);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static bool ContainsIdentifier(string text, string value)
    {
        if (value.Length == 0 || value.Length > text.Length) return false;
        int searchFrom = 0;
        while (searchFrom <= text.Length - value.Length)
        {
            int start = text.IndexOf(value, searchFrom, StringComparison.Ordinal);
            if (start < 0) return false;
            if (HasIdentifierBoundaries(text, start, value.Length)) return true;
            searchFrom = start + 1;
        }
        return false;
    }

    private static bool ContainsIdentifier(ReadOnlySpan<char> window, int windowStart,
        int localCoreStart, int localCoreEnd, int documentLength, string value)
    {
        if (value.Length == 0 || value.Length > documentLength) return false;
        int searchFrom = localCoreStart;
        while (searchFrom < localCoreEnd && window.Length - searchFrom >= value.Length)
        {
            int relative = window[searchFrom..].IndexOf(value.AsSpan(),
                StringComparison.Ordinal);
            if (relative < 0) return false;
            int localStart = searchFrom + relative;
            if (localStart >= localCoreEnd) return false;
            int globalStart = windowStart + localStart;
            bool leftBoundary = globalStart == 0 ||
                !SyntaxFacts.IsIdentifierPartCharacter(window[localStart - 1]);
            int globalAfter = globalStart + value.Length;
            bool rightBoundary = globalAfter == documentLength ||
                !SyntaxFacts.IsIdentifierPartCharacter(window[localStart + value.Length]);
            if (leftBoundary && rightBoundary) return true;
            searchFrom = localStart + 1;
        }
        return false;
    }

    private static bool ContainsRawText(ReadOnlySpan<char> window, int localCoreStart,
        int localCoreEnd, string value)
    {
        int searchFrom = localCoreStart;
        while (searchFrom < localCoreEnd && window.Length - searchFrom >= value.Length)
        {
            int relative = window[searchFrom..].IndexOf(value.AsSpan(),
                StringComparison.Ordinal);
            if (relative < 0) return false;
            int localStart = searchFrom + relative;
            if (localStart >= localCoreEnd) return false;
            return true;
        }
        return false;
    }

    private static bool ContainsIdentifierScalar(SourceText text, string value,
        CancellationToken cancellationToken)
    {
        if (value.Length == 0 || value.Length > text.Length) return false;
        int lastStart = text.Length - value.Length;
        for (int start = 0; start <= lastStart; start++)
        {
            if ((start & 0xfff) == 0) cancellationToken.ThrowIfCancellationRequested();
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

    private static bool ContainsValueTextTransformation(string text)
    {
        for (int start = 0; start < text.Length; start++)
        {
            char current = text[start];
            if (char.GetUnicodeCategory(current) == UnicodeCategory.Format)
                return true;
            if (char.IsHighSurrogate(current) && start + 1 < text.Length)
            {
                char low = text[start + 1];
                if (Rune.TryCreate(current, low, out Rune scalar) &&
                    Rune.GetUnicodeCategory(scalar) == UnicodeCategory.Format)
                    return true;
            }

            if (current == '&' && ContainsNumericXmlEntity(text, start))
                return true;
            if (current != '\\' || start + 2 >= text.Length) continue;

            char kind = text[start + 1];
            int minimumDigits = kind is 'u' or 'U' or 'x' ? 1 : 0;
            int maximumDigits = kind == 'u' ? 4 : kind == 'U' ? 8 : kind == 'x' ? 4 : 0;
            if (minimumDigits == 0) continue;

            int availableDigits = 0;
            while (availableDigits < maximumDigits &&
                   start + 2 + availableDigits < text.Length)
            {
                char digit = text[start + 2 + availableDigits];
                if (!IsHexDigit(digit)) break;
                availableDigits++;
            }

            // \u and \U have fixed widths. \x consumes one through four hex digits.
            if (kind == 'x' ? availableDigits >= 1 : availableDigits == maximumDigits)
                return true;
        }
        return false;
    }

    private static bool ContainsNumericXmlEntity(string text, int start)
    {
        if (start + 3 >= text.Length || text[start + 1] != '#')
            return false;

        int cursor = start + 2;
        char first = text[cursor];
        bool hexadecimal = first is 'x' or 'X';
        if (hexadecimal) cursor++;
        int digits = 0;
        while (cursor < text.Length)
        {
            char value = text[cursor];
            bool digit = hexadecimal ? IsHexDigit(value) : value is >= '0' and <= '9';
            if (!digit) break;
            digits++;
            cursor++;
        }
        return digits > 0 && cursor < text.Length && text[cursor] == ';';
    }

    private enum EscapeScanState
    {
        None,
        Backslash,
        Unicode4,
        Unicode8,
        Hexadecimal,
    }

    private enum NumericEntityScanState
    {
        None,
        Ampersand,
        Hash,
        HexPrefix,
        DecimalDigits,
        HexDigits,
    }

    private struct ValueTextTransformationScanner
    {
        private EscapeScanState _escapeState;
        private NumericEntityScanState _entityState;
        private int _escapeDigits;
        private char _pendingHighSurrogate;

        private readonly bool IsIdle =>
            _escapeState == EscapeScanState.None &&
            _entityState == NumericEntityScanState.None &&
            _pendingHighSurrogate == '\0';

        public bool Scan(ReadOnlySpan<char> text)
        {
            int index = 0;
            while (index < text.Length)
            {
                if (IsIdle)
                {
                    ReadOnlySpan<char> remaining = text[index..];
                    int asciiSpecial = remaining.IndexOfAny('&', '\\');
                    int nonAscii = remaining.IndexOfAnyExceptInRange('\0', '\x7f');
                    int next = asciiSpecial < 0
                        ? nonAscii
                        : nonAscii < 0 ? asciiSpecial : Math.Min(asciiSpecial, nonAscii);
                    if (next < 0) return false;
                    index += next;
                }

                char current = text[index++];
                if (AdvanceEscape(current) || AdvanceEntity(current)) return true;

                char pendingHigh = _pendingHighSurrogate;
                _pendingHighSurrogate = '\0';
                if (pendingHigh != '\0' && char.IsLowSurrogate(current) &&
                    Rune.TryCreate(pendingHigh, current, out Rune scalar) &&
                    Rune.GetUnicodeCategory(scalar) == UnicodeCategory.Format)
                    return true;

                if (current < '\x80') continue;
                if (char.GetUnicodeCategory(current) == UnicodeCategory.Format)
                    return true;
                if (char.IsHighSurrogate(current))
                    _pendingHighSurrogate = current;
            }
            return false;
        }

        private bool AdvanceEscape(char current)
        {
            switch (_escapeState)
            {
                case EscapeScanState.None:
                    if (current == '\\') _escapeState = EscapeScanState.Backslash;
                    return false;
                case EscapeScanState.Backslash:
                    _escapeDigits = 0;
                    _escapeState = current switch
                    {
                        'u' => EscapeScanState.Unicode4,
                        'U' => EscapeScanState.Unicode8,
                        'x' => EscapeScanState.Hexadecimal,
                        '\\' => EscapeScanState.Backslash,
                        _ => EscapeScanState.None,
                    };
                    return false;
                case EscapeScanState.Hexadecimal:
                    if (IsHexDigit(current)) return true;
                    RestartEscape(current);
                    return false;
                case EscapeScanState.Unicode4:
                    if (!IsHexDigit(current))
                    {
                        RestartEscape(current);
                        return false;
                    }
                    return ++_escapeDigits == 4;
                case EscapeScanState.Unicode8:
                    if (!IsHexDigit(current))
                    {
                        RestartEscape(current);
                        return false;
                    }
                    return ++_escapeDigits == 8;
                default:
                    throw new InvalidOperationException("Unknown escape scan state.");
            }
        }

        private void RestartEscape(char current)
        {
            _escapeDigits = 0;
            _escapeState = current == '\\'
                ? EscapeScanState.Backslash
                : EscapeScanState.None;
        }

        private bool AdvanceEntity(char current)
        {
            switch (_entityState)
            {
                case NumericEntityScanState.None:
                    if (current == '&') _entityState = NumericEntityScanState.Ampersand;
                    return false;
                case NumericEntityScanState.Ampersand:
                    if (current == '#')
                        _entityState = NumericEntityScanState.Hash;
                    else
                        RestartEntity(current);
                    return false;
                case NumericEntityScanState.Hash:
                    if (current is 'x' or 'X')
                        _entityState = NumericEntityScanState.HexPrefix;
                    else if (current is >= '0' and <= '9')
                        _entityState = NumericEntityScanState.DecimalDigits;
                    else
                        RestartEntity(current);
                    return false;
                case NumericEntityScanState.HexPrefix:
                    if (IsHexDigit(current))
                        _entityState = NumericEntityScanState.HexDigits;
                    else
                        RestartEntity(current);
                    return false;
                case NumericEntityScanState.DecimalDigits:
                    if (current is >= '0' and <= '9') return false;
                    if (current == ';') return true;
                    RestartEntity(current);
                    return false;
                case NumericEntityScanState.HexDigits:
                    if (IsHexDigit(current)) return false;
                    if (current == ';') return true;
                    RestartEntity(current);
                    return false;
                default:
                    throw new InvalidOperationException("Unknown numeric entity scan state.");
            }
        }

        private void RestartEntity(char current)
            => _entityState = current == '&'
                ? NumericEntityScanState.Ampersand
                : NumericEntityScanState.None;
    }

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
