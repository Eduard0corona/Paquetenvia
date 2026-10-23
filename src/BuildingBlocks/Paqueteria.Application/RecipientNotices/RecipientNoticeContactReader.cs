namespace Paqueteria.Application.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.contact.read_at_send</c>; AI-08 NTF-005): the notice
/// copies of one destination location - the phone and the optional e-mail sealed under the recipient-notice key, their
/// key version and the binding (owner organization and location row) they are authenticated with. Ciphertexts only:
/// nothing here is readable without the Worker's unwrap-only notice key. <see cref="ToString"/> prints no ciphertext.
/// </summary>
public sealed record RecipientNoticeContactCopies(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string? KeyVersion,
    byte[]? PhoneCiphertext,
    byte[]? EmailCiphertext)
{
    /// <summary>The ADP-001 purpose the phone copy is bound to.</summary>
    public const string PhonePurpose = "locations.notice_phone";

    /// <summary>The ADP-001 purpose the e-mail copy is bound to.</summary>
    public const string EmailPurpose = "locations.notice_email";

    public bool HasPhone => KeyVersion is not null && PhoneCiphertext is { Length: > 0 };

    public bool HasEmail => KeyVersion is not null && EmailCiphertext is { Length: > 0 };

    public override string ToString() =>
        $"RecipientNoticeContactCopies {{ LocationId = {LocationId:D}, HasPhone = {HasPhone}, HasEmail = {HasEmail} }}";
}

/// <summary>
/// The reader the Locations module publishes for the Worker (Locations.Infrastructure,
/// <c>AddLocationsRecipientNoticeContactReader</c>; the port lives here so the Notifications lane consumes it without
/// the Locations domain) for recipient notices (AI-24 <c>recipient_notices.expansion.reads</c> and
/// <c>contact.read_at_send</c>): one short tenant transaction of the owner organization under FORCE RLS, committed
/// before any decryption. A location the organization does not own is <see langword="null"/>.
/// </summary>
public interface IRecipientNoticeContactReader
{
    Task<RecipientNoticeContactCopies?> ReadAsync(
        Guid ownerOrganizationId,
        Guid locationId,
        CancellationToken cancellationToken);
}
