using Microsoft.Extensions.Configuration;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-003 phase 4 PR 6 (OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12): the <c>TelemetryExport</c> section that turns the
/// export of traces and metrics to Application Insights on. It is off by default: with <see cref="Enabled"/> false
/// nothing is registered, so no exporter exists and nothing leaves the process beyond the console logs.
/// </summary>
/// <remarks>
/// The section fails closed when the host starts: an unknown key, a value that is not a boolean, an export turned on
/// without a connection string, or an export turned on with a connection string that is not an Application Insights
/// one stops the host. With the export off the connection string is neither checked nor kept, so a key Azure adds to
/// the component's string can never stop a host that exports nothing. The sampling ratio is not configurable: it is
/// <see cref="TelemetryExportPolicy.TraceSamplingRatio"/> (10 %).
/// </remarks>
public sealed class TelemetryExportSettings
{
    public const string SectionName = "TelemetryExport";

    /// <summary>The only ingestion host suffix accepted (public Azure; the pilot is in Mexico Central).</summary>
    public const string IngestionHostSuffix = ".applicationinsights.azure.com";

    private static readonly HashSet<string> ConnectionStringKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "InstrumentationKey",
        "IngestionEndpoint",
        "LiveEndpoint",
        "ApplicationId",
    };

    private TelemetryExportSettings(bool enabled, string? connectionString)
    {
        Enabled = enabled;
        ConnectionString = connectionString;
    }

    /// <summary>True only when configuration turns the export on.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// The Application Insights connection string (instrumentation key ingestion, Q-PR6-2); null while the export is off.
    /// </summary>
    public string? ConnectionString { get; }

    /// <summary>Reads and validates the section; throws <see cref="TelemetryExportConfigurationException"/>.</summary>
    public static TelemetryExportSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var bound = new Section();
        try
        {
            section.Bind(bound, options => options.ErrorOnUnknownConfiguration = true);
        }
        catch (InvalidOperationException)
        {
            // The binder's own message can quote the value; this one names the settings only.
            throw new TelemetryExportConfigurationException(
                "TelemetryExport accepts only Enabled (true or false) and ConnectionString.");
        }

        if (!bound.Enabled)
        {
            // Off: nothing reads the connection string, so it is not checked (an unexpected key in the string Azure
            // writes must not stop a host that exports nothing) and not kept.
            return new TelemetryExportSettings(enabled: false, connectionString: null);
        }

        var connectionString = string.IsNullOrWhiteSpace(bound.ConnectionString) ? null : bound.ConnectionString;
        if (connectionString is null)
        {
            throw new TelemetryExportConfigurationException(
                "TelemetryExport:Enabled is true but TelemetryExport:ConnectionString is missing.");
        }

        // On: only the keys this file knows. An unknown key (EndpointSuffix, Authorization, AADAudience...) can change
        // where or how the exporter sends, so the export refuses to start instead of ignoring it.
        if (!IsIngestionConnectionString(connectionString))
        {
            throw new TelemetryExportConfigurationException(
                "TelemetryExport:ConnectionString must be an Application Insights connection string: an " +
                "InstrumentationKey GUID and an https IngestionEndpoint on " + IngestionHostSuffix + ".");
        }

        return new TelemetryExportSettings(enabled: true, connectionString);
    }

    /// <summary>
    /// <c>InstrumentationKey</c> (a GUID) and <c>IngestionEndpoint</c> (https, on <see cref="IngestionHostSuffix"/>, no
    /// path beyond <c>/</c>, query or credentials) are required; only the keys Azure writes are accepted.
    /// </summary>
    internal static bool IsIngestionConnectionString(string value)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            var key = part[..separator].Trim();
            if (!ConnectionStringKeys.Contains(key) || !pairs.TryAdd(key, part[(separator + 1)..].Trim()))
            {
                return false;
            }
        }

        return pairs.TryGetValue("InstrumentationKey", out var ikey) &&
               Guid.TryParseExact(ikey, "D", out var instrumentationKey) &&
               instrumentationKey != Guid.Empty &&
               pairs.TryGetValue("IngestionEndpoint", out var endpoint) &&
               IsIngestionEndpoint(endpoint) &&
               (!pairs.TryGetValue("LiveEndpoint", out var live) || IsIngestionEndpoint(live, ".monitor.azure.com")) &&
               (!pairs.TryGetValue("ApplicationId", out var application) || Guid.TryParseExact(application, "D", out _));
    }

    private static bool IsIngestionEndpoint(string value, string suffix = IngestionHostSuffix) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Length > suffix.Length;

    private sealed class Section
    {
        public bool Enabled { get; set; }

        public string? ConnectionString { get; set; }
    }
}

/// <summary>The <c>TelemetryExport</c> section is invalid; the message names the setting, never its value.</summary>
public sealed class TelemetryExportConfigurationException(string message) : Exception(message);
