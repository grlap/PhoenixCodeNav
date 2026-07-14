# Testing Review

Focus: Contract-level regression coverage, deterministic reproductions, decisive assertions, and full quality-gate discipline.

## What to check

1. **Changed behavior is tested**
   - Every bug fix and user-visible contract change has a regression test that reproduces the original failure.
   - Prefer exercising the public tool JSON as well as lower-level helpers.
   - Assert confidence, coverage, counts, notes, truncation, and conditional-field absence, not merely "does not throw."

2. **Test discrimination**
   - Review whether the test structurally distinguishes the intended behavior from obvious broken implementations and identify the decisive assertion or condition.
   - Reject vacuous assertions, tautological expected values, and tests that only prove the code does not throw.
   - Historical red-run or temporary defect-restoration evidence is not required.

3. **Cold/delta/worktree parity**
   - Test initial build, targeted refresh, detect-all sweep, failed-to-ready full rebuild, and seeded worktree reconciliation where relevant.
   - Verify persisted facts survive restart/schema checks.

4. **Git matrix**
   - Cover staged, unstaged, untracked, added, modified, deleted, renamed, and re-added files.
   - Cover raw SHA and named refs, branch movement, commit-less repos, shallow/error fallbacks, and adversarial Git config.
   - Include `.exe`, `.cmd`, and `.bat` launcher behavior on Windows.

5. **Path/diff matrix**
   - Include spaces, tabs, Unicode, CRLF, special shell characters, large output, many hunks, and files whose removed source resembles diff headers.
   - Test symbol deletion/rename inside surviving files, namespace/global-using changes, pure moves, and deleted project/build files.

6. **C# workspace matrix**
   - Include legacy and SDK projects, linked multi-owner files, orphan files, generated files, tests, partial/generic declarations, overloads, HintPath edges, and assembly-name collisions.

7. **Budget boundaries**
   - Test cap, cap+1, a single oversized item, multibyte UTF-8, large fixed metadata/notes/deleted sections, and pagination.
   - Assert actual serialized UTF-8 size is within requested and global hard limits.
   - Each omission produces the correct stable cause ID.

8. **Semantic lifecycle**
   - Test cold load, warm load, file-triggered reload, case-variant project names, eviction pressure, graph cycles, missing framework references, cancellation before results, and mid-scan salvage.
   - Verify the same-snapshot invariant through behavior.

9. **Concurrency/lifecycle**
   - Exercise startup-versus-dispose, watcher callbacks during teardown, overlapping Git/FSW refresh, rebuild with readers, foreign worktree ownership, and full-suite parallel ordering.
   - Use bounded polling and deterministic seams instead of timing luck wherever possible.

10. **Portable versus environment-gated coverage**
    - Real-Git tests may guard on `GitInfo.GitAvailable`, but parsing, validation, shell-inertness, and fallback logic also need unguarded unit coverage.
    - Platform-specific tests must not leave the core contract untested elsewhere.

11. **Cleanup and flake resistance**
    - Dispose managers/services/watchers, clear SQLite pools safely, and clean temporary repositories best-effort.
    - Avoid shared static test seams and order-dependent state.
    - The known watcher timing flake may be reported and verified in isolation; do not normalize new flakes.

12. **Quality gates**
    - `dotnet build` finishes with zero warnings.
    - `dotnet test` passes as a full suite, not only targeted classes.
    - Empirical reviewer reproductions become durable tests when actionable.

## What NOT to flag

- Lack of a code-coverage percentage target.
- Temporary-repository integration tests.
- A Git/platform guard when equivalent core logic has portable coverage.
- Bounded polling for genuine filesystem/process events.
- The documented watcher flake if it passes in isolation and is explicitly reported.
