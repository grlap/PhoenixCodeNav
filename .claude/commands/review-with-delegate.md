---
name: review-with-delegate
description: Review current PhoenixCodeNav changes through independent Codex and Claude TermAl delegations.
metadata:
  termal:
    title:
      strategy: default
---

Review the current staged, unstaged, and untracked changes by running `/review-local` in one Codex and one Claude TermAl reviewer session.

**IMPORTANT: This is a review-only command. Do not modify source files, stage, stash, checkout, reset, commit, push, run `bd dolt push`, or run any other mutating Git/remote-sync operation. Parent validation may create normal ignored build/test artifacts; parent-session local Beads reconciliation is the only permitted tracked/project workflow mutation. This command grants no commit authority; after a check-in-eligible result the outer workflow must still follow `CLAUDE.md` / `AGENTS.md`. Push always requires explicit per-changeset approval.**

**IMPORTANT: Beads (`bd`) is the canonical tracker for findings. Do not create markdown bug lists. Delegated read-only reviewers propose Beads actions; this parent command performs deduplicated reconciliation after fan-in.**

**IMPORTANT: Attempt exactly two reviewer spawns through the TermAl MCP bridge: one Codex and one Claude. Do not use platform subagents, Claude Task agents, Codex collaboration agents, shell-launched agents, raw HTTP, synchronous shell polling, or nested TermAl delegation for this command. `/review-local` is deliberately non-nesting. If the TermAl delegation tools are unavailable, stop and report that the bridge is required.**

Required TermAl MCP tools:

- `termal_spawn_session`
- `termal_get_session_status`
- `termal_get_session_result`
- `termal_resume_after_delegations`

## Step 1: Confirm the review target

Run this path-only inventory from the repository root. Do not emit patch content or run diff checks yet:

```text
git --no-optional-locks status --short
git --no-pager diff --no-ext-diff --no-textconv --no-color --name-only
git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --name-only
git ls-files --others --exclude-standard
```

If any inventory command fails or its path output is truncated or malformed, return INCONCLUSIVE before reading content or spawning reviewers.

The review target is the union of staged, unstaged, and ordinary untracked files. If that target is empty, tell the user there is nothing to review and stop.

Record the absolute repository root from `git rev-parse --show-toplevel`; pass it as `cwd` to both TermAl sessions so the local slash command resolves from this repository.

Before hashing, diffing, or opening target content, inspect changed-entry metadata without following links or reparse points. Require every path and traversed ancestor to remain inside the repository root. Treat tracked symlinks as Git link metadata and never dereference them; if an untracked symlink/junction/reparse point or any resolved path can escape the root, return INCONCLUSIVE without reading it.

Only after the bootstrap and containment checks pass, run:

```text
git --no-pager diff --no-ext-diff --no-textconv --no-color --check
git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --check
```

If either diff check reports an error, stop and report it before delegation.

Record a deterministic target identity containing:

- `HEAD` from `git rev-parse HEAD` (or the explicit unborn-HEAD state);
- a hash of `git --no-pager diff --binary --no-ext-diff --no-textconv --no-color`;
- a hash of the equivalent `--cached` patch;
- a sorted manifest of every untracked path plus its read-only `git hash-object --no-filters -- <path>` content hash.

Use a binary-safe direct native pipe into `git hash-object --stdin` for each deterministic patch stream so patch bytes are not emitted into the conversation; do not pass `-w`. Preserve this identity for comparison with both children and the post-fan-in worktree.

## Step 2: Validate before delegation

Validation belongs in the writable parent, not in read-only reviewer children.

1. Confirm reintroduction-test evidence for behavior changes or bug fixes. Accept evidence from the active session, Beads notes, or the user's handoff. If a regression test was never demonstrated red with the defect reintroduced, stop and report that the repository's commit discipline is not satisfied. Do not invent evidence.
2. Run `dotnet build PhoenixCodeNav.sln -c Release --no-restore`. Validation must use the dependency graph already restored by the implementation session; it must not unexpectedly download packages. If assets are missing or stale, stop and ask the implementer to restore explicitly before review.
3. Require a successful build with literal `0 Warning(s)` and `0 Error(s)`.
4. Run `dotnet test PhoenixCodeNav.sln -c Release --no-build --no-restore`.

If build or tests fail, stop and report the output. The sole documented exception is `WatcherTests.ExtensionlessFileDeleteDoesNotTriggerSweep`: when it is the only failure, rerun that exact test in isolation. Continue only if the isolated rerun passes, and carry the flake note into the final review. Do not silently bless any other intermittent failure.

After validation, restart all of Step 1 from its path-only inventories. Reapply no-follow containment before any diff check, content hash, or spawn; scan reviewable untracked text files for conflict markers and whitespace errors only after those checks pass. Recompute the target identity. If validation changed the identity, repeat Step 2 against the new identity and then restart all of Step 1 again. Never validate one byte set and send another to reviewers.

## Step 3: Attempt exactly two delegated reviewers

Attempt exactly two reviewer session spawns. Call `termal_spawn_session` exactly twice, even if the first attempt fails. Never retry either slot.

1. Codex reviewer:
   - `agent`: `Codex`
   - `prompt`: `/review-local`
   - `mode`: `reviewer`
   - `writePolicy`: exactly `{"kind":"readOnly"}`
   - `title`: `Codex /review-local`
   - `cwd`: the absolute repository root from Step 1
2. Claude reviewer:
   - `agent`: `Claude`
   - `prompt`: `/review-local`
   - `mode`: `reviewer`
   - `writePolicy`: exactly `{"kind":"readOnly"}`
   - `title`: `Claude /review-local`
   - `cwd`: the same repository root

Read-only shared-worktree sessions are intentional: both reviewers must see the exact staged, unstaged, and untracked state. Do not request an isolated worktree.

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

Before accepting either packet, compare both child-reported target identities with the pre-spawn identity, then recompute the current parent identity. Any mismatch means validation/review covered different bytes: return INCONCLUSIVE and do not claim a review gate.

Deduplicate findings without erasing independent agreement. If both reviewers found the same root issue, merge it and state that both caught it. Resolve severity disagreements explicitly.

Use this shape:

```markdown
# Delegated Review

## Validation
- Reintroduction evidence: ...
- Release build: ...
- Full suite: ...
- Known flake note: ...

## Codex /review-local
- Status: ...
- Verdict: ...
- Findings: ...
- Evidence / commands: ...

## Claude /review-local
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

## Check-in Gate
- ELIGIBLE | BLOCKED | INCONCLUSIVE
```

`CLEAN` requires both requested reviewers to complete successfully, return complete non-truncated packets, leave no unresolved serious risk, and report no actionable finding at any severity. Note-only observations may remain. A missing/dead reviewer or incomplete packet makes the result `INCONCLUSIVE`, never CLEAN.

Check-in eligibility is a separate aggregate decision. `ELIGIBLE` requires both requested
reviewers to complete against the exact reviewed target identity and requires the parent identity to remain unchanged.
There must be no unresolved Critical or High finding from either reviewer.
Medium and Low findings do not block check-in after they are reconciled into Beads. Any Critical or High finding
makes the gate `BLOCKED`; any lifecycle failure, incomplete packet, `INCONCLUSIVE` verdict, or
identity drift makes the gate `INCONCLUSIVE`.

## Step 6: Reconcile Beads in the parent

For every consolidated actionable finding:

1. Search before creating:
   - `bd search "<short finding title>" --status all`
   - `bd search --desc-contains "<file:line or unique phrase>" --status all` when useful.
2. Update an existing open issue with `bd update <id> --append-notes "<review evidence>"` when it tracks the same root cause.
3. Reopen a regressed closed issue or create a `discovered-from:<active-id>` follow-up when history should remain intact.
4. Create a new issue only when no existing Bead covers it. Include literal `## Problem`, `## Steps to Reproduce`, and `## Acceptance Criteria` sections, design guidance, and use `bd create --validate`.
5. Close an open issue only when the reviewed diff actually satisfies its acceptance criteria.

Severity mapping:

- Critical -> `P0`
- High -> `P1`
- Medium -> `P2`
- Low -> `P3`

Use `bug` for correctness, security, data-loss, freshness, concurrency, protocol, or lifecycle defects. Use `task` for pure tests or documentation. Add labels `review`, `severity-critical|high|medium|low`, and a domain label such as `git`, `indexing`, `semantic`, `mcp-contract`, `security`, `performance`, or `testing`.

Do not modify source files during reconciliation. If no changes are required, state `Beads is up to date - no changes needed.`

After reconciliation, rerun `git --no-optional-locks status --short` and the changed-file inventory. If Beads export files or any other reviewed path changed, the prior result no longer covers the complete diff: return NOT CLEAN and require another review round.

## Step 7: Hand off

- `BLOCKED`: list the Critical/High Beads ids. Any fix changes the target identity, so re-run reintroduction verification, quality gates, and a new complete dual review against the new bytes.
- `INCONCLUSIVE`: report the missing reviewer/tool/identity condition. It does not satisfy the commit discipline.
- `ELIGIBLE`: state whether the review verdict is CLEAN or NOT CLEAN with only Medium/Low findings, list the reconciled non-blocking Beads ids, and return control to the outer workflow. Do not commit or push inside this command.

Current TermAl MCP does not expose a follow-up call to an existing child session. When a fix changes the target identity, a fresh complete Codex/Claude pair is acceptable because it reviews the new bytes. Do not describe a fresh pair as same-session verification.
