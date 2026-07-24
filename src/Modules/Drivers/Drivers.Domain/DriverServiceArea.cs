namespace Drivers.Domain;

public sealed class DriverServiceArea
{
    private DriverServiceArea()
    {
    }

    public DriverServiceArea(
        Guid driverId,
        Guid serviceAreaId,
        Guid organizationId,
        DriverServiceAreaStatus status)
    {
        if (driverId == Guid.Empty || serviceAreaId == Guid.Empty || organizationId == Guid.Empty ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentException("The driver service area is invalid.");
        }

        DriverId = driverId;
        ServiceAreaId = serviceAreaId;
        OrganizationId = organizationId;
        Status = status;
    }

    public Guid DriverId { get; private set; }
    public Guid ServiceAreaId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public DriverServiceAreaStatus Status { get; private set; }
}
