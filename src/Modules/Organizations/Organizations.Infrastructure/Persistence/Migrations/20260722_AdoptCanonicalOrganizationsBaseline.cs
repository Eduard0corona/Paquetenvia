using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Organizations.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrganizationsDbContext))]
[Migration(MigrationId)]
public sealed class AdoptCanonicalOrganizationsBaseline : Migration
{
    public const string MigrationId = "20260722_AdoptCanonicalOrganizationsBaseline";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $adoption$
        BEGIN
          IF to_regclass('organizations.organizations') IS NULL
             OR to_regclass('organizations.organization_memberships') IS NULL THEN
            RAISE EXCEPTION 'TEN-001 adoption requires canonical organization tables from AI-06';
          END IF;
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organizations' AND column_name='organization_type' AND data_type='text' AND is_nullable='NO'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organizations' AND column_name='status' AND data_type='text' AND is_nullable='NO'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organization_memberships' AND column_name='user_id' AND data_type='uuid' AND is_nullable='NO'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organization_memberships' AND column_name='organization_id' AND data_type='uuid' AND is_nullable='NO'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organization_memberships' AND column_name='role' AND data_type='text' AND is_nullable='NO'
          ) OR NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organization_memberships' AND column_name='status' AND data_type='text' AND is_nullable='NO'
          ) THEN
            RAISE EXCEPTION 'canonical organization tables do not match AI-06';
          END IF;
          IF to_regclass('organizations.organization_memberships_active_role_uq') IS NULL
             OR to_regclass('organizations.organization_memberships_one_default_uq') IS NULL THEN
            RAISE EXCEPTION 'canonical partial membership indexes are missing';
          END IF;
        END
        $adoption$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Adoption is intentionally non-destructive. Canonical AI-06 objects are never dropped here.
    }
}
