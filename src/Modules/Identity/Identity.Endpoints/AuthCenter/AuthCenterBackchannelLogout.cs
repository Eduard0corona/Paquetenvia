using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// OpenID Connect Back-Channel Logout 1.0 receiver (AUTH-001-BACKCHANNEL-LOGOUT). Mirrors the
/// AuthCenter.Client reference: a logout token is accepted only when it is an RS256 JWT of type
/// <c>logout+jwt</c> signed with a discovery key, with the exact issuer, the client as audience, a
/// valid lifetime (30 s skew), a recent <c>iat</c>, the back-channel logout event, no <c>nonce</c>,
/// an unseen <c>jti</c> and a <c>sid</c> or <c>sub</c>. Never logs tokens, subjects or session ids.
/// </summary>
internal sealed partial class AuthCenterBackchannelLogout(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    IOptions<AuthCenterOptions> options,
    IAuthCenterSessionTerminationStore store,
    ILogger<AuthCenterBackchannelLogout> logger)
{
    internal const int MaximumTokenLength = 16_384;
    private const int MaximumIdentifierLength = 256;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumTokenAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReplayRetention = TimeSpan.FromMinutes(5);

    /// <summary>Validates <paramref name="logoutToken"/> and ends the sessions it names; false when not acceptable.</summary>
    public async Task<bool> ProcessAsync(string? logoutToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logoutToken) || logoutToken.Length > MaximumTokenLength)
        {
            return Reject("malformed");
        }

        var oidc = oidcOptions.Get(AuthCenterDefaults.OpenIdConnectScheme);
        ICollection<SecurityKey> signingKeys;
        try
        {
            signingKeys = (await oidc.ConfigurationManager!.GetConfigurationAsync(cancellationToken)).SigningKeys;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            // AuthCenter retries any non-2xx delivery, so an unavailable discovery is recoverable.
            return Reject("discovery_unavailable");
        }

        var now = DateTimeOffset.UtcNow;
        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(
            logoutToken,
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Value.Issuer,
                ValidateAudience = true,
                ValidAudience = options.Value.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = ClockSkew,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = signingKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                // A logout token must never be confused with an ID or access token.
                ValidTypes = [AuthCenterDefaults.LogoutTokenType],
            });
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
        {
            if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                // AuthCenter rotated its keys: the next delivery attempt reads fresh discovery.
                oidc.ConfigurationManager!.RequestRefresh();
            }

            return Reject(result.Exception?.GetType().Name ?? "invalid_token");
        }

        if (!string.Equals(token.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal) ||
            !string.Equals(token.Typ, AuthCenterDefaults.LogoutTokenType, StringComparison.Ordinal) ||
            !TryReadClaims(token, now, out var claims))
        {
            return Reject("invalid_claims");
        }

        if (!await store.TryEndAsync(
                claims.TokenId,
                claims.Expires + ReplayRetention,
                claims.SessionId,
                claims.SessionId is null ? claims.Subject : null,
                now,
                cancellationToken))
        {
            return Reject("replayed");
        }

        LogAccepted(logger, claims.SessionId is not null ? "sid" : "sub");
        return true;
    }

    private static bool TryReadClaims(JsonWebToken token, DateTimeOffset now, out LogoutTokenClaims claims)
    {
        claims = default;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.EncodedPayload));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return false;
        }

        using (document)
        {
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object ||
                payload.TryGetProperty("nonce", out _) ||
                !payload.TryGetProperty("events", out var events) ||
                events.ValueKind != JsonValueKind.Object ||
                !events.TryGetProperty(AuthCenterDefaults.BackchannelLogoutEvent, out var logoutEvent) ||
                logoutEvent.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("iat", out var issuedAtElement) ||
                !issuedAtElement.TryGetInt64(out var issuedAtSeconds) ||
                !payload.TryGetProperty("exp", out var expiresElement) ||
                !expiresElement.TryGetInt64(out var expiresSeconds) ||
                !TryReadIdentifier(payload, "jti", required: true, out var tokenId) ||
                !TryReadIdentifier(payload, AuthCenterDefaults.SessionIdClaim, required: false, out var sessionId) ||
                !TryReadIdentifier(payload, AuthCenterDefaults.SubjectClaim, required: false, out var subject) ||
                (sessionId is null && subject is null))
            {
                return false;
            }

            DateTimeOffset issuedAt;
            DateTimeOffset expires;
            try
            {
                issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds);
                expires = DateTimeOffset.FromUnixTimeSeconds(expiresSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            if (issuedAt > now + ClockSkew || now - issuedAt > MaximumTokenAge)
            {
                return false;
            }

            claims = new LogoutTokenClaims(tokenId!, sessionId, subject, expires);
            return true;
        }
    }

    private static bool TryReadIdentifier(JsonElement payload, string name, bool required, out string? value)
    {
        value = null;
        if (!payload.TryGetProperty(name, out var element))
        {
            return !required;
        }

        if (element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { Length: > 0 and <= MaximumIdentifierLength } text ||
            text.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private bool Reject(string reason)
    {
        LogRejected(logger, reason);
        return false;
    }

    private readonly record struct LogoutTokenClaims(string TokenId, string? SessionId, string? Subject, DateTimeOffset Expires);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning, Message = "AuthCenter back-channel logout token rejected ({FailureKind}).")]
    private static partial void LogRejected(ILogger logger, string failureKind);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Information, Message = "AuthCenter back-channel logout accepted ({Scope}).")]
    private static partial void LogAccepted(ILogger logger, string scope);
}
