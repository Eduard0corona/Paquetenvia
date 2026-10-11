using Paqueteria.Application.Voice;
using Paqueteria.Infrastructure.Messaging;

namespace Paqueteria.Infrastructure.Voice;

/// <summary>
/// AI-03 §16 resilience for the voice provider: the same circuit breaker the messaging adapters use and a bulkhead.
/// A call rejected by either never reaches the provider and returns a transient result (nothing was placed).
/// </summary>
internal sealed class VoiceProviderGuard(
    TimeProvider timeProvider,
    int failureThreshold,
    TimeSpan breakDuration,
    int maxConcurrentRequests) : IDisposable
{
    private readonly MessagingCircuitBreaker _breaker = new(timeProvider, failureThreshold, breakDuration);
    private readonly SemaphoreSlim _bulkhead = new(maxConcurrentRequests, maxConcurrentRequests);

    internal MessagingCircuitBreaker CircuitBreaker => _breaker;

    /// <summary>
    /// Transient and ambiguous outcomes count as provider failures; placed and permanent outcomes (the provider
    /// answered) close the circuit. A call cancelled by the caller records nothing.
    /// </summary>
    public async ValueTask<(VoiceBridgeResult Result, int? Status)> RunAsync(
        Func<CancellationToken, ValueTask<(VoiceBridgeResult Result, int? Status)>> call,
        CancellationToken cancellationToken)
    {
        if (!_breaker.TryAcquire())
        {
            return (new(VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.CircuitOpen), null);
        }

        if (!_bulkhead.Wait(0, CancellationToken.None))
        {
            _breaker.Release();
            return (new(VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.ConcurrencyLimited), null);
        }

        var recorded = false;
        try
        {
            var outcome = await call(cancellationToken).ConfigureAwait(false);
            if (outcome.Result.Outcome is VoiceBridgeOutcome.TransientFailure or VoiceBridgeOutcome.Ambiguous)
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
