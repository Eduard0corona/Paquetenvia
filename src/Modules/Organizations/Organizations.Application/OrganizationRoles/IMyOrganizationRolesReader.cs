using System.Text.Json.Serialization;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Application.OrganizationRoles;

/// <summary>
/// ORG-MY-ROLES-2026-10-12 (AI-05 <c>getMyOrganizationRoles</c>): the requesting person's own roles in the
/// organization selected with X-Organization-Id. Only role codes: no id, name or any other person's data.
/// </summary>
public sealed record MyOrganizationRolesResponse(
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles);

/// <summary>
/// The read behind <c>getMyOrganizationRoles</c>: every role of the person's ACTIVE memberships of the ACTIVE
/// organization, read inside one tenant transaction and never written.
/// </summary>
public interface IMyOrganizationRolesReader
{
    /// <summary>
    /// The roles as <see cref="MyOrganizationRoles.FromRoles"/> orders them; empty when the person holds no
    /// ACTIVE membership of that ACTIVE organization (the endpoint then answers the uniform 403).
    /// </summary>
    Task<IReadOnlyList<string>> ReadAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken);
}

/// <summary>The answer shape of <c>getMyOrganizationRoles</c>, shared by the reader and its tests.</summary>
public static class MyOrganizationRoles
{
    /// <summary>The AI-05 contract values of <paramref name="roles"/>, each once, in ordinal order.</summary>
    public static IReadOnlyList<string> FromRoles(IEnumerable<OrganizationRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles
            .Select(role => role.ToContractValue())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed class MyOrganizationRolesUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
