using System.Security.Claims;
using Identity.Application.Bootstrap;
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
        var membershipClaims = TrustedClaims(source, IdentityClaimTypes.SourceMembership).ToArray();

        if (subjectClaims.Length != 1 ||
            string.IsNullOrWhiteSpace(subjectClaims[0].Value) ||
            statusClaims.Length > 1 ||
            statusClaims.Length == 1 && statusClaims[0].Value != "ACTIVE" ||
            statusClaims.Length == 0 && membershipClaims.Length != 0 ||
            mfaClaims.Length != 1 ||
            !bool.TryParse(mfaClaims[0].Value, out var mfaSatisfied))
        {
            sessionClaims = [];
            return false;
        }

        var memberships = new List<IdentityContextMembership>();
        foreach (var membershipClaim in membershipClaims)
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
        };

        if (statusClaims.Length == 1)
        {
            claims.Add(SessionClaim(IdentityClaimTypes.Status, "ACTIVE"));
        }

        if (mfaSatisfied)
        {
            claims.Add(SessionClaim(IdentityClaimTypes.AuthenticationMethodReference, "mfa"));
        }

        claims.AddRange(memberships.Select(membership =>
            SessionClaim(IdentityClaimTypes.Membership, IdentityClaimsPrincipalFactory.SerializeMembership(membership))));

        sessionClaims = claims;
        return true;
    }

    internal static bool TryParseMembership(string value, out IdentityContextMembership membership)
    {
        var parts = value.Split('|', StringSplitOptions.None);
        if (parts.Length != 3 ||
            !Guid.TryParseExact(parts[0], "D", out var organizationId) ||
            organizationId == Guid.Empty ||
            !TryParseRole(parts[1], out var role) ||
            !bool.TryParse(parts[2], out var isDefault))
        {
            membership = null!;
            return false;
        }

        membership = new IdentityContextMembership(organizationId, role, isDefault);
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

    private static bool TryParseRole(string value, out OrganizationRole role)
    {
        role = value switch
        {
            "PLATFORM_ADMIN" => OrganizationRole.PlatformAdmin,
            "DISPATCHER" => OrganizationRole.Dispatcher,
            "FINANCE" => OrganizationRole.Finance,
            "ALLY_ADMIN" => OrganizationRole.AllyAdmin,
            "ALLY_OPERATOR" => OrganizationRole.AllyOperator,
            "BUSINESS_ADMIN" => OrganizationRole.BusinessAdmin,
            "BUSINESS_OPERATOR" => OrganizationRole.BusinessOperator,
            "DRIVER" => OrganizationRole.Driver,
            "VIEWER" => OrganizationRole.Viewer,
            _ => default,
        };

        return Enum.IsDefined(role) && value == role.ToContractValue();
    }
}
