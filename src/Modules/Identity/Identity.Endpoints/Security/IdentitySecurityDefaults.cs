namespace Identity.Endpoints.Security;

public static class IdentitySecurityDefaults
{
    public const string DefaultScheme = "Paquetenvia.Authentication";
    public const string MockScheme = "Paquetenvia.MockOidc";
    public const string DisabledScheme = "Paquetenvia.Disabled";
    public const string SourceAuthenticationType = "Paquetenvia.NormalizedIdentity";
    public const string SessionAuthenticationType = "Paquetenvia.Session";
}

public static class IdentityPolicies
{
    public const string Authenticated = "Identity.Authenticated";
    public const string ActiveIdentity = "Identity.Active";
    public const string RequireMfa = "Identity.RequireMfa";
    public const string PrivilegedMfa = "Identity.PrivilegedMfa";
}

public static class IdentityClaimTypes
{
    public const string Subject = "sub";
    public const string UserId = "urn:paquetenvia:identity:v1:user-id";
    public const string AuthenticationMethodReference = "amr";
    public const string Status = "urn:paquetenvia:identity:v1:status";
    public const string Membership = "urn:paquetenvia:identity:v1:membership";

    internal const string SourceSubject = "urn:paquetenvia:identity:v1:source:subject";
    internal const string SourceUserId = "urn:paquetenvia:identity:v1:source:user-id";
    internal const string SourceStatus = "urn:paquetenvia:identity:v1:source:status";
    internal const string SourceMfa = "urn:paquetenvia:identity:v1:source:mfa";
    internal const string SourceMembership = "urn:paquetenvia:identity:v1:source:membership";
    internal const string Issuer = "urn:paquetenvia:identity:internal";
}
