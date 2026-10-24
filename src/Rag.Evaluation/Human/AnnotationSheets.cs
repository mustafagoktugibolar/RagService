using System.Globalization;

namespace Rag.Evaluation.Human;

/// <summary>What an annotator sees for one item: the query, a context title, the passage. Nothing about which system found it, at what rank, or with what score.</summary>
public sealed record SheetRow(int ItemNo, string QueryId, string QueryText, string ChunkId, string DocumentTitle, string Passage);

public static class AnnotationSheets
{
    private static readonly string[] Header =
        ["item_no", "query_id", "query", "chunk_id", "document_title", "passage", "grade", "flags", "seconds", "comment"];

    /// <summary>
    /// A blinded, annotator-specific sheet. Query order and passage order within a query are shuffled with a seed derived from the annotator
    /// id, so two annotators see different orders and position carries no information about rank. Items are numbered in the shuffled order.
    /// </summary>
    public static IReadOnlyList<SheetRow> Build(
        PoolKey pool, IReadOnlyList<PilotQuery> queries, IReadOnlyList<PilotChunk> chunks, string annotatorId, int seed)
    {
        var chunkById = chunks.ToDictionary(c => c.ChunkId, StringComparer.Ordinal);
        var random = new Random(StableHash($"{seed}:{annotatorId}"));
        var rows = new List<SheetRow>();
        var number = 1;

        foreach (var query in queries.OrderBy(_ => random.Next()))
        {
            foreach (var item in pool.Pools[query.Id].OrderBy(_ => random.Next()))
            {
                var chunk = chunkById[item.ChunkId];
                rows.Add(new SheetRow(number++, query.Id, query.Text, chunk.ChunkId, chunk.Title, chunk.Text));
            }
        }

        return rows;
    }

    public static string ToCsv(IReadOnlyList<SheetRow> rows) =>
        Csv.Write(new[] { (IReadOnlyList<string>)Header }.Concat(rows.Select(r =>
            (IReadOnlyList<string>)[r.ItemNo.ToString(CultureInfo.InvariantCulture), r.QueryId, r.QueryText, r.ChunkId, r.DocumentTitle, r.Passage, "", "", "", ""])));

    /// <summary>Reads a returned sheet. Rows with an empty grade are ignored (they count as not graded); a non-integer grade is an error.</summary>
    public static AnnotatorJudgments ParseCsv(string csv, string annotatorId, string guidelinesVersion)
    {
        var rows = Csv.Read(csv);
        if (rows.Count == 0)
        {
            throw new PilotValidationException(["The sheet is empty."]);
        }

        var columns = rows[0].Select((name, index) => (name: name.Trim().ToLowerInvariant(), index)).ToDictionary(x => x.name, x => x.index);
        var missing = new[] { "query_id", "chunk_id", "grade" }.Where(c => !columns.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new PilotValidationException([$"The sheet is missing column(s): {string.Join(", ", missing)}."]);
        }

        string Cell(List<string> row, string name) => columns.TryGetValue(name, out var i) && i < row.Count ? row[i].Trim() : string.Empty;

        var errors = new List<string>();
        var items = new List<GradedItem>();
        for (var r = 1; r < rows.Count; r++)
        {
            var gradeText = Cell(rows[r], "grade");
            if (gradeText.Length == 0)
            {
                continue;
            }

            if (!int.TryParse(gradeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var grade))
            {
                errors.Add($"Row {r + 1}: grade '{gradeText}' is not an integer.");
                continue;
            }

            var secondsText = Cell(rows[r], "seconds");
            items.Add(new GradedItem
            {
                QueryId = Cell(rows[r], "query_id"),
                ChunkId = Cell(rows[r], "chunk_id"),
                Grade = grade,
                Flags = Cell(rows[r], "flags").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Seconds = double.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null,
                Comment = Cell(rows[r], "comment") is { Length: > 0 } comment ? comment : null
            });
        }

        if (errors.Count > 0)
        {
            throw new PilotValidationException(errors);
        }

        return new AnnotatorJudgments { AnnotatorId = annotatorId, GuidelinesVersion = guidelinesVersion, Items = items };
    }

    // string.GetHashCode is randomised per process; sheets must be reproducible.
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in text)
            {
                hash = hash * 31 + c;
            }

            return hash;
        }
    }
}
