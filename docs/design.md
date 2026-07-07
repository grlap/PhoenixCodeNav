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
(index version, timestamps, coverage). WAL mode gives one writer + many readers.

**Build** (`IndexBuilder`): scan the tree (excluding `.git`, `bin`, `obj`, `packages`,
`node_modules`, `.vs`, generated files, and symlink/junction targets); parse every
`.csproj`/`.sln` in parallel; parse every `.cs` with Roslyn syntax on all cores, streaming
symbol rows through a bounded channel to the single writer. On the synthetic 2k-project /
2.1M-line workspace this is ~55s cold; the index is ~240 MB.

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
- **Lazy, FTS-scoped clusters.** A reference query loads only the declaring project's
  dependency closure plus the FTS-candidate dependent projects (bounded by `maxProjects`),
  never the whole repo. Skipped candidates are reported so the agent can widen deliberately.
- **One snapshot per operation.** Each op resolves the symbol against, *and* runs
  `SymbolFinder` against, a single pinned `Solution` — so a background reload/eviction can't
  orphan the symbol mid-query (which previously produced empty "exact" results).
- **Reload keeps identity.** A changed project reloads under its *existing* `ProjectId`, and
  eviction only removes projects nothing loaded references — so dependents' references never
  dangle. An LRU soft-caps the loaded set (~160 projects).

Cold cluster load is ~1s; warm queries ~10ms; working set stays under ~300 MB.

## Freshness — and how git operations are handled

The index is kept live without rebuilding on every keystroke:

- **`IndexManager`** owns the lifecycle: it opens or builds the index in the background
  (never blocking the MCP handshake), then runs a serialized refresh pump.
- **`WorkspaceWatcher`** (a `FileSystemWatcher`) debounces working-tree changes (600 ms
  quiet window) into batches. `DeltaRefresher` applies them: re-hash, re-parse changed
  `.cs`, update FTS + symbols, mark deletes, and rebuild the project graph when a
  `.csproj`/`.sln` changed. Directory-level changes (folder rename/move/delete) escalate to
  a full detect-all sweep, since the OS emits no per-child events for them.
- **Startup sweep.** When an existing index is reopened, a detect-all sweep reconciles any
  edits made while the server was down.
- Every response reports `indexStatus` (`ready` / `building` / `refreshing` / `stale`) and
  `pendingChanges`, so an agent can see when results may lag the working tree.

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
and, on any HEAD change, deterministically enqueue a targeted sweep — replacing "infer a
branch switch from thousands of file events" with "the branch changed, reconcile now." This
also lets `repo_overview` report the commit the index reflects. Tracked in the backlog; a
manual `refresh_index()` (full sweep) is the current workaround if you ever suspect drift
right after a big pull.

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
