using Rag.Core.Abstractions;
using Rag.Core.Models;

namespace Rag.Core.Tests.Fakes;

/// <summary>
/// Deterministic in-memory <see cref="IVectorStore"/>. Search returns stored chunks in insertion order
/// (score 1.0) after applying exact-match metadata filters; ranking quality is not its concern —
/// it exists so services can be exercised end to end without infrastructure.
/// </summary>
public class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<string, List<VectorDocument>> _collections = new();
    private readonly Dictionary<string, CollectionMetadata> _metadata = new();

    public int SearchCalls { get; private set; }

    protected IEnumerable<VectorDocument> Documents(string collection, Dictionary<string, string>? filters) =>
        !_collections.TryGetValue(collection, out var list)
            ? []
            : list.Where(d => filters is null || filters.All(f =>
                d.Metadata is not null && d.Metadata.TryGetValue(f.Key, out var v) && v == f.Value)).ToArray();

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
        SearchCalls++;
        IReadOnlyList<VectorSearchResult> results = !_collections.TryGetValue(collection, out var list)
            ? []
            : list
                .Where(d => filters is null || filters.All(f =>
                    d.Metadata is not null && d.Metadata.TryGetValue(f.Key, out var v) && v == f.Value))
                .Take(topK)
                .Select(d => new VectorSearchResult
                {
                    DocumentId = d.DocumentId,
                    ChunkId = d.ChunkId,
                    Text = d.Text,
                    Score = 1.0,
                    Metadata = d.Metadata
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
        Task.FromResult(_metadata.TryGetValue(collection, out var m) ? m : null);

    public Task SetCollectionMetadataAsync(CollectionMetadata metadata, CancellationToken ct)
    {
        _metadata[metadata.Collection] = metadata;
        return Task.CompletedTask;
    }
}
