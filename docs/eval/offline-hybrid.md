# Retrieval evaluation — offline-hybrid

| Setting | Value |
|---|---|
| Strategy | `hybrid:rrf60:cand50:w1/1` |
| Embedder | `hashing-bow-v1` (synthetic) |
| Chunking | size 1000 / overlap 200 tokens |
| Reranker | `none` |
| Query transformer | `none` |
| Dataset version | `golden-v1.0.0` (44 queries) |
| Index version | `in-memory-per-run` |
| Git commit | `b7ec678` |

## Document level

Scored queries: 40; skipped (no relevant item at this level): 4.

| K | Hit | Precision | Recall | MRR | nDCG | MAP |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.775 | 0.775 | 0.587 | 0.775 | 0.732 | 0.775 |
| 3 | 0.875 | 0.350 | 0.725 | 0.821 | 0.733 | 0.681 |
| 5 | 0.875 | 0.230 | 0.758 | 0.821 | 0.756 | 0.696 |
| 10 | 0.975 | 0.135 | 0.879 | 0.833 | 0.787 | 0.720 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 1.000 | 0.833 | 0.778 | 0.649 |
| keyword | 14 | 1.000 | 1.000 | 1.000 | 1.000 |
| multi-relevant | 7 | 1.000 | 0.810 | 0.929 | 0.744 |
| paraphrase | 16 | 0.938 | 0.812 | 0.655 | 0.646 |

## Chunk level

Scored queries: 37; skipped (no relevant item at this level): 7.

| K | Hit | Precision | Recall | MRR | nDCG | MAP |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.730 | 0.730 | 0.676 | 0.730 | 0.714 | 0.730 |
| 3 | 0.811 | 0.279 | 0.757 | 0.770 | 0.738 | 0.718 |
| 5 | 0.838 | 0.178 | 0.797 | 0.776 | 0.758 | 0.731 |
| 10 | 0.919 | 0.100 | 0.892 | 0.786 | 0.786 | 0.745 |

By query category at K=10:

| Category | Queries | Hit | Recall | MRR | nDCG |
|---|---:|---:|---:|---:|---:|
| conceptual | 3 | 0.667 | 0.667 | 0.667 | 0.633 |
| keyword | 14 | 1.000 | 1.000 | 1.000 | 1.000 |
| multi-relevant | 4 | 1.000 | 1.000 | 0.675 | 0.715 |
| paraphrase | 16 | 0.875 | 0.812 | 0.649 | 0.646 |

## Latency (retrieval only, per query)

| mean ms | p50 ms | p95 ms | max ms |
|---:|---:|---:|---:|
| 0.59 | 0.11 | 0.21 | 21.06 |

