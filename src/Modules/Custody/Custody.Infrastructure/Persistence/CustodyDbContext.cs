using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;

namespace Custody.Infrastructure.Persistence;

public sealed class CustodyDbContext(
    DbContextOptions<CustodyDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    internal DbSet<ProofUploadSessionRow> UploadSessions => Set<ProofUploadSessionRow>();
    internal DbSet<ProofRow> Proofs => Set<ProofRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<ProofUploadSessionRow>();
        session.ToTable("proof_upload_sessions", "custody");
        session.HasKey(value => value.Id);
        session.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        session.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        session.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        session.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id").ValueGeneratedNever();
        session.Property(value => value.RequestedBy).HasColumnName("requested_by").ValueGeneratedNever();
        session.Property(value => value.QuarantineObjectKey).HasColumnName("object_key_quarantine").ValueGeneratedNever();
        session.Property(value => value.ExpectedContentType).HasColumnName("expected_content_type").ValueGeneratedNever();
        session.Property(value => value.MaximumBytes).HasColumnName("maximum_bytes").ValueGeneratedNever();
        session.Property(value => value.Status).HasColumnName("status").ValueGeneratedNever();
        session.Property(value => value.ExpiresAt).HasColumnName("expires_at").ValueGeneratedNever();
        session.Property(value => value.CreatedAt).HasColumnName("created_at").ValueGeneratedNever();
        session.Property(value => value.UpdatedAt).HasColumnName("updated_at").ValueGeneratedNever();
        session.HasIndex(value => value.QuarantineObjectKey).IsUnique();
        session.HasIndex(value => new { value.Status, value.ExpiresAt })
            .HasDatabaseName("proof_upload_sessions_expiry_idx");
        session.HasQueryFilter(value =>
            tenantState.OrganizationIds.Contains(value.OwnerOrganizationId) ||
            (value.OperatorOrganizationId != null &&
             tenantState.OrganizationIds.Contains(value.OperatorOrganizationId.Value)));

        var proof = modelBuilder.Entity<ProofRow>();
        proof.ToTable("proofs", "custody");
        proof.HasKey(value => value.Id);
        proof.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        proof.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        proof.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        proof.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id").ValueGeneratedNever();
        proof.Property(value => value.UploadSessionId).HasColumnName("upload_session_id").ValueGeneratedNever();
        proof.Property(value => value.ProofType).HasColumnName("proof_type").ValueGeneratedNever();
        proof.Property(value => value.ObjectKey).HasColumnName("object_key").ValueGeneratedNever();
        proof.Property(value => value.Sha256).HasColumnName("sha256").ValueGeneratedNever();
        proof.Property(value => value.ContentType).HasColumnName("content_type").ValueGeneratedNever();
        proof.Property(value => value.SizeBytes).HasColumnName("size_bytes").ValueGeneratedNever();
        proof.Property(value => value.RecipientNameCiphertext).HasColumnName("recipient_name_ciphertext").ValueGeneratedNever();
        proof.Property(value => value.PiiKeyVersion).HasColumnName("pii_key_version").ValueGeneratedNever();
        proof.Property(value => value.CapturedAt).HasColumnName("captured_at").ValueGeneratedNever();
        proof.Property(value => value.CapturedPoint).HasColumnName("captured_point").HasColumnType("geometry(Point,4326)").ValueGeneratedNever();
        proof.Property(value => value.CreatedBy).HasColumnName("created_by").ValueGeneratedNever();
        proof.Property(value => value.CreatedAt).HasColumnName("created_at").ValueGeneratedNever();
        proof.HasIndex(value => value.UploadSessionId).IsUnique();
        proof.HasIndex(value => value.ObjectKey).IsUnique();
        proof.HasIndex(value => new { value.OwnerOrganizationId, value.OrderId, value.CreatedAt })
            .HasDatabaseName("proofs_tenant_order_idx");
        proof.HasQueryFilter(value =>
            tenantState.OrganizationIds.Contains(value.OwnerOrganizationId) ||
            (value.OperatorOrganizationId != null &&
             tenantState.OrganizationIds.Contains(value.OperatorOrganizationId.Value)));
    }
}

internal sealed class ProofUploadSessionRow
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OwnerOrganizationId { get; set; }
    public Guid? OperatorOrganizationId { get; set; }
    public Guid RequestedBy { get; set; }
    public string QuarantineObjectKey { get; set; } = string.Empty;
    public string ExpectedContentType { get; set; } = string.Empty;
    public long MaximumBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ProofRow
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OwnerOrganizationId { get; set; }
    public Guid? OperatorOrganizationId { get; set; }
    public Guid UploadSessionId { get; set; }
    public string ProofType { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public byte[] Sha256 { get; set; } = [];
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[]? RecipientNameCiphertext { get; set; }
    public string? PiiKeyVersion { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public NetTopologySuite.Geometries.Point? CapturedPoint { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
