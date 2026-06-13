using Rag.Core.Abstractions;

namespace Rag.Core.Providers.TextExtraction;

public sealed class FileExtensionTextExtractor(
    PlainTextExtractor plainTextExtractor,
    PdfTextExtractor pdfTextExtractor) : ITextExtractor
{
    public Task<string> ExtractAsync(string filePath, CancellationToken ct)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? pdfTextExtractor.ExtractAsync(filePath, ct)
            : plainTextExtractor.ExtractAsync(filePath, ct);
    }
}
