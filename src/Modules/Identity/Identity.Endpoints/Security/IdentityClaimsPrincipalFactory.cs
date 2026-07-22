using System.Globalization;
using System.Security.Claims;
using Identity.Application.Authentication;

namespace Identity.Endpoints.Security;

public static class IdentityClaimsPrincipalFactory
{
    public static bool TryCreate(NormalizedIdentity normalizedIdentity, out ClaimsPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(normalizedIdentity);

        if (string.IsNullOrWhiteSpace(normalizedIdentity.Subject) ||
            !Enum.IsDefined(normalizedIdentity.Status) ||
            normalizedIdentity.Memberships.Any(membership =>
                membership.OrganizationId == Guid.Empty ||
                !Enum.IsDefined(membership.Role) ||
                !Enum.IsDefined(membership.Status)))
        {
            principal = null;
            return false;
        }

        var claims = new List<Claim>
        {
            InternalClaim(IdentityClaimTypes.SourceSubject, normalizedIdentity.Subject),
            InternalClaim(IdentityClaimTypes.SourceStatus, normalizedIdentity.Status.ToContractValue()),
            InternalClaim(
                IdentityClaimTypes.SourceMfa,
                normalizedIdentity.MfaSatisfied.ToString(CultureInfo.InvariantCulture)),
        };

        claims.AddRange(normalizedIdentity.Memberships.Select(membership =>
            InternalClaim(IdentityClaimTypes.SourceMembership, SerializeMembership(membership))));

        principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            IdentitySecurityDefaults.SourceAuthenticationType,
            IdentityClaimTypes.SourceSubject,
            roleType: null));
        return true;
    }

    internal static string SerializeMembership(NormalizedOrganizationMembership membership) =>
        string.Join(
            '|',
            membership.OrganizationId.ToString("D", CultureInfo.InvariantCulture),
            membership.Role.ToContractValue(),
            membership.Status.ToContractValue());

    private static Claim InternalClaim(string type, string value) =>
        new(type, value, ClaimValueTypes.String, IdentityClaimTypes.Issuer, IdentityClaimTypes.Issuer);
}
