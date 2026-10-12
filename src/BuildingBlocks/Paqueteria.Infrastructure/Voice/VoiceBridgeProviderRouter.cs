using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Voice;

namespace Paqueteria.Infrastructure.Voice;

/// <summary>
/// Routes each call to the configured provider. <c>Disabled</c> fails closed with
/// <see cref="VoiceBridgeResultCodes.Disabled"/> (nothing is placed). The Twilio adapter is resolved lazily, so a
/// disabled or synthetic host never builds its HTTP client.
/// </summary>
internal sealed class VoiceBridgeProviderRouter(IServiceProvider services, IOptions<VoiceBridgeOptions> options)
    : IVoiceBridgeProvider
{
    public ValueTask<VoiceBridgeResult> PlaceCallAsync(VoiceBridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        IVoiceBridgeProvider? provider = options.Value.Provider switch
        {
            VoiceBridgeProviderKind.Twilio => services.GetRequiredService<TwilioVoiceBridgeProvider>(),
            VoiceBridgeProviderKind.Synthetic => services.GetRequiredService<SyntheticVoiceBridgeProvider>(),
            _ => null,
        };
        return provider is null
            ? ValueTask.FromResult(new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.Disabled))
            : provider.PlaceCallAsync(request, cancellationToken);
    }
}

/// <summary>
/// Deterministic fake for tests, CI and DEV_SYNTHETIC (AI-03 §15): no network. It requires the same request shape as
/// the Twilio adapter (two different <c>+52</c> numbers; synthetic placeholders are accepted because they are never
/// dialed) and returns the configured outcome with a call id derived only from the call request id.
/// </summary>
internal sealed class SyntheticVoiceBridgeProvider(IOptions<VoiceBridgeOptions> options) : IVoiceBridgeProvider
{
    public ValueTask<VoiceBridgeResult> PlaceCallAsync(VoiceBridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CallRequestId == Guid.Empty || request.OrganizationId == Guid.Empty ||
            request.Driver is null || request.Recipient is null ||
            string.Equals(request.Driver.E164, request.Recipient.E164, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new VoiceBridgeResult(VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.PhoneInvalid));
        }

        return ValueTask.FromResult(options.Value.SyntheticOutcome switch
        {
            VoiceBridgeOutcome.Placed => new VoiceBridgeResult(
                VoiceBridgeOutcome.Placed,
                VoiceBridgeResultCodes.SyntheticPlaced,
                SyntheticCallId(request.CallRequestId)),
            VoiceBridgeOutcome.TransientFailure => new VoiceBridgeResult(
                VoiceBridgeOutcome.TransientFailure, VoiceBridgeResultCodes.SyntheticTransient),
            VoiceBridgeOutcome.PermanentFailure => new VoiceBridgeResult(
                VoiceBridgeOutcome.PermanentFailure, VoiceBridgeResultCodes.SyntheticPermanent),
            VoiceBridgeOutcome.Ambiguous => new VoiceBridgeResult(
                VoiceBridgeOutcome.Ambiguous, VoiceBridgeResultCodes.SyntheticAmbiguous),
            _ => throw new InvalidOperationException("Unknown synthetic voice outcome."),
        });
    }

    /// <summary><c>SY</c> and 32 hexadecimal characters, so it can never be mistaken for a Twilio <c>CA</c> sid.</summary>
    internal static string SyntheticCallId(Guid callRequestId) =>
        "SY" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"voice-001|{callRequestId:D}")))
            .ToLowerInvariant()[..32];
}

/// <summary>The configured mode, for the modules that protect, collect or decrypt phones.</summary>
internal sealed class VoiceBridgeStatus(IOptions<VoiceBridgeOptions> options) : IVoiceBridgeStatus
{
    public VoiceBridgeMode Mode => options.Value.Provider switch
    {
        VoiceBridgeProviderKind.Twilio => VoiceBridgeMode.Live,
        VoiceBridgeProviderKind.Synthetic => VoiceBridgeMode.Synthetic,
        _ => VoiceBridgeMode.Disabled,
    };

    public string ProviderName => options.Value.Provider switch
    {
        VoiceBridgeProviderKind.Twilio => "TWILIO",
        VoiceBridgeProviderKind.Synthetic => "SYNTHETIC",
        _ => "DISABLED",
    };
}
