using NetTopologySuite.Geometries;

namespace Locations.Domain;

public sealed class OperatingZone
{
    private OperatingZone()
    {
    }

    public OperatingZone(Guid id, Guid ownerOrganizationId, Guid serviceAreaId, string name,
        OperatingZoneType zoneType, MultiPolygon polygon, GeographicStatus status, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || ownerOrganizationId == Guid.Empty || serviceAreaId == Guid.Empty ||
            string.IsNullOrWhiteSpace(name) || !Enum.IsDefined(zoneType) || polygon is null || polygon.SRID != 4326 ||
            !Enum.IsDefined(status) || createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The operating zone is invalid.");
        }

        Id = id;
        OwnerOrganizationId = ownerOrganizationId;
        ServiceAreaId = serviceAreaId;
        Name = name;
        ZoneType = zoneType;
        Polygon = polygon;
        Status = status;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid ServiceAreaId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public OperatingZoneType ZoneType { get; private set; }
    public MultiPolygon Polygon { get; private set; } = null!;
    public GeographicStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
