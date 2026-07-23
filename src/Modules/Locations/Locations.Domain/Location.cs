using NetTopologySuite.Geometries;

namespace Locations.Domain;

public sealed class Location
{
    private Location()
    {
    }

    public Location(Guid id, Guid ownerOrganizationId, Guid cityId, Guid? serviceAreaId,
        Guid? operatingZoneId, Point point, byte[] addressCiphertext, string addressSummary,
        byte[]? contactNameCiphertext, byte[]? phoneCiphertext, string piiKeyVersion, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || ownerOrganizationId == Guid.Empty || cityId == Guid.Empty || point is null ||
            point.SRID != 4326 || addressCiphertext is not { Length: > 0 } ||
            string.IsNullOrWhiteSpace(addressSummary) || addressSummary.Length > 180 ||
            string.IsNullOrWhiteSpace(piiKeyVersion) || createdAt.Offset != TimeSpan.Zero)
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
}
