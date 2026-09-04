# PhoenixCodeNav — agent instructions

Paste the section below into the target repository's `CLAUDE.md` or `AGENTS.md`.
It is intentionally short: the MCP response metadata is the authority for exceptional
cases, budgets, and recovery.

---

## Code Navigation (Phoenix MCP)

Phoenix is the primary source-navigation tool for this repository. Prefer it to broad
`rg`/`grep` and whole-file reads when answering questions about source symbols, callers,
ownership, dependencies, or likely tests.

### Default route

1. Call `repo_overview` once before code work. Check index status, mode, language coverage,
   and partial reasons. Ordinary launches use the shared same-worktree daemon automatically;
   `meta.indexMode` is the authority, and agents do not configure a daemon fallback.
2. For an identifier, call `search_symbol`, then `context_pack` for the best candidate.
   Use `definition`, `references`, `implementations`, `callers`, `callees`, and `type_hierarchy`
   when the question needs compiler-resolved facts rather than an orientation bundle.
   Carry a returned C# `documentationCommentId` into `definition`, `references`, or
   `implementations` when identity must survive reindexing; keep `symbolId` for cheap same-index
   follow-ups. A documentation ID is semantic-only: never request indexed mode and never replace a
   semantic failure with a same-name result. When documentation-ID coverage is incomplete, treat
   returned candidates as evidence and follow their explicit position recovery. If
   `documentation_id_position_shared` is present, the current tool cannot distinguish the linked
   assemblies by position; inspect the reported project and assembly evidence instead.
3. From a stack trace, build error, or diff hunk, call `symbol_at(path, line)` and continue
   with the owning symbol and project.
4. Use `search_text` for literals, messages, comments, and non-symbol text. Use
   `config_lookup` for configuration keys.
5. Before reading a large file, call `outline` or `batch_outline`; fetch only the required
   ranges with `source_context`.
6. Before changing behavior, inspect `references` and `related_tests`; add `impact` for a
   public or risky symbol. For a whole change set, use `review_pack` to collect the bounded
   review surface.
7. Use `projects_containing`, `project_graph`, and `dependency_path` for ownership and
   dependency direction. `dependencies` means canonical `downstream`; `dependents` means canonical
   `upstream`. Project selectors first try an exact project-file path. A bare selector then uses
   either the exact suffixed filename or the extensionless stem according to its shape, followed by
   `AssemblyName`; an ambiguous result must be resolved with an exact path. Do not infer ownership
   from folders. When shadow evidence is truncated, use `shadowedMatchCount` and
   `shadowedMatchesReturned` to distinguish the complete precedence decision from its diagnostic
   sample; the selected exact path or filename remains authoritative.

   The index keeps one row per physical project file; C# semantic compilation and graph answers
   are keyed by assembly name. Identical answers for two physical paths can reflect either the
   recognized legacy `project.csproj` / SDK `project.Net.csproj` companion pair or another
   same-name collision. Only documentation-comment-ID resolution exposes a non-pair collision
   through `nameKeyedOwnerCollisionGroups` and
   `documentation_id_name_keyed_owner_collision`; other semantic selectors and graph/composite
   tools carry no collision count. Use an exact project-file path when you need the physical row.
   The recognized companion pair is one semantic project at that layer and two rows in the index.

### Act on the response contract

- Phoenix reports domain failures as structured tool content with stable error and reason
  identifiers. A transport-successful MCP call can therefore still be a domain failure.
  Inspect the response before treating an empty result as authoritative.
- Reuse a returned `symbolId` for follow-up calls instead of resolving the same name again.
  Symbol handles fail closed after a reindex rather than silently retargeting.
- If a response sets `retryRecommended: true`, follow `retryHint`. Retry the same request
  when instructed; otherwise narrow scope, raise the disclosed budget, or take the named
  recovery action. Do not invent an unbounded retry loop.
- Treat `meta.confidence: exact` as compiler-verified within the stated coverage.
  `indexed` is a strong syntax/index lead that may need source verification. Never hide
  `partial`, `partialReason`, stale status, omitted counts, or truncation from your conclusion.
- A zero-hit retry template preserves the effective filters and `queryScope`; replay it as emitted
  so the suggested symbol remains visible under the same evidence scope.
- Recovery has two executable shapes. When `arguments` stands alone, call the named tool with
  those arguments. When `replayOriginalRequest: true` is present, replay your original call,
  remove every argument named by `remove`, then merge the supplied `arguments` patch; this keeps
  all non-selector filters and budgets unchanged while replacing only the failed selector.
- Keep result limits small and tighten filters before paging. When completeness matters,
  use the response's coverage fields and recovery guidance rather than assuming that no hit
  means no symbol.
- List-like string fields accept CSV or a JSON-array encoded string. Use the JSON form when an item
  contains a comma. If a configured default query scope is active, pass `queryScope: "all"` when
  complete generated/vendor/external indexed evidence is required. Exact semantic operations are
  not narrowed by this default. `queryScope` accepts `default`, `all`, or `first_party`; empty means
  `default`, while whitespace-only input is invalid.
- If `review_pack.affectedPaths` is present, the review is incomplete. Follow its stable reason ids
  and split the changed manifest into smaller explicit-path calls as instructed.

### Language boundary

- C# supports compiler-exact semantic navigation when its project model closes.
- F# `.fs` and `.fsi` files support indexed declarations, outlines, position-based
  `symbol_at`, and same-project definitions in a selected compiler context. Broader F#
  semantic operations may return explicit unsupported or partial reasons. `.fsx` remains
  text-only.
- Mixed-language results are only as complete as the reported per-language coverage. Treat
  an F# parse or project-option failure as missing F# evidence, not proof that the requested
  C# symbol is absent.

Fall back to shell search only for true regex work, files outside the indexed workspace,
transient build output, or when Phoenix explicitly reports that the required layer is
unavailable. When the user asks to open the Phoenix Operations Portal, call
`open_operations_portal` and return its URL verbatim; do not open a browser yourself.

### Shell-only agents

When MCP transport is unavailable but the Phoenix executable is installed, use its direct CLI
instead of replacing structured navigation with broad shell search. Start with
`PhoenixCodeNav.Mcp tools` and inspect a tool through `help <tool>` or `schema <tool>`. Discovery is
local to the executable, accepts but ignores global workspace/index flags, does not start a daemon, and carries
`meta.build` plus `meta.indexSchema` on `tools` and `help`; `schema` is intentionally the bare JSON
Schema, so cache it with the stamped help response when build identity matters. Invoke the exact
MCP name directly, for example
`PhoenixCodeNav.Mcp search_symbol --workspace-root <repo> --query IndexManager --lang csharp`.
Invocation workspace precedence is `--workspace-root`, then `CODENAV_WORKSPACE_ROOT`, then the
current working directory, so `cd <repo>` is the shortest reliable setup.
Direct flags use exact case-sensitive MCP wire names and accept `--name value` or `--name=value`.
Use `--json`, a regular non-symlink `--args-file <path>`, or `--args-file -` for complete JSON
arguments; stdin is one complete UTF-8 object terminated by EOF. Parse the single JSON document on stdout and keep stderr
separate. Exit codes are `0` success/partial, `1` domain/request-rejected/invalid-tool-result error, `2` bad input,
`3` daemon unavailable only, and `130` interrupted. A validated tool call joins the same shared
daemon and preserves the same response contract. CLI-generated and daemon-unavailable envelopes use
`retryable`; relayed tool-domain results keep their MCP `retryRecommended` / `retryHint` fields. Act
on the stated recovery before retrying at most once. Treat `index_building` separately: inspect
`server_capabilities.index.progress`, wait while its counters advance, and retry the original tool
after `index.state` becomes `ready`; do not replace structured navigation with broad shell search.
The CLI is not permission to invent automatic retries or name-based fallbacks;
`phoenix_tool_result_invalid` is non-retryable. The CLI adds no
second wall-clock timeout: tool work keeps each tool's own deadline, Ctrl-C/SIGTERM returns `130`,
and a host that needs a whole-process deadline should enforce it around the CLI process. `--pretty`
changes indentation only; response budgets apply to the compact JSON representation.
The CLI-only names `--workspace-root`, `--index-db`, `--json`, `--args-file`, and `--pretty` are
reserved. It rejects host lifecycle flags: request a rebuild through the `refresh_index` tool and
configure daemon keepalive/idle lifetime on the MCP host, never as a side effect of a navigation
command.
