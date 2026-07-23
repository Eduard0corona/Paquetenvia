using Locations.Domain;
using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;

namespace Locations.Infrastructure.Persistence;

public sealed class LocationsDbContext(
    DbContextOptions<LocationsDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    public DbSet<City> Cities => Set<City>();
    public DbSet<ServiceArea> ServiceAreas => Set<ServiceArea>();
    public DbSet<OperatingZone> OperatingZones => Set<OperatingZone>();
    public DbSet<Location> Locations => Set<Location>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var city = modelBuilder.Entity<City>();
        city.ToTable("cities", "locations");
        city.HasKey(entity => entity.Id);
        city.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        city.Property(entity => entity.CountryCode).HasColumnName("country_code").HasColumnType("character(2)").IsRequired();
        city.Property(entity => entity.StateCode).HasColumnName("state_code").HasColumnType("text").IsRequired();
        city.Property(entity => entity.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        city.Property(entity => entity.Timezone).HasColumnName("timezone").HasColumnType("text").IsRequired();
        city.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseStatus(value)).ValueGeneratedNever();
        city.HasIndex(entity => new { entity.CountryCode, entity.StateCode, entity.Name })
            .IsUnique().HasDatabaseName("cities_country_code_state_code_name_key");

        var serviceArea = modelBuilder.Entity<ServiceArea>();
        serviceArea.ToTable("service_areas", "locations");
        serviceArea.HasKey(entity => entity.Id);
        serviceArea.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        serviceArea.Property(entity => entity.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        serviceArea.Property(entity => entity.CityId).HasColumnName("city_id").ValueGeneratedNever();
        serviceArea.Property(entity => entity.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        serviceArea.Property(entity => entity.Polygon).HasColumnName("polygon").HasColumnType("geometry(MultiPolygon,4326)").IsRequired();
        serviceArea.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseStatus(value)).ValueGeneratedNever();
        serviceArea.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        serviceArea.HasOne<City>().WithMany().HasForeignKey(entity => entity.CityId).OnDelete(DeleteBehavior.NoAction);
        serviceArea.HasIndex(entity => new { entity.OwnerOrganizationId, entity.CityId, entity.Name })
            .IsUnique().HasDatabaseName("service_areas_owner_org_id_city_id_name_key");
        serviceArea.HasIndex(entity => entity.Polygon).HasMethod("gist").HasDatabaseName("service_areas_polygon_gix");
        serviceArea.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.OwnerOrganizationId));

        var zone = modelBuilder.Entity<OperatingZone>();
        zone.ToTable("operating_zones", "locations");
        zone.HasKey(entity => entity.Id);
        zone.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        zone.Property(entity => entity.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        zone.Property(entity => entity.ServiceAreaId).HasColumnName("service_area_id").ValueGeneratedNever();
        zone.Property(entity => entity.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        zone.Property(entity => entity.ZoneType).HasColumnName("zone_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseZoneType(value)).ValueGeneratedNever();
        zone.Property(entity => entity.Polygon).HasColumnName("polygon").HasColumnType("geometry(MultiPolygon,4326)").IsRequired();
        zone.Property(entity => entity.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseStatus(value)).ValueGeneratedNever();
        zone.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        zone.HasOne<ServiceArea>().WithMany().HasForeignKey(entity => entity.ServiceAreaId).OnDelete(DeleteBehavior.NoAction);
        zone.HasIndex(entity => new { entity.OwnerOrganizationId, entity.ServiceAreaId, entity.Name })
            .IsUnique().HasDatabaseName("operating_zones_owner_org_id_service_area_id_name_key");
        zone.HasIndex(entity => entity.Polygon).HasMethod("gist").HasDatabaseName("operating_zones_polygon_gix");
        zone.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.OwnerOrganizationId));

        var location = modelBuilder.Entity<Location>();
        location.ToTable("locations", "locations");
        location.HasKey(entity => entity.Id);
        location.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        location.Property(entity => entity.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        location.Property(entity => entity.CityId).HasColumnName("city_id").ValueGeneratedNever();
        location.Property(entity => entity.ServiceAreaId).HasColumnName("service_area_id");
        location.Property(entity => entity.OperatingZoneId).HasColumnName("operating_zone_id");
        location.Property(entity => entity.Point).HasColumnName("point").HasColumnType("geometry(Point,4326)").IsRequired();
        location.Property(entity => entity.AddressCiphertext).HasColumnName("address_ciphertext").HasColumnType("bytea").IsRequired();
        location.Property(entity => entity.AddressSummary).HasColumnName("address_summary").HasColumnType("text").IsRequired();
        location.Property(entity => entity.ContactNameCiphertext).HasColumnName("contact_name_ciphertext").HasColumnType("bytea");
        location.Property(entity => entity.PhoneCiphertext).HasColumnName("phone_ciphertext").HasColumnType("bytea");
        location.Property(entity => entity.PiiKeyVersion).HasColumnName("pii_key_version").HasColumnType("text").IsRequired();
        location.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        location.HasOne<City>().WithMany().HasForeignKey(entity => entity.CityId).OnDelete(DeleteBehavior.NoAction);
        location.HasOne<ServiceArea>().WithMany().HasForeignKey(entity => entity.ServiceAreaId).OnDelete(DeleteBehavior.NoAction);
        location.HasOne<OperatingZone>().WithMany().HasForeignKey(entity => entity.OperatingZoneId).OnDelete(DeleteBehavior.NoAction);
        location.HasIndex(entity => entity.Point).HasMethod("gist").HasDatabaseName("locations_point_gix");
        location.HasIndex(entity => new { entity.OwnerOrganizationId, entity.CityId }).HasDatabaseName("locations_owner_city_idx");
        location.HasQueryFilter(entity => tenantState.OrganizationIds.Contains(entity.OwnerOrganizationId));
    }

    private static GeographicStatus ParseStatus(string value) => value switch
    {
        "ACTIVE" => GeographicStatus.Active,
        "INACTIVE" => GeographicStatus.Inactive,
        _ => throw new InvalidOperationException("Unknown geographic status stored in PostgreSQL."),
    };

    private static OperatingZoneType ParseZoneType(string value) => value switch
    {
        "CORE" => OperatingZoneType.Core,
        "STANDARD" => OperatingZoneType.Standard,
        "EXTENDED" => OperatingZoneType.Extended,
        "EXCLUDED" => OperatingZoneType.Excluded,
        _ => throw new InvalidOperationException("Unknown operating zone type stored in PostgreSQL."),
    };
}
