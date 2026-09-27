using Microsoft.Extensions.Hosting;

namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Where BFF sessions live (BFF-SESSION-STORE-POSTGRESQL). <see cref="PostgreSql"/> is the default and the
/// only multi-instance store; <see cref="Memory"/> keeps the single-instance in-memory store and serves as
/// the documented rollback switch.
/// </summary>
public enum AuthCenterSessionStoreKind
{
    PostgreSql,
    Memory,
}

/// <summary>
/// AuthCenter BFF settings. <see cref="ClientSecret"/> must come from the environment secret store
/// (Key Vault reference / user-secrets); it is never committed to appsettings files.
/// </summary>
public sealed class AuthCenterOptions
{
    /// <summary>Public HTTPS base URL of AuthCenter; discovery is read from /.well-known/openid-configuration.</summary>
    public string? Authority { get; set; }

    /// <summary>Exact <c>iss</c> value (AuthCenter <c>Jwt:Issuer</c>); compared ordinally.</summary>
    public string? Issuer { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Browser-facing origin of the web app (scheme://host[:port]); the redirect URI is derived from it.</summary>
    public string? PublicOrigin { get; set; }

    public int SessionLifetimeMinutes { get; set; } = 480;

    /// <summary><c>PostgreSql</c> (default) needs <c>ConnectionStrings:Paqueteria</c>; <c>Memory</c> is single-instance.</summary>
    public AuthCenterSessionStoreKind SessionStore { get; set; } = AuthCenterSessionStoreKind.PostgreSql;

    internal Uri AuthorityUri => new(Authority!.TrimEnd('/'), UriKind.Absolute);

    internal string NormalizedPublicOrigin => new Uri(PublicOrigin!, UriKind.Absolute).GetLeftPart(UriPartial.Authority);

    internal string RedirectUri => NormalizedPublicOrigin + AuthCenterDefaults.CallbackPath;

    /// <summary>Registered exactly in AuthCenter as the client's post-logout redirect URI.</summary>
    internal string PostLogoutRedirectUri => NormalizedPublicOrigin + AuthCenterDefaults.PostLogoutRedirectPath;

    internal IEnumerable<string> Validate(IHostEnvironment environment)
    {
        if (!TryParseSecureBase(Authority, allowLoopbackHttp: false, out _))
        {
            yield return "AuthCenter:Authority must be an absolute HTTPS URL without credentials, query or fragment.";
        }

        if (string.IsNullOrWhiteSpace(Issuer) || !Uri.TryCreate(Issuer, UriKind.Absolute, out _))
        {
            yield return "AuthCenter:Issuer must be the exact absolute issuer emitted by AuthCenter.";
        }

        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 200 || ClientId.Any(char.IsWhiteSpace))
        {
            yield return "AuthCenter:ClientId is required.";
        }

        if (string.IsNullOrEmpty(ClientSecret) || ClientSecret.Length < 32)
        {
            yield return "AuthCenter:ClientSecret must be supplied by the secret store and be at least 32 characters.";
        }

        var allowLoopbackHttp = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (!TryParseSecureBase(PublicOrigin, allowLoopbackHttp, out var origin) || origin.AbsolutePath != "/")
        {
            yield return "AuthCenter:PublicOrigin must be the HTTPS origin of the web application (no path).";
        }

        if (SessionLifetimeMinutes is < 1 or > 1440)
        {
            yield return "AuthCenter:SessionLifetimeMinutes must be between 1 and 1440.";
        }

        if (!Enum.IsDefined(SessionStore))
        {
            yield return "AuthCenter:SessionStore must be PostgreSql or Memory.";
        }
    }

    private static bool TryParseSecureBase(string? value, bool allowLoopbackHttp, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps ||
            (allowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }
}
