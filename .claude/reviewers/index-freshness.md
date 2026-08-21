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
   - On supported anchored destinations, builds and finalizes a private database while the previous
     publication remains readable; a pre-install failure preserves that publication and cleans the
     stage.
   - Publishes destination `rebuilding` only at the final installation boundary and before
     releasing writer handles; local and follower gates prevent new reads from barging while every
     admitted writer query, pinned review snapshot, and follower handle drains under the bounded
     observable timeout. One deadline covers the local drain and remaining atomic-install retries,
     and local timeout restores the prior publication before its store is disposed.
   - Proves that the stage and the retained manager/builder authority identify the same destination
     before staging and after test seams. After installation, proves that the live database has the
     reserved stage identity and that a fresh no-follow lexical open reaches the retained
     destination; a directory replacement fails closed before reads or readiness are published.
   - On Linux, reads a staged build through the retained workspace handle. On Windows, scans the
     lexical path and relies on pre/post-build and post-install identity checks. Both persist the
     original lexical root only as metadata and revalidate that it still identifies the
     ownership-lease workspace before publication; a whole-root replacement fails closed.
   - Treats failure to establish a required Windows/Linux workspace-local anchor as a safety
     refusal; only an intentionally unsupported path layout may use the destructive fallback, and
     that fallback first revalidates the acquisition-time workspace identity.
   - Atomically installs the complete stage; the in-place compatibility fallback still removes the
     main database and stale SQLite sidecars before creating its replacement.
   - Restoring a prior publication refreshes cached metadata and schedules a detect-all convergence
     sweep before reporting it readable.
   - Cold bulk loading retains primary/unique constraints and commits the deferred external-content
     FTS rebuild plus every deferred secondary index before the compatible `schema_version` marker
     can be published; a rolled-back finalization must leave that barrier armed.
   - After acquiring the physical-workspace writer lease and destination claim, validates stored
     workspace ownership before reaping exact identity-verified Phoenix stage/publish artifacts;
     bounded discovery must identify count/time refusal separately from unsafe link-set refusal
     through retained directory authority. Discovery is count/time bounded before handles open; a
     live claimed stage, foreign publication, and unrelated filenames are retained.
   - Clears stale cached metadata and prior error state.
   - Reattaches filesystem and Git tracking when recovering from startup failure.

8. **Worktree indexes**
   - Targets come from `git worktree list`; arbitrary paths and bare/headless entries are rejected.
   - Seed uses a consistent `VACUUM INTO` snapshot.
   - The target workspace mutex and destination claim cover the complete staged publication; a
     live target Phoenix or foreign destination claimant is detected before writes.
   - Staged metadata is rebound to the target workspace before install.
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
