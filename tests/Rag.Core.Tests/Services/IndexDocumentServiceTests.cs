using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Services;

namespace Rag.Core.Tests.Services;

public sealed class IndexDocumentServiceTests
{
    private const string DefaultCollection = "my-collection";
    private const string DefaultModel = "text-embedding-3-small";
    private const string DefaultProvider = "OpenAI";
    private const int DefaultVectorSize = 1536;

    private static RagOptions DefaultRagOptions => new()
    {
        EmbeddingProvider = DefaultProvider,
        EmbeddingModel = DefaultModel,
        VectorSize = DefaultVectorSize
    };

    private static CollectionMetadata CompatibleMetadata => new()
    {
        Collection = DefaultCollection,
        EmbeddingProvider = DefaultProvider,
        EmbeddingModel = DefaultModel,
        VectorSize = DefaultVectorSize
    };

    private static IndexDocumentRequest ValidRequest => new()
    {
        RequestId = "req-1",
        Collection = DefaultCollection,
        DocumentId = "doc-1",
        FilePath = "path/to/file.txt",
        Metadata = new Dictionary<string, string> { ["type"] = "test" }
    };

    private static IndexDocumentService Build(
        ITextExtractor? textExtractor = null,
        IChunker? chunker = null,
        IEmbeddingProvider? embeddingProvider = null,
        IVectorStore? vectorStore = null,
        RagOptions? ragOptions = null)
    {
        return new IndexDocumentService(
            textExtractor ?? Substitute.For<ITextExtractor>(),
            chunker ?? Substitute.For<IChunker>(),
            embeddingProvider ?? Substitute.For<IEmbeddingProvider>(),
            vectorStore ?? Substitute.For<IVectorStore>(),
            Microsoft.Extensions.Options.Options.Create(ragOptions ?? DefaultRagOptions),
            NullLogger<IndexDocumentService>.Instance);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "col", "doc", "path")]
    [InlineData("req", "", "doc", "path")]
    [InlineData("req", "col", "", "path")]
    [InlineData("req", "col", "doc", "")]
    public async Task IndexAsync_MissingRequiredField_ReturnsFailure(
        string requestId, string collection, string documentId, string filePath)
    {
        var sut = Build();

        var result = await sut.IndexAsync(
            new IndexDocumentRequest 
            { 
                RequestId = requestId, 
                Collection = collection, 
                DocumentId = documentId, 
                FilePath = filePath 
            }, 
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Text Extraction ──────────────────────────────────────────────────────

    [Fact]
    public async Task IndexAsync_EmptyText_ReturnsFailure()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(""));

        var sut = Build(textExtractor: textExtractor);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No text could be extracted", result.ErrorMessage);
    }

    // ── Chunking ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task IndexAsync_NoChunks_ReturnsFailure()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult("Some text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([]));

        var sut = Build(textExtractor, chunker);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No chunks were produced", result.ErrorMessage);
    }

    // ── Embedding ────────────────────────────────────────────────────────────

    [Fact]
    public async Task IndexAsync_EmbeddingCountMismatch_ReturnsFailure()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult("Some text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([new DocumentChunk { Text = "chunk1" }]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<float[]>>([])); // Returning 0 embeddings for 1 chunk

        var sut = Build(textExtractor, chunker, embeddingProvider);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("different number of embeddings", result.ErrorMessage);
    }

    [Fact]
    public async Task IndexAsync_EmbeddingSizeMismatch_ReturnsFailure()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult("Some text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([new DocumentChunk { Text = "chunk1" }]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<float[]>>([new float[512]])); // Returning 512, expected 1536

        var sut = Build(textExtractor, chunker, embeddingProvider);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("does not match configured", result.ErrorMessage);
    }

    // ── Collection Metadata ──────────────────────────────────────────────────

    [Theory]
    [InlineData("WrongProvider", DefaultModel, DefaultVectorSize)]
    [InlineData(DefaultProvider, "wrong-model", DefaultVectorSize)]
    [InlineData(DefaultProvider, DefaultModel, 512)]
    public async Task IndexAsync_IncompatibleMetadata_ReturnsFailure(string provider, string model, int vectorSize)
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult("Some text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([new DocumentChunk { Text = "chunk1" }]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<float[]>>([new float[DefaultVectorSize]]));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(new CollectionMetadata
                   {
                       Collection = DefaultCollection,
                       EmbeddingProvider = provider,
                       EmbeddingModel = model,
                       VectorSize = vectorSize
                   }));

        var sut = Build(textExtractor, chunker, embeddingProvider, vectorStore);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("current configuration is", result.ErrorMessage);
    }

    [Fact]
    public async Task IndexAsync_NoExistingMetadata_SetsMetadata()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([new DocumentChunk { Text = "chunk1" }]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<float[]>>([new float[DefaultVectorSize]]));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(null));

        var sut = Build(textExtractor, chunker, embeddingProvider, vectorStore);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.True(result.Success);
        await vectorStore.Received(1).SetCollectionMetadataAsync(
            Arg.Is<CollectionMetadata>(m => m.Collection == DefaultCollection && m.EmbeddingProvider == DefaultProvider), 
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IndexAsync_CompatibleMetadata_DoesNotOverwriteMetadata()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([new DocumentChunk { Text = "chunk1" }]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<float[]>>([new float[DefaultVectorSize]]));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));

        var sut = Build(textExtractor, chunker, embeddingProvider, vectorStore);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.True(result.Success);
        await vectorStore.DidNotReceive().SetCollectionMetadataAsync(Arg.Any<CollectionMetadata>(), Arg.Any<CancellationToken>());
    }

    // ── Happy Path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task IndexAsync_HappyPath_ReturnsSuccessWithChunkCount()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([
                   new DocumentChunk { Text = "chunk1" }, 
                   new DocumentChunk { Text = "chunk2" }
               ]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<float[]>>([
                             new float[DefaultVectorSize], 
                             new float[DefaultVectorSize]
                         ]));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));

        var sut = Build(textExtractor, chunker, embeddingProvider, vectorStore);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.ChunkCount);
        Assert.Equal(ValidRequest.RequestId, result.RequestId);
        Assert.Equal(ValidRequest.DocumentId, result.DocumentId);
    }

    [Fact]
    public async Task IndexAsync_HappyPath_DeletesOldBeforeUpsert()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([new DocumentChunk { Text = "chunk1" }]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<float[]>>([new float[DefaultVectorSize]]));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));

        var sut = Build(textExtractor, chunker, embeddingProvider, vectorStore);

        await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Received.InOrder(() =>
        {
            vectorStore.DeleteByDocumentIdAsync(DefaultCollection, ValidRequest.DocumentId, Arg.Any<CancellationToken>());
            vectorStore.UpsertAsync(DefaultCollection, Arg.Any<IReadOnlyList<VectorDocument>>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task IndexAsync_HappyPath_EnsuresCollection()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([new DocumentChunk { Text = "chunk1" }]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult<IReadOnlyList<float[]>>([new float[DefaultVectorSize]]));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));

        var sut = Build(textExtractor, chunker, embeddingProvider, vectorStore);

        await sut.IndexAsync(ValidRequest, CancellationToken.None);

        await vectorStore.Received(1).EnsureCollectionAsync(DefaultCollection, DefaultVectorSize, Arg.Any<CancellationToken>());
    }

    // ── Error Handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task IndexAsync_ExceptionFromEmbedding_ReturnsFailure()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("text"));

        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<DocumentChunk>>([new DocumentChunk { Text = "chunk1" }]));

        var embeddingProvider = Substitute.For<IEmbeddingProvider>();
        embeddingProvider.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                         .Returns<Task<IReadOnlyList<float[]>>>(_ => throw new Exception("API is down"));

        var sut = Build(textExtractor, chunker, embeddingProvider);

        var result = await sut.IndexAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("API is down", result.ErrorMessage);
    }

    [Fact]
    public async Task IndexAsync_OperationCancelled_Rethrows()
    {
        var textExtractor = Substitute.For<ITextExtractor>();
        textExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns<Task<string>>(_ => throw new OperationCanceledException());

        var sut = Build(textExtractor);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.IndexAsync(ValidRequest, CancellationToken.None));
    }
}
