using System.Text.Json.Serialization;
using Paqueteria.Domain.Tenancy;

namespace Organizations.Application.OrganizationContexts;

public sealed record AuthorizedOrganizationMembership(
    Guid OrganizationId,
    OrganizationRole Role,
    bool IsDefault);

public sealed record OrganizationContextResponse(
    [property: JsonPropertyName("organization_id")] Guid OrganizationId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("is_default")] bool IsDefault);

public interface IOrganizationContextReader
{
    Task<IReadOnlyList<OrganizationContextResponse>> ReadAsync(
        Guid userId,
        IReadOnlyCollection<AuthorizedOrganizationMembership> memberships,
        CancellationToken cancellationToken);
}

public sealed class OrganizationContextUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
