# Design: dispatch-map — resolving runtime-dispatched call edges

Status: **Draft / proposal** — not started, not decided. Captures the contract proposed
2026-07-07 plus an engineering assessment of the hard parts, so the plan can be
pressure-tested before any build. Where this doc and a future implementation disagree, the
code wins.
Relates to: [`design.md`](./design.md) (the static index this complements),
[`intro.md`](./intro.md) (why grep is not enough).

## Problem

Static navigation — phoenix's syntactic index and its bounded Roslyn semantic layer — resolves
edges the compiler can see: calls, interface implementations, overrides. In a large enterprise
.NET codebase a large fraction of the *real* call graph is **chosen at runtime** and is
invisible to that analysis:

- **Castle Windsor DI** — `Component.For<IFoo>().ImplementedBy<Foo>()`, conventions, typed
  factories, interceptors, sub-resolvers. Following `IFoo.Bar` lands on an interface; which
  concrete is wired is in an installer somewhere.
- **Reflection with string type names** — `Activator.CreateInstance(Type.GetType(name))`,
  `Assembly.Load(...).GetType(literal)`, project-specific helpers like
  `Reflection.DynamicallyLoadClass<T>`. The concrete is a *string*, often passed in from a caller.
- **Factory methods with type-branching bodies** — one method returning different concretes per
  branch/condition (and a fallback in a `finally`).
- **Message-bus / handler dispatch** — a message type resolved to a handler at runtime.
- **Hand-curated bindings** — anything the analysis genuinely cannot derive.

At each of these an agent's symbol-hop dead-ends and it falls back to grep — exactly the failure
phoenix exists to remove, reappearing at the hardest spots. `implementations` returns *all*
candidate impls heuristically; it cannot say **which one this site actually dispatches to**.

## Goals

- Resolve a virtual/interface/reflected/factory site to its **concrete runtime target(s)**, with
  the **indirection path** (`callChain` / `via`) that explains *why*, not just the answer.
- Support the reverse direction: **who reaches this concrete type** via indirection — the
  blast-radius query you run before deleting or renaming.
- Be **honest about confidence** to a fault: a false "exact" is worse than no answer, because the
  agent will follow a phantom edge and stop checking.
- **Complement, not replace** phoenix: invoked at the points where static resolution stops.

## Proposed contract (from the 2026-07-07 proposal, lightly refined)

Server/capability name `dispatch-map`. Every response carries the phoenix-style envelope so an
agent uses both with one mental model:

```jsonc
"meta": {
  "confidence": "exact" | "narrowed" | "inferred" | "curated",
  "indexStatus": "ready" | "refreshing" | "index_building",
  "lastRefreshUtc": "...",
  "sources": ["castle", "reflection", "factory", "curated"]
}
```

Five tools:

1. **`resolve`** — interface/virtual member (by name **or** usage position) → concrete `targets[]`,
   each with `type`, `member`, `decl` span, and `edges[]`. Each edge carries `kind`
   (`castle|reflection|factory|curated|impls|fallback`), the `site`, a **`callChain`** (the
   indirection path), a human `resolution`/`via` string, and per-edge `confidence`. `edgeKinds`
   filters; `impls` folds in phoenix's static implementations so one call answers "what does this
   resolve to" across both static and runtime edges.
2. **`explain`** — a position ("what does *this* line dispatch to") → `site` classification +
   `resolves[]` (each with `via`) + `unresolved[]` (populated when an argument is non-literal).
3. **`reverse`** — a concrete type → `reachedBy[]` (interface composition, reflection sites, …).
   The refactor-safety query.
4. **`factory_targets`** — a factory-shaped method → `returns[]`, each tagged with the `branch`
   condition and confidence.
5. **`list_bindings`** — audit/observe the curated map (and, optionally, derived bindings) by
   kind/namespace.

The `callChain`/`via`/`resolution` payload is the point of the whole thing: it is what makes an
agent *act* on the answer instead of re-verifying by hand.

## The hard part: the analysis engine (build in reliability tiers)

The contract is the easy 20%. Populating the map is the project, and each source has a different
cost/reliability. **Ship value tier by tier — do not build the dataflow engine first.**

| Tier | Source | Technique | Reliability |
|---|---|---|---|
| 1 | **Curated bindings** | Read a checked-in map; `list_bindings` audits it | Exact by fiat; zero analysis; immediate value |
| 2 | **Castle literal registrations** | Static parse of `Component.For<>().ImplementedBy<>()` etc. | High for literal idioms; **measure coverage** — Windsor has ~100 registration shapes (conventions, `Classes.FromThisAssembly()`, factories) |
| 3 | **Reflection, intraprocedural literal strings** | Roslyn constant/string value at the load site | High when the string is a local literal |
| 4 | **Reflection, interprocedural strings** | Constant propagation across the call chain | **The crux.** The motivating example flows a literal `className` `GetSyncInterfaceProxy → GetInterfaceProxy → DynamicallyLoadClass`. Needs bounded interprocedural dataflow |
| 5 | **Factory branch CFA** | Control-flow analysis enumerating return types per branch | The tail; abstract-interpretation-lite |

An MVP of **tiers 1–2** already proves the contract with real answers before any dataflow
investment. Tier 4 is where most of the differentiated value *and* most of the difficulty live.

### Confidence discipline (load-bearing)

The entire value proposition is trust. Bias hard toward `inferred`/`narrowed` + `via` +
list-the-alternatives; reserve `exact` for cases that are provably so (a resolved literal, a
single unconditional Windsor registration). A concatenated "literal", or a registration overridden
later conditionally, must **never** be labelled `exact`.

### `reverse` blind-spots (why it needs the most care)

`reverse` is the safety tool, so its value is inversely proportional to its **false-negative**
rate. "Nothing reaches this type" authorizes a deletion; if an un-analyzed reflection edge (a
non-literal string) actually reaches it, that green-light is wrong. `reverse` must **always**
declare its blind spots in-band (e.g. `"caveats": ["non-literal-string reflection not captured"]`)
and should be the most conservative tool in the set.

## Architecture (decisions to make)

- **Reuse phoenix's semantic core; do not rebuild Roslyn project-loading.** The dataflow analysis
  needs exactly the `AdhocWorkspace`/compilations phoenix already builds for `definition`/
  `references`. A separate process re-loading ~2000 csproj is wasteful and creates two freshness
  models. Lean: dispatch-map as a **capability/tool-group sharing phoenix's core library**, not a
  second workspace. The `impls`-fold-in reinforces this.
- **Open tension — loading scope.** phoenix deliberately *bounds* semantic loading (candidate
  clusters + timeouts) because full-solution Roslyn is infeasible at this scale. Interprocedural
  dispatch resolution wants *broader* loading than a single cluster (the reflection literal's
  caller may be in another project). Prototype this loading-strategy conflict first — it is the
  main technical risk to the whole idea.
- **Build cadence.** DI/reflection topology changes far less often than code, so a slower/batch
  analysis with a `refreshing` window is acceptable — it need not match phoenix's live per-file
  refresh.

## Phased plan

1. **Spike** the loading-scope conflict: can we resolve one real interprocedural reflection edge
   (the `DynamicallyLoadClass` example) on phoenix's semantic layer within a tolerable budget? This
   gates everything.
2. **MVP (tiers 1–2):** curated bindings + Castle literal registrations, behind `resolve` /
   `explain` / `list_bindings`, with the envelope + `via`. Real value, no dataflow.
3. **`reverse`** over the tier-1/2 edge set, with explicit caveats.
4. **Tier 3–4** reflection string resolution (intra- then inter-procedural).
5. **`factory_targets`** (tier 5).

## Non-goals (for now)

- A general whole-program points-to/alias analysis. Stay targeted at the dispatch idioms this
  codebase actually uses.
- Resolving genuinely dynamic strings (config-driven, computed at runtime) — surface them as
  `unresolved`, never guess.
- Replacing phoenix's `implementations` — `resolve` *folds it in*, it does not duplicate it.

## Open questions

- Separate MCP server vs. phoenix tool-group — the reuse argument favors a shared core; the
  branding/surface can still read as a distinct capability.
- Where does the curated map live (checked-in file in the target repo?) and who maintains it?
- Coverage honesty: how do we *measure and report* what fraction of Castle registrations / reflection
  sites we actually resolve, so an agent knows the map's completeness?
