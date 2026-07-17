# PhoenixCodeNav — Why it exists, and how it compares

## The problem

A coding agent (Claude, Codex, Cursor's model, …) navigating a **2,000-project C#
monorepo** with only `grep`/`ripgrep` runs into a wall:

- **Too many weak matches.** `rg InvoiceService` returns hundreds of hits across
  comments, strings, generated code, similarly-named symbols, and dead projects.
  The agent burns context reading them to find the one that matters.
- **No dependency direction.** Grep can't tell you that `Billing.Api` depends on
  `Billing.Application`, or which of 2,000 projects even compile a given file.
- **No semantics.** Grep can't distinguish a definition from a usage, resolve an
  interface's implementations, or find the *exact* references to a method (vs. every
  line that happens to contain its name).
- **Whole-file reads.** Without an outline, the agent reads a 3,000-line file to find
  one method — spending the transcript budget on noise.

At small scale none of this matters; `rg` is great. At enterprise scale it means the
agent spends most of its context *finding* the edit surface instead of *making* the edit.

## What PhoenixCodeNav is

A **read-only [MCP](https://modelcontextprotocol.io) server** that gives any agent a
fast, structured, honesty-labeled way to navigate large C# and mixed C#/F# codebases. It indexes the
workspace once (persisted SQLite) and answers navigation questions in four layers,
each of which tells you *how much to trust it*:

| Layer | Answers | Confidence |
|---|---|---|
| **Indexed text** (FTS5, C# + F#) | where literal text/config/keys appear, ranked | `indexed` |
| **Syntax (C#)** (Roslyn parse) | file outlines, symbol declarations, spans | `indexed` |
| **Syntax (F#)** (FCS parse) | compile-owned `.fs` / `.fsi` outlines | `indexed` |
| **Semantic** (Roslyn for C#; bounded FCS Stage 2A for F#) | exact C# navigation; F# position symbols and same-project definitions | C# may be `exact`; F# is compiler-checked `indexed` with explicit partial causes |

Plus structural facts (project graph, ownership, dependency paths) and composites
(`context_pack`, `impact`). Every response is budget-capped, line-addressable, and
carries `confidence` + index-freshness metadata.

F# source text, `.fsproj` compile ownership, and C#↔F# project edges are indexed. Compile-owned
`.fs` / `.fsi` files support FCS outlines plus position-based `symbol_at` and same-project
`definition` in a selected physical-project/TFM type-check context. Package/project-reference
closure and broader F# semantic operations still return explicit unsupported boundaries rather
than misleading empty answers. Indexed search is language-neutral unless the caller supplies a file
scope; a mixed C#/F# symbol scope returns the available C# symbols and marks the skipped portion
partial.

The design rule is: **return the smallest precise context that lets the agent take the
next step** — and never present a guess as a fact.

## How it compares

### vs. `grep` / `ripgrep`

Grep is still the right tool for: regex, binary-adjacent logs, transient build output,
and anything outside the indexed source tree — and PhoenixCodeNav says so explicitly
when a layer is unavailable or stale. What PhoenixCodeNav adds on **source navigation**:

- **Ranking + budgets** instead of a flat dump (handwritten over generated, project-local
  over vendored, production vs test separated).
- **Symbol semantics**: `definition`/`references`/`implementations` are *compiler-exact*
  for C#, not "lines containing this word." `search_text` even grades each hit `precise`
  (all query tokens on the line) vs `partial` (a lead), so you're never handed a
  one-word match dressed as a full match.
- **Project graph & ownership**: `project_graph`, `projects_containing`,
  `dependency_path` — facts grep cannot produce.
- **Outlines before reads**: `outline` + `source_context` fetch only the needed spans.

> **When it still falls back to grep** (by design): the MCP is unavailable or still
> indexing; the query is a regex or targets non-source/binary/log content; the scope is
> outside the indexed workspace; or the relevant layer reports itself `stale`/`partial`.
> If the agent falls back *more than expected*, that usually means the index is cold,
> a project cluster failed to load, or the agent instructions aren't attached — check
> `repo_overview` / `server_capabilities` first.

### vs. Cursor (and other IDE-embedded indexing)

Cursor indexes your codebase for *its own* AI features using embedding/RAG-style
retrieval. That's excellent for "find code similar to this" inside the Cursor editor,
but it is:

- **Similarity-based, not compiler-exact.** It surfaces plausibly-related code; it does
  not give you Roslyn-verified reference sets or overload-accurate definitions.
- **Editor- and model-bound.** The index serves Cursor's model in Cursor's UI.

PhoenixCodeNav is **complementary, not a replacement for an editor**:

- **Agent-agnostic.** It attaches over MCP to *any* agent — Claude Code, Codex, delegated
  explorer/reviewer sub-sessions — and exposes the *same* tools to all of them.
- **Deterministic + compiler-exact** for C# code facts, with explicit confidence labels.
- **Local, no cloud.** Nothing leaves the machine.
- **Shared warm index.** One index per workspace serves the parent session and its
  delegated children — no per-agent cold start.

### vs. other Roslyn/LSP MCP servers (Serena, roslyn-lens, RoslynMCP, …)

Those exist and work; PhoenixCodeNav's differentiators are the ones that matter at
*enterprise net472 scale*:

- **No MSBuild dependency.** The semantic layer builds Roslyn compilations directly from
  parsed `.csproj` facts (`AdhocWorkspace`), so it works on legacy (`ToolsVersion=15.0`,
  `packages.config`) and SDK-style projects without `MSBuildWorkspace.OpenSolutionAsync`
  (which can take *hours* on a few-thousand-project solution — see dotnet/roslyn#14325).
- **Lazy, FTS-scoped clusters.** It never loads the whole repo; a reference query loads
  only the projects that can *see* the symbol and *textually mention* it.
- **Confidence honesty.** Results are `exact` / `indexed` / `heuristic`, and degrade
  visibly (`partial`, `stale`, coverage counts) rather than silently downgrading.

## The bottom line

PhoenixCodeNav doesn't try to replace your editor or grep. It gives an *agent* the
navigation layer a large C# repo needs: ranked search, outlines, project ownership, and
**compiler-exact** symbol facts — labeled with how much to trust each one — so the agent
spends its context editing, not hunting.

See [`design.md`](./design.md) for the architecture and [`agent-instructions.md`](./agent-instructions.md)
for the snippet to drop into your repo's `CLAUDE.md` / `AGENTS.md`.
