using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;

namespace CodeNav.Tests;

/// <summary>
/// Regression coverage for review batch 4: PhoenixCodeNav-cdd (search_text line grading,
/// no silent first-token substitution) and 1ze (heuristic confidence label).
/// </summary>
public class Batch4SearchGradingTests : IClassFixture<IndexFixture>, IDisposable
{
    private readonly IndexFixture _fx;
    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;

    public Batch4SearchGradingTests(IndexFixture fx)
    {
        _fx = fx;
        _manager = new IndexManager(_fx.Root, _fx.DbPath);
        _manager.Start();
        for (int i = 0; i < 600 && !_manager.IsQueryable; i++) Thread.Sleep(50); // 30s: the 5s wait was the suite-wide startup-starvation flake class
        _semantic = new SemanticService(_manager);
    }

    public void Dispose()
    {
        _semantic.Dispose();
        _manager.Dispose();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void PreciseHitsContainAllTokens()
    {
        using var q = _manager.OpenQueries();
        var res = q.SearchTextGraded("Guard NotNull", 30, null, 300, 0, "auto");
        Assert.True(res.TotalPrecise > 0, "expected precise co-occurrence hits for Guard.NotNull call sites");
        var precise = res.Hits.Where(h => h.MatchKind == "precise").ToList();
        Assert.NotEmpty(precise);
        Assert.All(precise, h =>
        {
            Assert.Contains("Guard", h.LineText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("NotNull", h.LineText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void SingleTokenQueryIsAllPrecise()
    {
        using var q = _manager.OpenQueries();
        var res = q.SearchTextGraded("AcmeException", 20, null, 300, 0, "auto");
        Assert.True(res.TotalPrecise > 0);
        Assert.Equal(0, res.TotalPartial);
        Assert.All(res.Hits, h => Assert.Equal("precise", h.MatchKind));
    }

    [Fact]
    public void SplitTokensYieldTokenCoveringPartials_NotFirstTokenSpam()
    {
        // The exact bug: two tokens both present in a file but never on one line. The old code
        // returned every first-token line as a full hit; the fix returns token-covering partials.
        using var q0 = _manager.OpenQueries();
        var anyCs = q0.FindFiles("*.cs", 1).Single();
        string dir = Path.GetDirectoryName(anyCs.Path)!.Replace('\\', '/');
        string rel = $"{dir}/ZebraSplit.cs";
        string full = Path.Combine(_fx.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(full,
            "namespace Zebra\n{\n" +
            "    // ZebraAlpha marker one\n" +
            "    // ZebraAlpha marker two\n" +
            "    // ZebraAlpha marker three\n" +
            "    class C\n    {\n" +
            "        // ZebraBeta marker\n" +
            "    }\n}\n");
        try
        {
            IndexManagerTestSupport.RefreshAndWait(
                _manager,
                new[] { rel },
                q => q.ContentByPath(rel)?.Contains("ZebraBeta", StringComparison.Ordinal) == true,
                "the added grading fixture was not indexed");

            using var q = _manager.OpenQueries();
            var res = q.SearchTextGraded("ZebraAlpha ZebraBeta", 20, null, 300, 0, "auto");

            Assert.Equal(0, res.TotalPrecise);                 // no line has both tokens
            Assert.True(res.TotalPartial >= 2, "expected token-covering partials");
            var fileHits = res.Hits.Where(h => h.FilePath == rel).ToList();
            Assert.True(fileHits.Count <= 2,
                $"token-covering means <=1 line per token (<=2 total), got {fileHits.Count} (first-token spam?)");
            Assert.All(res.Hits, h => Assert.Equal("partial", h.MatchKind));
            Assert.Contains(res.Hits, h => h.Matched is not null && h.Matched.Contains("ZebraAlpha"));
            Assert.Contains(res.Hits, h => h.Matched is not null && h.Matched.Contains("ZebraBeta"));
            Assert.Contains(rel, res.FilesMatchedAcrossLines);

            // partials='never' drops them entirely (no precise -> empty).
            var never = q.SearchTextGraded("ZebraAlpha ZebraBeta", 20, null, 300, 0, "never");
            Assert.Empty(never.Hits);
            Assert.Equal(0, never.TotalPrecise);

            // Single token collapses to all-precise (the repeated ZebraAlpha lines).
            var single = q.SearchTextGraded("ZebraAlpha", 20, null, 300, 0, "auto");
            Assert.True(single.TotalPrecise >= 3);
            Assert.All(single.Hits, h => Assert.Equal("precise", h.MatchKind));
        }
        finally
        {
            File.Delete(full);
            IndexManagerTestSupport.RefreshAndWait(
                _manager,
                new[] { rel },
                q => q.ContentByPath(rel) is null,
                "the deleted grading fixture remained indexed");
        }
    }

    [Fact]
    public void SubstringTokenIsNotGradedPrecise()
    {
        // 'Zeb' is a whole-token substring of 'ZebItem'. For query 'Zeb ZebItem', a ZebItem-only
        // line must NOT be graded precise (the pre-fix raw-substring check wrongly did — Order/OrderId).
        using var q0 = _manager.OpenQueries();
        var anyCs = q0.FindFiles("*.cs", 1).Single();
        string dir = Path.GetDirectoryName(anyCs.Path)!.Replace('\\', '/');
        string rel = $"{dir}/ZebSubstring.cs";
        string full = Path.Combine(_fx.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(full,
            "namespace Zebra\n{\n" +
            "    // standalone Zeb marker\n" +
            "    // ZebItem alpha\n" +
            "    // ZebItem beta\n" +
            "}\n");
        try
        {
            IndexManagerTestSupport.RefreshAndWait(
                _manager,
                new[] { rel },
                q => q.ContentByPath(rel)?.Contains("ZebItem", StringComparison.Ordinal) == true,
                "the substring grading fixture was not indexed");
            using var q = _manager.OpenQueries();
            var res = q.SearchTextGraded("Zeb ZebItem", 20, null, 300, 0, "auto");
            // No line contains BOTH whole tokens ('Zeb' as a token appears only on the standalone line).
            Assert.Equal(0, res.TotalPrecise);
            Assert.True(res.TotalPartial >= 2);
            Assert.Contains(res.Hits, h => h.Matched is not null && h.Matched.Contains("Zeb"));
            Assert.Contains(res.Hits, h => h.Matched is not null && h.Matched.Contains("ZebItem"));
        }
        finally
        {
            File.Delete(full);
            IndexManagerTestSupport.RefreshAndWait(
                _manager,
                new[] { rel },
                q => q.ContentByPath(rel) is null,
                "the deleted substring fixture remained indexed");
        }
    }

    [Fact]
    public void SearchTextToolExposesMatchKindAndCounts()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.SearchText("AcmeException"));
        Assert.True(json.GetProperty("preciseCount").GetInt32() > 0);
        Assert.Equal(0, json.GetProperty("partialCount").GetInt32());
        var first = json.GetProperty("hits").EnumerateArray().First();
        Assert.Equal("precise", first.GetProperty("matchKind").GetString());
        // 'matched' is null on precise hits (omitted from JSON by the null-ignoring serializer).
        Assert.False(first.TryGetProperty("matched", out var m) && m.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void RelatedTestsIsHeuristic()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.RelatedTests("Guard"));
        Assert.Equal("heuristic", json.GetProperty("meta").GetProperty("confidence").GetString());
    }

    [Fact]
    public void ImplementationsFallbackIsHeuristic()
    {
        // A name with no semantic target skips the exact path and hits the base-list-name fallback.
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.Implementations(name: "NoSuchTypeXyz123", timeoutMs: 5000));
        Assert.Equal("heuristic", json.GetProperty("meta").GetProperty("confidence").GetString());
    }

    [Fact]
    public void CapabilitiesAdvertiseHeuristicConfidence()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.ServerCapabilities());
        // confidenceModel is an object mapping each tier to its meaning (r2o steering).
        var model = json.GetProperty("confidenceModel");
        Assert.False(string.IsNullOrEmpty(model.GetProperty("heuristic").GetString()));
        string indexed = model.GetProperty("indexed").GetString()!;
        Assert.Contains("bounded FCS semantic result", indexed);
        Assert.Contains("with an error", indexed);
        Assert.Contains("authority loss", indexed);
        Assert.Contains("unclassified partial reason", indexed);
        Assert.Contains("Roslyn", model.GetProperty("exact").GetString());
        Assert.Contains("bounded FCS semantic result", model.GetProperty("exact").GetString());
        Assert.Contains("preserve selected-context authority",
            model.GetProperty("exact").GetString());
        var languages = json.GetProperty("languages").EnumerateArray()
            .Select(language => language.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("markdown", languages);
        Assert.Contains("sql", languages);
        Assert.Equal(
            new[] { "text" },
            json.GetProperty("languageLayers").GetProperty("markdown").EnumerateArray()
                .Select(layer => layer.GetString()));
        Assert.Equal(
            new[] { "text" },
            json.GetProperty("languageLayers").GetProperty("sql").EnumerateArray()
                .Select(layer => layer.GetString()));
        JsonElement semantic = json.GetProperty("semantic");
        Assert.Equal("cs", semantic.GetProperty("exactToolsLanguage").GetString());
        Assert.Contains("definition", semantic.GetProperty("csharpExactTools")
            .EnumerateArray().Select(tool => tool.GetString()));
        Assert.Contains("definition", semantic.GetProperty("fsharpSemanticTools")
            .EnumerateArray().Select(tool => tool.GetString()));
        Assert.Contains("references", semantic.GetProperty("fsharpSemanticTools")
            .EnumerateArray().Select(tool => tool.GetString()));
        Assert.DoesNotContain("search_symbol", semantic.GetProperty("fsharpSemanticTools")
            .EnumerateArray().Select(tool => tool.GetString()));
        Assert.False(semantic.TryGetProperty("fsharpIndexedTools", out _));
        Assert.Contains("search_symbol", semantic.GetProperty("fsharpSyntaxIndexedTools")
            .EnumerateArray().Select(tool => tool.GetString()));
        Assert.Contains("compiler-checked", semantic.GetProperty("note").GetString());
        Assert.Contains("successful results are exact only", semantic.GetProperty("note").GetString());
        Assert.Contains("every error", semantic.GetProperty("note").GetString());
        Assert.Contains("unclassified partial reason is indexed",
            semantic.GetProperty("note").GetString());
        Assert.Contains("workspace lower bound", semantic.GetProperty("note").GetString());
        Assert.Contains("syntax-indexed", semantic.GetProperty("fsharpSyntaxNote").GetString());
        Assert.Contains("SDK/import limits advisory",
            semantic.GetProperty("fsharpSyntaxNote").GetString());
        JsonElement ownerCoverage = json.GetProperty("index")
            .GetProperty("fsharpParseOwnerCoverage");
        Assert.Equal("search_symbol.fsharpParseCoverage",
            ownerCoverage.GetProperty("response").GetString());
        Assert.Equal("per_file_owner_incidences",
            ownerCoverage.GetProperty("aggregation").GetString());
        Assert.Contains("at least one omitted",
            ownerCoverage.GetProperty("truncatedOwnerProjects").GetString());
        Assert.Contains("no retained",
            ownerCoverage.GetProperty("unrepresentedOwnerProjects").GetString());
        Assert.Contains("some but not all",
            ownerCoverage.GetProperty("partiallyTruncatedOwnerProjects").GetString());
        Assert.Equal(
            "truncatedOwnerProjects = unrepresentedOwnerProjects + partiallyTruncatedOwnerProjects",
            ownerCoverage.GetProperty("invariant").GetString());
    }

    // Deploy-verifiability (field feedback: an agent could not confirm a deploy because the version
    // was a hardcoded literal and no build identity was surfaced). A caller must be able to tell WHICH
    // build is running: version is sourced from BuildInfo, build.commit round-trips the git stamp,
    // indexSchema matches the builder.
    [Fact]
    public void CapabilitiesStampBuildIdentityForDeployVerification()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.ServerCapabilities());
        Assert.Equal(BuildInfo.Version, json.GetProperty("version").GetString());
        var build = json.GetProperty("build");
        Assert.Equal(BuildInfo.Version, build.GetProperty("version").GetString());
        string commit = build.GetProperty("commit").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(commit)); // a SHA when built in a repo, else "unknown"
        Assert.Equal(BuildInfo.Commit, commit);           // round-trips the build-time stamp
        Assert.Equal(IndexBuilder.SchemaVersion, build.GetProperty("indexSchema").GetString());
        Assert.Equal("30", build.GetProperty("indexSchema").GetString());
        Assert.Equal(64 * 1024,
            json.GetProperty("budgets").GetProperty("hardBytes").GetInt32());
        Assert.Contains("complete compiler identity",
            json.GetProperty("budgets")
                .GetProperty("indivisibleSemanticIdentity").GetString());
        JsonElement semantic = json.GetProperty("semantic");
        string? expectedFrameworkSource = _semantic.FrameworkRefsSource;
        Assert.Equal(expectedFrameworkSource is not null, _semantic.FrameworkRefsAvailable);
        if (expectedFrameworkSource is null)
        {
            Assert.False(semantic.TryGetProperty("frameworkRefsSource", out _));
        }
        else
        {
            Assert.True(semantic.TryGetProperty("frameworkRefsSource", out JsonElement source));
            Assert.Equal(expectedFrameworkSource, source.GetString());
        }
    }

    [Fact]
    public void CapabilitiesExposeTheExactFrameworkReferenceSource()
    {
        var health = new IndexHealth("ready", "epoch-29", "indexed", "refreshed", 0,
            null, 123, "/workspace", "index.db");
        const string pinnedSource = "/fixtures/net472";

        JsonElement semantic = Parse(NavigationTools.ServerCapabilitiesUncompactedForTest(
                health, frameworkRefsAvailable: true, frameworkRefsSource: pinnedSource))
            .GetProperty("semantic");

        Assert.True(semantic.GetProperty("frameworkRefsAvailable").GetBoolean());
        Assert.Equal(pinnedSource, semantic.GetProperty("frameworkRefsSource").GetString());
    }

    [Fact]
    public void CapabilitiesOmitFrameworkReferenceSourceWhenUnavailable()
    {
        var health = new IndexHealth("ready", "epoch-29", "indexed", "refreshed", 0,
            null, 123, "/workspace", "index.db");

        JsonElement semantic = Parse(NavigationTools.ServerCapabilitiesUncompactedForTest(
                health, frameworkRefsAvailable: false, frameworkRefsSource: null))
            .GetProperty("semantic");

        Assert.False(semantic.GetProperty("frameworkRefsAvailable").GetBoolean());
        Assert.False(semantic.TryGetProperty("frameworkRefsSource", out _));
    }

    [Fact]
    public void CapabilitiesPreserveStartupRebuildEvidenceAfterReadiness()
    {
        var health = new IndexHealth("ready", "epoch-29", "indexed", "refreshed", 0,
            null, 123, "C:/workspace", "index.db",
            StartupBuildReason: "startup_incompatible", StartupPriorSchema: "28");
        JsonElement index = Parse(
            NavigationTools.ServerCapabilitiesUncompactedForTest(health))
            .GetProperty("index");

        Assert.Equal("startup_incompatible",
            index.GetProperty("startupBuildReason").GetString());
        Assert.False(index.TryGetProperty("startupBuildReasonTruncated", out _));
        Assert.False(index.TryGetProperty("startupBuildReasonBytes", out _));
        Assert.Equal("28", index.GetProperty("startupPriorSchema").GetString());
        Assert.False(index.TryGetProperty("startupPriorSchemaTruncated", out _));
        Assert.False(index.TryGetProperty("startupPriorSchemaBytes", out _));

        var ordinaryReuse = new IndexHealth("ready", "epoch-29", "indexed", "reused", 0,
            null, 123, "C:/workspace", "index.db");
        JsonElement ordinaryIndex = Parse(
            NavigationTools.ServerCapabilitiesUncompactedForTest(ordinaryReuse))
            .GetProperty("index");
        Assert.False(ordinaryIndex.TryGetProperty("startupBuildReason", out _));
        Assert.False(ordinaryIndex.TryGetProperty("startupBuildReasonTruncated", out _));
        Assert.False(ordinaryIndex.TryGetProperty("startupBuildReasonBytes", out _));
        Assert.False(ordinaryIndex.TryGetProperty("startupPriorSchema", out _));
        Assert.False(ordinaryIndex.TryGetProperty("startupPriorSchemaTruncated", out _));
        Assert.False(ordinaryIndex.TryGetProperty("startupPriorSchemaBytes", out _));
    }

    // The features manifest lets a caller CONFIRM a capability without triggering its silent-when-clean
    // response fields — the exact verification the field agent couldn't do from a bare response.
    [Fact]
    public void CapabilitiesFeatureManifestLetsCallerConfirmCapabilities()
    {
        var health = new IndexHealth("ready", "11", "indexed", "refreshed", 0,
            null, 123, "C:/workspace", "index.db");
        var json = Parse(NavigationTools.ServerCapabilitiesUncompactedForTest(health));
        var idList = json.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("id").GetString()!)
            .ToList();
        Assert.Equal(idList.Count, idList.Distinct(StringComparer.Ordinal).Count());
        var ids = idList.ToHashSet(StringComparer.Ordinal);
        Assert.Contains("compiled-awareness", ids);
        Assert.Contains("implementer-completeness", ids);
        Assert.Contains("generic-arity-resolution", ids);
        Assert.Contains("friend-assembly-semantics", ids);
        Assert.Contains("fsharp-outline-parse-context-budget", ids);
        Assert.Contains("fsharp-indexed-symbol-name-search", ids);
        Assert.Contains("fsharp-indexed-parse-context-budget", ids);
        Assert.Contains("fsharp-parse-owner-coverage-breakdown", ids);
        Assert.Contains("fsharp-symbol-at-semantic", ids);
        Assert.Contains("fsharp-definition-same-project", ids);
        Assert.Contains("fsharp-references-same-project", ids);
        Assert.Contains("fsharp-semantic-project-reference-closure", ids);
        Assert.Contains("fsharp-type-check-context-selection", ids);
        Assert.Contains("fsharp-semantic-confidence-authority", ids);
        Assert.Contains("fsharp-semantic-snapshot", ids);
        Assert.Contains("fsharp-semantic-bounded-project-evaluation", ids);
        Assert.Contains("fsharp-semantic-package-asset-closure", ids);
        Assert.Contains("csharp-semantic-central-package-management", ids);
        Assert.Contains("csharp-semantic-central-package-property-expansion", ids);
        Assert.Contains("shared-mcp-daemon", ids);
        Assert.Contains("shared-mcp-daemon-default", ids);
        Assert.Contains("workspace-msbuild-config-indexing", ids);
        Assert.Contains("hierarchy-ranking", ids);
        Assert.Contains("capabilities-hard-budget", ids);
        Assert.Contains("semantic-large-repo-budget", ids);
        Assert.DoesNotContain("index-read-followers", ids);
        Assert.Contains("single-workspace-writer-mutex", ids);
        Assert.Contains("index-destination-claim", ids);
        Assert.DoesNotContain("semantic-rebuild-coordination", ids);
        Assert.Contains("semantic-candidate-completeness-over-accounting", ids);
        Assert.Contains("semantic-planning-attribution", ids);
        Assert.Contains("indexed-base-type-edges", ids);
        Assert.Contains("references-stage-attribution", ids);
        Assert.Contains("references-deterministic-samples", ids);
        Assert.Contains("references-parallel-compilation-preparation", ids);
        Assert.Contains("references-document-scoped-search", ids);
        Assert.Contains("semantic-persistent-syntax-indexes", ids);
        Assert.Contains("references-compilation-critical-path-attribution", ids);
        Assert.Contains("stack-safe-syntax-indexing", ids);
        Assert.Contains("csharp-conversion-operator-indexing", ids);
        Assert.Contains("csharp-conversion-semantic-handles", ids);
        Assert.Contains("references-candidate-file-cap-disclosure", ids);
        Assert.Contains("csharp-conversion-usage-enumeration", ids);
        Assert.Contains("csharp-foreach-conversion-operator-kind", ids);
        Assert.Contains("csharp-operator-semantic-handles", ids);
        Assert.Contains("csharp-explicit-interface-operator-accessibility", ids);
        Assert.Contains("semantic-indivisible-identity-completeness", ids);
        Assert.Contains("references-buffered-document-scope-scan", ids);
        Assert.Contains("semantic-byte-governed-retention", ids);
        Assert.Contains("references-process-cpu-attribution", ids);
        Assert.Contains("references-gc-pause-attribution", ids);
        Assert.Contains("index-raw-ordinal-symbol-batching", ids);
        Assert.Contains("index-raw-ordinal-file-batching", ids);
        Assert.Contains("index-deferred-secondary-index-build", ids);
        Assert.Contains("index-private-staged-rebuild-publication", ids);
        Assert.Contains("index-live-recovery-sidecar-publication-boundary", ids);
        Assert.Contains("index-schema-29-fsharp-output-rebuild", ids);
        Assert.Contains("index-startup-rebuild-evidence", ids);
        Assert.Contains("review-deleted-solution-metadata-counts", ids);
        Assert.Contains("index-raw-ordinal-content-fts-batching", ids);
        Assert.Contains("index-bounded-synchronous-csharp-build-handoff", ids);
        Assert.Contains("index-build-request-dispatch-isolation", ids);
        Assert.Contains("index-deferred-fts-rebuild", ids);
        Assert.Contains("index-size-prioritized-csharp-build-scheduling", ids);
        Assert.Contains("index-abandoned-private-stage-reaping", ids);
        Assert.Contains("linux-arm64-anchored-authority", ids);
        Assert.Contains("portal-directory-entry-nul-decoding", ids);
        Assert.Contains("operations-portal-jsonl-readonly", ids);
        Assert.Contains("operations-portal-live-build-status", ids);
        Assert.Contains("operations-portal-mcp-launcher", ids);
        Assert.Contains("operations-portal-queryable-evidence", ids);
        Assert.Contains("operations-portal-deterministic-semantic-summary", ids);
        Assert.Contains("mcp-structured-argument-errors", ids);
        Assert.Contains("implementations-semantic-retry-guidance", ids);
        Assert.Contains("cold-start-retry-contract", ids);
        Assert.Contains("refresh-recovery-self-heal", ids);
        Assert.Contains("git-awareness", ids);
        Assert.Contains("batch-outline-json-array-paths", ids);
        Assert.Contains("csharp-symbol-free-outline", ids);
        Assert.Contains("refresh-review-json-array-paths", ids);
        Assert.Contains("search-symbol-filtered-existence", ids);
        Assert.Contains("search-symbol-type-relevance", ids);
        Assert.Contains("indexed-path-suggestions", ids);
        Assert.Contains("source-context-range-alias", ids);
        Assert.Contains("markdown-sql-text-indexing", ids);
        Assert.Equal(1, idList.Count(id => id == "search-symbol-filtered-existence"));
        Assert.Equal(1, idList.Count(id => id == "search-symbol-type-relevance"));
        Assert.Equal(1, idList.Count(id => id == "indexed-path-suggestions"));
        Assert.Equal(1, idList.Count(id => id == "source-context-range-alias"));
        Assert.Equal(1, idList.Count(id => id == "markdown-sql-text-indexing"));
        Assert.Equal(1, idList.Count(id => id == "fsharp-indexed-symbol-name-search"));
        Assert.Equal(1, idList.Count(id => id == "fsharp-indexed-parse-context-budget"));
        Assert.Equal(1,
            idList.Count(id => id == "fsharp-parse-owner-coverage-breakdown"));
        Assert.Equal(1,
            idList.Count(id => id == "fsharp-semantic-project-reference-closure"));
        Assert.Equal(1, idList.Count(id => id == "csharp-symbol-free-outline"));
        Assert.Equal(1, idList.Count(id => id == "csharp-conversion-operator-indexing"));
        Assert.Equal(1, idList.Count(id => id == "csharp-conversion-semantic-handles"));
        Assert.Equal(1, idList.Count(id => id == "references-candidate-file-cap-disclosure"));
        Assert.Equal(1, idList.Count(id => id == "references-deterministic-samples"));
        Assert.Equal(1, idList.Count(id => id == "csharp-conversion-usage-enumeration"));
        Assert.Equal(1, idList.Count(id => id == "csharp-foreach-conversion-operator-kind"));
        Assert.Equal(1, idList.Count(id => id ==
            "index-live-recovery-sidecar-publication-boundary"));
        Assert.Equal(1, idList.Count(id => id ==
            "index-schema-29-fsharp-output-rebuild"));
        Assert.Equal(1, idList.Count(id => id ==
            "index-startup-rebuild-evidence"));
        Assert.Equal(1, idList.Count(id => id ==
            "review-deleted-solution-metadata-counts"));
        Assert.Equal(1, idList.Count(id => id == "csharp-operator-semantic-handles"));
        Assert.Equal(1, idList.Count(id => id ==
            "csharp-explicit-interface-operator-accessibility"));
        Assert.Equal(1, idList.Count(id => id ==
            "semantic-indivisible-identity-completeness"));
        Assert.Equal(1, idList.Count(id => id == "refresh-review-json-array-paths"));
        Assert.Equal(
            1,
            idList.Count(id => id == "operations-portal-jsonl-readonly"));
        Assert.Equal(
            1,
            idList.Count(id => id == "operations-portal-live-build-status"));
        Assert.Equal(
            1,
            idList.Count(id => id == "operations-portal-mcp-launcher"));
        Assert.Equal(
            1,
            idList.Count(id => id == "operations-portal-queryable-evidence"));
        Assert.Equal(
            1,
            idList.Count(id => id == "operations-portal-deterministic-semantic-summary"));
        Assert.Equal(
            1,
            idList.Count(id => id == "mcp-structured-argument-errors"));
        Assert.Equal(
            1,
            idList.Count(id => id == "implementations-semantic-retry-guidance"));
        Assert.Equal(1, idList.Count(id => id == "cold-start-retry-contract"));
        string fsharpSymbolSearch = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-indexed-symbol-name-search")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.52", fsharpSymbolSearch);
        Assert.Contains("schema v26", fsharpSymbolSearch);
        Assert.Contains("indexed owner/TFM parse contexts", fsharpSymbolSearch);
        Assert.DoesNotContain("all available", fsharpSymbolSearch);
        Assert.Contains("project-option delta convergence", fsharpSymbolSearch);
        Assert.Contains("parse and project-option coverage", fsharpSymbolSearch);
        Assert.Contains("actionably incomplete contexts are partial", fsharpSymbolSearch);
        Assert.Contains("SDK/import limits remain advisory", fsharpSymbolSearch);
        Assert.Contains(".fsx-only scopes fail closed", fsharpSymbolSearch);
        string fsharpIndexedContextBudget = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-indexed-parse-context-budget")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.55", fsharpIndexedContextBudget);
        Assert.Contains("schema v28", fsharpIndexedContextBudget);
        Assert.Contains("at most 64", fsharpIndexedContextBudget);
        Assert.Contains("one context per valid compile owner", fsharpIndexedContextBudget);
        Assert.Contains("total/processed/truncated", fsharpIndexedContextBudget);
        Assert.Contains("truncatedOwnerProjects", fsharpIndexedContextBudget);
        Assert.Contains("fsharp_parse_contexts_truncated", fsharpIndexedContextBudget);
        string fsharpOwnerCoverage = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-parse-owner-coverage-breakdown")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.82", fsharpOwnerCoverage);
        Assert.Contains("schema v30", fsharpOwnerCoverage);
        Assert.Contains("search_symbol.fsharpParseCoverage", fsharpOwnerCoverage);
        Assert.Contains("truncatedOwnerProjects", fsharpOwnerCoverage);
        Assert.Contains("unrepresentedOwnerProjects", fsharpOwnerCoverage);
        Assert.Contains("partiallyTruncatedOwnerProjects", fsharpOwnerCoverage);
        Assert.Contains("truncatedOwnerProjects = unrepresentedOwnerProjects + partiallyTruncatedOwnerProjects",
            fsharpOwnerCoverage);
        Assert.Contains("incidences per affected file", fsharpOwnerCoverage);
        Assert.Contains("not distinct project identities", fsharpOwnerCoverage);
        string fsharpSemanticConfidence = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-semantic-confidence-authority")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.80", fsharpSemanticConfidence);
        Assert.Contains("selected context", fsharpSemanticConfidence);
        Assert.Contains("disclosed assumptions", fsharpSemanticConfidence);
        Assert.Contains("immutable-evidence provenance", fsharpSemanticConfidence);
        Assert.Contains("substituted", fsharpSemanticConfidence);
        Assert.Contains("errored", fsharpSemanticConfidence);
        Assert.Contains("removed from the context", fsharpSemanticConfidence);
        Assert.Contains("not yet classified", fsharpSemanticConfidence);
        Assert.Contains("partial reasons remain visible", fsharpSemanticConfidence);
        Assert.Contains("renamed fsharpSemanticTools", fsharpSemanticConfidence);
        string fsharpSnapshot = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-semantic-snapshot")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("exact-path opened-handle verification", fsharpSnapshot);
        Assert.Contains("Windows and Linux", fsharpSnapshot);
        Assert.Contains("macOS since v0.12.56", fsharpSnapshot);
        string fsharpPackageClosure = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-semantic-package-asset-closure")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.56", fsharpPackageClosure);
        Assert.Contains("Directory.Packages.props", fsharpPackageClosure);
        Assert.Contains("PackageVersion", fsharpPackageClosure);
        Assert.Contains("since v0.12.67", fsharpPackageClosure);
        Assert.Contains("exact case-insensitive explicit direct identity set",
            fsharpPackageClosure);
        Assert.Contains("SDK auto-referenced packages", fsharpPackageClosure);
        Assert.Contains("project.assets.json", fsharpPackageClosure);
        Assert.Contains("transitive compile assets", fsharpPackageClosure);
        Assert.Contains("without restore or MSBuild execution", fsharpPackageClosure);
        string csharpCentralPackages = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "csharp-semantic-central-package-management")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.57", csharpCentralPackages);
        Assert.Contains("Directory.Packages.props", csharpCentralPackages);
        Assert.Contains("PackageVersion", csharpCentralPackages);
        Assert.Contains("exact global-cache directories", csharpCentralPackages);
        Assert.Contains("warm model identity", csharpCentralPackages);
        Assert.Contains("without guessing or executing restore", csharpCentralPackages);
        string csharpCentralPackageProperties = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "csharp-semantic-central-package-property-expansion")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.58", csharpCentralPackageProperties);
        Assert.Contains("PackageVersion", csharpCentralPackageProperties);
        Assert.Contains("local-property expansion", csharpCentralPackageProperties);
        Assert.Contains("assignment-time", csharpCentralPackageProperties);
        Assert.Contains("later imported property authority", csharpCentralPackageProperties);
        Assert.Contains("exceeded limits", csharpCentralPackageProperties);
        string sharedDaemon = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "shared-mcp-daemon")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.59", sharedDaemon);
        Assert.Contains("physical-worktree", sharedDaemon);
        Assert.Contains("named pipe or Unix socket", sharedDaemon);
        Assert.Contains("typed unavailable", sharedDaemon);
        Assert.Contains("client-fair", sharedDaemon);
        string sharedDaemonDefault = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "shared-mcp-daemon-default")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("since v0.12.60", sharedDaemonDefault);
        Assert.Contains("no flag or environment opt-in", sharedDaemonDefault);
        Assert.Contains("compatibility alias", sharedDaemonDefault);
        Assert.Contains("diagnostics only", sharedDaemonDefault);
        Assert.Contains("never fall back", sharedDaemonDefault);
        string stableUnixDiscovery = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "shared-daemon-stable-unix-discovery")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.61", stableUnixDiscovery);
        Assert.Contains("owner-verified /tmp", stableUnixDiscovery);
        Assert.Contains("XDG_RUNTIME_DIR/TMPDIR", stableUnixDiscovery);
        Assert.Contains("legacy environment-derived endpoints", stableUnixDiscovery);
        Assert.Contains("frozen-preamble retirement", stableUnixDiscovery);
        Assert.Contains("explicit remediation", stableUnixDiscovery);
        string connectionDispatch = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "shared-daemon-connection-dispatch")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.62", connectionDispatch);
        Assert.Contains("independent per-connection dispatch", connectionDispatch);
        Assert.Contains("frozen-preamble handshakes", connectionDispatch);
        Assert.Contains("daemon_handshake_timeout", connectionDispatch);
        Assert.Contains("retirement or restart cancellation", connectionDispatch);
        string startupDiagnostics = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "shared-daemon-startup-diagnostics")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.63", startupDiagnostics);
        Assert.Contains("bounded private ready/refusal report", startupDiagnostics);
        Assert.Contains("owner-checked typed failures", startupDiagnostics);
        Assert.Contains("without respawn storms", startupDiagnostics);
        Assert.Contains("stale or corrupt advisory state", startupDiagnostics);
        Assert.Contains("without exposing daemon controls", startupDiagnostics);
        string portalReadOnly = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "operations-portal-jsonl-readonly")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("without opening SQLite", portalReadOnly);
        string portalLauncher = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "operations-portal-mcp-launcher")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.49", portalLauncher);
        Assert.Contains("open_operations_portal", portalLauncher);
        Assert.Contains("separately packaged", portalLauncher);
        Assert.Contains("away from MCP stdout", portalLauncher);
        Assert.Contains("without opening a browser", portalLauncher);
        string portalQueryable = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "operations-portal-queryable-evidence")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.50", portalQueryable);
        Assert.Contains("current observed index-file generation", portalQueryable);
        Assert.Contains("connected Phoenix process", portalQueryable);
        Assert.Contains("successful retained query", portalQueryable);
        Assert.Contains("invalidates old query evidence", portalQueryable);
        Assert.Contains("freshness remains unknown", portalQueryable);
        Assert.Contains("count stays stable", portalQueryable);
        string portalSemanticSummary = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "operations-portal-deterministic-semantic-summary")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.51", portalSemanticSummary);
        Assert.Contains("independently of instance ordering", portalSemanticSummary);
        Assert.Contains("unanimous", portalSemanticSummary);
        Assert.Contains("differing states report mixed", portalSemanticSummary);
        string argumentErrors = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "mcp-structured-argument-errors")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.50", argumentErrors);
        Assert.Contains("structured bad_request", argumentErrors);
        Assert.Contains("expected type", argumentErrors);
        string selectorIncompatibility = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "semantic-selector-incompatibility-errors")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("structured semantic-selector incompatibility errors",
            selectorIncompatibility);
        Assert.Contains("incompatible_mode", selectorIncompatibility);
        Assert.Contains("incompatible_filter", selectorIncompatibility);
        Assert.Contains("semantic_required", selectorIncompatibility);
        string retryGuidance = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "implementations-semantic-retry-guidance")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.50", retryGuidance);
        Assert.Contains("machine-readable semantic cause", retryGuidance);
        Assert.Contains("retryRecommended", retryGuidance);
        Assert.Contains("does not retry automatically", retryGuidance);
        string coldStartRetry = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "cold-start-retry-contract")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("typed cold-start retry contract", coldStartRetry);
        Assert.Contains("access-mode-aware", coldStartRetry);
        Assert.Contains("server_capabilities.index.progress", coldStartRetry);
        Assert.Contains("index.error", coldStartRetry);
        Assert.Contains("index.state", coldStartRetry);
        Assert.Contains("context_pack", coldStartRetry);
        Assert.Contains("larger timeoutMs", coldStartRetry);
        Assert.Contains("documented family maximum", coldStartRetry);
        Assert.Contains("unchanged at that maximum", coldStartRetry);
        string coldStartTiming = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "semantic-cold-start-phase-timing")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("timing.semanticColdStart", coldStartTiming);
        Assert.Contains("present only when the call enters the C# semantic pipeline",
            coldStartTiming);
        Assert.Contains("F# semantic navigation", coldStartTiming);
        Assert.Contains("omit the field", coldStartTiming);
        Assert.Contains("integer-millisecond", coldStartTiming);
        Assert.Contains("semanticOp", coldStartTiming);
        string gcPauseAttribution = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "references-gc-pause-attribution")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.79", gcPauseAttribution);
        Assert.Contains("queryStages.compilationPreparation.gcPauseMs", gcPauseAttribution);
        Assert.Contains("whole-millisecond process-wide GC pause time", gcPauseAttribution);
        Assert.Contains("overlapping pauses", gcPauseAttribution);
        Assert.Contains("below 1 ms", gcPauseAttribution);
        Assert.Contains("omission", gcPauseAttribution);
        string fsharpReferences = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-references-same-project")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.81", fsharpReferences);
        Assert.Contains("selected physical .fsproj + TFM", fsharpReferences);
        Assert.Contains("compiler-bound non-definition uses", fsharpReferences);
        Assert.Contains("pinned source snapshot", fsharpReferences);
        Assert.Contains("workspace lower bound", fsharpReferences);
        Assert.Contains("dependent projects are not scanned", fsharpReferences);
        Assert.DoesNotContain("exact", fsharpReferences, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("indexed", fsharpReferences, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("heuristic", fsharpReferences, StringComparison.OrdinalIgnoreCase);
        string fsharpProjectReferenceClosure = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-semantic-project-reference-closure")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("v0.12.83", fsharpProjectReferenceClosure);
        Assert.Contains("exact selected TFM", fsharpProjectReferenceClosure);
        Assert.Contains("literal physical project paths", fsharpProjectReferenceClosure);
        Assert.Contains("in-memory referenced-project options", fsharpProjectReferenceClosure);
        Assert.Contains("without emitted or last-built DLLs", fsharpProjectReferenceClosure);
        Assert.Contains("flat transitive SDK default", fsharpProjectReferenceClosure);
        Assert.Contains("DisableTransitiveProjectReferences=true",
            fsharpProjectReferenceClosure);
        Assert.Contains("legacy-style projects remain direct-only",
            fsharpProjectReferenceClosure);
        Assert.Contains("child compiler errors retain their source paths",
            fsharpProjectReferenceClosure);
        Assert.Contains("fsharp_semantic_diagnostics_present",
            fsharpProjectReferenceClosure);
        Assert.Contains("declarationsFromProjectReferenceClosureCount",
            fsharpProjectReferenceClosure);
        Assert.Contains("declarationsOutsideSelectedProjectCount retains its not-returned meaning",
            fsharpProjectReferenceClosure);
        Assert.Contains("references still count only the selected root",
            fsharpProjectReferenceClosure);
        Assert.Contains("same-assembly", fsharpProjectReferenceClosure);
        string fsharpBoundary = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "fsharp-unsupported-language-boundary")
            .GetProperty("summary")
            .GetString()!;
        Assert.DoesNotContain("F# references", fsharpBoundary, StringComparison.Ordinal);
        Assert.Contains("callers/callees", fsharpBoundary);
        Assert.Contains("implementations", fsharpBoundary);
        Assert.Contains("hierarchy", fsharpBoundary);
        Assert.Contains("never retries automatically", coldStartRetry);
        Assert.Contains("open_operations_portal",
            json.GetProperty("tools").EnumerateArray().Select(tool => tool.GetString()));
        string refreshRecovery = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "refresh-recovery-self-heal")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("5/10/30/60-second capped backoff", refreshRecovery);
        Assert.Contains("timer-initiated recovery sweeps make one capture attempt each",
            refreshRecovery);
        Assert.Contains("re-resolve pending Git baselines", refreshRecovery);
        Assert.Contains("remain honestly stale until success", refreshRecovery);
        string gitAwareness = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString() == "git-awareness")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("same-commit attachment changes", gitAwareness);
        Assert.Contains("serialized HEAD snapshot acquisition", gitAwareness);
        Assert.Contains("ordered recovery publication", gitAwareness);
        Assert.Contains("rebuild-generation retirement", gitAwareness);
        Assert.Contains("rapid inverse transitions preserve final rows",
            gitAwareness);
        Assert.Contains("unavailable recovery snapshots force older queued Git tuples to revalidate",
            gitAwareness);
        Assert.Contains("at or after the latest unavailable sample allowed to publish ready",
            gitAwareness);
        Assert.Contains(
            "full rebuilds reject ordered recovery publications sampled for the replaced database",
            gitAwareness);
        string refreshInputRetry = Assert.Single(
                json.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "refresh-input-retry")
            .GetProperty("summary")
            .GetString()!;
        Assert.Contains("initial or event-driven serialized requests", refreshInputRetry);
        Assert.Contains("timer-initiated stale-index recovery", refreshInputRetry);
        Assert.Contains("search-symbol-malformed-query", ids);
        Assert.DoesNotContain("index-follower-liveness-fail-closed", ids);
        string semanticBudget = Assert.Single(json.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("id").GetString() == "semantic-large-repo-budget")
            .GetProperty("summary").GetString()!;
        Assert.Contains("default all candidates", semanticBudget);
        Assert.Contains("positive maxProjects bounds", semanticBudget);
        string completeness = Assert.Single(json.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("id").GetString() ==
                       "semantic-candidate-completeness-over-accounting")
            .GetProperty("summary").GetString()!;
        Assert.Contains("byte/managed-heap pressure retention", completeness);
        Assert.DoesNotContain("resident-count eviction", completeness);

        string arityResolution = Assert.Single(json.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("id").GetString() == "generic-arity-resolution")
            .GetProperty("summary").GetString()!;
        Assert.Contains("implementations/type_hierarchy select by arity or symbolId", arityResolution);
        Assert.Contains("mixed-arity names refuse", arityResolution);
        Assert.Contains("syntax fallback is arity-exact", arityResolution);

        string malformedQuery = Assert.Single(json.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("id").GetString() == "search-symbol-malformed-query")
            .GetProperty("summary").GetString()!;
        Assert.Contains("malformed_query", malformedQuery);
        Assert.Contains("select:", malformedQuery);
    }

    [Fact]
    public void CapabilitiesAdvertiseV0111ReviewContractsAsSingularFeatures()
    {
        var json = Parse(NavigationTools.ServerCapabilitiesUncompactedForTest(_manager.Health()));
        var features = json.GetProperty("features").EnumerateArray().ToList();
        var ids = features.Select(feature => feature.GetProperty("id").GetString()!).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("review-git-safety", ids);

        string Summary(string id) => Assert.Single(features,
            feature => feature.GetProperty("id").GetString() == id)
            .GetProperty("summary").GetString()!;

        string capabilityBudget = Summary("capabilities-hard-budget");
        Assert.Contains("UTF-8 hardBytes", capabilityBudget);
        Assert.Contains("*Truncated/*Bytes", capabilityBudget);
        Assert.Contains("featuresCompacted/featureSummariesReturned", capabilityBudget);
        Assert.Contains("every singular feature id", capabilityBudget);

        string stdin = Summary("review-git-stdin-transport");
        Assert.Contains("cat-file --batch-check", stdin);
        Assert.Contains("reads base blobs with cat-file --batch", stdin);
        Assert.Contains("accepted dynamic ref names and paths travel on stdin", stdin);
        Assert.Contains("validated 4-64 ASCII-hex prefixes", stdin);
        Assert.Contains("rev-parse --disambiguate=<hex>", stdin);
        Assert.Contains(".cmd/.bat", stdin);

        string refResolution = Summary("review-ref-resolution");
        Assert.Contains("Hex-only branch/tag names", refResolution);
        Assert.Contains("Git-validated and peeled", refResolution);
        Assert.Contains("full commits", refResolution);
        Assert.Contains("repository-format-width objects", refResolution);
        Assert.Contains("distinct short-hex ambiguity is refused", refResolution);

        string diff = Summary("review-diff-determinism");
        Assert.Contains("--raw -z --patch", diff);
        Assert.Contains("ordinal/C-quoted path identity", diff);
        Assert.Contains("binary/mode/empty/type", diff);
        Assert.Contains("old/new hunk-coordinate overflow fails closed as malformed", diff);
        Assert.Contains("stage-only unmerged gitlinks report unmerged", diff);
        Assert.Contains("process/status failures never become partial success", diff);

        string filters = Summary("review-content-filter-refusal");
        Assert.Contains("clean/process", filters);
        Assert.Contains("without executing", filters);
        Assert.Contains("git_filter_unsafe", filters);
        Assert.DoesNotContain("* !filter", filters);

        string filterOverlay = Summary("review-content-filter-overlay");
        Assert.Contains("highest-precedence info/attributes overlay", filterOverlay);
        Assert.Contains("* !filter", filterOverlay);
        Assert.Contains("after preflight", filterOverlay);
        Assert.Contains("newly introduced driver", filterOverlay);

        string submodules = Summary("review-submodule-coverage");
        Assert.Contains("coverage.submoduleWorktrees", submodules);
        Assert.Contains("review.submodule_worktrees_excluded", submodules);
        Assert.Contains("changedSubmoduleLinks", submodules);

        string nestedRepositories = Summary("review-untracked-repository-coverage");
        Assert.Contains("coverage.untrackedRepositories", nestedRepositories);
        Assert.Contains("review.untracked_repositories_excluded", nestedRepositories);
        Assert.Contains("child-local helpers", nestedRepositories);

        string linkedUntracked = Summary("review-untracked-link-coverage");
        Assert.Contains("coverage.untrackedLinks", linkedUntracked);
        Assert.Contains("review.untracked_links_excluded", linkedUntracked);
        Assert.Contains("before hashing", linkedUntracked);

        string layered = Summary("review-layered-change-refusal");
        Assert.Contains("staged and unstaged", layered);
        Assert.Contains("git_layered_changes", layered);
        Assert.Contains("both byte layers", layered);

        string snapshot = Summary("review-snapshot-consistency");
        Assert.Contains("exact raw patch bytes", snapshot);
        Assert.Contains("typed staged/unstaged/unmerged/untracked manifests", snapshot);
        Assert.Contains("snapshot_changed", snapshot);
        Assert.Contains("git_worktree_changed", snapshot);
        Assert.Contains("no partial result", snapshot);
        Assert.Contains("different worktree epochs", snapshot);
        Assert.Contains("symlink payloads", snapshot);
        Assert.Contains("gitlinks", snapshot);
        Assert.Contains("modes", snapshot);
        Assert.Contains("tracked bytes", snapshot);

        string liveEvidence = Summary("review-live-evidence-revalidation");
        Assert.Contains("safe existence classification", liveEvidence);
        Assert.Contains("contradictory repeated observations", liveEvidence);
        Assert.Contains("bounded untracked move-candidate bytes actually consumed", liveEvidence);
        Assert.Contains("fails closed", liveEvidence);

        string conversionOperators = Summary("csharp-conversion-operator-indexing");
        Assert.Contains("v0.12.46", conversionOperators);
        Assert.Contains("schema v21", conversionOperators);
        Assert.Contains("implicit and explicit C# conversion declarations", conversionOperators);
        Assert.Contains("target-bearing names", conversionOperators);
        Assert.Contains("canonical declaration keys", conversionOperators);
        Assert.Contains("modifiers, source order, and parent links", conversionOperators);

        string conversionHandles = Summary("csharp-conversion-semantic-handles");
        Assert.Contains("v0.12.48", conversionHandles);
        Assert.Contains("schema v24", conversionHandles);
        Assert.Contains("uncapped canonical declaration keys", conversionHandles);
        Assert.Contains("existing per-file content hash", conversionHandles);
        Assert.Contains("deterministic syntax ordinal among declarations on the same source line", conversionHandles);
        Assert.Contains("distinguishing same-file twins", conversionHandles);
        Assert.Contains("without follow-up queries", conversionHandles);
        Assert.Contains("invalidating the file epoch conservatively", conversionHandles);
        Assert.Contains("without a per-symbol context digest", conversionHandles);
        Assert.Contains("rejecting older identities", conversionHandles);

        string conversionUsages = Summary("csharp-conversion-usage-enumeration");
        Assert.Contains("v0.12.47", conversionUsages);
        Assert.Contains("compiler-bound", conversionUsages);
        Assert.Contains("implicitConversion", conversionUsages);
        Assert.Contains("explicitConversion", conversionUsages);
        Assert.Contains("checkedConversion", conversionUsages);
        Assert.Contains("stacked, nullable-tuple", conversionUsages);
        Assert.Contains("full C# compound-assignment", conversionUsages);
        Assert.Contains("primary-constructor", conversionUsages);
        Assert.Contains("foreach", conversionUsages);
        Assert.Contains("deconstruction", conversionUsages);
        Assert.Contains("interface-dispatch carriers", conversionUsages);
        Assert.Contains("exact zero", conversionUsages);
        string foreachConversionKind = Summary("csharp-foreach-conversion-operator-kind");
        Assert.Contains("v0.12.68", foreachConversionKind);
        Assert.Contains("explicit, implicit, and checked operators", foreachConversionKind);
        Assert.Contains("retain their distinct usage classification", foreachConversionKind);

        string deterministicSamples = Summary("references-deterministic-samples");
        Assert.Contains("unique by path, line, and usage kind", deterministicSamples);
        Assert.Contains("multiple source spans with the same path/line/kind share one sample",
            deterministicSamples);
        Assert.Contains("canonical per-group project spelling", deterministicSamples);
        Assert.Contains("sampleCoverage", deterministicSamples);
        Assert.Contains("post-response-budget", deterministicSamples);
        Assert.Contains("separate deadline, other text-loss, or byte-budget causes",
            deterministicSamples);
        Assert.Contains("v0.12.68", deterministicSamples);
        Assert.Contains("completed reference scans", deterministicSamples);
        Assert.Contains("ordinal path, line, and usage-kind order", deterministicSamples);
        Assert.Contains("equal-count project groups in ordinal project order",
            deterministicSamples);
        Assert.Contains("read source text only for that final bounded set", deterministicSamples);
        Assert.Contains("queryStages.samplesRead meaning", deterministicSamples);
        Assert.Contains("public sample cap remain unchanged", deterministicSamples);

        string stableNoteIds = Summary("stable-note-ids");
        Assert.Contains("references.sampleCoverage.reasons[].noteId", stableNoteIds);
        Assert.Contains("samples_deadline | samples_trimmed | samples_byte_budget",
            stableNoteIds);

        string deadlineHonesty = Summary("deadline-honesty");
        Assert.Contains("project+path+source-span+kind", deadlineHonesty);
        Assert.Contains("distinct same-line operations", deadlineHonesty);

        string operatorHandles = Summary("csharp-operator-semantic-handles");
        Assert.Contains("v0.12.47", operatorHandles);
        Assert.Contains("regular and conversion operator idx handles", operatorHandles);
        Assert.Contains("canonical syntax declaration keys", operatorHandles);
        Assert.Contains("checked and explicit-interface", operatorHandles);
        Assert.Contains("indexed definition retains the resolved row", operatorHandles);
        Assert.Contains("failed-auto references fail closed", operatorHandles);
        Assert.Contains("implementations/type_hierarchy reject operator handles", operatorHandles);

        string operatorAccessibility = Summary(
            "csharp-explicit-interface-operator-accessibility");
        Assert.Contains("v0.12.47", operatorAccessibility);
        Assert.Contains("schema v23", operatorAccessibility);
        Assert.Contains("explicit-interface regular operators as private",
            operatorAccessibility);
        Assert.Contains("review_pack", operatorAccessibility);

        string indivisibleIdentity = Summary(
            "semantic-indivisible-identity-completeness");
        Assert.Contains("v0.12.47", indivisibleIdentity);
        Assert.Contains("complete indivisible compiler symbol identity",
            indivisibleIdentity);
        Assert.Contains("remove optional declaration-site lists", indivisibleIdentity);
        Assert.Contains("truthful declaration totals", indivisibleIdentity);
        Assert.Contains("semantic.declaration_sites_budget", indivisibleIdentity);
        Assert.Contains("responseBudget", indivisibleIdentity);
        Assert.Contains("serializedBytes", indivisibleIdentity);
        Assert.Contains("indivisible_semantic_identity", indivisibleIdentity);
        Assert.Contains("without identity truncation or rejection", indivisibleIdentity);

        string candidateFileCap = Summary("references-candidate-file-cap-disclosure");
        Assert.Contains("v0.12.46", candidateFileCap);
        Assert.Contains("existing caller-selected maxFiles", candidateFileCap);
        Assert.Contains("coverage", candidateFileCap);
        Assert.Contains("candidate_file_cap", candidateFileCap);
        Assert.Contains("references.candidate_file_cap", candidateFileCap);
        Assert.Contains("lower-bound totals", candidateFileCap);

        string launcher = Summary("review-git-launcher-isolation");
        Assert.Contains("canonical absolute paths", launcher);
        Assert.Contains("missing or non-directory working directory fails before spawn", launcher);
        Assert.Contains("protocol.allow=never", Summary("review-git-transport-isolation"));
        string gitEnvironment = Summary("review-git-environment-isolation");
        Assert.Contains("clears inherited repository/object/index selectors", gitEnvironment);
        Assert.Contains("GIT_DIR", gitEnvironment);
        Assert.Contains("GIT_ALTERNATE_OBJECT_DIRECTORIES", gitEnvironment);
        Assert.Contains("reinstates only validated paths", gitEnvironment);
        Assert.Contains("actual toplevel", Summary("review-workspace-path-domain"));
        string unixPaths = Summary("unix-git-path-identity");
        Assert.Contains("literal backslashes", unixPaths);
        Assert.Contains("root-level leading literal backslash", unixPaths);
        Assert.Contains("scan, watcher, refresh, commit reconciliation", unixPaths);
        Assert.Contains("Windows still treats backslash", unixPaths);
        string worktreePaths = Summary("worktree-workspace-path-domain");
        Assert.Contains("NUL-framed porcelain roots", worktreePaths);
        Assert.Contains("repository-subtree prefix", worktreePaths);
        Assert.Contains("host-sensitive identity preserves case-distinct Git paths", worktreePaths);
        Assert.Contains("invalid caller roots return structured errors", worktreePaths);
        Assert.Contains("UntrackedFiles", Summary("review-dirt-provenance"));
        Assert.Contains("symbolsCoverage", Summary("review-budget-coverage"));
        Assert.Contains("reduce every optional list to zero", Summary("review-budget-coverage"));
        Assert.Contains("old and new coordinates", Summary("review-two-sided-diff-ranges"));
        Assert.Contains("formerSymbols", Summary("review-former-symbol-evidence"));
        Assert.Contains("declarationExclusionBudgetHit",
            Summary("review-reference-declaration-budget"));
        Assert.Contains("review.reference_declaration_budget",
            Summary("review-reference-declaration-budget"));
        string declarationIdentity = Summary("review-declaration-identity");
        Assert.Contains("v0.11.5", declarationIdentity);
        Assert.Contains("index schema v14", declarationIdentity);
        Assert.Contains("generic arity", declarationIdentity);
        Assert.Contains("checked-vs-unchecked operators", declarationIdentity);
        Assert.Contains("explicit-interface operator qualifiers", declarationIdentity);
        Assert.Contains("tuple labels are omitted", declarationIdentity);
        Assert.Contains("tuple types and nesting remain identity-bearing", declarationIdentity);
        string exactMoves = Summary("review-exact-move-evidence");
        Assert.Contains("movedFiles", exactMoves);
        Assert.Contains("size/count-bounded", exactMoves);
        Assert.Contains("anchored no-follow", exactMoves);
        Assert.Contains("oversized or excess candidates conservatively remain uncorrelated",
            exactMoves);
        string batchOutlinePaths = Summary("batch-outline-json-array-paths");
        Assert.Contains("shared 64 KiB exact workspace-relative grammar", batchOutlinePaths);
        Assert.Contains("control-character", batchOutlinePaths);
        string normalizedMoves = Summary("review-normalized-move-evidence");
        Assert.Contains("normalized_blob", normalizedMoves);
        Assert.Contains("never exact_blob", normalizedMoves);
        Assert.Contains("each target is claimed at most once", normalizedMoves);
        Assert.Contains("ambiguous candidates remain uncorrelated", normalizedMoves);
        Assert.Contains("review.base_blob_unavailable", Summary("review-base-blob-recovery-honesty"));
        Assert.Contains("namespaceAnalysisCoverage", Summary("review-namespace-analysis-budget"));
        Assert.Contains("projectOwnershipFallbackCoverage",
            Summary("review-project-shape-budget"));
        string projectGlobBudget = Summary("review-project-glob-budget");
        Assert.Contains("Iterative project-ownership glob budget", projectGlobBudget);
        Assert.Contains("default-SDK checks", projectGlobBudget);
        Assert.Contains("globBudgetHit", projectGlobBudget);
        Assert.Contains("review.project_glob_budget", projectGlobBudget);
        Assert.Contains("segment, operation, or deadline exhaustion", projectGlobBudget);
        Assert.Contains("fail proof closed", projectGlobBudget);
        Assert.Contains("evaluationIncomplete",
            Summary("review-project-shape-completeness"));
        Assert.Contains("review.project_shape_incomplete",
            Summary("review-project-shape-completeness"));
        string projectFiles = Summary("review-project-file-guidance");
        Assert.Contains("changedProjectFiles reports every modified or deleted project, build, and solution input",
            projectFiles);
        Assert.Contains("review.project_files_changed counts only authoritative", projectFiles);
        Assert.Contains(".csproj/.fsproj/.csproj.user/.fsproj.user/.shproj/.proj/.projitems/.props/.targets",
            projectFiles);
        Assert.Contains("Directory.Build.rsp/MSBuild.rsp", projectFiles);
        string solutionMetadata = Summary("review-solution-metadata-guidance");
        Assert.Contains(".sln/.slnx/.slnf", solutionMetadata);
        Assert.Contains("changedProjectFiles", solutionMetadata);
        Assert.Contains("changedProjectFilesCoverage", solutionMetadata);
        Assert.Contains("authoritative and solutionMetadata counts", solutionMetadata);
        Assert.Contains("review.solution_files_changed", solutionMetadata);
        Assert.Contains("never invalidates exact-move, declaration-survivor, ownership, dependency, build, or symbol-resolution proof",
            solutionMetadata);
        string deletedSolutionMetadata = Summary("review-deleted-solution-metadata-counts");
        Assert.Contains("v0.12.68", deletedSolutionMetadata);
        Assert.Contains(".sln/.slnx/.slnf", deletedSolutionMetadata);
        Assert.Contains("changedFiles totals", deletedSolutionMetadata);
        Assert.Contains("source-deletion expansion", deletedSolutionMetadata);
        Assert.Contains("review.deleted_solution_metadata_scope", deletedSolutionMetadata);
        string fsharpSchemaRebuild = Summary("index-schema-29-fsharp-output-rebuild");
        Assert.Contains("v0.12.68", fsharpSchemaRebuild);
        Assert.Contains("schema v29", fsharpSchemaRebuild);
        Assert.Contains("schema-28 indexes", fsharpSchemaRebuild);
        Assert.Contains("stored symbol and orphan-classification drift", fsharpSchemaRebuild);
        Assert.Contains("ordinary reuse", fsharpSchemaRebuild);
        string startupRebuildEvidence = Summary("index-startup-rebuild-evidence");
        Assert.Contains("v0.12.68", startupRebuildEvidence);
        Assert.Contains("startupBuildReason", startupRebuildEvidence);
        Assert.Contains("startupPriorSchema", startupRebuildEvidence);
        Assert.Contains("existing-index migration or recovery", startupRebuildEvidence);
        string friendAssembly = Summary("friend-assembly-semantics");
        Assert.Contains("since v0.12.69", friendAssembly);
        Assert.Contains("references census uses its selected consumer scan", friendAssembly);
        Assert.Contains("candidates with no compiler-bound result site", friendAssembly);
        string buildDispatchIsolation = Summary("index-build-request-dispatch-isolation");
        Assert.Contains("v0.12.69", buildDispatchIsolation);
        Assert.Contains("dedicated long-running execution lanes", buildDispatchIsolation);
        Assert.Contains("server_capabilities dispatch", buildDispatchIsolation);
        Assert.Contains("without changing parser concurrency, queue capacity, publication, or authority",
            buildDispatchIsolation);
        string packageRootAuthority = Summary("semantic-package-root-override-authority");
        Assert.Contains("v0.12.71", packageRootAuthority);
        Assert.Contains("exclusive external NuGet authority", packageRootAuthority);
        Assert.Contains("ordinary user-profile global cache remains supported",
            packageRootAuthority);
        string packageInputEvidence = Summary("semantic-package-input-evidence");
        Assert.Contains("resolvedPackageDllCount", packageInputEvidence);
        Assert.Contains("frameworkRefsAvailable", packageInputEvidence);
        Assert.Contains("v0.12.71", packageInputEvidence);
        Assert.Contains("successfully admitted", packageInputEvidence);
        string frameworkOverride = Summary("semantic-framework-reference-override-authority");
        Assert.Contains("v0.12.71", frameworkOverride);
        Assert.Contains("authoritative", frameworkOverride);
        Assert.Contains("mscorlib, System, and System.Core", frameworkOverride);
        string frameworkSource = Summary("semantic-framework-reference-source-evidence");
        Assert.Contains("v0.12.71", frameworkSource);
        Assert.Contains("frameworkRefsSource", frameworkSource);
        Assert.Contains("exact", frameworkSource);
        string defaultBaseline = Summary("review-default-baseline-honesty");
        Assert.Contains("bounded git_index_baseline_unavailable", defaultBaseline);
        Assert.Contains("refresh_index", defaultBaseline);
        Assert.Contains("explicit baseRef", defaultBaseline);
        Assert.Contains("caller-supplied invalid refs remain bad_request", defaultBaseline);
        Assert.Contains("unmappedChanges", Summary("review-unmapped-change-coverage"));
        string reviewEpoch = Summary("review-index-epoch-consistency");
        Assert.Contains("one stable SQLite read epoch", reviewEpoch);
        Assert.Contains("cannot mix old symbols with new ownership or health evidence", reviewEpoch);
        string perHunk = Summary("review-per-hunk-type-mapping");
        Assert.Contains("per old/new hunk", perHunk);
        Assert.Contains("type-header edit remains reviewable", perHunk);
        string destinationIsolation = Summary("worktree-index-destination-isolation");
        Assert.Contains("private staging", destinationIsolation);
        Assert.Contains("anchored no-follow destination", destinationIsolation);
        Assert.Contains("without touching their targets", destinationIsolation);
        Assert.Contains("rollback-journal", destinationIsolation);
        string writeAuthority = Summary("index-write-destination-authority");
        Assert.Contains("Windows pins the full no-delete-share chain", writeAuthority);
        Assert.Contains("Linux writes through a held directory fd", writeAuthority);
        Assert.Contains("macOS performs startup and per-open identity revalidation", writeAuthority);
        string platformPolicy = Summary("worktree-index-platform-policy");
        Assert.Contains("Windows uses targeted", platformPolicy);
        Assert.Contains("Linux uses an anchored full sweep", platformPolicy);
        Assert.Contains("usedFullSweep=true", platformPolicy);
        Assert.Contains("macOS returns unsupported_platform", platformPolicy);
        string worktreeIndexes = Summary("worktree-indexes");
        Assert.Contains("On Windows and Linux", worktreeIndexes);
        // "macOS returns unsupported_platform" is worktree-index-platform-policy's OWNED token
        // (one grep-able token, one id — the singular-features loop below enforces it); this
        // envelope-level summary states the platform gap in its own words.
        Assert.Contains("macOS is unsupported for both operations", worktreeIndexes);
        string worktreeLease = Summary("worktree-index-lease");
        Assert.Contains("cross-process ownership lease", worktreeLease);
        Assert.Contains("worktree_index_locked", worktreeLease);
        string worktreeBudget = Summary("worktree-response-budget");
        Assert.Contains("trim every item to zero", worktreeBudget);
        Assert.Contains("UTF-8-bounds reflected paths/details", worktreeBudget);
        Assert.Contains("complete hardBytes envelope", worktreeBudget);

        string reviewPack = Summary("review-pack");
        Assert.Contains("ONE budget-bounded call", reviewPack);
        foreach (var (token, owner) in new[]
                 {
                     ("cat-file --batch-check", "review-git-stdin-transport"),
                     ("--raw -z --patch", "review-diff-determinism"),
                     ("git_filter_unsafe", "review-content-filter-refusal"),
                     ("* !filter", "review-content-filter-overlay"),
                     ("coverage.submoduleWorktrees", "review-submodule-coverage"),
                     ("coverage.untrackedRepositories", "review-untracked-repository-coverage"),
                     ("coverage.untrackedLinks", "review-untracked-link-coverage"),
                      ("git_layered_changes", "review-layered-change-refusal"),
                      ("git_worktree_changed", "review-snapshot-consistency"),
                      ("contradictory repeated observations",
                          "review-live-evidence-revalidation"),
                       ("protocol.allow=never", "review-git-transport-isolation"),
                       ("GIT_ALTERNATE_OBJECT_DIRECTORIES", "review-git-environment-isolation"),
                       ("literal backslashes", "unix-git-path-identity"),
                       ("NUL-framed porcelain roots", "worktree-workspace-path-domain"),
                       ("UntrackedFiles", "review-dirt-provenance"),
                      ("symbolsCoverage", "review-budget-coverage"),
                      ("old and new coordinates", "review-two-sided-diff-ranges"),
                      ("formerSymbols", "review-former-symbol-evidence"),
                      ("declarationExclusionBudgetHit", "review-reference-declaration-budget"),
                      ("tuple labels are omitted", "review-declaration-identity"),
                      ("movedFiles", "review-exact-move-evidence"),
                      ("normalized_blob", "review-normalized-move-evidence"),
                      ("review.base_blob_unavailable", "review-base-blob-recovery-honesty"),
                      ("namespaceAnalysisCoverage", "review-namespace-analysis-budget"),
                      ("review.project_shape_budget", "review-project-shape-budget"),
                      ("segment, operation, or deadline exhaustion", "review-project-glob-budget"),
                      ("evaluationIncomplete", "review-project-shape-completeness"),
                      ("review.project_files_changed", "review-project-file-guidance"),
                      ("review.solution_files_changed", "review-solution-metadata-guidance"),
                      ("Linux ARM64 ABI", "linux-arm64-anchored-authority"),
                      ("first bounded NUL", "portal-directory-entry-nul-decoding"),
                      ("away from MCP stdout", "operations-portal-mcp-launcher"),
                       ("structured bad_request", "mcp-structured-argument-errors"),
                       ("structured semantic-selector incompatibility errors",
                           "semantic-selector-incompatibility-errors"),
                       ("v0.12.50 transient implementations",
                          "implementations-semantic-retry-guidance"),
                       ("typed cold-start retry contract", "cold-start-retry-contract"),
                       ("timing.semanticColdStart", "semantic-cold-start-phase-timing"),
                       ("queryStages.compilationPreparation.gcPauseMs",
                           "references-gc-pause-attribution"),
                       ("immutable-evidence provenance",
                           "fsharp-semantic-confidence-authority"),
                       ("compiler-bound non-definition uses",
                           "fsharp-references-same-project"),
                       ("in-memory referenced-project options",
                           "fsharp-semantic-project-reference-closure"),
                       ("unrepresentedOwnerProjects",
                           "fsharp-parse-owner-coverage-breakdown"),
                      ("declaration-free C# files", "csharp-symbol-free-outline"),
                      ("target-bearing names", "csharp-conversion-operator-indexing"),
                      ("implicitConversion", "csharp-conversion-usage-enumeration"),
                      ("canonical syntax declaration keys",
                          "csharp-operator-semantic-handles"),
                      ("explicit-interface regular operators as private",
                          "csharp-explicit-interface-operator-accessibility"),
                      ("indivisible_semantic_identity",
                          "semantic-indivisible-identity-completeness"),
                      ("semantic.declaration_sites_budget",
                          "semantic-indivisible-identity-completeness"),
                      ("comma-bearing JSON paths", "refresh-review-json-array-paths"),
                      ("unsupportedLanguageFiles", "review-fsharp-file-coverage"),
                      ("git_index_baseline_unavailable", "review-default-baseline-honesty"),
                      ("unmappedChanges", "review-unmapped-change-coverage"),
                      ("one stable SQLite read epoch", "review-index-epoch-consistency"),
                      ("per old/new hunk", "review-per-hunk-type-mapping"),
                       ("anchored no-follow destination", "worktree-index-destination-isolation"),
                       ("held directory fd", "index-write-destination-authority"),
                       ("macOS returns unsupported_platform", "worktree-index-platform-policy"),
                      ("worktree_index_locked", "worktree-index-lease"),
                      ("complete hardBytes envelope", "worktree-response-budget"),
                 })
        {
            Assert.Contains(token, Summary(owner));
            JsonElement duplicate = features.FirstOrDefault(feature =>
                feature.GetProperty("id").GetString() != owner &&
                feature.TryGetProperty("summary", out JsonElement otherSummary) &&
                otherSummary.GetString()!.Contains(
                    token, StringComparison.Ordinal));
            string? duplicateId = duplicate.ValueKind == JsonValueKind.Undefined
                ? null
                : duplicate.GetProperty("id").GetString();
            Assert.True(duplicateId is null,
                $"Feature token '{token}' is also owned by '{duplicateId}'.");
        }
    }

    [Fact]
    public void CapabilitiesDynamicTextUsesExactUtf8BoundaryAndReportsTruncation()
    {
        static IndexHealth Health(string root) => new("ready", "11", "indexed", "refreshed",
            0, null, 123, root, "index.db");

        IndexHealth healthy = Health("C:/" + new string('r', 257));
        string uncompactedJson = NavigationTools.ServerCapabilitiesUncompactedForTest(healthy);
        int uncompactedMargin = Json.HardBudgetBytes - Json.Utf8Bytes(uncompactedJson);
        Assert.True(uncompactedMargin >= 2 * 1024,
            $"uncompacted capabilities retained only {uncompactedMargin} bytes of growth margin");
        string budgetedHealthyJson = NavigationTools.ServerCapabilitiesForTest(healthy);
        JsonElement compact = Parse(budgetedHealthyJson);
        Assert.False(compact.TryGetProperty("featuresCompacted", out _));
        Assert.False(compact.TryGetProperty("featureSummariesReturned", out _));
        Assert.Equal("ids", compact.GetProperty("featureSummaryMode").GetString());
        Assert.All(compact.GetProperty("features").EnumerateArray(), feature =>
            Assert.False(feature.TryGetProperty("summary", out _)));
        foreach (string retained in new[]
                 {
                     "languages", "budgets", "confidenceModel", "index",
                 })
        {
            Assert.True(compact.TryGetProperty(retained, out _), retained);
        }

        string detailedJson = NavigationTools.ServerCapabilitiesForTest(healthy, detail: true);
        JsonElement detailed = Parse(detailedJson);
        Assert.Equal("detail", detailed.GetProperty("featureSummaryMode").GetString());
        Assert.Contains(detailed.GetProperty("features").EnumerateArray(), feature =>
            feature.TryGetProperty("summary", out _));
        Assert.True(Json.Utf8Bytes(budgetedHealthyJson) < Json.Utf8Bytes(detailedJson));

        string exactRoot = new('é', NavigationTools.CapabilityDynamicTextBytes / 2);
        string exactJson = NavigationTools.ServerCapabilitiesForTest(Health(exactRoot));
        JsonElement exact = Parse(exactJson);
        Assert.True(Json.Utf8Bytes(exactJson) <= Json.HardBudgetBytes);
        JsonElement exactIndex = exact.GetProperty("index");
        Assert.Equal(exactRoot, exactIndex.GetProperty("workspaceRoot").GetString());
        Assert.False(exactIndex.TryGetProperty("workspaceRootTruncated", out _));
        Assert.False(exactIndex.TryGetProperty("workspaceRootBytes", out _));

        string overRoot = exactRoot + "é";
        string overJson = NavigationTools.ServerCapabilitiesForTest(Health(overRoot));
        JsonElement over = Parse(overJson);
        Assert.True(Json.Utf8Bytes(overJson) <= Json.HardBudgetBytes);
        JsonElement overIndex = over.GetProperty("index");
        Assert.Equal(exactRoot, overIndex.GetProperty("workspaceRoot").GetString());
        Assert.True(overIndex.GetProperty("workspaceRootTruncated").GetBoolean());
        Assert.Equal(Json.Utf8Bytes(overRoot),
            overIndex.GetProperty("workspaceRootBytes").GetInt32());
    }

    [Fact]
    public void CapabilitiesKeepEveryFeatureIdWithinBudgetForLongHealthStates()
    {
        static List<string> FeatureIds(JsonElement response) => response.GetProperty("features")
            .EnumerateArray().Select(feature => feature.GetProperty("id").GetString()!)
            .ToList();

        var baselineHealth = new IndexHealth("ready", "11", "indexed", "refreshed", 0,
            null, 123, "C:/workspace", "index.db");
        List<string> expectedIds = FeatureIds(Parse(
            NavigationTools.ServerCapabilitiesForTest(baselineHealth)));

        // Extended-length Windows-style path: every component is legal and the total remains
        // below the platform's 32K extended-path ceiling.
        string longRoot = @"\\?\C:\" + string.Join("\\",
            Enumerable.Repeat(new string('r', 100), 300));
        string longError = new('e', 30_000);
        string longPhase = new('p', 30_000);
        var states = new[]
        {
            new IndexHealth("ready", "11", "indexed", "refreshed", 0, null, 123,
                longRoot, "index.db"),
            new IndexHealth("building", "11", "indexed", "refreshed", 4, longError, 123,
                longRoot, "index.db", Progress: new IndexProgress(longPhase, 321, 999, 12_345)),
            new IndexHealth("failed", "11", "indexed", "refreshed", 0, longError, 123,
                longRoot, "index.db"),
        };

        foreach (IndexHealth health in states)
        {
            string json = NavigationTools.ServerCapabilitiesForTest(health);
            Assert.True(Json.Utf8Bytes(json) <= Json.HardBudgetBytes,
                $"{health.State} capabilities used {Json.Utf8Bytes(json)} bytes");
            JsonElement response = Parse(json);
            Assert.Equal(expectedIds, FeatureIds(response));
            JsonElement index = response.GetProperty("index");
            Assert.True(index.GetProperty("workspaceRootTruncated").GetBoolean());
            Assert.Equal(Json.Utf8Bytes(longRoot),
                index.GetProperty("workspaceRootBytes").GetInt32());
            if (health.Error is not null)
            {
                Assert.True(index.GetProperty("errorTruncated").GetBoolean());
                Assert.Equal(Json.Utf8Bytes(longError), index.GetProperty("errorBytes").GetInt32());
            }
            if (health.Progress is not null)
            {
                JsonElement progress = index.GetProperty("progress");
                Assert.True(progress.GetProperty("phaseTruncated").GetBoolean());
                Assert.Equal(Json.Utf8Bytes(longPhase),
                    progress.GetProperty("phaseBytes").GetInt32());
            }
        }
    }

    [Fact]
    public void CapabilitiesBoundMalformedIndexMetadataWithoutLosingFeatureIds()
    {
        static List<string> FeatureIds(JsonElement response) => response.GetProperty("features")
            .EnumerateArray().Select(feature => feature.GetProperty("id").GetString()!)
            .ToList();

        var baseline = new IndexHealth("ready", "11", "indexed", "refreshed", 0,
            null, 123, "C:/workspace", "index.db");
        List<string> expectedIds = FeatureIds(Parse(
            NavigationTools.ServerCapabilitiesForTest(baseline)));

        // Control characters and JSON metacharacters expand by much more than their raw UTF-8
        // length when serialized. Exercise every string in the non-removable index identity at
        // once so the hard bound is about the actual wire payload, not a friendly ASCII case.
        string malformed = string.Concat(Enumerable.Repeat("\0\"\\", 10_000));
        var health = new IndexHealth(malformed, malformed, malformed, malformed, 0,
            malformed, 123, malformed, "index.db",
            Progress: new IndexProgress(malformed, 1, 2, 3));

        string json = NavigationTools.ServerCapabilitiesForTest(health);
        Assert.True(Json.Utf8Bytes(json) <= Json.HardBudgetBytes,
            $"malformed capabilities used {Json.Utf8Bytes(json)} bytes");
        JsonElement response = Parse(json);
        Assert.Equal(expectedIds, FeatureIds(response));

        JsonElement index = response.GetProperty("index");
        foreach (string field in new[]
                 {
                     "state", "indexVersion", "indexedAtUtc", "lastRefreshUtc",
                     "workspaceRoot", "error",
                 })
        {
            Assert.True(index.GetProperty(field + "Truncated").GetBoolean());
            Assert.Equal(Json.Utf8Bytes(malformed),
                index.GetProperty(field + "Bytes").GetInt32());
        }

        JsonElement progress = index.GetProperty("progress");
        Assert.True(progress.GetProperty("phaseTruncated").GetBoolean());
        Assert.Equal(Json.Utf8Bytes(malformed), progress.GetProperty("phaseBytes").GetInt32());
    }

    [Fact]
    public void CapabilitySummaryCompactionIsDeterministicHonestAndKeepsIds()
    {
        var envelope = new
        {
            server = "test",
            features = new object[]
            {
                new { id = "alpha", summary = new string('a', Json.HardBudgetBytes) },
                new { id = "review-beta", summary = new string('b', Json.HardBudgetBytes) },
                new { id = "gamma", summary = "small" },
            },
        };

        string first = Json.WithCapabilitiesBudget(envelope);
        string second = Json.WithCapabilitiesBudget(envelope);
        Assert.Equal(first, second);
        Assert.True(Json.Utf8Bytes(first) <= Json.HardBudgetBytes);
        JsonElement response = Parse(first);
        Assert.True(response.GetProperty("featuresCompacted").GetBoolean());
        var features = response.GetProperty("features").EnumerateArray().ToList();
        Assert.Equal(new[] { "alpha", "review-beta", "gamma" },
            features.Select(feature => feature.GetProperty("id").GetString()));
        Assert.Equal(features.Count(feature => feature.TryGetProperty("summary", out _)),
            response.GetProperty("featureSummariesReturned").GetInt32());
    }

    // The build commit comes from the SDK's "<version>+<sha>" AssemblyInformationalVersion. Pin the
    // parse — in particular the "unknown" fallback for a git-less build (no +sha), the exact scenario
    // a review flagged: the stamp must degrade to "unknown", never a partial/garbage commit.
    [Theory]
    [InlineData("1.0.0+868bf8c88be235d377159b7d84b96997a9c1fefc", "868bf8c88be2")]
    [InlineData("0.2.0+abc123", "abc123")]
    [InlineData("1.0.0", "unknown")]  // git-less build: SDK appends no +sha
    [InlineData("1.0.0+", "unknown")] // malformed suffix
    [InlineData(null, "unknown")]
    public void BuildInfoParsesCommitOrFallsBackToUnknown(string? informationalVersion, string expected)
        => Assert.Equal(expected, BuildInfo.ParseCommit(informationalVersion));

    // search_text context lines (grep -C): hits carry surrounding lines only when context is requested;
    // by default before/after are omitted (no byte cost). The agent's #1 "biggest single win".
    [Fact]
    public void SearchTextReturnsContextLinesOnlyWhenRequested()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var hits = Parse(tools.SearchText("NotNull", context: 2)).GetProperty("hits").EnumerateArray().ToList();
        Assert.NotEmpty(hits);
        foreach (var h in hits)
        {
            if (h.TryGetProperty("before", out var b)) Assert.InRange(b.GetArrayLength(), 1, 2);
            if (h.TryGetProperty("after", out var a)) Assert.InRange(a.GetArrayLength(), 1, 2);
        }
        // Guard.NotNull sits inside a namespace+class (and call sites inside methods), so some hit has lines above it.
        Assert.Contains(hits, h => h.TryGetProperty("before", out var b) && b.GetArrayLength() > 0);
        // Default (no context) omits before/after entirely.
        var plain = Parse(tools.SearchText("NotNull")).GetProperty("hits").EnumerateArray().ToList();
        Assert.All(plain, h => Assert.False(h.TryGetProperty("before", out _) || h.TryGetProperty("after", out _)));
    }

    // ContextSlice is byte-bounded so a single context-heavy (e.g. CJK) hit can't breach the response
    // hard-byte budget (which floors at one item), and returns null (omitted) at file edges — never [].
    [Fact]
    public void ContextSliceIsByteBoundedAndEdgeSafe()
    {
        // 50 wide (multi-byte) lines; Snippet caps each at 240 chars => ~723 UTF-8 bytes/line.
        var wide = Enumerable.Range(0, 50).Select(_ => new string('中', 300)).ToArray();
        var (before, after) = IndexQueries.ContextSlice(wide, 25, before: 20, after: 20);
        // 4KB/side over ~723-byte lines => far fewer than the 20 requested (the byte cap bit).
        Assert.InRange(before!.Count, 1, 10);
        Assert.InRange(after!.Count, 1, 10);
        // Edge safety: no 'before' on the first line, no 'after' on the last — null, not [].
        Assert.Null(IndexQueries.ContextSlice(wide, 0, 5, 5).Before);
        Assert.Null(IndexQueries.ContextSlice(wide, wide.Length - 1, 5, 5).After);
        // Small ASCII lines: full requested window (byte cap not hit), correct ordering, hit line excluded.
        var (b, a) = IndexQueries.ContextSlice(new[] { "a0", "a1", "a2", "a3", "a4" }, 2, 2, 2);
        Assert.Equal(new[] { "a0", "a1" }, b);
        Assert.Equal(new[] { "a3", "a4" }, a);
    }

    // Precise-by-default: the noisy cross-line 'partial' co-occurrence bucket is opt-in now (agent's #3).
    [Fact]
    public void SearchTextIsPreciseByDefaultPartialsOptIn()
    {
        var tools = new NavigationTools(_manager, _semantic);
        // 'using' and 'namespace' occur in every .cs file but on different lines -> partial leads.
        var def = Parse(tools.SearchText("namespace using")).GetProperty("hits").EnumerateArray().ToList();
        Assert.DoesNotContain(def, h => h.GetProperty("matchKind").GetString() == "partial");
        var opt = Parse(tools.SearchText("namespace using", partials: "always")).GetProperty("hits").EnumerateArray().ToList();
        Assert.Contains(opt, h => h.GetProperty("matchKind").GetString() == "partial");
    }
}
