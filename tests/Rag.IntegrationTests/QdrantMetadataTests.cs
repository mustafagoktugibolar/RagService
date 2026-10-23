using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Providers.VectorStores;
using Xunit;

namespace Rag.IntegrationTests;

/// <summary>Qdrant-only fixture: needs just one container, independent of the full web-app stack.</summary>
public sealed class QdrantOnlyFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("qdrant/qdrant")
        .WithPortBinding(6334, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(6334))
        .Build();

    public QdrantVectorStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Store = new QdrantVectorStore(
            Microsoft.Extensions.Options.Options.Create(new QdrantOptions
            {
                Host = _container.Hostname,
                Port = _container.GetMappedPublicPort(6334)
            }),
            NullLoggerFactory.Instance);
    }

    public async Task DisposeAsync()
    {
        Store?.Dispose();
        await _container.DisposeAsync();
    }
}

public sealed class QdrantMetadataTests(QdrantOnlyFixture fixture) : IClassFixture<QdrantOnlyFixture>
{
    [Fact]
    public async Task CollectionMetadata_RoundTripsIndexVersion()
    {
        var collection = $"meta-{Guid.NewGuid():N}";
        await fixture.Store.SetCollectionMetadataAsync(new CollectionMetadata
        {
            Collection = collection, EmbeddingProvider = "OpenAI", EmbeddingModel = "m", VectorSize = 3, IndexVersion = "v1"
        }, CancellationToken.None);
        var first = await fixture.Store.GetCollectionMetadataAsync(collection, CancellationToken.None);

        await fixture.Store.SetCollectionMetadataAsync(first! with { IndexVersion = "v2" }, CancellationToken.None);
        var second = await fixture.Store.GetCollectionMetadataAsync(collection, CancellationToken.None);

        Assert.Equal("v1", first!.IndexVersion);
        Assert.Equal("v2", second!.IndexVersion);
        Assert.Equal(3, second.VectorSize);
    }

    [Fact]
    public async Task CollectionMetadata_UnknownCollection_IsNull()
    {
        Assert.Null(await fixture.Store.GetCollectionMetadataAsync($"never-{Guid.NewGuid():N}", CancellationToken.None));
    }

    [Fact]
    public async Task CollectionMetadata_BeforeAnythingWasIndexed_IsNullNotAnError()
    {
        // Fresh container: the metadata collection itself does not exist yet.
        var fresh = new QdrantOnlyFixture();
        await fresh.InitializeAsync();
        try
        {
            Assert.Null(await fresh.Store.GetCollectionMetadataAsync("anything", CancellationToken.None));
        }
        finally
        {
            await fresh.DisposeAsync();
        }
    }
}
