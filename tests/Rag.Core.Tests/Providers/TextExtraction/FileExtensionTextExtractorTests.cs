using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Providers.TextExtraction;

namespace Rag.Core.Tests.Providers.TextExtraction;

public sealed class FileExtensionTextExtractorTests
{
    private static (FileExtensionTextExtractor Sut, IBlobStorage BlobStorage) Build()
    {
        var blobStorage = Substitute.For<IBlobStorage>();
        blobStorage.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<Stream>(new MemoryStream()));

        var plain = new PlainTextExtractor(blobStorage);
        var pdf = new PdfTextExtractor(blobStorage);
        var docx = new DocxTextExtractor(blobStorage);
        var sut = new FileExtensionTextExtractor(plain, pdf, docx);

        return (sut, blobStorage);
    }

    [Theory]
    [InlineData("test.txt")]
    [InlineData("test.md")]
    [InlineData("test")]
    [InlineData("test.unknown")]
    public async Task ExtractAsync_UnknownOrTxtExtension_DelegatesToPlainTextExtractor(string filePath)
    {
        var (sut, blobStorage) = Build();

        // Plain text extractor succeeds with empty stream and returns empty string
        var result = await sut.ExtractAsync(filePath, CancellationToken.None);

        Assert.Equal(string.Empty, result);
        await blobStorage.Received(1).OpenReadAsync(filePath, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("test.pdf")]
    [InlineData("test.PDF")]
    [InlineData("TEST.PdF")]
    public async Task ExtractAsync_PdfExtension_DelegatesToPdfExtractor(string filePath)
    {
        var (sut, blobStorage) = Build();

        // PdfPig will throw on empty stream, which proves it was dispatched to PdfTextExtractor
        await Assert.ThrowsAnyAsync<Exception>(() => sut.ExtractAsync(filePath, CancellationToken.None));
        
        await blobStorage.Received(1).OpenReadAsync(filePath, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("test.docx")]
    [InlineData("test.DOCX")]
    public async Task ExtractAsync_DocxExtension_DelegatesToDocxExtractor(string filePath)
    {
        var (sut, blobStorage) = Build();

        // OpenXML will throw on empty stream, which proves it was dispatched to DocxTextExtractor
        await Assert.ThrowsAnyAsync<Exception>(() => sut.ExtractAsync(filePath, CancellationToken.None));
        
        await blobStorage.Received(1).OpenReadAsync(filePath, Arg.Any<CancellationToken>());
    }
}
