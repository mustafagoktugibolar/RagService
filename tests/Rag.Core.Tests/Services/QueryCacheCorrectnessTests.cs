using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Contracts;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Services;
using Rag.Core.Tests.Fakes;

namespace Rag.Core.Tests.Services;

/// <summary>
/// Regression tests for query-cache identity. They drive the real IndexDocumentService and
/// VectorSearchService against an in-memory store and a shared cache, so they describe the
/// behaviour a caller observes — not the internals of the cache key.
/// </summary>
public sealed class QueryCacheCorrectnessTests
{
    private const string Collection = "docs";
    private const int Dim = 4;

    private static RagOptions Rag(string model = "model-a") => new()
    {
        EmbeddingProvider = "OpenAI",
        EmbeddingModel = model,
        VectorSize = Dim,
        DefaultTopK = 5
    };

    private static IDistributedCache NewCache() =>
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    private static IEmbeddingProvider Embedder()
    {
        var embedder = Substitute.For<IEmbeddingProvider>();
        float[] v = [1f, 0f, 0f, 0f];
        embedder.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(v);
        embedder.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<float[]>)call.Arg<IReadOnlyList<string>>().Select(_ => v).ToArray());
        return embedder;
    }

    private static VectorSearchService Searcher(IVectorStore store, IDistributedCache cache, RagOptions rag) =>
        new(Embedder(), store, cache,
            Microsoft.Extensions.Options.Options.Create(rag),
            Microsoft.Extensions.Options.Options.Create(new QueryCacheOptions { Enabled = true, TtlMinutes = 5 }),
            NullLogger<VectorSearchService>.Instance);

    private static IndexDocumentService Indexer(IVectorStore store, string text, RagOptions rag)
    {
        var extractor = Substitute.For<ITextExtractor>();
        extractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(text);
        var chunker = Substitute.For<IChunker>();
        chunker.ChunkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<DocumentChunk>)
            [
                new DocumentChunk { DocumentId = call.ArgAt<string>(0), ChunkId = $"{call.ArgAt<string>(0)}:000000", Text = text }
            ]);
        return new IndexDocumentService(extractor, chunker, Embedder(), store,
            Microsoft.Extensions.Options.Options.Create(rag), NullLogger<IndexDocumentService>.Instance);
    }

    private static IndexDocumentRequest IndexRequest(string requestId) => new()
    {
        RequestId = requestId,
        Collection = Collection,
        DocumentId = "doc-1",
        FilePath = "doc-1.txt"
    };

    private static QueryRequest Query(Dictionary<string, string>? filters = null) => new()
    {
        RequestId = "q",
        Collection = Collection,
        Query = "anything",
        TopK = 3,
        Filters = filters
    };

    [Fact]
    public async Task Reindexing_a_document_invalidates_cached_query_results()
    {
        var store = new InMemoryVectorStore();
        var cache = NewCache();
        var rag = Rag();
        var search = Searcher(store, cache, rag);

        Assert.True((await Indexer(store, "old text", rag).IndexAsync(IndexRequest("i1"), CancellationToken.None)).Success);
        var before = await search.SearchAsync(Query(), CancellationToken.None);
        Assert.Equal("old text", Assert.Single(before.Results).Text);

        Assert.True((await Indexer(store, "new text", rag).IndexAsync(IndexRequest("i2"), CancellationToken.None)).Success);
        var after = await search.SearchAsync(Query(), CancellationToken.None);

        Assert.Equal("new text", Assert.Single(after.Results).Text);
    }

    [Fact]
    public async Task Cache_entries_are_not_shared_between_embedding_models()
    {
        var cache = NewCache();

        var storeA = new InMemoryVectorStore();
        await Indexer(storeA, "answer from model A", Rag("model-a")).IndexAsync(IndexRequest("a"), CancellationToken.None);
        var storeB = new InMemoryVectorStore();
        await Indexer(storeB, "answer from model B", Rag("model-b")).IndexAsync(IndexRequest("b"), CancellationToken.None);

        var fromA = await Searcher(storeA, cache, Rag("model-a")).SearchAsync(Query(), CancellationToken.None);
        var fromB = await Searcher(storeB, cache, Rag("model-b")).SearchAsync(Query(), CancellationToken.None);

        Assert.Equal("answer from model A", Assert.Single(fromA.Results).Text);
        Assert.Equal("answer from model B", Assert.Single(fromB.Results).Text);
    }

    [Fact]
    public async Task Filters_that_differ_only_by_delimiter_characters_do_not_collide()
    {
        var store = new InMemoryVectorStore();
        var cache = NewCache();
        var search = Searcher(store, cache, Rag());
        await store.EnsureCollectionAsync(Collection, Dim, CancellationToken.None);
        await store.SetCollectionMetadataAsync(new CollectionMetadata
        {
            Collection = Collection, EmbeddingProvider = "OpenAI", EmbeddingModel = "model-a", VectorSize = Dim
        }, CancellationToken.None);
        await store.UpsertAsync(Collection,
        [
            new VectorDocument { DocumentId = "d1", ChunkId = "d1:0", Text = "tricky", Embedding = new float[Dim],
                Metadata = new Dictionary<string, string> { ["a"] = "1|b=2" } },
            new VectorDocument { DocumentId = "d2", ChunkId = "d2:0", Text = "plain", Embedding = new float[Dim],
                Metadata = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" } }
        ], CancellationToken.None);

        // Both filters flatten to the raw string "a=1|b=2" in the current key format.
        var tricky = await search.SearchAsync(Query(new() { ["a"] = "1|b=2" }), CancellationToken.None);
        var plain = await search.SearchAsync(Query(new() { ["a"] = "1", ["b"] = "2" }), CancellationToken.None);

        Assert.Equal("tricky", Assert.Single(tricky.Results).Text);
        Assert.Equal("plain", Assert.Single(plain.Results).Text);
    }
}
