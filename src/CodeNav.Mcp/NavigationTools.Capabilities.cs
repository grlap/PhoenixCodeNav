using System.ComponentModel;
using System.Text.Json;
using CodeNav.Core.Indexing;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp;

/// <summary>
/// Owns: server_capabilities and repo_overview — the features manifest, capability text budgets, and index/health summaries.
/// Does not own: index-health production (CodeNav.Core), semantic execution, or the shared response primitives in NavigationTools.cs.
/// </summary>
public sealed partial class NavigationTools
{
    // ---------------------------------------------------------------- capabilities / overview

    [McpServerTool(Name = "server_capabilities")]
    [Description("Reports supported languages, available tools, feature ids, index status, and response budgets. The compact default omits feature summaries; set detail=true only when their prose is needed.")]
    public string ServerCapabilities(
        [Description("Include verbose feature summaries (default false). Stable feature ids are always returned.")] bool detail = false) =>
        ServerCapabilitiesJson(_manager.Health(), _semantic.FrameworkRefsAvailable,
            _semantic.FrameworkRefsSource, includeFeatureSummaries: detail,
            defaultQueryScope: _defaultQueryScope,
            fsharpProjectModel: _semantic.SelectedFSharpProjectModel);

    internal static string ServerCapabilitiesForTest(IndexHealth health,
        bool frameworkRefsAvailable = true, string? frameworkRefsSource = null,
        bool detail = false) =>
        ServerCapabilitiesJson(health, frameworkRefsAvailable, frameworkRefsSource,
            includeFeatureSummaries: detail);

    internal static string ServerCapabilitiesUncompactedForTest(IndexHealth health,
        bool frameworkRefsAvailable = true, string? frameworkRefsSource = null) =>
        ServerCapabilitiesJson(health, frameworkRefsAvailable, frameworkRefsSource,
            applyBudget: false);

    internal static string[] CapabilityFeatureIds(IndexHealth health)
    {
        using JsonDocument document = JsonDocument.Parse(
            ServerCapabilitiesJson(health, frameworkRefsAvailable: true));
        return document.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
    }

    private static string ServerCapabilitiesJson(IndexHealth h, bool frameworkRefsAvailable,
        string? frameworkRefsSource = null, bool applyBudget = true,
        bool includeFeatureSummaries = false, string defaultQueryScope = "all",
        CodeNav.Core.Semantic.ProjectModelMode fsharpProjectModel = CodeNav.Core.Semantic.ProjectModelMode.Simple)
    {
        string state = CapabilityText(h.State, CapabilityIdentityTextBytes,
            out bool stateTruncated, out int? stateBytes)!;
        string? indexVersion = CapabilityText(h.IndexVersion, CapabilityIdentityTextBytes,
            out bool indexVersionTruncated, out int? indexVersionBytes);
        string? indexedAtUtc = CapabilityText(h.IndexedAtUtc, CapabilityIdentityTextBytes,
            out bool indexedAtUtcTruncated, out int? indexedAtUtcBytes);
        string? lastRefreshUtc = CapabilityText(h.LastRefreshUtc, CapabilityIdentityTextBytes,
            out bool lastRefreshUtcTruncated, out int? lastRefreshUtcBytes);
        string? startupBuildReason = CapabilityText(h.StartupBuildReason,
            CapabilityIdentityTextBytes, out bool startupBuildReasonTruncated,
            out int? startupBuildReasonBytes);
        string? startupPriorSchema = CapabilityText(h.StartupPriorSchema,
            CapabilityIdentityTextBytes, out bool startupPriorSchemaTruncated,
            out int? startupPriorSchemaBytes);
        string workspaceRoot = Json.Utf8Prefix(h.WorkspaceRoot, CapabilityDynamicTextBytes,
            out bool workspaceRootTruncated);
        int? workspaceRootBytes = workspaceRootTruncated ? Json.Utf8Bytes(h.WorkspaceRoot) : null;
        string? error = h.Error is null
            ? null
            : Json.Utf8Prefix(h.Error, CapabilityDynamicTextBytes, out _);
        bool errorTruncated = h.Error is not null &&
            Json.Utf8Bytes(h.Error) > CapabilityDynamicTextBytes;
        int? errorBytes = errorTruncated ? Json.Utf8Bytes(h.Error!) : null;
        bool incompletePathsTruncated = false;
        string[]? incompletePaths = h.RefreshIncompletePaths?.Take(8)
            .Select(path =>
            {
                string bounded = Json.Utf8Prefix(path, 512, out bool truncated);
                incompletePathsTruncated |= truncated;
                return bounded;
            }).ToArray();
        incompletePathsTruncated |= h.RefreshIncompletePathCount >
            (incompletePaths?.Length ?? 0);

        object envelope = new
        {
            server = "phoenixCodeNav",
            version = BuildInfo.Version,
            // Build identity so a caller can verify WHICH build is deployed. The old hardcoded version
            // went stale at 0.1.0 across many feature batches; commit auto-tracks the actual build.
            build = new { version = BuildInfo.Version, commit = BuildInfo.Commit, indexSchema = BuildInfo.IndexSchema },
            runtime = new
            {
                indexMode = PhoenixRuntimeMode.IndexMode(h.AccessMode),
                processMode = PhoenixRuntimeMode.Current.ToString().ToLowerInvariant(),
                processId = Environment.ProcessId,
            },
            languages = new[] { "csharp", "fsharp", "markdown", "sql" },
            queryDefaults = new
            {
                scope = defaultQueryScope,
                configuredBy = "CODENAV_DEFAULT_QUERY_SCOPE",
                appliesTo = new[] { "find_file", "search_text", "search_symbol" },
                explicitAll = new { queryScope = "all" },
                exactSemanticOperations = "always search all indexed candidates and are never silently narrowed by this default",
            },
            languageLayers = new
            {
                csharp = new[] { "text", "syntax", "semantic" },
                fsharp = new[] { "text", "syntax", "semantic", "projectGraph" },
                markdown = new[] { "text" },
                sql = new[] { "text" },
            },
            navigationLayers = new[] { "text", "syntax", "semantic" },
            // Explicit capability manifest: lets a caller CONFIRM a feature is present without having to
            // trigger its (often silent-when-clean) response fields — grep an id to verify a deploy.
            features = new object[]
            {
                new { id = "agent-cli-tool-surface", summary = "PhoenixCodeNav.Mcp CLI: registration-backed offline tools/help/schema discovery; exact MCP tool invocation via shared workspace daemon; one-JSON-document output, schema-derived flags or complete JSON arguments, deterministic exit codes" },
                new { id = "agent-first-server-instructions", summary = "MCP initialize routes agents via repo_overview, context_pack, exact semantic tools, impact/related_tests, review_pack; names structured domain errors and bounded retry guidance" },
                new { id = "compact-capability-discovery", summary = "server_capabilities returns every stable feature id plus status, language, budget, and confidence contracts by default; detail=true adds bounded feature summaries" },
                new { id = "language-scoped-symbol-search", summary = "search_symbol lang=csharp|fsharp limits declarations and computes partiality from the effective language and path scope, so unrelated language failures do not poison authoritative scoped misses" },
                new { id = "agent-zero-hit-recovery", summary = "project and symbol misses disclose effective scope plus bounded ranked suggestions and retry arguments without silently substituting a candidate; project selectors use strict path/filename/stem/AssemblyName precedence, preserve physical ambiguity, and byte-budget selector echoes plus shadow evidence with truthful counts" },
                new { id = "agent-request-patch-recovery", summary = "Caller-dependent recovery: replayOriginalRequest, explicit selector-removal list and replacement arguments; preserves original filters/limits/budgets, never reflects or truncates them into a different request" },
                new { id = "dual-list-string-inputs", summary = "list-like string arguments accept their established comma-separated form or a JSON-array encoded string while retaining host-compatible string schemas" },
                new { id = "documentation-comment-selectors", summary = "definition, references, and implementations accept stable C# T:/M:/P:/F:/E: documentationCommentId selectors, preserve assembly ambiguity, and refuse unsupported implementation targets without retargeting" },
                new { id = "semantic-selector-incompatibility-errors", summary = "definition and references return structured semantic-selector incompatibility errors as bad_request: documentationCommentId rejects indexed mode with incompatible_mode, while references rejects pathGlob/excludePath for documentationCommentId and operator idx handles with incompatible_filter; operator idx handles also reject indexed mode, and genuine semantic unavailability remains semantic_required" },
                new { id = "semantic-retry-guidance", summary = "definition, references, implementations, callers, callees, and type_hierarchy use the same bounded retry recommendation and hint contract for cluster_cold_load and semantic_timeout" },
                new { id = "cold-start-retry-contract", summary = "Shared navigation/bounded review_pack non-ready gates: access-mode-aware retryRecommended; retryHint names server_capabilities.index.progress + index.state for transitional writer/follower states, index.error for terminal writer/unavailable failure. context_pack keeps the same typed cold-start retry contract for cluster_cold_load: retry canceled load once with larger timeoutMs below the documented family maximum, unchanged at that maximum. Phoenix never retries automatically" },
                new { id = "semantic-cold-start-phase-timing", summary = "definition/references/implementations/type_hierarchy: fixed bounded timing.semanticColdStart for load/preparation/metadata-reference work/compilation/resolution on exact AND degraded C# semantic paths. Same integer-ms snapshot in semanticOp; answers unchanged. F# uses fcs phases; C# shape omitted for calls ending before the C# pipeline" },
                new { id = "fsharp-semantic-cold-start-attribution", summary = "v0.12.109 F# symbol_at/definition/references/implementations/callers/callees: timing.semanticColdStart.engine=fcs; identical snapshot in one semanticOp" },
                new { id = "fsharp-semantic-admission-attribution", summary = "v0.12.109 admissionWaitMs: observed gate wait" },
                new { id = "fsharp-semantic-snapshot-attribution", summary = "v0.12.109 snapshotCaptureMs: input/context capture attempts" },
                new { id = "fsharp-semantic-fcs-setup-attribution", summary = "v0.12.109 fcsSetupMs: runtime/checker and project options" },
                new { id = "fsharp-semantic-project-parse-check-attribution", summary = "v0.12.109 projectParseAndCheckMs: summed FCS project awaits" },
                new { id = "fsharp-semantic-file-parse-check-attribution", summary = "v0.12.109 fileParseAndCheckMs: summed FCS file awaits" },
                new { id = "references-gc-pause-attribution", summary = "v0.12.79 references semanticOp queryStages.compilationPreparation.gcPauseMs reports whole-millisecond process-wide GC pause time between the preparation entry and exit samples; concurrent operations can attribute the same overlapping pauses, 0 means a measured interval below 1 ms, and omission means the process counter was unavailable" },
                new { id = "default-query-scope", summary = "CODENAV_DEFAULT_QUERY_SCOPE configures all or first_party for broad indexed find/search tools; queryScope='all' overrides it, affected responses echo the effective scope, and exact semantic operations remain unscoped" },
                new { id = "dependency-direction-aliases", summary = "project_graph accepts dependencies as downstream and dependents as upstream while echoing the canonical direction and preserving established names" },
                new { id = "review-pack-actionable-budget-gaps", summary = "review_pack discloses a separately bounded affected-path sample with total/returned/truncated coverage, stable reason ids, and explicit recovery when evidence is clipped" },
                new { id = "capabilities-hard-budget", summary = "UTF-8 hardBytes is the ordinary response target; *Truncated/*Bytes and featuresCompacted/featureSummariesReturned disclose compaction; every singular feature id remains, while one indivisible complete semantic identity or documentation-id ambiguity candidate uses the separately declared measured exception" },
                new { id = "review-ref-resolution", summary = "Hex-only branch/tag names, abbreviations, and object ids are Git-validated and peeled to full commits; repository-format-width objects retain object precedence and distinct short-hex ambiguity is refused" },
                new { id = "confidence-honesty", summary = "every result carries confidence exact|indexed|heuristic; confidenceNote only when heuristic (tier meanings live in confidenceModel here); meta.statusNote explains refreshing/stale; meta.build stamps every result meta with version+commit" },
                new { id = "hierarchy-ranking", summary = "implementations ranks concrete hits first; conditional likelyImplementation names the sole concrete hit and via identifies indirect base derivation. implementations wraps hit metadata; type_hierarchy returns flat symbols" },
                new { id = "implementer-completeness", summary = "Member fallback exposes implementerCount/omittedImplementers. When identity is compiler-resolved but implementers are indexed, symbolConfidence exact and implementationsConfidence heuristic remain separate; type_hierarchy fallback returns heuristic base-list candidates without compiler-only bases/interfaces" },
                new { id = "generic-arity-resolution", summary = "v0.11.8 implementations/type_hierarchy select by arity or symbolId; mixed-arity names refuse; syntax fallback is arity-exact" },
                new { id = "friend-assembly-semantics", summary = "v0.11.9 models literal local SDK InternalsVisibleTo grants; friend-only results disclose project_model_unproven when imports, package build assets, or Directory.Build authority can change the grant; since v0.12.69 the references census uses its selected consumer scan, including candidates with no compiler-bound result site, to determine that authority" },
                new { id = "fsharp-text-indexing", summary = "v0.12.0 F# FTS" },
                new { id = "markdown-sql-text-indexing", summary = "v0.12.33 schema v19 indexes .md and .sql as text-only files for find_file, search_text, and indexed source fallback; no syntax or compiler semantics" },
                new { id = "fsharp-project-graph", summary = "v0.12.0 .fsproj ownership/edges; no Roslyn" },
                new { id = "fsharp-outline", summary = "v0.12.1 owned .fs/.fsi FCS outline" },
                new { id = "fsharp-outline-parse-context-selection", summary = "v0.12.4 base/.Net project+TFM parse-context selection affects only F# parser options and #if symbols" },
                new { id = "fsharp-outline-parse-context-budget", summary = "v0.12.4 max 64 project/TFM parse contexts with total/returned/truncated coverage; 64 KiB hard envelope" },
                new { id = "fsharp-indexed-symbol-name-search", summary = "v0.12.52 schema v26 persists deterministic FCS syntax declarations plus parse and project-option coverage from .fs/.fsi files across indexed owner/TFM parse contexts. search_symbol: F# kinds, namespace/path/generated filters, generic arity, orphan disclosure, signature/implementation pairs, linked multi-owner files, source/project-option delta convergence. actionably incomplete contexts are partial; ordinary SDK/import limits remain advisory coverage; .fsx-only scopes fail closed; mixed script scopes disclose skipped text-only files" },
                new { id = "fsharp-indexed-parse-context-budget", summary = "v0.12.55 schema v28 processes at most 64 deterministic owner/TFM parse contexts per .fs/.fsi file, reserves one context per valid compile owner while capacity remains, persists total/processed/truncated plus truncatedOwnerProjects coverage across cold and delta indexing, and makes affected search_symbol scopes partial with fsharp_parse_contexts_truncated" },
                new { id = "fsharp-parse-owner-coverage-breakdown", summary = "v0.12.82 schema v30 search_symbol.fsharpParseCoverage: truncatedOwnerProjects = unrepresentedOwnerProjects + partiallyTruncatedOwnerProjects; owners with none/some-but-not-all contexts retained respectively. Omitted-context totals sum incidences per affected file, not distinct project identities" },
                new { id = "fsharp-symbol-at-semantic", summary = "v0.12.5 FCS position resolution for one explicit physical .fsproj + TFM type-check context; since v0.12.83 the context includes its F# ProjectReference closure under the evaluated MSBuild transitivity policy and the bounded child-TFM policy disclosed separately" },
                new { id = "fsharp-definition-same-project", summary = "v0.12.5 FCS signature/implementation declarations for the selected physical F# project; since v0.12.83 position resolution can return declarations from its F# ProjectReference closure under the evaluated MSBuild transitivity and bounded child-TFM policies" },
                new { id = "fsharp-semantic-confidence-authority", summary = "v0.12.80 F# semantic confidence exact: selected context has only disclosed assumptions or immutable-evidence provenance. Indexed: substituted, errored, removed from the context, or reason not yet classified; partial reasons remain visible in either case. server_capabilities.semantic.fsharpIndexedTools renamed fsharpSemanticTools" },
                new { id = "fsharp-references-same-project", summary = "v0.12.81 references position mode counts compiler-bound non-definition uses in one selected physical .fsproj + TFM and returns bounded samples from the pinned source snapshot for that root; since v0.12.83 FCS binds the root through its F# ProjectReference closure under the evaluated MSBuild transitivity and bounded child-TFM policies without counting dependency-project uses" },
                new { id = "fsharp-references-workspace-dependents", summary = "v0.12.86 scans proven F# source dependents/TFMs using bounded evaluated ProjectReference authority; counts a distinct declaring project once, deduplicates physical sites across contexts, and reports incomplete lower-bound coverage plus test/generated filters" },
                new { id = "fsharp-semantic-project-reference-closure", summary = "v0.12.83 exact selected root TFM; literal physical project paths; dependency-first in-memory referenced-project options without emitted or last-built DLLs. flat transitive SDK default; DisableTransitiveProjectReferences=true and legacy-style projects remain direct-only. child compiler errors retain source paths (fsharp_semantic_diagnostics_present); declarationsFromProjectReferenceClosureCount; declarationsOutsideSelectedProjectCount stays not-returned. v0.12.86 references also scan workspace dependents. Missing/non-F#/cyclic/metadata-unsupported/same-assembly/unavailable-TFM closure fails closed" },
                new { id = "fsharp-semantic-project-reference-netstandard-compatibility", summary = "v0.12.84 exact child-TFM wins; single-target netstandard1.0-2.1 follows Microsoft's table, including .NET Framework 4.6.1; multi-target remains exact-only. netstandard1.x fails closed: inputs are never materialized or replaced with netstandard2.0. netstandard2.0/2.1 works end to end under the chosen child TFM" },
                new { id = "fsharp-type-check-context-selection", summary = "v0.12.5 ambiguous owner/TFM sets fail closed and expose bounded selected/available F# type-check contexts" },
                new { id = "fsharp-semantic-snapshot", summary = "v0.12.5 pinned immutable source/project snapshot and request-private verified HintPath binaries; ProjectReference closure since v0.12.83; exact-path opened-handle verification on Windows and Linux, plus macOS since v0.12.56; bounded work" },
                new { id = "workspace-msbuild-config-indexing", summary = "schema v16 persists arbitrary workspace .props/.targets as config inputs for find_file/config_lookup and pinned bounded project evaluation" },
                new { id = "fsharp-semantic-bounded-project-evaluation", summary = "v0.12.6 bounded properties/conditions/Choose/imports feed FCS; ProjectReference literal metadata since v0.12.83; only declared path intrinsics; unresolved ambient inputs and targets/tasks fail closed" },
                new { id = "fsharp-semantic-makerelative-project-root", summary = "v0.12.91 exact MakeRelative project/import directories; other functions fail closed" },
                new { id = "fsharp-semantic-indexed-file-exists", summary = "v0.12.92 indexed-file presence incl. web.config; unproven paths fail closed" },
                new { id = "fsharp-semantic-literal-file-exists", summary = "v0.12.94 F# literal/expanded Exists: project-relative pinned presence/absence; excluded/unsafe paths stay unknown" },
                new { id = "fsharp-semantic-property-startswith", summary = "v0.12.93 F# Property.StartsWith is ordinal/case-sensitive; other string methods fail closed" },
                new { id = "fsharp-semantic-compound-self-defaults", summary = "v0.12.95 self-empty guards compose with And/Or/!; unrelated unknowns fail closed" },
                new { id = "fsharp-semantic-default-configuration-platform", summary = "v0.12.97 analysis Debug|AnyCPU before imports; mutable; disclosed" },
                new { id = "fsharp-semantic-import-markers", summary = "v0.12.103 self-marked Import != true guards assume absent empty; indexed disclosure" },
                new { id = "fsharp-semantic-item-property-reads", summary = "v0.12.103 live items freeze consumed properties; unknown/skipped/Choose stay conservative" },
                new { id = "msbuild-diagnostics", summary = "v0.12.104 opt-in local F# evaluation JSONL beside telemetry" },
                new { id = "fsharp-simple-project-model", summary = "Raw C#-like F# inputs; totalIsApproximate/countScope qualify counts" },
                new { id = "fsharp-shared-simple-default", summary = "Simple default; atomic evaluated opt-in" },
                new { id = "fsharp-simple-binding-confidence", summary = "Exact binding, approximate model" },
                new { id = "fsharp-simple-input-omissions", summary = "Distinct input-omission reasons" },
                new { id = "fsharp-approximate-scan-progress", summary = "Separate scanIncomplete/model scope" },
                new { id = "fsharp-semantic-project-reference-copy-local", summary = "v0.12.102 shared Core metadata roles: Private is copy-local, not compiler inclusion; F# admits it; ReferenceOutputAssembly retained; no new C# closure support" },
                new { id = "fsharp-semantic-optional-property-guards", summary = "v0.12.102 exact PropertyGroup nonempty guards assume absent property empty; disclosed; assigned values retained; no environment reads or general missing-property fallback" },
                new { id = "fsharp-semantic-directory-build-reference-evaluation", summary = "v0.12.8 nearest indexed ancestor Directory.Build.props/targets surround each F# project: bounded property-before-item conditions, Reference Include/Remove item lists; v0.12.83 active item-phase ProjectReference; irrelevant chained targets ignored; reference-affecting targets/tasks fail closed" },
                new { id = "fsharp-semantic-package-asset-closure", summary = "v0.12.56 F# PackageReference + conditional PackageVersion from nearest indexed Directory.Packages.props; since v0.12.67 exact case-insensitive explicit direct identity set matches one restored project.assets.json target; SDK auto-referenced packages validated separately. Trusted-folder reachable transitive compile assets: bounded verified private immutable copies, reverified after FCS; missing/stale/mismatched/changed/ambiguous/unsafe closure fails without restore or MSBuild execution" },
                new { id = "csharp-semantic-central-package-management", summary = "v0.12.57 C# versionless PackageReference compiler inputs: unconditional literal PackageVersion from nearest indexed Directory.Packages.props; exact global-cache directories; central authority in warm model identity; MSBuild-dependent shapes stay unresolved, without guessing or executing restore" },
                new { id = "shared-semantic-package-evaluation", summary = "v0.12.98 C#/F# share PackageReference/PackageVersion operations and VersionOverride; central references admitted; GlobalPackageReference is restore-only, not compile; caller authority/budgets retained" },
                new { id = "shared-semantic-sdk-context", summary = "v0.12.99 C#/F# root Microsoft.NET.Sdk: UsingMicrosoftNETSdk/UsingNETSdkDefaults=true before imports; mutable; existing caller authority retained" },
                new { id = "shared-semantic-project-extension", summary = "v0.12.100 C#/F# MSBuildProjectExtension from root project path before imports; reserved, SDK-independent" },
                new { id = "shared-semantic-path-context", summary = "v0.12.102 C#/F# MSBuildProject*/MSBuildThisFile* paths; published root, lexical document, RHS capture; contained paths stay relative; known unprovided built-ins never assumed empty; caller authority retained" },
                new { id = "shared-semantic-invariant-conditions", summary = "v0.12.101 shared scalar conditions; invariant-false F# items allow later properties; mutable guards kept" },
                new { id = "shared-daemon-session-recovery", summary = "v0.12.100 eligible startup failures reconnect the same MCP session to an existing daemon; no spawning or dispatched-call replay" },
                new { id = "shared-daemon-recovery-cause-policy", summary = "v0.12.108 recovery by cause, not retryable advice" },
                new { id = "semantic-package-root-override-authority", summary = "v0.12.71 explicit NUGET_PACKAGES: exclusive external NuGet authority for C#/F# semantic package inputs and strict F# net472 probing; absent: ordinary user-profile global cache remains supported" },
                new { id = "semantic-package-input-evidence", summary = "v0.12.71 C# semantic coverage: frameworkRefsAvailable + resolvedPackageDllCount; counts only successfully admitted package-DLL metadata references across requested loaded projects" },
                new { id = "semantic-framework-reference-override-authority", summary = "v0.12.71 CODENAV_NET472_REFS is authoritative with no host fallback; requires valid matching mscorlib, System, and System.Core assemblies" },
                new { id = "semantic-framework-reference-source-evidence", summary = "v0.12.71 frameworkRefsSource exposes the exact C# framework directory in capabilities and per-request coverage" },
                new { id = "semantic-framework-managed-assembly-inputs", summary = "v0.12.90 admits managed assemblies and facades; excludes native DLLs and standalone netmodules" },
                new { id = "csharp-semantic-central-package-property-expansion", summary = "v0.12.58 PackageVersion: bounded local-property expansion, assignment-time chains/reassignment; project overrides, later imported property authority, unsupported expressions, exceeded limits stay unresolved" },
                new { id = "shared-mcp-daemon", summary = "v0.12.59 one current-user physical-worktree index/watcher/Roslyn/FCS owner for multiple lightweight stdio MCP proxies; authority-checked named pipe or Unix socket, exact tool/schema negotiation, typed unavailable shims, client-fair admission, isolated cancellation, graceful replacement and honest failures" },
                new { id = "shared-mcp-daemon-default", summary = "since v0.12.60 ordinary MCP launches join/elect one shared physical-worktree daemon, no flag or environment opt-in; --shared-daemon compatibility alias; --standalone diagnostics only; failures never fall back to another service topology" },
                new { id = "shared-daemon-stable-unix-discovery", summary = "v0.12.61 Unix prefers an owner-verified /tmp endpoint independent of XDG_RUNTIME_DIR/TMPDIR; probes existing legacy environment-derived endpoints for authenticated connection or frozen-preamble retirement on upgrade; undiscoverable old endpoints get explicit remediation" },
                new { id = "shared-daemon-connection-dispatch", summary = "v0.12.62 independent per-connection dispatch keeps busy MCP sessions from starving frozen-preamble handshakes; typed daemon_handshake_timeout bounds admission, not retirement or restart cancellation" },
                new { id = "shared-daemon-startup-diagnostics", summary = "v0.12.63 bootstrap relays one bounded private ready/refusal report; concurrent proxies share exact owner-checked typed failures without respawn storms; stale or corrupt advisory state is ignored; safe shutdown permits re-election without exposing daemon controls" },
                new { id = "shared-daemon-canonical-index-destination", summary = "v0.12.85 physical workspace identity + host-canonical relative/absolute DB path unify Windows drive-letter case, slash, trailing-separator, cwd and workspace-link aliases without endpoint filesystem side effects; daemons accept their own legacy key during upgrade; authenticated legacy-key mismatch lets newer clients replace older daemons; same-version genuinely different destinations still fail closed" },
                new { id = "semantic-parallel-cold-start-loader", summary = "v0.12.9 C# clusters: bounded process scheduler prepares immutable inputs concurrently, then commits one dependency-ordered Roslyn solution; reload/cycle/source-over-binary authority unchanged" },
                new { id = "semantic-candidate-completeness-over-accounting", summary = "v0.12.12 aggregate semantic input accounting never omits an already-selected candidate project; bounded file capture, preparation concurrency, deadlines, and byte/managed-heap pressure retention remain the safety boundaries" },
                new { id = "semantic-planning-attribution", summary = "v0.12.13 implementations/type_hierarchy semanticOp: transitive-closure DB query+row mapping, managed identity filtering, frontier bookkeeping, seed discovery and scan-set planning; privacy-safe work counts + writer/follower accessMode" },
                new { id = "indexed-base-type-edges", summary = "v0.12.14 schema v18 stores syntax simple-name/arity base-list edges before signature truncation; implementations/type_hierarchy use indexed lookups, not repeated leading-wildcard scans. Semantic verification preserves same-name collision honesty" },
                new { id = "references-stage-attribution", summary = "v0.12.15 references semanticOp: direct-candidate/scan-set planning; post-resolution query wall split into Roslyn FindReferences, syntax-root loading, usage classification, sample text and remaining processing; privacy-safe work counts" },
                new { id = "references-deterministic-samples", summary = "v0.12.68 completed reference scans: bounded per-project samples unique by path/line/usage kind, ordinally ordered by that tuple; equal-count groups in ordinal project order, using canonical per-group project spelling, independent of Roslyn order. Read text only for final samples. Same-path/line/kind spans share a sample; totals count every span. post-response-budget sampleCoverage: selected/emitted evidence, distinct deadline/text-loss/byte-budget causes. Complete counts, queryStages.samplesRead meaning and public sample cap unchanged" },
                new { id = "references-parallel-compilation-preparation", summary = "v0.12.16 exact references prepares the selected scan set's Roslyn compilations in dependency-first waves on the same pinned Solution through one bounded process-wide lane; queryStages exposes preparation, cache reuse, queueing, wave, and completeness counts" },
                new { id = "references-document-scoped-search", summary = "v0.12.17 exact references: eligible symbols use a conservative document superset from the same leased live Solution; unsafe kinds/planning uncertainty keep full-solution SymbolFinder authority; mode/reason/count telemetry" },
                new { id = "semantic-persistent-syntax-indexes", summary = "v0.12.18 stable storage-only Solution/Project/Document ids enable cross-process checksum-validated Roslyn SyntaxTreeIndex persistence; source/project authority unchanged; documentScope reports project-wide alias-scan breadth" },
                new { id = "references-compilation-critical-path-attribution", summary = "v0.12.19 compilationPreparation: summed slot-held work, slowest project, wave-barrier floor and measured dependency critical path expose scheduling headroom without project identities" },
                new { id = "stack-safe-syntax-indexing", summary = "v0.12.20 C# declaration extraction uses an iterative depth-first walk so deeply nested generated types remain fully indexed on bounded-stack parallel workers" },
                new { id = "references-buffered-document-scope-scan", summary = "v0.12.21 exact reference scoping scans large leased SourceText through pooled windows with streaming ValueText hazard detection, and parses candidate syntax roots for global-alias widening only after a conservative raw-token prefilter" },
                new { id = "semantic-byte-governed-retention", summary = "v0.12.22 semantic project retention is governed by accounted input bytes and managed-heap pressure with hysteresis and strict safe-project LRU; multi-phase operations defer pressure eviction until the complete scan set is protected, and load telemetry exposes resident/eviction state" },
                new { id = "references-process-cpu-attribution", summary = "v0.12.23 references semanticOp telemetry publishes process-wide CPU for cluster loading and compilation preparation alongside processor/lane capacity, while PhoenixCodeNav-Semantic EventPipe phase markers share the record correlation id for trace attribution" },
                new { id = "index-raw-ordinal-symbol-batching", summary = "v0.12.24 full builds and delta refreshes persist C# symbols through cached exact-size 1..32 raw SQLite statements with ordinal binding, preserving stored output while removing managed parameter-name lookup and allocation" },
                new { id = "index-raw-ordinal-file-batching", summary = "v0.12.35 file rows use client-assigned ids and cached raw ordinal SQLite statements; full C# builds persist exact batches of 1..32 while delta and structural writes use the same one-row path" },
                new { id = "index-deferred-secondary-index-build", summary = "v0.12.35 cold builds bulk-load under primary and unique constraints, then create all nine query-facing secondary indexes inside the unpublished finalization transaction and report their measured cost" },
                new { id = "index-private-staged-rebuild-publication", summary = "v0.12.36 supported workspace-local rebuilds finalize a pinned private DB while prior publication stays readable; rebuilding covers only bounded handle drain and anchored atomic install" },
                new { id = "index-live-recovery-sidecar-publication-boundary", summary = "v0.12.68 keeps live SQLite recovery sidecars intact throughout private construction on Windows and Unix, then re-inspects and reserves or removes the current safe names only inside the post-drain publication boundary" },
                new { id = "index-schema-29-fsharp-output-rebuild", summary = "v0.12.68 schema v29 rebuilds schema-28 indexes after fresh unchanged-commit FSharp stored symbol and orphan-classification drift; external gate pins fresh counts and requires matching ordinary reuse" },
                new { id = "index-startup-rebuild-evidence", summary = "v0.12.68 server_capabilities.index startupBuildReason/startupPriorSchema retain this process's startup cause after ready; reusable-index gate rejects existing-index migration or recovery instead of treating matching rebuilt counts as ordinary reuse" },
                new { id = "index-raw-ordinal-content-fts-batching", summary = "v0.12.37 introduced exact-width raw ordinal file_contents and FTS5 batching; v0.12.38 retains raw content batches while superseding cold FTS batches with one deferred rebuild, and live writes keep transactional content+FTS updates" },
                new { id = "index-bounded-synchronous-csharp-build-handoff", summary = "v0.12.38 cold C# parsing hands prepared sources to the single writer through a bounded synchronous queue, eliminating sync-over-async ThreadPool starvation while retaining full-width writer batches" },
                new { id = "index-build-request-dispatch-isolation", summary = "v0.12.69 startup and explicit full rebuild orchestration plus cold C# producer coordination use dedicated long-running execution lanes, preserving MCP server_capabilities dispatch during unrestricted parser fan-out without changing parser concurrency, queue capacity, publication, or authority" },
                new { id = "index-deferred-fts-rebuild", summary = "v0.12.38 cold builds populate file_contents first, rebuild the external-content FTS5 index once during unpublished finalization, and retain transactional incremental FTS maintenance for live refreshes" },
                new { id = "index-size-prioritized-csharp-build-scheduling", summary = "v0.12.39 cold builds schedule C# parsing by descending scanned byte size with an ordinal-path tie-breaker, overlapping giant Roslyn parses with ordinary parse-and-persist work instead of leaving a long final straggler tail" },
                new { id = "index-abandoned-private-stage-reaping", summary = "v0.12.40 new Windows/Linux writer validates stored workspace ownership, then reaps at most 256 identity-verified crash-orphaned private stages, publish links and SQLite sidecars under retained destination authority within five seconds. Active claims, foreign publications and unrelated files stay" },
                new { id = "linux-arm64-anchored-authority", summary = "v0.12.42 Linux ARM64 ABI mapping supplies architecture-correct O_DIRECTORY and O_NOFOLLOW flags for retained index publication, bounded Git/source capture, and Operations Portal traversal while every other supported Linux architecture keeps its kernel ABI mapping" },
                new { id = "portal-directory-entry-nul-decoding", summary = "v0.12.42 Operations Portal directory traversal stops each bounded Unix directory-entry name at the first bounded NUL so record padding never becomes a file name" },
                new { id = "operations-portal-jsonl-readonly", summary = "v0.12.26 the loopback Operations Portal tails bounded workspace JSONL and observes anchored index-file presence and size without opening SQLite; source gaps, retention, paging, and response budgets remain explicit" },
                new { id = "operations-portal-live-build-status", summary = "v0.12.26 full builds emit bounded JSONL lifecycle progress plus one server identity/capability record per process so the local portal can show live phase, file, symbol, byte, version, schema, platform, and access-mode status" },
                new { id = "operations-portal-mcp-launcher", summary = "v0.12.49 open_operations_portal explicitly starts or reuses the separately packaged loopback read-only portal, keeps child output away from MCP stdout, and returns the authenticated URL for the agent to show verbatim without opening a browser" },
                new { id = "operations-portal-queryable-evidence", summary = "v0.12.50 the Operations Portal reports queryable only when the current observed index-file generation, a connected Phoenix process, and that process's successful retained query agree; changing the observed generation invalidates old query evidence, freshness remains unknown, and the workspace's retained operation count stays stable across unchanged refreshes" },
                new { id = "operations-portal-deterministic-semantic-summary", summary = "v0.12.51 the multi-instance Operations Portal aggregates semantic state independently of instance ordering: unanimous warm, warming, cold, or unknown evidence remains exact, while differing states report mixed" },
                new { id = "mcp-structured-argument-errors", summary = "v0.12.50 every registered MCP tool preserves its required JSON schema while missing or mistyped fields return a structured bad_request naming the tool, field, reason, and expected type so agents can self-correct" },
                new { id = "implementations-semantic-retry-guidance", summary = "v0.12.50 transient implementations fallbacks caused by cluster_cold_load or semantic_timeout retain honest heuristic confidence and a machine-readable semantic cause while adding retryRecommended and retryHint; Phoenix does not retry automatically or raise the requested deadline" },
                new { id = "search-symbol-malformed-query", summary = "v0.12.10 search_symbol rejects ToolSearch-style select: routing prefixes with malformed_query instead of returning a clean empty result; valid C# qualification and generic punctuation remain searchable" },
                new { id = "search-symbol-filtered-existence", summary = "v0.12.30 first-page empty search_symbol results report existsUnfiltered plus active appliedFilters; declarations hidden by those filters also disclose their unfilteredKinds, while genuine absence remains a clean symbols:[] result with existsUnfiltered:false" },
                new { id = "search-symbol-type-relevance", summary = "v0.12.30 exact-name type declarations receive a soft relevance preference over same-named members without filtering or omitting either result class" },
                new { id = "indexed-path-suggestions", summary = "v0.12.31 outline/source_context not-found errors and first-page exact-path find_file misses may include a byte-budgeted pathSuggestions object with up to three pinned-index paths, exact total and truncation state; basename matches rank by preserved suffix then prefix and are never substituted" },
                new { id = "source-context-range-alias", summary = "v0.12.32 source_context accepts range as a compatibility alias when canonical spans is omitted; conflicting simultaneous values return bad_request instead of applying silent precedence" },
                new { id = "batch-outline-json-array-paths", summary = "v0.12.29 batch_outline accepts comma-separated paths or a serialized JSON string array; v0.12.44 applies the shared 64 KiB exact workspace-relative grammar, rejecting rooted, traversing, control-character, malformed, non-string, and over-12 inputs before lookup" },
                new { id = "csharp-symbol-free-outline", summary = "v0.12.43 outline and batch_outline return normal indexed syntax envelopes with symbols:[] and file-level generated state for indexed declaration-free C# files" },
                new { id = "refresh-review-json-array-paths", summary = "v0.12.44 refresh_index and review_pack accept comma-separated paths or serialized string arrays within a 64 KiB input bound, preserve comma-bearing JSON paths without wrapper leakage, reject non-relative, traversing, malformed, and over-limit items before lookup, and refuse more than 256 explicit paths" },
                new { id = "refresh-input-retry", summary = "v0.12.7 unavailable regular-source captures roll back the complete delta transaction and retry initial or event-driven serialized requests after bounded 100/250/1000 ms delays; timer-initiated stale-index recovery uses its separately declared paced cadence" },
                new { id = "refresh-sweep-publication-gating", summary = "v0.12.7 builds and refreshes persist a follower-visible refresh_sweep_pending marker before publication or row mutation and clear it only after the serialized convergence sweep commits" },
                new { id = "refresh-incomplete-freshness", summary = "v0.12.7 exhausted source capture keeps index state stale, preserves the Git baseline, exposes a stable refreshIncompleteReason plus bounded paths, and widens the next request to a recovery sweep" },
                new { id = "refresh-recovery-self-heal", summary = "v0.12.27 an index left stale by unavailable workspace input autonomously retries complete convergence sweeps with 5/10/30/60-second capped backoff; timer-initiated recovery sweeps make one capture attempt each, re-resolve pending Git baselines, and remain honestly stale until success" },
                new { id = "oversized-source-coverage", summary = "v0.12.7 oversized regular sources are a distinct persistent outcome with explicit bounded coverage; they are not rapidly retried, cannot publish ready/current/exact evidence, and prevent strict worktree index installation" },
                new { id = "fsharp-unsupported-language-boundary", summary = "F# hierarchy, C#-targeted ProjectReference semantics, compatibility fallback from multi-target children, and netstandard1.x compile inputs remain unsupported; F# search, definition closure, implementations, callers/callees, explicit multi-target project/TFM selection, exact child-TFM matching, and single-target netstandard2.0/2.1 semantics are supported" },
                new { id = "fsharp-workspace-coverage-tokens", summary = "v0.12.88 uses nine operation-neutral fsharp_workspace_* partial-reason tokens, with prior names retired without aliases" },
                new { id = "fsharp-implementations-workspace", summary = "v0.12.87 resolves compile-owned F# type and dispatch-slot positions with FCS, including typed object expressions; scans proven workspace contexts with concrete before abstract ordering and honest lower-bound coverage" },
                new { id = "fsharp-callers-workspace", summary = "v0.12.88 compile-owned F# callable positions cover proven workspace dependents with global physical-site deduplication and honest lower-bound coverage" },
                new { id = "fsharp-callees-body", summary = "v0.12.88 resolves the innermost compile-owned F# body only through its source-ProjectReference closure, without a workspace-dependent scan" },
                new { id = "review-fsharp-file-coverage", summary = "review_pack: F# changes in unsupportedLanguageFiles" },
                new { id = "compiled-awareness", summary = "search_symbol orphaned; repo_overview.orphanedFiles; compiled ownership guides semantic resolution, impact, and context_pack" },
                new { id = "text-search-compiled-awareness", summary = "token/regex hits, token samples: orphaned for unowned .cs/.fs/.fsi (indexed)" },
                new { id = "git-awareness", summary = "v0.12.28 indexed commit/branch; serialized HEAD snapshot acquisition, ordered recovery publication, rebuild-generation retirement, execution-time diffs preserve final rows/attachment through same-commit attachment changes and rapid inverse transitions. Detached HEAD clears indexed branch. Unavailable recovery snapshots force older queued Git tuples to revalidate; ready requires resolved generation at/after latest unavailable sample. Full rebuilds reject ordered recovery samples from replaced DB. repo_overview.git: indexed vs HEAD, commit match. .cmd/.bat Git: cmd + hex-gated args; commit-less repos: reflog watch attaches on .git/logs creation; unresolved Git logged" },
                new { id = "vendor-noise", summary = "queryScope='first_party' / excludePath / per-hit 'noise' flag / repo_overview.suggestedExcludes" },
                new { id = "text-search", summary = "search_text: whole-word tokens, context, containingSymbol, precise/partial grading; bounded line-based .NET regex narrowed by FTS; filesTotal/budgetHit/timedOut disclose coverage. Zero hits probe elsewhere/didYouMean; suggestions are probed, never substituted, with path/line/owner samples" },
                new { id = "reference-kinds", summary = "references (exact path): per-location execution, declaration, documentation, and conversion-operation kinds with a kinds breakdown, usageKinds filter (validated), and publicConsumersOnly (usages outside the symbol's declaring project); indexed fallback stays unclassified and says so" },
                new { id = "symbol-handles", summary = "Reindex-detecting idx handles pin source_context, definition, references, impact, implementations, and type_hierarchy" },
                new { id = "filter-honest-counts", summary = "references: totalReferences/totalCandidates, kinds, groups, and summary all honor includeTests (filtered BEFORE counting on both exact and indexed paths); linked multi-project files counted once; filtered summaries say 'test projects excluded' instead of a misleading '0 test'" },
                new { id = "test-classification", summary = "isTest: package/binary nunit/xunit/MSTest references, including names containing nunit.framework; compiled [TestFixture] graph leaves promoted without framework refs. Names: narrow dotted-suffix fallback, never TestRoute. Schema v7 broadened reference signals; older cached isTest may differ. Since v8, same-AssemblyName csproj pairs share classification" },
                new { id = "bounded-source-reads", summary = "source_context streams only the requested spans from disk (never whole-file reads); contextLines clamped; zero/negative span starts clamp to line 1" },
                new { id = "arity-exact-partials", summary = "partialFiles separate Foo and Foo<T> by syntax arity" },
                new { id = "member-modifiers", summary = "outline/search_symbol/symbol_at/definition: modifiers=static/sealed/abstract/virtual/override/new/readonly/const (schema v4; omitted if none). partial uses isPartial on every symbol, plus outline-type partialFiles. accessors={get:'public',set:'private'} only for accessibility differing from the member (schema v9)" },
                new { id = "rebuild-hatch", summary = "refresh_index force:auto|incremental = delta, skip hash-identical files, never rebuild intact-looking index; full = delete/rebuild from scratch (pump-serialized, even in failed state; reattach watcher/git tracking, clear old error; watch index.progress). In-band corruption recovery without shell" },
                new { id = "deadline-honesty", summary = "Semantic tools return deadlineMs/elapsedMs. Mid-scan expiry keeps partial, totalIsLowerBound, and 'at least N'; cold load uses partialReason cluster_cold_load. references/implementations split clusterLoadMs/queryMs. Reference totals dedupe project+path+source-span+kind, preserving distinct same-line operations, expose solutionProjects, and retain outOfGraphCandidates" },
                new { id = "assembly-ref-edges", summary = "IN-WORKSPACE legacy Reference+HintPath assemblies (including staged DLLs) create dependents, semantic-cluster and project_graph edges. Binding prefers SOURCE (source-over-binary) for exact cross-project implementations/references. Assembly-name collisions retain all consumers via name-level edges (schema v6+, recovery v5); responses carry meta.indexSchema" },
                new { id = "build-progress", summary = "building only: server_capabilities.index.progress + index_building carry phase=scanning|parsing_projects|indexing_files|finalizing, filesIndexed/filesTotal/elapsedMs; monotonic counts, no fake percent; no ready/background-refresh bar. filesSkipped/projectsFailed only >0. filesPerSecond/estimatedRemainingMs only after >=100 files over >=1s in indexing_files. pendingProcessed=monotonic applied deltas; with pendingChanges both flat means stuck pump" },
                new { id = "edge-provenance", summary = "Schema v10 projectReference/hintPathReference edges: project_graph.kind, dependency_path.via, context_pack hint-path owners, impact.directDependentProjects.viaHintPathOnly. Mixed paths: one transitive count/note; dual wiring prefers project; orphans: orphaned:true, no project" },
                new { id = "review-pack", summary = "review_pack: ONE budget-bounded call; (validated-base Git diff + working-tree dirt) or explicit paths -> hunk-mapped symbols/per-symbol impact digests: symbolId handle, owner, directDependentProjects (+viaHintPathOnly), transitive count, publicApi, related_tests signal, indexed reference candidates, deterministic risks. Deleted .cs: read-only base blob; danglingCandidates per former top-level type (DELETION honesty). All disclose INDEXED confidence; stable note ids; no semantic resolution. Escalate: references(symbolId, mode:'semantic'). baseRef: sha or strict-charset ref name" },
                new { id = "review-git-stdin-transport", summary = "v0.11.1 Git operand transport: review_pack resolves refs with cat-file --batch-check and reads base blobs with cat-file --batch; accepted dynamic ref names and paths travel on stdin, while only validated 4-64 ASCII-hex prefixes may use rev-parse --disambiguate=<hex>, preventing .cmd/.bat metacharacter reinterpretation" },
                new { id = "review-diff-determinism", summary = "--raw -z --patch uses ordinal/C-quoted path identity; binary/mode/empty/type sections degrade whole-file; old/new hunk-coordinate overflow fails closed as malformed; stage-only unmerged gitlinks report unmerged; process/status failures never become partial success" },
                new { id = "review-content-filter-refusal", summary = "v0.11.1 content-filter refusal: review_pack discovers configured clean/process drivers and evaluates tracked filter attributes without executing helpers; a path selecting an active driver returns git_filter_unsafe rather than running it or claiming complete coverage" },
                new { id = "review-content-filter-overlay", summary = "v0.11.1 content-filter race boundary: every worktree comparison uses a private highest-precedence info/attributes overlay ending in * !filter, so no clean/process driver can become selectable after preflight, including a newly introduced driver" },
                new { id = "review-submodule-coverage", summary = "v0.11.1 submodule boundary: parent review excludes dirty child worktrees and reports that boundary through coverage.submoduleWorktrees plus review.submodule_worktrees_excluded; changedSubmoduleLinks separately reports superproject gitlink pointer changes for child-root follow-up" },
                new { id = "review-untracked-repository-coverage", summary = "v0.11.1 nested-repository boundary: parent review treats an untracked embedded Git worktree as an atomic excluded path without running child-local helpers, reports bounded coverage.untrackedRepositories, and emits review.untracked_repositories_excluded for child-root follow-up" },
                new { id = "review-untracked-link-coverage", summary = "v0.11.2 untracked link boundary: Git-reported files reached through a symbolic link or junction are excluded before hashing or review aggregation, reported through bounded coverage.untrackedLinks, and emit review.untracked_links_excluded for target-root follow-up" },
                new { id = "review-layered-change-refusal", summary = "v0.11.1 layered-change refusal: when independent staged and unstaged manifests contain the same path, review_pack returns git_layered_changes rather than presenting a final-worktree hunk map as coverage of both byte layers" },
                new { id = "review-snapshot-consistency", summary = "Repeated bounded Git captures compare exact raw patch bytes with typed staged/unstaged/unmerged/untracked manifests; symlink payloads, gitlinks, modes, and tracked bytes must match; snapshot_changed becomes git_worktree_changed with no partial result from different worktree epochs" },
                new { id = "review-live-evidence-revalidation", summary = "v0.12.45 review_pack revalidates every bounded live file digest and safe existence classification after aggregation, latches contradictory repeated observations, and recaptures only bounded untracked move-candidate bytes actually consumed; any mismatch fails closed without a partial result" },
                new { id = "csharp-conversion-operator-indexing", summary = "v0.12.46 schema v21 indexes implicit and explicit C# conversion declarations as operator rows with target-bearing names, canonical declaration keys, modifiers, source order, and parent links" },
                new { id = "csharp-conversion-semantic-handles", summary = "v0.12.48 schema v24 conversion idx handles pin semantic definitions and references with uncapped canonical declaration keys; fingerprints bind the existing per-file content hash to a deterministic syntax ordinal among declarations on the same source line, distinguishing same-file twins without follow-up queries, invalidating the file epoch conservatively without a per-symbol context digest, and rejecting older identities that cannot prove the current row" },
                new { id = "references-candidate-file-cap-disclosure", summary = "v0.12.46 indexed references disclose the existing caller-selected maxFiles candidate-file bound through coverage, candidate_file_cap, references.candidate_file_cap, and lower-bound totals instead of presenting a scanned subset as complete" },
                new { id = "csharp-conversion-usage-enumeration", summary = "v0.12.47 semantic references enumerate compiler-bound implicitConversion, explicitConversion, and checkedConversion sites across the selected dependent closure, including stacked, nullable-tuple, full C# compound-assignment, primary-constructor, foreach, deconstruction, and interface-dispatch carriers, so exact zero means no matching conversion was found in complete loaded coverage" },
                new { id = "csharp-foreach-conversion-operator-kind", summary = "v0.12.68 classifies compiler-selected foreach conversions by operator identity so explicit, implicit, and checked operators retain their distinct usage classification" },
                new { id = "csharp-operator-semantic-handles", summary = "v0.12.47 regular and conversion operator idx handles pin definition/references with canonical syntax declaration keys, including checked and explicit-interface forms; indexed definition retains the resolved row, indexed/failed-auto references fail closed, and implementations/type_hierarchy reject operator handles instead of retargeting" },
                new { id = "csharp-explicit-interface-operator-accessibility", summary = "v0.12.47 schema v23 persists explicit-interface regular operators as private so search, outline, and review_pack do not overstate public API" },
                new { id = "semantic-indivisible-identity-completeness", summary = "v0.12.47 definition/references remove optional declaration-site lists with truthful declaration totals and stable note id semantic.declaration_sites_budget before preserving a complete indivisible compiler symbol identity above ordinary hardBytes; responseBudget then reports measured serializedBytes, exceeded:true, completeIdentity:true, and reason:indivisible_semantic_identity without identity truncation or rejection" },
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
                new { id = "review-exact-move-evidence", summary = "v0.11.2 exact move evidence: movedFiles reports unique staged or unstaged .cs raw-byte relocations as exact_blob; untracked candidates are read through size/count-bounded anchored no-follow handles, while oversized or excess candidates conservatively remain uncorrelated" },
                new { id = "review-normalized-move-evidence", summary = "v0.12.44 review_pack correlates a unique untracked C# worktree CRLF candidate whose normalized bytes match a stored LF blob as normalized_blob, never exact_blob; raw-byte identity remains preferred, each target is claimed at most once, and ambiguous candidates remain uncorrelated" },
                new { id = "review-base-blob-recovery-honesty", summary = "v0.11.2 base-blob recovery honesty: per-file size plus cumulative character/attempt/time bounds appear in baseBlobRecoveryCoverage; batch-check rejects oversized blobs before content streaming, failures emit review.base_blob_unavailable, cumulative exhaustion emits review.base_blob_budget, and recoveryStatus/unmapped evidence omits unknown former-type totals instead of serializing false zero coverage" },
                new { id = "review-namespace-analysis-budget", summary = "v0.11.2 namespace analysis budget: namespace-only classification loads indexed content only for uncovered ranges and stops at per-file plus cumulative character/file/time bounds; namespaceAnalysisCoverage and review.namespace_analysis_budget mark conservative file_level fallback" },
                new { id = "review-project-shape-budget", summary = "Bounded no-follow XML caps project count, bytes, and time; projectOwnershipFallbackCoverage plus review.project_shape_budget disclose incomplete deleted-path proof" },
                new { id = "review-project-glob-budget", summary = "Iterative project-ownership glob budget covers default-SDK checks and Include/Exclude; globBudgetHit plus review.project_glob_budget expose segment, operation, or deadline exhaustion and fail proof closed" },
                new { id = "review-project-shape-completeness", summary = "Unevaluated imports/SDKs/conditions/expressions block deleted-path proof; projectOwnershipFallbackCoverage.evaluationIncomplete and review.project_shape_incomplete disclose it" },
                new { id = "review-project-file-guidance", summary = "v0.12.41 changedProjectFiles reports every modified or deleted project, build, and solution input; review.project_files_changed counts only authoritative .csproj/.fsproj/.csproj.user/.fsproj.user/.shproj/.proj/.projitems/.props/.targets and Directory.Build.rsp/MSBuild.rsp inputs and warns that dependency, compile-set, or test-classification evidence may shift" },
                new { id = "review-solution-metadata-guidance", summary = "v0.12.44 .sln/.slnx/.slnf changes remain visible in changedProjectFiles and emit review.solution_files_changed, while changedProjectFilesCoverage splits authoritative and solutionMetadata counts; solution metadata never invalidates exact-move, declaration-survivor, ownership, dependency, build, or symbol-resolution proof" },
                new { id = "review-deleted-solution-metadata-counts", summary = "v0.12.68 retains deleted .sln/.slnx/.slnf metadata in changedFiles totals while excluding it only from source-deletion expansion and former-symbol analysis; review.deleted_solution_metadata_scope discloses that distinct cause" },
                new { id = "review-default-baseline-honesty", summary = "v0.11.4 bounded git_index_baseline_unavailable gives refresh_index or explicit baseRef guidance; caller-supplied invalid refs remain bad_request" },
                new { id = "review-unmapped-change-coverage", summary = "v0.11.2 unmapped change coverage: namespace and file-level C# regions not fully covered by reviewable indexed symbols appear in bounded unmappedChanges records with explicit side, old/new coordinates, reason, and total/returned/truncated" },
                new { id = "review-index-epoch-consistency", summary = "review_pack pins rows and response metadata to one stable SQLite read epoch; an overlapping refresh cannot mix old symbols with new ownership or health evidence" },
                new { id = "review-per-hunk-type-mapping", summary = "Type/member suppression is evaluated per old/new hunk, so a type-header edit remains reviewable when a separate hunk touches one of its members" },
                new { id = "stable-note-ids", summary = "Stable machine-matchable ids, not prose: review_pack notes={id,text}; references.noteId=zero_loading_gap; references.sampleCoverage.reasons[].noteId=samples_deadline|samples_trimmed|samples_byte_budget; impact.transitiveNoteId=transitive_single_count; type_hierarchy.noteId=heuristic_fallback; search_text.noteId=did_you_mean|elsewhere_matches|absent_everywhere. Additive retrofits; one id per cause; one cause per id. Prose may change; ids may not" },
                new { id = "worktree-indexes", summary = "On Windows and Linux, worktrees lists anchored sibling status and index_worktree creates or refreshes a Git-validated target; macOS is unsupported for both operations" },
                new { id = "index-write-destination-authority", summary = "IndexManager and direct IndexBuilder writes validate database/WAL/SHM/rollback-journal leaves and parents: Windows pins the full no-delete-share chain, Linux writes through a held directory fd, and macOS performs startup and per-open identity revalidation only" },
                new { id = "worktree-index-platform-policy", summary = "Windows uses targeted indexed_commit-to-HEAD plus dirt reconciliation, Linux uses an anchored full sweep with usedFullSweep=true, and macOS returns unsupported_platform" },
                new { id = "worktree-index-destination-isolation", summary = "Sibling SQLite work stays in private staging; an anchored no-follow destination atomically publishes the checkpointed database and refuses linked database, WAL, SHM, and rollback-journal paths without touching their targets" },
                new { id = "worktree-index-lease", summary = "A cross-process ownership lease guards every writable Phoenix index lifetime; index_worktree returns worktree_index_locked while another Phoenix owns that target" },
                new { id = "worktree-response-budget", summary = "worktrees may trim every item to zero, and index_worktree UTF-8-bounds reflected paths/details with truncation metadata before enforcing the complete hardBytes envelope" },
                new { id = "single-workspace-writer-mutex", summary = "v0.12.34 one crash-recoverable identity-named mutex per workspace/worktree elects the sole watcher, refresh, rebuild, and mutation owner; cross-worktree acquisition is zero-wait" },
                new { id = "index-destination-claim", summary = "v0.12.34 a crash-recoverable database claim binds --index-db to its physical workspace and publishes ready|rebuilding; follower double-checks prevent new SQLite opens from barging across replacement" },
                new { id = "semantic-large-repo-budget", summary = "default all candidates; positive maxProjects bounds" },
                new { id = "related-tests-signal", summary = "related_tests/impact/context_pack: signal ranks sampled mentions callSite > typeUsage > nameMention; heuristic, not compiler evidence. Naming-convention/project-reference and ungraded mention groups omit it: absent means UNGRADED, never nameMention. Located samples retain actual mention line + text" },
            },
            tools = new[]
            {
                "server_capabilities", "repo_overview", "find_file", "search_text", "outline",
                "source_context", "search_symbol", "symbol_at", "definition", "references",
                "implementations", "callers", "callees", "type_hierarchy", "related_tests",
                "dependency_path", "config_lookup", "batch_outline", "context_pack", "impact",
                "project_graph", "projects_containing", "refresh_index",
                "worktrees", "index_worktree", "review_pack", "open_operations_portal",
            },
            budgets = new
            {
                softBytes = Json.SoftBudgetBytes,
                hardBytes = Json.HardBudgetBytes,
                defaultLimit = DefaultResultLimit,
                indivisibleSemanticIdentity = "definition/references/implementations/callers/callees and a documentation-id ambiguity floor may exceed hardBytes only to preserve one complete compiler identity or exact recovery candidate; responseBudget reports the measured exception",
            },
            confidenceModel = new
            {
                exact = "compiler-verified Roslyn semantic resolution, or a successful bounded FCS semantic result whose disclosed partial reasons preserve selected-context authority",
                indexed = "index/syntax-backed evidence, or a bounded FCS semantic result with an error, authority loss, or an unclassified partial reason — inspect error and partialReason before edits",
                heuristic = "naming/text inference — a lead, verify before relying on it",
            },
            semantic = new
            {
                engine = "Roslyn ad hoc for C#; bounded FCS for compile-owned .fs/.fsi",
                fsharpProjectModel = fsharpProjectModel.ToString().ToLowerInvariant(),
                frameworkRefsAvailable,
                frameworkRefsSource,
                exactTools = new[] { "definition", "references", "implementations" },
                exactToolsLanguage = "cs",
                csharpExactTools = new[] { "definition", "references", "implementations" },
                fsharpSemanticTools = new[]
                {
                    "symbol_at", "definition", "references", "implementations", "callers", "callees",
                },
                fsharpSyntaxIndexedTools = new[] { "search_symbol" },
                note = "C# exact: loaded clusters, never last-built dependency DLLs. Evaluated F#: symbol_at/definition compiler-checked in selected project/TFM + ProjectReference closure; references/implementations/callers add proven source dependents, callees body-local. potentialConsumersUnevaluated/group statuses show workspace lower bound/filters. TraitCall unresolved. Success is exact only with authority-preserving disclosed reasons; every error, authority loss or unclassified partial reason is indexed. Simple default: exact binding; totalIsApproximate/countScope, scanIncomplete. Both F# modes: explicit multi-target project/TFM, exact child-TFM, single-target netstandard2.0/2.1 supported; multi-target child compatibility fallback/netstandard1.x compile inputs fail closed; assets snapshotted.",
                fsharpSyntaxNote = "F# search_symbol is syntax-indexed across the available owner/TFM parse contexts, including orphaned .fs/.fsi files; it is not compiler-checked, reports actionable incomplete context coverage as partial, and keeps ordinary SDK/import limits advisory.",
            },
            index = new
            {
                state,
                mode = h.AccessMode,
                fsharpParseOwnerCoverage = new
                {
                    response = "search_symbol.fsharpParseCoverage",
                    aggregation = "per_file_owner_incidences",
                    truncatedOwnerProjects = "owners with at least one omitted parse context",
                    unrepresentedOwnerProjects = "owners with no retained parse context",
                    partiallyTruncatedOwnerProjects = "owners with some but not all parse contexts retained",
                    invariant = "truncatedOwnerProjects = unrepresentedOwnerProjects + partiallyTruncatedOwnerProjects",
                },
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
                startupBuildReason,
                startupBuildReasonTruncated = startupBuildReasonTruncated ? true : (bool?)null,
                startupBuildReasonBytes,
                startupPriorSchema,
                startupPriorSchemaTruncated = startupPriorSchemaTruncated ? true : (bool?)null,
                startupPriorSchemaBytes,
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
                refreshIncompleteReason = h.RefreshIncompleteReason,
                incompleteSourcePaths = incompletePaths,
                incompleteSourcePathCount = h.RefreshIncompleteReason is null
                    ? (int?)null
                    : h.RefreshIncompletePathCount,
                incompleteSourcePathCountLowerBound =
                    h.RefreshIncompleteReason is not null &&
                    h.RefreshIncompletePathCountIsLowerBound
                        ? true
                        : (bool?)null,
                incompleteSourcePathsTruncated = incompletePathsTruncated
                    ? true
                    : (bool?)null,
                dbBytes = h.DbBytes,
                workspaceRoot,
                workspaceRootTruncated = workspaceRootTruncated ? true : (bool?)null,
                workspaceRootBytes,
            },
        };
        return applyBudget
            ? Json.WithCapabilitiesBudget(envelope, includeFeatureSummaries)
            : Json.Serialize(envelope);
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
                headMatchesIndex = h.RefreshIncompleteReason is null &&
                    headCommit is not null && h.IndexedCommit is not null
                    && string.Equals(headCommit, h.IndexedCommit, StringComparison.OrdinalIgnoreCase),
            };

        return Json.Serialize(new
        {
            workspaceRoot = h.WorkspaceRoot,
            projects = new
            {
                total = stats.Projects,
                csharp = stats.CSharpProjects,
                fsharp = stats.FSharpProjects,
                legacyStyle = stats.LegacyProjects,
                sdkStyle = stats.SdkProjects,
                test = stats.TestProjects,
            },
            solutions = stats.Solutions,
            csFiles = stats.CsFiles,
            fsFiles = stats.FsFiles,
            mdFiles = stats.MarkdownFiles,
            sqlFiles = stats.SqlFiles,
            totalLines = stats.TotalLines,
            symbols = stats.Symbols,
            generatedFiles = stats.GeneratedFiles,
            // C# plus compile-form F# source (.fs/.fsi) with no indexed compile owner.
            // F# scripts (.fsx) are intentionally excluded. The graph
            // expands <Compile Include> globs and honors <Compile Remove>; residual gaps are shared
            // .projitems, props-level globs, and ignored Conditions; this is not native-build proof.
            // Per source symbol/text hit and structured text suggestion sample: orphaned.
            orphanedFiles = stats.OrphanedFiles,
            targetFrameworks = stats.TfmBreakdown,
            // Vendored/generated directory globs detected in the index — pass to search_symbol /
            // search_text excludePath (or queryScope='first_party') to drop third-party noise. [] when none.
            suggestedExcludes = q.SuggestedExcludes(),
            git,
            meta = Meta.From(h, "indexed", "text"),
        });
    }
}
