# RagService Retrieval Engineering — Living Implementation Plan

Companion docs: [ASSESSMENT.md](ASSESSMENT.md) · [Knowledge Base](kb/README.md)

**Rules of this document**
- A checkbox is ticked only after the task's validation command was run and passed. Code existing ≠ done.
- Each phase is a separate commit series; the KB chapter named in the phase is updated in the same phase.
- No benchmark number appears anywhere until it was produced by the evaluation harness (Phase 3) and recorded in `docs/eval/`.
- Status legend: ⬜ not started · 🟦 in progress · ✅ validated.

**Global constraints**: .NET 10; keep `IVectorStore`/`IChunker`/`IEmbeddingProvider` source-compatible; every new async path takes a `CancellationToken`; fast tests (`dotnet test tests/Rag.Core.Tests`) must need no network/Docker; slow tests are tagged `[Trait("Category","Integration")]` or live in `Rag.IntegrationTests`.

**Decisions taken (my calls, from the assessment)**
- Evaluation corpus: a **small, hand-authored, in-repo golden corpus** (deterministic, offline-runnable) plus an optional Wikipedia-seeder adapter. The `plan.md` layout/SQL definitions are not in the repo yet, so they are not used.
- Embeddings in fast tests use a **deterministic fake embedder**; real-model numbers are only produced by the opt-in slow run.
- New retrieval abstractions are **additive** (new interfaces); `IVectorStore` is not widened.

---

## Phase 1 — Assessment, plan, Knowledge Base foundation — 🟦
**Goal** Establish truthful documentation of the current system before changing it.
**Design** `docs/ASSESSMENT.md`, this plan, `docs/kb/` (Markdown source of truth; Turkish prose, English terms), per-chapter maturity tags.
**Tasks**
- [x] Read all of `Rag.Core`, both workers, API, compose, infra, tests
- [x] Baseline build + unit tests (0 errors, 72 pass)
- [x] ASSESSMENT.md
- [x] PLAN.md
- [ ] KB index + Ch.1 (architecture), Ch.2 (.NET), Ch.8 (cache) written from source
- [ ] KB chapters 3,4,5,9,10,11,12,13 for *existing* tech written from source
- [ ] Claude Artifact index published
**Acceptance** Every claim in KB cites a real file; every capability tagged with a maturity status; planned features never described as implemented.
**Tests** Link/reference check script (Phase 9 automation); manual review.
**Risks** Documentation drift → mitigated by "Status" lines per chapter and the Phase-9 reference check.

## Phase 2 — Query cache correctness and index versioning — ⬜
**Goal** Cached results can never outlive the index or configuration that produced them.
**Technical design**
- `CollectionMetadata` gains `IndexVersion` (string token; empty = legacy collection, treated as `"0"` → backward compatible).
- `IndexDocumentService` writes a new token after every successful upsert (and after delete) via `SetCollectionMetadataAsync`.
- New `QueryCacheKeyBuilder` (pure, unit-testable): key = hash of length-prefixed fields: schema version, collection, **index version**, embedding provider/model/dim, retrieval strategy (`"dense"` for now), topK, sorted filters (escaped), query. Later phases add fields through one `RetrievalFingerprint` record, not by string concatenation.
- Query path reads metadata (already does) → builds key from `metadata.IndexVersion`.
**Tasks**
- [ ] Failing regression tests that prove today's bug: (a) reindex → same query returns stale cached results; (b) different embedding model config → same key; (c) filter value containing `|`/`=` collides
- [ ] `QueryCacheKeyBuilder` + unit tests (determinism, field sensitivity, no collisions, filter order independence)
- [ ] `IndexVersion` on `CollectionMetadata`; Qdrant + Pg persistence (nullable column/payload, legacy → `"0"`)
- [ ] `IndexDocumentService` bumps version; tests for bump-on-index
- [ ] `VectorSearchService` uses builder; cache-hit increments `QueriesExecuted`
- [ ] Qdrant: metadata ops moved inside resilience pipeline; avoid per-query `EnsureCollection` on metadata collection (assessment A3)
- [ ] Integration test (Testcontainers) of reindex → fresh results, if Docker available
**Acceptance** The three regression tests fail on `main` and pass after; 72 existing tests still pass; legacy metadata without version still works.
**Perf** One string hash per query (negligible); removes one RPC per query (A3).
**Risks** Version bump races between concurrent indexers (benign: any change yields a different token; documented in KB Ch.8). Old cache entries become unreachable and expire via TTL (no flush needed).
**KB** Ch.8 (Redis/caching) gains the full design, the failure scenario, and interview section.

## Phase 3 — Retrieval evaluation framework, golden dataset, dense baseline — ⬜
**Goal** Measure retrieval quality deterministically before optimizing.
**Design** New project `src/Rag.Evaluation` (library) + `tests/Rag.Evaluation.Tests`. Pure metric functions over `IReadOnlyList<string> ranked` and `IReadOnlyDictionary<string,int> judgments` (graded relevance). Document-level and chunk-level evaluation are separate entry points sharing the metric code. Dataset: JSON (`datasets/golden-v1/`) with `datasetVersion`, queries, graded judgments, optional category.
**Metric definitions fixed up-front** (documented in KB Ch.7): Hit@K, Precision@K (denominator K), Recall@K (denominator = #relevant; query with zero relevant is excluded and reported, not scored 0), MRR@K, nDCG@K (gain `2^rel − 1`, log2 discount, ideal DCG from all judged docs, truncated at K; 0 when IDCG = 0 is excluded), MAP@K. Duplicate IDs in a ranking are counted once at first rank.
**Tasks**
- [ ] Metric tests first with hand-computed expected values (worked examples reused in KB)
- [ ] Metrics implementation
- [ ] Dataset schema + loader + validator (unknown IDs, empty judgments)
- [ ] `RetrievalEvaluator` running any `IRetriever`-shaped delegate; per-query + aggregate + per-category report; latency percentiles
- [ ] Golden corpus + dataset v1 (≥ 40 queries incl. keyword-exact, paraphrase, multi-relevant, no-answer) — authored in repo
- [ ] Baseline run for current dense pipeline with a deterministic fake embedder (CI-safe) and an opt-in real-embedder run; results saved to `docs/eval/baseline-dense.md` with the exact config
**Acceptance** Metric tests pass; baseline report is generated by a command, not typed by hand.
**Risks** Fake-embedder baselines measure the harness, not semantic quality — labelled as such; real-embedder numbers only when run.

## Phase 4 — Hybrid retrieval and RRF — ⬜
**Goal** Dense + sparse retrieval with fusion, provider-capability aware.
**Design** Evaluate Qdrant native sparse vectors + Query API prefetch/fusion first (record the decision in an ADR). New `IRetriever` (strategy), `RetrievalCapabilities` flags per store (`DenseVectors`, `SparseVectors`, `NativeFusion`, `FullTextSearch`), `IResultFusion` with `ReciprocalRankFusion` (k default 60, configurable, weights optional, stable tie-break by best rank then chunk id, identity = `ChunkId`, source attribution list per hit). Fallback matrix: store lacks sparse → dense with a logged, tagged degradation. Filter allow-list validation shared by all strategies (assessment A5).
**Tasks**
- [ ] RRF unit tests (ranking correctness, ties, duplicates, weights, k sensitivity) → implementation
- [ ] Sparse encoder (BM25-style term weighting at index time) + Qdrant sparse index; Pg `tsvector` path
- [ ] Retrieval strategy selection via options; cache fingerprint includes strategy+k
- [ ] Evaluate hybrid vs. baseline on golden-v1 — enable by default **only if** it passes the quality gate
**Risks** Index-time schema change needs reindex (index version bump from Phase 2 covers cache). Provider asymmetry.

## Phase 5 — Reranking — ⬜
`IReranker` (none / API / local cross-encoder behind the same interface), candidate over-fetch (`CandidateK` → `TopK`), timeout + fallback to un-reranked order, cost/latency metrics. Adopted only with measured nDCG/MRR gain vs. added p95 latency.

## Phase 6 — Query rewriting and expansion — ⬜
`IQueryTransformer` (original / LLM rewrite / expansion / multi-query), intent-preservation guard (reject rewrites that drop or add named entities/numbers; fallback to original), skip heuristic to avoid LLM calls, original+transformed recorded on the span. HyDE only if evaluation justifies it.

## Phase 7 — Chunking experiments — ⬜
Parameter sweep (size/overlap), recursive/structure-aware candidates, parent-child retrieval — each judged by the Phase 3 harness at chunk and document level. Only winners are kept.

## Phase 8 — Regression testing and CI quality gates — ⬜
Run record (strategy, embedder, chunking config, index version, dataset version, reranker, transformer, metrics, latency, cost); baseline-vs-candidate comparison with explicit, documented thresholds (e.g. absolute nDCG@10 drop > 0.02 or relative p95 latency increase > 25% fails — thresholds to be justified from observed run-to-run variance, not guessed); GitHub Actions: fast job on every PR, slow job opt-in.

## Phase 9 — Final review — ⬜
Perf pass, README/KB/plan consistency, doc reference checker, diagram accuracy check, final test summary, remaining risks.

---

## Progress log
| Date | Phase | Event |
|---|---|---|
| 2026-10-24 | 1 | Baseline verified: build OK, 72/72 unit tests; ASSESSMENT + PLAN written |
