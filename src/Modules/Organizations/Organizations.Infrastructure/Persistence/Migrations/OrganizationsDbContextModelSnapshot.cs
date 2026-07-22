using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Organizations.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrganizationsDbContext))]
public sealed class OrganizationsDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.Entity("Organizations.Domain.Organization", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("id");
            entity.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone").HasColumnName("created_at");
            entity.Property<string>("DisplayName").IsRequired().HasColumnType("text").HasColumnName("display_name");
            entity.Property<string>("LegalName").IsRequired().HasColumnType("text").HasColumnName("legal_name");
            entity.Property<string>("OrganizationType").IsRequired().HasColumnType("text").HasColumnName("organization_type");
            entity.Property<string>("Status").IsRequired().HasColumnType("text").HasColumnName("status");
            entity.HasKey("Id");
            entity.ToTable("organizations", "organizations");
        });
        modelBuilder.Entity("Organizations.Domain.OrganizationMembership", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("id");
            entity.Property<DateTimeOffset>("GrantedAt").HasColumnType("timestamp with time zone").HasColumnName("granted_at");
            entity.Property<bool>("IsDefault").HasColumnType("boolean").HasColumnName("is_default");
            entity.Property<Guid>("OrganizationId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("organization_id");
            entity.Property<DateTimeOffset?>("RevokedAt").HasColumnType("timestamp with time zone").HasColumnName("revoked_at");
            entity.Property<string>("Role").IsRequired().HasColumnType("text").HasColumnName("role");
            entity.Property<string>("Status").IsRequired().HasColumnType("text").HasColumnName("status");
            entity.Property<Guid>("UserId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("user_id");
            entity.HasKey("Id");
            entity.HasIndex("OrganizationId", "Status").HasDatabaseName("organization_memberships_org_idx");
            entity.HasIndex("UserId", "Status").HasDatabaseName("organization_memberships_user_idx");
            entity.HasIndex("UserId").IsUnique().HasFilter("status = 'ACTIVE' AND is_default").HasDatabaseName("organization_memberships_one_default_uq");
            entity.HasIndex("UserId", "OrganizationId", "Role").IsUnique().HasFilter("status = 'ACTIVE'").HasDatabaseName("organization_memberships_active_role_uq");
            entity.ToTable("organization_memberships", "organizations");
        });
    }
}
