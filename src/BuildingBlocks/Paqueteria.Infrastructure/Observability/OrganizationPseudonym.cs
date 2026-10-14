using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Paqueteria.Infrastructure.Observability;

/// <summary>
/// OBS-003-TENANT-PSEUDONYM-2026-10-12 ("Seudónimo de empresa (Recommended)"): the key of the organization pseudonym.
/// In Azure it is the Key Vault secret <c>paquetenvia-telemetry-pseudonym-key-1</c>, which the owner creates and the
/// API and Worker read at start (<c>TelemetryPseudonym__Keys__1</c> through <c>KeyVaultSecrets:Mappings</c>); never in
/// appsettings, the database, a log, GitHub or a pipeline variable.
/// </summary>
/// <remarks>
/// Exactly one key is configured: the one of <see cref="CurrentKeyVersion"/>, Base64 of 32 to 128 random bytes.
/// Rotating it means a new secret version name and a new <see cref="CurrentKeyVersion"/>; every pseudonym written
/// afterwards carries the new version. Development and Testing use a fixed synthetic key (version 0) when none is
/// configured; every other environment fails at start without a key, because the logs carry the pseudonym even when
/// the export is off.
/// </remarks>
public sealed class TelemetryPseudonymOptions
{
    public const string SectionName = "TelemetryPseudonym";
    public const int MinimumKeyBytes = 32;
    public const int MaximumKeyBytes = 128;
    public const int MaximumKeyVersion = 32767;

    /// <summary>The version the synthetic Development and Testing key is written under; never a configured version.</summary>
    public const int SyntheticKeyVersion = 0;

    /// <summary>The version every pseudonym is written under.</summary>
    public int CurrentKeyVersion { get; set; } = 1;

    /// <summary>Key version to Base64 key; exactly the current version.</summary>
    public Dictionary<int, string> Keys { get; set; } = [];
}

/// <summary>Computes the organization pseudonym (<see cref="OrganizationPseudonym"/>).</summary>
public interface IOrganizationPseudonymizer
{
    /// <summary>The key version the pseudonyms of this process carry.</summary>
    int KeyVersion { get; }

    /// <summary><c>v{version}-</c> and 32 lowercase hex digits; never the organization id.</summary>
    string Pseudonymize(Guid organizationId);
}

/// <summary>
/// HMAC-SHA256 over a versioned, domain-separated message: <c>paquetenvia.telemetry.organization-pseudonym.v1</c>, a
/// line feed and the UTF-8 bytes of the organization id in its 36-character lowercase form. The pseudonym is
/// <c>v{key version}-</c> followed by the first 16 bytes of the MAC as lowercase hex.
/// </summary>
public static partial class OrganizationPseudonym
{
    /// <summary>The domain separator of the MAC input.</summary>
    public const string Domain = "paquetenvia.telemetry.organization-pseudonym.v1\n";

    private const string SyntheticKeySeed = "paquetenvia.telemetry.organization-pseudonym.synthetic-key";

    /// <summary>
    /// The Development and Testing key: SHA-256 of a public constant, so anyone can recompute it. It protects nothing
    /// and is never used outside those environments.
    /// </summary>
    internal static byte[] SyntheticKey() => SHA256.HashData(Encoding.UTF8.GetBytes(SyntheticKeySeed));

    /// <summary>The pseudonym of <paramref name="organizationId"/> under <paramref name="key"/> and its version.</summary>
    public static string Compute(ReadOnlySpan<byte> key, int keyVersion, Guid organizationId)
    {
        if (keyVersion is < TelemetryPseudonymOptions.SyntheticKeyVersion or > TelemetryPseudonymOptions.MaximumKeyVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(keyVersion));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty organization id is required.", nameof(organizationId));
        }

        var message = Encoding.UTF8.GetBytes(Domain + organizationId.ToString("D", CultureInfo.InvariantCulture));
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, message, mac);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"v{keyVersion}-{Convert.ToHexStringLower(mac[..16])}");
    }

    /// <summary>True for a value with the shape of a pseudonym.</summary>
    public static bool IsPseudonym(string? value) => value is not null && PseudonymPattern().IsMatch(value);

    [GeneratedRegex(@"^v(?:0|[1-9][0-9]{0,4})-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PseudonymPattern();
}

public static class OrganizationPseudonymServiceCollectionExtensions
{
    /// <summary>
    /// Registers the organization pseudonymizer. A configured key must be valid in every environment; outside
    /// Development and Testing a key is required and the host fails at start without one.
    /// </summary>
    public static IServiceCollection AddTelemetryOrganizationPseudonym(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var allowsSyntheticKey = AllowsSyntheticKey(environment);
        services.AddOptions<TelemetryPseudonymOptions>()
            .Bind(configuration.GetSection(TelemetryPseudonymOptions.SectionName))
            .Validate(
                options => options.Keys.Count > 0 || allowsSyntheticKey,
                "TelemetryPseudonym:Keys is required outside Development and Testing: the logs carry the organization " +
                "pseudonym even when the export is off (OBS-003-TENANT-PSEUDONYM-2026-10-12).")
            .Validate(
                options => options.Keys.Count == 0 || TelemetryPseudonymKey.TryDecode(options, out _),
                "TelemetryPseudonym must hold exactly one key, the one of CurrentKeyVersion (1 to 32767), Base64 of 32 to 128 bytes.")
            .ValidateOnStart();
        services.TryAddSingleton<IOrganizationPseudonymizer>(provider =>
            new HmacOrganizationPseudonymizer(provider.GetRequiredService<IOptions<TelemetryPseudonymOptions>>(), allowsSyntheticKey));
        return services;
    }

    internal static bool AllowsSyntheticKey(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");
}

internal static class TelemetryPseudonymKey
{
    public static bool TryDecode(TelemetryPseudonymOptions options, out byte[] key)
    {
        key = [];
        if (options.Keys.Count != 1 ||
            options.CurrentKeyVersion is < 1 or > TelemetryPseudonymOptions.MaximumKeyVersion ||
            !options.Keys.TryGetValue(options.CurrentKeyVersion, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            key = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return false;
        }

        return key.Length is >= TelemetryPseudonymOptions.MinimumKeyBytes and <= TelemetryPseudonymOptions.MaximumKeyBytes;
    }
}

/// <summary>The configured key, or the synthetic one in Development and Testing when none is configured.</summary>
public sealed class HmacOrganizationPseudonymizer : IOrganizationPseudonymizer
{
    private readonly byte[] _key;

    internal HmacOrganizationPseudonymizer(IOptions<TelemetryPseudonymOptions> options, bool allowsSyntheticKey)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (value.Keys.Count > 0 && TelemetryPseudonymKey.TryDecode(value, out var key))
        {
            _key = key;
            KeyVersion = value.CurrentKeyVersion;
        }
        else if (value.Keys.Count == 0 && allowsSyntheticKey)
        {
            _key = OrganizationPseudonym.SyntheticKey();
            KeyVersion = TelemetryPseudonymOptions.SyntheticKeyVersion;
        }
        else
        {
            throw new OptionsValidationException(
                TelemetryPseudonymOptions.SectionName,
                typeof(TelemetryPseudonymOptions),
                ["TelemetryPseudonym has no valid key."]);
        }
    }

    public int KeyVersion { get; }

    public string Pseudonymize(Guid organizationId) => OrganizationPseudonym.Compute(_key, KeyVersion, organizationId);
}

/// <summary>
/// The organization context of the current flow, for the log lines written inside it: the pseudonym and the actor role
/// kind, never the organization id. The API enters it after the tenant context middleware selected an organization;
/// lines written outside (host start, the Worker, the OBS-002 summaries) carry neither.
/// </summary>
public static class TelemetryOrganizationContext
{
    /// <summary>Structured log property of the organization pseudonym (never an OBS-002 telemetry dimension).</summary>
    public const string PseudonymLogProperty = "OrganizationPseudonym";

    /// <summary>Structured log property of the actor role kind (never an OBS-002 telemetry dimension).</summary>
    public const string RoleKindLogProperty = "ActorRoleKind";

    private static readonly AsyncLocal<Entry?> CurrentEntry = new();

    /// <summary>The pseudonym and role kind of the current flow, or null outside an organization context.</summary>
    public static (string Pseudonym, string RoleKind)? Current =>
        CurrentEntry.Value is { } entry ? (entry.Pseudonym, entry.RoleKind) : null;

    /// <summary>Enters an organization context for the rest of the flow; dispose to restore the previous one.</summary>
    public static IDisposable Enter(string pseudonym, string roleKind)
    {
        if (!OrganizationPseudonym.IsPseudonym(pseudonym))
        {
            throw new ArgumentException("Not an organization pseudonym.", nameof(pseudonym));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(roleKind);
        var previous = CurrentEntry.Value;
        CurrentEntry.Value = new Entry(pseudonym, roleKind);
        return new Restore(previous);
    }

    private sealed record Entry(string Pseudonym, string RoleKind);

    private sealed class Restore(Entry? previous) : IDisposable
    {
        public void Dispose() => CurrentEntry.Value = previous;
    }
}
