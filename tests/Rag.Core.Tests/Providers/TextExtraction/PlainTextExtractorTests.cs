using System.Text;
using NSubstitute;
using Rag.Core.Abstractions;
using Rag.Core.Providers.TextExtraction;

namespace Rag.Core.Tests.Providers.TextExtraction;

public sealed class PlainTextExtractorTests
{
    [Fact]
    public async Task ExtractAsync_ReturnsTextFromStream()
    {
        var blobStorage = Substitute.For<IBlobStorage>();
        var content = "This is a simple text file.";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        
        blobStorage.OpenReadAsync("test.txt", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<Stream>(stream));

        var sut = new PlainTextExtractor(blobStorage);

        var result = await sut.ExtractAsync("test.txt", CancellationToken.None);

        Assert.Equal(content, result);
    }
}
