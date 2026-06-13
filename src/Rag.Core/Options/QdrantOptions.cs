namespace Rag.Core.Options;

public sealed class QdrantOptions
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 6334;

    public int HttpPort { get; set; } = 6333;

    public bool Https { get; set; }

    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public string MetadataCollection { get; set; } = "rag_collection_metadata";
}
