namespace Rag.Core.Contracts;

public sealed record RetrievedChunkDto
{
    public string DocumentId { get; init; } = string.Empty;

    public string ChunkId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public double Score { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Which retrievers produced this chunk and at which rank. Null for plain dense retrieval (legacy payload);
    /// for lexical and hybrid retrieval <see cref="Score"/> is the fused/lexical score, not a cosine similarity.
    /// </summary>
    public List<ChunkSourceDto>? Sources { get; init; }
}

public sealed record ChunkSourceDto(string Source, int Rank, double Score);
