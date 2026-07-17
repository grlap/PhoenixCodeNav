# Design: concept search — embedding-backed code discovery

Status: **Draft / proposal** — not started, not decided. This document defines the
product contract and an implementation shape to validate before dependencies or index formats
are selected. Where this document and a future implementation disagree, the code wins.

Bead: `PhoenixCodeNav-p8vw`

Relates to: [`intro.md`](./intro.md) (why agent navigation needs more than grep),
[`design.md`](./design.md) (the existing text, syntax, compiler, graph, freshness, and
writer/follower architecture), and [`agent-instructions.md`](./agent-instructions.md) (tool
routing for agents).

## Summary

Phoenix is strong once an agent knows a file, symbol, identifier, or literal. It is weaker when
the agent knows only the *idea* it is looking for:

> Where do we recover from temporary failures when submitting an invoice?

The relevant code may be named `CreateResiliencePipeline`, `withBackoff`, or
`HandleTransientGatewayFault`. Text search cannot reliably bridge that vocabulary gap.

This proposal adds an optional `search_concept` Model Context Protocol (MCP) tool backed by
embeddings. It returns a small ranked set of source chunks and, where possible, their Phoenix symbol
handles. The agent then uses Phoenix's existing compiler and project-graph tools to verify the
candidates.

The central rule is:

> **Concept search discovers candidates; compiler-backed navigation verifies them.**

Embedding results are always `heuristic`. They never become `exact`, never claim exhaustive
references or reachability, and never prove that code is absent or dead.

The feature is local-first, optional, independently rebuildable, and kept off Phoenix's base-index
readiness path. If it is disabled, building, corrupt, or unavailable, all existing Phoenix tools
continue to work unchanged.

### Proposal at a glance

| Question | Proposed answer |
|---|---|
| What is added? | One optional `search_concept` MCP tool over symbol-aware source chunks |
| How does it search? | Local query embedding + chunk-level lexical search, fused into one bounded ranking |
| What does a hit mean? | "This is a plausible place to inspect," never proof or exhaustive coverage |
| How is it verified? | Existing Phoenix source, Roslyn, F# Compiler Service (FCS), and project-graph tools |
| Does it block Phoenix startup? | No; it uses a separate disposable index and only complete published generations |
| What ships as MVP? | Phases 0–3: evaluated model/backend, hybrid query contract, and durable incremental lifecycle |

Reading guide: if embeddings are new to you, read [Why Phoenix needs this](#why-phoenix-needs-this),
[How this positions Phoenix](#how-this-positions-phoenix), and the
[Concept primer](#concept-primer) first. The [MCP surface](#proposed-mcp-surface) shows the
user-visible proposal. [Indexing](#indexing-architecture),
[lifecycle](#build-refresh-and-snapshot-lifecycle), and [evaluation](#quality-evaluation) form the
implementation/acceptance contract and can be read separately.

## Why Phoenix needs this

Phoenix currently answers three classes of questions well:

| Known input | Best current tool | Example |
|---|---|---|
| Exact text or literal | `search_text` | `ERR_BILLING_409` |
| Identifier or source position | `search_symbol`, `definition`, `references` | `SubmitInvoiceAsync` |
| Project relationship | `project_graph`, `dependency_path` | What depends on `Billing.Core`? |

There is a missing fourth question:

> I understand the behavior or business concept, but I do not know what the code is called.

An agent can approximate this today by repeatedly inventing keywords, running text searches,
opening files, and trying again. In a 2,000-project repository this has predictable costs:

- The prompt's vocabulary may differ from the implementation's vocabulary.
- Generic names such as `Execute`, `Process`, `Policy`, and `Handler` produce weak matches.
- C# and F# may express the same behavior with different idioms and naming styles.
- Each failed search/read cycle consumes time and model context.
- Dead, generated, test, and vendored code compete with the production edit surface.

Concept search is intended to produce the first useful symbol or file quickly. It does not
replace the rest of the navigation workflow.

## How this positions Phoenix

Cursor already combines embedding retrieval with fast lexical search and editor language services;
embeddings alone are therefore not a Phoenix differentiator, and this proposal makes no claim that
Phoenix's fuzzy ranking is better before the repository benchmark. Phoenix's differentiation is the
MCP-facing evidence contract around the result: stable source identity where proven, direct routing
into compiler/project verification, and explicit freshness, coverage, and budget disclosure.

Phoenix can also complement Cursor rather than replace it. A Cursor agent can call Phoenix over MCP:
Cursor remains the interactive editor and Phoenix supplies repository-scale discovery plus its
compiler/project evidence contract.

Cursor-style indexing and Phoenix currently optimize different parts of navigation:

- Embedding search is good at fuzzy, natural-language discovery and finding similar code.
- Phoenix is good at deterministic source spans, symbol identity, references, implementations,
  static call edges, project membership, dependency paths, freshness, and coverage disclosure.

Adding concept search makes Phoenix a two-stage navigation system:

```text
DISCOVER                                      VERIFY

natural-language query                        candidate symbol/position
        |                                              |
        v                                              v
embeddings + lexical retrieval       ->      Roslyn / FCS + project graph
        |                                              |
        v                                              v
small ranked candidate set                     bounded code evidence
```

Phoenix should not position the feature as "embeddings but local" or as a replacement for an
editor. The stronger position is:

> **Agent-agnostic conceptual discovery connected directly to compiler-backed verification,
> with explicit provenance, coverage, freshness, and response budgets.**

Unlike an editor-owned index, the same MCP contract can serve Codex, Claude, Cursor, and other
compatible clients. Unlike an embedding-only retriever, results can flow directly into
`definition`, `references`, `implementations`, `context_pack`, and `impact`.

The proposed agent routing is:

| Starting point | First tool | Then |
|---|---|---|
| Behavior described in ordinary language | `search_concept` | Verify returned symbol IDs with `context_pack` or compiler tools |
| Known identifier | `search_symbol` or compiler tool | Do not detour through embeddings |
| Exact literal, error, route, or regex | `search_text` | Inspect the owning symbol |
| Rename, deletion, or blast-radius question | Compiler tools + project graph + text search | Build/test and consider runtime evidence |

## Concept primer

This section explains the machinery without assuming machine-learning experience.

### 1. Chunks: the units being searched

A repository is too large and too structurally varied to represent as one item. Concept search
splits it into **chunks**, each of which can be retrieved independently.

For Phoenix, a chunk should normally follow code structure:

- a C# type, method, constructor, property, or other member;
- an F# module section, type, member, function, or value;
- a bounded part of a very large symbol; or
- a compact type/module overview containing its declaration and member names.

For example, a chunk might be represented to the embedding model as:

```text
language: csharp
namespace: Billing.Resilience
containing type: GatewayPolicy
symbol: CreatePipeline
signature: ResiliencePipeline<HttpResponseMessage> CreatePipeline(...)
documentation: Creates the retry policy used for transient gateway failures.
source:
    ...method body...
```

The extra context matters. A method body containing only `ExecuteAsync` and `Handle` is weak by
itself; its namespace, containing type, signature, and documentation make its purpose clearer.

Chunk size is a tradeoff:

- A chunk that is too large mixes multiple concepts and dilutes the useful part.
- A chunk that is too small loses the surrounding meaning.
- Overlap reduces boundary misses but creates duplicates and increases index size.

The chunker therefore needs a model-specific token budget and syntax-aware splitting rather
than a fixed number of lines.

A **token** is a unit the model's tokenizer produces from text. It may be a whole short word, part
of a word, punctuation, or a code fragment; it is not the same thing as a character or source line.

### 2. Embeddings: learned coordinates for text and code

An **embedding model** converts text or code into a fixed-length list of numbers called a
**vector**:

\[
E(\text{chunk}) \rightarrow [v_1, v_2, \ldots, v_d]
\]

The vector may contain hundreds or thousands of numbers. The individual dimensions do not have
simple human labels such as "billing" or "retry." The useful property is geometric: inputs the
model considers related tend to be placed near one another.

For example, a suitable model may place these near one another even though their words differ:

```text
Query:
"Where do we recover from temporary billing gateway failures?"

C#:
CreateResiliencePipeline().AddRetry(...).Handle<HttpRequestException>()

F#:
let submitWithBackoff request =
    resilientCall transientHttpFaults (fun () -> gateway.Send request)
```

The model has learned associations among temporary failures, retry, resilience, backoff, HTTP
exceptions, and recovery. It has **not** proved that either result implements the requested
behavior.

The user's query is converted into a vector by the same compatible model. Search then looks for
stored chunk vectors close to the query vector.

Indexing and querying happen at different times:

```text
INDEX TIME (background, reusable)          QUERY TIME (interactive)

source -> chunks -> embedding model        question -> same embedding space
                    |                                   |
                    v                                   v
              stored vectors  <--- nearest search --- query vector
                    |
                    +--- path/span/source-hash metadata
```

Phoenix pays the expensive document-embedding cost when code changes. A normal query embeds only
the short question, searches the already stored vectors, and validates returned locations against
one current base-index snapshot.

Embeddings are not generated prose and do not require an LLM to answer the question. They are a
compact learned representation used for ranking.

### 3. Similarity: deciding what is close

A common comparison is **cosine similarity**:

\[
\operatorname{cosine}(q,c)
=
\frac{q \cdot c}{\lVert q \rVert\lVert c \rVert}
\]

Here `q` is the query vector and `c` is a code-chunk vector. If vectors are normalized to length
one, cosine similarity is simply their dot product.

A larger score means only that the selected model placed the query and chunk closer together.
It does **not** mean:

- `0.82` is an 82% probability that the result is relevant;
- `0.82` from one model is comparable to `0.82` from another model; or
- one universal threshold separates correct and incorrect results.

Scores vary with the model, query, language, repository, and chunking strategy. Phoenix should
treat them as ranking inputs, not user-facing confidence probabilities.

### 4. Vector search and nearest neighbors

With `N` stored chunks of `d` dimensions, comparing a query with every vector costs roughly:

\[
O(Nd)
\]

An exact scan is simple and provides a useful correctness oracle, but it may become too slow as
the index grows. A **vector index** is a data structure designed to find the nearest vectors more
quickly.

Large vector indexes often use **approximate nearest-neighbor** search, or ANN. ANN trades a
small chance of missing a nearby vector for lower latency. HNSW is one common ANN family, but the
feature contract must not depend on one algorithm.

The embedding model and vector index solve different problems:

- The model decides where chunks live in the vector space.
- The index makes searching that space efficient.

Phoenix will request the nearest `k` candidates, known as **top-k retrieval**. Top-k is bounded;
it is never an exhaustive list of every conceptually relevant location.

### 5. Precision and recall

Two useful quality measures are:

\[
\text{precision} =
\frac{\text{relevant results returned}}{\text{all results returned}}
\]

\[
\text{recall} =
\frac{\text{relevant results returned}}{\text{all relevant results}}
\]

High precision means most returned results are useful. High recall means few useful results were
missed.

Concept discovery initially favors recall: retrieve enough plausible candidates that the right
area is unlikely to be missed. Response shaping then favors precision because irrelevant chunks
consume the agent's context budget.

There are two independent ways to lose recall:

- **Model/chunking recall:** the relevant code was embedded, but the model placed it far from the
  query or the useful context was split badly.
- **ANN recall:** the relevant vector was close, but an approximate index failed to retrieve it.

An exact vector scan can measure the second problem, not the first.

### 6. Hybrid retrieval

Vector and lexical search have complementary strengths:

- Vector search handles vocabulary mismatch and conceptual questions.
- FTS/BM25/grep handles exact identifiers, acronyms, literals, error codes, and rare technical
  terms.

**Full-text search (FTS)** builds a lexical index over words/tokens rather than scanning every byte.
**BM25** is a common lexical ranking formula that rewards useful term matches while discounting very
common terms and excessive repetition. Grep remains best for direct literal/regular-expression
matching; this chunk-level FTS is a candidate-ranking channel.

Phoenix should run both by default and fuse their candidate rankings. Raw FTS scores and vector
similarities are not directly comparable, so the MVP uses **Reciprocal Rank Fusion** (RRF):

\[
\operatorname{RRF}(x) = \sum_i \frac{1}{\text{rrfConstant} + \operatorname{rank}_i(x)}
\]

RRF combines rank positions instead of pretending unrelated score scales mean the same thing.
For example, with `rrfConstant = 60`, a chunk ranked 1st by vectors and 4th lexically scores
`1/61 + 1/64`; a vector-only chunk ranked 2nd scores `1/62`. The first chunk wins because two
retrieval channels support it. `rrfConstant` and candidate budgets are evaluation parameters, not
trust levels.

Phoenix honors explicit project/path/language filters before fusion. After fusion, deterministic
rules can:

- prefer an exact identifier token;
- demote generated, vendored, or test code without silently excluding it;
- collapse overlapping chunks and duplicate declarations; and
- cap repeated results from one file or symbol.

### 7. Reranking

A reranker jointly examines the query and a small candidate set. It can make finer distinctions
than independent embeddings, but adds another model, more latency, more deployment surface, and
another source of heuristic behavior.

No learned reranker is part of the MVP. RRF plus deterministic ranking is inspectable and gives
a clean baseline. A reranker may be evaluated later behind the same `heuristic` contract.

### 8. Model and index versioning

Vectors from unrelated models do not share a dependable coordinate space. Queries and stored
chunks must use compatible model versions, dimensions, tokenization, and normalization.

Phoenix must record at least:

- model ID, model artifact hash, and vector dimension;
- normalization and numeric representation;
- chunker version and canonical-input format version;
- source content hash and chunk content hash;
- base Phoenix index generation/commit;
- concept-index generation and indexed timestamp.

A model or canonical-input change rebuilds the concept index. It does not make the existing text,
syntax, or compiler index unusable.

### 9. Concept similarity is not compiler semantics

The word *semantic* is overloaded:

| Property | Concept/embedding search | Compiler-backed navigation |
|---|---|---|
| Meaning of "semantic" | Conceptually related according to a learned model | Program element selected by language/build rules |
| Input | Natural language or example code | Symbol or source position |
| Output | Ranked chunks | Definitions, references, implementations, static call edges |
| Vocabulary mismatch | Strong | Not its purpose |
| Overload resolution | No guarantee | Yes within the modeled compilation |
| Exhaustive | No; top-k and heuristic | Potentially within declared scope and reported coverage |
| Typical failure | False positives or false negatives | Missing/unsupported compiler inputs or bounded scope |
| Proof of dead code | No | Still no; only stronger static evidence |

To avoid this ambiguity, the public tool and navigation-layer name is **concept**, not
`semantic_search`. Phoenix reserves **semantic** for its compiler-backed Roslyn/FCS layer.

## Goals

- Find likely relevant code in source languages advertised by the deployment when identifiers are
  unknown. C# is required for the production MVP; F# joins the contract only when its base-index and
  concept capability are enabled and quality-gated.
- Find conceptually similar implementations even when names differ.
- Return small, line-addressable, budget-bounded results with project/symbol metadata.
- Hand candidates directly to Phoenix's compiler-backed and graph tools.
- Combine conceptual and lexical retrieval rather than weakening exact-text behavior.
- Preserve Phoenix's local-first, read-only-source, agent-agnostic design.
- Report model, index generation, freshness, index coverage, retrieval bounds, and failures
  honestly.
- Build and refresh independently so concept indexing never delays base Phoenix readiness.
- Support an empirical model/backend bake-off on the real large repository.

## Non-goals

- Replacing `search_text`, `search_symbol`, Roslyn/FCS, or the project graph.
- Claiming exhaustive references, runtime call graphs, reachability, or dead code.
- Generating answers, explanations, or summaries with an LLM.
- Automatically selecting files for deletion or authorizing a refactor.
- Silently uploading source code or silently downloading a model.
- A cloud-only embedding dependency in the MVP.
- A learned reranker, LLM query expansion, or hypothetical-document generation in the MVP.
- Indexing arbitrary binaries, build outputs, logs, or files outside Phoenix's workspace/path
  safety rules.
- Team-wide remote index distribution in the MVP.

## Decisions made by this proposal

1. The tool is named `search_concept`; its navigation layer is `concept`.
2. Concept retrieval is a parallel discovery path beside FTS, not a confidence level above
   syntax or compiler semantics.
3. Every result has `meta.confidence: "heuristic"`, regardless of similarity score.
4. Hybrid vector + chunk-level lexical retrieval is the default.
5. RRF and deterministic rules perform MVP fusion; no learned reranker is required.
6. Chunks follow symbols/source structure and carry stable path/span metadata.
7. The concept index is an independently versioned, disposable derived cache.
8. Base indexing, MCP handshake, and existing queries never wait for concept indexing.
9. The first delivery is local and explicitly enabled; no silent model acquisition occurs.
10. Model and production vector-backend selection are gated by repository-specific evaluation.
11. A flat exact vector scan exists as the quality oracle even if production uses ANN.
12. A failed concept subsystem degrades to a clear error/fallback, never a failed Phoenix server.

## Proposed user workflow

Given:

> Where do we recover when submission to the billing gateway fails temporarily?

The intended flow is:

```text
1. search_concept(query)
      -> C# GatewayResilience.CreatePipeline
      -> F# Submission.withBackoff
      -> GatewayRetryTests

2. context_pack(symbolId) or definition(path, line)
      -> exact/indexed declaration and local context

3. references / implementations / callers / project_graph
      -> compiler and structural evidence with coverage

4. search_text for configuration/reflection/string uses
      -> dynamic/non-compiler evidence

5. build and test the affected dependency slice
```

If the query is already `CreatePipeline`, the agent should skip step 1 and use symbol search.

## Proposed MCP surface

### `search_concept`

Purpose: find conceptually relevant source when the caller does not know the identifier.

Proposed signature, following existing Phoenix filter names:

```text
search_concept(
  query: string,
  mode: "hybrid" | "vector" = "hybrid",
  pathGlob: string? = null,
  excludePath: string? = null,
  firstPartyOnly: bool = false,
  project: string? = null,
  scope: "all" | "production" | "tests" = "all",
  lang: "cs" | "fs" | null = null,
  kinds: string? = null,
  includeGenerated: bool = false,
  limit: int = 10,
  candidateBudget: int = 100,
  timeoutMs: int = 5000
)
```

Contract details:

- `query` is 3–2,000 Unicode scalar values after trimming and at most 8,192 UTF-8 bytes. Values
  outside either bound fail as `bad_request`. It may be a question, behavior description, or short
  code example.
- `mode: "hybrid"` runs vector and chunk-level lexical retrieval. `"vector"` is primarily an
  evaluation/debug escape hatch; exact text remains the job of `search_text`.
- Existing path/project/test/generated filters retain their Phoenix meanings.
- Files in no known project's compile set are not silently excluded: results carry Phoenix's
  additive `orphaned` signal and are demoted. Compile-item gaps mean this is useful dead-code
  evidence, not proof; a future hard filter would need the same coverage disclosure.
- `kinds` is a comma-separated allow-list such as `type,method,function,module`; unknown values
  fail as `bad_request` rather than being silently ignored. A string is retained for consistency
  with Phoenix's existing filter surface; a future breaking API revision should prefer an enum
  array.
- `limit` defaults to 10 and accepts 1–20. `candidateBudget` defaults to 100, accepts 20–500, must
  be at least `limit`, and bounds each retrieval channel before fusion. `timeoutMs` defaults to
  5,000 and accepts 250–30,000. These values are rejected—not silently clamped—when invalid, and
  the response reports effective budgets.
- Deep paging is deliberately absent in the MVP; conceptual result pages encourage context dumping
  instead of query refinement.
- The initial evaluated query language is English. `server_capabilities` advertises evaluated query
  languages separately from source languages; C#/F# source support does not imply multilingual
  natural-language quality. The string API may receive other languages, but Phoenix makes no
  supported-quality claim for them until each passes its own evaluation gate.
- Only committed complete generations are searchable. If no complete generation exists, the tool
  returns `concept_index_building`; file-order-biased live staging is never exposed.

Tool description shown to an agent:

> Find code by behavior or concept when you do not know its identifier. Results are heuristic
> ranked leads, not exhaustive references. Follow returned symbol IDs/positions with
> `context_pack`, `definition`, or other compiler-backed tools.

### Response shape

Illustrative response; field names are part of the proposal, values are examples:

```jsonc
{
  "meta": {
    "indexStatus": "ready",
    "indexVersion": "...",
    "confidence": "heuristic",
    "navigationLayer": "concept",
    "confidenceNote": "learned similarity/ranking lead — verify with source and compiler tools",
    "build": "...",
    "indexSchema": "...",
    "indexMode": "follower"
  },
  "conceptIndex": {
    "status": "ready",
    "generation": "c:8f12...",
    "generationBaseIndexVersion": "...",
    "queryBaseIndexVersion": "...",
    "baseRelation": "current", // or "behind"
    "indexedCommit": "...",
    "model": "local:model-name@artifact-hash",
    "chunkerVersion": 1,
    "retrievalPolicyVersion": "rp:3",
    "vectorSearch": "exact", // or "approximate"
    "pendingChangesKnown": false
  },
  "results": [
    {
      "rank": 1,
      "path": "src/Billing/Resilience/GatewayPolicy.cs",
      "startLine": 42,
      "endLine": 78,
      "language": "csharp",
      "projects": ["Billing.Infrastructure"],
      "sourceHash": "xxh64:...",
      "mappingStatus": "current",
      "symbol": {
        "symbolId": "idx:123~fingerprint",
        "kind": "method",
        "name": "CreatePipeline",
        "container": "Billing.Resilience.GatewayPolicy",
        "signature": "..."
      },
      "matchedBy": ["vector", "lexical"],
      "vectorRank": 2,
      "lexicalRank": 4,
      "snippet": "...bounded excerpt from current canonical indexed text...",
      "isTest": false,
      "generated": false,
      "orphaned": false
    }
  ],
  "coverage": {
    "eligibleSources": 128400,
    "indexedSources": 128400,
    "failedSources": 0,
    "sourceGaps": 0,
    "eligibleChunks": 482100,
    "indexedChunks": 482100,
    "pendingChunks": null,
    "failedChunks": 0,
    "chunkDenominatorComplete": true,
    "generationCoverageComplete": true,
    "coverageAtBaseIndexVersion": "...",
    "relevanceCompleteness": "not_claimed"
  },
  "retrieval": {
    "mode": "hybrid",
    "boundedTopK": true,
    "vectorCompleted": true,
    "lexicalCompleted": true,
    "rankingCompleted": true,
    "partial": false,
    "returnedCountIsLowerBound": false,
    "deadlineHit": false,
    "vectorCandidates": 100,
    "lexicalCandidates": 100,
    "fusedCandidates": 163,
    "filterCandidateBudgetHit": false,
    "staleCandidatesDropped": 0
  },
  "notes": [],
  "truncated": false
}
```

Design notes:

- Raw similarity is omitted from the normal agent response. Component ranks communicate why a
  result surfaced without inviting the false interpretation that a score is a probability.
- An optional diagnostic surface may expose raw scores for benchmarks, but it must not change
  `confidence`.
- Coverage reports both source and chunk levels. A parser can fail before it can say how many chunks
  a source would have produced, so source-level gaps cannot be hidden in a chunk-only denominator.
  When `chunkDenominatorComplete:false`, eligible/indexed/failed chunk counts cover only described
  sources and are a known lower bound, not a complete repository denominator.
- `generationCoverageComplete` is relative to `coverageAtBaseIndexVersion`; it describes whether all
  eligible sources/chunks known at that committed base epoch were indexed. It is false when known
  chunks are pending/failed or when a base source/parser gap prevents the eligible corpus from being
  established. Counts remain the authority; the Boolean is only a convenience summary. It may be
  true while `baseRelation:"behind"`; clients use that separate field for current-base lag.
- `pendingChunks` is nullable. A follower reports `pendingChangesKnown:false` and `null` rather than
  falsely turning the writer's unknown in-process queue into zero.
- `relevanceCompleteness` is always `not_claimed`; top-k conceptual relevance has no honest
  repository-wide denominator.
- `matchedBy` reports evidence channels, not a natural-language explanation invented after the
  fact.
- Every returned source location is validated against the query's current base-index snapshot.
  `sourceHash` is the algorithm-qualified base file-version hash over original file bytes (currently
  xxHash64), not a claim about retained encoding or a cryptographic signature. Snippets come from
  the corresponding canonical indexed text in that snapshot and are budget-capped, never from stale
  embedding payload or model-generated prose.
- `mappingStatus:"current"` means the source hash and symbol fingerprint remapped to the captured
  base epoch, so `symbol` may contain a current handle. `mappingStatus:"unavailable"` means the
  canonical source text/span is current but no compiler/syntax symbol mapping is proved, so `symbol`
  is null.
  A changed or deleted source candidate is not returned by stale position; it is dropped and counted
  in `staleCandidatesDropped`.
- `noise:true`, when present, retains Phoenix's existing meaning: the path is under a recognized
  vendored/generated directory. It is omitted when false; it is not an embedding-quality judgment.
- `orphaned:true`, when present, retains Phoenix's existing additive meaning: no indexed project
  compile set owns the file. It is ranked lower but never called dead-code proof because project
  parsing/compile-item coverage may be partial.
- Potentially large nested lists, including compile-item owners, use the usual
  total/returned/truncated shape rather than disappearing under the whole-response byte cap.
- The whole response remains under Phoenix's existing ~64 KiB hard cap.
- The existing response-meta factory currently has a fixed heuristic note; implementation must
  generalize it or add a concept-specific factory so the advertised learned-similarity note is not
  overwritten by text/naming wording.

### No-result semantics

Vector search always has a nearest neighbor, even for a nonsensical query. Phoenix must not return
irrelevant code merely because something is mathematically closest.

The model bake-off will calibrate a conservative low-relevance policy. When all candidates fall
below that policy, return zero results with:

```jsonc
{
  "results": [],
  "notes": [{
    "id": "concept_low_relevance",
    "detail": "No sufficiently relevant lead was found in the indexed chunks; this is not proof of absence. Try a more concrete description or use search_text/search_symbol."
  }]
}
```

The policy is model/repository-specific. It must not be a hard-coded universal cosine threshold.
Phoenix emits `concept_low_relevance` only when the required retrieval channels completed; a zero
caused by timeout, filtering, stale-candidate validation, unsupported language, or index gaps carries
that different reason instead.

### Fatal errors, partial success, and notes

A **fatal error** returns the normal Phoenix error envelope and no results:

| Cause | Meaning and recovery |
|---|---|
| `concept_search_disabled` | Enable/configure the optional feature or use existing search tools |
| `concept_model_unavailable` | Compatible query model missing, invalid, or failed to load |
| `concept_model_mismatch` | Query provider does not match the committed index embedding space/dimension |
| `concept_index_building` | No complete committed generation exists; wait or use existing search tools |
| `concept_index_unavailable` | No readable committed concept generation |
| `concept_index_corrupt` | Concept cache failed validation; rebuild it; base Phoenix remains usable |
| `concept_base_unavailable` | This process cannot pin the required base-index snapshot; recover the writer/follower role |
| `concept_source_language_unavailable` | Caller explicitly requested a source language this deployment does not advertise |
| `concept_catalog_mismatch` | Follower/configured catalog is not bound to this workspace/base-index identity |
| `concept_writer_unconfigured` | No complete generation exists and the base writer is not configured to create/refresh one |
| `concept_query_timeout` | Deadline expired before a trustworthy ranked response could be completed |
| `bad_request` | Invalid query, mode, filter, kind, or numeric budget/deadline |

A response may instead succeed with `retrieval.partial:true` when every returned location is current
and trustworthy but candidate generation/ranking was bounded. Completion flags say exactly which
channels contributed. Stable `notes[].id` values include:

| Note ID | Meaning |
|---|---|
| `concept_filter_budget` | Native/post-filter oversampling could not fill the requested limit |
| `concept_deadline_partial` | The deadline stopped one or more channels after enough evidence existed to shape a bounded response |
| `concept_channel_partial` | Vector or lexical retrieval did not complete; ranks are over the completed evidence only |
| `concept_index_behind` | The committed concept generation targets an older base-index version |
| `concept_no_refresher` | A matching committed generation is readable, but the current base writer is not refreshing concept data |
| `concept_stale_candidates_dropped` | Changed/deleted source candidates were removed during current-snapshot validation |
| `concept_low_relevance` | Completed candidates did not pass the repository/model-specific relevance policy |

Partial success never changes `relevanceCompleteness:"not_claimed"`. If one channel fails, Phoenix
does not imply that its hybrid ranks are comparable to a full two-channel run; `vectorCompleted`,
`lexicalCompleted`, `rankingCompleted`, `deadlineHit`, and the note make the degradation explicit.
`returnedCountIsLowerBound:true` means a retry with completed channels/budgets may find additional
filter-satisfying candidates; it is not a lower-bound claim about all conceptually relevant code.

Illustrative fatal envelope (using Phoenix's existing flat stable-error convention):

```jsonc
{
  "error": "concept_index_building",
  "detail": "No complete concept generation is published yet.",
  "conceptIndex": { "status": "building", "generation": null, "progress": { "phase": "embedding", "processed": 12000, "total": 482100 } },
  "meta": { "indexStatus": "ready", "confidence": "heuristic", "navigationLayer": "concept" }
}
```

Illustrative partial-success fields (the normal success envelope, current validated results, and
coverage fields remain present):

```jsonc
{
  "results": ["...current-source-validated hits..."],
  "retrieval": {
    "mode": "hybrid",
    "vectorCompleted": true,
    "lexicalCompleted": false,
    "rankingCompleted": true,
    "partial": true,
    "returnedCountIsLowerBound": true,
    "deadlineHit": true
  },
  "notes": [{ "id": "concept_channel_partial", "detail": "Lexical retrieval did not complete before the deadline; ranks use vector evidence only." }],
  "meta": { "confidence": "heuristic", "navigationLayer": "concept" }
}
```

### Future `find_similar`

A later tool may accept `symbolId` or `path+line`, reuse that chunk's embedding as the query, and
find structurally/conceptually similar implementations. It is not required for MVP because it
adds no new indexing primitive and should be added only after `search_concept` quality is proven.

## Retrieval pipeline

The default query path is:

```text
validate and normalize query
        |
        +--> embed query --> filtered vector top-N ----+
        |                                              |
        +--> filtered chunk FTS/BM25 top-N ------------+--> oversample/union
                                                            |
                                      remaining hard filters + current-source validation
                                                            |
                                                 reciprocal-rank fusion
                                                            |
                                             deterministic boosts/penalties
                                                            |
                                            overlap/symbol deduplication
                                                            |
                                               top-k response shaping
```

### Query normalization

- Normalize line endings and Unicode into one documented form.
- Preserve original identifier spelling and punctuation in the model input.
- Produce a separate deterministic lexical form that splits common identifier shapes, for
  example `SubmitInvoiceAsync` → `Submit Invoice Async`, while retaining the original token.
- Do not call an LLM to rewrite, expand, or hypothesize an answer in MVP.
- Cache query embeddings in a bounded in-memory LRU keyed by normalized query + model fingerprint.
- Generate the query embedding before acquiring Phoenix's long-lived base-index/rebuild reader
  guard. Then pin the concept generation and base epoch, verify that the embedding-space ID still
  matches, and retry once if publication raced the query. A slow provider must not unnecessarily
  delay a destructive base rebuild.

### Lexical channel

Natural-language queries are a poor fit for the current line-level all-token semantics of
`search_text`. The concept subsystem therefore owns a chunk-level FTS table over the same
canonical chunk text used for embedding. It performs OR/BM25-style candidate retrieval with
rare/exact identifiers preserved.

This lexical index is derived data; `search_text` remains the authoritative exact-text tool.

### Filters and candidate budgets

Backends should apply language/path/project/classification filters before nearest-neighbor search
when they support it. When they cannot, Phoenix adaptively oversamples, filters afterward, and
reports when the candidate budget prevented it from filling the requested result count.

Native filtering is only an optimization when its metadata belongs to the pinned base version. If a
concept generation is behind, mutable project/compile-item/classification filters are re-evaluated
from the current base snapshot and are not pushed down in a way that could exclude newly eligible
candidates. This may cost recall within the bounded oversampling budget and is disclosed; every
returned candidate still satisfies the caller's hard filters in the current snapshot.

Phoenix must not silently present ten global candidates as complete project-scoped results after
the project filter removed the other ninety.

### Fusion and deterministic ranking

MVP ranking order:

1. Retrieve each channel with every filter that backend can apply natively.
2. Oversample, apply all remaining explicit filters as hard include/exclude rules, and drop any
   candidate whose captured path/source hash does not match the pinned current base snapshot.
3. Re-rank each surviving channel in its original order, then apply RRF over vector and lexical
   ranks.
4. Apply an exact whole-identifier match boost.
5. Apply stable classification preferences: compiled handwritten first-party production, then
   compiled tests, then orphaned source, then generated/vendor/noise unless caller filters say
   otherwise.
6. Deduplicate overlapping chunks and cap repeated results from one symbol/file.
7. Use a stable tie-breaker by normalized workspace path and start line.

Model/chunker/canonical-input/vector-build parameters belong to the immutable concept-generation
fingerprint. RRF constants, boosts, penalties, and diversity rules are query-time policy: they are
selected by evaluation, versioned independently, pinned for one query, and reported as
`retrievalPolicyVersion`. Changing only retrieval policy does not rebuild vectors.

## Indexing architecture

### Separate derived subsystem

The base `index.db` remains authority for files, source gaps, projects, compile items, symbols,
FTS, and freshness. Concept search is a disposable derived subsystem under:

```text
<workspace>/.codenav/concept/
  catalog/active-manifest
  spaces/<embedding-space-and-chunker-fingerprint>/generations/...
  cache/...
```

The workspace-level catalog is the only selector of the active embedding space/generation; a
per-space manifest cannot activate itself. The catalog is bound to the canonical workspace/base-
index store identity and can atomically switch between old and new spaces during model migration.
That stable binding is distinct from the changing `baseIndexVersion` recorded by each generation.

It contains:

- a bounded active/retained-generation catalog plus manifests with
  concept-schema/model/chunker/base-index identity;
- chunk location metadata, captured source/content hashes, and chunk-level lexical data;
- provider-independent content hashes and failure records; and
- vector-backend-owned data files.

The exact physical files are deliberately not fixed before the vector-backend spike. The
required behavior is fixed: atomic committed generations, snapshot reads, incremental upsert and
delete, validation, crash recovery, and independent rebuild.

Concept storage never becomes source or compiler authority. Deleting the directory only makes
`search_concept` unavailable until rebuilt.

### Chunk identity and embedding reuse

Use two identities:

- **Chunk location ID:** generation-local identity for path/span/symbol mapping.
- **Embedding content hash:** hash of model-compatible canonical content, independent of database
  row IDs and preferably independent of location-only metadata.

Each location also records the base index's algorithm-qualified **source hash** over original file
bytes (currently xxHash64). It validates that a stored path/span still names the same indexed file
version; returned excerpts come from the base index's canonical decoded text. The embedding content
hash answers the different question of whether a vector can be safely reused. A base hash-algorithm
change is a schema/reconciliation event rather than an unversioned comparison.

The content hash is over the exact versioned bytes sent to the document embedding provider. It lets
Phoenix reuse embeddings when unchanged canonical content moves, branches switch, or multiple
projects compile the same linked source. Embedding-space ID + canonical content hash form the cache
key. Location-only metadata can be excluded from model input, but nothing that reaches the model may
be excluded from the hash.

### Symbol-aware chunking

Proposed chunk types:

1. **Declaration overview:** type/module declaration, documentation, base information, and a
   compact member-name/signature list.
2. **Member body:** method/function/member declaration, documentation, attributes, signature,
   and body.
3. **Large-member segment:** syntax-boundary segment with a repeated declaration header and a
   small measured overlap.
4. **File/module fallback:** bounded structural segment when language parsing is unavailable;
   reported as lower-quality chunk provenance.

C# uses Roslyn syntax spans already captured by Phoenix. F# should use the F# syntax/FCS work when
available; a text fallback may provide early conceptual discovery but must report that symbol
mapping is unavailable and must not masquerade as FCS-backed structure.

The canonical embedding input should normally include:

- language;
- namespace/module and containing declarations;
- original and split identifier forms;
- symbol kind and signature;
- declaration attributes and leading documentation;
- bounded source body; and
- a normalized relative path only if evaluation proves it helps.

Project ownership, test/generated/vendor classification, and graph edges are primarily retrieval
metadata. Embedding all project names into linked source can distort meaning and duplicate one
physical chunk, so project metadata is not part of the default vector input.

### Chunk-size policy

The selected model defines a maximum token count. The chunker records the tokenizer/model pair and
uses three measured thresholds:

- target tokens for ordinary chunks;
- maximum tokens before syntax-aware splitting; and
- overlap tokens for large-member segments.

Do not choose these thresholds from line counts. Tokenization can vary greatly between prose,
C#, F#, and long identifiers.

Very small adjacent declarations may be packed into one overview chunk, while each member retains
its own exact path/span metadata where possible. Repeated headers are part of the content hash when
they are part of the model input.

### Embedding-provider boundary

Phoenix Core owns a small provider-neutral contract conceptually equivalent to:

```csharp
interface IEmbeddingProvider
{
    EmbeddingModelDescriptor Descriptor { get; }
    ValueTask<EmbeddingBatch> EmbedDocumentsAsync(...);
    ValueTask<EmbeddingVector> EmbedQueryAsync(...);
}
```

`EmbeddingModelDescriptor` includes an immutable `embeddingSpaceId`, model ID/revision, artifact
SHA-256, dimension, maximum tokens, tokenizer/version, document/query roles or prefixes, pooling
strategy, truncation/preprocessing rules, normalization, distance metric, numeric type, provider
kind, `dataBoundary: local|remote`, and license metadata. Mutable aliases such as `latest` are not
sufficient identity.

The initial delivery path is a local model loaded explicitly from a configured artifact, likely
through ONNX Runtime if the model bake-off supports a correct export. This is a delivery direction,
not a preselected model.

Provider rules:

- No model is downloaded silently.
- Model artifacts are hash-verified and license-disclosed.
- Document embedding is batched and bounded by memory/concurrency settings.
- Query and document vectors must have compatible descriptors.
- Provider failure is isolated from base indexing.
- Remote providers are outside MVP. If added later, they require explicit opt-in and in-band data
  handling disclosure.

The model bake-off must include at least one code-oriented and one strong general text/code model.
Selection criteria are retrieval quality on this repository, C#/F# behavior, CPU throughput,
memory, dimension/index size, context length, offline deployment, license, and stable ONNX/.NET
support. Popularity is not an acceptance criterion.

### Vector-index boundary and selection gate

Phoenix Core owns a backend-neutral contract with these semantics:

- create/open a versioned index;
- batch upsert and tombstone/delete by chunk ID;
- atomically commit a generation;
- open a pinned read snapshot;
- nearest-neighbor search with a hard candidate/time budget;
- expose `exact` versus `approximate` search mode and algorithm parameters;
- validate dimension/model/generation/checksum;
- support Windows and Linux deployment and Phoenix path-safety rules; and
- recover or fail isolated after interruption/corruption.

Implementation sequence:

1. Build a flat exact-scan reference backend. It is the quality oracle and may be production if
   it meets the real repository's latency/memory targets.
2. Benchmark production candidates against it. Candidates may include a SQLite vector extension,
   an HNSW sidecar such as USearch, or a purpose-built memory-mapped matrix; none is approved by
   this document.
3. If ANN is necessary, require measured ANN recall against the exact backend and record search
   parameters in each generation.

This gate matters because current SQLite vector extensions may be pre-1.0 or lack the exact
filter/update/snapshot behavior Phoenix needs, while a native HNSW dependency adds packaging,
crash, deletion, and follower-snapshot complexity. Vendor microbenchmarks do not answer those
questions.

Production-backend evaluation must cover:

- query p50/p95/p99 at the full target chunk count;
- build and incremental update throughput;
- float32-exact/production-exact representation loss and production-exact/ANN overlap Recall@K at
  every deployed result/candidate depth;
- filtered-query behavior across 2,000 projects;
- index bytes, resident memory, and cold-open time;
- deletion/tombstone and compaction behavior;
- writer/follower visibility and Windows file-sharing semantics;
- interrupted-write and corrupt-file recovery;
- self-contained/framework-dependent packaging; and
- license and supply-chain posture.

### Recommended publication shape

The backend may choose its physical representation, but the first production spike should evaluate
an immutable-segment design because it fits Phoenix's followers and crash model:

```text
catalog/manifest
  -> immutable base vector generation
  -> zero or more immutable add/replace overlays
  -> tombstone/replacement sets
```

A query searches the active base plus overlays, applies newest-wins/tombstones, and merges the
candidate lists. Compaction publishes a new base when measured overlay count, tombstone ratio, disk
use, or query fan-out crosses a threshold.

Crash-safe publication order is:

1. Build a private segment/generation.
2. Close, checksum, and flush it.
3. Atomically rename it into its immutable final path.
4. Commit the new manifest pointer.
5. Retire old generations only after pinned readers release them.

A crash before the manifest commit leaves an unreferenced artifact; startup cleanup may remove it.
A corrupt new generation never displaces the last valid one. A backend offering equivalent
transactional snapshot semantics does not need to reproduce these exact files.

The catalog is a small checksummed manifest containing the active space/generation ID and a bounded
list of retained generations; publication atomically replaces that manifest only after validating
every referenced immutable file. Catalog-read/lease-acquire and generation retirement share an
anchored cross-process lifecycle turnstile. A reader holds the shared side while it reads the active
pointer, opens all immutable files, and establishes its process/generation lease; GC must acquire the
exclusive writer-intent side before marking or removing a generation. There is therefore no gap in
which GC can retire files between pointer lookup and reader acquisition.

Readers keep the lease until their last query handle closes. A lease may be represented by OS shared
handles or by a PID/start-token record plus heartbeat. Heartbeat expiry alone only nominates cleanup:
GC must also verify process/start-token death and obtain the exclusive lifecycle guard. A paused but
live follower is never declared dead solely because its heartbeat is late.

GC never removes the active or building generation, a generation with a live lease, or the newest
previous valid generation. Expired staging and unreferenced artifacts are removed on startup.
Deletion uses Windows-compatible sharing and retry semantics; a sharing violation defers cleanup
rather than breaking readers. A configured concept-disk budget is checked before building or
compacting. Budget exhaustion preserves the active generation, reports an actionable failure, and
never deletes the only rollback generation merely to finish a rebuild.

### Capacity model

Raw float32 vector bytes are approximately:

\[
\text{chunks} \times \text{dimensions} \times 4
\]

For illustration, 500,000 chunks × 384 dimensions × 4 bytes is about 768 MB before metadata or
ANN overhead. Float16 or validated quantization can reduce space, but may change ranking quality.
The chosen numeric representation is therefore part of the evaluated model/index contract.

Phoenix must report observed chunk count, vector bytes, metadata bytes, and backend overhead after
a full target-repository build. The specification deliberately makes no "minutes at most" or
fixed index-size claim before that measurement.

## Build, refresh, and snapshot lifecycle

### Initial build

1. Base Phoenix performs its normal scan/index and becomes ready.
2. Under the existing rebuild-coordination reader guard, the writer snapshots eligible indexed
   source and creates deterministic chunk descriptors, then releases the guard.
3. The concept builder reuses content-hash cache hits and batches missing document embeddings.
4. It builds chunk FTS and vector data in private staging.
5. It validates counts, model descriptor, checksums, and the captured base-index identity.
6. It atomically publishes a complete concept generation.

The MCP handshake and all existing tools remain available throughout.

On the first build, `search_concept` returns `concept_index_building`. Mutable staging and partial
first-build results are never searchable: deterministic indexing order would bias the apparent
repository ranking and contradict committed-generation follower reads. If the base index advances
during a long build, the published generation is complete for its captured base epoch, reports
`behind`, and immediately enters reconciliation.

### Incremental refresh

Base `DeltaRefresher` events feed a separate bounded concept work queue only after the base
transaction commits:

- unchanged canonical content reuses its embedding;
- added/changed chunks are embedded and upserted;
- removed chunks are deleted or tombstoned;
- project/classification-only changes update metadata without re-embedding when vector input is
  unchanged; and
- queue overflow escalates to a detect-all concept reconciliation, not silent loss.

The queue is a latency optimization, not the durability record. On every writer startup, after any
base schema/index-generation replacement, and after a full rebuild or branch-switch detect-all
sweep, the writer deterministically compares the current complete chunk-descriptor set with the
active concept manifest. It reconciles every add/change/delete before declaring the concept
generation current. A lost process, queue item, or notification can therefore delay convergence but
cannot silently become permanent state.

Embedding is asynchronous, so each work item carries the captured source hash, chunk identity, and
canonical-input hash. Before committing returned embeddings to an overlay, the writer reacquires a
compatible base snapshot and verifies those identities. A changed result is discarded and requeued;
an embedding computed for old bytes is never labelled current for new bytes.

`refresh_index(force:"full")` and base schema migration use Phoenix's existing rebuild turnstile.
Descriptor capture and publish validation hold the shared reader guard; slow model inference does
not. A queued destructive rebuild can therefore drain readers without waiting for the whole model
batch, while stale batch results fail the hash recheck afterward. Concept refresh is eventually
consistent with the committed base index and reports its own status/counts without changing base
`pendingChanges` semantics.

### Model or chunker migration

A model, tokenizer, normalization, dimension, canonical-input, or chunker change creates a new
side-by-side generation. A logical provider registry maps immutable embedding-space IDs to
hash-verified artifacts. It pins query artifacts for the active, building, and retained rollback
generations; provider/artifact GC follows generation retention. Those packages may be
administrator-provisioned paths and need not be copied into the index.

Phoenix continues serving the last complete generation as `stale` during migration only while its
compatible query provider remains available, including after restart. If an administrator removes
that artifact, the old generation remains valid data but is not queryable and the tool reports
`concept_model_unavailable` until a compatible artifact or the replacement is available.

Vectors from different model fingerprints are never mixed in one search.

### Writer/follower behavior

- The Phoenix writer owns document embedding, vector mutation, compaction, rebuild, and publish.
- Followers query only committed concept generations.
- Every process performing a local concept query needs access to the compatible query-embedding
  model. Model weights should be file-backed so the OS can share immutable pages where possible;
  per-process runtime/session memory still requires measurement.
- Followers never infer the writer's in-process concept queue. They report committed generation
  identity and `pendingChangesKnown:false`, matching the existing follower honesty model.
- A rebuild publishes a new generation rather than invalidating readers holding the old one.
- Concept search requires a readable base snapshot for source validation and mapping. If the base
  follower becomes unavailable because its writer exits, concept search also returns
  `concept_base_unavailable`; it does not keep serving position-bearing results independently.
  Recovery and restart-only promotion follow Phoenix's existing writer/follower policy.

Multi-process configuration has one authority:

- The base writer's concept configuration alone selects the catalog, document model/space, build
  policy, and disk budget. Followers never select or mutate the active space.
- The default catalog locator is deterministic beside the base index. A custom locator and its
  opaque catalog/workspace identity are published through base-owned coordination metadata; an
  attaching follower validates both without exposing an absolute path in capabilities.
- `--concept-search local` means build/query for a writer and query-only for a follower. `off`
  disables the tool in that process; it does not delete shared data or stop a differently configured
  writer from maintaining it for other followers.
- A querying follower must attach to the writer-selected matching catalog and load a query provider
  whose immutable embedding-space descriptor matches the active generation. Its device/runtime may
  differ; its output contract may not. Path/catalog mismatch is `concept_catalog_mismatch`, while a
  model mismatch remains `concept_model_mismatch`.
- If a matching complete generation exists but the current base writer has concept mutation off,
  followers may query it using stale-generation/current-source validation. Capabilities report
  `refreshAuthority:"unconfigured"`, status/base relation, and a `concept_no_refresher` note. If no
  complete generation exists, the request fails as `concept_writer_unconfigured`; followers never
  start an orphan refresher.

### Snapshot consistency

A `search_concept` operation pins:

- one concept generation;
- the generation's model descriptor;
- one **current** base SQLite read epoch for source/path/project/symbol validation; and
- one `retrievalPolicyVersion` for fusion, boosts, penalties, dedupe, and response shaping.

Phoenix does not retain historical base SQLite epochs. A generation targeting an older base epoch
can still propose candidates, but before shaping results Phoenix oversamples and validates each
candidate's path plus source hash against the pinned current base epoch. Changed/deleted candidates
are dropped and counted. Snippets are read from the pinned base bytes, not an old embedding payload.
A symbol handle is returned only if its fingerprint remaps in that same epoch; otherwise the current
source/span may be returned with `mappingStatus:"unavailable"` and a null symbol.

The response reports both generation and query base-index versions plus `baseRelation`. This is a
truthful stale-generation search over current validated evidence—not a claim that Phoenix reopened
an old database snapshot. If validation/oversampling cannot fill the requested limit, Phoenix
returns fewer results with a stable note. It never advises following a changed stale span.

## Freshness and coverage contract

Concept status is independent from base `meta.indexStatus`:

```text
disabled | unavailable | writer_unconfigured | model_unavailable | building | ready | refreshing | stale | failed
```

`server_capabilities` gains a `conceptSearch` block containing:

- enabled/available;
- evaluated query languages, supported source languages, and chunk provenance per source language;
- advertised query modes and default mode for this build;
- provider kind and model fingerprint (no absolute model path);
- chunker/concept schema/vector backend;
- exact versus approximate vector search;
- current status/generation/base index identity, catalog relation, and refresh authority;
- eligible/indexed/failed source counts and eligible/indexed/pending/failed chunk counts;
- configured budgets; and
- stable failure reason when unavailable.

`repo_overview` adds a smaller operational summary. `server_capabilities.features[]` gains one
singular, grep-able `concept-search` feature ID when implemented.

Every response distinguishes:

1. **Base freshness:** whether file/project/symbol facts lag the worktree.
2. **Concept freshness:** whether chunk/vector data lags the committed base index.
3. **Index coverage:** eligible versus indexed/pending/failed chunks and source gaps.
4. **Retrieval bounds:** top-k/candidate budgets, exact versus ANN vector mode, filter budget, and
   response truncation.
5. **Relevance completeness:** never claimed.

No combination of ready status, full index coverage, exact vector scan, or high similarity changes
`meta.confidence` from `heuristic`.

Eligibility is deliberately inherited from the base index rather than a second workspace crawler:

| Source category | Concept behavior |
|---|---|
| Base-indexed handwritten C# | Roslyn syntax-structured chunks |
| Base-indexed C# marked generated by file/banner classification | Eligible only when the base retains it; classified and excluded by default through `includeGenerated:false` |
| Base-indexed source with no compile-item owner | Eligible, returned with `orphaned:true`, and demoted rather than hidden or labelled proven dead |
| Source under trees the base scan excludes (`bin`, `obj`, packages, configured generated/vendor trees, filesystem symlink/junction/reparse-point targets) | Not concept-eligible; disclosed through the base source-gap/eligibility contract, not silently rediscovered |
| Base-indexed vendored path | Eligible if present, marked `noise`, demoted or removed by `firstPartyOnly` |
| F# after the base index exposes `.fs` content/freshness and approved FCS/syntax integration exists | Structured F# chunks with advertised provenance |
| F# text fallback, if explicitly shipped after that same base-index prerequisite | Bounded fallback chunks, no symbol-handle claim, separately measured and advertised |
| `lang:"fs"` when no F# concept capability is available | Fatal `concept_source_language_unavailable`, never an unexplained zero-result success |

The exact base generated/excluded matrix must be tested against current scanner behavior before
shipping; concept search must not broaden source traversal under the guise of recall. Current base
Phoenix is C#-only, so every F# concept mode is contingent on the in-progress base-index work first
exposing `.fs` canonical content, hashes, freshness, and eligibility. The concept subsystem never
independently crawls F# files.

## Configuration and deployment

Proposed initial CLI surface:

```text
--concept-search off|local
--embedding-model <absolute-or-workspace-approved-model-path>
--embedding-device auto|cpu|cuda
--concept-index <path>            # optional; defaults under .codenav/concept
--concept-disk-budget-mib <int>
--rebuild-concept                 # writer only
```

Exact names may change during implementation, but these behaviors are required:

- First release defaults to `off` until a model is explicitly configured.
- Enabling concept search does not alter existing tool results.
- No network access or download occurs implicitly.
- A model can be pre-provisioned for offline/enterprise deployment.
- `server_capabilities` clearly distinguishes disabled, missing model, building, and ready.
- Self-contained Phoenix publishing does not silently bundle a large model; model packaging is an
  explicit artifact/installer decision.

If a remote provider is considered later, configuration must name it explicitly and capabilities
must state that chunk/query content leaves the machine. Remote indexing is not allowed to inherit a
generic network setting silently.

## Privacy and security

- The MVP embeds source locally. Only ordinary selected MCP result snippets can reach the attached
  agent/model provider, matching Phoenix's existing behavior.
- Concept telemetry records no query text, source, symbol names, project names, or paths.
- Model/tokenizer artifacts are treated as executable dependencies for supply-chain purposes:
  explicit source, pinned version/hash, license, and supported-runtime matrix.
- Model and vector files follow Phoenix's anchored path, no-follow, reparse-point, and destination
  validation rules.
- Canonical chunk text, hashes, vectors, lexical data, manifests, and caches are source-equivalent
  sensitive artifacts. Concept directories inherit workspace/base-index access controls, are never
  created with broader permissions, and are not suitable for a shared cache without a separate
  authorization and tenant-isolation design.
- Corrupt, dimension-mismatched, or untrusted concept files fail isolated and never become base
  index authority.
- Query length, candidate count, time, output bytes, embedding batches, and background concurrency
  are bounded.
- Source comments and strings retrieved by concept search remain untrusted repository content, not
  MCP/server instructions.
- Generated/vendor code can be indexed for recall but is classified and demoted/excludable so it
  cannot silently dominate results.

## Performance and resource budgets

The following are **provisional product goals**, not accepted SLOs. Phase 0 freezes or replaces them
only after recording the reference workstation, OS/runtime, model/backend, vector/chunk count,
numeric representation, warmup/cold-state rules, query mix, concurrency, sample count, and
measurement method on the full target repository:

- Warm `search_concept(limit:10)` p95 ≤ 750 ms and p99 ≤ 2 s, excluding MCP transport.
- Default hard query deadline: 5 s; deadline exhaustion is disclosed.
- One-file incremental concept refresh p95 ≤ 5 s after the base delta commits, excluding the
  existing watcher debounce.
- Base index readiness and existing query latency regress by no more than 5% with concept search
  enabled and idle, measured with repeated paired baselines and separated from run-to-run noise.
- Background build concurrency is configurable and yields to foreground Phoenix operations.
- A full cold concept build reports phase, processed/total chunks, cache hits, throughput, elapsed,
  and an ETA only after enough measured work exists; it makes no fabricated percentage/ETA.
- Response size remains within Phoenix's existing soft/hard UTF-8 budgets.

Cold-build wall time, model load time, resident memory, and disk size need measured deployment
budgets before production approval. They cannot be responsibly fixed before model, chunk count,
dimension, numeric type, and backend are known.

Latency reporting includes median and tail confidence intervals across repeated runs, separates cold
model load from warm queries, and includes filtered/unfiltered plus vector/hybrid cohorts. A target
becomes an acceptance gate only after this protocol and its workload are checked into the benchmark
artifacts before final model/backend tuning.

## Observability

Add a bounded privacy-safe `conceptOp` telemetry record with no query/source identity:

- operation/result/reason;
- model fingerprint and concept generation (bounded identifiers);
- query-model cold/warm and query-embedding cache hit;
- query embedding, vector retrieval, lexical retrieval, fusion/filter/dedupe, and shaping times;
- vector/lexical/fused/returned candidate counts;
- exact versus ANN backend and ANN parameters where applicable;
- index coverage/pending/failed counts;
- filter/candidate/deadline/response budget flags; and
- total elapsed time.

Add `conceptBuild` phase telemetry for chunking, cache lookup, embedding, lexical write, vector
write, validation, and publish. It follows the existing bounded channel/file/privacy guarantees;
telemetry failure never blocks a query or build.

## Quality evaluation

Performance alone does not decide whether concept search works. Before selecting a model or
backend, build a repository-specific relevance corpus reviewed by engineers who know the code. It
has two stratified parts:

- a **development/calibration set** used for model, chunk, threshold, and retrieval-policy tuning;
- a **held-out acceptance set** whose labels and results are not inspected during tuning.

Freeze the split before comparing finalists. At least two knowledgeable reviewers label each
acceptance query independently; disagreements are recorded and adjudicated rather than silently
collapsed. Report project area, C#/F#, production/test/orphaned/generated/vendor, and query-type
cohorts so a large easy cohort cannot hide a weak one.

Suggested minimum corpus:

- 40 natural-language feature/location questions whose identifiers are intentionally absent from
  the query;
- 20 exact-identifier/literal questions to catch hybrid regressions;
- 20 cross-language C#/F# concept questions once F# chunks are available;
- 10 similar-implementation questions; and
- 10 ambiguous, weak, or no-answer questions to evaluate low-relevance behavior.

For each answerable query, record one or more accepted files/symbols and optionally graded
relevance. For no-answer/ambiguous queries, record the expected abstention behavior instead of
inventing a relevance denominator.

For user-facing retrieval, `Recall@K` means the fraction of all labelled relevant targets present in
the first `K` results, averaged per answerable query; also report `Success@K`, the fraction of queries
with at least one accepted target. For the vector backend, **ANN overlap Recall@K** is a different
metric: the fraction of an exact vector search's top-`K` IDs returned by ANN for the same vectors,
filters, and candidate budget.

Measure:

- Recall@5 and Recall@10;
- Precision@5/10;
- Mean Reciprocal Rank (MRR) or nDCG for graded results;
- low-relevance false-positive/abstention behavior;
- result diversity by symbol/file/project;
- C# versus F# quality;
- vector-only, lexical-only, and hybrid results;
- float32 exact versus production-vector exact representation loss, and production-vector exact
  versus ANN overlap Recall@K;
- warm/cold latency and resource use; and
- agent-level tool calls, bytes/tokens read, time-to-correct-edit-surface, and final task success.

Minimum quality gates for the production MVP, evaluated once on the held-out set:

- Hybrid Recall@10 materially exceeds chunk-lexical-only Recall@10 on conceptual questions; the
  exact threshold is frozen from development results before held-out evaluation.
- Hybrid is non-inferior to current lexical/symbol routing on exact-identifier questions.
- If ANN is used, its gate covers every deployed result/candidate `K` (at least 5, 10, and the
  default candidate budget), both filtered and unfiltered. ANN is compared with an exact scan over
  the same production numeric vectors to isolate index loss. A separate float32 exact comparison
  measures quantization/representation loss. Thresholds are frozen in Phase 0 from an explicit
  retrieval error budget rather than inferred from one convenient `Recall@100` number.
- No advertised language/classification cohort is hidden inside an aggregate score; C# and F#
  report separately when both are advertised, and unavailable F# is marked not applicable rather
  than scored as an empty corpus.
- Every response and failure preserves the confidence/freshness/coverage contract.

Run an agent-level A/B with the same model and tasks:

```text
current Phoenix tools
vs.
current Phoenix tools + search_concept
vs.
hybrid discovery + compiler verification routing
```

Search metrics alone are not enough if agents ignore the tool, over-trust it, or spend more context
verifying noisy results. Because agent runs are nondeterministic, randomize condition order, repeat
each task enough times to report confidence intervals, and publish failures as well as aggregate
success. Do not tune prompts or routing on the held-out acceptance outcomes and then call the same
run a test.

## Failure modes and required behavior

| Failure | Required behavior |
|---|---|
| Model file missing/hash mismatch | Concept unavailable with stable reason; base Phoenix ready |
| Model load/OOM/provider exception | Fail current concept operation, release resources, preserve last committed generation |
| Query/document dimension mismatch | Refuse; never compare or coerce vectors |
| First build interrupted | Ignore private staging; restart/reconcile; no half-ready generation |
| Incremental queue overflow, crash, or restart | Escalate to/perform manifest-versus-current-descriptor reconciliation |
| Vector file corrupt/checksum invalid | Isolate concept cache and request rebuild; never affect base index |
| Chunk/parser failure | Record bounded failed/source-gap counts; continue other chunks |
| ANN/filter candidate starvation | Return fewer results with a `concept_filter_budget` note; do not claim complete scope |
| Query deadline | Return current-source-validated partial evidence with channel completion flags, or a fatal stable timeout error |
| Base refresh overlaps query | Query uses its pinned current base snapshot; a destructive rebuild follows the existing reader turnstile |
| Model/chunker config changes | Build side-by-side generation; never mix vector spaces |
| Active query-model artifact removed | Keep generation data but report model unavailable until a compatible artifact is restored/replaced |
| Writer exits | Base followers and therefore concept search become unavailable under existing Phoenix policy |
| Disk budget/GC sharing violation | Preserve active/rollback generations; defer GC or fail the new build without harming base Phoenix |
| F# parser/FCS unavailable | F# concept capability reports fallback/unavailable provenance explicitly |

## Phased implementation

### Phase 0 — evaluation and dependency spike

- Freeze the development/held-out corpus split and agent tasks.
- Implement deterministic chunk export without an MCP tool.
- Benchmark candidate local embedding models on C# and, once the base export exists, F#; measure
  quality, CPU, memory, and index size.
- Implement the flat exact-scan oracle.
- Spike vector backends against update/filter/snapshot/follower requirements.
- Decide the model, tokenizer, canonical input, dimension/numeric type, and production backend.

This phase gates all production dependency choices.

### Phase 1 — local vector discovery

- Add provider/vector abstractions and independently versioned concept storage.
- Build/publish a complete background concept generation.
- Add an internal/experimental `search_concept(mode:"vector")`, filters, budgets, status, coverage,
  and telemetry. If exposed in a development build, capabilities advertise only `vector` and make
  no `hybrid` default claim.
- Return path/spans and current symbol handles where mapping is proven.
- Keep the feature experimental and explicitly enabled.

### Phase 2 — hybrid retrieval

- Add chunk FTS/BM25.
- Add RRF, exact-identifier preference, classification ordering, and dedupe.
- Set `hybrid` as the preview default after development-set and agent A/B success; continue using
  only development/regression data while Phase 3 can still change the production artifact.
- Add stable no-result calibration and agent routing documentation.

This is the first phase that implements the public request/default contract in this document. It may
remain preview-only until Phase 3 proves durable large-repository operation.

### Phase 3 — live large-repository operation

- Add incremental chunk/embedding refresh and content-hash reuse.
- Complete writer/follower committed-generation behavior.
- Add model/chunker side-by-side migration and rebuild recovery.
- Run full 2,000-project/14M-line performance, soak, branch-switch, and corruption tests.
- Freeze the completed production candidate, then run the held-out retrieval and agent acceptance
  set once. A material implementation change after inspection requires a fresh untouched holdout.

The **production MVP** is Phases 0–3, not the Phase 1 vector prototype.

### Phase 4 — extensions after proof

- `find_similar`.
- Optional learned reranker if it wins quality per unit latency/resource.
- Optional explicitly configured remote provider.
- Optional team content-addressed embedding cache/index distribution.
- Documentation/config/non-source concept chunks if validated and separately classified.

## MVP acceptance criteria

- Phases 0–3 are complete; no vector-only prototype is labelled the production MVP.
- One `search_concept` MCP tool implements the request/response/confidence contract above.
- The feature is disabled or unavailable without affecting any existing Phoenix behavior.
- A complete concept generation can be built, validated, atomically published, reopened, and
  independently rebuilt.
- Changed/added/deleted chunks converge; changed/deleted source positions are never presented as
  current, and any older discovery generation is explicitly disclosed.
- Query and document model descriptors must match exactly.
- C# symbol-aware chunks are supported; F# capability states exactly which parser/compiler
  provenance is available at ship time, or explicitly reports unavailable.
- Hybrid retrieval beats the frozen lexical baseline on the held-out concept acceptance set.
- ANN, if used, meets the frozen recall gate against exact scan.
- Full-scale latency/resource measurements meet the accepted deployment budget and are published
  with hardware, model, chunk count, and configuration.
- All responses remain budget-capped and report concept/base freshness, index coverage, retrieval
  bounds, and `heuristic` confidence.
- No concept telemetry contains query text, source, symbol/project names, or paths.
- Corrupt/missing model or vector data fails isolated with actionable stable errors.
- `server_capabilities` exposes one singular `concept-search` feature ID and deployed model/backend
  identity.
- Agent instructions route vague concepts to `search_concept` and route every selected candidate
  into source/compiler verification before edits.

## Open questions to resolve in Phase 0

1. Which local embedding model best handles natural-language-to-code retrieval for this repository's
   C# and F# dialects?
2. What tokenizer/chunk target/maximum/overlap wins the development set without excessive index
   growth and then passes held-out acceptance?
3. Does an exact memory-mapped scan meet the latency budget at the real chunk count, or is ANN
   required?
4. Which vector backend satisfies filters, updates, atomic snapshots, Windows followers, recovery,
   packaging, and license requirements?
5. Should model artifacts be installed by a separate Phoenix command/package or only referenced by
   an administrator-provided path?
6. How much per-process memory does local query embedding add for many follower/agent processes?
7. Is syntax-structured F# chunking sufficient for first release, or is FCS-backed mapping a launch
   gate?
8. Which low-relevance policy allows useful abstention without hiding unusual but valid code?
9. Should docs/config chunks be added later, and how are they kept distinct from source results?
10. Is a global content-addressed embedding cache worth its cross-workspace privacy and lifecycle
    complexity?

## Alternatives deliberately rejected

- **Replace FTS with embeddings.** Exact identifiers, literals, errors, and regex are better served
  lexically; hybrid retrieval needs both.
- **Call embedding hits compiler-exact.** A compiler can verify a selected candidate afterward;
  similarity itself is never a program fact.
- **One embedding per file.** Files are too coarse, especially generated/legacy files with thousands
  of lines and multiple unrelated concepts.
- **One embedding per line.** Lines are too small to preserve meaning and produce an excessive,
  noisy index.
- **Add raw vector and BM25 scores.** Their scales are unrelated; RRF combines ranks honestly.
- **Use an LLM to rewrite every query in MVP.** It adds cost, nondeterminism, privacy surface, and a
  second failure mode before the basic retriever is measured.
- **Make concept build part of base readiness.** Optional discovery must not delay exact text,
  syntax, graph, or compiler navigation.
- **Choose a vector library from public microbenchmarks.** Phoenix's snapshot, filter, incremental,
  follower, deployment, and recovery requirements need a direct spike.
- **Return hundreds of chunks or deep concept-result pages.** The purpose is to find the next precise
  navigation anchor, not fill the model context with approximate matches.
- **Treat no results as absence.** Embedding and top-k recall never justify that conclusion.

## Background references

- [Cursor: Improving agent with semantic search](https://cursor.com/blog/semsearch) — an example
  of embedding retrieval used alongside grep; vendor evaluation, not a Phoenix comparison.
- [Cursor: Securely indexing large codebases](https://cursor.com/blog/secure-codebase-indexing) —
  chunk caching, Merkle-based change detection, and team index reuse.
- [Cursor: Fast regex search](https://cursor.com/blog/fast-regex-search) — why exact indexed text
  search remains complementary to concept/embedding retrieval.
- [ONNX Runtime C# documentation](https://onnxruntime.ai/docs/get-started/with-csharp.html) — one
  possible local inference runtime, subject to the model bake-off.
- [Microsoft.Extensions.AI `IEmbeddingGenerator`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.iembeddinggenerator) — a possible adapter surface; Phoenix should
  keep its core contract small and provider-neutral.
- [`sqlite-vec`](https://github.com/asg017/sqlite-vec) and
  [USearch](https://github.com/unum-cloud/usearch) — vector-backend research candidates, not
  approved dependencies.
