using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Paqueteria.Application.Security;

namespace Notifications.Infrastructure.RecipientNotices.Webhooks;

/// <summary>How the API seals the sender number of an automatic-reply request (O11 choice 10).</summary>
public enum RecipientAutoReplyNumberSealerKind
{
    /// <summary>No sealer: the WhatsApp webhook cannot be enabled.</summary>
    Disabled,

    /// <summary>Random bytes under the synthetic key version (Development, Testing and DEV_SYNTHETIC only).</summary>
    Mock,

    /// <summary>The recipient-notice key in Key Vault, wrap only (<c>PiiProtection:RecipientNoticeKey</c>).</summary>
    AzureKeyVault,
}

/// <summary>
/// <c>Messaging:WhatsApp:Webhook</c> (AI-24 <c>recipient_notices.webhooks.whatsapp</c> and <c>switches.webhooks</c>;
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 O11 choices 5 and 9). Off by default (uniform 404). The app secret and the
/// verify token come from Key Vault (NTF-008), each with a previous slot for rotation; the phone number id names the one
/// configured WhatsApp Business number. No value is ever logged.
/// </summary>
public sealed class WhatsAppWebhookOptions
{
    public const string SectionName = "Messaging:WhatsApp:Webhook";

    public bool Enabled { get; set; }

    /// <summary>The Meta app secret (HMAC-SHA256 key of <c>X-Hub-Signature-256</c>).</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>The previous app secret, accepted while a rotation is in progress; empty otherwise.</summary>
    public string PreviousAppSecret { get; set; } = string.Empty;

    /// <summary>The verify token of the GET verification.</summary>
    public string VerifyToken { get; set; } = string.Empty;

    /// <summary>The previous verify token, accepted while a rotation is in progress; empty otherwise.</summary>
    public string PreviousVerifyToken { get; set; } = string.Empty;

    /// <summary>The WhatsApp Business phone number id whose changes are read (O11 choice 5).</summary>
    public string PhoneNumberId { get; set; } = string.Empty;

    /// <summary>How the sender number of an automatic reply is sealed (O11 choice 10).</summary>
    public RecipientAutoReplyNumberSealerKind NumberSealer { get; set; }
}

/// <summary>
/// <c>Messaging:Email:Events</c> (AI-24 <c>recipient_notices.webhooks.email</c>; O2 "Secreto en un encabezado
/// (Recommended)"; O11 choice 11). Off by default (uniform 404). The Event Grid delivery secret comes from Key Vault, with
/// a previous slot for rotation; the source resource id is the Communication Services resource whose events are read.
/// </summary>
public sealed class EmailEventsOptions
{
    public const string SectionName = "Messaging:Email:Events";

    public bool Enabled { get; set; }

    /// <summary>The value of the secret delivery header (<see cref="RecipientNoticeWebhookSecrets.EventGridSecretHeader"/>).</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>The previous secret, accepted while a rotation is in progress; empty otherwise.</summary>
    public string PreviousSecret { get; set; } = string.Empty;

    /// <summary>The Azure resource id of the Communication Services resource (the events' <c>topic</c>).</summary>
    public string SourceResourceId { get; set; } = string.Empty;
}

/// <summary>The fixed names of the webhook secrets on the wire.</summary>
public static class RecipientNoticeWebhookSecrets
{
    /// <summary>O2: the Event Grid delivery property that carries the Key Vault secret (marked secret in Event Grid).</summary>
    public const string EventGridSecretHeader = "X-Paquetenvia-Event-Secret";

    public const string MetaSignatureHeader = "X-Hub-Signature-256";

    public const string MetaSignaturePrefix = "sha256=";
}

/// <summary>
/// Start validation of the provider webhooks. Each enabled webhook needs its secrets (at least 16 characters for the
/// Meta app secret, 32 for the verify token and the Event Grid secret, no whitespace), the WhatsApp phone number id or
/// the Communication Services resource id, and, for WhatsApp, the contact lookup key and a number sealer (the mock only
/// where synthetic data is allowed). No failure message carries a value.
/// </summary>
internal sealed partial class RecipientNoticeWebhookOptionsValidation(
    IHostEnvironment environment,
    IRecipientContactLookup contactLookup) :
    IValidateOptions<WhatsAppWebhookOptions>,
    IValidateOptions<EmailEventsOptions>
{
    public ValidateOptionsResult Validate(string? name, WhatsAppWebhookOptions options)
    {
        var failures = new List<string>();
        if (!Enum.IsDefined(options.NumberSealer))
        {
            failures.Add("Messaging:WhatsApp:Webhook:NumberSealer is not a known value.");
        }

        if (options.NumberSealer == RecipientAutoReplyNumberSealerKind.Mock && !IsSyntheticAllowed(environment))
        {
            failures.Add("Messaging:WhatsApp:Webhook:NumberSealer=Mock is allowed only in Development, Testing or DEV_SYNTHETIC.");
        }

        if (!IsOptionalSecret(options.PreviousAppSecret, 16) || !IsOptionalSecret(options.PreviousVerifyToken, 32))
        {
            failures.Add("Messaging:WhatsApp:Webhook:PreviousAppSecret and PreviousVerifyToken must be empty or valid secrets.");
        }

        if (options.Enabled)
        {
            if (!IsSecret(options.AppSecret, 16) || !IsSecret(options.VerifyToken, 32))
            {
                failures.Add("Messaging:WhatsApp:Webhook:Enabled requires AppSecret (16 or more characters) and VerifyToken (32 or more), mapped from Key Vault.");
            }

            if (!PhoneNumberIdPattern().IsMatch(options.PhoneNumberId ?? string.Empty))
            {
                failures.Add("Messaging:WhatsApp:Webhook:Enabled requires the numeric PhoneNumberId of the WhatsApp Business number.");
            }

            if (options.NumberSealer == RecipientAutoReplyNumberSealerKind.Disabled)
            {
                failures.Add("Messaging:WhatsApp:Webhook:Enabled requires a NumberSealer (the recipient-notice key).");
            }

            if (!contactLookup.IsAvailable)
            {
                failures.Add("Messaging:WhatsApp:Webhook:Enabled requires ContactLookup:Keys (the opt-out and the automatic-reply limit).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public ValidateOptionsResult Validate(string? name, EmailEventsOptions options)
    {
        var failures = new List<string>();
        if (!IsOptionalSecret(options.PreviousSecret, 32))
        {
            failures.Add("Messaging:Email:Events:PreviousSecret must be empty or a valid secret.");
        }

        if (options.Enabled)
        {
            if (!IsSecret(options.Secret, 32))
            {
                failures.Add("Messaging:Email:Events:Enabled requires Secret (32 or more characters), mapped from Key Vault.");
            }

            if (!SourceResourceIdPattern().IsMatch(options.SourceResourceId ?? string.Empty))
            {
                failures.Add("Messaging:Email:Events:Enabled requires the SourceResourceId of the Communication Services resource.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsSyntheticAllowed(IHostEnvironment environment) =>
        environment.IsDevelopment() ||
        environment.IsEnvironment("Testing") ||
        SyntheticEnvironmentPolicy.IsDevSynthetic(environment.EnvironmentName);

    private static bool IsSecret(string? value, int minimumLength) =>
        value is not null && value.Length >= minimumLength && value.Length <= 512 && !value.Any(char.IsWhiteSpace) &&
        !value.Any(char.IsControl);

    private static bool IsOptionalSecret(string? value, int minimumLength) =>
        string.IsNullOrEmpty(value) || IsSecret(value, minimumLength);

    [GeneratedRegex("^[0-9]{5,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneNumberIdPattern();

    [GeneratedRegex(
        "^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[A-Za-z0-9._()-]{1,90}/providers/Microsoft\\.Communication/communicationServices/[A-Za-z0-9-]{1,63}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SourceResourceIdPattern();
}

/// <summary>
/// O2 and O11 choice 9: every secret is compared in constant time against both slots, with no short-circuit. A token is
/// compared as the SHA-256 of its UTF-8 bytes, so neither its length nor its content shows in the timing; a signature is
/// the HMAC-SHA256 of the raw body under each app secret, compared with <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// </summary>
internal sealed class RecipientNoticeWebhookGuard(
    IOptions<WhatsAppWebhookOptions> whatsApp,
    IOptions<EmailEventsOptions> email) : IRecipientNoticeWebhookGuard
{
    public bool WhatsAppEnabled => whatsApp.Value.Enabled;

    public bool EmailEventsEnabled => email.Value.Enabled;

    public string WhatsAppPhoneNumberId => whatsApp.Value.PhoneNumberId;

    public string EmailEventsSourceResourceId => email.Value.SourceResourceId;

    public bool IsValidMetaSignature(string? header, ReadOnlySpan<byte> body)
    {
        var provided = new byte[32];
        var wellFormed = header is { Length: 71 } &&
            header.StartsWith(RecipientNoticeWebhookSecrets.MetaSignaturePrefix, StringComparison.Ordinal) &&
            TryHex(header.AsSpan(7), provided);
        var match = false;
        foreach (var secret in new[] { whatsApp.Value.AppSecret, whatsApp.Value.PreviousAppSecret })
        {
            if (string.IsNullOrEmpty(secret))
            {
                continue;
            }

            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
            match |= CryptographicOperations.FixedTimeEquals(expected, provided);
        }

        return wellFormed && match;
    }

    public bool IsValidMetaVerifyToken(string? token) =>
        Matches(token, whatsApp.Value.VerifyToken, whatsApp.Value.PreviousVerifyToken);

    public bool IsValidEventGridSecret(string? header) =>
        Matches(header, email.Value.Secret, email.Value.PreviousSecret);

    private static bool Matches(string? presented, params string[] slots)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(presented ?? string.Empty));
        var match = false;
        foreach (var slot in slots)
        {
            if (string.IsNullOrEmpty(slot))
            {
                continue;
            }

            match |= CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(Encoding.UTF8.GetBytes(slot)));
        }

        return !string.IsNullOrEmpty(presented) && match;
    }

    private static bool TryHex(ReadOnlySpan<char> hex, Span<byte> destination)
    {
        if (hex.Length != destination.Length * 2)
        {
            return false;
        }

        for (var index = 0; index < destination.Length; index++)
        {
            var high = HexValue(hex[2 * index]);
            var low = HexValue(hex[(2 * index) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => -1,
    };
}
