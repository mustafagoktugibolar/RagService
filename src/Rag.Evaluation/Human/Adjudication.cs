using System.Globalization;

namespace Rag.Evaluation.Human;

public sealed record Disagreement(string QueryId, string ChunkId, int GradeA, int GradeB, IReadOnlyList<string> FlagsA, IReadOnlyList<string> FlagsB);

public sealed record AssemblyResult(IReadOnlyList<FinalJudgment> Final, IReadOnlyList<Disagreement> Pending, int GradedByOnlyOne);

/// <summary>
/// Turns two independent annotators' grades into the final labels. Equal grades with no flag are accepted as they are; <b>every</b> other item
/// — any difference, however small, and anything flagged — needs a human adjudication with a written reason. The tool never decides.
/// </summary>
public static class FinalJudgments
{
    public static IReadOnlyList<Disagreement> Disagreements(AnnotatorJudgments a, AnnotatorJudgments b)
    {
        var graded = b.Items.ToDictionary(i => (i.QueryId, i.ChunkId));
        return a.Items
            .Where(x => graded.ContainsKey((x.QueryId, x.ChunkId)))
            .Select(x => (X: x, Y: graded[(x.QueryId, x.ChunkId)]))
            .Where(p => p.X.Grade != p.Y.Grade || p.X.Flags.Count > 0 || p.Y.Flags.Count > 0)
            .Select(p => new Disagreement(p.X.QueryId, p.X.ChunkId, p.X.Grade, p.Y.Grade, p.X.Flags, p.Y.Flags))
            .OrderBy(d => d.QueryId, StringComparer.Ordinal).ThenBy(d => d.ChunkId, StringComparer.Ordinal)
            .ToList();
    }

    public static AssemblyResult Assemble(AnnotatorJudgments a, AnnotatorJudgments b, IReadOnlyList<Adjudication> adjudications)
    {
        var byKey = b.Items.ToDictionary(i => (i.QueryId, i.ChunkId));
        var decided = adjudications.ToDictionary(x => (x.QueryId, x.ChunkId));
        var final = new List<FinalJudgment>();
        var pending = new List<Disagreement>();

        foreach (var x in a.Items)
        {
            if (!byKey.TryGetValue((x.QueryId, x.ChunkId), out var y))
            {
                continue;
            }

            var needsHuman = x.Grade != y.Grade || x.Flags.Count > 0 || y.Flags.Count > 0;
            if (!needsHuman)
            {
                final.Add(new FinalJudgment(x.QueryId, x.ChunkId, x.Grade, "agreed"));
            }
            else if (decided.TryGetValue((x.QueryId, x.ChunkId), out var resolution))
            {
                if (resolution.Grade is < 0 or >= PilotValidator.Grades || string.IsNullOrWhiteSpace(resolution.Note) || string.IsNullOrWhiteSpace(resolution.AdjudicatorId))
                {
                    throw new PilotValidationException([$"Adjudication for ({x.QueryId}, {x.ChunkId}) needs a grade in 0..3, an adjudicator id and a written note."]);
                }

                final.Add(new FinalJudgment(x.QueryId, x.ChunkId, resolution.Grade, "adjudicated"));
            }
            else
            {
                pending.Add(new Disagreement(x.QueryId, x.ChunkId, x.Grade, y.Grade, x.Flags, y.Flags));
            }
        }

        var bothGraded = a.Items.Count(i => byKey.ContainsKey((i.QueryId, i.ChunkId)));
        return new AssemblyResult(final, pending, a.Items.Count + b.Items.Count - 2 * bothGraded);
    }

    public static string DisagreementSheet(
        IReadOnlyList<Disagreement> disagreements, IReadOnlyList<PilotQuery> queries, IReadOnlyList<PilotChunk> chunks)
    {
        var chunkById = chunks.ToDictionary(c => c.ChunkId);
        var queryById = queries.ToDictionary(q => q.Id);
        return Csv.Write(new[] { (IReadOnlyList<string>)["query_id", "query", "chunk_id", "document_title", "passage", "grade_a", "grade_b", "flags_a", "flags_b", "final_grade", "note"] }
            .Concat(disagreements.Select(d => (IReadOnlyList<string>)
            [
                d.QueryId, queryById[d.QueryId].Text, d.ChunkId, chunkById[d.ChunkId].Title, chunkById[d.ChunkId].Text,
                d.GradeA.ToString(CultureInfo.InvariantCulture), d.GradeB.ToString(CultureInfo.InvariantCulture),
                string.Join(';', d.FlagsA), string.Join(';', d.FlagsB), "", ""
            ])));
    }

    public static IReadOnlyList<Adjudication> ParseAdjudicationCsv(string csv, string adjudicatorId)
    {
        var rows = Csv.Read(csv);
        var columns = rows[0].Select((n, i) => (n: n.Trim().ToLowerInvariant(), i)).ToDictionary(x => x.n, x => x.i);
        string Cell(List<string> row, string name) => columns.TryGetValue(name, out var i) && i < row.Count ? row[i].Trim() : string.Empty;

        var result = new List<Adjudication>();
        var errors = new List<string>();
        for (var r = 1; r < rows.Count; r++)
        {
            var text = Cell(rows[r], "final_grade");
            if (text.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var grade))
            {
                errors.Add($"Row {r + 1}: final_grade '{text}' is not an integer.");
                continue;
            }

            result.Add(new Adjudication
            {
                QueryId = Cell(rows[r], "query_id"), ChunkId = Cell(rows[r], "chunk_id"), Grade = grade,
                AdjudicatorId = adjudicatorId, Note = Cell(rows[r], "note")
            });
        }

        if (errors.Count > 0)
        {
            throw new PilotValidationException(errors);
        }

        return result;
    }
}
