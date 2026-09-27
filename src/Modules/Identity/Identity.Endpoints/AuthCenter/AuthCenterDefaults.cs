namespace Identity.Endpoints.AuthCenter;

/// <summary>
/// Fixed BFF contract shared with AuthCenter.Client (login, callback, session, logout, back-channel
/// logout, CSRF header).
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
    public const string BackchannelLogoutPath = "/auth/backchannel-logout";
    public const string RemoteFailureRedirect = "/login?error=signin_failed";

    /// <summary>AuthCenter answered <c>error=access_denied</c>: the account has no access to Paquetenvia.</summary>
    public const string AccessDeniedRedirect = "/login?error=access_denied";

    /// <summary>
    /// AUTH-EMAIL-VERIFIED-REQUIRED: AuthCenter did not assert <c>email_verified=true</c>; no user is
    /// created or linked and no session is issued.
    /// </summary>
    public const string EmailNotVerifiedRedirect = "/login?error=email_not_verified";

    /// <summary>Registered exactly (ordinal) as the post-logout redirect URI, after the public origin.</summary>
    public const string PostLogoutRedirectPath = "/login";

    /// <summary><c>/auth/login?mfa=required</c> requests a step-up to this AuthCenter class.</summary>
    public const string MfaRequiredQueryValue = "required";
    public const string MfaContextClass = "urn:authcenter:acr:mfa";
    public const string PhishingResistantContextClass = "urn:authcenter:acr:phr";

    internal const string ExternalAuthenticationType = "Paquetenvia.AuthCenter.External";
    internal const string CsrfPropertyKey = "paquetenvia.csrf";
    internal const string MfaClaim = "urn:paquetenvia:authcenter:v1:mfa";
    internal const string SubjectClaim = "sub";
    internal const string NameClaim = "name";
    internal const string EmailClaim = "email";
    internal const string EmailVerifiedClaim = "email_verified";
    internal const string RefreshTokenName = "refresh_token";
    internal const string IdTokenName = "id_token";
    internal const string SessionIdClaim = "sid";
    internal const string RequiredContextClassItemKey = "paquetenvia.acr";

    /// <summary>Sign-in moment in Unix milliseconds (the ticket's IssuedUtc is kept to the second).</summary>
    internal const string SignedInAtItemKey = "paquetenvia.signed_in_at";
    internal const string LogoutTokenType = "logout+jwt";
    internal const string BackchannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";
    internal const int MaximumSessionIdLength = 128;
    internal const string ProfileItemKey = "Paquetenvia.AuthCenter.Profile";
    internal const string CsrfItemKey = "Paquetenvia.AuthCenter.Csrf";
    internal const int MaximumSubjectLength = 256;

    internal static readonly string[] RequiredScopes = ["openid", "profile", "email", "offline_access"];
}
