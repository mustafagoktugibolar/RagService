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
    
    public const string S3AccessKey = "testaccess";
    public const string S3SecretKey = "testsecret";

    // SeaweedFS is the default S3-compatible store; any other S3 store is a config-only swap.
    public IContainer ObjectStorageContainer { get; } = new ContainerBuilder()
        .WithImage("chrislusf/seaweedfs:4.48")
        .WithCommand("server", "-s3", "-dir=/data")
        .WithEnvironment("AWS_ACCESS_KEY_ID", S3AccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", S3SecretKey)
        .WithPortBinding(8333, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8333).ForPath("/healthz")))
        .Build();

    public IContainer QdrantContainer { get; } = new ContainerBuilder()
        .WithImage("qdrant/qdrant")
        .WithPortBinding(6333, true)
        .WithPortBinding(6334, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(6333).ForPath("/healthz")))
        .Build();

    public WireMockServer WireMockServer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            RedisContainer.StartAsync(),
            RabbitMqContainer.StartAsync(),
            ObjectStorageContainer.StartAsync(),
            QdrantContainer.StartAsync()
        );

        WireMockServer = WireMockServer.Start();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        // Program.cs reads some settings (Redis, Qdrant health URL) eagerly while building the host, before
        // ConfigureAppConfiguration sources are visible. UseSetting is applied early enough for both.
        var inMemorySettings = new Dictionary<string, string?>
        {
            {"Redis:ConnectionString", RedisContainer.GetConnectionString()},
            {"RabbitMq:HostName", RabbitMqContainer.Hostname},
            {"RabbitMq:Port", RabbitMqContainer.GetMappedPublicPort(5672).ToString()},
            {"RabbitMq:UserName", "guest"},
            {"RabbitMq:Password", "guest"},
            {"Qdrant:Host", QdrantContainer.Hostname},
            {"Qdrant:Port", QdrantContainer.GetMappedPublicPort(6334).ToString()},
            {"Qdrant:HttpPort", QdrantContainer.GetMappedPublicPort(6333).ToString()},
            {"Rag:StorageProvider", "S3"},
            {"S3:ServiceUrl", $"http://{ObjectStorageContainer.Hostname}:{ObjectStorageContainer.GetMappedPublicPort(8333)}"},
            {"S3:AccessKey", S3AccessKey},
            {"S3:SecretKey", S3SecretKey},
            {"S3:ForcePathStyle", "true"},
            {"S3:Bucket", $"it-{Guid.NewGuid():N}"},
            {"OpenAI:BaseUrl", WireMockServer.Urls[0]},
            {"OpenAI:ApiKey", "test-key"}
        };

        foreach (var (key, value) in inMemorySettings)
            builder.UseSetting(key, value);
    }

    public new async Task DisposeAsync()
    {
        WireMockServer?.Stop();
        WireMockServer?.Dispose();

        await Task.WhenAll(
            RedisContainer.DisposeAsync().AsTask(),
            RabbitMqContainer.DisposeAsync().AsTask(),
            ObjectStorageContainer.DisposeAsync().AsTask(),
            QdrantContainer.DisposeAsync().AsTask()
        );
    }
}
