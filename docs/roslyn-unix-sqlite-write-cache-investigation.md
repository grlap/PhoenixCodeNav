# Roslyn Unix SQLite write-cache investigation

Date: 2026-09-07

## Conclusion

The intermittent Roslyn failure on macOS is not caused by the installed .NET SDK
version and is not primarily a missing lock in PhoenixCodeNav. It is caused by
the scope of Roslyn's SQLite in-memory write cache on non-Windows platforms.

Roslyn attaches every persistent-storage database in a process to the same
in-memory URI on macOS and Linux:

```text
file::memory:?cache=shared
```

Each `SQLitePersistentStorage` instance has its own scheduler, so two storage
instances independently access the same cache tables. This can produce
`SQLITE_LOCKED` (`database table is locked: SolutionDataN`) when their work
overlaps.

The defect is not macOS-only. The global URI is selected for every non-Windows
platform, so Linux has the same underlying exposure. Windows derives the cache
URI from the persistent database path and does not use this exact fallback.

## Evidence

### A global non-Windows cache

`external/roslyn/src/Workspaces/Core/Portable/Storage/SQLite/v2/Interop/SqlConnection.cs`
attaches a path-derived cache on Windows but the same `file::memory:` cache on
all other platforms.

The current Roslyn `main` branch still contains this fallback, so upgrading the
Microsoft.CodeAnalysis packages alone does not currently resolve the problem:

- <https://github.com/dotnet/roslyn/blob/main/src/Workspaces/Core/Portable/Storage/SQLite/v2/Interop/SqlConnection.cs>
- <https://github.com/dotnet/roslyn/pull/51682>
- <https://github.com/ericsink/SQLitePCL.raw/issues/407>

The fallback was introduced as a workaround for a macOS SQLite URI problem.
The workaround removed the disk path from the memory-database name and thereby
collapsed all Unix persistent stores in a process into one cache namespace.

### Coordination has narrower scope than the data

`external/roslyn/src/Workspaces/Core/Portable/Storage/SQLite/v2/SQLitePersistentStorage.cs`
creates one `ConcurrentExclusiveSchedulerPair` per storage instance. That
scheduler coordinates connections belonging to that storage, but it does not
coordinate other storage instances using the same global Unix cache.

SQLite permits at most one writer in a shared cache and returns `SQLITE_LOCKED`
when a required table lock cannot be obtained:

- <https://www.sqlite.org/sharedcache.html>

Roslyn's own scheduler comment notes that the busy timeout cannot resolve the
relevant shared-cache deadlock. Increasing the timeout is therefore not a
correct fix.

### A process-wide lock would still be incorrect

`external/roslyn/src/Workspaces/Core/Portable/Storage/SQLite/v2/SQLitePersistentStorage.Accessor.cs`
flushes a write-cache table by copying all its rows into the current storage's
main database and then deleting all rows from the cache table.

Because numeric string identifiers are allocated against each main database,
rows written by storage B do not belong to storage A even if their table shapes
match. A process-wide lock could prevent concurrent access, but storage A could
still flush B's rows into A's disk database and delete them before B sees them.
The required invariant is therefore one write-cache namespace per persistent
database, not merely serialized access to a global namespace.

### Background work outlives the public call

Roslyn batches cache flushes on a delayed background queue. The storage service
does not expose a complete shutdown barrier that cancels and drains this queue,
closes pooled connections, and releases database ownership when the containing
workspace is disposed.

Consequently, a PhoenixCodeNav lock around `SymbolFinder` or other public
semantic operations cannot cover all SQLite work. A delayed flush may execute
after that lock has been released or while another workspace is starting.

### PhoenixCodeNav enables the affected persistence path

`src/CodeNav.Core/Semantic/SemanticWorkspace.cs` assigns a stable, non-null
synthetic `Solution.FilePath`. Roslyn uses that identity to enable
SyntaxTreeIndex persistent storage for an `AdhocWorkspace`.

The production MCP daemon registers one singleton `SemanticService` in
`src/CodeNav.Mcp/McpApplication.cs`, so normal daemon operation has a much
smaller collision surface. The test process, however, creates many independent
semantic workspaces; some tests deliberately create multiple workspaces at the
same time.

## Correct source fix

On Unix, Roslyn should derive a URI-safe, collision-resistant in-memory cache
name from the canonical persistent database path, for example:

```text
file:roslyn-writecache-<sha256(canonical-database-path)>?mode=memory&cache=shared
```

This preserves both necessary properties:

1. Connections for one persistent database derive the same name and continue
   sharing their write cache.
2. Different persistent databases derive different names and cannot lock,
   read, flush, or delete each other's cache rows.

The existing per-storage scheduler can remain in place after the namespace is
properly isolated. Windows behavior should remain unchanged.

A local macOS experiment using the repository's SQLite stack confirmed that
connections using the same named in-memory URI share data, while different URI
names remain isolated. A full disk path is not required in the URI; a digest of
the canonical path is sufficient.

## Lifecycle follow-up

Separately from the namespace correction, Roslyn persistent storage should have
an explicit shutdown sequence:

1. stop accepting new work;
2. cancel the delayed batching queue;
3. await or drain in-flight scheduled operations;
4. perform any required final flush against the correct cache;
5. close pooled SQLite connections;
6. release database ownership.

This is lifecycle hardening rather than the primary collision fix. Cache
isolation must not depend on completing this follow-up first.

## PhoenixCodeNav workaround until a fixed Roslyn package exists

The supported way to disable Roslyn persistence is to leave
`Solution.FilePath` null. PhoenixCodeNav can expose an explicit semantic
workspace option controlling whether Roslyn persistence is enabled.

Recommended temporary policy:

- keep persistence enabled for the production daemon, which owns a single
  `SemanticService` per process;
- disable persistence for ordinary parallel, multi-workspace tests;
- run the persistent-storage contract canary in a separate test process or test
  assembly, so it cannot share the global Unix cache with unrelated workspaces;
- remove the workaround after consuming a Roslyn package containing the
  per-database cache namespace fix.

This should be an explicit constructor or factory option, not process-name
detection or a hidden environment-dependent behavior.

## Rejected approaches

- **PhoenixCodeNav-wide `SemaphoreSlim`:** does not cover Roslyn's delayed
  background flush and cannot prevent cross-database row movement.
- **A static scheduler inside Roslyn without cache isolation:** may suppress
  `SQLITE_LOCKED` while retaining data mixing between stores.
- **Retrying `SqlException`:** masks the collision and cannot undo a row flushed
  to the wrong main database.
- **Longer busy timeout:** shared-cache table deadlocks can return
  `SQLITE_LOCKED` without honoring the timeout.
- **Disabling all xUnit parallelism:** reduces reproduction probability but
  does not provide a workspace-disposal barrier and makes the suite slower.
- **Changing SQLite threading mode:** connection mutex safety is different from
  table-level coordination and cache namespace isolation.
- **Changing the .NET SDK:** does not alter this Roslyn storage implementation.

## Required regression coverage

The Roslyn-level correction should prove:

1. two connections for the same persistent database observe the same write
   cache;
2. two different persistent database paths cannot observe each other's
   sentinel rows;
3. concurrent reads, writes, and flushes across two stores complete without
   `SQLITE_LOCKED`;
4. neither store can flush or delete the other store's rows;
5. disposing storage drains background work and leaves no later flush using the
   disposed workspace;
6. the formerly failing PhoenixCodeNav full-suite scenario remains green under
   repeated stress on macOS and Linux.

## Recommendation

Implement the per-database named-cache correction upstream or in a controlled
Roslyn fork, then consume the fixed Workspaces package. In parallel, use the
explicit no-persistence test mode plus a process-isolated persistence canary as
the immediate PhoenixCodeNav workaround.
