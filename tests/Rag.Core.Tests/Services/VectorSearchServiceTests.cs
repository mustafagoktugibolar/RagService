using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Services;

namespace Rag.Core.Tests.Services;

public sealed class VectorSearchServiceTests
{
    private const string DefaultCollection = "my-collection";
    private const string DefaultModel = "text-embedding-3-small";
    private const string DefaultProvider = "OpenAI";
    private const int DefaultVectorSize = 1536;

    private static readonly float[] DefaultEmbedding = Enumerable.Repeat(0.1f, DefaultVectorSize).ToArray();

    private static RagOptions DefaultRagOptions => new()
    {
        EmbeddingProvider = DefaultProvider,
        EmbeddingModel = DefaultModel,
        VectorSize = DefaultVectorSize,
        DefaultTopK = 5
    };

    private static CollectionMetadata CompatibleMetadata => new()
    {
        Collection = DefaultCollection,
        EmbeddingProvider = DefaultProvider,
        EmbeddingModel = DefaultModel,
        VectorSize = DefaultVectorSize
    };

    private static QueryRequest ValidRequest => new()
    {
        RequestId = "req-1",
        Collection = DefaultCollection,
        Query = "What is AI?",
        TopK = 3
    };

    private static IDistributedCache BuildCache() =>
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    private static VectorSearchService Build(
        IEmbeddingProvider? embedding = null,
        IVectorStore? vectorStore = null,
        IDistributedCache? cache = null,
        RagOptions? ragOptions = null,
        bool queryCacheEnabled = false) =>
        new(
            embedding ?? Substitute.For<IEmbeddingProvider>(),
            vectorStore ?? Substitute.For<IVectorStore>(),
            cache ?? BuildCache(),
            Microsoft.Extensions.Options.Options.Create(ragOptions ?? DefaultRagOptions),
            Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions { Enabled = queryCacheEnabled, TtlMinutes = 1 }),
            NullLogger<VectorSearchService>.Instance);

    // ── Validation ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "col", "query")]
    [InlineData("req", "", "query")]
    [InlineData("req", "col", "")]
    public async Task SearchAsync_MissingRequiredField_ReturnsFailure(
        string requestId, string collection, string query)
    {
        var sut = Build();

        var result = await sut.SearchAsync(
            new QueryRequest { RequestId = requestId, Collection = collection, Query = query },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    // ── Collection metadata ──────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_CollectionNotIndexed_ReturnsFailure()
    {
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(null));
        var sut = Build(vectorStore: vectorStore);

        var result = await sut.SearchAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(DefaultCollection, result.ErrorMessage);
    }

    [Theory]
    [InlineData("WrongProvider", DefaultModel, DefaultVectorSize)]
    [InlineData(DefaultProvider, "wrong-model", DefaultVectorSize)]
    [InlineData(DefaultProvider, DefaultModel, 512)]
    public async Task SearchAsync_IncompatibleMetadata_ReturnsFailure(
        string provider, string model, int vectorSize)
    {
        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(new CollectionMetadata
                   {
                       Collection = DefaultCollection,
                       EmbeddingProvider = provider,
                       EmbeddingModel = model,
                       VectorSize = vectorSize
                   }));
        var sut = Build(vectorStore: vectorStore);

        var result = await sut.SearchAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SearchAsync_EmbeddingSizeMismatch_ReturnsFailure()
    {
        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new float[512]));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));

        var sut = Build(embedding, vectorStore);

        var result = await sut.SearchAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("512", result.ErrorMessage);
        Assert.Contains(DefaultVectorSize.ToString(), result.ErrorMessage);
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_HappyPath_ReturnsMappedResults()
    {
        var searchResult = new VectorSearchResult
        {
            DocumentId = "doc1",
            ChunkId = "chunk1",
            Text = "result text",
            Score = 0.9,
            Metadata = new Dictionary<string, string> { ["lang"] = "en" }
        };

        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(DefaultCollection, DefaultEmbedding, 3, null, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>([searchResult]));

        var sut = Build(embedding, vectorStore);

        var result = await sut.SearchAsync(ValidRequest, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("req-1", result.RequestId);
        var dto = Assert.Single(result.Results);
        Assert.Equal("doc1", dto.DocumentId);
        Assert.Equal("chunk1", dto.ChunkId);
        Assert.Equal("result text", dto.Text);
        Assert.Equal(0.9, dto.Score);
        Assert.Equal("en", dto.Metadata!["lang"]);
    }

    [Fact]
    public async Task SearchAsync_TopKZero_UsesDefaultTopK()
    {
        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
                                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>([]));

        var ops = DefaultRagOptions;
        ops.DefaultTopK = 7;
        var sut = Build(embedding, vectorStore, ragOptions: ops);

        await sut.SearchAsync(ValidRequest with { TopK = 0 }, CancellationToken.None);

        await vectorStore.Received(1).SearchAsync(
            Arg.Any<string>(), Arg.Any<float[]>(),
            7,
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_FiltersPassedToVectorStore()
    {
        var filters = new Dictionary<string, string> { ["lang"] = "en", ["type"] = "article" };

        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
                                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>([]));

        var sut = Build(embedding, vectorStore);

        await sut.SearchAsync(ValidRequest with { Filters = filters }, CancellationToken.None);

        await vectorStore.Received(1).SearchAsync(
            Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
            Arg.Is<Dictionary<string, string>?>(f =>
                f != null && f["lang"] == "en" && f["type"] == "article"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_ExceptionFromVectorStore_ReturnsFailure()
    {
        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(DefaultCollection, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
                                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
                   .Returns<Task<IReadOnlyList<VectorSearchResult>>>(
                       _ => throw new InvalidOperationException("store down"));

        var sut = Build(embedding, vectorStore);

        var result = await sut.SearchAsync(ValidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("store down", result.ErrorMessage);
    }

    // ── Query cache ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_QueryCacheDisabled_NeverTouchesCache()
    {
        var cache = Substitute.For<IDistributedCache>();
        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
                                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>([]));

        var sut = Build(embedding, vectorStore, cache, queryCacheEnabled: false);

        await sut.SearchAsync(ValidRequest, CancellationToken.None);

        await cache.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(),
            Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_QueryCacheEnabled_CacheMiss_CallsEmbeddingAndStore()
    {
        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
                                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                   [new VectorSearchResult { DocumentId = "d1", ChunkId = "c1", Text = "t", Score = 0.8 }]));

        var sut = Build(embedding, vectorStore, queryCacheEnabled: true);

        var result = await sut.SearchAsync(ValidRequest, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Results);
        await embedding.Received(1).EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await vectorStore.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_QueryCacheEnabled_CacheHit_SkipsEmbeddingAndStore()
    {
        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
                                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>(
                   [new VectorSearchResult { DocumentId = "d1", ChunkId = "c1", Text = "t", Score = 0.8 }]));

        var sut = Build(embedding, vectorStore, queryCacheEnabled: true);

        await sut.SearchAsync(ValidRequest, CancellationToken.None);
        var result = await sut.SearchAsync(ValidRequest, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Results);
        await embedding.Received(1).EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await vectorStore.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_QueryCacheEnabled_DifferentFilters_DifferentCacheEntries()
    {
        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
                                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>([]));

        var sut = Build(embedding, vectorStore, queryCacheEnabled: true);

        await sut.SearchAsync(ValidRequest with { Filters = new Dictionary<string, string> { ["lang"] = "en" } }, CancellationToken.None);
        await sut.SearchAsync(ValidRequest with { Filters = new Dictionary<string, string> { ["lang"] = "tr" } }, CancellationToken.None);

        await vectorStore.Received(2).SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_QueryCacheEnabled_FilterOrderDoesNotAffectCacheKey()
    {
        var embedding = Substitute.For<IEmbeddingProvider>();
        embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(DefaultEmbedding));

        var vectorStore = Substitute.For<IVectorStore>();
        vectorStore.GetCollectionMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<CollectionMetadata?>(CompatibleMetadata));
        vectorStore.SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
                                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>([]));

        var sut = Build(embedding, vectorStore, queryCacheEnabled: true);

        await sut.SearchAsync(ValidRequest with
        {
            Filters = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }
        }, CancellationToken.None);

        await sut.SearchAsync(ValidRequest with
        {
            Filters = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" }
        }, CancellationToken.None);

        // Same logical filter, different insertion order → same cache key → store called once
        await vectorStore.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<float[]>(), Arg.Any<int>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }
}
