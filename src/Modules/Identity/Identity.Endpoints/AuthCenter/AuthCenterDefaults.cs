namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Fixed BFF contract shared with AuthCenter.Client (login, callback, session, logout, CSRF header).
/// Paths are not configurable so that the web rewrites, the ingress and the AuthCenter redirect URI
/// registration stay in lockstep.
/// </summary>
public static class AuthCenterDefaults
{
    public const string SectionName = "AuthCenter";
    public const string CookieScheme = "Paquetenvia.AuthCenter.Cookie";
    public const string OpenIdConnectScheme = "Paquetenvia.AuthCenter.Oidc";
    public const string SessionCookieName = "__Host-Paquetenvia.Session";
    public const string CorrelationCookiePrefix = "__Host-Paquetenvia.Correlation.";
    public const string NonceCookiePrefix = "__Host-Paquetenvia.Nonce.";
    public const string CsrfHeaderName = "X-AuthCenter-CSRF";
    public const string CallbackPath = "/signin-authcenter";
    public const string LoginPath = "/auth/login";
    public const string SessionPath = "/auth/session";
    public const string LogoutPath = "/auth/logout";
    public const string RemoteFailureRedirect = "/login?error=signin_failed";

    internal const string ExternalAuthenticationType = "Paquetenvia.AuthCenter.External";
    internal const string CsrfPropertyKey = "paquetenvia.csrf";
    internal const string MfaClaim = "urn:paquetenvia:authcenter:v1:mfa";
    internal const string SubjectClaim = "sub";
    internal const string NameClaim = "name";
    internal const string EmailClaim = "email";
    internal const string RefreshTokenName = "refresh_token";
    internal const string ProfileItemKey = "Paquetenvia.AuthCenter.Profile";
    internal const string CsrfItemKey = "Paquetenvia.AuthCenter.Csrf";
    internal const int MaximumSubjectLength = 256;

    internal static readonly string[] RequiredScopes = ["openid", "profile", "email", "offline_access"];
}
