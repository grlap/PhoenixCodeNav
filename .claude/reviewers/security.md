# Security / Process Execution Review

Focus: Workspace containment, hostile-repository inputs, safe Git execution, bounded subprocesses, and information exposure.

## What to check

1. **Path containment**
   - All caller-supplied read/write/refresh paths resolve inside the workspace.
   - Reject rooted paths, traversal, malformed paths, control characters, and separator/case edge cases.
   - Check symlink/junction targets and ancestors before following a caller path.
   - Scanner exclusions also apply to watcher/Git/manual targeted paths.

2. **Write scope**
   - Phoenix writes only its index under an authorized workspace/worktree.
   - Worktree targets must be validated against Git's worktree list.
   - Never write into bare repositories, arbitrary directories, source files, or Git metadata.

3. **Executable resolution**
   - Resolve Git to an absolute executable/wrapper from explicit override or PATH; never execute a workspace-local `git` through current-directory search.
   - `UseShellExecute=false`, no visible window, and no inherited MCP stdin.

4. **`.cmd`/`.bat` wrapper safety**
   - Any argument crossing `cmd.exe` is strictly allowlisted or encoded safely before interpolation.
   - Test metacharacters and expansion syntax: `& | < > ^ % ! ( ) "`, whitespace, Unicode, and leading `-`.
   - Ref outputs are hex-gated; symbolic refs have a strict charset.
   - Paths must not become shell-interpreted through `git show`, diff, or status helpers.

5. **Hostile Git configuration/repository content**
   - Read-only commands disable external diff/textconv behavior and other configurable helpers capable of executing programs.
   - Use stable output flags and `--` where path/revision ambiguity exists.
   - Disable prompts and optional locks; no credentials or interactivity.
   - Phoenix remains Git-read-only.

6. **Bounded process lifecycle**
   - Stdout and stderr drain concurrently with explicit encoding and size caps.
   - Timeouts are reachable even when pipes fill or descendants retain handles.
   - Timeout/drain failure kills the full process tree and closes read ends.
   - No reader-thread, process, or pipe leak survives degraded calls.

7. **Regex and source-read bounds**
   - Regex mode has per-match and overall ReDoS limits.
   - Source reads are span- and byte-bounded.
   - Untrusted indexed content cannot force unbounded allocations or responses.

8. **SQL/query construction**
   - SQLite values are parameterized.
   - FTS/regex/glob query syntax is validated or safely escaped.
   - Attacker-controlled symbol names cannot alter SQL or create non-terminating scans.

9. **Error exposure**
   - Client errors do not echo raw exception messages containing account names, absolute internal paths, connection details, or command internals.
   - Full diagnostic detail may go to server logs.
   - Do not log tokens, environment secrets, or unintended source bodies.

## What NOT to flag

- The explicitly documented `workspaceRoot` field.
- Read-only Git CLI usage itself.
- Broad catches at OS/process boundaries when failure is logged and degrades honestly.
- An explicit `CODENAV_GIT_EXE` override merely because it is user-controlled; still review argument safety.
- Relaxed JSON escaping on the stdio transport.
