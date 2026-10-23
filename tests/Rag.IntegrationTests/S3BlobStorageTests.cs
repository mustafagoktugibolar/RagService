using Microsoft.Extensions.DependencyInjection;
using Rag.Core.Abstractions;
using Rag.Core.Providers.Storage;
using Xunit;

namespace Rag.IntegrationTests;

/// <summary>Contract tests for IBlobStorage over a real S3-compatible store (SeaweedFS in the fixture).</summary>
[Collection("Integration")]
public class S3BlobStorageTests(IntegrationTestWebAppFactory factory) : IClassFixture<IntegrationTestWebAppFactory>
{
    private IBlobStorage Storage => factory.Services.GetRequiredService<IBlobStorage>();

    private static Stream Bytes(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAll(Stream s)
    {
        await using (s)
        using (var reader = new StreamReader(s))
            return await reader.ReadToEndAsync();
    }

    [Fact]
    public void S3Provider_IsWired()
    {
        Assert.IsType<S3BlobStorage>(Storage);
    }

    [Fact]
    public async Task List_BeforeBucketExists_IsEmpty()
    {
        // Nothing has been uploaded to this storage instance's bucket by this test.
        var keys = new List<string>();
        await foreach (var k in Storage.ListObjectKeysAsync($"never-written-{Guid.NewGuid():N}/", CancellationToken.None))
            keys.Add(k);

        Assert.Empty(keys);
    }

    [Fact]
    public async Task List_FiltersByPrefix_AndPagesThroughMoreThanOnePage()
    {
        var run = Guid.NewGuid().ToString("N");
        // 1100 > the 1000-key S3 page size, so continuation tokens are exercised.
        await Parallel.ForEachAsync(Enumerable.Range(0, 1100), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, ct) => await Storage.UploadAsync($"{run}/a/{i:D4}.txt", Bytes("x"), "text/plain", ct));
        await Storage.UploadAsync($"{run}/b/other.txt", Bytes("y"), "text/plain", CancellationToken.None);

        var keys = new List<string>();
        await foreach (var k in Storage.ListObjectKeysAsync($"{run}/a/", CancellationToken.None))
            keys.Add(k);

        Assert.Equal(1100, keys.Count);
        Assert.All(keys, k => Assert.StartsWith($"{run}/a/", k));
        Assert.Equal(1100, keys.Distinct().Count());
    }

    [Fact]
    public async Task Upload_NonSeekableStream_RoundTrips()
    {
        var key = $"nonseekable/{Guid.NewGuid():N}.txt";
        await using var pipe = new NonSeekableStream(Bytes("streamed body"));

        await Storage.UploadAsync(key, pipe, "text/plain", CancellationToken.None);

        Assert.Equal("streamed body", await ReadAll(await Storage.OpenReadAsync(key, CancellationToken.None)));
    }

    [Fact]
    public async Task Upload_SameKeyTwice_Overwrites()
    {
        var key = $"overwrite/{Guid.NewGuid():N}.txt";
        await Storage.UploadAsync(key, Bytes("first"), "text/plain", CancellationToken.None);
        await Storage.UploadAsync(key, Bytes("second"), "text/plain", CancellationToken.None);

        Assert.Equal("second", await ReadAll(await Storage.OpenReadAsync(key, CancellationToken.None)));
    }

    [Fact]
    public async Task OpenRead_MissingKey_Throws_WithoutLongRetryStorm()
    {
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Storage.OpenReadAsync($"missing/{Guid.NewGuid():N}.txt", CancellationToken.None));

        // 404 is not transient: it must fail fast instead of burning the 1s+2s+4s backoff.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
