# Reranking experiment — openai-decisions:gpt-6-luna:rel-v2:score

| Setting | Value |
|---|---|
| Reranker identity | `openai-decisions:gpt-6-luna:rel-v2:score` |
| Resolved model reported by provider | `none reported` |
| Dataset | `golden-v1.1.0` (scored queries only) |
| Candidate budget (dense and hybrid alike) | 10 |
| Hybrid configuration (from the tuning run, not re-tuned) | lexical weight 0.5, RRF k 30, 20 candidates per leg |
| Failure policy during the experiment | Fail (a silent fallback would be indistinguishable from "no gain") |
| Git commit | `988e3c9` |

**The held-out slice decides. Tune and pooled slices are descriptive.** No parameter was tuned on any of them.

## Held-out (decides) — 19 queries

| pipeline | nDCG@1 | nDCG@5 | nDCG@10 | MRR@10 | Recall@10 | candidate Recall@20 |
|---|---:|---:|---:|---:|---:|---:|
| dense | 0.729 | 0.849 | 0.861 | 0.877 | 0.930 | 1.000 |
| dense+rerank | 0.970 | 0.961 | 0.961 | 1.000 | 0.930 | 1.000 |
| hybrid | 0.594 | 0.765 | 0.785 | 0.798 | 0.956 | 1.000 |
| hybrid+rerank | 0.940 | 0.963 | 0.963 | 1.000 | 0.956 | 1.000 |

## Tune (descriptive) — 21 queries

| pipeline | nDCG@1 | nDCG@5 | nDCG@10 | MRR@10 | Recall@10 | candidate Recall@20 |
|---|---:|---:|---:|---:|---:|---:|
| dense | 0.925 | 0.922 | 0.940 | 0.960 | 0.952 | 1.000 |
| dense+rerank | 1.000 | 0.979 | 0.980 | 1.000 | 0.952 | 1.000 |
| hybrid | 0.925 | 0.941 | 0.946 | 0.962 | 0.976 | 1.000 |
| hybrid+rerank | 1.000 | 0.983 | 0.984 | 1.000 | 0.976 | 1.000 |

## All scored queries (descriptive) — 40 queries

| pipeline | nDCG@1 | nDCG@5 | nDCG@10 | MRR@10 | Recall@10 | candidate Recall@20 |
|---|---:|---:|---:|---:|---:|---:|
| dense | 0.832 | 0.887 | 0.903 | 0.921 | 0.942 | 1.000 |
| dense+rerank | 0.986 | 0.970 | 0.971 | 1.000 | 0.942 | 1.000 |
| hybrid | 0.768 | 0.857 | 0.870 | 0.884 | 0.967 | 1.000 |
| hybrid+rerank | 0.971 | 0.973 | 0.974 | 1.000 | 0.967 | 1.000 |

## Comparisons on the held-out split (paired bootstrap, 10 000 resamples, 95 % interval)

| comparison | nDCG@10 difference [interval] | nDCG@1 difference | interval > 0 | nDCG@1 ≥ 0 | no category loss > 0.05 | rerank latency ≤ limit | candidate |
|---|---|---:|:-:|:-:|:-:|:-:|:-:|
| rerank on dense candidates (dense → dense+rerank) | +0.100 [+0.033, +0.177] | +0.241 | yes | yes | yes | not measured | **no** |
| rerank on hybrid candidates (hybrid → hybrid+rerank) | +0.178 [+0.085, +0.274] | +0.346 | yes | yes | yes | not measured | **no** |
| hybrid candidates vs dense candidates, both reranked (dense+rerank → hybrid+rerank) | +0.002 [0.000, +0.007] | -0.030 | no | no | yes | n/a | **no** |
| best reranked hybrid vs production default (dense) (dense → hybrid+rerank) | +0.102 [+0.034, +0.182] | +0.211 | yes | yes | yes | not measured | **no** |
| hybrid vs dense, no reranking (reference) (dense → hybrid) | -0.076 [-0.155, -0.010] | -0.135 | no | no | no | n/a | **no** |

`n/a` = not applicable to that comparison; `not measured` would mean no live request was made.

## Latency and cost

No live provider request was made (everything replayed from cache): rerank latency was **not measured**.

| pipeline | provider requests | failed | input tokens | cached input tokens | observed cost (USD) |
|---|---:|---:|---:|---:|---:|
| dense+rerank | 0 | 0 | 0 | 0 | 0.000000 |
| hybrid+rerank | 0 | 0 | 0 | 0 | 0.000000 |

Requests and tokens count only live calls: pairs already scored by an earlier pipeline are replayed from the pair cache, so the hybrid pipeline pays only for candidates dense did not already cover.

## Query-level evidence: where hybrid candidates changed the reranked outcome (|Δ nDCG@10| ≥ 0.10)

No query differed by 0.10 or more between dense+rerank and hybrid+rerank.

A reranked hybrid can beat reranked dense only when its candidate set contains relevant documents dense missed ("rescued by hybrid" > 0) or when the extra candidates are ordered better; the columns above show which.
