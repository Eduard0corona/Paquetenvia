namespace Locations.Infrastructure;

public enum LocationsProviderKind
{
    Disabled,
    PostgreSql,
}

public enum GeocodingProviderKind
{
    Disabled,
    Manual,
    Mock,
}

public enum LocationPiiProtectorKind
{
    Disabled,
    Mock,

    /// <summary>ADP-001 production envelope protector backed by Azure Key Vault.</summary>
    AzureKeyVault,
}

public sealed class LocationsOptions
{
    public const string SectionName = "Locations";

    public LocationsProviderKind Provider { get; set; }
    public GeocodingProviderKind GeocodingProvider { get; set; }
    public LocationPiiProtectorKind PiiProtector { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 30;
}
