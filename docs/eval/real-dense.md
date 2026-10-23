# Retrieval evaluation — real-dense

| Setting | Value |
|---|---|
| Strategy | `dense` |
| Embedder | `text-embedding-3-small` (real) |
| Chunking | size 1000 / overlap 200 tokens |
| Reranker | `none` |
| Query transformer | `none` |
| Dataset version | `golden-v1.0.0` (44 queries) |
| Index version | `in-memory-per-run` |
| Git commit | `0b0b830` |

## Document level

Scored queries: 40; skipped (no relevant item at this level): 4.

| K | Hit | Precision | Recall | MRR | nDCG | MAP |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.875 | 0.875 | 0.671 | 0.875 | 0.832 | 0.875 |
| 3 | 0.975 | 0.417 | 0.833 | 0.917 | 0.877 | 0.792 |
| 5 | 0.975 | 0.275 | 0.887 | 0.917 | 0.887 | 0.822 |
| 10 | 1.000 | 0.148 | 0.942 | 0.921 | 0.903 | 0.838 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 1.000 | 0.833 | 0.833 | 0.823 |
| keyword | 14 | 1.000 | 1.000 | 0.964 | 0.974 |
| multi-relevant | 7 | 1.000 | 0.952 | 1.000 | 0.866 |
| paraphrase | 16 | 1.000 | 0.906 | 0.865 | 0.871 |

## Chunk level

Scored queries: 37; skipped (no relevant item at this level): 7.

| K | Hit | Precision | Recall | MRR | nDCG | MAP |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.811 | 0.811 | 0.757 | 0.811 | 0.811 | 0.811 |
| 3 | 0.973 | 0.342 | 0.932 | 0.883 | 0.887 | 0.845 |
| 5 | 0.973 | 0.211 | 0.946 | 0.883 | 0.891 | 0.851 |
| 10 | 1.000 | 0.111 | 0.986 | 0.887 | 0.906 | 0.860 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 1.000 | 1.000 | 0.833 | 0.854 |
| keyword | 14 | 1.000 | 1.000 | 0.964 | 0.974 |
| multi-relevant | 4 | 1.000 | 1.000 | 0.750 | 0.798 |
| paraphrase | 16 | 1.000 | 0.969 | 0.865 | 0.884 |

## Latency (retrieval only, per query)

| mean ms | p50 ms | p95 ms | max ms |
|---:|---:|---:|---:|
| 204.30 | 194.69 | 304.97 | 333.80 |

