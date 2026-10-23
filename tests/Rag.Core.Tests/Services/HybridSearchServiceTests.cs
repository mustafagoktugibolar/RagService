using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Retrieval;
using Rag.Core.Services;
using Rag.Core.Tests.Fakes;

namespace Rag.Core.Tests.Services;

public sealed class HybridSearchServiceTests
{
    private const string Collection = "docs";
    private const int Dim = 2;

    private static readonly RagOptions Rag = new() { EmbeddingProvider = "OpenAI", EmbeddingModel = "m", VectorSize = Dim, DefaultTopK = 5 };

    private static IDistributedCache NewCache() =>
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    private static async Task<T> Seeded<T>(T store) where T : InMemoryVectorStore
    {
        await store.EnsureCollectionAsync(Collection, Dim, CancellationToken.None);
        await store.SetCollectionMetadataAsync(new CollectionMetadata
        {
            Collection = Collection, EmbeddingProvider = "OpenAI", EmbeddingModel = "m", VectorSize = Dim, IndexVersion = "v1"
        }, CancellationToken.None);
        return store;
    }

    private static Task Add(InMemoryVectorStore store, string chunk, string text, Dictionary<string, string>? metadata = null) =>
        store.UpsertAsync(Collection,
            [new VectorDocument { DocumentId = chunk, ChunkId = chunk, Text = text, Embedding = new float[Dim], Metadata = metadata }],
            CancellationToken.None);

    private static VectorSearchService Service(
        IVectorStore store, RetrievalOptions retrieval, IDistributedCache? cache = null, bool cacheEnabled = false)
    {
        var embedder = Substitute.For<IEmbeddingProvider>();
        embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1f, 0f });
        return new VectorSearchService(
            embedder, store, cache ?? NewCache(),
            Microsoft.Extensions.Options.Options.Create(Rag),
            Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions { Enabled = cacheEnabled, TtlMinutes = 5 }),
            NullLogger<VectorSearchService>.Instance,
            Microsoft.Extensions.Options.Options.Create(retrieval));
    }

    private static QueryRequest Query(string text = "alpha", Dictionary<string, string>? filters = null) =>
        new() { RequestId = "q", Collection = Collection, Query = text, TopK = 5, Filters = filters };

    [Fact]
    public async Task DefaultStrategy_IsDense_AndTheLegacyPayloadShapeIsUnchanged()
    {
        var store = await Seeded(new LexicalInMemoryVectorStore());
        await Add(store, "a", "alpha beta");

        var response = await Service(store, new RetrievalOptions()).SearchAsync(Query(), CancellationToken.None);

        Assert.True(response.Success);
        Assert.Null(response.Warnings);
        Assert.Null(response.Results.Single().Sources);
        Assert.Equal(0, store.LexicalCalls);
    }

    [Fact]
    public async Task Hybrid_OnAStoreWithLexicalSearch_FusesBothAndAttributesSources()
    {
        var store = await Seeded(new LexicalInMemoryVectorStore());
        await Add(store, "dense-only", "unrelated words");        // dense returns everything (fake), lexical does not match it
        await Add(store, "both", "alpha keyword here");

        var response = await Service(store, new RetrievalOptions { Strategy = RetrievalStrategy.Hybrid })
            .SearchAsync(Query("alpha"), CancellationToken.None);

        var both = response.Results.Single(r => r.ChunkId == "both");
        Assert.Equal("both", response.Results[0].ChunkId);   // found by both legs → ranked first
        Assert.Equal(["dense", "lexical"], both.Sources!.Select(s => s.Source));
        Assert.Equal(["dense"], response.Results.Single(r => r.ChunkId == "dense-only").Sources!.Select(s => s.Source));
    }

    [Fact]
    public async Task Hybrid_OnAStoreWithoutLexicalSearch_FallsBackToDenseAndWarns()
    {
        var store = await Seeded(new InMemoryVectorStore());   // dense only
        await Add(store, "a", "alpha");

        var response = await Service(store, new RetrievalOptions { Strategy = RetrievalStrategy.Hybrid })
            .SearchAsync(Query(), CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal([RetrievalPlan.HybridFellBackToDense], response.Warnings);
        Assert.Equal("a", response.Results.Single().ChunkId);
    }

    [Fact]
    public async Task Lexical_OnAStoreWithoutLexicalSearch_FailsWithAClearMessage()
    {
        var store = await Seeded(new InMemoryVectorStore());

        var response = await Service(store, new RetrievalOptions { Strategy = RetrievalStrategy.Lexical })
            .SearchAsync(Query(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("not supported by the configured vector store", response.ErrorMessage);
    }

    [Fact]
    public async Task Lexical_SkipsTheEmbeddingCall()
    {
        var store = await Seeded(new LexicalInMemoryVectorStore());
        await Add(store, "a", "alpha");
        var embedder = Substitute.For<IEmbeddingProvider>();
        var service = new VectorSearchService(
            embedder, store, NewCache(),
            Microsoft.Extensions.Options.Options.Create(Rag),
            Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions()),
            NullLogger<VectorSearchService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new RetrievalOptions { Strategy = RetrievalStrategy.Lexical }));

        var response = await service.SearchAsync(Query(), CancellationToken.None);

        Assert.True(response.Success);
        await embedder.DidNotReceiveWithAnyArgs().EmbedAsync(default!, default);
    }

    [Fact]
    public async Task CacheEntries_AreNotSharedBetweenStrategies()
    {
        var cache = NewCache();
        var store = await Seeded(new LexicalInMemoryVectorStore());
        await Add(store, "dense-only", "unrelated words");
        await Add(store, "both", "alpha keyword");

        var dense = await Service(store, new RetrievalOptions(), cache, cacheEnabled: true).SearchAsync(Query(), CancellationToken.None);
        var hybrid = await Service(store, new RetrievalOptions { Strategy = RetrievalStrategy.Hybrid }, cache, cacheEnabled: true)
            .SearchAsync(Query(), CancellationToken.None);

        Assert.Null(dense.Results[0].Sources);          // dense payload
        Assert.NotNull(hybrid.Results[0].Sources);      // not the cached dense payload
    }

    [Fact]
    public async Task CacheEntries_AreNotSharedBetweenFusionParameters()
    {
        var cache = NewCache();
        var store = await Seeded(new LexicalInMemoryVectorStore());
        await Add(store, "a", "alpha");

        var k60 = await Service(store, new RetrievalOptions { Strategy = RetrievalStrategy.Hybrid, RrfK = 60 }, cache, true)
            .SearchAsync(Query(), CancellationToken.None);
        var k10 = await Service(store, new RetrievalOptions { Strategy = RetrievalStrategy.Hybrid, RrfK = 10 }, cache, true)
            .SearchAsync(Query(), CancellationToken.None);

        Assert.NotEqual(k60.Results[0].Score, k10.Results[0].Score);   // k=60 → 2/61, k=10 → 2/11
    }

    [Fact]
    public async Task DegradedHybridResults_AreNeverCached()
    {
        var cache = NewCache();
        var store = await Seeded(new LexicalInMemoryVectorStore { LexicalFailure = new IOException("lexical down") });
        await Add(store, "a", "alpha");
        var service = Service(store, new RetrievalOptions { Strategy = RetrievalStrategy.Hybrid }, cache, true);

        var degraded = await service.SearchAsync(Query(), CancellationToken.None);
        store.LexicalFailure = null;
        var recovered = await service.SearchAsync(Query(), CancellationToken.None);

        Assert.Equal([HybridRetriever.LexicalFailed], degraded.Warnings);
        Assert.Null(recovered.Warnings);                       // would be a stale degraded hit if it had been cached
        Assert.Equal(2, recovered.Results.Single().Sources!.Count);
    }

    [Fact]
    public async Task Filters_AreAppliedIdenticallyByEveryStrategy()
    {
        var store = await Seeded(new LexicalInMemoryVectorStore());
        await Add(store, "en", "alpha text", new() { ["lang"] = "en" });
        await Add(store, "tr", "alpha metin", new() { ["lang"] = "tr" });
        var filters = new Dictionary<string, string> { ["lang"] = "tr" };

        foreach (var strategy in new[] { RetrievalStrategy.Dense, RetrievalStrategy.Lexical, RetrievalStrategy.Hybrid })
        {
            var response = await Service(store, new RetrievalOptions { Strategy = strategy })
                .SearchAsync(Query("alpha", filters), CancellationToken.None);

            Assert.Equal(["tr"], response.Results.Select(r => r.ChunkId));
        }
    }

    [Theory]
    [InlineData("metadata.secret")]   // would address a nested payload path
    [InlineData("bad key")]
    [InlineData("")]
    [InlineData("a;b")]
    public async Task InvalidFilterKeys_AreRejectedBeforeAnyStoreCall(string key)
    {
        var store = await Seeded(new LexicalInMemoryVectorStore());

        var response = await Service(store, new RetrievalOptions())
            .SearchAsync(Query(filters: new() { [key] = "x" }), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("Invalid metadata filter key", response.ErrorMessage);
        Assert.Equal(0, store.SearchCalls);
    }

    [Fact]
    public async Task TooManyOrTooLongFilters_AreRejected()
    {
        var store = await Seeded(new LexicalInMemoryVectorStore());
        var service = Service(store, new RetrievalOptions());

        var many = Enumerable.Range(0, FilterValidator.MaxFilters + 1).ToDictionary(i => $"k{i}", _ => "v");
        var longValue = new Dictionary<string, string> { ["k"] = new string('x', FilterValidator.MaxValueLength + 1) };

        Assert.False((await service.SearchAsync(Query(filters: many), CancellationToken.None)).Success);
        Assert.False((await service.SearchAsync(Query(filters: longValue), CancellationToken.None)).Success);
    }
}
