using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drivers.Infrastructure.Persistence.Migrations;

/// <summary>
/// VOICE-001-MASKED-CALLS-2026-10-11 (Drivers lane, forward only by default). Two Drivers-owned objects:
/// <list type="bullet">
/// <item><c>drivers.driver_profiles</c> gains the driver's own mobile number for the masked bridge, protected with the
/// ADP-001 envelope: <c>phone_ciphertext</c>, <c>phone_pii_key_version</c>, <c>phone_consent_version</c> and
/// <c>phone_consented_at</c>, all nullable and set or cleared together (<c>driver_profiles_phone_check</c>). No
/// existing row is rewritten: every driver starts without a number.</item>
/// <item><c>drivers.recipient_call_requests</c>: one row per call a driver asked for (who, which order and
/// assignment, provider, status, result code, the provider call id and the final call status and duration). It holds
/// no phone number. FORCE RLS with a tenant policy on <c>org_id</c> (the driver's organization); the Worker has no
/// privilege and the API cannot delete. There is no foreign key into Orders or Dispatch: the order and assignment are
/// verified under RLS when the row is written, so the Drivers schema does not depend on theirs.</item>
/// </list>
/// AI-06 does not carry these objects (lane-owned, like the NTF-001 tables). The lane runs as
/// <c>paqueteria_migrator</c>, so the table inherits the module default privileges before the narrowing REVOKEs.
/// </summary>
[DbContext(typeof(DriversDbContext))]
[Migration(MigrationId)]
public sealed class AddDriverVoiceBridge : Migration
{
    public const string MigrationId = "20261011000100_AddDriverVoiceBridge";
    public const string DecisionId = "VOICE-001-MASKED-CALLS-2026-10-11";
    public const string PhoneCheckName = "driver_profiles_phone_check";
    public const string CallRequestsTable = "drivers.recipient_call_requests";
    public const string DowngradeBlockedMessage = "VOICE001_DOWNGRADE_BLOCKED_DATA_PRESENT";

    /// <summary>The phone check exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string PhoneCheckDefinition =
        "CHECK ((((phone_ciphertext IS NULL) = (phone_pii_key_version IS NULL)) AND ((phone_ciphertext IS NULL) = (phone_consent_version IS NULL)) AND ((phone_ciphertext IS NULL) = (phone_consented_at IS NULL)) AND ((phone_ciphertext IS NULL) OR ((octet_length(phone_ciphertext) >= 16) AND (octet_length(phone_ciphertext) <= 4096))) AND ((phone_pii_key_version IS NULL) OR (phone_pii_key_version ~ '^[!-~]{1,200}$'::text)) AND ((phone_consent_version IS NULL) OR (phone_consent_version ~ '^[A-Z0-9][A-Z0-9-]{2,63}$'::text))))";

    /// <summary>The table's columns as <c>attname:type:nullability</c>, in order.</summary>
    public static IReadOnlyList<string> CallRequestColumns { get; } = Array.AsReadOnly(new[]
    {
        "id:uuid:not-null",
        "org_id:uuid:not-null",
        "driver_id:uuid:not-null",
        "requested_by:uuid:not-null",
        "order_id:uuid:not-null",
        "assignment_id:uuid:not-null",
        "provider:text:not-null",
        "status:text:not-null",
        "result_code:text:not-null",
        "provider_call_sid:text:nullable",
        "provider_call_status:text:nullable",
        "call_duration_seconds:integer:nullable",
        "requested_at:timestamp with time zone:not-null",
        "completed_at:timestamp with time zone:nullable",
        "status_reported_at:timestamp with time zone:nullable",
    });

    public const string UpSql =
        """
        DO $adoption$
        BEGIN
          IF to_regclass('drivers.driver_profiles') IS NULL THEN
            RAISE EXCEPTION 'VOICE-001 requires the canonical AI-06 drivers.driver_profiles table';
          END IF;

          IF (SELECT count(*) FROM information_schema.columns
              WHERE table_schema='drivers' AND table_name='driver_profiles'
                AND column_name IN ('phone_ciphertext','phone_pii_key_version','phone_consent_version','phone_consented_at'))
             NOT IN (0,4)
          THEN
            RAISE EXCEPTION 'drivers.driver_profiles phone columns are partially present';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='drivers.driver_profiles'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'VOICE-001 requires FORCE ROW LEVEL SECURITY on drivers.driver_profiles';
          END IF;
        END
        $adoption$;

        ALTER TABLE drivers.driver_profiles
          ADD COLUMN IF NOT EXISTS phone_ciphertext bytea,
          ADD COLUMN IF NOT EXISTS phone_pii_key_version text,
          ADD COLUMN IF NOT EXISTS phone_consent_version text,
          ADD COLUMN IF NOT EXISTS phone_consented_at timestamptz;

        DO $phone_check$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid='drivers.driver_profiles'::regclass AND conname='driver_profiles_phone_check'
          ) THEN
            ALTER TABLE drivers.driver_profiles ADD CONSTRAINT driver_profiles_phone_check CHECK (
              (phone_ciphertext IS NULL) = (phone_pii_key_version IS NULL)
              AND (phone_ciphertext IS NULL) = (phone_consent_version IS NULL)
              AND (phone_ciphertext IS NULL) = (phone_consented_at IS NULL)
              AND (phone_ciphertext IS NULL OR octet_length(phone_ciphertext) BETWEEN 16 AND 4096)
              AND (phone_pii_key_version IS NULL OR phone_pii_key_version ~ '^[!-~]{1,200}$')
              AND (phone_consent_version IS NULL OR phone_consent_version ~ '^[A-Z0-9][A-Z0-9-]{2,63}$'));
          END IF;
        END
        $phone_check$;

        CREATE TABLE IF NOT EXISTS drivers.recipient_call_requests (
          id uuid PRIMARY KEY,
          org_id uuid NOT NULL REFERENCES organizations.organizations(id),
          driver_id uuid NOT NULL REFERENCES drivers.driver_profiles(id),
          requested_by uuid NOT NULL REFERENCES identity.users(id),
          order_id uuid NOT NULL,
          assignment_id uuid NOT NULL,
          provider text NOT NULL,
          status text NOT NULL,
          result_code text NOT NULL,
          provider_call_sid text,
          provider_call_status text,
          call_duration_seconds integer,
          requested_at timestamptz NOT NULL,
          completed_at timestamptz,
          status_reported_at timestamptz,
          CONSTRAINT recipient_call_requests_provider_check CHECK (provider IN ('TWILIO','SYNTHETIC')),
          CONSTRAINT recipient_call_requests_status_check
            CHECK (status IN ('REQUESTED','PLACED','UNCONFIRMED','FAILED')),
          CONSTRAINT recipient_call_requests_result_code_check CHECK (result_code ~ '^[A-Z][A-Z0-9_]{2,63}$'),
          CONSTRAINT recipient_call_requests_call_sid_check
            CHECK (provider_call_sid IS NULL OR provider_call_sid ~ '^[A-Za-z0-9]{2,64}$'),
          CONSTRAINT recipient_call_requests_call_status_check CHECK (provider_call_status IS NULL OR provider_call_status IN
            ('queued','initiated','ringing','in-progress','completed','busy','no-answer','canceled','failed')),
          CONSTRAINT recipient_call_requests_duration_check
            CHECK (call_duration_seconds IS NULL OR call_duration_seconds BETWEEN 0 AND 86400),
          CONSTRAINT recipient_call_requests_completion_check CHECK ((status='REQUESTED') = (completed_at IS NULL))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS recipient_call_requests_call_sid_key
          ON drivers.recipient_call_requests(provider_call_sid) WHERE provider_call_sid IS NOT NULL;
        CREATE INDEX IF NOT EXISTS recipient_call_requests_driver_idx
          ON drivers.recipient_call_requests(org_id,driver_id,requested_at DESC);
        CREATE INDEX IF NOT EXISTS recipient_call_requests_order_idx
          ON drivers.recipient_call_requests(org_id,driver_id,order_id,requested_at DESC);
        ALTER TABLE drivers.recipient_call_requests ENABLE ROW LEVEL SECURITY;
        ALTER TABLE drivers.recipient_call_requests FORCE ROW LEVEL SECURITY;

        DO $policy$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_policies
            WHERE schemaname='drivers' AND tablename='recipient_call_requests'
              AND policyname='recipient_call_requests_tenant'
          ) THEN
            CREATE POLICY recipient_call_requests_tenant ON drivers.recipient_call_requests
              USING (security.app_allowed_org(org_id)) WITH CHECK (security.app_allowed_org(org_id));
          END IF;
        END
        $policy$;

        -- The API reads, inserts and updates its own tenant's rows; nobody deletes them at runtime and the Worker
        -- never touches them.
        REVOKE DELETE,TRUNCATE ON drivers.recipient_call_requests FROM paqueteria_app;
        REVOKE ALL ON drivers.recipient_call_requests FROM paqueteria_worker;

        DO $verify$
        BEGIN
          IF (SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
                ORDER BY column_name)
              FROM information_schema.columns
              WHERE table_schema='drivers' AND table_name='driver_profiles'
                AND column_name IN ('phone_ciphertext','phone_pii_key_version','phone_consent_version','phone_consented_at'))
             IS DISTINCT FROM ARRAY[
               'phone_ciphertext:bytea:YES:',
               'phone_consent_version:text:YES:',
               'phone_consented_at:timestamp with time zone:YES:',
               'phone_pii_key_version:text:YES:'
             ]
          THEN
            RAISE EXCEPTION 'drivers.driver_profiles phone columns differ from the VOICE-001 contract';
          END IF;

          IF (SELECT array_agg(pg_get_constraintdef(oid) || ':' || convalidated::text)
              FROM pg_constraint
              WHERE conrelid='drivers.driver_profiles'::regclass AND conname='driver_profiles_phone_check' AND contype='c')
             IS DISTINCT FROM ARRAY['{{PhoneCheck}}:true']
          THEN
            RAISE EXCEPTION 'drivers.driver_profiles phone check differs from the VOICE-001 contract';
          END IF;

          IF (SELECT array_agg(a.attname || ':' || format_type(a.atttypid,a.atttypmod) || ':' ||
                CASE WHEN a.attnotnull THEN 'not-null' ELSE 'nullable' END ORDER BY a.attnum)
              FROM pg_attribute a
              WHERE a.attrelid='drivers.recipient_call_requests'::regclass AND a.attnum > 0 AND NOT a.attisdropped)
             IS DISTINCT FROM ARRAY[{{CallRequestColumns}}]
          THEN
            RAISE EXCEPTION 'drivers.recipient_call_requests differs from the VOICE-001 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='drivers.recipient_call_requests'::regclass AND relrowsecurity AND relforcerowsecurity
              AND pg_get_userbyid(relowner)='paqueteria_migrator'
          ) THEN
            RAISE EXCEPTION 'drivers.recipient_call_requests must FORCE ROW LEVEL SECURITY and belong to paqueteria_migrator';
          END IF;

          IF has_table_privilege('paqueteria_worker','drivers.recipient_call_requests','SELECT,INSERT,UPDATE,DELETE')
             OR has_table_privilege('paqueteria_app','drivers.recipient_call_requests','DELETE')
             OR NOT has_table_privilege('paqueteria_app','drivers.recipient_call_requests','SELECT')
             OR NOT has_table_privilege('paqueteria_app','drivers.recipient_call_requests','INSERT')
             OR NOT has_table_privilege('paqueteria_app','drivers.recipient_call_requests','UPDATE')
          THEN
            RAISE EXCEPTION 'drivers.recipient_call_requests runtime privileges differ from the VOICE-001 contract';
          END IF;
        END
        $verify$;
        """;

    /// <summary>
    /// Rollback. It refuses while any call request or stored driver phone exists: both are records a person or the
    /// owner may still need (and the phones were given with consent), so they are never erased silently. FORCE RLS
    /// hides every row from the migrator, so the guard is a constraint: validating it reads every row. On an
    /// installation without such data it drops exactly what <see cref="UpSql"/> added.
    /// </summary>
    public const string DownSql =
        """
        DO $downgrade$
        BEGIN
          ALTER TABLE drivers.recipient_call_requests
            ADD CONSTRAINT recipient_call_requests_downgrade_guard CHECK (false);
          ALTER TABLE drivers.driver_profiles
            ADD CONSTRAINT driver_profiles_phone_downgrade_guard CHECK (phone_ciphertext IS NULL);
        EXCEPTION WHEN check_violation THEN
          RAISE EXCEPTION USING
            MESSAGE = 'VOICE001_DOWNGRADE_BLOCKED_DATA_PRESENT',
            DETAIL = 'Recipient call requests or driver phones exist; VOICE-001 is rolled back with Voice:Provider=Disabled instead.',
            ERRCODE = 'P0001';
        END
        $downgrade$;

        DROP TABLE drivers.recipient_call_requests;
        ALTER TABLE drivers.driver_profiles
          DROP CONSTRAINT driver_profiles_phone_downgrade_guard,
          DROP CONSTRAINT driver_profiles_phone_check,
          DROP COLUMN phone_ciphertext,
          DROP COLUMN phone_pii_key_version,
          DROP COLUMN phone_consent_version,
          DROP COLUMN phone_consented_at;
        """;

    /// <summary>The Up SQL with the expected catalog filled in, exactly as the migration runs it.</summary>
    public static string RenderUpSql() => UpSql
        .Replace("{{PhoneCheck}}", PhoneCheckDefinition.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal)
        .Replace(
            "{{CallRequestColumns}}",
            string.Join(",", CallRequestColumns.Select(column => $"'{column}'")),
            StringComparison.Ordinal);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RenderUpSql());

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
