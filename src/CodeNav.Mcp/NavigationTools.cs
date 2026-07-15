using System.ComponentModel;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: the MCP tool surface — argument handling, result shaping, budgets, and
/// confidence/freshness metadata. Results carry the confidence they earn: "exact" for
/// compiler-backed semantic answers, "indexed" for index/syntax-backed facts, and
/// "heuristic" for naming/text inferences (implementations fallback, related_tests).
/// Does not own: index building/queries (CodeNav.Core).
/// </summary>
[McpServerToolType]
public sealed partial class NavigationTools
{
    internal const int CapabilityDynamicTextBytes = 1024;
    internal const int CapabilityIdentityTextBytes = 96;

    private readonly IndexManager _manager;
    private readonly SemanticService _semantic;

    public NavigationTools(IndexManager manager, SemanticService semantic)
    {
        _manager = manager;
        _semantic = semantic;
    }

    private static readonly string[] TypeKinds = { "class", "interface", "struct", "record", "record_struct", "enum", "delegate" };

    // ---------------------------------------------------------------- capabilities / overview

    [McpServerTool(Name = "server_capabilities")]
    [Description("Reports supported languages, available tools, index status, and response budgets. Call this first if unsure what is available or whether the index is ready.")]
    public string ServerCapabilities() => ServerCapabilitiesJson(_manager.Health(),
        _semantic.FrameworkRefsAvailable);

    internal static string ServerCapabilitiesForTest(IndexHealth health,
        bool frameworkRefsAvailable = true) =>
        ServerCapabilitiesJson(health, frameworkRefsAvailable);

    private static string ServerCapabilitiesJson(IndexHealth h, bool frameworkRefsAvailable)
    {
        string state = CapabilityText(h.State, CapabilityIdentityTextBytes,
            out bool stateTruncated, out int? stateBytes)!;
        string? indexVersion = CapabilityText(h.IndexVersion, CapabilityIdentityTextBytes,
            out bool indexVersionTruncated, out int? indexVersionBytes);
        string? indexedAtUtc = CapabilityText(h.IndexedAtUtc, CapabilityIdentityTextBytes,
            out bool indexedAtUtcTruncated, out int? indexedAtUtcBytes);
        string? lastRefreshUtc = CapabilityText(h.LastRefreshUtc, CapabilityIdentityTextBytes,
            out bool lastRefreshUtcTruncated, out int? lastRefreshUtcBytes);
        string workspaceRoot = Json.Utf8Prefix(h.WorkspaceRoot, CapabilityDynamicTextBytes,
            out bool workspaceRootTruncated);
        int? workspaceRootBytes = workspaceRootTruncated ? Json.Utf8Bytes(h.WorkspaceRoot) : null;
        string? error = h.Error is null
            ? null
            : Json.Utf8Prefix(h.Error, CapabilityDynamicTextBytes, out _);
        bool errorTruncated = h.Error is not null &&
            Json.Utf8Bytes(h.Error) > CapabilityDynamicTextBytes;
        int? errorBytes = errorTruncated ? Json.Utf8Bytes(h.Error!) : null;

        return Json.WithCapabilitiesBudget(new
        {
            server = "phoenixCodeNav",
            version = BuildInfo.Version,
            // Build identity so a caller can verify WHICH build is deployed. The old hardcoded version
            // went stale at 0.1.0 across many feature batches; commit auto-tracks the actual build.
            build = new { version = BuildInfo.Version, commit = BuildInfo.Commit, indexSchema = BuildInfo.IndexSchema },
            languages = new[] { "csharp" },
            navigationLayers = new[] { "text", "syntax", "semantic" },
            // Explicit capability manifest: lets a caller CONFIRM a feature is present without having to
            // trigger its (often silent-when-clean) response fields — grep an id to verify a deploy.
            features = new object[]
            {
                new { id = "capabilities-hard-budget", summary = "UTF-8 hardBytes; *Truncated/*Bytes and featuresCompacted/featureSummariesReturned disclose compaction; every singular feature id remains" },
                new { id = "review-ref-resolution", summary = "Hex-only branch/tag names, abbreviations, and object ids are Git-validated and peeled to full commits; repository-format-width objects retain object precedence and distinct short-hex ambiguity is refused" },
                new { id = "confidence-honesty", summary = "every result carries confidence exact|indexed|heuristic; confidenceNote only when heuristic (tier meanings live in confidenceModel here); meta.statusNote explains refreshing/stale; meta.build stamps every result meta with version+commit" },
                new { id = "hierarchy-ranking", summary = "implementations ranks concrete hits first; conditional likelyImplementation names the sole concrete hit and via identifies indirect base derivation. implementations wraps hit metadata; type_hierarchy returns flat symbols" },
                new { id = "implementer-completeness", summary = "Member fallback exposes implementerCount/omittedImplementers. When identity is compiler-resolved but implementers are indexed, symbolConfidence exact and implementationsConfidence heuristic remain separate; type_hierarchy fallback returns heuristic base-list candidates without compiler-only bases/interfaces" },
                new { id = "generic-arity-resolution", summary = "v0.11.8 implementations/type_hierarchy select by arity or symbolId; mixed-arity names refuse; syntax fallback is arity-exact" },
                new { id = "friend-assembly-semantics", summary = "v0.11.9 models literal local SDK InternalsVisibleTo grants; friend-only results disclose project_model_unproven when imports, package build assets, or Directory.Build authority can change the grant" },
                new { id = "compiled-awareness", summary = "search_symbol orphaned; repo_overview.orphanedFiles; compiled ownership guides semantic resolution, impact, and context_pack" },
                new { id = "git-awareness", summary = "index tracks the workspace's indexed_commit; repo_overview.git reports indexed vs HEAD commit/branch and whether they match. Robust to git shipped as a .cmd/.bat wrapper (spawned via cmd, hex-gated args) and to commit-less repos (reflog watch attaches when .git/logs is born); an unresolved git is LOGGED, never silent" },
                new { id = "vendor-noise", summary = "firstPartyOnly / excludePath / per-hit 'noise' flag / repo_overview.suggestedExcludes" },
                new { id = "text-search", summary = "search_text: whole-word tokens, context lines, containingSymbol, precise-by-default; regex:true (.NET, line-based, FTS-narrowed via narrowedOn, ReDoS-guarded) with coverage honesty (filesTotal/budgetHit/timedOut); zero-hit 'elsewhere' redirect probe + didYouMean (variantKind 'tokenForm': Mode4 <-> 'Mode 4'; variantKind 'spelling': single-token Damerau edit-distance-1 against indexed SYMBOL names, first-char-anchored — first-character typos and sub-4-char tokens are deliberately not covered — every suggestion PROBED before surfacing, never substituted) + contextual notes; elsewhere/didYouMean precise probes carry structured samples {path, line, containingSymbol} beside samplePaths (the redirect no longer drops the owner context main hits carry; the cross-line co-occur branch stays paths-only — its evidence is file-level)" },
                new { id = "reference-kinds", summary = "references (exact path): per-location usage kinds — call/construction/typeMention/attribute/nameof/xmldoc/usingDirective/baseList/typeof/other — with a kinds breakdown, usageKinds filter (validated), and publicConsumersOnly (usages outside the symbol's declaring project); indexed fallback stays unclassified and says so" },
                new { id = "symbol-handles", summary = "Reindex-detecting idx handles pin source_context, definition, references, impact, implementations, and type_hierarchy" },
                new { id = "filter-honest-counts", summary = "references: totalReferences/totalCandidates, kinds, groups, and summary all honor includeTests (filtered BEFORE counting on both exact and indexed paths); linked multi-project files counted once; filtered summaries say 'test projects excluded' instead of a misleading '0 test'" },
                new { id = "test-classification", summary = "isTest is REFERENCE-driven: test frameworks via packages OR binary <Reference> (nunit/xunit/MSTest, incl. non-standard names containing nunit.framework), plus compiled-[TestFixture]-attributes-on-graph-leaf promotion for custom-resolve builds where the framework never appears in the csproj. Name shapes are a narrow dotted-suffix fallback only — TestRoute-style names never classify. Classification broadened in schema v7 (reference signals can reclassify a large share of legacy test projects, so isTest cached from earlier schemas may differ); NAME-uniform across same-AssemblyName csproj pairs since v8" },
                new { id = "bounded-source-reads", summary = "source_context streams only the requested spans from disk (never whole-file reads); contextLines clamped; zero/negative span starts clamp to line 1" },
                new { id = "arity-exact-partials", summary = "partialFiles separate Foo and Foo<T> by syntax arity" },
                new { id = "member-modifiers", summary = "outline/search_symbol/symbol_at/definition symbols carry 'modifiers' (static/sealed/abstract/virtual/override/new/readonly/const, omitted when none) — pick the right override site in deep hierarchies without opening files. 'partial' is DELIBERATELY not in this string: it has its own isPartial field on every symbol node (plus partialFiles cross-links on outline types). Members also carry 'accessors' ({get: 'public', set: 'private'}) — ONLY when an accessor's accessibility differs from the member's own, so a private setter is no longer invisible. Modifiers since schema v4; accessors since v9" },
                new { id = "rebuild-hatch", summary = "refresh_index accepts force: 'auto'|'incremental' (delta refresh; hash-identical files skipped — never rebuilds an intact-looking index) or 'full' (delete the index and REBUILD FROM SCRATCH, pump-serialized, works even from state 'failed' — recovery re-attaches the file watcher and git tracking and clears the old error; watch index.progress). The in-band corruption-recovery hatch — no shell access needed" },
                new { id = "deadline-honesty", summary = "Semantic tools return deadlineMs/elapsedMs. Mid-scan expiry keeps partial, totalIsLowerBound, and 'at least N'; cold load uses partialReason cluster_cold_load. references/implementations split clusterLoadMs/queryMs. Reference totals dedupe project+path+line+kind, expose solutionProjects, and retain outOfGraphCandidates" },
                new { id = "assembly-ref-edges", summary = "legacy <Reference Include>+HintPath to an IN-WORKSPACE assembly counts as a project-graph edge (multi-staged builds that reference dlls from a common output folder, not projects) — dependents-closure candidate discovery, semantic cluster wiring, and project_graph all see it; semantic compilations bind such references to the SOURCE project (source-over-binary), so cross-project implementations/references resolve exactly. Assembly-name collisions (net-old/net-new csproj pairs) resolve to a name-level edge — the graph and the semantic workspace are name-keyed, so paired declarers keep all their consumer edges (schema v6+; edge recovery itself since v5). meta.indexSchema stamped on every response" },
                new { id = "build-progress", summary = "while state=='building', server_capabilities.index.progress and index_building error bodies carry {phase: scanning|parsing_projects|indexing_files|finalizing, filesIndexed, filesTotal, elapsedMs} — monotonic counters, no fabricated percent; absent when ready, and background refreshes never show a cold-build bar. filesSkipped and projectsFailed appear only when >0 (efa); filesPerSecond + estimatedRemainingMs appear only during indexing_files once >=100 files over >=1s of that phase are measured (0tn); pendingProcessed is the monotonic applied-delta count paired with pendingChanges — both flat means a stuck pump (z4c)" },
                new { id = "edge-provenance", summary = "Schema v10 edges distinguish projectReference from hintPathReference; project_graph exposes kind, dependency_path per-hop via, context_pack flags hint-path owners, and impact reports directDependentProjects viaHintPathOnly risk. Mixed paths keep one transitive count/note; dual wiring prefers project; orphan references use orphaned:true with no project" },
                new { id = "review-pack", summary = "review_pack: ONE budget-bounded call from a changed set (validated-base Git diff union working-tree dirt, or explicit paths) to hunk-mapped symbols and per-symbol impact digests: symbolId handle, owner, directDependentProjects (+viaHintPathOnly), transitive count, publicApi, related_tests with signal, indexed reference candidates, and deterministic risks. Deleted .cs files get DELETION honesty from a read-only base blob: each former top-level type reports danglingCandidates. Everything is INDEXED confidence and says so (notes carry stable ids); no semantic resolution runs inside — escalate chosen symbols via references(symbolId, mode:'semantic'). baseRef accepts a sha or strict-charset ref name" },
                new { id = "review-git-stdin-transport", summary = "v0.11.1 Git operand transport: review_pack resolves refs with cat-file --batch-check and reads base blobs with cat-file --batch; accepted dynamic ref names and paths travel on stdin, while only validated 4-64 ASCII-hex prefixes may use rev-parse --disambiguate=<hex>, preventing .cmd/.bat metacharacter reinterpretation" },
                new { id = "review-diff-determinism", summary = "--raw -z --patch uses ordinal/C-quoted path identity; binary/mode/empty/type sections degrade whole-file; old/new hunk-coordinate overflow fails closed as malformed; stage-only unmerged gitlinks report unmerged; process/status failures never become partial success" },
                new { id = "review-content-filter-refusal", summary = "v0.11.1 content-filter refusal: review_pack discovers configured clean/process drivers and evaluates tracked filter attributes without executing helpers; a path selecting an active driver returns git_filter_unsafe rather than running it or claiming complete coverage" },
                new { id = "review-content-filter-overlay", summary = "v0.11.1 content-filter race boundary: every worktree comparison uses a private highest-precedence info/attributes overlay ending in * !filter, so no clean/process driver can become selectable after preflight, including a newly introduced driver" },
                new { id = "review-submodule-coverage", summary = "v0.11.1 submodule boundary: parent review excludes dirty child worktrees and reports that boundary through coverage.submoduleWorktrees plus review.submodule_worktrees_excluded; changedSubmoduleLinks separately reports superproject gitlink pointer changes for child-root follow-up" },
                new { id = "review-untracked-repository-coverage", summary = "v0.11.1 nested-repository boundary: parent review treats an untracked embedded Git worktree as an atomic excluded path without running child-local helpers, reports bounded coverage.untrackedRepositories, and emits review.untracked_repositories_excluded for child-root follow-up" },
                new { id = "review-untracked-link-coverage", summary = "v0.11.2 untracked link boundary: Git-reported files reached through a symbolic link or junction are excluded before hashing or review aggregation, reported through bounded coverage.untrackedLinks, and emit review.untracked_links_excluded for target-root follow-up" },
                new { id = "review-layered-change-refusal", summary = "v0.11.1 layered-change refusal: when independent staged and unstaged manifests contain the same path, review_pack returns git_layered_changes rather than presenting a final-worktree hunk map as coverage of both byte layers" },
                new { id = "review-snapshot-consistency", summary = "Repeated bounded Git captures compare exact raw patch bytes with typed staged/unstaged/unmerged/untracked manifests; symlink payloads, gitlinks, modes, and tracked bytes must match; snapshot_changed becomes git_worktree_changed with no partial result from different worktree epochs" },
                new { id = "review-git-launcher-isolation", summary = "Only canonical absolute paths and trusted system cmd.exe launch Git; batch percent expansion is refused, and a missing or non-directory working directory fails before spawn" },
                new { id = "review-git-transport-isolation", summary = "v0.11.2 Git transport isolation: the highest-precedence GIT_ALLOW_PROTOCOL denylist plus the protocol.allow=never fallback keep read-only plumbing local even when protocol-specific repository config attempts to enable a transport" },
                new { id = "review-git-environment-isolation", summary = "v0.11.4 clears inherited repository/object/index selectors (GIT_DIR, GIT_WORK_TREE, GIT_INDEX_FILE, GIT_ALTERNATE_OBJECT_DIRECTORIES) before discovery; the sandbox reinstates only validated paths" },
                new { id = "review-workspace-path-domain", summary = "v0.11.2 workspace path domain: the safety sandbox binds Git's actual toplevel while --relative scopes every manifest and :./ blob lookup to the configured workspace" },
                new { id = "unix-git-path-identity", summary = "v0.11.4 Unix literal backslashes, including a root-level leading literal backslash, retain file identity across scan, watcher, refresh, commit reconciliation, and review; Windows still treats backslash as a directory separator" },
                new { id = "worktree-workspace-path-domain", summary = "v0.11.5 worktree path domain: NUL-framed porcelain roots carry the configured repository-subtree prefix into linked worktrees; host-sensitive identity preserves case-distinct Git paths, and invalid caller roots return structured errors" },
                new { id = "review-dirt-provenance", summary = "v0.11.2 dirt provenance: ReviewDiff preserves true UntrackedFiles separately from staged and unstaged tracked dirt, so only genuinely untracked files are widened to whole-file evidence or counted as untracked" },
                new { id = "review-budget-coverage", summary = "v0.11.2 review budget coverage: symbolsCoverage, changedCsFilesCoverage, changedProjectFilesCoverage, deletedFilesCoverage, and per-record former-type totals expose every cap; whole-envelope maxBytes trimming can reduce every optional list to zero without exceeding the accepted UTF-8 budget" },
                new { id = "review-two-sided-diff-ranges", summary = "v0.11.2 two-sided diff ranges: DiffHunk retains old and new coordinates so deletion and replacement evidence identifies its coordinate side explicitly" },
                new { id = "review-former-symbol-evidence", summary = "v0.11.2 former-symbol evidence: review_pack reparses bounded base blobs and reports formerSymbols for removed or renamed members, including members lost from modified relocations or deleted partial declarations; current declaration survivors are project-domain/advisory evidence rather than workspace-global proof" },
                new { id = "review-reference-declaration-budget", summary = "Name-scoped former-reference exclusion is bounded; declarationExclusionBudgetHit plus review.reference_declaration_budget disclose lower-bound candidates" },
                new { id = "review-declaration-identity", summary = "v0.11.5 review declaration identity (index schema v14) includes parameter types, ancestor generic arity, checked-vs-unchecked operators, and explicit-interface operator qualifiers; tuple labels are omitted while tuple types and nesting remain identity-bearing" },
                new { id = "review-exact-move-evidence", summary = "v0.11.2 exact move evidence: movedFiles reports unique staged or unstaged .cs raw-byte relocations; untracked candidates are read through size/count-bounded anchored no-follow handles, and normalization-only, oversized, or excess candidates conservatively remain uncorrelated" },
                new { id = "review-base-blob-recovery-honesty", summary = "v0.11.2 base-blob recovery honesty: per-file size plus cumulative character/attempt/time bounds appear in baseBlobRecoveryCoverage; batch-check rejects oversized blobs before content streaming, failures emit review.base_blob_unavailable, cumulative exhaustion emits review.base_blob_budget, and recoveryStatus/unmapped evidence omits unknown former-type totals instead of serializing false zero coverage" },
                new { id = "review-namespace-analysis-budget", summary = "v0.11.2 namespace analysis budget: namespace-only classification loads indexed content only for uncovered ranges and stops at per-file plus cumulative character/file/time bounds; namespaceAnalysisCoverage and review.namespace_analysis_budget mark conservative file_level fallback" },
                new { id = "review-project-shape-budget", summary = "Bounded no-follow XML caps project count, bytes, and time; projectOwnershipFallbackCoverage plus review.project_shape_budget disclose incomplete deleted-path proof" },
                new { id = "review-project-glob-budget", summary = "Iterative project-ownership glob budget covers default-SDK checks and Include/Exclude; globBudgetHit plus review.project_glob_budget expose segment, operation, or deadline exhaustion and fail proof closed" },
                new { id = "review-project-shape-completeness", summary = "Unevaluated imports/SDKs/conditions/expressions block deleted-path proof; projectOwnershipFallbackCoverage.evaluationIncomplete and review.project_shape_incomplete disclose it" },
                new { id = "review-project-file-guidance", summary = "v0.11.4 one classifier drives changedProjectFiles and review.project_files_changed for modified or deleted .csproj/.csproj.user/.shproj/.proj/.projitems/.sln/.slnx/.slnf/.props/.targets and Directory.Build.rsp and MSBuild.rsp inputs" },
                new { id = "review-default-baseline-honesty", summary = "v0.11.4 bounded git_index_baseline_unavailable gives refresh_index or explicit baseRef guidance; caller-supplied invalid refs remain bad_request" },
                new { id = "review-unmapped-change-coverage", summary = "v0.11.2 unmapped change coverage: namespace and file-level C# regions not fully covered by reviewable indexed symbols appear in bounded unmappedChanges records with explicit side, old/new coordinates, reason, and total/returned/truncated" },
                new { id = "review-index-epoch-consistency", summary = "review_pack pins rows and response metadata to one stable SQLite read epoch; an overlapping refresh cannot mix old symbols with new ownership or health evidence" },
                new { id = "review-per-hunk-type-mapping", summary = "Type/member suppression is evaluated per old/new hunk, so a type-header edit remains reviewable when a separate hunk touches one of its members" },
                new { id = "stable-note-ids", summary = "diagnostic notes carry MACHINE-MATCHABLE stable ids (a0b): consumers match ids, not prose. review_pack notes are {id, text} natively; retrofitted additively elsewhere — references.noteId (zero_loading_gap), impact.transitiveNoteId (transitive_single_count), type_hierarchy.noteId (heuristic_fallback), search_text.noteId (did_you_mean | elsewhere_matches | absent_everywhere). Ids are the contract — prose may be reworded, ids may not; one id per CAUSE" },
                new { id = "worktree-indexes", summary = "On Windows and Linux, worktrees lists anchored sibling status and index_worktree creates or refreshes a Git-validated target; macOS is unsupported for both operations" },
                new { id = "index-write-destination-authority", summary = "IndexManager and direct IndexBuilder writes validate database/WAL/SHM/rollback-journal leaves and parents: Windows pins the full no-delete-share chain, Linux writes through a held directory fd, and macOS performs startup and per-open identity revalidation only" },
                new { id = "worktree-index-platform-policy", summary = "Windows uses targeted indexed_commit-to-HEAD plus dirt reconciliation, Linux uses an anchored full sweep with usedFullSweep=true, and macOS returns unsupported_platform" },
                new { id = "worktree-index-destination-isolation", summary = "Sibling SQLite work stays in private staging; an anchored no-follow destination atomically publishes the checkpointed database and refuses linked database, WAL, SHM, and rollback-journal paths without touching their targets" },
                new { id = "worktree-index-lease", summary = "A cross-process ownership lease guards every writable Phoenix index lifetime; index_worktree returns worktree_index_locked while another Phoenix owns that target" },
                new { id = "worktree-response-budget", summary = "worktrees may trim every item to zero, and index_worktree UTF-8-bounds reflected paths/details with truncation metadata before enforcing the complete hardBytes envelope" },
                new { id = "index-read-followers", summary = "Windows writer/followers; rebuild drains readers; modes writer|follower|unavailable; index_writer_required" },
                new { id = "semantic-large-repo-budget", summary = "default all candidates; positive maxProjects bounds" },
                new { id = "semantic-rebuild-coordination", summary = "semantic loads drain; rebuild resumes after readers" },
                new { id = "related-tests-signal", summary = "related_tests / impact / context_pack test groups carry 'signal' — the strongest usage shape among sampled mention lines: callSite > typeUsage > nameMention. A heuristic lead-strength label, not a compiler fact; omitted on naming-convention / project-reference groups and on an ungraded mention group — absent means UNGRADED, never nameMention. Samples carry the real mention line + text when located" },
            },
            tools = new[]
            {
                "server_capabilities", "repo_overview", "find_file", "search_text", "outline",
                "source_context", "search_symbol", "symbol_at", "definition", "references",
                "implementations", "callers", "callees", "type_hierarchy", "related_tests",
                "dependency_path", "config_lookup", "batch_outline", "context_pack", "impact",
                "project_graph", "projects_containing", "refresh_index",
                "worktrees", "index_worktree", "review_pack",
            },
            budgets = new { softBytes = Json.SoftBudgetBytes, hardBytes = Json.HardBudgetBytes, defaultLimit = 20 },
            confidenceModel = new
            {
                exact = "compiler-verified (Roslyn semantic resolution)",
                indexed = "index/syntax-backed, not compiler-verified — confirm with source_context before edits",
                heuristic = "naming/text inference — a lead, verify before relying on it",
            },
            semantic = new
            {
                engine = "roslyn-adhoc (no MSBuild dependency)",
                frameworkRefsAvailable,
                exactTools = new[] { "definition", "references", "implementations" },
                note = "Exact results are scoped to loaded candidate clusters; coverage/partial fields report anything skipped.",
            },
            index = new
            {
                state,
                mode = h.AccessMode,
                stateTruncated = stateTruncated ? true : (bool?)null,
                stateBytes,
                // Live build progress (bead two, field-requested): phase + monotonic counters +
                // elapsedMs; filesTotal only once the scan knows it; absent unless building.
                // No ETA/percent by design — see the BuildProgress doc for the honesty rationale.
                progress = ProgressJson(h),
                indexVersion,
                indexVersionTruncated = indexVersionTruncated ? true : (bool?)null,
                indexVersionBytes,
                indexedAtUtc,
                indexedAtUtcTruncated = indexedAtUtcTruncated ? true : (bool?)null,
                indexedAtUtcBytes,
                lastRefreshUtc,
                lastRefreshUtcTruncated = lastRefreshUtcTruncated ? true : (bool?)null,
                lastRefreshUtcBytes,
                h.PendingChanges,
                pendingChangesKnown = h.AccessMode == IndexManager.WriterAccessMode
                    ? (bool?)null
                    : false,
                // z4c: the pair that turns 'refreshing' from a binary into movement — pending
                // drains while processed climbs; both flat = a stuck pump, not a busy one.
                pendingProcessed = h.PendingProcessed,
                error,
                errorTruncated = errorTruncated ? true : (bool?)null,
                errorBytes,
                dbBytes = h.DbBytes,
                workspaceRoot,
                workspaceRootTruncated = workspaceRootTruncated ? true : (bool?)null,
                workspaceRootBytes,
            },
        });
    }

    private static string? CapabilityText(string? value, int maxBytes,
        out bool truncated, out int? originalBytes)
    {
        if (value is null)
        {
            truncated = false;
            originalBytes = null;
            return null;
        }

        string result = Json.Utf8Prefix(value, maxBytes, out truncated);
        originalBytes = truncated ? Json.Utf8Bytes(value) : null;
        return result;
    }

    [McpServerTool(Name = "repo_overview")]
    [Description("Compact workspace map: project/solution/file/symbol counts, styles, target frameworks, and index freshness. Call before starting code work.")]
    public string RepoOverview()
    {
        if (NotReady() is { } notReady) return notReady;
        using var q = _manager.OpenQueries();
        var stats = q.Overview();
        var h = _manager.Health();

        // Live HEAD lookup (occasional call — not in the per-response meta). When it differs
        // from the indexed commit, a branch switch / pull is still reconciling; indexStatus
        // already reports the transient lag. gitStatus is HONEST about failure (field: a silent
        // absence after the hang guard fired left "why is headCommit empty?" undiagnosable):
        // "ok" | "unavailable" (git absent / not a repo) | "timed_out" (git slow — the guard fired,
        // not a hang; timeoutMs says how long it waited).
        var (headCommit, gitStatus) = _manager.CurrentHeadCommitEx();
        object? git = gitStatus == "unavailable" && h.IndexedCommit is null
            ? null // never was a git workspace — omit the block entirely
            : new
            {
                status = gitStatus,
                timeoutMs = gitStatus == "timed_out" ? 10000 : (int?)null,
                indexedCommit = h.IndexedCommit,
                indexedBranch = h.IndexedBranch,
                headCommit,
                headMatchesIndex = headCommit is not null && h.IndexedCommit is not null
                    && string.Equals(headCommit, h.IndexedCommit, StringComparison.OrdinalIgnoreCase),
            };

        return Json.Serialize(new
        {
            workspaceRoot = h.WorkspaceRoot,
            projects = new
            {
                total = stats.Projects,
                legacyStyle = stats.LegacyProjects,
                sdkStyle = stats.SdkProjects,
                test = stats.TestProjects,
            },
            solutions = stats.Solutions,
            csFiles = stats.CsFiles,
            totalLines = stats.TotalLines,
            symbols = stats.Symbols,
            generatedFiles = stats.GeneratedFiles,
            // .cs files in no project's compile set — dead code the compiler never builds (3tz: the
            // graph expands <Compile Include> globs and honors <Compile Remove>; residual gaps are
            // shared .projitems, props-level globs, and ignored Conditions). Per hit: the orphaned flag.
            orphanedFiles = stats.OrphanedFiles,
            targetFrameworks = stats.TfmBreakdown,
            // Vendored/generated directory globs detected in the index — pass to search_symbol /
            // search_text excludePath (or firstPartyOnly) to drop third-party noise. [] when none.
            suggestedExcludes = q.SuggestedExcludes(),
            git,
            meta = Meta.From(h, "indexed", "text"),
        });
    }

    // ---------------------------------------------------------------- files / text

    [McpServerTool(Name = "find_file")]
    [Description("Find files by name or glob (e.g. 'InvoiceService.cs', '*Controller.cs', 'src/Billing/**/*.csproj'). Cheap path-only lookup; use search_symbol for code symbols.")]
    public string FindFile(
        [Description("File name or glob pattern. '*' matches any characters including '/'.")] string nameOrGlob,
        [Description("Exclude paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party files).")] string? excludePath = null,
        [Description("Max results (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call to fetch the next page.")] string? cursor = null)
    {
        if (NotReady() is { } notReady) return notReady;
        (limit, int offset, _) = Page(limit, cursor);
        using var q = _manager.OpenQueries();
        var excludes = excludePath is { Length: > 0 } ex ? new[] { ex } : null;
        var files = q.FindFiles(nameOrGlob, limit + 1, excludes, offset);
        bool hadMore = files.Count > limit;
        if (hadMore) files.RemoveAt(files.Count - 1);

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        // nextCursor resumes at offset + the count actually RETURNED: the byte-budget shrink drops the
        // page tail (keeping a prefix), so a fixed offset+limit would skip the dropped items (bug e2q).
        return Json.WithListBudget(files, (items, truncated) => new
        {
            files = items.Select(f => new { f.Path, sizeBytes = f.Size, lines = f.LineCount, f.IsGenerated }),
            nextCursor = (hadMore || truncated) ? $"o:{offset + items.Count}" : null,
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "search_text")]
    [Description("Ranked full-text search over the indexed C# surface (.cs plus .csproj/.sln/config) — NOT .sql/.js/other file types. WHOLE-WORD and token-based by default: 'Batch' does NOT match 'Batching'. For \\s / alternation / character classes set regex:true (.NET regex, line-based, scoped by pathGlob) — still not rust/ripgrep syntax; other file types need grep. Returns 'precise' hits (all query tokens on one line) by default; set partials='always' for weaker co-occurrence leads. Use context (or contextBefore/contextAfter) for surrounding lines, like grep -C/-B/-A. Best for literals, config keys, error messages, comments; for code identifiers prefer search_symbol/definition/references.")]
    public string SearchText(
        [Description("Text to find. Multi-word queries are AND-ed by token; a line with all tokens is 'precise'.")] string query,
        [Description("Restrict to paths matching this glob (e.g. 'src/Billing/**').")] string? pathGlob = null,
        [Description("Exclude paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party source).")] string? excludePath = null,
        [Description("Drop hits under known vendor/generated dir names (3rdparty, vendor, external, generated...) at ANY depth. Matches the per-hit noise flag; convenience over excludePath.")] bool firstPartyOnly = false,
        [Description("Restrict to files compiled by this project name.")] string? project = null,
        [Description("'all' (default), 'production' (exclude tests), or 'tests'.")] string scope = "all",
        [Description("Restrict by file language: cs | csproj | sln | config.")] string? lang = null,
        [Description("Include generated files (default false).")] bool includeGenerated = false,
        [Description("Weaker 'some query tokens co-occur, not all on one line' leads: 'never' (default — precise only), 'auto' (fill space precise did not), or 'always'. filesMatchedAcrossLines still flags files where all tokens co-occur across lines.")] string partials = "never",
        [Description("Lines of context around each hit, like grep -C (0-20, default 0 = just the line). Applies both before and after.")] int context = 0,
        [Description("Context lines BEFORE each hit (grep -B); overrides 'context' when set.")] int? contextBefore = null,
        [Description("Context lines AFTER each hit (grep -A); overrides 'context' when set.")] int? contextAfter = null,
        [Description("Treat 'query' as a .NET regex instead of tokens — NOT rust/ripgrep syntax. LINE-BASED: a pattern spanning multiple lines matches NOTHING. Case-sensitive; prefix (?i) for insensitive. Scope with pathGlob; ReDoS-guarded (per-match timeout + overall budget) with honest coverage (filesTotal/budgetHit/timedOut). Overrides whole-word/partials.")] bool regex = false,
        [Description("Max hits (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call.")] string? cursor = null)
    {
        if (NotReady() is { } notReady) return notReady;
        (limit, int offset, _) = Page(limit, cursor);
        // Fail-safe: an unrecognized value falls back to the precise-only default, not the more
        // permissive "auto" — a typo must not silently reintroduce the noisy partial bucket.
        string mode = partials switch { "never" or "auto" or "always" => partials, _ => "never" };
        int ctxBefore = Math.Clamp(contextBefore ?? context, 0, 20);
        int ctxAfter = Math.Clamp(contextAfter ?? context, 0, 20);
        using var q = _manager.OpenQueries();
        if (regex)
            return RegexResponse(q, query, pathGlob, excludePath, firstPartyOnly, project, scope, lang, includeGenerated, limit, offset, ctxBefore, ctxAfter);
        var filter = new IndexQueries.TextFilter(
            PathGlob: pathGlob,
            Project: project,
            IncludeGenerated: includeGenerated,
            TestsOnly: scope switch { "tests" => true, "production" => false, _ => null },
            Lang: lang,
            ExcludePaths: BuildExcludes(excludePath, firstPartyOnly));
        var result = q.SearchTextGraded(query, limit + 1, filter, maxCandidateFiles: 300, offset: offset, partialsMode: mode, ctxBefore: ctxBefore, ctxAfter: ctxAfter);
        var hits = result.Hits;
        bool hadMore = hits.Count > limit;
        if (hadMore) hits.RemoveAt(hits.Count - 1);

        // Only surface the file-level "tokens co-occur but not on one line" signal when the
        // page shows no precise hits — that is exactly when it changes the caller's read.
        bool anyPrecise = hits.Any(h => h.MatchKind == "precise");
        var acrossLines = !anyPrecise && result.FilesMatchedAcrossLines.Count > 0
            ? result.FilesMatchedAcrossLines.Take(10).ToList()
            : null;

        // Dead-end redirect (field evidence: an agent scoped pathGlob to the wrong dir, got a correct 0,
        // and fell back to manually reading files). The index knows where matches actually are — one
        // bounded unscoped probe turns "0 hits" into "0 HERE, but they exist THERE" or an honest
        // "absent everywhere". Only on a first-page total dead end, so the probe costs nothing normally.
        object? elsewhere = null;
        object? didYouMean = null;
        string? note = null;
        string? noteId = null; // a0b: stable id for the CAUSE behind `note` (catalog: NoteIds)
        // Token-VARIANT probe (field: searched 'Mode4', the code says 'Mode 4' -> 0 hits, agent fell
        // back to grep). Probes the split (Mode4 -> "Mode 4") and joined ("Mode 4" -> Mode4) forms; a
        // hit is SUGGESTED via didYouMean, never silently substituted. Called from EVERY zero-precise
        // first-page branch — review showed the join direction is starved if gated to total dead ends
        // only (common tokens co-occur across lines in any real repo, landing in the co-occur or
        // partial-leads branches instead). Counts are hedged: the probe grades <=100 candidate files.
        void ProbeVariants(bool replaceNote)
        {
            // Candidate order is a policy: token-FORM variants (Mode4 <-> "Mode 4") first —
            // they preserve the caller's spelling — then SPELLING near-misses (1ly, field:
            // didYouMean never fired on identifier typos because only form variants existed).
            // Spelling candidates only for a single bare identifier-ish token: multi-token or
            // punctuated queries aren't symbol names, and the vocabulary is the symbol index.
            var candidates = new List<(string Variant, string Kind)>();
            if (QueryVariants.SplitVariant(query) is { } split) candidates.Add((split, "tokenForm"));
            if (QueryVariants.JoinVariant(query) is { } join) candidates.Add((join, "tokenForm"));
            if (query.Length >= 4 && query.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                candidates.AddRange(q.NearMissSymbolNames(query, 3).Select(n => (n, "spelling")));
            }
            foreach (var (variant, kind) in candidates)
            {
                var vp = q.SearchTextGraded(variant, 5, new IndexQueries.TextFilter(IncludeGenerated: true),
                    maxCandidateFiles: 100, offset: 0, partialsMode: "never");
                if (vp.TotalPrecise > 0)
                {
                    // Structured samples (dzi): the redirect used to drop exactly the owner
                    // context main hits carry; samplePaths stays for compatibility.
                    var sampleHits = vp.Hits.Take(3).ToList();
                    var sampleOwners = OwningSymbols(q, sampleHits);
                    didYouMean = new
                    {
                        query = variant,
                        variantKind = kind, // 'tokenForm' (Mode4 <-> "Mode 4") | 'spelling' (edit distance 1, probed)
                        preciseCount = vp.TotalPrecise,
                        samplePaths = vp.Hits.Select(h => h.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList(),
                        samples = sampleHits.Select(h => new
                        {
                            path = h.FilePath,
                            h.Line,
                            containingSymbol = sampleOwners.TryGetValue((h.FilePath, h.Line), out var cs) ? cs : null,
                        }),
                    };
                    string what = kind == "spelling"
                        ? $"the near-miss identifier '{variant}' (edit distance 1, from the symbol index)"
                        : $"the variant '{variant}'";
                    string msg = $"{what} has at least {vp.TotalPrecise} precise line(s) in the probed candidates (see didYouMean) — retry with that query"
                        + (vp.Hits.All(h => h.IsGenerated) ? " with includeGenerated:true (the probe hits are all in generated files)" : "") + ".";
                    note = replaceNote ? $"No file contains all query tokens together, but {msg}" : $"{note} Also: {msg}";
                    noteId = NoteIds.SearchDidYouMean; // the suggestion is the actionable cause now
                    break;
                }
            }
        }
        if (result.TotalPrecise == 0 && result.TotalPartial == 0 && offset == 0)
        {
            bool scoped = pathGlob is { Length: > 0 } || excludePath is { Length: > 0 } || firstPartyOnly
                || project is not null || scope != "all" || lang is not null;
            if (IndexQueries.FtsQuery(query).Length == 0)
            {
                // Pure punctuation/whitespace: the tokenizer sees nothing, so a probe is pointless.
                note = "The query has no indexable tokens (letters/digits/underscore) — token search cannot match it. Use regex:true for punctuation patterns, or grep.";
            }
            else
            {
                var probe = q.SearchTextGraded(query, 5, new IndexQueries.TextFilter(IncludeGenerated: true),
                    maxCandidateFiles: 100, offset: 0, partialsMode: "never");
                if (probe.TotalPrecise > 0)
                {
                    // Structured samples (dzi): parity with main hits — the redirect used to
                    // ship bare path strings, dropping the containingSymbol context that makes
                    // a lead actionable. samplePaths stays for compatibility (their spec rows
                    // reference it). The co-occur branch below keeps paths only — its evidence
                    // is file-level (no line to anchor an owner on).
                    var probeSamples = probe.Hits.Take(3).ToList();
                    var probeOwners = OwningSymbols(q, probeSamples);
                    elsewhere = new
                    {
                        preciseCount = probe.TotalPrecise,
                        samplePaths = probe.Hits.Select(h => h.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList(),
                        samples = probeSamples.Select(h => new
                        {
                            path = h.FilePath,
                            h.Line,
                            containingSymbol = probeOwners.TryGetValue((h.FilePath, h.Line), out var cs) ? cs : null,
                        }),
                    };
                    bool probeAllGenerated = probe.Hits.All(h => h.IsGenerated);
                    // "Outside your filters" covers every filter, not just pathGlob: a samplePath INSIDE
                    // the caller's glob means scope/lang/project/excludePath excluded it, not the glob.
                    note = scoped
                        ? $"0 hits within your filters, but at least {probe.TotalPrecise} precise line(s) exist outside them (see elsewhere.samplePaths; a sample inside your pathGlob means a different filter — scope/lang/project/excludePath — excluded it) — widen or relax filters."
                          + (probeAllGenerated ? " The probe hits are all in generated files — pass includeGenerated:true." : "")
                        : "The probe found matches only in generated files — pass includeGenerated:true.";
                    noteId = NoteIds.SearchElsewhereMatches;
                }
                else if (probe.TotalPartial > 0)
                {
                    // The tokens DO co-occur, just never on one line (the probe's partial signal —
                    // asserting "absent anywhere" here would be false; a precise line can also exist
                    // below the probe's candidate cap, hence "in the probed candidates").
                    elsewhere = new
                    {
                        coOccurringFiles = probe.FilesMatchedAcrossLines.Count,
                        samplePaths = probe.FilesMatchedAcrossLines.Take(3).ToList(),
                    };
                    note = $"No single line in the probed candidates has all query tokens, but they co-occur across lines in {probe.FilesMatchedAcrossLines.Count} file(s) (see elsewhere.samplePaths) — drop a token or retry with partials:'always'{(scoped ? " without your filters" : "")}.";
                    ProbeVariants(replaceNote: false); // a variant with PRECISE lines beats cross-line leads
                }
                else
                {
                    // Provably index-wide: an FTS AND over all files matched nothing (the inner LIMIT
                    // truncates results, not the match), so no file holds all tokens together.
                    note = "No file contains all query tokens together (whole-word: 'Batch' does not match 'Batching'). Check spelling, drop a token, try search_symbol match='substring' for identifiers, or grep for non-C# file types.";
                    noteId = NoteIds.SearchAbsentEverywhere; // ProbeVariants upgrades this to did_you_mean when a suggestion lands
                    ProbeVariants(replaceNote: true);
                }
            }
        }
        else if (result.TotalPrecise == 0 && result.TotalPartial > 0 && mode == "never")
        {
            note = $"0 precise lines; {result.TotalPartial} weaker cross-line partial lead(s) exist — retry with partials:'always'.";
            if (offset == 0) ProbeVariants(replaceNote: false);
        }
        else if (result.TotalPrecise >= 1000 && offset == 0)
        {
            note = $"Common term: {result.TotalPrecise} precise lines in the scanned candidate set — "
                 + (pathGlob is { Length: > 0 } ? "narrow the glob further or add tokens to sharpen." : "scope with pathGlob or add tokens to sharpen.");
        }

        // Best-effort owning symbol per hit (feedback: jump from a text match to the owning
        // method/type without a follow-up symbol_at). Only .cs files carry symbols.
        var owners = OwningSymbols(q, hits);

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            preciseCount = result.TotalPrecise,
            partialCount = result.TotalPartial,
            hits = items.Select(t => new
            {
                path = t.FilePath,
                t.Line,
                text = t.LineText,
                before = t.Before, // surrounding lines — present only when context was requested
                after = t.After,
                t.IsGenerated,
                matchKind = t.MatchKind,
                matched = t.MatchKind == "partial" ? t.Matched : null, // tokens only meaningful on partials
                containingSymbol = owners.TryGetValue((t.FilePath, t.Line), out var cs) ? cs : null,
                noise = IndexQueries.IsVendorPath(t.FilePath) ? true : (bool?)null, // under a vendored/generated dir
            }),
            filesMatchedAcrossLines = acrossLines,
            elsewhere, // dead-end redirect: where matches DO exist when the filtered result is empty
            didYouMean, // dead-end token-form suggestion (Mode4 -> "Mode 4"); a suggestion, never a substitution
            nextCursor = (hadMore || truncated) ? $"o:{offset + items.Count}" : null,
            truncated,
            // Contextual, not verbatim (feedback: the fixed explainer was duplicated token waste; the
            // whole-word/precise semantics live in the tool description). Present only when it changes
            // the caller's next move: redirect, absent, partial-leads, or common-term steering.
            note,
            noteId, // a0b: stable id for the cause (only the cataloged causes carry one)
            meta,
        });
    }

    // Best-effort owning symbol per .cs hit — BATCHED (one grouped query per ~40 keys) instead of one
    // InnermostSymbolAt point query per hit (9fr N+1: a full page issued up to ~100 queries).
    private static Dictionary<(string Path, int Line), string> OwningSymbols(IndexQueries q, IEnumerable<TextHit> hits)
    {
        var keys = hits.Where(h => h.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(h => (h.FilePath, h.Line)).Distinct().ToList();
        var owners = new Dictionary<(string, int), string>();
        foreach (var (key, sym) in q.InnermostSymbolsAt(keys))
            owners[key] = sym.Container is { Length: > 0 } c ? $"{c}.{sym.Name}" : sym.Name;
        return owners;
    }

    // Regex mode for search_text (Batch B): .NET regex over indexed content, FTS-narrowed by required
    // literals when possible, else a bounded scan; ReDoS-guarded. Mirrors the token response shape
    // (context lines, containingSymbol, noise) so callers get one consistent hit format.
    private string RegexResponse(IndexQueries q, string pattern, string? pathGlob, string? excludePath,
        bool firstPartyOnly, string? project, string scope, string? lang, bool includeGenerated,
        int limit, int offset, int ctxBefore, int ctxAfter)
    {
        var filter = new IndexQueries.TextFilter(
            PathGlob: pathGlob, Project: project, IncludeGenerated: includeGenerated,
            TestsOnly: scope switch { "tests" => true, "production" => false, _ => null },
            Lang: lang, ExcludePaths: BuildExcludes(excludePath, firstPartyOnly));
        var res = q.SearchRegex(pattern, filter, maxCandidateFiles: 300, offset, limit + 1, ctxBefore, ctxAfter);
        if (res.Error is not null)
            return Json.Serialize(new { error = "bad_request", detail = res.Error, meta = Meta.From(_manager.Health(), "indexed", "text") });

        var hits = res.Hits;
        bool hadMore = hits.Count > limit;
        if (hadMore) hits.RemoveAt(hits.Count - 1);

        // Owning symbol per .cs hit — parity with token search_text (feedback loved containingSymbol).
        var owners = OwningSymbols(q, hits);

        // Contextual note — only when it changes the caller's next move (timeout, clipped coverage, or
        // a zero-hit that needs the line-based/case-sensitivity reminder). Silent on a clean success.
        bool scoped = pathGlob is { Length: > 0 } || excludePath is { Length: > 0 } || firstPartyOnly
            || project is not null || scope != "all" || lang is not null;
        bool coverageClipped = res.FilesTotal > res.FilesScanned;
        string? note = res.TimedOut
            ? "PARTIAL: the scan hit its time budget. Narrow with pathGlob or add a distinctive whole-word literal (e.g. \\bWord\\b) so FTS can pre-narrow."
            : coverageClipped
                ? $"PARTIAL coverage: scanned {res.FilesScanned} of {res.FilesTotal} candidate files (cap) — narrow with pathGlob or add a whole-word literal (\\bWord\\b) so FTS can pre-narrow."
                : res.TotalMatches == 0
                    ? ".NET regex is LINE-BASED — a pattern spanning multiple lines matches NOTHING — and case-sensitive without (?i)."
                      + (scoped ? " The scan honored your filters; retry without them to check elsewhere." : "")
                    : null;

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            mode = "regex",
            matchCount = res.TotalMatches,
            filesScanned = res.FilesScanned,
            // Coverage honesty: candidates in scope BEFORE the cap; budgetHit means coverage was clipped
            // (cap or timeout), so "0 matches" or a small count is NOT proof of absence.
            filesTotal = res.FilesTotal,
            budgetHit = coverageClipped ? true : (bool?)null,
            narrowed = res.Narrowed,                 // FTS-pre-narrowed by required literals, vs a full scan
            narrowedOn = res.Literals,               // WHICH whole-token literals narrowed (null on a scan)
            timedOut = res.TimedOut ? true : (bool?)null,
            hits = items.Select(t => new
            {
                path = t.FilePath,
                t.Line,
                text = t.LineText,
                before = t.Before,
                after = t.After,
                t.IsGenerated,
                containingSymbol = owners.TryGetValue((t.FilePath, t.Line), out var cs) ? cs : null,
                noise = IndexQueries.IsVendorPath(t.FilePath) ? true : (bool?)null,
            }),
            nextCursor = (hadMore || truncated) ? $"o:{offset + items.Count}" : null,
            truncated,
            note,
            meta,
        });
    }

    // ---------------------------------------------------------------- outline / source

    [McpServerTool(Name = "outline")]
    [Description("Syntactic map of a file (namespaces, types, members with line spans) without reading the body. ALWAYS call this before reading a large file, then fetch only needed spans via source_context.")]
    public string Outline(
        [Description("Workspace-relative file path (forward slashes).")] string path,
        [Description("1 = namespaces + types, 2 = + members (default), 3 = reserved (currently same as 2).")] int depth = 2)
    {
        if (NotReady() is { } notReady) return notReady;
        string normPath = NormalizePath(path);
        using var q = _manager.OpenQueries();
        var rows = q.Outline(normPath);
        if (rows.Count == 0)
        {
            return Json.Serialize(new { error = "file_not_indexed", path, meta = Meta.From(_manager.Health(), "indexed", "syntax") });
        }

        var byId = rows.ToDictionary(r => r.Id);
        var children = new Dictionary<long, List<SymbolHit>>();
        var roots = new List<SymbolHit>();
        foreach (var row in rows)
        {
            if (row.ParentId is { } pid && byId.ContainsKey(pid))
            {
                (children.TryGetValue(pid, out var list) ? list : children[pid] = new()).Add(row);
            }
            else
            {
                roots.Add(row);
            }
        }

        // Memoized per type identity: BuildNested runs up to twice on budget degradation,
        // and batch_outline multiplies calls — one lookup per unique partial type, total.
        // Arity is part of the identity (szs): partial Foo and partial Foo<T> in the SAME file
        // are different types with different partial-file sets — one cache slot each.
        var partialCache = new Dictionary<(string Name, string? Ns, string Kind, string? Container, int Arity), List<string>>();

        // includeMembers=false keeps only namespace/type nodes (the depth-1 view);
        // true adds member leaves (methods, properties, ...) — the depth-2 view.
        object Node(SymbolHit s, bool includeMembers)
        {
            List<object>? memberNodes = null;
            if (children.TryGetValue(s.Id, out var kids))
            {
                var kept = kids
                    .Where(k => includeMembers || TypeKinds.Contains(k.Kind) || k.Kind == "namespace")
                    .Select(k => Node(k, includeMembers))
                    .ToList();
                if (kept.Count > 0) memberNodes = kept;
            }
            // Partial-type cross-links (feedback): the other files declaring this type,
            // so a caller need not run definition(name) just to find them.
            List<string>? partialFiles = null;
            bool partialFilesMore = false;
            if (s.IsPartial && TypeKinds.Contains(s.Kind))
            {
                var key = (s.Name, s.Ns, s.Kind, s.Container, s.Arity);
                if (!partialCache.TryGetValue(key, out var others))
                {
                    // Arity-matched (szs): without it, outline(FooOfT.cs) listed partial class Foo's
                    // files as Foo<T>'s "other halves" — navigation to the WRONG type's declarations.
                    others = q.PartialDeclarationFiles(s.Name, s.Ns, s.Kind, s.Container, normPath, s.Arity); // up to 11
                    partialCache[key] = others;
                }
                if (others.Count > 0)
                {
                    partialFilesMore = others.Count > 10;
                    partialFiles = partialFilesMore ? others.Take(10).ToList() : others;
                }
            }
            return new
            {
                s.Name,
                s.Kind,
                s.Signature,
                s.Accessibility,
                modifiers = s.Modifiers, // bt7: virtual/override/abstract/static/sealed..., omitted when none
                accessors = AccessorsJson(s.Accessors), // hu7: {get, set} only when an accessor differs
                s.StartLine,
                s.EndLine,
                isPartial = s.IsPartial ? true : (bool?)null,
                partialFiles,
                partialFilesTruncated = partialFilesMore ? true : (bool?)null,
                attributes = s.AttrMarkers,
                members = memberNodes,
            };
        }

        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        bool generated = rows[0].FileIsGenerated;

        string BuildNested(bool includeMembers, bool truncated) => Json.Serialize(new
        {
            path,
            isGenerated = generated,
            symbols = roots.Select(r => Node(r, includeMembers)).ToList(),
            truncated,
            meta,
        });

        // The nested tree lives under one namespace root, so trimming the top-level list
        // cannot bound it. Degrade instead: requested depth -> types-only -> flat capped.
        string nested = BuildNested(includeMembers: depth >= 2, truncated: false);
        if (Json.Utf8Bytes(nested) <= Json.HardBudgetBytes) return nested;

        if (depth >= 2)
        {
            string typesOnly = BuildNested(includeMembers: false, truncated: true);
            if (Json.Utf8Bytes(typesOnly) <= Json.HardBudgetBytes) return typesOnly;
        }

        // Pathological (thousands of types in one file): flatten namespace/type nodes to
        // a bounded row list and let the list budget converge.
        var flat = rows
            .Where(r => r.Kind == "namespace" || TypeKinds.Contains(r.Kind))
            .Select(r => (object)new { r.Name, r.Kind, ns = r.Ns, r.StartLine, r.EndLine })
            .ToList();
        return Json.WithListBudget(flat, (items, _) => new
        {
            path,
            isGenerated = generated,
            symbols = items,
            truncated = true,
            note = "File has too many top-level declarations for a full outline; showing a bounded flat list.",
            meta,
        });
    }

    [McpServerTool(Name = "source_context")]
    [Description("Bounded live source read around one or more line spans (the bridge from navigation results to actual code). Use spans from outline/definition/search results instead of reading whole files.")]
    public string SourceContext(
        [Description("Workspace-relative file path. Optional when symbolId is given.")] string? path = null,
        [Description("Spans as 'start-end' or 'line', comma-separated (e.g. '42-88,120'). Optional when symbolId is given (defaults to the symbol's own declaration span).")] string spans = "",
        [Description("Extra context lines around each span (default 2).")] int contextLines = 2,
        [Description("Byte budget for returned source (default 8192, max 24576).")] int maxBytes = 8192,
        [Description("Show one symbol's source by handle instead of path+spans: 'idx:NNN' from a prior result. Overrides path/spans with the symbol's declaration span. Note: 'idx:' handles are index-local and change on reindex.")] string? symbolId = null)
    {
        if (NotReady() is { } notReady) return notReady;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            path = hit!.FilePath;
            spans = $"{hit.StartLine}-{hit.EndLine}";
        }
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(spans))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'symbolId', or 'path'+'spans'." });
        }
        path = NormalizePath(path);
        maxBytes = Math.Clamp(maxBytes, 256, Json.HardBudgetBytes);
        // Clamped BEFORE it enters line arithmetic: an absurd contextLines (int.MaxValue) would
        // overflow start/end math and defeat the bounded read below.
        contextLines = Math.Clamp(contextLines, 0, 500);

        // Parse the span specs BEFORE reading anything (gep): the requested spans bound how much
        // of the file we ever materialize. Unparsable specs are skipped. Zero/negative starts are
        // CLAMPED to line 1, not rejected — the old code accepted them ("0-10" rendered lines
        // 1..12) and 0-based callers are common; rejecting would be a silent-empty (review).
        var ranges = new List<(int Start, int End)>();
        foreach (var spec in spans.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = spec.Split('-');
            if (!int.TryParse(parts[0], out int start)) continue;
            int end = parts.Length > 1 && int.TryParse(parts[1], out int e) ? e : start;
            start = Math.Max(1, start);
            end = Math.Max(1, end);
            if (end < start) continue; // inverted range — yields nothing
            ranges.Add((start, end));
        }
        // Long arithmetic: end near int.MaxValue plus context must saturate, not wrap negative.
        int maxNeededLine = 0;
        foreach (var r in ranges)
            maxNeededLine = (int)Math.Min(int.MaxValue, Math.Max(maxNeededLine, r.End + (long)contextLines));

        // Reject paths that escape the workspace root before touching the filesystem.
        if (!CodeNav.Core.WorkspacePaths.TryResolveInside(_manager.WorkspaceRoot, path, out string full))
        {
            return Json.Serialize(new { error = "path_outside_workspace", path, meta = Meta.From(_manager.Health(), "indexed", "text") });
        }

        string freshness = "live";
        // Live read is BOUNDED (gep): stream lines only up to the last requested line and stop —
        // never File.ReadAllText, which materialized entire files (a multi-hundred-MB artifact in
        // the workspace = one allocation spike per call) just to slice a few lines out.
        // Skip paths reaching outside via a symlink/junction (target or any ancestor) so an
        // in-workspace link cannot be followed to external content; fall through to the index.
        IReadOnlyList<string>? lines = null;
        if (File.Exists(full) && !CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(_manager.WorkspaceRoot, full))
        {
            lines = ReadLinesUpTo(full, maxNeededLine);
        }
        if (lines is null)
        {
            // Index fallback is keyed by relative path, so it can only ever return in-workspace
            // content — a contained path that is simply not on disk. This path still materializes
            // the stored content whole (no per-file size cap exists yet — rs7); the DoS-relevant
            // vector was the LIVE read of arbitrary on-disk files, which is now bounded above.
            using var q = _manager.OpenQueries();
            string? content = q.ContentByPath(path);
            if (content is not null) lines = content.Split('\n');
            freshness = "index";
        }
        if (lines is null)
        {
            return Json.Serialize(new { error = "file_not_found", path, meta = Meta.From(_manager.Health(), "indexed", "text") });
        }

        (List<object> Spans, bool Truncated) BuildSpans(long rawBudget)
        {
            var spanResults = new List<object>();
            long budget = rawBudget;
            bool truncated = false;
            foreach (var (rawStart, rawEnd) in ranges)
            {
                int start = Math.Max(1, rawStart - contextLines);
                int end = Math.Min(lines.Count, (int)Math.Min(int.MaxValue, rawEnd + (long)contextLines));

                var numbered = new List<string>();
                for (int i = start; i <= end; i++)
                {
                    string line = $"{i,5}| {lines[i - 1].TrimEnd('\r')}";
                    int cost = Json.Utf8Bytes(line) + 1; // budget is a UTF-8 byte contract
                    if (budget - cost < 0) { truncated = true; break; }
                    budget -= cost;
                    numbered.Add(line);
                }
                // Skip spans that yielded nothing (e.g. start past EOF) — no inverted ranges.
                if (numbered.Count > 0)
                    spanResults.Add(new { startLine = start, endLine = start + numbered.Count - 1, source = string.Join("\n", numbered) });
                if (truncated) break;
            }
            return (spanResults, truncated);
        }

        string BuildResponse(long rawBudget)
        {
            var (spanResults, truncated) = BuildSpans(rawBudget);
            // When JSON-escaping headroom forced rawBudget below the caller's maxBytes, "raise
            // maxBytes" is unfollowable (they may already be at the max) — advise narrowing instead.
            string? hint = !truncated ? null
                : rawBudget < maxBytes
                    ? $"cut at {rawBudget} bytes (escaping headroom below your maxBytes {maxBytes}) — narrow the spans"
                    : maxBytes >= Json.HardBudgetBytes
                        ? $"cut at {rawBudget} bytes (at the max) — narrow the spans"
                        : $"cut at {rawBudget} bytes — raise maxBytes (max {Json.HardBudgetBytes}) or narrow the spans";
            return Json.Serialize(new
            {
                path,
                freshness,
                spans = spanResults,
                truncated,
                hint,
                meta = Meta.From(_manager.Health(), freshness == "live" ? "exact" : "indexed", "text"),
            });
        }

        // Raw budget bounds the source bytes; JSON escaping still inflates, so shrink the raw
        // budget until the SERIALIZED response fits the hard cap.
        long effective = maxBytes;
        string json = BuildResponse(effective);
        while (Json.Utf8Bytes(json) > Json.HardBudgetBytes && effective > 256)
        {
            effective /= 2;
            json = BuildResponse(effective);
        }
        return json;
    }

    /// <summary>Streams at most <paramref name="maxLines"/> lines from a file, then stops reading
    /// (gep): the caller's spans bound the read, so a giant file costs only its requested prefix —
    /// never a whole-file materialization. Returns null on IO/access failure (caller falls back to
    /// index content). Line endings are normalized by ReadLine; the formatter's TrimEnd('\r') stays
    /// correct for both this path and the index path's Split('\n').</summary>
    private static List<string>? ReadLinesUpTo(string fullPath, int maxLines)
    {
        try
        {
            var list = new List<string>(Math.Min(maxLines, 4096));
            using var sr = new StreamReader(fullPath);
            string? line;
            while (list.Count < maxLines && (line = sr.ReadLine()) is not null) list.Add(line);
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------------------------------------------------------------- symbols

    [McpServerTool(Name = "search_symbol")]
    [Description("Find declared symbols by name across the workspace (types, methods, properties...). Prefer this over search_text for anything that is a code identifier. Scope with pathGlob / excludePath / namespace (e.g. excludePath='3rdparty/**' to drop vendored third-party source). Hits carry an 'orphaned' flag (present only when true) for files in NO project's compile set — dead code the compiler never builds (Compile Include globs expanded, Compile Remove honored).")]
    public string SearchSymbol(
        [Description("Symbol name. Match behavior set by 'match'. Empty (or '*') with a 'namespace' or 'pathGlob' ENUMERATES that scope's symbols instead — kind-filterable, paged.")] string query = "",
        [Description("Comma-separated kind filter: class,interface,struct,record,enum,delegate,method,constructor,property,field,event,enum_member. Empty = all.")] string? kinds = null,
        [Description("'auto' (exact, then prefix, then substring), 'exact', 'prefix', or 'substring'.")] string match = "auto",
        [Description("Include symbols in generated files (default false).")] bool includeGenerated = false,
        [Description("Restrict to file paths matching this glob (e.g. 'SOAPAPI/**'); a bare name matches at any depth.")] string? pathGlob = null,
        [Description("Exclude file paths matching this glob (e.g. '3rdparty/**' to drop vendored third-party source).")] string? excludePath = null,
        [Description("Drop hits under known vendor/generated dir names (3rdparty, vendor, external, generated...) at ANY depth. Matches the per-hit noise flag; convenience over excludePath. See repo_overview.suggestedExcludes for the dirs actually present.")] bool firstPartyOnly = false,
        [Description("Restrict to a namespace subtree: the exact namespace or anything nested under it (e.g. 'ExactTarget.Integration'). Distinct from a containing type.")] string? @namespace = null,
        [Description("Max results (default 20, max 100).")] int limit = 20,
        [Description("Opaque cursor from a previous call.")] string? cursor = null)
    {
        if (NotReady() is { } notReady) return notReady;
        (limit, int offset, string? cursorMode) = Page(limit, cursor);
        var kindList = SplitCsv(kinds);
        using var q = _manager.OpenQueries();
        var excludes = BuildExcludes(excludePath, firstPartyOnly);

        List<SymbolHit> hits;
        string effectiveMatch = match;
        if (string.IsNullOrWhiteSpace(query) || query.Trim() == "*")
        {
            // Enumeration mode (field evidence: an agent passed kinds+namespace with no name expecting
            // the namespace's classes, got a silent [] and had to guess a name). An empty name within a
            // namespace/pathGlob scope lists that scope; without a scope it is an explicit error —
            // a silent empty result is the one answer that helps nobody.
            if (!(@namespace is { Length: > 0 } || pathGlob is { Length: > 0 }))
            {
                return Json.Serialize(new
                {
                    error = "bad_request",
                    detail = "An empty name enumerates a scope — provide 'namespace' or 'pathGlob' (optionally 'kinds').",
                    meta = Meta.From(_manager.Health(), "indexed", "syntax"),
                });
            }
            hits = q.SearchSymbols("", "prefix", kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
            effectiveMatch = "enumerate";
        }
        else if (match == "auto" && cursorMode is "exact" or "prefix" or "substring")
        {
            // Continue the mode resolved on page 1. Re-running the exact->prefix->substring ladder on a
            // later page fails: the fallback is gated to offset==0, so exact-at-offset returns [] and the
            // page comes back empty, losing the prefix/substring results (bug cli).
            effectiveMatch = cursorMode;
            hits = q.SearchSymbols(query, cursorMode, kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
        }
        else if (match == "auto")
        {
            hits = q.SearchSymbols(query, "exact", kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
            effectiveMatch = "exact";
            if (hits.Count == 0 && offset == 0)
            {
                hits = q.SearchSymbols(query, "prefix", kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
                effectiveMatch = "prefix";
            }
            if (hits.Count == 0 && offset == 0)
            {
                hits = q.SearchSymbols(query, "substring", kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
                effectiveMatch = "substring";
            }
        }
        else
        {
            hits = q.SearchSymbols(query, match, kindList, limit + 1, includeGenerated, offset, pathGlob, excludes, @namespace);
        }

        // Flag hits in files that no project compiles — the "really compiled?" signal grep can't give
        // (phoenix has the compile graph; 3tz expands Include globs + honors Remove). Additive ONLY:
        // the hit is still returned and tagged orphaned:true, never hidden — residual gaps (shared
        // .projitems, props globs, ignored Conditions) mean hiding could still bury live code.
        if (hits.Count > 0)
        {
            var orphaned = q.OrphanedPaths(hits.Select(h => h.FilePath).ToList());
            if (orphaned.Count > 0)
                hits = hits.Select(h => orphaned.Contains(h.FilePath) ? h with { IsOrphaned = true } : h).ToList();
        }

        bool hadMore = hits.Count > limit;
        if (hadMore) hits.RemoveAt(hits.Count - 1);

        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        return Json.WithListBudget(hits, (items, truncated) => new
        {
            matchMode = effectiveMatch,
            symbols = items.Select(SymbolJson),
            // Carry the resolved mode so a later page continues it (bug cli); resume at the returned
            // count so a byte-budget shrink doesn't skip the dropped tail (bug e2q).
            nextCursor = (hadMore || truncated) ? $"o:{offset + items.Count}:{effectiveMatch}" : null,
            truncated,
            // Steer the follow-up (feedback: nothing nudged toward references after a symbol
            // hit). First page only — repeating it on cursored pages just burns budget.
            hint = items.Count > 0 && cursor is null
                ? "Next: references(name) for usages by project; definition(name, includeBody:true) for the declaration with source."
                : null,
            meta,
        });
    }

    [McpServerTool(Name = "symbol_at")]
    [Description("Reverse lookup: given a file + line (from a stack trace, build error, diff hunk, or grep hit), returns the smallest containing symbol and its enclosing chain plus owning projects.")]
    public string SymbolAt(
        [Description("Workspace-relative file path.")] string path,
        [Description("1-based line number.")] int line,
        [Description("Optional 1-based column (currently unused; line-level resolution).")] int column = 0)
    {
        if (NotReady() is { } notReady) return notReady;
        path = NormalizePath(path);
        using var q = _manager.OpenQueries();
        var chain = q.SymbolAt(path, line);
        var projects = q.ProjectsContaining(path);
        return Json.Serialize(new
        {
            path,
            line,
            found = chain.Count > 0,
            chain = chain.Select(SymbolJson),
            owningProjects = projects.Select(p => new { p.Name, p.Path, p.Style, p.IsTest }),
            meta = Meta.From(_manager.Health(), "indexed", "syntax"),
        });
    }

    [McpServerTool(Name = "definition")]
    [Description("Declaration site(s) for a symbol — all partial declarations included. Target by exact name (optionally 'container' to disambiguate) OR by position (path+line[,column]) from a usage site. Tries compiler-exact resolution first (confidence 'exact' with documentationCommentId), falling back to the name index. includeBody=true also returns the primary declaration's source inline — no follow-up source_context needed.")]
    public string Definition(
        [Description("Exact symbol name (case-insensitive). Optional when path+line given.")] string? name = null,
        [Description("Optional containing type or namespace fragment to disambiguate.")] string? container = null,
        [Description("Comma-separated kind filter (defaults to all kinds).")] string? kinds = null,
        [Description("Workspace-relative file path of a usage or declaration site (position mode).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("'auto' (semantic first, indexed fallback), 'semantic', or 'indexed'.")] string mode = "auto",
        [Description("Semantic resolution deadline in ms (default 10000).")] int timeoutMs = 10000,
        [Description("Also return the primary declaration's source body (numbered lines, budget-bounded).")] bool includeBody = false,
        [Description("Byte budget for the inline body (default 12288, max 16384).")] int bodyMaxBytes = 12288,
        [Description("Resolve by a prior result's handle instead of name/position: 'idx:NNN' (from search_symbol / symbol_at / definition). Takes precedence over name and path+line. Note: 'idx:' handles are index-local and change on reindex; a documentationCommentId is not yet accepted here.")] string? symbolId = null)
    {
        if (NotReady() is { } notReady) return notReady;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            name = hit!.Name; path = hit.FilePath; line = hit.StartLine; column = 0;
            // The handle already disambiguated the symbol — caller kinds/container filters exist to
            // narrow a bare name, so applying them here can only wrongly suppress the resolved hit.
            kinds = null; container = null;
        }
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'symbolId', 'name', or 'path'+'line'." });
        }

        string? failReason = null;
        if (mode is "auto" or "semantic")
        {
            int deadlineMs = Math.Clamp(timeoutMs, 500, 60000); // mirror DefinitionAsync's clamp (24n)
            var swSem = System.Diagnostics.Stopwatch.StartNew();
            var (target, hint) = ResolveSemanticTarget(name, container, kinds, path, line, column);
            if (target is { } t)
            {
                var (decl, reason, projectModelUnproven) = _semantic
                    .DefinitionAsync(t.Path, t.Line, t.Column, hint, timeoutMs)
                    .GetAwaiter().GetResult();
                if (decl is not null)
                {
                    // Order declarations largest-span-first (partial stubs lose), path as the
                    // deterministic tie-break — so the body's declaration (d0) is always the first
                    // shown and never trimmed out of the displayed set.
                    var ordered = decl.Declarations
                        .OrderByDescending(d => d.EndLine - d.StartLine)
                        .ThenBy(d => d.Path, StringComparer.Ordinal)
                        .ToList();
                    var d0 = ordered.FirstOrDefault();
                    int totalDecls = ordered.Count;
                    var shown = ordered.Take(MaxDeclarationSites).ToList();
                    // Declarations serialized ONCE here (symbol omits them) and adaptively
                    // byte-bounded via WithListBudget — the semantic path no longer bypasses the
                    // central budget, so even a bodyless response with long paths stays under cap.
                    string BuildSemantic(object? body) => Json.WithListBudget(shown, (items, listTrunc) => new
                    {
                        name = name ?? decl.SymbolDisplay,
                        symbol = SemanticIdentityJson(decl),
                        declarations = items.Select(d => new { d.Path, d.StartLine, d.EndLine }),
                        declarationsTruncated = (listTrunc || totalDecls > MaxDeclarationSites) ? true : (bool?)null,
                        body,
                        partial = projectModelUnproven ? true : (bool?)null,
                        partialReason = projectModelUnproven ? "project_model_unproven" : null,
                        timing = new { deadlineMs, elapsedMs = swSem.ElapsedMilliseconds }, // 24n
                        meta = Meta.From(_manager.Health(),
                            projectModelUnproven ? "indexed" : "exact", "semantic"),
                    });
                    // Semantic spans come from live sources — pair them with live content.
                    object? MakeSemanticBody(int budget) =>
                        d0 is null ? null : BuildDeclarationBody(d0.Path, d0.StartLine, d0.EndLine, budget, preferLive: true);
                    return SerializeBodyBounded(BuildSemantic, includeBody ? MakeSemanticBody(bodyMaxBytes) : null, MakeSemanticBody, bodyMaxBytes);
                }
                failReason = ExpandReason(reason); // t2b: cold-load token gains inline retry advice
            }
            else
            {
                failReason = "target_not_found_in_index";
            }
            if (mode == "semantic")
            {
                return Json.Serialize(new
                {
                    error = "semantic_unavailable",
                    partialReason = failReason,
                    hint = "Retry with mode='indexed' for name-index declarations.",
                    timing = new { deadlineMs, elapsedMs = swSem.ElapsedMilliseconds }, // 24n: was the deadline the cause?
                    meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                });
            }
        }

        // Indexed fallback (name required).
        using var q = _manager.OpenQueries();
        string lookupName = name ?? "";
        if (lookupName.Length == 0 && path is not null)
        {
            var chain = q.SymbolAt(NormalizePath(path), line);
            lookupName = chain.Count > 0 ? chain[0].Name : "";
        }
        var hits = q.SearchSymbols(lookupName, "exact", SplitCsv(kinds), 100, includeGenerated: true);
        if (container is { } c)
        {
            hits = hits.Where(h =>
                (h.Container?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Ns?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        // Same primary-declaration rule as the semantic path: largest span first (partial
        // stubs lose), path as the deterministic tie-break — so the two paths agree.
        var primary = hits
            .OrderByDescending(h => h.EndLine - h.StartLine)
            .ThenBy(h => h.FilePath, StringComparer.Ordinal)
            .FirstOrDefault();
        var meta = Meta.From(_manager.Health(), "indexed", "syntax");
        // Indexed spans come from the index — pair them with index content (consistent even
        // when the working tree has drifted; freshness is reported on the body).
        object? MakeBody(int budget) =>
            primary is null ? null : BuildDeclarationBody(primary.FilePath, primary.StartLine, primary.EndLine, budget, preferLive: false);
        string Build(object? body) => Json.WithListBudget(hits, (items, truncated) => new
        {
            name = lookupName,
            declarations = items.Select(SymbolJson),
            body,
            partialReason = failReason,
            hint = items.Count == 0
                ? "No declaration found. Try search_symbol with match='substring', or the name may come from a package/generated source."
                : null,
            truncated,
            meta,
        });
        return SerializeBodyBounded(Build, includeBody ? MakeBody(bodyMaxBytes) : null, MakeBody, bodyMaxBytes);
    }

    /// <summary>Numbered source for a declaration span, byte-bounded — what lets
    /// definition(includeBody:true) replace a follow-up source_context call.
    /// preferLive=true reads the working-tree file (the semantic path computed its spans from
    /// live sources, so index content could mismatch them); preferLive=false uses index
    /// content (the indexed path's spans come from the index, so that pairing is consistent).
    /// Returns an { omitted, reason } object instead of null when no content is available.</summary>
    internal object BuildDeclarationBody(string path, int startLine, int endLine, int maxBytes, bool preferLive) // internal for tests (trp)
    {
        maxBytes = Math.Clamp(maxBytes, 512, 16 * 1024);

        string? content = null;
        string freshness = "index";
        if (preferLive
            && CodeNav.Core.WorkspacePaths.TryResolveInside(_manager.WorkspaceRoot, path, out string full)
            && File.Exists(full)
            && !CodeNav.Core.WorkspacePaths.EscapesViaReparsePoint(_manager.WorkspaceRoot, full))
        {
            try
            {
                content = File.ReadAllText(full);
                freshness = "live";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* fall through to index content */ }
        }
        if (content is null)
        {
            using var q = _manager.OpenQueries();
            content = q.ContentByPath(path);
            freshness = "index";
        }
        if (content is null)
        {
            return new { omitted = true, reason = "content_unavailable", path };
        }

        var lines = content.Split('\n');
        int start = Math.Max(1, startLine);
        // Span beyond the (possibly stale) content: report it honestly instead of an inverted
        // empty span (start past EOF is reachable when live spans are applied to shorter index content).
        if (start > lines.Length)
        {
            return new { omitted = true, reason = "span_beyond_content", path, contentLines = lines.Length, freshness };
        }
        int end = Math.Min(lines.Length, Math.Max(endLine, start));
        var numbered = new List<string>();
        long budget = maxBytes;
        bool truncated = false;
        for (int i = start; i <= end; i++)
        {
            string lineText = $"{i,5}| {lines[i - 1].TrimEnd('\r')}";
            int cost = Json.Utf8Bytes(lineText) + 1;
            if (budget - cost < 0) { truncated = true; break; }
            budget -= cost;
            numbered.Add(lineText);
        }
        if (numbered.Count == 0)
        {
            // Even the first line overflowed the budget — nothing to show, but say so.
            return new { omitted = true, reason = "first_line_exceeds_budget", path, freshness };
        }
        int lastIncluded = start + numbered.Count - 1;
        return new
        {
            path,
            startLine = start,
            endLine = lastIncluded,
            source = string.Join("\n", numbered),
            truncated,
            hint = truncated
                ? $"body cut at line {lastIncluded} — source_context('{path}', '{lastIncluded + 1}-{end}', maxBytes: {Json.HardBudgetBytes}) resumes where this stopped"
                : null,
            freshness,
        };
    }

    /// <summary>Re-serializes a body-carrying response until the ESCAPED payload fits the hard
    /// budget, halving the body budget each pass and dropping the body as the last resort.
    /// The line-loop budget counts raw chars; JSON escaping (quotes/backslashes) inflates, so
    /// only measuring the serialized length makes the budget contract actually hold.</summary>
    private static string SerializeBodyBounded(Func<object?, string> serialize, object? body, Func<int, object?> rebuildBody, int bodyMaxBytes)
    {
        string json = serialize(body);
        int budget = Math.Clamp(bodyMaxBytes, 512, 16 * 1024); // seed matches BuildDeclarationBody's own clamp
        while (Json.Utf8Bytes(json) > Json.HardBudgetBytes && body is not null)
        {
            budget /= 2;
            body = budget >= 512 ? rebuildBody(budget) : null;
            json = serialize(body);
        }
        return json;
    }

    [McpServerTool(Name = "implementations")]
    [Description("Implementations of an interface (or interface member), derived classes, and overrides — RANKED concrete-first (instantiable leaves before abstract scaffolding), each with its derivation path (via). Generic declarations may be selected by arity or symbolId; a bare name spanning multiple arities returns symbol_ambiguous. A single concrete implementation is flagged as likelyImplementation (the probable runtime target). Compiler-exact within the loaded cluster; falls back to arity-aware base-list syntax matching (confidence 'heuristic', unranked). For an interface MEMBER, the syntactic fallback (when compiler-exact override resolution finds none) reports implementerCount and omittedImplementers (silent when none omitted); the exact path reports coverage instead.")]
    public string Implementations(
        [Description("Interface/type/member name. Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of the declaration or a usage (position mode).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("Candidate-project budget; 0 (default) loads all matching projects, while a positive value opts into a bound.")] int maxProjects = SemanticService.DefaultCandidateProjectBudget,
        [Description("Semantic deadline in ms (default 15000).")] int timeoutMs = 15000,
        [Description("Optional generic type-parameter count. Use 0 for a non-generic declaration, 1 for Foo<T>, etc. A bare name with multiple available arities is refused.")] int? arity = null,
        [Description("Resolve by a search_symbol candidate's current idx: handle. Takes precedence over name, path+line, and arity.")] string? symbolId = null)
    {
        if (NotReady() is { } notReady) return notReady;
        var (selection, selectionError) = ResolveArityTarget(
            name, path, line, column, arity, symbolId, typeOnly: false);
        if (selectionError is not null) return selectionError;
        name = selection!.Name;
        path = selection.Path;
        line = selection.Line;
        column = selection.Column;
        arity = selection.Arity;
        bool allowHeuristicFallback = selection.AllowHeuristicFallback;

        string? failReason = null;
        // dve (b): when the compiler RESOLVED the symbol but found no implementers, the fallback
        // used to discard that exact identity and relabel EVERYTHING heuristic — keep it for
        // mixed-section honesty (symbol exact, list heuristic), mirroring type_hierarchy's
        // derivedConfidence split.
        SemanticDeclaration? resolvedSymbol = null;
        int deadlineMs = Math.Clamp(timeoutMs, 500, 120000); // mirror the service clamp (24n)
        var swSem = System.Diagnostics.Stopwatch.StartNew();
        var (target, hint) = ResolveSemanticTarget(name, null, null, path, line, column, arity);
        if (target is { } t)
        {
            var (result, reason) = _semantic
                .ImplementationsAsync(t.Path, t.Line, t.Column, hint, maxProjects, timeoutMs, arity)
                .GetAwaiter().GetResult();
            resolvedSymbol = result?.Symbol;
            if (result is { Implementations.Count: > 0 })
            {
                var impls = result.Implementations; // already ranked concrete-first by the semantic layer
                int concreteCount = impls.Count(r => !r.Declaration.IsAbstract);
                bool exhausted = result.DeadlineExhausted;
                bool bounded = result.SkippedCandidateProjects.Count > 0 ||
                    result.Coverage.FailedProjects.Count > 0 ||
                    result.Coverage.LoadedProjects < result.Coverage.RequestedProjects;
                bool partial = exhausted || bounded || result.ProjectModelUnproven;
                var meta0 = Meta.From(_manager.Health(),
                    result.ProjectModelUnproven ? "indexed" : "exact", "semantic");
                long elapsedMs = swSem.ElapsedMilliseconds;
                return Json.WithAuxiliaryListBudget(impls, result.SkippedCandidateProjects,
                    (items, truncated, skippedItems, skippedTruncated) => new
                {
                    symbol = SemanticSymbolJson(result.Symbol),
                    implementations = items.Select(r => new
                    {
                        symbol = SemanticSymbolJson(r.Declaration),
                        isAbstract = r.Declaration.IsAbstract ? true : (bool?)null, // omitted when concrete
                        rank = r.Declaration.IsAbstract ? "abstract" : "concrete", // make the ranking legible to the model
                        via = r.Via, // the base type that introduces the interface, when implemented indirectly
                    }),
                    concreteCount,
                    // High-signal case: exactly one instantiable implementation is very likely THE
                    // runtime type; anything else is abstract scaffolding. Never claimed when the
                    // deadline cut the search short — the "one" may just be the one found in time.
                    likelyImplementation = concreteCount == 1 && !partial
                        ? impls.First(r => !r.Declaration.IsAbstract).Declaration.SymbolDisplay
                        : null,
                    coverage = CoverageJson(result.Coverage),
                    skippedCandidateProjects = skippedItems.Count > 0
                        ? skippedItems
                        : null,
                    skippedCandidateProjectCount = result.SkippedCandidateProjects.Count > 0
                        ? result.SkippedCandidateProjects.Count
                        : (int?)null,
                    skippedCandidateProjectsTruncated = skippedTruncated ? true : (bool?)null,
                    partial = partial ? true : (bool?)null,
                    partialReason = exhausted
                        ? $"semantic_timeout: deadline exhausted after {elapsedMs}ms of {deadlineMs}ms; this list is a lower bound (raise timeoutMs)"
                        : bounded
                            ? $"candidate_cluster_bounded: skipped {result.SkippedCandidateProjects.Count} candidate projects and {result.Coverage.FailedProjects.Count} failed loads (raise maxProjects)"
                            : result.ProjectModelUnproven
                                ? "project_model_unproven"
                                : null,
                    // t2b: where the budget went — cluster load+resolve vs the finder passes.
                    timing = new { deadlineMs, elapsedMs, clusterLoadMs = result.ClusterLoadMs, queryMs = result.QueryMs },
                    truncated,
                    hint = concreteCount == 1 && !partial
                        ? "One concrete implementation — likely the runtime target. Ranked concrete-first; isAbstract/rank mark non-instantiable scaffolding."
                        : null,
                    meta = meta0,
                });
            }
            // Semantic RESOLVED the symbol but found no implementers, OR it could not resolve. Be
            // honest about which: bounded coverage (raising maxProjects may help) vs genuinely none.
            failReason = result is null ? ExpandReason(reason)
                : (result.SkippedCandidateProjects.Count > 0 ||
                   result.Coverage.LoadedProjects < result.Coverage.RequestedProjects ||
                   result.Coverage.FailedProjects.Count > 0)
                    ? "candidate_cluster_bounded"
                    : "no_semantic_implementers";
        }

        // Heuristic fallback: types whose base list textually mentions the name — a naming
        // guess, not a compiler fact, so it is labeled confidence 'heuristic'.
        using var q = _manager.OpenQueries();
        string lookupName = name ?? hint ?? "";
        SymbolHit? targetSym = null;
        // Position mode (path+line): resolve the cursor so the fallback has a name AND the target's
        // kind + declaring type — the base-list heuristic only makes sense for a TYPE target.
        if (path is not null)
        {
            string normalizedPath = NormalizePath(path);
            var lineSymbols = q.SymbolsStartingAt(normalizedPath, line);
            targetSym = lineSymbols.FirstOrDefault(hit =>
                (lookupName.Length == 0 || string.Equals(hit.Name, lookupName, StringComparison.OrdinalIgnoreCase)) &&
                (arity is null || hit.Arity == arity.Value));
            var chain = q.SymbolAt(normalizedPath, line);
            if (chain.Count > 0)
            {
                targetSym ??= chain[0];
                if (lookupName.Length == 0) lookupName = targetSym.Name;
            }
        }
        if (lookupName.Length == 0)
        {
            return Json.Serialize(new
            {
                error = "symbol_not_resolved",
                partialReason = failReason,
                meta = Meta.From(_manager.Health(), "heuristic", "syntax"),
            });
        }
        targetSym ??= q.SearchSymbols(
            lookupName, "exact", null, 1, arity: arity).FirstOrDefault();
        int? fallbackArity = targetSym?.Arity ?? arity;
        string? targetKind = targetSym?.Kind;
        var meta = Meta.From(_manager.Health(), "heuristic", "syntax");

        // The base-list heuristic is a TYPE operation. For a MEMBER target, scope to the declaring
        // type's syntactic implementers and return the SAME-named member in each — not the type-only
        // base-list sweep (pure noise), and not a bare empty when the members are actually there.
        if (targetKind is not (null or "interface" or "class" or "struct" or "record" or "record_struct"))
        {
            List<SymbolHit> memberImpls = new();
            int implementerCount = 0;
            if (targetSym?.Container is { Length: > 0 } declType)
            {
                // Scope the member lookup to the implementer types by (namespace, type name) IDENTITY —
                // so the query's cap bounds only genuine implementer members (not every same-simple-named
                // type across all namespaces) and an unrelated type can't sneak in. ImplementationCandidates
                // now whole-token-matches the base list, so a superstring interface (IFooBar) doesn't scope in.
                var typeKeys = q.ImplementationCandidates(declType, 100)
                    .Select(t => (t.Ns ?? "", t.Name))
                    .ToList();
                implementerCount = typeKeys.Count;
                if (typeKeys.Count > 0)
                    memberImpls = q.MembersNamedInTypes(lookupName, typeKeys, 100).Take(50).ToList();
            }
            if (memberImpls.Count > 0)
            {
                // Coverage transparency: an implementer that declares no such member (an interface impl
                // without this override) is legitimately omitted — say how many, so the caller knows.
                int matchedTypes = memberImpls.Select(m => (m.Ns ?? "", m.Container ?? "")).Distinct().Count();
                int omitted = Math.Max(0, implementerCount - matchedTypes);
                return Json.WithListBudget(memberImpls, (items, truncated) => new
                {
                    name = lookupName,
                    declaringType = targetSym!.Container,
                    // dve (b): same mixed-section honesty as the type fallback below.
                    symbol = resolvedSymbol is not null ? SemanticSymbolJson(resolvedSymbol) : null,
                    symbolConfidence = resolvedSymbol is not null ? "exact" : null,
                    implementationsConfidence = resolvedSymbol is not null ? "heuristic" : null,
                    implementerCount,
                    omittedImplementers = omitted > 0 ? omitted : (int?)null,
                    implementations = items.Select(SymbolJson),
                    partialReason = "member_scoped_syntactic",
                    note = $"Same-named members of the syntactic implementers of {targetSym!.Container} (confidence heuristic — compiler-exact override resolution found none, likely a type-twin identity mismatch)."
                        + (omitted > 0 ? $" {omitted} of {implementerCount} implementer(s) declare no such member and were omitted." : "")
                        + " Verify with source_context.",
                    truncated = truncated || memberImpls.Count >= 50,
                    meta,
                });
            }
            // Nothing to scope to — honest empty + the recovery note. Policy reason, not the transient
            // semantic one: the type-only heuristic won't help on retry.
            return Json.Serialize(new
            {
                name = lookupName,
                symbol = resolvedSymbol is not null ? SemanticSymbolJson(resolvedSymbol) : null,
                symbolConfidence = resolvedSymbol is not null ? "exact" : null,
                implementations = Array.Empty<object>(),
                partialReason = "member_fallback_type_scoped",
                semanticReason = failReason, // why the exact path returned nothing (context, not actionable)
                note = "No compiler-exact member implementations in the loaded cluster (possibly a type-twin identity mismatch), and no same-named member found in the declaring type's implementers. Run implementations on the declaring interface/type, then read this member in each implementer.",
                meta,
            });
        }

        var heuristic = allowHeuristicFallback
            ? q.ImplementationCandidates(lookupName, 50, fallbackArity)
            : new List<SymbolHit>();
        return Json.WithListBudget(heuristic, (items, truncated) => new
        {
            name = lookupName,
            // dve (b): mixed-section honesty, mirroring type_hierarchy.derivedConfidence — the
            // compiler-resolved identity survives into the fallback with its own confidence,
            // instead of the whole payload flattening to one heuristic label. Both fields are
            // omitted when the symbol itself never resolved (then meta's heuristic covers all).
            symbol = resolvedSymbol is not null ? SemanticSymbolJson(resolvedSymbol) : null,
            symbolConfidence = resolvedSymbol is not null ? "exact" : null,
            implementationsConfidence = resolvedSymbol is not null ? "heuristic" : null,
            implementations = items.Select(SymbolJson),
            partialReason = failReason ?? "semantic_unavailable",
            note = items.Count > 0 && failReason is "no_semantic_implementers" or "candidate_cluster_bounded"
                // Field (lhg): the old "declared in more than one assembly / generated twin" wording
                // went stale once compiled-awareness + assembly-ref edges landed — say what we now
                // actually know and what to do about it.
                ? "Compiler-exact resolution matched no implementers, but these types name it in their base list (confidence heuristic). Implementer projects were likely not loaded into the semantic cluster (raise maxProjects, or scope with pathGlob), or the implementers bind the name to a declaration outside the workspace. Verify with source_context."
                : "Base-list name matches from the index (confidence heuristic) — verify with source_context.",
            truncated = truncated || heuristic.Count >= 50, // count-capped even if the byte budget fit
            meta,
        });
    }

    [McpServerTool(Name = "references")]
    [Description("Where a symbol is used across the workspace, grouped by project with counts and sample lines. mode='auto' tries compiler-exact references (target by position path+line, or by name) scoped to candidate projects, falling back to index candidates. Exact references are usage-kind classified (kinds breakdown: call/construction/typeMention/attribute/nameof/xmldoc/usingDirective/baseList/typeof) — filter with usageKinds (e.g. 'call' to skip doc mentions) or publicConsumersOnly for external callers. Pass pathGlob/excludePath to scope candidates (e.g. excludePath='3rdparty/**'); a path filter runs the indexed candidate path so counts reflect the filter. Call before changing behavior.")]
    public string References(
        [Description("Symbol name (whole-identifier). Optional when path+line given.")] string? name = null,
        [Description("Workspace-relative path of a usage or declaration (position mode — most precise).")] string? path = null,
        [Description("1-based line for position mode.")] int line = 0,
        [Description("1-based column for position mode (optional).")] int column = 0,
        [Description("'auto' (semantic first), 'semantic', or 'indexed' (fast candidates).")] string mode = "auto",
        [Description("Include usages in test projects (default true).")] bool includeTests = true,
        [Description("Include usages in generated files (default false).")] bool includeGenerated = false,
        [Description("Comma-separated usage-kind filter — SEMANTIC (exact) path only: call, construction, typeMention, attribute, nameof, xmldoc, usingDirective, baseList, typeof, other. Counts and groups honor it (e.g. 'call,construction' = real executions only).")] string? usageKinds = null,
        [Description("Only usages OUTSIDE the symbol's own declaring PROJECT (project-scoped, NOT accessibility-scoped — the name is about API blast radius, not access modifiers). The external-consumer view; semantic path only.")] bool publicConsumersOnly = false,
        [Description("Restrict candidate paths to this glob (supplying a path filter runs indexed candidates).")] string? pathGlob = null,
        [Description("Exclude candidate paths matching this glob, e.g. '3rdparty/**' (supplying a path filter runs indexed candidates).")] string? excludePath = null,
        [Description("Max candidate files scanned in indexed mode (default 500).")] int maxFiles = 500,
        [Description("Candidate-project budget; 0 (default) loads all matching projects, while a positive value opts into a bound.")] int maxProjects = SemanticService.DefaultCandidateProjectBudget,
        [Description("Sample lines per project group (default 3).")] int samplesPerGroup = 3,
        [Description("Semantic deadline in ms (default 15000).")] int timeoutMs = 15000,
        [Description("Resolve by a prior result's handle instead of name/position: 'idx:NNN' (from search_symbol / symbol_at / definition). Takes precedence over name and path+line. Note: 'idx:' handles are index-local and change on reindex; a documentationCommentId is not yet accepted here.")] string? symbolId = null)
    {
        if (NotReady() is { } notReady) return notReady;
        if (symbolId is { Length: > 0 })
        {
            var (hit, error) = ResolveSymbolIdHandle(symbolId);
            if (error is not null) return error;
            name = hit!.Name; path = hit.FilePath; line = hit.StartLine; column = 0;
        }
        if (name is null && (path is null || line <= 0))
        {
            return Json.Serialize(new { error = "bad_request", detail = "Provide 'symbolId', 'name', or 'path'+'line'." });
        }

        // Path filters are honored precisely only on the indexed candidate path (semantic counts
        // are project-level and cannot be re-derived per path), so a filter forces indexed mode.
        bool hasPathFilter = pathGlob is { Length: > 0 } || excludePath is { Length: > 0 };
        string? failReason = hasPathFilter && mode != "indexed" ? "path_filter_ran_indexed_candidates" : null;
        // Usage-kind buckets + external-consumers view are syntax/compiler facts — semantic path only.
        var kindSet = SplitCsv(usageKinds) is { Count: > 0 } uk
            ? new HashSet<string>(uk, StringComparer.OrdinalIgnoreCase)
            : null;
        if (kindSet is not null)
        {
            // Validate up front: a typo ('calls') silently filtering everything to zero would read as
            // "dead code" at exact confidence — the silent-empty anti-pattern this codebase keeps killing.
            var unknown = kindSet.Where(k => !SemanticReferenceKinds.All.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
            {
                return Json.Serialize(new
                {
                    error = "bad_request",
                    detail = $"Unknown usageKinds value(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", SemanticReferenceKinds.All)}.",
                    meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                });
            }
        }
        if (mode is "auto" or "semantic" && !hasPathFilter)
        {
            // Deadline visibility (24n): every semantic response reports the effective deadline and
            // how much of it was spent — "why is this partial / slow?" needs numbers, not guesses.
            int deadlineMs = Math.Clamp(timeoutMs, 500, 120000);
            var swSem = System.Diagnostics.Stopwatch.StartNew();
            var (target, hint) = ResolveSemanticTarget(name, null, null, path, line, column);
            if (target is { } t)
            {
                var (result, reason) = _semantic
                    .ReferencesAsync(t.Path, t.Line, t.Column, hint, maxProjects, Math.Clamp(samplesPerGroup, 0, 10), timeoutMs, includeGenerated, kindSet, publicConsumersOnly, includeTests)
                    .GetAwaiter().GetResult();
                if (result is not null)
                {
                    // includeTests is filtered INSIDE the semantic scan, before counting (wu1) —
                    // so TotalLocations, KindCounts, Groups, and this summary all describe one set.
                    var groups0 = result.Groups;
                    int prod0 = groups0.Where(g => !g.IsTestProject).Sum(g => g.Count);
                    int test0 = groups0.Where(g => g.IsTestProject).Sum(g => g.Count);
                    // "0 test" would misread as "no test usages exist" when they were EXCLUDED.
                    string mix0 = includeTests ? $"{prod0} production, {test0} test" : $"{prod0} production; test projects excluded";
                    // Field 0.7.2 P2: an exact ZERO when the base-list index KNOWS implementers is
                    // almost certainly a loading gap, not dead code — say so, actionably. (The
                    // honesty posture says "0 is a fact", but here the tool holds contrary data.)
                    // GUARDED (review): the note must not fire on FILTER-caused zeros — with
                    // usageKinds:'call' the base-list namers it would cite are exactly the
                    // baseList usages the filter excluded, and "raise maxProjects" cannot help.
                    // Same for publicConsumersOnly/includeTests. And the advice is only honest
                    // when coverage was actually BOUNDED (skipped/failed/under-loaded) — post-
                    // seeds, an unfiltered zero with namers and full coverage shouldn't occur.
                    string? zeroNote = null;
                    bool zeroUnfiltered = kindSet is null && !publicConsumersOnly && includeTests;
                    bool coverageBounded = result.SkippedCandidateProjects.Count > 0
                        || result.Coverage.FailedProjects.Count > 0
                        || result.Coverage.LoadedProjects < result.Coverage.RequestedProjects;
                    if (result.TotalLocations == 0 && zeroUnfiltered && coverageBounded)
                    {
                        using var qz = _manager.OpenQueries();
                        string probeName = name ?? hint ?? "";
                        int baseListNamers = probeName.Length > 0 ? qz.ImplementationCandidates(probeName, 5).Count : 0;
                        if (baseListNamers > 0)
                        {
                            zeroNote = $"0 exact references, but {(baseListNamers >= 5 ? "5+" : baseListNamers.ToString())} indexed types name '{probeName}' in their base lists (see implementations) — coverage was bounded (see skipped/failed); raise maxProjects or scope with pathGlob.";
                        }
                    }
                    bool exhausted = result.DeadlineExhausted;
                    bool partial = exhausted || result.SkippedCandidateProjects.Count > 0 ||
                        result.Coverage.FailedProjects.Count > 0 || result.ProjectModelUnproven;
                    // "at least": exhausted counts are a salvaged lower bound (24n), never the census.
                    string atLeast = exhausted ? "at least " : "";
                    var meta0 = Meta.From(_manager.Health(),
                        result.ProjectModelUnproven ? "indexed" : "exact", "semantic");
                    long elapsedMs = swSem.ElapsedMilliseconds;
                    return Json.WithAuxiliaryListBudget(groups0, result.SkippedCandidateProjects,
                        (items, truncated, skippedItems, skippedTruncated) => new
                    {
                        symbol = SemanticSymbolJson(result.Symbol),
                        summary = $"{atLeast}{result.TotalLocations} {(result.ProjectModelUnproven ? "compiler-resolved candidate" : "exact")} references across {groups0.Count} projects ({mix0}).",
                        totalReferences = result.TotalLocations,
                        totalIsLowerBound = exhausted ? true : (bool?)null,
                        // HOW the symbol is used, e.g. {"call":20,"xmldoc":480} — the anti-"500 refs
                        // that are mostly doc mentions" signal. Filter with usageKinds.
                        kinds = result.KindCounts is { Count: > 0 } ? result.KindCounts : null,
                        groupBy = "project",
                        groups = items.Select(g => new
                        {
                            project = g.Project,
                            isTest = g.IsTestProject,
                            count = g.Count,
                            samples = g.Samples.Select(s => new { s.Path, s.Line, text = s.LineText, kind = s.Kind }),
                        }),
                        coverage = CoverageJson(result.Coverage),
                        partial,
                        partialReason = !partial ? null
                            : exhausted
                                ? $"deadline exhausted after {elapsedMs}ms of {deadlineMs}ms — counts cover the scanned portion only (raise timeoutMs)"
                                  + (result.SkippedCandidateProjects.Count > 0 || result.Coverage.FailedProjects.Count > 0
                                      ? $"; also skipped {result.SkippedCandidateProjects.Count} candidate projects, {result.Coverage.FailedProjects.Count} failed loads"
                                      : "")
                                : result.SkippedCandidateProjects.Count > 0 || result.Coverage.FailedProjects.Count > 0
                                    ? $"skipped {result.SkippedCandidateProjects.Count} candidate projects (raise maxProjects), {result.Coverage.FailedProjects.Count} failed loads"
                                    : "project_model_unproven",
                        skippedCandidateProjects = skippedItems.Count > 0 ? skippedItems : null,
                        skippedCandidateProjectCount = result.SkippedCandidateProjects.Count > 0
                            ? result.SkippedCandidateProjects.Count
                            : (int?)null,
                        skippedCandidateProjectsTruncated = skippedTruncated ? true : (bool?)null,
                        // kbn: projects that textually mention the symbol but have NO graph path
                        // to its declarer (plugins, config-wired consumers) — previously dropped
                        // without a trace. Not loadable via maxProjects; scope with pathGlob or
                        // run indexed mode to see their candidate lines.
                        outOfGraphCandidates = result.OutOfGraphCandidates,
                        note = zeroNote,
                        noteId = zeroNote is not null ? NoteIds.ReferencesZeroLoadingGap : null, // a0b: stable, machine-matchable
                        // t2b: where the budget went — cluster load+resolve vs find+count.
                        timing = new { deadlineMs, elapsedMs, clusterLoadMs = result.ClusterLoadMs, queryMs = result.QueryMs },
                        truncated,
                        meta = meta0,
                    });
                }
                failReason = ExpandReason(reason); // t2b: cold-load token gains inline retry advice
            }
            else
            {
                failReason = "target_not_found_in_index";
            }
            if (mode == "semantic")
            {
                return Json.Serialize(new
                {
                    error = "semantic_unavailable",
                    partialReason = failReason,
                    hint = "Retry with mode='indexed' for fast text candidates.",
                    timing = new { deadlineMs, elapsedMs = swSem.ElapsedMilliseconds }, // 24n: was the deadline the cause?
                    meta = Meta.From(_manager.Health(), "indexed", "semantic"),
                });
            }
        }

        // Indexed fallback (name required — derive from position when missing).
        using var q = _manager.OpenQueries();
        if (string.IsNullOrEmpty(name) && path is not null)
        {
            var chain = q.SymbolAt(NormalizePath(path), line);
            name = chain.Count > 0 ? chain[0].Name : name;
        }
        if (string.IsNullOrEmpty(name))
        {
            return Json.Serialize(new { error = "symbol_not_resolved", partialReason = failReason });
        }
        var excludes = excludePath is { Length: > 0 } ex ? new[] { ex } : null;
        // includeTests is filtered INSIDE the candidate scan, before counting (wu1) — so `total`
        // and the groups describe one set. Summing the filtered groups here instead was itself
        // dishonest (review-reproduced): a file linked into TWO production projects appears in
        // both groups, so the sum double-counted it and the "filtered" total EXCEEDED the real one.
        var (total, prod, test, groups) = q.ReferenceCandidates(
            name, Math.Clamp(maxFiles, 10, 2000), Math.Clamp(samplesPerGroup, 0, 10), pathGlob, excludes, includeGenerated, includeTests);

        // prod/test are PHYSICAL splits of `total` (each file once — 0ok: the old per-group sums
        // let "4 candidate lines (8 production)" appear when a file is linked into two projects).
        string mix = includeTests ? $"{prod} production, {test} test" : $"{prod} production; test projects excluded";
        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(groups, (items, truncated) => new
        {
            name,
            partialReason = failReason,
            summary = $"{total} candidate reference lines across {groups.Count} projects ({mix}).",
            totalCandidates = total,
            groupBy = "project",
            groups = items.Select(g => new
            {
                project = GroupProject(g.Project),
                orphaned = GroupOrphaned(g.Project),
                isTest = g.IsTestProject,
                count = g.Count,
                samples = g.Samples.Select(s => new { path = s.FilePath, s.Line, text = s.LineText }),
            }),
            truncated,
            note = "Candidates are whole-identifier text matches (confidence: indexed), not compiler-resolved references."
                + (kindSet is not null || publicConsumersOnly
                    ? " NOTE: usageKinds/publicConsumersOnly need compiler syntax and were NOT applied on this indexed path."
                    : ""),
            meta,
        });
    }

    // ---------------------------------------------------------------- projects

    [McpServerTool(Name = "project_graph")]
    [Description("Project dependency edges around a project (upstream = dependents, downstream = dependencies). Use to understand ownership and blast radius before changes.")]
    public string ProjectGraph(
        [Description("Project name (e.g. 'Acme.Billing.Invoicing.Application').")] string project,
        [Description("Traversal depth (default 2, max 5).")] int depth = 2,
        [Description("'upstream', 'downstream', or 'both' (default).")] string direction = "both")
    {
        if (NotReady() is { } notReady) return notReady;
        depth = Math.Clamp(depth, 1, 5);
        using var q = _manager.OpenQueries();
        var root = q.ProjectByName(project);
        if (root is null)
        {
            return Json.Serialize(new { error = "project_not_found", project, meta = Meta.From(_manager.Health(), "indexed", "text") });
        }
        var edges = q.ProjectGraph(root.Name, depth, direction);
        var nodes = edges.SelectMany(e => new[] { e.FromProject, e.ToProject })
            .Append(root.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var meta = Meta.From(_manager.Health(), "indexed", "text");
        return Json.WithListBudget(edges, (items, truncated) => new
        {
            root = new { root.Name, root.Path, root.Style, root.Tfms, root.IsTest },
            direction,
            depth,
            nodeCount = nodes.Count,
            // Edge provenance (bxw, schema v10): 'projectReference' = a real <ProjectReference>;
            // 'hintPathReference' = recovered from <Reference>+HintPath / bare Include (the
            // multi-staged build). kind was previously HARDCODED 'projectReference' for every
            // edge — presenting binary couplings as source-graph ones.
            edges = items.Select(e => new { from = e.FromProject, to = e.ToProject, kind = EdgeKind(e.Kind) }),
            truncated,
            meta,
        });
    }

    [McpServerTool(Name = "projects_containing")]
    [Description("All projects that compile a given file (multiple for linked/shared files). Needed before interpreting diagnostics or symbol identity for shared files.")]
    public string ProjectsContaining(
        [Description("Workspace-relative file path.")] string path)
    {
        if (NotReady() is { } notReady) return notReady;
        using var q = _manager.OpenQueries();
        var projects = q.ProjectsContaining(NormalizePath(path));
        return Json.Serialize(new
        {
            path,
            projects = projects.Select(p => new { p.Name, p.Path, p.Style, p.Tfms, p.IsTest }),
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
    }

    // ---------------------------------------------------------------- maintenance

    [McpServerTool(Name = "refresh_index")]
    [Description("Queue an index refresh from the writer process. Read-only followers return index_writer_required. force='auto'/'incremental': targeted paths or a change-detection sweep — hash-identical files are SKIPPED, so this never rebuilds an intact-looking index. force='full': the 'I know the db is wrong' hatch — delete the index and REBUILD FROM SCRATCH (works even from state 'failed'; watch server_capabilities.index.progress). Normally unnecessary — the writer's file watcher keeps the index fresh.")]
    public string RefreshIndex(
        [Description("Optional comma-separated EXACT workspace-relative paths to refresh — no globs (a glob silently matches nothing). Use '/' on Unix, where backslash is a legal filename character; Windows accepts either separator. Ignored with force='full'.")] string? paths = null,
        [Description("'auto' (default) / 'incremental': delta refresh, unchanged files skipped. 'full': rebuild from scratch — corruption/recovery hatch.")] string force = "auto")
    {
        // The two paths are EXPLICIT by contract (field: "calling refresh_index and hoping is
        // not that" — a caller recovering from corruption needs a way in; the delta path's
        // hash-skip makes it a no-op on untouched files by design).
        if (force is not ("auto" or "incremental" or "full"))
        {
            return Json.Serialize(new
            {
                error = "bad_request",
                detail = $"Unknown force value '{force}'. Valid: auto, incremental, full.",
            });
        }
        if (_manager.IsFollower) return IndexWriterRequired();
        if (force == "full")
        {
            if (!_manager.RequestFullRebuild()) return IndexMutationUnavailable();
            return Json.Serialize(new
            {
                queued = true,
                scope = "full rebuild from scratch",
                hint = "The index is discarded and rebuilt; poll server_capabilities.index.progress (state 'building') until 'ready'.",
                meta = Meta.From(_manager.Health(), "indexed", "text"),
            });
        }
        // Normalize the host platform's separator to the forward-slash form stored by the index.
        // On Unix a backslash is a legal filename character and must remain byte-for-byte distinct;
        // on Windows this still accepts either slash style (bug 9h3).
        var list = SplitCsv(paths)?.Select(NormalizePath).ToList();
        if (!_manager.RequestRefresh(list?.Count > 0 ? list : null))
            return _manager.IsFollower ? IndexWriterRequired() : IndexMutationUnavailable();
        return Json.Serialize(new
        {
            queued = true,
            scope = list?.Count > 0 ? $"{list.Count} paths" : "full sweep",
            meta = Meta.From(_manager.Health(), "indexed", "text"),
        });
    }

    private string IndexWriterRequired() => Json.Serialize(new
    {
        error = "index_writer_required",
        detail = "This Phoenix process is a read-only follower; run this operation from the writer process.",
        meta = Meta.From(_manager.Health(), "indexed", "text"),
    });

    private string IndexMutationUnavailable() => NotReady() ?? Json.Serialize(new
    {
        error = "index_unavailable",
        detail = "The index writer is not available to accept this operation.",
        meta = Meta.From(_manager.Health(), "indexed", "text"),
    });

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Turns a name- or position-target into a concrete (path, line, column) for the
    /// semantic layer, using the index to locate the best declaration for name targets.
    /// </summary>
    private ((string Path, int Line, int? Column)? Target, string? NameHint) ResolveSemanticTarget(
        string? name, string? container, string? kinds, string? path, int line, int column,
        int? arity = null)
    {
        if (path is not null && line > 0)
        {
            return ((NormalizePath(path), line, column > 0 ? column : null), name);
        }
        if (name is null) return (null, null);

        using var q = _manager.OpenQueries();
        // includeGenerated: TRUE (review 4a): the LIVE twin of a dead declaration is typically the
        // WSDL/codegen copy, which carries an <auto-generated> banner — excluding generated files
        // here made the live twin invisible and the orphan gate below useless for the exact
        // production case. The ORDER BY still ranks non-generated first, so picks only change when
        // the non-generated candidates are dead.
        var hits = q.SearchSymbols(
            name, "exact", SplitCsv(kinds), 20, includeGenerated: true, arity: arity);
        if (container is { } c)
        {
            hits = hits.Where(h =>
                (h.Container?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.Ns?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }
        // NEVER target an uncompiled declaration (3tz, the dead-twin bug): a file no project compiles
        // is in no compilation, so semantic resolution against it can only fail — while a LIVE twin of
        // the same symbol (e.g. the WSDL-generated one) exists elsewhere. The old pick even preferred
        // the dead twin: non-generated files sort first. Fall back to orphaned-only when there is no
        // live declaration at all (then semantic fails honestly and the heuristic path takes over).
        if (hits.Count > 0)
        {
            var orphaned = q.OrphanedPaths(hits.Select(h => h.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            if (orphaned.Count > 0)
            {
                var live = hits.Where(h => !orphaned.Contains(h.FilePath)).ToList();
                if (live.Count > 0) hits = live;
            }
        }
        var best = hits
            .OrderBy(h => h.Kind switch { "interface" => 0, "class" => 1, "struct" => 1, "enum" => 1, _ => 2 })
            .FirstOrDefault();
        if (best is null) return (null, name);
        return ((best.FilePath, best.StartLine, null), name);
    }

    // Cap on declaration sites emitted for one symbol. A namespace or heavily-partial type can
    // have hundreds of spans; this bounds the count for tools that serialize it via a plain
    // Serialize (implementations/references/etc.). definition additionally routes its (single)
    // declaration list through WithListBudget for a hard byte bound; this cap keeps the common
    // case small and gives an early declarationsTruncated signal.
    private const int MaxDeclarationSites = 20;

    // Kept single-arg so method-group use (.Select(SemanticSymbolJson)) still binds.
    private static object SemanticSymbolJson(SemanticDeclaration d) => SemanticSymbolCore(d, includeDeclarations: true);

    // Identity without the declaration list — for definition, which renders declarations once
    // in its own budgeted top-level array (avoids serializing the same sites twice).
    private static object SemanticIdentityJson(SemanticDeclaration d) => SemanticSymbolCore(d, includeDeclarations: false);

    private static object SemanticSymbolCore(SemanticDeclaration d, bool includeDeclarations)
    {
        var sites = includeDeclarations ? d.Declarations.Take(MaxDeclarationSites + 1).ToList() : null;
        return new
        {
            display = d.SymbolDisplay,
            documentationCommentId = d.DocumentationCommentId,
            d.Kind,
            containingType = d.ContainingType,
            ns = d.Namespace,
            assembly = d.Assembly,
            declarations = sites?.Take(MaxDeclarationSites).Select(s => new { s.Path, s.StartLine, s.EndLine, project = s.Project }),
            declarationsTruncated = sites is { Count: > MaxDeclarationSites }
                ? $"more than {MaxDeclarationSites} declaration sites — narrow the query or use references"
                : (string?)null,
        };
    }

    private static object CoverageJson(ClusterCoverage c) => new
    {
        loadedProjects = c.LoadedProjects,
        requestedProjects = c.RequestedProjects,
        // Field 0.7.0: SymbolFinder scans the WHOLE solution (including projects loaded by earlier
        // calls), so hits can legitimately exceed the requested set — this makes that legible
        // instead of "coverage 1/1 but 8 hits from 8 projects".
        solutionProjects = c.SolutionProjects > 0 ? c.SolutionProjects : (int?)null,
        failedProjects = c.FailedProjects.Count > 0
            ? c.FailedProjects.Take(8).Select(project =>
                Json.Utf8Prefix(project, 256, out _)).ToList()
            : null,
        failedProjectCount = c.FailedProjects.Count > 0 ? c.FailedProjects.Count : (int?)null,
        failedProjectsTruncated = c.FailedProjects.Count > 8 ||
                                  c.FailedProjects.Any(project => Json.Utf8Bytes(project) > 256)
            ? true
            : (bool?)null,
        unprovenFriendAssemblyProjects = c.UnprovenFriendAssemblyProjects is { Count: > 0 } unproven
            ? unproven.Take(8).Select(project => Json.Utf8Prefix(project, 256, out _)).ToList()
            : null,
        unprovenFriendAssemblyProjectCount = c.UnprovenFriendAssemblyProjects is { Count: > 0 }
            ? c.UnprovenFriendAssemblyProjects.Count
            : (int?)null,
        frameworkRefsAvailable = c.FrameworkRefsAvailable,
    };

    /// <summary>Combines an explicit excludePath glob with firstPartyOnly's whole-segment vendor
    /// globs into one exclude list (null when empty, so callers pass null for "no filter").
    /// firstPartyOnly uses <see cref="IndexQueries.VendorExcludeGlobs"/> — a complete, scan-free
    /// exclusion that matches exactly what the per-hit noise flag reports.</summary>
    private static IReadOnlyList<string>? BuildExcludes(string? excludePath, bool firstPartyOnly)
    {
        var list = new List<string>();
        if (excludePath is { Length: > 0 } ex) list.Add(ex);
        if (firstPartyOnly) list.AddRange(IndexQueries.VendorExcludeGlobs());
        return list.Count > 0 ? list.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;
    }

    /// <summary>Resolves a symbolId handle to its indexed declaration row. Accepts the idx:NNN
    /// handle (from any search_symbol / symbol_at / definition result) — index-local, so it changes
    /// on reindex. A documentationCommentId is not yet accepted as an input handle. Returns the hit
    /// on success, or an error-JSON string to hand straight back to the caller.</summary>
    private (SymbolHit? Hit, string? Error) ResolveSymbolIdHandle(string symbolId)
    {
        // Handle shape: idx:<rowid>[~<fingerprint>]. The fingerprint is optional so a hand-typed
        // idx:<rowid> still resolves (best-effort, unverified); emitted handles always carry it.
        string body = symbolId.StartsWith("idx:", StringComparison.Ordinal) ? symbolId[4..] : "";
        string? fp = null;
        int tilde = body.IndexOf('~');
        if (tilde >= 0) { fp = body[(tilde + 1)..]; body = body[..tilde]; }
        if (body.Length == 0 || !long.TryParse(body, out long id))
        {
            return (null, Json.Serialize(new
            {
                error = "bad_request",
                detail = "symbolId must be an 'idx:NNN' handle (from a prior search_symbol / symbol_at / definition result). A documentationCommentId (e.g. 'T:Ns.Type') is not yet accepted as input — use name, or path+line.",
            }));
        }
        using var q = _manager.OpenQueries();
        var hit = q.SymbolById(id);
        if (hit is null)
        {
            return (null, Json.Serialize(new
            {
                error = "symbol_not_found",
                detail = $"No indexed symbol with id {id}. 'idx:' handles are index-local — re-run search_symbol for a current handle.",
            }));
        }
        if (fp is not null && !string.Equals(fp, Fingerprint(hit), StringComparison.Ordinal))
        {
            // The rowid still exists but now holds a DIFFERENT symbol (a reindex reused it). Refuse
            // rather than return the wrong symbol as if the handle were exact.
            return (null, Json.Serialize(new
            {
                error = "stale_handle",
                detail = $"symbolId 'idx:{id}' now refers to a different symbol ({hit.Name}) — the index changed since the handle was issued. Re-run search_symbol for a current handle.",
            }));
        }
        return (hit, null);
    }

    /// <summary>t2b: expand the cluster_cold_load token with inline advice at the moment of
    /// confusion — the deadline died LOADING compilations (the first call after an index build
    /// or cluster reload), and an immediate retry usually returns exact. The uniform
    /// "semantic_timeout" sent agents raising timeoutMs (or distrusting the tool) when the fix
    /// was simply to call again. Every other reason passes through untouched, and the token
    /// stays the string's PREFIX so it remains machine-matchable.</summary>
    internal static string? ExpandReason(string? reason) => // internal: token contract pinned by tests
        reason == "cluster_cold_load"
            ? "cluster_cold_load — the deadline expired while loading compilations (first call after an index build or cluster reload); an immediate retry usually returns exact"
            : reason;

    /// <summary>Wire vocabulary for edge provenance (bxw): the stored kind ('project'/'assembly')
    /// maps to 'projectReference'/'hintPathReference' — 'projectReference' predates provenance on
    /// the wire (project_graph hardcoded it), so the common case keeps its established value.
    /// Same tokens everywhere kind/via appears (project_graph, dependency_path, context_pack).</summary>
    private static string EdgeKind(string storedKind) =>
        storedKind == "assembly" ? "hintPathReference" : "projectReference";

    /// <summary>Structured attribution for reference groups whose files sit in NO project's
    /// compile set (bxw): the payload used to ship the magic display string "(no project)",
    /// which callers had to know to string-match. Now the project field is null — which the
    /// house serialization OMITS (WhenWritingNull) — and orphaned:true rides alongside as the
    /// positive discriminator, the same 'orphaned' vocabulary search_symbol/repo_overview use.
    /// Nullable bool so compiled rows stay clean (orphaned omitted too).</summary>
    private static string? GroupProject(string project) =>
        project == IndexQueries.NoProjectGroup ? null : project;

    private static bool? GroupOrphaned(string project) =>
        project == IndexQueries.NoProjectGroup ? true : null;

    /// <summary>Short, stable (cross-process) identity hash of a symbol row — FNV-1a over
    /// name/kind/arity/line/path. Embedded in the idx: handle so a rowid the index later reuses for a
    /// different symbol fails the check instead of resolving silently.</summary>
    private static string Fingerprint(SymbolHit s)
    {
        string identity = $"{s.Name}{s.Kind}{s.Arity}{s.StartLine}{s.FilePath}";
        uint h = 2166136261u;
        foreach (char c in identity) h = (h ^ c) * 16777619u;
        return h.ToString("x8");
    }

    private static object SymbolJson(SymbolHit s) => new
    {
        // idx:<rowid>~<identity fingerprint>. The fingerprint lets a rowid that delta refresh
        // reused/reassigned be DETECTED on the way back in (stale_handle) rather than silently
        // resolving to a different symbol — the id alone is not stable across reindex.
        symbolId = $"idx:{s.Id}~{Fingerprint(s)}",
        s.Name,
        s.Kind,
        s.Arity,
        ns = s.Ns,
        containingType = s.Container,
        s.Signature,
        s.Accessibility,
        // Inheritance/lifetime modifiers (bt7): "virtual"/"override"/"abstract"/"static sealed"...
        // Omitted when none — in deep hierarchies this picks the override site without opening files.
        modifiers = s.Modifiers,
        // Per-accessor accessibility (hu7): { get: "public", set: "private" } — ONLY when an
        // accessor differs from the member's own accessibility (silent-when-nothing-to-say).
        accessors = AccessorsJson(s.Accessors),
        path = s.FilePath,
        s.StartLine,
        s.EndLine,
        isPartial = s.IsPartial ? true : (bool?)null,
        isGenerated = s.FileIsGenerated ? true : (bool?)null,
        // Best-effort "this hit lives under a vendored/generated dir" signal (only present when
        // true). Lets a caller spot third-party noise without firstPartyOnly, or excludePath it.
        noise = IndexQueries.IsVendorPath(s.FilePath) ? true : (bool?)null,
        // "No project compiles this file" (only present when true) — the compile-graph signal grep
        // lacks. 3tz: Include globs expanded, Remove honored; residual gaps are shared .projitems,
        // props-level globs, and ignored Conditions — near-proof, not absolute proof.
        orphaned = s.IsOrphaned ? true : (bool?)null,
        attributes = s.AttrMarkers,
    };

    /// <summary>Parses the stored "get=public;set=private" accessor split into a structured
    /// object (hu7). Null (omitted) when the member's accessors are uniform.</summary>
    private static Dictionary<string, string>? AccessorsJson(string? accessors)
    {
        if (accessors is null) return null;
        var map = new Dictionary<string, string>();
        foreach (var part in accessors.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) map[part[..eq]] = part[(eq + 1)..];
        }
        return map.Count > 0 ? map : null;
    }

    private string? NotReady()
    {
        if (_manager.IsQueryable) return null;
        var h = _manager.Health();
        return Json.Serialize(new
        {
            error = h.State == "building" ? "index_building" : "index_unavailable",
            state = h.State,
            detail = h.Error,
            // The progress struct rides IN the error (field: "otherwise callers still have to
            // poll server_capabilities separately") — phase + monotonic counters + elapsed,
            // filesTotal omitted until the scan knows it, no fabricated ETA/percent (bead two).
            progress = ProgressJson(h),
            hint = h.State == "building"
                ? "The workspace index is still building (first run). Retry shortly; use shell tools meanwhile."
                : "Index unavailable. Falling back to shell search is appropriate.",
        });
    }

    /// <summary>Test seam: the shared progress emitter — the silent-when-zero (efa) and
    /// gated-estimate (0tn) emission rules are pinned by tests through this.</summary>
    internal static object? ProgressJsonForTest(IndexHealth h) => ProgressJson(h);

    /// <summary>Build-progress envelope shared by server_capabilities.index and the
    /// index_building error body — one shape everywhere; null (omitted) unless building.</summary>
    private static object? ProgressJson(IndexHealth h)
    {
        if (h.Progress is not { } p) return null;
        string phase = Json.Utf8Prefix(p.Phase, CapabilityDynamicTextBytes,
            out bool phaseTruncated);
        return new
        {
            phase,
            phaseTruncated = phaseTruncated ? true : (bool?)null,
            phaseBytes = phaseTruncated ? Json.Utf8Bytes(p.Phase) : (int?)null,
            filesIndexed = p.FilesIndexed,
            filesTotal = p.FilesTotal,
            elapsedMs = p.ElapsedMs,
            // efa: silent on a clean build — the counters exist to make a LOSSY build visible
            // (skipped reads are absent from the index until a delta refresh retries them;
            // failed csproj parses mean guessed compile sets), not to add noise to a good one.
            filesSkipped = p.FilesSkipped > 0 ? p.FilesSkipped : (int?)null,
            projectsFailed = p.ProjectsFailed > 0 ? p.ProjectsFailed : (int?)null,
            // 0tn: DERIVED from the monotonic counters over the indexing_files phase's own
            // clock, present only once measurable (>=100 files over >=1s) — labeled estimates
            // that may fluctuate; still no percent field (derive % from the counters).
            filesPerSecond = p.FilesPerSecond,
            estimatedRemainingMs = p.EstimatedRemainingMs,
        };
    }

    // Cursor is "o:<offset>" or, for a mode-carrying tool (search_symbol auto), "o:<offset>:<mode>".
    private static (int Limit, int Offset, string? Mode) Page(int limit, string? cursor)
    {
        limit = Math.Clamp(limit, 1, 100);
        int offset = 0;
        string? mode = null;
        if (cursor is not null && cursor.StartsWith("o:", StringComparison.Ordinal))
        {
            string rest = cursor[2..];
            int colon = rest.IndexOf(':');
            string offsetPart = colon < 0 ? rest : rest[..colon];
            if (int.TryParse(offsetPart, out int parsed) && parsed > 0) offset = parsed;
            if (colon >= 0 && colon + 1 < rest.Length) mode = rest[(colon + 1)..];
        }
        return (limit, offset, mode);
    }

    private static List<string>? SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string NormalizePath(string path) => CodeNav.Core.WorkspacePaths.Normalize(path);
}
