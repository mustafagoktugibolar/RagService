using Grpc.Core;

namespace Rag.Core.Resilience;

/// <summary>
/// Minimal in-house resilience pipeline: CircuitBreaker (outer) → Retry → Timeout (per attempt).
/// </summary>
public sealed class ResiliencePipeline
{
    private readonly CircuitBreaker _breaker;
    private readonly RetryOptions _retry;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    public ResiliencePipeline(
        CircuitBreakerOptions breaker,
        RetryOptions retry,
        TimeSpan timeout,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        retry.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _breaker = new CircuitBreaker(breaker, _timeProvider);
        _retry = retry;
        _timeout = timeout;
    }

    public async ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> callback, CancellationToken ct)
    {
        await ExecuteAsync<object?>(async token =>
        {
            await callback(token).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> callback, CancellationToken ct)
    {
        var ticket = _breaker.OnBeforeCall();
        try
        {
            var result = await RetryAsync(callback, ct).ConfigureAwait(false);
            _breaker.OnSuccess(ticket);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _breaker.OnInconclusive(ticket);
            throw;
        }
        catch (Exception ex) when (_breaker.ShouldHandle(ex))
        {
            _breaker.OnFailure(ticket);
            throw;
        }
        catch
        {
            // Not a downstream-health signal (e.g. validation error): count it as a success.
            _breaker.OnSuccess(ticket);
            throw;
        }
    }

    private async ValueTask<T> RetryAsync<T>(Func<CancellationToken, ValueTask<T>> callback, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await AttemptWithTimeoutAsync(callback, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Whatever the callback threw (IOException, RpcException(Cancelled), ...), the caller asked to stop:
                // surface it as cancellation so it is neither retried nor counted against the circuit.
                ct.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception ex) when (attempt < _retry.MaxRetryAttempts && _retry.ShouldHandle(ex))
            {
                await Task.Delay(ComputeDelay(attempt), _timeProvider, ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<T> AttemptWithTimeoutAsync<T>(Func<CancellationToken, ValueTask<T>> callback, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await callback(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            timeoutCts.IsCancellationRequested
            && !ct.IsCancellationRequested
            // Grpc.Net.Client reports a cancelled token as RpcException(Cancelled) rather than OperationCanceledException.
            && ex is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled })
        {
            throw new TimeoutRejectedException($"The operation didn't complete within the allowed timeout of '{_timeout}'.", ex);
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var factor = Math.Pow(2, Math.Min(attempt, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(_retry.Delay.Ticks * factor, _retry.MaxDelay.Ticks));
        return _retry.UseJitter ? delay * (0.5 + Random.Shared.NextDouble()) : delay;
    }
}
