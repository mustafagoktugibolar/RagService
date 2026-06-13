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

        services.AddSingleton<PlainTextExtractor>();
        services.AddSingleton<PdfTextExtractor>();
        services.AddSingleton<DocxTextExtractor>();
        services.AddSingleton<ITextExtractor, FileExtensionTextExtractor>();
        services.AddSingleton<IChunker, DataIngestionTokenChunker>();

        services.AddBlobStorage(configuration);
        services.AddEmbeddingProvider(configuration);
        services.AddVectorStore(configuration);

        services.AddTransient<IndexDocumentService>();
        services.AddTransient<VectorSearchService>();

        return services;
    }

    public static IServiceCollection AddBlobStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = GetRagOptions(configuration).StorageProvider;

        switch (Normalize(provider))
        {
            case "minio":
                services.Configure<MinioOptions>(configuration.GetSection("Minio"));
                services.AddSingleton<IBlobStorage, MinioBlobStorage>();
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
                services.AddTransient<IEmbeddingProvider, OpenAiEmbeddingProvider>();
                break;

            case "azureopenai":
            case "azureopenaiembedding":
                services.AddTransient<IEmbeddingProvider, AzureOpenAiEmbeddingProvider>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Rag:EmbeddingProvider '{provider}'. Supported values are OpenAI and AzureOpenAI.");
        }

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
