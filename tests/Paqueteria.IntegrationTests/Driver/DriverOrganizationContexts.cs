using System.Text.Json;
using Organizations.Application.OrganizationContexts;
using Paqueteria.Domain.Tenancy;

namespace Paqueteria.IntegrationTests.Driver;

/// <summary>
/// ORG-CONTEXT-ROLES-2026-10-12: the token-mode driver sessions of the PWA tests are installed from a
/// GET /api/v1/me/organization-contexts answer shaped exactly like the API's, never from an injected flag. The
/// answer is built with the API's own response record and role aggregation
/// (<see cref="OrganizationContextResponse"/>, <see cref="OrganizationContextRoles"/>) for one person's ACTIVE
/// memberships in one organization and serialized with the web JSON defaults; the init script then installs the
/// session the way the BFF installation does (the selected organization's DRIVER context, its <c>roles</c>
/// copied as <c>organizationRoles</c>).
/// </summary>
internal static class DriverOrganizationContexts
{
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    /// <summary>The API answer for a person whose memberships in the organization hold these roles (DRIVER by default).</summary>
    public static JsonElement Answer(string organizationId, IReadOnlyCollection<OrganizationRole> roles)
    {
        var id = Guid.Parse(organizationId);
        IReadOnlyCollection<OrganizationRole> held = roles.Count == 0 ? [OrganizationRole.Driver] : roles;
        var memberships = held
            .Select(role => new AuthorizedOrganizationMembership(id, role, role == OrganizationRole.Driver))
            .ToArray();
        // As PostgreSqlOrganizationContextReader: role and is_default from the default membership.
        var membership = memberships.OrderByDescending(value => value.IsDefault).First();
        OrganizationContextResponse[] answer =
        [
            new(
                id,
                "Paquetería Sintética",
                membership.Role.ToContractValue(),
                membership.IsDefault,
                OrganizationContextRoles.ByOrganization(memberships)[id]),
        ];
        return JsonSerializer.SerializeToElement(answer, ApiJson);
    }

    /// <summary>
    /// The statements that install <c>window.__paquetenviaDriverSession</c> from <c>value</c>, an object with
    /// <c>organizationId</c>, <c>cacheNamespace</c>, <c>token</c> and <c>contexts</c> (the API answer). A context
    /// that is missing or not DRIVER installs nothing, so the test fails loudly.
    /// </summary>
    public const string InstallFromAnswer =
        """
        const selected = value.contexts.find(
          (context) => context.organization_id === value.organizationId);
        if (selected && selected.role === "DRIVER") {
          window.__paquetenviaDriverSession = {
            organizationId: selected.organization_id,
            cacheNamespace: value.cacheNamespace,
            getAccessToken: () => value.token,
            organizationRoles: selected.roles,
          };
        }
        """;
}
