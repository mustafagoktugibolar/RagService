using Rag.Core.Abstractions;

namespace Rag.Evaluation.Harness;

/// <summary>An embedder plus the identity that must be recorded with every run that used it.</summary>
public sealed record EmbedderSpec(
    IEmbeddingProvider Provider,
    string ProviderName,
    string Model,
    int VectorSize,
    string Kind)
{
    public static EmbedderSpec Hashing(int vectorSize = 256) => new(
        new HashingEmbeddingProvider(vectorSize), "Hashing", HashingEmbeddingProvider.ModelName, vectorSize, "synthetic");
}
