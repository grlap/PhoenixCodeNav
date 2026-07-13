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
├── src/CodeNav.Mcp/           # the server, published as PhoenixCodeNav.Mcp.exe
│   ├── Program.cs             # host + stdio transport; starts indexing in the background
│   ├── NavigationTools(.Expanded).cs   # the 23 MCP tools
│   └── Responses.cs          # JSON policy, response budgets, the Meta envelope
├── src/CodeNav.WorkspaceGen/  # deterministic synthetic 2k-project workspace generator (for tests/benchmarks)
├── src/CodeNav.Bench/         # cold-build + warm-query benchmarks vs the latency targets
└── tests/CodeNav.Tests/       # 76 tests
```

`CodeNav.Core` has no dependency on the MCP SDK — it is a plain library that could back a
different front end. `CodeNav.Mcp` is a thin protocol/shaping layer over it.

## The three navigation layers

Agents use the cheapest layer that answers the question, preferring compiler-backed facts
for code identifiers.

1. **Indexed text** — `find_file`, `search_text`, `config_lookup`. SQLite FTS5 over file
   contents with workspace-aware ranking and byte/line offsets. `search_text` grades each
   line `precise` (contains all query tokens as whole tokens) vs `partial` (a token-covering
   lead), so a partial-token match is never presented as a full hit.
2. **Syntax** — `outline`, `search_symbol`, `symbol_at`, `batch_outline`. Roslyn
   *syntax-only* parsing (no compilation) extracts namespaces/types/members with spans,
   signatures, accessibility, partial flags, and generated/test classification. This is the
   token-saver: `outline` before any large-file read, then `source_context` for the spans.
3. **Semantic** — `definition`, `references`, `implementations`, `callers`, `callees`,
   `type_hierarchy`. Roslyn *compilations* give compiler-exact answers with
   `documentationCommentId`s.

Structural facts (`project_graph`, `projects_containing`, `dependency_path`,
`repo_overview`) come from the csproj/sln parse. Composites (`context_pack`, `impact`,
`related_tests`) synthesize the lower layers.

### Confidence model

Every response carries a `confidence`:

- `exact` — compiler/Roslyn verified.
- `indexed` — from the persisted index or syntax parse; trustworthy but not compiler-checked.
- `heuristic` — inferred from naming, base-list text, or project relationships
  (`implementations` fallback, `related_tests`) — leads, not facts.
- degradation flags: `partial` (a deadline/coverage limit was hit), `stale` (index older
  than the working tree), plus `coverage` counts.

## The index substrate

**Storage** is SQLite with FTS5 (`IndexStore`). Schema: `files` (path, hash, generated/test
flags, freshness), `file_contents` + an external-content `fts_content` virtual table,
`projects` / `project_refs` / `package_refs` / `compile_items`, `solutions` /
`solution_projects`, `symbols` (kind, name facets, spans, parent links), and `meta`
(index version, timestamps, coverage). On Windows, WAL mode is exposed as one writer process plus
many read-only follower processes. Follower index-backed evidence uses committed snapshots and
followers never open a writer connection; explicitly live source/Git and compiler-backed semantic
evidence may use newer workspace bytes. Other platforms remain writer-only for now.

**Build** (`IndexBuilder`): scan the tree (excluding `.git`, `bin`, `obj`, `packages`,
`node_modules`, `.vs`, generated files, and symlink/junction targets); parse every `.csproj`
directly, independent of solution membership; parse every `.cs` with Roslyn syntax on all cores,
streaming symbol rows through a bounded channel to the single writer. Solution files are optional
editor inventory only: they never select projects or provide build, dependency, ownership, or
symbol-resolution authority. A cold build of a
multi-thousand-project workspace completes in minutes at most; live progress counters
(phase, files, throughput) report the real numbers for any given machine.

**Compile-item ownership**: legacy projects list `<Compile Include>` explicitly (exact,
including linked files); SDK-style projects are approximated by longest-dir-prefix globbing.

## Project identity, output variants, and reference resolution

This is the target design for project-backed semantic resolution. The current runtime still has
name-keyed compatibility paths; those paths must not be treated as the final identity model.

### Identity invariants

- A **physical project** is identified by its normalized `.csproj` path and persisted `project_id`.
  `AssemblyName`, project filename, solution membership, and emitted DLL filename are not project
  identity.
- A **compilation variant** is identified by `variant_id`: physical project plus the statically
  observable build dimensions that affect compilation, such as target framework, configuration,
  platform, and explicit MSBuild properties. `variant_id` is database-local; normalized project path
  plus `dimension_key` is the stable logical identity used across refreshes and Roslyn reloads. One
  physical project may produce several variants.
- An **output artifact** belongs to a variant and is identified by its normalized full output path.
  Output paths are facts used for matching, not globally unique keys: multi-stage builds can make
  multiple variants write the same path, in which case the producer is genuinely ambiguous.
- `AssemblyName` is preserved exactly as parsed. It remains authoritative evidence for the existing
  `Reference Include` assembly-edge recovery when it identifies a unique workspace project, but it
  is not physical-project or compilation-variant identity and must never collapse either one.
  Phoenix also preserves `TargetName` and `TargetExt`; it does not assume that `AssemblyName` omits
  `.dll` or reconstruct a target filename when a more direct output fact exists.

The motivating repository shape is valid and common:

```text
Partner.Framework.csproj
  AssemblyName: <present, unique legacy assembly name>
  TargetFramework: net472
  TargetName: Partner.Framework
  OutputPath: Build/
  -> Build/Partner.Framework.dll

Partner.Framework.Net.csproj
  AssemblyName: <present, unique SDK assembly name>
  TargetName: Partner.Framework
  TargetFrameworks: net8.0;net472
  OutputPath (net8.0 condition): Build/Net8/
    -> Build/Net8/Partner.Framework.dll
  OutputPath (net472 condition): Build/Net472/
    -> Build/Net472/Partner.Framework.dll
```

The field projects have unique `AssemblyName` values; this example is about one physical project's
multiple target-framework outputs, not an assembly-name collision diagnosis. Physical projects and
their variants must remain separate through indexing, dependency selection, Roslyn loading, cache
invalidation, and coverage reporting even when a name happens to be sufficient for one recovered
graph edge.

### Persisted model

Implementing this model changes stored index output and therefore requires an
`IndexBuilder.SchemaVersion` bump and a full rebuild. The target relational model is:

```text
projects
  id, path, style, raw project metadata, load status

project_variants
  id, project_id, target_framework, configuration, platform,
  dimension_key, stable_variant_key, condition, evidence, is_complete

parse_contexts
  id, language_version, preprocessor_symbols, context_key, is_complete

variant_parse_contexts
  variant_id, parse_context_id

project_outputs
  id, variant_id, raw AssemblyName, raw TargetName, raw TargetExt,
  raw OutputPath/OutDir, path domain, normalized output directory, normalized target path,
  condition, evidence

variant_compile_items
  variant_id, file_id, provenance, condition, evaluation_status

variant_fact_coverage
  variant_id, parse_context_complete, compile_ownership_complete,
  reference_graph_complete, reasons

project_reference_facts
  from_variant_id, raw Include, normalized target project path,
  requested target framework/properties, condition

assembly_reference_facts
  from_variant_id, raw Include, simple assembly name, raw HintPath,
  normalized HintPath, condition

variant_package_refs
  variant_id, package id, version, condition

reference_resolutions
  id, reference_fact_kind, reference_fact_id, from_variant_id,
  status, is_complete, selected_candidate_id (nullable)

reference_resolution_candidates
  id, resolution_id, target_variant_id (nullable), binary_path (nullable),
  provenance, compatibility, matched_output_path, rank

resolved_reference_edges
  resolution_id, from_variant_id, target_variant_id, provenance
```

Raw parse facts, candidate resolutions, and chosen edges are separate. Every declared reference item
has its own fact-keyed resolution group; every possible producer variant or binary path has its own
candidate row. Re-resolution can change or remove a chosen edge without losing competing evidence,
and diagnostics can explain whether a group came from an exact `ProjectReference`, recovered
assembly edge, exact output-path match, package input, binary-only HintPath, or unresolved/ambiguous
evidence.

Because Phoenix intentionally does not run MSBuild, it records every statically observable output
candidate rather than pretending to have one fully evaluated `TargetPath`. Parsing includes
`TargetFramework` / `TargetFrameworks`, conditioned property groups, `Configuration`, `Platform`,
`AssemblyName`, `TargetName`, `TargetExt`, `OutputPath`, `OutDir`, `BaseOutputPath`, and
`AppendTargetFrameworkToOutputPath`. Raw conditions are retained. Unsupported imports or conditions
make the relevant variant incomplete; they do not authorize a guessed exact edge.
Paths inside the workspace are stored in the workspace-relative Git path domain so an index remains
portable to a sibling worktree. Absolute external paths retain an explicit external path domain and
are never silently re-rooted. HintPath and output matching always compares paths in the same domain.

### MSBuild-free variant evaluation

Phoenix remains MSBuild-free, so its evaluator is deliberately bounded and evidence-preserving. For
each statically visible dimension tuple it evaluates project property/item groups in document order,
with later applicable assignments overriding earlier ones as MSBuild properties do. Dimension
domains come from `TargetFramework`/`TargetFrameworks`, `Configurations`, `Platforms`, explicit
caller properties, and literal values visible in supported conditions. Defaults are `Debug` and
`AnyCPU` only when the project supplies no observable alternatives.

Evaluation permits at most 256 dimension tuples per physical project, 32 recursive property-
expansion levels, and 4,096 property substitutions per tuple. These are named implementation
constants, not silent SQL limits. Evaluation checks cancellation/deadline between property groups,
items, conditions, and dimension tuples. Reaching any bound preserves observed facts, marks the
affected project/variant dimensions incomplete, and forces partial coverage; it never truncates
while retaining a complete claim.

Property expansion supports bounded, cycle-detected `$(Property)` substitution. The supported
condition subset includes literal/property equality and inequality, parentheses, boolean `And`/`Or`,
and safely contained `Exists` checks. Unsupported functions, imports, wildcard dimensions, or values
that remain unresolved preserve the raw condition and mark the affected variant facts incomplete;
they are never treated as false merely because Phoenix cannot evaluate them.

Output evaluation uses the effective non-empty `OutDir` when present, otherwise `OutputPath`.
`OutputPath` uses the evaluated `BaseOutputPath`/`Configuration` defaults only when no applicable
assignment exists. For SDK projects, the target framework is appended unless
`AppendTargetFrameworkToOutputPath` evaluates to false. `TargetName` defaults to the evaluated
`AssemblyName`, and `TargetExt` defaults from the output type (normally `.dll` for a library). Every
conditioned result remains a separate `project_outputs` fact; Phoenix does not collapse paths that
normalize to the same filename.

The effective parse context combines evaluated `DefineConstants` with the standard symbols implied
by the target framework. For example, net8.0 supplies `NET8_0` and its applicable
`NET8_0_OR_GREATER` family, while net472 supplies the applicable .NET Framework symbols. Unknown or
unsupported TFMs make the implicit-symbol set incomplete. Variant-to-variant selection prefers an
exact TFM, then uses standard framework compatibility rules; no compatible result stays unresolved,
and multiple equally compatible results stay ambiguous rather than being selected by order.

### Resolution precedence

Resolution is path-first and evidence-preserving:

1. **`ProjectReference`** — normalize `Include` relative to the referring `.csproj` and resolve the
   exact physical project path. If `SetTargetFramework`, `AdditionalProperties`, or equivalent
   metadata selects a variant, honor it. Otherwise select compatible target-framework variants
   from the consuming variant. More than one compatible result remains observable ambiguity; do
   not choose by assembly name or row order.
2. **`Reference` assembly-edge recovery** — retain the existing direct-project parser behavior:
   normalize and match the `Reference Include` simple name against a unique workspace
   `AssemblyName`. A safe `HintPath` gates and refines that evidence, but its DLL basename is not a
   fallback project-name match. Preserve the recovered graph/candidate edge with
   `hintPathReference` provenance. This is authoritative build evidence for legacy multi-stage
   repositories and must not be removed by the variant migration. A recovered physical-project
   edge does not by itself collapse or silently select among that project's compilation variants.
3. **`Reference` + `HintPath` variant refinement** — normalize `HintPath` relative to the referring
   physical project and compare the full path with `project_outputs.normalized target path`.
   - One matching output resolves to that exact producer variant.
   - No matching output preserves the existing recovered workspace-project edge when one exists.
     If none exists, the safely readable HintPath DLL remains a binary metadata reference.
   - Multiple matching outputs mean the file is a shared/overwritten multi-stage artifact. Preserve
     the variant ambiguity and recovered project/binary provenance; never pick the first variant.
4. **Package input** — resolve the declared package id/version and chosen compatible asset. Package
   identity does not collapse a workspace project with the same assembly name.
5. **Plain `Reference` without `HintPath`** — retain unique-name workspace edge recovery, otherwise
   bind through framework/search-path evidence when available. Ambiguous assembly-name matches stay
   observable; row order is never selection authority.

The legacy name-keyed `project_refs` projection retains its established collision behavior for
nonsemantic graph consumers. Semantic resolution uses the physical-project/variant relations above,
expands every physical match, and reports ambiguity instead of choosing by row order.

Shipping the physical-project/variant model requires a `BuildInfo.Version` bump and its own singular
capability id, `physical-project-variant-resolution`. Candidate discovery is independently deployable
and uses a separate `syntax-authoritative-semantic-candidates` capability id.

An exact `ProjectReference` has source-project precedence over a binary copy. A recovered assembly
edge and a HintPath that maps uniquely to a known project output may use that source variant in
Roslyn while retaining `hintPathReference` provenance. An unresolved variant choice must preserve
all compatible/recovered evidence and surface partial or ambiguous coverage; Phoenix must not
replace last-built binary semantics with a first-row variant.

### Roslyn loading, caching, and result honesty

- `SemanticWorkspace` creates one Roslyn `ProjectId` per stable variant key; database `variant_id`
  locates the current persisted row but is not the reload identity. Project reevaluation upserts by
  `(normalized project path, dimension_key)` and reuses the existing database and Roslyn ids when
  the logical variant survives. Cache, dependency, and eviction identity are variant-based. Two
  projects or variants with the same assembly name are never merged into one Roslyn project.
- Documents, compile items, project references, HintPath assemblies, and packages are attached to
  the variant that declared them. Warm fingerprints include compiled source plus every structural
  input that contributed to that variant, so reference-only changes invalidate the compilation.
- Semantic queries may scan several variants and aggregate their results, but deduplication happens
  after compilation. Results retain owning project path, variant/TFM, and reference provenance;
  aggregation never erases which compilation produced a hit.
- `confidence: exact` describes compiler verification of returned evidence; completeness is carried
  independently by `partial`, lower-bound totals, and coverage. Unsupported conditions, unresolved
  output selection, ambiguous producers, or omitted variants must set those completeness signals
  honestly. Exact returned hits may coexist with `partial: true`; an exact-looking complete total
  may not.

The migration is complete only when candidate discovery, dependency closures, project graph APIs,
semantic cache keys, Roslyn project construction, symbol handles, and coverage all carry physical
project and variant identity. A query such as `ProjectsByName(AssemblyName)` may remain a search
helper, but it must never again be a compilation-identity boundary.

## The semantic layer — MSBuild-free, lazy, snapshot-pinned

This is the part designed specifically for net472 enterprise scale.

- **No MSBuild.** `SemanticWorkspace` builds a Roslyn `AdhocWorkspace` by hand from parsed
  csproj facts: documents from live files, framework reference assemblies (located via a
  targeting pack, the NuGet reference-assembly package, or the installed .NET Framework —
  see `ReferenceAssemblyLocator`), hint-path/NuGet package dlls, and in-cluster project
  references. This avoids `MSBuildWorkspace.OpenSolutionAsync`, which does not scale to a
  few-thousand-project solution.
- **Lazy, candidate-scoped clusters.** A reference query always loads the declaring project's dependency
  closure, then selects up to 128 optional candidate projects by default. `maxProjects: 0` selects
  all candidates and positive values have no fixed upper ceiling. Complete candidate discovery
  precedes selection; coverage excludes the mandatory closure from its candidate count, omits the
  applied bound for unbounded selection, and marks bounded reference totals as lower bounds. Bounded
  responses report the authoritative skipped count plus a size-limited sample. Candidate discovery
  follows the syntax-authoritative contract below; an FTS miss is never negative symbol evidence.
- **One snapshot per operation.** Each op resolves the symbol against, *and* runs
  `SymbolFinder` against, a single pinned `Solution` — so a background reload/eviction can't
  orphan the symbol mid-query (which previously produced empty "exact" results).
- **Rebuild-coordinated long scans.** Candidate enumeration and semantic cluster loading hold a
  shared cross-process reader guard, so a destructive Windows rebuild drains them before replacing
  the SQLite database.
- **Reload keeps identity.** A changed compilation variant reloads under its *existing* Roslyn
  `ProjectId`, and
  eviction only removes projects nothing loaded references — so dependents' references never
  dangle. An LRU soft-caps the loaded set (~160 projects).

`maxProjects` counts unique optional physical projects, matching the public parameter name and
existing coverage contract. A selected project consumes the budget once; every applicable compatible
variant then expands in stable `dimension_key` order without consuming another project slot. Coverage
reports candidate/selected/loaded physical-project counts and variant counts separately. Deadline or
load failure during variant expansion sets partial coverage rather than silently dropping a variant.

Cold-cluster latency and working set scale with the selected project budget; use
`CodeNav.Bench` against the target repository for deployment sizing. Warm clusters avoid
reloading unchanged projects.

### Authoritative semantic candidate universe and implementation seeds

Semantic candidate discovery answers a coverage question: *which variants can bind to, implement,
or reference this exact target symbol?* Direct base-list discovery answers a narrower prioritization
question: *which variants visibly declare a type whose base list names the target?* A direct seed is
valuable but is not the complete denominator: `Concrete : Base` can implement `IFoo` indirectly when
`Base : IFoo`, and an ordinary `IFoo field;` reference has no base-list fact at all.

The complete graph-valid universe is the transitive reverse variant-reference closure from every
exact target-owner variant. It includes direct and indirect consumers for implementations,
references, and callers. Base-list facts prioritize likely direct implementers and recover
observable graph-gap leads; they never replace the reverse closure. When reference-graph or compile
ownership facts are incomplete, conservative variants and their reverse dependents are included and
the response is partial rather than falsely complete.

File-content FTS answers a different question: *which indexed files contain this text token?* FTS is
useful for interactive indexed-text search and for ordering fallback leads, but its absence is not
proof that a parsed declaration does not exist. It must never be a prefilter for the semantic
candidate universe or base-list seeds.

The search starts only after the requested target has been resolved as precisely as the request
allows. A documentation identity or valid symbol handle supplies its exact name and arity. A bare
name that maps to more than one indexed identity remains ambiguous; candidate enumeration must not
silently merge those identities just because their terminal names match.

#### Stored base-list facts

The scalable target is a syntax-authoritative relation populated from every type declaration's
Roslyn `BaseListSyntax` under every distinct, statically known parse context used by an owning
compilation variant:

```text
symbol_base_types
  id, parse_context_id, file_id, declaration_key, ordinal,
  raw_type_text, lookup_name, syntactic_arity,
  qualifier_text, resolution_kind, scope_evidence

index
  (lookup_name, syntactic_arity, parse_context_id, file_id, id)

unique
  (parse_context_id, file_id, declaration_key, ordinal)

supporting reverse indexes
  symbol_base_types(resolution_kind, parse_context_id, file_id, id)
  symbol_base_types(file_id, parse_context_id, id)
  variant_parse_contexts(parse_context_id, variant_id)
  variant_compile_items(file_id, variant_id)
  variant_compile_items(variant_id, file_id)
  resolved_reference_edges(target_variant_id, from_variant_id)
  resolved_reference_edges(from_variant_id, target_variant_id)
  reference_resolution_candidates(resolution_id, id)
  reference_resolution_candidates(target_variant_id, resolution_id)
  reference_resolutions(from_variant_id, id)
```

`lookup_name` and `syntactic_arity` are search keys, not resolved semantic identity. For example,
`global::Contracts.IHandler<Order>` contributes terminal name `IHandler` and arity `1` while
retaining the qualifier and raw text as evidence. Candidate discovery deliberately over-includes
same-name syntax; Roslyn later decides whether a candidate base type binds to the exact requested
symbol.

A base alias that can be expanded safely from syntax and its lexical scope contributes the expanded
lookup name/arity with `resolution_kind = syntaxAlias`. Aliases that depend on global/project
usings, conditional compilation, extern aliases, or other variant context use
`resolution_kind = unresolved`. Every query unions owners of all unresolved base facts with the
direct name/arity lookup. This conservative bucket may over-include projects, but it cannot hide an
implementation. Exact completeness is possible only after this bucket is fully enumerated and
Roslyn has bound the selected variants.

Parse contexts include at least language version and the effective preprocessor-symbol set. Variants
with identical syntax-affecting options share one context; a file is reparsed only once per distinct
context, not once per project row. This is required for declarations such as a NET8-only
`class Handler : IHandler<Order>` hidden behind `#if NET8_0`. A net472-first parse cannot represent
that variant's base list.

When imports or unsupported conditions prevent Phoenix from determining a variant's parse context,
that variant has incomplete base-list coverage. It is conservatively included in every semantic
candidate set until Roslyn can inspect it, and coverage reports the reason. Implementations may later
add an all-branches syntax lead index to narrow this bucket, but unevaluated disabled text can never
support complete negative evidence.

Adding this relation changes indexer output and requires an `IndexBuilder.SchemaVersion` bump and a
full rebuild. Its rows are inserted and deleted transactionally with the corresponding file and
parse-context facts during cold build and delta refresh; deletion is keyed by `file_id`, and the
table has a supporting `file_id` index for refresh cleanup. Shipping this independently deployable
candidate capability requires a `BuildInfo.Version` bump and the singular, grep-able
`syntax-authoritative-semantic-candidates` feature id; it does not reuse the variant-resolution id.

Every paged stream uses the columns of its supporting index as a stable keyset cursor; semantic
enumeration does not use `OFFSET`. Direct lookup, unresolved-fact enumeration, file cleanup,
file-to-variant ownership, and reverse dependency traversal therefore remain separately bounded
without joining declaration rows to every owner.

Parse-context completeness and compile-ownership completeness are independent. An unevaluated
`Compile` condition or import marks ownership incomplete even when `DefineConstants` and the parse
context are known. Such variants are conservative candidates and cannot contribute a complete total
until their document set is known.

A project-data refresh reevaluates variants, outputs, reference groups, compile membership, and parse
contexts in one transaction. It reparses the union of old and new compile files under every affected
old/new parse-context key whenever a `.csproj`, imported project fact, `Directory.Build.*`, or other
syntax-affecting structural input changes—even when the `.cs` bytes did not. Obsolete base facts are
removed only after replacement mappings are ready, shared file/context facts remain while another
variant uses them, and structural fingerprints invalidate every loaded Roslyn variant affected by
the change.

The existing `symbols.signature` is truncated and therefore cannot be a complete compatibility
authority. Before the new relation exists, a compatibility search may reparse complete indexed
source content with Roslyn and enumerate its `BaseListSyntax`; files whose complete snapshot content
is unavailable make discovery explicitly incomplete. A direct signature scan may supply additional
leads, but it cannot justify `candidateDiscoveryComplete: true`. FTS absence remains irrelevant in
both compatibility modes.

#### Query plan

For one resolved target identity, candidate discovery executes against one pinned index snapshot:

1. Resolve every exact target-owner variant. Its forward dependency closure is mandatory for
   compiling the target and does not consume the optional candidate budget.
2. Page the transitive reverse closure over resolved and recovered variant-reference edges. This is
   the complete graph-valid candidate universe for ordinary references and direct or indirect
   implementations; there is no fixed project ceiling.
3. Union variants whose reference graph or compile ownership is incomplete, plus their known reverse
   dependents. Ambiguous reference-resolution candidate groups contribute every compatible target,
   never a first row. These conservative additions set coverage partial until Roslyn and complete
   project facts can prove the result.
4. For implementation prioritization, page direct/syntax-expanded `symbol_base_types` by exact
   lookup name and syntactic arity, and in a separate stream page every unresolved base fact. The
   compatibility path reparses complete indexed source and remains observably incomplete wherever
   that source is unavailable. A base-list owner outside the reverse closure is retained as a
   graph-gap lead, and its known reverse dependents are added, but that gap prevents a complete claim.
5. Deduplicate qualifying `(file_id, parse_context_id)` pairs before expanding seed ownership. In
   separate bounded SQL batches, map those pairs through `variant_parse_contexts` and
   `variant_compile_items` to every owning `(variant_id, project_id, project_path)`. Union every
   variant whose parse context is incomplete. Do not join declaration rows directly to variant
   owners: one file with 513 matching declarations and 513 owners must not materialize a
   513-by-513 result. During migration, project-level `compile_items` may produce compatibility
   owners only; their missing variant dimension keeps coverage partial until compatible variants
   are conservatively expanded.
6. Aggregate variant evidence to physical projects, rank projects deterministically, then apply the
   caller's `maxProjects` budget once per optional project. The final key is independent of FTS:
   direct/expanded base evidence precedes reverse-closure consumers, then conservative
   unresolved/incomplete buckets, followed by normalized physical project path. Enumeration precedes
   selection, so candidate growth cannot silently create an undiscovered denominator.
7. Expand each selected physical project to every applicable compatible variant in stable
   `dimension_key` order, then load those variants and let Roslyn bind occurrences and inheritance
   to the exact target symbol. Only compiler-bound matches become exact implementations or
   references.

Semantic candidate discovery does not query FTS. FTS remains the authority for indexed text search,
but it does not define candidate identity, processing order, selection, the denominator, or
completeness for compiler-backed tools. Deleting an FTS row therefore cannot change a semantic
candidate set, including a bounded or deadline-limited one.

#### Deadlines, budgets, and honesty

Candidate enumeration observes the semantic operation's cancellation token and deadline. A deadline
during discovery returns only salvaged work with `partial: true`, `totalIsLowerBound: true`, and a
distinct `partialReason` such as `candidate_discovery_deadline`. Coverage distinguishes:

- target-owner/mandatory variants, reverse-closure variants, direct base-list seeds, graph-gap
  leads, and conservative incomplete-fact variants enumerated so far;
- whether the graph-valid universe, compile ownership, parse contexts, and base-list seed discovery
  each completed;
- the optional project budget applied after enumeration;
- selected, loaded, skipped-by-budget, failed, and deadline-omitted projects/variants.

`confidence` and completeness are orthogonal. A compiler-bound salvaged hit may retain
`confidence: exact`, while incomplete discovery, mandatory closure, variant expansion, or project
loading sets `partial: true` and makes totals lower bounds. Missing FTS rows affect neither because
FTS is not authority. Missing or stale syntax/base-list facts make the index stale or the result
partial; they cannot produce an exact-looking complete total.

The pinned snapshot means one SQLite read transaction spans target-owner resolution, graph closure,
coverage facts, every base-fact page, and every variant-owner expansion batch. A concurrent delta
refresh becomes visible only to a later operation; it cannot mix old syntax facts with new ownership
inside one candidate result.

#### Regression and scale contract

The implementation must be reintroduction-verified with fixtures that prove:

- deleting or withholding an implementation file's FTS row changes neither the semantic candidate
  universe nor syntax-discovered base-list seeds;
- reverse closure loads an indirect `Concrete : Base` implementer when only `Base : IFoo` is a
  direct seed, and loads a graph-valid project containing an ordinary `IFoo field;` reference;
- qualified, generic, and non-generic base types respect terminal name and arity, while semantic
  binding separates identities that syntax deliberately over-includes;
- a TFM-conditioned base list is indexed under the applicable parse context and discovered only for
  its owning variants; an unknown parse context conservatively includes the affected variant and
  marks coverage partial;
- direct aliases are syntax-expanded, and unresolved/global/project alias owners are conservatively
  included with variant-aware partial coverage;
- an unevaluated compile-item condition includes the owning variant conservatively, and a csproj-only
  change to TFM, `DefineConstants`, or compile membership reparses affected unchanged source;
- conditional `OutputPath` plus unique `AssemblyName`/shared `TargetName` facts produce the expected
  net8.0 and net472 output candidates without MSBuild;
- a linked file with hundreds of matching declarations and hundreds of owners is expanded by the
  two independently paged dimensions without a declaration-by-owner cross product;
- more than 2,000 matching projects are enumerated before `maxProjects` selection;
- physical project/variant owners are not collapsed by project or assembly display names;
- cancellation during authoritative enumeration produces honest lower-bound coverage rather than
  false completeness; and
- a refresh committed between syntax enumeration and ownership expansion does not enter the pinned
  read transaction or create a mixed-epoch result.

## Freshness — and how git operations are handled

The index is kept live without rebuilding on every keystroke:

- **`IndexManager`** owns the lifecycle. One process acquires the index writer lease, opens or
  builds in the background (never blocking the MCP handshake), and runs the serialized refresh
  pump. On Windows, compatible contenders attach as read-only followers with no writer
  connection, pump, watcher, build, or automatic promotion.
- **`WorkspaceWatcher`** (a `FileSystemWatcher`) debounces working-tree changes (600 ms
  quiet window) into batches. `DeltaRefresher` applies them: re-hash, re-parse changed
  `.cs`, update FTS + symbols, mark deletes, and rebuild the authoritative project graph when a
  `.csproj` changes. Solution changes can update non-authoritative editor inventory only.
  Directory-level changes (folder rename/move/delete) escalate to a full detect-all sweep, since
  the OS emits no per-child events for them.
- **Startup sweep.** When an existing index is reopened by the writer, a detect-all sweep
  reconciles edits made while the server was down. Followers never sweep or repair.
- Every response reports `indexStatus`, `indexVersion`, and `meta.indexMode`; capabilities expose
  the same role as `index.mode`. `writer` owns refresh/build/worktree mutations. `follower` remains
  fully queryable but returns `index_writer_required` for `refresh_index` and `index_worktree`;
  `unavailable` means the process has not attached to either database role. Followers report
  `pendingChangesKnown: false`: their index-backed fields see committed WAL state, not the writer's
  in-process queue. Live source/Git and compiler-backed semantic fields retain their own provenance.
  A follower also becomes unavailable when its writer exits and recovers when another writer owns
  the lease; promotion to writer is deliberately restart-only.
- Review snapshots and semantic loads retain shared handles to one anchored Windows coordination
  file; there is no configured reader-slot ceiling. Full rebuild holds a writer-intent turnstile,
  drains the shared handles, and only then replaces the database. Its queued request remains pending
  and resumes automatically after readers release; new readers cannot barge ahead of it.

### `git checkout <branch>` / `git pull` / `merge` / `rebase`

These are **bulk working-tree mutations**, and today they are handled *indirectly*:

- Git rewrites the affected working-tree files, which the watcher sees as ordinary
  create/change/delete events → a (possibly large) delta batch. If enough events arrive at
  once to overflow the FSW buffer, the watcher's overflow handler triggers a full detect-all
  sweep. Directory add/remove on a branch also escalates to a sweep. The startup sweep is a
  final backstop after any restart.
- `.git/` itself is excluded, so git's internal churn never pollutes the index.

**So switching branches or pulling *does* converge the index to the new tree** — but with
two honest caveats:

1. **Brief staleness window.** For the ~600 ms debounce + refresh duration the index lags
   the new branch; during it, responses report `indexStatus: refreshing`/`stale` and
   non-zero `pendingChanges`. An agent that ignores that signal could act on a stale result.
2. **No explicit git-awareness.** The index tracks *files*, not the current commit/branch.
   Convergence relies on the watcher catching every file event (or overflowing into a
   sweep). This is robust in practice but not *guaranteed* for every git plumbing path.

**Planned hardening (git-aware refresh):** watch `.git/HEAD` (+ `MERGE_HEAD`, packed-refs)
and, on any HEAD change, deterministically enqueue a `git diff`-scoped refresh — replacing
"infer a branch switch from thousands of file events" with "the branch changed, reconcile
now." This also lets `repo_overview` report the commit the index reflects. Full design in
[`git-refresh-design.md`](./git-refresh-design.md). Until it ships, a manual
`refresh_index()` (full sweep) is the workaround if you ever suspect drift right after a big
pull.

## Result discipline

- **Budgets.** ~8 KB soft target, ~24 KB hard cap per response; oversized results shrink
  (precise-first) and set `truncated: true`.
- **Cursors.** List tools return `nextCursor` for paging.
- **Stable, line-addressable hits.** Every result carries enough path/line/span metadata
  for a follow-up `source_context`.

## Deployment

Published as a self-contained single-file `PhoenixCodeNav.Mcp.exe` (no prerequisites) or a
framework-dependent build (needs .NET 9). Attach over MCP (`.mcp.json` for Claude Code,
`config.toml` for Codex). First run builds the index in the background; it lives in
`<workspace>/.codenav/index.db`. See [`../README.md`](../README.md) for exact commands.
