using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Options;

namespace Rag.Core.Providers.Embeddings;

public sealed class AzureOpenAiEmbeddingProvider(IOptions<AzureOpenAiOptions> options) : IEmbeddingProvider
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var generator = CreateGenerator();
        var embedding = await generator.GenerateAsync(text, new Microsoft.Extensions.AI.EmbeddingGenerationOptions(), ct);
        return embedding.Vector.ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var generator = CreateGenerator();
        var result = await generator.GenerateAsync(texts, new Microsoft.Extensions.AI.EmbeddingGenerationOptions(), ct);

        return result
            .Select(embedding => embedding.Vector.ToArray())
            .ToArray();
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateGenerator()
    {
        var azureOptions = options.Value;
        if (string.IsNullOrWhiteSpace(azureOptions.Endpoint))
        {
            throw new InvalidOperationException("AzureOpenAI:Endpoint is required when Rag:EmbeddingProvider is AzureOpenAI.");
        }

        if (string.IsNullOrWhiteSpace(azureOptions.ApiKey))
        {
            throw new InvalidOperationException("AzureOpenAI:ApiKey is required when Rag:EmbeddingProvider is AzureOpenAI.");
        }

        if (string.IsNullOrWhiteSpace(azureOptions.Deployment))
        {
            throw new InvalidOperationException("AzureOpenAI:Deployment is required when Rag:EmbeddingProvider is AzureOpenAI.");
        }

        var azureClient = new AzureOpenAIClient(
            new Uri(azureOptions.Endpoint),
            new ApiKeyCredential(azureOptions.ApiKey));
        return azureClient.GetEmbeddingClient(azureOptions.Deployment)
            .AsIEmbeddingGenerator(azureOptions.Dimensions);
    }
}
