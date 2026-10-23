using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Rag.Core.Abstractions;
using Rag.Core.Options;
using Rag.Core.Resilience;
using System.Runtime.CompilerServices;

namespace Rag.Core.Providers.Storage;

/// <summary>
/// <see cref="IBlobStorage"/> over the S3 API (official AWS SDK), so it works with any S3-compatible store.
/// Retries/circuit breaking are owned by <see cref="RagPipelines.Storage"/>, not by the SDK.
/// </summary>
public sealed class S3BlobStorage : IBlobStorage, IDisposable
{
    private const string NoSuchBucket = "NoSuchBucket";

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly bool _createBucketIfMissing;
    private int _bucketReady;

    public S3BlobStorage(IOptions<S3Options> options)
    {
        var opts = options.Value;

        var config = new AmazonS3Config
        {
            ForcePathStyle = opts.ForcePathStyle,
            // One retry layer only (RagPipelines.Storage); SDK retries would multiply with ours.
            MaxErrorRetry = 0,
            // Newer SDKs add CRC checksums by default, which several S3-compatible servers reject.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };

        if (string.IsNullOrWhiteSpace(opts.ServiceUrl))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(opts.Region);
        }
        else
        {
            config.ServiceURL = opts.ServiceUrl;
            config.AuthenticationRegion = opts.Region;
        }

        _client = new AmazonS3Client(new BasicAWSCredentials(opts.AccessKey, opts.SecretKey), config);
        _bucket = opts.Bucket;
        _createBucketIfMissing = opts.CreateBucketIfMissing;
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct)
    {
        // Buffer inside the pipeline so a connection dropped mid-body is retried, not surfaced half-read.
        return await RagPipelines.Storage.ExecuteAsync(async token =>
        {
            using var response = await _client.GetObjectAsync(_bucket, objectKey, token).ConfigureAwait(false);
            var memory = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memory, token).ConfigureAwait(false);
            memory.Position = 0;
            return (Stream)memory;
        }, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ListObjectKeysAsync(
        string? prefix,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? continuationToken = null;
        do
        {
            var page = await ListPageAsync(prefix, continuationToken, ct).ConfigureAwait(false);
            if (page is null)
                yield break;   // bucket doesn't exist yet: nothing has been uploaded

            foreach (var item in page.S3Objects ?? [])
                yield return item.Key;

            continuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (continuationToken is not null);
    }

    public async Task UploadAsync(string objectKey, Stream content, string? contentType, CancellationToken ct)
    {
        await EnsureBucketAsync(ct).ConfigureAwait(false);

        // Signing needs the length up front; unseekable streams (e.g. request bodies) are buffered once.
        var body = content;
        MemoryStream? buffered = null;
        if (!content.CanSeek)
        {
            buffered = new MemoryStream();
            await content.CopyToAsync(buffered, ct).ConfigureAwait(false);
            body = buffered;
        }

        try
        {
            var start = body.Position;
            await RagPipelines.Storage.ExecuteAsync(async token =>
            {
                body.Position = start;   // rewind for every retry attempt
                await _client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = objectKey,
                    InputStream = body,
                    AutoCloseStream = false,
                    ContentType = contentType ?? "application/octet-stream"
                }, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    /// <summary>Cheap authenticated round-trip for health checks. A missing bucket still proves the store is reachable.</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, MaxKeys = 1 }, ct)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == NoSuchBucket)
        {
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<ListObjectsV2Response?> ListPageAsync(string? prefix, string? continuationToken, CancellationToken ct)
    {
        try
        {
            return await RagPipelines.Storage.ExecuteAsync(
                async token => await _client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
                    ContinuationToken = continuationToken
                }, token).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == NoSuchBucket)
        {
            return null;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (!_createBucketIfMissing || Volatile.Read(ref _bucketReady) == 1)
            return;

        try
        {
            await RagPipelines.Storage.ExecuteAsync(
                async token => await _client.PutBucketAsync(
                    new PutBucketRequest { BucketName = _bucket, UseClientRegion = true }, token).ConfigureAwait(false),
                ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Created concurrently or already present.
        }

        Volatile.Write(ref _bucketReady, 1);
    }
}
