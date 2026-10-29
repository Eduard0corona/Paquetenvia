namespace Notifications.Application.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O11 choice 10; AI-06 automatic-reply store; AI-24
/// <c>recipient_notices.auto_reply.path</c>): the sender number of an automatic reply or an opt-out confirmation is sealed
/// by the API under the recipient-notice key with the ADP-001 envelope format v1, bound to a fixed platform identifier
/// (never an organization: an inbound message carries none) and to the request id, with its own purpose. Only the Worker
/// opens it, once, and the store erases it right after the attempt.
/// </summary>
public static class RecipientAutoReplyNumbers
{
    /// <summary>The fixed platform identifier of the binding (AI-24 records it); no organization has this id.</summary>
    public static readonly Guid PlatformBindingId = new("b32d80c1-ecb9-4b22-a049-ffb64e63f38a");

    /// <summary>The ADP-001 purpose of a sealed sender number.</summary>
    public const string Purpose = "notifications.auto_reply_phone";

    /// <summary>The key version of the synthetic seal (Development, Testing and DEV_SYNTHETIC only); never opened.</summary>
    public const string SyntheticKeyVersion = "ntf006-auto-reply-mock-v1";

    /// <summary>AI-24 <c>recipient_notices.auto_reply.path</c>: a request older than this is dropped unsent.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(23);
}

/// <summary>The kind of an automatic-reply request (AI-06 <c>recipient_auto_replies.kind</c>).</summary>
public enum RecipientAutoReplyKind
{
    AutoReply,
    OptOutConfirmation,
}

/// <summary>
/// One request the Worker leased: its id, kind, the sealed number and its key version, the message time and the lease
/// token. <see cref="ToString"/> prints no ciphertext.
/// </summary>
public sealed record ClaimedRecipientAutoReply(
    Guid RequestId,
    RecipientAutoReplyKind Kind,
    byte[] SealedNumber,
    string NumberKeyVersion,
    DateTimeOffset ReceivedAt,
    Guid LeaseToken)
{
    public override string ToString() => $"ClaimedRecipientAutoReply {{ Kind = {Kind} }}";
}

/// <summary>The settlement of a leased request (AI-18 <c>security.settle_recipient_auto_reply</c>).</summary>
public enum RecipientAutoReplyOutcome
{
    /// <summary>The provider accepted the reply; the number is erased.</summary>
    Sent,

    /// <summary>The one provider call failed; the number is erased and it is never retried.</summary>
    Failed,

    /// <summary>Dropped without a provider call (notices or WhatsApp off, an unusable number); the number is erased.</summary>
    Dropped,

    /// <summary>A transient failure before the provider call: back to pending with its number.</summary>
    Retry,
}

/// <summary>The Worker's half of the automatic-reply store, under the lease of each claim.</summary>
public interface IRecipientAutoReplyStore
{
    Task<IReadOnlyList<ClaimedRecipientAutoReply>> ClaimAutoRepliesAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken);

    Task<bool> SettleAutoReplyAsync(
        ClaimedRecipientAutoReply request,
        RecipientAutoReplyOutcome outcome,
        string? code,
        DateTimeOffset? availableAt,
        CancellationToken cancellationToken);
}
