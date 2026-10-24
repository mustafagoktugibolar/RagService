# ADR 0001 — Hybrid search is implemented but not the default

Status: accepted (2026-10-24)

## Context
Dense retrieval misses exact tokens (identifiers, codes); BM25 misses paraphrase. Hybrid retrieval (dense + sparse, reciprocal rank fusion) is the usual remedy.

## Decision
Implement hybrid retrieval (`Retrieval:Strategy = Hybrid`, Qdrant sparse vectors with IDF, client-side RRF) and keep **`Dense` the default**.

## Evidence
`golden-v1.1.0`, `text-embedding-3-small`. Default settings: document nDCG@10 dense 0.903, hybrid 0.857. After tuning weight, k and candidate count on a tuning split
and validating **once** on a held-out split (protocol committed first): held-out nDCG@10 dense 0.861, tuned hybrid 0.785, difference −0.076, 95 % bootstrap interval
[−0.155, −0.010]; all three pre-registered adoption conditions failed. A synthetic-embedder run had suggested the opposite; it was an artefact of a weak dense leg.
Reports: `docs/eval/phase4-hybrid-comparison.md`, `docs/eval/hybrid-tuning.md`.

## Consequences
Hybrid stays available for corpora where it may help (many identifiers). Fusion is client-side so one implementation serves every store and keeps source attribution.
Legacy Qdrant collections cannot gain a sparse vector (verified): lexical/hybrid needs a new collection and a re-index; indexing into legacy collections keeps working.
Revisit if the corpus changes character or a reranker changes the candidate economics (ADR 0003).
