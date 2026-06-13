namespace Rag.Core.Contracts;

public sealed record QueryResponse
{
    public string RequestId { get; init; } = string.Empty;

    public List<RetrievedChunkDto> Results { get; init; } = [];

    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }
}
