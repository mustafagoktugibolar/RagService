using System.Text.Json;

namespace Rag.Evaluation.Dataset;

public sealed class DatasetValidationException(IReadOnlyList<string> errors)
    : Exception($"Dataset is invalid:{Environment.NewLine}- {string.Join(Environment.NewLine + "- ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class DatasetLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<EvalDataset> LoadAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var dataset = await JsonSerializer.DeserializeAsync<EvalDataset>(stream, JsonOptions, ct)
            ?? throw new DatasetValidationException(["File is empty."]);
        Validate(dataset);
        return dataset;
    }

    public static EvalDataset Parse(string json)
    {
        var dataset = JsonSerializer.Deserialize<EvalDataset>(json, JsonOptions)
            ?? throw new DatasetValidationException(["Document is empty."]);
        Validate(dataset);
        return dataset;
    }

    /// <summary>Throws <see cref="DatasetValidationException"/> listing every problem found, not just the first.</summary>
    public static void Validate(EvalDataset dataset)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(dataset.DatasetVersion))
        {
            errors.Add("datasetVersion is required.");
        }

        if (dataset.Corpus.Count == 0)
        {
            errors.Add("corpus is empty.");
        }

        var documents = new Dictionary<string, CorpusDocument>(StringComparer.Ordinal);
        foreach (var document in dataset.Corpus)
        {
            if (string.IsNullOrWhiteSpace(document.DocumentId))
            {
                errors.Add("A corpus document has an empty documentId.");
            }
            else if (!documents.TryAdd(document.DocumentId, document))
            {
                errors.Add($"Duplicate corpus documentId '{document.DocumentId}'.");
            }

            if (string.IsNullOrWhiteSpace(document.Text))
            {
                errors.Add($"Corpus document '{document.DocumentId}' has no text.");
            }
        }

        var queryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in dataset.Queries)
        {
            if (string.IsNullOrWhiteSpace(query.Id) || !queryIds.Add(query.Id))
            {
                errors.Add($"Query id '{query.Id}' is empty or duplicated.");
            }

            if (string.IsNullOrWhiteSpace(query.Query))
            {
                errors.Add($"Query '{query.Id}' has no text.");
            }

            var judged = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relevant in query.RelevantDocuments)
            {
                if (!documents.TryGetValue(relevant.DocumentId, out var document))
                {
                    errors.Add($"Query '{query.Id}' judges unknown document '{relevant.DocumentId}'.");
                    continue;
                }

                if (!judged.Add(relevant.DocumentId))
                {
                    errors.Add($"Query '{query.Id}' judges document '{relevant.DocumentId}' twice.");
                }

                if (relevant.Grade is < 1 or > 3)
                {
                    errors.Add($"Query '{query.Id}' / '{relevant.DocumentId}': grade must be 1..3 (was {relevant.Grade}).");
                }

                if (relevant.Evidence is not null
                    && !TextMatch.Contains(document.Text, relevant.Evidence))
                {
                    errors.Add($"Query '{query.Id}' / '{relevant.DocumentId}': evidence '{relevant.Evidence}' does not occur in the document.");
                }
            }

            if (query.RelevantChunkIds is not null && query.RelevantChunkIds.Values.Any(grade => grade < 0))
            {
                errors.Add($"Query '{query.Id}' has a negative chunk grade.");
            }
        }

        if (errors.Count > 0)
        {
            throw new DatasetValidationException(errors);
        }
    }
}

/// <summary>Whitespace- and case-insensitive containment, so evidence survives line wrapping and chunk trimming.</summary>
public static class TextMatch
{
    public static bool Contains(string text, string phrase) =>
        Normalize(text).Contains(Normalize(phrase), StringComparison.Ordinal);

    public static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
