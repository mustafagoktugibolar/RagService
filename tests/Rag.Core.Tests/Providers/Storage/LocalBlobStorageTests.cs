using System.Text;
using Microsoft.Extensions.Options;
using Rag.Core.Options;
using Rag.Core.Providers.Storage;

namespace Rag.Core.Tests.Providers.Storage;

public sealed class LocalBlobStorageTests : IDisposable
{
    private readonly string _tempPath;
    private readonly LocalBlobStorage _sut;

    public LocalBlobStorageTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "rag-tests", Guid.NewGuid().ToString());
        var options = Microsoft.Extensions.Options.Options.Create(new LocalStorageOptions { BasePath = _tempPath });
        _sut = new LocalBlobStorage(options);
    }

    [Fact]
    public async Task UploadAsync_And_OpenReadAsync_WorksCorrectly()
    {
        var content = "test content";
        var bytes = Encoding.UTF8.GetBytes(content);
        using var uploadStream = new MemoryStream(bytes);

        await _sut.UploadAsync("folder/file.txt", uploadStream, "text/plain", CancellationToken.None);

        await using var downloadStream = await _sut.OpenReadAsync("folder/file.txt", CancellationToken.None);
        using var reader = new StreamReader(downloadStream);
        var result = await reader.ReadToEndAsync();

        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ListObjectKeysAsync_ReturnsCorrectKeys()
    {
        var content = new MemoryStream("data"u8.ToArray());
        await _sut.UploadAsync("foo/bar/file1.txt", content, null, CancellationToken.None);
        
        content.Position = 0;
        await _sut.UploadAsync("foo/file2.txt", content, null, CancellationToken.None);

        var keys = new List<string>();
        await foreach (var key in _sut.ListObjectKeysAsync("foo", CancellationToken.None))
        {
            keys.Add(key);
        }

        Assert.Contains("foo/bar/file1.txt", keys);
        Assert.Contains("foo/file2.txt", keys);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, recursive: true);
        }
    }
}
