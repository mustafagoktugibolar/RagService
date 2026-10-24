using Rag.Core.Abstractions;
using Rag.Evaluation.Harness;

namespace Rag.Evaluation.Tests;

public sealed class CacheOnlyEmbeddingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"emb-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Counting : IEmbeddingProvider
    {
        public int Calls { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(Vector(text));

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Vector).ToArray());
        }

        private static float[] Vector(string text) => [text.Length, 1f, 2f];
    }

    [Fact]
    public void TheKey_DependsOnModelAndText_AndIsStable()
    {
        Assert.Equal(FileCachingEmbeddingProvider.KeyFor("m", "hello"), FileCachingEmbeddingProvider.KeyFor("m", "hello"));
        Assert.NotEqual(FileCachingEmbeddingProvider.KeyFor("m", "hello"), FileCachingEmbeddingProvider.KeyFor("m2", "hello"));
        Assert.NotEqual(FileCachingEmbeddingProvider.KeyFor("m", "hello"), FileCachingEmbeddingProvider.KeyFor("m", "hello "));
        Assert.Matches("^[0-9A-F]{64}$", FileCachingEmbeddingProvider.KeyFor("m", "x"));
    }

    [Fact]
    public async Task CacheOnly_ReturnsWhatTheCachingProviderStored_AndRefusesAnythingElse()
    {
        var inner = new Counting();
        await new FileCachingEmbeddingProvider(inner, "m", _directory).EmbedBatchAsync(["stored text"], CancellationToken.None);
        var cacheOnly = new CacheOnlyEmbeddingProvider("m", _directory);

        var stored = await cacheOnly.EmbedAsync("stored text", CancellationToken.None);
        Assert.Equal(new[] { 11f, 1f, 2f }, stored);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => cacheOnly.EmbedAsync("never embedded", CancellationToken.None));

        Assert.Contains("cannot make a live (paid) call", ex.Message);
        Assert.Equal(1, inner.Calls);   // storing took one call; reading and the refused miss took none
    }

    [Fact]
    public async Task CacheOnly_UsesTheModelInTheKey_SoAnotherModelsVectorsAreNotServed()
    {
        await new FileCachingEmbeddingProvider(new Counting(), "model-a", _directory).EmbedBatchAsync(["text"], CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CacheOnlyEmbeddingProvider("model-b", _directory).EmbedAsync("text", CancellationToken.None));
    }

    [Fact]
    public void CacheOnly_HasNoInnerProviderAtAll_SoNoCredentialOrNetworkCanBeInvolved()
    {
        var fields = typeof(CacheOnlyEmbeddingProvider).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        Assert.DoesNotContain(fields, f => typeof(IEmbeddingProvider).IsAssignableFrom(f.FieldType));
        Assert.DoesNotContain(fields, f => f.FieldType.Name.Contains("Http", StringComparison.OrdinalIgnoreCase));
    }
}
