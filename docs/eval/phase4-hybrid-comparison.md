# Phase 4 — dense vs lexical vs hybrid (offline, synthetic embedder)

Runs: `baseline-dense`, `offline-lexical`, `offline-hybrid` (same directory). Dataset `golden-v1.0.0`, chunking 1000/200,
hybrid = RRF(k=60) over BM25 + dense, 50 candidates per leg. Generated with
`dotnet run --project src/Rag.Evaluation.Cli -- --strategy <dense|lexical|hybrid> --label <name>`.

**Read this first.** The "dense" leg here is a deterministic bag-of-words hashing embedder, not a semantic model.
Both legs match on surface tokens, so this experiment cannot say whether hybrid helps a *real* embedding model.
It does show the plumbing, fusion and metrics work end to end.

## Document level

| K | strategy | Hit | Recall | MRR | nDCG |
|---:|---|---:|---:|---:|---:|
| 1 | dense | 0.675 | 0.517 | 0.675 | 0.646 |
| 1 | lexical | 0.775 | 0.587 | 0.775 | 0.718 |
| 1 | hybrid | 0.775 | 0.587 | 0.775 | 0.732 |
| 5 | dense | 0.850 | 0.721 | 0.739 | 0.694 |
| 5 | lexical | 0.925 | 0.800 | 0.825 | 0.757 |
| 5 | hybrid | 0.875 | 0.758 | 0.821 | 0.756 |
| 10 | dense | 0.900 | 0.796 | 0.746 | 0.717 |
| 10 | lexical | 0.975 | 0.879 | 0.832 | 0.787 |
| 10 | hybrid | 0.975 | 0.879 | 0.833 | 0.787 |

## Chunk level (evidence-judged)

| K | strategy | Hit | Recall | MRR | nDCG |
|---:|---|---:|---:|---:|---:|
| 1 | dense | 0.676 | 0.622 | 0.676 | 0.660 |
| 1 | lexical | 0.730 | 0.676 | 0.730 | 0.699 |
| 1 | hybrid | 0.730 | 0.676 | 0.730 | 0.714 |
| 10 | dense | 0.865 | 0.838 | 0.741 | 0.743 |
| 10 | lexical | 0.946 | 0.919 | 0.770 | 0.779 |
| 10 | hybrid | 0.919 | 0.892 | 0.786 | 0.786 |

## What this does and does not show

- Dense results are bit-for-bit identical to the Phase 3 baseline: adding the strategy layer did not change the default path.
- BM25 beats the hashing "dense" leg on every metric. Expected: the hashing embedder has no IDF weighting, BM25 does. It says
  more about the weak synthetic embedder than about lexical search in general.
- Hybrid ≈ lexical (differences of 0.01–0.03, e.g. nDCG@1 0.732 vs 0.718; Hit@5 0.875 vs 0.925, lexical ahead). With 40 scored queries
  a single query moving one rank changes a metric by 0.025, so these gaps are **within noise**. No claim of improvement is made.
- **Decision:** `Retrieval:Strategy` stays `Dense` by default. Hybrid becomes a candidate default only after a comparison with a real
  embedding model on this dataset (and ideally a larger one) shows a gain beyond run-to-run variance.
