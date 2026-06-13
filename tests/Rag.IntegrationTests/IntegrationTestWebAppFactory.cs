using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Microsoft.Extensions.Configuration;
using WireMock.Server;
using Xunit;

namespace Rag.IntegrationTests;

public class IntegrationTestWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public RedisContainer RedisContainer { get; } = new RedisBuilder().WithImage("redis:7-alpine").Build();
    public RabbitMqContainer RabbitMqContainer { get; } = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
    
    public IContainer MinioContainer { get; } = new ContainerBuilder()
        .WithImage("minio/minio")
        .WithCommand("server", "/data")
        .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
        .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/live")))
        .Build();

    public IContainer QdrantContainer { get; } = new ContainerBuilder()
        .WithImage("qdrant/qdrant")
        .WithPortBinding(6334, true)
        .Build();

    public WireMockServer WireMockServer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            RedisContainer.StartAsync(),
            RabbitMqContainer.StartAsync(),
            MinioContainer.StartAsync(),
            QdrantContainer.StartAsync()
        );

        WireMockServer = WireMockServer.Start();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                {"ConnectionStrings:Redis", RedisContainer.GetConnectionString()},
                {"RabbitMq:HostName", RabbitMqContainer.Hostname},
                {"RabbitMq:Port", RabbitMqContainer.GetMappedPublicPort(5672).ToString()},
                {"RabbitMq:UserName", "guest"},
                {"RabbitMq:Password", "guest"},
                {"Qdrant:Host", QdrantContainer.Hostname},
                {"Qdrant:Port", QdrantContainer.GetMappedPublicPort(6334).ToString()},
                {"Minio:Endpoint", $"{MinioContainer.Hostname}:{MinioContainer.GetMappedPublicPort(9000)}"},
                {"Minio:AccessKey", "minioadmin"},
                {"Minio:SecretKey", "minioadmin"},
                {"Minio:UseSSL", "false"},
                {"OpenAI:BaseUrl", WireMockServer.Urls[0]},
                {"OpenAI:ApiKey", "test-key"}
            };

            config.AddInMemoryCollection(inMemorySettings);
        });
    }

    public new async Task DisposeAsync()
    {
        WireMockServer?.Stop();
        WireMockServer?.Dispose();

        await Task.WhenAll(
            RedisContainer.DisposeAsync().AsTask(),
            RabbitMqContainer.DisposeAsync().AsTask(),
            MinioContainer.DisposeAsync().AsTask(),
            QdrantContainer.DisposeAsync().AsTask()
        );
    }
}
