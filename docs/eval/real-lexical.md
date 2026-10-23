# Retrieval evaluation — real-lexical

| Setting | Value |
|---|---|
| Strategy | `lexical` |
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
| 1 | 0.775 | 0.775 | 0.587 | 0.775 | 0.718 | 0.775 |
| 3 | 0.850 | 0.358 | 0.704 | 0.808 | 0.718 | 0.670 |
| 5 | 0.925 | 0.240 | 0.800 | 0.825 | 0.757 | 0.698 |
| 10 | 0.975 | 0.135 | 0.879 | 0.832 | 0.787 | 0.715 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 1.000 | 1.000 | 0.778 | 0.739 |
| keyword | 14 | 1.000 | 1.000 | 1.000 | 1.000 |
| multi-relevant | 7 | 1.000 | 0.810 | 0.929 | 0.739 |
| paraphrase | 16 | 0.938 | 0.781 | 0.652 | 0.630 |

## Chunk level

Scored queries: 37; skipped (no relevant item at this level): 7.

| K | Hit | Precision | Recall | MRR | nDCG | MAP |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.730 | 0.730 | 0.676 | 0.730 | 0.699 | 0.730 |
| 3 | 0.757 | 0.261 | 0.703 | 0.739 | 0.701 | 0.689 |
| 5 | 0.838 | 0.184 | 0.811 | 0.756 | 0.745 | 0.719 |
| 10 | 0.946 | 0.103 | 0.919 | 0.770 | 0.779 | 0.733 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 1.000 | 1.000 | 0.704 | 0.740 |
| keyword | 14 | 1.000 | 1.000 | 1.000 | 1.000 |
| multi-relevant | 4 | 1.000 | 1.000 | 0.611 | 0.651 |
| paraphrase | 16 | 0.875 | 0.812 | 0.621 | 0.625 |

## Latency (retrieval only, per query)

| mean ms | p50 ms | p95 ms | max ms |
|---:|---:|---:|---:|
| 0.35 | 0.02 | 0.10 | 14.31 |

