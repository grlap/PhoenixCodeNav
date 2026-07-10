# MCP Contract / Budget Review

Focus: Stable agent-facing JSON, confidence and coverage honesty, hard response budgets, and deploy-verifiable capabilities.

## What to check

1. **Envelope consistency**
   - Normal results and degraded fallbacks carry correct `Meta` freshness, confidence, navigation layer, build, and index schema.
   - Errors never masquerade as successful empty results.
   - A missing field, null, zero, and empty collection retain their documented distinct meanings.

2. **Confidence and partiality**
   - Exact/indexed/heuristic labels match the evidence source.
   - Partial results expose the cause, skipped scope, and whether counts are lower bounds.
   - Clean-looking zeroes are forbidden when timeout, cap, load failure, graph gap, or parse failure could explain them.

3. **Hard byte budget**
   - Measure serialized UTF-8 bytes through the shared serializer, never `string.Length`.
   - Every output path fits `Json.HardBudgetBytes`, including errors, metadata, notes, project/deleted-file sections, and a single oversized item.
   - Per-call `maxBytes` is clamped and enforced over the whole payload, not only its primary list.

4. **Truncation honesty**
   - Every independent cap - items, files, ranges, deleted files/types, samples, source body, timeout, or byte trimming - has a visible reason.
   - Counts distinguish total/pre-cap scope from returned scope.
   - Pagination exposes `nextCursor` when more results remain.
   - Distinct causes do not share an ambiguous note ID.

5. **Filter-honest responses**
   - Filters apply before totals, groups, kinds, summaries, risk calculations, and test/production splits.
   - Returned counts and prose describe the same set.
   - Deduplication rules are stable and documented.

6. **Stable diagnostics and shapes**
   - Recurring causes use stable machine-matchable IDs from `NoteIds`; prose may change but IDs may not.
   - One cause per ID; do not overload an existing ID for a new concept.
   - Conditional fields are tested both when present and intentionally absent.
   - Ordering is deterministic where consumers or snapshots rely on it.

7. **Argument validation**
   - Modes, kinds, scopes, cursors, refs, paths, spans, limits, timeouts, and globs are validated/clamped consistently.
   - Invalid input produces a clear error, not a silent empty match.
   - Descriptions match actual defaults, limits, semantics, and fallback behavior.

8. **Capability/deployment manifest**
   - Every new feature gets its own singular, grep-able `features[]` ID even if it reuses an existing response envelope.
   - Never extend an unrelated feature ID to cover a second concept.
   - New/removed tools update the tools list and relevant semantic declarations.
   - User-visible changes bump `BuildInfo.Version`; persisted-output changes bump schema.

9. **Symbol handles**
   - Handles remain index-local and detect reindex/stale fingerprints.
   - Handle-targeted operations stay pinned to that declaration and do not silently re-disambiguate by name.

10. **`review_pack` contract**
    - `changedFiles` counts describe the real changed set, not capped processing.
    - Deleted/renamed symbols, unmapped C# hunks, structural files, orphan status, owners, risks, and indexed-only escalation guidance remain visible.
    - Budget trimming cannot silently erase the most safety-critical sections.

## What NOT to flag

- Null fields omitted by `WhenWritingNull`.
- Conditional fields absent when their documented condition is false.
- `UnsafeRelaxedJsonEscaping`; JSON is sent over stdio, not embedded into HTML.
- Indexed reference candidates when explicitly labeled indexed.
- A documented finite result cap with truthful truncation/paging.
