using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
public sealed class IdentityDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.10");
        modelBuilder.Entity("Identity.Domain.User", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("id");
            entity.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone").HasColumnName("created_at");
            entity.Property<byte[]>("EmailCiphertext").HasColumnType("bytea").HasColumnName("email_ciphertext");
            entity.Property<string>("IdentitySubject").IsRequired().HasColumnType("text").HasColumnName("identity_subject");
            entity.Property<string>("Status").IsRequired().HasColumnType("text").HasColumnName("status");
            entity.HasKey("Id");
            entity.HasIndex("IdentitySubject").IsUnique().HasDatabaseName("users_identity_subject_key");
            entity.ToTable("users", "identity");
        });
    }
}
