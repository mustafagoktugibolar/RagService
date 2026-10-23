# Retrieval evaluation — real-hybrid

| Setting | Value |
|---|---|
| Strategy | `hybrid:rrf60:cand50:w1/1` |
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
| 1 | 0.825 | 0.825 | 0.621 | 0.825 | 0.768 | 0.825 |
| 3 | 0.925 | 0.392 | 0.804 | 0.863 | 0.805 | 0.743 |
| 5 | 0.950 | 0.270 | 0.871 | 0.869 | 0.835 | 0.780 |
| 10 | 1.000 | 0.150 | 0.954 | 0.877 | 0.857 | 0.800 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 1.000 | 1.000 | 0.750 | 0.802 |
| keyword | 14 | 1.000 | 1.000 | 0.964 | 0.974 |
| multi-relevant | 7 | 1.000 | 0.952 | 1.000 | 0.852 |
| paraphrase | 16 | 1.000 | 0.906 | 0.771 | 0.767 |

## Chunk level

Scored queries: 37; skipped (no relevant item at this level): 7.

| K | Hit | Precision | Recall | MRR | nDCG | MAP |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.757 | 0.757 | 0.703 | 0.757 | 0.741 | 0.757 |
| 3 | 0.892 | 0.315 | 0.851 | 0.806 | 0.799 | 0.775 |
| 5 | 0.946 | 0.205 | 0.919 | 0.820 | 0.829 | 0.795 |
| 10 | 1.000 | 0.108 | 0.973 | 0.829 | 0.849 | 0.804 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 1.000 | 1.000 | 0.750 | 0.810 |
| keyword | 14 | 1.000 | 1.000 | 0.964 | 0.974 |
| multi-relevant | 4 | 1.000 | 1.000 | 0.646 | 0.750 |
| paraphrase | 16 | 1.000 | 0.938 | 0.771 | 0.771 |

## Latency (retrieval only, per query)

| mean ms | p50 ms | p95 ms | max ms |
|---:|---:|---:|---:|
| 0.98 | 0.47 | 0.62 | 23.28 |

