# Semantic Operation Telemetry (epuc.1)

Bead: `PhoenixCodeNav-epuc.1` · Consumed by: [`../internal-operations-portal.md`](../internal-operations-portal.md) (x5ls, design-frozen)

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
 "tool":"references","result":"exact","cold":true,
 "ownerLoad":{"gateWaitMs":0.2,"fingerprintMs":1.5,"topoMs":48.0,"projectLoadMs":8917.3,
              "loadedBefore":0,"requested":4,"reloaded":0,"loaded":4,"failed":0},
 "scanLoad":{"gateWaitMs":0.1,"fingerprintMs":3.9,"topoMs":95.2,"projectLoadMs":6120.4,
             "loadedBefore":4,"requested":21,"reloaded":0,"loaded":21,"failed":0}}
```

Fields that are `null` are omitted from the JSON entirely (`reason` on success, `scanLoad`
on single-phase tools, `cold` when not cold, both load blocks when the op failed before any
load ran).

| Field | Meaning |
|---|---|
| `ts` | UTC, `yyyy-MM-ddTHH:mm:ss.fffZ` |
| `tool` | `references` \| `implementations` \| `type_hierarchy` \| `definition` \| `callers` \| `callees` |
| `result` | `exact` (success) \| `degraded` (deadline died: see `reason`) \| `unresolved` (position/symbol didn't resolve; see `reason`) \| `error` |
| `reason` | `cluster_cold_load` \| `semantic_timeout` \| `index_snapshot_unavailable` \| `symbol_not_resolved` \| `symbol_not_resolved_in_scope` \| `not_a_type` \| exception type name |
| `clusterLoadMs` | the op's LOAD+RESOLVE wall (all phases through symbol resolution) — restored after a field regression hid a 48s query behind load-only telemetry |
| `queryMs` | the op's FIND wall (SymbolFinder/scan/count after resolution; includes lazy Roslyn compilation on cold ops — the v1 caveat below). Null when the op died during load |
| `cold` | present+true when phase 1 found zero projects already loaded — the workspace was cold before this op |
| `ownerLoad` | stage split of phase 1: loading the owning project's dependency closure (all six tools) |
| `scanLoad` | stage split of phase 2: loading the dependent scan set (`references`/`implementations`/`callers`/`type_hierarchy` only) |
| `*.gateWaitMs` | time queued behind another caller's load — the duplicate-concurrent-load signal |
| `*.fingerprintMs` | warm-set freshness check |
| `*.topoMs` | dependency-closure discovery (index queries + topo order) |
| `*.projectLoadMs` | project parse + source reads + metadata refs + workspace mutation |
| `*.loadedBefore/requested/reloaded/loaded/failed` | warm-set size before this load, and this load's work volume |

The load blocks are **per-call**: each operation's record carries the splits of the loads
that operation itself ran, filled even when the load died mid-flight (a `cluster_cold_load`
record shows how far the load got — the phase that was running absorbs the remaining wall,
phases never entered report 0). Concurrent operations cannot contaminate each other's stats.

One special shape: a load that died while **queued for the workspace gate** (cold workspace,
two parallel ops — the primary contention case) reports `gateWaitMs` as the whole wall, all
other phase times 0, and **omits `loadedBefore`** — the warm-set size is guarded by the gate
and cannot be read honestly from outside it. Absent `loadedBefore` means unknown, not 0, and
such records carry no `cold` flag.

Known v1 attribution caveat: Roslyn compiles lazily inside the find stage, so on a cold op
the find/query time (outside the load blocks) includes compilation of the scan set; the
cold-vs-warm delta attributes it numerically. Splitting compile out is the flagged v2 item.

Auxiliary records: `telemetry_dropped` (backpressure dropped N oldest queued records — the
channel never blocks a request) and `telemetry_truncated` (file cap reached).

## Privacy posture (matches the portal spec)

No source code, no query arguments, no symbol names or payloads, no absolute or relative
paths. Correlation ids are random per operation. Pinned by
`Batch51TelemetryTests.SemanticOperationEmitsBoundedPrivacySafeTelemetry`.

## Guarantees

- Every semantic operation emits exactly one `semanticOp` record — success, unresolved,
  degraded, or error.
- Emission never blocks or throws into a request path (bounded channel, drop-oldest,
  disclosed in-band; I/O failure disables the file quietly and logs once).
- Bounded everywhere: 1024-record queue, 256-record in-memory ring
  (`TelemetryLog.Snapshot()` — the portal's future IPC source), 16 MiB file
  (pinned by `Batch51TelemetryTests.FileCapTruncatesHonestlyWhileRingKeepsRolling`).
- One `TelemetryLog` per `IndexManager`; disposed with it (2 s flush cap).
