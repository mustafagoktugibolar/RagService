using Microsoft.Extensions.Options;
using Rag.Core.Models;
using Rag.Core.Options;
using Rag.Core.Providers.Chunking;

namespace Rag.Core.Tests.Providers.Chunking;

public sealed class DataIngestionTokenChunkerTests
{
    private static DataIngestionTokenChunker Build(int chunkSize = 1000, int overlap = 200, string model = "text-embedding-3-small")
    {
        var chunkingOptions = Microsoft.Extensions.Options.Options.Create(new ChunkingOptions
        {
            ChunkSize = chunkSize,
            ChunkOverlap = overlap
        });

        var ragOptions = Microsoft.Extensions.Options.Options.Create(new RagOptions
        {
            EmbeddingModel = model
        });

        return new DataIngestionTokenChunker(chunkingOptions, ragOptions);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\n")]
    public async Task ChunkAsync_EmptyOrWhitespaceText_ReturnsEmpty(string text)
    {
        var sut = Build();

        var result = await sut.ChunkAsync("doc1", text, null, CancellationToken.None);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ChunkAsync_ChunkSizeZeroOrNegative_Throws(int chunkSize)
    {
        var sut = Build(chunkSize: chunkSize);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChunkAsync("doc1", "text", null, CancellationToken.None));
    }

    [Fact]
    public async Task ChunkAsync_OverlapNegative_Throws()
    {
        var sut = Build(overlap: -1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChunkAsync("doc1", "text", null, CancellationToken.None));
    }

    [Fact]
    public async Task ChunkAsync_OverlapEqualsChunkSize_Throws()
    {
        var sut = Build(chunkSize: 100, overlap: 100);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ChunkAsync("doc1", "text", null, CancellationToken.None));
    }

    [Fact]
    public async Task ChunkAsync_ShortText_ReturnsSingleChunk()
    {
        var sut = Build(chunkSize: 1000, overlap: 200);

        var result = await sut.ChunkAsync("doc1", "This is a short text.", null, CancellationToken.None);

        var chunk = Assert.Single(result);
        Assert.Equal("This is a short text.", chunk.Text);
        Assert.Equal("doc1", chunk.DocumentId);
        Assert.Equal("doc1:000000", chunk.ChunkId);
    }

    [Fact]
    public async Task ChunkAsync_SetsDocumentIdOnAllChunks()
    {
        var sut = Build(chunkSize: 5, overlap: 0);

        var result = await sut.ChunkAsync(
            "my-doc-123", 
            "word1 word2 word3 word4 word5 word6 word7 word8 word9 word10 word11", 
            null, 
            CancellationToken.None);

        Assert.True(result.Count > 1, "Should produce multiple chunks");
        Assert.All(result, chunk => Assert.Equal("my-doc-123", chunk.DocumentId));
    }

    [Fact]
    public async Task ChunkAsync_ChunkIdFollowsExpectedFormat()
    {
        var sut = Build(chunkSize: 5, overlap: 0);

        var result = await sut.ChunkAsync(
            "doc2", 
            "word1 word2 word3 word4 word5 word6 word7 word8 word9 word10 word11", 
            null, 
            CancellationToken.None);

        Assert.True(result.Count > 1, "Should produce multiple chunks");
        Assert.Equal("doc2:000000", result[0].ChunkId);
        Assert.Equal("doc2:000001", result[1].ChunkId);
    }

    [Fact]
    public async Task ChunkAsync_MetadataCopiedToEachChunk()
    {
        var sut = Build(chunkSize: 5, overlap: 0);
        var metadata = new Dictionary<string, string> { { "key1", "value1" } };

        var result = await sut.ChunkAsync(
            "doc1", 
            "word1 word2 word3 word4 word5 word6 word7 word8 word9 word10 word11", 
            metadata, 
            CancellationToken.None);

        Assert.True(result.Count > 1);
        Assert.All(result, chunk => 
        {
            Assert.NotNull(chunk.Metadata);
            Assert.Equal("value1", chunk.Metadata["key1"]);
            Assert.NotSame(metadata, chunk.Metadata); // verify it's a copy
        });
    }

    [Fact]
    public async Task ChunkAsync_NullMetadata_ChunksHaveNullMetadata()
    {
        var sut = Build(chunkSize: 5, overlap: 0);

        var result = await sut.ChunkAsync(
            "doc1", 
            "word1 word2 word3 word4 word5 word6 word7 word8 word9 word10 word11", 
            null, 
            CancellationToken.None);

        Assert.True(result.Count > 1);
        Assert.All(result, chunk => Assert.Null(chunk.Metadata));
    }
}
