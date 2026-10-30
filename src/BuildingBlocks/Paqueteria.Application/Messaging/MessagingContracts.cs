namespace Paqueteria.Application.Messaging;

/// <summary>
/// GATE-004-CHANNELS: the external customer channels chosen by the owner. <c>IN_APP</c> is not an
/// external channel and stays with the Notifications synthetic provider.
/// </summary>
public enum MessagingChannel
{
    WhatsApp,
    Email,
}

/// <summary>
/// The four delivery outcomes of the outbox contract (NTF-001, <c>apply_notification_outcome</c>):
/// <list type="bullet">
/// <item><see cref="Accepted"/>: the provider accepted the message (SENT, outbox PROCESSED).</item>
/// <item><see cref="TransientFailure"/>: nothing was delivered and a later attempt may succeed (RETRY with backoff).</item>
/// <item><see cref="PermanentFailure"/>: retrying cannot succeed (FAILED, outbox DEAD).</item>
/// <item><see cref="AmbiguousTimeout"/>: the request may have reached the provider. WhatsApp: FAILED with
/// <c>AMBIGUOUS_TIMEOUT</c>, never retried (NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02); email: RETRY with backoff.</item>
/// </list>
/// </summary>
public enum MessagingOutcome
{
    Accepted,
    TransientFailure,
    PermanentFailure,
    AmbiguousTimeout,
}

/// <summary>
/// A recipient address (E.164 phone or email). It is personal data: <see cref="ToString"/> never
/// returns it, so a recipient that reaches a log, an exception or a snapshot stays redacted.
/// </summary>
public sealed class MessagingRecipient
{
    public MessagingRecipient(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        Address = address.Trim();
    }

    public string Address { get; }

    public override string ToString() => "[redacted]";
}

/// <summary>
/// One message to send through a configured provider template.
/// </summary>
/// <param name="MessageId">Stable id of the message (for example the Notification id); retries reuse it.</param>
/// <param name="TemplateKey">Logical template key; each channel maps it to an owner-approved provider template in configuration.</param>
/// <param name="Parameters">Positional template parameters (<c>{{1}}</c>, <c>{{2}}</c>…). They may hold personal data.</param>
public sealed record MessagingRequest(
    Guid MessageId,
    MessagingChannel Channel,
    MessagingRecipient Recipient,
    string TemplateKey,
    IReadOnlyList<string> Parameters)
{
    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.templates.whatsapp.url_button</c>; AI-08 NTF-005):
    /// the dynamic suffix of the template's URL button, the first button ("Ver seguimiento"). It is the public tracking
    /// token, so it is never logged. WhatsApp only.
    /// </summary>
    public string? UrlButtonSuffix { get; init; }

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.templates.whatsapp.quick_reply_button</c>): the
    /// payload Meta returns when the recipient taps the template's quick reply, the second button ("No recibir avisos").
    /// A fixed upper-case code, never personal data. WhatsApp only.
    /// </summary>
    public string? QuickReplyPayload { get; init; }

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.templates.email.headers</c>): the one-click
    /// unsubscribe URL of this notification, sent as <c>List-Unsubscribe</c> with
    /// <c>List-Unsubscribe-Post: List-Unsubscribe=One-Click</c> (RFC 8058). It carries a token, so it is never logged.
    /// E-mail only.
    /// </summary>
    public string? OneClickUnsubscribeUrl { get; init; }

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-24 <c>recipient_notices.auto_reply</c> and
    /// <c>opt_out.confirmation</c>; AI-08 NTF-006): a free-form text message instead of a template, sent only inside the
    /// 24-hour customer service window an inbound message opened. It is one of the fixed texts the platform answers
    /// with, never a recipient's text, and it is never logged. WhatsApp only, with no parameter and no button; the
    /// template key then only names the fixed text.
    /// </summary>
    public string? FreeFormText { get; init; }

    /// <summary>Recipient, parameters, button suffix, unsubscribe URL and free-form text are never printed here.</summary>
    public override string ToString() => $"MessagingRequest {{ Channel = {Channel}, TemplateKey = {TemplateKey} }}";
}

/// <param name="Code">One of <see cref="MessagingResultCodes"/>; never a provider message or body.</param>
/// <param name="ProviderReference">Provider message id (WhatsApp <c>wamid</c>, ACS operation id), when accepted.</param>
/// <param name="RetryAfter">Provider back-off hint (<c>Retry-After</c>) for transient failures.</param>
public sealed record MessagingResult(
    MessagingOutcome Outcome,
    string Code,
    string? ProviderReference = null,
    TimeSpan? RetryAfter = null);

/// <summary>The allowlisted, low-cardinality result codes; safe for logs and persistence.</summary>
public static class MessagingResultCodes
{
    public const string Accepted = "MESSAGING_ACCEPTED";
    public const string SyntheticAccepted = "MESSAGING_SYNTHETIC_ACCEPTED";
    public const string SyntheticTransient = "MESSAGING_SYNTHETIC_TRANSIENT";
    public const string SyntheticPermanent = "MESSAGING_SYNTHETIC_PERMANENT";
    public const string SyntheticAmbiguous = "MESSAGING_SYNTHETIC_AMBIGUOUS_TIMEOUT";
    public const string ChannelDisabled = "MESSAGING_CHANNEL_DISABLED";
    public const string TemplateNotConfigured = "MESSAGING_TEMPLATE_NOT_CONFIGURED";
    public const string TemplateParametersInvalid = "MESSAGING_TEMPLATE_PARAMETERS_INVALID";
    public const string RecipientInvalid = "MESSAGING_RECIPIENT_INVALID";
    public const string Timeout = "MESSAGING_PROVIDER_TIMEOUT";
    public const string Unreachable = "MESSAGING_PROVIDER_UNREACHABLE";
    public const string RateLimited = "MESSAGING_PROVIDER_RATE_LIMITED";
    public const string Unavailable = "MESSAGING_PROVIDER_UNAVAILABLE";
    public const string AuthenticationFailed = "MESSAGING_PROVIDER_AUTH_FAILED";
    public const string Rejected = "MESSAGING_PROVIDER_REJECTED";
    public const string ResponseInvalid = "MESSAGING_PROVIDER_RESPONSE_INVALID";
    public const string RecipientUndeliverable = "MESSAGING_RECIPIENT_UNDELIVERABLE";
    public const string ReengagementRequired = "MESSAGING_REENGAGEMENT_REQUIRED";
    public const string ProviderTemplateRejected = "MESSAGING_PROVIDER_TEMPLATE_REJECTED";
    public const string PolicyBlocked = "MESSAGING_POLICY_BLOCKED";
    public const string CircuitOpen = "MESSAGING_PROVIDER_CIRCUIT_OPEN";
    public const string ConcurrencyLimited = "MESSAGING_PROVIDER_CONCURRENCY_LIMITED";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Accepted, SyntheticAccepted, SyntheticTransient, SyntheticPermanent, SyntheticAmbiguous,
        ChannelDisabled, TemplateNotConfigured, TemplateParametersInvalid, RecipientInvalid,
        Timeout, Unreachable, RateLimited, Unavailable, AuthenticationFailed, Rejected,
        ResponseInvalid, RecipientUndeliverable, ReengagementRequired, ProviderTemplateRejected,
        PolicyBlocked, CircuitOpen, ConcurrencyLimited,
    };
}

/// <summary>
/// GATE-004-CHANNELS and AI-24 <c>recipient_notices.receipts.whatsapp</c>: the Meta error code classifier, shared by the
/// send adapter and the delivery receipts of the WhatsApp webhook (a <c>failed</c> status carries the same codes). It
/// returns the transient flag and one closed <see cref="MessagingResultCodes"/> code; a code it does not know is a
/// permanent rejection.
/// </summary>
public static class MetaWhatsAppErrorCodes
{
    // Meta error codes (https://developers.facebook.com/docs/whatsapp/cloud-api/support/error-codes).
    private static readonly HashSet<long> TransientCodes =
    [
        1,      // API unknown
        2,      // API service
        4,      // API too many calls
        80007,  // WABA rate limit
        130429, // throughput rate limit
        131000, // something went wrong
        131016, // service unavailable
        131048, // spam rate limit
        131056, // business/consumer pair rate limit
        131057, // account in maintenance mode
        133004, // server temporarily unavailable
    ];

    private static readonly HashSet<long> ThrottleCodes = [4, 80007, 130429, 131048, 131056];

    public static bool IsTransient(long code) => TransientCodes.Contains(code);

    /// <summary>The closed code of a Meta error code; <see cref="IsTransient"/> says whether a send may be retried.</summary>
    public static string Classify(long code)
    {
        if (TransientCodes.Contains(code))
        {
            return ThrottleCodes.Contains(code) ? MessagingResultCodes.RateLimited : MessagingResultCodes.Unavailable;
        }

        return code switch
        {
            190 or 0 or 3 or 10 or (>= 200 and <= 299) => MessagingResultCodes.AuthenticationFailed,
            131026 or 131030 or 133010 => MessagingResultCodes.RecipientUndeliverable,
            131047 => MessagingResultCodes.ReengagementRequired,
            131008 or 131009 or (>= 132000 and <= 132999) => MessagingResultCodes.ProviderTemplateRejected,
            368 or 131031 or 131049 or 131050 => MessagingResultCodes.PolicyBlocked,
            _ => MessagingResultCodes.Rejected,
        };
    }
}

/// <summary>
/// AI-03 §15 <c>IMessagingProvider</c>: sends one message through the provider configured for its
/// channel. Implementations never retry internally (a WhatsApp send is not idempotent); the outbox
/// RETRY/backoff with <c>lease_token</c> is the only retry loop. Implementations never throw for a
/// provider failure: every failure is classified into a <see cref="MessagingResult"/>. They throw only
/// for caller cancellation.
/// </summary>
public interface IMessagingProvider
{
    ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken);
}
