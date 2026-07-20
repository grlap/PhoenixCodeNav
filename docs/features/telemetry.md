# Semantic Operation Telemetry (epuc.1, epuc.3)

Beads: `PhoenixCodeNav-epuc.1`, `PhoenixCodeNav-epuc.3` · Consumed by: [`../internal-operations-portal.md`](../internal-operations-portal.md) (x5ls, design-frozen)

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
 "tool":"references","accessMode":"writer","result":"exact","clusterLoadMs":15400,"queryMs":47642,"cold":true,
 "ownerLoad":{"gateWaitMs":0.2,"fingerprintMs":1.5,"topoMs":48.0,"projectLoadMs":8917.3,
              "planMs":51.0,"preparationMs":8800.0,"preparationQueueMs":12.0,
              "preparedProjects":4,"committedProjects":4,"effectiveProjectConcurrency":4,
              "admittedBytesHighWater":18350080,"retainedBytes":12582912,
              "replanCount":0,"totalElapsedMs":8968.5,
              "loadedBefore":0,"requested":4,"reloaded":0,"loaded":4,"failed":0},
 "scanLoad":{"gateWaitMs":0.1,"fingerprintMs":3.9,"topoMs":95.2,"projectLoadMs":6120.4,
             "planMs":101.0,"preparationMs":5980.0,"preparationQueueMs":46.0,
             "preparedProjects":17,"committedProjects":17,"effectiveProjectConcurrency":8,
             "admittedBytesHighWater":62914560,"retainedBytes":52428800,
             "replanCount":0,"totalElapsedMs":6220.1,
             "loadedBefore":4,"requested":21,"reloaded":0,"loaded":21,"failed":0}}
```

`implementations` and `type_hierarchy` add a privacy-safe planning block after type
resolution:

```json
"planning":{
  "implementationClosure":{"totalMs":5534.2,"dbQueryAndMapMs":5410.8,
    "managedFilterMs":91.3,"otherMs":32.1,"dbQueries":147,"rowsReturned":826,
    "frontierExpansions":147,"matches":146,"capped":false},
  "seedDiscovery":{"mode":"closureOwners","totalMs":211.4,"inputs":146,"projects":19},
  "scanSet":{"totalMs":83.6,"dependentGraphMs":15.1,"candidateDiscoveryMs":42.7,
    "dependencyGraphMs":13.2,"otherMs":12.6,"seedProjects":19,
    "candidateProjects":37,"selectedProjects":37,"scanProjects":41,
    "skippedProjects":0,"outOfGraphProjects":0,"unsupportedLanguageProjects":0}}
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
| `reason` | Stable primary cause, including `cluster_cold_load`, `semantic_timeout`, `project_load_failed`, `index_snapshot_unavailable`, symbol-resolution causes, or an exception type name |
| `clusterLoadMs` | the op's LOAD+RESOLVE wall (all phases through symbol resolution) — restored after a field regression hid a 48s query behind load-only telemetry |
| `queryMs` | the op's FIND wall (SymbolFinder/scan/count after resolution; includes lazy Roslyn compilation on cold ops — the v1 caveat below). Null when the op died during load |
| `cold` | present+true when phase 1 found zero projects already loaded — the workspace was cold before this op |
| `ownerLoad` | stage split of phase 1: loading the owning project's dependency closure (all six tools) |
| `scanLoad` | stage split of phase 2: loading the dependent scan set (`references`/`implementations`/`callers`/`type_hierarchy` only) |
| `planning` | pre-load closure, seed, and scan-set attribution for `implementations`/`type_hierarchy`; omitted before type resolution and on tools that do not run the transitive implementation closure |
| `planning.implementationClosure.totalMs` | complete transitive implementation-closure wall time |
| `planning.implementationClosure.dbQueryAndMapMs` | SQLite command execution plus returned-row materialization; this is deliberately not labeled pure engine time |
| `planning.implementationClosure.managedFilterMs/otherMs` | exact whole-token/arity filtering, then the remaining frontier/deduplication bookkeeping |
| `planning.implementationClosure.dbQueries/rowsReturned/frontierExpansions/matches/capped` | bounded closure work volume and completeness; no names or paths |
| `planning.seedDiscovery` | maps closure rows to declaring projects (`closureOwners`) or records the capped/direct candidate fallback; `inputs` is present only for closure-owner mapping |
| `planning.scanSet.totalMs` | complete scan-set planning wall before `EnsureLoadedAsync` |
| `planning.scanSet.dependentGraphMs/candidateDiscoveryMs/dependencyGraphMs/otherMs` | dependent-graph query+walk, text-candidate discovery, mandatory dependency-graph query+walk, and remaining selection/set bookkeeping |
| `planning.scanSet.*Projects` | privacy-safe input/output counts for seed, candidate, selected, final scan, skipped, out-of-graph, and unsupported-language project sets |
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
| `*.admittedBytesHighWater/retainedBytes` | process-wide accounted-input high-water and current retained-byte ownership; observational only, never a candidate-project completeness gate |
| `*.replanCount/totalElapsedMs` | stale-plan retries and complete per-call wall time |
| `*.loadedBefore/requested/reloaded/loaded/failed` | warm-set size before this load, and this load's compatibility work volume |

The load blocks are **per-call**: each operation's record carries the splits of the loads
that operation itself ran, filled even when the load died mid-flight (a `cluster_cold_load`
record shows how far the load got — the phase that was running absorbs the remaining wall,
phases never entered report 0). Concurrent operations cannot contaminate each other's stats.

One special shape remains: a load cancelled before it acquires its first brief workspace gate
reports `gateWaitMs` as the whole wall, all other phase times 0, and omits `loadedBefore`.
Absent `loadedBefore` means unknown, not 0, and such records carry no `cold` flag.

Known v1 attribution caveat: Roslyn compiles lazily inside the find stage, so on a cold op
the find/query time (outside the load blocks) includes compilation of the scan set; the
cold-vs-warm delta attributes it numerically. Splitting compile out is the flagged v2 item.

`dbQueryAndMapMs` is the decision field for `epuc.3`: if it dominates closure `totalMs`,
optimize or cache the SQLite-backed closure lookup; if `managedFilterMs` dominates, optimize
the exact syntax/arity filter; if neither dominates, use the reported query/frontier/row counts
to target repeated BFS or seed/scan planning. Measure writer and follower processes separately.

Auxiliary records: `telemetry_dropped` (backpressure dropped N oldest queued records — the
channel never blocks a request) and `telemetry_truncated` (file cap reached).

## Privacy posture (matches the portal spec)

No source code, no query arguments, no symbol names or payloads, no absolute or relative
paths. Correlation ids are random per operation. Pinned by
`Batch51TelemetryTests.SemanticOperationEmitsBoundedPrivacySafeTelemetry` and the planning
canaries in `Batch57TransitiveImplementationsTests.DeepChainAndInterfaceHopImplementersAreAllFound`.

## Guarantees

- Every semantic operation emits exactly one `semanticOp` record — success, unresolved,
  degraded, or error.
- Emission never blocks or throws into a request path (bounded channel, drop-oldest,
  disclosed in-band; I/O failure disables the file quietly and logs once).
- Bounded everywhere: 1024-record queue, 256-record in-memory ring
  (`TelemetryLog.Snapshot()` — the portal's future IPC source), 16 MiB file
  (pinned by `Batch51TelemetryTests.FileCapTruncatesHonestlyWhileRingKeepsRolling`).
- One `TelemetryLog` per `IndexManager`; disposed with it (2 s flush cap).
