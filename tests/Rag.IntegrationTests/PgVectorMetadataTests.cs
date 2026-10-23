using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Providers.VectorStores;
using Xunit;

namespace Rag.IntegrationTests;

public sealed class PgVectorOnlyFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("pgvector/pgvector:pg17")
        .WithEnvironment("POSTGRES_DB", "rag")
        .WithEnvironment("POSTGRES_USER", "rag")
        .WithEnvironment("POSTGRES_PASSWORD", "rag")
        .WithPortBinding(5432, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(5432))
        .Build();

    public string ConnectionString => $"Host={_container.Hostname};Port={_container.GetMappedPublicPort(5432)};Database=rag;Username=rag;Password=rag;Pooling=false";

    public PgVectorStore CreateStore(string metadataTable) => new(Microsoft.Extensions.Options.Options.Create(
        new PgVectorOptions { ConnectionString = ConnectionString, MetadataTableName = metadataTable, TableName = $"v_{metadataTable}" }));

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        // Postgres restarts once during init; wait until it accepts real connections.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception) when (attempt < 29)
            {
                await Task.Delay(1000);
            }
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class PgVectorMetadataTests(PgVectorOnlyFixture fixture) : IClassFixture<PgVectorOnlyFixture>
{
    [Fact]
    public async Task CollectionMetadata_RoundTripsIndexVersion()
    {
        await using var store = fixture.CreateStore("meta_roundtrip");
        await store.SetCollectionMetadataAsync(new CollectionMetadata
        {
            Collection = "c", EmbeddingProvider = "OpenAI", EmbeddingModel = "m", VectorSize = 3, IndexVersion = "v1"
        }, CancellationToken.None);
        var first = await store.GetCollectionMetadataAsync("c", CancellationToken.None);
        await store.SetCollectionMetadataAsync(first! with { IndexVersion = "v2" }, CancellationToken.None);
        var second = await store.GetCollectionMetadataAsync("c", CancellationToken.None);

        Assert.Equal("v1", first!.IndexVersion);
        Assert.Equal("v2", second!.IndexVersion);
    }

    [Fact]
    public async Task LegacyMetadataTable_WithoutIndexVersionColumn_IsUpgradedAndReadsEmptyVersion()
    {
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                CREATE TABLE "meta_legacy" (
                    collection text PRIMARY KEY,
                    embedding_provider text NOT NULL,
                    embedding_model text NOT NULL,
                    vector_size integer NOT NULL);
                INSERT INTO "meta_legacy" VALUES ('old', 'OpenAI', 'm', 3);
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        await using var store = fixture.CreateStore("meta_legacy");
        var legacy = await store.GetCollectionMetadataAsync("old", CancellationToken.None);

        Assert.NotNull(legacy);
        Assert.Equal(string.Empty, legacy.IndexVersion);
        Assert.Equal(3, legacy.VectorSize);
    }
}
