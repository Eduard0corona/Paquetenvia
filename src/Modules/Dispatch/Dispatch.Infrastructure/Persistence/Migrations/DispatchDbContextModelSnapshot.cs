using Dispatch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dispatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DispatchDbContext))]
public sealed class DispatchDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");

        modelBuilder.Entity<Assignment>(entity =>
        {
            entity.ToTable("assignments", "dispatch");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
            entity.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id")
                .ValueGeneratedNever();
            entity.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id")
                .ValueGeneratedNever();
            entity.Property(value => value.DriverId).HasColumnName("driver_id").ValueGeneratedNever();
            entity.Property(value => value.RouteId).HasColumnName("route_id").ValueGeneratedNever();
            entity.Property(value => value.AssignmentType).HasColumnName("assignment_type")
                .HasColumnType("text").HasConversion<string>().ValueGeneratedNever();
            entity.Property(value => value.Status).HasColumnName("status")
                .HasColumnType("text").HasConversion<string>().ValueGeneratedNever();
            entity.Property(value => value.CostCents).HasColumnName("cost_cents")
                .HasColumnType("bigint").ValueGeneratedNever();
            entity.Property(value => value.AcceptedAt).HasColumnName("accepted_at")
                .HasColumnType("timestamp with time zone").ValueGeneratedNever();
            entity.Property(value => value.CreatedAt).HasColumnName("created_at")
                .HasColumnType("timestamp with time zone").ValueGeneratedNever();
            entity.HasIndex(value => value.OrderId).IsUnique()
                .HasDatabaseName("one_active_assignment_per_order")
                .HasFilter("status IN ('ACCEPTED','ACTIVE')");
        });
    }
}
