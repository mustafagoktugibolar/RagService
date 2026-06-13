using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Telemetry;

namespace Rag.Core.Services;

public sealed class VectorSearchService(
    IEmbeddingProvider embeddingProvider,
    IVectorStore vectorStore,
    IDistributedCache cache,
    IOptions<RagOptions> options,
    IOptions<QueryCacheOptions> queryCacheOptions,
    ILogger<VectorSearchService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<QueryResponse> SearchAsync(QueryRequest request, CancellationToken ct)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.query");
        activity?.SetTag("rag.collection", request.Collection);

        try
        {
            ValidateRequest(request);
            var ragOptions = options.Value;
            var topK = request.TopK > 0 ? request.TopK : ragOptions.DefaultTopK;

            logger.LogInformation(
                "Searching collection {Collection} for request {RequestId} with TopK {TopK}",
                request.Collection,
                request.RequestId,
                topK);

            var metadata = await vectorStore.GetCollectionMetadataAsync(request.Collection, ct);
            if (metadata is null)
            {
                return Failure(request, $"Collection '{request.Collection}' has not been indexed.");
            }

            var expected = new CollectionMetadata
            {
                Collection = request.Collection,
                EmbeddingProvider = ragOptions.EmbeddingProvider,
                EmbeddingModel = ragOptions.EmbeddingModel,
                VectorSize = ragOptions.VectorSize
            };

            if (!IsCompatible(metadata, expected))
            {
                return Failure(
                    request,
                    $"Collection '{request.Collection}' was indexed with {metadata.EmbeddingProvider}/{metadata.EmbeddingModel}/{metadata.VectorSize}, but current configuration is {expected.EmbeddingProvider}/{expected.EmbeddingModel}/{expected.VectorSize}.");
            }

            var cacheOpts = queryCacheOptions.Value;
            string? queryCacheKey = cacheOpts.Enabled ? BuildQueryCacheKey(request, topK) : null;

            if (queryCacheKey is not null)
            {
                var cached = await cache.GetAsync(queryCacheKey, ct);
                if (cached is not null)
                {
                    var cachedResults = JsonSerializer.Deserialize<List<RetrievedChunkDto>>(cached, JsonOptions)!;
                    activity?.SetTag("rag.result_count", cachedResults.Count);
                    activity?.SetTag("rag.cache_hit", true);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return new QueryResponse { RequestId = request.RequestId, Results = cachedResults, Success = true };
                }
            }

            var embedding = await embeddingProvider.EmbedAsync(request.Query, ct);
            if (embedding.Length != ragOptions.VectorSize)
            {
                return Failure(
                    request,
                    $"Embedding vector size {embedding.Length} does not match configured Rag:VectorSize {ragOptions.VectorSize}.");
            }

            var results = await vectorStore.SearchAsync(request.Collection, embedding, topK, request.Filters, ct);
            var resultDtos = results
                .Select(result => new RetrievedChunkDto
                {
                    DocumentId = result.DocumentId,
                    ChunkId = result.ChunkId,
                    Text = result.Text,
                    Score = result.Score,
                    Metadata = result.Metadata
                })
                .ToList();

            if (queryCacheKey is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(resultDtos, JsonOptions);
                await cache.SetAsync(queryCacheKey, bytes, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cacheOpts.TtlMinutes)
                }, ct);
            }

            activity?.SetTag("rag.result_count", resultDtos.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            RagTelemetry.QueriesExecuted.Add(1, new TagList { { "collection", request.Collection } });

            return new QueryResponse
            {
                RequestId = request.RequestId,
                Results = resultDtos,
                Success = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to search collection {Collection} for request {RequestId}",
                request.Collection,
                request.RequestId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RagTelemetry.QueryErrors.Add(1, new TagList { { "collection", request.Collection } });
            return Failure(request, ex.Message);
        }
    }

    private static string BuildQueryCacheKey(QueryRequest request, int topK)
    {
        var filterPart = request.Filters is null || request.Filters.Count == 0
            ? string.Empty
            : string.Join("|", request.Filters.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var raw = $"{request.Collection}|{topK}|{filterPart}|{request.Query}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"rag:query:{Convert.ToHexString(hash)}";
    }

    private static void ValidateRequest(QueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new InvalidOperationException("RequestId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Collection))
        {
            throw new InvalidOperationException("Collection is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new InvalidOperationException("Query is required.");
        }
    }

    private static bool IsCompatible(CollectionMetadata current, CollectionMetadata expected)
    {
        return string.Equals(current.EmbeddingProvider, expected.EmbeddingProvider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.EmbeddingModel, expected.EmbeddingModel, StringComparison.Ordinal)
            && current.VectorSize == expected.VectorSize;
    }

    private static QueryResponse Failure(QueryRequest request, string error)
    {
        return new QueryResponse
        {
            RequestId = request.RequestId,
            Results = [],
            Success = false,
            ErrorMessage = error
        };
    }
}
