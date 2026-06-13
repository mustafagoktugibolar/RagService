using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Options;
using System.Runtime.CompilerServices;

namespace Rag.Core.Providers.Storage;

public sealed class LocalBlobStorage : IBlobStorage
{
    private readonly string _basePath;

    public LocalBlobStorage(IOptions<LocalStorageOptions> options)
    {
        _basePath = options.Value.BasePath;
        Directory.CreateDirectory(_basePath);
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.Combine(_basePath, objectKey);
        Stream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public async IAsyncEnumerable<string> ListObjectKeysAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var searchPath = string.IsNullOrEmpty(prefix)
            ? _basePath
            : Path.Combine(_basePath, prefix);

        if (!Directory.Exists(searchPath))
            yield break;

        foreach (var file in Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            yield return Path.GetRelativePath(_basePath, file);
        }
    }

    public async Task UploadAsync(string objectKey, Stream content, string? contentType, CancellationToken ct)
    {
        var fullPath = Path.Combine(_basePath, objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file, ct);
    }
}
