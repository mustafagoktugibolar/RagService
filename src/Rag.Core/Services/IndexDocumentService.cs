using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Telemetry;

namespace Rag.Core.Services;

public sealed class IndexDocumentService(
    ITextExtractor textExtractor,
    IChunker chunker,
    IEmbeddingProvider embeddingProvider,
    IVectorStore vectorStore,
    IOptions<RagOptions> options,
    ILogger<IndexDocumentService> logger)
{
    public async Task<IndexDocumentResponse> IndexAsync(IndexDocumentRequest request, CancellationToken ct)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.index");
        activity?.SetTag("rag.collection", request.Collection);
        activity?.SetTag("rag.document_id", request.DocumentId);

        try
        {
            ValidateRequest(request);

            logger.LogInformation(
                "Indexing document {DocumentId} into collection {Collection}",
                request.DocumentId,
                request.Collection);

            var text = await textExtractor.ExtractAsync(request.FilePath, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Failure(request, "No text could be extracted from the document.");
            }

            var chunks = await chunker.ChunkAsync(request.DocumentId, text, request.Metadata, ct);
            if (chunks.Count == 0)
            {
                return Failure(request, "No chunks were produced from the document text.");
            }

            var embeddings = await embeddingProvider.EmbedBatchAsync(chunks.Select(chunk => chunk.Text).ToArray(), ct);
            if (embeddings.Count != chunks.Count)
            {
                return Failure(request, "Embedding provider returned a different number of embeddings than requested.");
            }

            var ragOptions = options.Value;
            ValidateEmbeddingSize(embeddings, ragOptions.VectorSize);

            var expectedMetadata = CreateCollectionMetadata(request.Collection, ragOptions);
            var currentMetadata = await vectorStore.GetCollectionMetadataAsync(request.Collection, ct);
            if (currentMetadata is not null && !IsCompatible(currentMetadata, expectedMetadata))
            {
                return Failure(
                    request,
                    $"Collection '{request.Collection}' was indexed with {currentMetadata.EmbeddingProvider}/{currentMetadata.EmbeddingModel}/{currentMetadata.VectorSize}, but current configuration is {expectedMetadata.EmbeddingProvider}/{expectedMetadata.EmbeddingModel}/{expectedMetadata.VectorSize}.");
            }

            await vectorStore.EnsureCollectionAsync(request.Collection, ragOptions.VectorSize, ct);
            if (currentMetadata is null)
            {
                await vectorStore.SetCollectionMetadataAsync(expectedMetadata, ct);
            }

            var vectorDocuments = chunks
                .Select((chunk, index) => new VectorDocument
                {
                    DocumentId = chunk.DocumentId,
                    ChunkId = chunk.ChunkId,
                    Text = chunk.Text,
                    Metadata = chunk.Metadata,
                    Embedding = embeddings[index]
                })
                .ToArray();

            await vectorStore.DeleteByDocumentIdAsync(request.Collection, request.DocumentId, ct);
            await vectorStore.UpsertAsync(request.Collection, vectorDocuments, ct);

            logger.LogInformation(
                "Indexed document {DocumentId} into collection {Collection} with {ChunkCount} chunks",
                request.DocumentId,
                request.Collection,
                chunks.Count);

            activity?.SetTag("rag.chunk_count", chunks.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            RagTelemetry.DocumentsIndexed.Add(1, new TagList { { "collection", request.Collection } });

            return new IndexDocumentResponse
            {
                RequestId = request.RequestId,
                DocumentId = request.DocumentId,
                ChunkCount = chunks.Count,
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
                "Failed to index document {DocumentId} into collection {Collection}",
                request.DocumentId,
                request.Collection);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RagTelemetry.IndexErrors.Add(1, new TagList { { "collection", request.Collection } });
            return Failure(request, ex.Message);
        }
    }

    private static void ValidateRequest(IndexDocumentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            throw new InvalidOperationException("RequestId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Collection))
        {
            throw new InvalidOperationException("Collection is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DocumentId))
        {
            throw new InvalidOperationException("DocumentId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            throw new InvalidOperationException("FilePath is required.");
        }
    }

    private static void ValidateEmbeddingSize(IReadOnlyList<float[]> embeddings, int vectorSize)
    {
        if (vectorSize <= 0)
        {
            throw new InvalidOperationException("Rag:VectorSize must be greater than zero.");
        }

        var invalid = embeddings.FirstOrDefault(embedding => embedding.Length != vectorSize);
        if (invalid is not null)
        {
            throw new InvalidOperationException(
                $"Embedding vector size {invalid.Length} does not match configured Rag:VectorSize {vectorSize}.");
        }
    }

    private static CollectionMetadata CreateCollectionMetadata(string collection, RagOptions options)
    {
        return new CollectionMetadata
        {
            Collection = collection,
            EmbeddingProvider = options.EmbeddingProvider,
            EmbeddingModel = options.EmbeddingModel,
            VectorSize = options.VectorSize
        };
    }

    private static bool IsCompatible(CollectionMetadata current, CollectionMetadata expected)
    {
        return string.Equals(current.EmbeddingProvider, expected.EmbeddingProvider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.EmbeddingModel, expected.EmbeddingModel, StringComparison.Ordinal)
            && current.VectorSize == expected.VectorSize;
    }

    private static IndexDocumentResponse Failure(IndexDocumentRequest request, string error)
    {
        return new IndexDocumentResponse
        {
            RequestId = request.RequestId,
            DocumentId = request.DocumentId,
            Success = false,
            ErrorMessage = error
        };
    }
}
