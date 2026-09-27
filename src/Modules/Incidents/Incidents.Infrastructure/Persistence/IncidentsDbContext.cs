using Microsoft.EntityFrameworkCore;
using Paqueteria.Infrastructure.Tenancy;

namespace Incidents.Infrastructure.Persistence;

public sealed class IncidentsDbContext(
    DbContextOptions<IncidentsDbContext> options,
    TenantDatabaseExecutionState tenantState) : DbContext(options)
{
    internal DbSet<IncidentRow> Incidents => Set<IncidentRow>();
    internal DbSet<IncidentEvidenceRow> IncidentEvidence => Set<IncidentEvidenceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var incident = modelBuilder.Entity<IncidentRow>();
        incident.ToTable("incidents", "incidents");
        incident.HasKey(value => value.Id);
        incident.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        incident.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        incident.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        incident.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id").ValueGeneratedNever();
        incident.Property(value => value.IncidentType).HasColumnName("incident_type").ValueGeneratedNever();
        incident.Property(value => value.Severity).HasColumnName("severity").ValueGeneratedNever();
        incident.Property(value => value.Status).HasColumnName("status").ValueGeneratedNever();
        incident.Property(value => value.CustodyAcquired).HasColumnName("custody_acquired").ValueGeneratedNever();
        incident.Property(value => value.ReasonCode).HasColumnName("reason_code").ValueGeneratedNever();
        incident.Property(value => value.NextAction).HasColumnName("next_action").ValueGeneratedNever();
        incident.Property(value => value.OccurredAt).HasColumnName("occurred_at").ValueGeneratedNever();
        incident.Property(value => value.SlaDueAt).HasColumnName("sla_due_at").ValueGeneratedNever();
        incident.Property(value => value.DescriptionCiphertext).HasColumnName("description_ciphertext").ValueGeneratedNever();
        incident.Property(value => value.PiiKeyVersion).HasColumnName("pii_key_version").ValueGeneratedNever();
        incident.Property(value => value.CreatedBy).HasColumnName("created_by").ValueGeneratedNever();
        incident.Property(value => value.CreatedAt).HasColumnName("created_at").ValueGeneratedNever();
        incident.Property(value => value.ResolvedAt).HasColumnName("resolved_at").ValueGeneratedNever();
        incident.HasIndex(value => new { value.OwnerOrganizationId, value.OrderId, value.Status })
            .HasDatabaseName("incidents_tenant_order_idx");
        incident.HasIndex(value => new { value.Status, value.SlaDueAt })
            .HasDatabaseName("incidents_sla_idx");
        incident.HasQueryFilter(value =>
            tenantState.OrganizationIds.Contains(value.OwnerOrganizationId) ||
            (value.OperatorOrganizationId != null &&
             tenantState.OrganizationIds.Contains(value.OperatorOrganizationId.Value)));

        var evidence = modelBuilder.Entity<IncidentEvidenceRow>();
        evidence.ToTable("incident_evidence", "incidents");
        evidence.HasKey(value => value.Id);
        evidence.Property(value => value.Id).HasColumnName("id").ValueGeneratedNever();
        evidence.Property(value => value.IncidentId).HasColumnName("incident_id").ValueGeneratedNever();
        evidence.Property(value => value.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        evidence.Property(value => value.OwnerOrganizationId).HasColumnName("owner_org_id").ValueGeneratedNever();
        evidence.Property(value => value.OperatorOrganizationId).HasColumnName("operator_org_id").ValueGeneratedNever();
        evidence.Property(value => value.ProofId).HasColumnName("proof_id").ValueGeneratedNever();
        evidence.Property(value => value.CreatedBy).HasColumnName("created_by").ValueGeneratedNever();
        evidence.Property(value => value.CreatedAt).HasColumnName("created_at").ValueGeneratedNever();
        evidence.HasIndex(value => new { value.IncidentId, value.ProofId }).IsUnique();
        evidence.HasIndex(value => new { value.OwnerOrganizationId, value.IncidentId })
            .HasDatabaseName("incident_evidence_tenant_incident_idx");
        evidence.HasIndex(value => new { value.OrderId, value.ProofId })
            .HasDatabaseName("incident_evidence_order_proof_idx");
        evidence.HasQueryFilter(value =>
            tenantState.OrganizationIds.Contains(value.OwnerOrganizationId) ||
            (value.OperatorOrganizationId != null &&
             tenantState.OrganizationIds.Contains(value.OperatorOrganizationId.Value)));
    }
}

internal sealed class IncidentRow
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OwnerOrganizationId { get; set; }
    public Guid? OperatorOrganizationId { get; set; }
    public string IncidentType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool CustodyAcquired { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string NextAction { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset SlaDueAt { get; set; }
    public byte[]? DescriptionCiphertext { get; set; }
    public string? PiiKeyVersion { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

internal sealed class IncidentEvidenceRow
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public Guid OrderId { get; set; }
    public Guid OwnerOrganizationId { get; set; }
    public Guid? OperatorOrganizationId { get; set; }
    public Guid ProofId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
