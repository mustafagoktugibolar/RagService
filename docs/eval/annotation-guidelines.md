# Annotation guidelines — `guidelines-v1`

*Kısa özet (TR): Her (sorgu, pasaj) çifti için 0–3 arası bir ilgi derecesi verirsiniz. Yalnızca verilen metne bakın, dış bilgi kullanmayın, diğer anotatörle konuşmayın ve **hiçbir yapay zekâ aracı kullanmayın**. Her satır için yaklaşık süreyi `seconds` sütununa yazın.*

These guidelines are for the people who write queries and the people who grade passages in the human-labeling pilot. They are written independently of any
model prompt on purpose: the labels must measure relevance as a person sees it, not agreement with a particular reranker's wording.

## 0. Ground rules (apply to everyone)

1. **Labels are made by humans only.** Do not use any AI assistant or model to write queries, to grade passages, to "check" a grade, or to explain a passage. This is
   attested in `annotators.json` (`kind: human`) and cannot be verified by the tools, so it rests on you.
2. **Work independently.** Do not discuss grades with the other annotator until both of you have submitted. Disagreements are resolved later, on purpose, by adjudication.
3. **Judge only what is on the sheet.** Do not search the repository, the web or your memory of the documents to decide a grade. A passage is graded on its own text.
4. **Record time honestly.** Fill `seconds` with how long the item took you (a rough timer per sitting divided by items is fine). It is used to plan the effort of the final phase.
5. **Do not try to guess how a system ranked a passage.** Order on the sheet is random and carries no information.
6. **A leading apostrophe is a safety marker, not part of the text.** Sheets are CSV files opened in a spreadsheet; any cell that starts with `=`, `+`, `-`, `@` or a tab is written with a leading `'` so a spreadsheet cannot run it as a formula (a markdown bullet `- item` therefore appears as `'- item`). Leave the apostrophe in place; the tools remove it when they read your sheet back. Do not start your own comments with those characters.

## Part A — Writing queries (query writers)

You receive `topics.md`: only the *titles* of documents, never their text.

- Write the question you would ask **if you did not know the answer** and wanted to find it in these documents. Do not read the documents first.
- Do not copy phrases from the titles; real users rarely use the document's own wording.
- Every query is one sentence, 8–300 characters, with a language tag: `en` or `tr`.
- Write the number and kind of queries shown in `queries.template.csv`; each row has a category:

| Category | What to write |
|---|---|
| `keyword` | A query built around an exact term, setting name, identifier or error you expect to appear in the text |
| `paraphrase` | The same need expressed in words a document is unlikely to use |
| `multi-relevant` | A question whose answer needs more than one passage, or that several passages answer in different ways |
| `conceptual` | A "why" or "how does this work" question rather than a lookup |
| `cross-lingual` | A query in one language where the best passages are likely written in the other (the corpus mixes Turkish and English) |
| `no-answer` | A plausible question about this project that the documents do **not** answer |

Put your own id in `written_by`. Each person writes some of the rows; ids are fixed by the template.

## Part B — Grading passages (annotators)

For every row of your sheet, read the query and the passage and write one grade from 0 to 3 in `grade`.

| Grade | Meaning | Test question |
|---|---|---|
| **3** | **Answers it.** A reader with only this passage could give the complete answer (or the essential part of a multi-part answer). | "Could I answer the query using just this passage?" — yes, fully |
| **2** | **Substantially helpful.** It addresses the query directly, but the answer is incomplete, needs a small inference, or needs one more passage. | yes, but partly |
| **1** | **Related only.** Same topic or area, may use a relevant term, but would not help answer. | no, but it is about the same thing |
| **0** | **Not relevant.** | no |

### Decision rules

- **Judge the meaning, not the language.** A Turkish passage can fully answer an English query; grade it 3 if it does.
- **A passing mention is not an answer.** If the answer appears only as an aside that a reader could easily miss, prefer 1 or 2.
- **Negative answers count.** If the query asks whether something is the default and the passage says it is not, that answers it.
- **Grade what the text says, as written**, even if you believe it is outdated or wrong.
- **Fragments:** passages are cut at fixed sizes and may begin or end mid-sentence. Grade what is present. If an answer is split across two passages, each is graded on its own content.
- **Overlap:** neighbouring passages share a few sentences; the same shared text should receive the same judgement in both.
- **Tables, lists and code** are ordinary content.
- **Do not reward length.** A short passage that answers the query beats a long one that only touches it.

### Flags (optional, `flags` column, separate several with `;`)

- `unclear-query` — the query can reasonably mean two things. Say in `comment` which reading you graded against.
- `unjudgeable` — the passage is cut or garbled so badly that you cannot judge it.

An item with a flag is always sent to adjudication, so use flags when they are true, not as a shortcut.

### Worked examples (generic, not taken from the corpus)

| Query | Passage (abridged) | Grade | Why |
|---|---|---|---|
| "How long do cached entries live?" | "Entries expire after 24 hours; the setting `TtlHours` changes this." | 3 | States the answer and where to change it |
| "How long do cached entries live?" | "Entries can expire. Expiry is configured in the options section." | 1 | Same topic, no duration |
| "How long do cached entries live?" | "The default is 24 hours for embeddings. Query results use a separate, shorter setting (not shown here)." | 2 | Answers for one kind of entry; the rest is elsewhere |
| "Why not cache everything forever?" | "Cached values go stale when the index changes, so a version is part of the key." | 3 | Gives the reason |
| "Is the feature on by default?" | "The feature is disabled unless explicitly enabled." | 3 | A negative statement answers the question |
| "Where is the queue configured?" | "The worker consumes messages from the queue and acknowledges them." | 1 | About the queue, not its configuration |

## Part C — Adjudication

After both sheets are returned, the tool lists every item where you differ by any amount or where either of you used a flag. The adjudicator (a person) sees both grades,
**never any system or rank**, writes the final grade and a short reason in `note`. Nothing is decided by averaging or by a tool.

## Revision log

| Version | Change |
|---|---|
| `guidelines-v1` | First version, used for the pilot. Any change after the pilot becomes `guidelines-v2`; pilot labels are never reused as final labels. |
