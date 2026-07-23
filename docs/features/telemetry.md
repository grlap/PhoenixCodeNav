# Semantic Operation Telemetry (epuc.1, epuc.3, epuc.4, epuc.5, epuc.10)

Beads: `PhoenixCodeNav-epuc.1`, `PhoenixCodeNav-epuc.3`, `PhoenixCodeNav-epuc.4`, `PhoenixCodeNav-epuc.5`, `PhoenixCodeNav-epuc.10` · Consumed by: [`../internal-operations-portal.md`](../internal-operations-portal.md) (x5ls, design-frozen)

Phoenix writes one JSONL record per semantic operation to a bounded, privacy-safe,
per-process file. This is the data layer the operations portal renders later; it is useful
today by reading the file directly.

## Where

```
{workspace}/.codenav/telemetry/phoenix-{pid}-{utcstart}-{seq}.jsonl
```

One file per `TelemetryLog` instance (`{seq}` uniquifies two managers opened in the same
second). Files older than 7 days are deleted best-effort at startup. The file is size-capped
at 16 MiB and ends with a `telemetry_truncated` record when the cap is reached (the in-memory
ring keeps rolling regardless).

**Reader contract:** the writer holds the file open with `FileShare.Read`. Live readers MUST
open with `FileShare.ReadWrite` (a plain `File.ReadAllText` is refused on Windows while the
process is alive).

## Record: `semanticOp`

```json
{"e":"semanticOp","ts":"2026-07-13T23:49:12.482Z","corr":"a1b2c3d4",
 "tool":"references","accessMode":"writer","result":"exact","clusterLoadMs":15400,
 "clusterLoadProcessWideCpuMs":18920.4,"queryMs":47642,"cold":true,
 "ownerLoad":{"gateWaitMs":0.2,"fingerprintMs":1.5,"topoMs":48.0,"projectLoadMs":8917.3,
              "planMs":51.0,"preparationMs":8800.0,"preparationQueueMs":12.0,
              "preparedProjects":4,"committedProjects":4,"effectiveProjectConcurrency":4,
              "admittedBytesHighWater":18350080,"retainedBytes":12582912,
              "retainedInputBytes":12582912,
              "residentProjects":4,"evictedProjects":0,"evictedInputBytes":0,
              "managedHeapBytes":73400320,
              "replanCount":0,"totalElapsedMs":8968.5,
              "loadedBefore":0,"requested":4,"reloaded":0,"loaded":4,"failed":0},
 "scanLoad":{"gateWaitMs":0.1,"fingerprintMs":3.9,"topoMs":95.2,"projectLoadMs":6120.4,
             "planMs":101.0,"preparationMs":5980.0,"preparationQueueMs":46.0,
             "preparedProjects":17,"committedProjects":17,"effectiveProjectConcurrency":8,
             "admittedBytesHighWater":62914560,"retainedBytes":52428800,
             "retainedInputBytes":52428800,
             "residentProjects":21,"evictedProjects":0,"evictedInputBytes":0,
             "managedHeapBytes":167772160,
             "replanCount":0,"totalElapsedMs":6220.1,
             "loadedBefore":4,"requested":21,"reloaded":0,"loaded":21,"failed":0}}
```

`implementations` and `type_hierarchy` add a privacy-safe planning block after type
resolution. `references` emits the same `seedDiscovery`/`scanSet` shape (with
`mode:"directCandidates"` for type targets) but no `implementationClosure`:

```json
"planning":{
  "implementationClosure":{"totalMs":12.4,"dbQueryAndMapMs":8.1,
    "managedFilterMs":0,"otherMs":4.3,"dbQueries":9,"rowsReturned":8,
    "frontierExpansions":9,"matches":8,"capped":false},
  "seedDiscovery":{"mode":"closureOwners","totalMs":11.4,"inputs":8,"projects":19},
  "scanSet":{"totalMs":83.6,"dependentGraphMs":15.1,"candidateDiscoveryMs":42.7,
    "dependencyGraphMs":13.2,"otherMs":12.6,"seedProjects":19,
    "candidateProjects":37,"selectedProjects":37,"scanProjects":41,
    "skippedProjects":0,"outOfGraphProjects":0,"unsupportedLanguageProjects":0}}
```

`references` also owns the post-resolution query split that field evidence identified as
the remaining dominant wall:

```json
"queryStages":{"path":"symbol_finder",
  "compilationPreparation":{"totalMs":2410.7,"processWideCpuMs":5980.2,"queueMs":386.2,
    "busySumMs":8234.5,"maxProjectBusyMs":712.1,
    "waveMaxSumMs":1924.6,"criticalPathMs":1540.3,
    "requestedProjects":41,"graphProjects":41,"cacheHits":1,"preparedProjects":40,
    "failedProjects":0,"skippedProjects":0,"unfinishedProjects":0,"waves":7,
    "laneLimit":8,"processorCount":8,"effectiveConcurrency":8},
  "findReferencesMs":6823.4,
  "postProcessMs":947.6,"syntaxRootLoadMs":621.4,"classificationMs":42.8,
  "sampleTextMs":18.2,"postProcessOtherMs":265.2,"otherMs":186.3,
  "referencedSymbols":4,"rawLocations":531,"sourceLocations":525,
  "uniqueSyntaxTrees":194,"uniqueSites":525,"samplesRead":159}
```

The numbers above illustrate the shape; they are not a benchmark baseline.

Fields that are `null` are omitted from the JSON entirely (`reason` on success, `scanLoad`
on single-phase tools, `cold` when not cold, both load blocks when the op failed before any
load ran).

| Field | Meaning |
|---|---|
| `ts` | UTC, `yyyy-MM-ddTHH:mm:ss.fffZ` |
| `tool` | `references` \| `implementations` \| `type_hierarchy` \| `definition` \| `callers` \| `callees` |
| `accessMode` | `writer` \| `follower` \| `unattached`; compare planning samples within the same mode |
| `result` | `exact` (success) \| `degraded` (deadline died: see `reason`) \| `unresolved` (position/symbol didn't resolve; see `reason`) \| `error` |
| `reason` | Stable primary cause, including `cluster_cold_load`, `semantic_timeout`, `project_load_failed`, `index_snapshot_unavailable`, symbol-resolution causes, or an exception type name. A `semantic_timeout` with `queryStages.compilationPreparation.unfinishedProjects > 0` expired during eager preparation |
| `clusterLoadMs` | the op's LOAD+RESOLVE wall (all phases through symbol resolution) — restored after a field regression hid a 48s query behind load-only telemetry |
| `clusterLoadProcessWideCpuMs` | `references` only: process-wide CPU consumed while `clusterLoadMs` was open. It includes GC, runtime, and every concurrent MCP thread; it is diagnostic attribution, never elapsed duration |
| `queryMs` | the op's FIND wall after scan-set planning/loading/resolution (compilation preparation, SymbolFinder, and result processing). Null when the op died during load |
| `cold` | present+true when phase 1 found zero projects already loaded — the workspace was cold before this op |
| `ownerLoad` | stage split of phase 1: loading the owning project's dependency closure (all six tools) |
| `scanLoad` | stage split of phase 2: loading the dependent scan set (`references`/`implementations`/`callers`/`type_hierarchy` only) |
| `planning` | pre-load seed and scan-set attribution for `references`/`implementations`/`type_hierarchy`, plus implementation closure for the latter two; omitted before the relevant planning runs |
| `planning.implementationClosure.totalMs` | complete transitive implementation-closure wall time |
| `planning.implementationClosure.dbQueryAndMapMs` | SQLite command execution plus returned-row materialization; this is deliberately not labeled pure engine time |
| `planning.implementationClosure.managedFilterMs/otherMs` | compatibility filter bucket (zero on schema v18's exact normalized edge lookup), then the remaining frontier/deduplication bookkeeping |
| `planning.implementationClosure.dbQueries/rowsReturned/frontierExpansions/matches/capped` | bounded closure work volume and completeness; no names or paths |
| `planning.seedDiscovery` | maps closure rows to declaring projects (`closureOwners`), expands a capped walk to the complete graph-valid dependent set (`dependentClosure`), or records direct candidate discovery (`directCandidates`); `inputs` is present only for closure-owner mapping |
| `planning.scanSet.totalMs` | complete scan-set planning wall before `EnsureLoadedAsync` |
| `planning.scanSet.dependentGraphMs/candidateDiscoveryMs/dependencyGraphMs/otherMs` | dependent-graph query+walk, text-candidate discovery, mandatory dependency-graph query+walk, and remaining selection/set bookkeeping |
| `planning.scanSet.*Projects` | privacy-safe input/output counts for seed, candidate, selected, final scan, skipped, out-of-graph, and unsupported-language project sets |
| `queryStages` | Per-tool post-resolution attribution discriminated by `path`: `symbol_finder` (`references`), `closure_verified`, or `exhaustive_fallback` (`implementations`/`type_hierarchy`). Emitted on exact/partial success and on post-load degraded/error records when query wall is available |
| `queryStages.compilationPreparation` | `references` only: dependency-first eager preparation on the same pinned `Solution` later passed to SymbolFinder; omitted if the operation never reached preparation |
| `queryStages.compilationPreparation.totalMs/queueMs` | preparation wall and summed project time queued for the shared process-wide project lanes; summed queue time may exceed wall time |
| `queryStages.compilationPreparation.processWideCpuMs` | process-wide CPU between the exact preparation entry/exit brackets, including GC/runtime/concurrent work. Compare with `totalMs` and `busySumMs`; do not interpret it as a duration or as CPU owned exclusively by the query |
| `queryStages.compilationPreparation.busySumMs/maxProjectBusyMs` | sum of slot-held compilation-call wall across projects, and its single-project maximum. `busySumMs / totalMs` is the observed work parallelism; cache hits and lane wait are excluded |
| `queryStages.compilationPreparation.waveMaxSumMs/criticalPathMs` | measured scheduling floors over the same project busy times: the sum of the slowest project in each current barrier wave, and the longest weighted dependency path. A lane-aware ready-queue floor is `max(criticalPathMs, busySumMs / laneLimit)`; subtract it from `waveMaxSumMs` to estimate recoverable barrier wall. When `unfinishedProjects > 0`, all four busy-work scalars are lower bounds because unmeasured projects contribute zero |
| `queryStages.compilationPreparation.requestedProjects/graphProjects` | successfully loaded projects requested by this operation, narrowed to the owner and its graph dependents when available, then closed over actual Roslyn project dependencies; unrelated warm resident projects are excluded |
| `queryStages.compilationPreparation.cacheHits/preparedProjects/failedProjects/skippedProjects/unfinishedProjects` | terminal work counts. Failures do not change the search contract: SymbolFinder remains authoritative. `unfinishedProjects` is nonzero when cancellation stops preparation |
| `queryStages.compilationPreparation.waves/laneLimit/processorCount/effectiveConcurrency` | dependency waves, configured process-wide preparation cap, raw `Environment.ProcessorCount`, and this operation's observed concurrent compilation high-water |
| `queryStages.documentScope` | `references` only: exact document-scope planning after compilation preparation; always emitted once reached, including full-solution fallbacks |
| `queryStages.documentScope.mode/reason/candidateSource` | `documentScoped`, `fullSolution`, or `notCompleted`; a stable decision reason (`eligible`, `ineligible_kind`, `forced_full_solution`, `unsupported_language`, `no_documents`, `no_candidates`, `no_reduction`, `planning_error`, or `cancelled`); and `leasedSolutionText` as the exact live-snapshot candidate authority |
| `queryStages.documentScope.totalMs/cacheHit` | cached leased-solution text scan plus conservative global-alias inspection, separate from Roslyn finding; repeated queries on the same immutable `Solution` reuse the exact scope and report `cacheHit:true` |
| `queryStages.documentScope.solutionDocuments/candidateDocuments/scopedDocuments/scopedProjects/documentsInScopedProjects/aliasWidenedProjects/transformedIncludedDocuments` | privacy-safe scope volume. Document/project counts are omitted on early full-solution fallbacks that do not enumerate the complete regular-plus-generated universe. Candidate documents contain a case-exact, identifier-bounded candidate name or a lexical transformation Roslyn can decode into one: complete C# `\\u`/`\\U`/`\\x` escapes, numeric XML entities, or Unicode `Format` scalars; scoped documents also include whole projects widened for global using aliases. `documentsInScopedProjects` is the project-wide named-type global-alias index census, not the binding scope |
| `queryStages.findReferencesMs/postProcessMs/otherMs` | Roslyn `SymbolFinder.FindReferencesAsync` wall, complete filtering/counting/sampling wall, and remaining response/coverage shaping residue; together with `compilationPreparation.totalMs` and `documentScope.totalMs` explain `queryMs` subject to 0.1 ms rounding |
| `queryStages.syntaxRootLoadMs/classificationMs/sampleTextMs/postProcessOtherMs` | nested subsets of `postProcessMs`: syntax-root fetches, usage-kind classification, sampled line fetches, and remaining filter/dedup/group bookkeeping |
| `queryStages.referencedSymbols/rawLocations/sourceLocations` | returned Roslyn work volume before Phoenix filtering; scalar counts only |
| `queryStages.uniqueSyntaxTrees/uniqueSites/samplesRead` | post-filter trees visited, deduplicated physical sites, and sample lines fetched; scalar counts only |
| `*.gateWaitMs` | cumulative time queued for the brief plan/commit workspace-mutation sections |
| `*.fingerprintMs` | warm-set freshness check |
| `*.topoMs` | dependency-closure discovery (index queries + topo order) |
| `*.projectLoadMs` | compatibility aggregate: preparation wall plus ordered commit/apply wall |
| `*.planMs` | pinned-snapshot project/graph/file planning wall |
| `*.preparationMs` | gate-free bounded parallel preparation wall, including cancellable pinned-index text fallback after live reads join |
| `*.preparationQueueMs` | summed project time queued for the process-wide descriptor/project lanes; it can exceed wall time |
| `*.projectParseMs/sourceReadMs/metadataResolveMs` | summed worker phase durations; parallel durations may overlap and can exceed preparation wall |
| `*.workspaceMutationMs` | ordered `ProjectInfo` construction, reference wiring, and the single Roslyn apply |
| `*.preparedProjects/committedProjects/effectiveProjectConcurrency` | preparation volume, successfully published projects, and the effective process-wide project concurrency cap |
| `*.admittedBytesHighWater/retainedBytes/retainedInputBytes` | process-wide accounted-input high-water and current retained-input ownership; `retainedBytes` is the compatibility name and `retainedInputBytes` makes the unit explicit; observational only, never a candidate-project completeness gate. Lease disposal can make this gauge settle down/up between owner and scan blocks without any project eviction |
| `*.residentProjects` | projects resident in this semantic workspace after the load's retention pass |
| `*.evictedProjects/evictedInputBytes` | safe LRU projects removed by this load and the process-wide accounted bytes their released ownership actually reclaimed |
| `*.evictionReason` | present when a pressure pass was requested: `pressure_inputs`, `pressure_heap_soft`, `pressure_heap_hard`, or `no_safe_candidates`; the last means every resident was requested, referenced, concurrently active, or dependency-protected |
| `*.managedHeapBytes` | observational managed-heap sample used by the retention pressure decision; it is not a candidate completeness gate |
| `*.replanCount/totalElapsedMs` | stale-plan retries and complete per-call wall time |
| `*.loadedBefore/requested/reloaded/loaded/failed` | warm-set size before this load, and this load's compatibility work volume |

The load blocks are **per-call**: each operation's record carries the splits of the loads
that operation itself ran, filled even when the load died mid-flight (a `cluster_cold_load`
record shows how far the load got — the phase that was running absorbs the remaining wall,
phases never entered report 0). Concurrent operations cannot contaminate each other's stats.

One special shape remains: a load cancelled before it acquires its first brief workspace gate
reports `gateWaitMs` as the whole wall, all other phase times 0, and omits `loadedBefore`.
Absent `loadedBefore` means unknown, not 0, and such records carry no `cold` flag.

Any actual eviction creates a new Roslyn `Solution`; a later operation therefore performs one
new document-scope scan even when its selected projects survived. Since v0.12.21 that scan is
buffered and cheap. No-pressure steady state performs no eviction and retains the same `Solution`
and scope cache. Deferred owner-phase pressure is completed from the operation's terminal path even
when resolution, planning, or scan loading fails.

Since v0.12.16, `references` forces the operation-selected scan set's compilations before
`FindReferencesAsync`. The same immutable `Solution` instance is used for preparation and search,
so Roslyn's `CompilationTracker` reuses the work; dependencies finish before their dependents and
ready siblings share one bounded process-wide lane. Unrelated projects resident from earlier calls
are deliberately not prepared. If SymbolFinder needs one of those, its residual lazy work remains
honestly inside `findReferencesMs`.

Since v0.12.17, eligible reference searches narrow Roslyn to a conservative document superset
derived from the exact leased `Solution` text already materialized by compilation preparation.
Committed FTS is deliberately not the authority because follower pending state and live edits can
diverge from the pinned index. FTS remains the project-discovery authority, while exact leased text
is the document-narrowing authority. The planner retains every document containing the case-exact,
identifier-bounded candidate name or a conservative Roslyn `ValueText` transformation hazard,
scans source-generated documents, and widens a whole project when a candidate document declares a global using alias. Unsafe symbol
kinds and every uncertain/error case silently retain the existing full-solution search.
`documentScope` makes both the exclusion ratio and the fallback reason visible without exposing
names, text, or paths. `scopedProjects` is the number of Roslyn projects containing the final
document set; `documentsInScopedProjects` is the complete regular-plus-generated document census
inside those projects. For named types, Roslyn proves global-alias completeness across that latter
project-wide census before honoring its document filter, so it is the cold syntax-index work
predictor while `scopedDocuments` describes the later binding scope.

Since v0.12.21, this live-text pass scans large `SourceText` instances through pooled bounded
windows with a streaming, boundary-exact `ValueText` hazard detector. It also avoids materializing
a candidate document's syntax root unless the raw text contains the unordered case-sensitive
`global`, `using`, and `=` spellings required by a global using alias. The check is deliberately
substring-conservative; the syntax tree still decides whether an alias exists and whether the
project must widen.

Since v0.12.18, the semantic `AdhocWorkspace` has deterministic storage-only solution, project,
and document identities plus a stable synthetic solution path. This enables Roslyn's shipped
SQLite persistent storage to reuse checksum-validated `SyntaxTreeIndex` data across MCP processes.
The synthetic path is never opened and grants no solution/build authority; Phoenix continues to
derive project, source, reference, and live-text truth from its existing index/workspace model.
Changed source bytes and parse options retain Roslyn's normal checksum invalidation.

Since v0.12.19, preparation records measured compilation-work floors without changing the
scheduler. `waveMaxSumMs - max(criticalPathMs, busySumMs / laneLimit)` is the lane-aware ready-queue
headroom on a completed capture: a gap below roughly 15% of `totalMs` is treated as structure-bound,
while a larger gap justifies testing a completion-driven scheduler against that floor. A wave wider
than the lane limit can make `totalMs` exceed `waveMaxSumMs`; that is expected lane serialization,
not unexplained residue. This is a decision signal, not a promise that scheduling overhead can
reach the theoretical floor.

Since v0.12.23, `references` also records process-wide CPU for two deliberately broad brackets:
the exact `compilationPreparation` call and the complete `clusterLoad` interval. CPU can exceed
wall time and can include unrelated work. A comparable capture therefore uses an otherwise idle
host, waits for `refresh_sweep_pending` to clear, and runs one semantic operation at a time.
`clusterLoadProcessWideCpuMs` covers the complete load+resolve interval through scan-set
resolution, including intervening planning; `compilationPreparation.processWideCpuMs` covers its
later preparation phase only, so the two brackets do not overlap. A missing CPU field means the
process counter was unavailable; `0.0` means a measured near-zero interval.
`PhoenixCodeNav-Semantic` EventSource emits `PhaseStart`/`PhaseStop` markers for `ownerLoad`,
`scanLoad`, `compilationPreparation`, `documentScope`, and `findReferences`. Each marker carries
the same privacy-safe operation id published as `semanticOp.corr`, so EventPipe samples can be
joined to the JSON record without exposing symbols or paths. A phase is marked only when its
start occurs while the provider is enabled; attaching mid-phase misses that phase, while detaching
mid-phase can omit its stop marker.

`dbQueryAndMapMs` was the decision field for `epuc.3`. Field captures showed roughly
600–700 ms per frontier query even when only 5–322 rows were returned, and an identical warm
re-hit repeated the same cost. Schema v18 therefore replaces the leading-wildcard symbol scan
and per-row signature reparse with an indexed normalized edge lookup. On v18,
`managedFilterMs` is retained for telemetry compatibility but is always zero; use
`dbQueryAndMapMs`, query/frontier counts, and the seed/scan split to verify the closure has
collapsed and to locate any remaining planning cost. Measure writer and follower processes
separately.

Auxiliary records: `telemetry_dropped` (backpressure dropped N oldest queued records — the
channel never blocks a request) and `telemetry_truncated` (file cap reached).

## Privacy posture (matches the portal spec)

No source code, no query arguments, no symbol names or payloads, no absolute or relative
paths. Correlation ids are random per operation. Pinned by
`Batch51TelemetryTests.SemanticOperationEmitsBoundedPrivacySafeTelemetry`, its unguarded
references query-stage wire-shape test, and the planning canaries in
`Batch57TransitiveImplementationsTests.DeepChainAndInterfaceHopImplementersAreAllFound`.

## Guarantees

- Every semantic operation emits exactly one `semanticOp` record — success, unresolved,
  degraded, or error.
- Emission never blocks or throws into a request path (bounded channel, drop-oldest,
  disclosed in-band; I/O failure disables the file quietly and logs once).
- Bounded everywhere: 1024-record queue, 256-record in-memory ring
  (`TelemetryLog.Snapshot()` — the portal's future IPC source), 16 MiB file
  (pinned by `Batch51TelemetryTests.FileCapTruncatesHonestlyWhileRingKeepsRolling`).
- One `TelemetryLog` per `IndexManager`; disposed with it (2 s flush cap).
