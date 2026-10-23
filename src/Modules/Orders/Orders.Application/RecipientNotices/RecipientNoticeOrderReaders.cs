namespace Orders.Application.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.tracking_link.read</c>; AI-08 NTF-005): the active
/// derived link of an order (generation, key version and SHA-256 of the token; <c>revoked_at</c> null). It is what the
/// public lookup accepts; the token itself is never stored, so it is derived again in memory and checked against
/// <see cref="TokenHash"/>. <see cref="ToString"/> prints no hash.
/// </summary>
public sealed record RecipientNoticeLinkRecord(int Generation, int KeyVersion, byte[] TokenHash)
{
    public override string ToString() => $"RecipientNoticeLinkRecord {{ Generation = {Generation}, KeyVersion = {KeyVersion} }}";
}

/// <summary>
/// What a recipient notice needs from an order of the owner organization, read under FORCE RLS: its public id (the
/// número de guía), its internal status, its destination location and its active derived link, if any. No personal
/// data: the contact is read by the Locations module from the destination location.
/// </summary>
public sealed record RecipientNoticeOrderSnapshot(
    Guid OrderId,
    string PublicId,
    string Status,
    Guid DestinationLocationId,
    RecipientNoticeLinkRecord? ActiveLink);

/// <summary>
/// The Orders reader the Worker uses for recipient notices (AI-24 <c>recipient_notices.expansion.reads</c>,
/// <c>validity.check</c> and <c>tracking_link.read</c>): one short tenant transaction of the owner organization
/// (BEGIN, <c>set_config(..., true)</c>, <c>SET LOCAL ROLE paqueteria_worker</c>), committed before anything else
/// happens. An order the organization does not own (missing, foreign or only operated) is <see langword="null"/>.
/// </summary>
public interface IRecipientNoticeOrderReader
{
    Task<RecipientNoticeOrderSnapshot?> ReadAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        CancellationToken cancellationToken);
}

/// <summary>
/// AI-24 <c>recipient_notices.consent</c> (E4-P1): whether the order recorded the WhatsApp consent box. The record is
/// <c>orders.recipient_notice_consents</c>, which AI-08 NTF-004 creates; until then no order has one, so no WhatsApp
/// notice is ever created (NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 L4 choice 1). E-mail is unaffected.
/// </summary>
public interface IRecipientNoticeConsentReader
{
    Task<bool> HasWhatsAppConsentAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The NTF-005 consent reader: <c>orders.recipient_notice_consents</c> does not exist before AI-08 NTF-004, so no
/// consent is recorded for any order and the answer is always <see langword="false"/> (fail closed).
/// </summary>
public sealed class UnrecordedRecipientNoticeConsentReader : IRecipientNoticeConsentReader
{
    public Task<bool> HasWhatsAppConsentAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

/// <summary>
/// The tracking link a recipient notice carries: the derived token (the WhatsApp URL button suffix) and the full public
/// URL <c>{PublicTracking:PublicBaseUrl}/track/{token}</c> (the e-mail link). Both live in memory only and are never
/// stored, logged or placed in a row, an event or a trace; <see cref="ToString"/> prints neither.
/// </summary>
public sealed class RecipientNoticeTrackingLink(string token, string url)
{
    public string Token { get; } = token;

    public string Url { get; } = url;

    public override string ToString() => "[redacted]";
}

/// <summary>
/// AI-24 <c>recipient_notices.tracking_link.derive</c>: derives the token of a stored link with the Worker's key ring
/// (<c>PublicTracking:LinkKeys</c>), checks that the SHA-256 of its exact UTF-8 bytes equals the stored hash and builds
/// the public URL. No live link (no key ring or base URL, a key version the Worker does not hold, or a hash mismatch) is
/// <see langword="null"/>: the notice ends FAILED with <c>TRACKING_LINK_UNAVAILABLE</c>.
/// </summary>
public interface IRecipientNoticeTrackingLinks
{
    /// <summary>Whether links can be built at all, and with what key (the start validation reads it).</summary>
    RecipientNoticeLinkReadiness Readiness { get; }

    RecipientNoticeTrackingLink? TryBuild(Guid orderId, RecipientNoticeLinkRecord link);
}

/// <summary>
/// AI-24 <c>tracking_token.fail_closed</c>: whether the Worker can build tracking links - no key ring or base URL, only
/// the Development/Testing synthetic key, or a configured key ring.
/// </summary>
public enum RecipientNoticeLinkReadiness
{
    Unavailable,
    Synthetic,
    Configured,
}
