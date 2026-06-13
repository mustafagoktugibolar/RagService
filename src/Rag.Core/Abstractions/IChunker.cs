using Rag.Core.Models;

namespace Rag.Core.Abstractions;

public interface IChunker
{
    Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        string documentId,
        string text,
        Dictionary<string, string>? metadata,
        CancellationToken ct);
}
