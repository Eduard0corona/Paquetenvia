namespace Locations.Domain;

public enum OperatingZoneType
{
    Core,
    Standard,
    Extended,
    Excluded,
}

public static class OperatingZoneTypeExtensions
{
    public static string ToContractValue(this OperatingZoneType value) => value switch
    {
        OperatingZoneType.Core => "CORE",
        OperatingZoneType.Standard => "STANDARD",
        OperatingZoneType.Extended => "EXTENDED",
        OperatingZoneType.Excluded => "EXCLUDED",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
