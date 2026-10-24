# Protocol — decision-based reranking (OpenAI Decisions, TypeSafe Jev)

**Written and committed before any provider request was made and before any result existed.** A change made after results are seen must be
recorded here with its reason and the affected run labelled *exploratory*. Provider facts and unknowns: `docs/research/reranking-providers.md`.

## Question
Does reranking the retrieved candidates with a decision-based relevance score improve final ranking quality on `golden-v1.1.0`
compared with the current default (dense, no reranking) — and do hybrid candidates offer any measurable advantage once reranked?

## The six strategies
`Dense` · `Dense + OpenAI Decisions` · `Dense + Jev` · `Hybrid` · `Hybrid + OpenAI Decisions` · `Hybrid + Jev`. A provider run evaluates
its two reranked pipelines next to the two shared baselines, so every comparison uses identical queries, judgments, candidate budgets and categories.

## Fixed in advance (nothing below is tuned on any query)
| Item | Value |
|---|---|
| Dataset | `golden-v1.1.0`, the 40 queries that have relevant documents (tune 21, held-out 19) |
| Embeddings | `text-embedding-3-small`, replayed from the on-disk cache: no embedding call is made |
| Hybrid | lexical weight 0.5, RRF k 30, 20 candidates per leg — selected by the earlier tuning run, **not re-tuned** |
| Candidate budget (primary) | **20**, identical for dense and hybrid |
| Mechanism | `score`, rubric `rel-v2` (4 levels), one batched request (20 questions) per pipeline pass |
| OpenAI model | `gpt-6-luna` (the only model in the guide) |
| Jev model | `jev-latest`; the resolved version in the response is recorded in the report |
| Failure policy | `Fail`: a silent fallback would be indistinguishable from "no gain" |

**Secondary arm (diagnostic only):** budget **10**. At budget 20 the candidate sets of dense and hybrid are *identical* on this corpus (the plan
measured 0 hybrid-only pairs in 0/40 queries; the corpus has 35 chunks), so "hybrid candidate diversity" cannot be observed there. Budget 10
can make them differ. Because scores are cached per (query, passage), this arm adds no provider requests after the budget-20 run, hence **no live latency**;
it can never satisfy the latency condition and is not used for adoption. It exists to show whether diversity matters at all.

**Exploratory, not part of any verdict:** the `predicate`/`noul` mechanism and `QuestionsPerRequest = 1` (the isolation control). Each needs its own cost approval.

## Metrics
nDCG@1, nDCG@5, nDCG@10, MRR@10, Recall@10 (document level), **candidate Recall@20** (the recall ceiling reranking cannot exceed),
p50/p95 latency of a live rerank request, provider request count, input tokens, observed cost. Candidate generation and reranking are reported
separately: a reranker reorders a fixed set, so its gain is bounded by candidate Recall.

## Statistics and decision rule
Same method as the hybrid tuning: per-query nDCG@10 differences, paired bootstrap, 10 000 resamples, seed 20261024, 95 % interval, on the
**held-out slice only**. Tune and pooled slices are descriptive.

A challenger is an **adoption candidate** only if all hold:
1. the lower bound of the nDCG@10 interval is > 0;
2. mean nDCG@1 difference ≥ 0;
3. no category with ≥ 3 held-out queries loses more than 0.05 nDCG@10;
4. (rerank vs no rerank) p95 latency of a live rerank request ≤ **1 500 ms** — a policy limit, not derived from data. With no live request the latency is
   *not measured* and the challenger cannot be a candidate.

Comparisons: rerank on dense; rerank on hybrid; **hybrid vs dense candidates both reranked** (the diversity question); best reranked hybrid vs the
production default; hybrid vs dense without reranking (reference). A claim that hybrid candidates help requires that comparison to pass **and**
"rescued by hybrid" > 0 on the queries that moved (the relevant document was a hybrid candidate dense missed).

## What changes the production default
Nothing in this experiment changes it. The default stays `Dense`, reranking off. Even a passing challenger only becomes a *proposal*; flipping the
default also weighs observed cost, provider availability and terms, and **data egress**: query and passage text leave the process to a third party.

## Known limits, stated up front
- 19 held-out queries, one annotator, 35 short documents: only a large, consistent effect can pass; failing means "no evidence", not "proven useless".
- Provider run-to-run variance is **not measured** (the cache replays one answer per pair). A repeat run would need a separate approval.
- Whether OpenAI questions in one request are isolated is unverified; Jev documents isolation.
- `jev-latest` can change behind the alias; the report records the resolved version, and a different version mid-run would invalidate the cache assumption.
- Latency is that of the machine and network that ran it, from live requests only; cached replays do not count.
- Cost uses documented prices (OpenAI guide $0.10/M; Jev announcement $0.042/M, a company claim). Observed `usage` tokens replace the estimate in the report.

## Spending procedure
1. `rerank-plan` (no API call) prints requests, tokens and estimated cost per provider.
2. The owner approves explicit numbers; the CLI refuses to run without `--approve-requests` and `--approve-usd` that cover the plan.
3. A spend guard stops the run before the request that would exceed either number.
4. Jev runs only with a Jev credential and separate approval; the OpenAI key is not assumed to work for Jev.
