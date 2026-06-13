namespace Rag.Core.Models;

public sealed record DocumentChunk
{
    public string DocumentId { get; init; } = string.Empty;

    public string ChunkId { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public Dictionary<string, string>? Metadata { get; init; }
}
