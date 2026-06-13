namespace Rag.Core.Contracts;

public sealed record IndexDocumentResponse
{
    public string RequestId { get; init; } = string.Empty;

    public string DocumentId { get; init; } = string.Empty;

    public int ChunkCount { get; init; }

    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }
}
