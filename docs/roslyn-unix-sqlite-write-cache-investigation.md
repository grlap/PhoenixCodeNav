# Roslyn Unix SQLite write-cache investigation

Date: 2026-09-07

## Status and evidence boundary

This records the A1 investigation and the authorized A2 test-isolation workaround.
The macOS A1 experiments below now reproduce `SQLITE_LOCKED` with storage
disablement and cross-store flush/delete using the Roslyn binaries Phoenix
loads. They establish the mechanism, but do not yet attribute a particular
historical failing Phoenix test to a captured overlapping-store lifetime.
The A2 constructor opt-out and process-isolated persistence canary are now implemented
locally. Focused tests and the full selected macOS suite pass; the external gate
still reports the pre-existing F# count difference. The owner's later disposition
accepts that specific Windows/macOS difference as non-blocking for this batch;
it does not waive other failures or close the separate F# investigation.
No Roslyn package replacement or namespace repair
has been made; production persistence remains enabled by default.

This is local cache reliability and correctness work, not a cybersecurity
finding. No attacker model or additional defensive-hardening scope is required.

Keep two claims separate:

- **D1, contention/availability:** shared-cache access can produce
  `SQLITE_LOCKED`, or disable persistence without failing the semantic query.
  Attribute the reported symptom to this mechanism through runtime evidence.
- **D2, cross-database cache-row mixing:** one store flushes another store's
  rows to the wrong disk database and removes them from its cache. A1 now
  observes this in both directions through real storage flushes. This is not
  equivalent to a demonstrated wrong semantic answer or corruption of Phoenix's
  own index. The earlier raw URI experiment alone did not establish D2.

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

### Measured macOS A1 results (2026-09-07)

Baseline: `main` at `cf7ce15`, which adds the staged investigation plan
on top of `1ddac3c` without changing product code. The diagnostic harness was
built outside the repository's committed test suite and loaded assemblies
directly from `src/CodeNav.Mcp/bin/Release/net10.0`, not from the external source
checkout. No pinned external, package, product option, timeout, test filter, or
parallelism setting was changed.

An explicit final submodule inventory shows untracked `.codenav/` directories
in both external checkouts; they were left untouched. Both checkout HEADs still
match the repository gitlinks. The default parent status hides this untracked
submodule content, so its initial clean display is not a claim of pristine
external fixtures or a passing external integration preflight.

| Input | Actually loaded value |
| --- | --- |
| OS / architecture | macOS 26.6.1 / Arm64 |
| Diagnostic managed runtime | .NET 10.0.0, system `/usr/local/share/dotnet/dotnet` |
| Roslyn Workspaces and compiler | Assembly 5.6.0.0; informational version `5.6.0-2.26263.10+c0573ed0a7dc3e3b4d2e70da47f97cc51a35524f` |
| SQLitePCLRaw core/provider/batteries | Assembly 2.1.12.3116; informational version 2.1.12 |
| Native library | Product output `runtimes/osx-arm64/native/libe_sqlite3.dylib` |
| Native SQLite | 3.53.3; source id `2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62` |
| Actual storage tables | `StringInfo7`, `SolutionData7`, `ProjectData7`, `DocumentData7` |

The native version above comes from `sqlite3_libversion`/`sqlite3_sourceid`, not
from an assumed correspondence with the managed package version. A native-load
resolver points both the Roslyn provider and the read-only observer at that same
product library.

#### Controls and results

Each scenario uses a fresh OS process and two real `SQLitePersistentStorage`
instances admitted by `TryCreate`, with different on-disk `storage.db` paths
and live database-ownership handles. A diagnostic listener holds the existing
500 ms batching delay without canceling storage; writes and reads use the real
storage API. Flush calls use
`FlushInMemoryDataToDiskIfNotShutdownAsync` and await its actual completion on
Roslyn's exclusive scheduler. No hand-written SQL substitutes for its flush.
Read-only observations capture both disk tables and the attached shared cache.
Private `_isDisabled` is inspected independently of write return values;
Roslyn logging and first-chance SQLite exceptions capture swallowed failures.

| Scenario | Observed result |
| --- | --- |
| D2, forward A flush | A and B sentinel bytes were pending, absent from their owning disk tables, and readable before flush. A's completed flush persisted both A's and B's rows into A's disk table and removed B's pending cache row. B's own disk never received its sentinel. |
| D2, reverse B flush | New writes/readbacks succeeded on both active stores. B's completed flush persisted B's own row and A's foreign row into B's disk table and removed A's pending cache row. Both stores remained enabled and accepted another write/read after the experiment. |
| D1, controlled overlap | A held a transaction/read lock on its cache table through a connection borrowed on A's scheduler. B's real flush logged `SQLite_SqlException` and `SQLite_StorageDisabled`, `Result=6`, `database table is locked: SolutionData7`. Flush returned normally with B disabled. A remained usable; B's guarded write API returned false. |
| Checksum control | A different non-null checksum rejected a foreign pending row and used A's valid disk fallback. After foreign flush overwrote that disk row, A's expected checksum produced a cache miss. Null-checksum and deliberately matching-foreign-checksum reads admitted foreign bytes. These latter calls are diagnostic controls, not evidence of the normal references path accepting them. |
| Real syntax-index control | Actual `SyntaxTreeIndex` instances were built from different C# texts (`OnlyAlpha` versus `OnlyBeta`), serialized by Roslyn, and written through its document-storage API. Their numeric document keys collided across stores; checksums differed. Loading with A's real checksum returned A's disk index before the foreign flush and a cache miss afterward. A diagnostic null-checksum load returned B's index under A's key. Clean-cache recomputation retained `OnlyAlpha`, not `OnlyBeta`. |

For the D1 schedule the harness holds a read transaction explicitly; this is a
deterministic reproduction of overlapping storage work, not a measurement of
its natural frequency. The D2 schedule is serialized, demonstrating why a
process-wide lock alone cannot repair the shared namespace. The D1 result is
not counted as an isolation pass merely because the flush task completed.

The real syntax-index control uses Roslyn's save/load methods and checksums,
with a diagnostic storage-service adapter returning the two observed stores.
It proves a checksum-aware miss plus an independent clean-document recomputation
control, not automatic recovery of the same affected request or a full Phoenix
MCP result. Source inspection of `AbstractSyntaxIndex_Persistence.cs` agrees:
the ordinary index load supplies current text/format/preprocessor checksums and
recomputes on a miss. These observations support cache loss/recomputation for
the exercised different-text case. They neither demonstrate a wrong MCP answer
nor prove semantic equivalence for every equal-checksum/context combination. The product
uses fixed C# `LanguageVersion.Latest` parse options; no cross-language or
hostile-input scenario is needed to explain the observed reliability defect.

#### Reproduction artifacts and run accounting

Local, Git-ignored archive:
`artifacts/roslyn-unix-sqlite-a1-20260907/`. It contains the ad-hoc `Probe.csproj`,
`Program.cs`, generated binaries, separate database directories, and JSONL
evidence. It is not included in a normal commit or push of this document.
The harness intentionally records this machine's absolute product/output paths;
relocate those constants explicitly before running on another machine.

Original commands (run from the same Mac, outside the committed test suite):

```text
/usr/local/share/dotnet/dotnet build /private/tmp/phoenix-a1.wGJrib/Probe.csproj --no-restore --disable-build-servers
/usr/local/share/dotnet/dotnet /private/tmp/phoenix-a1.wGJrib/bin/Debug/net10.0/Probe.dll sentinel
/usr/local/share/dotnet/dotnet /private/tmp/phoenix-a1.wGJrib/bin/Debug/net10.0/Probe.dll checksum
/usr/local/share/dotnet/dotnet /private/tmp/phoenix-a1.wGJrib/bin/Debug/net10.0/Probe.dll lock
/usr/local/share/dotnet/dotnet run --project /private/tmp/phoenix-a1.wGJrib/Probe.csproj --no-restore --disable-build-servers -- syntax
```

The package-free diagnostic project initially used `dotnet run` to generate its
own SDK assets; no Phoenix or external package restore was performed. The
diagnostic build completed with zero warnings/errors. This is not a product
Release build or a substitute for the repository gates.

Each of the three confirmation modes was declared as one run, with no stress
loop or retry-until-green. The subsequent syntax control also has one completed
valid run. The evidence directories are:

- D2: `run-5aaa5bd6ae8a4e539188eeb2b93f81ca/evidence.jsonl`.
- Checksums: `run-7b806b60f6df44cba2d00ea2dc876a4d/evidence.jsonl`.
- D1: `run-6bc53ceda2934747b59790c044d95c32/evidence.jsonl`.
- Real syntax index: `run-e419813748224343aa842dbaee4e97c1/evidence.jsonl`.

Including harness development, ten diagnostic processes were run: seven
completed their controls, and three failed setup. The three earlier completed
development runs reproduced D2, checksum behavior, and D1 respectively. The
setup failures were classified and corrected in the ad-hoc harness only:

- The first observer assumed `SolutionData4`; the shipping table is
  `SolutionData7`. Table discovery replaced that assumption. The first run has
  terminal evidence and database files but predates JSONL logging.
- A lock-control prototype passed `SELECT` to Roslyn's non-query
  `ExecuteCommand`, yielding `SQLITE_ROW` (`another row available`) before the
  lock precondition. A completed read via a temporary-table select inside the
  transaction replaced it. This was not counted as D1.
- The first syntax fixture lost its project file path through
  `AdhocWorkspace.TryApplyChanges`; its real save correctly returned false.
  Retaining the immutable solution with explicit project/document paths fixed
  the fixture. That failed save was not counted as an isolation result.

No data was removed from existing workspaces. The diagnostic processes own all
of their databases, cancel their held queues at exit, and retain their evidence
files. This does not claim a Roslyn public disposal barrier was fixed or tested.

#### macOS selection audit

`tests/Directory.Build.props` automatically chooses `tests/macos.runsettings`
on macOS unless a runsettings path is supplied. Thus the default full-suite
command still has the existing macOS exclusions. No solution suite was run in
A1; the standalone harness has no xUnit filter. Prior suite totals must be read
with their actual runsettings, not described as unfiltered platform coverage.

The read-only audit found:

- `Batch44*` excludes the explicitly Unix
  `UnixLiteralBackslashPathResolvesThroughTheSemanticCallerBoundary`, including
  real `DefinitionAsync`/`ReferencesAsync` calls. The two excluded Batch47
  generic-family tests and the IVT exact-subtype test also invoke real Roslyn
  semantics. Their exclusion can hide relevant coverage; this does not establish
  that their historical failures were caused by this cache defect.
- Batch42/43/44 blanket exclusions lack per-test runtime justification in the
  available history (`71bff96 Init`). They include Unix-specific cases, while
  current Git code has macOS support. Lazy `SemanticService` construction alone
  is not evidence of an active Roslyn store.
- Framework-reference checks and the Batch47/IVT semantic cases have genuine
  framework prerequisites in source; that does not prove those prerequisites
  are missing on the current machine.
- The telemetry case-folding test assumes Windows paths. Path-rejection theories
  mix Windows drive-path rows with ordinary escape checks; whole-method filters
  also remove the latter. The F# case-only-file test requires a case-sensitive
  volume, not simply an OS other than Windows.
- F# cases use FCS, not this Roslyn persistent store. Some have identifiable
  framework or volume requirements; no current macOS-specific cause was found
  for the three excluded hint-path capture/limit/stability cases. The explicitly
  Unix dangling-link test, first-commit watcher case, telemetry lifecycle case,
  and F# review-pack exclusion also lack an established current failure cause.
- `Batch40Tests.SkippedFilesAndFailedProjectsAreCounted` is a stale filter: the
  current replacement is `UnreadableColdBuildFailsClosedAndCountsCaptureFailure`.

The audit does not authorize changing platform policy. None of the exclusions
were added, removed, or used to make A1 pass.

#### A1 decision boundary

A1 provides runtime support for a cache-isolation intervention. D2 is observed
cache-row mixing, so production persistence policy still needs an explicit
scope decision before A2; the singleton daemon alone is not a safety proof.
The tested ordinary different-text syntax-index load rejected the foreign
result, so this report does not assert incorrect navigation answers. Keep the
response proportional: a reliability repair, not security hardening. The user subsequently
approved A2 with the existing production default retained. A patched dependency and
the separate shutdown work remain unimplemented.
Linux/Windows runtime evidence, full MCP comparison, and attribution of the
historical flaky test remain pending.

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

The row-movement scenario above motivated D2. The measured A1 section now
demonstrates it through the loaded package's active-storage flush path; its
limits concerning semantic answers remain explicit.

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

By default, `src/CodeNav.Core/Semantic/SemanticWorkspace.cs` assigns a stable, non-null
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
diagnosis, not automatic addition of a persistence toggle. The measured A1
section above records the completed macOS experiments and remaining limits.

## Phase A2: conditional Phoenix workaround and coverage

Proceed only after the runtime evidence supports the intervention and its scope
is accepted. This is product implementation, not part of A1's diagnostic scope.

### Implemented local A2 change (2026-09-07)

The approved workaround adds `enableRoslynPersistence = true` to `SemanticService`
and both `SemanticWorkspace` constructor paths. `SemanticService` stores the choice
before creating its lazy workspace. The false branch omits only the synthetic
`Solution.FilePath`; stable solution/project/document IDs, Phoenix index pools,
limits, and parallelism are unchanged. Ordinary in-process semantic fixtures now
pass false explicitly. The production daemon still uses the default true branch.

`Batch61PersistentSemanticIdentityTests` checks default activation, stable-ID parity,
and propagation through the service's lazy workspace. Its ordinary direct
`AdhocWorkspace` invalidation fixture now leaves the solution path null as well.

`SemanticPersistenceProcessTests` launches the existing test assembly as a dedicated
canary process, one workspace per invocation. Its explicit command does not alter
the production CLI. The canary uses the actual workspace-exported Roslyn SQLite
storage service; its existing configuration overload redirects only the cache
directory into the disposable fixture. It verifies that normal service lookup
returns the same active SQLite store, rather than replacing the persistence service.

Nine child-process phases exercise an initial write, unchanged-input disk reuse,
preprocessor-symbol invalidation and reuse, source-byte invalidation and reuse,
and matching semantic reference counts with persistence disabled. A load-only
`SyntaxTreeIndex` lookup runs before computation in each process, so reuse is not
inferred from timing or an in-memory index hit. Enabled phases require the actual
SQLite store, the exact fixture database path, an enabled store before/after real
flush, and no observed Roslyn SQLite exception. The disabled phases require actual
NoOp storage and no cache directory. Cleanup occurs after child-process exit,
not on an assumed Roslyn disposal barrier.

The mixed-language `FSharpTierATests.BuildIndexesFSharpTextOwnershipAndCrossLanguageGraphWithHonestToolGates`
fixture also needed a startup-readiness correction. Waiting for `IsQueryable` alone
allowed the semantic call to overlap the startup freshness sweep; queryability is
not the ready-snapshot precondition required by `SymbolAt`. The test now uses
`IndexManagerTestSupport.WaitUntilReady` with the same 30-second deadline. It still
uses the existing 60-second semantic timeout, and checks `found` with
`TryGetProperty` while retaining the complete response in the assertion message.
The expected declaration name and reference assertions are unchanged.

The original full-suite failure was a `KeyNotFoundException` at the `found`
assertion, without its response payload. A controlled startup-sweep pause reproduced
`index_snapshot_unavailable` with no `found` field under both persistence choices;
the same request succeeded after readiness. That proves the invalid test-precondition
mechanism, not the exact cause of the original incident whose payload was lost.
The fix strengthens synchronization and failure diagnostics; it does not add a retry,
increase a timeout, change runner parallelism, or change F# product behavior.

This workaround does not prove the underlying dependency repaired, remove the
existing macOS test exclusions, or establish Linux/Windows runtime results. It
does not add a CLI/MCP option, change a capability envelope, or change stored index
output. That opt-out alone requires no BuildInfo/features/schema change; the separate
framework-input defect discovered by the expanded canary below does bump BuildInfo
and adds a feature entry, without a schema change.

### A2 validation on macOS (2026-09-07)

- Focused Batch61 and process-canary tests: **7 passed, 0 failed**. The canary
  completed all nine child phases; it also passed during the full suite.
- Release build: **0 Warning(s), 0 Error(s)**. The first development build
  caught a missing option on the internal preparation-concurrency constructor
  and an ambiguous preprocessor-options overload in the new canary. Both were
  corrected before the passing build and tests.
- Complete solution command with the unchanged `tests/macos.runsettings`:
  **1253 passed, 0 failed, 0 skipped** among selected tests (989 IndexTests,
  77 GitTests, 76 LifecycleTests, 99 Tests, 12 WatcherTests). This is not an
  unfiltered all-platform claim. The formerly failing
  `AgentExperienceTests.DocumentationCommentIdsResolveStableDeclarationsAndKinds`
  passed. No blanket serialization, filter, or timeout adjustment was made.
- External fresh-index MCP harness: **46 passed, 1 failed**. The only failure is
  `official FSharp repository counts`: expected 116170 symbols, observed
  116168, with 4174 orphaned files. Both external HEADs match their gitlinks.
  The saved pre-A2 evidence (build `0.12.89+108dbaed3051`) has the same failure
  and count pair; the A2 run is `0.12.89+cf7ce153bd45`. This remains an
  unexplained, pre-existing indexing/ownership discrepancy, not a demonstrated
  Roslyn-persistence regression. No Indexing, Discovery, F# parser, submodule,
  or baseline file was changed to accept it. Its separate investigation
  was still required at that validation point. The later owner disposition below
  supersedes treating this particular count difference as a blocker.
- Website verifier: **167 checks passed**.
- Formatting: the wrapper's first run failed while loading
  `System.Composition.AttributedModel, Version=10.0.0.9`, not on a formatting
  diagnostic. This host's PowerShell resets `DOTNET_ROOT` to Homebrew while
  PATH selects `/usr/local/share/dotnet/dotnet`, mixing SDK installations.
  Setting `DOTNET_ROOT` inside PowerShell to `/usr/local/share/dotnet` before
  the same `dotnet format --verify-no-changes --no-restore` check passed
  (exit 0). No repository SDK policy was changed.
- The gate's final TEMP sweep removed four residual entries, with zero survivors
  and zero cleanup failures. The wrapper itself correctly reported RED for
  its original formatter failure and the external MCP mismatch.

The complete wrapper command was
`pwsh -NoProfile -File ./scripts/gate-summary.ps1`, with PATH initially selecting
the system dotnet installation. Corrected formatter invocation:

```powershell
$env:DOTNET_ROOT = "/usr/local/share/dotnet"
dotnet format PhoenixCodeNav.sln --verify-no-changes --no-restore --verbosity minimal
```

Local ignored evidence is in `artifacts/roslyn-unix-sqlite-a2-gate.log`,
`artifacts/gate-results/` (TRX),
`artifacts/roslyn-unix-sqlite-a2-before-results.json`, and
`artifacts/external-integration/last-results.json`. These are not delivered by
committing this document. Engram tools and its CLI were unavailable in this
session, so tracker reconciliation is not claimed. Dual review was not started
with the external gate red at that initial validation point; subsequent reviews
were authorized with the known count difference disclosed. There has been no
commit or push.

### Review follow-up and F# owner disposition (2026-09-07)

`SemanticPersistenceProcessTests` now takes the existing `ProcessHeavyTestIsolation`
lease in the parent only. Child lifetime/drain handling reuses `TestProcessLifecycle`;
the deadline remains 60 seconds. Incremental stream capture retains partial output
even when a reader faults during timeout cleanup, and failures identify the phase
and its arguments. The canary emits progress to stderr without changing stdout's
single-result JSON contract. An ignored diagnostic forced the real capture path to
time out: phase/parameters and both non-newline-terminated stream fragments were
present, and the child had exited before the diagnostic returned.

A separate two-project/four-document scenario broadens production-default coverage
without re-enabling overlapping stores in the ordinary suite. It runs disabled,
enabled cold, and enabled warm in three separate processes over one unchanged root.
Before any index computation or `SymbolFinder` call, all four documents must miss
or hit both `SyntaxTreeIndex` and `TopLevelSyntaxTreeIndex` as the phase requires.
Cold phases explicitly seed both families; the latter is used by hierarchy and
implementation discovery. The compiler must load both projects with their source
project reference and no skipped/failed projects or compiler errors. Assertions
require exact cross-project type-reference spans and documentation IDs plus owning
project/document for implementations, derived types, and overrides. Both modes
must satisfy these non-empty expectations and agree across process boundaries.

The SQL exception observer now resolves the pinned exception type with
`throwOnError: true` and compares type identity, so a rename cannot silently disable
the observation. The two enabled Batch61 constructor fixtures explicitly document
their identity-only invariant: semantic loads belong in the separate-process canary,
not in those parallel in-process fixtures.

The expanded canary's first run exposed a separate, pre-existing compiler-input
defect rather than a persistence failure: the C# net472 locator admitted
`System.EnterpriseServices.Thunk.dll` (no managed metadata, CS0009) and
`System.EnterpriseServices.Wrapper.dll` (not an assembly, CS1509) from the restored
targeting pack. `MetadataReference.CreateFromFile` did not reject those inputs
eagerly. C# framework discovery now applies the existing F# `IsManagedAssemblyPath`
filter once when filling its process-wide cache. Genuine assemblies/facades remain
eligible; native helpers and standalone managed netmodules are not assembly
references, and their files are not deleted. The graph canary retains its strict
zero-compiler-errors assertion. A focused regression emits valid assemblies and a
real netmodule and also supplies a non-managed DLL, proving exact admission and
facade retention without depending on host targeting-pack contents.

This compiler-input correction changes the default semantic path, so BuildInfo is
`0.12.90` with the singular `semantic-framework-managed-assembly-inputs` feature.
No indexer-stored output, schema, baseline, or MCP envelope changed. The complete
follow-up validation for this expanded canary and framework-input correction
(BuildInfo `0.12.90`) was:

- Release build: **0 Warning(s), 0 Error(s)**; formatter: **clean**.
- Focused persistence, identity, framework-input and capabilities tests:
  **44 passed, 0 failed**.
- Complete solution command with unchanged `tests/macos.runsettings`:
  **1255 passed, 0 failed, 0 skipped** among selected tests (991 IndexTests,
  77 GitTests, 76 LifecycleTests, 99 Tests, 12 WatcherTests). This is not an
  unfiltered all-platform claim.
- Both process-canary tests passed: **twelve child phases** in total, comprising
  the original nine persistence/invalidation phases and the three new
  cross-project graph phases.
- Fresh pinned Roslyn/F# MCP harness: **46 passed, 1 failed**. The sole failure
  was `official FSharp repository counts`, expected 116170 symbols and observed
  116168; the owner disposition below applies only to this difference. The
  wrapper's raw result remains RED/exit 1, not a claimed fully green harness.
- Website verifier: **167 checks passed**.
- TEMP cleanup: **0 survivors, 0 cleanup failures**.

The local ignored log is `artifacts/a2-review-followup-gate.log`; the totals above
remain available when this document is shared without that artifact. They are
distinct from the earlier 1253-test, nine-phase A2 runs. Two existing capability
summaries were shortened and one new feature entry was added; the existing
2048-byte capabilities growth-margin assertion and all response limits stayed
unchanged.

Lease headroom was measured separately without changing the shared helper or
adding runner instrumentation. An isolated diagnostic invoked the actual built
test methods, constructing and disposing a separate fixture for each method as
xUnit does. For the nine-phase test, constructor/acquisition took **0.005 s** and
the post-construction interval through fixture disposal took **8.843 s**; for the
three-phase graph test, those intervals were **0.001 s** and **3.848 s**. The lease
is released between fixture instances, rather than held across all twelve child
processes. These are one macOS sample without competing test classes, not a
worst-case bound. The prior full-suite TRX recorded **18.613 s** and **5.397 s**
for the respective tests; those are test durations, not instrumented lease-hold
times. Six classes use this helper repository-wide, but the unchanged macOS filter
excludes Batch42 and Batch43; this run does not establish six-way or Windows
contention headroom. The selected suite completed without an acquisition timeout.
This evidence supports retaining the existing **130 s acquisition** and
**60 s per-child** deadlines for this batch. If contention later reaches that
ceiling, its owner/waiter timing must be classified rather than silently increasing
the timeout. No new performance limit or permanent infrastructure test was added.

The owner explicitly classified the known F# symbol-count difference (116170 on
the recorded baseline versus 116168 on this macOS run) as non-blocking for this
batch: the conditional `#if FRAMEWORK` branch applies on Windows, whereas this
macOS evaluation uses the net8 symbols. This is the owner's cross-platform
classification, not a new Windows measurement from this session. The raw harness
result remains visible; no baseline, submodule, target-framework policy, or
assertion was changed to turn it green. This exception does not cover other
external failures or any solution-test failure.

The readiness-fixed gate recorded in
`artifacts/fsharp-startup-readiness-gate.log` passed formatting, a zero-warning
Release build, all 1253 selected macOS tests, and all 167 website checks. The
external harness passed 46 cases and failed only the known F# count comparison.
New review-fix validation must be reported separately from that earlier run.

### Acceptance checklist

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

The preparation plan was delivered in `cf7ce15`; this local work adds the
macOS A1 measurements and the subsequently authorized A2 workaround. A fork or
patched-package integration still needs Greg's approval. No pinned external edit,
commit, or push is authorized by that implementation approval. These changes and the
ignored diagnostic archive have not been committed or pushed; another machine
will not receive them through `git pull` until separately delivered.
