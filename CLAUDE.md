# Project Instructions for AI Agents

This file provides instructions and context for AI coding agents working on this project.

## Work Tracking

This repository tracks its work in Engram. In a TermAl-hosted session the
injected `engram` MCP tools (next, ls, show, add, claim, update, note,
done, handoff, search) ARE the words — use them directly. The shell form
below serves humans and hosts; it needs `engram` on PATH and the host
environment (`ENGRAM_HOME`, `ENGRAM_ACTOR_ID`, `ENGRAM_SESSION_ID`,
`ENGRAM_WORK_AUTHORITY_GRANT` — the grant value comes from a host-private
file and is never typed or logged):

```bash
engram work next                  # what you hold, what is ready, what changed
engram work ls | show REF
engram work add "Title" [--under REF]
engram work claim REF
engram work note "what you found or decided"
engram work done ["what was delivered"]
```

- Claim before you change anything; note decisions and evidence once;
  `done` tells you what is still owed. Receipts carry `next:` commands —
  follow them.
- File follow-up work with `engram work add`; findings and decisions go
  into `note` on the item they concern.
- Never place work refs in source comments, identifiers, or docs prose.

At session end: run the quality gates if code changed, update your Engram
items (`note`, `done`), report changed files and validation, and wait for
explicit authority before any commit or push.


## Standing Directives & Known Facts

Carried from the previous tracker's memories; Greg's directives unless
marked as facts.

- For design/API decisions and meaningful implementation choices,
  proactively consult an independent opinion before implementing.
- No tests of infrastructure, build targets, scripts, or repository policy
  files — reaffirmed 2026-09-03 ("again").
- Never create feature, fix, review, or temporary Git branches for this
  repository; work on the default branch.
- Do not introduce or lower any MCP or internal count, byte, transfer,
  response, result, file, project, candidate, or scan limit without
  explicit approval.
- PhoenixCodeNav is assumed to run in a safe, trusted environment;
  usability outranks defensive hardening.
- Paired legacy `project.<ext>` and SDK-style `project.Net.<ext>` files:
  resolution authority follows the architecture directive — the pair is
  one project, never two competing ones.
- features[] manifest discipline: every new feature that reuses an
  existing envelope gets its own `features[]` entry.
- Fact: `dotnet build projA projB` fails with MSB1008 and builds
  NOTHING — build projects separately.
- Fact: the Grep/ripgrep regex engine has no lookahead; `(?!…)`/`(?=…)`
  patterns silently match nothing.
- Fact: reusable live F# MCP canary lives at
  `C:\Temp\PhoenixFSharpParseContextCanary` (covers legacy
  `Project.fsproj` parse context).
- Fact: RavenDB build profile reference (24-core, Release): ~9.6 s wall
  dominated by the single SQLite writer thread.

## Commit Discipline — NEVER check in without review

**Every commit requires an adversarial review round FIRST. No exceptions and no review
exemptions.** "Just docs", "just tests", "trivial cleanup" do not qualify for a
skip — this session's history is the proof: batches that looked safe repeatedly carried real
defects (a recovery path that silently dropped the file watcher; a diagnostic note giving
wrong advice on filtered zeros; a test seam added to the hottest loop in the codebase). The
discipline works precisely because it does not trust the author's risk assessment.

The loop, in order — no step skipped or reordered:

1. Implement.
2. Add focused regression or contract tests for changed behavior. Tests must exercise the
   decisive behavior and assertions, not merely prove that the code does not throw.
3. `dotnet build` at **0 warnings**; `dotnet test` green;
   `pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1` green against the pinned Roslyn/F#
   submodules; and `node ./website/verify.mjs` green. The MCP harness first requires each external
   checkout to match its pinned commit, then builds new isolated Roslyn and F# indexes through
   normal MCP startup and runs every assertion against those fresh indexes. It never updates a
   submodule, repairs an old index, or learns a new baseline automatically. A mismatched checkout,
   pre-existing explicit index path, baseline mismatch, missing prerequisite, integration failure,
   or solution-test failure blocks check-in. An isolated pass is diagnostic evidence only; it never
   converts a failed full suite into a green gate. Fix nondeterministic tests or product races.
   The complete suite requires directory-link support: NTFS junction creation on Windows
   (ordinary non-elevated NTFS is sufficient; Developer Mode is not required) and directory
   symbolic links on Unix. Failure of either prerequisite is infrastructure failure, never a
   silent green containment result.
4. **Adversarial subagent review of the full uncommitted implementation diff**, with empirical
   reproduction required for findings. Critical/High findings → fix → verification round with
   the same reviewer. Medium/Low findings → record in Engram; they do not block check-in.
5. Only after both reviewers complete and no Critical or High finding remains: commit.
   (Autonomous commit after the gate passes was EXPLICITLY
   pre-authorized by Greg for this repository's batch loop — "when review is clean, check-in
   and let me know", reaffirmed 2026-07-09 — which is what lets it override the global
   do-not-commit rule here; in any session where Greg has not affirmed this workflow, the
   global rule wins and every commit needs his explicit word.
   **Push always needs explicit per-changeset approval from Greg — no standing grant exists.**)
6. Update the Engram items (`note`, `done`) with the commit. Bump `BuildInfo.Version` when the tool surface or
   a user-visible capability changes; bump `IndexBuilder.SchemaVersion` whenever the schema
   **or the indexer's stored output** changes (it forces the rebuild deployments rely on —
   edge content and classification results count as stored output).

If the reviewer dies mid-pass (session limits), the batch is **not reviewed** — do not commit
on a self-performed probe run; wait for capacity or ask Greg.

**Failed gates are an investigation, never a stop.** When the build, the suite, or an external
gate fails, classify every failure in the same session and act:

- **Test or environment defect** (wrong assertion, stale fixture, host contention, missing
  prerequisite, an unpinned submodule): fix it in the current changeset and rerun the gates.
- **Product defect**: file one Engram item per defect with the failing test as the acceptance
  criterion (`engram work add "…" --accept "<test> passes" --kind bug --under <current item>`),
  mark the current item blocked on it when check-in depends on it, and fix it now when it is in
  scope. Never delete, skip, or loosen a test to make it pass.

Report the classification of every failure — test names, cause, action — before asking Greg for
a decision. "The suite failed" alone is not a report, and an INCONCLUSIVE review with unclassified
failures is not a finished turn.

## Build & Test

```bash
dotnet build          # must be 0 warnings
dotnet test           # full suite; every test must pass
pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1  # external Roslyn/F# MCP gate
```

## Architecture Overview

_Add a brief overview of your project architecture_

## Conventions & Patterns

_Add your project-specific conventions here_

## Review System - TermAl (Codex + Claude)

The preferred adversarial review gate is `/review-changes`. TermAl resolves the
project command from `.claude/commands/` for both Codex and Claude, validates the parent
worktree, then runs exactly one read-only `/review-code` child in each agent and performs
a durable fan-in.

- `/review-changes` is the only command allowed to spawn TermAl reviewer sessions.
- `/review-code` is a leaf command: it applies every `.claude/reviewers/*.md` lens inline
  and never nests delegation or platform subagents.
- Invoke `/review-changes` directly in the active writable parent session; never spawn it
  as a reviewer. Only its `/review-code` children use `writePolicy: readOnly`.
- Both commands are review-only. They never edit source, mutate Git, commit, push, or run
  tracker sync. The parent may record findings in Engram after fan-in.
- The parent compares implementation path inventories around validation, delegation, and
  fan-in. Reviewers do not compute or report Git-object, patch, or content hashes.
- A failed, missing, or dead reviewer or an incomplete packet makes the review INCONCLUSIVE.
- Critical and High findings block check-in. Medium and Low findings must be recorded in
  Engram but do not block check-in.
- Changes to review commands, reviewer lenses, repository instructions, and their contract tests
  are ordinary implementation review targets; they do not disable the review gate.
- If the TermAl MCP bridge is unavailable, stop and report it; a self-review does not
  substitute for the required independent review round.
- The current TermAl MCP surface cannot send a follow-up turn to an existing child. When
  same-session verification is required after fixing a Critical or High finding, continue
  through the original child session UI or ask for direction.
