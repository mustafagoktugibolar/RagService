namespace Rag.Core.Models;

public sealed record CollectionMetadata
{
    public string Collection { get; init; } = string.Empty;

    public string EmbeddingProvider { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public int VectorSize { get; init; }

    /// <summary>
    /// Opaque token that changes whenever the collection's content changes (index or delete).
    /// Empty for collections written before versioning existed; readers treat that as a valid, stable version.
    /// </summary>
    public string IndexVersion { get; init; } = string.Empty;
}
