---
name: review-local
description: Run an inline read-only PhoenixCodeNav review through every specialized lens.
metadata:
  termal:
    title:
      strategy: default
---

Review the current PhoenixCodeNav staged, unstaged, and untracked changes through every repository reviewer lens.

**IMPORTANT: `/review-local` is read-only. Never edit source, mutate Git state, commit, push, or fix findings. Do not run required formatters, builds, or tests here; `/review-with-delegate` owns validation before creating read-only reviewers. Safe read-only inspection and targeted non-writing probes are allowed.**

**IMPORTANT: `/review-local` must never spawn another reviewer. Do not call TermAl delegation tools, platform subagents, Claude Task agents, Codex collaboration agents, shell-launched agents, raw delegation APIs, or polling tools. When this command runs as a delegated child, every lens is applied inline in this same session.**

When the session context says `You are a delegated child session for TermAl delegation`, state in the result that nested reviewer spawning was intentionally skipped, then apply all lenses inline.

**IMPORTANT: Beads is canonical. In a delegated/read-only session, do not run mutating `bd` commands. Return exact proposed creates, updates, reopens, or closes for the parent `/review-with-delegate` session to reconcile.**

## Phase 1: Establish the exact change set

Run this path-only inventory. Do not emit patch content yet:

```text
git --no-optional-locks status --short
git --no-pager diff --no-ext-diff --no-textconv --no-color --name-only
git --no-pager diff --cached --no-ext-diff --no-textconv --no-color --name-only
git ls-files --others --exclude-standard
```

If any inventory command fails or its path output is truncated or malformed, return a lifecycle `Status: completed` packet with `Review verdict: INCONCLUSIVE` and stop before reading target content.

The target is the union of staged, unstaged, and ordinary untracked files. If the target is empty, return a `## Result` packet with lifecycle `Status: completed`, `Review verdict: INCONCLUSIVE` in `Summary:`, and an explanation that no changes were visible to the child even though the parent requested review, then stop. Review-policy and instruction files are ordinary review targets: inspect their exact dirty bytes instead of refusing the review.

Record the absolute repository root with `git rev-parse --show-toplevel`.

Before hashing, diffing, or opening target content, inspect changed-entry metadata without following links or reparse points; apply the same lstat/containment check to the instruction files about to be read. Require every path and traversed ancestor to remain inside the repository root. Treat tracked symlinks as Git link metadata and never dereference them; if an untracked symlink/junction/reparse point or any resolved path can escape the root, return `Review verdict: INCONCLUSIVE` without reading it.

Read the current `CLAUDE.md` and `AGENTS.md`; their commit, Beads, build, and repository-safety rules govern the remaining review. If either file is dirty, it is also part of the exact review target and must be inspected adversarially.

Only after the containment checks pass, run the content-bearing diffs:

```text
git --no-pager diff --binary --no-ext-diff --no-textconv --no-color
git --no-pager diff --cached --binary --no-ext-diff --no-textconv --no-color
```

Calculate and retain the same target identity used by the parent command: HEAD/unborn state, deterministic unstaged and staged patch hashes, and sorted untracked path/content hashes. Hash deterministic patch streams through a binary-safe direct native pipe so their bytes are not surfaced a second time. Include the identity in the final result. Recompute it immediately before returning; any drift during review makes the result INCONCLUSIVE.

After that check passes, inspect every untracked entry and read every reviewable text/source file plus relevant surrounding implementation/tests; untracked content is not present in `git diff`. For a binary or path too large to read safely, record its metadata and make the review INCONCLUSIVE unless it is demonstrably irrelevant or intentionally generated.

Read `README.md`, `docs/design.md`, and relevant implementation/tests as the diff requires.

Raw Git state is authoritative. If the Phoenix MCP server is attached and ready, `review_pack` may be used with explicit changed paths as a secondary lead source, followed by semantic escalation for selected symbols. Never treat an empty or truncated `review_pack` response as proof of completeness, and never skip raw diff/source inspection because of it.

## Phase 2: Broad adversarial scan

Review every changed file yourself across all of these concerns before applying the specialized files:

- correctness and edge cases;
- architectural boundaries;
- Git, index freshness, and path semantics;
- Roslyn semantic accuracy and degradation honesty;
- MCP schema, stable contracts, budgets, cursors, and truncation;
- untrusted-workspace/process security;
- concurrency, deadlines, memory, and large-monorepo performance;
- regression-test quality.

Read relevant callers and tests on demand. Do not rely on the diff alone.

For each suspected defect, attempt a safe empirical confirmation using existing tests, read-only commands, or a non-mutating trace. If reproduction would require writes forbidden by reviewer mode, label the finding `Reasoned, not reproduced` and explain the missing probe. Never present speculation as reproduced fact.

Severity:

- **Critical** - arbitrary execution, data loss/corruption, security boundary break, or a false-clean review that can authorize unsafe work.
- **High** - incorrect behavior, false exactness/completeness, stale-index misdirection, race, hang, or broken public contract.
- **Medium** - meaningful reliability, maintainability, observability, or test-coverage defect.
- **Low** - minor but actionable clarity, naming, or robustness issue.
- **Note** - informational only; no follow-up required.

## Phase 3: Apply every reviewer lens inline

Discover and read every `.claude/reviewers/*.md` file. Apply each checklist inline against the same complete change set. Do not spawn agents for individual lenses.

If the lens directory is absent, empty, or any lens is unreadable, return `INCONCLUSIVE`; never silently reduce coverage and report CLEAN.

The required Phoenix lens files are exactly:

- `.claude/reviewers/architecture.md`
- `.claude/reviewers/index-freshness.md`
- `.claude/reviewers/semantic-correctness.md`
- `.claude/reviewers/mcp-contract.md`
- `.claude/reviewers/security.md`
- `.claude/reviewers/performance.md`
- `.claude/reviewers/testing.md`

Require every listed file to exist and be readable. Extra reviewer files are allowed and must also be applied.

### Accepted project patterns

Do not flag these by themselves:

- Indexed or heuristic fallbacks when they are labeled honestly and carry the required partial/coverage signal.
- Omission of null/false JSON fields where the existing serialization contract intentionally uses omission-on-nothing-to-say.
- `idx:` symbol handles being index-local when stale-handle detection remains intact.
- Roslyn `AdhocWorkspace` and direct project parsing instead of MSBuild; this is a core design constraint.
- Read-only Git CLI use when arguments, helpers, time, output, path roots, and wrapper transport are bounded safely.
- The documented isolated watcher timing flake, but only when the parent validation proved the isolated rerun passes.

## Phase 4: Contract-specific checks

Always verify the following when touched:

1. A new user-visible capability has its own singular, grep-able `features[]` id and an appropriate `BuildInfo.Version` bump.
2. A schema or indexer-stored-output change bumps `IndexBuilder.SchemaVersion` and forces the required rebuild.
3. Counts honor filters; bounded counts are labeled lower bounds; every cap and trim is observable.
4. Confidence (`exact|indexed|heuristic`), navigation layer, partialReason, coverage, timing, and freshness metadata match the evidence actually returned.
5. Stable note ids identify one cause each; prose may evolve without changing the id.
6. Regression and contract tests structurally exercise the changed behavior and contain decisive assertions that would fail under obvious broken implementations.

## Phase 5: Consolidate

Deduplicate findings across the broad scan and lenses. Keep independent agreement in the evidence. Use precise `file:line` locations and avoid style-only noise.

Return a compact TermAl-parser-compatible result packet with these exact labels. `Status` is delegation lifecycle, not the review verdict:

```markdown
## Result
Status: completed

Summary:
Review verdict: CLEAN | NOT CLEAN | INCONCLUSIVE
Target identity: ...
One-paragraph review summary.

Findings:

- Critical `file:line` - Description. Reproduction: Reproduced | Reasoned, not reproduced. Why it matters: ... Evidence: ... Suggested fix: ...
- High `file:line` - Description and evidence. Suggested fix: ...
- Medium `file:line` - Description and evidence. Suggested fix: ...
- Low `file:line` - Description and evidence. Suggested fix: ...

Notes:
- ...

Lens Summaries:
- Architecture: ...
- Index Freshness and Git: ...
- Semantic Correctness: ...
- MCP Contract and Honesty: ...
- Security: ...
- Performance and Concurrency: ...
- Testing: ...

Proposed Beads Updates:
- Search/update/create/reopen/close: ...

Commands Run:
- ...

Files Inspected:
- ...
```

For a clean review, use this exact empty section:

```text
Findings:
- None
```

Every actionable finding must be one physical list line beginning with the plain severity token (`Critical`, `High`, `Medium`, or `Low`); Markdown decoration around the severity and indented evidence bullets are parser-incompatible. Put any additional supporting detail under `Notes:` instead.

The `Review verdict` is CLEAN only when there is no actionable Critical/High/Medium/Low finding. Note-only observations are allowed. If the available diff or context is incomplete, a required lens is missing, target identity drifted, or a serious risk remains unverified because read-only policy blocked the required probe, use `Review verdict: INCONCLUSIVE`, not CLEAN. Keep lifecycle `Status: completed` when the command itself completed successfully; use `Status: failed` only when command execution failed.

## Phase 6: Propose Beads reconciliation

Search read-only with `bd search ... --status all` when available. Do not mutate Beads in delegated reviewer mode.

For each remaining actionable finding, propose one exact action with:

- existing Bead id to update/reopen, or a deduplicated new title;
- issue type (`bug` or `task`);
- priority derived from severity;
- `file:line`, why it matters, reproduction status, and suggested fix;
- labels `review`, severity, and reviewer domain.

If the diff demonstrably fixes an open issue, propose closure with the acceptance evidence. If no reconciliation is needed, state `Beads is up to date - no changes needed.`
