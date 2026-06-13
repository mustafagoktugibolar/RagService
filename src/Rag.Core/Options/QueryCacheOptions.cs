namespace Rag.Core.Options;

public sealed class QueryCacheOptions
{
    public bool Enabled { get; set; } = false;
    public int TtlMinutes { get; set; } = 5;
}
