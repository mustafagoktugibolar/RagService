using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Reranking;
using Rag.Core.Retrieval;
using Rag.Core.Rewriting;
using Rag.Core.Telemetry;

namespace Rag.Core.Services;

public sealed class VectorSearchService(
    IEmbeddingProvider embeddingProvider,
    IVectorStore vectorStore,
    IDistributedCache cache,
    IOptions<RagOptions> options,
    IOptions<QueryCacheOptions> queryCacheOptions,
    ILogger<VectorSearchService> logger,
    IOptions<RetrievalOptions>? retrievalOptions = null,
    IReranker? reranker = null,
    QueryRewritingStage? rewriting = null)
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
            var retrieval = retrievalOptions?.Value ?? new RetrievalOptions();
            var plan = RetrievalPlan.Resolve(retrieval, StoreCapabilities.Of(vectorStore));
            if (plan.Error is not null)
            {
                return Failure(request, plan.Error);
            }

            var rerank = retrieval.Reranking;
            var rerankActive = reranker is not null && rerank.Enabled;
            // The reranker's identity and candidate budget change the result list, so they are part of the strategy id
            // that keys the query cache.
            var strategyId = rerankActive
                ? $"{plan.StrategyId}+{reranker!.Name}:budget{rerank.CandidateBudget}"
                : plan.StrategyId;
            // Rewriting changes what is searched, so the rewriter's identity keys the cache too. The key still uses the ORIGINAL query:
            // a cache hit answers without a rewrite call.
            var rewriteActive = rewriting is not null && retrieval.QueryRewriting.Enabled;
            if (rewriteActive)
            {
                strategyId += $"+rw:{rewriting!.Name}";
            }

            activity?.SetTag("rag.strategy", strategyId);

            string? queryCacheKey = cacheOpts.Enabled
                ? QueryCacheKeyBuilder.Build(new RetrievalFingerprint
                {
                    Collection = request.Collection,
                    IndexVersion = metadata.IndexVersion,
                    EmbeddingProvider = expected.EmbeddingProvider,
                    EmbeddingModel = expected.EmbeddingModel,
                    VectorSize = expected.VectorSize,
                    Strategy = strategyId,
                    TopK = topK,
                    Query = request.Query,
                    Filters = request.Filters
                })
                : null;

            if (queryCacheKey is not null)
            {
                var cached = await cache.GetAsync(queryCacheKey, ct);
                if (cached is not null)
                {
                    var cachedResults = JsonSerializer.Deserialize<List<RetrievedChunkDto>>(cached, JsonOptions)!;
                    activity?.SetTag("rag.result_count", cachedResults.Count);
                    activity?.SetTag("rag.cache_hit", true);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    RagTelemetry.QueriesExecuted.Add(1, new TagList { { "collection", request.Collection }, { "cache_hit", true } });
                    return new QueryResponse { RequestId = request.RequestId, Results = cachedResults, Success = true };
                }
            }

            var searchQuery = request.Query;
            var rewriteWarnings = new List<string>();
            if (rewriteActive)
            {
                var transformation = await rewriting!.TransformAsync(request.Query, ct);
                searchQuery = transformation.Effective;
                activity?.SetTag("rag.rewrite.outcome", transformation.Outcome.ToString());
                activity?.SetTag("rag.rewrite.query_hash", QueryRewritingStage.Fingerprint(request.Query));
                if (transformation.Outcome == RewriteOutcome.Rewritten)
                {
                    activity?.SetTag("rag.rewrite.rewritten_hash", QueryRewritingStage.Fingerprint(searchQuery));
                    if (retrieval.QueryRewriting.RecordQueryText)
                    {
                        activity?.SetTag("rag.rewrite.original", request.Query);
                        activity?.SetTag("rag.rewrite.rewritten", searchQuery);
                    }
                }

                if (transformation.Warning is not null)
                {
                    rewriteWarnings.Add(transformation.Warning);
                }
            }

            float[]? embedding = null;
            if (plan.Strategy != RetrievalStrategy.Lexical)
            {
                embedding = await embeddingProvider.EmbedAsync(searchQuery, ct);
                if (embedding.Length != ragOptions.VectorSize)
                {
                    return Failure(
                        request,
                        $"Embedding vector size {embedding.Length} does not match configured Rag:VectorSize {ragOptions.VectorSize}.");
                }
            }

            IRetriever retriever = CreateRetriever(plan.Strategy, retrieval);
            if (rerankActive)
            {
                retriever = new RerankingRetriever(retriever, reranker!, rerank, logger);
            }

            var outcome = await retriever
                .RetrieveAsync(new RetrievalRequest(request.Collection, searchQuery, embedding, topK, request.Filters, request.Query), ct);

            var warnings = plan.Warnings.Concat(rewriteWarnings).Concat(outcome.Warnings).ToList();
            foreach (var warning in warnings)
            {
                logger.LogWarning("Query {RequestId} on {Collection} degraded: {Warning}", request.RequestId, request.Collection, warning);
            }

            var includeSources = plan.Strategy != RetrievalStrategy.Dense || rerankActive;
            var resultDtos = outcome.Results
                .Select(fused => new RetrievedChunkDto
                {
                    DocumentId = fused.Result.DocumentId,
                    ChunkId = fused.Result.ChunkId,
                    Text = fused.Result.Text,
                    Score = fused.Score,
                    Metadata = fused.Result.Metadata,
                    Sources = includeSources
                        ? fused.Sources.Select(s => new ChunkSourceDto(s.Source, s.Rank, s.OriginalScore)).ToList()
                        : null
                })
                .ToList();

            // A result produced by a failed hybrid leg is worse than normal; never pin it in the cache.
            if (queryCacheKey is not null && outcome.Warnings.Count == 0 && rewriteWarnings.Count == 0)
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
                Success = true,
                Warnings = warnings.Count == 0 ? null : warnings
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

    private IRetriever CreateRetriever(RetrievalStrategy strategy, RetrievalOptions retrieval) => strategy switch
    {
        RetrievalStrategy.Dense => new DenseRetriever(vectorStore),
        RetrievalStrategy.Lexical => new LexicalRetriever((ILexicalSearchStore)vectorStore),
        RetrievalStrategy.Hybrid => new HybridRetriever(
            vectorStore,
            (ILexicalSearchStore)vectorStore,
            new ReciprocalRankFusion(retrieval.RrfK),
            retrieval,
            logger),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null)
    };

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

        FilterValidator.Validate(request.Filters);
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
