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

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2; AI-08 NTF-002): whether quote destinations get the notice copies of their
/// phone and e-mail under the recipient-notice key. <see cref="Disabled"/> (the default) writes none.
/// </summary>
public enum LocationNoticeKeyProtectorKind
{
    Disabled,

    /// <summary>Synthetic one-way digests; Development, Testing and DEV_SYNTHETIC only.</summary>
    Mock,

    /// <summary>The ADP-001 envelope under the Key Vault recipient-notice key, wrapped with its public part.</summary>
    AzureKeyVault,
}

public sealed class LocationsOptions
{
    public const string SectionName = "Locations";

    public LocationsProviderKind Provider { get; set; }
    public GeocodingProviderKind GeocodingProvider { get; set; }
    public LocationPiiProtectorKind PiiProtector { get; set; }
    public LocationNoticeKeyProtectorKind NoticeKeyProtector { get; set; }
    public GoogleMapsGeocodingOptions GoogleMaps { get; set; } = new();
    public int CommandTimeoutSeconds { get; set; } = 30;
}
