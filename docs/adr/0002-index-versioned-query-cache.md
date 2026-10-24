# ADR 0002 — Query-cache keys carry the index version and the whole retrieval fingerprint

Status: accepted (2026-10-24)

## Context
The query cache keyed on `collection|topK|filters|query`. Re-indexing a document, changing the embedding model, or two filters whose text collided kept serving stale or wrong results.
Three failing regression tests reproduced it before any fix.

## Decision
`CollectionMetadata.IndexVersion` (an opaque token, new on every index run) and a length-prefixed, hashed key over: schema version, collection, index version, embedding provider/model/size,
strategy id (including fusion parameters and, later, reranker identity and candidate budget), topK, filters, query. Invalidation is by key change, not deletion. Degraded results are never cached.

## Consequences
No distributed delete races; old entries expire by TTL. GUID tokens rather than counters because Qdrant has no atomic increment. A read/write race can only mis-serve one answer, not pin it.
Details and failure modes: knowledge base chapter 8.
