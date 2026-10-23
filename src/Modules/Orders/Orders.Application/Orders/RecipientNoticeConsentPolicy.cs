namespace Orders.Application.Orders;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (project owner, E4-P1 "Casilla en la orden (Recommended)"; AI-24
/// <c>recipient_notices.consent</c>; AI-08 NTF-004) and NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N3 choices 1 to 5): the
/// WhatsApp consent box of "Nueva orden" and of the CSV-001 commit. It is optional and unticked by default; ticked, the
/// order records one row of <c>orders.recipient_notice_consents</c> (order, WHATSAPP, <see cref="Version"/>, the
/// creating actor and the server time) in its creation transaction, and the row never changes. Without that row no
/// WhatsApp notice is created for the order; e-mail is unaffected. No phone, e-mail or name is ever part of it.
/// </summary>
public static class RecipientNoticeConsentPolicy
{
    /// <summary>The approved box text (AI-24 <c>consent.text</c>), version <see cref="Version"/>.</summary>
    public const string Text = "El destinatario aceptó recibir avisos de su envío por WhatsApp";

    /// <summary>The only consent version the API accepts and records (AI-24 <c>consent.version</c>).</summary>
    public const string Version = "NTF-RECIPIENT-WHATSAPP-CONSENT-V1";

    /// <summary>The only channel a consent row names.</summary>
    public const string Channel = "WHATSAPP";

    /// <summary>
    /// CSV-001 commit form field (AI-05 <c>CsvImportCommitRequest.recipient_whatsapp_consent_attested</c>): <c>true</c> or
    /// <c>false</c>, at most once.
    /// </summary>
    public const string CsvFieldAttested = "recipient_whatsapp_consent_attested";

    /// <summary>CSV-001 commit form field with the consent version, required with <c>true</c> and absent otherwise.</summary>
    public const string CsvFieldVersion = "recipient_whatsapp_consent_version";

    /// <summary>Whether a command's consent version is acceptable: absent (not ticked) or exactly <see cref="Version"/>.</summary>
    public static bool IsAcceptable(string? version) => version is null || string.Equals(version, Version, StringComparison.Ordinal);

    /// <summary>
    /// Reads the CSV-001 commit fields: each given at most once; both absent, or <c>false</c> without a version, is no
    /// consent (<paramref name="version"/> <see langword="null"/>); <c>true</c> with exactly <see cref="Version"/> is the
    /// consent. Anything else (another value, a version without <c>true</c>, <c>true</c> without the version, a repeated
    /// field) is refused, and the caller answers the uniform 409 before the batch is reserved.
    /// </summary>
    public static bool TryReadCsvFields(IReadOnlyList<string?> attested, IReadOnlyList<string?> versions, out string? version)
    {
        ArgumentNullException.ThrowIfNull(attested);
        ArgumentNullException.ThrowIfNull(versions);
        version = null;
        if (attested.Count > 1 || versions.Count > 1)
        {
            return false;
        }

        if (attested.Count == 0)
        {
            return versions.Count == 0;
        }

        switch (attested[0])
        {
            case "false":
                return versions.Count == 0;
            case "true" when versions.Count == 1 && string.Equals(versions[0], Version, StringComparison.Ordinal):
                version = Version;
                return true;
            default:
                return false;
        }
    }
}
