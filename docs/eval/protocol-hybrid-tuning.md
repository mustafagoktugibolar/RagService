# Protocol — tuning hybrid retrieval with a held-out check

**Written and committed before any tuning or held-out result existed.** If anything below is changed after results are seen,
the run must be labelled *exploratory* and the change recorded here with the reason.

## Question
Can some RRF configuration make hybrid retrieval better than dense-only retrieval on queries not used to choose it?
Context: with default settings (equal weights, k=60, 50 candidates) hybrid was *worse* than dense on `golden-v1.0.0`
(`phase4-hybrid-comparison.md`).

## Data
`datasets/golden-v1/dataset.json`, version `golden-v1.1.0` (same corpus and judgments as 1.0.0; only a `split` field was added).
Split rule, fixed before tuning: within each category, queries sorted by id; even positions → `tune`, odd → `heldout`.

| split | scored queries (with relevant docs) | + no-answer |
|---|---:|---:|
| tune | 21 | 2 |
| heldout | 19 | 2 |

**Limitation, stated up front:** 19 held-out queries is very small and judgments come from one annotator. Per-query nDCG noise is large;
only a large, consistent effect can pass the rule below. A failed test here means "no evidence", not "proven worse".
Embedder: `text-embedding-3-small`; chunking 1000/200; retrieval depth K=10.

## Search space (tuning split only)
Dense weight fixed at 1. `LexicalWeight` ∈ {0.25, 0.5, 0.75, 1.0} × `RrfK` ∈ {10, 30, 60} × `CandidateK` ∈ {10, 20, 50} = 36 configurations.
Reference rows: dense-only, and the untuned default (1.0 / 60 / 50).

## Selection (tuning split only)
Order: document nDCG@10 (desc) → document nDCG@5 (desc) → `CandidateK` (asc) → `LexicalWeight` (asc) → `RrfK` (asc);
metrics compared after rounding to 4 decimals. The held-out split is not read during selection.
Trying 36 configurations makes the best tuning score optimistic (winner's curse) — that is exactly why a held-out check follows.

## Validation (held-out split, run once)
Compare dense-only against the single selected hybrid configuration. Statistic: per-query document nDCG@10 difference
(hybrid − dense), paired bootstrap, 10 000 resamples, fixed seed 20261024, 95 % percentile interval of the mean difference.

**Hybrid is an adoption candidate only if all three hold:**
1. the lower bound of the 95 % interval for nDCG@10 difference is > 0;
2. mean nDCG@1 difference ≥ 0 (no harm at the top rank);
3. no category with ≥ 3 held-out scored queries loses more than 0.05 nDCG@10.

Otherwise hybrid stays off. Even if it passes, the default flips only after the reranking phase re-tests hybrid candidates
(a reranker may change the verdict in either direction).

## Not decided by this experiment
Latency: the offline harness uses cached embeddings and an in-memory store, so retrieval latency here says nothing about
production. Latency enters the decision in the reranking phase, measured with the same limits for each variant.
