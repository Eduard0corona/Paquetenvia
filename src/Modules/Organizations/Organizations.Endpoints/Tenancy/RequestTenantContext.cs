using Paqueteria.Application.Tenancy;

namespace Organizations.Endpoints.Tenancy;

public sealed class RequestTenantContext : ITenantContext
{
    public bool IsSelected { get; private set; }

    public Guid OrganizationId { get; private set; }

    internal void Select(Guid organizationId)
    {
        if (IsSelected)
        {
            throw new InvalidOperationException("The active organization is immutable for the request.");
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty organization id is required.", nameof(organizationId));
        }

        OrganizationId = organizationId;
        IsSelected = true;
    }
}
