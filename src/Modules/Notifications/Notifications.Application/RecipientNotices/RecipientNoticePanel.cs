using Paqueteria.Application.Security;

namespace Notifications.Application.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007; AI-05 <c>listOrderRecipientNotices</c> and
/// <c>stopOrderRecipientNotices</c>, x-pilot-contract-deltas NTF-RECIPIENT-NOTICES part NTF-007; AI-24
/// <c>recipient_notices.dispatcher_view</c> and <c>opt_out.dispatcher</c>): the closed values of the dispatcher's panel.
/// Nothing here ever holds a phone, an e-mail, a hash, a link or a template parameter.
/// </summary>
public static class RecipientNoticePanelContract
{
    public const string DecisionId = "NTF-RECIPIENT-NOTICE-PANEL-2026-10-30";

    /// <summary>AI-24 <c>opt_out.dispatcher</c>: the audit action of "Detener avisos", on the order, without any value.</summary>
    public const string StoppedAuditAction = "RECIPIENT_NOTICES_STOPPED";

    public const string AuditEntityType = "Order";

    /// <summary>The <c>platform.idempotency_keys</c> scope of one press of "Detener avisos".</summary>
    public const string IdempotencyScope = "NTF-007:STOP_RECIPIENT_NOTICES";

    /// <summary>A stored answer is replayed for 24 hours, like the other operations' keys.</summary>
    public static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    /// <summary>T6 choice 2: no pagination, at most 100 rows.</summary>
    public const int MaximumItems = 100;

    /// <summary>AI-24 <c>opt_out.dispatcher</c>: the source the opt-out writer records.</summary>
    public const string OptOutSource = "DISPATCHER";

    public const string WhatsAppChannel = "WHATSAPP";
    public const string EmailChannel = "EMAIL";

    /// <summary>The channels of a recipient notice, in the panel's order (T6 choice 2: WhatsApp before e-mail).</summary>
    public static IReadOnlyList<string> Channels { get; } = Array.AsReadOnly(new[] { WhatsAppChannel, EmailChannel });

    public static IReadOnlyList<string> Statuses { get; } =
        Array.AsReadOnly(new[] { "PENDING", "SENT", "DELIVERED", "FAILED", "CANCELLED" });

    /// <summary>The <c>MAX_ATTEMPTS_EXHAUSTED</c> code the NTF-001 finalizer writes when a send request runs out.</summary>
    public const string MaxAttemptsExhausted = "MAX_ATTEMPTS_EXHAUSTED";

    /// <summary>
    /// T6 choice 4: the closed <c>last_code</c> set - the codes of a WhatsApp or e-mail Notification that the NTF-003
    /// lane admits (the closed <c>MessagingResultCodes</c>, <c>AMBIGUOUS_TIMEOUT</c>, <c>TRACKING_LINK_UNAVAILABLE</c> and
    /// the cancellation reasons) plus <see cref="MaxAttemptsExhausted"/>. A stored code outside it fails closed (503) and
    /// is never passed on.
    /// </summary>
    public static IReadOnlyList<string> LastCodes { get; } = Array.AsReadOnly(new[]
    {
        "MESSAGING_ACCEPTED",
        "MESSAGING_SYNTHETIC_ACCEPTED",
        "MESSAGING_SYNTHETIC_TRANSIENT",
        "MESSAGING_SYNTHETIC_PERMANENT",
        "MESSAGING_SYNTHETIC_AMBIGUOUS_TIMEOUT",
        "MESSAGING_CHANNEL_DISABLED",
        "MESSAGING_TEMPLATE_NOT_CONFIGURED",
        "MESSAGING_TEMPLATE_PARAMETERS_INVALID",
        "MESSAGING_RECIPIENT_INVALID",
        "MESSAGING_PROVIDER_TIMEOUT",
        "MESSAGING_PROVIDER_UNREACHABLE",
        "MESSAGING_PROVIDER_RATE_LIMITED",
        "MESSAGING_PROVIDER_UNAVAILABLE",
        "MESSAGING_PROVIDER_AUTH_FAILED",
        "MESSAGING_PROVIDER_REJECTED",
        "MESSAGING_PROVIDER_RESPONSE_INVALID",
        "MESSAGING_RECIPIENT_UNDELIVERABLE",
        "MESSAGING_REENGAGEMENT_REQUIRED",
        "MESSAGING_PROVIDER_TEMPLATE_REJECTED",
        "MESSAGING_POLICY_BLOCKED",
        "MESSAGING_PROVIDER_CIRCUIT_OPEN",
        "MESSAGING_PROVIDER_CONCURRENCY_LIMITED",
        "AMBIGUOUS_TIMEOUT",
        "TRACKING_LINK_UNAVAILABLE",
        "NOTICE_SUPERSEDED",
        "NOTICE_STALE",
        "NOTICE_LIMIT_REACHED",
        "RECIPIENT_OPTED_OUT",
        MaxAttemptsExhausted,
    });

    private static readonly HashSet<string> LastCodeSet = new(LastCodes, StringComparer.Ordinal);

    /// <summary>
    /// One stored row as the panel answers it, or false when any value is outside the closed sets: an unknown template
    /// key, channel, status or code, or a negative attempt count. The caller fails closed; the value is never echoed.
    /// </summary>
    public static bool TryMap(RecipientNoticeStoredRow row, out RecipientNoticeListItem item)
    {
        ArgumentNullException.ThrowIfNull(row);
        item = null!;
        if (row.NotificationId == Guid.Empty ||
            !RecipientNoticeCatalog.TryFromTemplateKey(row.TemplateKey, out var kind) ||
            !Channels.Contains(row.Channel, StringComparer.Ordinal) ||
            !Statuses.Contains(row.Status, StringComparer.Ordinal) ||
            row.Attempts < 0 ||
            (row.LastCode is not null && !LastCodeSet.Contains(row.LastCode)))
        {
            return false;
        }

        item = new RecipientNoticeListItem(
            row.NotificationId,
            RecipientNoticeCatalog.Notice(kind),
            row.Channel,
            row.Status,
            row.Attempts,
            row.LastCode,
            row.SentAt,
            row.DeliveredAt,
            row.UpdatedAt);
        return true;
    }
}

/// <summary>The 409 codes of <c>stopOrderRecipientNotices</c> (AI-05 <c>RecipientNoticeStopConflictProblem</c>).</summary>
public static class RecipientNoticeStopConflictCodes
{
    /// <summary>A missing, repeated or malformed Idempotency-Key, a query or a body; decided before any state is read.</summary>
    public const string InvalidRequest = "INVALID_REQUEST";

    /// <summary>The key was used for another order or by another person of the organization.</summary>
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";

    /// <summary>T6 choice 8: neither the phone nor the e-mail of the destination can be used; nothing was recorded.</summary>
    public const string RecipientContactUnavailable = "RECIPIENT_CONTACT_UNAVAILABLE";

    public static IReadOnlyList<string> All { get; } =
        Array.AsReadOnly(new[] { InvalidRequest, IdempotencyConflict, RecipientContactUnavailable });
}

/// <summary>One notification row as the store read it, before the closed sets are checked.</summary>
public sealed record RecipientNoticeStoredRow(
    Guid NotificationId,
    string TemplateKey,
    string Channel,
    string Status,
    int Attempts,
    string? LastCode,
    DateTimeOffset? SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset UpdatedAt);

/// <summary>AI-05 <c>RecipientNotice</c>: one notice and channel, never a recipient value.</summary>
public sealed record RecipientNoticeListItem(
    Guid NotificationId,
    string Notice,
    string Channel,
    string Status,
    int Attempts,
    string? LastCode,
    DateTimeOffset? SentAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset UpdatedAt);

/// <summary>AI-05 <c>RecipientNoticeList</c>: the notices of the order and the two booleans (T6 choices 2 and 3).</summary>
public sealed record RecipientNoticeListResult(
    IReadOnlyList<RecipientNoticeListItem> Items,
    bool WhatsAppConsentAttested,
    bool EmailOnFile);

/// <summary>
/// AI-05 <c>RecipientNoticeStopResult</c>: the order, the channels whose opt-out is now on record - recorded by this
/// press or already there, so no opt-out made through another organization shows (T6 choice 6) - and the server time of
/// this action.
/// </summary>
public sealed record RecipientNoticeStopResult(Guid OrderId, IReadOnlyList<string> Channels, DateTimeOffset RecordedAt);

public sealed record ListOrderRecipientNoticesQuery(Guid ActorId, Guid OrganizationId, Guid OrderId);

public sealed record StopOrderRecipientNoticesCommand(
    Guid ActorId,
    Guid OrganizationId,
    Guid OrderId,
    string IdempotencyKey);

/// <summary>
/// The destination contact of an order as <c>locations.locations</c> keeps it under the PII key: the location row, its
/// key version and the two ciphertexts, either of which may be absent. <see cref="ToString"/> prints no ciphertext.
/// </summary>
public sealed record RecipientNoticeStopContact(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string KeyVersion,
    byte[]? PhoneCiphertext,
    byte[]? EmailCiphertext)
{
    public override string ToString() => $"RecipientNoticeStopContact {{ LocationId = {LocationId:D}, [ciphertexts redacted] }}";
}

/// <summary>What the first transaction of a stop found: the stored answer of the same press, or the contact to open.</summary>
public sealed record RecipientNoticeStopRead(RecipientNoticeStopResult? Replay, RecipientNoticeStopContact? Contact)
{
    public static RecipientNoticeStopRead ForReplay(RecipientNoticeStopResult result) => new(result, null);

    /// <summary>The order exists and is owned by the organization; <paramref name="contact"/> is null without a destination.</summary>
    public static RecipientNoticeStopRead ForContact(RecipientNoticeStopContact? contact) => new(null, contact);
}

/// <summary>The opt-out of one channel: its wire name and the contact hashed under every configured key version.</summary>
public sealed record RecipientNoticeStopOptOut(string Channel, IReadOnlyList<ContactLookupHash> Hashes)
{
    public override string ToString() => $"RecipientNoticeStopOptOut {{ Channel = {Channel}, [hashes redacted] }}";
}

/// <summary>The second transaction of a stop: the opt-outs, the audit row and the stored answer, all or nothing.</summary>
public sealed record RecipientNoticeStopWrite(
    StopOrderRecipientNoticesCommand Command,
    byte[] RequestHash,
    IReadOnlyList<RecipientNoticeStopOptOut> OptOuts,
    DateTimeOffset RecordedAt)
{
    public override string ToString() =>
        $"RecipientNoticeStopWrite {{ OrderId = {Command.OrderId:D}, Channels = {string.Join(',', OptOuts.Select(optOut => optOut.Channel))} }}";
}

/// <summary>The dispatcher's panel and "Detener avisos" (AI-05 part NTF-007).</summary>
public interface IRecipientNoticePanelService
{
    Task<RecipientNoticeListResult> ListAsync(ListOrderRecipientNoticesQuery query, CancellationToken cancellationToken);

    Task<RecipientNoticeStopResult> StopAsync(StopOrderRecipientNoticesCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// The API's store of the panel, in tenant transactions of the selected organization on the <c>paqueteria_app</c> pool
/// (NOBYPASSRLS, FORCE RLS). It reads Notifications, Orders and Locations rows under RLS and writes only through the
/// opt-out writer of the receipt executor (AI-18 recipient notices step 3) plus the shared idempotency and audit rows
/// (AI-13 §7.2).
/// </summary>
public interface IRecipientNoticePanelStore
{
    /// <summary>The notices of an order the organization owns; null when it does not own it (or it does not exist).</summary>
    Task<RecipientNoticeListResult?> ListAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The stored answer of the same press, or the destination contact of an order the organization owns; null when it
    /// does not own it. A key used for another request is <see cref="RecipientNoticeStopConflictException"/>.
    /// </summary>
    Task<RecipientNoticeStopRead?> ReadStopAsync(
        StopOrderRecipientNoticesCommand command,
        byte[] requestHash,
        CancellationToken cancellationToken);

    /// <summary>
    /// One transaction: the opt-outs (source DISPATCHER), the <c>RECIPIENT_NOTICES_STOPPED</c> audit row and the stored
    /// answer; a press already stored meanwhile returns its answer and writes nothing.
    /// </summary>
    Task<RecipientNoticeStopResult> RecordStopAsync(RecipientNoticeStopWrite write, CancellationToken cancellationToken);
}

/// <summary>A missing order, an order of another organization or one the organization only operates: the uniform 404.</summary>
public sealed class RecipientNoticePanelNotFoundException() : Exception("The order is not available.");

/// <summary>The store, the PII key or the contact lookup key is unavailable, or a stored value is unknown: 503.</summary>
public sealed class RecipientNoticePanelUnavailableException(Exception? innerException = null)
    : Exception("The recipient notice panel is unavailable.", innerException);

/// <summary>A 409 of <c>stopOrderRecipientNotices</c> with one of <see cref="RecipientNoticeStopConflictCodes"/>.</summary>
public sealed class RecipientNoticeStopConflictException(string code) : Exception("The stop request conflicts.")
{
    public string Code { get; } = RecipientNoticeStopConflictCodes.All.Contains(code, StringComparer.Ordinal)
        ? code
        : throw new ArgumentOutOfRangeException(nameof(code), "Unknown stop conflict code.");
}
