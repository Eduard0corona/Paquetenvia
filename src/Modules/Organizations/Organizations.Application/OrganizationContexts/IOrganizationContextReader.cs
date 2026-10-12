using System.Text.Json.Serialization;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Application.OrganizationContexts;

public sealed record AuthorizedOrganizationMembership(
    Guid OrganizationId,
    OrganizationRole Role,
    bool IsDefault);

/// <summary>
/// One organization the requesting person may act in. <see cref="Role"/> and <see cref="IsDefault"/> come from one
/// membership of that organization (the default one when there is one), exactly as before.
/// <see cref="Roles"/> (ORG-CONTEXT-ROLES-2026-10-12) lists every role the person holds there through an ACTIVE
/// membership: unique, sorted by contract value, never empty and always containing <see cref="Role"/>. Only the
/// caller's own roles; nothing about any other person.
/// </summary>
public sealed record OrganizationContextResponse(
    [property: JsonPropertyName("organization_id")] Guid OrganizationId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("is_default")] bool IsDefault,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles);

public interface IOrganizationContextReader
{
    Task<IReadOnlyList<OrganizationContextResponse>> ReadAsync(
        Guid userId,
        IReadOnlyCollection<AuthorizedOrganizationMembership> memberships,
        CancellationToken cancellationToken);
}

/// <summary>
/// ORG-CONTEXT-ROLES-2026-10-12: the roles of <see cref="OrganizationContextResponse.Roles"/> for every organization
/// of the requesting person's own active memberships: unique contract values, sorted ordinally.
/// </summary>
public static class OrganizationContextRoles
{
    public static IReadOnlyDictionary<Guid, IReadOnlyList<string>> ByOrganization(
        IEnumerable<AuthorizedOrganizationMembership> memberships)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        return memberships
            .GroupBy(membership => membership.OrganizationId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(membership => membership.Role.ToContractValue())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
    }
}

public sealed class OrganizationContextUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
