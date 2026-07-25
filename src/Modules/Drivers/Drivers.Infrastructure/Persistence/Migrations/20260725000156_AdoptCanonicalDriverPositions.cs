using Microsoft.EntityFrameworkCore.Migrations;

namespace Drivers.Infrastructure.Persistence.Migrations;

public partial class AdoptCanonicalDriverPositions : Migration
{
    public const string MigrationId = "20260725000156_AdoptCanonicalDriverPositions";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $adoption$
        DECLARE
          actual_columns text[];
        BEGIN
          IF to_regclass('drivers.driver_positions') IS NULL THEN
            RAISE EXCEPTION 'DRV-003 adoption requires canonical drivers.driver_positions from AI-06';
          END IF;

          SELECT array_agg(
                   a.attname || ':' || format_type(a.atttypid,a.atttypmod) ||
                   ':' || CASE WHEN a.attnotnull THEN 'not-null' ELSE 'nullable' END
                   ORDER BY a.attnum)
          INTO actual_columns
          FROM pg_attribute a
          WHERE a.attrelid='drivers.driver_positions'::regclass
            AND a.attnum > 0
            AND NOT a.attisdropped;
          IF actual_columns <> ARRAY[
            'id:uuid:not-null',
            'driver_id:uuid:not-null',
            'org_id:uuid:not-null',
            'city_id:uuid:not-null',
            'client_event_id:uuid:not-null',
            'point:geometry(Point,4326):not-null',
            'accuracy_m:numeric(8,2):not-null',
            'heading_degrees:numeric(6,2):nullable',
            'speed_mps:numeric(8,2):nullable',
            'captured_at:timestamp with time zone:not-null',
            'received_at:timestamp with time zone:not-null',
            'publish_realtime:boolean:not-null'] THEN
            RAISE EXCEPTION 'canonical driver_positions columns or types differ from AI-06';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='drivers.driver_positions'::regclass AND contype='p'
              AND pg_get_constraintdef(oid)='PRIMARY KEY (id)'
          ) OR NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='drivers.driver_positions'::regclass AND contype='u'
              AND pg_get_constraintdef(oid)='UNIQUE (driver_id, client_event_id)'
          ) THEN
            RAISE EXCEPTION 'canonical driver_positions primary or deduplication key is missing';
          END IF;

          IF (
            SELECT count(*)
            FROM pg_constraint
            WHERE conrelid='drivers.driver_positions'::regclass
              AND contype='f'
              AND confrelid IN (
                'drivers.driver_profiles'::regclass,
                'organizations.organizations'::regclass,
                'locations.cities'::regclass)
          ) <> 3 THEN
            RAISE EXCEPTION 'canonical driver_positions foreign keys differ from AI-06';
          END IF;

          IF to_regclass('drivers.driver_positions_tenant_time_idx') IS NULL
             OR to_regclass('drivers.driver_positions_captured_brin') IS NULL
             OR to_regclass('drivers.driver_positions_point_gix') IS NULL THEN
            RAISE EXCEPTION 'canonical driver_positions indexes are missing';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='drivers.driver_positions'::regclass
              AND relrowsecurity AND relforcerowsecurity
          ) OR NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname='drivers'
              AND tablename='driver_positions'
              AND policyname='driver_positions_tenant'
          ) THEN
            RAISE EXCEPTION 'canonical driver_positions RLS contract is missing';
          END IF;

          IF Find_SRID('drivers','driver_positions','point') <> 4326 THEN
            RAISE EXCEPTION 'canonical driver_positions point SRID must be 4326';
          END IF;
        END
        $adoption$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
