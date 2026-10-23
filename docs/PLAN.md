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
- [x] KB index + Ch.1 (architecture) + Ch.8 (cache) written from source
- [ ] Ch.2 (.NET) written from source
- [ ] KB chapters 3,4,5,9,10,11,12,13 for *existing* tech written from source
- [ ] Claude Artifact index published
**Acceptance** Every claim in KB cites a real file; every capability tagged with a maturity status; planned features never described as implemented.
**Tests** Link/reference check script (Phase 9 automation); manual review.
**Risks** Documentation drift → mitigated by "Status" lines per chapter and the Phase-9 reference check.

## Phase 2 — Query cache correctness and index versioning — ✅
**Goal** Cached results can never outlive the index or configuration that produced them.
**Technical design**
- `CollectionMetadata` gains `IndexVersion` (string token; empty = legacy collection, treated as `"0"` → backward compatible).
- `IndexDocumentService` writes a new token after every successful upsert (and after delete) via `SetCollectionMetadataAsync`.
- New `QueryCacheKeyBuilder` (pure, unit-testable): key = hash of length-prefixed fields: schema version, collection, **index version**, embedding provider/model/dim, retrieval strategy (`"dense"` for now), topK, sorted filters (escaped), query. Later phases add fields through one `RetrievalFingerprint` record, not by string concatenation.
- Query path reads metadata (already does) → builds key from `metadata.IndexVersion`.
**Tasks**
- [x] Failing regression tests that prove today's bug (verified red on pre-fix code, commit `test: regression tests…`): (a) reindex → same query returns stale cached results; (b) different embedding model config → same key; (c) filter value containing `|`/`=` collides
- [x] `QueryCacheKeyBuilder` + unit tests (determinism, field sensitivity, no collisions, filter order independence)
- [x] `IndexVersion` on `CollectionMetadata`; Qdrant + Pg persistence (legacy → empty string, treated as a stable version; Pg `ALTER TABLE … IF NOT EXISTS` verified against real Postgres)
- [x] `IndexDocumentService` bumps version (GUID token); tests for bump-on-index and distinct versions
- [x] `VectorSearchService` uses builder; cache-hit increments `QueriesExecuted` (tag `cache_hit=true`)
- [x] Qdrant: metadata get/set and delete inside resilience pipeline; per-query `EnsureCollection` removed (A3). Pg still runs `EnsureTablesAsync` DDL on every call — **not fixed, open risk**
- [x] Testcontainers tests against real Qdrant and real pgvector for metadata round-trip, unknown collection, fresh instance, legacy table upgrade (5 tests, passing). A full reindex→query integration test through the API is **not** written (the main `IntegrationTestWebAppFactory` cannot start here: `minio/minio` is not pullable from Docker Hub and Quay requires auth); the reindex behaviour is covered at unit level with a real `IndexDocumentService` + `VectorSearchService`
**Acceptance** ✅ The three regression tests failed on `main` and pass after. 71 of the original 72 tests unchanged and passing; 1 test that asserted the old "never rewrite metadata" behaviour was deliberately rewritten (`…KeepsEmbeddingConfigAndPublishesNewIndexVersion`). Legacy metadata without version works (Pg verified). Result: 92 unit tests + 5 new integration tests pass.
**Perf** One string hash per query (negligible); removes one RPC per query (A3).
**Risks** Version bump races between concurrent indexers (benign: any change yields a different token; documented in KB Ch.8). Old cache entries become unreachable and expire via TTL (no flush needed).
**KB** Ch.8 (Redis/caching) gains the full design, the failure scenario, and interview section.

## Phase 3 — Retrieval evaluation framework, golden dataset, dense baseline — 🟦
**Goal** Measure retrieval quality deterministically before optimizing.
**Design** New project `src/Rag.Evaluation` (library) + `tests/Rag.Evaluation.Tests`. Pure metric functions over `IReadOnlyList<string> ranked` and `IReadOnlyDictionary<string,int> judgments` (graded relevance). Document-level and chunk-level evaluation are separate entry points sharing the metric code. Dataset: JSON (`datasets/golden-v1/`) with `datasetVersion`, queries, graded judgments, optional category.
**Metric definitions fixed up-front** (documented in KB Ch.7): Hit@K, Precision@K (denominator K), Recall@K (denominator = #relevant; query with zero relevant is excluded and reported, not scored 0), MRR@K, nDCG@K (gain `2^rel − 1`, log2 discount, ideal DCG from all judged docs, truncated at K; 0 when IDCG = 0 is excluded), MAP@K. Duplicate IDs in a ranking are counted once at first rank.
**Tasks**
- [x] Metric tests first with hand-computed expected values (28 tests; worked example reused in KB Ch.7)
- [x] Metrics implementation (`Rag.Evaluation/Metrics/RankingMetrics.cs`)
- [x] Dataset schema + loader + validator (unknown IDs, duplicate IDs, bad grades, evidence that is not in the document)
- [x] `RetrievalEvaluator` over a `RetrieveAsync` delegate; document + chunk level; per-query/aggregate/per-category; latency percentiles
- [x] Golden corpus + dataset `golden-v1.0.0`: 35 documents, 44 queries (14 keyword, 16 paraphrase, 7 multi-relevant, 3 conceptual, 4 no-answer); author-judged, single annotator
- [x] Synthetic baseline (deterministic hashing embedder, real chunker/services) generated by `dotnet run --project src/Rag.Evaluation.Cli` → `docs/eval/baseline-dense.{md,json}`
- [ ] **Real-embedder baseline (OpenAI/Azure) — NOT done.** Needs an API key and a decision on which model; until then no quality claim about the real pipeline exists
**Acceptance** ✅ for the offline harness (metric tests pass, baseline generated by a command, determinism tested). ⬜ phase stays 🟦 until the real-embedder baseline exists.
**Risks** Fake-embedder baselines measure the harness, not semantic quality — labelled as such; real-embedder numbers only when run.

## Phase 4 — Hybrid retrieval and RRF — 🟦
**Goal** Dense + sparse retrieval with fusion, provider-capability aware.
**Design (as built)** `ILexicalSearchStore` is an optional capability (`IVectorStore` unchanged); `RetrievalPlan.Resolve` picks the effective strategy and falls back hybrid→dense with a warning; `HybridRetriever` runs both legs concurrently with identical filters and degrades per leg; `ReciprocalRankFusion` (k=60, weights, stable ordering, source attribution). Qdrant lexical leg = native sparse vector with `Modifier.Idf` + client-side BM25 tf weights; **fusion stays client-side** (decision and comparison in KB Ch.6 §6.6). Pg has no lexical leg → capability fallback.
**Tasks**
- [x] RRF tests (ranking correctness, ties, duplicates, weights, k) → implementation — 14 tests, worked example hand-computed
- [x] Evaluate Qdrant native hybrid before building custom infra — client API verified; decision recorded (KB Ch.6 §6.6)
- [x] Capability model + retriever abstraction + strategy selection via `Retrieval:*` options; cache fingerprint includes strategy id and RRF parameters
- [x] Filter allow-list validation shared by all strategies (assessment A5)
- [x] Qdrant sparse-vector lexical search, verified against a real Qdrant container (7 integration tests)
- [x] Offline BM25 leg + `--strategy` CLI; dense/lexical/hybrid comparison recorded in `docs/eval/phase4-hybrid-comparison.md`
- [ ] **PgVector lexical leg (`tsvector`)** — not built; hybrid on Pg falls back to dense (tested at service level with a dense-only fake, not against Postgres)
- [ ] **Hybrid enabled by default — NOT done, deliberately.** Needs a real-embedder comparison on golden-v1 that beats run-to-run noise
- [ ] Capability cache per collection (a legacy Qdrant collection costs one failing lexical RPC per hybrid query)
**Findings that changed the design**
- Qdrant (version used in tests) cannot add a sparse vector to an existing collection → legacy collections stay dense-only; enabling lexical/hybrid needs a new collection + reindex. Indexing into legacy collections keeps working.
- Synthetic-embedder result: hybrid ≈ lexical > dense; differences between hybrid and lexical are within noise (40 scored queries). No improvement is claimed.
**Acceptance** ✅ code paths validated by 166 unit + 12 integration tests. ⬜ phase stays 🟦 until the real-embedder comparison and the Pg leg exist.
**Risks** Provider asymmetry; `AssumedAverageLength=200` makes BM25 length normalisation approximate; hash/tokenizer change silently invalidates lexical indexes (hash pinned by a test).

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
| 2026-10-24 | 2 | Stale-cache bug reproduced (3 red tests), fixed with `IndexVersion` + `QueryCacheKeyBuilder`; 92 unit + 5 integration tests green |
| 2026-10-24 | 3 | Metrics, dataset, evaluator, offline harness, CLI, synthetic baseline; 45 evaluation tests green |
| 2026-10-24 | 3 | **Bug found by the harness:** `DataIngestionTokenChunker` crashed on model names unknown to Tiktoken (`NotSupportedException` not caught). Red test first, then fixed |
| 2026-10-24 | 4 | RRF, retriever layer, strategy/capability model, filter allow-list, Qdrant sparse lexical leg (real container), offline BM25 + comparison; 166 unit + 12 integration tests |
| 2026-10-24 | 4 | **Discovery:** Qdrant cannot add a sparse vector to an existing collection; first design (in-place upgrade) was disproved by a failing integration test and replaced by "legacy collections stay dense-only" |
| 2026-10-24 | coord | The in-house resilience layer (replacing Polly) landed in commits `066182a`/`f128474`; full suite green afterwards |
| 2026-10-24 | env | Pre-existing: `IntegrationTestWebAppFactory` fails to start here (`minio/minio` pull denied) → 6 of the older integration tests cannot run in this environment; unrelated to Phase 2 |

## Open coordination note
Uncommitted changes by someone else appeared in the working tree during Phase 3 (a custom `Rag.Core/Resilience/*` layer replacing Polly, edits to `RagPipelines.cs`, both workers, `Rag.Core.csproj`, `Rag.Core.Tests.csproj`, and 12 `ResiliencePipelineTests`, 11 of which fail because `CircuitBreaker.ShouldBreak` is unimplemented). These were not touched or committed by the retrieval-engineering work. Commits from this effort stage explicit paths only. Resilience-related KB content (Ch.10) is deliberately deferred until that work lands, so it does not describe a layer that is about to change.
