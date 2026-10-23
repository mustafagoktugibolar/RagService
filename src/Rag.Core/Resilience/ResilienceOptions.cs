namespace Rag.Core.Resilience;

public sealed class RetryOptions
{
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>Base delay; attempt n waits Delay * 2^n (exponential backoff), capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Scales each delay by a random factor in [0.5, 1.5) to avoid thundering herds.</summary>
    public bool UseJitter { get; init; } = true;

    public required Func<Exception, bool> ShouldHandle { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetryAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(Delay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDelay, TimeSpan.Zero);
    }
}

public sealed class CircuitBreakerOptions
{
    /// <summary>Failure ratio (0..1] within the sampling window at which the circuit opens.</summary>
    public double FailureRatio { get; init; } = 0.5;

    /// <summary>Minimum number of calls in the sampling window before the ratio is evaluated.</summary>
    public int MinimumThroughput { get; init; } = 5;

    public TimeSpan SamplingDuration { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    public required Func<Exception, bool> ShouldHandle { get; init; }

    internal void Validate()
    {
        if (FailureRatio is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(FailureRatio), FailureRatio, "Must be in (0, 1].");
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumThroughput, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(SamplingDuration, TimeSpan.FromMilliseconds(500));
        ArgumentOutOfRangeException.ThrowIfLessThan(BreakDuration, TimeSpan.FromMilliseconds(500));
    }
}
