# Hybrid tuning result

Protocol: `protocol-hybrid-tuning.md` (pre-registered). Dataset `golden-v1.1.0`, embedder `text-embedding-3-small`.
Tuning split: 23 queries. Held-out split: 21 queries (read once, after selection).

## Tuning split (document level)

| configuration | lexical weight | RRF k | candidates | nDCG@10 | nDCG@5 |
|---|---:|---:|---:|---:|---:|
| dense only | — | — | — | 0.940 | 0.922 |
| default hybrid | 1.0 | 60 | 50 | 0.942 | 0.919 |
| grid **← selected** | 0.5 | 30 | 20 | 0.946 | 0.941 |
| grid | 0.5 | 60 | 20 | 0.946 | 0.941 |
| grid | 0.5 | 10 | 20 | 0.946 | 0.941 |
| grid | 0.5 | 10 | 50 | 0.946 | 0.941 |
| grid | 0.5 | 30 | 50 | 0.946 | 0.940 |
| grid | 0.5 | 60 | 50 | 0.946 | 0.940 |
| grid | 0.5 | 10 | 10 | 0.944 | 0.941 |
| grid | 0.5 | 30 | 10 | 0.944 | 0.941 |
| grid | 0.5 | 60 | 10 | 0.944 | 0.941 |
| grid | 0.25 | 60 | 20 | 0.944 | 0.939 |

(top 10 of 36 configurations shown; all are in the JSON.)

## Held-out validation (document level)

Selected: lexical weight 0.5, RRF k 30, 20 candidates.

| | Hit@10 | Recall@10 | MRR@10 | nDCG@10 |
|---|---:|---:|---:|---:|
| dense | 1.000 | 0.930 | 0.877 | 0.861 |
| tuned hybrid | 1.000 | 0.956 | 0.798 | 0.785 |

nDCG@10 difference (hybrid − dense): -0.076, 95 % bootstrap interval [-0.155, -0.010] over 19 queries.
nDCG@1 difference: -0.135.

| category | held-out queries | dense nDCG@10 | hybrid nDCG@10 | difference |
|---|---:|---:|---:|---:|
| conceptual | 1 | 0.618 | 0.442 | -0.176 |
| keyword | 7 | 0.947 | 0.947 | 0.000 |
| multi-relevant | 3 | 0.761 | 0.754 | -0.007 |
| paraphrase | 8 | 0.853 | 0.698 | -0.155 |

## Verdict against the pre-registered rule

1. Interval lower bound > 0: **fail**
2. nDCG@1 difference ≥ 0: **fail**
3. No category with ≥ 3 queries loses more than 0.05: **fail**

**Not adopted.** Hybrid stays off by default.
