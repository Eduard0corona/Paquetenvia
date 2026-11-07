using Paqueteria.Application.Tenancy;

namespace Organizations.Endpoints.Tenancy;

public sealed class RequestTenantContext : ITenantContext
{
    public bool IsSelected { get; private set; }

    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// REL-001-CROSS-TENANT-SECURITY-EVENT-2026-11-06: true when the tenant context middleware refused the requested
    /// organization with the uniform 403, because the actor has no active membership in it. Another tenant's, an
    /// inactive and an unknown organization are refused alike, so this says nothing about which one it was.
    /// </summary>
    public bool OrganizationRefused { get; private set; }

    internal void Select(Guid organizationId)
    {
        if (IsSelected || OrganizationRefused)
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

    internal void RefuseOrganization()
    {
        if (IsSelected)
        {
            throw new InvalidOperationException("The active organization is immutable for the request.");
        }

        OrganizationRefused = true;
    }
}
