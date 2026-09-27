using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Identity.Application.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// PostgreSQL BFF session store (BFF-SESSION-STORE-POSTGRESQL, BFF-SESSION-TABLE-SHAPE). The browser
/// cookie still carries only the protected opaque key; the database receives the SHA-256 of that key and
/// the ticket protected with the platform Data Protection key ring (the same purpose string as the
/// single-instance store), so sessions survive restarts and are shared by every API replica. A revoked,
/// expired or unknown key resolves to no ticket and the request is anonymous.
/// </summary>
internal sealed partial class PostgreSqlAuthCenterTicketStore : ITicketStore
{
    private readonly IBffSessionStore _sessions;
    private readonly IDataProtector _protector;
    private readonly IOptions<AuthCenterOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PostgreSqlAuthCenterTicketStore> _logger;

    public PostgreSqlAuthCenterTicketStore(
        IBffSessionStore sessions,
        IDataProtectionProvider dataProtection,
        IOptions<AuthCenterOptions> options,
        TimeProvider clock,
        ILogger<PostgreSqlAuthCenterTicketStore> logger)
    {
        _sessions = sessions;
        _protector = dataProtection.CreateProtector(AuthCenterTicketStore.ProtectorPurpose);
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var external = ticket.Principal.Identities.SingleOrDefault(identity =>
            identity.AuthenticationType == AuthCenterDefaults.ExternalAuthenticationType);
        var subject = SingleValue(external, AuthCenterDefaults.SubjectClaim);
        if (subject is not { Length: > 0 and <= IBffSessionStore.MaximumIdentifierLength })
        {
            // TicketReceived always builds the minimal principal; anything else is never persisted.
            throw new InvalidOperationException("An AuthCenter BFF ticket without a single subject cannot be stored.");
        }

        var sessionId = SingleValue(external, AuthCenterDefaults.SessionIdClaim);
        if (sessionId is { Length: > IBffSessionStore.MaximumIdentifierLength })
        {
            sessionId = null;
        }

        var key = AuthCenterTicketStore.NewKey();
        await _sessions.CreateAsync(
            new BffSessionRecord(
                Hash(key),
                subject,
                string.IsNullOrEmpty(sessionId) ? null : sessionId,
                _protector.Protect(TicketSerializer.Default.Serialize(ticket)),
                ticket.Properties.ExpiresUtc ??
                    _clock.GetUtcNow().AddMinutes(_options.Value.SessionLifetimeMinutes)),
            CancellationToken.None);
        return key;
    }

    /// <summary>
    /// Sessions have a fixed lifetime and every sign-in issues a new key (AUTH-001-MFA-STEP-UP), so the
    /// cookie handler never renews a stored ticket in this configuration. Should it ever try, the session
    /// fails closed: the key is revoked and the next request is anonymous.
    /// </summary>
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(ticket);
        LogRenewalRefused(_logger);
        if (AuthCenterTicketStore.HasKeyShape(key))
        {
            await _sessions.RevokeAsync(Hash(key), CancellationToken.None);
        }
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (!AuthCenterTicketStore.HasKeyShape(key))
        {
            return null;
        }

        var hash = Hash(key);
        var protectedTicket = await _sessions.ResolveAsync(hash, CancellationToken.None);
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
            // A ticket this key ring cannot read is useless everywhere: end it instead of retrying.
            await _sessions.RevokeAsync(hash, CancellationToken.None);
            return null;
        }
    }

    public async Task RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (AuthCenterTicketStore.HasKeyShape(key))
        {
            await _sessions.RevokeAsync(Hash(key), CancellationToken.None);
        }
    }

    /// <summary>SHA-256 of the exact UTF-8 bytes of the opaque key; the key itself never leaves the API.</summary>
    internal static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static string? SingleValue(ClaimsIdentity? identity, string type)
    {
        if (identity is null)
        {
            return null;
        }

        var values = identity.FindAll(type).Select(claim => claim.Value).ToArray();
        return values.Length == 1 ? values[0] : null;
    }

    [LoggerMessage(EventId = 4105, Level = LogLevel.Warning,
        Message = "AuthCenter BFF session renewal is not supported; the session was revoked.")]
    private static partial void LogRenewalRefused(ILogger logger);
}

/// <summary>
/// Back-channel logout effect on the PostgreSQL store (AUTH-001-BACKCHANNEL-LOGOUT,
/// BFF-LOGOUT-JTI-PERSISTENCE): the logout-token jti is registered in <c>identity.bff_logout_jtis</c> and,
/// in the same transaction and only the first time any replica sees it, the sessions of the <c>sid</c>
/// (or those of the <c>sub</c> created at or before the logout) are revoked in the shared table. Every
/// replica therefore refuses both the ended sessions and a replayed token.
/// </summary>
internal sealed class PostgreSqlAuthCenterSessionTerminationStore(IBffSessionStore sessions)
    : IAuthCenterSessionTerminationStore
{
    public Task<bool> TryRegisterLogoutTokenAsync(
        string tokenId,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken) =>
        sessions.ApplyLogoutTokenAsync(
            new BffLogoutTokenEffect(Hash(tokenId), retainUntil, null, null, retainUntil),
            cancellationToken);

    public Task EndSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        sessions.RevokeByAuthCenterSessionAsync(sessionId, cancellationToken);

    public Task EndSubjectSessionsAsync(string subject, DateTimeOffset endedAt, CancellationToken cancellationToken) =>
        sessions.RevokeBySubjectAsync(subject, endedAt, cancellationToken);

    public Task<bool> TryEndAsync(
        string tokenId,
        DateTimeOffset retainUntil,
        string? sessionId,
        string? subject,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken) =>
        sessions.ApplyLogoutTokenAsync(
            new BffLogoutTokenEffect(Hash(tokenId), retainUntil, sessionId, sessionId is null ? subject : null, endedAt),
            cancellationToken);

    /// <summary>
    /// A revoked session never resolves a ticket, so a principal that reaches validation belongs to a
    /// session that was live when this request read it.
    /// </summary>
    public Task<bool> IsEndedAsync(
        string? sessionId,
        string subject,
        DateTimeOffset? signedInAt,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>The jti is stored only as the SHA-256 of its exact UTF-8 bytes.</summary>
    internal static byte[] Hash(string tokenId) => SHA256.HashData(Encoding.UTF8.GetBytes(tokenId));
}
