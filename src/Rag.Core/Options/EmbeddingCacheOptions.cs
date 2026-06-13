namespace Rag.Core.Options;

public sealed class EmbeddingCacheOptions
{
    public bool Enabled { get; set; } = true;
    public int TtlHours { get; set; } = 24;
}
