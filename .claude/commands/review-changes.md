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

**IMPORTANT: This is a review-only command. Do not modify source files, stage, stash, checkout, reset, commit, push, run `bd dolt push`, or run any other mutating Git/remote-sync operation. Parent validation may create normal ignored build/test artifacts; parent-session local Beads reconciliation is the only permitted tracked/project workflow mutation. This command grants no commit authority; after the review gate passes the outer workflow must still follow `CLAUDE.md` / `AGENTS.md`. Push always requires explicit per-changeset approval.**

**IMPORTANT: Beads (`bd`) is the canonical tracker for findings. Do not create markdown bug lists. Delegated read-only reviewers propose Beads actions; this parent command performs deduplicated reconciliation after fan-in. Generated `.beads/interactions.jsonl`, `.beads/issues.jsonl`, and `.beads/events.jsonl` ledgers are workflow bookkeeping, not part of the implementation review target, and their mutation never invalidates an otherwise completed implementation review. Other tracked `.beads` configuration, metadata, and hooks remain ordinary review targets.**

**IMPORTANT: Attempt exactly two reviewer spawns through the TermAl MCP bridge: one Codex and one Claude. Do not use platform subagents, Claude Task agents, Codex collaboration agents, shell-launched agents, raw HTTP, synchronous shell polling, or nested TermAl delegation for this command. `/review-code` is deliberately non-nesting. If the TermAl delegation tools are unavailable, stop and report that the bridge is required.**

Required TermAl MCP tools:

- `termal_spawn_session`
- `termal_get_session_status`
- `termal_get_session_result`
- `termal_resume_after_delegations`

## Step 1: Confirm the implementation review target

Run this path-only inventory from the repository root. Exclude only the generated Beads JSONL ledgers from every inventory. Do not emit patch content yet:

```text
git --no-optional-locks status --short -- . ':(exclude).beads/interactions.jsonl' ':(exclude).beads/issues.jsonl' ':(exclude).beads/events.jsonl'
git --no-pager diff --no-ext-diff --no-textconv --no-color --name-only -- . ':(exclude).beads/interactions.jsonl' ':(exclude).beads/issues.jsonl' ':(exclude).beads/events.jsonl'
git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --name-only -- . ':(exclude).beads/interactions.jsonl' ':(exclude).beads/issues.jsonl' ':(exclude).beads/events.jsonl'
git ls-files --others --exclude-standard -- . ':(exclude).beads/interactions.jsonl' ':(exclude).beads/issues.jsonl' ':(exclude).beads/events.jsonl'
```

If any inventory command fails or its path output is truncated or malformed, return INCONCLUSIVE before reading content or spawning reviewers.

The implementation review target is the sorted union of staged, unstaged, and ordinary untracked paths after those three generated ledgers are removed. If that target is empty, tell the user there is nothing to review and stop. Review-policy, instruction, and tracked Beads configuration files are ordinary review targets: do not exclude or short-circuit them; include their changed paths and content in validation and both delegated reviews.

Record the absolute repository root from `git rev-parse --show-toplevel`; pass it as `cwd` to both TermAl sessions so the local slash command resolves from this repository.

Before diffing or opening target content, inspect changed-entry metadata without following links or reparse points. Require every path and traversed ancestor to remain inside the repository root. Treat tracked symlinks as Git link metadata and never dereference them; if an untracked symlink/junction/reparse point or any resolved path can escape the root, return INCONCLUSIVE without reading it.

Only after the containment checks pass, run:

```text
git --no-pager diff --no-ext-diff --no-textconv --no-color --check -- . ':(exclude).beads/interactions.jsonl' ':(exclude).beads/issues.jsonl' ':(exclude).beads/events.jsonl'
git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --check -- . ':(exclude).beads/interactions.jsonl' ':(exclude).beads/issues.jsonl' ':(exclude).beads/events.jsonl'
```

If either diff check reports an error, stop and report it before delegation. Preserve the sorted implementation path inventory for comparison after validation and fan-in. Do not calculate or require Git-object, patch, or content hashes from either reviewer.

## Step 2: Validate before delegation

Validation belongs in the writable parent, not in read-only reviewer children.

1. Run `dotnet build PhoenixCodeNav.sln -c Release --no-restore`. Validation must use the dependency graph already restored by the implementation session; it must not unexpectedly download packages. If assets are missing or stale, stop and ask the implementer to restore explicitly before review.
2. Require a successful build with literal `0 Warning(s)` and `0 Error(s)`.
3. Run `dotnet test PhoenixCodeNav.sln -c Release --no-build --no-restore`.
4. Run `pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1` against the pinned Roslyn and F# submodules and their reusable indexes.
5. Require the external MCP integration harness to exit successfully with zero failed cases. Missing submodules, mismatched external commits, missing reusable indexes, or any harness failure stop the review gate; do not restore, rebuild, mutate, or refresh external fixtures implicitly.

If the build, solution tests, or external MCP integration harness fail, stop and report the output. The sole documented exception is `WatcherTests.ExtensionlessFileDeleteDoesNotTriggerSweep`: when it is the only solution-test failure, rerun that exact test in isolation. Continue only if the isolated rerun passes, and carry the flake note into the final review. Do not silently bless any other intermittent failure.

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

Require both requested reviewers to return complete non-truncated packets that list the reviewed implementation paths. Do not require reviewer-computed hashes or identities. Before accepting the packets, rerun the Step 1 path-only inventories in the parent. If the sorted implementation path inventory differs from the pre-spawn inventory, return INCONCLUSIVE. Changes confined to the three generated Beads JSONL ledgers do not count as implementation drift.

Deduplicate findings without erasing independent agreement. If both reviewers found the same root issue, merge it and state that both caught it. Resolve severity disagreements explicitly. Owner severity decisions govern the consolidated severity.

Use this shape:

```markdown
# Delegated Review

## Validation
- Release build: ...
- Full suite: ...
- External MCP integration: ...
- Known flake note: ...

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

`CLEAN` requires both requested reviewers to complete successfully, return complete non-truncated packets, and leave no unresolved Critical or High finding. Medium and Low findings are allowed when they are reconciled in Beads and reported in the final packet. A missing/dead reviewer, incomplete packet, or serious unverified risk makes the result `INCONCLUSIVE`, never CLEAN. `NOT CLEAN` is reserved for unresolved Critical or High findings.

## Step 6: Reconcile Beads in the parent

For every consolidated actionable finding:

1. Search before creating:
   - `bd search "<short finding title>" --status all`
   - `bd search --desc-contains "<file:line or unique phrase>" --status all` when useful.
2. Update an existing open issue with `bd update <id> --append-notes "<review evidence>"` when it tracks the same root cause.
3. Reopen a regressed closed issue or create a `discovered-from:<active-id>` follow-up when history should remain intact.
4. Create a new issue only when no existing Bead covers it. Include literal `## Problem`, `## Steps to Reproduce`, and `## Acceptance Criteria` sections, design guidance, and use `bd create --validate`.
5. Close an open issue only when the reviewed implementation satisfies its acceptance criteria or the owner explicitly accepts or defers the finding.

Severity mapping:

- Critical -> `P0`
- High -> `P1`
- Medium -> `P2`
- Low -> `P3`

Use `bug` for correctness, security, data-loss, freshness, concurrency, protocol, or lifecycle defects. Use `task` for pure tests or documentation. Add labels `review`, `severity-critical|high|medium|low`, and a domain label such as `git`, `indexing`, `semantic`, `mcp-contract`, `security`, `performance`, or `testing`.

Do not modify source files during reconciliation. If no changes are required, state `Beads is up to date - no changes needed.`

Generated `.beads/interactions.jsonl`, `.beads/issues.jsonl`, and `.beads/events.jsonl` changes are tracker bookkeeping outside the implementation review target. Report every Beads action and confirm the resulting issue state with read-only `bd show`, but do not hash, prefix-validate, parse, or compare those generated ledgers as a condition of the implementation review verdict.

After reconciliation, rerun the Step 1 implementation path inventory. If another implementation path was added, removed, staged, or unstaged during reconciliation, return INCONCLUSIVE. Generated-ledger-only changes preserve the review verdict.

## Step 7: Hand off

- `NOT CLEAN`: list blocking Critical/High Beads ids and tell the implementer to fix, re-run focused tests and quality gates, then request verification from the original Codex and Claude child sessions through the TermAl UI. If those sessions cannot be continued, stop and ask for explicit direction; a fresh review is additional evidence but does not satisfy literal same-session verification.
- `INCONCLUSIVE`: report the missing reviewer/tool/packet condition. It does not satisfy the commit discipline.
- `CLEAN`: state that the review gate passed, including any reconciled Medium/Low Beads. Do not commit or push inside this command; return control to the outer workflow.

Current TermAl MCP does not expose a follow-up call to an existing child session. Re-running this command creates fresh Codex/Claude reviewer sessions and therefore cannot claim literal same-session verification of a fixed Critical/High finding. Continue with the original child sessions through the TermAl UI or stop and ask for direction; do not silently claim compliance.
