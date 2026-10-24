# ADR 0003 — Provider-independent reranking: neutral contract, per-provider adapters, candidate text inside each question

Status: accepted for implementation; **not adopted for production** (no live evaluation has run) (2026-10-24)

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
