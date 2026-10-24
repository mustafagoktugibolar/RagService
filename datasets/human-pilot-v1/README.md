# Human-labeling pilot dataset — `human-pilot-v1`

A 20–30 query pilot to learn how hard this corpus is, how well two people agree, and how long labeling really takes, **before** committing to the ~160-query effort.
Protocol and pre-registered acceptance criteria: `docs/eval/protocol-human-pilot.md`. Rules for labelers: `docs/eval/annotation-guidelines.md`.
What may be sent to a provider: `docs/eval/send-safety-policy.md`.

**This dataset is separate from the final held-out set.** Pilot queries are `pilot-…`; pilot labels are never reused as final labels.
**No relevance label here is produced by a model.** Nothing in this directory is sent to any provider by the pilot, and no paid call is made.

## Layout

| Path | What | Written by |
|---|---|---|
| `corpus-manifest.json` | Included/excluded files, hashes, chunking, source commit, safety review and owner sign-off | tool + owner |
| `corpus/` | Frozen copies of the included documents | tool |
| `chunks.json` | Every passage as it would be sent: id, document, title, language, text | tool |
| `topics.md` | Topic titles for query writers (no passage text) | tool |
| `queries.template.csv` | 24 rows with ids and category slots; copy to `queries.csv` and fill | people |
| `queries.json` | Validated queries | tool |
| `annotators.template.json` | Copy to `annotators.json`; `kind` must be `human` | owner |
| `pools/sheets/annotator-<id>.csv` | **Blinded** sheet: query + passage only, shuffled per annotator | tool |
| `pools/private/pool-key.json` | **Unblinded** key (which system found what). Do not open before all grading is done | tool |
| `judgments/annotator-<id>.csv` | The returned, graded sheets | annotators |
| `adjudication/disagreements.csv` → `adjudicated.csv` | Items to resolve, then the resolutions with reasons | tool → adjudicator |
| `final-judgments.json` | Final labels: `agreed` or `adjudicated`, nothing else | tool |
| `docs/eval/human-pilot-report.md` | Agreement, headroom, effort, criteria, decision | tool |

## Steps (all offline; run from the repository root)

```bash
# 1. freeze + scan the corpus (requires the allow-listed files to be committed)
dotnet run --project src/Rag.Evaluation.Cli -- --mode pilot-corpus
# 2. review corpus-manifest.json and corpus/, then sign off (a human act)
dotnet run --project src/Rag.Evaluation.Cli -- --mode pilot-signoff --by <your-id> --confirm "I reviewed the corpus and it is safe to send to external model providers"
# 3. copy annotators.template.json -> annotators.json and queries.template.csv -> queries.csv, fill them in (humans only), then validate
dotnet run --project src/Rag.Evaluation.Cli -- --mode pilot-queries
# 4. build pools and the blinded sheets (add --depth / --negatives to change the pool)
dotnet run --project src/Rag.Evaluation.Cli -- --mode pilot-pool
# 5. each annotator grades ONLY their own sheet and saves it as judgments/annotator-<id>.csv
# 6. list disagreements, then the adjudicator fills final_grade + note and saves adjudication/adjudicated.csv
dotnet run --project src/Rag.Evaluation.Cli -- --mode pilot-adjudication
# 7. final labels and the report
dotnet run --project src/Rag.Evaluation.Cli -- --mode pilot-report
```

Open CSV files as UTF-8 (they carry a BOM so spreadsheets show Turkish letters correctly) and keep the column order.
