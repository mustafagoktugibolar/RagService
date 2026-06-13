using System.Text;
using Rag.Core.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Rag.Core.Providers.TextExtraction;

public sealed class PdfTextExtractor(IBlobStorage blobStorage)
{
    public async Task<string> ExtractAsync(string filePath, CancellationToken ct)
    {
        await using var stream = await blobStorage.OpenReadAsync(filePath, ct);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var document = PdfDocument.Open(memory);
        var builder = new StringBuilder();

        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);

            foreach (var block in blocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                builder.AppendLine(block.Text.Trim());
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }
}
