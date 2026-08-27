# PhoenixCodeNav

A code-navigation [MCP](https://modelcontextprotocol.io) server for **very large C# and mixed
C#/F# workspaces** (designed for enterprise monorepos with thousands of csproj/fsproj, legacy
*and* SDK-style, net472-first).
It gives coding agents (Claude Code, Codex, anything MCP) a fast, structured alternative to
grep-driven exploration: ranked search, file outlines, exact references, project graphs, and
compact context packs — with strict ordinary response budgets so results stay compact. One
declared exception preserves an intrinsically oversized compiler symbol identity intact instead
of truncating or rejecting it, with exact byte metadata on that response.

> Named after **Phoenix A**, the most massive known black hole — built to navigate the
> heaviest repositories. No relation to Apache Phoenix.

**Docs:** [`docs/intro.md`](docs/intro.md) — why it exists, and how it compares to grep,
Cursor, and other tools · [`docs/design.md`](docs/design.md) — architecture, projects, and
how freshness (incl. git branch switch / pull) is handled ·
[`docs/agent-instructions.md`](docs/agent-instructions.md) — the snippet for your repo's
`CLAUDE.md` / `AGENTS.md`.

## Why not just grep?

At thousands of projects / millions of lines, text search returns too many weak matches, dependency
direction is invisible, and agents burn context reading whole files. PhoenixCodeNav answers
navigation questions in four layers, each labeled with how trustworthy it is:

| Layer | Tools | Confidence |
|---|---|---|
| **Indexed text** (SQLite FTS5, C# + F# + Markdown + SQL) | `find_file`, `search_text`, `source_context`, `config_lookup`, `references` (candidates) | `indexed` |
| **Syntax (C#)** (Roslyn parse, no compile; includes implicit/explicit conversion operators) | `outline`, `search_symbol`, `symbol_at`, `batch_outline` | `indexed` |
| **Syntax (F#)** (FCS parse, no type check) | `search_symbol`; `outline` for project-owned `.fs` / `.fsi` | `indexed` |
| **Semantic** (Roslyn for C#; bounded FCS type checks for F#) | C#: `definition`, `references`, `implementations`, `callers`, `callees`, `type_hierarchy`; F#: position `symbol_at` and same-project `definition` | C# may be `exact`; bounded F# is `indexed` with explicit partial causes |

Plus structural facts parsed directly from every `.csproj` and `.fsproj` (`project_graph`,
`projects_containing`, `dependency_path`, `repo_overview`) and composites (`context_pack`,
`impact`, `related_tests`). Solution files may be inventoried for editor context, but they
never select projects or contribute build, ownership, dependency, or symbol-resolution authority.

**C# semantic loading supports standard central package management.** A versionless
`PackageReference` can take its unconditional simple version from the nearest indexed
`Directory.Packages.props`. A `PackageVersion` may use bounded `$(Name)` expansion from local,
unconditional `PropertyGroup` assignments, including assignment-time chains and reassignment; project
overrides, explicit project imports, applicable `Directory.Build.targets`, conditions, property
functions, unresolved references, and over-budget property tables retain the established
unresolved-reference behavior. Literal central versions are unaffected by the property budgets. That central file participates in the
warm Roslyn project identity, so an indexed property or `PackageVersion` refresh reloads the affected
project. Phoenix never runs restore or MSBuild: the selected version must already exist in its exact
global-cache directory. Existing analyzer-only or target-incompatible packages keep the established
unresolved-reference behavior. Other central shapes that require MSBuild evaluation retain that
behavior instead of guessing, while a selected but unavailable exact package fails the project load
rather than substituting another installed version. This extends the existing
C# direct-package assembly strategy; the verified transitive-assets closure described below remains
specific to F#.

**F# support is real, and deliberately bounded.** Phoenix indexes `.fs`, `.fsi`, and `.fsx` text,
parses `.fsproj` compile ownership and references, and preserves C#↔F# project edges. Compile-owned
`.fs` / `.fsi` files get indexed declaration-name search and syntax-only `outline`s from a pinned
FSharp.Compiler.Service adapter, plus position-based `symbol_at` and same-project `definition`
through a bounded FCS type check. Active `PackageReference` items—including conditional central
`PackageVersion` authority from the nearest indexed `Directory.Packages.props`—use the selected
target in an already-restored `project.assets.json`; reachable transitive compile assets are
snapshotted without executing restore or MSBuild, while stale/missing/ambiguous assets and project-reference
closure fail explicitly. Stored
declaration indexing processes at most 64 deterministically
selected owner/TFM parse contexts per file, reserving one context per valid compile owner while
capacity remains before filling the budget in global ordinal order. FCS syntax parse failures, context truncation, and
unavailable/unevaluated project-option contexts are retained as index coverage evidence:
`search_symbol` returns stable joined `partialReason` / `partialReasons` causes plus
total/processed/truncated parse and project-file-context counts, including
`truncatedOwnerProjects`, instead of treating missing rows as
authoritative absence. Ordinary SDK/import limitations remain visible in
`fsharpProjectOptionCoverage` as advisory evidence without making every search partial. F# references, implementations, callers/callees, and hierarchy stay
**unsupported** rather than
returning an empty or falsely exact answer. Phoenix never executes MSBuild targets or tasks: it
evaluates a documented subset of project files (simple properties and conditions, `Choose`, literal workspace-local
`.props`, and the nearest ancestor `Directory.Build.props`/`.targets`). Unsupported authority either
fails closed with a stable cause or continues only with an explicit partial cause; partial
continuation is limited to standard `Microsoft.NET.Sdk` / recognized compiler-toolchain implicit
authority and a host-selected `FSharp.Core`. See [`docs/design.md`](docs/design.md) for the exact
evaluation boundaries and how ambiguous project/TFM ownership is disclosed.

**A miss is recoverable, and never disguised.** A first-page empty `search_symbol` response stays a
clean result but reports `existsUnfiltered` and `appliedFilters`, so a declaration hidden by your
own narrowing never looks like one that does not exist. Exact-path misses in `outline`,
`source_context`, and `find_file` may offer up to three indexed `pathSuggestions.paths` — Phoenix
never silently substitutes a suggestion, and never consults Git history for deleted files.

The dependency graph also sees what MSBuild's project view hides in large legacy codebases:
binary `<Reference Include>` + HintPath couplings from **multi-staged builds** (phase one
builds dlls to a common folder; later projects reference the dll, not the project) count as
graph edges, so cross-project `references`/`implementations` resolve exactly across them.
Every edge carries its provenance — `projectReference` vs `hintPathReference` — and `impact`
flags dependents wired only via HintPath: they bind to the last-*built* dll, and
ProjectReference-aware refactor tooling won't follow that edge.

**No MSBuild required.** The semantic layer builds Roslyn compilations directly from parsed
project files (AdhocWorkspace): documents from disk, framework reference assemblies, hint-path
and NuGet-cache package dlls, in-cluster project references. It works identically for legacy
(`ToolsVersion=15.0`, `packages.config`) and SDK-style projects.

## Keeping the index fresh

Index updates are incremental. The writer process watches the working tree and applies debounced
C#, F#, Markdown, and SQL deltas; `.csproj` / `.fsproj` changes rebuild compile ownership and the
authoritative project graph, while solution changes update only non-authoritative editor inventory.
A startup sweep catches edits made while the server was down, and branch switches / pulls are
detected by watching `.git` — `repo_overview.git` reports indexed vs HEAD commit. Every response
carries `indexStatus` / `indexVersion` freshness metadata, and cold builds expose live progress
counters (no fabricated ETAs).

`refresh_index` is the in-band writer hatch: `force: 'auto'` (the default) re-detects changes,
`force: 'incremental'` explicitly requests that incremental sweep, and `force: 'full'` rebuilds
from scratch — recovering even a corrupted index without shell access.
Targeted `refresh_index(paths: ...)` and explicit `review_pack(paths: ...)` accept one to 256 exact
workspace-relative paths, within a 64 KiB input string, as comma-separated text or a serialized
JSON string array. Use the JSON form for a filename containing a comma. Blank, rooted, traversing,
control-character, malformed, and over-limit inputs return `bad_request` before any refresh is
queued or review lookup begins.
`review_pack` revalidates the bounded live evidence it consumed after aggregation; Git-diff mode
also recaptures its patch, dirt, boundary classifications, move evidence, and the bounded
untracked move-candidate bytes it actually hashed. A mid-call mismatch fails with
`git_worktree_changed` and no partial digest. Unreadable, non-regular, oversized, or cap-excluded
untracked candidates remain conservatively uncorrelated instead of failing an otherwise stable
review.

Every registered MCP tool retains its required JSON schema and validates arguments before SDK
binding. A missing or mistyped field returns an error result with `error:"bad_request"`, plus the
tool, field, reason, expected type, and `retryable:true`; clients do not have to recover from an
opaque host invocation error. This rejection happens before workspace health is constructed, so
its deliberately minimal envelope does not include the ordinary `meta` object.

**A full rebuild keeps the prior index available during private construction.** On Windows and
Linux workspace-local destinations, Phoenix builds and finalizes a private database while the
previous index stays queryable. During the final bounded reader drain and atomic install, new
queries may briefly retry or fail; already-admitted readers keep their
consistent handles. Failures during private construction or before stage installation may return a
previously readable publication to service, but only while workspace and live-database authority
remain valid. Once the stage has been installed, a later failure cannot restore the previous
publication and Phoenix fails closed. Identity or authority validation failures fail closed at any
phase rather than re-serving a publication Phoenix can no longer prove belongs to that workspace.
Results served from the old index during construction report `building` rather than implying
freshness they do not have.

Phoenix v0.12.60 uses a **shared MCP daemon transparently on every ordinary launch**. Each MCP host
starts a small stdio relay; the relays for one physical worktree automatically join or elect one
same-user daemon that owns the index, watcher, refresh pump, Roslyn workspace, and F# semantic
service. There is no opt-in flag or environment variable. Claude, Codex, and delegated agents keep
independent MCP sessions while sharing one committed epoch, and every session can call
`refresh_index`; the daemon serializes mutations. The daemon lingers for 15 minutes after the last
client disconnects, or indefinitely with `--keepalive`. If it dies, initialized MCP sessions end;
the MCP hosts restart their relays, which elect or join one successor daemon.

Normal launches never fall back to a second standalone server or a read-only compatibility
process. Endpoint, identity, authority, protocol, or startup failures return a typed unavailable
MCP surface instead of silently changing architecture. Served sessions report database ownership
as `server_capabilities.index.mode: "writer"`; unavailable shims omit `index` and report
`meta.indexMode: "unavailable"` with a stable cause. Response `meta.indexMode` reports public
runtime topology (`daemon`, `standalone`, or `unavailable`). `standalone` occurs only when a human
explicitly supplies `--standalone` for diagnostics or isolated tests, and it refuses to serve when
it cannot acquire the writer lease.

Startup is self-managing: Phoenix privately relays one bounded ready/refusal result through its own
bootstrap, shares an exact owner-checked failure with concurrent relays, discards stale or corrupt
advisory state, and retries election after the owning failed session or retiring daemon is gone.
There is no daemon startup protocol or repair control for MCP users to configure. Recoverable local
state repairs on the next safe election; decisions that could discard or rebind an index remain an
explicit typed action rather than a silent rebuild.

[`docs/design.md`](docs/design.md) documents the publication, claim, drain, and crash-recovery
mechanics in full.

## Install (work machine)

Prerequisites: **none** for the self-contained build. For semantic (`exact`) results the
machine needs net472 reference assemblies from any of (probed in this order):
`CODENAV_NET472_REFS` env var → VS/Build Tools targeting pack → NuGet
`Microsoft.NETFramework.ReferenceAssemblies.net472` package cache → installed .NET Framework
runtime. Any machine with Visual Studio qualifies automatically. Without them, tools degrade
to `indexed` confidence and say so.

```text
dotnet publish src/CodeNav.Mcp -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

Copy the complete `artifacts/win-x64` publish directory to the install location (for example,
`C:\tools\phoenix\`). It contains `PhoenixCodeNav.Mcp.exe`, its adjacent `FSharp.Core.dll`, and
the separately executable `portal/` companion with its static website. The MCP executable remains
self-contained; the F# sidecar is the physical compiler reference asset used by bounded F#
semantic navigation. Keep the `portal/` directory beside the MCP executable so
`open_operations_portal` can launch it without a source checkout or `dotnet run`.
(A framework-dependent build — `dotnet publish -c Release -o artifacts/portable` — is ~5 MB
but requires the .NET 10 runtime.)

### Attach to Claude Code

Project-scoped `.mcp.json` at the repo root (recommended — checked in for the whole team):

```json
{
  "mcpServers": {
    "phoenix": {
      "command": "C:\\tools\\phoenix\\PhoenixCodeNav.Mcp.exe",
      "args": ["--workspace-root", "."]
    }
  }
}
```

or per-user: `claude mcp add phoenix -- C:\tools\phoenix\PhoenixCodeNav.Mcp.exe --workspace-root C:\path\to\repo`

### Attach to Codex

`~/.codex/config.toml`:

```toml
[mcp_servers.phoenix]
command = "C:\\tools\\phoenix\\PhoenixCodeNav.Mcp.exe"
args = ["--workspace-root", "C:\\path\\to\\repo"]
```

Then add the agent instructions from `docs/agent-instructions.md` to your repo's
`CLAUDE.md` / `AGENTS.md` so agents prefer these tools over shell grep.

### Operations Portal

When you explicitly ask the agent to open the Phoenix Operations Portal, it calls
`open_operations_portal`. The tool starts or reuses the workspace's loopback-only, read-only
portal and returns an authenticated `http://127.0.0.1:.../#token=...` URL. The agent shows that
URL as a clickable link in the conversation; the tool does not open a browser and the portal
process never writes through the MCP stdout transport. A startup or reuse attempt is bounded to
30 seconds and reports a structured error if the packaged companion is missing or cannot become
ready. Cross-process coordination lives below the current user's profile. On Unix, Phoenix-owned
directories are forced to owner-only modes and unsafe writable ancestors fail closed; on every
platform, reparse-point ancestors fail closed before any authenticated descriptor is read or
written. Windows relies on the current user profile's inherited ACLs for directory privacy. A
reused descriptor is accepted only when `/healthz` proves the same private portal session and PID.
Because the portal never opens SQLite, it reports an index as `queryable` only when the current
anchored index-file generation, a connected Phoenix process, and a successful retained query from
that process agree. Replacing or changing the observed index file invalidates older query evidence;
freshness remains explicitly unknown. The recent-operation metric comes from the workspace's
retained telemetry count and does not restart its animation on unchanged refreshes.

For manual development, the portal can still be run from the workspace with
`dotnet run --project src/CodeNav.Portal/CodeNav.Portal.csproj -c Release`.

The shared daemon acquires the writer lease and builds the index in the background (a 10M-LOC repo
takes a few minutes; the server answers `index_building` hints meanwhile). All ordinary MCP
sessions reach that same owner. A missing, corrupt, or schema-stale index is repaired only by the
daemon. The index lives in
`<workspace>/.codenav/index.db` — add `.codenav/` to `.gitignore` — or point `--index-db`
elsewhere. On macOS, or with a non-workspace-local destination, Phoenix uses the in-place
compatibility rebuild path instead of staged publication. A custom destination is still owned by
exactly one physical workspace; do not share one database path between workspace roots.

## Git worktrees (review flows)

Each worktree carries its own index under `<worktree>/.codenav/` — indexes are
workspace-relative, local-only, and never shared or committed (large-workspace indexes can
run to gigabytes).
Phoenix never creates or removes worktrees — its git usage is strictly **read-only**. A
review system creates the worktree; phoenix seeds and follows it:

```text
git worktree add ../review-1234 <ref>          # yours (or your review system's)
index_worktree(path: "../review-1234")         # MCP, on the MAIN instance: seeds a staged
                                               # snapshot of the live index (never torn, pump
                                               # never pauses) and installs it atomically into
                                               # the worktree's anchored .codenav destination,
                                               # then reconciles. refresh re-seeds the same way.
```

That one-shot publisher holds the target worktree's writer mutex for the whole staged install and
rewrites the stored workspace identity before publishing, so the sibling daemon accepts the result
as its own and no competing writer can barge mid-install. If the target worktree is launched during
that bounded install window, Phoenix reports unavailable until a relay can elect or join its daemon;
it never serves through a different read-only topology.

Platform policy: **Windows** reconciles with one targeted delta (git diff of
`indexed_commit->HEAD` UNION git status dirt — no fresh-checkout sweep); **Linux** always
runs an anchored full sweep of the sibling tree (`usedFullSweep: true`); **macOS** returns
`unsupported_platform` for both `worktrees` and `index_worktree`.

The review session then starts its own phoenix on the worktree — a **relative**
`--workspace-root .` in a checked-in `.mcp.json` serves the main enlistment and every
worktree identically, and the seeded index is queryable immediately. `worktrees` lists all
worktrees with per-index status (schema, indexed commit, in-sync) — loop it for "refresh
all". A worktree whose own Phoenix daemon is running reports `worktree_index_locked`; refresh from
that worktree's Phoenix session (`refresh_index`) instead.

## Server CLI

```text
PhoenixCodeNav.Mcp.exe --workspace-root <dir> [--index-db <path>] [--rebuild]
    [--standalone] [--keepalive]
```

The shared daemon is unconditional for ordinary launches. `--shared-daemon` remains accepted only
as an unnecessary compatibility alias, and the former `--daemon-fallback-standalone` spelling is an
inert compatibility no-op. `--standalone` is reserved for diagnostics and isolated tests; it never
acts as a fallback and refuses to serve if it cannot own the writer lease. `--rebuild` is honored by
the shared daemon or by an explicitly selected standalone writer.

## Development

```text
dotnet test tests/CodeNav.Tests                                  # fast unit + contract checks
dotnet test tests/CodeNav.IndexTests                             # index + semantic functionality
dotnet test tests/CodeNav.GitTests                               # Git/worktree manipulation
dotnet test tests/CodeNav.WatcherTests                           # watcher timing
dotnet test tests/CodeNav.LifecycleTests                         # leases, publication, process lifecycle
dotnet test PhoenixCodeNav.sln                                   # complete solution suite
pwsh -NoProfile -File ./scripts/test-roslyn-mcp.ps1              # external Roslyn/F# MCP gate
node ./website/verify.mjs                                        # static-site contract gate
dotnet run --project src/CodeNav.WorkspaceGen -- --out C:/temp/acme-2k \
    --projects 2000 --density 6 --clean                          # synthetic enterprise repo
dotnet run --project src/CodeNav.Bench -c Release -- --workspace C:/temp/acme-2k --rebuild
dotnet run --project src/CodeNav.Bench -c Release -- --workspace C:/src/runtime \
    --db C:/temp/runtime-index.db --rebuild --build-only         # non-destructive cold-build gate
dotnet run --project src/CodeNav.Bench -c Release -- --workspace C:/temp/acme-2k --semantic
bash scripts/smoke-mcp.sh C:/temp/acme-2k                        # stdio protocol smoke test
```

The complete suite requires directory-link support: NTFS junction creation on Windows
(ordinary non-elevated NTFS is sufficient; Developer Mode is not required) and directory
symbolic links on Unix. Failure of either prerequisite is an infrastructure failure rather than
a green containment result. The website verifier additionally requires Node.js and Git
working-tree/index metadata (ordinary or split index) so it can verify that content-hashed assets are tracked without
launching a Git subprocess.

`--db` plus `--build-only` indexes the real workspace into an explicit scratch database and exits
after reporting phase time, total time, symbol count, and database size.

The external MCP gate requires the pinned Roslyn and F# submodules to be checked out. Its first run
builds missing `.codenav/index.db` files through normal MCP startup; later runs reuse those indexes.
If a reused index disagrees with its fresh-index baseline canary (overview counts plus Roslyn
reference-authority evidence), the gate performs one in-band full rebuild to preserve before/after
evidence, but that run still fails so a missing schema-version bump cannot
hide behind automatic repair. Only a later run against the repaired index may pass, and the rebuilt counts
must match the pinned fresh-index baseline.

Projects: `CodeNav.Core` (discovery, index, semantic layer), `CodeNav.FSharp` (isolated FCS syntax
and bounded semantic adapter), `CodeNav.Mcp` (server, ships as `PhoenixCodeNav.Mcp.exe`),
`CodeNav.Portal` (separately packaged loopback operations website), `CodeNav.WorkspaceGen`
(synthetic workspace generator),
`CodeNav.Bench` (benchmarks vs the brief's latency targets), plus focused unit, index, Git,
watcher, and lifecycle test projects under `tests/`.

## Known limitations (v1)

- SDK-style compile items are approximated by `<Compile Include>` glob expansion plus
  longest-dir-prefix heuristics (no MSBuild evaluation); explicit legacy `<Compile Include>`
  items — including linked files — are exact. Residual gaps: shared `.projitems`, props-level
  globs, and MSBuild `Condition`s are not evaluated.
- `search_text` regex mode (`regex:true`) is line-based .NET regex narrowed by FTS tokens —
  no multi-line patterns.
- Token-mode `search_text` grades at most 300 files after applying language/path/project/scope
  filters; clipped calls report `filesScanned`, `filesAtLeast`, and
  `partialReason:"candidate_file_cap"` rather than presenting bounded counts as complete.
- F# `search_symbol` persists syntax declarations from `.fs` / `.fsi` across indexed owner/TFM
  parse contexts, processing at most 64 deterministic contexts per file with one context reserved
  per valid compile owner while capacity remains, and disclosing exact
  total/processed/truncated coverage plus `truncatedOwnerProjects`; paired signature/implementation files remain separate deterministic hits, linked
  multi-owner files remain one physical declaration set, orphaned files are labeled, and failed
  parses, truncated contexts, or unavailable/unevaluated project-option contexts are disclosed as partial coverage;
  ordinary SDK/import limitations remain advisory structured coverage. `.fsx` stays text-only: script-only scopes are
  refused, while mixed scopes explicitly report skipped scripts. F# `outline` is syntax-only and
  limited to compile-owned `.fs` / `.fsi`.
  F# semantics are position-only (`symbol_at`, `definition`) within one physical project and target
  framework, over a documented MSBuild-evaluation subset. Unsupported authority either fails closed
  with a stable cause or continues only through the explicitly partial standard-SDK/toolchain and
  host-selected `FSharp.Core` boundaries described above. F# references, implementations,
  callers/callees, and hierarchy are not supported. Unscoped and explicit F# `search_symbol`
  scopes query the shared syntax index; results are partial when the scope contains a text-only
  language or when any F# parse context is failed/truncated or any actionable project-option
  context is incomplete.
- Indexed `references` are whole-identifier text candidates; use `mode="semantic"` (or the
  default auto-upgrade) for compiler-exact results. When the caller-selected `maxFiles` boundary
  is reached, the response reports candidate-file coverage, `totalIsLowerBound:true`, and
  `noteId:"references.candidate_file_cap"` instead of presenting the scanned subset as complete.
- C# operator handles pin semantic definitions and references with their uncapped declaration key;
  v3 handle fingerprints bind the file's existing content hash to the declaration's deterministic
  syntax ordinal among declarations on its source line. This distinguishes same-file declarations with identical display
  identity, while a file edit conservatively invalidates every handle from the previous file epoch,
  without computing or persisting a separate digest for every symbol. Older fingerprints fail closed.
  Conversion `references` walk compiler-bound operation trees across the selected dependent
  closure and supplement them with compound-operation, tuple-element, primary-constructor,
  `foreach` element, and deconstruction conversion APIs. Implicit contextual conversions,
  explicit and checked casts, stacked/nullable-tuple conversions, full C# compound-assignment
  input/output conversions, and `foreach`
  conversions are reported as `implicitConversion`, `explicitConversion`, and
  `checkedConversion`. Exact zero is returned only after the complete loaded scope contains no
  matching conversion. Distinct same-line operations remain distinct through source-span dedup.
  Indexed definitions retain the exact resolved operator row, while indexed or failed-automatic
  operator references fail closed because text candidates cannot preserve overload identity.
  Operator handles are explicitly rejected by `implementations` and `type_hierarchy`; Phoenix
  does not yet model the meaningful static-abstract-interface implementations subcase.
- Semantic `definition` and `references` normally fit the advertised 64 KiB `hardBytes` target.
  Optional declaration-site lists are removed first with truthful total/returned counts and stable
  note id `semantic.declaration_sites_budget`. If the remaining complete compiler identity
  is intrinsically larger, Phoenix preserves it without a new truncation or rejection limit and
  returns `responseBudget` with measured `serializedBytes`,
  `exceeded:true`, `completeIdentity:true`, and reason `indivisible_semantic_identity`.
- Semantic scans load all matching candidate projects by default (`maxProjects:0`). A positive
  `maxProjects` value is an explicit latency/memory tradeoff; bounded responses report the total
  skipped count and a size-bounded sample.
- When `implementations` falls back to indexed heuristics because the compiler path reports
  `cluster_cold_load` or `semantic_timeout`, it preserves that `partialReason` and heuristic
  confidence while returning `retryRecommended:true` plus a one-retry `retryHint`. Phoenix does
  not retry automatically or silently raise the requested deadline; non-partial exact responses
  omit the retry fields.
- Multi-TFM projects index a single symbol row per declaration (net472-first design).
- Git awareness covers freshness (indexed vs HEAD commit/branch), not navigation — a
  `recent_changes` tool, `xml_doc`, and `diagnostics` from the brief are not yet implemented.
