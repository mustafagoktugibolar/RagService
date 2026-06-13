namespace Rag.Core.Models;

public sealed record VectorDocument
{
    public string DocumentId { get; init; } = string.Empty;

    public string ChunkId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public float[] Embedding { get; init; } = [];

    public Dictionary<string, string>? Metadata { get; init; }
}
