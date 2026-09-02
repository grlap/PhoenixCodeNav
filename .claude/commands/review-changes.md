---
name: review-changes
description: Review current PhoenixCodeNav changes through independent Codex and Claude TermAl delegations.
metadata:
  termal:
    title:
      strategy: default
---

Review the current staged, unstaged, and untracked implementation changes by running `/review-code` in one Codex and one Claude TermAl reviewer session.

**IMPORTANT: Run `/review-changes` directly in the existing active, writable parent session. Never delegate or spawn `/review-changes` itself. The coordinator must be able to create normal build/test artifacts; only the `/review-code` children are delegated with `writePolicy: readOnly`.**

**IMPORTANT: This is a review-only command. Do not modify source files, stage, stash, checkout, reset, commit, push, or run any other mutating Git/remote-sync operation. Parent validation may create normal ignored build/test artifacts; recording findings in Engram from the parent session is the only permitted project-workflow mutation. This command grants no commit authority; after the review gate passes the outer workflow must still follow `CLAUDE.md` / `AGENTS.md`. Push always requires explicit per-changeset approval.**

**IMPORTANT: Engram is the canonical tracker for findings. Do not create markdown bug lists. Delegated read-only reviewers propose tracker actions; this parent command records the deduplicated findings after fan-in with the Engram words.**

**IMPORTANT: Attempt exactly two reviewer spawns through the TermAl MCP bridge: one Codex and one Claude. Do not use platform subagents, Claude Task agents, Codex collaboration agents, shell-launched agents, raw HTTP, synchronous shell polling, or nested TermAl delegation for this command. `/review-code` is deliberately non-nesting. If the TermAl delegation tools are unavailable, stop and report that the bridge is required.**

Required TermAl MCP tools:

- `termal_spawn_session`
- `termal_get_session_status`
- `termal_get_session_result`
- `termal_resume_after_delegations`

## Step 1: Confirm the implementation review target

Run this path-only inventory from the repository root. Do not emit patch content yet:

```text
git --no-optional-locks status --short
git --no-pager diff --no-ext-diff --no-textconv --no-color --name-only
git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --name-only
git ls-files --others --exclude-standard
```

If any inventory command fails or its path output is truncated or malformed, return INCONCLUSIVE before reading content or spawning reviewers.

The implementation review target is the sorted union of staged, unstaged, and ordinary untracked paths. If that target is empty, tell the user there is nothing to review and stop. Review-policy and instruction files are ordinary review targets: do not exclude or short-circuit them; include their changed paths and content in validation and both delegated reviews.

Record the absolute repository root from `git rev-parse --show-toplevel`; pass it as `cwd` to both TermAl sessions so the local slash command resolves from this repository.

Before diffing or opening target content, inspect changed-entry metadata without following links or reparse points. Require every path and traversed ancestor to remain inside the repository root. Treat tracked symlinks as Git link metadata and never dereference them; if an untracked symlink/junction/reparse point or any resolved path can escape the root, return INCONCLUSIVE without reading it.

Only after the containment checks pass, run:

```text
git --no-pager diff --no-ext-diff --no-textconv --no-color --check
git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --check
```

If either diff check reports an error, stop and report it before delegation. Preserve the sorted implementation path inventory for comparison after validation and fan-in. Do not calculate or require Git-object, patch, or content hashes from either reviewer.

## Step 2: Validate before delegation

Validation belongs in the writable parent, not in read-only reviewer children.

1. Run `dotnet build PhoenixCodeNav.sln -c Release --no-restore`. Validation must use the dependency graph already restored by the implementation session; it must not unexpectedly download packages. If assets are missing or stale, stop and ask the implementer to restore explicitly before review.
2. Require a successful build with literal `0 Warning(s)` and `0 Error(s)`.
3. Run `dotnet test PhoenixCodeNav.sln -c Release --no-build --no-restore`.
4. Run `pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1` against the pinned Roslyn and F# submodules. The harness requires both checkouts to match their pinned commits before startup, builds new isolated indexes through normal MCP startup, and runs every assertion against those fresh indexes. It never updates submodules, repairs old indexes, or changes baselines.
5. Require the external MCP integration harness to exit successfully with zero failed cases. Missing submodules, mismatched external commits, pre-existing explicit index paths, fresh-index baseline drift, or any harness failure stop the review gate; do not restore or update submodules implicitly.
6. Run `node ./website/verify.mjs` and require every static-site contract check to pass.

If the build, solution tests, external MCP integration harness, or website verifier fail, do not spawn reviewers or modify source files. A failed gate still requires investigation in this same turn: classify every failing test or check using read-only evidence.

- Test or environment defect (wrong assertion, stale fixture, host contention, missing prerequisite, an unpinned submodule): report the cause, the evidence, and the remediation the writable implementation workflow should perform.
- Product defect: record one Engram item per defect with the failing test as its acceptance criterion (`engram work add "…" --accept "<test> passes" --kind bug --under <item under review>`), and mark the item under review blocked on it when check-in depends on the fix. Never delete, skip, or loosen a test to make it pass.

After classification and any required Engram recording, hand remediation back to the writable implementation workflow and end the turn with every test name, cause, and action. "The suite failed" alone is not a report. Any solution-test failure blocks the gate. An isolated pass is diagnostic evidence only and never converts a failed full suite into a green gate; fix the test synchronization or product defect, then rerun the complete suite.

After validation, restart all of Step 1 from its path-only inventories. Reapply no-follow containment before any diff check, content read, or spawn; scan reviewable untracked text files for conflict markers and whitespace errors only after those checks pass. If the sorted implementation path inventory changed, repeat Step 2 against the new inventory and restart Step 1. If it changes again, return INCONCLUSIVE. Never validate one implementation path set and send a different one to reviewers.

## Step 3: Attempt exactly two delegated reviewers

Attempt exactly two reviewer session spawns. Call `termal_spawn_session` exactly twice, even if the first attempt fails. Never retry either slot.

1. Codex reviewer:
   - `agent`: `Codex`
   - `prompt`: `/review-code`
   - `mode`: `reviewer`
   - `writePolicy`: exactly `{"kind":"readOnly"}`
   - `title`: `Codex /review-code`
   - `cwd`: the absolute repository root from Step 1
2. Claude reviewer:
   - `agent`: `Claude`
   - `prompt`: `/review-code`
   - `mode`: `reviewer`
   - `writePolicy`: exactly `{"kind":"readOnly"}`
   - `title`: `Claude /review-code`
   - `cwd`: the same repository root

Read-only shared-worktree sessions are intentional: both reviewers must see the current staged, unstaged, and untracked implementation state. Do not request an isolated worktree.

Record each successful delegation id and each failed spawn. If neither spawn succeeds, report an INCONCLUSIVE review and stop.

## Step 4: Schedule durable fan-in, then stop

Call `termal_resume_after_delegations` with all successfully created delegation ids, `mode: "all"`, and a descriptive title such as `Phoenix dual-agent review fan-in`.

Inspect the tool response before yielding. Success requires a successful tool call containing a non-empty `wait.id`. For a newly persisted wait whose children are still running, `resumePromptQueued` and `resumeDispatchRequested` may both legitimately be `false`; they become true only after the wait is satisfied, so do not reject a valid wait id because those flags are false. If the call errors or no non-empty `wait.id` is returned, report INCONCLUSIVE with every delegation/child id and explicit manual recovery guidance; do not claim that a durable resume exists. Otherwise report the wait id, reviewer delegation ids, and any returned child session ids, then stop the current turn immediately. Do not poll with `termal_wait_delegations`, shell commands, logs, or raw HTTP. Do not continue to consolidation until TermAl resumes the parent with the fan-in prompt; keeping the turn active can prevent the queued resume from running.

In other words: after scheduling the durable wait, **stop this turn immediately**.

## Step 5: Fetch and consolidate after resume

For each successful delegation id:

1. Call `termal_get_session_status`.
2. If completed, call `termal_get_session_result`.
3. Preserve failed, cancelled, or missing statuses in the report.

TermAl lifecycle status and review verdict are different fields. A healthy child packet uses lifecycle `Status: completed`; derive the review verdict from `Review verdict: CLEAN|NOT CLEAN|INCONCLUSIVE` in its `Summary:` section plus its structured findings. Never interpret lifecycle `completed` as review CLEAN.

Require both requested reviewers to return complete non-truncated packets that list the reviewed implementation paths. Do not require reviewer-computed hashes or identities. Before accepting the packets, rerun the Step 1 path-only inventories in the parent. If the sorted implementation path inventory differs from the pre-spawn inventory, return INCONCLUSIVE.

Deduplicate findings without erasing independent agreement. If both reviewers found the same root issue, merge it and state that both caught it. Resolve severity disagreements explicitly. Owner severity decisions govern the consolidated severity.

Use this shape:

```markdown
# Delegated Review

## Validation
- Release build: ...
- Full suite: ...
- External MCP integration: ...
- Solution-test failures: none | ...

## Codex /review-code
- Status: ...
- Verdict: ...
- Findings: ...
- Evidence / commands: ...

## Claude /review-code
- Status: ...
- Verdict: ...
- Findings: ...
- Evidence / commands: ...

## Consolidated Findings
- Critical: ...
- High: ...
- Medium: ...
- Low: ...
- Notes: ...

## Verdict
- CLEAN | NOT CLEAN | INCONCLUSIVE
```

`CLEAN` requires both requested reviewers to complete successfully, return complete non-truncated packets, and leave no unresolved Critical or High finding. Medium and Low findings are allowed when they are recorded in Engram and reported in the final packet. A missing/dead reviewer, incomplete packet, or serious unverified risk makes the result `INCONCLUSIVE`, never CLEAN. `NOT CLEAN` is reserved for unresolved Critical or High findings.

## Step 6: Record findings in Engram from the parent

For every consolidated actionable finding:

1. Search before creating: `engram work ls --search "<short finding title or file:line>" --all`.
2. When an existing item tracks the same root cause, `engram work note <ref> "<review evidence>"` (claim it first if you hold nothing on it).
3. Create a new item only when nothing covers it: `engram work add "<finding title>" --outcome "<what correct behaviour looks like>" --accept "<the check that proves it>" --kind bug --priority <N> --label review --label <domain> --under <item under review>`.
4. Complete an item only when the reviewed implementation satisfies its acceptance criteria or the owner explicitly accepts or defers the finding.

Severity mapping: Critical -> priority 0, High -> 1, Medium -> 2, Low -> 3.

Use kind `bug` for correctness, security, data-loss, freshness, concurrency, protocol, or lifecycle defects and `task` for pure tests or documentation. Add labels `review` and a domain label such as `git`, `indexing`, `semantic`, `mcp-contract`, `security`, `performance`, or `testing`.

Do not modify source files while recording. If no changes are required, state `Engram is up to date - no changes needed.` Report every Engram action and confirm the resulting state with `engram work show <ref>`.

After recording, rerun the Step 1 implementation path inventory. If another implementation path was added, removed, staged, or unstaged meanwhile, return INCONCLUSIVE.

## Step 7: Hand off

- `NOT CLEAN`: list blocking Critical/High Engram refs and tell the implementer to fix, re-run focused tests and quality gates, then request verification from the original Codex and Claude child sessions through the TermAl UI. If those sessions cannot be continued, stop and ask for explicit direction; a fresh review is additional evidence but does not satisfy literal same-session verification.
- `INCONCLUSIVE`: report the missing reviewer/tool/packet condition — and, when gates failed, the classification of every failure and the Engram refs filed for product defects. It does not satisfy the commit discipline.
- `CLEAN`: state that the review gate passed, including any recorded Medium/Low Engram refs. Do not commit or push inside this command; return control to the outer workflow.

Current TermAl MCP does not expose a follow-up call to an existing child session. Re-running this command creates fresh Codex/Claude reviewer sessions and therefore cannot claim literal same-session verification of a fixed Critical/High finding. Continue with the original child sessions through the TermAl UI or stop and ask for direction; do not silently claim compliance.
