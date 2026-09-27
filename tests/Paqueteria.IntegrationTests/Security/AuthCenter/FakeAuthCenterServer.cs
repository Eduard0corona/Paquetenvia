using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Paqueteria.IntegrationTests.Security.AuthCenter;

/// <summary>
/// In-process OIDC provider that mimics the AuthCenter contract: discovery, JWKS, token and
/// revocation endpoints. The RSA key is generated per instance; nothing is persisted. The browser
/// parts of /oauth/authorize and /oauth/logout are played by the test itself through
/// <see cref="Authorize"/> and <see cref="AssertEndSessionRequest"/>; back-channel logout tokens are
/// minted with <see cref="CreateLogoutToken"/>.
/// </summary>
internal sealed class FakeAuthCenterServer : HttpMessageHandler
{
    public const string Authority = "https://authcenter.test";
    public const string Issuer = "https://authcenter.test";
    public const string AcrSingleFactor = "urn:authcenter:acr:1fa";
    public const string AcrMfa = "urn:authcenter:acr:mfa";
    public const string AcrPhishingResistant = "urn:authcenter:acr:phr";

    /// <summary>AuthCenter SSO session id (`sid`), the same value its back-channel logout_token carries.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString();
    public const string ClientId = "paquetenvia-web-testing";
    public const string ClientSecret = "fake-authcenter-client-secret-0123456789abcdef";
    public const string PublicOrigin = "https://app.paquetenvia.test";
    public const string RedirectUri = PublicOrigin + "/signin-authcenter";
    public const string EndSessionEndpoint = Authority + "/oauth/logout";
    public const string PostLogoutRedirectUri = PublicOrigin + "/login";
    public const string BackchannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;
    private readonly ConcurrentDictionary<string, AuthorizationGrant> _codes = new(StringComparer.Ordinal);

    public FakeAuthCenterServer()
    {
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = "fake-authcenter-key-1" };
    }

    public ConcurrentQueue<string> RevokedTokens { get; } = new();

    public ConcurrentQueue<string> TokenRequestFailures { get; } = new();

    public TokenBehavior Behavior { get; set; } = new();

    public string LastRefreshToken { get; private set; } = string.Empty;

    public string LastAccessToken { get; private set; } = string.Empty;

    public string LastIdToken { get; private set; } = string.Empty;

    /// <summary><c>sid</c> of the last authorization: <see cref="SessionId"/> unless the behavior names another.</summary>
    public string LastSessionId { get; private set; } = string.Empty;

    /// <summary><c>acr_values</c> of the last authorization request; null when absent.</summary>
    public string? LastRequestedAcrValues { get; private set; }

    /// <summary>What discovery publishes as <c>end_session_endpoint</c>; null omits it.</summary>
    public string? PublishedEndSessionEndpoint { get; set; } = EndSessionEndpoint;

    /// <summary>
    /// Plays the IdP interactive step: validates the authorization request exactly like AuthCenter
    /// (client, exact redirect URI, scopes, S256 PKCE, state, nonce) and returns the callback URL.
    /// </summary>
    public AuthorizationResult Authorize(Uri authorizationRequest, string subject)
    {
        Assert.Equal(Authority + "/oauth/authorize", authorizationRequest.GetLeftPart(UriPartial.Path));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizationRequest.Query);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal(ClientId, query["client_id"].ToString());
        Assert.Equal(RedirectUri, query["redirect_uri"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.False(string.IsNullOrEmpty(query["state"].ToString()));
        Assert.False(string.IsNullOrEmpty(query["nonce"].ToString()));
        Assert.Equal(
            ["email", "offline_access", "openid", "profile"],
            query["scope"].ToString().Split(' ').Order(StringComparer.Ordinal));
        Assert.False(query.ContainsKey("client_secret"));
        var requestedAcrValues = query.TryGetValue("acr_values", out var acrValues) ? acrValues.ToString() : null;
        LastRequestedAcrValues = requestedAcrValues;

        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var sessionId = Behavior.SessionId ?? SessionId;
        LastSessionId = sessionId;
        _codes[code] = new AuthorizationGrant(
            subject,
            query["nonce"].ToString(),
            query["code_challenge"].ToString(),
            query["redirect_uri"].ToString(),
            sessionId,
            requestedAcrValues);
        return new AuthorizationResult(code, query["state"].ToString(), query["nonce"].ToString());
    }

    /// <summary>
    /// Checks an RP-initiated logout URL the way AuthCenter /oauth/logout does: an id_token_hint
    /// issued here for this client and the exact registered post_logout_redirect_uri.
    /// </summary>
    public static void AssertEndSessionRequest(string endSessionUrl, string expectedIdToken)
    {
        var uri = new Uri(endSessionUrl, UriKind.Absolute);
        Assert.Equal(EndSessionEndpoint, uri.GetLeftPart(UriPartial.Path));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(["id_token_hint", "post_logout_redirect_uri"], query.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expectedIdToken, query["id_token_hint"].ToString());
        Assert.Equal(PostLogoutRedirectUri, query["post_logout_redirect_uri"].ToString());
        Assert.Equal(ClientId, Assert.Single(new JsonWebToken(expectedIdToken).Audiences));
    }

    /// <summary>Authorization error response, with state and the RFC 9207 iss AuthCenter always adds.</summary>
    public static string CallbackErrorPath(string error, string state) =>
        $"/signin-authcenter?error={Uri.EscapeDataString(error)}" +
        $"&error_description={Uri.EscapeDataString("secret-detail")}" +
        $"&state={Uri.EscapeDataString(state)}&iss={Uri.EscapeDataString(Issuer)}";

    /// <summary>
    /// Mints a back-channel logout token like AuthCenter TokenService.GenerateLogoutToken: header
    /// typ logout+jwt, RS256 with the JWKS key, iss, aud, sub, sid, iat, exp = iat + 2 min, jti and the
    /// back-channel logout event, never a nonce. <paramref name="options"/> breaks one property at a time.
    /// </summary>
    public string CreateLogoutToken(string? subject, string? sessionId, LogoutTokenOptions? options = null)
    {
        options ??= new LogoutTokenOptions();
        var now = DateTime.UtcNow + options.IssuedAtOffset;
        var claims = new Dictionary<string, object>();
        if (options.IncludeTokenId)
        {
            claims["jti"] = options.TokenId ?? Guid.NewGuid().ToString();
        }

        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (sessionId is not null)
        {
            claims["sid"] = sessionId;
        }

        if (options.IncludeEvents)
        {
            claims["events"] = new Dictionary<string, object> { [options.EventName] = new Dictionary<string, object>() };
        }

        if (options.Nonce is not null)
        {
            claims["nonce"] = options.Nonce;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer ?? Issuer,
            Audience = options.Audience ?? ClientId,
            IssuedAt = now,
            Expires = now + options.Lifetime,
            TokenType = options.TokenType,
            Claims = claims,
            SigningCredentials = options.SignWithHs256
                ? new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ClientSecret + ClientSecret)),
                    SecurityAlgorithms.HmacSha256)
                : new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    public static string CallbackPath(string code, string state) => CallbackPath(code, state, Issuer);

    /// <summary>Authorization response with an explicit RFC 9207 <c>iss</c>; <c>null</c> omits it.</summary>
    public static string CallbackPath(string code, string state, string? issuer) =>
        $"/signin-authcenter?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}" +
        (issuer is null ? string.Empty : $"&iss={Uri.EscapeDataString(issuer)}");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/.well-known/openid-configuration" => Json(Discovery()),
            "/.well-known/jwks.json" => Json(Jwks()),
            "/oauth/token" when request.Method == HttpMethod.Post => await TokenAsync(request, cancellationToken),
            "/oauth/revoke" when request.Method == HttpMethod.Post => await RevokeAsync(request, cancellationToken),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
        }

        base.Dispose(disposing);
    }

    private Dictionary<string, object> Discovery()
    {
        var discovery = CoreDiscovery();
        if (PublishedEndSessionEndpoint is not null)
        {
            discovery["end_session_endpoint"] = PublishedEndSessionEndpoint;
        }

        return discovery;
    }

    private static Dictionary<string, object> CoreDiscovery() => new()
    {
        ["issuer"] = Issuer,
        ["authorization_endpoint"] = Authority + "/oauth/authorize",
        ["token_endpoint"] = Authority + "/oauth/token",
        ["revocation_endpoint"] = Authority + "/oauth/revoke",
        ["userinfo_endpoint"] = Authority + "/oauth/userinfo",
        ["jwks_uri"] = Authority + "/.well-known/jwks.json",
        ["scopes_supported"] = new[] { "openid", "profile", "email", "offline_access" },
        ["response_types_supported"] = new[] { "code" },
        ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
        ["subject_types_supported"] = new[] { "public" },
        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post" },
        ["code_challenge_methods_supported"] = new[] { "S256" },
        ["authorization_response_iss_parameter_supported"] = true,
        // end_session_endpoint is added by Discovery() from PublishedEndSessionEndpoint.
        ["acr_values_supported"] = new[] { AcrSingleFactor, AcrMfa, AcrPhishingResistant },
        ["backchannel_logout_supported"] = true,
        ["backchannel_logout_session_supported"] = true,
        ["frontchannel_logout_supported"] = false,
    };

    private object Jwks()
    {
        var parameters = _rsa.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new Dictionary<string, string>
                {
                    ["kty"] = "RSA",
                    ["use"] = "sig",
                    ["alg"] = "RS256",
                    ["kid"] = _signingKey.KeyId!,
                    ["n"] = Base64UrlEncoder.Encode(parameters.Modulus!),
                    ["e"] = Base64UrlEncoder.Encode(parameters.Exponent!),
                },
            },
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(request, cancellationToken);
        if (!IsClientAuthenticated(request, form))
        {
            return Fail("invalid_client");
        }

        if (form.GetValueOrDefault("grant_type") != "authorization_code")
        {
            return Fail("unsupported_grant_type");
        }

        var code = form.GetValueOrDefault("code") ?? string.Empty;
        if (!_codes.TryRemove(code, out var grant))
        {
            return Fail("invalid_grant:code");
        }

        if (!string.Equals(form.GetValueOrDefault("redirect_uri"), grant.RedirectUri, StringComparison.Ordinal))
        {
            return Fail("invalid_grant:redirect_uri");
        }

        var verifier = form.GetValueOrDefault("code_verifier") ?? string.Empty;
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var expectedChallenge = Behavior.OverrideExpectedCodeChallenge ?? grant.CodeChallenge;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(challenge),
                Encoding.ASCII.GetBytes(expectedChallenge)))
        {
            return Fail("invalid_grant:pkce");
        }

        // The response is built from this request's own tokens. The Last* properties are only a
        // convenience for sequential tests: concurrent redemptions overwrite them, so reading them back
        // into the response would hand one browser another browser's ID token (and nonce).
        var idToken = CreateIdToken(grant);
        var accessToken = "opaque-access-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var refreshToken = "opaque-refresh-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        LastIdToken = idToken;
        LastAccessToken = accessToken;
        LastRefreshToken = refreshToken;
        return Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = 300,
            ["refresh_token"] = refreshToken,
            ["id_token"] = idToken,
            ["scope"] = "openid profile email offline_access",
        });
    }

    private async Task<HttpResponseMessage> RevokeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = await ReadFormAsync(request, cancellationToken);
        if (!IsClientAuthenticated(request, form) || form.ContainsKey("client_secret"))
        {
            return Fail("invalid_client");
        }

        RevokedTokens.Enqueue(form.GetValueOrDefault("token") ?? string.Empty);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private string CreateIdToken(AuthorizationGrant grant)
    {
        var now = DateTime.UtcNow;
        var behavior = Behavior;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = grant.Subject,
            ["sid"] = grant.SessionId,
            ["azp"] = ClientId,
            ["nonce"] = behavior.OverrideNonce ?? grant.Nonce,
            ["auth_time"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["name"] = "Usuario Sintético",
            ["email"] = behavior.Email ?? "synthetic.user@paquetenvia.test",
            ["roles"] = "AUTHCENTER_SUPERADMIN",
            ["permissions"] = "EVERYTHING",
        };

        // AUTH-EMAIL-VERIFIED-REQUIRED: the real AuthCenter emits a JSON boolean; OmitEmailVerified and
        // EmailVerified play an ID token without the claim or with any other value.
        if (!behavior.OmitEmailVerified)
        {
            claims["email_verified"] = behavior.EmailVerified ?? true;
        }

        // AuthCenter honors acr_values by asking (or enrolling) the second factor: the ID token then
        // carries acr mfa and "mfa" in amr. IgnoreAcrValues plays a server that did not.
        var steppedUp = !behavior.IgnoreAcrValues &&
            grant.AcrValues?.Split(' ').Contains(AcrMfa, StringComparer.Ordinal) == true;
        var amr = behavior.Amr ?? (steppedUp ? ["pwd", "otp", "mfa"] : null);
        if (amr is not null)
        {
            claims["amr"] = amr;
        }

        claims["acr"] = behavior.Acr ??
            (amr?.Contains("mfa", StringComparer.Ordinal) == true ? AcrMfa : AcrSingleFactor);

        var expires = behavior.Expired ? now.AddMinutes(-10) : now.AddMinutes(5);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = behavior.OverrideIssuer ?? Issuer,
            Audience = behavior.OverrideAudience ?? ClientId,
            IssuedAt = behavior.Expired ? now.AddMinutes(-20) : now,
            NotBefore = behavior.Expired ? now.AddMinutes(-20) : now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = behavior.SignWithHs256
                ? new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ClientSecret + ClientSecret)),
                    SecurityAlgorithms.HmacSha256)
                : behavior.SignWithForeignKey
                    ? new SigningCredentials(
                        new RsaSecurityKey(RSA.Create(2048)) { KeyId = _signingKey.KeyId },
                        SecurityAlgorithms.RsaSha256)
                    : new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private static bool IsClientAuthenticated(HttpRequestMessage request, Dictionary<string, string> form)
    {
        if (request.Headers.Authorization is not { Scheme: "Basic", Parameter: { } parameter })
        {
            // client_secret_post, as advertised by AuthCenter discovery.
            return form.GetValueOrDefault("client_id") == ClientId &&
                form.GetValueOrDefault("client_secret") == ClientSecret;
        }

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 &&
            Uri.UnescapeDataString(decoded[..separator]) == ClientId &&
            Uri.UnescapeDataString(decoded[(separator + 1)..]) == ClientSecret;
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(body)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
    }

    private HttpResponseMessage Fail(string reason)
    {
        TokenRequestFailures.Enqueue(reason);
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = reason.Split(':')[0] }),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

    private sealed record AuthorizationGrant(
        string Subject,
        string Nonce,
        string CodeChallenge,
        string RedirectUri,
        string SessionId,
        string? AcrValues);
}

internal sealed record AuthorizationResult(string Code, string State, string Nonce);

public sealed record TokenBehavior
{
    public string? OverrideIssuer { get; init; }
    public string? OverrideAudience { get; init; }
    public string? OverrideNonce { get; init; }
    public string? OverrideExpectedCodeChallenge { get; init; }
    public bool Expired { get; init; }
    public bool SignWithHs256 { get; init; }
    public bool SignWithForeignKey { get; init; }
    public string[]? Amr { get; init; }
    public string? Acr { get; init; }
    public bool IgnoreAcrValues { get; init; }
    public string? SessionId { get; init; }
    public object? EmailVerified { get; init; }
    public bool OmitEmailVerified { get; init; }

    /// <summary>REG-002: the email claim of the ID token; the synthetic default otherwise.</summary>
    public string? Email { get; init; }
}

/// <summary>One deviation at a time from a valid AuthCenter logout token.</summary>
public sealed record LogoutTokenOptions
{
    public string? Issuer { get; init; }
    public string? Audience { get; init; }
    public string TokenType { get; init; } = "logout+jwt";
    public bool SignWithHs256 { get; init; }
    public bool IncludeEvents { get; init; } = true;
    public string EventName { get; init; } = FakeAuthCenterServer.BackchannelLogoutEvent;
    public string? Nonce { get; init; }
    public string? TokenId { get; init; }
    public bool IncludeTokenId { get; init; } = true;
    public TimeSpan IssuedAtOffset { get; init; }
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(2);
}
