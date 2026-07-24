using Dispatch.Domain;
using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;

namespace Dispatch.Infrastructure.Persistence;

public sealed class DispatchDbContext(
    DbContextOptions<DispatchDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    internal DbSet<Assignment> Assignments => Set<Assignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var assignment = modelBuilder.Entity<Assignment>();
        assignment.ToTable("assignments", "dispatch");
        assignment.HasKey(value => value.Id);
        assignment.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        assignment.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        assignment.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id")
            .ValueGeneratedNever();
        assignment.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id")
            .ValueGeneratedNever();
        assignment.Property(value => value.DriverId).HasColumnName("driver_id").ValueGeneratedNever();
        assignment.Property(value => value.RouteId).HasColumnName("route_id").ValueGeneratedNever();
        assignment.Property(value => value.AssignmentType).HasColumnName("assignment_type").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseAssignmentType(value))
            .ValueGeneratedNever();
        assignment.Property(value => value.Status).HasColumnName("status").HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseAssignmentStatus(value))
            .ValueGeneratedNever();
        assignment.Property(value => value.CostCents).HasColumnName("cost_cents").HasColumnType("bigint")
            .ValueGeneratedNever();
        assignment.Property(value => value.AcceptedAt).HasColumnName("accepted_at")
            .HasColumnType("timestamp with time zone").ValueGeneratedNever();
        assignment.Property(value => value.CreatedAt).HasColumnName("created_at")
            .HasColumnType("timestamp with time zone").ValueGeneratedNever();
        assignment.HasIndex(value => value.OrderId)
            .IsUnique()
            .HasDatabaseName("one_active_assignment_per_order")
            .HasFilter("status IN ('ACCEPTED','ACTIVE')");
        assignment.HasQueryFilter(value =>
            tenantState.OrganizationIds.Contains(value.OwnerOrganizationId) ||
            (value.OperatorOrganizationId != null &&
             tenantState.OrganizationIds.Contains(value.OperatorOrganizationId.Value)));
    }

    private static AssignmentType ParseAssignmentType(string value) => value switch
    {
        "OWN" => AssignmentType.Own,
        "EXTERNAL" => AssignmentType.External,
        "ALLY_CAPACITY" => AssignmentType.AllyCapacity,
        _ => throw Unknown("assignment type"),
    };

    private static AssignmentStatus ParseAssignmentStatus(string value) => value switch
    {
        "OFFERED" => AssignmentStatus.Offered,
        "ACCEPTED" => AssignmentStatus.Accepted,
        "ACTIVE" => AssignmentStatus.Active,
        "COMPLETED" => AssignmentStatus.Completed,
        "CANCELLED" => AssignmentStatus.Cancelled,
        _ => throw Unknown("assignment status"),
    };

    private static InvalidOperationException Unknown(string kind) =>
        new($"Unknown {kind} stored in PostgreSQL.");
}
