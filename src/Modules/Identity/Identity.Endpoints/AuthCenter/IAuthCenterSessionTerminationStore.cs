using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Records AuthCenter sessions ended through OpenID Connect Back-Channel Logout so every API
/// instance rejects the matching BFF sessions on their next request
/// (AUTH-001-BACKCHANNEL-LOGOUT). The default implementation keeps markers in
/// <see cref="IDistributedCache"/>; the PostgreSQL BFF session table (BFF-SESSION-STORE-POSTGRESQL,
/// BFF-SESSION-TABLE-SHAPE) can implement it by deleting the rows whose <c>authcenter_sid</c>
/// matches instead of keeping a marker.
/// </summary>
internal interface IAuthCenterSessionTerminationStore
{
    /// <summary>
    /// Accepts a logout token identifier once. Returns false when it was already seen; the record is
    /// kept until <paramref name="retainUntil"/> (token expiry plus a margin).
    /// </summary>
    Task<bool> TryRegisterLogoutTokenAsync(string tokenId, DateTimeOffset retainUntil, CancellationToken cancellationToken);

    /// <summary>Ends every BFF session created from the AuthCenter session <paramref name="sessionId"/>.</summary>
    Task EndSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Ends every BFF session of <paramref name="subject"/> issued at or before <paramref name="endedAt"/>.</summary>
    Task EndSubjectSessionsAsync(string subject, DateTimeOffset endedAt, CancellationToken cancellationToken);

    /// <summary>
    /// True when AuthCenter ended the session this BFF ticket was created from. A ticket without a
    /// sign-in moment cannot prove it is newer than a subject-wide logout and counts as ended.
    /// </summary>
    Task<bool> IsEndedAsync(string? sessionId, string subject, DateTimeOffset? signedInAt, CancellationToken cancellationToken);

    /// <summary>
    /// Accepts a logout token once and ends the sessions it names (the <paramref name="sessionId"/>, or
    /// otherwise those of <paramref name="subject"/> issued at or before <paramref name="endedAt"/>).
    /// Returns false for a replayed token id, which ends nothing. Stores that can do so apply both steps
    /// atomically (BFF-LOGOUT-JTI-PERSISTENCE).
    /// </summary>
    async Task<bool> TryEndAsync(
        string tokenId,
        DateTimeOffset retainUntil,
        string? sessionId,
        string? subject,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken)
    {
        if (!await TryRegisterLogoutTokenAsync(tokenId, retainUntil, cancellationToken))
        {
            return false;
        }

        if (sessionId is not null)
        {
            await EndSessionAsync(sessionId, cancellationToken);
        }
        else if (subject is not null)
        {
            await EndSubjectSessionsAsync(subject, endedAt, cancellationToken);
        }

        return true;
    }
}

internal sealed class DistributedCacheAuthCenterSessionTerminationStore(
    IDistributedCache cache,
    IOptions<AuthCenterOptions> options) : IAuthCenterSessionTerminationStore
{
    private const string ReplayPrefix = "paquetenvia:authcenter:logout:jti:";
    private const string SessionPrefix = "paquetenvia:authcenter:logout:sid:";
    private const string SubjectPrefix = "paquetenvia:authcenter:logout:sub:";

    public async Task<bool> TryRegisterLogoutTokenAsync(
        string tokenId,
        DateTimeOffset retainUntil,
        CancellationToken cancellationToken)
    {
        var key = ReplayPrefix + Digest(tokenId);
        if (await cache.GetAsync(key, cancellationToken) is not null)
        {
            return false;
        }

        await cache.SetAsync(key, [1], new DistributedCacheEntryOptions { AbsoluteExpiration = retainUntil }, cancellationToken);
        return true;
    }

    public Task EndSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        cache.SetAsync(SessionPrefix + Digest(sessionId), [1], Marker(), cancellationToken);

    public async Task EndSubjectSessionsAsync(string subject, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        var key = SubjectPrefix + Digest(subject);
        // Keep the latest moment when several sub-only tokens arrive.
        var previous = ParseMoment(await cache.GetStringAsync(key, cancellationToken));
        var moment = previous is { } earlier && earlier > endedAt ? earlier : endedAt;
        await cache.SetStringAsync(
            key,
            moment.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            Marker(),
            cancellationToken);
    }

    public async Task<bool> IsEndedAsync(
        string? sessionId,
        string subject,
        DateTimeOffset? signedInAt,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(sessionId) &&
            await cache.GetAsync(SessionPrefix + Digest(sessionId), cancellationToken) is not null)
        {
            return true;
        }

        var endedAt = ParseMoment(await cache.GetStringAsync(SubjectPrefix + Digest(subject), cancellationToken));
        return endedAt is { } moment && (signedInAt is not { } signedIn || signedIn <= moment);
    }

    private DistributedCacheEntryOptions Marker() => new()
    {
        // A BFF session never outlives SessionLifetimeMinutes, so neither does its marker.
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(options.Value.SessionLifetimeMinutes),
    };

    private static DateTimeOffset? ParseMoment(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

    // Keys never carry raw identifiers: bounded length and no separator injection.
    private static string Digest(string value) =>
        Base64UrlTextEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
