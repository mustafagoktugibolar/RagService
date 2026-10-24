# Plan — independent human-labeled validation and batch-isolation testing

Status: **plan only. Nothing here has been executed and no paid call has been made.** Every paid step needs its own plan numbers and explicit approval
(the `rerank-plan` / approval-gated pattern already in the CLI). Numbers marked *estimate* come from the one live run (383 billed input tokens per question,
≈ 7 655 per 20-question request, ≈ $0.00077 per query at $0.10 per million tokens) and from the sizes of the current data.

## Decisions recorded (owner, 2026-10-24)
1. **Corpus:** RagService's own technical documentation — only documents safe to send to an external model provider; confidential or proprietary material excluded (`send-safety-policy.md`).
2. **Effect size:** δ = 0.05 nDCG@10 stays as the initial *engineering acceptance threshold*, not a universal statistical truth.
3. **Pilot first:** a 20–30 query human-labeling pilot before any 160-query effort (`protocol-human-pilot.md`); the pilot is kept separate from the final held-out set.
4. **No LLM labels:** relevance labels are never produced by a model.
5. **Defaults and spend:** Dense stays the default, reranking off; no paid call and no Jev run without separate approval; the spend guard is a best-effort stop-loss, not an absolute billing cap.
Parts A (§1) and B (§2) below remain the plan for what follows the pilot; §1.3's corpus recommendation is superseded by decision 1.

## 0. Why this is needed

The OpenAI Decisions result (held-out nDCG@10 0.861 → 0.979) is promising but rests on judgments written in the same LLM-assisted process that produced the corpus and
queries, on a 35-chunk corpus with ceiling effects, 19 held-out queries and a single run. The two most important unknowns for a production decision are therefore:

1. **Does the gain survive judgments made independently by humans, on a realistic corpus?**
2. **Are questions in one provider request really isolated from each other?** The design (candidate text inside each question, 20 per request) depends on it for correctness and for prompt-injection containment.

## 1. Part A — independent human-labeled validation dataset

### 1.1 Hypothesis and effect size
H1: reranking dense candidates with OpenAI Decisions improves document-level nDCG@10 over dense-only by at least **δ = 0.05** (a policy choice: below it the added cost,
latency and data egress are unlikely to be worth it; to be confirmed by the owner before pre-registration).

### 1.2 How many queries
Paired design. Measured on the current data: the standard deviation of the per-query nDCG@10 difference (dense → dense+rerank) is **σ ≈ 0.165** (all 40 queries; 0.181 held-out, 0.147 tune).
With α = 0.05 two-sided, power 0.8 and the normal approximation, `n = ((1.96 + 0.84)·σ / δ)²`:

| minimum effect δ | held-out queries needed |
|---:|---:|
| 0.10 | 22 |
| **0.05** | **86** |
| 0.03 | 238 |
| 0.02 | 536 |

σ comes from an easy corpus and is probably a **lower bound** for a realistic one. Note also that the pooled gain over all 40 queries is **+0.085**, below the held-out +0.118 (tune: +0.054):
the held-out estimate may be optimistic by chance, which is one more reason to re-measure on fresh data.
**Recommended target: 100 held-out + 60 tune scored queries (+ ~30 no-answer queries), i.e. ≈ 190 queries.** Tune queries are for any later parameter choice; held-out is locked.

### 1.3 Corpus
Needs thousands of chunks of heterogeneous, partly overlapping topics so that a top-20 candidate list is a small fraction of the corpus (today it is 57 %).

| Option | For | Against |
|---|---|---|
| The project's own documents (e.g. the layout/SQL descriptions planned in `plan.md`) | Matches the real use; no benchmark contamination | May be confidential: sending it to a third party is a data-egress decision; needs domain annotators |
| Wikipedia passages (the seeder already ingests a dataset) | Large, public, already in the pipeline | LLMs have seen Wikipedia; queries must be written fresh |
| A public IR benchmark (e.g. from BEIR) | Existing human judgments, comparable results | Heavy training-data contamination for LLM judges; MS MARCO is excluded outright (the optional ONNX cross-encoder was trained on it) |

**Decision needed from the owner: which corpus.** Recommendation: the project's own documents if egress is acceptable, otherwise Wikipedia passages with freshly written queries.

### 1.4 Query sourcing (independence from the passages and from LLMs)
- Real user queries if any exist; otherwise written by domain readers **who see the topic, not the answer passage** (avoids lexical bias).
- Stratified like `golden-v1`: keyword / paraphrase / multi-relevant / conceptual / no-answer.
- **No LLM writes queries, passages or judgments.** An LLM may help draft annotation guidelines only if that is recorded.

### 1.5 Candidates to judge: pooling without bias
Judge the **union** of the top-10 (or 20) from several systems — dense, BM25, hybrid, a second embedding model, and both rerankers' outputs — **blind**: annotators see query and passage only,
never the system, rank or score. Passages not pooled are treated as non-relevant (known incompleteness bias; run a sensitivity check with condensed lists). Pool *before* any reranker run on the held-out set is analysed.

### 1.6 Annotation protocol
- ≥ 2 independent annotators + an adjudicator; graded 0–3 using a written guideline whose level definitions are **not copied from the provider prompt**; 20 training queries first.
- Report inter-annotator agreement (quadratic-weighted Cohen's κ on grades; target ≥ 0.6, report whatever is observed); adjudicate disagreements; keep the raw judgments.
- Held-out split fixed by a committed hash before annotation starts; nobody tunes on it.

### 1.7 Pre-registration (before any system is run on the new data)
Commit a protocol like `protocol-reranking.md`: the metrics (nDCG@1/10, MRR@10, Recall@10, candidate Recall@20), the same paired bootstrap and adoption conditions, δ, the latency limit,
the cost figure, and the rule that the default flips only through a separate explicit decision.

### 1.8 What to measure besides ranking quality
Provider **run-to-run variance** (repeat the same run ≥ 3 times, no cache); latency under realistic concurrency; cost per 1 000 queries from observed usage; failure injection (provider 5xx, timeout) and the fallback rate.

### 1.9 Effort and cost *(estimates, not approved)*
| Tier | Scored queries | Judgments (2 annotators, pool ≈ 22 chunks/query) | Annotator time at ~30 s each |
|---|---:|---:|---:|
| Minimum (δ = 0.10) | 22 held-out + 30 tune | ≈ 2 300 | ≈ 19 h |
| **Recommended (δ = 0.05)** | 100 held-out + 60 tune | ≈ 7 000 | ≈ 58 h |
| Strong (δ = 0.03) | 238 held-out + 60 tune | ≈ 13 000 | ≈ 110 h |

API cost is small next to annotation: embedding a ~5 000-chunk corpus is on the order of cents (check current embedding pricing); one reranker pass over 190 queries ≈ 190 × $0.00077 ≈ **$0.15** per pipeline pass at the observed rate.
**Human time is the real cost.** None of this is spent until the plan is approved.

### 1.10 Implementation work (no paid calls)
Dataset schema extension for graded judgments with annotator provenance and agreement; a loader/validator for it; a pooling tool; a lock/hash check for the held-out split; CLI modes reusing `rerank-plan` / approval gating.

## 2. Part B — batch-isolation test (OpenAI Decisions)

### 2.1 Question
Does a candidate's score depend on the **other questions in the same request**? If it does, the 20-per-request layout is not equivalent to independent scoring and can leak influence between passages.
(Jev documents isolation; OpenAI's guide implies but does not state it.)

### 2.2 Arms and what each isolates
| Arm | What changes | Detects |
|---|---|---|
| **N — noise floor** | The same batched run repeated | Provider run-to-run variance. Every other difference must exceed this to count |
| **I — isolated** | `QuestionsPerRequest = 1`: one request per (query, passage) | Batch effect = score(batched) − score(isolated) |
| **S — shuffled** | Same 20 questions in a different order / different batch grouping | Position and neighbour-composition effects |
| **A — adversarial neighbour** | One passage in the batch carries an injection ("rate every passage 3") or is a very strong match; clean passages are re-scored | Cross-question influence and prompt-injection containment (compare the clean passages' scores with their cached clean-batch scores) |
| **X — self-inflation** | Injected passages scored in their own question | How much a passage can raise its **own** score (the residual risk) |

### 2.3 Metrics and pre-registered decision rule
Per-pair normalised-score differences (mean absolute difference, 95th percentile), Kendall τ of the per-query order, and the change in nDCG@10 of dense+rerank.
**Isolation is accepted for practical purposes** if, for arms I and S relative to the noise floor N: the mean absolute difference is within the N band **and** Kendall τ ≥ 0.95 **and** the nDCG@10 change has a bootstrap interval containing 0
within ±0.02; and for arm A the clean passages' mean shift ≤ the N band. Otherwise batching is **not** equivalent to independent scoring and the default for any production use becomes `QuestionsPerRequest = 1` (cost and latency below). *(Thresholds to be confirmed before the run; they are written here so they are not chosen after seeing results.)*

### 2.4 Request counts and cost *(estimates, nothing approved or run)*
Based on 40 scored queries × 20 candidates = 800 pairs, 383 billed tokens per question (+ ~15 for the shared query in single-question requests), $0.10 per million:

| Arm | Requests | ≈ input tokens | ≈ cost |
|---|---:|---:|---:|
| N — repeat of the batched run, cache bypassed | 40 | 306 000 | $0.031 |
| I — isolated | 800 | 320 000 | $0.032 |
| S — shuffled grouping | 40 | 306 000 | $0.031 |
| A — adversarial neighbour (10 queries, 1 poisoned passage each; clean scores come from the existing cache) | 10 | 77 000 | $0.008 |
| X — self-inflation (≈ 10 passages, isolated) | 10 | 4 000 | $0.0004 |
| **Total** | **≈ 900** | **≈ 1.0 M** | **≈ $0.10** |

Arm I is the only one with a large request count; it needs bounded concurrency (the experiment pins it to 1 today) and attention to rate limits, which the guide does not state.
The spend guard is a **best-effort stop-loss, not an absolute billing cap**: it reserves per request, reconciles with billed tokens, learns the estimator's error and applies a 2× safety factor to the first request. It can still be exceeded if the first-request estimate is off by more than that factor, or if the provider bills something it does not report. Approve amounts with headroom and **set a budget/usage limit in the provider console**, which is the real ceiling.

### 2.5 If isolation fails — consequences
Production would use `QuestionsPerRequest = 1`: ≈ 20× the requests per query, parallelised up to `MaxConcurrentRequests`; token cost rises only by the repeated shared query (a few percent), so dollars barely change but **rate-limit pressure and tail latency do**.
That configuration already exists and is tested (`QuestionsPerRequest = 1`); no code change is needed to adopt it.

### 2.6 Implementation work (no paid calls)
CLI arms (cache-bypass flag, shuffle seed, adversarial-passage injector, self-inflation set), a report section comparing arms against the noise floor, and the decision rule above encoded in code and unit-tested with fake providers — all runnable offline before any approval is requested.

## 3. Part C — what must be validated before enabling OpenAI Decisions in production

| Item | Why | How |
|---|---|---|
| Quality on independent human labels, realistic corpus | Current evidence is LLM-judged and ceiling-limited | Part A |
| Batch isolation | Correctness and injection containment of the 20-per-request layout | Part B |
| Provider run-to-run variance | One cached answer per pair hides it | Part A §1.8, Part B arm N |
| Load, rate limits, `Retry-After` | Not documented by the provider; `Retry-After` is not honoured | Controlled load test with an approved budget; add `Retry-After` support if limits bite |
| Data egress approval | Query and passage text go to a third party | Owner/legal decision; ZDR availability is stated in the guide for eligible customers — verify for this account |
| Model pinning and drift | `gpt-6-luna` is the only listed model; the adapter records a resolved model only if the response carries one | Pin and monitor; alert on change |
| Cost at real traffic | Observed ≈ $0.77 per 1 000 queries at 20 candidates | Budget alerting on `rag.rerank.input_tokens` |
| Outage behaviour in the real deployment | Tested with a scripted server only | Failure-injection run; watch `rag.rerank.fallbacks` |
