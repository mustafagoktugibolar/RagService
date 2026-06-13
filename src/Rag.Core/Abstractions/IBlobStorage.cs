namespace Rag.Core.Abstractions;

public interface IBlobStorage
{
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct);
    IAsyncEnumerable<string> ListObjectKeysAsync(string? prefix, CancellationToken ct);
    Task UploadAsync(string objectKey, Stream content, string? contentType, CancellationToken ct);
}
