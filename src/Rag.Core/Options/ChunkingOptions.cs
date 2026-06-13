namespace Rag.Core.Options;

public sealed class ChunkingOptions
{
    public int ChunkSize { get; set; } = 1000;

    public int ChunkOverlap { get; set; } = 200;
}
