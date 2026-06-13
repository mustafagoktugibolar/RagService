using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Providers.TextExtraction;

namespace Rag.Core.Tests.Providers.TextExtraction;

public sealed class DocxTextExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ValidDocxStream_ExtractsText()
    {
        var docxBytes = CreateDummyDocx("Hello DOCX World");
        var stream = new MemoryStream(docxBytes);
        
        var blobStorage = Substitute.For<IBlobStorage>();
        blobStorage.OpenReadAsync("test.docx", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<Stream>(stream));

        var sut = new DocxTextExtractor(blobStorage);

        var result = await sut.ExtractAsync("test.docx", CancellationToken.None);

        Assert.Contains("Hello DOCX World", result);
    }

    private static byte[] CreateDummyDocx(string text)
    {
        using var memory = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(memory, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
            mainPart.Document.Save();
        }
        return memory.ToArray();
    }
}
