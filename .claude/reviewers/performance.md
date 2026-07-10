# Performance / Concurrency Review

Focus: Monolith-scale complexity, bounded resource use, single-writer safety, lifecycle races, and behavior-preserving optimization.

## What to check

1. **Scale assumptions**
   - Evaluate against thousands of projects, tens of thousands of files, and millions of lines.
   - Flag per-file/per-project SQL loops, repeated full-tree scans, whole-file materialization, or whole-solution semantic loads on hot paths.
   - Prefer grouped queries, FTS narrowing, cached facts, and bounded candidate sets.

2. **Cold build pipeline**
   - Parsing may run in parallel but persistence stays single-writer.
   - Producer channels are bounded and always completed on failure.
   - Progress/loss counters remain monotonic and do not add contention to the per-file loop.

3. **Incremental refresh**
   - Hash-identical files skip work.
   - Ordinary edits do not trigger graph rebuilds or workspace scans unnecessarily.
   - Performance shortcuts preserve cold/delta parity, especially legacy compile ownership.

4. **SQLite concurrency**
   - One write connection is confined to serialized build/pump work.
   - Tool queries use short-lived reader connections under WAL.
   - Rebuild, snapshot, pool clearing, lock probes, and sibling worktree writes cannot contend with another active writer.

5. **Semantic loading**
   - Avoid N+1 fingerprint/project queries and repeated compilation loads.
   - Respect project/time bounds and cancellation.
   - Reload only changed projects, reuse IDs, and preserve cache/LRU safety.
   - Test-only hooks must not add meaningful production overhead to the hottest loops.

6. **Locks and callbacks**
   - No deadlock-prone lock ordering.
   - Do not invoke expensive/external callbacks while holding lifecycle locks.
   - Timers/watchers cannot rearm or publish after disposal.
   - Semantic gate, refresh pump, startup task, and disposal have explicit ownership.

7. **Shutdown/recovery races**
   - Do not dispose the store while startup/pump/tool readers may still use it.
   - Bounded shutdown may intentionally leak during process teardown rather than cause use-after-dispose, but must log it.
   - Full rebuild cannot interleave with pending refreshes.

8. **Subprocess resources**
   - Time, output, threads, handles, and process trees are bounded under hangs and noisy output.
   - Repeated degraded Git calls must not leak background readers or pipe handles.

9. **Response construction**
   - Budget trimming does not repeatedly serialize quadratic amounts of data.
   - Samples/source text are capped before large object graphs accumulate where practical.
   - Stable ordering does not require avoidable global sorts.

10. **Performance evidence**
    - Meaningful hot-path changes include a regression test or benchmark on representative synthetic scale.
    - Compare latency and behavioral parity; an optimization that drops ownership, graph edges, coverage, or counts is incorrect.

## What NOT to flag

- A correctness-driven full sweep after overflow, huge/failed diff, or ambiguous directory mutation.
- Cold semantic cluster load around the documented target.
- The semantic project cap being soft when eviction would dangle references.
- A bounded rebuild retry merely for using a short sleep.
- Allocation/style micro-optimizations without demonstrated monolith impact.
