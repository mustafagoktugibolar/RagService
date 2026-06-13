using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Options;
using Rag.Core.Providers.Embeddings;

namespace Rag.Core.Tests.Providers.Embeddings;

public sealed class CachingEmbeddingProviderTests
{
    private static IDistributedCache BuildCache() =>
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    private static CachingEmbeddingProvider Build(
        IEmbeddingProvider inner,
        IDistributedCache? cache = null,
        string model = "text-embedding-3-small",
        int? dimensions = null,
        int ttlHours = 24) =>
        new(
            inner,
            cache ?? BuildCache(),
            Microsoft.Extensions.Options.Options.Create(new RagOptions { EmbeddingModel = model }),
            Microsoft.Extensions.Options.Options.Create(new EmbeddingCacheOptions { Enabled = true, TtlHours = ttlHours }),
            dimensions);

    // ── EmbedAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EmbedAsync_CacheMiss_CallsInnerAndReturnsEmbedding()
    {
        var expected = new float[] { 0.1f, 0.2f, 0.3f };
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedAsync("hello", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(expected));
        var sut = Build(inner);

        var result = await sut.EmbedAsync("hello", CancellationToken.None);

        Assert.Equal(expected, result);
        await inner.Received(1).EmbedAsync("hello", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedAsync_CacheHit_DoesNotCallInner()
    {
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedAsync("hello", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new float[] { 1f, 2f }));
        var sut = Build(inner);

        await sut.EmbedAsync("hello", CancellationToken.None);
        var result = await sut.EmbedAsync("hello", CancellationToken.None);

        Assert.Equal(new float[] { 1f, 2f }, result);
        await inner.Received(1).EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedAsync_DifferentModels_CacheSeparately()
    {
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedAsync("hello", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new float[] { 1f }));
        var cache = BuildCache();

        await Build(inner, cache, model: "model-a").EmbedAsync("hello", CancellationToken.None);
        await Build(inner, cache, model: "model-b").EmbedAsync("hello", CancellationToken.None);

        await inner.Received(2).EmbedAsync("hello", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedAsync_DifferentDimensions_CacheSeparately()
    {
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedAsync("hello", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new float[] { 1f }));
        var cache = BuildCache();

        await Build(inner, cache, dimensions: 1536).EmbedAsync("hello", CancellationToken.None);
        await Build(inner, cache, dimensions: 256).EmbedAsync("hello", CancellationToken.None);

        await inner.Received(2).EmbedAsync("hello", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedAsync_FloatValuesRoundTripThroughCache()
    {
        var original = new float[] { 1.23456789f, -0.5f, float.MaxValue, float.Epsilon };
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedAsync("text", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(original));
        var sut = Build(inner);

        await sut.EmbedAsync("text", CancellationToken.None);
        var fromCache = await sut.EmbedAsync("text", CancellationToken.None);

        Assert.Equal(original, fromCache);
    }

    // ── EmbedBatchAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task EmbedBatchAsync_EmptyList_ReturnsEmpty()
    {
        var inner = Substitute.For<IEmbeddingProvider>();
        var sut = Build(inner);

        var result = await sut.EmbedBatchAsync([], CancellationToken.None);

        Assert.Empty(result);
        await inner.DidNotReceive()
                   .EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedBatchAsync_AllMiss_CallsInnerWithAllTexts()
    {
        var texts = new[] { "a", "b", "c" };
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<float[]>>([[1f], [2f], [3f]]));
        var sut = Build(inner);

        var result = await sut.EmbedBatchAsync(texts, CancellationToken.None);

        Assert.Equal(3, result.Count);
        await inner.Received(1).EmbedBatchAsync(
            Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(texts)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedBatchAsync_AllHit_DoesNotCallInner()
    {
        var texts = new[] { "a", "b" };
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<float[]>>([[1f], [2f]]));
        var sut = Build(inner);

        await sut.EmbedBatchAsync(texts, CancellationToken.None);
        var result = await sut.EmbedBatchAsync(texts, CancellationToken.None);

        Assert.Equal(2, result.Count);
        await inner.Received(1)
                   .EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmbedBatchAsync_PartialHit_CallsInnerOnlyWithMisses()
    {
        // Pre-cache "a" and "c"
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedBatchAsync(
                 Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "a", "c" })),
                 Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<float[]>>([[10f], [30f]]));
        inner.EmbedBatchAsync(
                 Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "b", "d" })),
                 Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<float[]>>([[20f], [40f]]));
        var sut = Build(inner);

        await sut.EmbedBatchAsync(["a", "c"], CancellationToken.None);

        var result = await sut.EmbedBatchAsync(["a", "b", "c", "d"], CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Equal([10f], result[0]); // "a" — cache hit
        Assert.Equal([20f], result[1]); // "b" — cache miss
        Assert.Equal([30f], result[2]); // "c" — cache hit
        Assert.Equal([40f], result[3]); // "d" — cache miss
    }

    [Fact]
    public async Task EmbedBatchAsync_AfterMiss_ResultsCachedForNextCall()
    {
        var embedding = new float[] { 9f, 8f };
        var inner = Substitute.For<IEmbeddingProvider>();
        inner.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<float[]>>([embedding]));
        var sut = Build(inner);

        await sut.EmbedBatchAsync(["x"], CancellationToken.None);
        var second = await sut.EmbedBatchAsync(["x"], CancellationToken.None);

        Assert.Equal(embedding, second[0]);
        await inner.Received(1)
                   .EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }
}
