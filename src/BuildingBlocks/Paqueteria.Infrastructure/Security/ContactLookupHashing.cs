using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Paqueteria.Application.Security;

namespace Paqueteria.Infrastructure.Security;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.opt_out.hash</c>) and NTF-RECIPIENT-NOTICE-CAPS-2026-10-23
/// L4 choice 6: the contact lookup key ring. Each key is Base64 of 32 to 128 random bytes, read from the Key Vault secret
/// <c>paquetenvia-contact-lookup-key-1</c> in Azure (<c>ContactLookup:Keys:1</c>, mapped by AI-08 NTF-008) and from
/// user-secrets locally; never committed, logged or stored in the database. Older versions stay configured while hashes
/// or links made with them must still match.
/// </summary>
public sealed class ContactLookupOptions
{
    public const string SectionName = "ContactLookup";
    public const int MaximumKeys = 8;
    public const int MinimumKeyBytes = 32;
    public const int MaximumKeyBytes = 128;
    public const int MaximumKeyVersion = 32767;

    /// <summary>The version new opt-out hashes and new unsubscribe tokens use.</summary>
    public int CurrentKeyVersion { get; set; } = 1;

    /// <summary>Key version to Base64 key.</summary>
    public Dictionary<int, string> Keys { get; set; } = [];
}

public static class ContactLookupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the contact lookup hasher and the unsubscribe token signer. Configured keys must be valid in every
    /// environment; whether a key is required is decided by the feature that uses it (recipient notices refuse to start
    /// enabled without one).
    /// </summary>
    public static IServiceCollection AddContactLookup(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ContactLookupOptions>()
            .Bind(configuration.GetSection(ContactLookupOptions.SectionName))
            .Validate(
                ContactLookupKeyRing.IsValid,
                "ContactLookup must hold 1 to 8 keys with versions 1 to 32767, each Base64 of 32 to 128 bytes, and CurrentKeyVersion must be one of them.")
            .ValidateOnStart();
        services.TryAddSingleton<ContactLookupKeyRing>();
        services.TryAddSingleton<IRecipientContactLookup, HmacRecipientContactLookup>();
        services.TryAddSingleton<IRecipientNoticeUnsubscribeTokens, HmacRecipientNoticeUnsubscribeTokens>();
        return services;
    }
}

/// <summary>The decoded keys of <see cref="ContactLookupOptions"/>; empty when none is configured.</summary>
public sealed class ContactLookupKeyRing
{
    private readonly IReadOnlyDictionary<int, byte[]> keys;

    public ContactLookupKeyRing(IOptions<ContactLookupOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value.Keys.Count > 0 && TryDecode(options.Value, out var decoded))
        {
            keys = decoded;
            CurrentKeyVersion = options.Value.CurrentKeyVersion;
        }
        else
        {
            keys = new Dictionary<int, byte[]>();
        }
    }

    public bool IsAvailable => keys.Count > 0;

    public int CurrentKeyVersion { get; }

    /// <summary>Current version first, then the others newest first.</summary>
    public IEnumerable<int> Versions => keys.Keys
        .OrderByDescending(version => version == CurrentKeyVersion)
        .ThenByDescending(version => version);

    public bool TryGet(int version, out byte[] key)
    {
        if (keys.TryGetValue(version, out var value))
        {
            key = value;
            return true;
        }

        key = [];
        return false;
    }

    public static bool IsValid(ContactLookupOptions options) => options.Keys.Count == 0 || TryDecode(options, out _);

    private static bool TryDecode(ContactLookupOptions options, out IReadOnlyDictionary<int, byte[]> decoded)
    {
        decoded = new Dictionary<int, byte[]>();
        if (options.Keys.Count is < 1 or > ContactLookupOptions.MaximumKeys ||
            !options.Keys.ContainsKey(options.CurrentKeyVersion))
        {
            return false;
        }

        var result = new Dictionary<int, byte[]>();
        foreach (var (version, value) in options.Keys)
        {
            if (version is < 1 or > ContactLookupOptions.MaximumKeyVersion || string.IsNullOrWhiteSpace(value))
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

            if (key.Length is < ContactLookupOptions.MinimumKeyBytes or > ContactLookupOptions.MaximumKeyBytes)
            {
                return false;
            }

            result[version] = key;
        }

        decoded = result;
        return true;
    }
}

/// <summary>HMAC-SHA256 over the UTF-8 bytes of <c>paquetenvia-contact-v1|{channel}|{value}</c>.</summary>
public sealed class HmacRecipientContactLookup(ContactLookupKeyRing keyRing) : IRecipientContactLookup
{
    public const string Prefix = "paquetenvia-contact-v1";

    public bool IsAvailable => keyRing.IsAvailable;

    public IReadOnlyList<ContactLookupHash> HashForLookup(RecipientContactChannel channel, string contact)
    {
        if (!keyRing.IsAvailable)
        {
            throw new ContactLookupUnavailableException("No contact lookup key is configured.");
        }

        if (!RecipientContactValue.TryNormalize(channel, contact, out var normalized))
        {
            throw new ArgumentException("The contact cannot be normalized.", nameof(contact));
        }

        var message = Encoding.UTF8.GetBytes($"{Prefix}|{RecipientContactValue.WireName(channel)}|{normalized}");
        try
        {
            return keyRing.Versions
                .Select(version => keyRing.TryGet(version, out var key)
                    ? new ContactLookupHash(version, HMACSHA256.HashData(key, message))
                    : throw new ContactLookupUnavailableException("The contact lookup key ring changed."))
                .ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }
}

/// <summary>
/// The unsubscribe token: Base64URL without padding of the key version (big-endian unsigned 16 bits), the owner
/// organization and the notification (16 bytes each, big-endian RFC 4122 order) and
/// HMAC-SHA256(key, UTF-8 <c>paquetenvia-unsubscribe-v1|{key_version}|{owner_org_id}|{notification_id}</c>, ids in
/// lowercase D format): 66 bytes, 88 characters.
/// </summary>
public sealed class HmacRecipientNoticeUnsubscribeTokens(ContactLookupKeyRing keyRing) : IRecipientNoticeUnsubscribeTokens
{
    public const string Prefix = "paquetenvia-unsubscribe-v1";
    public const int TokenBytes = 2 + 16 + 16 + 32;
    public const int TokenLength = 88;

    public bool IsAvailable => keyRing.IsAvailable;

    public string Issue(Guid ownerOrganizationId, Guid notificationId)
    {
        if (ownerOrganizationId == Guid.Empty || notificationId == Guid.Empty)
        {
            throw new ArgumentException("The owner organization and the notification are required.");
        }

        if (!keyRing.IsAvailable || !keyRing.TryGet(keyRing.CurrentKeyVersion, out var key))
        {
            throw new ContactLookupUnavailableException("No contact lookup key is configured.");
        }

        var token = new byte[TokenBytes];
        BinaryPrimitives.WriteUInt16BigEndian(token, (ushort)keyRing.CurrentKeyVersion);
        _ = ownerOrganizationId.TryWriteBytes(token.AsSpan(2, 16), bigEndian: true, out _);
        _ = notificationId.TryWriteBytes(token.AsSpan(18, 16), bigEndian: true, out _);
        Mac(key, keyRing.CurrentKeyVersion, ownerOrganizationId, notificationId).CopyTo(token.AsSpan(34));
        return Base64UrlEncode(token);
    }

    public bool TryVerify(string? token, out Guid ownerOrganizationId, out Guid notificationId)
    {
        ownerOrganizationId = Guid.Empty;
        notificationId = Guid.Empty;
        if (token is null || token.Length != TokenLength || !TryBase64UrlDecode(token, out var bytes) ||
            bytes.Length != TokenBytes)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        var owner = new Guid(bytes.AsSpan(2, 16), bigEndian: true);
        var notification = new Guid(bytes.AsSpan(18, 16), bigEndian: true);
        if (owner == Guid.Empty || notification == Guid.Empty || !keyRing.TryGet(version, out var key))
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Mac(key, version, owner, notification), bytes.AsSpan(34)))
        {
            return false;
        }

        ownerOrganizationId = owner;
        notificationId = notification;
        return true;
    }

    private static byte[] Mac(byte[] key, int version, Guid owner, Guid notification) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}|{version}|{owner:D}|{notification:D}")));

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
