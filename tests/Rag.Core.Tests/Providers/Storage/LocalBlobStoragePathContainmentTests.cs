using Microsoft.Extensions.Options;
using Rag.Core.Options;
using Rag.Core.Providers.Storage;

namespace Rag.Core.Tests.Providers.Storage;

public sealed class LocalBlobStoragePathContainmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rag-local-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside;

    public LocalBlobStoragePathContainmentTests()
    {
        Directory.CreateDirectory(_root);
        _outside = Path.Combine(Path.GetTempPath(), "rag-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(_outside, "secret outside the root");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        File.Delete(_outside);
    }

    private LocalBlobStorage Storage() => new(Microsoft.Extensions.Options.Options.Create(new LocalStorageOptions { BasePath = Path.Combine(_root, "store") }));

    [Fact]
    public async Task ANestedKey_RoundTrips()
    {
        var storage = Storage();
        await storage.UploadAsync("docs/a/b.txt", new MemoryStream("hello"u8.ToArray()), "text/plain", CancellationToken.None);

        await using var read = await storage.OpenReadAsync("docs/a/b.txt", CancellationToken.None);
        using var reader = new StreamReader(read);

        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("docs/../../outside.txt")]
    [InlineData("..")]
    public async Task ATraversalKey_IsRejected_ForReadAndWrite(string key)
    {
        var storage = Storage();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.UploadAsync(key, new MemoryStream([1]), null, CancellationToken.None));
    }

    [Fact]
    public async Task AnAbsolutePathKey_CannotReadAFileOutsideTheRoot()
    {
        var storage = Storage();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(_outside, CancellationToken.None));
    }

    [Fact]
    public async Task ASiblingDirectoryWithTheSamePrefix_IsNotInsideTheRoot()
    {
        var storage = Storage();
        Directory.CreateDirectory(Path.Combine(_root, "store-evil"));
        File.WriteAllText(Path.Combine(_root, "store-evil", "x.txt"), "x");

        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync("../store-evil/x.txt", CancellationToken.None));
    }

    [Fact]
    public async Task AListPrefixOutsideTheRoot_IsRejected()
    {
        var storage = Storage();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in storage.ListObjectKeysAsync("../", CancellationToken.None))
            {
            }
        });
    }
}
