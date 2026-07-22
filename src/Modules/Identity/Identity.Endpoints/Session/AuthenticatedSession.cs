using System.Collections.Immutable;
using System.Security.Claims;
using Identity.Application.Bootstrap;
using Identity.Application.Session;
using Identity.Endpoints.Security;

namespace Identity.Endpoints.Session;

public sealed class AuthenticatedSession : IAuthenticatedSession
{
    private AuthenticatedSession(
        bool isAuthenticated,
        string? subject,
        Guid? userId,
        IdentityContextStatus? identityStatus,
        bool mfaSatisfied,
        ImmutableArray<IdentityContextMembership> activeMemberships)
    {
        IsAuthenticated = isAuthenticated;
        Subject = subject;
        UserId = userId;
        IdentityStatus = identityStatus;
        MfaSatisfied = mfaSatisfied;
        ActiveMemberships = activeMemberships;
    }

    public bool IsAuthenticated { get; }
    public string? Subject { get; }
    public Guid? UserId { get; }
    public IdentityContextStatus? IdentityStatus { get; }
    public bool MfaSatisfied { get; }
    public IReadOnlyList<IdentityContextMembership> ActiveMemberships { get; }

    public bool HasOrganizationAccess(Guid organizationId) =>
        IsAuthenticated && ActiveMemberships.Any(membership => membership.OrganizationId == organizationId);

    public bool HasRole(Guid organizationId, OrganizationRole role) =>
        IsAuthenticated && ActiveMemberships.Any(membership =>
            membership.OrganizationId == organizationId && membership.Role == role);

    public bool HasAnyActiveRole(OrganizationRole role) =>
        IsAuthenticated && ActiveMemberships.Any(membership => membership.Role == role);

    public static AuthenticatedSession FromPrincipal(ClaimsPrincipal? principal)
    {
        var identity = principal?.Identities.SingleOrDefault(candidate =>
            candidate.AuthenticationType == IdentitySecurityDefaults.SessionAuthenticationType);
        if (identity is null || !identity.IsAuthenticated)
        {
            return Anonymous();
        }

        var subject = TrustedValues(identity, IdentityClaimTypes.Subject).SingleOrDefault();
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Anonymous();
        }

        var statusValues = TrustedValues(identity, IdentityClaimTypes.Status).ToArray();
        if (statusValues.Length > 1 || statusValues.Length == 1 && statusValues[0] != "ACTIVE")
        {
            return Anonymous();
        }

        var userIdValues = TrustedValues(identity, IdentityClaimTypes.UserId).ToArray();
        var userId = Guid.Empty;
        if (statusValues.Length == 1 &&
            (userIdValues.Length != 1 ||
             !Guid.TryParseExact(userIdValues[0], "D", out userId) ||
             userId == Guid.Empty))
        {
            return Anonymous();
        }

        var memberships = ImmutableArray.CreateBuilder<IdentityContextMembership>();
        foreach (var value in TrustedValues(identity, IdentityClaimTypes.Membership))
        {
            if (!PaquetenviaClaimsTransformation.TryParseMembership(value, out var membership))
            {
                return Anonymous();
            }

            memberships.Add(membership);
        }

        var mfaSatisfied = TrustedValues(identity, IdentityClaimTypes.AuthenticationMethodReference)
            .Contains("mfa", StringComparer.Ordinal);

        return new AuthenticatedSession(
            true,
            subject,
            statusValues.Length == 1 ? userId : null,
            statusValues.Length == 1 ? IdentityContextStatus.Active : null,
            mfaSatisfied,
            memberships.ToImmutable());
    }

    private static AuthenticatedSession Anonymous() => new(false, null, null, null, false, []);

    private static IEnumerable<string> TrustedValues(ClaimsIdentity identity, string type) =>
        identity.Claims
            .Where(claim =>
                claim.Type == type &&
                claim.Issuer == IdentityClaimTypes.Issuer &&
                claim.OriginalIssuer == IdentityClaimTypes.Issuer)
            .Select(claim => claim.Value);
}
