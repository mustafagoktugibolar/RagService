namespace Rag.Core.Abstractions;

public interface ITextExtractor
{
    Task<string> ExtractAsync(string filePath, CancellationToken ct);
}
