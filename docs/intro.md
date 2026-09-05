# PhoenixCodeNav — Why it exists, and how it compares

## The problem

A coding agent (Claude, Codex, Cursor's model, …) navigating a **C# monorepo
with thousands of projects** using only `grep`/`ripgrep` runs into a wall:

- **Too many weak matches.** `rg InvoiceService` returns hundreds of hits across
  comments, strings, generated code, similarly-named symbols, and dead projects.
  The agent burns context reading them to find the one that matters.
- **No dependency direction.** Grep can't tell you that `Billing.Api` depends on
  `Billing.Application`, or which of thousands of projects even compile a given file.
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
| **Indexed text** (FTS5, C# + F# + Markdown + SQL) | where literal text/config/keys appear, ranked | `indexed` |
| **Syntax (C#)** (Roslyn parse) | file outlines, symbol declarations, spans | `indexed` |
| **Syntax (F#)** (FCS parse) | compile-owned `.fs` / `.fsi` outlines | `indexed` |
| **Semantic** (Roslyn for C#; bounded FCS for F#) | exact C# navigation; F# position symbols and definitions through an exact-first project-reference closure with bounded single-target `netstandard` compatibility, including restored direct/transitive and centrally managed package compile assets | C# may be `exact`; successful bounded F# results may be `exact` when disclosed partial reasons preserve selected-context authority |

Plus structural facts (project graph, ownership, dependency paths) and composites
(`context_pack`, `impact`). Every response is budget-capped, line-addressable, and
carries `confidence` + index-freshness metadata.

Markdown and SQL files are indexed for path lookup, ranked text search, regex/context search, and
bounded source reads, but remain text-only and never claim syntax or compiler semantics.
C# Roslyn project loading understands unconditional central package versions from the nearest indexed
`Directory.Packages.props`, including bounded simple `$(Name)` expansion from local unconditional
properties: versionless direct `PackageReference` inputs use an exact installed cache version and
central property/version changes reload warm projects. Project overrides, explicit project imports,
applicable `Directory.Build.targets`, conditioned or functional property evaluation, other unsupported shapes, and existing packages without a compatible
compiler library retain the established unresolved-reference behavior without guessing or invoking
restore or MSBuild, while an unavailable selected exact version directory fails closed. This does not
claim target-specific transitive package closure
for C#.
F# source text, `.fsproj` compile ownership, and C#↔F# project edges are indexed. Compile-owned
`.fs` / `.fsi` files support FCS outlines plus position-based `symbol_at`, `definition` through the
selected root's F# `ProjectReference` closure under the evaluated MSBuild transitivity and bounded
child-TFM policies, and references counted only in
that physical root project. The semantic path evaluates a
bounded legacy-project subset (simple properties and conditions, `Choose`, and literal local
`.props`) without executing MSBuild. It also evaluates the nearest indexed ancestor
`Directory.Build.props`/`.targets` around the project for bounded, metadata-free Reference
Include/Remove item-list mutations; target/task-driven mutations still fail closed. Standard
SDK/toolchain implicit authority is partial, and custom SDK authority remains unsupported. Restored
`PackageReference` closure is supported only when Phoenix can verify the selected direct and transitive
compile assets safely. Active F# project references use literal physical paths and in-memory FCS
project options. Exact child TFMs win; if exact is absent, the public Microsoft .NET Standard table
may select a single-target `netstandard` child. `netstandard2.0`/`netstandard2.1` resolve end to end;
`netstandard1.x` compile inputs and all multi-target compatibility remain fail-closed. Missing
projects, cycles, unsupported metadata, non-F# targets, same-assembly conflicts, and unavailable
TFMs fail explicitly without borrowing a
last-built DLL. SDK-style closure is transitive by default, while
`DisableTransitiveProjectReferences=true` and legacy-style projects remain direct-only. Child
compiler diagnostics are preserved with their source paths and downgrade semantic confidence just
like root diagnostics. Broader F# semantic operations still return explicit unsupported boundaries
rather than misleading empty answers. Indexed search is language-neutral unless the caller supplies a file
scope; a mixed C#/F# symbol scope returns the available C# symbols and marks the skipped portion
partial.

The design rule is: **return the smallest precise context that lets the agent take the
next step** — and never present a guess as a fact.

## Agent-first navigation contract

Phoenix serves a concise route in the MCP handshake, keeps capability discovery compact by default,
and exposes stable feature ids through `server_capabilities`. Broad find/search calls can use the
host-configured `CODENAV_DEFAULT_QUERY_SCOPE` (`all` or `first_party`) and always echo what was
applied; `queryScope: "all"` restores all indexed content without changing index-time inclusion.
Shell-only agents can access that same live surface through the published executable's direct CLI:
`tools`, `help <tool>`, and `schema <tool>` inspect the executable's own MCP registration without
starting the daemon and stamp their build/schema identity, while an exact tool name invokes it with
schema-derived flags or a complete JSON object. Validated invocations join the shared daemon and
return the unchanged JSON response rather than reimplementing navigation in a separate process.

Symbol and project dead ends include bounded recovery evidence rather than silent substitution.
Project selectors use exact path, exact suffixed filename, extensionless stem, then assembly
metadata; lower-precedence matches are byte-budgeted with true total, returned, and truncation
counts. `documentationCommentId` carries compiler-stable
C# identity through definition, references, and implementations on one semantic deadline, with no
indexed name fallback. CSV and JSON-array encoded strings are equivalent for list-like string fields.
Transient semantic cold-load/timeout responses across the navigation family share one bounded
`retryRecommended` / `retryHint` contract. When `review_pack` clips evidence, its separately bounded
`affectedPaths` block names stable causes and the path-splitting recovery action.

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
- **Shared warm daemon.** Since v0.12.60, every ordinary launch transparently joins or elects one
  same-user process per physical worktree. It owns the watcher, index, Roslyn workspace, and F#
  semantic state while each agent keeps an independent MCP session. No flag or environment opt-in
  is required, so per-agent semantic cold starts and duplicate background refresh processes simply
  do not occur.

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

See [`design.md`](./design.md) for the architecture,
[`agent-instructions.md`](./agent-instructions.md) for the concise snippet to drop into your
repo's `CLAUDE.md` / `AGENTS.md`, and
[`agent-experience-roadmap.md`](./agent-experience-roadmap.md) for the prioritized MCP
improvements aimed at coding agents.
