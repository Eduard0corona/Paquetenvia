using Paqueteria.Application.Messaging;

namespace Paqueteria.Infrastructure.Messaging;

/// <summary>
/// AI-03 §16 resilience for one messaging provider: a circuit breaker for a degraded provider and a
/// bulkhead (concurrency limit). A call rejected by either never reaches the provider and returns a
/// transient result, so the outbox retries it later under its own backoff. One instance per adapter.
/// </summary>
internal sealed class MessagingProviderGuard(
    TimeProvider timeProvider,
    int failureThreshold,
    TimeSpan breakDuration,
    int maxConcurrentRequests) : IDisposable
{
    private readonly MessagingCircuitBreaker _breaker = new(timeProvider, failureThreshold, breakDuration);
    private readonly SemaphoreSlim _bulkhead = new(maxConcurrentRequests, maxConcurrentRequests);

    internal MessagingCircuitBreaker CircuitBreaker => _breaker;

    /// <summary>
    /// Runs <paramref name="send"/> when the circuit and the bulkhead allow it. Transient and ambiguous
    /// outcomes count as provider failures; accepted and permanent outcomes (the provider answered)
    /// close the circuit. A call cancelled by the caller records nothing.
    /// </summary>
    public async ValueTask<(MessagingResult Result, int? Status)> RunAsync(
        Func<CancellationToken, ValueTask<(MessagingResult Result, int? Status)>> send,
        CancellationToken cancellationToken)
    {
        if (!_breaker.TryAcquire())
        {
            return (new(MessagingOutcome.TransientFailure, MessagingResultCodes.CircuitOpen, RetryAfter: breakDuration), null);
        }

        if (!_bulkhead.Wait(0, CancellationToken.None))
        {
            _breaker.Release();
            return (new(MessagingOutcome.TransientFailure, MessagingResultCodes.ConcurrencyLimited), null);
        }

        var recorded = false;
        try
        {
            var outcome = await send(cancellationToken).ConfigureAwait(false);
            if (outcome.Result.Outcome is MessagingOutcome.TransientFailure or MessagingOutcome.AmbiguousTimeout)
            {
                _breaker.RecordFailure();
            }
            else
            {
                _breaker.RecordSuccess();
            }

            recorded = true;
            return outcome;
        }
        finally
        {
            if (!recorded)
            {
                _breaker.Release();
            }

            _bulkhead.Release();
        }
    }

    public void Dispose() => _bulkhead.Dispose();
}

/// <summary>
/// After <c>threshold</c> consecutive failed calls the circuit opens for <c>breakDuration</c>; then
/// exactly one half-open probe is allowed, which closes it on success or reopens it on failure.
/// Thread-safe. Same behaviour as the GATE-003 Google Maps breaker.
/// </summary>
internal sealed class MessagingCircuitBreaker(TimeProvider timeProvider, int threshold, TimeSpan breakDuration)
{
    private readonly Lock _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private bool _probeInFlight;

    internal enum State
    {
        Closed,
        Open,
        HalfOpen,
    }

    internal State CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _openUntil is null ? State.Closed
                    : timeProvider.GetUtcNow() < _openUntil ? State.Open
                    : State.HalfOpen;
            }
        }
    }

    /// <summary><see langword="false"/> while open, or while the single half-open probe is running.</summary>
    internal bool TryAcquire()
    {
        lock (_gate)
        {
            if (_openUntil is null)
            {
                return true;
            }

            if (timeProvider.GetUtcNow() < _openUntil || _probeInFlight)
            {
                return false;
            }

            _probeInFlight = true;
            return true;
        }
    }

    internal void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
            _probeInFlight = false;
        }
    }

    internal void RecordFailure()
    {
        lock (_gate)
        {
            if (_probeInFlight || ++_consecutiveFailures >= threshold)
            {
                _openUntil = timeProvider.GetUtcNow() + breakDuration;
                _consecutiveFailures = 0;
            }

            _probeInFlight = false;
        }
    }

    /// <summary>A call that ended without a provider outcome (for example, cancelled by the caller).</summary>
    internal void Release()
    {
        lock (_gate)
        {
            _probeInFlight = false;
        }
    }
}
