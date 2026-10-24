using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rag.Core.Abstractions;
using Rag.Core.Extensions;
using Rag.Core.Providers.Embeddings;

namespace Rag.Core.Tests.Providers.Embeddings;

public sealed class HashingEmbeddingProviderTests
{
    [Fact]
    public async Task Vectors_AreDeterministic_UnitLength_AndOfTheConfiguredSize()
    {
        var provider = new HashingEmbeddingProvider(64);

        var a = await provider.EmbedAsync("the circuit breaker opens after failures", CancellationToken.None);
        var b = await provider.EmbedAsync("the circuit breaker opens after failures", CancellationToken.None);

        Assert.Equal(64, a.Length);
        Assert.Equal(a, b);
        Assert.Equal(1.0, Math.Sqrt(a.Sum(x => (double)x * x)), 5);
    }

    [Fact]
    public async Task SimilarTexts_ScoreHigherThanUnrelatedOnes()
    {
        var provider = new HashingEmbeddingProvider();
        var query = await provider.EmbedAsync("circuit breaker failures", CancellationToken.None);
        var related = await provider.EmbedAsync("a circuit breaker counts failures and opens", CancellationToken.None);
        var unrelated = await provider.EmbedAsync("walnut harbor violet falcon", CancellationToken.None);

        double Dot(float[] x, float[] y) => x.Zip(y, (p, q) => (double)p * q).Sum();

        Assert.True(Dot(query, related) > Dot(query, unrelated));
    }

    [Fact]
    public async Task TheHashingProvider_IsSelectedByConfiguration_WithoutAnyCredential()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Rag:EmbeddingProvider"] = "Hashing", ["Rag:EmbeddingModel"] = HashingEmbeddingProvider.ModelName, ["Rag:VectorSize"] = "128",
            ["Qdrant:Host"] = "localhost", ["Redis:ConnectionString"] = "localhost:6379,abortConnect=false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagCore(configuration);
        using var provider = services.BuildServiceProvider();

        var embedder = provider.GetRequiredService<IEmbeddingProvider>();

        Assert.IsType<HashingEmbeddingProvider>(embedder);
        Assert.Equal(128, (await embedder.EmbedAsync("anything", CancellationToken.None)).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("the and of")]   // only stop-words: no signal, but still a valid (zero) vector, not an exception
    public async Task TextWithoutIndexableTerms_StillEmbedsWithoutThrowing(string text)
    {
        var vector = await new HashingEmbeddingProvider().EmbedAsync(text, CancellationToken.None);

        Assert.Equal(256, vector.Length);
    }
}
