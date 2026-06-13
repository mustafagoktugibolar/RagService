namespace Rag.Core.Contracts;

public sealed record QueryRequest
{
    public string RequestId { get; init; } = string.Empty;

    public string Collection { get; init; } = string.Empty;

    public string Query { get; init; } = string.Empty;

    public int TopK { get; init; }

    public Dictionary<string, string>? Filters { get; init; }
}
