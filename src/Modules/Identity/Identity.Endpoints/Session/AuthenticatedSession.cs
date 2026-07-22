using System.Collections.Immutable;
using System.Security.Claims;
using Identity.Application.Authentication;
using Identity.Application.Session;
using Identity.Endpoints.Security;

namespace Identity.Endpoints.Session;

public sealed class AuthenticatedSession : IAuthenticatedSession
{
    private AuthenticatedSession(
        bool isAuthenticated,
        string? subject,
        NormalizedIdentityStatus? identityStatus,
        bool mfaSatisfied,
        ImmutableArray<NormalizedOrganizationMembership> activeMemberships)
    {
        IsAuthenticated = isAuthenticated;
        Subject = subject;
        IdentityStatus = identityStatus;
        MfaSatisfied = mfaSatisfied;
        ActiveMemberships = activeMemberships;
    }

    public bool IsAuthenticated { get; }

    public string? Subject { get; }

    public NormalizedIdentityStatus? IdentityStatus { get; }

    public bool MfaSatisfied { get; }

    public IReadOnlyList<NormalizedOrganizationMembership> ActiveMemberships { get; }

    public bool HasOrganizationAccess(Guid organizationId) =>
        IsAuthenticated && ActiveMemberships.Any(membership => membership.OrganizationId == organizationId);

    public bool HasRole(Guid organizationId, NormalizedOrganizationRole role) =>
        IsAuthenticated && ActiveMemberships.Any(membership =>
            membership.OrganizationId == organizationId && membership.Role == role);

    public bool HasAnyActiveRole(NormalizedOrganizationRole role) =>
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
        var statusValue = TrustedValues(identity, IdentityClaimTypes.Status).SingleOrDefault();
        if (string.IsNullOrWhiteSpace(subject) || !TryParseStatus(statusValue, out var status))
        {
            return Anonymous();
        }

        var memberships = ImmutableArray.CreateBuilder<NormalizedOrganizationMembership>();
        foreach (var value in TrustedValues(identity, IdentityClaimTypes.Membership))
        {
            if (!PaquetenviaClaimsTransformation.TryParseMembership(value, out var membership))
            {
                return Anonymous();
            }

            if (membership.Status == NormalizedMembershipStatus.Active)
            {
                memberships.Add(membership);
            }
        }

        var mfaSatisfied = TrustedValues(identity, IdentityClaimTypes.AuthenticationMethodReference)
            .Contains("mfa", StringComparer.Ordinal);

        return new AuthenticatedSession(true, subject, status, mfaSatisfied, memberships.ToImmutable());
    }

    private static AuthenticatedSession Anonymous() =>
        new(false, null, null, false, []);

    private static IEnumerable<string> TrustedValues(ClaimsIdentity identity, string type) =>
        identity.Claims
            .Where(claim =>
                claim.Type == type &&
                claim.Issuer == IdentityClaimTypes.Issuer &&
                claim.OriginalIssuer == IdentityClaimTypes.Issuer)
            .Select(claim => claim.Value);

    private static bool TryParseStatus(string? value, out NormalizedIdentityStatus status)
    {
        status = value switch
        {
            "ACTIVE" => NormalizedIdentityStatus.Active,
            "SUSPENDED" => NormalizedIdentityStatus.Suspended,
            "DISABLED" => NormalizedIdentityStatus.Disabled,
            _ => default,
        };

        return value is "ACTIVE" or "SUSPENDED" or "DISABLED";
    }
}
