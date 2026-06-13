using Rag.Core.Models;

namespace Rag.Core.Abstractions;

public interface IVectorStore
{
    Task EnsureCollectionAsync(string collection, int vectorSize, CancellationToken ct);

    Task UpsertAsync(string collection, IReadOnlyList<VectorDocument> documents, CancellationToken ct);

    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collection,
        float[] embedding,
        int topK,
        CancellationToken ct);

    Task DeleteByDocumentIdAsync(string collection, string documentId, CancellationToken ct);

    Task<CollectionMetadata?> GetCollectionMetadataAsync(string collection, CancellationToken ct);

    Task SetCollectionMetadataAsync(CollectionMetadata metadata, CancellationToken ct);
}
