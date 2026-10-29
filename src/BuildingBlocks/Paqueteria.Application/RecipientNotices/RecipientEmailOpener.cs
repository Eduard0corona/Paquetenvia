namespace Paqueteria.Application.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O11 choice 1; AI-05 <c>unsubscribeRecipientNotices</c>): the e-mail of a
/// delivery destination exactly as <c>locations.locations</c> keeps it under the PII key (<c>email_ciphertext</c> and the
/// row's <c>pii_key_version</c>), bound to the owner organization and the location row. <see cref="ToString"/> prints no
/// ciphertext.
/// </summary>
public sealed record StoredRecipientEmail(
    Guid OwnerOrganizationId,
    Guid LocationId,
    string KeyVersion,
    byte[] Ciphertext)
{
    public override string ToString() => $"StoredRecipientEmail {{ LocationId = {LocationId:D}, [ciphertext redacted] }}";
}

public enum RecipientEmailOpenStatus
{
    /// <summary>The e-mail was opened and normalized (or, synthetic, replaced by its placeholder).</summary>
    Opened,

    /// <summary>The copy cannot be read for its row or is no e-mail: the link answers the uniform 404.</summary>
    Unusable,

    /// <summary>The PII key could not be reached: the link answers 503 and may be retried.</summary>
    Unavailable,
}

/// <summary>An opened e-mail, in memory only; <see cref="ToString"/> never prints it.</summary>
public sealed class OpenedRecipientEmail
{
    public static readonly OpenedRecipientEmail Unusable = new(RecipientEmailOpenStatus.Unusable, null);
    public static readonly OpenedRecipientEmail Unavailable = new(RecipientEmailOpenStatus.Unavailable, null);

    private OpenedRecipientEmail(RecipientEmailOpenStatus status, string? normalized)
    {
        Status = status;
        Normalized = normalized;
    }

    public RecipientEmailOpenStatus Status { get; }

    /// <summary>The trimmed, lower-cased address the opt-out hash is taken of.</summary>
    public string? Normalized { get; }

    public static OpenedRecipientEmail Opened(string normalized) => new(RecipientEmailOpenStatus.Opened, normalized);

    public override string ToString() => $"OpenedRecipientEmail {{ Status = {Status} }}";
}

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (O11 choice 1, "con la llave PII de la API"): the Locations module opens the
/// e-mail of a destination the way it protected it (<c>Locations:PiiProtector</c>), so no other module repeats the
/// protection mode, the purpose or the storage format. The API calls it after the transaction that read the copy
/// committed; the value never leaves memory. A synthetic protector never decrypts and answers the AI-24 placeholder.
/// </summary>
public interface IRecipientEmailOpener
{
    Task<OpenedRecipientEmail> OpenAsync(StoredRecipientEmail email, CancellationToken cancellationToken);
}
