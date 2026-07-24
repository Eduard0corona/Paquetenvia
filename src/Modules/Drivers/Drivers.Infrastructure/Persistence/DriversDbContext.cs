using Drivers.Domain;
using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;

namespace Drivers.Infrastructure.Persistence;

public sealed class DriversDbContext(
    DbContextOptions<DriversDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    public DbSet<DriverProfile> DriverProfiles => Set<DriverProfile>();
    public DbSet<DriverServiceArea> DriverServiceAreas => Set<DriverServiceArea>();
    public DbSet<DriverDocument> DriverDocuments => Set<DriverDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var profile = modelBuilder.Entity<DriverProfile>();
        profile.ToTable("driver_profiles", "drivers");
        profile.HasKey(value => value.Id);
        profile.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        profile.Property(value => value.UserId).HasColumnName("user_id").ValueGeneratedNever();
        profile.Property(value => value.OrganizationId).HasColumnName("org_id").ValueGeneratedNever();
        profile.Property(value => value.HomeCityId).HasColumnName("home_city_id").ValueGeneratedNever();
        profile.Property(value => value.DriverType).HasColumnName("driver_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseDriverType(value)).ValueGeneratedNever();
        profile.Property(value => value.VehicleType).HasColumnName("vehicle_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseVehicleType(value)).ValueGeneratedNever();
        profile.Property(value => value.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseProfileStatus(value)).ValueGeneratedNever();
        profile.Property(value => value.CreatedAt).HasColumnName("created_at")
            .HasColumnType("timestamp with time zone").ValueGeneratedNever();
        profile.HasIndex(value => value.UserId)
            .IsUnique().HasDatabaseName("driver_profiles_user_id_key");
        profile.HasQueryFilter(value => tenantState.OrganizationIds.Contains(value.OrganizationId));

        var serviceArea = modelBuilder.Entity<DriverServiceArea>();
        serviceArea.ToTable("driver_service_areas", "drivers");
        serviceArea.HasKey(value => new { value.DriverId, value.ServiceAreaId });
        serviceArea.Property(value => value.DriverId).HasColumnName("driver_id").ValueGeneratedNever();
        serviceArea.Property(value => value.ServiceAreaId).HasColumnName("service_area_id").ValueGeneratedNever();
        serviceArea.Property(value => value.OrganizationId).HasColumnName("org_id").ValueGeneratedNever();
        serviceArea.Property(value => value.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseServiceAreaStatus(value)).ValueGeneratedNever();
        serviceArea.HasOne<DriverProfile>().WithMany().HasForeignKey(value => value.DriverId)
            .OnDelete(DeleteBehavior.NoAction);
        serviceArea.HasQueryFilter(value => tenantState.OrganizationIds.Contains(value.OrganizationId));

        var document = modelBuilder.Entity<DriverDocument>();
        document.ToTable("driver_documents", "drivers");
        document.HasKey(value => value.Id);
        document.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        document.Property(value => value.DriverId).HasColumnName("driver_id").ValueGeneratedNever();
        document.Property(value => value.OrganizationId).HasColumnName("org_id").ValueGeneratedNever();
        document.Property(value => value.DocumentType).HasColumnName("document_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseDocumentType(value)).ValueGeneratedNever();
        document.Property(value => value.ObjectKey).HasColumnName("object_key").HasColumnType("text").IsRequired()
            .ValueGeneratedNever();
        document.Property(value => value.Sha256).HasColumnName("sha256").HasColumnType("bytea").IsRequired()
            .ValueGeneratedNever();
        document.Property(value => value.ExpiresAt).HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone");
        document.Property(value => value.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseDocumentStatus(value)).ValueGeneratedNever();
        document.Property(value => value.CreatedAt).HasColumnName("created_at")
            .HasColumnType("timestamp with time zone").ValueGeneratedNever();
        document.HasOne<DriverProfile>().WithMany().HasForeignKey(value => value.DriverId)
            .OnDelete(DeleteBehavior.NoAction);
        document.HasIndex(value => new { value.DriverId, value.Status, value.ExpiresAt })
            .HasDatabaseName("driver_documents_eligibility_idx");
        document.HasQueryFilter(value => tenantState.OrganizationIds.Contains(value.OrganizationId));
    }

    private static DriverType ParseDriverType(string value) => value switch
    {
        "OWN" => DriverType.Own,
        "EXTERNAL" => DriverType.External,
        "ALLY" => DriverType.Ally,
        _ => throw Unknown("driver type"),
    };

    private static VehicleType ParseVehicleType(string value) => value switch
    {
        "MOTORCYCLE" => VehicleType.Motorcycle,
        "CAR" => VehicleType.Car,
        "VAN" => VehicleType.Van,
        "BICYCLE" => VehicleType.Bicycle,
        "WALKER" => VehicleType.Walker,
        _ => throw Unknown("vehicle type"),
    };

    private static DriverProfileStatus ParseProfileStatus(string value) => value switch
    {
        "PENDING" => DriverProfileStatus.Pending,
        "ACTIVE" => DriverProfileStatus.Active,
        "SUSPENDED" => DriverProfileStatus.Suspended,
        "INACTIVE" => DriverProfileStatus.Inactive,
        _ => throw Unknown("driver profile status"),
    };

    private static DriverServiceAreaStatus ParseServiceAreaStatus(string value) => value switch
    {
        "ACTIVE" => DriverServiceAreaStatus.Active,
        "INACTIVE" => DriverServiceAreaStatus.Inactive,
        _ => throw Unknown("driver service area status"),
    };

    private static DriverDocumentType ParseDocumentType(string value) => value switch
    {
        "IDENTITY" => DriverDocumentType.Identity,
        "DRIVER_LICENSE" => DriverDocumentType.DriverLicense,
        "VEHICLE_CARD" => DriverDocumentType.VehicleCard,
        "INSURANCE" => DriverDocumentType.Insurance,
        "BACKGROUND_CHECK" => DriverDocumentType.BackgroundCheck,
        "OTHER" => DriverDocumentType.Other,
        _ => throw Unknown("driver document type"),
    };

    private static DriverDocumentStatus ParseDocumentStatus(string value) => value switch
    {
        "PENDING" => DriverDocumentStatus.Pending,
        "VALID" => DriverDocumentStatus.Valid,
        "REJECTED" => DriverDocumentStatus.Rejected,
        "EXPIRED" => DriverDocumentStatus.Expired,
        "REVOKED" => DriverDocumentStatus.Revoked,
        _ => throw Unknown("driver document status"),
    };

    private static InvalidOperationException Unknown(string kind) =>
        new($"Unknown {kind} stored in PostgreSQL.");
}
