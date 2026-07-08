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

Plus structural facts from csproj/sln parsing (`project_graph`, `projects_containing`,
`dependency_path`, `repo_overview`) and composites (`context_pack`, `impact`, `related_tests`).

**No MSBuild required.** The semantic layer builds Roslyn compilations directly from parsed
project files (AdhocWorkspace): documents from disk, framework reference assemblies, hint-path
and NuGet-cache package dlls, in-cluster project references. It works identically for legacy
(`ToolsVersion=15.0`, `packages.config`) and SDK-style projects.

## Measured performance (synthetic 2,003-project / 2.1M-line workspace)

| Operation | Result |
|---|---|
| Cold full index build | ~55 s (54.5k files, 572k symbols, 242 MB db) |
| `outline` / `symbol_at` / `definition` (indexed) | < 1 ms p95 |
| `search_text` / `search_symbol` | < 30 ms p95 (worst substring case 153 ms) |
| `references` indexed candidates, hot symbol, grouped | 41 ms p95 |
| `definition` semantic, **cold cluster** | ~1 s (then ~10 ms warm) |
| `references` semantic, hot symbol (24 projects loaded) | ~1.2 s, 2,252 exact refs, skipped candidates reported |
| Semantic memory after loads | < 300 MB working set (LRU cap: 160 projects) |

Index updates are incremental: a file watcher applies debounced deltas (edit/add/delete,
FTS-consistent); csproj/sln changes rebuild the project graph (~1 s). A startup sweep
catches offline edits. Every response carries `indexStatus` / `indexVersion` freshness metadata.

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

First run builds the index in the background (a 10M-LOC repo takes a few minutes; the server
answers `index_building` hints meanwhile and everything works from then on). The index lives
in `<workspace>/.codenav/index.db` — add `.codenav/` to `.gitignore` — or point `--index-db`
elsewhere.

## Server CLI

```text
PhoenixCodeNav.Mcp.exe --workspace-root <dir> [--index-db <path>] [--rebuild]
```

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

- SDK-style compile items are approximated by longest-dir-prefix globbing (no MSBuild
  evaluation); explicit legacy `<Compile Include>` items — including linked files — are exact.
- `search_text` is token-based FTS with substring line-location, not a regex engine.
- Indexed `references` are whole-identifier text candidates; use `mode="semantic"` (or the
  default auto-upgrade) for compiler-exact results.
- Semantic scans are scoped to FTS-candidate projects capped by `maxProjects`; skipped
  candidates are always listed so agents can widen deliberately.
- Multi-TFM projects index a single symbol row per declaration (net472-first design).
- `recent_changes` (git-aware navigation), `xml_doc`, and `diagnostics` from the brief are
  not yet implemented.
