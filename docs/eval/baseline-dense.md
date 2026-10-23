# Retrieval evaluation — baseline-dense

| Setting | Value |
|---|---|
| Strategy | `dense` |
| Embedder | `hashing-bow-v1` (synthetic) |
| Chunking | size 1000 / overlap 200 tokens |
| Reranker | `none` |
| Query transformer | `none` |
| Dataset version | `golden-v1.0.0` (44 queries) |
| Index version | `in-memory-per-run` |
| Git commit | `9dc7455` |

## Document level

Scored queries: 40; skipped (no relevant item at this level): 4.

| K | Hit | Precision | Recall | MRR | nDCG | MAP |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.675 | 0.675 | 0.517 | 0.675 | 0.646 | 0.675 |
| 3 | 0.800 | 0.317 | 0.688 | 0.729 | 0.678 | 0.616 |
| 5 | 0.850 | 0.210 | 0.721 | 0.739 | 0.694 | 0.629 |
| 10 | 0.900 | 0.123 | 0.796 | 0.746 | 0.717 | 0.646 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 1.000 | 0.833 | 0.714 | 0.637 |
| keyword | 14 | 1.000 | 1.000 | 0.917 | 0.938 |
| multi-relevant | 7 | 1.000 | 0.762 | 0.771 | 0.648 |
| paraphrase | 16 | 0.750 | 0.625 | 0.591 | 0.568 |

## Chunk level

Scored queries: 37; skipped (no relevant item at this level): 7.

| K | Hit | Precision | Recall | MRR | nDCG | MAP |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.676 | 0.676 | 0.622 | 0.676 | 0.660 | 0.676 |
| 3 | 0.811 | 0.279 | 0.770 | 0.734 | 0.722 | 0.689 |
| 5 | 0.811 | 0.168 | 0.770 | 0.734 | 0.722 | 0.689 |
| 10 | 0.865 | 0.095 | 0.838 | 0.741 | 0.743 | 0.701 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 0.667 | 0.667 | 0.667 | 0.628 |
| keyword | 14 | 1.000 | 1.000 | 0.917 | 0.938 |
| multi-relevant | 4 | 1.000 | 1.000 | 0.786 | 0.822 |
| paraphrase | 16 | 0.750 | 0.688 | 0.591 | 0.575 |

## Latency (retrieval only, per query)

| mean ms | p50 ms | p95 ms | max ms |
|---:|---:|---:|---:|
| 0.57 | 0.14 | 0.14 | 19.13 |

