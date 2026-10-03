using System.Text.RegularExpressions;

namespace Locations.Infrastructure.Geocoding.GoogleMaps;

/// <summary>
/// GATE-003-PROVIDER-GOOGLE: <c>Locations:GoogleMaps</c>. <see cref="ApiKey"/> is never set in plain
/// configuration: the pilot maps the Key Vault secret <c>google-maps-api-key</c> to
/// <c>Locations:GoogleMaps:ApiKey</c> through <c>KeyVaultSecrets:Mappings</c>. Nothing in this type
/// is ever logged.
/// </summary>
public sealed partial class GoogleMapsGeocodingOptions
{
    public const string DefaultBaseUri = "https://maps.googleapis.com/";

    /// <summary>
    /// GATE-003-MAPS-PILOT-RULES-2026-10-02: searches are restricted to Mexico. The provider always
    /// sends <c>components=country:MX</c> from this constant, never from configuration.
    /// </summary>
    public const string PilotCountry = "MX";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Scheme and host of the Geocoding API; tests point it at a fake handler.</summary>
    public string BaseUri { get; set; } = DefaultBaseUri;

    /// <summary>ccTLD region bias (<c>region=</c>); empty sends none.</summary>
    public string Region { get; set; } = "mx";

    /// <summary>
    /// Locked to <see cref="PilotCountry"/> (GATE-003-MAPS-PILOT-RULES-2026-10-02). It is kept only so
    /// that a configured override fails closed: any other value, empty included, fails validation at
    /// start instead of being silently ignored. The request never reads it.
    /// </summary>
    public string ComponentsCountry { get; set; } = PilotCountry;

    /// <summary>Timeout of one HTTP attempt, response body included.</summary>
    public int AttemptTimeoutMilliseconds { get; set; } = 3000;

    /// <summary>Retries after the first attempt, only for transient failures (the call is an idempotent GET).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Exponential backoff base; attempt n waits base * 2^(n-1) plus up to 50 % jitter.</summary>
    public int RetryBaseDelayMilliseconds { get; set; } = 200;

    /// <summary>Consecutive failed calls (after their retries) that open the circuit.</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>How long an open circuit skips the provider before one half-open probe.</summary>
    public int CircuitBreakerBreakSeconds { get; set; } = 30;

    /// <summary>Bulkhead: concurrent provider calls; a call beyond it falls back at once.</summary>
    public int MaxConcurrentRequests { get; set; } = 8;

    internal TimeSpan AttemptTimeout => TimeSpan.FromMilliseconds(AttemptTimeoutMilliseconds);

    internal TimeSpan BreakDuration => TimeSpan.FromSeconds(CircuitBreakerBreakSeconds);

    /// <summary>Value-free validation; the message never contains the key.</summary>
    public static bool IsValid(GoogleMapsGeocodingOptions options) =>
        options is not null &&
        !string.IsNullOrWhiteSpace(options.ApiKey) &&
        ApiKeyPattern().IsMatch(options.ApiKey) &&
        IsValidBaseUri(options.BaseUri) &&
        (options.Region.Length == 0 || RegionPattern().IsMatch(options.Region)) &&
        string.Equals(options.ComponentsCountry, PilotCountry, StringComparison.Ordinal) &&
        options.AttemptTimeoutMilliseconds is >= 200 and <= 10_000 &&
        options.MaxRetries is >= 0 and <= 3 &&
        options.RetryBaseDelayMilliseconds is >= 0 and <= 5_000 &&
        options.CircuitBreakerFailureThreshold is >= 1 and <= 50 &&
        options.CircuitBreakerBreakSeconds is >= 1 and <= 600 &&
        options.MaxConcurrentRequests is >= 1 and <= 64;

    private static bool IsValidBaseUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    // Printable, no whitespace and nothing that changes the query string once escaped is still
    // escaped; the pattern only rejects obvious configuration mistakes (empty, spaces, line breaks).
    [GeneratedRegex("^[\\x21-\\x7E]{8,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex("^[a-z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();
}
