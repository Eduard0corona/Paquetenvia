using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Security;

namespace Paqueteria.Infrastructure.Security;

/// <summary>
/// REG-PENDING-MEMBERSHIP-STORAGE key material. Each key is Base64 of 32 to 128 random bytes, stored
/// in Key Vault in Azure (<c>EmailLookup__Keys__1</c> as a Key Vault reference) and in
/// <c>dotnet user-secrets</c> locally; never in appsettings, GitHub or pipeline variables.
/// </summary>
public sealed class EmailLookupOptions
{
    public const string SectionName = "EmailLookup";
    public const int MaximumKeys = 8;
    public const int MinimumKeyBytes = 32;
    public const int MaximumKeyBytes = 128;
    public const int MaximumKeyVersion = 32767;

    /// <summary>The version new pending memberships are stored under.</summary>
    public int CurrentKeyVersion { get; set; } = 1;

    /// <summary>Key version to Base64 key. Older versions stay configured until their entries expire.</summary>
    public Dictionary<int, string> Keys { get; set; } = [];
}

public static class EmailLookupHashingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the email lookup hasher. Any configured key must be valid in every environment. A
    /// missing key fails the host at start whenever registration or tenancy runs on PostgreSQL, except
    /// in Development and Testing, where the feature simply stays unavailable.
    /// </summary>
    public static IServiceCollection AddEmailLookupHashing(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var requiresKey = !(environment.IsDevelopment() || environment.IsEnvironment("Testing")) &&
            (IsPostgreSql(configuration["Tenancy:Provider"]) || IsPostgreSql(configuration["IdentityBootstrap:Provider"]));
        services.AddOptions<EmailLookupOptions>()
            .Bind(configuration.GetSection(EmailLookupOptions.SectionName))
            .Validate(
                options => options.Keys.Count > 0 || !requiresKey,
                "EmailLookup:Keys is required outside Development and Testing when Tenancy or IdentityBootstrap uses PostgreSql.")
            .Validate(
                EmailLookupKeyRing.IsValid,
                "EmailLookup must hold 1 to 8 keys with versions 1 to 32767, each Base64 of 32 to 128 bytes, and CurrentKeyVersion must be one of them.")
            .ValidateOnStart();
        services.TryAddSingleton<IEmailLookupHasher, HmacEmailLookupHasher>();
        return services;
    }

    private static bool IsPostgreSql(string? value) =>
        string.Equals(value, "PostgreSql", StringComparison.OrdinalIgnoreCase);
}

internal static class EmailLookupKeyRing
{
    public static bool IsValid(EmailLookupOptions options) =>
        options.Keys.Count == 0 || TryDecode(options, out _);

    public static bool TryDecode(EmailLookupOptions options, out IReadOnlyDictionary<int, byte[]> keys)
    {
        keys = new Dictionary<int, byte[]>();
        if (options.Keys.Count is < 1 or > EmailLookupOptions.MaximumKeys ||
            !options.Keys.ContainsKey(options.CurrentKeyVersion))
        {
            return false;
        }

        var decoded = new Dictionary<int, byte[]>();
        foreach (var (version, value) in options.Keys)
        {
            if (version is < 1 or > EmailLookupOptions.MaximumKeyVersion || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            byte[] key;
            try
            {
                key = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return false;
            }

            if (key.Length is < EmailLookupOptions.MinimumKeyBytes or > EmailLookupOptions.MaximumKeyBytes)
            {
                return false;
            }

            decoded[version] = key;
        }

        keys = decoded;
        return true;
    }
}

/// <summary>
/// HMAC-SHA256 over a versioned, domain-separated message: <c>paquetenvia.email-lookup.v1</c>, a line
/// feed and the UTF-8 bytes of the normalized address.
/// </summary>
public sealed class HmacEmailLookupHasher : IEmailLookupHasher
{
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("paquetenvia.email-lookup.v1\n");
    private readonly IReadOnlyDictionary<int, byte[]> keys;
    private readonly int currentVersion;

    public HmacEmailLookupHasher(IOptions<EmailLookupOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (value.Keys.Count > 0 && EmailLookupKeyRing.TryDecode(value, out var decoded))
        {
            keys = decoded;
            currentVersion = value.CurrentKeyVersion;
        }
        else
        {
            keys = new Dictionary<int, byte[]>();
        }
    }

    public bool IsAvailable => keys.Count > 0;

    public EmailLookupHash HashForStorage(string normalizedEmail)
    {
        EnsureAvailable();
        return Hash(currentVersion, normalizedEmail);
    }

    public IReadOnlyList<EmailLookupHash> HashForLookup(string normalizedEmail)
    {
        EnsureAvailable();
        return keys.Keys
            .OrderByDescending(version => version == currentVersion)
            .ThenByDescending(version => version)
            .Select(version => Hash(version, normalizedEmail))
            .ToArray();
    }

    private EmailLookupHash Hash(int version, string normalizedEmail)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalizedEmail);
        var address = Encoding.UTF8.GetBytes(normalizedEmail);
        var message = new byte[Domain.Length + address.Length];
        Domain.CopyTo(message, 0);
        address.CopyTo(message, Domain.Length);
        return new EmailLookupHash(version, HMACSHA256.HashData(keys[version], message));
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new EmailLookupUnavailableException("No email lookup key is configured.");
        }
    }
}
