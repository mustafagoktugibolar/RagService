using Rag.Core.Abstractions;
using Rag.Core.Models;

namespace Rag.Evaluation.Harness;

/// <summary>
/// Exact (brute-force) cosine search over in-memory chunks with exact-match AND metadata filters.
/// Ties are broken by chunk id so rankings are reproducible. Exact search also removes ANN approximation as a
/// variable when comparing retrieval strategies offline.
/// </summary>
public sealed class InMemoryCosineVectorStore : IVectorStore
{
    private readonly Dictionary<string, List<VectorDocument>> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollectionMetadata> _metadata = new(StringComparer.Ordinal);

    public Task EnsureCollectionAsync(string collection, int vectorSize, CancellationToken ct)
    {
        _collections.TryAdd(collection, []);
        return Task.CompletedTask;
    }

    public Task UpsertAsync(string collection, IReadOnlyList<VectorDocument> documents, CancellationToken ct)
    {
        var list = _collections.TryGetValue(collection, out var existing) ? existing : _collections[collection] = [];
        foreach (var document in documents)
        {
            list.RemoveAll(d => d.ChunkId == document.ChunkId);
            list.Add(document);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collection,
        float[] embedding,
        int topK,
        Dictionary<string, string>? filters,
        CancellationToken ct)
    {
        if (!_collections.TryGetValue(collection, out var list))
        {
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        }

        IReadOnlyList<VectorSearchResult> results = list
            .Where(d => filters is null || filters.All(f =>
                d.Metadata is not null && d.Metadata.TryGetValue(f.Key, out var value) && value == f.Value))
            .Select(d => (Document: d, Score: Cosine(embedding, d.Embedding)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Document.ChunkId, StringComparer.Ordinal)
            .Take(topK)
            .Select(x => new VectorSearchResult
            {
                DocumentId = x.Document.DocumentId,
                ChunkId = x.Document.ChunkId,
                Text = x.Document.Text,
                Score = x.Score,
                Metadata = x.Document.Metadata
            })
            .ToArray();
        return Task.FromResult(results);
    }

    public Task DeleteByDocumentIdAsync(string collection, string documentId, CancellationToken ct)
    {
        if (_collections.TryGetValue(collection, out var list))
        {
            list.RemoveAll(d => d.DocumentId == documentId);
        }

        return Task.CompletedTask;
    }

    public Task<CollectionMetadata?> GetCollectionMetadataAsync(string collection, CancellationToken ct) =>
        Task.FromResult(_metadata.TryGetValue(collection, out var metadata) ? metadata : null);

    public Task SetCollectionMetadataAsync(CollectionMetadata metadata, CancellationToken ct)
    {
        _metadata[metadata.Collection] = metadata;
        return Task.CompletedTask;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
