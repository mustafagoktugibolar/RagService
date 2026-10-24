# ADR 0003 — Provider-independent reranking: neutral contract, per-provider adapters, candidate text inside each question

Status: accepted. OpenAI Decisions **validated live** (40 requests) and a pre-registered *adoption candidate* for dense retrieval; Jev **not** evaluated; **production default unchanged** (2026-10-24)

## Context
Two decision APIs were to be compared — OpenAI Decisions and TypeSafe Jev — with a local ONNX cross-encoder as an optional third provider. Their wire formats differ (`input`/`questions[]` vs
`state`/`questions{}`, `levels` vs `criteria`, `predicate` vs `noul`, refusals, model aliasing, separate credentials). Facts and unknowns: `docs/research/reranking-providers.md`.

## Decision
1. **Contract** (`Rag.Core.Reranking`): `IReranker.RerankAsync(query, candidates) → scores by chunk id`, plus `Name` = provider:model:rubric:mechanism, which is part of the cache key.
   `RerankingRetriever` owns ordering (ties and unscored candidates keep retrieval order), keeps text/metadata/source attribution and original scores, and applies the failure policy.
2. **Adapters** (`OpenAiDecisionsReranker`, `JevReranker`) contain only their own wire format; a small `ProviderReranker` base holds what is identical (batching, bounded concurrency,
   partial-batch failure, validation, usage, telemetry). Provider DTOs never leave the adapter. `NoOpReranker` is the ablation baseline; the ONNX reranker is an optional separate project.
3. **Mechanism:** pointwise `score` over a shared 4-level rubric (`rel-v2`). `choice` cannot rank, pairwise is O(N²), listwise is not offered by either API. `predicate`/`noul` is implemented but exploratory.
4. **Layout:** the query is the shared input, each candidate travels inside its own question, named positionally (`c0…`); chunk ids never leave the process. Questions are isolated from one another (documented for Jev), so one
   passage cannot steer another's score, and a request still carries up to 20–400 questions. `QuestionsPerRequest = 1` gives strictly independent requests as a control.
5. **Safety:** passages are fenced by a random per-request delimiter and stripped of invisible characters (a security review showed the first fixed-tag design was bypassable); responses are validated before anything is applied;
   logs and exceptions never contain query, passage, response body or credential; HTTPS enforced; startup validation refuses a bad configuration.
6. **Failure:** `Fallback` (default) serves retrieval order with a warning and never caches it; `Fail` fails the query. The experiment always uses `Fail`.

## Consequences
- Providers are interchangeable by configuration (`Retrieval:Reranking:Provider`); adding Jina or another model means one new adapter class.
- Scores are provider-calibrated: only order within a request is used, never values across providers.
- **Data egress:** with a hosted provider, query and passage text leave the process. Reranking is off by default for that reason as well as for cost.
- Retries use the existing in-house pipeline (two retries, only 408/429/5xx); `Retry-After` is not honoured yet.
- The OpenAI request is only as good as an assumption that questions do not influence each other; the isolation control exists to test it.

## Update — live OpenAI Decisions result (2026-10-24)
Held-out nDCG@10 dense 0.861 → dense+OpenAI Decisions 0.979 (+0.118, 95 % interval [+0.044, +0.200]), nDCG@1 0.729 → 0.940, rerank request p50 399 ms / p95 600 ms, 0 failed requests,
≈ $0.00077 per query observed. All four pre-registered conditions passed. Reranked hybrid equals reranked dense at budget 20 (identical candidate sets); at budget 10 the difference is +0.002 [0.000, +0.007].
Limits: LLM-authored judgments, a 35-chunk corpus with ceiling effects, 19 held-out queries, one run. Enabling reranking in production remains a separate decision (cost, data egress, single provider, the limits above).
The spend estimator was 61 % low and the approved dollar amount was exceeded ($0.0306 vs $0.024). The guard now reserves per request, reconciles with billed tokens, learns the estimator's error and applies a 2× initial safety factor.
It is a **best-effort stop-loss, not an absolute billing cap**; the first-request estimate can still be wrong by more than the factor, so the provider-side budget limit remains the real ceiling.
