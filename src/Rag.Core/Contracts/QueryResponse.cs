namespace Rag.Core.Contracts;

public sealed record QueryResponse
{
    public string RequestId { get; init; } = string.Empty;

    public List<RetrievedChunkDto> Results { get; init; } = [];

    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>Machine-readable notes when a query succeeded in a degraded way (fallback strategy, failed hybrid leg).</summary>
    public List<string>? Warnings { get; init; }
}
