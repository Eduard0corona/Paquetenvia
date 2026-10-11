using System.Text.RegularExpressions;
using Paqueteria.Application.Voice;

namespace Paqueteria.Infrastructure.Voice;

public enum VoiceBridgeProviderKind
{
    Disabled,
    Synthetic,

    /// <summary>VOICE-001-PROVIDER-TWILIO-2026-10-11: Twilio Programmable Voice (REST create call + TwiML).</summary>
    Twilio,
}

/// <summary>
/// <c>Voice</c>: VOICE-001 masked call bridge. <c>Disabled</c> unless the deployment names a provider. Twilio
/// credentials and the company number arrive only through the allowlisted <c>KeyVaultSecrets:Mappings</c> source; the
/// pilot does not configure the bridge before the owner provides them and GATE-007 is resolved.
/// </summary>
public sealed class VoiceBridgeOptions
{
    public const string SectionName = "Voice";

    public VoiceBridgeProviderKind Provider { get; set; }

    /// <summary>Outcome of the synthetic provider (tests and DEV_SYNTHETIC only).</summary>
    public VoiceBridgeOutcome SyntheticOutcome { get; set; } = VoiceBridgeOutcome.Placed;

    public TwilioVoiceOptions Twilio { get; set; } = new();
}

public sealed class TwilioVoiceOptions
{
    public string BaseUri { get; set; } = "https://api.twilio.com";

    /// <summary><c>AC</c> and 32 hexadecimal characters. Key Vault mapping.</summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>Account auth token: signs every webhook (and authenticates REST without an API key). Key Vault mapping.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>Optional REST API key (<c>SK</c> and 32 hexadecimal characters). Key Vault mapping.</summary>
    public string ApiKeySid { get; set; } = string.Empty;

    /// <summary>Optional REST API key secret. Key Vault mapping.</summary>
    public string ApiKeySecret { get; set; } = string.Empty;

    /// <summary>The company's Mexican Twilio number (<c>+52</c> and ten digits) both parties see. Key Vault mapping.</summary>
    public string CompanyNumber { get; set; } = string.Empty;

    /// <summary>Public origin Twilio calls back (<c>https://host</c>, no path); never taken from request headers.</summary>
    public string WebhookBaseUri { get; set; } = string.Empty;

    /// <summary>The decision-log row that resolves GATE-007 for real phone numbers (for example <c>GATE-007-...</c>).</summary>
    public string Gate007DecisionId { get; set; } = string.Empty;

    /// <summary>Optional dispatch line a call to the company number is forwarded to; without it a message is played.</summary>
    public string InboundForwardNumber { get; set; } = string.Empty;

    /// <summary>Text-to-speech voice for the Spanish messages (Twilio <c>&lt;Say voice&gt;</c>).</summary>
    public string SayVoice { get; set; } = "Polly.Mia";

    /// <summary>Per-attempt timeout of the REST request.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>How long the driver's phone rings (REST <c>Timeout</c>).</summary>
    public int DriverRingSeconds { get; set; } = 25;

    /// <summary>How long the recipient's phone rings (<c>&lt;Dial timeout&gt;</c>).</summary>
    public int RecipientRingSeconds { get; set; } = 30;

    /// <summary>Hard cap of a bridged conversation (<c>&lt;Dial timeLimit&gt;</c>); bounds the cost of one call.</summary>
    public int MaximumCallSeconds { get; set; } = 300;

    /// <summary>AI-03 §16 circuit breaker: consecutive provider failures that open the circuit.</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>Seconds the circuit stays open before one half-open probe.</summary>
    public int CircuitBreakerBreakSeconds { get; set; } = 30;

    /// <summary>AI-03 §16 bulkhead: concurrent provider calls; a call beyond it fails fast as transient.</summary>
    public int MaxConcurrentRequests { get; set; } = 8;
}

internal static partial class VoiceBridgeOptionsValidator
{
    /// <summary>Returns the failures; never includes a configured value.</summary>
    public static IReadOnlyList<string> Validate(VoiceBridgeOptions options, bool syntheticAllowed, bool liveAllowed)
    {
        var failures = new List<string>();
        if (!Enum.IsDefined(options.Provider) || !Enum.IsDefined(options.SyntheticOutcome))
        {
            failures.Add("Voice:Provider or Voice:SyntheticOutcome is not a known value.");
            return failures;
        }

        if (options.Provider == VoiceBridgeProviderKind.Synthetic && !syntheticAllowed)
        {
            failures.Add("Voice:Provider=Synthetic is allowed only in Development, Testing or DEV_SYNTHETIC.");
        }

        if (options.Provider != VoiceBridgeProviderKind.Twilio)
        {
            return failures;
        }

        if (!liveAllowed)
        {
            failures.Add(
                "Voice:Provider=Twilio is refused in Development, Testing and DEV_SYNTHETIC: synthetic phone numbers must never be dialed.");
        }

        var twilio = options.Twilio ?? new TwilioVoiceOptions();
        if (!IsHttpsRoot(twilio.BaseUri))
        {
            failures.Add("Voice:Twilio:BaseUri must be an https URI without path, query or credentials.");
        }

        if (!AccountSidPattern().IsMatch(twilio.AccountSid ?? string.Empty))
        {
            failures.Add("Voice:Twilio:AccountSid is missing or malformed (map it from Key Vault).");
        }

        if (!SecretPattern().IsMatch(twilio.AuthToken ?? string.Empty))
        {
            failures.Add("Voice:Twilio:AuthToken is missing or malformed (map it from Key Vault).");
        }

        var hasKeySid = !string.IsNullOrEmpty(twilio.ApiKeySid);
        var hasKeySecret = !string.IsNullOrEmpty(twilio.ApiKeySecret);
        if (hasKeySid != hasKeySecret ||
            (hasKeySid && (!ApiKeySidPattern().IsMatch(twilio.ApiKeySid!) || !SecretPattern().IsMatch(twilio.ApiKeySecret!))))
        {
            failures.Add("Voice:Twilio:ApiKeySid and Voice:Twilio:ApiKeySecret must be both absent or both valid (map them from Key Vault).");
        }

        if (!VoicePhoneNumber.TryParseE164(twilio.CompanyNumber, out var company) || !company.IsDialable)
        {
            failures.Add("Voice:Twilio:CompanyNumber must be a dialable Mexican number, +52 and ten digits (map it from Key Vault).");
        }

        if (!string.IsNullOrEmpty(twilio.InboundForwardNumber) &&
            (!VoicePhoneNumber.TryParseE164(twilio.InboundForwardNumber, out var forward) || !forward.IsDialable))
        {
            failures.Add("Voice:Twilio:InboundForwardNumber, when set, must be a dialable Mexican number, +52 and ten digits.");
        }

        if (!IsHttpsRoot(twilio.WebhookBaseUri))
        {
            failures.Add("Voice:Twilio:WebhookBaseUri must be the public https origin without path, query or credentials.");
        }

        if (!Gate007DecisionPattern().IsMatch(twilio.Gate007DecisionId ?? string.Empty))
        {
            failures.Add(
                "Voice:Twilio:Gate007DecisionId must name the decision-log row that resolves GATE-007 (GATE-007-...).");
        }

        if (!SayVoicePattern().IsMatch(twilio.SayVoice ?? string.Empty))
        {
            failures.Add("Voice:Twilio:SayVoice must be a Twilio voice name such as Polly.Mia.");
        }

        if (twilio.TimeoutSeconds is < 1 or > 30 ||
            twilio.DriverRingSeconds is < 5 or > 60 ||
            twilio.RecipientRingSeconds is < 5 or > 60 ||
            twilio.MaximumCallSeconds is < 60 or > 1800)
        {
            failures.Add(
                "Voice:Twilio needs TimeoutSeconds 1-30, DriverRingSeconds 5-60, RecipientRingSeconds 5-60 and MaximumCallSeconds 60-1800.");
        }

        if (twilio.CircuitBreakerFailureThreshold is < 1 or > 50 ||
            twilio.CircuitBreakerBreakSeconds is < 1 or > 600 ||
            twilio.MaxConcurrentRequests is < 1 or > 64)
        {
            failures.Add(
                "Voice:Twilio resilience needs CircuitBreakerFailureThreshold 1-50, CircuitBreakerBreakSeconds 1-600 and MaxConcurrentRequests 1-64.");
        }

        return failures;
    }

    internal static bool IsHttpsRoot(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    [GeneratedRegex("^AC[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    internal static partial Regex AccountSidPattern();

    [GeneratedRegex("^SK[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeySidPattern();

    [GeneratedRegex("^[A-Za-z0-9]{16,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex("^GATE-007-[A-Z0-9][A-Z0-9-]{2,76}$", RegexOptions.CultureInvariant)]
    private static partial Regex Gate007DecisionPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9.\-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex SayVoicePattern();
}
