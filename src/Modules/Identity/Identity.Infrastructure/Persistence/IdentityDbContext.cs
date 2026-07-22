using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;

namespace Identity.Infrastructure.Persistence;

public sealed class IdentityDbContext(
    DbContextOptions<IdentityDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<User>();
        user.ToTable("users", "identity");
        user.HasKey(entity => entity.Id);
        user.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        user.Property(entity => entity.IdentitySubject).HasColumnName("identity_subject").HasColumnType("text").IsRequired();
        user.Property(entity => entity.EmailCiphertext).HasColumnName("email_ciphertext").HasColumnType("bytea");
        user.Property(entity => entity.Status)
            .HasColumnName("status")
            .HasColumnType("text")
            .HasConversion(value => value.ToContractValue(), value => ParseStatus(value))
            .ValueGeneratedNever();
        user.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").ValueGeneratedNever();
        user.HasIndex(entity => entity.IdentitySubject).IsUnique().HasDatabaseName("users_identity_subject_key");
        user.HasQueryFilter(entity => tenantState.UserId.HasValue && entity.Id == tenantState.UserId.Value);
    }

    private static IdentityStatus ParseStatus(string value) => value switch
    {
        "ACTIVE" => IdentityStatus.Active,
        "SUSPENDED" => IdentityStatus.Suspended,
        "DISABLED" => IdentityStatus.Disabled,
        _ => throw new InvalidOperationException("Unknown identity status stored in PostgreSQL."),
    };
}
