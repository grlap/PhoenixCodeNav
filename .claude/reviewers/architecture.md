# Architecture Review

Focus: Component boundaries, index-model invariants, graph fidelity, and full-build/delta parity.

## What to check

1. **Layer boundaries**
   - `CodeNav.Core` remains protocol-agnostic and must not depend on the MCP SDK or MCP JSON shapes.
   - `CodeNav.Mcp` owns argument validation, result shaping, metadata, and budgets; substantial discovery/index/semantic logic belongs in Core.
   - `CodeNav.WorkspaceGen` and `CodeNav.Bench` must not become alternate production implementations.

2. **Component ownership**
   - Discovery scans and parses workspace structure.
   - Indexing owns persisted facts, queries, refresh, watchers, and Git/worktree reconciliation.
   - Semantic owns Roslyn compilations and compiler-backed navigation.
   - Shared policy stays centralized: paths in `WorkspacePaths`, JSON/budgets in `Responses`, stable diagnostic IDs in `Notes`.

3. **Full-build versus delta parity**
   - A cold build and an incremental refresh must produce equivalent stored facts.
   - Changes to graph edges, compile ownership, test classification, symbols, modifiers/accessors, or file classification must be reflected in both `IndexBuilder` and delta/project refresh paths.
   - Re-add/delete/branch-switch paths must not leave facts that only a full rebuild repairs.

4. **Index consistency**
   - File rows, external-content FTS rows, symbols, compile items, and graph rows change transactionally.
   - Deletes remove FTS content and dependent rows without ghosts.
   - SQLite remains one-writer/many-reader; tool threads must not use the manager's write connection.

5. **Project-graph fidelity**
   - Preserve dependency direction: upstream means dependents; downstream means dependencies.
   - Preserve provenance between real `ProjectReference` edges and recovered HintPath/binary-reference edges.
   - Account for legacy explicit compile items, SDK globs/removes, linked files with multiple owners, assembly-name collisions, and orphaned files.
   - Do not silently choose one owner where the contract requires all owners.

6. **Stored-output and deployment discipline**
   - Bump `IndexBuilder.SchemaVersion` whenever schema or any persisted/indexed interpretation changes.
   - Bump `BuildInfo.Version` for a user-visible capability or tool-surface change.
   - Ensure a rebuild cannot open old rows using new semantics.

7. **Read-only product boundary**
   - Phoenix may inspect Git/worktrees and write `.codenav` indexes, but must never create, remove, check out, reset, commit, or otherwise mutate Git worktrees/repository state.

8. **Honest degradation**
   - Unsupported MSBuild shapes, missing reference assemblies, graph gaps, unreadable files, and failed parses remain visible through coverage/status rather than becoming clean-looking facts.

## What NOT to flag

- The deliberate no-MSBuild `AdhocWorkspace` architecture.
- Documented SDK compile-item approximations unless a change worsens or misrepresents them.
- Intentional partial-class splitting of `NavigationTools` or `SemanticService`.
- Full sweeps used as correctness fallbacks.
- Style preferences that do not affect an architectural invariant.
