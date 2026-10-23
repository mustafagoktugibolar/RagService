namespace Rag.Core.Resilience;

/// <summary>One recorded call outcome inside the breaker's sampling window.</summary>
public readonly record struct CircuitSample(DateTimeOffset At, bool Failed);

/// <summary>
/// Thread-safe circuit breaker: Closed → Open (after failure ratio is exceeded) → HalfOpen (one probe) → Closed/Open.
/// Pipelines are static and shared, so all state transitions happen under a lock.
/// </summary>
public sealed class CircuitBreaker
{
    private enum State { Closed, Open, HalfOpen }

    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly List<CircuitSample> _samples = [];
    private State _state = State.Closed;
    private DateTimeOffset _openedAt;
    private bool _probeInFlight;
    private long _probeId;
    private DateTimeOffset _probeStartedAt;

    /// <summary>Ticket for a call that is not the half-open probe.</summary>
    internal const long NotAProbe = 0;

    public CircuitBreaker(CircuitBreakerOptions options, TimeProvider timeProvider)
    {
        options.Validate();
        _options = options;
        _timeProvider = timeProvider;
    }

    internal Func<Exception, bool> ShouldHandle => _options.ShouldHandle;

    /// <summary>
    /// Call before running the protected action. Throws <see cref="BrokenCircuitException"/> if rejected.
    /// Returns a ticket to hand back to <see cref="OnSuccess"/>/<see cref="OnFailure"/>/<see cref="OnInconclusive"/>:
    /// <see cref="NotAProbe"/> for ordinary calls, a non-zero id when this call is the half-open probe.
    /// </summary>
    internal long OnBeforeCall()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();

            switch (_state)
            {
                case State.Open:
                    if (now - _openedAt < _options.BreakDuration)
                        throw new BrokenCircuitException("The circuit is open and the call was rejected.");
                    _state = State.HalfOpen;
                    return StartProbe(now);

                case State.HalfOpen:
                    // A probe that hangs longer than a full break must not block the dependency's recovery forever.
                    if (_probeInFlight && now - _probeStartedAt < _options.BreakDuration)
                        throw new BrokenCircuitException("The circuit is half-open and a probe call is already running.");
                    return StartProbe(now);

                default:
                    return NotAProbe;
            }
        }
    }

    private long StartProbe(DateTimeOffset now)
    {
        _probeInFlight = true;
        _probeStartedAt = now;
        return ++_probeId;
    }

    internal void OnSuccess(long ticket) => Record(ticket, failed: false);

    internal void OnFailure(long ticket) => Record(ticket, failed: true);

    /// <summary>The call ended without a verdict (e.g. caller cancelled): free the probe slot, record nothing.</summary>
    internal void OnInconclusive(long ticket)
    {
        lock (_gate)
        {
            if (_state == State.HalfOpen && ticket == _probeId)
                _probeInFlight = false;
        }
    }

    private void Record(long ticket, bool failed)
    {
        lock (_gate)
        {
            // Calls that were already in flight when the circuit opened finish late; they must not re-open the
            // circuit or push out the break.
            if (_state == State.Open)
                return;

            var now = _timeProvider.GetUtcNow();

            if (_state == State.HalfOpen)
            {
                // Only the current probe decides the outcome: a call that started before the circuit opened, or a
                // probe that was superseded after hanging, says nothing about the dependency's current health.
                if (ticket != _probeId)
                    return;

                _probeInFlight = false;
                if (failed)
                {
                    Open(now);
                }
                else
                {
                    _state = State.Closed;
                    _samples.Clear();
                }
                return;
            }

            _samples.Add(new CircuitSample(now, failed));
            if (ShouldBreak(_samples, now))
                Open(now);
        }
    }

    private void Open(DateTimeOffset now)
    {
        _state = State.Open;
        _openedAt = now;
        _samples.Clear();
    }

    /// <summary>
    /// Exact sliding window: a sample counts while <c>now - At &lt; SamplingDuration</c>; the circuit
    /// opens when the newest outcome is a failure, the window holds at least <c>MinimumThroughput</c> calls and
    /// <c>failures / total &gt;= FailureRatio</c>. Expired samples are pruned in place (list is only touched under the lock).
    /// </summary>
    private bool ShouldBreak(List<CircuitSample> samples, DateTimeOffset now)
    {
        var expired = 0;
        while (expired < samples.Count && now - samples[expired].At >= _options.SamplingDuration)
            expired++;
        if (expired > 0)
            samples.RemoveRange(0, expired);

        // Only a failure can open the circuit; otherwise F, F, S would open on a success.
        if (samples.Count == 0 || !samples[^1].Failed)
            return false;
        if (samples.Count < _options.MinimumThroughput)
            return false;

        var failures = 0;
        foreach (var s in samples)
            if (s.Failed)
                failures++;

        return (double)failures / samples.Count >= _options.FailureRatio;
    }
}
