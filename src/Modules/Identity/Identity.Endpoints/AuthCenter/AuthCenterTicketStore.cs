using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Server-side session store: the browser cookie only carries an opaque, protected key. The ticket
/// (minimal identity, CSRF secret and the AuthCenter refresh token) is protected with the platform
/// Data Protection key ring and kept in <see cref="IDistributedCache"/>. The default cache is
/// in-memory (single instance); a lost entry fails closed as an anonymous request.
/// </summary>
internal sealed class AuthCenterTicketStore : ITicketStore
{
    private const string KeyPrefix = "paquetenvia:authcenter:session:";
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly IOptions<AuthCenterOptions> _options;

    public AuthCenterTicketStore(
        IDistributedCache cache,
        IDataProtectionProvider dataProtection,
        IOptions<AuthCenterOptions> options)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("Paquetenvia.Identity.AuthCenter.SessionTicket.v1");
        _options = options;
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = KeyPrefix + Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        await RenewAsync(key, ticket);
        return key;
    }

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
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
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
