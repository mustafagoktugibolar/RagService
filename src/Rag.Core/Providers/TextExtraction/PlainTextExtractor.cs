using System.Text;
using Rag.Core.Abstractions;

namespace Rag.Core.Providers.TextExtraction;

public sealed class PlainTextExtractor(IBlobStorage blobStorage)
{
    public async Task<string> ExtractAsync(string filePath, CancellationToken ct)
    {
        await using var stream = await blobStorage.OpenReadAsync(filePath, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }
}
