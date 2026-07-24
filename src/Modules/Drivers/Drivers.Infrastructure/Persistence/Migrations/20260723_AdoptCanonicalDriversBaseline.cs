using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Drivers.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DriversDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalDriversBaseline : Migration
{
    public const string MigrationId = "20260723_AdoptCanonicalDriversBaseline";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $adoption$
        BEGIN
          IF to_regclass('drivers.driver_profiles') IS NULL
             OR to_regclass('drivers.driver_service_areas') IS NULL
             OR to_regclass('drivers.driver_documents') IS NULL THEN
            RAISE EXCEPTION 'DSP-001 adoption requires canonical driver tables from AI-06';
          END IF;
          IF to_regclass('drivers.driver_documents_eligibility_idx') IS NULL THEN
            RAISE EXCEPTION 'canonical driver document eligibility index is missing';
          END IF;
          IF EXISTS (
            SELECT 1
            FROM (VALUES
              ('driver_profiles','driver_profiles_tenant'),
              ('driver_service_areas','driver_service_areas_tenant'),
              ('driver_documents','driver_documents_tenant')
            ) expected(table_name,policy_name)
            WHERE NOT EXISTS (
              SELECT 1 FROM pg_policies
              WHERE schemaname='drivers'
                AND tablename=expected.table_name
                AND policyname=expected.policy_name
            )
          ) THEN
            RAISE EXCEPTION 'canonical driver RLS policies are missing';
          END IF;
        END
        $adoption$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
