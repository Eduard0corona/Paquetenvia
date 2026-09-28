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
/// <item><see cref="AmbiguousTimeout"/>: the request may have reached the provider (RETRY with backoff, may duplicate).</item>
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
    /// <summary>Recipient and parameters are personal data and never appear here.</summary>
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
