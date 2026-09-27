using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Incidents.Infrastructure.Persistence.Migrations;

/// <summary>
/// ORD-002 asks, on every transition into <c>PICKED_UP</c> or <c>DELIVERED</c>, whether a proof of
/// the order is already incident evidence (such a proof never completes an attempt). That lookup is
/// by <c>(order_id, proof_id)</c>, which neither <c>(owner_org_id, incident_id)</c> nor
/// <c>UNIQUE (incident_id, proof_id)</c> serves. <c>incidents.incident_evidence</c> is created by
/// the INC-001 module migration, not by the frozen AI-06 bundle, so the index belongs to this
/// module. It adds no rule and rewrites no row, and dropping it only removes an access path, so
/// this migration — unlike the adoption before it — rolls back.
/// </summary>
[DbContext(typeof(IncidentsDbContext))]
[Migration(MigrationId)]
public sealed class IndexIncidentEvidenceByOrderProof : Migration
{
    public const string MigrationId = "20260927000100_IndexIncidentEvidenceByOrderProof";

    public const string IndexName = "incident_evidence_order_proof_idx";

    public const string UpSql =
        """
        DO $index$
        BEGIN
          IF to_regclass('incidents.incident_evidence') IS NULL THEN
            RAISE EXCEPTION 'the incident evidence index requires the INC-001 incident evidence table';
          END IF;

          CREATE INDEX IF NOT EXISTS incident_evidence_order_proof_idx
            ON incidents.incident_evidence(order_id,proof_id);

          IF NOT EXISTS (
            SELECT 1
            FROM pg_index x
            JOIN pg_class i ON i.oid=x.indexrelid
            WHERE x.indrelid='incidents.incident_evidence'::regclass
              AND i.relname='incident_evidence_order_proof_idx'
              AND x.indisvalid
              AND pg_get_indexdef(x.indexrelid) LIKE '%(order_id, proof_id)') THEN
            RAISE EXCEPTION 'incident_evidence_order_proof_idx does not cover (order_id, proof_id)';
          END IF;
        END
        $index$;
        """;

    public const string DownSql =
        "DROP INDEX IF EXISTS incidents.incident_evidence_order_proof_idx;";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(DownSql);
}
