# Feature Spec: Phoenix Operations Portal

Status: **Draft / proposal — design only; implementation is not authorized**

Bead: `PhoenixCodeNav-x5ls`

Depends on: `PhoenixCodeNav-epuc.1` (bounded internal telemetry)

Related: [`design.md`](./design.md), [`telemetry-api.md`](./telemetry-api.md)

## Decision summary

Phoenix should have one polished local operations website that can observe every Phoenix MCP
instance owned by the current OS user.

The recommended architecture is:

1. Each MCP process publishes bounded, redacted diagnostic snapshots over local OS IPC.
2. One separate per-user `PhoenixCodeNav.Portal` process aggregates those instances.
3. The portal serves one loopback-only HTTP website and read-only API.
4. The UI groups processes by workspace and physical index, then distinguishes the writer from
   read-only followers.

Delivery is split into two independently buildable projects against the versioned
[`Phoenix Telemetry API v1`](./telemetry-api.md): the Phoenix telemetry producer owns
instrumentation and local-IPC publication; the Operations Portal owns the multi-instance broker,
read-only HTTP/SSE API, aggregation, and complete website. Neither project references the other's
implementation internals.

The portal is a separate process deliberately. Running an HTTP server in every MCP process would
create port conflicts, multiple dashboards, browser cross-origin problems, duplicated retention,
and a portal whose lifetime depends on whichever editor or agent launched that MCP process.

Version one is observation only. It does not rebuild an index, clear caches, restart processes,
change configuration, run Git, execute commands, or mutate a workspace.

## Problem

Phoenix already knows much of the state needed to explain its behavior:

- index state, access mode, epoch, database size, build phase, file counts, throughput, and ETA;
- refresh queue depth and processed-change count;
- writer/follower ownership;
- semantic cold/warm state and loaded-project cache state;
- MCP query duration, outcome, confidence, deadline, and degradation cause;
- warnings and lifecycle transitions.

That evidence is currently distributed across MCP envelopes and stderr logs. An operator cannot
open one place and answer:

- Is this instance still indexing, refreshing, ready, semantically cold, or degraded?
- Is the index making progress or stuck?
- Which query is slow, and in which phase?
- Did a request return `exact`, `indexed`, `heuristic`, or `partial`, and why?
- Is this process the writer or a follower?
- Are two editor sessions using the same workspace and physical index?
- Is a problem isolated to one MCP instance or common to all instances for the workspace?

The website should make those answers immediate without adding risk or latency to the MCP server.

## Goals

- Make current Phoenix state understandable in a few seconds.
- Show live index rebuild and refresh progress honestly.
- Show recent MCP queries with timing waterfalls, outcomes, confidence, and bounded work counts.
- Show safe structured diagnostic events with useful filtering and correlation.
- Show operational metrics and cold-versus-warm behavior.
- Aggregate multiple MCP processes into one coherent view.
- Deliver a modern, responsive, accessible interface with purposeful animation.
- Keep telemetry bounded, non-blocking, local, and privacy-safe.
- Preserve every existing indexing, project-resolution, semantic, and MCP behavior.

## Non-goals for version one

- Remote access, cloud telemetry, team dashboards, or multi-user hosting.
- Long-term telemetry persistence or historical analytics across portal restarts.
- Raw source code, query arguments, symbol payloads, prompts, Git diffs, or absolute paths.
- A raw stderr/stdout log tail.
- Index rebuild, refresh, cache-clear, restart, shutdown, configuration, or Git controls.
- Changes to `.csproj` / `.Net.csproj`, Reference, HintPath, package, assembly-edge, TFM, or
  semantic-resolution behavior.
- Increasing query deadlines or prewarming the whole repository.
- Making the portal required for MCP startup or correct operation.

## Primary user journeys

### 1. Inspect an index rebuild

The user opens the portal and sees the affected workspace at the top with a clear `building`
state. The build view presents Phoenix's real pipeline:

`scanning -> parsing_projects -> indexing_files -> finalizing -> ready`

It shows:

- current phase and phase elapsed time;
- files indexed and total when the total is known;
- an indeterminate progress treatment when the total is not yet known;
- files per second and estimated remaining time only when Phoenix has enough measured data;
- files skipped and projects failed;
- database size and publish/checkpoint state;
- a phase timeline for the current build and bounded recent builds;
- writer instance identity and any followers waiting for publication;
- structured warnings and the correlation id for the build.

The UI must never invent a percentage or ETA. Unknown means visibly unknown.

### 2. Investigate a slow or degraded query

The user opens Queries, filters to `references`, `implementations`, or `type_hierarchy`, and sees:

- operation, instance, workspace, start time, and duration;
- cold, warming, warm, or cache-reused classification;
- outcome and confidence;
- deadline, timeout/cancellation phase, and incomplete reason;
- requested, loaded, reused, failed, and evicted project counts;
- candidate projects, graph edges, files/bytes read, and cache hits/misses;
- a waterfall of owner lookup, graph discovery, project load, workspace wait/mutation,
  compilation, candidate discovery, SymbolFinder, and result shaping;
- related structured events linked by correlation id.

The portal records operation metadata, not the requested symbol name, source position, prompt, or
result payload.

### 3. Inspect logs without leaking workspace data

The Logs view is a structured diagnostic event stream, not a raw console log viewer. Events use
stable ids, severity, component, instance id, correlation id, timestamp, and bounded redacted
fields. Filters include severity, component, event id, workspace group, instance, and correlation.

Examples include:

- index build started / phase changed / published / failed;
- refresh queued / drained / failed;
- writer acquired / follower attached / writer lost;
- semantic cold load started / shared / completed / cancelled;
- query deadline exhausted;
- telemetry dropped because a bounded buffer was full;
- portal protocol mismatch or instance disconnected.

Existing arbitrary logger strings must not be copied blindly into the portal because they can
contain absolute paths or other high-cardinality data.

### 4. Compare multiple instances

The All Instances page groups processes first by workspace identity, then by physical index
identity. Within a group it shows:

- one writer badge when a writer exists;
- zero or more follower badges;
- MCP version, diagnostics protocol version, PID, process start time, and uptime;
- editor/client label when explicitly supplied;
- connected, stale, disconnected, incompatible, or degraded state;
- active query count and recent latency/error indicators;
- whether more than one process is serving the same workspace;
- whether multiple workspace roots unexpectedly point at the same physical index.

Index build/database metrics are counted once per physical index. Query and semantic-cache metrics
remain per MCP process and may be aggregated across the workspace group.

## Information architecture

### Global shell

- **Workspace/instance switcher** with `All instances` as the default.
- **Connection indicator** for the browser-to-portal event stream.
- **Time range**: live, 5 minutes, 15 minutes, 1 hour, or portal session.
- **Theme**: dark, light, or system.
- **Pause live updates** for investigation without stopping collection.

### Pages

1. **Overview** — fleet/workspace health, active work, current build, query health, warnings.
2. **Index** — rebuild/refresh phase timeline, counters, throughput, recent builds.
3. **Queries** — live table, filters, latency charts, operation detail waterfall.
4. **Logs** — virtualized structured event table and correlation detail drawer.
5. **Metrics** — request rate, latency, confidence, timeouts, cache behavior, index throughput,
   queue movement, memory, and process health.
6. **Instances** — grouped writer/follower topology, versions, lifecycle, and connection health.

There is no Settings/control page in version one. An About panel may show the portal version,
protocol version, retention bounds, privacy statement, and startup command.

## Visual and interaction design

The portal should feel like a modern engineering console rather than a generated admin template.

### Visual language

- Dark-first neutral canvas with restrained depth, crisp typography, and a light theme.
- State colors have consistent meaning: healthy, active, warning, failed, stale, and unknown.
- Dense data remains readable through spacing, hierarchy, monospace numerics, and progressive
  disclosure rather than oversized cards.
- Charts use SVG or canvas only where they improve comprehension; tables remain selectable and
  accessible.
- The layout works from a laptop window through a wide operations display.

### Motion

Animation must communicate state change, not decorate idle screens:

- phase transitions move a highlight along the real index pipeline;
- newly completed work settles into counters rather than making them jump;
- query rows enter subtly and a selected row expands into its timing waterfall;
- charts interpolate new samples without rescaling violently;
- stale/disconnected state fades once, rather than pulsing forever;
- page and drawer transitions use short opacity/transform animations.

Rules:

- honor `prefers-reduced-motion` and provide equivalent non-animated state;
- pause continuous animation in background tabs and when live updates are paused;
- animate transforms/opacity rather than layout where possible;
- avoid perpetual glow, particle, parallax, or high-GPU effects;
- target smooth interaction under a full bounded event buffer, not only empty demo data.

## Multiple-instance architecture

### Chosen model: one per-user portal broker

```text
MCP instance A (workspace X, writer)   --\
MCP instance B (workspace X, follower) --- local IPC ---> PhoenixCodeNav.Portal
MCP instance C (workspace Y, writer)   --/                       |
                                                               loopback HTTP + SSE
                                                                        |
                                                                      browser
```

`PhoenixCodeNav.Portal` is a separate executable/process. The operator starts it explicitly, for
example:

```text
PhoenixCodeNav.Portal --open
```

MCP instances never bind HTTP ports. They opportunistically connect to a stable per-user local IPC
endpoint when the portal is present. If the portal is absent, slow, incompatible, or crashes, the
MCP request path continues unchanged; publication is non-blocking and bounded.

Suggested IPC transports:

- Windows: named pipe restricted to the current user SID.
- Linux/macOS: Unix-domain socket in the user runtime directory with owner-only permissions.

The wire contract is versioned independently from the MCP tool contract.

### Identities

- `instanceId`: random UUID generated for one MCP process lifetime. PID is display metadata only
  because PIDs are reused.
- `workspaceId`: salted hash of the normalized workspace root. Display uses a friendly basename or
  explicit alias, never the unredacted path.
- `indexId`: salted hash of the canonical database identity/path. Writer and followers sharing one
  physical index have the same index id.
- `sessionId`: random portal-process lifetime id, used to scope cursors and retention.

The salt is per OS user/install and is not exposed by the API. Hashes are identifiers, not security
boundaries.

### Registration and lifecycle

An instance registers protocol version, instance/workspace/index ids, writer/follower/unavailable
role, MCP version, process start time, PID, and optional explicit client label. It then publishes
heartbeats and delta/snapshot records.

Initial lifecycle targets:

- heartbeat every 2 seconds while connected;
- mark stale after 10 seconds without a heartbeat;
- mark disconnected when IPC closes;
- retain a disconnected instance for 2 minutes for diagnosis, then remove it;
- reconnect with backoff and jitter, without blocking MCP startup or shutdown;
- replace state only when `(instanceId, sequence)` advances;
- show incompatible protocol versions without trying to deserialize unknown payloads.

Exact intervals remain implementation-tunable, but every queue, retry, and retained record is
bounded.

### Alternatives rejected

1. **HTTP server in every MCP instance** — simplest locally, but produces port discovery, CORS,
   duplicate UI assets, multiple tabs, and no natural aggregate view.
2. **First MCP instance becomes portal leader** — requires leader election and state transfer, and
   the website disappears when that editor session exits.
3. **Shared files as the telemetry database** — polling and cleanup are awkward, secrets and file
   permissions are easier to get wrong, and hot writes add avoidable I/O.

## Telemetry and data contracts

The portal consumes immutable bounded snapshots/events. It must not reach into `IndexManager`,
`SemanticWorkspace`, SQLite, or process memory from the outside.

The normative producer/consumer fields, negotiation, framing, bounds, privacy rules, message
examples, HTTP endpoints, and independent contract fixtures are specified in
[`telemetry-api.md`](./telemetry-api.md). This section describes the product data model; the API
document is the parallel-development contract.

### Instance state

- ids, versions, role, uptime, heartbeat, connection state;
- index health summary and current epoch;
- semantic cache summary;
- active-operation counts;
- bounded process CPU, managed memory, working set, thread count, and GC counters when available.

### Index build and refresh

- build id and correlation id;
- real phase, phase start, total elapsed;
- monotonic files indexed, optional total, skipped files, failed projects;
- measured throughput and measured ETA when available;
- refresh pending and lifetime processed counts;
- completion/failure/publish event;
- bounded recent build summaries and phase durations.

The existing `BuildProgress` and `IndexManager.Health()` provide a baseline, but the portal needs a
safe structured event history and phase-duration snapshots rather than polling raw objects.

### Query operations

- operation/correlation id and MCP tool name;
- start/end/duration and active/completed state;
- deadline and cancellation/timeout phase;
- outcome, confidence, partial/stale state, stable diagnostic cause;
- cold/warm/reused classification;
- phase durations and bounded work/cache counters;
- response byte count and truncation metadata;
- no raw arguments, names, file positions, prompts, response bodies, or source excerpts.

### Structured logs

- stable event id, severity, component, timestamp;
- instance/workspace/index and correlation ids;
- small schema-controlled numeric/enum/redacted fields;
- no arbitrary exception dumps by default; exception type and stable cause are allowed, while
  messages and stack traces require explicit redaction policy.

### Metrics

Derived in memory from the bounded event stream:

- query rate and concurrent operations;
- p50/p95/p99 latency by tool;
- exact/indexed/heuristic/partial/failed ratios;
- timeout and cancellation counts by phase;
- cold/warm latency and cache hit/miss/eviction counts;
- loaded semantic projects and workspace-gate wait;
- index phase duration, file rate, skipped/failed counts;
- refresh queue pending versus processed movement;
- process CPU/memory/GC where safely available;
- dropped telemetry/event counts.

Metrics describe only the portal session unless a separately approved persistence feature is added.

## Retention and backpressure

Initial design bounds:

- recent operations: 2,000 records or 8 MiB per instance, whichever is reached first;
- structured events: 5,000 records or 8 MiB per instance;
- build history: 20 summaries per physical index;
- high-resolution metric buckets: 1 second for 15 minutes;
- rolled-up metric buckets: 10 seconds for up to 6 hours or the portal session;
- global portal retention ceiling: configurable with a conservative default such as 64 MiB;
- maximum serialized record and API page sizes are fixed and tested.

When full, buffers evict oldest records and increment an observable dropped/evicted counter. They
never block indexing, semantic work, MCP responses, or process shutdown.

## HTTP API and live updates

Use a versioned, read-only API:

- `GET /healthz` — portal liveness only; no workspace data.
- `GET /api/v1/snapshot` — bounded initial aggregate snapshot.
- `GET /api/v1/instances` — grouped instance/workspace/index summaries.
- `GET /api/v1/instances/{id}/queries` — bounded, cursor-paged operations.
- `GET /api/v1/instances/{id}/events` — bounded, cursor-paged structured events.
- `GET /api/v1/indexes/{id}/builds` — current and recent bounded build summaries.
- `GET /api/v1/metrics` — bounded time-series buckets.
- `GET /api/v1/events?cursor=...` — Server-Sent Events for one-way live updates.
- `GET /` — static UI assets.

SSE is preferred to WebSockets for version one because updates are server-to-browser and the API is
read-only. The browser obtains a bounded snapshot, then advances an opaque session-scoped cursor.
On overflow or reconnect beyond retention, the server sends `reset_required` and the browser
fetches a new snapshot. Slow clients are disconnected rather than growing an unbounded queue.

All non-GET API methods return `405`. Unknown routes return `404`; no fallback may accidentally
turn an API typo into the SPA shell.

## Security and privacy

Loopback is not automatically safe: hostile websites can target localhost, DNS rebinding can alter
Host resolution, and sensitive workspace information can leak through browser APIs.

Requirements:

- disabled until explicitly started;
- bind only to `127.0.0.1` and/or `::1`; reject wildcard and non-loopback addresses;
- choose an explicit or reported ephemeral port without falling back to a public bind;
- generate at least 128 bits of random access-token entropy per portal process;
- place the bootstrap token in the URL fragment, not the query string, then keep it in
  `sessionStorage` and send it in an authorization header;
- validate exact Host and Origin values; send no wildcard CORS headers;
- use no ambient authentication cookie;
- set `Content-Security-Policy`, `frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer`, and no-store headers for diagnostic APIs;
- restrict local IPC to the current OS user and validate peer/protocol registration;
- redact before publication at the producer, then defensively validate again at the portal;
- never expose source, query arguments, result payloads, prompts, environment, credentials,
  absolute paths, Git diffs, or package secrets;
- keep security failures structured and bounded without echoing rejected secret material.

The token protects against browser-origin attacks, not a malicious process already running as the
same OS user. That boundary is documented honestly.

## Performance budgets

The portal must not become the new reason Phoenix is slow.

- MCP-side instrumentation/publication adds less than 1% CPU on representative workloads and less
  than 1 ms p95 synchronous overhead per query.
- The publisher never performs network/IPC I/O while holding the index writer, semantic workspace
  gate, or response-shaping hot lock.
- Portal absence or backpressure costs only a bounded enqueue/drop and bounded reconnect attempts.
- Idle portal CPU is effectively zero between heartbeat/UI update work.
- Portal memory remains within its configured retention ceiling plus a documented fixed overhead.
- Browser updates are coalesced to at most a few visual commits per second even if the event rate is
  higher.
- Large tables are virtualized; charts operate on bounded buckets, not raw lifetime events.
- Static UI assets have a documented size budget and are served from the local process only.

## Frontend implementation direction

The Portal project owns the local IPC broker, normalized retention/metrics model, read-only HTTP
and SSE API, and all browser UI. The Phoenix producer project owns no web code. Portal development
uses the frozen telemetry contract fixtures and therefore does not wait for live producer work.

Recommended stack for a polished but maintainable UI:

- backend: a separate .NET 9 ASP.NET Core minimal-API executable;
- frontend: a small TypeScript SPA built as static assets and embedded/copied into the portal
  output;
- component and animation dependencies kept intentionally small and pinned;
- design tokens for color, spacing, typography, elevation, motion, and state;
- SVG-based charts where practical and a virtualized table for queries/logs;
- no CDN, analytics, fonts, scripts, or runtime assets loaded from the internet.

React/Preact plus Vite is a reasonable implementation choice, but this spec does not select a
framework. A short prototype should compare bundle size, accessibility, charting, and animation
quality before the implementation Bead commits to one.

## Accessibility

- Meet WCAG 2.2 AA for contrast, keyboard navigation, focus order, labels, and status semantics.
- Every chart has a table/text equivalent for its key values.
- Color is never the only state signal.
- Live regions announce important state transitions without announcing every metric update.
- Respect reduced motion, high contrast, text scaling, and browser zoom.
- Query/log tables remain navigable without a pointer.

## Failure behavior

- Portal unavailable: MCP behavior is unchanged; telemetry remains bounded and may be dropped.
- Instance disconnects: retain its last snapshot briefly and mark it disconnected/stale.
- Writer disappears: followers and the shared index group visibly report writer loss.
- Protocol mismatch: show incompatible version; do not crash either process.
- UI event cursor expires: fetch a fresh bounded snapshot.
- Browser is slow: disconnect its SSE stream; never expand server buffers.
- Portal crashes: no effect on indexing or MCP; a restarted portal accepts reconnecting instances.
- One malformed instance record: reject that instance/record and retain healthy instances.

## Test strategy

### Multi-instance

- two processes, same workspace/index: one writer plus one follower, grouped once;
- multiple followers and writer loss;
- two different workspaces;
- same workspace with process restart/PID reuse;
- portal starts after MCP instances and instances reconnect;
- portal restarts while instances remain alive;
- stale heartbeat, abrupt process death, duplicate/out-of-order sequence, protocol mismatch;
- custom index paths and collision-resistant redacted identities.

### Security and privacy

- loopback-only bind and port conflict behavior;
- hostile Host, Origin, CORS preflight, iframe, and DNS-rebinding-shaped requests;
- missing/incorrect token and token absence from logs, URLs sent to the server, and referrers;
- IPC ACL/permissions;
- producer and portal redaction tests using canary paths, secrets, prompts, query arguments, and
  exception messages;
- every mutation verb/route fails closed.

### Correctness and honesty

- unknown index total produces no percentage or ETA;
- measured ETA appears only under the existing gates;
- writer/follower build metrics are not double-counted;
- metrics match source events across eviction and reconnect;
- timeouts, partial confidence, dropped records, and stale instances remain visible;
- SSE cursor reset and snapshot recovery are deterministic.

### Performance and UX

- telemetry enabled/disabled overhead benchmark;
- portal absent, connected, slow, and buffer-full benchmarks;
- global retention ceiling under many instances;
- browser rendering with full query/log buffers;
- keyboard, screen-reader, contrast, reduced-motion, and responsive-layout tests;
- visual regression tests for major states rather than animations at arbitrary frames.

## Delivery sequence

No implementation begins until this feature spec and the telemetry API contract are reviewed and
approved.

1. **Freeze the shared contract** — review `telemetry-api.md`, schemas, bounds, privacy rules, and
   canonical fixtures.
2. Work in parallel:
   - **Producer project** — index/build/refresh, MCP operation, search, semantic, metric, and
     structured-event instrumentation plus the bounded local-IPC publisher.
   - **Portal project** — fixture-backed broker, grouping/retention, HTTP/SSE API, Instances,
     Overview, Index, Queries, Logs, Metrics, animation, and accessibility.
3. **Integrate** — run the live producer against the portal and verify the same models as fixtures.
4. **Harden** — multi-process lifecycle, privacy/security, load, browser performance, and visual
   regression coverage.

Each stage should be a separate Bead after approval, with decisive regression tests and the
normal zero-warning build, full-suite, and adversarial review gates. The portal requires its own
singular capability/version decision if it changes the user-visible tool/server surface; any
stored index-output change requires the normal schema-version discipline.

## Acceptance criteria for the feature

- One explicit command opens one local website containing all compatible Phoenix MCP instances for
  the current user.
- Same-workspace writer and followers are grouped correctly without double-counting the index.
- A live rebuild can be inspected through honest phase/counter/rate/ETA evidence.
- Recent queries show safe operation metadata, timing waterfalls, confidence, deadlines, and work
  counts without source or argument disclosure.
- Structured logs and metrics are filterable, correlated, bounded, and privacy-safe.
- The UI is modern, responsive, accessible, and uses purposeful animation with reduced-motion
  support.
- Portal absence, failure, or backpressure cannot change MCP correctness or materially affect
  latency.
- HTTP and IPC surfaces pass loopback, authentication, Host/Origin/CORS, redaction, lifecycle, and
  bounded-resource tests.
- Version one contains no mutation or remote-access surface.
- Implementation preserves established indexing, project-resolution, semantic, and MCP contracts.

## Open decisions requiring approval before implementation

1. Frontend framework: React, Preact, or dependency-light TypeScript components.
2. Portal startup UX: dedicated command only, or an optional launcher that starts it on demand.
3. Whether friendly workspace aliases are configured locally or derived only from directory names.
4. Final retention/memory defaults after measurement.
5. Whether any process CPU/memory fields are too platform-specific for version one.
6. Whether a later, separately approved control plane is desirable; it is not part of this spec.
