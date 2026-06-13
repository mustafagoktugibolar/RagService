namespace Rag.Core.Contracts;

public sealed record IndexDocumentRequest
{
    public string RequestId { get; init; } = string.Empty;

    public string Collection { get; init; } = string.Empty;

    public string DocumentId { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public Dictionary<string, string>? Metadata { get; init; }
}
