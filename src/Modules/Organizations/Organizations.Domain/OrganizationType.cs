namespace Organizations.Domain;

public enum OrganizationType
{
    Platform,
    Ally,
    Business,
}

public static class OrganizationTypeExtensions
{
    public static string ToContractValue(this OrganizationType value) => value switch
    {
        OrganizationType.Platform => "PLATFORM",
        OrganizationType.Ally => "ALLY",
        OrganizationType.Business => "BUSINESS",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown organization type."),
    };
}
