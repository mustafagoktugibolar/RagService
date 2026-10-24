# Protocol — human-labeling pilot (20–30 queries)

**Written and committed before any query exists and before any annotator has seen a passage.** Changes after labels exist must be recorded at the bottom with their reason.
Thresholds in the table below are the same constants the report tool applies (`PilotReport`); a test fails if this document and the code disagree.

Context: `plan-independent-validation.md` (why), `send-safety-policy.md` (what may be sent to a provider), `annotation-guidelines.md` (how to label).

## Owner decisions this protocol implements
1. **Corpus:** RagService's own technical documentation, restricted to documents that are safe to send to an external model provider. Confidential or proprietary material is excluded.
2. **Target effect:** δ = 0.05 nDCG@10 is the *initial engineering acceptance threshold*, not a universal statistical truth. It is reconsidered after the pilot.
3. **Scope:** a pilot of 20–30 queries first, to measure corpus difficulty, annotation quality, disagreement and realistic effort. The full ~160-query effort does **not** start yet.
4. **Separation:** the pilot is kept apart from the final held-out set (see "Isolation").
5. **Process:** written guidelines, blinded candidate pools, a relevance scale, inter-annotator agreement.
6. **No LLM labels:** relevance labels are never produced by a model.
7. **Production default unchanged:** Dense, reranking off.
8. **No paid call** and no Jev run without separate explicit approval. The pilot itself is fully offline.

## Corpus
- Allow-listed files only (README, assessment, knowledge-base chapters 1/6/6B/7/8 and index, ADR 0001–0003, provider research). Frozen into `datasets/human-pilot-v1/corpus/` with SHA-256 hashes and the source commit.
- **Chunking for the pilot: 250 tokens with 40 overlap** (a pilot parameter, recorded in the manifest). At the production setting (1000/200) this corpus is only about 36 chunks, as small as golden-v1,
  and a top-20 list would again cover more than half of it. Shorter passages also make a judgment faster. The consequence: the pilot evaluates retrieval at a finer granularity than production
  does; the final phase must decide whether to keep it.
- Language: the knowledge-base chapters are Turkish, the rest English. Mixed-language retrieval is realistic for this project; it also means the English-trained local models used for pooling are weaker on part of the corpus.
- Limitation stated up front: the documents themselves were written with AI assistance. The queries and **all relevance labels** are human. A corpus of about 130–150 chunks is still small next to a production index.

## Roles
| Role | Who | Notes |
|---|---|---|
| Query writers | ≥ 2 people | Write from `topics.md`, never seeing passages |
| Annotators | 2 independent people | Grade blinded sheets independently |
| Adjudicator | 1 person | Resolves every disagreement; sees both grades, never systems or ranks |
| Owner | repository owner | Reviews the corpus and signs it off for sending |

If a second annotator is genuinely unavailable, the documented fallback is **test–retest**: the same person re-grades a fixed random 30 % of items after at least a week. Agreement is then intra-annotator,
reported as such, and weaker evidence. It must be chosen before annotation starts, not after seeing disagreements.

## Workflow
| # | Step | Command / artifact | Done by |
|---|---|---|---|
| 1 | Freeze the corpus, scan it | `--mode pilot-corpus` → manifest, `corpus/`, `chunks.json`, `topics.md` | tool |
| 2 | Review and sign off | `--mode pilot-signoff --by <id> --confirm "…"` | owner |
| 3 | Write queries | fill `queries.csv` from `queries.template.csv`; `--mode pilot-queries` validates | people |
| 4 | Pool and blind | `--mode pilot-pool` → `pools/sheets/annotator-<id>.csv`, `pools/private/pool-key.json` | tool |
| 5 | Grade independently | return `judgments/annotator-<id>.csv` | annotators |
| 6 | List disagreements | `--mode pilot-adjudication` → `adjudication/disagreements.csv` | tool |
| 7 | Adjudicate | return `adjudication/adjudicated.csv` (final grade + note) | adjudicator |
| 8 | Report | `--mode pilot-report` → `final-judgments.json`, `docs/eval/human-pilot-report.md` | tool |

Pools use only **free offline systems**: BM25, a hashing-embedding cosine search and (if the model is present) BM25 followed by the local ONNX cross-encoder; plus 3 random passages per
query. A dense run with OpenAI embeddings would add candidates but is a paid call, so it is deferred; the report states that passages only such a system would retrieve are not judged.

## Query set (24 queries planned)
`keyword` 5 · `paraphrase` 6 · `multi-relevant` 4 · `conceptual` 4 · `cross-lingual` 3 · `no-answer` 2. Minimums enforced by the validator: 4 / 5 / 3 / 3 / 2 / 2. Ids start with `pilot-`.

## Relevance scale
0 not relevant · 1 related only · 2 substantially helpful · 3 answers it (`annotation-guidelines.md`). "Relevant" for binary measures means grade ≥ 2.

## Acceptance criteria (fixed in advance)
| id | criterion | threshold | outcome rule |
|---|---|---|---|
| P1 | Safe to send | scanner clean **and** owner sign-off bound to the manifest | otherwise fail |
| P2 | Queries | 20–30, category minimums met, all by registered humans | otherwise fail |
| P3 | Isolation | 0 overlaps with golden-v1 or any final-set query | otherwise fail |
| P4 | Completeness | ≥ 98% of pooled items graded by both annotators | otherwise fail |
| P5 | Agreement | quadratic-weighted κ ≥ 0.60 **and** binary κ (grade ≥ 2) ≥ 0.60 | κ 0.40–0.60 or weak binary κ → review; below 0.40 or undefined → fail |
| P6 | Answerability | ≥ 85% of answerable queries have a grade ≥ 2 passage; 0 no-answer queries with one | otherwise fail |
| P7 | Headroom | best free baseline mean nDCG@10 ≤ 0.90 **and** ≥ 30% of queries below 0.90; a mean ≥ 0.95 fails (ceiling) | in between → review |
| P8 | Effort | median ≤ 45 s per judgment | 45–60 s → review; above 60 s → fail; no timings → review |
| P9 | Pool size | median 15-30 unique candidates per query | otherwise review |
| P10 | Human labels | every final label is a two-human agreement or a recorded human adjudication | otherwise fail |

**Decision:** any *fail* → **NO-GO** (fix and repeat the affected steps); no fail but a *review* → **REVISE** (adjust guidelines, pool depth or passage length as indicated);
all pass → **GO** to the final-dataset phase with the guidelines unchanged. P7 uses δ = 0.05 as the gain the dataset must be able to resolve.

## What the pilot also produces
Weighted κ with a 95% query-cluster bootstrap interval, binary κ, exact and within-one agreement, a confusion matrix, per-system headroom, median seconds and an extrapolation to the final phase
(median seconds × 190 queries × median pool size × 2 annotators), and a **proxy** standard deviation of the per-query nDCG@10 difference between the two best free systems, with the number of
held-out queries it implies for δ = 0.05. The proxy is not the variance of the comparison that matters (dense vs dense + reranker, unmeasured here).

## Isolation from the final held-out set
- Pilot queries carry the `pilot-` prefix and live in `datasets/human-pilot-v1/`; the final set will have its own directory and ids.
- `SplitIsolation` rejects any pilot query that equals, after normalisation, or nearly equals (Jaccard ≥ 0.8) a golden-v1 or final-set query; pooling is refused until it is clean.
- Guidelines may change after the pilot (`guidelines-v2`). **Pilot labels are never reused as final labels**, and the people who graded the pilot should not write final queries from passages they remember seeing.
- The pilot corpus is frozen; the final phase may use the same frozen corpus (with different queries) or a larger one, decided after the report.

## Cost and billing statements
- The pilot makes **no paid call**: BM25, hashing embeddings and the local ONNX model run on this machine.
- The spend guard used by the reranking experiments is a **best-effort stop-loss, not an absolute billing cap**; its residual risks are in `docs/kb/06b-reranking.md`. The provider-side limit is the real ceiling.
- Any later paid step (OpenAI embeddings to top up the pool, a reranker run on the new labels, Jev) needs its own plan numbers and explicit approval.

## Deliverables of the pilot phase
The report, `final-judgments.json`, the agreement figures, a recommendation for the final phase (GO / REVISE / NO-GO with the reasons), and the proposed next validation step.

## Amendments
*(none yet)*
