using System.Diagnostics;
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
    IOptions<RagOptions> options,
    ILogger<VectorSearchService> logger)
{
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

            var embedding = await embeddingProvider.EmbedAsync(request.Query, ct);
            if (embedding.Length != ragOptions.VectorSize)
            {
                return Failure(
                    request,
                    $"Embedding vector size {embedding.Length} does not match configured Rag:VectorSize {ragOptions.VectorSize}.");
            }

            var fetchK = request.Filters is null || request.Filters.Count == 0
                ? topK
                : Math.Min(Math.Max(topK * 10, topK), 1000);

            var results = await vectorStore.SearchAsync(request.Collection, embedding, fetchK, ct);
            var filtered = ApplyFilters(results, request.Filters)
                .Take(topK)
                .Select(result => new RetrievedChunkDto
                {
                    DocumentId = result.DocumentId,
                    ChunkId = result.ChunkId,
                    Text = result.Text,
                    Score = result.Score,
                    Metadata = result.Metadata
                })
                .ToList();

            activity?.SetTag("rag.result_count", filtered.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            RagTelemetry.QueriesExecuted.Add(1, new TagList { { "collection", request.Collection } });

            return new QueryResponse
            {
                RequestId = request.RequestId,
                Results = filtered,
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

    private static IEnumerable<VectorSearchResult> ApplyFilters(
        IEnumerable<VectorSearchResult> results,
        Dictionary<string, string>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return results;
        }

        return results.Where(result =>
            result.Metadata is not null
            && filters.All(filter =>
                result.Metadata.TryGetValue(filter.Key, out var value)
                && string.Equals(value, filter.Value, StringComparison.Ordinal)));
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
