using Microsoft.EntityFrameworkCore;

namespace Paqueteria.Infrastructure.Database.Outbox;

internal sealed class PlatformOutboxDbContext(DbContextOptions<PlatformOutboxDbContext> options) : DbContext(options)
{
    internal DbSet<OutboxEventRecord> OutboxEvents => Set<OutboxEventRecord>();

    internal DbSet<LocationOutboxEventRecord> LocationOutboxEvents => Set<LocationOutboxEventRecord>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectLifecycleMutation();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        RejectLifecycleMutation();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema("platform");
        ConfigureOutbox(modelBuilder.Entity<OutboxEventRecord>());
        ConfigureLocationOutbox(modelBuilder.Entity<LocationOutboxEventRecord>());
    }

    private static void ConfigureOutbox(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<OutboxEventRecord> entity)
    {
        entity.ToTable("outbox_events", "platform");
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        entity.Property(item => item.TenantContext).HasColumnName("tenant_context").HasColumnType("jsonb").IsRequired().ValueGeneratedNever();
        entity.Property(item => item.Topic).HasColumnName("topic").IsRequired().ValueGeneratedNever();
        entity.Property(item => item.AggregateType).HasColumnName("aggregate_type").IsRequired().ValueGeneratedNever();
        entity.Property(item => item.AggregateId).HasColumnName("aggregate_id").ValueGeneratedNever();
        entity.Property(item => item.AggregateVersion).HasColumnName("aggregate_version").ValueGeneratedNever();
        entity.Property(item => item.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired().ValueGeneratedNever();
        entity.Property(item => item.Priority).HasColumnName("priority").ValueGeneratedNever();
        entity.Property(item => item.Status).HasColumnName("status").IsRequired().ValueGeneratedNever();
        entity.Property(item => item.Attempts).HasColumnName("attempts").ValueGeneratedNever();
        entity.Property(item => item.AvailableAt).HasColumnName("available_at").ValueGeneratedNever();
        entity.Property(item => item.LockedAt).HasColumnName("locked_at").ValueGeneratedNever();
        entity.Property(item => item.LockedBy).HasColumnName("locked_by").ValueGeneratedNever();
        entity.Property(item => item.LeaseToken).HasColumnName("lease_token").ValueGeneratedNever();
        entity.Property(item => item.LeaseExpiresAt).HasColumnName("lease_expires_at").ValueGeneratedNever();
        entity.Property(item => item.LastError).HasColumnName("last_error").ValueGeneratedNever();
        entity.Property(item => item.CreatedAt).HasColumnName("created_at").ValueGeneratedNever();
        entity.Property(item => item.ProcessedAt).HasColumnName("processed_at").ValueGeneratedNever();
    }

    private static void ConfigureLocationOutbox(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<LocationOutboxEventRecord> entity)
    {
        entity.ToTable("location_outbox_events", "platform");
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(item => item.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        entity.Property(item => item.DriverPositionId).HasColumnName("driver_position_id").ValueGeneratedNever();
        entity.Property(item => item.Topic).HasColumnName("topic").IsRequired().ValueGeneratedNever();
        entity.Property(item => item.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired().ValueGeneratedNever();
        entity.Property(item => item.Status).HasColumnName("status").IsRequired().ValueGeneratedNever();
        entity.Property(item => item.Attempts).HasColumnName("attempts").ValueGeneratedNever();
        entity.Property(item => item.AvailableAt).HasColumnName("available_at").ValueGeneratedNever();
        entity.Property(item => item.LockedAt).HasColumnName("locked_at").ValueGeneratedNever();
        entity.Property(item => item.LockedBy).HasColumnName("locked_by").ValueGeneratedNever();
        entity.Property(item => item.LeaseToken).HasColumnName("lease_token").ValueGeneratedNever();
        entity.Property(item => item.LeaseExpiresAt).HasColumnName("lease_expires_at").ValueGeneratedNever();
        entity.Property(item => item.LastError).HasColumnName("last_error").ValueGeneratedNever();
        entity.Property(item => item.CreatedAt).HasColumnName("created_at").ValueGeneratedNever();
        entity.Property(item => item.ProcessedAt).HasColumnName("processed_at").ValueGeneratedNever();
    }

    private void RejectLifecycleMutation()
    {
        var invalidEntry = ChangeTracker.Entries()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (invalidEntry is not null)
        {
            throw new InvalidOperationException(
                $"Tracked lifecycle mutation is prohibited for {invalidEntry.Metadata.ClrType.Name}; use the canonical security functions.");
        }
    }
}
