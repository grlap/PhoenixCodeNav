# Phoenix Telemetry API v1

Status: **Draft contract — design only; implementation is not authorized**

Feature spec: [`internal-operations-portal.md`](./internal-operations-portal.md)

This document is the integration boundary between two independently developed projects:

| Project | Owner | Responsibilities |
|---|---|---|
| **Phoenix telemetry producer** | indexing/search agent | Instrument Phoenix, create privacy-safe snapshots/events, and publish them through bounded local IPC. |
| **Phoenix Operations Portal** | portal/UI agent | Accept many producer connections, group instances, retain/aggregate bounded data, expose the local HTTP/SSE API, and implement the complete website. |

The projects share this protocol, not implementation assemblies. The portal must never reference
`IndexManager`, `IndexStore`, `IndexQueries`, `SemanticWorkspace`, or the MCP tool classes. The
producer must not serve HTTP, ship UI assets, or implement portal retention and charts.

## Contract principles

- **Local only:** producer-to-portal traffic uses current-user-only OS IPC, never TCP.
- **Non-blocking:** telemetry can be dropped; it can never delay indexing or an MCP response.
- **Bounded:** frames, queues, batches, strings, arrays, retention, and reconnect work have limits.
- **Redacted at source:** sensitive data is removed before it leaves the MCP process.
- **Read-only:** portal protocol messages cannot request index, cache, process, Git, or workspace
  mutation.
- **Versioned:** both sides negotiate a major protocol version and tolerate additive fields.
- **Honest:** absent/unknown measurements remain absent or `null`; they are never reported as zero.
- **Instance-aware:** every record identifies one MCP process; workspace and physical-index ids
  allow safe grouping without revealing absolute paths.
- **Testable independently:** canonical JSON fixtures let the portal run before a real producer and
  let the producer validate output without the website.

## Architecture and ownership

```text
Project A: Phoenix runtime

IndexManager / IndexBuilder / IndexQueries / NavigationTools / SemanticService
                              |
                     Telemetry producers
                              |
                 bounded in-process channel
                              |
                 local IPC publisher client
                              |
------------------------ contract boundary ------------------------
                              |
Project B: Phoenix Operations Portal

                  local IPC broker/server
                              |
              validation + grouping + retention
                              |
              read-only HTTP API + SSE stream
                              |
                    TypeScript website
```

Project A owns the meaning and correct measurement of producer fields. Project B owns presentation,
cross-instance aggregation, time-series rollups, paging, browser security, and UI behavior.

## Transport

### Endpoint

- Windows: a named pipe restricted to the current user SID.
- Linux/macOS: a Unix-domain socket under the user's runtime directory with owner-only permissions.
- One portal process owns one well-known endpoint per OS user.
- MCP processes connect as clients. They do not create or listen on portal endpoints.

Endpoint names are implementation details but must be derived from a stable product id and current
user identity, not a workspace path.

**V1 normative derivation** (both projects must compute exactly this, or they never meet):

- Windows named pipe: `phoenix.codenav.telemetry.v1.{userSid}` (full path
  `\\.\pipe\phoenix.codenav.telemetry.v1.{userSid}`), where `{userSid}` is the current user's
  SID string (`S-1-5-21-...`). The portal additionally ACLs the pipe to that SID; the
  name-embedded SID only prevents cross-user collision, the ACL enforces access.
- Unix socket: `$XDG_RUNTIME_DIR/phoenix-codenav/telemetry.v1.sock`, falling back to
  `/tmp/phoenix-codenav-{uid}/telemetry.v1.sock`; the containing directory is `0700`.
- Producer hardening: pipe names are squattable by other local users. BOTH sides MUST pass
  `PipeOptions.CurrentUserOnly` — the portal's server rejects foreign clients, and the
  producer's client refuses a server owned by another user (the runtime enforces the owner
  check; no P/Invoke needed). All frames are privacy-redacted regardless, so a squatted
  endpoint would learn nothing sensitive even without the flag.
- Producer enforcement point: identity fields are bound and the privacy/bounds gate runs at
  frame MATERIALIZATION (immediately before the socket write) — the salt that ids need does
  not exist at enqueue time. A frame the gate refuses consumed a sequence, so the producer
  discloses it in-band as `telemetry.dropped` with reason `producer_validation_rejected`;
  the portal must treat that reason exactly like a buffer gap for data-completeness.
- Producer opt-out: setting `PHOENIX_TELEMETRY_IPC=0` in the MCP process environment disables
  the IPC client entirely (no connect attempts). `PHOENIX_CLIENT_LABEL` supplies the optional
  `clientLabel` — this is the "explicit safe configuration value" for it.

### Framing

- UTF-8 compact JSON, one object per line (NDJSON).
- Newlines inside strings are JSON-escaped; producers emit no pretty-printed frames.
- Maximum frame size: **262,144 bytes** in v1.
- Maximum event-batch size: **100 records** and the same frame-size ceiling.
- Readers enforce limits while streaming, before allocating an unbounded line or JSON tree.
- A malformed or oversized frame rejects that connection without affecting other instances.

### Delivery behavior

- The in-process producer queue is bounded and uses a non-blocking `TryWrite`-style operation.
- When full, it drops the oldest non-lifecycle telemetry first and later reports the dropped count.
- IPC writes occur on a dedicated background path and never while holding Phoenix's index writer,
  review snapshot, workspace gate, or response-shaping lock.
- Reconnect uses bounded exponential backoff with jitter.
- On every successful connection/reconnection, the producer sends a fresh full instance snapshot.
- The producer may replay only its bounded retained records. The portal de-duplicates by
  `(portalSessionId, instanceId, sequence)` or stable event id.

## Common frame envelope

Every frame is an object. Producer frames after negotiation use:

```jsonc
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "operation.completed",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 1842,
  "timestampUtc": "2026-07-13T23:30:14.682Z",
  "data": {}
}
```

Rules:

- `protocol` is exactly `phoenix.telemetry`.
- `version` is the negotiated integer major version.
- `type` is a stable lowercase dotted identifier.
- `instanceId` is a random UUID created once per MCP process lifetime.
- `sequence` is an unsigned, strictly increasing integer for that `instanceId`.
- `timestampUtc` is ISO-8601 UTC with millisecond precision.
- `data` is an object whose schema depends on `type`.
- Unknown top-level or `data` fields are ignored and preserved only when explicitly needed for
  forwarding. Required fields cannot change within v1.
- Unknown message types are ignored with a bounded diagnostic event; they do not disconnect an
  otherwise valid v1 producer.

## Connection negotiation

Workspace and index identities are not sent until the portal supplies a per-session salt.

### 1. Producer to portal: `hello`

`hello` is not yet version-negotiated and therefore has no common envelope:

```json
{
  "protocol": "phoenix.telemetry",
  "type": "hello",
  "supportedVersions": [1],
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "mcpVersion": "0.11.8",
  "processId": 38124,
  "processStartUtc": "2026-07-13T23:20:01.118Z",
  "platform": "windows-x64",
  "clientLabel": "VS Code"
}
```

`clientLabel` is optional and must come from an explicit safe configuration value. It is not
derived from a command line or environment dump.

### 2. Portal to producer: `welcome`

```json
{
  "protocol": "phoenix.telemetry",
  "type": "welcome",
  "selectedVersion": 1,
  "portalSessionId": "b9b3035a-03bb-4fad-b85a-686eb1f54d53",
  "identitySaltBase64": "<32 random bytes, base64>",
  "heartbeatIntervalMs": 2000,
  "fullSnapshotIntervalMs": 10000,
  "maxFrameBytes": 262144,
  "maxBatchRecords": 100
}
```

The salt is new for each portal process lifetime and is shared with every accepted producer in that
portal session. It enables consistent grouping without sending canonical paths.

If no version overlaps, the portal sends `reject` and closes:

```json
{
  "protocol": "phoenix.telemetry",
  "type": "reject",
  "code": "protocol_version_unsupported",
  "supportedVersions": [1]
}
```

### 3. Producer identity derivation

After `welcome`, the producer calculates:

```text
workspaceId = base64url(HMAC-SHA256(salt, "workspace\0" + canonicalWorkspaceRoot))
indexId     = base64url(HMAC-SHA256(salt, "index\0" + canonicalDatabaseIdentity))
```

The canonical values never leave the MCP process. The portal-session ids are opaque, stable across
instances for the same underlying value, and intentionally change after a portal restart.

## Producer message types

### `instance.snapshot`

Sent immediately after negotiation, after reconnect, every requested full-snapshot interval, and
after a major lifecycle transition.

```jsonc
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "instance.snapshot",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 1,
  "timestampUtc": "2026-07-13T23:30:00.000Z",
  "data": {
    "workspace": {
      "id": "wa_opaque_base64url",
      "label": "PhoenixCodeNav"
    },
    "index": {
      "id": "ix_opaque_base64url",
      "accessMode": "writer",
      "state": "building",
      "epoch": 18,
      "indexVersion": "14",
      "indexedAtUtc": null,
      "lastRefreshUtc": null,
      "databaseBytes": 48234496,
      "pendingChanges": 0,
      "pendingProcessed": 0,
      "errorCode": null,
      "build": {
        "buildId": "c42f5911-bcbb-4410-a063-b70568806340",
        "phase": "indexing_files",
        "filesIndexed": 41200,
        "filesTotal": 78000,
        "filesSkipped": 0,
        "projectsFailed": 0,
        "elapsedMs": 6280,
        "filesPerSecond": 7021.3,
        "estimatedRemainingMs": 5241
      }
    },
    "semantic": {
      "state": "cold",
      "loadedProjects": 0,
      "loadingProjects": 0,
      "failedProjects": 0,
      "evictedProjects": 0,
      "cacheBytesEstimate": null,
      "activeLoads": 0,
      "sharedLoadWaiters": 0
    },
    "operations": {
      "active": 0,
      "completedSinceStart": 0,
      "failedSinceStart": 0,
      "timedOutSinceStart": 0
    },
    "process": {
      "uptimeMs": 59882,
      "cpuPercent": 12.4,
      "workingSetBytes": 312475648,
      "managedHeapBytes": 108412920,
      "threadCount": 31,
      "gen0Collections": 45,
      "gen1Collections": 8,
      "gen2Collections": 1
    },
    "telemetry": {
      "queuedRecords": 0,
      "droppedRecords": 0,
      "lastPublishedSequence": 0
    }
  }
}
```

Enums:

- `index.accessMode`: `writer | follower | unavailable`
- `index.state`: `missing | building | ready | refreshing | failed`
- `semantic.state`: `cold | warming | warm | degraded | unavailable | unknown`

Rules:

- `workspace.label` is a bounded friendly basename or explicit alias, not a path.
- `epoch` is present only when Phoenix can expose a stable index epoch honestly.
- follower snapshots omit writer-only build/refresh counters they cannot know.
- unknown numeric values are `null` or absent, never fabricated as zero.
- `errorCode` is a stable redacted code; arbitrary exception messages are excluded.

### `heartbeat`

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "heartbeat",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 2,
  "timestampUtc": "2026-07-13T23:30:02.000Z",
  "data": {
    "uptimeMs": 61882,
    "activeOperations": 1
  }
}
```

Heartbeats carry no hidden state and may be coalesced. A full snapshot repairs missed gauge state.

### `index.build.started`

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "index.build.started",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 20,
  "timestampUtc": "2026-07-13T23:30:03.000Z",
  "data": {
    "buildId": "c42f5911-bcbb-4410-a063-b70568806340",
    "indexId": "ix_opaque_base64url",
    "reason": "startup_missing",
    "phase": "scanning"
  }
}
```

`reason`: `startup_missing | startup_incompatible | explicit_full | recovery | unknown`.

### `index.build.progress`

Coalesced to at most four publications per second per build:

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "index.build.progress",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 21,
  "timestampUtc": "2026-07-13T23:30:04.000Z",
  "data": {
    "buildId": "c42f5911-bcbb-4410-a063-b70568806340",
    "indexId": "ix_opaque_base64url",
    "phase": "indexing_files",
    "phaseElapsedMs": 1480,
    "elapsedMs": 3120,
    "filesIndexed": 8400,
    "filesTotal": 78000,
    "filesSkipped": 0,
    "projectsFailed": 0,
    "filesPerSecond": 5675.7,
    "estimatedRemainingMs": 12263,
    "databaseBytes": 16777216
  }
}
```

`phase`: `scanning | parsing_projects | indexing_files | finalizing`.

`filesTotal`, `filesPerSecond`, and `estimatedRemainingMs` remain `null` until their existing
honesty gates are satisfied. The consumer must not derive an ETA from another phase.

### `index.build.completed` and `index.build.failed`

```jsonc
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "index.build.completed",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 25,
  "timestampUtc": "2026-07-13T23:30:12.000Z",
  "data": {
    "buildId": "c42f5911-bcbb-4410-a063-b70568806340",
    "indexId": "ix_opaque_base64url",
    "durationMs": 9000,
    "filesIndexed": 78000,
    "filesSkipped": 0,
    "projectsFailed": 0,
    "databaseBytes": 947912704,
    "phaseDurations": [
      { "phase": "scanning", "durationMs": 620 },
      { "phase": "parsing_projects", "durationMs": 1100 },
      { "phase": "indexing_files", "durationMs": 6910 },
      { "phase": "finalizing", "durationMs": 370 }
    ]
  }
}
```

The failed form replaces completion-only fields with:

```json
{
  "failedPhase": "finalizing",
  "errorCode": "index_publish_failed",
  "retryable": true
}
```

No raw exception message, stack trace, or path is sent.

### `index.refresh.snapshot`

Refresh progress is a movement snapshot rather than a fabricated percentage:

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "index.refresh.snapshot",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 30,
  "timestampUtc": "2026-07-13T23:31:00.000Z",
  "data": {
    "refreshId": "8074be50-8477-4388-89b0-31cc4663c509",
    "indexId": "ix_opaque_base64url",
    "state": "running",
    "reason": "watcher_batch",
    "pendingChanges": 18,
    "pendingProcessed": 244,
    "batchProcessed": 9,
    "elapsedMs": 184,
    "errorCode": null
  }
}
```

`state`: `queued | running | completed | failed | deferred`.

V1 producer `reason` values (additive within v1; render unknowns tolerantly):
`watcher_batch` (file-watcher debounced batch) | `explicit` (refresh_index tool request) |
`git_head` (HEAD-move reconcile, including its diff-unavailable full-sweep fallback and
baseline-record batches) | `full_sweep` (startup/overflow/recovery detect-all pass).
The v1 producer emits one OUTCOME frame per batch (`completed`/`failed`) rather than
running-state frames; a failed frame carries the measured `elapsedMs` and an honest
`batchProcessed: 0` (the refresh transaction rolls back atomically).

### `operation.started`

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "operation.started",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 40,
  "timestampUtc": "2026-07-13T23:32:10.000Z",
  "data": {
    "operationId": "1a3db3c4-111a-40e8-bcd0-7df4b519d2b5",
    "category": "semantic",
    "tool": "implementations",
    "deadlineMs": 15000,
    "indexEpoch": 18,
    "coldState": "cold"
  }
}
```

`category`: `index | search | syntax | semantic | graph | review | lifecycle | other`.

No arguments, symbol names, query strings, paths, line numbers, prompts, or caller content are
included.

### `operation.completed`

One completed record carries the timing waterfall and bounded work counters:

```jsonc
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "operation.completed",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 41,
  "timestampUtc": "2026-07-13T23:32:18.420Z",
  "data": {
    "operationId": "1a3db3c4-111a-40e8-bcd0-7df4b519d2b5",
    "category": "semantic",
    "tool": "implementations",
    "durationMs": 8420,
    "deadlineMs": 15000,
    "outcome": "completed",
    "confidence": "exact",
    "partial": false,
    "stale": false,
    "coldState": "cold",
    "responseBytes": 18240,
    "responseTruncated": false,
    "causeCodes": [],
    "phases": [
      { "name": "snapshot_acquire", "durationMs": 3 },
      { "name": "owner_lookup", "durationMs": 8 },
      { "name": "dependency_closure", "durationMs": 12 },
      { "name": "project_parse", "durationMs": 422 },
      { "name": "source_read", "durationMs": 918 },
      { "name": "workspace_wait", "durationMs": 0 },
      { "name": "workspace_mutation", "durationMs": 605 },
      { "name": "compilation", "durationMs": 3310 },
      { "name": "candidate_discovery", "durationMs": 248 },
      { "name": "expanded_load", "durationMs": 1090 },
      { "name": "symbol_finder", "durationMs": 1632 },
      { "name": "result_shape", "durationMs": 172 }
    ],
    "counters": {
      "projectsRequested": 48,
      "projectsAlreadyLoaded": 0,
      "projectsLoaded": 48,
      "projectsReloaded": 0,
      "projectsFailed": 0,
      "projectsEvicted": 0,
      "sourceFilesRead": 3920,
      "sourceBytesRead": 43881210,
      "sourceCacheHits": 0,
      "sourceCacheMisses": 3920,
      "candidateProjects": 31,
      "graphEdges": 84,
      "databaseBatches": 5,
      "databaseRows": 6281,
      "resultsReturned": 12,
      "sharedLoadWaiters": 0
    }
  }
}
```

`outcome`: `completed | degraded | failed | timed_out | cancelled`.

`confidence`: `exact | indexed | heuristic | unknown`. `partial` and `stale` remain separate
orthogonal facts.

Stable phase names in v1:

- `snapshot_acquire`
- `target_resolve`
- `owner_lookup`
- `dependency_closure`
- `project_parse`
- `source_read`
- `metadata_resolve`
- `workspace_wait`
- `workspace_mutation`
- `syntax_parse`
- `compilation`
- `semantic_model`
- `candidate_discovery`
- `scan_set_select`
- `expanded_load`
- `final_resolve`
- `symbol_finder`
- `database_query`
- `result_shape`
- `other`

The producer may omit non-applicable phases. It must not report overlapping phase durations as an
additive total unless a separate `parallelGroup` field identifies parallel work. The portal shows
the observed waterfall and does not assume phase durations sum exactly to wall-clock duration.

Counters are optional individually. Unknown counters are omitted. V1 counter names are the fields
shown above plus:

- `filesMatched`, `filesScanned`, `ftsRowsScanned`, `symbolsMatched`, `symbolsConsidered`
- `sqliteStatements`, `metadataReferences`, `compileItems`
- `compilationsCreated`, `compilationsReused`, `semanticModelsCreated`
- `queueWaiters`, `resultsConsidered`, `resultsTruncated`

New counters may be added within v1 if they are bounded numeric facts and consumers ignore unknown
names.

For `timed_out`, `cancelled`, `failed`, or `degraded`, include:

```json
{
  "lastCompletedPhase": "candidate_discovery",
  "elapsedAtStopMs": 14987,
  "remainingBudgetMs": 0,
  "causeCodes": ["semantic_timeout"]
}
```

### `diagnostic.event`

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "diagnostic.event",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 50,
  "timestampUtc": "2026-07-13T23:33:00.000Z",
  "data": {
    "eventId": "2bd3812d-fef8-4185-bc18-b97b3cb09d9a",
    "code": "semantic.shared_load_joined",
    "severity": "info",
    "component": "semantic_workspace",
    "correlationId": "1a3db3c4-111a-40e8-bcd0-7df4b519d2b5",
    "fields": {
      "waiterCount": 2,
      "loadedProjectCount": 31
    }
  }
}
```

`severity`: `trace | debug | info | warning | error | critical`.

`fields` accepts only schema-approved booleans, numbers, enums, opaque ids, and bounded redacted
strings. It is not a general logger property bag. Raw logger messages and exception `Message` /
`StackTrace` values are excluded unless a specific event schema defines a redacted field.

### `metric.sample`

Used only for gauges that cannot be reconstructed from lifecycle/operation events:

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "metric.sample",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 60,
  "timestampUtc": "2026-07-13T23:33:02.000Z",
  "data": {
    "values": {
      "process.cpu_percent": 8.1,
      "process.working_set_bytes": 318767104,
      "semantic.loaded_projects": 48,
      "semantic.active_loads": 0,
      "index.pending_changes": 0
    }
  }
}
```

Metric names and units are stable. Counters such as query count are derived by the portal from
operation events rather than repeatedly sampled.

### `telemetry.dropped`

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "telemetry.dropped",
  "instanceId": "298f1a35-7f32-49ef-a579-0ed69d9162cd",
  "sequence": 61,
  "timestampUtc": "2026-07-13T23:33:03.000Z",
  "data": {
    "records": 42,
    "reason": "producer_buffer_full",
    "sinceSequence": 10
  }
}
```

The portal displays telemetry gaps and must not compute false-complete metrics over a known gap.

## Portal control frames

The portal may send protocol-management frames only. They do not mutate Phoenix:

### `resync`

```json
{
  "protocol": "phoenix.telemetry",
  "version": 1,
  "type": "resync",
  "reason": "sequence_gap"
}
```

The producer responds with a fresh `instance.snapshot`. It need not replay evicted events.

### `shutdown_notice`

The portal may announce its own orderly shutdown so producers reconnect with normal backoff. It
does not ask MCP to stop.

No other portal-to-producer command is accepted in v1. Unknown control frames are ignored. Any
future mutation/control plane requires a separate protocol, threat model, user approval, and Bead.

## Portal HTTP API v1

The Operations Portal project owns this API. It is defined now so UI work can use a mock server
while the producer is being implemented.

All diagnostic API calls require the portal session bearer token. The static shell and `/healthz`
are the only unauthenticated resources. CORS is not enabled.

### Common list envelope

```json
{
  "items": [],
  "nextCursor": null,
  "returned": 0,
  "total": null,
  "truncated": false,
  "dataComplete": true
}
```

`total` is `null` unless known without an unbounded count. `dataComplete` becomes false when the
portal observed producer drops, sequence gaps, incompatible frames, or retention eviction relevant
to the requested range.

### Common error envelope

```json
{
  "error": {
    "code": "cursor_expired",
    "message": "The live cursor is outside the retained portal session window.",
    "retryable": true
  }
}
```

Messages are portal-authored bounded text and contain no producer payload echoes.

### `GET /healthz`

Liveness only:

```json
{
  "status": "ok",
  "portalVersion": "0.1.0",
  "apiVersion": 1
}
```

No instance/workspace data and no token requirement.

### `GET /api/v1/bootstrap`

One bounded initial UI snapshot:

```jsonc
{
  "apiVersion": 1,
  "portal": {
    "sessionId": "b9b3035a-03bb-4fad-b85a-686eb1f54d53",
    "version": "0.1.0",
    "startedAtUtc": "2026-07-13T23:29:58.000Z",
    "nowUtc": "2026-07-13T23:34:00.000Z"
  },
  "retention": {
    "operationsPerInstance": 2000,
    "eventsPerInstance": 5000,
    "buildsPerIndex": 20,
    "globalBytes": 67108864
  },
  "summary": {
    "workspaceCount": 2,
    "indexCount": 2,
    "connectedInstanceCount": 3,
    "staleInstanceCount": 0,
    "activeOperationCount": 1,
    "warningCount": 0
  },
  "workspaces": [],
  "indexes": [],
  "instances": [],
  "activeOperations": [],
  "cursor": "opaque_session_cursor",
  "dataComplete": true
}
```

### `GET /api/v1/workspaces`

Returns workspace groups with aggregate health and member instance/index ids. Query parameters:
`state`, `cursor`, `limit` (1–200).

### `GET /api/v1/instances`

Returns instance summaries. Query parameters: `workspaceId`, `indexId`, `connectionState`,
`accessMode`, `cursor`, `limit`.

Connection state: `connected | stale | disconnected | incompatible`.

### `GET /api/v1/indexes/{indexId}`

Returns one physical index summary, current build/refresh state, writer instance id, follower ids,
and bounded recent build summaries. Shared index metrics appear once.

### `GET /api/v1/indexes/{indexId}/builds`

Cursor-paged build summaries. Query parameters: `cursor`, `limit` (1–100).

### `GET /api/v1/operations`

Cursor-paged recent/active operations. Filters:

- `workspaceId`, `instanceId`, `category`, `tool`
- `outcome`, `confidence`, `coldState`
- `minDurationMs`, `fromUtc`, `toUtc`
- `cursor`, `limit` (1–500)

The operation detail returned here matches the producer's normalized completed-operation model.
It never adds raw query arguments.

### `GET /api/v1/events`

Cursor-paged structured diagnostic events. Filters: `workspaceId`, `instanceId`, `indexId`,
`severity`, `component`, `code`, `correlationId`, `fromUtc`, `toUtc`, `cursor`, `limit`.

### `GET /api/v1/metrics`

Parameters:

- `metric` (repeatable stable metric name)
- `workspaceId`, `instanceId`, `indexId`, `tool`, `outcome`
- `fromUtc`, `toUtc`
- `resolution`: `1s | 10s | auto`

Response:

```json
{
  "series": [
    {
      "metric": "operation.duration_ms.p95",
      "unit": "ms",
      "labels": { "tool": "implementations" },
      "points": [
        { "timestampUtc": "2026-07-13T23:33:50.000Z", "value": 8420.0 }
      ]
    }
  ],
  "resolution": "10s",
  "dataComplete": true
}
```

Only schema-approved low-cardinality labels are accepted.

### `GET /api/v1/stream?cursor={cursor}`

Server-Sent Events. Each event has an opaque `id`, a stable event name, and a normalized portal API
record:

```text
id: opaque_next_cursor
event: operation.completed
data: {"instanceId":"...","operationId":"...","durationMs":8420,...}

```

Possible event names:

- `instance.updated`, `instance.stale`, `instance.disconnected`
- `index.updated`, `index.build.progress`, `index.build.completed`
- `operation.started`, `operation.completed`
- `diagnostic.event`, `metrics.updated`
- `telemetry.gap`
- `reset_required`

The stream is one-way. Slow consumers are disconnected. `reset_required` tells the UI to call
`/api/v1/bootstrap` and replace its local state.

### Method and route policy

- `/api/**` supports documented `GET` endpoints only.
- `POST`, `PUT`, `PATCH`, `DELETE`, and mutation-shaped routes return `405`.
- Unknown API routes return `404`, not the SPA shell.
- Page sizes and response bytes are capped; truncation/data-completeness fields are explicit.

## Privacy contract

Project A must reject or redact these before enqueueing a frame:

- canonical or absolute workspace, project, database, Git, package, temp, or source paths;
- source text, excerpts, identifiers/symbol names, query/search strings, arguments, line numbers;
- prompts, MCP request/response bodies, environment variables, command lines;
- tokens, credentials, connection strings, package feeds/secrets;
- Git diffs, untracked contents, or raw file hashes that can identify content;
- arbitrary exception messages and stack traces;
- unbounded user-controlled labels or logger properties.

Allowed:

- salted opaque ids;
- explicit friendly workspace/client aliases and bounded workspace basename;
- tool names, stable event/cause codes, enum states;
- counts, durations, byte counts, rates, booleans;
- exception type only when it does not contain generic/user-controlled text;
- portal-authored explanatory text keyed from stable codes.

Project B validates bounds and allowed shapes again. It must never attempt to reconstruct or request
the redacted data.

## Bounds

V1 producer limits:

- frame: 256 KiB;
- batch: 100 records;
- phase entries per operation: 64;
- counter fields per operation: 64;
- diagnostic fields: 32;
- any string: 256 UTF-8 bytes unless a smaller field-specific bound applies;
- cause codes: 16;
- in-process pending queue: implementation-measured, fixed, and exposed through dropped counts;
- publication cadence: build progress and gauges at most 4 Hz per instance; heartbeat 0.5 Hz.

Portal retention is defined in the feature spec and reported by `/api/v1/bootstrap`.

## Compatibility policy

- V1 readers ignore unknown optional fields, counter names, and message types.
- V1 writers do not remove required fields or change units/meaning.
- New enum values are possible; UI renders unknown values as `unknown (<value>)` without failure.
- Breaking field, privacy, framing, identity, or semantic changes require protocol v2.
- Portal may support multiple major versions concurrently through separate normalizers.
- Producer and portal versions are displayed independently from the telemetry protocol version.

## Independent development workflow

### Contract fixtures

Before either implementation begins, freeze canonical fixtures for:

1. writer building with unknown total;
2. writer building with measured throughput/ETA;
3. follower observing the same physical index;
4. cold exact semantic operation;
5. timed-out/partial operation;
6. fast indexed search operation;
7. structured warning and critical event;
8. producer buffer gap;
9. stale/disconnected instance;
10. incompatible protocol.

Project B uses these fixtures in its mock broker/API and UI visual tests. Project A's contract tests
must emit schema-equivalent records for the same scenarios.

### Producer deliverables (Project A)

- instrumentation and phase/counter ownership;
- redaction and bounds before enqueue;
- local IPC client, negotiation, reconnect, snapshot/event publication;
- golden-frame/schema tests and privacy canaries;
- enabled/disabled/portal-absent overhead measurements;
- no portal HTTP or UI code.

### Portal deliverables (Project B)

- local IPC server and protocol negotiation;
- multi-instance grouping, writer/follower physical-index de-duplication;
- bounded retention, sequence-gap handling, metric derivation;
- loopback HTTP API, bearer bootstrap, Host/Origin policy, SSE;
- mock server generated from contract fixtures;
- complete responsive UI, animation, accessibility, and browser tests;
- no direct Phoenix Core/MCP/SQLite dependencies.

### Integration gate

Integration is complete when:

- fixture and live producer records normalize to the same portal models;
- two live same-workspace processes appear as one physical index with writer/follower instances;
- index progress, operations, events, and metrics update without polling producer internals;
- a portal crash/disconnect has no measurable correctness impact on MCP;
- privacy canaries never reach IPC, HTTP, SSE, browser state, or portal logs;
- both projects pass their independent tests plus an end-to-end multi-process test.

## Explicit scope boundary

This contract adds observability only. It does not approve:

- changes to index contents/schema or query semantics;
- project/TFM/assembly/reference resolution changes;
- raw query/source/log export;
- remote telemetry or internet access;
- a portal-to-MCP control plane;
- increased deadlines or eager semantic prewarming;
- implementation before the feature and API specifications are reviewed and approved.
