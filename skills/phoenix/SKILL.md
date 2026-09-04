---
name: phoenix
description: "In this repository, prefer Phoenix for symbols, references, implementations, callers, impact, tests, and review; use shell search only within Phoenix's stated language and indexed-text boundary. Tool parameters come from `help TOOL`."
---

# Phoenix code navigation

In this repository, prefer Phoenix for symbols, references, implementations, callers, impact, tests, and review; use shell search only within the language and indexed-text boundary below.

If Phoenix MCP tools are registered in this session, call them directly; otherwise use the CLI exactly as below. The CLI is a view of the MCP tool surface: validated calls join the same workspace daemon and return the tool's JSON envelope unchanged.

## This repository

Executable: `{{PHOENIX_EXE}}`

Run commands from the repository root. Workspace precedence is `--workspace-root`, then `CODENAV_WORKSPACE_ROOT`, then the current directory; use `--index-db` when the index lives outside `.codenav/index.db`.

```sh
{{PHOENIX_EXE}} repo_overview --workspace-root "$PWD"
```

## Discover the live contract

Discovery is executable-local, accepts but ignores workspace/index flags, does not start a daemon, and is safe before an index exists:

```sh
{{PHOENIX_EXE}} tools
{{PHOENIX_EXE}} help search_symbol
{{PHOENIX_EXE}} schema search_symbol
```

`tools` and `help` carry `meta.build` and `meta.indexSchema`. `schema` is the bare registration-backed JSON Schema. Treat `{{PHOENIX_EXE}} help TOOL` and `{{PHOENIX_EXE}} schema TOOL` as the only sources of truth for parameters, defaults, and tool availability. Do not maintain a copied tool catalog or parameter list.

## Invoke tools

Direct flags use the exact case-sensitive camelCase wire names returned by discovery.

- Scalars accept `--name value` or `--name=value`.
- A bare boolean flag means `true`; use `--flag=false` for false.
- Pass one complete JSON object with `--json '{"field":"value"}'`.
- Pass JSON from a regular file (not a symlink, FIFO, or device) with `--args-file PATH`, or from UTF-8 stdin terminated by EOF with `--args-file -`.
- Do not mix `--json` or `--args-file` with direct tool flags.
- Use `--pretty` only for human reading; compact output is the machine contract.

The CLI reserves `--workspace-root`/`-w`, `--index-db`, `--json`, `--args-file`, and `--pretty`. It rejects host lifecycle flags `--rebuild`, `--keepalive`, and `--daemon-idle-ms`; call `refresh_index` explicitly when a rebuild is intended. Tool help is `{{PHOENIX_EXE}} help TOOL`, not `--help` or `-h`.

Keep stdout and stderr separate. Every non-interrupted completed command writes exactly one JSON document to stdout; diagnostics go to stderr. Parse the JSON rather than scraping prose.

## Interpret results and retry

- `0`: success, including honestly marked partial results
- `1`: tool-domain error, `daemon_request_rejected`, invalid tool-result contract, or internal CLI failure
- `2`: invalid CLI or tool input
- `3`: shared daemon unavailable
- `130`: interrupted

Always inspect the JSON even when the process exits nonzero. CLI-generated envelopes use `retryable`; relayed tool-domain results preserve `retryRecommended` and `retryHint`. Follow the stated recovery before retrying; never invent an unbounded loop or a name-based fallback.

`server_capabilities` is the exception: when the daemon is unavailable it has no top-level `error`; read `meta.indexMode == "unavailable"`, `meta.cause`, `meta.recovery`, and `meta.retryable`. Every other tool returns top-level `error: "phoenix_daemon_unavailable"` with `cause`, `recovery`, and `retryable`.

`symbolId` handles (`idx:NNN`) are valid only against the same index snapshot that returned them; `documentationCommentId` is the stable C# selector across sessions. A zero-hit answer carries suggestions and retry arguments; act on them explicitly, never guess a substitute.

When `daemon_request_rejected` is retryable, rerun the unchanged call once so it can join the successor daemon.

For `index_building` or `index_unavailable`, follow `retryRecommended` and `retryHint`: when retry is recommended, inspect `server_capabilities.index.progress` and `server_capabilities.index.state`, wait until the state is `ready`, then retry once; otherwise, read `server_capabilities.index.error` and the stated recovery instead of waiting. For `cluster_cold_load`, the index was already queryable: follow the returned deadline-aware hint, using a larger `timeoutMs` when the prior request was below the documented maximum or retrying unchanged when it was already at that maximum. Do not replace structured navigation with broad shell search.

## Route code-navigation work

1. Start with `repo_overview`.
2. Use `search_symbol`, then `context_pack`, to orient on an identifier.
3. From a stack trace, build error, or diff hunk, call `symbol_at` with its path and line.
4. Use `definition`, `references`, and `implementations` for exact C# evidence.
5. Use `impact` and `related_tests` before risky changes.
6. Use `review_pack` for a bounded change-set surface.
7. Use `search_text` for literals, messages, comments, and other non-symbol text; use `config_lookup` for configuration keys.
8. Run `outline` on a large file before fetching only needed spans with `source_context`.

A transport-successful response can still contain a structured domain error. Inspect `error`, `coverage`, `confidence`, `partial`, and truncation fields before drawing conclusions. F# semantic navigation is intentionally narrower; respect explicit unsupported and partial boundaries.

## Language boundary

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

## Example investigation

Use discovery first if any argument is uncertain. From the repository root, follow one symbol from workspace orientation through review:

Replace `OrderService` with the symbol under investigation.

```sh
{{PHOENIX_EXE}} repo_overview
{{PHOENIX_EXE}} search_symbol --query OrderService
{{PHOENIX_EXE}} definition --name OrderService
{{PHOENIX_EXE}} references --name OrderService --mode semantic
{{PHOENIX_EXE}} impact --name OrderService
{{PHOENIX_EXE}} related_tests --name OrderService
{{PHOENIX_EXE}} review_pack
```
