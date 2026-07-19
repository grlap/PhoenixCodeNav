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
├── src/CodeNav.FSharp/        # isolated, pinned FCS syntax-outline adapter
├── src/CodeNav.Mcp/           # the server, published as PhoenixCodeNav.Mcp.exe
│   ├── Program.cs             # host + stdio transport; starts indexing in the background
│   ├── NavigationTools(.Expanded).cs   # the 23 MCP tools
│   └── Responses.cs          # JSON policy, response budgets, the Meta envelope
├── src/CodeNav.WorkspaceGen/  # deterministic synthetic 2k-project workspace generator (for tests/benchmarks)
├── src/CodeNav.Bench/         # cold-build + warm-query benchmarks vs the latency targets
├── tests/CodeNav.Tests/       # fast unit and contract checks
├── tests/CodeNav.IndexTests/  # index/semantic behavior; shared immutable functional index
├── tests/CodeNav.GitTests/    # isolated repositories, worktrees, diffs, and Git safety
├── tests/CodeNav.WatcherTests/ # isolated watcher timing checks
└── tests/CodeNav.LifecycleTests/ # writer leases, followers, and process lifecycle
```

The functional index collection builds its standard generated workspace once and reuses it
for read-only behavior checks. Tests that mutate files, SQLite state, Git repositories, or
watchers keep exclusive workspaces and writer ownership.

`CodeNav.Core` has no dependency on the MCP SDK — it is a plain library that could back a
different front end. `CodeNav.Mcp` is a thin protocol/shaping layer over it.

## The four navigation layers

Agents use the cheapest layer that answers the question, preferring compiler-backed facts
for code identifiers.

1. **Indexed text** — `find_file`, `search_text`, `config_lookup`. SQLite FTS5 over C# and F# file
   contents with workspace-aware ranking and byte/line offsets. `search_text` grades each
   line `precise` (contains all query tokens as whole tokens) vs `partial` (a token-covering
   lead), so a partial-token match is never presented as a full hit.
2. **Syntax (C#)** — `outline`, `search_symbol`, `symbol_at`, `batch_outline`. Roslyn
   *syntax-only* parsing (no compilation) extracts namespaces/types/members with spans,
   signatures, accessibility, partial flags, and generated/test classification. This is the
   token-saver: `outline` before any large-file read, then `source_context` for the spans.
3. **Syntax (F#)** — `outline` for compile-owned `.fs` and `.fsi`. A pinned, isolated
    FSharp.Compiler.Service adapter parses on demand without type checking; `.fsx` stays text-only.
    An exact same-directory legacy `Project.fsproj` + dual-target `Project.Net.fsproj` ownership
    pair selects the single-target legacy parse context and discloses up to 64 project/TFM parse
    contexts with complete coverage counts. A parse context controls only F# `#if` symbols and
    parser options; it does not select assemblies, builds, reference resolution, or semantic
    workspaces. Without that base owner, a multi-target project selects its first declared TFM and
    marks the result partial.
4. **Semantic** — C# uses lazy Roslyn compilations for `definition`, `references`,
   `implementations`, `callers`, `callees`, and `type_hierarchy`. F# Stage 2A uses a bounded FCS
   type check for position-based `symbol_at` and same-physical-project `definition`. An F# type-check
   context is exactly one physical `.fsproj` plus one target framework; ambiguous files require
   explicit selection, and the selection never changes or merges ownership/reference graph facts.

F# `.fs/.fsi/.fsx` content and `.fsproj` ownership/reference graphs are indexed. The FCS semantic
adapter consumes one immutable source/project snapshot captured from a pinned index epoch, copies
workspace `HintPath` assemblies through verified open handles into request-private snapshots, releases
SQLite before type checking, and bounds source count/bytes, references, concurrency, cache size,
deadline, diagnostics, contexts, and response bytes. Stage 2A deliberately accepts only literal
ordered compile items and a bounded evaluation-lite project subset: simple property
assignment/expansion before semantic items, comparisons and boolean/`Exists` conditions, `Choose`, and recursively loaded
literal workspace-local `.props` imports with count/depth/aggregate-byte limits and cycle detection.
Unique imported files, active import occurrences, condition depth, and evaluator nesting are bounded
separately. Only the conventional self-default property idiom may treat an unset property as empty;
other unresolved ambient/global condition inputs fail closed.
Import paths are selected only from canonical paths in the pinned index using the host path policy;
semantic evaluation never walks the mutable live filesystem to resolve casing.
Known compiler target imports are terminal boundaries. It never runs MSBuild, targets, or tasks and
rejects property functions, item transforms, imported semantic items, and unsupported conditions
with stable causes. Standard `Microsoft.NET.Sdk` and recognized toolchain implicit authority are
partial, including unobservable build authority above the workspace root; custom/child/qualified SDK
declarations, indexed in-workspace `Directory.Build.*`, and property assignment after semantic items
fail closed. Workspace-contained managed `HintPath` snapshots have their original identity
verified after the check; declarations remain same-project only. The host's target-compatible
`FSharp.Core` fallback is always disclosed as partial because it was not selected by evaluated
project authority. Package/project-reference closure and the
remaining semantic operations still disclose stable unsupported boundaries. Generic indexed search
remains language-neutral. For the C# `search_symbol` surface, an explicit F#-only path scope is
rejected and a mixed scope returns C# results with `unsupported_language_files_skipped`. This keeps
cross-language graph holes visible without fabricating semantics for unsupported F# project shapes.

Structural facts (`project_graph`, `projects_containing`, `dependency_path`,
`repo_overview`) come from the physical project-file and optional solution parse. Composites (`context_pack`, `impact`,
`related_tests`) synthesize the lower layers.

### Confidence model

Every response carries a `confidence`:

- `exact` — compiler-verified by a closed Roslyn project model.
- `indexed` — trustworthy indexed/syntax evidence, including bounded FCS compiler checks
  whose deliberately incomplete Stage 2A project model remains partial.
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
`node_modules`, `.vs`, generated files, and symlink/junction targets); parse every `.csproj` and
`.fsproj` directly, independent of solution membership; index `.cs`, `.fs`, `.fsi`, and `.fsx`
text, while parsing only `.cs` with Roslyn syntax during indexing. Symbol rows stream through a
bounded channel to the single writer; F# outlines are parsed on demand and are not stored. Solution files are
optional editor inventory: they never select projects or provide build, dependency, ownership,
or symbol-resolution authority. A cold build of a
multi-thousand-project workspace completes in minutes at most; live progress counters
(phase, files, throughput) report the real numbers for any given machine.

**Compile-item ownership**: legacy projects list `<Compile Include>` explicitly (exact,
including linked files). C# SDK projects use longest-dir-prefix approximation for implicit `.cs`;
F# stays ordered/explicit unless the project literally enables default items, whose SDK glob owns
only `.fs` (not unlisted `.fsi` signatures or `.fsx` scripts).

### Project and symbol-resolution authority

Each discovered `.csproj` or `.fsproj` is a physical project whose compile items, language, and
references are read from that project file. A side-by-side legacy project and SDK-style
`.Net.csproj` remain
separate physical projects under the established 0.11.7 model; their filename pairing alone
is not evidence that they should be merged or expanded into a new variant model.

Project resolution uses directly parsed `ProjectReference`, `Reference`, `HintPath`, package,
and recovered assembly-edge facts. Solution membership is editor inventory only, and FTS is
never project-identity authority. The current architecture does not create duplicate Roslyn
projects per target framework, select projects by output directory, or merge physical projects
solely because they share an assembly name. Any such change requires a separately justified
design backed by a concrete reproducer.

For symbol search, FTS generates and ranks candidates; syntax or compiler evidence decides
identity. `implementations` and `type_hierarchy` select generic declarations by the stored
syntax arity (explicit `arity`, or a `search_symbol` `symbolId`). A bare exact name spanning
multiple arities is refused rather than merging generic and non-generic symbols. FTS text
matches remain candidate evidence only and cannot accept, reject, or merge symbol identities.

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
- **Rebuild-coordinated long scans.** Candidate enumeration and semantic cluster loading hold a
  shared cross-process reader guard, so a destructive Windows rebuild drains them before replacing
  the SQLite database.
- **Reload keeps identity.** A changed project reloads under its *existing* `ProjectId`, and
  eviction only removes projects nothing loaded references — so dependents' references never
  dangle. An LRU soft-caps the loaded set (~160 projects).

Cold-cluster latency and working set scale with the selected project budget; use
`CodeNav.Bench` against the target repository for deployment sizing. Warm clusters avoid
reloading unchanged projects.

## Freshness — and how git operations are handled

The index is kept live without rebuilding on every keystroke:

- **`IndexManager`** owns the lifecycle. One process acquires the index writer lease, opens or
  builds in the background (never blocking the MCP handshake), and runs the serialized refresh
  pump. On Windows, compatible contenders attach as read-only followers with no writer
  connection, pump, watcher, build, or automatic promotion.
- **`WorkspaceWatcher`** (a `FileSystemWatcher`) debounces working-tree changes (600 ms
  quiet window) into batches. `DeltaRefresher` applies them: re-hash changed C# and F# source,
  update FTS, re-parse C# symbols, mark deletes, and rebuild compile ownership plus the
  authoritative project graph when a `.csproj` or `.fsproj` changes. Solution changes can update
  non-authoritative editor inventory only.
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

Before that pump mutates rows it commits a follower-visible `refresh_sweep_pending` marker. A new
database writes the same marker before its schema-version compatibility barrier, and startup keeps
it through watcher attachment. The marker is cleared only after the serialized post-build/startup
or ordinary refresh transaction succeeds. Thus a writer and any Windows followers report a
queryable-but-stale epoch while convergence is pending; neither can publish `ready` in the gap
between build capture and watcher-backed reconciliation. The manager tracks durable marker
publication separately from its in-memory state: if the marker transaction fails, it refuses the
refresh and every later request retries marker persistence before reading or mutating source rows.
Response metadata advises callers to retry `refresh_index` if that pending state persists; the same
marker also covers ordinary refresh convergence, not only the build/open handoff.

The index writer lease is per database destination. Only the process holding that lease owns the
watcher, queue, build, and refresh pump. On Windows, additional processes may attach to committed
WAL state as read-only followers; followers never watch, enqueue, refresh, or rebuild. On macOS and
Linux, a contender currently remains unavailable rather than attaching as a follower. Processes
configured with different index database paths own independent leases and refresh pipelines.

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
components also require directory-only opens. The leaf must pass regular-file `fstat`, remain under
the byte limit, and produce exactly its measured bytes with no extra byte. Windows uses relative
`NtCreateFile` calls with `FILE_OPEN` semantics, read-data/read-attributes access,
read/write/delete sharing, reparse-point refusal, and a non-directory leaf requirement. These
flags avoid following workspace-controlled links and avoid blocking on special files, but they
cannot eliminate an editor save/rename race.

`Unavailable` and `Oversized` regular sources are refresh failures, not skipped files. `Missing`
and `DefinitelyNonRegular` scan entries are omitted with skipped-input accounting. `DeltaRefresher` throws a
typed failure inside the transaction so every row and commit-metadata change in the complete batch
rolls back while the previously persisted sweep marker remains visible to followers; the manager
then refines that marker to the specific incomplete-source latch. The single pump retains an
unavailable request ahead of later
refreshes and retries the complete transaction
after short bounded delays (100 ms, 250 ms, then 1 second); it must not sleep while a SQLite
transaction is open. A retry success may publish the rows and commit metadata normally. While
retries are pending, health reports a known incomplete refresh and does not publish `ready`,
advance `indexed_commit`, report worktree `inSync`, or allow semantic coverage to claim
exact/current source evidence.

If the quick retries are exhausted, the writer keeps a stable `refresh_input_unavailable` cause,
persists that latch for read-only followers, widens the next queued targeted request into a
detect-all recovery sweep, and remains stale until that recovery or a full rebuild succeeds. It
never relies on the operating system producing a second identical notification. A fresh watcher
signal may start a new bounded retry budget, but cannot clear the incomplete-freshness latch unless
the recovery sweep captures the previously unavailable source.

`Oversized` is persistent rather than transient and receives no rapid retry loop. The failure
identifies the regular source that prevented the atomic batch and propagates bounded partial
coverage through refresh health, follower metadata, worktree-index results, and response metadata.
Because capture aborts on the first known-incomplete input, its path count is explicitly a lower
bound rather than a complete workspace total. It cannot advance the Git
baseline or earn `inSync`/exact claims. Strict worktree reconciliation follows the same rule and
does not install a staged database as `created` or `refreshed` when a regular source is unavailable
or oversized. Cold and explicit full builds also fail closed on any scanned regular source they
cannot capture, so a lossy new database is never published as ready. Regression coverage pins
transient failure followed by success, transaction rollback,
retry exhaustion and recovery, oversize behavior, Git-baseline preservation, queued-request
ordering, follower propagation (including a specific-latch persistence failure), post-build
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

## Result discipline

- **Budgets.** ~8 KB soft target, ~64 KB hard cap per response; oversized results shrink
  (precise-first) and set `truncated: true`.
- **Cursors.** List tools return `nextCursor` for paging.
- **Stable, line-addressable hits.** Every result carries enough path/line/span metadata
  for a follow-up `source_context`.

## Deployment

Published as a self-contained `PhoenixCodeNav.Mcp.exe` plus adjacent `FSharp.Core.dll` reference
sidecar (no installed runtime prerequisite), or a
framework-dependent build (needs .NET 10). Attach over MCP (`.mcp.json` for Claude Code,
`config.toml` for Codex). First run builds the index in the background; it lives in
`<workspace>/.codenav/index.db`. See [`../README.md`](../README.md) for exact commands.
