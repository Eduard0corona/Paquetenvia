using Paqueteria.Domain.Tenancy;

namespace Identity.Domain;

public sealed record OrganizationMembership(
    Guid OrganizationId,
    OrganizationRole Role,
    MembershipStatus Status);
