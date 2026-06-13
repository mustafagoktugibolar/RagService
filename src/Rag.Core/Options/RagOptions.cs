namespace Rag.Core.Options;

public sealed class RagOptions
{
    public string EmbeddingProvider { get; set; } = "OpenAI";

    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    public string VectorStoreProvider { get; set; } = "Qdrant";

    public string StorageProvider { get; set; } = "Local";

    public int VectorSize { get; set; } = 1536;

    public int DefaultTopK { get; set; } = 5;
}
