using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dispatch.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DispatchDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalDispatchAssignmentsBaseline : Migration
{
    public const string MigrationId = "20260723_AdoptCanonicalDispatchAssignmentsBaseline";

    public const string AdoptionSql =
        """
        DO $adoption$
        DECLARE
          actual_columns text[];
          assignment_check_count integer;
          status_check_count integer;
          cost_check_count integer;
          canonical_cost_check_count integer;
          canonical_foreign_keys integer;
          matching_foreign_key_count integer;
          canonical_index_count integer;
          assignment_policy_count integer;
          total_assignment_policy_count integer;
          expected_foreign_key record;
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
          FROM pg_constraint constraint_row
          JOIN pg_attribute column_row
            ON column_row.attrelid=constraint_row.conrelid
           AND column_row.attname='assignment_type'
          WHERE constraint_row.conrelid='dispatch.assignments'::regclass
            AND constraint_row.contype='c'
            AND constraint_row.convalidated
            AND constraint_row.conkey=ARRAY[column_row.attnum]::smallint[]
            AND pg_get_constraintdef(constraint_row.oid)=
              'CHECK ((assignment_type = ANY (ARRAY[''OWN''::text, ''EXTERNAL''::text, ''ALLY_CAPACITY''::text])))';
          SELECT count(*) INTO status_check_count
          FROM pg_constraint constraint_row
          JOIN pg_attribute column_row
            ON column_row.attrelid=constraint_row.conrelid
           AND column_row.attname='status'
          WHERE constraint_row.conrelid='dispatch.assignments'::regclass
            AND constraint_row.contype='c'
            AND constraint_row.convalidated
            AND constraint_row.conkey=ARRAY[column_row.attnum]::smallint[]
            AND pg_get_constraintdef(constraint_row.oid)=
              'CHECK ((status = ANY (ARRAY[''OFFERED''::text, ''ACCEPTED''::text, ''ACTIVE''::text, ''COMPLETED''::text, ''CANCELLED''::text])))';
          IF assignment_check_count <> 1 OR status_check_count <> 1 THEN
            RAISE EXCEPTION 'canonical assignment enum checks are missing or inconsistent';
          END IF;

          SELECT
            count(*),
            count(*) FILTER (
              WHERE constraint_row.convalidated
                AND pg_get_constraintdef(constraint_row.oid)='CHECK ((cost_cents >= 0))')
          INTO cost_check_count, canonical_cost_check_count
          FROM pg_constraint constraint_row
          JOIN pg_attribute column_row
            ON column_row.attrelid=constraint_row.conrelid
           AND column_row.attname='cost_cents'
          WHERE constraint_row.conrelid='dispatch.assignments'::regclass
            AND constraint_row.contype='c'
            AND constraint_row.conkey=ARRAY[column_row.attnum]::smallint[];
          IF cost_check_count <> 1 OR canonical_cost_check_count <> 1 THEN
            RAISE EXCEPTION 'canonical cost_cents non-negative check is missing or inconsistent';
          END IF;

          SELECT count(*) INTO canonical_foreign_keys
          FROM pg_constraint
          WHERE conrelid='dispatch.assignments'::regclass AND contype='f';
          IF canonical_foreign_keys <> 5 THEN
            RAISE EXCEPTION 'dispatch.assignments must have exactly five canonical foreign keys';
          END IF;

          FOR expected_foreign_key IN
            SELECT *
            FROM (VALUES
              ('order_id', 'orders.orders', 'id'),
              ('owner_org_id', 'organizations.organizations', 'id'),
              ('operator_org_id', 'organizations.organizations', 'id'),
              ('driver_id', 'drivers.driver_profiles', 'id'),
              ('route_id', 'routes.routes', 'id')
            ) AS canonical(local_column, referenced_table, referenced_column)
          LOOP
            SELECT count(*) INTO matching_foreign_key_count
            FROM pg_constraint constraint_row
            JOIN pg_attribute local_column
              ON local_column.attrelid=constraint_row.conrelid
             AND local_column.attname=expected_foreign_key.local_column
            JOIN pg_attribute referenced_column
              ON referenced_column.attrelid=constraint_row.confrelid
             AND referenced_column.attname=expected_foreign_key.referenced_column
            WHERE constraint_row.conrelid='dispatch.assignments'::regclass
              AND constraint_row.contype='f'
              AND constraint_row.conkey=ARRAY[local_column.attnum]::smallint[]
              AND constraint_row.confrelid=expected_foreign_key.referenced_table::regclass
              AND constraint_row.confkey=ARRAY[referenced_column.attnum]::smallint[]
              AND cardinality(constraint_row.conkey)=1
              AND cardinality(constraint_row.confkey)=1
              AND constraint_row.confupdtype='a'
              AND constraint_row.confdeltype='a'
              AND constraint_row.confmatchtype='s'
              AND NOT constraint_row.condeferrable
              AND NOT constraint_row.condeferred
              AND constraint_row.convalidated;
            IF matching_foreign_key_count <> 1 THEN
              RAISE EXCEPTION
                'canonical foreign key %.% -> %(%) is missing or inconsistent',
                'dispatch.assignments',
                expected_foreign_key.local_column,
                expected_foreign_key.referenced_table,
                expected_foreign_key.referenced_column;
            END IF;
          END LOOP;

          SELECT count(*) INTO canonical_index_count
          FROM pg_index index_row
          JOIN pg_class index_class ON index_class.oid=index_row.indexrelid
          JOIN pg_namespace index_namespace ON index_namespace.oid=index_class.relnamespace
          JOIN pg_attribute order_column
            ON order_column.attrelid=index_row.indrelid
           AND order_column.attname='order_id'
          WHERE index_row.indrelid='dispatch.assignments'::regclass
            AND index_namespace.nspname='dispatch'
            AND index_class.relname='one_active_assignment_per_order'
            AND index_row.indisunique
            AND index_row.indisvalid
            AND index_row.indisready
            AND index_row.indnkeyatts=1
            AND index_row.indnatts=1
            AND index_row.indkey[0]=order_column.attnum
            AND pg_get_expr(index_row.indpred,index_row.indrelid)=
              '(status = ANY (ARRAY[''ACCEPTED''::text, ''ACTIVE''::text]))';
          IF canonical_index_count <> 1 THEN
            RAISE EXCEPTION 'canonical active assignment index is missing or inconsistent';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='dispatch.assignments'::regclass
              AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'canonical assignment RLS configuration is missing';
          END IF;

          SELECT count(*) INTO total_assignment_policy_count
          FROM pg_policies
          WHERE schemaname='dispatch' AND tablename='assignments';
          SELECT count(*) INTO assignment_policy_count
          FROM pg_policies
            WHERE schemaname='dispatch' AND tablename='assignments'
              AND policyname='assignments_tenant'
              AND permissive='PERMISSIVE'
              AND roles=ARRAY['public'::name]
              AND cmd='ALL'
              AND regexp_replace(qual,'\s+','','g')=
                '(security.app_allowed_org(owner_org_id)ORsecurity.app_allowed_org(operator_org_id))'
              AND regexp_replace(with_check,'\s+','','g')=
                '(security.app_allowed_org(owner_org_id)ORsecurity.app_allowed_org(operator_org_id))';
          IF total_assignment_policy_count <> 1 OR assignment_policy_count <> 1 THEN
            RAISE EXCEPTION 'canonical assignments_tenant policy is missing or inconsistent';
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
