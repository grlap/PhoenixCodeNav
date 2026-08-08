# PhoenixCodeNav — agent instructions

Paste this section into the target repository's `CLAUDE.md` / `AGENTS.md`.

---

## Code Navigation (phoenix MCP)

This repository is too large for broad grep-based C# navigation. A `phoenix` MCP server
(PhoenixCodeNav) is attached with a persisted index of every project, file, and symbol.
Prefer its tools over shell `rg`/`grep`/`find` for source navigation.

Default flow:

1. Call `repo_overview` once before code work and check `meta.indexStatus` and
   `meta.indexMode`. On Windows, `follower` is fully queryable; it means another Phoenix
   process owns refresh/build authority. Do not retry `refresh_index` or `index_worktree`
   there — run the operation from the `writer` process when those tools return
   `index_writer_required`.
   `unavailable` means this process has not attached to an index role.
2. For anything that is a **code identifier** (type, method, property), use the symbol tools:
   `search_symbol`, `definition`, `references`, `implementations` — not text search. Indexed
   `search_symbol` supports F# `.fs/.fsi` declarations too; `.fsx` remains text-only. Treat
   `fsharp_parse_failed`, `fsharp_parse_contexts_truncated`, and any project-option cause in `partialReasons` as incomplete indexed F#
   declaration evidence rather than authoritative absence; inspect the parse/project-option
   coverage objects too. Stored indexing reserves one context per valid compile owner while the
   64-context budget has capacity; `truncatedOwnerProjects` counts owners whose distinct contexts
   were still omitted. `fsharp_project_options_imported` alone is advisory structured coverage,
   not a blanket partial result.
   F# semantic resolution is narrower: use
   position-based `symbol_at` / `definition`; references, implementations, callers/callees, and
   hierarchy are not available yet. The bounded evaluator processes a
   bounded subset of simple project properties/conditions/`Choose` and local `.props`; an explicit
   `fsharp_semantic_*_unsupported` cause means the project crossed that boundary, not that the symbol
   is absent. An unresolved condition-property cause means the result depends on an ambient/global
   build input that the selected project/TFM context does not claim to know. Standard SDK/toolchain
   implicit authority is disclosed as partial. The nearest indexed ancestor `Directory.Build.props`
   and `.targets` are evaluated only for bounded properties, conditions, and metadata-free Reference
   Include/Remove lists. For C# semantic navigation, an unconditional simple `PackageVersion` in the
   nearest indexed `Directory.Packages.props` supplies a versionless direct `PackageReference`; its
   version may use bounded simple `$(Name)` expansion from local unconditional properties, and the
   exact package version must already be installed. Project overrides, explicit project imports,
   applicable `Directory.Build.targets`, conditions, property functions, unresolved properties,
   exceeded limits, and other unsupported evaluation shapes retain
   established unresolved-reference behavior rather than guessing. For F#, the nearest indexed `Directory.Packages.props` contributes bounded,
   conditional `PackageVersion` authority for active versionless `PackageReference` items. Those
   identities use the selected target from an already-restored `project.assets.json`; transitive
   compile assets are copied into immutable request-private snapshots, while missing/stale or
   ambiguous assets and all project-reference closure fail closed. Custom SDKs and
   target/task-driven semantic mutations fail closed.
   If `implementations` returns `retryRecommended:true` with a cold-load or semantic-timeout
   reason, retry it once with the same arguments; non-partial exact responses omit that signal.
3. Use `search_text` only for literals: config keys, route strings, error messages, log
   fragments, comments. Use `config_lookup` for configuration keys specifically.
4. Starting from a **stack trace, build error, or diff hunk**: call
   `symbol_at(path, line)` to get the owning symbol and projects, then continue from it.
5. **Never read a large file blind.** Call `outline(path)` first (or `batch_outline` for
   several), then fetch only the needed spans with `source_context(path, "start-end")`.
6. Before **changing behavior**: `references(name or path+line)` grouped by project, plus
   `related_tests(name)`; for risky/public symbols run `impact(name)` first.
7. To orient on an unfamiliar symbol quickly, `context_pack(name)` returns definition,
   source, reference summary, tests, and project edges in one call.
8. For ownership and dependency direction use `project_graph`, `projects_containing`,
   and `dependency_path` — never guess from folder names.
9. Trust `meta.confidence`:
   - `exact` — compiler-verified by a closed Roslyn project model; safe to act on.
   - `indexed` — index/syntax-backed leads, including bounded FCS results whose
     `partialReason` names unevaluated project inputs; verify with `source_context` before
     large edits. `partial: true` or a `partialReason` means coverage was bounded —
      use `maxProjects: 0` after an explicitly bounded call, raise `timeoutMs`, or narrow the
      target if completeness matters; Phoenix does not impose a fixed project ceiling.
10. Keep limits small and tighten filters before paging. Fall back to shell `rg` only when
    the server reports `index_building`/`index_unavailable`, the path is outside the
    workspace, or you need true regex matching.
11. Call `open_operations_portal` only when the user explicitly asks to open or show the Phoenix
    Operations Portal. On success, show its returned `url` field verbatim as a clickable link.
    The tool starts or reuses the read-only loopback portal; it intentionally does not open a
    browser itself.
