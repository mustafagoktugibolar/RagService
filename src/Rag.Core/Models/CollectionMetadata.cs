namespace Rag.Core.Models;

public sealed record CollectionMetadata
{
    public string Collection { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int VectorSize { get; init; }
}
