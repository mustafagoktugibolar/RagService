# Reranking providers — verified API facts and design analysis

Researched 2026-10-24. **Every row is tagged with where it came from.** Nothing here was exercised against a live endpoint: no
paid request has been made to either provider, so *how the services actually behave* (latency, determinism, rate limits, error
bodies) is **unverified** and listed as such.

## 1. Sources and what could not be read

| Source | Result |
|---|---|
| `platform.openai.com/decisions` (the URL given) | **HTTP 403, could not be read.** |
| `developers.openai.com/api/docs/guides/decisions` (OpenAI's guide, found via search) | Read. Used as the OpenAI source of truth. |
| `typesafe.ai/blog/introducing-system-one-models-and-jev` | Read. Marketing announcement, no API reference. |
| `docs.typesafe.ai/introduction`, `/primitives/score.md`, `/introduction/quickstart.md`, `/api` | Read. Used as the Jev source of truth. |
| Secondary articles on Decisions (Promptfoo, LangChain reference, blogs) | Seen in search results only; **not relied on** (they disagree on details). |

## 2. OpenAI Decisions (OpenAI guide)

| Item | Finding | Status |
|---|---|---|
| Endpoint | `POST /v1/decisions` | documented |
| Auth | `Authorization: Bearer $OPENAI_API_KEY` — the same key family already used for embeddings | documented |
| Model | `gpt-6-luna` is the only supported model | documented |
| Request | `model`, `input` (string, or ordered messages incl. `input_json` parts), `questions` (**1–400**, each with a unique `name`) | documented |
| Question types | `predicate` (→ probability 0–1), `choice` (→ one supplied value), `score` (2–10 ordered `levels`, each `label` + `description`, index from 0) | documented |
| Response | `answers[]` with `type` and `name`; predicate → `probability`; score → `score` (probability-weighted average, may fall between levels), `probabilities[]` (`value`, `label`, `probability`), `confidence`; **a question can come back as `type: "refusal"`** | documented |
| Usage | `usage.input_tokens` (+ `input_tokens_details.cached_tokens`, `cache_write_tokens`) | documented |
| Pricing | $0.10 / 1M uncached input tokens; **output tokens not charged**; cached reads discounted | documented in the guide; re-check before spending |
| Prompt caching | Explicit mode via `prompt_cache_key`, `prompt_cache_breakpoint`; shared prefix ≥ 1 024 tokens; breakpoints not allowed in question instructions | documented |
| Availability | Described as generally available | documented |
| Relevance guidance | No ranking API. "Use `predicate` for checks such as passage relevance; set thresholds from labeled examples." | documented |
| Rate limits, max input size, error body format | **not stated** | **unknown** |
| Whether questions in one request are fully isolated from each other | implied ("use separate requests when a later question depends on an earlier answer") but **not stated** | **unverified** |

## 3. TypeSafe Jev / System One

| Item | Finding | Status |
|---|---|---|
| Endpoint | `POST https://api.typesafe.ai/v1/systemone` (`GET /v1/models` lists models) | documented |
| Auth | `Authorization: Bearer <API_KEY>`, key from `console.typesafe.ai/keys`. **A separate credential; the OpenAI key is unrelated.** | documented |
| Model | `jev-latest` (alias); the response reports the resolved version, e.g. `jev-1.13.0`, "which can change between releases" | documented |
| Request | `state` (string, object or array — the content all questions refer to), `model`, `questions` (object; **keys are names the caller chooses**) | documented |
| Question types | `noul` (probability of yes, 0–1), `choice` (criteria object, ≤ 255), `score` (criteria array of ordered levels, ≤ 10; the Score page says at least two, the API reference says `minItems: 1`), `box` (images) | documented |
| Response | `score` → `score` (expected level, may fall between), `probabilities` (keyed by level), `legend`, `confidence`; `noul` → `noul`; `usage.input_tokens`, `usage.output_tokens` | documented |
| Multiple questions | Many questions in one call, "evaluated in parallel and in isolation against the same state" | documented |
| Pricing | Announcement: $0.042 / MTok input, output "FREE". The API reference says output tokens are "currently free of charge". | company claim, not independently confirmed |
| Availability | The announcement says "early access … bringing developers off the waitlist". The quick start does not mention a waitlist. | **conflicting / unverified** |
| Errors | Only `422 HTTPValidationError` (`{"detail":[{loc,msg,type}]}`) is documented | partial |
| Limits on questions per request, state size, rate limits, determinism, timeouts | **not stated** | **unknown** |

## 4. The two APIs are close but not the same

| | OpenAI Decisions | Jev |
|---|---|---|
| Shared content | `input` | `state` |
| Questions | array, each with `name` | object keyed by name |
| Score levels | `levels: [{label, description}]` | `criteria: ["…", "…"]` (plain strings or objects) |
| Score result | `answers[i].score` | `answers[name].score` |
| Binary result | `predicate` → `probability` | `noul` → `noul` |
| Refusal | `type: "refusal"` possible | not documented |
| Pinning | one model id | alias vs resolved version differ |
| Credential | existing OpenAI key | new, separate |

Hence two independent adapters with provider DTOs kept inside each adapter; only a neutral request plan and neutral scores are shared.

## 5. Which mechanism ranks independent query–passage pairs?

| Mechanism | Output | Fit for ranking |
|---|---|---|
| **`score`** over ordered relevance levels | expected level, continuous (probability-weighted) | **Best fit.** Continuous values order candidates with few ties; the graded rubric matches the 0–3 graded judgments in `golden-v1`. Costs rubric tokens per question. |
| `predicate` / `noul` | P(relevant), 0–1 | Cheaper (no level text) and what OpenAI recommends for passage relevance, but a binary question compresses graded relevance; many strong candidates may saturate near 1 and tie. Kept as an optional mechanism, to be compared before it is trusted. |
| `choice` | one label | **Not a ranking tool**: it picks a single winner. Could pick the best of N, but not order N. Rejected for reranking. |
| Pairwise (A vs B) | preference | O(N²) judgments (190 for 20 candidates); rejected on cost. |
| Listwise (rank the whole list in one prompt) | an ordering | Neither API offers it: `choice` selects one value, there is no "order these". Emulating it with an LLM prompt is outside "decision" APIs and was not approved. |

**Primary mechanism: pointwise `score`** with one shared rubric. `predicate`/`noul` is implemented as an alternative but is
**exploratory** until measured.

## 6. Request layout and why

Questions in one request share the same content (`input`/`state`). Two layouts:

- **Shared candidates:** put the query and all candidates into the shared content and ask one question per candidate. Cheap, but every question *sees every other candidate*: a score can be influenced by neighbours, and **one malicious chunk can address the scorer for all of them** (prompt injection reaches every score).
- **Candidate in the question (chosen):** shared content = **only the query**; each question's `instructions` carry **its own passage** (delimited, declared untrusted). Questions are isolated from each other (documented for Jev, assumed for OpenAI), so a candidate cannot influence another's score, and one request still carries up to 20–400 questions.

`QuestionsPerRequest` sets the batch size. **`1` means strictly independent per-pair requests** (same code path, N× the requests), which is the
control for the isolation assumption on OpenAI. Question names are positional (`c0…cN`), mapped back to chunk ids locally: chunk ids never
leave the process and cannot collide with provider naming rules.

Untrusted passages are placed inside the question text, so the instruction states that the passage is data and any instructions inside it must be ignored.
This reduces, but does not eliminate, injection risk; a passage can still try to inflate **its own** score. (KB chapter 13.)

## 7. Scores, calibration and ties

- Each provider returns an expected level in `[0, levels−1]`; the adapter maps it to a **relevance in [0, 1]** (`score / (levels − 1)`).
- **Scores are provider-calibrated only. A 0.7 from OpenAI and a 0.7 from Jev are not comparable.** Only the order within one request is used; nothing compares values across providers or across queries.
- Ties (and unscored candidates) keep their **original retrieval order** (`RerankingRetriever`): ordering is deterministic.
- Validation before any ranking is applied: answer set must match the requested names, no duplicates, value finite and within range, refusals count as missing. Anything else is a provider failure, not a score.

## 8. Cost model (estimates, to be replaced by observed usage)

Per query with 20 candidates of ~90 tokens each, a ~120-token rubric repeated per question and ~40 tokens of instructions:
`20 × (90 + 120 + 40) ≈ 5 000` input tokens + the shared query. At the guide's price that is ≈ $0.0005 / query (OpenAI) and ≈ $0.0002 (Jev, announcement price).
These are order-of-magnitude figures; the experiment records real `usage` per request. Output tokens are reported as free by both.

## 9. What is still unknown and must be measured, not assumed

1. Whether the existing OpenAI key/org can call `/v1/decisions` and `gpt-6-luna` at all.
2. Rate limits and whether batching 20 questions is accepted/fast; error bodies; determinism run-to-run.
3. Whether OpenAI questions are truly isolated (the `QuestionsPerRequest = 1` control).
4. Jev access (waitlist) and whether its key is available.
5. Whether either provider improves ranking on `golden-v1` — the point of the experiment.
