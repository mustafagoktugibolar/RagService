using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Rag.Core.Abstractions;

namespace Rag.Core.Providers.TextExtraction;

public sealed class DocxTextExtractor(IBlobStorage blobStorage)
{
    public async Task<string> ExtractAsync(string filePath, CancellationToken ct)
    {
        await using var stream = await blobStorage.OpenReadAsync(filePath, ct);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var document = WordprocessingDocument.Open(memory, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            ct.ThrowIfCancellationRequested();
            var text = paragraph.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;
            builder.AppendLine(text);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
