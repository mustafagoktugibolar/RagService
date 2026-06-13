using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Rag.Core.Abstractions;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Resilience;

namespace Rag.Core.Providers.VectorStores;

public sealed class QdrantVectorStore : IVectorStore, IDisposable
{
    private readonly QdrantClient _client;
    private readonly QdrantOptions _options;

    public QdrantVectorStore(
        IOptions<QdrantOptions> options,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            throw new InvalidOperationException("Qdrant:Host is required when Rag:VectorStoreProvider is Qdrant.");
        }

        _client = new QdrantClient(
            _options.Host,
            _options.Port,
            _options.Https,
            _options.ApiKey ?? string.Empty,
            TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)),
            loggerFactory);
    }

    public async Task EnsureCollectionAsync(string collection, int vectorSize, CancellationToken ct)
    {
        await EnsureCollectionInternalAsync(collection, vectorSize, ct);
    }

    public async Task UpsertAsync(
        string collection,
        IReadOnlyList<VectorDocument> documents,
        CancellationToken ct)
    {
        if (documents.Count == 0)
        {
            return;
        }

        var points = documents.Select(document => new PointStruct
        {
            Id = CreatePointId($"{collection}:{document.ChunkId}"),
            Vectors = document.Embedding,
            Payload =
            {
                ["documentId"] = document.DocumentId,
                ["chunkId"] = document.ChunkId,
                ["text"] = document.Text,
                ["metadata"] = ToQdrantValue(document.Metadata)
            }
        }).ToArray();

        await RagPipelines.Qdrant.ExecuteAsync(
            async token => await _client.UpsertAsync(collection, points, wait: true, cancellationToken: token), ct);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string collection,
        float[] embedding,
        int topK,
        CancellationToken ct)
    {
        var results = await RagPipelines.Qdrant.ExecuteAsync(
            async token => await _client.SearchAsync(
                collection, embedding, limit: (ulong)topK,
                payloadSelector: true, vectorsSelector: false, cancellationToken: token), ct);

        return results
            .Select(point => new VectorSearchResult
            {
                DocumentId = GetString(point.Payload, "documentId"),
                ChunkId = GetString(point.Payload, "chunkId"),
                Text = GetString(point.Payload, "text"),
                Metadata = GetMetadata(point.Payload),
                Score = point.Score
            })
            .ToArray();
    }

    public async Task DeleteByDocumentIdAsync(string collection, string documentId, CancellationToken ct)
    {
        var filter = new Filter
        {
            Must =
            {
                Conditions.MatchKeyword("documentId", documentId)
            }
        };

        await _client.DeleteAsync(collection, filter, wait: true, cancellationToken: ct);
    }

    public async Task<CollectionMetadata?> GetCollectionMetadataAsync(string collection, CancellationToken ct)
    {
        await EnsureCollectionInternalAsync(_options.MetadataCollection, 1, ct);

        var points = await _client.RetrieveAsync(
            _options.MetadataCollection,
            CreatePointId(collection),
            withPayload: true,
            withVectors: false,
            cancellationToken: ct);

        var payload = points.FirstOrDefault()?.Payload;
        if (payload is null || payload.Count == 0)
        {
            return null;
        }

        return new CollectionMetadata
        {
            Collection = GetString(payload, "collection"),
            EmbeddingProvider = GetString(payload, "embeddingProvider"),
            EmbeddingModel = GetString(payload, "embeddingModel"),
            VectorSize = GetInt(payload, "vectorSize")
        };
    }

    public async Task SetCollectionMetadataAsync(CollectionMetadata metadata, CancellationToken ct)
    {
        await EnsureCollectionInternalAsync(_options.MetadataCollection, 1, ct);

        var point = new PointStruct
        {
            Id = CreatePointId(metadata.Collection),
            Vectors = new[] { 1f },
            Payload =
            {
                ["collection"] = metadata.Collection,
                ["embeddingProvider"] = metadata.EmbeddingProvider,
                ["embeddingModel"] = metadata.EmbeddingModel,
                ["vectorSize"] = (long)metadata.VectorSize
            }
        };

        await _client.UpsertAsync(_options.MetadataCollection, new[] { point }, wait: true, cancellationToken: ct);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private async Task EnsureCollectionInternalAsync(string collection, int vectorSize, CancellationToken ct)
    {
        if (await _client.CollectionExistsAsync(collection, ct))
        {
            return;
        }

        try
        {
            await _client.CreateCollectionAsync(
                collection,
                new VectorParams
                {
                    Size = (ulong)vectorSize,
                    Distance = Distance.Cosine
                },
                cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
        }
    }

    private static Guid CreatePointId(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes);
    }

    private static Value ToQdrantValue(Dictionary<string, string>? metadata)
    {
        return metadata is null || metadata.Count == 0
            ? new Dictionary<string, Value>()
            : metadata.ToDictionary(pair => pair.Key, pair => (Value)pair.Value);
    }

    private static string GetString(IReadOnlyDictionary<string, Value> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && value.HasStringValue
            ? value.StringValue
            : string.Empty;
    }

    private static int GetInt(IReadOnlyDictionary<string, Value> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && value.HasIntegerValue
            ? (int)value.IntegerValue
            : 0;
    }

    private static Dictionary<string, string>? GetMetadata(IReadOnlyDictionary<string, Value> payload)
    {
        if (!payload.TryGetValue("metadata", out var metadata) || metadata.StructValue is null)
        {
            return null;
        }

        var result = metadata.StructValue.Fields
            .Where(pair => pair.Value.HasStringValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value.StringValue);

        return result.Count == 0 ? null : result;
    }
}
