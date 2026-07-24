using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dispatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DispatchDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalDispatchAssignmentsBaseline : Migration
{
    public const string MigrationId = "20260723_AdoptCanonicalDispatchAssignmentsBaseline";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $adoption$
        DECLARE
          actual_columns text[];
          assignment_check_count integer;
          status_check_count integer;
          canonical_foreign_keys integer;
          canonical_index text;
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname='dispatch')
             OR to_regclass('dispatch.assignments') IS NULL THEN
            RAISE EXCEPTION 'DSP-002 adoption requires dispatch.assignments from AI-06';
          END IF;

          SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY ordinal_position)
          INTO actual_columns
          FROM information_schema.columns
          WHERE table_schema='dispatch' AND table_name='assignments';
          IF actual_columns IS DISTINCT FROM ARRAY[
            'id:uuid:NO','order_id:uuid:NO','owner_org_id:uuid:NO','operator_org_id:uuid:YES',
            'driver_id:uuid:NO','route_id:uuid:YES','assignment_type:text:NO','status:text:NO',
            'cost_cents:bigint:NO','accepted_at:timestamp with time zone:YES',
            'created_at:timestamp with time zone:NO'
          ] THEN
            RAISE EXCEPTION 'dispatch.assignments columns do not match AI-06';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='dispatch.assignments'::regclass AND contype='p'
              AND pg_get_constraintdef(oid)='PRIMARY KEY (id)'
          ) THEN
            RAISE EXCEPTION 'canonical assignment primary key is missing';
          END IF;

          SELECT count(*) INTO assignment_check_count
          FROM pg_constraint
          WHERE conrelid='dispatch.assignments'::regclass AND contype='c'
            AND pg_get_constraintdef(oid) LIKE '%assignment_type%'
            AND pg_get_constraintdef(oid) LIKE '%OWN%'
            AND pg_get_constraintdef(oid) LIKE '%EXTERNAL%'
            AND pg_get_constraintdef(oid) LIKE '%ALLY_CAPACITY%';
          SELECT count(*) INTO status_check_count
          FROM pg_constraint
          WHERE conrelid='dispatch.assignments'::regclass AND contype='c'
            AND pg_get_constraintdef(oid) LIKE '%status%'
            AND pg_get_constraintdef(oid) LIKE '%OFFERED%'
            AND pg_get_constraintdef(oid) LIKE '%ACCEPTED%'
            AND pg_get_constraintdef(oid) LIKE '%ACTIVE%'
            AND pg_get_constraintdef(oid) LIKE '%COMPLETED%'
            AND pg_get_constraintdef(oid) LIKE '%CANCELLED%';
          IF assignment_check_count <> 1 OR status_check_count <> 1 THEN
            RAISE EXCEPTION 'canonical assignment enum checks are missing';
          END IF;

          SELECT count(*) INTO canonical_foreign_keys
          FROM pg_constraint
          WHERE conrelid='dispatch.assignments'::regclass AND contype='f'
            AND confrelid IN (
              'orders.orders'::regclass,
              'organizations.organizations'::regclass,
              'drivers.driver_profiles'::regclass,
              'routes.routes'::regclass
            );
          IF canonical_foreign_keys <> 5 THEN
            RAISE EXCEPTION 'canonical assignment foreign keys are missing';
          END IF;

          SELECT pg_get_indexdef(indexrelid) INTO canonical_index
          FROM pg_index
          WHERE indexrelid=to_regclass('dispatch.one_active_assignment_per_order');
          IF canonical_index IS NULL
             OR canonical_index NOT LIKE '%UNIQUE INDEX one_active_assignment_per_order%'
             OR canonical_index NOT LIKE '%(order_id)%'
             OR canonical_index NOT LIKE '%status = ANY (ARRAY[''ACCEPTED''::text, ''ACTIVE''::text])%' THEN
            RAISE EXCEPTION 'canonical active assignment index is missing or inconsistent';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='dispatch.assignments'::regclass
              AND relrowsecurity AND relforcerowsecurity
          ) OR NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname='dispatch' AND tablename='assignments'
              AND policyname='assignments_tenant'
              AND cmd='ALL'
              AND qual LIKE '%app_allowed_org(owner_org_id)%'
              AND qual LIKE '%app_allowed_org(operator_org_id)%'
              AND with_check LIKE '%app_allowed_org(owner_org_id)%'
              AND with_check LIKE '%app_allowed_org(operator_org_id)%'
          ) THEN
            RAISE EXCEPTION 'canonical assignment RLS configuration is missing';
          END IF;
        END
        $adoption$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
