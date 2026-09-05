# PhoenixCodeNav — Architecture & Design

This document describes how PhoenixCodeNav is built. For *why* it exists and how it
compares to grep / Cursor / other tools, see [`intro.md`](./intro.md).

## Solution layout

```
PhoenixCodeNav.sln
├── src/CodeNav.Core/          # all the engine: discovery, index, semantic layer
│   ├── Discovery/             # WorkspaceScanner, ProjectFileParser (legacy+SDK csproj), SolutionParser (.sln/.slnx/.slnf)
│   ├── Indexing/              # IndexStore (SQLite schema/writes), IndexQueries (reads), IndexBuilder (build pipeline),
│   │                          #   SyntaxIndexer (Roslyn parse), DeltaRefresher (incremental), WorkspaceWatcher (FSW),
│   │                          #   IndexManager (lifecycle), CompileItemResolver, FileClassifier
│   ├── Semantic/              # SemanticWorkspace (AdhocWorkspace, lazy clusters, LRU),
│   │                          #   SemanticService(+.Graph) (definition/references/impls/callers/callees/hierarchy),
│   │                          #   ReferenceAssemblyLocator
│   └── WorkspacePaths.cs      # path-containment + reparse-point safety
├── src/CodeNav.FSharp/        # isolated, pinned FCS syntax-declaration/semantic adapter
├── src/CodeNav.Mcp/           # the server, published as PhoenixCodeNav.Mcp.exe
│   ├── Program.cs             # host + stdio transport; starts indexing in the background
│   ├── NavigationTools*.cs             # 27 MCP tools across partial-class files
│   └── Responses.cs          # JSON policy, response budgets, the Meta envelope
├── src/CodeNav.WorkspaceGen/  # deterministic generator for workspaces with thousands of projects
├── src/CodeNav.Bench/         # cold-build + warm-query benchmarks vs the latency targets
├── tests/CodeNav.Tests/       # fast unit and contract checks
├── tests/CodeNav.IndexTests/  # index/semantic behavior; shared immutable functional index
├── tests/CodeNav.GitTests/    # isolated repositories, worktrees, diffs, and Git safety
├── tests/CodeNav.WatcherTests/ # isolated watcher timing checks
└── tests/CodeNav.LifecycleTests/ # writer leases, publication, and process lifecycle
```

The functional index collection builds its standard generated workspace once and reuses it
for read-only behavior checks. Tests that mutate files, SQLite state, Git repositories, or
watchers keep exclusive workspaces and writer ownership.

`CodeNav.Core` has no dependency on the MCP SDK — it is a plain library that could back a
different front end. `CodeNav.Mcp` is a thin protocol/shaping layer over it.

## The four navigation layers

Agents use the cheapest layer that answers the question, preferring compiler-backed facts
for code identifiers.

1. **Indexed text** — `find_file`, `search_text`, `source_context`, `config_lookup`. SQLite FTS5
   over C#, F#, Markdown, SQL, project, solution, and configuration contents with workspace-aware
   ranking and byte/line offsets. Markdown and SQL are text-only: they do not enter syntax or
   compiler-semantic services. `search_text` applies every caller filter before ranking, grades
   at most 300 matching files, and exposes `filesScanned`, `filesAtLeast`, `partial`, and
   `partialReason:"candidate_file_cap"` when that bound clips coverage. Each graded line is
   `precise` (contains all query tokens as whole tokens) or `partial` (a token-covering lead), so
   a partial-token match is never presented as a full hit.
2. **Syntax (C#)** — `outline`, `search_symbol`, `symbol_at`, `batch_outline`. Roslyn
   *syntax-only* parsing (no compilation) extracts namespaces/types/members with spans,
   signatures, accessibility, partial flags, and generated/test classification. This is the
   token-saver: `outline` before any large-file read, then `source_context` for the spans.
3. **Syntax (F#)** — `search_symbol` over persisted `.fs` / `.fsi` declaration rows and `outline`
    for compile-owned `.fs` and `.fsi`. A pinned, isolated FSharp.Compiler.Service adapter parses
    without type checking; `.fsx` stays text-only. Stored symbol search deterministically composes
    every available owner/TFM parser context, reserves one context per valid compile owner while
    the 64-context budget has capacity, fills the remainder in global ordinal order, and persists exact
    total/processed/truncated counts. `truncatedOwnerProjects` remains the compatibility total for
    owner/file incidences missing at least one context; `unrepresentedOwnerProjects` retained none,
    while `partiallyTruncatedOwnerProjects` retained some but not all, and the two disjoint counts
    sum to the total. These scope aggregates are incidences per affected file rather than distinct
    project identities. The counts sit alongside FCS parse failures and project-option
    unavailable/partial causes, so a hit or miss remains explicitly partial whenever actionable
    context authority is incomplete or contexts were omitted. Ordinary SDK/import limitations remain advisory structured
    coverage rather than making every search partial. Cold build and delta refresh use the same
    context-selection model. Ordinary project changes parse only their old/new owned F# files
    before the SQLite persistence phase; a malformed-project authority transition refreshes global
    coverage, but reparses only files whose effective parser-context set changes.
    An exact same-directory legacy `Project.fsproj` + dual-target `Project.Net.fsproj` ownership
    pair selects the single-target legacy parse context and discloses up to 64 project/TFM parse
    contexts with complete coverage counts. A parse context controls only F# `#if` symbols and
    parser options; it does not select assemblies, builds, reference resolution, or semantic
    workspaces. Without that base owner, a multi-target project selects its first declared TFM and
    marks the result partial.
4. **Semantic** — C# uses lazy Roslyn compilations for `definition`, `references`,
   `implementations`, `callers`, `callees`, and `type_hierarchy`. F# uses a bounded FCS
   type check for position-based `symbol_at` and `definition` through the selected root's
   exact-TFM F# `ProjectReference` closure under the evaluated MSBuild transitivity policy, plus `references` counted within the selected
   physical root. F# references enumerate compiler-bound non-definition uses from that root,
   keep the selected-project count exact while bounding only response
   samples, and expose that count as a workspace lower bound because dependent projects are not
   scanned. An F# type-check
   context is exactly one physical `.fsproj` plus one target framework; ambiguous files require
   explicit selection, and the selection never changes or merges ownership/reference graph facts.
   C# regular and conversion operator handles bridge syntax rows to Roslyn with the uncapped
   declaration key, not the display name or capped display signature. Compiler-bound operation
   tree scans enumerate implicit, explicit, checked, stacked, nullable-tuple, and interface-dispatch
   user-defined conversions across the selected dependent closure; supplemental Roslyn APIs cover
   full C# compound-assignment input/output conversions,
   primary-constructor base arguments, `foreach` elements, and deconstruction conversions that
   are not operation-tree children. The corresponding reference
   kinds are `implicitConversion`, `explicitConversion`, and `checkedConversion`, and source-span
   dedup preserves distinct same-line operations. Indexed definition retains the handle-resolved
   operator row; indexed or failed-automatic references fail closed rather than widening by name.
   Operator implementations remain explicitly unsupported, including the meaningful static
   abstract interface subcase. Semantic `definition` and `references` retain the ordinary 64 KiB
   target and remove optional declaration sites first with truthful counts and stable note id
   `semantic.declaration_sites_budget`, but one intrinsically larger identity remains
   complete and carries measured `responseBudget` exception metadata rather than being truncated
   or rejected.

   Literal local SDK `InternalsVisibleTo` items are modeled, but imported or package/build authority
   remains explicitly unproven. For internal symbols, the selected dependent consumer scan—not the
   projects that happened to produce compiler-bound result groups—decides whether that uncertainty
   can affect the answer. A candidate that cannot bind because the grant was not modeled therefore
   keeps `project_model_unproven` instead of allowing an incomplete same-assembly result to certify
   itself as exact.

`open_operations_portal` is an explicit operational tool outside the navigation layers. It starts
or reuses the separately packaged, loopback-only, read-only portal for the current workspace and
returns its authenticated URL; the agent must show that URL verbatim and the tool never opens a
browser. The companion speaks one bounded private JSON handshake over redirected pipes, so its
output cannot enter the MCP stdout framing. A per-workspace runtime lock and descriptor allow
independent MCP processes to converge on one live portal while stale owner state is recoverable;
reuse requires `/healthz` to echo the descriptor's private session identity and PID. Coordination
state lives below the current user's profile. Unix rejects unsafe writable ancestors and forces
each Phoenix-owned directory to owner-only modes. Every platform rejects reparse-point ancestors;
Windows otherwise relies on inherited current-user profile ACLs. These checks run before lock or
descriptor access. The startup/reuse attempt is bounded to 30 seconds and cleanup
terminates only that newly launched helper/owner attempt. Portal absence or failure has no effect
on index or navigation correctness. The portal does not infer `ready` or freshness from file
presence. It derives the narrower `queryable` presentation only when the current anchored
index-file generation, a connected Phoenix process, and a successful retained operation from that
same process agree. Any observed index stamp change invalidates earlier query evidence; failed
operations, stale processes, and index-only observations remain `unknown`. Workspace
bootstrap data carries the retained operation count independently of the default operation page,
so unchanged polling neither changes the value nor restarts its animation.

Exact path identity is never fuzzy. When `outline` or `source_context` cannot resolve a path, or
the first page of an exact-path `find_file` query is empty, the MCP layer asks `IndexQueries` for
up to three recovery candidates. The query considers only pinned-index paths with the same
basename, ranks them by longest matching path-segment suffix and then preserved prefix, and
returns a byte-budgeted `pathSuggestions` object with `paths`, exact `total`, and observable
`truncated` state. The original result remains a not-found error or empty list; suggestions are
never substituted, never read from the mutable filesystem, and never widen into Git history.

F# `.fs/.fsi/.fsx` content and `.fsproj` ownership/reference graphs are indexed. The syntax index
unions `.fs/.fsi` declaration rows deterministically across at most 64 indexed owning-project and
declared-target-framework parse contexts per file. It reserves one context per valid compile owner
while capacity remains, then fills in global ordinal order. Paired signature/implementation declarations remain
separate rows, linked multi-owner files are stored once, generic arity is retained where syntax
exposes it, and source or project-option refreshes replace affected rows transactionally. Search
filters, generated-file policy, namespace scoping, ownership/orphan disclosure, paging, and indexed
confidence use the same shared contract as C#. Per-file FCS parse coverage records total, processed,
truncated, and failed contexts plus a compatibility total for owners with omitted contexts,
partitioned into owners with no retained context and owners with some but not all retained.
Search scopes sum those owner/file incidences, so one physical project can contribute once for each
affected file; they do not claim a distinct-project census. Bounded `outline` and semantic recovery
lists retain their separate total/returned/truncated contract and do not reuse these stored counts;
affected search scopes report `fsharp_parse_failed` and/or
`fsharp_parse_contexts_truncated`, including partial-context recovery where successful declarations
remain searchable. `.fsx` remains text-only, so script-only scopes fail
closed and mixed scopes disclose the skipped script files.

The C# Roslyn cold-start loader supports the standard unconditional central-package shape without
invoking MSBuild or restore. For a versionless project `PackageReference`, it reads the nearest
applicable `Directory.Packages.props` from the same pinned index snapshot, requires
`ManagePackageVersionsCentrally=true`, and projects one unconditioned simple `PackageVersion` for
that identity. Its version may contain simple `$(Name)` references to local, unconditional root-level
property assignments. Properties are expanded at assignment time in document order, so bounded
chains, reassignment, and self-reassignment through the previous value are supported; the final
property value supplies the later item pass. When a `PackageVersion` consumes a property, evaluation
is bounded to 1,024 central property assignments, 1,024 project property names, 4,096 substitutions,
16 KiB per raw or expanded value, and 256 KiB of aggregate expanded text; literal versions do not pay
those property-table budgets. Undefined or forward-only references, cycles, property functions,
conditioned/non-root assignments, project-side reassignment of a consumed version property, explicit
project imports, applicable `Directory.Build.targets`, and exceeded limits retain the established
unresolved-reference behavior. The central path and indexed hash are part of the targeted
semantic model identity, so an indexed central-property or version change reloads an already-warm
project while unrelated config changes do not. Centrally selected packages resolve only from the
exact global-cache version directory;
missing versions never fall back to the newest installed version, while an existing analyzer-only
or target-incompatible package keeps the established unresolved-reference degradation. Imports, conditions, duplicate or
mutating `PackageVersion` items, `GlobalPackageReference`, invalid versions, missing authority, and
Windows path ambiguity are not projected and retain established unresolved-reference behavior. A
selected exact version whose cache directory is absent fails with the stable
`csharp_semantic_central_package_asset_unavailable` cause. Direct-version
`PackageReference` and legacy `packages.config` keep their established behavior. This is deliberately
the existing C# direct-package assembly model with central version authority, not the target-specific
transitive `project.assets.json` closure implemented for F# below.

The FCS semantic adapter consumes one immutable source/project snapshot captured from a pinned index epoch, copies
workspace `HintPath` assemblies and restored package compile assets through verified open handles into request-private snapshots, releases
SQLite before type checking, and bounds source count/bytes, references, concurrency, cache size,
deadline, diagnostics, contexts, and response bytes. The bounded evaluator deliberately accepts only literal
ordered compile items and a bounded evaluation-lite project subset: simple property
assignment/expansion before semantic items, comparisons and boolean/`Exists` conditions, `Choose`, and recursively loaded
literal workspace-local `.props` imports with count/depth/aggregate-byte limits and cycle detection.
Unique imported files, active import occurrences, condition depth, and evaluator nesting are bounded
separately. Only the conventional self-default property idiom may treat an unset property as empty;
other unresolved ambient/global condition inputs fail closed.
The same projection discovers the nearest indexed ancestor `Directory.Build.props` and
`Directory.Build.targets` independently, evaluates props before the project and targets afterward,
and applies bounded metadata-free reference input lists plus top-level `Reference Include`/`Remove`.
Local chained `.props`/`.targets` files are inspected without executing targets: unrelated target
logic is ignored, but a target or task that can mutate compile/reference/compiler facts remains a
hard boundary.
For central package management, the nearest indexed ancestor `Directory.Packages.props` is evaluated
after `Directory.Build.props` and before the project with the same bounded property/condition/import
machinery. Conditional simple-version `PackageVersion` Include/Update/Remove items may depend on the
selected TFM and supported earlier property authority; dependence on a property first defined later in
the project fails closed as unresolved rather than being guessed. Versionless
`PackageReference` items must resolve through that authority, direct Version metadata is rejected
under CPM, and disabled VersionOverride authority fails closed. Ambiguous Windows host-case matches
and unsupported central constructs, including `GlobalPackageReference`, remain explicit. Active `ExcludeAssets`, `IncludeAssets`, or
`Aliases` metadata fails closed because the bounded evaluator does not model package compile-asset
filtering or reference aliases.
An explicit `CODENAV_NET472_REFS` directory is likewise authoritative for net472 compiler metadata:
Phoenix does not fall through to installed targeting packs or package caches when that override is
missing or unusable, and availability remains false unless valid assemblies with the expected
`mscorlib`, `System`, and `System.Core` identities are present. C# semantic coverage reports the
exact selected `frameworkRefsSource` and counts only package DLLs successfully admitted as compiler
metadata in `resolvedPackageDllCount`.
Active package identities are matched against the selected target in the project's existing
`obj/project.assets.json`; Phoenix never restores or invokes MSBuild. The assets snapshot must name
the same physical project, selected framework, exact case-insensitive explicit direct package identity set, and evaluated version
constraints, select a package version satisfying each constraint, and must be newer than the
project plus every live evaluated package/build/import authority file whose content must still
match the pinned index snapshot. Well-formed package dependencies marked `autoReferenced: true`
by restore are validated against the selected target but excluded from the explicit identity-set
comparison; they do not become PackageReference closure roots.
Direct versions support one-to-four-part numeric versions with optional prerelease/build labels,
bounded NuGet ranges (including exact `[version]`), and numeric-prefix floating versions from `major.*`
through `major.minor.patch.*`; unsupported shapes fail closed. Central `PackageVersion` authority
remains restricted to simple versions.
Only target packages reachable from the evaluated direct PackageReference roots contribute
transitive `compile` assets. Each reachable package resolves its containing root once beneath
declared package folders that remain inside the workspace or match the explicit `NUGET_PACKAGES`
root exclusively when it is set; when it is absent, the ordinary user-profile global cache remains
supported. Resolution is bounded by the existing dependency/reference/byte budgets and request cancellation,
copied to immutable request-private files, and reverified
after FCS. Missing, stale, mismatched, changed, non-managed, or path-unsafe package inputs fail with
explicit `fsharp_semantic_package_*` causes. Selected-project references use that identical captured
context and post-check binary reverification boundary; sample text comes only from captured root
source strings and never from a live filesystem or index reread. Active F# `ProjectReference`
items are evaluated from the same pinned index snapshot and recursively captured dependency-first at
the exact selected TFM. Each FCS project receives in-memory referenced-project options plus a matching
virtual `-r:` output identity; SDK-style projects receive the flat transitive compiler reference set
unless `DisableTransitiveProjectReferences=true`, while legacy-style projects remain direct-only.
Phoenix neither emits nor reads a project DLL. Every physical node is checked once dependency-first;
child diagnostics retain their source provenance and use the existing
`fsharp_semantic_diagnostics_present` confidence downgrade. The closure memoizes
physical path + exact TFM and separately reports returned dependency declarations through
`declarationsFromProjectReferenceClosureCount`; `declarationsOutsideSelectedProjectCount` remains
the count of declarations omitted from the response. It fingerprints
children into parents, applies source, binary, import,
property, and item-list budgets across the aggregate request, and reverifies every captured binary
after the root check. Literal physical Include paths remain authoritative for legacy/`.Net` companion
pairs. Missing or unreadable projects, unsupported metadata, cycles, same-assembly conflicts,
non-F# targets, and unavailable exact TFMs fail closed with stable causes. Compatible
`netstandard` selection remains a separate bounded follow-up rather than an implicit guess.
Exact-path opened-handle verification is available on Windows, Linux, and macOS; the macOS
`proc_pidfdinfo` path added in v0.12.56 applies equally to workspace `HintPath` binaries and restored
package assets. A platform verification failure remains fail-closed and produces no semantic result.
Import paths are selected only from canonical paths in the pinned index using the host path policy;
ambiguous Windows case aliases fail closed, and semantic evaluation never walks the mutable live
filesystem to resolve casing.
Known compiler target imports are terminal boundaries. It never runs MSBuild, targets, or tasks and
rejects property functions, compiler-item transforms/metadata outside restored PackageReference
authority, imported compile items, and unsupported conditions
with stable causes. Standard `Microsoft.NET.Sdk` and recognized toolchain implicit authority are
partial, including unobservable build authority above the workspace root; custom/child/qualified SDK
declarations, Directory.Build mutations outside the bounded reference projection, and ordinary
project/import property assignment after semantic items fail closed. Workspace-contained managed `HintPath` snapshots have their original identity
verified after the check; declarations may come from the captured F# project-reference closure while
reference counts remain selected-root-only. The host's target-compatible
`FSharp.Core` fallback is always disclosed as partial because it was not selected by evaluated
project authority. C#-targeted F# project references, compatible-TFM selection, and the remaining
semantic operations still disclose stable unsupported boundaries. Generic indexed search
is language-neutral for C# and F# `.fs/.fsi`: an `.fsx`-only or other text-only scope is refused,
while a mixed scope reports `unsupported_language_files_skipped`. This keeps
cross-language graph holes visible without fabricating semantics for unsupported F# project shapes.
Exact-name type declarations receive a soft ordering preference over same-named members, but both
remain pageable results. A first-page empty result probes the same name/match semantics without
generated/path/namespace/kind filters: `existsUnfiltered` distinguishes genuine absence from
filter exclusion, `appliedFilters` echoes the active narrowing, and `unfilteredKinds` reports the
declaration kinds hidden by that narrowing.

Structural facts (`project_graph`, `projects_containing`, `dependency_path`,
`repo_overview`) come from the physical project-file and optional solution parse. Composites (`context_pack`, `impact`,
`related_tests`) synthesize the lower layers.

### Confidence model

Every response carries a `confidence`:

- `exact` — compiler-verified by a closed Roslyn project model, or by an F# selected context whose
  disclosed partial reasons preserve authority under the table below.
- `indexed` — trustworthy indexed/syntax evidence, including F# semantic checks whose selected
  context substituted, lost, or removed authority.
- `heuristic` — inferred from naming, base-list text, or project relationships
  (`implementations` fallback, `related_tests`) — leads, not facts.
- degradation flags: `partial` (a deadline/coverage limit was hit), `stale` (index older
  than the working tree), plus `coverage` counts.

F# semantic confidence is exact when the selected context carries only disclosed assumptions or
immutable-evidence provenance; it is indexed when anything was substituted, errored, or removed
from the context. `partial:true` and `partialReason` remain visible independently of that confidence:

| F# semantic partial reason | Confidence | Authority meaning |
| --- | --- | --- |
| `fsharp_semantic_sdk_implicit_authority` | exact | The selected standard SDK supplied disclosed implicit authority. |
| `fsharp_semantic_toolchain_implicit_authority` | exact | The selected recognized compiler toolchain supplied disclosed implicit authority. |
| `fsharp_core_reference_defaulted` | exact | The selected context used the expected `FSharp.Core` default without host fallback. |
| `fsharp_binary_references_snapshotted` | exact | Binary inputs were copied and verified as immutable request evidence. |
| `fsharp_package_references_snapshotted` | exact | Restored package inputs were copied and verified as immutable request evidence. |
| `fsharp_references_workspace_dependents_not_scanned` | exact | The selected-project result is compiler-exact; this separately discloses that its workspace total is a lower bound. |
| `fsharp_core_reference_host_fallback` | indexed | A host-selected `FSharp.Core` substituted for project authority. |
| `fsharp_semantic_diagnostics_present` | indexed | Compiler errors mean the selected context did not close cleanly. |

Successful responses use a closed partial-reason classifier: any unclassified partial reason is
`indexed` until deliberately admitted. Every error envelope is `indexed` regardless of its
partial reasons; this includes bounded `*_limit`, `*_unavailable`, and `*_unresolved` error causes.
Indexed-layer
`fsharp_parse_failed`, `fsharp_parse_contexts_truncated`, and
`fsharp_alternate_parse_contexts` coverage reasons are not copied into semantic results merely to
influence confidence.

The MCP registration boundary wraps every attributed navigation tool without changing its
generated required-field schema. Missing or mistyped arguments are rejected before SDK method
binding as structured `bad_request` tool results naming the tool and field. This avoids the SDK's
generic non-`McpException` failure while keeping required parameters required.
Because this rejection occurs before the navigation-tool instance and workspace health exist, its
minimal documented envelope intentionally omits the ordinary `meta` object.

Transient `implementations` fallback is similarly actionable without changing resource policy:
`cluster_cold_load` and `semantic_timeout` remain machine-readable as `partialReason`, or as
`semanticReason` when a member fallback needs its established policy-specific `partialReason`.
Heuristic confidence is preserved and `retryRecommended`/`retryHint` are added. No automatic
second call or deadline increase is hidden behind those fields.

## The index substrate

**Storage** is SQLite with FTS5 (`IndexStore`). Schema: `files` (path, hash, generated/test
flags, freshness), `file_contents` + an external-content `fts_content` virtual table,
`projects` / `project_refs` / `package_refs` / `compile_items`, `solutions` /
`solution_projects`, `symbols` (kind, name facets, spans, parent links), `type_base_edges`
(syntax-derived direct base name/arity keyed to the derived declaration and deletion file), and `meta`
(index version, timestamps, coverage). The MCP deployment exposes one daemon writer. The Core
library also retains a Windows-only read-only WAL attachment for direct compatibility tests; those
readers use committed snapshots, never open a writer connection, and are not an MCP process mode.
Explicitly live source/Git and compiler-backed semantic evidence may use newer workspace bytes.

**Build** (`IndexBuilder`): scan the tree (excluding `.git`, `bin`, `obj`, `packages`,
`node_modules`, `.vs`, generated files, and symlink/junction targets); parse every `.csproj` and
`.fsproj` directly, independent of solution membership; index `.cs`, `.fs`, `.fsi`, `.fsx`, `.md`,
and `.sql` text, while parsing only `.cs` with Roslyn syntax during indexing. Parsed C# rows cross a
bounded synchronous queue to the single writer, so backpressure cannot strand every parser behind a
ThreadPool-scheduled async continuation. Since v0.12.69, startup and explicit full-build orchestration
plus the cold C# producer run on dedicated long-running execution lanes. Parser concurrency and queue
capacity are unchanged, while lightweight MCP dispatch remains independent of synchronous build
coordination. Since v0.12.39, cold builds schedule C# files by descending
scanned byte size with an ordinal-path tie-breaker, overlapping giant Roslyn parses with ordinary
parse-and-persist work instead of leaving them as final stragglers. Since v0.12.24, that writer
caches raw SQLite statements for every exact symbol-batch size from 1 through 32 and binds by
ordinal, avoiding provider-level
parameter-name lookup and per-execution parameter allocation without changing row or ID semantics;
the same insertion path is used by delta refresh. Since v0.12.35, file ids are also assigned by the
single writer and persisted through cached raw ordinal statements; full C# builds batch exact
groups of up to 32 files, while delta and structural writes use the same one-row statement. Since
v0.12.37, each cold C# wave persists its associated `file_contents` through cached raw ordinal
statements at the same exact width. Since v0.12.38, cold builds defer FTS5 population until every
`file_contents` row is present, then issue one external-content rebuild during the unpublished
finalization transaction; live delta writes still update content and FTS together. Cold builds
create tables, primary keys, and uniqueness constraints first, bulk-load every row, then complete
FTS and construct all nine query-facing secondary indexes before writing the compatible schema
marker. `IndexStore` rejects that marker while either deferred structure remains pending, making
the compatibility boundary an enforced store invariant rather than straight-line builder ordering.
The writer split reports schema, secondary-index groups, project/graph/compile-item SQL,
file/content/FTS statement counts, commits, analysis, and checkpoint costs independently. Since
v0.12.36, supported
Windows/Linux workspace-local full rebuilds perform
that complete load in a pinned private database using MEMORY/OFF journaling while the previous live
WAL database remains queryable. Only final publication closes local reads, marks the destination
`rebuilding`, drains every admitted writer query, pinned review snapshot, and old compatibility-reader handle
under one three-minute deadline shared by the local drain and remaining install retries, and
atomically installs the stage. The anchored stage must match the writer's retained destination
identity before its build and again before installation, so a Linux lexical directory replacement
cannot split publication from the manager's read authority; a fresh no-follow destination open is
also compared with the installed stage identity after installation before reads reopen. Linux maps
`O_DIRECTORY` and `O_NOFOLLOW` through the running architecture ABI, preserving the same anchored
authority on x64 and ARM64. Linux scans
through the retained workspace
handle. Windows scans the lexical path, then relies on the same pre/post-build and post-install
identity checks to refuse publication if that path changes. Both store the original lexical root
only as publication metadata and revalidate it against the workspace ownership lease before
publication. Replacing the whole root therefore fails closed instead of serving the replacement
tree. A
platform/path layout that requires anchoring also fails closed when the anchor cannot be opened;
the destructive compatibility path is reserved for macOS or an index outside the workspace and
first revalidates the acquisition-time workspace identity. A pre-install failure or local-drain
timeout cleans the stage, refreshes cached metadata, schedules a convergence sweep, and returns the
prior publication to `ready` only while workspace authority still matches. Compatible startup
force-rebuilds expose that prior publication through the new writer while the private stage builds;
responses preserve `building` even when that prior publication also carries a freshness-convergence
warning. After a crash, the next elected writer first holds the physical-workspace lease and
destination claim and validates any existing publication's stored workspace ownership. Only then
does it enumerate the retained destination authority and remove exact GUID-named Phoenix stage,
publish-link, and SQLite sidecar artifacts whose complete link set is accounted for. Candidate
discovery is capped at 256 matching names and five seconds before any handles are opened; a refusal
names the exceeded bound and observed candidate count separately from an unsafe-link-set refusal. A
live claimed stage denies successor ownership, foreign publications are retained, and unrelated
names are retained.
F# outline response trees are parsed on demand; the normalized declaration rows used by
`search_symbol` are stored in the shared syntax index.
Solution files are
optional editor inventory: they never select projects or provide build, dependency, ownership,
or symbol-resolution authority. A cold build of a
multi-thousand-project workspace completes in minutes at most; live progress counters
(phase, files, throughput) report the real numbers for any given machine.

**Compile-item ownership**: legacy projects list `<Compile Include>` explicitly (exact,
including linked files). C# SDK projects use longest-dir-prefix approximation for implicit `.cs`;
F# stays ordered/explicit unless the project literally enables default items, whose SDK glob owns
only `.fs` (not unlisted `.fsi` signatures or `.fsx` scripts).

### Project and symbol-resolution authority

Phoenix keeps two project-identity layers, and they are keyed differently on purpose.

The index is keyed by physical project file. Each discovered `.csproj` or `.fsproj` is one row
with its own compile items, language, style (`legacy` or `sdk`), and parsed references. Project
selectors first resolve an exact workspace-relative project-file path. For a bare selector, they
next use either the exact suffixed project filename (when the selector ends in `.csproj` or
`.fsproj`) or the extensionless stem, then `AssemblyName`. Lower-precedence matches are kept in
`shadowedMatches`, and several physical matches return `project_ambiguous` instead of a first-match
guess. A side-by-side legacy `project.csproj` and SDK-style `project.Net.csproj` are therefore two
index rows.

C# semantic compilation and the reference graph are keyed by assembly name. This is deliberate.
A project's assembly name is its literal `<AssemblyName>` property when present, otherwise its
project file name without extension; a `<Reference>` is matched by its `Include` simple name only,
the `HintPath` directory serves solely to classify a binary as external when it points into a
never-indexed location, the `HintPath` file name is never used for matching, and output paths are
never consulted.
`<Reference>` items with a `HintPath` — the multi-staged monorepo idiom, where an early phase builds
assemblies into a common folder and later phases reference the assembly rather than the project —
carry an assembly identity and no referenced project file, and physical-project-keyed alternatives
that were tried failed to bind those consumers to their source project, which truncated
`references` and `impact` answers. Graph edges therefore bind a referenced simple name to the
in-workspace project with that assembly name: a same-language name collision binds to one physical
row deterministically (the first), mixed C#/F# collisions keep one target per language, and a
`HintPath` into a never-indexed directory is external and produces no edge. Solution membership is
editor inventory only, and FTS is never project-identity authority.

At this layer the legacy `project.csproj` / SDK `project.Net.csproj` companion pair is one project,
never two competing ones: exactly two C# rows with one assembly name, one legacy-style and one
SDK-style, where the SDK file is the exact `.Net` companion of the legacy file, are recognized as
the pair and are not a collision. Any other set of same-name physical rows is a name-keyed
collision, and the index rows are never merged. Documentation-comment-ID resolution discloses a
non-pair collision in its owner set through `nameKeyedOwnerCollisionGroups` and the
`documentation_id_name_keyed_owner_collision` note. Other semantic selectors and graph/composite
tools use the same assembly-name-keyed model but do not currently emit that collision coverage;
project selectors continue to expose the physical rows separately. The current architecture does
not create duplicate Roslyn projects per target framework, select projects by output directory, or
merge physical index rows solely because they share an assembly name; any such change requires a
separately justified design backed by a concrete reproducer.

`review_pack` preserves changed `.sln`, `.slnx`, and `.slnf` paths in
`changedProjectFiles` and emits `review.solution_files_changed`, while treating those files as
non-authoritative. Only changed project or build inputs can invalidate exact ownership, move,
or declaration evidence.

Explicit `review_pack` and targeted `refresh_index` path lists share one bounded input grammar:
one to 256 exact workspace-relative paths within a 64 KiB input string, as comma-separated text or
a serialized JSON string array. The JSON form preserves commas inside path strings. Blank, rooted,
traversing, control-character, malformed, and over-limit inputs fail with `bad_request` before
lookup or queueing. A null `refresh_index.paths` alone requests the full sweep; a non-null blank
value is invalid rather than silently widening a targeted request.

After its last workspace-dependent aggregation read, `review_pack` revalidates every bounded live
file digest and safe existence classification it consumed. Contradictory repeated observations
latch the call unstable even when the path later returns to its first state. Git-diff mode then
recaptures the exact patch, typed dirt, link/repository classification, move evidence, and only the
bounded untracked move-candidate bytes it actually hashed. Unreadable, non-regular, oversized, or
cap-excluded candidates stay conservatively uncorrelated rather than hard-failing a stable review.
A mismatch returns `git_worktree_changed` without a partial digest, so one response never combines
evidence from different worktree epochs; explicit-path mode applies the same live-evidence
revalidation without requiring Git.

`review_pack.movedFiles[].match` distinguishes evidence strength. `exact_blob` means the staged or
unstaged C# relocation is byte-identical. A unique untracked worktree candidate whose CRLF bytes
normalize to the stored LF blob is reported as `normalized_blob`; the reverse direction remains
uncorrelated. Normalized evidence is never promoted to byte-exact evidence, exact matches reserve
their targets first, each target is claimed at most once, and ambiguous candidates remain
uncorrelated.

For symbol search, FTS generates and ranks candidates; syntax or compiler evidence decides
identity. `implementations` and `type_hierarchy` select generic declarations by the stored
syntax arity (explicit `arity`, or a `search_symbol` `symbolId`). A bare exact name spanning
multiple arities is refused rather than merging generic and non-generic symbols. FTS text
matches remain candidate evidence only and cannot accept, reject, or merge symbol identities.

Direct C# base-list entries are normalized at syntax-index time into `type_base_edges` using
the right-most simple name and generic arity. Extraction happens before the 400-character
display-signature cap, so long declarations cannot disappear from implementation discovery.
Qualification is deliberately not stored as identity: same-name namespace collisions remain
candidate over-inclusion and are pruned by the compiler-backed verification step. Exact closure
lookups use the table's `(base_name, base_arity, derived_symbol_id)` primary key; incremental
refresh deletes edges by `file_id` in the same transaction as the replaced symbol rows.

## The semantic layer — MSBuild-free, lazy, snapshot-pinned

This is the part designed specifically for net472 enterprise scale.

- **No MSBuild.** `SemanticWorkspace` builds a Roslyn `AdhocWorkspace` by hand from parsed
  csproj facts: documents from live files, framework reference assemblies (located via a
  targeting pack, the NuGet reference-assembly package, or the installed .NET Framework —
  see `ReferenceAssemblyLocator`), hint-path/NuGet package dlls, and in-cluster project
  references. This avoids `MSBuildWorkspace.OpenSolutionAsync`, which does not scale to a
  few-thousand-project solution.
- **Lazy, FTS-scoped clusters.** A reference query loads the declaring project's dependency
  closure plus every matching FTS-candidate dependent project by default (`maxProjects: 0`).
  A positive value opts into a bound; bounded responses report the total skipped count and a
  size-limited sample. Phoenix has no hidden candidate-project ceiling.
- **One snapshot per operation.** Each op resolves the symbol against, *and* runs
  `SymbolFinder` against, a single pinned `Solution` — so a background reload/eviction can't
  orphan the symbol mid-query (which previously produced empty "exact" results).
- **Dependency-first compilation preparation for references.** Before a `references` search,
  the operation prepares the owner and its graph dependents among the selected scan projects,
  plus their actual Roslyn dependencies, in topological waves. Ready siblings share the loader's
  bounded process-wide project lanes. The exact leased `Solution` is then passed to `SymbolFinder`,
  so its `CompilationTracker` reuses the work; no cross-snapshot compilation cache is introduced.
  Privacy-safe work attribution records the summed slot-held compilation wall, slowest project,
  current wave-barrier floor, and measured weighted dependency critical path. The difference
  between the wave floor and the lane-aware ready-queue floor — the greater of the dependency
  critical path and summed work divided by the lane limit — decides whether a completion-driven
  queue has material headroom; the scheduler itself remains unchanged until field evidence
  establishes that gap.
- **Exact document narrowing for references.** After preparation, eligible name-addressable
  symbols use a conservative candidate-document superset derived from the cached text of that
  same leased `Solution`. This is intentionally not committed FTS: compatibility readers cannot see a
  writer's pending queue and live bytes can move after an index snapshot is pinned. FTS chooses the
  candidate projects; case-exact, identifier-bounded live-text matches choose documents inside the
  leased solution. Global using aliases widen their entire project; documents with C# escapes, numeric
  XML entities, or Unicode format scalars are retained because Roslyn transforms them in token
  `ValueText`; and constructors, instantiable types, operators, accessors, indexers,
  compiler-pattern members, unsupported kinds, or any planning uncertainty silently use
  full-solution `SymbolFinder`. Confidence and candidate project coverage are unchanged; only
  documents inside the already selected solution are narrowed. Since v0.12.21, large
  `SourceText` instances are scanned through pooled bounded windows rather than virtual per-character
  indexing, with exact state carried across windows for escapes, numeric entities, and Unicode
  format scalars. Candidate syntax roots are materialized for alias widening only when raw text
  contains the unordered case-sensitive `global`, `using`, and `=` token spellings; comments and
  strings may conservatively over-admit, while the syntax tree remains the final authority.
- **Persistent Roslyn syntax indexes.** The semantic `AdhocWorkspace` uses a stable synthetic
  solution path and deterministic solution/project/document ids solely as Roslyn cache identity.
  Roslyn can therefore reload checksum-validated `SyntaxTreeIndex` records from its local
  application-data SQLite store after an MCP process restart instead of rebuilding them across
  every project touched by named-type global-alias discovery. The synthetic path is never read,
  solution files remain non-authoritative, and changed bytes or parse options invalidate through
  Roslyn's existing checksums.
- **Pinned long scans.** Candidate enumeration and semantic cluster loading pin one local SQLite
  read epoch. A writer drains its own pinned snapshots before rebuilding. A database-destination
  claim stops new Windows compatibility-reader opens before replacement while already-open bounded operations
  retain their consistent old SQLite handle; no sidecar reader registration is required.
- **Reload keeps identity and retention is byte-governed.** A changed project reloads under its
  *existing* `ProjectId`, and eviction only removes projects nothing loaded references — so
  dependents' references never dangle. Since v0.12.22, the legacy ~160-project proxy is gone:
  process-wide accounted semantic inputs trigger a strict safe-project LRU at 2 GiB and drain
  toward 1.5 GiB, while 2.6/3 GiB managed-heap signals request progressively stronger drains.
  Triggering is process-wide, but each workspace evicts only its own residents; requested,
  referenced, and concurrently active projects are never candidates. Multi-phase semantic
  operations defer the owner load's pressure pass until the scan load has protected its complete
  project set, preserving one Roslyn `Solution` and its document-scope cache across the operation;
  a terminal `finally` completes the deferred pass if resolution, planning, cancellation, or scan
  loading ends early. Each pass iteratively peels dependency layers that become safe after their
  final dependent is selected, rather than waiting for later operations to rediscover them.
  The thresholds are pressure signals with hysteresis, not candidate-project ceilings: if every
  resident is protected, Phoenix stays over target rather than silently reducing exact coverage.
  The runtime-corpus measurement behind these thresholds retained about 636 MiB of accounted input
  at about 903 MiB managed heap (`k ~= 1.42`); the 2 GiB input trigger therefore projects to roughly
  2.8 GiB heap, between the 2.6 GiB soft and 3 GiB hard signals. Soft pressure targets the smaller
  of 1.5 GiB or 75% of current retained input; hard pressure uses the smaller of 1.5 GiB or 50%.
  Any actual eviction creates one new Roslyn `Solution`, so the next operation performs one new
  document-scope scan; no-pressure steady state preserves both solution identity and the cache.

### Semantic cold-start loader: parallel prepare, ordered commit

Before 0.12.9, the C# semantic cold path held one `SemaphoreSlim(1, 1)` for the complete
`EnsureLoadedAsync` operation and loaded projects sequentially in dependency-first order. Source
open/read/decode within one project fanned out to at most eight workers, but the next project could
not begin until the current project had been parsed, captured, wired, and added to the
`AdhocWorkspace`. A concurrent semantic request waited behind that whole batch. This was safe, but
it left independent projects idle on wide dependency graphs and made the gate a cold-start
throughput governor rather than a narrow workspace-mutation lock.

Version 0.12.9 replaces that path with a two-phase loader:

1. **Prepare immutable project inputs with bounded parallelism.** From one planned project set,
   capture each project file and `packages.config`, parse the supported project facts, read and
   decode its source documents, collect framework/package/HintPath metadata candidates, and
   produce a `PreparedSemanticProject`. Preparation may create or reuse immutable metadata
   reference objects, but it does not finalize the project's metadata-reference list.
   `PreparedSemanticProject` retains only caller-independent compiler inputs plus binary
   candidates. Durable named project-edge intent is retained with the resident project;
   operation-specific `ensureReferenceTo` intent remains only on each caller's plan. Commit
   reconstructs references from those two sources instead of inheriting the previous solution's
   edges, so concurrent searches can share capture without leaking one caller's wiring into the
   next. Position-based definition resolution supplies its own operation edge only when the index
   proves one bounded, unambiguous C# declaration project; it does not depend on a prior scan's
   transient edge. Preparation must not read or mutate
   `AdhocWorkspace`, `_loaded`, the LRU counters, or SQLite. The coordinator prefetches project
   rows, file lists, and graph facts from one pinned index snapshot into immutable inputs before
   worker fan-out; after the live-read workers join, it resolves any disk-miss text fallbacks
   through that same snapshot. New and reused `ProjectId` values are assigned before preparation,
   so prepared references name stable ids rather than depending on completion order.
2. **Commit prepared projects deterministically.** Acquire the workspace-mutation gate only after
   preparation completes. Revalidate the planned index epoch/fingerprints, then add the successful
   prepared projects and wire their references in the original topological order. Every resident
   reverse consumer is rewired in place when its physical dependency is reloaded, recovers from an
   earlier preparation failure, or fails after previously loading, even when that consumer was not
   requested by the current operation. Requested warm consumers are also rebuilt whenever their
   desired operation-specific edge set changes: newly requested edges are added and edges retained
   from a prior caller are removed, including strict-subset and empty successor calls. Only
   after the successfully committed dependencies and cycle-safe wired edges are known does commit
   finalize metadata references: a valid HintPath/assembly candidate is suppressed only when its
   source project edge was actually wired, and is restored if that source edge later cannot be
   wired. If preparation failed for that dependency or the cycle guard rejected the edge, the valid
   binary candidate remains available; preparation must never leave the consumer with neither
   binding merely because it anticipated source substitution. Prefer
   building one immutable `Solution` value and applying it once; publish `_loaded`, fingerprints,
   LRU state, and coverage only after the Roslyn change succeeds. Reload keeps its existing
   `ProjectId`, the cycle guard remains in force, and eviction still cannot remove a project
   referenced by another resident project.

```text
Before:   gate -> load A -> load B -> load C -> publish -> release

Now:      plan -> prepare A --\
             prepare B ----+-> gate -> commit A -> B -> C -> publish -> release
             prepare C --/
```

Planning briefly acquires the mutation gate to snapshot the resident solution generation,
existing/reusable project ids, and LRU state, then releases it before preparation. Project rows,
file lists, graph facts, and fallback text come from one pinned index snapshot. The coordinator
uses batched row/fingerprint/file/edge/authority queries and reserves a conservative aggregate
descriptor charge before materializing those lists, including every ancestor
`Directory.Build.props`/`.targets` candidate path. Immediately before commit it checks the
fingerprint and targeted model identity for every requested project, including warm residents that
did not need preparation. That identity covers the schema/index format plus the paths and hashes of
the nearest applicable Directory.Build authority; an unrelated project refresh does not invalidate
otherwise valid preparation. Commit proceeds only if those facts and the workspace generation
still match. A concurrent relevant commit or index refresh therefore causes bounded re-planning
rather than installing preparation derived from stale workspace state. Independent callers share
an in-flight preparation keyed by project identity, fingerprint, and targeted model identity. One
caller's cancellation does
not cancel work still awaited by another; cancellation of the final waiter retires and cancels the
unpublished preparation.

`EnsureLoadedAsync` returns an operation-scoped `SemanticSolutionLease`, not an unowned `Solution`
snapshot. The lease references every project-owned source/metadata reservation reachable from the
immutable solution and the caller holds it through symbol resolution and the complete
`SymbolFinder`/compilation operation. Reload or LRU eviction drops only the workspace's resident
owner; an old project version and its retired metadata references remain charged while any
preparation, resident project, or active semantic operation can still reach them. Cancellation,
failure, and normal completion dispose the operation lease in `finally`, and only the final owner
releases the underlying reservations. This preserves the same-snapshot invariant even when another
request commits, reloads, or evicts projects concurrently.

Metadata-reference reuse becomes an explicit process-wide single-flight cache rather than the
current gate-confined `Dictionary`. The key is the canonical path plus the observed modification
time and size. Concurrent preparations of the same DLL await one creation and receive the same
immutable `PortableExecutableReference` identity. The cache returns a reference-counted lease,
not an unowned raw entry: replacing a changed stamp retires the old version, but its byte
reservation remains charged until the last prepared, resident, or active-operation lease releases
it. A failed or cancelled creation does not permanently poison the key, and cancellation by one
waiter does not cancel a creation that still has other waiters. Candidate creation may happen
before commit, but the source-over-binary filtering above remains a commit decision. Tests must
cover simultaneous same-DLL preparation, identity sharing, stamp invalidation while the old
reference remains in a resident or actively searched solution, and failure/cancellation followed
by retry.

Preparation uses one process-wide runtime, not one scheduler per request or batch. Bounded
descriptor, project, source-read, and large-file lanes cap concurrent work. Project files and
file/reference sizes produce a conservative whole-project byte estimate for observability and
resource ownership; that estimate is not a completeness gate. Once the semantic planner selects a
candidate project, aggregate byte accounting cannot omit it or turn an otherwise usable solution
into partial coverage. Live file sizes are folded into the estimate immediately before capture,
and the bounded reader still refuses an individual file that grows past its type-specific maximum.
After shared metadata and actual capture sizes are known, pessimistic accounting is reduced to the
retained inputs.

Project and `packages.config` parsing uses a separate descriptor charge based on the pinned and
live structural-file sizes. It is recorded before either file is captured and released before the
whole-project retained-input charge replaces it. Structural-file and source-file reads remain
individually bounded, so malformed growth is rejected at the input boundary rather than by
discarding a candidate because unrelated projects already consume an aggregate allowance.

The same accounting covers owned input bytes retained by in-flight work, prepared results,
metadata leases, resident semantic projects, and active solution generations. Since v0.12.22,
process-wide accounted-input and managed-heap pressure signals drive a strict safe-project LRU;
there is no production resident-count boundary. The managed-heap signals cover Roslyn's internal
allocations, which cannot be measured exactly by input size. Charges remain held while
prepared results wait to commit or re-plan. Shared preparations and metadata leases are charged
once, not once per waiter or project. On commit, source/reference reservations transfer to the
resident project; reload, cancellation, failed preparation, stale-plan discard, safe LRU eviction,
operation-lease disposal, retired-cache lease release, and workspace disposal release their
respective ownership when the final owner is gone.

No worker waits for aggregate byte capacity while holding the workspace-mutation gate. Selected
projects proceed through the process-wide bounded preparation and source-read lanes, whose caps are
not multiplied by the number of requests or projects. Large files use one sequential process lane,
so parallelism cannot multiply the per-file maximum into an unbounded transient working set. The
retention pass never evicts a requested, concurrently active, dependency-protected, or otherwise
unsafe candidate merely to meet a target; when no safe candidate exists it publishes
`no_safe_candidates` and remains above target. Cancellation stops work that has no remaining waiter
and is observed by every worker and pinned-index fallback query. A two-phase operation's terminal
path still completes any pressure pass deferred by its owner load.
Indexed fallback resolution remains part of the preparation phase for timing and cancellation;
it cannot continue invisibly past the caller's deadline. No prepared result becomes visible after
cancellation, a fingerprint or workspace-generation mismatch, or a failed Roslyn apply.

The process-global net472 reference-assembly set remains a fixed bootstrap cache owned by
`ReferenceAssemblyLocator`; its constant baseline is outside incremental semantic-input
accounting. HintPath and package candidates, source inputs, prepared results, residents, and active
solution snapshots are accounted and leased by the cold-start runtime.

Failure and honesty policy does not weaken. Unsupported-language projects remain explicit skips;
project capture or preparation failures remain in `FailedProjects`; unloaded references remain
disclosed navigation-grade holes. A successful subset may be committed only when it produces the
same coverage semantics as today's sequential loader. The owning project cannot earn
compiler-backed evidence if its own preparation or required closure fails. Live source capture,
project-resolution authority, exact-first path handling, reload identity, snapshot pinning, and
fail-closed project-model boundaries remain unchanged.

Aggregate semantic input accounting has no failure cause and cannot populate `FailedProjects`.
Real project capture, parse, metadata, model, or Roslyn-apply failures continue through the generic
`project_load_failed` coverage path, with bounded per-project cause samples where a stable concrete
cause exists. This keeps memory estimates observable without allowing an internal estimate to
change the compiler-visible candidate set.

The load telemetry must separate `planMs`, preparation queue/wall time, prepared project count,
effective project concurrency, process-wide accounted/retained-byte high-water marks,
workspace-gate wait, commit/apply time, and committed/failed counts. The existing cold/warm
attribution and aggregate
`projectLoadMs` remain available during migration. This makes a wide-graph preparation bottleneck
distinguishable from Roslyn commit cost or contention with another semantic request.

Since v0.12.23, references telemetry adds two process-wide CPU brackets without changing the
scheduler: one exactly around compilation preparation and one around the complete cluster-load
interval. Both include GC/runtime and concurrent MCP work, so wall time remains the duration
authority and production comparisons use an idle, sweep-settled, single-operation capture.
Cluster-load CPU includes planning through scan-set resolution and stops before compilation
preparation begins, so the brackets are disjoint; an unavailable process counter is omitted
rather than represented as a clean-looking zero.
The `PhoenixCodeNav-Semantic` EventSource brackets owner load, scan load, compilation preparation,
document scoping, and Roslyn finding; its phase markers and the matching `semanticOp` record carry
the same privacy-safe correlation id for EventPipe attribution. Raw processor count and the
preparation lane limit are published together so capacity assumptions stay visible. Marker
scopes are decided at phase start: a trace attached mid-phase does not receive that phase's pair.

The executable regression matrix covers deterministic order and cancellable indexed-text fallback,
reload/`ProjectId` and cycle behavior, distinct concurrent caller wiring (including reciprocal
absence) on one shared preparation, atomic planned-id cleanup after concurrent terminal failures,
   operation-local definition binding through a transitive project-reference shape, sequential
   operation-edge narrowing from a superset to a subset and then to empty without recapture, warm-resident
refresh immediately before another project's commit, unrelated-refresh stability, bounded planning
descriptors including deep Directory.Build ancestor candidates, failed/dependency-recovered/reload
source-over-binary transitions when only the dependency is requested, and cycle-rejected fallback,
simultaneous metadata-cache identity/invalidation/lease-lifetime tests, cancellation before
commit, stale-plan rejection, partial-coverage parity, aggregate-accounting candidate completeness,
eight-project `type_hierarchy`/`implementations` parity on one index epoch, individually bounded
oversize-input rejection, active-search snapshot survival across eviction/reload, concurrent
disjoint-cluster scheduling and reservation-leak tests, and cancellation telemetry after partial
preparation completion. `CodeNav.Bench --semantic` remains the
deployment benchmark;
rollout comparisons should include both wide independent layers and deep dependency chains. Wide
graphs should gain from concurrent preparation; deep chains may still require ordered commit but
can overlap their independent capture work. Warm operations must not regress.

For a C# declaration already resolved by Roslyn, an unsupported-language dependency remains
visible in coverage but does not by itself downgrade the declaration from exact to indexed: an F#
project cannot contain another C# declaration for that symbol. Failed C# loads, incomplete C#
coverage, and unproven project-model authority still downgrade the result.

This implementation changes only C# semantic cluster materialization. Initial indexing already uses
parallel file capture/parsing with a single SQLite writer and is a separate pipeline. Since v0.12.20,
C# declaration extraction traverses nested namespaces and types with an explicit depth-first work
stack rather than recursive calls, so machine-generated nesting remains fully indexed on bounded-stack
parallel workers without changing symbol order or parent links. Since v0.12.46 / schema v21, that
syntax index also persists implicit and explicit C# conversions as `operator` rows with
target-bearing display names, canonical target/parameter declaration identity, modifiers, source
order, and parent links. From schema v22 through v23, a persisted context key was the full SHA-256 digest of
the parent context digest plus each local declaration key, so a rebuilt `idx:` row cannot validate
against an identical-looking declaration from another namespace or containing type. The fixed-size
digest retains complete chained identity without quadratic deep-nesting storage. Since schema v23,
explicit-interface regular operator rows persist private accessibility, matching
Roslyn and preventing syntax/search/review evidence from overstating public API. Schema v24 removes
the per-symbol context digest: v3 handle fingerprints instead bind the existing per-file content
hash to the declaration's deterministic syntax ordinal among declarations on its source line. That
ordinal is projected with the symbol row from existing indexed order, distinguishing same-file twins
without follow-up queries and conservatively invalidating every handle when that file changes without
adding hashing, allocation, or storage to the cold symbol-index path. F# semantic resolution
captures one selected physical root plus its exact-TFM F# `ProjectReference` closure under the
evaluated MSBuild transitivity policy behind
one single-slot gate. The pinned SQLite snapshot is released before dependency-first FCS checking;
selected-root reference enumeration shares the closure but never counts dependency-project uses.
Parallel FCS requests, C# dependencies, and compatible-TFM selection remain outside this design.

Cold-cluster latency and working set still scale with the selected project budget. Use
`CodeNav.Bench --db <scratch.db> --rebuild --build-only` as the non-destructive cold-index
regression gate against the target repository, and the ordinary query/`--semantic` modes for
deployment sizing. Warm clusters avoid
reloading unchanged projects.

## Freshness — and how git operations are handled

The index is kept live without rebuilding on every keystroke:

- **`IndexManager`** owns the lifecycle. The shared daemon acquires the index writer lease, opens or
  builds in the background (never blocking the MCP handshake), and runs the serialized refresh
  pump. Ordinary MCP relays never open SQLite or instantiate a competing `IndexManager`.
- **`WorkspaceWatcher`** (a `FileSystemWatcher`) debounces working-tree changes (600 ms
  quiet window) into batches. `DeltaRefresher` applies them: re-hash changed C#, F#, Markdown,
  and SQL files, update FTS, re-parse C# symbols, mark deletes, and rebuild compile ownership plus the
  authoritative project graph when a `.csproj` or `.fsproj` changes. Solution changes can update
  non-authoritative editor inventory only.
  Directory-level changes (folder rename/move/delete) escalate to a full detect-all sweep, since
  the OS emits no per-child events for them.
- **Startup sweep.** When an existing index is reopened by the daemon, a detect-all sweep
  reconciles edits made while the server was down.
- Every response reports `indexStatus`, `indexVersion`, and `meta.indexMode`. The two fields answer
  different questions: `index.mode` is the database ownership role (`writer` or `unavailable` for
  served MCP sessions), while `meta.indexMode` is the public runtime topology (`daemon`,
  `standalone`, or `unavailable`). Ordinary launches always report `daemon` once connected. An
  explicit diagnostics-only standalone process reports `standalone` only if it owns the writer
  lease; otherwise it serves a typed unavailable shim. The Core library retains a Windows
  read-only WAL attachment mechanism for direct lifecycle compatibility tests, but `McpApplication`
  never exposes that attachment as a server topology or automatic fallback.
- There is no cross-process reader registry or writer-intent turnstile. The writer still drains its
  own in-process ordinary queries and pinned review snapshots at the final publication boundary.
  The sole writer also holds
  `<index-db>.phoenix-owner`, a fixed-size claim containing its physical workspace identity and a
  `ready`/`rebuilding` state. Internal Windows compatibility readers validate the claim before and after each SQLite
  open. Once the writer publishes `rebuilding`, new opens fail honestly while existing bounded
  handles retain their consistent old database; the already-complete private replacement waits
  only for those handles to drain before atomic install. One three-minute publication budget covers
  the writer's local reader drain plus the remaining OS-handle install retries; a local timeout
  restores the prior publication before its store is released. That budget is above Phoenix's
  longest semantic operation deadline
  (120 seconds). The claim is not a reader registry: compatibility readers never write it or
  register slots. This mechanism is not an advertised MCP launch mode.

### Filesystem notification and refresh serialization

The writer detects workspace edits through a recursive `.NET FileSystemWatcher`. It observes file
and directory names, last-write changes, and size changes; create/change/delete events record one
canonical workspace-relative path, while rename records both the old and new path. A concurrent
set deduplicates paths during a 600 ms quiet window. A directory-level operation, incomplete
directory classification, or watcher-buffer error supersedes the path batch with a detect-all
sweep because the operating system may not emit one event per affected child.

After debounce, the watcher removes the collected paths from its pending set and publishes one
`RefreshRequest` to the `IndexManager` channel. Git reconciliation, explicit refresh requests, and
the post-startup sweep use the same channel. Exactly one refresh pump consumes it, so all SQLite
mutations are serialized and each delta is applied in one transaction. A detect-all sweep still
uses `DeltaRefresher`; it is not a destructive rebuild. Full rebuild is reserved for a missing,
incompatible, corrupt, or explicitly rebuilt index.

Before that pump mutates rows it commits a reader-visible `refresh_sweep_pending` marker. A new
database writes the same marker before its schema-version compatibility barrier, and startup keeps
it through watcher attachment. The marker is cleared only after the serialized post-build/startup
or ordinary refresh transaction succeeds. Thus a writer and any internal Windows compatibility readers report a
queryable-but-stale epoch while convergence is pending; neither can publish `ready` in the gap
between build capture and watcher-backed reconciliation. The manager tracks durable marker
publication separately from its in-memory state: if the marker transaction fails, it refuses the
refresh and every later request retries marker persistence before reading or mutating source rows.
Response metadata advises callers to retry `refresh_index` if that pending state persists; the same
marker also covers ordinary refresh convergence, not only the build/open handoff.

The index writer lease is one named mutex per physical workspace/worktree directory, independent of
the replaceable database file or its configured spelling. Only the process holding that mutex owns
the watcher, queue, build, and refresh pump. Its adjacent destination claim prevents different
workspace identities from sharing one `--index-db`. The Core library can attach an internal
Windows compatibility reader to committed WAL state when the claim proves that its configured
database is the writer's destination; that reader never watches, enqueues, refreshes, rebuilds, or
appears in the normal MCP process model. macOS and Linux Core contenders remain unavailable.
When a database moves with its workspace and its stored lexical root no longer exists, an ordinary
open refuses to infer ownership from temporary unreachability. An explicit full rebuild may rebind
`workspace_root` to the current location under the mutex and claim; an existing different physical
workspace still fails the ownership check.
Each worktree is an independent lock domain. `index_worktree` may make a zero-wait acquisition
attempt for a target worktree while the caller owns its own workspace; it never blocks on that
second mutex, so there is no cross-worktree wait cycle. Once acquired, the one-shot publisher
holds the target destination claim in rebuilding state through the anchored install and rewrites
the staged database's `workspace_root` to the target before publication. A target Phoenix relay
started mid-publication remains unavailable until it can elect or join the target daemon.

### Unavailable source capture and retry contract

Schema 17 and server version 0.12.7 keep these outcomes distinct:

- `Success`: complete bounded bytes from one held regular-file handle.
- `Missing`: the path is authoritatively absent, so an existing row may be deleted.
- `DefinitelyNonRegular`: the leaf is a directory, link/reparse point, device, FIFO, or another
  refused file type; it must never be opened as source evidence.
- `Unavailable`: a regular file could not be captured completely because of a transient open/read,
  sharing, permission, replacement, or length-stability failure.
- `Oversized`: a regular file exceeds the configured source-byte limit; retry cannot repair it.

On Unix, source capture walks from an anchored directory with relative `openat` calls using
read-only, no-follow, non-blocking, close-on-exec, and no-controlling-terminal flags; directory
components also require directory-only opens. Linux resolves its directory/no-follow flag values
from the process architecture rather than assuming the x86 ABI. The leaf must pass regular-file `fstat`, remain under
the byte limit, and produce exactly its measured bytes with no extra byte. Windows uses relative
`NtCreateFile` calls with `FILE_OPEN` semantics, read-data/read-attributes access,
read/write/delete sharing, reparse-point refusal, and a non-directory leaf requirement. These
flags avoid following workspace-controlled links and avoid blocking on special files, but they
cannot eliminate an editor save/rename race.

`Unavailable` and `Oversized` regular sources are refresh failures, not skipped files. `Missing`
and `DefinitelyNonRegular` scan entries are omitted with skipped-input accounting. `DeltaRefresher` throws a
typed failure inside the transaction so every row and commit-metadata change in the complete batch
rolls back while the previously persisted sweep marker remains visible to compatibility readers; the manager
then refines that marker to the specific incomplete-source latch. The single pump retains an
unavailable request ahead of later
refreshes and retries the complete transaction
after short bounded delays (100 ms, 250 ms, then 1 second); it must not sleep while a SQLite
transaction is open. A retry success may publish the rows and commit metadata normally. While
retries are pending, health reports a known incomplete refresh and does not publish `ready`,
advance `indexed_commit`, report worktree `inSync`, or allow semantic coverage to claim
exact/current source evidence.

If the quick retries are exhausted, the writer keeps a stable `refresh_input_unavailable` cause,
persists that latch for read-only compatibility readers, and remains stale until a complete recovery or full
rebuild succeeds. It schedules autonomous detect-all recovery sweeps after 5, 10, 30, and then
capped 60 second delays, with one capture attempt per timer-initiated sweep so a permanently
unreadable source cannot amplify into a tight full-workspace scan loop. A successful sweep clears
the durable latch, cancels the pending timer, and resets the next unavailable episode to 5 seconds;
if row publication succeeds but clearing the durable latch fails, paced recovery remains armed.
The next queued targeted event is also widened to detect-all and retains the short bounded capture
retry ladder. Recovery therefore never relies on the operating system producing a second identical
notification, and no request can clear the incomplete-freshness latch unless its complete sweep
captures the previously unavailable source.

When the failed request carries a Git target, every later recovery request inherits that pending
baseline and re-resolves the current commit plus attached/detached state in one status-aware
`HEAD` snapshot immediately before capture. An unresolvable snapshot
leaves the writer stale, rearms paced recovery, and forces every older queued Git tuple to
revalidate before it can publish; a successful complete sweep publishes rows, `indexed_commit`,
and `indexed_branch` in the same transaction before the durable latch is cleared. Recovery samples
carry a monotonic generation, so a resolved request may clear that latch only when it is at or after
the latest unavailable generation; an older resolved request that was already queued may commit its
internally consistent rows and metadata, but it remains explicitly stale with paced recovery armed.
Because the recovery request is already active when it samples that tuple, it appends the
revalidated request at the channel tail under the observation gate instead of publishing in place.
Any older Git observation already queued therefore commits first, and the recovery snapshot remains
the final baseline until a genuinely newer observation is appended after it. A successful full
rebuild retires every ordered recovery generation sampled for the replaced database before it
installs the replacement; a retired request already queued behind the rebuild completes without
touching rows, Git metadata, or the replacement's convergence marker.
Ordinary Git signals compare both commit and branch identity, so detaching, attaching, or switching
branch names at the same commit still queues an atomic metadata reconcile. Only a resolved detached
snapshot deletes `indexed_branch`; a failed Git invocation cannot impersonate detachment.
The Git watcher is attached before the startup sample, and snapshot acquisition, comparison, and
request publication share one observation gate. Overlapping watcher/retry callbacks therefore
cannot return snapshots in one order and publish them in another. The latest resolved HEAD
observation is tracked independently of published metadata, so an inverse transition is still
queued while an older snapshot waits in the refresh pump. Commit-changing requests resolve their
changed-file scope at execution time against the baseline actually published ahead of them; rapid
A-to-B-to-A movement therefore restores both A's rows and its attachment state.

`Oversized` is persistent rather than transient and receives no rapid retry loop. The failure
identifies the regular source that prevented the atomic batch and propagates bounded partial
coverage through refresh health, compatibility-reader metadata, worktree-index results, and response metadata.
Because capture aborts on the first known-incomplete input, its path count is explicitly a lower
bound rather than a complete workspace total. It cannot advance the Git
baseline or earn `inSync`/exact claims. Strict worktree reconciliation follows the same rule and
does not install a staged database as `created` or `refreshed` when a regular source is unavailable
or oversized. Cold and explicit full builds also fail closed on any scanned regular source they
cannot capture, so a lossy new database is never published as ready. Regression coverage pins
transient failure followed by success, transaction rollback,
retry exhaustion and recovery, oversize behavior, Git-baseline preservation, queued-request
ordering, compatibility-reader propagation (including a specific-latch persistence failure), post-build
publication gating, normal writer refresh, and strict worktree refusal.

### `git checkout <branch>` / `git pull` / `merge` / `rebase`

These are **bulk working-tree mutations** handled by two complementary signals:

- Git rewrites the affected working-tree files, which the watcher sees as ordinary
  create/change/delete events → a (possibly large) delta batch. If enough events arrive at
  once to overflow the FSW buffer, the watcher's overflow handler triggers a full detect-all
  sweep. Directory add/remove on a branch also escalates to a sweep. The startup sweep is a
  final backstop after any restart.
- `GitWatcher` observes repository HEAD changes explicitly. `IndexManager` diffs the stored
  `indexed_commit` against the new HEAD and enqueues that changed-file set through the same
  serialized refresh channel. If Git cannot provide the diff or it exceeds the configured cap,
  the manager enqueues a detect-all sweep. The new commit baseline is recorded only in the same
  successful reconcile that applies the corresponding rows.
- `.git/` itself is excluded, so git's internal churn never pollutes the index.

**So switching branches or pulling *does* converge the index to the new tree** — with two honest
caveats:

1. **Brief staleness window.** During the ~600 ms debounce, watcher-backed responses may report
   `stale` with non-zero `pendingChanges`. The watcher drains those paths when it enqueues the
   serialized request, so the pump can report `refreshing` with `pendingChanges: 0`; Git-triggered
   requests never contribute to that watcher-derived count. State and the incomplete-source fields,
   rather than a zero pending count alone, determine whether current-source evidence is earned.
2. **Git can be unavailable.** An unresolved configured Git executable logs watcher-only degraded
   mode. Unresolved repository metadata can leave Git tracking unattached, while a failed or
   over-cap commit diff falls back to a detect-all reconcile instead of logging watcher-only mode.
   A watcher overflow also escalates to a detect-all sweep; `refresh_index()` remains the manual
   recovery hatch when the reported commit/freshness state is uncertain.

## Shared MCP daemon and per-agent proxies

Phoenix's original deployment put one full stdio MCP process behind every client. Phoenix v0.12.60
replaces that public topology with one **Phoenix daemon per current user and canonical physical
worktree**. The existing executable remains the MCP command configured by Claude, Codex, and other
hosts, but every ordinary invocation is now a small stdio proxy with no flag or environment opt-in:

```text
Claude/Codex --stdio--> Phoenix proxy --named pipe / UDS--> Phoenix daemon
                                                       |-- IndexManager
                                                       |-- WorkspaceWatcher + refresh pump
                                                       |-- SQLite read/write pools
                                                       |-- shared Roslyn workspace
                                                       `-- shared F# semantic service
```

The daemon is the sole ordinary live-index reader and writer. Proxies never load Core indexing or
semantic state and never open SQLite. Cross-worktree seeding is not an ordinary query path: it keeps
the existing destination and file-level claim protocol. `--standalone` is an explicit diagnostic
and isolated-test mode only; it is never selected automatically and refuses to serve unless it
acquires the writer lease. Every daemon startup, endpoint, identity, ownership, workspace, protocol,
or schema failure stays visible through the typed unavailable MCP shim.

### Transport and session contract

Phoenix runs MCP itself over the local transport rather than defining a second request RPC. The MCP
SDK's stream server transport binds one accepted pipe/socket stream to one `McpServer` session;
all sessions receive the same daemon service provider and therefore the same `IndexManager` and
`SemanticService` singletons. Tool registration, JSON schemas, cancellation notifications, progress,
and error envelopes remain single-sourced. CLI discovery and structural argument validation create
the same reflection-backed `McpServerTool` registrations locally; their request-time factories do
not construct `NavigationTools` or touch Core state. Only a validated tool invocation opens an MCP
client on the private daemon stream and writes the returned structured content as one JSON document.
Complete JSON files/stdin are parsed directly from UTF-8 streams; stdin is EOF-framed, and the CLI
does not invent a second input-size limit. Discovery results carry the producing build/schema stamp.
A daemon response that is not the expected structured or single-JSON-text MCP result is an honest
non-retryable `phoenix_tool_result_invalid` exit-1 response, never daemon-unavailable. The CLI never
opens SQLite or selects standalone mode. The proxy owns only stdio/CLI transport
adaptation, discovery, autostart, the pre-MCP handshake, liveness, and reconnect before an MCP
session is initialized.
The short-lived bootstrap severs caller stdout/stderr handle inheritance as well as process-tree
ancestry before it launches the daemon; the daemon receives fresh private startup output streams
and detaches its inherited stdin immediately on entry.
Each accepted stream is dispatched before any session handler runs, so a first client whose MCP
reads and requests keep completing synchronously cannot monopolize the accept loop and starve a
second client's preamble. If a connected endpoint nevertheless fails to answer the bounded preamble,
the proxy reports the retryable `daemon_handshake_timeout` cause rather than a generic proxy failure.

Before the first MCP `initialize` message, the proxy and daemon exchange one bounded preamble. It
contains:

- fixed magic and preamble version;
- `BuildInfo.Version` and `IndexBuilder.SchemaVersion`;
- canonical physical-worktree identity and normalized lexical workspace root;
- current-user identity plus client PID/name;
- requested mode (`connect` or `retire-and-replace`); and
- a random connection nonce echoed by the response.

The wire prefix containing the magic, the preamble-version field, and the framed
`retire-and-replace` request is frozen permanently. Every future daemon version must continue to
parse that prefix even when it cannot parse the remainder of an older or newer ordinary-connect
preamble. Fields after the frozen prefix may evolve under an explicit preamble version. This keeps
graceful retirement available across an otherwise incompatible version distance without allowing a
new proxy to kill a process it could not authenticate.

The preamble is length-prefixed and capped independently of MCP response budgets. Malformed,
truncated, over-limit, rooted/mismatched-workspace, wrong-user, incompatible-protocol, or incompatible-
schema input receives a typed refusal and the stream closes before MCP parsing. A successful
response repeats daemon PID, version, schema, workspace identity, mode, and nonce. This is an
authority handshake, not merely discovery metadata: the proxy never trusts a descriptor without a
successful live exchange.

The stable transport address is derived from current-user identity plus the same canonical physical-
worktree identity used by the ownership lease; it does not contain the Phoenix version. Windows uses
a named pipe. Unix first uses a short identity hash beneath the owner-only
`/tmp/phoenix-codenav-<uid>/` directory so sessions with different `XDG_RUNTIME_DIR` or `TMPDIR`
environments still discover one daemon; environment-specific runtime directories remain availability
fallbacks. During the v0.12.60-to-v0.12.61 address migration, a proxy also probes already-existing,
owner-authorized legacy directories derived from its environment and uses the frozen preamble to
connect or retire an older daemon before electing at the stable address. If an older daemon was
started with an environment the new client cannot reconstruct, startup fails with explicit safe
remediation to close or restart that older session; it never identifies or kills a process by PID.
The socket is never placed beneath a possibly deep worktree because Unix socket paths are
short. A bounded, endpoint-keyed descriptor
beside the pipe/socket startup lock publishes address, PID, version, schema, workspace identity, and
start time for discovery and operations visibility. The descriptor is never written through the
untrusted worktree; on Unix it remains beneath the owner-only runtime directory. It is still only an
untrusted hint, and the live handshake remains authoritative.

### Election, upgrades, and shutdown

The proxy first connects to the stable address. A failed connect does not make the descriptor or its
PID authoritative: the proxy enters the endpoint-keyed startup election and retries the live
authority handshake. A workspace-keyed startup mutex provides single-flight daemon creation: one
proxy launches the daemon, losers wait within a bounded startup deadline and then connect. The daemon
acquires the existing index ownership lease before publishing its endpoint ready; inability to prove
the configured database belongs to this worktree fails closed.
Launch passes through a short-lived bootstrap process before the long-lived daemon is started. The
daemon sends exactly one bounded private ready/refusal frame to the bootstrap, which relays it to the
electing proxy and exits. Standard output exists only as inherited Phoenix-to-Phoenix startup IPC and
is detached as soon as that frame is written; it is not a public command, option, or MCP protocol.
The bootstrap therefore removes the daemon from any one stdio proxy's process tree without hiding an
index-authority refusal behind a generic timeout. If the daemon dies or closes the pipe first, the
proxy reports `daemon_died_before_report` rather than inferring meaning from an exit code.

The electing proxy publishes a bounded advisory refusal beside the startup lock so concurrent
proxies can expose the same exact typed cause instead of launching duplicate doomed daemons. The
record is decisive only while its exact owner PID and process start time remain live and its build,
workspace, database, endpoint, and rebuild intent all match. Stale, corrupt, mismatched, linked, or
foreign records are ignored; a later election clears them before launch and successful readiness
removes them. This is refusal sharing, not durable failure state: once the owner session ends, the
next ordinary launch retries and auto-repairs whenever the local blocker has cleared. The unavailable
shim is intentionally terminal for its one MCP session: repeated tool calls are cheap and return the
same cause; retryable conditions are retried by reconnecting, never by a per-call daemon spawn loop.
Core's local manager error may mention the in-band `refresh_index force:'full'` recovery available to
an already-running server. A daemon that is refused before MCP initialization cannot serve that tool,
so its typed shim instead names the equivalent existing launch-time `--rebuild` action.

Version negotiation replaces versioned endpoint names. The initial compatibility predicate is exact
`BuildInfo.Version` equality **and** exact `IndexBuilder.SchemaVersion` equality. Any difference is
ordered by semantic tool version first and schema version second for the older/newer diagnosis; a
broader compatible range requires a later explicit contract and evidence.

- exactly compatible versions connect normally;
- an older client facing a newer daemon receives a typed `daemon_newer_than_client` response that
  tells the host to restart/update its agent;
- a newer client may request `retire-and-replace`; a compatible older daemon stops accepting new
  sessions, removes its endpoint, drains admitted sessions under the existing operation deadline
  ceiling, releases the workspace writer lease, removes its ready descriptor, and exits. Endpoint
  disappearance alone is not completion: unless another successor generation already owns
  discovery, the newer client must acquire and release a transient lease probe before starting its
  successor;
- if graceful retirement exceeds the bounded drain, the new client reports the typed
  `daemon_takeover_timeout` cause instead of killing an unverified process or serving mixed-version
  results; and
- if the lease probe itself cannot establish whether writer ownership is free, the client reports
  the distinct retryable `daemon_writer_lease_unverifiable` cause rather than claiming that the
  older daemon still holds the lease.

The daemon stays alive for a 15-minute linger after its last client disconnects so normal agent
restarts retain the watcher and semantic estate. Another connection cancels the idle shutdown. A
keep-alive option supports build servers. At final shutdown the daemon stops accepting connections,
drains admitted work, disposes semantic/index services, removes only its own verified descriptor and
endpoint, and releases the workspace lease. An initialized MCP session ends if its daemon dies: MCP
does not permit replacing the server transport without a new `initialize` exchange. The MCP host then
restarts its stdio proxy; replacement proxies use the same bounded autostart election, so one starts
the successor while peers wait. Negotiation failures before initialization remain visible through the
typed unavailable shim rather than silently opening the database.
Retirement or shutdown during an in-flight private staged rebuild abandons that GUID-named stage;
the existing verified orphan-scavenging contract reaps it after the successor acquires ownership,
without publishing partial output or deleting another owner's stage.

A fail-closed negotiation refusal must remain visible through MCP. Instead of simply exiting, the
proxy completes an MCP initialize handshake as a bounded unavailable shim: `server_capabilities`
reports `meta.indexMode: "unavailable"` plus the stable refusal id and actionable recovery, and every
other advertised tool returns the same typed unavailable cause. Hosts therefore see messages such as
"daemon is newer; restart/update this agent" or "takeover timed out" rather than an unexplained dead
stdio server.

### Security boundary

The daemon is per user and per worktree, never system-wide:

- Windows pipe ACLs grant only the current user SID. The proxy also verifies the named-pipe server
  process belongs to that user before accepting the preamble, preventing a same-machine pipe-squatting
  process running as another account from becoming authority.
- Unix runtime directories are mode `0700`; socket operations are owner-checked and refuse unsafe
  filesystem objects. The descriptor stays outside the worktree in that same owner-only directory,
  refuses link/reparse entries, and never becomes authority. The short address is derived from
  identity, while the full canonical workspace identity is still checked in the preamble.
- The daemon serves exactly one physical worktree and one configured index destination. A proxy
  cannot select an arbitrary database path after connection.
- Daemon executable launch authority is the currently running proxy's verified executable, not a
  path taken from the workspace or descriptor.

There is no standalone fallback. Availability, security, and identity failures all stay in the
typed unavailable surface. Responses expose
`meta.indexMode: "daemon" | "standalone" | "unavailable"`, plus daemon/client identity diagnostics
without publishing raw user or workspace paths.

### Multi-client isolation and sizing

Sharing semantic state increases the failure blast radius, so daemon admission is client-aware.
Each connection receives a stable runtime client id and separate concurrency counters. Global
semantic and index budgets remain hard ceilings, while weighted fair admission prevents one client
from monopolizing all semantic slots, refresh requests, pinned snapshots, or response memory.
Cancellation is scoped to the originating MCP session; closing one proxy cancels only that client's
outstanding requests. Refresh mutation remains the existing single serialized writer pump, and all
clients observe the same committed epoch and warm semantic identities.

Tools that launch a companion process, currently `open_operations_portal`, execute in daemon context.
Their executable resolution, working directory, environment isolation, and child-lifetime contract
must therefore be derived explicitly from packaged daemon authority rather than inherited implicitly
from an individual MCP host process.

The current 2.6/3.0 GiB semantic retention tiers are per-process measurements and are not copied
unchanged into the daemon. The daemon budget is calibrated on the runtime corpus and must grow
sublinearly with client count. Initial gates require three concurrent clients to remain at or below
approximately 1.5 times standalone RSS, no statistically meaningful single-client latency regression
against direct stdio, and no rebuild-wall regression relative to the established cold-index gate.

### Transparent default and decisive gates

There is one normal topology: every stdio launch proxies to the shared daemon. The obsolete
`--shared-daemon` spelling remains an unnecessary compatibility alias, and the obsolete
`--daemon-fallback-standalone` spelling is accepted as an inert no-op so stale configurations do not
fail argument parsing. Neither changes runtime behavior. Only an explicit `--standalone` selects the
diagnostic server, and that server must own the writer lease.

The implementation gate adds tests for the handshake/version/schema/workspace matrix, two-proxy cold-
start election, stale descriptor recovery, pipe squatting refusal, daemon crash during a write with
WAL recovery, host reconnect/new-proxy re-election, cancellation isolation, multi-client refresh storms, fairness
under a pathological semantic request, explicit-standalone refusal while a daemon owns the lease, idle linger and keep-alive,
single-client latency, multi-client RSS, and rebuild-wall parity. A test passes only when exactly one
ordinary database owner and one watcher exist for the physical worktree and every client converges to
the same committed index and semantic model identity.

## Result discipline

- **Budgets.** ~8 KB soft target and ~64 KB ordinary hard target per response; oversized lists
  shrink (precise-first) and set `truncated: true`. Optional semantic declaration sites are removed
  with truthful counts and `semantic.declaration_sites_budget` before the single indivisible
  compiler-identity exception is considered; that identity remains
  complete and reports its measured overage in `responseBudget`.
- **Cursors.** List tools return `nextCursor` for paging.
- **Stable, line-addressable hits.** Every result carries enough path/line/span metadata
  for a follow-up `source_context`.

## Deployment

Published as a self-contained `PhoenixCodeNav.Mcp.exe` plus adjacent `FSharp.Core.dll` reference
sidecar and a separate `portal/` companion directory (no installed runtime prerequisite), or a
framework-dependent build (needs .NET 10). Attach over MCP (`.mcp.json` for Claude Code,
`config.toml` for Codex). First run builds the index in the background; it lives in
`<workspace>/.codenav/index.db`. See [`../README.md`](../README.md) for exact commands.
