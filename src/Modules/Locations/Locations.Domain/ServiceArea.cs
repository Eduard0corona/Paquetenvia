using NetTopologySuite.Geometries;

namespace Locations.Domain;

public sealed class ServiceArea
{
    private ServiceArea()
    {
    }

    public ServiceArea(Guid id, Guid ownerOrganizationId, Guid cityId, string name, MultiPolygon polygon,
        GeographicStatus status, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || ownerOrganizationId == Guid.Empty || cityId == Guid.Empty ||
            string.IsNullOrWhiteSpace(name) || polygon is null || polygon.SRID != 4326 ||
            !Enum.IsDefined(status) || createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The service area is invalid.");
        }

        Id = id;
        OwnerOrganizationId = ownerOrganizationId;
        CityId = cityId;
        Name = name;
        Polygon = polygon;
        Status = status;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid OwnerOrganizationId { get; private set; }
    public Guid CityId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public MultiPolygon Polygon { get; private set; } = null!;
    public GeographicStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
