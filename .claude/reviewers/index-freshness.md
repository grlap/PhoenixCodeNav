# Git / Index Freshness Review

Focus: Deterministic convergence from the working tree to the index, truthful Git baselines, and review-diff completeness.

## What to check

1. **Every convergence path**
   - Cold build, startup sweep, filesystem watcher, Git HEAD watcher, manual incremental refresh, full rebuild, and sibling-worktree reconciliation reach equivalent index state.
   - Added, changed, deleted, renamed, and re-added files are all covered.

2. **Watcher correctness**
   - File changes batch and debounce safely.
   - Directory create/rename/delete and watcher overflow escalate to a detect-all sweep.
   - Excluded directories and symlink/junction trees cannot enter via watcher or Git-targeted refresh.
   - Watchers created during startup cannot outlive disposal.
   - Commit-less repos attach the reflog watch when `.git/logs` first appears.

3. **Deterministic Git commands and parsing**
   - Git output cannot be changed by user config, external diff drivers, textconv, mnemonic/no-prefix settings, quoting, rename detection, pagers, or locale.
   - Diff parsing is stateful and accepts headers only inside a validated file section.
   - Content lines beginning with `---`, `+++`, or `@@` cannot be mistaken for headers.
   - Spaces, tabs, Unicode, CRLF, NUL-delimited status output, deleted paths, and pure renames are handled.
   - Truncated or malformed output fails closed to an honest sweep/error, never a partial successful set.

4. **Baseline advancement**
   - `indexed_commit` advances only after the corresponding refresh succeeds.
   - Startup reconciliation compares stored commit to current HEAD.
   - Branch metadata cannot remain falsely attached after detach/switch.
   - A failed or partial reconcile never moves the baseline past indexed content.

5. **Dirty-tree union**
   - Staged, unstaged, and untracked changes are all included where promised.
   - Tracked dirt is not mislabeled as untracked or widened to whole-file accidentally.
   - Concurrent working-tree edits are not lost while a HEAD reconcile runs.
   - Git and FSW overlap stays idempotent through the serialized pump.

6. **Project-structure refresh**
   - `.csproj`, solution, and supported build/config changes update all facts they control.
   - Do not claim graph refresh for MSBuild constructs Phoenix intentionally does not evaluate.
   - Deleting a project/build file is treated as structurally significant, not only modification/addition.

7. **Full rebuild recovery**
   - Runs on the refresh pump and cannot interleave with deltas.
   - Deletes SQLite sidecars before the main database.
   - Clears stale cached metadata and prior error state.
   - Reattaches filesystem and Git tracking when recovering from startup failure.

8. **Worktree indexes**
   - Targets come from `git worktree list`; arbitrary paths and bare/headless entries are rejected.
   - Seed uses a consistent `VACUUM INTO` snapshot.
   - A live target Phoenix is detected before writes.
   - Schema mismatch, commit movement, and all target dirt are reconciled; any incomplete set falls back honestly.

9. **Freshness envelope**
   - `state`, `pendingChanges`, `pendingProcessed`, timestamps, indexed commit, and HEAD-match fields reflect actual state.
   - No result looks ready/current while known changes are pending.

10. **`review_pack` diff completeness**
    - Preserve old-side and new-side evidence so deleted or renamed members inside surviving files remain reviewable.
    - Cover namespace/global-using/file-level hunks that intersect no ordinary member.
    - Pure file moves do not become false dangling-deletion warnings.
    - Any file/range/deletion/type cap emits a distinct truncation/coverage signal with truthful pre-cap counts.

## What NOT to flag

- The brief, explicitly signaled watcher debounce/staleness window.
- Duplicate Git and FSW events when refresh is idempotent.
- A full sweep after an unavailable, excessive, or malformed diff.
- Commit-level worktree status omitting dirty-state detail when the contract explicitly says reconciliation, not listing, owns dirt.
