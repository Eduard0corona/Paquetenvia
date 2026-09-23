using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Incidents.Infrastructure.Persistence.Migrations;

/// <summary>
/// INC-001 adopts the canonical AI-06 <c>incidents.incidents</c> table and evolves it additively
/// with the attributes the backlog makes mandatory: reason, explicit next action, SLA deadline and
/// evidence. Following the NTF-001 precedent, the objects INC-001 introduces live in this module
/// migration rather than in the frozen v0.6 bundle. Every step is guarded, so the migration is
/// idempotent; nothing is dropped and no existing row is discarded.
/// </summary>
[DbContext(typeof(IncidentsDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalIncidentsBaseline : Migration
{
    public const string MigrationId = "20260922_AdoptCanonicalIncidentsBaseline";

    public const string AdoptionSql =
        """
        DO $adoption$
        DECLARE
          incident_columns text[];
          evidence_columns text[];
        BEGIN
          IF to_regclass('incidents.incidents') IS NULL THEN
            RAISE EXCEPTION 'INC-001 adoption requires the canonical incidents table from AI-06';
          END IF;
          IF to_regclass('custody.proofs') IS NULL THEN
            RAISE EXCEPTION 'INC-001 adoption requires POD-001 canonical custody proofs';
          END IF;

          -- Mandatory INC-001 attributes. Added nullable, backfilled, then constrained so an
          -- existing installation evolves without losing incident history.
          ALTER TABLE incidents.incidents
            ADD COLUMN IF NOT EXISTS reason_code text,
            ADD COLUMN IF NOT EXISTS next_action text,
            ADD COLUMN IF NOT EXISTS occurred_at timestamptz,
            ADD COLUMN IF NOT EXISTS sla_due_at timestamptz;

          UPDATE incidents.incidents
          SET reason_code = COALESCE(reason_code, incident_type),
              next_action = COALESCE(next_action, CASE WHEN custody_acquired THEN 'RETURNING' ELSE 'RESCHEDULED' END),
              occurred_at = COALESCE(occurred_at, created_at),
              sla_due_at = COALESCE(
                sla_due_at,
                COALESCE(occurred_at, created_at) + CASE severity
                  WHEN 'CRITICAL' THEN interval '2 hours'
                  WHEN 'HIGH' THEN interval '8 hours'
                  WHEN 'MEDIUM' THEN interval '24 hours'
                  ELSE interval '72 hours'
                END)
          WHERE reason_code IS NULL
             OR next_action IS NULL
             OR occurred_at IS NULL
             OR sla_due_at IS NULL;

          ALTER TABLE incidents.incidents
            ALTER COLUMN reason_code SET NOT NULL,
            ALTER COLUMN next_action SET NOT NULL,
            ALTER COLUMN occurred_at SET NOT NULL,
            ALTER COLUMN sla_due_at SET NOT NULL;

          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='incidents.incidents'::regclass AND conname='incidents_next_action_check') THEN
            ALTER TABLE incidents.incidents
              ADD CONSTRAINT incidents_next_action_check
              CHECK (next_action IN ('RESCHEDULED','RETURNING'));
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='incidents.incidents'::regclass AND conname='incidents_sla_due_at_check') THEN
            ALTER TABLE incidents.incidents
              ADD CONSTRAINT incidents_sla_due_at_check CHECK (sla_due_at > occurred_at);
          END IF;

          CREATE INDEX IF NOT EXISTS incidents_sla_idx ON incidents.incidents(status,sla_due_at);

          -- Mandatory evidence, linked to POD-001 proofs and append-only like the proofs themselves.
          CREATE TABLE IF NOT EXISTS incidents.incident_evidence (
            id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            incident_id uuid NOT NULL REFERENCES incidents.incidents(id),
            order_id uuid NOT NULL REFERENCES orders.orders(id),
            owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
            operator_org_id uuid REFERENCES organizations.organizations(id),
            proof_id uuid NOT NULL REFERENCES custody.proofs(id),
            created_by uuid NOT NULL REFERENCES identity.users(id),
            created_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE (incident_id,proof_id)
          );
          CREATE INDEX IF NOT EXISTS incident_evidence_tenant_incident_idx
            ON incidents.incident_evidence(owner_org_id,incident_id);

          ALTER TABLE incidents.incident_evidence ENABLE ROW LEVEL SECURITY;
          ALTER TABLE incidents.incident_evidence FORCE ROW LEVEL SECURITY;

          IF NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname='incidents' AND tablename='incident_evidence'
              AND policyname='incident_evidence_tenant') THEN
            CREATE POLICY incident_evidence_tenant ON incidents.incident_evidence
              USING (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id))
              WITH CHECK (security.app_allowed_org(owner_org_id) OR security.app_allowed_org(operator_org_id));
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='incidents.incident_evidence'::regclass
              AND tgname='incident_evidence_append_only' AND NOT tgisinternal) THEN
            CREATE TRIGGER incident_evidence_append_only
              BEFORE UPDATE OR DELETE ON incidents.incident_evidence
              FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();
          END IF;

          -- Evidence is mandatory, so the check runs at commit: the incident and its evidence
          -- are written in the same transaction and neither can be persisted alone.
          CREATE OR REPLACE FUNCTION platform.require_incident_evidence() RETURNS trigger
          LANGUAGE plpgsql AS $evidence$
          BEGIN
            IF NOT EXISTS (SELECT 1 FROM incidents.incident_evidence e WHERE e.incident_id=NEW.id) THEN
              RAISE EXCEPTION 'incidents.incidents requires at least one evidence record' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
          END $evidence$;

          IF NOT EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='incidents.incidents'::regclass
              AND tgname='incidents_require_evidence' AND NOT tgisinternal) THEN
            CREATE CONSTRAINT TRIGGER incidents_require_evidence AFTER INSERT ON incidents.incidents
              DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION platform.require_incident_evidence();
          END IF;

          -- Verification: the canonical AI-06 columns are intact and the INC-001 columns follow
          -- them, in the order ADD COLUMN appends to an installed baseline.
          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY ordinal_position)
          INTO incident_columns
          FROM information_schema.columns
          WHERE table_schema='incidents' AND table_name='incidents';
          IF incident_columns IS DISTINCT FROM ARRAY[
            'id:uuid:NO','order_id:uuid:NO','owner_org_id:uuid:NO','operator_org_id:uuid:YES',
            'incident_type:text:NO','severity:text:NO','status:text:NO','custody_acquired:boolean:NO',
            'description_ciphertext:bytea:YES','pii_key_version:text:YES','created_by:uuid:NO',
            'created_at:timestamp with time zone:NO','resolved_at:timestamp with time zone:YES',
            'reason_code:text:NO','next_action:text:NO','occurred_at:timestamp with time zone:NO',
            'sla_due_at:timestamp with time zone:NO'
          ] THEN
            RAISE EXCEPTION 'incidents.incidents columns do not match the INC-001 contract';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY ordinal_position)
          INTO evidence_columns
          FROM information_schema.columns
          WHERE table_schema='incidents' AND table_name='incident_evidence';
          IF evidence_columns IS DISTINCT FROM ARRAY[
            'id:uuid:NO','incident_id:uuid:NO','order_id:uuid:NO','owner_org_id:uuid:NO',
            'operator_org_id:uuid:YES','proof_id:uuid:NO','created_by:uuid:NO',
            'created_at:timestamp with time zone:NO'
          ] THEN
            RAISE EXCEPTION 'incidents.incident_evidence columns do not match the INC-001 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='incidents.incidents'::regclass AND relrowsecurity AND relforcerowsecurity)
             OR NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='incidents.incident_evidence'::regclass AND relrowsecurity AND relforcerowsecurity) THEN
            RAISE EXCEPTION 'canonical incidents RLS configuration is missing';
          END IF;

          IF (SELECT count(*) FROM pg_policies
              WHERE schemaname='incidents'
                AND policyname IN ('incidents_tenant','incident_evidence_tenant')) <> 2 THEN
            RAISE EXCEPTION 'canonical incidents tenant policies are missing';
          END IF;
        END
        $adoption$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(AdoptionSql);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // INC-001 rollback blocked: incident evidence is an append-only operational record.
        // Adoption is intentionally non-destructive and canonical AI-06 objects are never dropped.
    }
}
