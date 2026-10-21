using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Locations.Infrastructure.Persistence.Migrations;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (project owner: E4 "Los 4 del plan (Recommended)", E4-P2 "Llave propia para avisos
/// (Recommended)" and E4-P17 "Dos copias, como el teléfono (Recommended)"; AI-08 NTF-002; AI-24
/// <c>recipient_notices.contact</c>). <c>locations.locations</c> gains four nullable columns:
/// <list type="bullet">
/// <item><c>email_ciphertext</c> (<c>bytea</c>): the optional delivery recipient e-mail, sealed with the ADP-001 envelope
/// under the PII key and the row's <c>pii_key_version</c> (purpose <c>locations.email</c>).</item>
/// <item><c>notice_phone_ciphertext</c> and <c>notice_email_ciphertext</c> (<c>bytea</c>) and <c>notice_key_version</c>
/// (<c>text</c>): copies of the destination phone and e-mail under the recipient-notice key (purposes
/// <c>locations.notice_phone</c> and <c>locations.notice_email</c>), the only contact values the Worker can open.</item>
/// </list>
/// Two checks: <c>locations_notice_key_version_check</c> (the notice key version is present exactly when a notice copy
/// is) and <c>locations_notice_email_check</c> (a notice copy of the e-mail needs the e-mail). No row is rewritten:
/// every existing location reads as one without an e-mail or a notice copy. The table keeps its FORCE RLS tenant policy
/// and its runtime privileges (the Worker already reads it under RLS, AI-18 declared contract step 4).
/// <para>
/// AI-06 declares the same DDL (<see cref="CanonicalDdl"/>, byte for byte) on fresh installations; this step adopts it
/// there, refusing any other shape, and runs it on installations whose baseline predates the decision.
/// </para>
/// <para>
/// The rollback refuses while any of the four columns holds a value anywhere (FORCE RLS hides rows from the migrator,
/// so the guard is a constraint: validating it reads every row); otherwise it drops exactly the two checks and the four
/// columns. Recipient notices are rolled back with their switches instead (AI-24 <c>recipient_notices.rollback</c>).
/// </para>
/// </summary>
[DbContext(typeof(LocationsDbContext))]
[Migration(MigrationId)]
public sealed class AddRecipientNoticeContact : Migration
{
    public const string MigrationId = "20261020000100_AddRecipientNoticeContact";
    public const string DecisionId = "NTF-RECIPIENT-NOTICES-2026-10-20";
    public const string DowngradeBlockedMessage = "NTF002_DOWNGRADE_BLOCKED_DATA_PRESENT";
    public const string NoticeKeyVersionCheckName = "locations_notice_key_version_check";
    public const string NoticeEmailCheckName = "locations_notice_email_check";

    /// <summary>The four columns, in the order AI-06 adds them.</summary>
    public static IReadOnlyList<string> Columns { get; } = Array.AsReadOnly(new[]
    {
        "email_ciphertext",
        "notice_phone_ciphertext",
        "notice_email_ciphertext",
        "notice_key_version",
    });

    /// <summary>The notice key version check exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string NoticeKeyVersionCheckDefinition =
        "CHECK (((notice_key_version IS NULL) = ((notice_phone_ciphertext IS NULL) AND (notice_email_ciphertext IS NULL))))";

    /// <summary>The notice e-mail check exactly as <c>pg_get_constraintdef</c> renders it.</summary>
    public const string NoticeEmailCheckDefinition =
        "CHECK (((notice_email_ciphertext IS NULL) OR (email_ciphertext IS NOT NULL)))";

    /// <summary>The DDL AI-06 carries for fresh installations; the Up runs exactly this text when the columns are absent.</summary>
    public const string CanonicalDdl =
        """
        ALTER TABLE locations.locations
          ADD COLUMN email_ciphertext bytea,
          ADD COLUMN notice_phone_ciphertext bytea,
          ADD COLUMN notice_email_ciphertext bytea,
          ADD COLUMN notice_key_version text,
          ADD CONSTRAINT locations_notice_key_version_check
            CHECK ((notice_key_version IS NULL) = (notice_phone_ciphertext IS NULL AND notice_email_ciphertext IS NULL)),
          ADD CONSTRAINT locations_notice_email_check
            CHECK (notice_email_ciphertext IS NULL OR email_ciphertext IS NOT NULL);
        """;

    private const string UpTemplate =
        """
        DO $ntf002_recipient_contact$
        DECLARE
          v_present integer;
        BEGIN
          IF to_regclass('locations.locations') IS NULL THEN
            RAISE EXCEPTION 'NTF-002 requires the canonical AI-06 locations.locations table';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='locations.locations'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'NTF-002 requires FORCE ROW LEVEL SECURITY on locations.locations';
          END IF;

          SELECT count(*) INTO v_present
          FROM information_schema.columns
          WHERE table_schema='locations' AND table_name='locations'
            AND column_name IN ('email_ciphertext','notice_phone_ciphertext','notice_email_ciphertext','notice_key_version');
          IF v_present = 0 THEN
        {{CanonicalDdl}}
          ELSIF v_present <> 4 THEN
            RAISE EXCEPTION 'locations.locations recipient contact columns are partially present';
          END IF;
        END
        $ntf002_recipient_contact$;

        DO $ntf002_verify$
        BEGIN
          IF (SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
                ORDER BY column_name)
              FROM information_schema.columns
              WHERE table_schema='locations' AND table_name='locations'
                AND column_name IN ('email_ciphertext','notice_phone_ciphertext','notice_email_ciphertext','notice_key_version'))
             IS DISTINCT FROM ARRAY[
               'email_ciphertext:bytea:YES:',
               'notice_email_ciphertext:bytea:YES:',
               'notice_key_version:text:YES:',
               'notice_phone_ciphertext:bytea:YES:'
             ]
          THEN
            RAISE EXCEPTION 'locations.locations recipient contact columns differ from the NTF-002 contract';
          END IF;

          IF (SELECT array_agg(conname || ':' || pg_get_constraintdef(oid) || ':' || convalidated::text ORDER BY conname)
              FROM pg_constraint
              WHERE conrelid='locations.locations'::regclass AND contype='c'
                AND conname IN ('locations_notice_email_check','locations_notice_key_version_check'))
             IS DISTINCT FROM ARRAY[
               'locations_notice_email_check:{{NoticeEmailCheck}}:true',
               'locations_notice_key_version_check:{{NoticeKeyVersionCheck}}:true'
             ]
          THEN
            RAISE EXCEPTION 'locations.locations recipient contact checks differ from the NTF-002 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='locations.locations'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'NTF-002 requires FORCE ROW LEVEL SECURITY on locations.locations';
          END IF;
        END
        $ntf002_verify$;
        """;

    /// <summary>
    /// Rollback. It refuses while any location holds an e-mail or a notice copy: they are personal data a recipient
    /// gave, so they are never erased silently (GATE-007 retention and ARCO decide their end). On an installation
    /// without such data it drops exactly what <see cref="CanonicalDdl"/> added.
    /// </summary>
    public const string DownSql =
        """
        DO $ntf002_downgrade$
        BEGIN
          ALTER TABLE locations.locations
            ADD CONSTRAINT locations_recipient_contact_downgrade_guard CHECK (
              email_ciphertext IS NULL AND notice_phone_ciphertext IS NULL
              AND notice_email_ciphertext IS NULL AND notice_key_version IS NULL);
        EXCEPTION WHEN check_violation THEN
          RAISE EXCEPTION USING
            MESSAGE = 'NTF002_DOWNGRADE_BLOCKED_DATA_PRESENT',
            DETAIL = 'Locations hold a recipient e-mail or a recipient-notice copy; NTF-RECIPIENT-NOTICES-2026-10-20 is rolled back with Locations:NoticeKeyProtector=Disabled and the recipient notice switches instead.',
            ERRCODE = 'P0001';
        END
        $ntf002_downgrade$;

        ALTER TABLE locations.locations
          DROP CONSTRAINT locations_recipient_contact_downgrade_guard,
          DROP CONSTRAINT locations_notice_email_check,
          DROP CONSTRAINT locations_notice_key_version_check,
          DROP COLUMN email_ciphertext,
          DROP COLUMN notice_phone_ciphertext,
          DROP COLUMN notice_email_ciphertext,
          DROP COLUMN notice_key_version;
        """;

    /// <summary>The Up SQL with the canonical DDL and the expected catalog filled in, exactly as the migration runs it.</summary>
    public static string RenderUpSql() => UpTemplate
        .Replace("{{CanonicalDdl}}", CanonicalDdl.TrimEnd('\n'), StringComparison.Ordinal)
        .Replace("{{NoticeEmailCheck}}", NoticeEmailCheckDefinition.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal)
        .Replace(
            "{{NoticeKeyVersionCheck}}",
            NoticeKeyVersionCheckDefinition.Replace("'", "''", StringComparison.Ordinal),
            StringComparison.Ordinal);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RenderUpSql());

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
