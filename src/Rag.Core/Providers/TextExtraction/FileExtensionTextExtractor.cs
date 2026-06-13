using Rag.Core.Abstractions;

namespace Rag.Core.Providers.TextExtraction;

public sealed class FileExtensionTextExtractor(
    PlainTextExtractor plainTextExtractor,
    PdfTextExtractor pdfTextExtractor,
    DocxTextExtractor docxTextExtractor) : ITextExtractor
{
    public Task<string> ExtractAsync(string filePath, CancellationToken ct)
    {
        var extension = Path.GetExtension(filePath);

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return pdfTextExtractor.ExtractAsync(filePath, ct);

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return docxTextExtractor.ExtractAsync(filePath, ct);

        return plainTextExtractor.ExtractAsync(filePath, ct);
    }
}
