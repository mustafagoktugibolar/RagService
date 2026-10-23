# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

.NET 10 RAG backend (`RagService.slnx`): document ingestion → chunk → embed → vector store, and semantic/hybrid search. Workers are driven by RabbitMQ (`rag.index`, `rag.query`, with `rag.dlx` dead-letter routing). `README.md` has the sequence diagrams and service/port tables; `docs/kb/` (Turkish) is a technical textbook whose chapters carry maturity labels (Implemented / Planned / Experimental) — never describe a planned feature as implemented. `docs/PLAN.md` is the phased roadmap; the root `plan.md` is an unrelated, unimplemented "DefinitionSeeder" design note.

## Commands

```bash
docker compose up -d                                   # infra + workers + api (api on :8080, Scalar at /scalar/v1)
docker compose -f compose.infra.yaml up -d             # infra only, for running projects from the IDE/CLI
docker compose --profile seed run --rm rag.wikipedia-seeder   # seed Wikipedia passages

dotnet build RagService.slnx
dotnet test tests/Rag.Core.Tests                       # fast unit tests, no infra
dotnet test tests/Rag.Evaluation.Tests
dotnet test tests/Rag.Reranking.Onnx.Tests
dotnet test tests/Rag.IntegrationTests                 # needs Docker: Testcontainers start Redis, RabbitMQ, SeaweedFS, Qdrant
dotnet test tests/Rag.Core.Tests --filter "FullyQualifiedName~HybridRetrieverTests"   # single class/test

bash scripts/download-reranker-model.sh                # fetch pinned ONNX cross-encoder into models/ (git-ignored setup step)
```

Configuration is env-var based (`.env.template` → `.env`, double-underscore section separators, e.g. `RAG__VECTORSTOREPROVIDER`, `QDRANT__HOST`). `.env` is git-ignored and holds `OPENAI__APIKEY`.

### Evaluation CLI

```bash
dotnet run --project src/Rag.Evaluation.Cli -- --label baseline-dense --strategy dense|lexical|hybrid \
    [--embedder hashing|openai] [--candidates 50] [--rrf-k 60] [--mode run|tune|bench-reranker|bench-memory]
```

Args are parsed as strict `--key value` pairs (no bare flags). Defaults: `hashing` embedder (offline, deterministic), dataset `datasets/golden-v1/dataset.json`, output `docs/eval/<label>.{md,json}`. `--embedder openai` reads `OPENAI__APIKEY` from `.env` and caches embeddings in `.eval-cache/`. Results written to `docs/eval/` are committed artifacts.

## Architecture

**Dependency direction:** `Rag.Core` is the only shared library (abstractions, services, providers, options, resilience, telemetry). The hosts — `Rag.Api` (dev/test minimal API), `Rag.IndexWorker`, `Rag.QueryWorker`, `Rag.WikipediaSeeder` — each call `AddRagCore(configuration)` (`Rag.Core/Extensions/ServiceCollectionExtensions.cs`) and contain little logic. Real behavior lives in `Rag.Core/Services/IndexDocumentService` and `VectorSearchService`.

**Provider selection by config:** `Rag:EmbeddingProvider` (OpenAI | AzureOpenAI), `Rag:VectorStoreProvider` (Qdrant | PgVector), `Rag:StorageProvider` (S3 | Local; "Minio"/"SeaweedFS" are aliases for S3 — one `S3BlobStorage` serves every S3-compatible store). Adding a provider means implementing the `Rag.Core/Abstractions` interface and adding a case in the matching `Add*` extension. Embeddings are wrapped in `CachingEmbeddingProvider` (Redis, toggled by `EmbeddingCache:Enabled`).

**Retrieval pipeline (the non-obvious part):**
- `RetrievalOptions.Strategy` (Dense | Lexical | Hybrid) is resolved in `RetrievalPlan.Resolve` against capabilities inferred from whether the store implements the optional `ILexicalSearchStore` interface (`StoreCapabilities.Of`). Hybrid silently falls back to dense (with a warning) when it doesn't; only `QdrantVectorStore` implements it (sparse vectors via `SparseTextEncoder`), and Qdrant collections created before sparse vectors existed throw `LexicalSearchUnavailableException` and need re-indexing.
- Hybrid fuses dense + lexical legs with weighted reciprocal-rank fusion (`ResultFusion.cs`, `Retrievers.cs`).
- `RetrievalPlan.StrategyId` encodes effective strategy + fusion parameters and is part of the query-cache key (`QueryCacheKeyBuilder`), so changing strategy or tuning never serves stale cached results.
- Reranking (`Rag.Core/Reranking`: `IReranker`, `RerankingRetriever`) is optional, off by default (`Rerank:Enabled`), with a timeout that falls back to the un-reranked order. The local ONNX cross-encoder lives in `Rag.Reranking.Onnx` and is **not yet wired into any host**.
- `FilterValidator` constrains metadata filter keys/sizes because filters reach the store as payload paths / column lookups.

**Resilience:** in-house (no Polly) retry → circuit breaker → per-attempt timeout pipelines in `Rag.Core/Resilience` (`ExternalApi`, `Qdrant`, `Storage`). Workers ack on success, `Nack(requeue: true)` on `BrokenCircuitException`, and `Nack(requeue: false)` (→ DLQ) after retries are exhausted.

**Evaluation is a first-class part of the repo.** `Rag.Evaluation` holds the metrics (Recall/Precision/MRR/nDCG/AP), an in-memory harness (`DenseBaselineHarness`, BM25 + cosine store, file-cached embedder) that exercises the same chunker/retrievers/fusion as production, and the tuning code (`HybridGrid`, `PairedBootstrap`). Retrieval changes are expected to be justified by measurement: `docs/eval/protocol-hybrid-tuning.md` is a pre-registered protocol (tune/heldout split in `golden-v1.1.0`, paired-bootstrap adoption rule), and the recorded outcome is that hybrid did **not** beat dense on held-out queries, which is why `Strategy` defaults to Dense. If you change the protocol after seeing results, label the run exploratory and record why.

**Tests:** `Rag.Core.Tests` uses in-memory fakes (`Fakes/InMemoryVectorStore`, `LexicalInMemoryVectorStore`) rather than mocks; `Rag.IntegrationTests` shares one `IntegrationTestWebAppFactory` (Testcontainers + WireMock for OpenAI) via a collection fixture, and `Rag.Api` exposes `public partial class Program` for `WebApplicationFactory`.
