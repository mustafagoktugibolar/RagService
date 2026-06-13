using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Embeddings;
using Rag.Core.Abstractions;
using Rag.Core.Options;
using Rag.Core.Resilience;
using Rag.Core.Telemetry;

namespace Rag.Core.Providers.Embeddings;

public sealed class OpenAiEmbeddingProvider(
    IOptions<OpenAiOptions> options,
    IOptions<RagOptions> ragOptions) : IEmbeddingProvider
{
    private int MaxBatchSize => options.Value.MaxBatchSize;
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        return await RagPipelines.ExternalApi.ExecuteAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var generator = CreateGenerator();
            var embedding = await generator.GenerateAsync(text, new Microsoft.Extensions.AI.EmbeddingGenerationOptions(), token);
            RagTelemetry.EmbeddingDurationMs.Record(sw.ElapsedMilliseconds);
            return embedding.Vector.ToArray();
        }, ct);
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0)
            return [];

        if (texts.Count <= MaxBatchSize)
            return await EmbedBatchInternalAsync(texts, ct);

        var results = new List<float[]>(texts.Count);
        for (var i = 0; i < texts.Count; i += MaxBatchSize)
        {
            var batch = texts.Skip(i).Take(MaxBatchSize).ToArray();
            var batchResult = await EmbedBatchInternalAsync(batch, ct);
            results.AddRange(batchResult);
        }
        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchInternalAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        return await RagPipelines.ExternalApi.ExecuteAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var generator = CreateGenerator();
            var result = await generator.GenerateAsync(texts, new Microsoft.Extensions.AI.EmbeddingGenerationOptions(), token);
            RagTelemetry.EmbeddingDurationMs.Record(sw.ElapsedMilliseconds);
            return (IReadOnlyList<float[]>)result.Select(e => e.Vector.ToArray()).ToArray();
        }, ct);
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateGenerator()
    {
        var openAiOptions = options.Value;
        return CreateClient().AsIEmbeddingGenerator(openAiOptions.Dimensions);
    }

    private EmbeddingClient CreateClient()
    {
        var openAiOptions = options.Value;
        if (string.IsNullOrWhiteSpace(openAiOptions.ApiKey))
            throw new InvalidOperationException("OpenAI:ApiKey is required when Rag:EmbeddingProvider is OpenAI.");

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(openAiOptions.BaseUrl))
            clientOptions.Endpoint = new Uri(openAiOptions.BaseUrl);

        if (!string.IsNullOrWhiteSpace(openAiOptions.Organization))
            clientOptions.OrganizationId = openAiOptions.Organization;

        if (!string.IsNullOrWhiteSpace(openAiOptions.Project))
            clientOptions.ProjectId = openAiOptions.Project;

        return new EmbeddingClient(
            ragOptions.Value.EmbeddingModel,
            new ApiKeyCredential(openAiOptions.ApiKey),
            clientOptions);
    }
}
