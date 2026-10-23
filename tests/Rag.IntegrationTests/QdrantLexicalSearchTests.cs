using Qdrant.Client.Grpc;
using Rag.Core.Models;
using Rag.Core.Providers.VectorStores;
using Rag.Core.Retrieval;
using Xunit;

namespace Rag.IntegrationTests;

public sealed class QdrantLexicalSearchTests(QdrantOnlyFixture fixture) : IClassFixture<QdrantOnlyFixture>
{
    private const int Dim = 3;

    private static VectorDocument Doc(string chunk, string text, float[] embedding, string? lang = null) => new()
    {
        DocumentId = chunk.Split(':')[0],
        ChunkId = chunk,
        Text = text,
        Embedding = embedding,
        Metadata = lang is null ? null : new Dictionary<string, string> { ["lang"] = lang }
    };

    private async Task<string> NewIndexedCollectionAsync()
    {
        var collection = $"lex-{Guid.NewGuid():N}";
        await fixture.Store.EnsureCollectionAsync(collection, Dim, CancellationToken.None);
        await fixture.Store.UpsertAsync(collection,
        [
            Doc("a:0", "The dead letter exchange routes rejected messages to a queue", [1, 0, 0], "en"),
            Doc("b:0", "Prefetch limits unacknowledged deliveries for a consumer", [0, 1, 0], "en"),
            Doc("c:0", "Dead letter kuyrugu reddedilen mesajlari saklar", [0, 0, 1], "tr"),
            Doc("d:0", "!!! ??? ...", [1, 1, 1])   // no indexable terms: stored with a dense vector only
        ], CancellationToken.None);
        await Task.Delay(500);
        return collection;
    }

    [Fact]
    public async Task LexicalSearch_RanksTheChunkContainingTheRareTermFirst()
    {
        var collection = await NewIndexedCollectionAsync();

        var results = await fixture.Store.LexicalSearchAsync(collection, "prefetch", 5, null, CancellationToken.None);

        Assert.Equal("b:0", results[0].ChunkId);
        Assert.Single(results);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task LexicalSearch_ScoresTermsSharedByMoreChunksLowerThanRareOnes()
    {
        var collection = await NewIndexedCollectionAsync();

        // "dead letter" appears in two chunks; "exchange" only in one → the exchange chunk must win.
        var results = await fixture.Store.LexicalSearchAsync(collection, "dead letter exchange", 5, null, CancellationToken.None);

        Assert.Equal("a:0", results[0].ChunkId);
        Assert.Contains(results, r => r.ChunkId == "c:0");
    }

    [Fact]
    public async Task LexicalSearch_AppliesMetadataFilters()
    {
        var collection = await NewIndexedCollectionAsync();

        var results = await fixture.Store.LexicalSearchAsync(
            collection, "dead letter", 5, new Dictionary<string, string> { ["lang"] = "tr" }, CancellationToken.None);

        Assert.Equal(["c:0"], results.Select(r => r.ChunkId));
    }

    [Fact]
    public async Task LexicalSearch_QueryWithoutIndexableTerms_ReturnsNothing()
    {
        var collection = await NewIndexedCollectionAsync();

        Assert.Empty(await fixture.Store.LexicalSearchAsync(collection, "?! a", 5, null, CancellationToken.None));
    }

    [Fact]
    public async Task DenseSearch_StillWorksOnACollectionThatAlsoHoldsSparseVectors()
    {
        var collection = await NewIndexedCollectionAsync();

        var results = await fixture.Store.SearchAsync(collection, [0, 1, 0], 1, null, CancellationToken.None);

        Assert.Equal("b:0", results.Single().ChunkId);
    }

    [Fact]
    public async Task LegacyDenseOnlyCollection_KeepsIndexingAndDenseSearching()
    {
        var collection = $"legacy-{Guid.NewGuid():N}";
        await fixture.RawClient.CreateCollectionAsync(
            collection, new VectorParams { Size = Dim, Distance = Distance.Cosine });

        await fixture.Store.EnsureCollectionAsync(collection, Dim, CancellationToken.None);
        await fixture.Store.UpsertAsync(collection,
            [Doc("x:0", "existing deployments must not break", [1, 0, 0])], CancellationToken.None);
        await Task.Delay(500);

        var dense = await fixture.Store.SearchAsync(collection, [1, 0, 0], 1, null, CancellationToken.None);

        Assert.Equal("x:0", dense.Single().ChunkId);
    }

    [Fact]
    public async Task LexicalSearch_OnALegacyCollection_ThrowsATypedExceptionSoCallersCanDegrade()
    {
        var collection = $"legacy-{Guid.NewGuid():N}";
        await fixture.RawClient.CreateCollectionAsync(
            collection, new VectorParams { Size = Dim, Distance = Distance.Cosine });

        var ex = await Assert.ThrowsAsync<LexicalSearchUnavailableException>(() =>
            fixture.Store.LexicalSearchAsync(collection, "anything", 3, null, CancellationToken.None));

        Assert.Equal(collection, ex.Collection);
        Assert.Contains("re-index", ex.Message);
    }
}
