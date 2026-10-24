using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Options;
using Rag.Core.Providers.Chunking;
using Rag.Core.Providers.Embeddings;
using Rag.Core.Providers.Storage;
using Rag.Core.Providers.TextExtraction;
using Rag.Core.Providers.VectorStores;
using Rag.Core.Services;
using StackExchange.Redis;

namespace Rag.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RagOptions>(configuration.GetSection("Rag"));
        services.Configure<OpenAiOptions>(configuration.GetSection("OpenAI"));
        services.Configure<AzureOpenAiOptions>(configuration.GetSection("AzureOpenAI"));
        services.Configure<QdrantOptions>(configuration.GetSection("Qdrant"));
        services.Configure<PgVectorOptions>(configuration.GetSection("PgVector"));
        services.Configure<RabbitMqOptions>(configuration.GetSection("RabbitMQ"));
        services.Configure<ChunkingOptions>(configuration.GetSection("Chunking"));
        services.Configure<EmbeddingCacheOptions>(configuration.GetSection("EmbeddingCache"));
        services.Configure<QueryCacheOptions>(configuration.GetSection("QueryCache"));

        var redisConnectionString = configuration["Redis:ConnectionString"] ?? "localhost:6379";
        services.AddStackExchangeRedisCache(o => o.ConfigurationOptions = ConfigurationOptions.Parse(redisConnectionString));

        services.AddSingleton<PlainTextExtractor>();
        services.AddSingleton<PdfTextExtractor>();
        services.AddSingleton<DocxTextExtractor>();
        services.AddSingleton<ITextExtractor, FileExtensionTextExtractor>();
        services.AddSingleton<IChunker, DataIngestionTokenChunker>();

        services.AddBlobStorage(configuration);
        services.AddEmbeddingProvider(configuration);
        services.AddVectorStore(configuration);
        services.AddReranking(configuration);
        services.AddQueryRewriting(configuration);

        services.AddTransient<IndexDocumentService>();
        services.AddTransient<VectorSearchService>();

        return services;
    }

    public static IServiceCollection AddBlobStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = GetRagOptions(configuration).StorageProvider;

        switch (Normalize(provider))
        {
            // One S3 client for every S3-compatible store; "minio"/"seaweedfs" are accepted as aliases.
            case "s3":
            case "seaweedfs":
            case "minio":
                services.Configure<S3Options>(configuration.GetSection("S3"));
                services.AddSingleton<S3BlobStorage>();
                services.AddSingleton<IBlobStorage>(sp => sp.GetRequiredService<S3BlobStorage>());
                break;

            case "local":
            default:
                services.Configure<LocalStorageOptions>(configuration.GetSection("LocalStorage"));
                services.AddSingleton<IBlobStorage, LocalBlobStorage>();
                break;
        }

        return services;
    }

    public static IServiceCollection AddEmbeddingProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = GetRagOptions(configuration).EmbeddingProvider;

        switch (Normalize(provider))
        {
            case "openai":
                services.AddTransient<OpenAiEmbeddingProvider>();
                break;

            case "azureopenai":
            case "azureopenaiembedding":
                services.AddTransient<AzureOpenAiEmbeddingProvider>();
                break;

            case "hashing":
                // Local, deterministic, no network: development, tests and the Docker end-to-end check. Not semantic.
                services.AddTransient<HashingEmbeddingProvider>(sp =>
                    new HashingEmbeddingProvider(sp.GetRequiredService<IOptions<RagOptions>>().Value.VectorSize));
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Rag:EmbeddingProvider '{provider}'. Supported values are OpenAI, AzureOpenAI and Hashing (development/test only).");
        }

        services.AddTransient<IEmbeddingProvider>(sp =>
        {
            var cacheOptions = sp.GetRequiredService<IOptions<EmbeddingCacheOptions>>().Value;
            var normalizedProvider = Normalize(provider);

            IEmbeddingProvider inner;
            int? dimensions;

            if (normalizedProvider == "hashing")
            {
                return sp.GetRequiredService<HashingEmbeddingProvider>();   // already cheap and deterministic: no cache wrapper
            }

            if (normalizedProvider == "openai")
            {
                inner = sp.GetRequiredService<OpenAiEmbeddingProvider>();
                dimensions = sp.GetRequiredService<IOptions<OpenAiOptions>>().Value.Dimensions;
            }
            else
            {
                inner = sp.GetRequiredService<AzureOpenAiEmbeddingProvider>();
                dimensions = sp.GetRequiredService<IOptions<AzureOpenAiOptions>>().Value.Dimensions;
            }

            if (!cacheOptions.Enabled)
                return inner;

            return new CachingEmbeddingProvider(
                inner,
                sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
                sp.GetRequiredService<IOptions<RagOptions>>(),
                sp.GetRequiredService<IOptions<EmbeddingCacheOptions>>(),
                dimensions);
        });

        return services;
    }

    public static IServiceCollection AddVectorStore(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = GetRagOptions(configuration).VectorStoreProvider;

        switch (Normalize(provider))
        {
            case "qdrant":
                services.AddTransient<IVectorStore, QdrantVectorStore>();
                break;

            case "pgvector":
            case "postgres":
            case "postgresql":
                services.AddTransient<IVectorStore, PgVectorStore>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Rag:VectorStoreProvider '{provider}'. Supported values are Qdrant and PgVector.");
        }

        return services;
    }

    private static RagOptions GetRagOptions(IConfiguration configuration)
    {
        return configuration.GetSection("Rag").Get<RagOptions>() ?? new RagOptions();
    }

    private static string Normalize(string value)
    {
        return value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
    }
}
