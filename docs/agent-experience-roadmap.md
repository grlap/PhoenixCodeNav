# Agent-first MCP experience roadmap

Implementation target: PhoenixCodeNav 0.12.72. Delivery remains subject to the full integration
gate and adversarial agent review; the sections below are the product contract and rationale.

PhoenixCodeNav exists for coding agents. Its product surface should optimize for the next
correct agent action: precise routing, bounded context, explicit uncertainty, and recovery
that can be followed without guessing.

This roadmap does not turn domain failures into MCP transport errors or hide indexed workspace
content. Stable compiler identity is never weakened into a compatibility name fallback.

## Product principles

- Prefer one high-value call over a sequence of exploratory calls, while keeping exact tools
  available when the agent needs proof.
- Make absence trustworthy. A zero-result response must distinguish "not found" from partial,
  stale, unsupported, or budget-limited coverage.
- Put the next move in the response. Recovery must be structured, bounded, and specific enough
  for an agent to execute.
- Keep healthy-path payloads compact without making capability discovery opaque.
- Preserve host-compatible schemas and established argument names unless the owner explicitly
  approves one simpler replacement, as with the queryScope-only scope contract. Otherwise add
  aliases and accepted encodings instead of breaking clients.
- Index the whole workspace. Default query scope may reduce noise, but the response must disclose
  that default and allow an explicit all-content query.

## Priority 0 — remove agent friction at the entry points

### Concise operating instructions

Keep the reusable agent prompt short and route by intent:

- orient with `repo_overview`;
- find a symbol with `search_symbol`, then use `context_pack` for orientation;
- use exact semantic tools for proof;
- use `impact` and `related_tests` before behavior changes;
- use `review_pack` for a bounded change-set view;
- inspect structured domain errors and follow `retryRecommended` plus `retryHint`.

Success means an agent can choose the right first and second call without carrying the entire
project-model implementation in its prompt.

### Agent-first served instructions

The concise routing rules must also live in the MCP `ServerInstructions` handshake, because that
surface reaches every connected agent without requiring a repository to paste a prompt. It should
name `context_pack`, `impact`, `review_pack`, and `related_tests`, state the structured domain-error
convention, and tell agents to follow `retryRecommended` plus `retryHint`, without turning the
handshake into a manual.

Success means every fresh MCP session receives the minimum routing and recovery contract needed to
use Phoenix well before making its first tool call.

### Language-scoped symbol search and local partiality

Add an optional language scope to `search_symbol`. Coverage and partiality must be computed for
the requested language and effective search scope, not inherited from unrelated failures elsewhere
in the index. A healthy C#-only lookup must not become partial because unrelated F# files failed to
parse. Mixed-language and all-language searches must continue to disclose every relevant gap.

Success means the same response identifies the applied language scope, reports local coverage, and
never promotes an incomplete scoped search to an authoritative miss. Unknown language values return
a bounded `bad_request` that names valid values, while existing path-scoped unsupported-language
coverage remains intact.

### Compact capability discovery

Keep `server_capabilities` useful as a cheap handshake: status, languages, budgets, confidence
semantics, and every stable feature identifier remain directly discoverable. Move verbose feature
summaries behind an explicit detail mode or a bounded feature-details request.

The default response sets `featureSummaryMode: "ids"`. `featuresCompacted` and
`featureSummariesReturned` are reserved for actual byte-budget compaction of requested summaries;
they do not describe the caller-selected compact mode.

Success means agents can grep or compare feature identifiers from the healthy response without
paying for prose they did not request.

## Priority 1 — make common calls forgiving and self-correcting

### Uniform zero-hit and project-selection recovery

Project selectors should accept the forms agents naturally possess with strict precedence: exact
workspace-relative path, exact suffixed project filename, extensionless filename stem, then
AssemblyName. A suffixed `.csproj` selector never cross-matches `.fsproj` (or vice versa), and
lower-precedence matches are disclosed rather than silently substituted.

Selector echoes and lower-precedence matches share the ordinary response byte budget. The selected
project and actionable graph/path payload survive before diagnostic shadow matches; responses keep
the true shadow total plus returned and truncation counts.

Assembly name is metadata, not unique project identity. When it matches multiple physical projects,
the response must list every matching physical row as ambiguity evidence and never pick the first.
Suggestion probing and the returned suggestion list are both bounded and disclosed.

Apply the same next-move shape to symbol and project misses: stable reason, effective scope,
coverage, bounded suggestions, and a concrete retry template.

### Consistent list inputs

Extend the existing host-compatible convention across list-like string arguments, including
symbol kinds, usage kinds, and source spans: accept either comma-separated text or a JSON-array
encoded string. Normalize both forms identically and preserve the published string schemas so MCP
hosts do not lose compatibility.

### Documentation-comment selectors

Allow `documentationCommentId` wherever a stable symbol selector is accepted for definition,
references, and implementations. Echo the resolved symbol identity and ambiguity evidence. As part
of this work, audit that existing `symbolId` values are emitted consistently; do not create a
separate identifier-renaming project.

Accepted identifiers include type, method, property, field, and event forms, including constructor
and operator spellings. Malformed identifiers return bounded `bad_request`; an operation that does
not support the selected symbol kind keeps its stable error rather than silently retargeting.

A documentation ID always takes the compiler-semantic route: `auto` means semantic for this
selector, `indexed` is rejected, and semantic failure never falls back to a name scan. Seed lookup,
compiler resolution, and the requested operation share one deadline. Successful responses compare
canonical Roslyn identity end-to-end; pre-compilation misses remain indexed and name the missing
evidence, while absence becomes exact only after a complete compiler sweep. Seed discovery uses
only the innermost declaring type identifier and reports distinct indexed seed files. When compiler coverage is incomplete, any exact
candidate remains byte-bounded evidence for an explicit position choice and is never selected as
unique. Candidate evidence includes project, assembly, path, line, and column. If linked projects
share the same position, a stable note states that the current tool surface cannot disambiguate the
assemblies. Skipped-project evidence reports total, returned, and truncation. Deadline failures
disclose the effective and per-tool maximum deadline so every retry hint remains executable.

### Uniform transient retry guidance

Extend the established transient retry contract beyond `implementations` to `definition`,
`references`, `callers`, `callees`, and `type_hierarchy`. The same cold-load and semantic-timeout
causes should produce consistent `retryRecommended` and `retryHint` fields, with no unbounded retry
behavior and no change to permanent-failure semantics.

## Priority 2 — improve bounded review and repository-scale signal

### Actionable review-budget gaps

When `review_pack` cannot cover the full change set, report the total affected-path count, returned
count, truncation state, stable reason identifier, and a bounded list of affected paths. The affected
path list needs its own fixed disclosure bound so the recovery payload cannot become the next budget
problem.

The established metadata sampler returns at most eight paths and 512 serialized bytes. The response
must report those exact operative bounds—never a larger advertised limit than the sampler enforces.

### Dependency-direction aliases

Accept `dependencies` as an alias for canonical `downstream` and `dependents` as an alias for
canonical `upstream`. Responses should echo that canonical direction so agents can learn the
vocabulary without breaking existing callers. Tests must assert the edge sets for both aliases,
because an echoed label alone cannot detect an inverted implementation.

### Explicit default query scope

Support an overridable default query scope, such as first-party content, while continuing to index
the entire workspace. Every coverage-bearing response must echo the applied default. Agents can use
an explicit all-content scope when completeness across generated, vendored, or external content is
required.

Exact semantic operations (`definition`, `references`, `implementations`, `type_hierarchy`,
`callers`, and `callees`) must never be silently narrowed by that default. They must either ignore
it or visibly degrade coverage and confidence when it limits candidate discovery. The configured
default must also be discoverable through `server_capabilities`.

`queryScope` is the sole scope control: `default`, `all`, or `first_party`. As with other optional
string arguments, empty means unspecified/default and whitespace-only input is invalid. Phoenix does
not retain a second compatibility argument or undocumented spelling aliases for the same decision.

This is a query-time policy only. Index-time ignore rules are outside this roadmap because they can
erase evidence and weaken coverage claims.

## Cross-cutting delivery contract

Each MCP surface change receives its own stable `features[]` identifier and a user-visible version
bump. Every change preserves bounded responses, explicit coverage, existing names except for an
explicitly owner-approved simplification, and structured domain-error conventions. Tests should exercise agent-observable contracts at the product boundary;
repository support tooling, agent prompts, Markdown policy, and deployment plumbing do not belong in
product test projects.

## Explicit non-goals

- Do not add default index-time exclusions.
- Do not convert structured domain failures into MCP transport `isError` failures.
- Do not replace `upstream` and `downstream`; add aliases.
- Do not change list-like arguments to array schemas when that breaks MCP host compatibility.
- Do not split symbol-identity consistency into an unrelated standalone project.
