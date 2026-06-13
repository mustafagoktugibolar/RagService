namespace Rag.Core.Contracts;

public sealed record RetrievedChunkDto
{
    public string DocumentId { get; init; } = string.Empty;

    public string ChunkId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public double Score { get; init; }

    public Dictionary<string, string>? Metadata { get; init; }
}
