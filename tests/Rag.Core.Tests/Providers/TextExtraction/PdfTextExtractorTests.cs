using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Providers.TextExtraction;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Rag.Core.Tests.Providers.TextExtraction;

public sealed class PdfTextExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ValidPdfStream_ExtractsText()
    {
        var pdfBytes = CreateDummyPdf("Hello PDF World");
        var stream = new MemoryStream(pdfBytes);
        
        var blobStorage = Substitute.For<IBlobStorage>();
        blobStorage.OpenReadAsync("test.pdf", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<Stream>(stream));

        var sut = new PdfTextExtractor(blobStorage);

        var result = await sut.ExtractAsync("test.pdf", CancellationToken.None);

        Assert.Contains("Hello PDF World", result);
    }

    private static byte[] CreateDummyPdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(50, 700), font);
        return builder.Build();
    }
}
