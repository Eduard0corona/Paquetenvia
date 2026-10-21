using NetTopologySuite.Geometries;

namespace Locations.Domain;

public sealed class Location
{
    private Location()
    {
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002): <paramref name="emailCiphertext"/> is the optional recipient
    /// e-mail under the PII key (<paramref name="piiKeyVersion"/>); <paramref name="noticePhoneCiphertext"/> and
    /// <paramref name="noticeEmailCiphertext"/> are the copies under the recipient-notice key
    /// (<paramref name="noticeKeyVersion"/>), present only on quote destinations. The notice key version is present
    /// exactly when a notice copy is, and a notice copy of the e-mail needs the e-mail (the AI-06 checks).
    /// </summary>
    public Location(Guid id, Guid ownerOrganizationId, Guid cityId, Guid? serviceAreaId,
        Guid? operatingZoneId, Point point, byte[] addressCiphertext, string addressSummary,
        byte[]? contactNameCiphertext, byte[]? phoneCiphertext, string piiKeyVersion, DateTimeOffset createdAt,
        byte[]? emailCiphertext = null, byte[]? noticePhoneCiphertext = null, byte[]? noticeEmailCiphertext = null,
        string? noticeKeyVersion = null)
    {
        if (id == Guid.Empty || ownerOrganizationId == Guid.Empty || cityId == Guid.Empty || point is null ||
            point.SRID != 4326 || addressCiphertext is not { Length: > 0 } ||
            string.IsNullOrWhiteSpace(addressSummary) || addressSummary.Length > 180 ||
            string.IsNullOrWhiteSpace(piiKeyVersion) || createdAt.Offset != TimeSpan.Zero ||
            emailCiphertext is { Length: 0 } || noticePhoneCiphertext is { Length: 0 } ||
            noticeEmailCiphertext is { Length: 0 } ||
            (noticeKeyVersion is null) != (noticePhoneCiphertext is null && noticeEmailCiphertext is null) ||
            (noticeKeyVersion is not null && string.IsNullOrWhiteSpace(noticeKeyVersion)) ||
            (noticeEmailCiphertext is not null && emailCiphertext is null))
        {
            throw new ArgumentException("The location is invalid.");
        }

        Id = id;
        OwnerOrganizationId = ownerOrganizationId;
        CityId = cityId;
        ServiceAreaId = serviceAreaId;
        OperatingZoneId = operatingZoneId;
        Point = point;
        AddressCiphertext = addressCiphertext;
        AddressSummary = addressSummary;
        ContactNameCiphertext = contactNameCiphertext;
        PhoneCiphertext = phoneCiphertext;
        PiiKeyVersion = piiKeyVersion;
        CreatedAt = createdAt;
        EmailCiphertext = emailCiphertext;
        NoticePhoneCiphertext = noticePhoneCiphertext;
        NoticeEmailCiphertext = noticeEmailCiphertext;
        NoticeKeyVersion = noticeKeyVersion;
    }

    public Guid Id { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid CityId { get; private set; }
    public Guid? ServiceAreaId { get; private set; }
    public Guid? OperatingZoneId { get; private set; }
    public Point Point { get; private set; } = null!;
    public byte[] AddressCiphertext { get; private set; } = [];
    public string AddressSummary { get; private set; } = string.Empty;
    public byte[]? ContactNameCiphertext { get; private set; }
    public byte[]? PhoneCiphertext { get; private set; }
    public string PiiKeyVersion { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public byte[]? EmailCiphertext { get; private set; }
    public byte[]? NoticePhoneCiphertext { get; private set; }
    public byte[]? NoticeEmailCiphertext { get; private set; }
    public string? NoticeKeyVersion { get; private set; }
}
