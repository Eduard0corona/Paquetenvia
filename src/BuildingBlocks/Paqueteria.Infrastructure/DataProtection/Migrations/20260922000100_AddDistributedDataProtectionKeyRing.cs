using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Paqueteria.Infrastructure.DataProtection.Migrations;

[DbContext(typeof(PlatformDataProtectionDbContext))]
[Migration(MigrationId)]
public sealed class AddDistributedDataProtectionKeyRing : Migration
{
    public const string MigrationId = "20260922000100_AddDistributedDataProtectionKeyRing";

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);

    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'SCL001_SCHEMA_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'Dropping the shared key ring invalidates every payload protected by any replica; roll the deployment back instead.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    public const string UpSql =
        """
        DO $guard$
        BEGIN
          IF to_regnamespace('platform') IS NULL THEN
            RAISE EXCEPTION 'SCL-001 requires the canonical AI-06 platform schema';
          END IF;
        END
        $guard$;

        CREATE TABLE IF NOT EXISTS platform.data_protection_keys(
          id uuid PRIMARY KEY DEFAULT pg_catalog.gen_random_uuid(),
          application_name text NOT NULL,
          friendly_name text NOT NULL,
          xml text NOT NULL,
          created_at timestamptz NOT NULL DEFAULT pg_catalog.clock_timestamp(),
          CONSTRAINT data_protection_keys_application_name_bounded
            CHECK (pg_catalog.char_length(application_name) BETWEEN 3 AND 100),
          CONSTRAINT data_protection_keys_friendly_name_bounded
            CHECK (pg_catalog.char_length(friendly_name) BETWEEN 1 AND 200),
          CONSTRAINT data_protection_keys_xml_bounded
            CHECK (pg_catalog.char_length(xml) BETWEEN 1 AND 65536)
        );

        ALTER TABLE platform.data_protection_keys OWNER TO paqueteria_migrator;

        CREATE INDEX IF NOT EXISTS data_protection_keys_ring_idx
          ON platform.data_protection_keys(application_name, created_at, id);

        -- The key ring is platform infrastructure rather than tenant data, so the row policy is
        -- open and the reachable surface is governed by the grants below. RLS stays ENABLED and
        -- FORCED so the table satisfies the canonical DBA-001 protection assertion.
        ALTER TABLE platform.data_protection_keys ENABLE ROW LEVEL SECURITY;
        ALTER TABLE platform.data_protection_keys FORCE ROW LEVEL SECURITY;
        DROP POLICY IF EXISTS data_protection_keys_runtime ON platform.data_protection_keys;
        CREATE POLICY data_protection_keys_runtime ON platform.data_protection_keys
          USING (true) WITH CHECK (true);

        -- The key ring is append-only at runtime: replicas may read every key and publish new
        -- ones, but may never rewrite or revoke another replica's key material.
        REVOKE ALL ON platform.data_protection_keys FROM PUBLIC;
        GRANT SELECT, INSERT ON platform.data_protection_keys TO paqueteria_app;
        GRANT SELECT, INSERT ON platform.data_protection_keys TO paqueteria_worker;

        CREATE OR REPLACE FUNCTION platform.reject_data_protection_key_mutation() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $fn$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'SCL001_KEY_RING_IS_APPEND_ONLY',
            ERRCODE = 'P0001';
        END
        $fn$;
        ALTER FUNCTION platform.reject_data_protection_key_mutation() OWNER TO paqueteria_migrator;

        DROP TRIGGER IF EXISTS data_protection_keys_append_only ON platform.data_protection_keys;
        CREATE TRIGGER data_protection_keys_append_only
          BEFORE UPDATE OR DELETE ON platform.data_protection_keys
          FOR EACH ROW EXECUTE FUNCTION platform.reject_data_protection_key_mutation();
        """;
}
