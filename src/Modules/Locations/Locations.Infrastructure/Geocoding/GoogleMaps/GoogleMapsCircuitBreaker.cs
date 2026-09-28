namespace Locations.Infrastructure.Geocoding.GoogleMaps;

/// <summary>
/// AI-03 §16 circuit breaker for the Geocoding API: after <c>threshold</c> consecutive failed calls
/// the circuit opens for <c>breakDuration</c>; then exactly one half-open probe is allowed, which
/// closes it on success or reopens it on failure. Thread-safe; one instance per provider.
/// </summary>
internal sealed class GoogleMapsCircuitBreaker(TimeProvider timeProvider, int threshold, TimeSpan breakDuration)
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

    /// <summary>A call that ended without reaching the provider (for example, cancelled by the caller).</summary>
    internal void Release()
    {
        lock (_gate)
        {
            _probeInFlight = false;
        }
    }
}
