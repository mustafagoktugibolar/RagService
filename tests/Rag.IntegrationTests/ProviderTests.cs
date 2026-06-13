using Microsoft.Extensions.DependencyInjection;
using Rag.Core.Abstractions;
using Rag.Core.Models;
using Xunit;

namespace Rag.IntegrationTests;

[Collection("Integration")]
public class ProviderTests : IClassFixture<IntegrationTestWebAppFactory>
{
    private readonly IntegrationTestWebAppFactory _factory;

    public ProviderTests(IntegrationTestWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task MinioBlobStorage_UploadAndRead_Works()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var content = "integration test content";
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using var uploadStream = new MemoryStream(bytes);

        // Act
        await storage.UploadAsync("test-folder/test-file.txt", uploadStream, "text/plain", CancellationToken.None);

        await using var downloadStream = await storage.OpenReadAsync("test-folder/test-file.txt", CancellationToken.None);
        using var reader = new StreamReader(downloadStream);
        var result = await reader.ReadToEndAsync();

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task QdrantVectorStore_UpsertAndSearch_Works()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();

        var vector = new float[] { 0.1f, 0.2f, 0.3f };
        var id = Guid.NewGuid().ToString();
        var metadata = new Dictionary<string, string> { { "test", "value" } };

        var document = new VectorDocument
        {
            DocumentId = "doc1",
            ChunkId = id,
            Text = "Sample text",
            Embedding = vector,
            Metadata = metadata
        };

        // Act
        await vectorStore.EnsureCollectionAsync("test-collection", 3, CancellationToken.None);
        await vectorStore.UpsertAsync("test-collection", new[] { document }, CancellationToken.None);

        // Wait a bit for indexing
        await Task.Delay(1000);

        var results = await vectorStore.SearchAsync("test-collection", vector, 1, null, CancellationToken.None);

        // Assert
        Assert.Single(results);
        Assert.Equal("doc1", results[0].DocumentId);
        Assert.Equal(id, results[0].ChunkId);
        Assert.Equal("value", results[0].Metadata?["test"]);
    }

    [Fact]
    public async Task OpenAiEmbeddingProvider_EmbedAsync_Works()
    {
        // Arrange
        _factory.WireMockServer
            .Given(WireMock.RequestBuilders.Request.Create().WithPath(new WireMock.Matchers.RegexMatcher(".*embeddings.*")).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new
                {
                    @object = "list",
                    data = new[]
                    {
                        new { @object = "embedding", index = 0, embedding = new float[] { 0.5f, 0.6f, 0.7f } }
                    },
                    model = "text-embedding-ada-002",
                    usage = new { prompt_tokens = 5, total_tokens = 5 }
                }));

        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();

        // Act
        var result = await provider.EmbedAsync("test embedding", CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Length);
        Assert.Equal(0.5f, result[0]);
    }
}
