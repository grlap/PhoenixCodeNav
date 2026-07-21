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
        Assert.Contains("bounded FCS compiler checks", indexed);
        Assert.Contains("remain partial", indexed);
        Assert.Contains("Roslyn", model.GetProperty("exact").GetString());
        JsonElement semantic = json.GetProperty("semantic");
        Assert.Equal("cs", semantic.GetProperty("exactToolsLanguage").GetString());
        Assert.Contains("definition", semantic.GetProperty("csharpExactTools")
            .EnumerateArray().Select(tool => tool.GetString()));
        Assert.Contains("definition", semantic.GetProperty("fsharpIndexedTools")
            .EnumerateArray().Select(tool => tool.GetString()));
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
        Assert.Equal(64 * 1024,
            json.GetProperty("budgets").GetProperty("hardBytes").GetInt32());
    }

    // The features manifest lets a caller CONFIRM a capability without triggering its silent-when-clean
    // response fields — the exact verification the field agent couldn't do from a bare response.
    [Fact]
    public void CapabilitiesFeatureManifestLetsCallerConfirmCapabilities()
    {
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.ServerCapabilities());
        Assert.False(json.TryGetProperty("featuresCompacted", out _));
        var ids = json.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains("compiled-awareness", ids);
        Assert.Contains("implementer-completeness", ids);
        Assert.Contains("generic-arity-resolution", ids);
        Assert.Contains("friend-assembly-semantics", ids);
        Assert.Contains("fsharp-outline-parse-context-budget", ids);
        Assert.Contains("fsharp-symbol-at-semantic", ids);
        Assert.Contains("fsharp-definition-same-project", ids);
        Assert.Contains("fsharp-type-check-context-selection", ids);
        Assert.Contains("fsharp-semantic-snapshot", ids);
        Assert.Contains("fsharp-semantic-bounded-project-evaluation", ids);
        Assert.Contains("workspace-msbuild-config-indexing", ids);
        Assert.Contains("hierarchy-ranking", ids);
        Assert.Contains("capabilities-hard-budget", ids);
        Assert.Contains("semantic-large-repo-budget", ids);
        Assert.Contains("semantic-rebuild-coordination", ids);
        Assert.Contains("semantic-candidate-completeness-over-accounting", ids);
        Assert.Contains("semantic-planning-attribution", ids);
        Assert.Contains("indexed-base-type-edges", ids);
        Assert.Contains("references-stage-attribution", ids);
        Assert.Contains("references-parallel-compilation-preparation", ids);
        Assert.Contains("references-document-scoped-search", ids);
        Assert.Contains("semantic-persistent-syntax-indexes", ids);
        Assert.Contains("search-symbol-malformed-query", ids);
        Assert.Contains("index-follower-liveness-fail-closed", ids);
        string semanticBudget = Assert.Single(json.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("id").GetString() == "semantic-large-repo-budget")
            .GetProperty("summary").GetString()!;
        Assert.Contains("default all candidates", semanticBudget);
        Assert.Contains("positive maxProjects bounds", semanticBudget);

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
        var tools = new NavigationTools(_manager, _semantic);
        var json = Parse(tools.ServerCapabilities());
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
        Assert.Contains("normalization-only, oversized, or excess candidates conservatively remain uncorrelated",
            exactMoves);
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
        Assert.Contains("one classifier drives changedProjectFiles", projectFiles);
        Assert.Contains("review.project_files_changed", projectFiles);
        Assert.Contains("modified or deleted", projectFiles);
        Assert.Contains(".csproj/.fsproj/.csproj.user/.fsproj.user/.shproj/.proj/.projitems/.sln/.slnx/.slnf", projectFiles);
        Assert.Contains("Directory.Build.rsp and MSBuild.rsp", projectFiles);
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
                       ("protocol.allow=never", "review-git-transport-isolation"),
                       ("GIT_ALTERNATE_OBJECT_DIRECTORIES", "review-git-environment-isolation"),
                       ("literal backslashes", "unix-git-path-identity"),
                       ("NUL-framed porcelain roots", "worktree-workspace-path-domain"),
                       ("UntrackedFiles", "review-dirt-provenance"),
                      ("symbolsCoverage", "review-budget-coverage"),
                      ("old and new coordinates", "review-two-sided-diff-ranges"),
                      ("formerSymbols", "review-former-symbol-evidence"),
                      ("declarationExclusionBudgetHit", "review-reference-declaration-budget"),
                      ("explicit-interface", "review-declaration-identity"),
                      ("movedFiles", "review-exact-move-evidence"),
                      ("review.base_blob_unavailable", "review-base-blob-recovery-honesty"),
                      ("namespaceAnalysisCoverage", "review-namespace-analysis-budget"),
                      ("review.project_shape_budget", "review-project-shape-budget"),
                      ("segment, operation, or deadline exhaustion", "review-project-glob-budget"),
                      ("evaluationIncomplete", "review-project-shape-completeness"),
                      ("review.project_files_changed", "review-project-file-guidance"),
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
            Assert.DoesNotContain(features, feature =>
                feature.GetProperty("id").GetString() != owner &&
                feature.TryGetProperty("summary", out JsonElement otherSummary) &&
                otherSummary.GetString()!.Contains(
                    token, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CapabilitiesDynamicTextUsesExactUtf8BoundaryAndReportsTruncation()
    {
        static IndexHealth Health(string root) => new("ready", "11", "indexed", "refreshed",
            0, null, 123, root, "index.db");

        string healthyJson = NavigationTools.ServerCapabilitiesForTest(
            Health("C:/" + new string('r', 257)));
        int healthyMargin = Json.HardBudgetBytes - Json.Utf8Bytes(healthyJson);
        Assert.True(healthyMargin >= 2 * 1024,
            $"healthy capabilities retained only {healthyMargin} bytes of growth margin");
        Assert.False(Parse(healthyJson).TryGetProperty("featuresCompacted", out _));

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
