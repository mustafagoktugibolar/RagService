using Amazon.Runtime;
using Grpc.Core;
using System.Net;
using System.Net.Sockets;

namespace Rag.Core.Resilience;

internal static class RagPipelines
{
    private static bool IsTransientHttp(Exception ex) =>
        ex is HttpRequestException or TimeoutRejectedException or IOException;

    private static bool IsTransientGrpc(Exception ex) =>
        ex is HttpRequestException or TimeoutRejectedException
        || ex is RpcException { StatusCode: StatusCode.Unavailable or StatusCode.ResourceExhausted or StatusCode.DeadlineExceeded };

    // The AWS SDK wraps network failures in AmazonClientException and reports server errors as AmazonServiceException.
    private static bool IsTransientStorage(Exception ex) =>
        IsTransientHttp(ex)
        || ex is AmazonServiceException { StatusCode: var status }
            && (status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests || (int)status >= 500)
        || ex is AmazonClientException
            && ex.InnerException is HttpRequestException or IOException or SocketException;

    private static ResiliencePipeline Create(
        Func<Exception, bool> shouldHandle,
        int minimumThroughput,
        TimeSpan samplingDuration,
        TimeSpan breakDuration,
        TimeSpan timeout) =>
        new(
            new CircuitBreakerOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = minimumThroughput,
                SamplingDuration = samplingDuration,
                BreakDuration = breakDuration,
                ShouldHandle = shouldHandle
            },
            new RetryOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = true,
                ShouldHandle = shouldHandle
            },
            timeout);

    // OpenAI embeddings: circuit opens after 50% failures in 60s window (min 5 calls), stays open 30s
    internal static readonly ResiliencePipeline ExternalApi = Create(
        IsTransientHttp,
        minimumThroughput: 5,
        samplingDuration: TimeSpan.FromSeconds(60),
        breakDuration: TimeSpan.FromSeconds(30),
        timeout: TimeSpan.FromSeconds(60));

    // Qdrant: circuit opens faster (min 3 calls) since gRPC errors are usually infra-level
    internal static readonly ResiliencePipeline Qdrant = Create(
        IsTransientGrpc,
        minimumThroughput: 3,
        samplingDuration: TimeSpan.FromSeconds(30),
        breakDuration: TimeSpan.FromSeconds(20),
        timeout: TimeSpan.FromSeconds(30));

    // Object storage (any S3-compatible store): same shape as Qdrant
    internal static readonly ResiliencePipeline Storage = Create(
        IsTransientStorage,
        minimumThroughput: 3,
        samplingDuration: TimeSpan.FromSeconds(30),
        breakDuration: TimeSpan.FromSeconds(20),
        timeout: TimeSpan.FromSeconds(30));
}
