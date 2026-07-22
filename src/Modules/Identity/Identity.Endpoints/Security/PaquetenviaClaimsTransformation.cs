using System.Security.Claims;
using Identity.Application.Authentication;
using Microsoft.AspNetCore.Authentication;

namespace Identity.Endpoints.Security;

public sealed class PaquetenviaClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var retainedIdentities = principal.Identities
            .Where(identity => !IsSessionIdentity(identity))
            .ToList();
        var sources = retainedIdentities.Where(IsSourceIdentity).ToArray();
        if (sources.Length != 1 || !TryCreateSessionClaims(sources[0], out var sessionClaims))
        {
            return Task.FromResult(new ClaimsPrincipal(
                retainedIdentities.Where(identity => !IsSourceIdentity(identity))));
        }

        retainedIdentities.Add(new ClaimsIdentity(
            sessionClaims,
            IdentitySecurityDefaults.SessionAuthenticationType,
            IdentityClaimTypes.Subject,
            roleType: null));

        return Task.FromResult(new ClaimsPrincipal(retainedIdentities));
    }

    private static bool TryCreateSessionClaims(
        ClaimsIdentity source,
        out IReadOnlyCollection<Claim> sessionClaims)
    {
        var subjectClaims = TrustedClaims(source, IdentityClaimTypes.SourceSubject).ToArray();
        var statusClaims = TrustedClaims(source, IdentityClaimTypes.SourceStatus).ToArray();
        var mfaClaims = TrustedClaims(source, IdentityClaimTypes.SourceMfa).ToArray();

        if (subjectClaims.Length != 1 ||
            string.IsNullOrWhiteSpace(subjectClaims[0].Value) ||
            statusClaims.Length != 1 ||
            !TryParseIdentityStatus(statusClaims[0].Value, out var status) ||
            mfaClaims.Length != 1 ||
            !bool.TryParse(mfaClaims[0].Value, out var mfaSatisfied))
        {
            sessionClaims = [];
            return false;
        }

        var memberships = new List<NormalizedOrganizationMembership>();
        foreach (var membershipClaim in TrustedClaims(source, IdentityClaimTypes.SourceMembership))
        {
            if (!TryParseMembership(membershipClaim.Value, out var membership))
            {
                sessionClaims = [];
                return false;
            }

            memberships.Add(membership);
        }

        var claims = new List<Claim>
        {
            SessionClaim(IdentityClaimTypes.Subject, subjectClaims[0].Value),
            SessionClaim(IdentityClaimTypes.Status, status.ToContractValue()),
        };

        if (mfaSatisfied)
        {
            claims.Add(SessionClaim(IdentityClaimTypes.AuthenticationMethodReference, "mfa"));
        }

        claims.AddRange(memberships.Select(membership =>
            SessionClaim(IdentityClaimTypes.Membership, IdentityClaimsPrincipalFactory.SerializeMembership(membership))));

        sessionClaims = claims;
        return true;
    }

    internal static bool TryParseMembership(string value, out NormalizedOrganizationMembership membership)
    {
        var parts = value.Split('|', StringSplitOptions.None);
        if (parts.Length != 3 ||
            !Guid.TryParseExact(parts[0], "D", out var organizationId) ||
            organizationId == Guid.Empty ||
            !TryParseRole(parts[1], out var role) ||
            !TryParseMembershipStatus(parts[2], out var status))
        {
            membership = null!;
            return false;
        }

        membership = new NormalizedOrganizationMembership(organizationId, role, status);
        return true;
    }

    private static IEnumerable<Claim> TrustedClaims(ClaimsIdentity identity, string type) =>
        identity.Claims.Where(claim =>
            claim.Type == type &&
            claim.Issuer == IdentityClaimTypes.Issuer &&
            claim.OriginalIssuer == IdentityClaimTypes.Issuer);

    private static bool IsSourceIdentity(ClaimsIdentity identity) =>
        identity.AuthenticationType == IdentitySecurityDefaults.SourceAuthenticationType;

    private static bool IsSessionIdentity(ClaimsIdentity identity) =>
        identity.AuthenticationType == IdentitySecurityDefaults.SessionAuthenticationType;

    private static Claim SessionClaim(string type, string value) =>
        new(type, value, ClaimValueTypes.String, IdentityClaimTypes.Issuer, IdentityClaimTypes.Issuer);

    private static bool TryParseIdentityStatus(string value, out NormalizedIdentityStatus status)
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

    private static bool TryParseMembershipStatus(string value, out NormalizedMembershipStatus status)
    {
        status = value switch
        {
            "ACTIVE" => NormalizedMembershipStatus.Active,
            "SUSPENDED" => NormalizedMembershipStatus.Suspended,
            "REVOKED" => NormalizedMembershipStatus.Revoked,
            _ => default,
        };
        return value is "ACTIVE" or "SUSPENDED" or "REVOKED";
    }

    private static bool TryParseRole(string value, out NormalizedOrganizationRole role)
    {
        role = value switch
        {
            "PLATFORM_ADMIN" => NormalizedOrganizationRole.PlatformAdmin,
            "DISPATCHER" => NormalizedOrganizationRole.Dispatcher,
            "FINANCE" => NormalizedOrganizationRole.Finance,
            "ALLY_ADMIN" => NormalizedOrganizationRole.AllyAdmin,
            "ALLY_OPERATOR" => NormalizedOrganizationRole.AllyOperator,
            "BUSINESS_ADMIN" => NormalizedOrganizationRole.BusinessAdmin,
            "BUSINESS_OPERATOR" => NormalizedOrganizationRole.BusinessOperator,
            "DRIVER" => NormalizedOrganizationRole.Driver,
            "VIEWER" => NormalizedOrganizationRole.Viewer,
            _ => default,
        };

        return value is
            "PLATFORM_ADMIN" or
            "DISPATCHER" or
            "FINANCE" or
            "ALLY_ADMIN" or
            "ALLY_OPERATOR" or
            "BUSINESS_ADMIN" or
            "BUSINESS_OPERATOR" or
            "DRIVER" or
            "VIEWER";
    }
}
