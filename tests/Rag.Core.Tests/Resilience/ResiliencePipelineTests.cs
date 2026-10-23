using Microsoft.Extensions.Time.Testing;
using Rag.Core.Resilience;

namespace Rag.Core.Tests.Resilience;

public class ResiliencePipelineTests
{
    private static readonly Func<Exception, bool> HandleIo = ex => ex is IOException;

    private static ResiliencePipeline Create(
        FakeTimeProvider time,
        int maxRetries = 3,
        TimeSpan? timeout = null,
        CircuitBreakerOptions? breaker = null) =>
        new(
            breaker ?? new CircuitBreakerOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 3,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(20),
                ShouldHandle = HandleIo
            },
            new RetryOptions
            {
                MaxRetryAttempts = maxRetries,
                Delay = TimeSpan.FromSeconds(1),
                UseJitter = false,
                ShouldHandle = HandleIo
            },
            timeout ?? TimeSpan.FromSeconds(30),
            time);

    // Advances the fake clock until the pending task completes (retry delays use the TimeProvider).
    private static async Task<T> Drive<T>(FakeTimeProvider time, Task<T> task)
    {
        for (var i = 0; i < 200 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            // FakeTimeProvider fires timers synchronously, but continuations after Task.Delay/CTS timers are not
            // guaranteed to run inline: give them a (real) moment instead of racing with Task.Yield.
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(25)));
        }
        Assert.True(task.IsCompleted, "Drive: task did not complete within 200 clock steps");
        return await task;
    }

    private static async Task AdvanceAndSettle(FakeTimeProvider time, TimeSpan by)
    {
        time.Advance(by);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
    }

    private static CircuitBreakerOptions Breaker(int minimumThroughput = 3, double ratio = 0.5, Func<Exception, bool>? handle = null) =>
        new()
        {
            FailureRatio = ratio,
            MinimumThroughput = minimumThroughput,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(20),
            ShouldHandle = handle ?? HandleIo
        };

    private static async Task Fail(ResiliencePipeline pipeline) =>
        await Assert.ThrowsAsync<IOException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

    private static async Task Succeed(ResiliencePipeline pipeline) =>
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None);

    private static async Task AssertClosed(ResiliencePipeline pipeline)
    {
        var called = false;
        await pipeline.ExecuteAsync(_ => { called = true; return ValueTask.FromResult(1); }, CancellationToken.None);
        Assert.True(called);
    }

    [Fact]
    public async Task Success_RunsOnce()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time);
        var calls = 0;

        var result = await pipeline.ExecuteAsync(_ => { calls++; return ValueTask.FromResult(42); }, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retry_TransientFailure_RetriesUntilSuccess()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time);
        var calls = 0;

        var task = pipeline.ExecuteAsync<int>(_ =>
        {
            calls++;
            if (calls < 3) throw new IOException("transient");
            return ValueTask.FromResult(7);
        }, CancellationToken.None).AsTask();

        Assert.Equal(7, await Drive(time, task));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Retry_Exhausted_ThrowsLastException_AfterMaxPlusOneCalls()
    {
        var time = new FakeTimeProvider();
        // Breaker must not interfere here.
        var pipeline = Create(time, maxRetries: 3, breaker: new CircuitBreakerOptions
        {
            FailureRatio = 1.0, MinimumThroughput = 100,
            SamplingDuration = TimeSpan.FromSeconds(30), BreakDuration = TimeSpan.FromSeconds(20),
            ShouldHandle = HandleIo
        });
        var calls = 0;

        var task = pipeline.ExecuteAsync<int>(_ => { calls++; throw new IOException("down"); }, CancellationToken.None).AsTask();

        await Assert.ThrowsAsync<IOException>(() => Drive(time, task));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Retry_UnhandledException_IsNotRetried()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => { calls++; throw new InvalidOperationException(); }, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retry_CallerCancellation_StopsImmediately()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time);
        using var cts = new CancellationTokenSource();
        var calls = 0;

        var task = pipeline.ExecuteAsync<int>(_ =>
        {
            calls++;
            cts.Cancel();
            throw new IOException("transient");
        }, cts.Token).AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drive(time, task));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Timeout_Exceeded_ThrowsTimeoutRejected()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0, timeout: TimeSpan.FromSeconds(5));

        var task = pipeline.ExecuteAsync<int>(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
            return 1;
        }, CancellationToken.None).AsTask();

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => Drive(time, task));
    }

    [Fact]
    public async Task Timeout_CallerCancellation_SurfacesAsOperationCanceled()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0, timeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var task = pipeline.ExecuteAsync<int>(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
            return 1;
        }, cts.Token).AsTask();

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Breaker_BelowMinimumThroughput_StaysClosed()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);

        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<IOException>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        // Third call still reaches the callback (throws IOException, not BrokenCircuitException).
        await Assert.ThrowsAsync<IOException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));
    }

    [Fact]
    public async Task Breaker_OpensAfterThresholdAndRejectsWithoutCallingCallback()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<IOException>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        var called = false;
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => { called = true; return ValueTask.FromResult(1); }, CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task Breaker_AfterBreakDuration_ProbeSuccessClosesCircuit()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<IOException>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(21));

        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None));
        Assert.Equal(2, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2), CancellationToken.None));
    }

    [Fact]
    public async Task Breaker_ProbeFailure_ReopensCircuit()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<IOException>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(21));
        await Assert.ThrowsAsync<IOException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task Breaker_ExhaustedRetriesCountAsOneFailure()
    {
        // Breaker is the outer layer: 3 attempts inside one ExecuteAsync == 1 sample.
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 2);

        var task = pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None).AsTask();
        await Assert.ThrowsAsync<IOException>(() => Drive(time, task));

        // One sample only (< MinimumThroughput 3): circuit must still be closed.
        await AssertClosed(pipeline);
    }

    [Fact]
    public async Task Retry_BackoffDoublesEachAttempt()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 3, breaker: Breaker(minimumThroughput: 100));
        var calls = 0;

        var task = pipeline.ExecuteAsync<int>(_ => { calls++; throw new IOException(); }, CancellationToken.None).AsTask();
        Assert.Equal(1, calls);

        await AdvanceAndSettle(time, TimeSpan.FromMilliseconds(999));
        Assert.Equal(1, calls);
        await AdvanceAndSettle(time, TimeSpan.FromMilliseconds(1));   // 1s
        Assert.Equal(2, calls);
        await AdvanceAndSettle(time, TimeSpan.FromSeconds(2));
        Assert.Equal(3, calls);
        await AdvanceAndSettle(time, TimeSpan.FromSeconds(4));
        Assert.Equal(4, calls);

        await Assert.ThrowsAsync<IOException>(() => task);
    }

    [Fact]
    public async Task Breaker_ExactFailureRatio_Opens()
    {
        var pipeline = Create(new FakeTimeProvider(), maxRetries: 0, breaker: Breaker(minimumThroughput: 4, ratio: 0.5));

        await Succeed(pipeline);
        await Fail(pipeline);
        await Succeed(pipeline);
        await Fail(pipeline);   // 2/4 == 0.5 → opens (>=)

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await Succeed(pipeline));
    }

    [Fact]
    public async Task Breaker_SuccessNeverOpensCircuit()
    {
        var pipeline = Create(new FakeTimeProvider(), maxRetries: 0, breaker: Breaker(minimumThroughput: 3, ratio: 0.5));

        await Fail(pipeline);
        await Fail(pipeline);   // 2 samples, below minimum
        await Succeed(pipeline); // 3 samples, 2/3 failed, but the newest outcome is a success

        await AssertClosed(pipeline);
    }

    [Fact]
    public async Task Breaker_SamplesOlderThanWindow_AreIgnored()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0, breaker: Breaker(minimumThroughput: 3));

        await Fail(pipeline);
        await Fail(pipeline);
        time.Advance(TimeSpan.FromSeconds(30));   // exactly SamplingDuration → both expired (exclusive boundary)
        await Fail(pipeline);

        await AssertClosed(pipeline);
    }

    [Fact]
    public async Task Breaker_SamplesJustInsideWindow_StillCount()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0, breaker: Breaker(minimumThroughput: 3));

        await Fail(pipeline);
        await Fail(pipeline);
        time.Advance(TimeSpan.FromSeconds(29));
        await Fail(pipeline);

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await Succeed(pipeline));
    }

    [Fact]
    public async Task Breaker_HalfOpen_RejectsSecondCallWhileProbeRuns()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);
        for (var i = 0; i < 3; i++) await Fail(pipeline);
        time.Advance(TimeSpan.FromSeconds(21));

        var gate = new TaskCompletionSource<int>();
        var probe = pipeline.ExecuteAsync(_ => new ValueTask<int>(gate.Task), CancellationToken.None).AsTask();

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await Succeed(pipeline));

        gate.SetResult(1);
        await probe;
        await AssertClosed(pipeline);
    }

    [Fact]
    public async Task Breaker_CancelledProbe_FreesTheProbeSlot()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);
        for (var i = 0; i < 3; i++) await Fail(pipeline);
        time.Advance(TimeSpan.FromSeconds(21));

        using var cts = new CancellationTokenSource();
        var probe = pipeline.ExecuteAsync<int>(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
            return 1;
        }, cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);

        // Cancellation is inconclusive: circuit stays half-open, and a new probe is allowed (and closes it).
        await Succeed(pipeline);
        await AssertClosed(pipeline);
    }

    [Fact]
    public async Task Breaker_LateFailureWhileOpen_DoesNotExtendBreak()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);

        var gate = new TaskCompletionSource<int>();
        var inFlight = pipeline.ExecuteAsync(_ => new ValueTask<int>(gate.Task), CancellationToken.None).AsTask();

        for (var i = 0; i < 3; i++) await Fail(pipeline);   // circuit opens at t=0

        time.Advance(TimeSpan.FromSeconds(15));
        gate.SetException(new IOException("late"));
        await Assert.ThrowsAsync<IOException>(() => inFlight);

        time.Advance(TimeSpan.FromSeconds(6));               // t=21s > BreakDuration(20s) since the original opening
        await Succeed(pipeline);                              // probe is allowed
    }

    [Fact]
    public async Task Breaker_HungProbe_IsSupersededAfterBreakDuration()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);
        for (var i = 0; i < 3; i++) await Fail(pipeline);
        time.Advance(TimeSpan.FromSeconds(21));

        var hung = new TaskCompletionSource<int>();
        var probe1 = pipeline.ExecuteAsync(_ => new ValueTask<int>(hung.Task), CancellationToken.None).AsTask();
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await Succeed(pipeline));

        time.Advance(TimeSpan.FromSeconds(20));   // probe1 has now been silent for a full BreakDuration
        await Succeed(pipeline);                  // a new probe is let through and closes the circuit

        hung.SetException(new IOException("late"));   // the superseded probe's verdict is irrelevant
        await Assert.ThrowsAsync<IOException>(() => probe1);
        await AssertClosed(pipeline);
    }

    [Fact]
    public async Task Breaker_CallStartedBeforeOpening_CannotCloseHalfOpenCircuit()
    {
        var time = new FakeTimeProvider();
        var pipeline = Create(time, maxRetries: 0);

        var oldGate = new TaskCompletionSource<int>();
        var oldCall = pipeline.ExecuteAsync(_ => new ValueTask<int>(oldGate.Task), CancellationToken.None).AsTask();

        for (var i = 0; i < 3; i++) await Fail(pipeline);
        time.Advance(TimeSpan.FromSeconds(21));

        var probeGate = new TaskCompletionSource<int>();
        var probe = pipeline.ExecuteAsync(_ => new ValueTask<int>(probeGate.Task), CancellationToken.None).AsTask();

        oldGate.SetResult(1);   // succeeds while half-open, but it is not the probe
        await oldCall;
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await Succeed(pipeline));

        probeGate.SetException(new IOException("still down"));
        await Assert.ThrowsAsync<IOException>(() => probe);
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await Succeed(pipeline));   // re-opened
    }

    [Fact]
    public async Task Breaker_UnhandledExceptions_CountAsSuccesses()
    {
        var pipeline = Create(new FakeTimeProvider(), maxRetries: 0, breaker: Breaker(minimumThroughput: 3));

        await Fail(pipeline);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException(), CancellationToken.None));
        await Fail(pipeline);   // 2 failures / 5 calls = 0.4 < 0.5

        await AssertClosed(pipeline);
    }

    [Fact]
    public async Task Breaker_HandledTimeouts_CountAsFailures()
    {
        var time = new FakeTimeProvider();
        var pipeline = new ResiliencePipeline(
            Breaker(minimumThroughput: 2, handle: ex => ex is TimeoutRejectedException),
            new RetryOptions { MaxRetryAttempts = 0, ShouldHandle = _ => false },
            TimeSpan.FromSeconds(5),
            time);

        for (var i = 0; i < 2; i++)
        {
            var task = pipeline.ExecuteAsync<int>(async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, time, token);
                return 1;
            }, CancellationToken.None).AsTask();
            await Assert.ThrowsAsync<TimeoutRejectedException>(() => Drive(time, task));
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await Succeed(pipeline));
    }

    [Fact]
    public void InvalidOptions_AreRejected()
    {
        var time = new FakeTimeProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(time, timeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(time, breaker: Breaker(ratio: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(time, breaker: Breaker(minimumThroughput: 1)));
    }
}
