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

## The semantic layer — MSBuild-free, lazy, snapshot-pinned

This is the part designed specifically for net472 enterprise scale.

- **No MSBuild.** `SemanticWorkspace` builds a Roslyn `AdhocWorkspace` by hand from parsed
  csproj facts: documents from live files, framework reference assemblies (located via a
  targeting pack, the NuGet reference-assembly package, or the installed .NET Framework —
  see `ReferenceAssemblyLocator`), hint-path/NuGet package dlls, and in-cluster project
  references. This avoids `MSBuildWorkspace.OpenSolutionAsync`, which does not scale to a
  few-thousand-project solution.
- **Lazy, FTS-scoped clusters.** A reference query always loads the declaring project's dependency
  closure, then selects up to 128 optional candidate projects by default. `maxProjects: 0` selects
  all candidates and positive values have no fixed upper ceiling. Complete candidate discovery
  precedes selection; coverage excludes the mandatory closure from its candidate count, omits the
  applied bound for unbounded selection, and marks bounded reference totals as lower bounds. Bounded
  responses report the authoritative skipped count plus a size-limited sample.
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
