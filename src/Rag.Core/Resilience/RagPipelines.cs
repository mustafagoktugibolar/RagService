using Grpc.Core;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Rag.Core.Resilience;

internal static class RagPipelines
{
    internal static readonly ResiliencePipeline ExternalApi = new ResiliencePipelineBuilder()
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

    internal static readonly ResiliencePipeline Qdrant = new ResiliencePipelineBuilder()
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

    internal static readonly ResiliencePipeline Storage = new ResiliencePipelineBuilder()
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
