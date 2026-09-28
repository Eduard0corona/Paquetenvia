using Locations.Infrastructure.Geocoding.GoogleMaps;

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

    /// <summary>
    /// GATE-003-PROVIDER-GOOGLE: Google Maps Platform Geocoding API; degrades to the manual pin
    /// when the provider is unavailable or gives no precise single match.
    /// </summary>
    GoogleMaps,
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
    public GoogleMapsGeocodingOptions GoogleMaps { get; set; } = new();
    public int CommandTimeoutSeconds { get; set; } = 30;
}
