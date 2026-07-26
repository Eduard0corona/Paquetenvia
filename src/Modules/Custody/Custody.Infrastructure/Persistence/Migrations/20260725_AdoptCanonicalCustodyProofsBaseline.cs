using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Custody.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CustodyDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalCustodyProofsBaseline : Migration
{
    public const string MigrationId = "20260725_AdoptCanonicalCustodyProofsBaseline";

    public const string AdoptionSql =
        """
        DO $adoption$
        DECLARE
          session_columns text[];
          proof_columns text[];
          policy_count integer;
        BEGIN
          IF to_regclass('custody.proof_upload_sessions') IS NULL
             OR to_regclass('custody.proofs') IS NULL THEN
            RAISE EXCEPTION 'POD-001 adoption requires canonical custody tables from AI-06';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY ordinal_position)
          INTO session_columns
          FROM information_schema.columns
          WHERE table_schema='custody' AND table_name='proof_upload_sessions';
          IF session_columns IS DISTINCT FROM ARRAY[
            'id:uuid:NO','order_id:uuid:NO','owner_org_id:uuid:NO','operator_org_id:uuid:YES',
            'requested_by:uuid:NO','object_key_quarantine:text:NO','expected_content_type:text:NO',
            'maximum_bytes:bigint:NO','status:text:NO','expires_at:timestamp with time zone:NO',
            'created_at:timestamp with time zone:NO','updated_at:timestamp with time zone:NO'
          ] THEN
            RAISE EXCEPTION 'custody.proof_upload_sessions columns do not match AI-06';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY ordinal_position)
          INTO proof_columns
          FROM information_schema.columns
          WHERE table_schema='custody' AND table_name='proofs';
          IF proof_columns IS DISTINCT FROM ARRAY[
            'id:uuid:NO','order_id:uuid:NO','owner_org_id:uuid:NO','operator_org_id:uuid:YES',
            'upload_session_id:uuid:NO','proof_type:text:NO','object_key:text:NO','sha256:bytea:NO',
            'content_type:text:NO','size_bytes:bigint:NO','recipient_name_ciphertext:bytea:YES',
            'pii_key_version:text:YES','captured_at:timestamp with time zone:NO',
            'captured_point:USER-DEFINED:YES','created_by:uuid:NO','created_at:timestamp with time zone:NO'
          ] THEN
            RAISE EXCEPTION 'custody.proofs columns do not match AI-06';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='custody.proof_upload_sessions'::regclass
              AND relrowsecurity AND relforcerowsecurity)
             OR NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='custody.proofs'::regclass
              AND relrowsecurity AND relforcerowsecurity) THEN
            RAISE EXCEPTION 'canonical custody RLS configuration is missing';
          END IF;

          SELECT count(*) INTO policy_count FROM pg_policies
          WHERE schemaname='custody' AND tablename IN ('proof_upload_sessions','proofs')
            AND policyname IN ('proof_upload_sessions_tenant','proofs_tenant');
          IF policy_count <> 2 THEN
            RAISE EXCEPTION 'canonical custody tenant policies are missing';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid='custody.proofs'::regclass
              AND tgname='proofs_append_only' AND NOT tgisinternal) THEN
            RAISE EXCEPTION 'canonical proofs_append_only trigger is missing';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_indexes
            WHERE schemaname='custody' AND tablename='proof_upload_sessions'
              AND indexname='proof_upload_sessions_expiry_idx')
             OR NOT EXISTS (
            SELECT 1 FROM pg_indexes
            WHERE schemaname='custody' AND tablename='proofs'
              AND indexname='proofs_tenant_order_idx') THEN
            RAISE EXCEPTION 'canonical custody indexes are missing';
          END IF;
        END
        $adoption$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(AdoptionSql);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
