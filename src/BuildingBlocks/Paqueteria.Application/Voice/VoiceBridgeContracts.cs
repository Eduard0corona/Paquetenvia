using System.Text.RegularExpressions;

namespace Paqueteria.Application.Voice;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11: how the masked call bridge is configured in this host.
/// <list type="bullet">
/// <item><see cref="Disabled"/>: no call is placed and no driver phone is collected (default).</item>
/// <item><see cref="Synthetic"/>: deterministic fake for tests, CI and DEV_SYNTHETIC; nothing leaves the process and no
/// stored phone is ever decrypted.</item>
/// <item><see cref="Live"/>: the Twilio adapter (VOICE-001-PROVIDER-TWILIO-2026-10-11); phones are protected with the
/// ADP-001 Key Vault envelope and decrypted only in memory for the call.</item>
/// </list>
/// </summary>
public enum VoiceBridgeMode
{
    Disabled,
    Synthetic,
    Live,
}

/// <summary>The configured <see cref="VoiceBridgeMode"/>, read by the modules that collect or use phones.</summary>
public interface IVoiceBridgeStatus
{
    VoiceBridgeMode Mode { get; }

    /// <summary>The provider name stored with each call request (<c>TWILIO</c> or <c>SYNTHETIC</c>).</summary>
    string ProviderName { get; }
}

/// <summary>
/// A Mexican phone number in E.164 form (<c>+52</c> and ten digits). It is personal data: <see cref="ToString"/>
/// never returns it, so a number that reaches a log, an exception or a snapshot stays redacted. Numbers are built
/// only in memory from a decrypted value (or a synthetic placeholder) and are never persisted.
/// </summary>
public sealed partial class VoicePhoneNumber
{
    public const string CountryPrefix = "+52";

    private VoicePhoneNumber(string e164) => E164 = e164;

    /// <summary><c>+52</c> followed by the ten national digits.</summary>
    public string E164 { get; }

    /// <summary>
    /// A number a call can be placed to: the ten national digits start with 2-9 (Mexican numbers never start with 0
    /// or 1). The synthetic placeholders start with 0, so they are never dialable.
    /// </summary>
    public bool IsDialable => DialablePattern().IsMatch(E164);

    /// <summary>Ten national digits (already normalized by <c>MexicanPhonePolicy</c>), dialable or not.</summary>
    public static bool TryFromNationalDigits(string? digits, out VoicePhoneNumber number)
    {
        number = null!;
        if (digits is null || !NationalPattern().IsMatch(digits))
        {
            return false;
        }

        number = new VoicePhoneNumber(CountryPrefix + digits);
        return true;
    }

    /// <summary><c>+52</c> and ten digits exactly; nothing else is accepted.</summary>
    public static bool TryParseE164(string? value, out VoicePhoneNumber number)
    {
        number = null!;
        if (value is null || !E164Pattern().IsMatch(value))
        {
            return false;
        }

        number = new VoicePhoneNumber(value);
        return true;
    }

    public override string ToString() => "[redacted]";

    [GeneratedRegex(@"^\d{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex NationalPattern();

    [GeneratedRegex(@"^\+52\d{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164Pattern();

    [GeneratedRegex(@"^\+52[2-9]\d{9}$", RegexOptions.CultureInvariant)]
    private static partial Regex DialablePattern();
}

/// <summary>
/// One bridged call: the provider calls <paramref name="Driver"/> from the company number and, once answered,
/// connects it with <paramref name="Recipient"/>, showing the company number to both. The numbers are personal data
/// and are never part of <see cref="ToString"/>.
/// </summary>
/// <param name="CallRequestId">The stored call request; the status callback names it.</param>
/// <param name="OrganizationId">The tenant of the call request; the status callback names it.</param>
public sealed record VoiceBridgeRequest(
    Guid CallRequestId,
    Guid OrganizationId,
    VoicePhoneNumber Driver,
    VoicePhoneNumber Recipient)
{
    public override string ToString() =>
        $"VoiceBridgeRequest {{ CallRequestId = {CallRequestId:D}, OrganizationId = {OrganizationId:D} }}";
}

/// <summary>
/// <list type="bullet">
/// <item><see cref="Placed"/>: the provider created the call and returned its identifier.</item>
/// <item><see cref="TransientFailure"/>: nothing was placed; a later request may succeed.</item>
/// <item><see cref="PermanentFailure"/>: nothing was placed and repeating the same request cannot succeed.</item>
/// <item><see cref="Ambiguous"/>: the request may have reached the provider (timeout or broken answer); the call may
/// ring. It is never retried: creating a call has no idempotency key.</item>
/// </list>
/// </summary>
public enum VoiceBridgeOutcome
{
    Placed,
    TransientFailure,
    PermanentFailure,
    Ambiguous,
}

/// <param name="Code">One of <see cref="VoiceBridgeResultCodes"/>; never a provider message.</param>
/// <param name="ProviderCallId">The provider call identifier (Twilio <c>CallSid</c>) when placed.</param>
public sealed record VoiceBridgeResult(
    VoiceBridgeOutcome Outcome,
    string Code,
    string? ProviderCallId = null);

/// <summary>The allowlisted, low-cardinality result codes; safe for logs, audit and persistence.</summary>
public static class VoiceBridgeResultCodes
{
    public const string Placed = "VOICE_CALL_PLACED";
    public const string SyntheticPlaced = "VOICE_SYNTHETIC_PLACED";
    public const string SyntheticTransient = "VOICE_SYNTHETIC_TRANSIENT";
    public const string SyntheticPermanent = "VOICE_SYNTHETIC_PERMANENT";
    public const string SyntheticAmbiguous = "VOICE_SYNTHETIC_AMBIGUOUS";
    public const string Disabled = "VOICE_DISABLED";
    public const string PhoneInvalid = "VOICE_PHONE_INVALID";
    public const string PhoneUnavailable = "VOICE_PHONE_UNAVAILABLE";
    public const string DriverPhoneRejected = "VOICE_DRIVER_PHONE_REJECTED";
    public const string DestinationNotAllowed = "VOICE_DESTINATION_NOT_ALLOWED";
    public const string CallerIdRejected = "VOICE_CALLER_ID_REJECTED";
    public const string TrialRestricted = "VOICE_TRIAL_ACCOUNT_RESTRICTED";
    public const string AuthenticationFailed = "VOICE_PROVIDER_AUTH_FAILED";
    public const string Rejected = "VOICE_PROVIDER_REJECTED";
    public const string RateLimited = "VOICE_PROVIDER_RATE_LIMITED";
    public const string Unavailable = "VOICE_PROVIDER_UNAVAILABLE";
    public const string Unreachable = "VOICE_PROVIDER_UNREACHABLE";
    public const string Timeout = "VOICE_PROVIDER_TIMEOUT";
    public const string ResponseInvalid = "VOICE_PROVIDER_RESPONSE_INVALID";
    public const string CircuitOpen = "VOICE_PROVIDER_CIRCUIT_OPEN";
    public const string ConcurrencyLimited = "VOICE_PROVIDER_CONCURRENCY_LIMITED";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Placed, SyntheticPlaced, SyntheticTransient, SyntheticPermanent, SyntheticAmbiguous, Disabled, PhoneInvalid,
        PhoneUnavailable, DriverPhoneRejected, DestinationNotAllowed, CallerIdRejected, TrialRestricted,
        AuthenticationFailed, Rejected, RateLimited, Unavailable, Unreachable, Timeout, ResponseInvalid, CircuitOpen,
        ConcurrencyLimited,
    };
}

/// <summary>
/// The masked call bridge port (VOICE-001). One bounded attempt per call: implementations never retry (creating a
/// call is not idempotent) and never throw for a provider failure; every failure is classified into a
/// <see cref="VoiceBridgeResult"/>. They throw only for caller cancellation.
/// </summary>
public interface IVoiceBridgeProvider
{
    ValueTask<VoiceBridgeResult> PlaceCallAsync(VoiceBridgeRequest request, CancellationToken cancellationToken);
}

/// <summary>The provider's final report of a call, already authenticated. No phone number is carried.</summary>
/// <param name="CallStatus">One of <see cref="VoiceCallStatuses"/>.</param>
public sealed record VoiceCallStatusReport(
    Guid OrganizationId,
    Guid CallRequestId,
    string ProviderCallId,
    string CallStatus,
    int? DurationSeconds);

/// <summary>Twilio call statuses (the only values a status callback may report).</summary>
public static class VoiceCallStatuses
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "queued", "initiated", "ringing", "in-progress", "completed", "busy", "no-answer", "canceled", "failed",
    };

    /// <summary>The statuses that end a call.</summary>
    public static IReadOnlySet<string> Final { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "completed", "busy", "no-answer", "canceled", "failed",
    };
}

/// <summary>
/// The public webhook routes of the live provider (AI-05 <c>receiveTwilioCallStatus</c> and
/// <c>answerTwilioInboundCall</c>). The status callback URL names the tenant and the call request in its query; the
/// provider signs the full URL, so both are authenticated together with the form.
/// </summary>
public static class VoiceWebhookPaths
{
    public const string CallStatus = "/api/v1/voice/twilio/call-status";
    public const string Inbound = "/api/v1/voice/twilio/inbound";
    public const string OrganizationParameter = "o";
    public const string CallRequestParameter = "r";

    public static string CallStatusPathAndQuery(Guid organizationId, Guid callRequestId) =>
        $"{CallStatus}?{OrganizationParameter}={organizationId:D}&{CallRequestParameter}={callRequestId:D}";
}

/// <summary>One provider webhook as received: the path and query the provider requested and its form fields.</summary>
/// <param name="PathAndQuery">The request path and raw query string, for example
/// <c>/api/v1/voice/twilio/call-status?o=...&amp;r=...</c>.</param>
/// <param name="Signature">The single <c>X-Twilio-Signature</c> header value, or null.</param>
/// <param name="Form">Every form field, as posted. The values may hold phone numbers: they are used only to verify
/// the signature and are never logged or stored.</param>
public sealed record VoiceWebhookRequest(
    string PathAndQuery,
    string? Signature,
    IReadOnlyList<KeyValuePair<string, string>> Form)
{
    public override string ToString() => "VoiceWebhookRequest { [redacted] }";
}

public enum VoiceWebhookVerdict
{
    /// <summary>The live provider is not configured; the webhook endpoints are inert (404).</summary>
    NotConfigured,

    /// <summary>Missing or wrong signature, or another account (403).</summary>
    Rejected,

    Verified,
}

/// <summary>
/// Verifies provider webhooks (<c>X-Twilio-Signature</c>: HMAC-SHA1 over the configured public URL and the sorted
/// form fields) and builds the TwiML answers. Implemented by the infrastructure adapter; the endpoints never see a
/// credential.
/// </summary>
public interface IVoiceWebhookVerifier
{
    VoiceWebhookVerdict Verify(VoiceWebhookRequest request);

    /// <summary>TwiML for a call to the company number (no session lookup, no personal data).</summary>
    string BuildInboundCallResponse();
}
