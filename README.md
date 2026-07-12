# PhoenixCodeNav

A code-navigation [MCP](https://modelcontextprotocol.io) server for **very large C# workspaces**
(designed for enterprise monorepos with thousands of csproj, legacy *and* SDK-style, net472-first).
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
navigation questions in three layers, each labeled with how trustworthy it is:

| Layer | Tools | Confidence |
|---|---|---|
| **Indexed text** (SQLite FTS5) | `find_file`, `search_text`, `config_lookup`, `references` (candidates) | `indexed` |
| **Syntax** (Roslyn parse, no compile) | `outline`, `search_symbol`, `symbol_at`, `batch_outline` | `indexed` |
| **Semantic** (Roslyn compilations, lazy clusters) | `definition`, `references`, `implementations`, `callers`, `callees`, `type_hierarchy` | `exact` (falls back to `indexed` with `partialReason`) |

Plus structural facts parsed directly from every `.csproj` (`project_graph`,
`projects_containing`, `dependency_path`, `repo_overview`) and composites (`context_pack`,
`impact`, `related_tests`). Solution files may be inventoried for editor context, but they
never select projects or contribute build, ownership, dependency, or symbol-resolution authority.

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

Index updates are incremental: the writer process's file watcher applies debounced deltas
(edit/add/delete, FTS-consistent); `.csproj` changes rebuild the authoritative project graph,
while solution changes update only non-authoritative editor inventory. A startup sweep catches
offline edits, and branch switches / pulls are detected by watching `.git` (`repo_overview.git`
reports indexed vs HEAD commit). Every response carries `indexStatus` / `indexVersion`
freshness metadata; `refresh_index` is an in-band writer hatch (`force: 'incremental' | 'full'`)
that recovers even a corrupted index without shell access, and cold builds expose live progress
counters (no fabricated ETAs).

On Windows, Phoenix uses **one writer process and many read-only follower processes per index**.
The process that acquires the writer lease owns builds, watchers, refreshes, and worktree-index
mutations. Additional Claude, Codex, or other MCP processes attach to the same compatible SQLite
WAL index as followers and can use every navigation and semantic query tool concurrently. Check
`server_capabilities.index.mode` or response `meta.indexMode` for `writer`, `follower`, or
`unavailable` (the process has not attached to a database role).
Followers never build or repair an index; `refresh_index` and `index_worktree` return the stable
`index_writer_required` error and must be run from the writer process. A follower's index-backed
evidence reads committed writer state, while tools explicitly labeled live and compiler-backed
semantic operations may read newer workspace bytes. Followers cannot observe the writer process's
pending watcher queue, so capabilities report `pendingChangesKnown: false` rather than presenting
  a local zero as freshness evidence. Review snapshots and semantic loads retain scalable shared
  Windows reader handles; a full rebuild takes a writer-intent turnstile and drains those readers
  before replacing the database. The queued rebuild resumes automatically after release. If the writer exits, followers
stop reporting query-ready until another writer appears; they never silently promote themselves,
so restart a follower when it should take writer ownership.

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

Copy `artifacts/win-x64/PhoenixCodeNav.Mcp.exe` anywhere (e.g. `C:\tools\phoenix\`).
(A framework-dependent build — `dotnet publish -c Release -o artifacts/portable` — is ~5 MB
but requires the .NET 9 runtime.)

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
elsewhere.

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
dotnet test tests/CodeNav.Tests                                  # full suite
dotnet run --project src/CodeNav.WorkspaceGen -- --out C:/temp/acme-2k \
    --projects 2000 --density 6 --clean                          # synthetic enterprise repo
dotnet run --project src/CodeNav.Bench -c Release -- --workspace C:/temp/acme-2k --rebuild
dotnet run --project src/CodeNav.Bench -c Release -- --workspace C:/temp/acme-2k --semantic
bash scripts/smoke-mcp.sh C:/temp/acme-2k                        # stdio protocol smoke test
```

Projects: `CodeNav.Core` (discovery, index, semantic layer), `CodeNav.Mcp` (server, ships as
`PhoenixCodeNav.Mcp.exe`), `CodeNav.WorkspaceGen` (synthetic workspace generator),
`CodeNav.Bench` (benchmarks vs the brief's latency targets), `CodeNav.Tests`.

## Known limitations (v1)

- SDK-style compile items are approximated by `<Compile Include>` glob expansion plus
  longest-dir-prefix heuristics (no MSBuild evaluation); explicit legacy `<Compile Include>`
  items — including linked files — are exact. Residual gaps: shared `.projitems`, props-level
  globs, and MSBuild `Condition`s are not evaluated.
- `search_text` regex mode (`regex:true`) is line-based .NET regex narrowed by FTS tokens —
  no multi-line patterns.
- Indexed `references` are whole-identifier text candidates; use `mode="semantic"` (or the
  default auto-upgrade) for compiler-exact results.
- Semantic scans use a 128-project candidate budget by default; `maxProjects:0` explicitly loads
  every matching project and positive values have no fixed upper ceiling. Candidate discovery
  completes before selection. Coverage reports the optional candidate count and active bound
  (omitted for unbounded `maxProjects:0`); bounded reference totals are marked as lower bounds,
  and bounded responses report the authoritative skipped count and a size-bounded sample.
- Multi-TFM projects index a single symbol row per declaration (net472-first design).
- Git awareness covers freshness (indexed vs HEAD commit/branch), not navigation — a
  `recent_changes` tool, `xml_doc`, and `diagnostics` from the brief are not yet implemented.
