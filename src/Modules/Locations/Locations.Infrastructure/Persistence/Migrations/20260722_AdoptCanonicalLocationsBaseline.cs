using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Locations.Infrastructure.Persistence.Migrations;

[DbContext(typeof(LocationsDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalLocationsBaseline : Migration
{
    public const string MigrationId = "20260722_AdoptCanonicalLocationsBaseline";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $adoption$
        BEGIN
          IF to_regclass('locations.cities') IS NULL
             OR to_regclass('locations.service_areas') IS NULL
             OR to_regclass('locations.operating_zones') IS NULL
             OR to_regclass('locations.locations') IS NULL THEN
            RAISE EXCEPTION 'GEO-001 adoption requires canonical location tables from AI-06';
          END IF;
          IF NOT EXISTS (
            SELECT 1 FROM geometry_columns WHERE f_table_schema='locations' AND f_table_name='service_areas'
              AND f_geometry_column='polygon' AND type='MULTIPOLYGON' AND srid=4326
          ) OR NOT EXISTS (
            SELECT 1 FROM geometry_columns WHERE f_table_schema='locations' AND f_table_name='operating_zones'
              AND f_geometry_column='polygon' AND type='MULTIPOLYGON' AND srid=4326
          ) OR NOT EXISTS (
            SELECT 1 FROM geometry_columns WHERE f_table_schema='locations' AND f_table_name='locations'
              AND f_geometry_column='point' AND type='POINT' AND srid=4326
          ) THEN
            RAISE EXCEPTION 'canonical location geometries do not match AI-06';
          END IF;
          IF to_regclass('locations.service_areas_polygon_gix') IS NULL
             OR to_regclass('locations.operating_zones_polygon_gix') IS NULL
             OR to_regclass('locations.locations_point_gix') IS NULL THEN
            RAISE EXCEPTION 'canonical location GiST indexes are missing';
          END IF;
        END
        $adoption$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
