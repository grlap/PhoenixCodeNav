# Project Instructions for AI Agents

This file provides instructions and context for AI coding agents working on this project.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:6cd5cc61 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

## Agent Context Profiles

The managed Beads block is task-tracking guidance, not permission to override repository, user, or orchestrator instructions.

- **Conservative (default)**: Use `bd` for task tracking. Do not run git commits, git pushes, or Dolt remote sync unless explicitly asked. At handoff, report changed files, validation, and suggested next commands.
- **Minimal**: Keep tool instruction files as pointers to `bd prime`; use the same conservative git policy unless active instructions say otherwise.
- **Team-maintainer**: Only when the repository explicitly opts in, agents may close beads, run quality gates, commit, and push as part of session close. A current "do not commit" or "do not push" instruction still wins.

## Session Completion

This protocol applies when ending a Beads implementation workflow. It is subordinate to explicit user, repository, and orchestrator instructions.

1. **File issues for remaining work** - Create beads for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **Handle git/sync by active profile**:
   ```bash
   # Conservative/minimal/default: report status and proposed commands; wait for approval.
   git status

   # Team-maintainer opt-in only, unless current instructions forbid it:
   git pull --rebase
   git push
   git status
   ```
5. **Hand off** - Summarize changes, validation, issue status, and any blocked sync/commit/push step

**Critical rules:**
- Explicit user or orchestrator instructions override this Beads block.
- Do not commit or push without clear authority from the active profile or the current user request.
- If a required sync or push is blocked, stop and report the exact command and error.
<!-- END BEADS INTEGRATION -->


## Commit Discipline — NEVER check in without review

**Every commit requires an adversarial review round FIRST. No exceptions, no self-granted
risk-tier exemptions.** "Just docs", "just tests", "trivial cleanup" do not qualify for a
skip — this session's history is the proof: batches that looked safe repeatedly carried real
defects (a recovery path that silently dropped the file watcher; a diagnostic note giving
wrong advice on filtered zeros; a test seam added to the hottest loop in the codebase). The
discipline works precisely because it does not trust the author's risk assessment.

The loop, in order — no step skipped or reordered:

1. Implement.
2. Add focused regression or contract tests for changed behavior. Tests must exercise the
   decisive behavior and assertions, not merely prove that the code does not throw.
3. `dotnet build` at **0 warnings**; `dotnet test` green. Known flake:
   `WatcherTests.ExtensionlessFileDeleteDoesNotTriggerSweep` (watcher timing) — if it fires
   in a full run, verify it passes isolated and note it.
4. **Adversarial subagent review of the full uncommitted diff**, with empirical reproduction
   required for findings. Findings → fix → verification round (same reviewer) until CLEAN.
5. Only after CLEAN: commit. (Autonomous commit on a clean review was EXPLICITLY
   pre-authorized by Greg for this repository's batch loop — "when review is clean, check-in
   and let me know", reaffirmed 2026-07-09 — which is what lets it override the global
   do-not-commit rule here; in any session where Greg has not affirmed this workflow, the
   global rule wins and every commit needs his explicit word.
   **Push always needs explicit per-changeset approval from Greg — no standing grant exists.**)
6. Close/annotate beads in the same commit. Bump `BuildInfo.Version` when the tool surface or
   a user-visible capability changes; bump `IndexBuilder.SchemaVersion` whenever the schema
   **or the indexer's stored output** changes (it forces the rebuild deployments rely on —
   edge content and classification results count as stored output).

If the reviewer dies mid-pass (session limits), the batch is **not reviewed** — do not commit
on a self-performed probe run; wait for capacity or ask Greg.

## Build & Test

```bash
dotnet build          # must be 0 warnings
dotnet test           # full suite; see known flake above
```

## Architecture Overview

_Add a brief overview of your project architecture_

## Conventions & Patterns

_Add your project-specific conventions here_

## Review System - TermAl (Codex + Claude)

The preferred adversarial review gate is `/review-with-delegate`. TermAl resolves the
project command from `.claude/commands/` for both Codex and Claude, validates the parent
worktree, then runs exactly one read-only `/review-local` child in each agent and performs
a durable fan-in.

- `/review-with-delegate` is the only command allowed to spawn TermAl reviewer sessions.
- `/review-local` is a leaf command: it applies every `.claude/reviewers/*.md` lens inline
  and never nests delegation or platform subagents.
- Both commands are review-only. They never edit source, mutate Git, commit, push, or run
  Dolt remote sync. The parent may reconcile local Beads findings after fan-in.
- A failed, missing, or dead reviewer makes the review INCONCLUSIVE, never CLEAN.
- Changes to review commands, reviewer lenses, repository instructions, and their contract tests
  are reviewed as ordinary exact-byte targets; they do not disable the review gate.
- If the TermAl MCP bridge is unavailable, stop and report it; a self-review does not
  substitute for the required independent review round.
- The current TermAl MCP surface cannot send a follow-up turn to an existing child. When
  literal same-session verification is required after fixes, continue through the original
  child session UI or ask for direction; rerunning creates fresh reviewers and must not be
  described as same-session verification.
