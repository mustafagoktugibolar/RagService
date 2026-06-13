namespace Rag.Core.Abstractions;

public interface IEmbeddingProvider
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
