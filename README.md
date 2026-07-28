# PhoenixCodeNav

A code-navigation [MCP](https://modelcontextprotocol.io) server for **very large C# and mixed
C#/F# workspaces** (designed for enterprise monorepos with thousands of csproj/fsproj, legacy
*and* SDK-style, net472-first).
It gives coding agents (Claude Code, Codex, anything MCP) a fast, structured alternative to
grep-driven exploration: ranked search, file outlines, exact references, project graphs, and
compact context packs — with strict response budgets so results never flood the transcript.

> Named after **Phoenix A**, the most massive known black hole — built to navigate the
> heaviest repositories. No relation to Apache Phoenix.

**Docs:** [`docs/intro.md`](docs/intro.md) — why it exists, and how it compares to grep,
Cursor, and other tools · [`docs/design.md`](docs/design.md) — architecture, projects, and
how freshness (incl. git branch switch / pull) is handled ·
[`docs/agent-instructions.md`](docs/agent-instructions.md) — the snippet for your repo's
`CLAUDE.md` / `AGENTS.md`.

## Why not just grep?

At thousands of projects / millions of lines, text search returns too many weak matches, dependency
direction is invisible, and agents burn context reading whole files. PhoenixCodeNav answers
navigation questions in four layers, each labeled with how trustworthy it is:

| Layer | Tools | Confidence |
|---|---|---|
| **Indexed text** (SQLite FTS5, C# + F# + Markdown + SQL) | `find_file`, `search_text`, `source_context`, `config_lookup`, `references` (candidates) | `indexed` |
| **Syntax (C#)** (Roslyn parse, no compile) | `outline`, `search_symbol`, `symbol_at`, `batch_outline` | `indexed` |
| **Syntax (F#)** (FCS parse, no type check) | `outline` for project-owned `.fs` / `.fsi` | `indexed` |
| **Semantic** (Roslyn for C#; bounded FCS type checks for F# Stage 2A) | C#: `definition`, `references`, `implementations`, `callers`, `callees`, `type_hierarchy`; F#: position `symbol_at` and same-project `definition` | C# may be `exact`; bounded F# Stage 2A is `indexed` with explicit partial causes |

Plus structural facts parsed directly from every `.csproj` and `.fsproj` (`project_graph`,
`projects_containing`, `dependency_path`, `repo_overview`) and composites (`context_pack`,
`impact`, `related_tests`). Solution files may be inventoried for editor context, but they
never select projects or contribute build, ownership, dependency, or symbol-resolution authority.

Phoenix indexes `.fs`, `.fsi`, and `.fsx` text, parses `.fsproj` compile ownership and references,
and preserves C#↔F# project edges. Project-owned `.fs` and `.fsi` files also have an on-demand,
syntax-only `outline` backed by a pinned FSharp.Compiler.Service adapter; `.fsx` remains text-only.
When the same file is owned by an exact legacy `Project.fsproj` plus dual-target
`Project.Net.fsproj` migration pair, outline syntax defaults to the single-target legacy project's
parse context and reports up to 64 available project/TFM parse contexts with
total/returned/truncated coverage. A parse context controls only F# `#if` symbols and parser
options; it does not select assemblies, builds, reference resolution, or semantic workspaces. A
lone multi-target project uses its first declared TFM and reports that default as partial.
F# Stage 2A also supports position-based `symbol_at` and `definition` through a bounded FCS type
check of one physical `.fsproj` and one target framework. A file with exactly one owner/TFM is
selected automatically; every other shape requires explicit `projectPath` + `targetFramework` and
returns bounded `selectedFSharpTypeCheckContext` / `availableFSharpTypeCheckContexts` coverage.
This selection does not merge the legacy and `.Net.fsproj` migration projects. Stage 2A accepts
literal ordered compile items plus a bounded evaluation-lite subset used by legacy projects:
simple properties that precede semantic items, comparisons/boolean/`Exists` conditions, `Choose`, and literal
workspace-local `.props` imports. For each selected F# project, Phoenix also discovers the nearest
indexed ancestor `Directory.Build.props` and `Directory.Build.targets`, evaluates them before and
after the project respectively, and supports bounded `Reference Include` / `Remove` mutations whose
item-list inputs are literal and metadata-free. Chained local `.props`/`.targets` files are inspected;
irrelevant targets are ignored, while any active target/task that can mutate semantic inputs still
fails closed. Recognized compiler target imports are terminal boundaries; Phoenix never executes
targets/tasks, property functions, item transforms, or item metadata inheritance. Conventional self-default
properties are evaluated as unset; other unresolved ambient/global condition properties fail closed.
Import paths are selected only from canonical paths in the pinned index using the host path policy;
ambiguous Windows case aliases fail closed, and semantic evaluation never walks the mutable live
filesystem to resolve casing.
The standard `Microsoft.NET.Sdk` and recognized compiler-toolchain implicit authority are disclosed
as partial; custom/child/qualified SDK authority and Directory.Build mutations outside the bounded
property/condition/reference projection fail closed. Toolchain disclosure also covers unobservable
build authority above the workspace root.
Workspace-contained managed
`HintPath` binaries are copied into request-private immutable snapshots. A host-selected
`FSharp.Core` asset is disclosed as partial rather than
treated as project-evaluated authority. Package/project-reference closure, name-based F# search,
references, implementations, callers/callees, and hierarchy remain
unsupported instead of falling back to an empty or falsely exact result. Indexed searches stay
language-neutral by default. An explicit F#-only `search_symbol` path scope is rejected, while a
mixed C#/F# scope returns its C# symbols with `partialReason="unsupported_language_files_skipped"`.
For C# name lookup, exact-name type declarations receive a soft relevance preference over
same-named members without hiding either. A first-page empty `search_symbol` response remains a
clean result and reports `existsUnfiltered`; `appliedFilters` identifies the active narrowing, and
`unfilteredKinds` identifies declarations that become visible when those filters are dropped.
Exact path lookup remains strict, but a miss is recoverable: `outline` and `source_context`
not-found responses, plus a first-page exact-path `find_file` miss, may include up to three
workspace-relative paths under `pathSuggestions.paths` from the pinned index. The object also
reports the exact same-basename `total` and whether the returned sample was `truncated`.
Candidates are ranked by the longest matching path-segment suffix, then preserved prefix; Phoenix
never substitutes a suggestion or consults Git history for deleted files.

The dependency graph also sees what MSBuild's project view hides in large legacy codebases:
binary `<Reference Include>` + HintPath couplings from **multi-staged builds** (phase one
builds dlls to a common folder; later projects reference the dll, not the project) count as
graph edges, so cross-project `references`/`implementations` resolve exactly across them.
Every edge carries its provenance — `projectReference` vs `hintPathReference` — and `impact`
flags dependents wired only via HintPath: they bind to the last-*built* dll, and
ProjectReference-aware refactor tooling won't follow that edge.

**No MSBuild required.** The semantic layer builds Roslyn compilations directly from parsed
project files (AdhocWorkspace): documents from disk, framework reference assemblies, hint-path
and NuGet-cache package dlls, in-cluster project references. It works identically for legacy
(`ToolsVersion=15.0`, `packages.config`) and SDK-style projects.

## Keeping the index fresh

Index updates are incremental: the writer process's file watcher applies debounced C#, F#,
Markdown, and SQL deltas (edit/add/delete, FTS-consistent); `.csproj` and `.fsproj` changes rebuild compile
ownership and the authoritative project graph, while solution changes update only
non-authoritative editor inventory. A startup sweep catches
offline edits, and branch switches / pulls are detected by watching `.git` (`repo_overview.git`
reports indexed vs HEAD commit). Every response carries `indexStatus` / `indexVersion`
freshness metadata; `refresh_index` is an in-band writer hatch (`force: 'incremental' | 'full'`)
that recovers even a corrupted index without shell access, and cold builds expose live progress
counters (no fabricated ETAs). Cold rebuilds keep primary-key and uniqueness enforcement active
while bulk-loading rows, then construct the nine query-facing secondary indexes once inside the
still-unpublished finalization transaction. File rows use client-assigned ids and cached raw
ordinal SQLite statements; C# persistence batches up to 32 file rows before writing their content
and symbols. The final schema and query behavior are unchanged, while the build log separately
reports file-statement, structural-SQL, schema, secondary-index, commit, FTS, and checkpoint costs.

On Windows, Phoenix uses **one writer process and many read-only follower processes per index**.
One crash-recoverable named mutex, keyed by the physical workspace/worktree directory identity,
elects the process that owns builds, watchers, refreshes, and worktree-index mutations. Path aliases
to that worktree converge on the same mutex. Additional Claude, Codex, or other MCP processes attach
to the same compatible SQLite WAL index as followers and can use every navigation and semantic
query tool concurrently. Check
`server_capabilities.index.mode` or response `meta.indexMode` for `writer`, `follower`, or
`unavailable` (the process has not attached to a database role).
Followers never build or repair an index; `refresh_index` and `index_worktree` return the stable
`index_writer_required` error and must be run from the writer process. A follower's index-backed
evidence reads committed writer state, while tools explicitly labeled live and compiler-backed
semantic operations may read newer workspace bytes. Followers cannot observe the writer process's
pending watcher queue, so capabilities report `pendingChangesKnown: false` rather than presenting
a local zero as freshness evidence. There is no cross-process reader registry, slot file, or
turnstile. A small `<index-db>.phoenix-owner` claim binds that destination to the writer's physical
workspace identity and publishes `ready` or `rebuilding`. Followers check it before and after every
SQLite open. Publishing `rebuilding` therefore stops new opens from barging while existing bounded
operations keep their consistent old handle; the sole writer waits up to three minutes for those
handles before publishing the replacement. A different workspace cannot claim the same
`--index-db`, and a same-workspace process configured with another database refuses follower mode
instead of reading a path its writer does not maintain. If a database moves with its workspace,
an ordinary open refuses to guess while the old root may be temporarily unavailable. An explicit
`refresh_index(force: "full")` rebuild recognizes the missing old lexical root and rebinds
metadata to the new location; a still-live different workspace remains a hard refusal.
Followers reopen the compatible database on each request. If the writer exits, an existing follower
can continue serving the last committed state but never promotes itself; restart it if that process
should become the next writer.

## Install (work machine)

Prerequisites: **none** for the self-contained build. For semantic (`exact`) results the
machine needs net472 reference assemblies from any of (probed in this order):
`CODENAV_NET472_REFS` env var → VS/Build Tools targeting pack → NuGet
`Microsoft.NETFramework.ReferenceAssemblies.net472` package cache → installed .NET Framework
runtime. Any machine with Visual Studio qualifies automatically. Without them, tools degrade
to `indexed` confidence and say so.

```text
dotnet publish src/CodeNav.Mcp -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

Copy both `artifacts/win-x64/PhoenixCodeNav.Mcp.exe` and its adjacent `FSharp.Core.dll` to the
same directory (e.g. `C:\tools\phoenix\`). The executable remains self-contained; the sidecar is
the physical compiler reference asset used by bounded F# semantic navigation.
(A framework-dependent build — `dotnet publish -c Release -o artifacts/portable` — is ~5 MB
but requires the .NET 10 runtime.)

### Attach to Claude Code

Project-scoped `.mcp.json` at the repo root (recommended — checked in for the whole team):

```json
{
  "mcpServers": {
    "phoenix": {
      "command": "C:\\tools\\phoenix\\PhoenixCodeNav.Mcp.exe",
      "args": ["--workspace-root", "."]
    }
  }
}
```

or per-user: `claude mcp add phoenix -- C:\tools\phoenix\PhoenixCodeNav.Mcp.exe --workspace-root C:\path\to\repo`

### Attach to Codex

`~/.codex/config.toml`:

```toml
[mcp_servers.phoenix]
command = "C:\\tools\\phoenix\\PhoenixCodeNav.Mcp.exe"
args = ["--workspace-root", "C:\\path\\to\\repo"]
```

Then add the agent instructions from `docs/agent-instructions.md` to your repo's
`CLAUDE.md` / `AGENTS.md` so agents prefer these tools over shell grep.

The first process to acquire the writer lease builds the index in the background (a 10M-LOC repo
takes a few minutes; the server answers `index_building` hints meanwhile). On Windows, followers
attach once that writer has produced a compatible index; a missing, corrupt, or schema-stale index
requires the writer rather than being repaired by a follower. The index lives in
`<workspace>/.codenav/index.db` — add `.codenav/` to `.gitignore` — or point `--index-db`
elsewhere. A custom destination is still owned by exactly one physical workspace; do not share one
database path between workspace roots.

## Git worktrees (review flows)

Each worktree carries its own index under `<worktree>/.codenav/` — indexes are
workspace-relative, local-only, and never shared or committed (large-workspace indexes can
run to gigabytes).
Phoenix never creates or removes worktrees — its git usage is strictly **read-only**. A
review system creates the worktree; phoenix seeds and follows it:

```text
git worktree add ../review-1234 <ref>          # yours (or your review system's)
index_worktree(path: "../review-1234")         # MCP, on the MAIN instance: seeds a staged
                                               # snapshot of the live index (never torn, pump
                                               # never pauses) and installs it atomically into
                                               # the worktree's anchored .codenav destination,
                                               # then reconciles. refresh re-seeds the same way.
```

That one-shot publisher acquires the target worktree mutex and publishes the same destination
claim in `rebuilding` state for the entire staged install. It rewrites `workspace_root` to the
target before publication, so an existing follower cannot barge and the sibling Phoenix accepts
the resulting database as its own. If the target worktree's Phoenix starts while seeding owns the
mutex, it attaches as a follower and remains one after publication; restart that worktree server
after seeding when it should take writer ownership.

Platform policy: **Windows** reconciles with one targeted delta (git diff of
`indexed_commit->HEAD` UNION git status dirt — no fresh-checkout sweep); **Linux** always
runs an anchored full sweep of the sibling tree (`usedFullSweep: true`); **macOS** returns
`unsupported_platform` for both `worktrees` and `index_worktree`.

The review session then starts its own phoenix on the worktree — a **relative**
`--workspace-root .` in a checked-in `.mcp.json` serves the main enlistment and every
worktree identically, and the seeded index is queryable immediately. `worktrees` lists all
worktrees with per-index status (schema, indexed commit, in-sync) — loop it for "refresh
all". A worktree whose own Phoenix **writer** is running reports `worktree_index_locked`;
refresh from that writer (`refresh_index`) instead. A follower may list worktrees, but its
`index_worktree` call returns `index_writer_required`.

## Server CLI

```text
PhoenixCodeNav.Mcp.exe --workspace-root <dir> [--index-db <path>] [--rebuild]
```

`--rebuild` is honored only by the process that acquires the writer lease. A follower remains
read-only and does not promote itself; restart it after the writer exits if it must become writer.

## Development

```text
dotnet test tests/CodeNav.Tests                                  # fast unit + contract checks
dotnet test tests/CodeNav.IndexTests                             # index + semantic functionality
dotnet test tests/CodeNav.GitTests                               # Git/worktree manipulation
dotnet test tests/CodeNav.WatcherTests                           # watcher timing
dotnet test tests/CodeNav.LifecycleTests                         # leases, followers, process lifecycle
dotnet test PhoenixCodeNav.sln                                   # complete solution suite
pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1              # external Roslyn/F# MCP gate
dotnet run --project src/CodeNav.WorkspaceGen -- --out C:/temp/acme-2k \
    --projects 2000 --density 6 --clean                          # synthetic enterprise repo
dotnet run --project src/CodeNav.Bench -c Release -- --workspace C:/temp/acme-2k --rebuild
dotnet run --project src/CodeNav.Bench -c Release -- --workspace C:/temp/acme-2k --semantic
bash scripts/smoke-mcp.sh C:/temp/acme-2k                        # stdio protocol smoke test
```

Projects: `CodeNav.Core` (discovery, index, semantic layer), `CodeNav.FSharp` (isolated FCS syntax
and bounded semantic adapter), `CodeNav.Mcp` (server, ships as `PhoenixCodeNav.Mcp.exe`), `CodeNav.WorkspaceGen`
(synthetic workspace generator),
`CodeNav.Bench` (benchmarks vs the brief's latency targets), plus focused unit, index, Git,
watcher, and lifecycle test projects under `tests/`.

## Known limitations (v1)

- SDK-style compile items are approximated by `<Compile Include>` glob expansion plus
  longest-dir-prefix heuristics (no MSBuild evaluation); explicit legacy `<Compile Include>`
  items — including linked files — are exact. Residual gaps: shared `.projitems`, props-level
  globs, and MSBuild `Condition`s are not evaluated.
- `search_text` regex mode (`regex:true`) is line-based .NET regex narrowed by FTS tokens —
  no multi-line patterns.
- Token-mode `search_text` grades at most 300 files after applying language/path/project/scope
  filters; clipped calls report `filesScanned`, `filesAtLeast`, and
  `partialReason:"candidate_file_cap"` rather than presenting bounded counts as complete.
- F# `outline` is syntax-only and limited to compile-owned `.fs` / `.fsi`; `.fsx` is text-only.
  F# semantic Stage 2A is position-only and limited to bounded, same-project source closure for
  `symbol_at` and `definition`. It evaluates simple properties/conditions/`Choose`, literal
  workspace-local `.props`, and the nearest indexed ancestor `Directory.Build.props`/`.targets`
  for bounded metadata-free reference lists and exact `Reference Include`/`Remove` operations.
  It does not evaluate active targets/tasks, property functions, wildcard reference operations,
  imported compile items, property reassignment after semantic items, custom SDK authority,
  unresolved ambient/global condition inputs, package/project references, or arbitrary
  MSBuild. It also does not support
  F# name search, references, implementations, callers/callees, or hierarchy. Unscoped indexed
  search remains language-neutral; C# syntax search with an explicit F#-only scope discloses
  `unsupported_language`, and a mixed scope marks its C# results partial.
- Indexed `references` are whole-identifier text candidates; use `mode="semantic"` (or the
  default auto-upgrade) for compiler-exact results.
- Semantic scans load all matching candidate projects by default (`maxProjects:0`). A positive
  `maxProjects` value is an explicit latency/memory tradeoff; bounded responses report the total
  skipped count and a size-bounded sample.
- Multi-TFM projects index a single symbol row per declaration (net472-first design).
- Git awareness covers freshness (indexed vs HEAD commit/branch), not navigation — a
  `recent_changes` tool, `xml_doc`, and `diagnostics` from the brief are not yet implemented.
