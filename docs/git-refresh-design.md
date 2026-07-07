# Design: Git-aware index refresh (pull, branch switch, merge, rebase)

Status: **Proposed** (design only — no implementation yet).
Relates to: [`design.md`](./design.md) § Freshness. Backlog: PhoenixCodeNav git-aware refresh (P2).

## Problem

Bulk git operations — `git checkout <branch>`, `git pull`, `git merge`, `git rebase`,
`git reset --hard`, `git stash pop` — mutate many working-tree files at once and move
`HEAD`. Today the index reconciles only **indirectly**:

- git rewrites working-tree files → the `FileSystemWatcher` sees per-file
  create/change/delete → a (possibly large) debounced delta batch; on FSW-buffer overflow
  the overflow handler runs a full detect-all sweep; directory add/remove escalates to a
  sweep; the startup sweep is a backstop.

This *converges*, but has real gaps:

1. **Staleness window** — for the debounce (~600 ms) + refresh duration the index lags the
   new branch. It is signaled (`indexStatus: refreshing/stale`, `pendingChanges`), but an
   agent that ignores the signal can act on stale results.
2. **No git-awareness** — the index tracks *files*, not the current commit. Convergence
   relies on catching every file event (or overflowing into a sweep). Robust in practice,
   not *guaranteed* for every git plumbing path (e.g. events dropped under load, tooling
   that touches files in ways the FSW coalesces oddly).
3. **No commit identity** — nothing records which commit the index reflects, so a tool
   cannot say "index is for commit X, working tree is now on Y."
4. **Whole-repo work for a small diff** — a branch switch that changes 20 files can, on
   overflow, trigger a full ~55 s rescan instead of a 20-file delta.

## Goals

- Detect `HEAD`/ref changes **explicitly** and reconcile promptly and deterministically.
- Prefer a **diff-scoped** refresh (only the files git changed) over a full rescan.
- Record the **commit the index reflects** — needed to compute the diff, and cheap to
  surface in `repo_overview`.
- **Degrade gracefully**: git absent / not a repo / diff unavailable → never error, fall
  back to the current behavior.
- **No double work** when both the git signal and FSW fire for one operation.

## Design intent (decided)

**Detect the change, rebuild what changed, and accept a brief lag while reconciling.** The
existing `indexStatus: refreshing` + `pendingChanges` on every response already signals that
lag; results catching up a few hundred ms after a switch is not a real problem. So this
design deliberately does **not** add a per-response or per-project "HEAD diverged" staleness
contract — that machinery is not worth it for a low-impact window. Keep it simple.

## Non-goals

- Git *history* navigation (`recent_changes` / blame) — a separate feature.
- Non-git VCS.
- Blocking queries during refresh — stay non-blocking; keep signaling staleness.

## Design overview

A hybrid: **watch git metadata files for the trigger, use the git CLI for the computation.**

```
.git/HEAD, refs, packed-refs change
  -> GitWatcher (debounced)                     [signal]
  -> resolve current HEAD commit (git rev-parse) [identity]
  -> if changed vs indexed commit:
       git diff --name-only <indexed> <head>     [scope]
       -> enqueue a targeted DeltaRefresher batch on IndexManager's pump
       -> record indexed_commit = head in meta
  -> fall back to a full detect-all sweep when scoping is impossible
```

### 1. Trigger — what to watch

`.git/` is currently excluded from both the scanner and the `WorkspaceWatcher`. Add a
**separate, narrow** `GitWatcher` on the git metadata directory:

- Watch: `HEAD` (branch switch, commit), `packed-refs` and `refs/heads/**` (pull, fetch,
  merge, commit), `MERGE_HEAD` / `ORIG_HEAD` (merge/rebase/reset markers).
- Ignore: `index`, `index.lock`, `objects/**`, `logs/**` (pure noise).
- **`.git`-as-a-file** (linked worktrees, submodules): `.git` may be a file
  `gitdir: <path>`; resolve it to locate the real metadata dir. If it can't be resolved,
  disable the git watch and stay FSW-only.

Rationale for file-watch (not polling): zero idle cost, immediate signal. Rationale for CLI
computation (not parsing refs by hand): robust across packed vs loose refs, worktrees,
detached HEAD — without a libgit2 dependency (keeps the "no heavy deps" ethos; `git` is
universally on a dev PATH).

### 2. Identity — detect the transition

Persist `indexed_commit` (SHA) and `indexed_branch` in the `meta` table. On a debounced git
signal:

1. `git rev-parse HEAD` → `head`.
2. If `head == indexed_commit` → no-op (spurious/loose-ref churn).
3. Else reconcile (below), then set `indexed_commit = head`, `indexed_branch = <symbolic-ref>`.

Debounce the git signal **300–500 ms** so a `rebase`/interactive op that moves HEAD many
times reconciles once against the *final* HEAD, and a `pull` (fetch updates refs, then
merge updates HEAD+tree) coalesces into one reconcile.

### 3. Scope — diff-scoped refresh

Prefer a targeted batch over a full sweep:

- `git diff --name-only <indexed_commit> <head>` → changed paths (added/modified/deleted),
  repo-relative. Map to workspace-relative, drop excluded/unwatched, hand to
  `DeltaRefresher.Refresh(batch)` — which already handles add/change/delete via hash +
  `File.Exists` and rebuilds the project graph if a `.csproj`/`.sln` changed.
- **Uncommitted changes** are *not* in `diff <old> <head>`, but the FSW covers those
  independently (a checkout that touches uncommitted files also fires FSW events). After a
  git reconcile the index reflects the new *committed* state; the working-tree delta is the
  watcher's job. (See §5 on overlap.)

**Caps & fallbacks — always converge, never error:**

| Condition | Action |
|---|---|
| `indexed_commit` unknown (first run / legacy index) | full detect-all sweep, then record `head` |
| diff size > cap (e.g. 5,000 files) | full detect-all sweep (guard against pathological batches) |
| `git` CLI missing / not a repo / `.git` unresolvable | FSW-only (current behavior); log once |
| `git diff` fails (shallow clone, unrelated histories after force-push) | full detect-all sweep |
| detached HEAD / checkout of a bare commit | `rev-parse` yields the SHA → same flow |

### 4. Metadata (mechanism, not a new staleness contract)

- Persist `indexed_commit` + `indexed_branch` in `meta` — required to compute the diff
  against the new HEAD, and cheap to expose in `repo_overview` (`indexedCommit`,
  `indexedBranch`, current `headCommit`) for humans and curious agents.
- Reconcile status uses the **existing** signal: the pump sets `indexStatus: refreshing`
  while applying the batch, and `pendingChanges` stays non-zero until it drains — no new
  fields. Per *Design intent* above, there is deliberately no per-response/per-project
  HEAD-divergence flag; a brief lag is acceptable.

### 5. Interaction with the existing FSW watcher (no double work)

A branch switch fires **both** the git signal and per-file FSW events. Correctness rests on
one property already true of the pipeline:

- Both feed the **same serialized refresh pump** (IndexManager's channel), and
  `DeltaRefresher` is **idempotent** — it hashes each file and skips unchanged ones. So a
  git-scoped reconcile followed by the overlapping FSW batch finds identical hashes and does
  nothing. The overlap is cheap; **do not** suppress FSW batches (suppression risks dropping
  genuinely-uncommitted concurrent edits — correctness over micro-optimization).
- The FSW overflow→sweep remains as an independent backstop.

### 6. Component shape

- `GitWatcher` (Core, alongside `WorkspaceWatcher`): resolves the git dir, watches the
  trigger files, debounces, and calls back into `IndexManager` with "HEAD changed."
- `GitRefresh` (Core helper): `rev-parse`, `diff --name-only`, gitdir resolution — thin
  shell-outs to `git`, each wrapped so any failure returns "scope unavailable → full sweep."
- `IndexManager`: owns `indexed_commit`, wires the `GitWatcher` callback into the existing
  pump (as either a targeted batch or a `null` full-sweep signal), and reports git identity
  in `Health()`.

## Testing

- **Branch switch**: temp git repo, commit A with `.cs` files, build index (records A);
  create branch B with added/removed/modified files; `git checkout B` → assert the index
  reflects B (adds/deletes/changes) via the diff-scoped path, and `indexed_commit == B`.
- **Pull / fast-forward**: commit on a tracking branch, `git merge --ff-only` → assert
  reconcile.
- **Rebase churn**: multi-commit rebase → assert a *single* reconcile against final HEAD
  (debounce coalescing).
- **Dirty tree + checkout**: uncommitted edits present → assert both committed (git-scoped)
  and uncommitted (FSW) changes land.
- **git absent / non-repo**: point at a non-git dir → FSW-only, no error, no crash.
- **Large diff cap** → full-sweep path taken.
- **Idempotency**: git reconcile + overlapping FSW batch → no duplicate file/symbol rows.
- **Worktree/submodule**: `.git`-as-a-file resolves to the real gitdir (or cleanly falls
  back).

## Rollout

Additive and safe-by-default: enable the git watch when a resolvable `.git` and a working
`git` CLI are present; otherwise behave exactly as today. A `--no-git-watch` escape hatch is
cheap to add. No schema break — `meta` gains two keys. Ship behind the same
build→bench→multi-round-review loop as prior batches, with the tests above.

## Open questions

- Diff-size cap value — measure the crossover where a targeted batch stops beating a full
  sweep on the real work repo (likely a few thousand files).
- Rename detection (`git diff -M`) — treat a rename as delete+add (simpler, already
  supported) or track identity? Delete+add is fine for a text/symbol index.
