# Semantic Correctness Review

Focus: Compiler-evidence integrity, symbol identity, cluster completeness, and honest semantic degradation.

## What to check

1. **Confidence is earned**
   - `exact` is used only for a Roslyn-resolved symbol and compiler-backed operation.
   - Syntax/index candidates remain `indexed`; naming/base-list/test inference remains `heuristic`.
   - Mixed exact/heuristic sections carry separate confidence fields.

2. **Pinned snapshot invariant**
   - Symbol resolution and subsequent `SymbolFinder` work use the same `Solution` snapshot.
   - A reload/eviction cannot orphan a symbol between resolution and search.
   - Reload reuses the existing `ProjectId`.

3. **Owning-project identity**
   - Linked files and multiple compile owners are handled deliberately.
   - Semantic resolution targets a compiled declaration, never an orphan merely because it sorts first.
   - Public-consumer filtering anchors on the declaring assembly/project, not the query-position project.

4. **Candidate-cluster completeness**
   - Load the declaring dependency closure and the required dependent/candidate projects.
   - Implementation/base-list seeds enter before lower-value textual candidates.
   - Skipped, failed, capped, and out-of-graph candidate projects are surfaced.
   - Coverage includes the whole resident solution when `SymbolFinder` may scan previously loaded projects.

5. **Project-reference wiring**
   - Dependencies load before consumers.
   - Source-over-binary substitution happens only when the corresponding source project is actually wired.
   - Assembly-name collisions retain all relevant consumer edges.
   - Cycle prevention sees dangling/reloading references through `AllProjectReferences`.

6. **Symbol resolution**
   - Name hints match whole identifiers and exact declared names.
   - Columns, overloads, generic arity, partial declarations, namespaces, sibling declarators, constructors, accessors, and aliases are not conflated.
   - Declaration spans are physically deduplicated without erasing legitimate per-project attribution.

7. **Reference/count integrity**
   - Filters for tests, generated code, usage kind, and external consumers run before totals, groups, summaries, and breakdowns.
   - Physical-site dedupe includes project identity so linked files count once per real project binding.
   - Indexed fallback never presents whole-identifier candidates as compiler references.

8. **Hierarchy/implementation integrity**
   - Interface types, interface members, class derivation, overrides, abstract scaffolding, and concrete implementations use the correct Roslyn operation.
   - Ranking and `via` paths do not alter completeness.
   - Fallback payloads omit compiler-only facts they did not earn.

9. **Deadline behavior**
   - Cancellation reaches load, compilation, finder, and counting work.
   - Cold cluster load and scan timeout have distinct reasons.
   - Mid-scan salvage is labeled partial/lower-bound and only returned after internally consistent bookkeeping.
   - Zero work interrupted by a deadline must not become an exact zero.

10. **Cache safety**
    - Warm fingerprint batching matches single-project, case-insensitive semantics.
    - LRU eviction never leaves dangling references.
    - Memory-pressure backstops preserve correctness even when the project-count cap is soft.
    - Workspace/gate disposal cannot race active semantic work.

## What NOT to flag

- Bounded `maxProjects` when skipped/coverage fields are honest.
- A first-call `cluster_cold_load` degradation.
- Heuristic fallback when unmistakably labeled.
- The documented single-TFM/no-MSBuild limitations.
- Temporarily exceeding the semantic cache's soft project cap when safe eviction is impossible.
