using Grpc.Core;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Rag.Core.Resilience;

internal static class RagPipelines
{
    // OpenAI embeddings: circuit opens after 50% failures in 60s window (min 5 calls), stays open 30s
    internal static readonly ResiliencePipeline ExternalApi = new ResiliencePipelineBuilder()
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            SamplingDuration = TimeSpan.FromSeconds(60),
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .Handle<IOException>()
        })
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .Handle<IOException>()
        })
        .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(60) })
        .Build();

    // Qdrant: circuit opens faster (min 3 calls) since gRPC errors are usually infra-level
    internal static readonly ResiliencePipeline Qdrant = new ResiliencePipelineBuilder()
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 3,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(20),
            ShouldHandle = new PredicateBuilder()
                .Handle<RpcException>(ex => ex.StatusCode is
                    StatusCode.Unavailable or
                    StatusCode.ResourceExhausted or
                    StatusCode.DeadlineExceeded)
                .Handle<HttpRequestException>()
        })
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder()
                .Handle<RpcException>(ex => ex.StatusCode is
                    StatusCode.Unavailable or
                    StatusCode.ResourceExhausted or
                    StatusCode.DeadlineExceeded)
                .Handle<HttpRequestException>()
        })
        .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) })
        .Build();

    // MinIO: same shape as Qdrant
    internal static readonly ResiliencePipeline Storage = new ResiliencePipelineBuilder()
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 3,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(20),
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<IOException>()
                .Handle<TimeoutRejectedException>()
        })
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<IOException>()
                .Handle<TimeoutRejectedException>()
        })
        .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) })
        .Build();
}
