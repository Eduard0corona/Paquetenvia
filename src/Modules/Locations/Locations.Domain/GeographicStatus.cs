namespace Locations.Domain;

public enum GeographicStatus
{
    Active,
    Inactive,
}

public static class GeographicStatusExtensions
{
    public static string ToContractValue(this GeographicStatus value) => value switch
    {
        GeographicStatus.Active => "ACTIVE",
        GeographicStatus.Inactive => "INACTIVE",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
