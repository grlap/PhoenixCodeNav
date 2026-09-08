# Roslyn Unix SQLite write-cache investigation

Date: 2026-09-07

## Status and evidence boundary

This is an investigation and staged repair plan, not a completed runtime
diagnosis. The reported intermittent macOS symptom is `SQLITE_LOCKED`. Source
inspection identifies a shared write-cache namespace as a candidate cause;
the first handoff deliverable is a behavioural experiment against the packages
Phoenix actually loads, before adding a product workaround.

Keep two claims separate:

- **D1, contention/availability:** shared-cache access can produce
  `SQLITE_LOCKED`, or disable persistence without failing the semantic query.
  Attribute the reported symptom to this mechanism through runtime evidence.
- **D2, cross-database data corruption:** one store may flush another store's
  rows to the wrong disk database and remove them from its cache. This is a
  **source-based inference, not an observed result**. The earlier macOS URI
  experiment established sharing, not cross-store flush/delete corruption.

The user-visible question A1 must bound is whether D2, if reproduced, can cause
**incorrect semantic answers on macOS/Linux**, or only lost cache efficiency.
Rows associated with the wrong document are different from an empty cache miss;
determine whether key/checksum validation rejects them or permits their use.
Do not claim wrong answers from row movement alone, or safety from the absence
of an exception. The affected data here is Roslyn's persisted syntax-tree data,
not Phoenix's own `index.db`; these are separate stores.

The inspected Roslyn implementation attaches persistent-storage databases in
a process to the same in-memory URI on macOS and Linux:

```text
file::memory:?cache=shared
```

Each `SQLitePersistentStorage` instance has its own scheduler, so two storage
instances independently access the same cache tables. This can produce
`SQLITE_LOCKED` (`database table is locked: SolutionDataN`) when their work
overlaps.

That source branch applies to every non-Windows platform, so Linux needs
validation too. Windows derives the cache URI from the persistent database
path and does not use this exact fallback. Neither an SDK change nor an added
Phoenix lock should be treated as a demonstrated repair.

Preparation baseline: Phoenix commit `1ddac3c`; inspected external Roslyn commit
`e7680f251b8fc52a0ba7108906c0e40ff3263a32`. Phoenix instead consumes
`Microsoft.CodeAnalysis.CSharp` and `Microsoft.CodeAnalysis.CSharp.Workspaces`
**5.6.0**, with `SQLitePCLRaw.bundle_e_sqlite3` **2.1.12**, as declared in
`src/CodeNav.Core/CodeNav.Core.csproj`. Record the actually loaded assemblies and
native SQLite version in the experiment. A pinned source checkout, upstream
`main`, and the shipping package are not interchangeable evidence.

## Evidence

### A global non-Windows cache

`external/roslyn/src/Workspaces/Core/Portable/Storage/SQLite/v2/Interop/SqlConnection.cs`
attaches a path-derived cache on Windows but the same `file::memory:` cache on
all other platforms.

The Roslyn `main` source inspected on 2026-09-07 also contains this fallback.
That does not prove which implementation a released package loads or that a
package upgrade fixes the runtime symptom:

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

### Why a process-wide lock is not a sufficient repair proposal

`external/roslyn/src/Workspaces/Core/Portable/Storage/SQLite/v2/SQLitePersistentStorage.Accessor.cs`
flushes a write-cache table by copying all its rows into the current storage's
main database and then deleting all rows from the cache table.

Because numeric string identifiers are allocated against each main database,
rows written by storage B do not belong to storage A even if their table shapes
match. A process-wide lock could prevent concurrent access, but storage A could
still flush B's rows into A's disk database and delete them before B sees them.
The required invariant is therefore one write-cache namespace per persistent
database, not merely serialized access to a global namespace.

The row-movement scenario above is D2's inferred mechanism. Do not report it as
reproduced until the active-storage sentinel experiment below demonstrates it
against the loaded runtime package.

### A green query can conceal disabled persistence

`SQLitePersistentStorage_FlushWrites.cs` calls `RunInTransaction` with
`throwOnSqlException: false`, then calls `DisableStorage(exception)` on failure.
The inspected source explicitly says subsequent storage reads/writes return
empty results. Semantic queries can continue without a healthy persistent
cache: this is a fail-open persistence path in an in-process dependency, not
evidence that the semantic answer itself is wrong.

Visible exceptions therefore cannot measure the incidence of contention;
some failures can be swallowed and disable storage. A green suite or a sentinel
that never reached an active cache is not evidence of persistence health.
Experiments must prove real successful writes and active storage both before
and after the tested flush, and record storage-disable diagnostics separately.

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

Singleton registration reduces exposure; it is not a proof that all storage
instances and their delayed work have disjoint lifetimes. Do not infer production
safety from the registration alone, especially if D2 is demonstrated.

## Correct source fix

On Unix, Roslyn needs a URI-safe, collision-resistant in-memory cache name tied
to the persistent database's identity. The identity mechanism remains a Phase B
design decision, not a validated repair. A canonical-path digest is one candidate:

```text
file:roslyn-writecache-<sha256(canonical-database-path)>?mode=memory&cache=shared
```

Whichever mechanism is selected must preserve both necessary properties:

1. Connections for one persistent database derive the same name and continue
   sharing their write cache.
2. Different persistent databases derive different names and cannot lock,
   read, flush, or delete each other's cache rows.

The existing per-storage scheduler can remain in place after the namespace is
properly isolated. Windows behavior should remain unchanged.

### Phase B design note: investigate opened-file identity first

Prefer investigating a filesystem identity for the opened database (for example,
device plus inode on Unix) before implementing path canonicalization. This could
make aliases converge without guessing the volume's case policy. It is a
candidate, not yet verified through the shipping storage API. In the inspected
`SqlConnection.Create`, a successful `sqlite3_open_v2` with `SQLITE_OPEN_CREATE`
precedes the write-cache `ATTACH`; investigate obtaining identity at that point
rather than assuming the database must still be absent.

Verify that the identity refers to the file SQLite actually opened, remains
consistent across its pooled connections, and does not allow database replacement
or file-ID reuse to join an older still-live cache. A separate path lookup is not
automatically equivalent to identity obtained from the opened file. The native
handle/API availability and lifetime guarantees must be established in Phase B.
Require a proven incarnation/generation discriminator or equivalent ownership
lifetime guarantee: a reused file identity must never attach to an older cache.
The discriminator must stay stable across ordinary writes and be shared by all
connections for that database incarnation; mutable file size is not a suitable
generation. Do not assume timestamps uniquely identify an incarnation without
evidence. A mismatch must not be treated optimistically as the same database.

Use a canonical-path digest only if the chosen API/lifetime design requires it
and the alias tests below pass. An existing ancestor's identity plus an unresolved
relative tail is not a complete solution by itself: the tail still needs the
volume's case semantics, and creating intermediate directories must not change
the identity. Do not claim Phoenix's ancestor-resolution precedent proves this
new construction. These choices do not expand or block the A1 experiment.

For the path-based candidate, canonical identity is a filesystem-aware contract, not just
`Path.GetFullPath` or unconditional case folding:

- Resolve relative components and directory symlink/alias spellings to the same
  physical destination. For a not-yet-created database, resolve the nearest
  existing ancestor and retain a normalized relative destination; creation must
  not change the derived identity.
- Normalize supported directory separators and redundant trailing directory
  separators without changing the meaning of valid Unix filename characters or
  converting an invalid database-file path into a different valid one.
- Respect the actual volume's case semantics. Case aliases on a case-insensitive
  macOS volume must converge; distinct case-only files on a case-sensitive macOS
  or Linux volume must remain distinct. Do not lowercase every Unix path or
  assume the OS name determines the volume's behaviour. The chosen mechanism
  must prove both convergence and non-collision before package integration.

Phoenix prior art is `CanonicalDatabaseIdentity` / `DatabaseKey` in
`src/CodeNav.Mcp/Daemon/DaemonEndpoint.cs`, using `WorkspacePhysicalIdentity`,
and `WindowsWorkspaceSpellingsAndAliasesJoinOneDaemon` in
`tests/CodeNav.IndexTests/SharedDaemonTests.cs`. That test covers plain roots,
trailing separators, alternate Windows separators, `root/.`, and directory
aliases. Reuse its invariant/test shape, not a blind copy of its implementation:
`HostCanonicalPath` currently folds case only on Windows and that test does not
prove macOS-volume behaviour. Do not add a Roslyn dependency on Phoenix types.

A local macOS experiment using the repository's SQLite stack confirmed that
connections using the same named in-memory URI share data, while different URI
names remain isolated. A full disk path is not required in the URI; a digest of
the canonical path can supply the name. This validates the SQLite URI mechanism
only, not Roslyn's flush path, canonicalization, or correctness of a patched
runtime package.

## Lifecycle follow-up

Separately from the namespace correction, Roslyn persistent storage should have
an explicit shutdown sequence:

1. stop accepting new work;
2. cancel the delayed batching queue;
3. await or drain in-flight scheduled operations;
4. perform any required final flush against the correct cache;
5. close pooled SQLite connections;
6. release database ownership.

This is a separate lifecycle correctness repair, not cosmetic hardening. Its
tests must prove a shutdown barrier and no later flush. It is not part of the
namespace repair's acceptance and must not delay cache isolation. Existing
Phoenix shutdown and workspace-cleanup fixes remain valid and are not reopened.

## PhoenixCodeNav workaround until a fixed Roslyn package exists

The supported way to disable Roslyn persistence is to leave
`Solution.FilePath` null. PhoenixCodeNav can expose an explicit semantic
workspace option controlling whether Roslyn persistence is enabled.

Conditional temporary policy, only after Phase A1 justifies Phase A2:

- keep persistence enabled for the production daemon, which owns a single
  `SemanticService` per process;
- disable persistence for ordinary parallel, multi-workspace tests;
- run the persistent-storage contract canary in a genuinely separate OS process;
  a separate class, collection, or assembly is not sufficient without proving
  process isolation from unrelated persistent stores;
- remove the workaround after consuming a Roslyn package containing the
  per-database cache namespace fix.

This should be an explicit constructor or factory option, not process-name
detection or a hidden environment-dependent behavior.

Disabling persistence in ordinary tests deliberately reduces coverage of the
production configuration. The real, process-isolated persistence canary is a
required compensating control, not an optional mock or a service-export check.
If A1 demonstrates corruption, obtain an explicit production policy decision;
retaining the existing daemon default is not itself a safety conclusion.

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

This rejection of blanket serialization does not overturn the existing approved
GitTests parallelism cap for process contention. Preserve that cap and its
assertions; new caps or reduced coverage need separate approval.

## Phase A1: runtime evidence first

The macOS handoff starts here, in an ad-hoc diagnostic harness outside the
committed test suite. Do not add a product option, modify pinned externals, or
replace packages to obtain this baseline. Record the Phoenix commit, loaded
Roslyn assembly identities/locations, managed provider and native SQLite
versions, OS/architecture, commands, and relevant diagnostics.

### Decisive sentinel experiment

Use two independent persistent stores A and B over **different disk database
paths in the same process**, using the shipping Roslyn storage implementation.
A raw SQLite URI experiment alone cannot validate Roslyn's flush behaviour.
Confirm both stores were admitted and are distinct; an ownership refusal or
no-op storage service is not a successful isolation test.

1. Establish observability of active storage and successful writes. If this is
   unavailable, report the diagnostic limitation and do not run an experiment
   that could be mistaken for a clean sentinel result.
2. Write distinguishable sentinels through both stores. Verify actual cached
   bytes, their originating store, and that both stores remain active. Keep the
   necessary connections alive; record any background flush that changes the
   intended precondition.
3. Drive a flush through A, with B's sentinel demonstrably still pending in its
   write cache. Use an observable flush completion condition, not a fixed sleep.
4. Inspect A's disk database and B's data. B's sentinel must not appear in A;
   A's flush must not remove or alter B's pending data. Verify A's own sentinel
   was persisted, so a skipped flush cannot pass the experiment.
5. Assert both stores are still active and can perform another real write/read.
   Also exercise the reverse direction. Capture swallowed errors and storage
   disablement, not only exceptions escaping the query.

Declare outcomes before running:

| Outcome | Required evidence and interpretation |
| --- | --- |
| D2 reproduced | A successful, observed cross-store flush moves or removes the other store's sentinel under valid active-storage preconditions. Preserve before/after records. |
| D1 reproduced | The controlled runtime scenario reports `SQLITE_LOCKED`, including an observed storage-disable cause carrying that error. Other errors are classified separately. |
| Isolation holds for the exercised schedule | Both stores stayed active, both positive write/flush controls passed, and all isolation assertions held. This is not proof that intermittent failures cannot occur. |
| Inconclusive | Stores were inactive/unobservable, a write or flush did not occur, B flushed before the trigger, setup was rejected, or uncontrolled timing invalidated the preconditions. A plain non-reproduction without these controls is inconclusive. |

D1 and D2 are separate result axes: an observed D1 failure may make the D2
experiment inconclusive. Never convert storage disablement into an isolation
pass. For additional stress runs, state the schedule and run count before
execution, then report every result and the denominator without stopping at the
first green run. Deterministic isolation evidence takes precedence over timing.

If D2 reproduces, examine the read path and its key/checksum checks; where
observable, compare affected semantic results with a clean-cache recomputation.
Report separately whether foreign data was accepted, rejected, or its effect on
answers remains unbounded. Evidence of wrong answers escalates the production
policy decision; an unmeasured correctness consequence must remain explicit.

### Existing macOS selection audit

Read `tests/macos.runsettings` and record the effective test selection used by
each run. Preparation inspection found that its exclusion list arrived in the
initial commit `71bff96`; the comments cite path folding, framework references,
and unsupported Git operations, not this cache defect. That history does not
establish a per-test reason for every exclusion. The macOS owner must audit the
excluded cases for cache-related masking, recording confirmed causes and any
unknowns. Do not claim that none mask the defect without evidence, add new
exclusions, or silently remove existing platform policy to obtain a green run.

**A1 gates A2.** Report measurements and which claim they establish before
authorizing a workaround. A failed or inconclusive hypothesis leads to further
diagnosis, not automatic addition of a persistence toggle. No A1 runtime results
are claimed by this preparation document.

## Phase A2: conditional Phoenix workaround and coverage

Proceed only after the runtime evidence supports the intervention and its scope
is accepted. This is product implementation, not part of A1's diagnostic scope.

- Inventory all consumers of the synthetic solution path before changing it,
  including direct `AdhocWorkspace` construction in tests. Stable identities are
  intentional; do not assume persistence is their only consumer.
- Thread an explicit per-instance persistence choice through `SemanticService`
  and `SemanticWorkspace`, available before the lazy workspace is created.
  Keep the existing production default unless a separate decision changes it.
- The opt-out should leave `Solution.FilePath` null while retaining stable
  solution/project/document IDs, subject to the consumer audit. Do not conflate
  Roslyn persistence with `poolIndexConnections` or Phoenix's own SQLite pools.
- Ordinary multi-workspace tests opt out explicitly. Assert parity of semantic
  results with persistence enabled/disabled and verify the default remains on.
- Extend the existing persistent syntax-index activation canary work rather than
  treating `DefaultAdhocHostExportsRoslynSqlitePersistentStorage` as sufficient.
  In isolated processes, prove real persisted write/reuse for unchanged input
  and invalidation for changed source bytes and parse options. Distinguish a
  storage hit from recomputation; timing alone is not an assertion.
- Preserve existing handle-release, watcher shutdown, and test-workspace cleanup
  proofs. The workaround must not broaden pool clearing or weaken teardown.

## Phase B: Roslyn namespace repair and verified package integration

After approval of the dependency/fork route, implement the per-database named
cache described above. Resolve the opened-file identity versus canonical-path
design choice and demonstrate its lifetime/alias invariants first. Use that
persistent database identity, not a
random per-connection identifier. Preserve platform path semantics and existing
database ownership; do not force two owners of the same disk database merely to
test cache sharing. Keep Windows behaviour unchanged.

Focused product regression coverage must prove:

1. multiple connections for one persistent database share their cache;
2. different persistent databases cannot read each other's sentinels;
3. a real flush/delete in either direction cannot move or delete the other's
   rows, with active-storage and positive-write controls from A1;
4. concurrent read/write/flush schedules complete without `SQLITE_LOCKED` or
   silent storage disablement;
5. cache persistence, reuse, and invalidation continue to work in Phoenix;
6. supported spellings of one database path share one cache, including directory
   aliases and case aliases on case-insensitive volumes, while genuinely
   different case-sensitive paths remain isolated.

Editing `external/roslyn` does not change Phoenix's loaded NuGet packages and
breaks the pinned-checkout prerequisite of the external MCP gate. Leave that
checkout untouched. A controlled build/package source and its version must be
approved separately; show the actually loaded patched assemblies before claiming
the correction works. After integration, repeat A1 and restore the ordinary
tests' production persistence configuration before removing the workaround.

The explicit storage shutdown repair is **Phase C**, with its own barrier and
post-disposal regression tests. It remains important but is not an additional
acceptance criterion for Phase B.

## Validation and handoff boundary

The work complements, rather than reopens, the completed Phoenix TEMP/resource
cleanup effort. Existing cold-start and warm-reload work remains separate; any
performance comparison must state the persistence mode and cold/warm state.
Tracker relationships belong in the work records, not in source documentation.

For implementation acceptance, collect repeated macOS results, Linux coverage,
and Windows regression evidence. Report each platform actually tested and the
effective test selection; missing platform evidence remains pending. Follow the
repository's Release build with zero warnings, complete solution suite, fresh
Roslyn/F# MCP gate, and `node ./website/verify.mjs`, plus the required dual review
before check-in. An isolated diagnostic pass does not replace these gates. Do not
add tests of scripts/build policy or change timeouts, limits, or parallelism to
hide failures. Classify failures individually and retain their evidence.

The macOS agent may take **A1 now once this plan is made available**: inspect the
shipping runtime, build the external ad-hoc harness, run the controlled sentinel
experiment, audit test exclusions, and update this report with measured results.
A2 needs the A1 evidence decision; a fork or patched-package integration needs
Greg's approval. No product change, pinned external edit, commit, or push is
authorized by this handoff. The preparation document itself remains an
uncommitted local change until separately reviewed and approved for delivery;
another machine will not receive it through `git pull` before that delivery.
