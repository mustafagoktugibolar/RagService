# RagService — Repository Architecture Assessment

> Snapshot: 2026-10-24, commit `abd338e`. Every claim below was verified by reading the source file cited, not the README.
> Baseline: `dotnet build RagService.slnx` → 0 errors; `dotnet test tests/Rag.Core.Tests` → 72/72 pass.
> Maturity vocabulary used throughout: **Implemented** · **Partial** · **Planned** · **Absent**.

## 1. What the system is

A .NET 10 retrieval backend. Documents are uploaded to blob storage, an `IndexWorker` consumes `rag.index`
messages (extract → chunk → embed → upsert), a `QueryWorker` consumes `rag.query` messages (embed query →
vector search → reply to `reply-to` queue). `Rag.Api` exposes the same two services over HTTP for dev/test.
Everything that matters lives in `Rag.Core`; the other four projects are thin hosts.

```
Rag.Api ─┐
QueryWorker ─┼─► Rag.Core: VectorSearchService ─► IEmbeddingProvider (Caching → OpenAI|AzureOpenAI)
IndexWorker ─┘                                  └► IVectorStore (Qdrant | PgVector)
                IndexDocumentService ─► ITextExtractor → IChunker → IEmbeddingProvider → IVectorStore
```

## 2. Capability matrix

| Capability | Status | Evidence |
|---|---|---|
| Dense retrieval (cosine) | Implemented | `QdrantVectorStore.SearchAsync`, `PgVectorStore.SearchAsync` (`<=>`) |
| Metadata filtering (exact match, AND) | Implemented | Qdrant `Filter.Must` on `metadata.{key}`; Pg jsonb |
| Token chunking (size 1000 / overlap 200, cl100k) | Implemented | `DataIngestionTokenChunker` |
| Embedding cache (Redis, per text hash) | Implemented | `CachingEmbeddingProvider` |
| Query-result cache (Redis, TTL 5 min, **off by default**) | Implemented, **incorrect keying** | `VectorSearchService.BuildQueryCacheKey` |
| Collection compatibility guard (provider/model/dim) | Implemented | `IsCompatible` in both services |
| RabbitMQ workers, manual ack, DLQ via `rag.dlx`, prefetch+semaphore | Implemented | `*/Worker.cs` |
| Resilience (in-house; Polly removed): retry+jitter, breaker, timeout (OpenAI, Qdrant, object storage) | Implemented, partially applied | `RagPipelines.cs` |
| OpenTelemetry traces+metrics, Prometheus/Tempo/Grafana | Implemented | `RagTelemetry`, `OpenTelemetryExtensions`, `infra/` |
| Health checks (live/ready) | Implemented (Api only) | `Rag.Api/Program.cs:90-94` |
| Unit tests (72) / Testcontainers integration tests | Implemented | `tests/` |
| Sparse / BM25 / hybrid retrieval | **Absent** | `IVectorStore.SearchAsync` takes only `float[]` |
| Fusion, reranking, query rewriting | **Absent** | — |
| Retrieval evaluation, golden datasets, metrics | **Absent** | — |
| Index version / cache invalidation on reindex | **Absent** | `CollectionMetadata` has 4 fields, no version |
| AuthN/AuthZ on API or per-tenant retrieval filter | **Absent** | `Rag.Api/Program.cs` maps endpoints without auth |
| CI pipeline | **Absent** | no `.github/` |
| `Rag.DefinitionSeeder` (`plan.md`) | Planned (not in solution) | `plan.md`; no project exists |

## 3. Strengths

1. **Clean seams already exist**: `IVectorStore`, `IEmbeddingProvider`, `IChunker`, `ITextExtractor`, `IBlobStorage` are small, DI-registered, and mockable. Retrieval stages can be added without touching hosts.
2. **Compatibility guard** stops querying a collection with a different embedding model — the hardest silent-failure class in RAG.
3. **Chunk identity is deterministic** (`{documentId}:{index:D6}`); Qdrant point id = MD5(`collection:chunkId`), so reindexing is idempotent. Index flow deletes by `documentId` before upsert, so shrinking documents leave no orphan chunks.
4. **Worker semantics are right**: manual ack; `BrokenCircuitException` → requeue; other failure → nack without requeue → DLQ. Bounded concurrency via `SemaphoreSlim`.
5. **Observability is real**, not aspirational: spans per `rag.query`/`rag.index`, counters, an embedding duration histogram, and an infra stack provisioned in compose.

## 4. Weaknesses and risks

| # | Finding | Severity | Where |
|---|---|---|---|
| A1 | ✅ *Fixed in Phase 2.* **Stale query cache.** Key = `collection|topK|filters|query`. Excludes index version, embedding provider/model/dim, strategy. Reindex or document deletion keeps serving old results for up to `TtlMinutes`. Filter values joined with `|`/`=` without escaping → key collisions possible (`a=b|c=d` vs a single value containing `|`). | High (correctness) | `VectorSearchService.cs:119` |
| A2 | ✅ *Fixed in Phase 2.* A query-cache hit returns early and never increments `QueriesExecuted`, so the counter undercounts served queries once the cache is enabled. | Low | `VectorSearchService.cs:76-81` |
| A3 | ✅ *Fixed in Phase 2 for Qdrant (Pg still issues DDL per call).* `GetCollectionMetadataAsync` in Qdrant calls `EnsureCollectionInternalAsync` on the metadata collection **on every query** (an extra `CollectionExists` RPC) and is **outside** the `RagPipelines.Qdrant` pipeline (also `Upsert` metadata, `Delete`). Resilience is inconsistent. | Medium (latency/reliability) | `QdrantVectorStore.cs:107-150` |
| A4 | `IVectorStore.SearchAsync` is dense-only and has no capability surface. Hybrid cannot be added without a contract change. | Design gap | `IVectorStore.cs` |
| A5 | Filter keys are interpolated into the Qdrant field path (`metadata.{key}`) with no allow-list. Not SQL injection, but a caller controls which payload paths are probed. Pg path should be reviewed for the same. | Medium (security) | `QdrantVectorStore.cs:75` |
| A6 | No auth on `Rag.Api`; queries over RabbitMQ carry arbitrary `filters` so tenant isolation is the caller's job. Not documented as such. | Medium (security) | `Rag.Api/Program.cs` |
| A7 | PgVector: `embedding vector` is untyped-dimension with **no ANN index** (only a btree on `(collection, document_id)`), so every query is an exact seq scan. Fine for small corpora; will not scale. No hybrid-capable `tsvector` column. | Medium (perf) | `PgVectorStore.cs:213-232` |
| A8 | Metadata filter values are `string`-only; `GetMetadata` drops non-string payload values silently. | Low | `QdrantVectorStore.cs:222` |
| A9 | `Task.WhenAll` of N Redis `SetAsync` in `EmbedBatchAsync` is unbounded; batch sizes are small today, but not bounded by design. | Low | `CachingEmbeddingProvider.cs:53` |
| A10 | Embedding-cache key omits provider (model+dims only). Two providers serving the same model name would collide. Low probability. | Low | `CachingEmbeddingProvider.cs:68` |
| A11 | No retrieval-quality measurement at all; chunk size 1000/200 and `DefaultTopK=5` are unvalidated defaults. | High (process) | — |
| A12 | Workers have no health endpoint; compose cannot detect a wedged consumer. | Low | `Rag.*Worker` |

## 5. Implications for the roadmap

- **Cache correctness (A1) must precede any new retrieval strategy**: each strategy/config adds new key dimensions; fixing the key model once, with a fingerprint abstraction, avoids re-breaking it per phase.
- **A4 forces an additive contract**: keep `IVectorStore` unchanged for compatibility; introduce a separate retrieval-level abstraction and a capabilities descriptor that stores can opt into. See `PLAN.md` Phase 4.
- **A11 means Phase 3 is the real gate**: nothing in Phases 4–7 ships without measured evidence against the Phase 3 baseline.
- Security gaps (A5, A6) are documented in KB Chapter 13 and addressed only where retrieval work touches them (filter allow-list in Phase 4); API authentication is **out of scope** and listed as a remaining risk.
