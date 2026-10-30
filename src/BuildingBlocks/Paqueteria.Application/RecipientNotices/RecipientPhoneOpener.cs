namespace Paqueteria.Application.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (T6 choices 5 and 12; AI-05 <c>stopOrderRecipientNotices</c>): the phone of a
/// delivery destination exactly as <c>locations.locations</c> keeps it under the PII key (<c>phone_ciphertext</c> and the
/// row's <c>pii_key_version</c>), bound to the owner organization and the location row. <see cref="ToString"/> prints no
/// ciphertext.
/// </summary>
public sealed record StoredRecipientPhone(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string KeyVersion,
    byte[] Ciphertext)
{
    public override string ToString() => $"StoredRecipientPhone {{ LocationId = {LocationId:D}, [ciphertext redacted] }}";
}

public enum RecipientPhoneOpenStatus
{
    /// <summary>The phone was opened and normalized to E.164 <c>+52</c> (or, synthetic, replaced by its placeholder).</summary>
    Opened,

    /// <summary>The copy cannot be read for its row or is no Mexican number: that channel is left out.</summary>
    Unusable,

    /// <summary>The PII key could not be reached: the stop answers 503 and may be retried.</summary>
    Unavailable,
}

/// <summary>An opened phone, in memory only; <see cref="ToString"/> never prints it.</summary>
public sealed class OpenedRecipientPhone
{
    public static readonly OpenedRecipientPhone Unusable = new(RecipientPhoneOpenStatus.Unusable, null);
    public static readonly OpenedRecipientPhone Unavailable = new(RecipientPhoneOpenStatus.Unavailable, null);

    private OpenedRecipientPhone(RecipientPhoneOpenStatus status, string? normalized)
    {
        Status = status;
        Normalized = normalized;
    }

    public RecipientPhoneOpenStatus Status { get; }

    /// <summary>The E.164 <c>+52</c> number the opt-out hash is taken of.</summary>
    public string? Normalized { get; }

    public static OpenedRecipientPhone Opened(string normalized) => new(RecipientPhoneOpenStatus.Opened, normalized);

    public override string ToString() => $"OpenedRecipientPhone {{ Status = {Status} }}";
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (T6 choice 12, "Un abridor del teléfono con la llave de datos personales en
/// Locations, como el del correo de NTF-006"): the Locations module opens the phone of a destination the way it protected
/// it (<c>Locations:PiiProtector</c>), so no other module repeats the protection mode, the purpose or the storage format.
/// The API calls it after the transaction that read the copy committed (AI-13 §7.2); the value never leaves memory. A
/// synthetic protector never decrypts and answers the AI-24 placeholder.
/// </summary>
public interface IRecipientPhoneOpener
{
    Task<OpenedRecipientPhone> OpenAsync(StoredRecipientPhone phone, CancellationToken cancellationToken);
}
