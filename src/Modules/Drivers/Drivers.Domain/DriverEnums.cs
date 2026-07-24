namespace Drivers.Domain;

public enum DriverType
{
    Own,
    External,
    Ally,
}

public enum VehicleType
{
    Motorcycle,
    Car,
    Van,
    Bicycle,
    Walker,
}

public enum DriverProfileStatus
{
    Pending,
    Active,
    Suspended,
    Inactive,
}

public enum DriverServiceAreaStatus
{
    Active,
    Inactive,
}

public enum DriverDocumentType
{
    Identity,
    DriverLicense,
    VehicleCard,
    Insurance,
    BackgroundCheck,
    Other,
}

public enum DriverDocumentStatus
{
    Pending,
    Valid,
    Rejected,
    Expired,
    Revoked,
}

public static class DriverEnumContract
{
    public static string ToContractValue(this DriverType value) => value switch
    {
        DriverType.Own => "OWN",
        DriverType.External => "EXTERNAL",
        DriverType.Ally => "ALLY",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this VehicleType value) => value switch
    {
        VehicleType.Motorcycle => "MOTORCYCLE",
        VehicleType.Car => "CAR",
        VehicleType.Van => "VAN",
        VehicleType.Bicycle => "BICYCLE",
        VehicleType.Walker => "WALKER",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverProfileStatus value) => value switch
    {
        DriverProfileStatus.Pending => "PENDING",
        DriverProfileStatus.Active => "ACTIVE",
        DriverProfileStatus.Suspended => "SUSPENDED",
        DriverProfileStatus.Inactive => "INACTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverServiceAreaStatus value) => value switch
    {
        DriverServiceAreaStatus.Active => "ACTIVE",
        DriverServiceAreaStatus.Inactive => "INACTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverDocumentType value) => value switch
    {
        DriverDocumentType.Identity => "IDENTITY",
        DriverDocumentType.DriverLicense => "DRIVER_LICENSE",
        DriverDocumentType.VehicleCard => "VEHICLE_CARD",
        DriverDocumentType.Insurance => "INSURANCE",
        DriverDocumentType.BackgroundCheck => "BACKGROUND_CHECK",
        DriverDocumentType.Other => "OTHER",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToContractValue(this DriverDocumentStatus value) => value switch
    {
        DriverDocumentStatus.Pending => "PENDING",
        DriverDocumentStatus.Valid => "VALID",
        DriverDocumentStatus.Rejected => "REJECTED",
        DriverDocumentStatus.Expired => "EXPIRED",
        DriverDocumentStatus.Revoked => "REVOKED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
