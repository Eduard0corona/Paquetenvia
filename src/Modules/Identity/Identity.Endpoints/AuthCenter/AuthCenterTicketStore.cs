using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Single-instance server-side session store (<c>AuthCenter:SessionStore=Memory</c>): the browser cookie
/// only carries an opaque, protected key. The ticket (minimal identity, CSRF secret and the AuthCenter
/// refresh token) is protected with the platform Data Protection key ring and kept in
/// <see cref="IDistributedCache"/>. The default cache is in-memory; a lost entry fails closed as an
/// anonymous request. The default store is PostgreSQL (<see cref="PostgreSqlAuthCenterTicketStore"/>).
/// </summary>
internal sealed class AuthCenterTicketStore : ITicketStore
{
    internal const string ProtectorPurpose = "Paquetenvia.Identity.AuthCenter.SessionTicket.v1";
    private const string KeyPrefix = "paquetenvia:authcenter:session:";

    // 256 random bits, Base64URL without padding.
    private const int EncodedKeyLength = 43;
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly IOptions<AuthCenterOptions> _options;

    public AuthCenterTicketStore(
        IDistributedCache cache,
        IDataProtectionProvider dataProtection,
        IOptions<AuthCenterOptions> options)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _options = options;
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = NewKey();
        await RenewAsync(key, ticket);
        return key;
    }

    internal static string NewKey() => KeyPrefix + Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    internal static bool HasKeyShape(string? key) =>
        key is { Length: > 0 } value &&
        value.Length == KeyPrefix.Length + EncodedKeyLength &&
        value.StartsWith(KeyPrefix, StringComparison.Ordinal);

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(ticket);

        var expiration = ticket.Properties.ExpiresUtc ??
            DateTimeOffset.UtcNow.AddMinutes(_options.Value.SessionLifetimeMinutes);
        return _cache.SetAsync(
            key,
            _protector.Protect(TicketSerializer.Default.Serialize(ticket)),
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiration });
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (!HasKeyShape(key))
        {
            return null;
        }

        var protectedTicket = await _cache.GetAsync(key);
        if (protectedTicket is null)
        {
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(protectedTicket));
        }
        catch (CryptographicException)
        {
            await _cache.RemoveAsync(key);
            return null;
        }
    }

    public Task RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _cache.RemoveAsync(key);
    }
}
