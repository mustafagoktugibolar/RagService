using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Rag.Core.Abstractions;
using Rag.Core.Options;
using Rag.Core.Resilience;
using System.Runtime.CompilerServices;

namespace Rag.Core.Providers.Storage;

public sealed class MinioBlobStorage : IBlobStorage
{
    private readonly IMinioClient _client;
    private readonly string _bucket;

    public MinioBlobStorage(IOptions<MinioOptions> options)
    {
        var opts = options.Value;

        var builder = new MinioClient()
            .WithEndpoint(opts.Endpoint)
            .WithCredentials(opts.AccessKey, opts.SecretKey);

        if (opts.WithSsl)
            builder = builder.WithSSL();

        _client = builder.Build();
        _bucket = opts.Bucket;
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct)
    {
        var memory = new MemoryStream();

        var args = new GetObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithCallbackStream(async (stream, cancellationToken) =>
            {
                await stream.CopyToAsync(memory, cancellationToken);
            });

        await RagPipelines.Storage.ExecuteAsync(
            async token => await _client.GetObjectAsync(args, token), ct);
        memory.Position = 0;
        return memory;
    }

    public async IAsyncEnumerable<string> ListObjectKeysAsync(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var exists = await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), ct);
        if (!exists)
            yield break;

        var args = new ListObjectsArgs()
            .WithBucket(_bucket)
            .WithRecursive(true);

        if (!string.IsNullOrEmpty(prefix))
            args = args.WithPrefix(prefix);

        await foreach (var item in _client.ListObjectsEnumAsync(args, ct))
        {
            yield return item.Key;
        }
    }

    public async Task UploadAsync(string objectKey, Stream content, string? contentType, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);

        var args = new PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(content.CanSeek ? content.Length : -1)
            .WithContentType(contentType ?? "application/octet-stream");

        await RagPipelines.Storage.ExecuteAsync(
            async token => await _client.PutObjectAsync(args, token), ct);
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        var exists = await _client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(_bucket), ct);

        if (!exists)
        {
            await _client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(_bucket), ct);
        }
    }
}
