using Paqueteria.Domain.Tenancy;

namespace Organizations.Domain;

public sealed class OrganizationMembership
{
    private OrganizationMembership()
    {
    }

    public OrganizationMembership(
        Guid id,
        Guid userId,
        Guid organizationId,
        OrganizationRole role,
        MembershipStatus status,
        bool isDefault,
        DateTimeOffset grantedAt,
        DateTimeOffset? revokedAt)
    {
        if (id == Guid.Empty || userId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new ArgumentException("Membership ids must be non-empty UUIDs.");
        }

        Id = id;
        UserId = userId;
        OrganizationId = organizationId;
        Role = role;
        Status = status;
        IsDefault = isDefault;
        GrantedAt = grantedAt;
        RevokedAt = revokedAt;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public OrganizationRole Role { get; private set; }

    public MembershipStatus Status { get; private set; }

    public bool IsDefault { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }
}
