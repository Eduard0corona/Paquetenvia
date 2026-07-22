using System.Globalization;
using System.Security.Claims;
using Identity.Application.Authentication;
using Identity.Application.Bootstrap;

namespace Identity.Endpoints.Security;

public static class IdentityClaimsPrincipalFactory
{
    public static bool TryCreate(
        ExternalIdentity externalIdentity,
        IdentityContextResolution resolution,
        out ClaimsPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(externalIdentity);
        ArgumentNullException.ThrowIfNull(resolution);

        if (string.IsNullOrWhiteSpace(externalIdentity.Subject) ||
            resolution.Context is { } context &&
            (context.UserId == Guid.Empty ||
             context.Status != IdentityContextStatus.Active ||
             context.Memberships.Any(membership =>
                 membership.OrganizationId == Guid.Empty || !Enum.IsDefined(membership.Role))))
        {
            principal = null;
            return false;
        }

        var claims = new List<Claim>
        {
            InternalClaim(IdentityClaimTypes.SourceSubject, externalIdentity.Subject),
            InternalClaim(
                IdentityClaimTypes.SourceMfa,
                externalIdentity.MfaSatisfied.ToString(CultureInfo.InvariantCulture)),
        };

        if (resolution.Context is { } resolved)
        {
            claims.Add(InternalClaim(IdentityClaimTypes.SourceStatus, resolved.Status.ToContractValue()));
            claims.AddRange(resolved.Memberships.Select(membership =>
                InternalClaim(IdentityClaimTypes.SourceMembership, SerializeMembership(membership))));
        }

        principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            IdentitySecurityDefaults.SourceAuthenticationType,
            IdentityClaimTypes.SourceSubject,
            roleType: null));
        return true;
    }

    internal static string SerializeMembership(IdentityContextMembership membership) =>
        string.Join(
            '|',
            membership.OrganizationId.ToString("D", CultureInfo.InvariantCulture),
            membership.Role.ToContractValue(),
            membership.IsDefault.ToString(CultureInfo.InvariantCulture));

    private static Claim InternalClaim(string type, string value) =>
        new(type, value, ClaimValueTypes.String, IdentityClaimTypes.Issuer, IdentityClaimTypes.Issuer);
}
