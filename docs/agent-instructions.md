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
   `search_symbol`, `definition`, `references`, `implementations` — not text search.
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
   - `exact` — compiler-verified (Roslyn); safe to act on.
   - `indexed` — index/syntax-backed leads; verify with `source_context` before
      large edits. `partial: true` or a `partialReason` means coverage was bounded —
       raise `maxProjects` or use `maxProjects: 0` for every candidate after a bounded call, raise
       `timeoutMs`, or narrow the target if completeness matters; the default is 128 and positive
       values have no fixed maximum. Treat `totalIsLowerBound:true` reference totals as a floor,
       not a complete census.
10. Keep limits small and tighten filters before paging. Fall back to shell `rg` only when
    the server reports `index_building`/`index_unavailable`, the path is outside the
    workspace, or you need true regex matching.
