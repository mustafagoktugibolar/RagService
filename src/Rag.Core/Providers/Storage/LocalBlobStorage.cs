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

    /// <summary>
    /// Maps an object key to a file under the storage root. The key comes from a request, so it is untrusted: <c>../</c> segments and
    /// rooted paths (which <see cref="Path.Combine(string, string)"/> would let replace the base entirely) are rejected instead of
    /// being allowed to read or overwrite any file the process can reach. Symbolic links inside the root are not resolved.
    /// </summary>
    private string Resolve(string objectKey)
    {
        var root = Path.GetFullPath(_basePath);
        var full = Path.GetFullPath(Path.Combine(root, objectKey));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && !string.Equals(full, root, StringComparison.Ordinal))
        {
            throw new ArgumentException("The object key resolves outside the storage root.", nameof(objectKey));
        }

        return full;
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = Resolve(objectKey);
        Stream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(stream);
    }

    public async IAsyncEnumerable<string> ListObjectKeysAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var searchPath = string.IsNullOrEmpty(prefix)
            ? Path.GetFullPath(_basePath)
            : Resolve(prefix);

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
        var fullPath = Resolve(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var file = File.Create(fullPath);
        await content.CopyToAsync(file, ct);
    }
}
