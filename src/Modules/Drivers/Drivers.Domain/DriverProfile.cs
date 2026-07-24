namespace Drivers.Domain;

public sealed class DriverProfile
{
    private DriverProfile()
    {
    }

    public DriverProfile(
        Guid id,
        Guid userId,
        Guid organizationId,
        Guid homeCityId,
        DriverType driverType,
        VehicleType vehicleType,
        DriverProfileStatus status,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || userId == Guid.Empty || organizationId == Guid.Empty ||
            homeCityId == Guid.Empty || !Enum.IsDefined(driverType) || !Enum.IsDefined(vehicleType) ||
            !Enum.IsDefined(status) || createdAt == default || createdAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The driver profile is invalid.");
        }

        Id = id;
        UserId = userId;
        OrganizationId = organizationId;
        HomeCityId = homeCityId;
        DriverType = driverType;
        VehicleType = vehicleType;
        Status = status;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid HomeCityId { get; private set; }
    public DriverType DriverType { get; private set; }
    public VehicleType VehicleType { get; private set; }
    public DriverProfileStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
