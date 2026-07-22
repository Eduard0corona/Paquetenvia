using Microsoft.EntityFrameworkCore;
using Organizations.Domain;
using Paqueteria.Domain.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Organizations.Infrastructure.Persistence;

public sealed class OrganizationsDbContext(
    DbContextOptions<OrganizationsDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMembership> Memberships => Set<OrganizationMembership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var organization = modelBuilder.Entity<Organization>();
        organization.ToTable("organizations", "organizations");
        organization.HasKey(entity => entity.Id);
        organization.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        organization.Property(entity => entity.LegalName).HasColumnName("legal_name").HasColumnType("text").IsRequired();
        organization.Property(entity => entity.DisplayName).HasColumnName("display_name").HasColumnType("text").IsRequired();
        organization.Property(entity => entity.OrganizationType).HasColumnName("organization_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseOrganizationType(value)).ValueGeneratedNever();
        organization.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseOrganizationStatus(value)).ValueGeneratedNever();
        organization.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        organization.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.Id));

        var membership = modelBuilder.Entity<OrganizationMembership>();
        membership.ToTable("organization_memberships", "organizations");
        membership.HasKey(entity => entity.Id);
        membership.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        membership.Property(entity => entity.UserId).HasColumnName("user_id").ValueGeneratedNever();
        membership.Property(entity => entity.OrganizationId).HasColumnName("organization_id").ValueGeneratedNever();
        membership.Property(entity => entity.Role).HasColumnName("role").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseRole(value)).ValueGeneratedNever();
        membership.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseMembershipStatus(value)).ValueGeneratedNever();
        membership.Property(entity => entity.IsDefault).HasColumnName("is_default").ValueGeneratedNever();
        membership.Property(entity => entity.GrantedAt).HasColumnName("granted_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        membership.Property(entity => entity.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamp with time zone");
        membership.HasIndex(entity => new { entity.UserId, entity.OrganizationId, entity.Role })
            .IsUnique().HasFilter("status = 'ACTIVE'").HasDatabaseName("organization_memberships_active_role_uq");
        membership.HasIndex(entity => entity.UserId).IsUnique().HasFilter("status = 'ACTIVE' AND is_default")
            .HasDatabaseName("organization_memberships_one_default_uq");
        membership.HasIndex(entity => new { entity.UserId, entity.Status }).HasDatabaseName("organization_memberships_user_idx");
        membership.HasIndex(entity => new { entity.OrganizationId, entity.Status }).HasDatabaseName("organization_memberships_org_idx");
        membership.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.OrganizationId));
    }

    private static OrganizationType ParseOrganizationType(string value) => value switch
    {
        "PLATFORM" => OrganizationType.Platform,
        "ALLY" => OrganizationType.Ally,
        "BUSINESS" => OrganizationType.Business,
        _ => throw new InvalidOperationException("Unknown organization type stored in PostgreSQL."),
    };

    private static OrganizationStatus ParseOrganizationStatus(string value) => value switch
    {
        "ACTIVE" => OrganizationStatus.Active,
        "SUSPENDED" => OrganizationStatus.Suspended,
        "CLOSED" => OrganizationStatus.Closed,
        _ => throw new InvalidOperationException("Unknown organization status stored in PostgreSQL."),
    };

    private static MembershipStatus ParseMembershipStatus(string value) => value switch
    {
        "ACTIVE" => MembershipStatus.Active,
        "SUSPENDED" => MembershipStatus.Suspended,
        "REVOKED" => MembershipStatus.Revoked,
        _ => throw new InvalidOperationException("Unknown membership status stored in PostgreSQL."),
    };

    private static OrganizationRole ParseRole(string value) => value switch
    {
        "PLATFORM_ADMIN" => OrganizationRole.PlatformAdmin,
        "DISPATCHER" => OrganizationRole.Dispatcher,
        "FINANCE" => OrganizationRole.Finance,
        "ALLY_ADMIN" => OrganizationRole.AllyAdmin,
        "ALLY_OPERATOR" => OrganizationRole.AllyOperator,
        "BUSINESS_ADMIN" => OrganizationRole.BusinessAdmin,
        "BUSINESS_OPERATOR" => OrganizationRole.BusinessOperator,
        "DRIVER" => OrganizationRole.Driver,
        "VIEWER" => OrganizationRole.Viewer,
        _ => throw new InvalidOperationException("Unknown organization role stored in PostgreSQL."),
    };
}
