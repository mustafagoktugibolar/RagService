namespace Rag.Evaluation.Dataset;

/// <summary>A versioned, self-contained labeled dataset: the corpus to index plus judged queries.</summary>
public sealed record EvalDataset
{
    /// <summary>Bump whenever the corpus text or any judgment changes; recorded in every run.</summary>
    public string DatasetVersion { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<CorpusDocument> Corpus { get; init; } = [];

    public IReadOnlyList<EvalQuery> Queries { get; init; } = [];
}

public sealed record CorpusDocument
{
    public string DocumentId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed record EvalQuery
{
    public string Id { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    /// <summary>e.g. keyword, paraphrase, multi-relevant, no-answer. Used for per-category breakdowns.</summary>
    public string? Category { get; init; }

    public Dictionary<string, string>? Filters { get; init; }

    /// <summary>Graded document-level judgments. An empty list marks a deliberate no-answer query.</summary>
    public IReadOnlyList<RelevantDocument> RelevantDocuments { get; init; } = [];

    /// <summary>
    /// Optional explicit chunk-level judgments (chunkId → grade). Fragile: chunk ids change with chunking config.
    /// Prefer <see cref="RelevantDocument.Evidence"/>, which survives re-chunking.
    /// </summary>
    public Dictionary<string, int>? RelevantChunkIds { get; init; }
}

public sealed record RelevantDocument
{
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>1 = marginally relevant … 3 = directly answers the query.</summary>
    public int Grade { get; init; } = 1;

    /// <summary>
    /// Phrase that must appear in a chunk for that chunk to count as relevant at chunk level.
    /// Chunk judgments are derived from it at evaluation time, so they follow any chunking strategy.
    /// </summary>
    public string? Evidence { get; init; }
}
