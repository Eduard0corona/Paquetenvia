using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Drivers.Infrastructure.Persistence.Migrations;

/// <summary>
/// DRV-DOC-001-IN-PERSON-2026-11-02 (project owner, round Y: Y1 "Despacho en persona (Recommended)", Y3 "No, solo datos
/// (Recommended)", Y5, Y7, Y10, Y11 and Y12; docs/adr/ADR-DRV-DOC-001-IN-PERSON-DRIVER-DOCUMENTS.md; AI-08 DRV-DOC-001).
/// <c>drivers.driver_documents</c> gains an explicit evidence kind and the review columns:
/// <list type="bullet">
/// <item><c>evidence_kind</c> (<c>FILE</c> by default, so every existing row keeps its meaning, or <c>IN_PERSON</c>):
/// <c>object_key</c> and <c>sha256</c> become nullable and <c>driver_documents_evidence_check</c> requires both for a
/// FILE row and neither for an IN_PERSON row; DSP-001 keeps its evidence guarantee per kind.</item>
/// <item><c>expiry_basis</c> (<c>PRINTED_EXPIRY</c>, or <c>PERMANENT_LICENSE_REVIEW</c> for a driver license only, with
/// an expiry always present), <c>rejection_code</c> (the six Y7 codes, only on a REJECTED row), <c>reviewed_by</c>,
/// <c>reviewed_at</c>, <c>revoked_by</c> and <c>revoked_at</c> (set in pairs; a revoked row is REVOKED).</item>
/// <item><c>driver_documents_in_person_check</c>: an IN_PERSON row was reviewed, is one of the four recordable types and
/// is VALID or REVOKED with an expiry and its basis, or REJECTED with a reason and no expiry.</item>
/// </list>
/// No row is rewritten and no column holds a number, a file, a key or a hash of an in-person document. The table keeps
/// its FORCE RLS tenant policy, its index and its runtime privileges (AI-18 unchanged).
/// <para>
/// AI-06 declares the same DDL (<see cref="CanonicalDdl"/>, byte for byte) on fresh installations; this step adopts it
/// there, refusing any other shape, and runs it on installations whose baseline predates the decision (the
/// AddRecipientNoticeContact precedent).
/// </para>
/// <para>
/// The rollback refuses while any row is IN_PERSON or carries a review, a reason or a revocation (FORCE RLS hides rows
/// from the migrator, so the guard is a constraint: validating it reads every row); otherwise it drops exactly the five
/// checks and the seven columns and restores <c>NOT NULL</c> on <c>object_key</c> and <c>sha256</c>. Documents are
/// switched off with <c>Drivers:Documents:Enabled=false</c> instead (AI-24 <c>driver_documents.rollback</c>).
/// </para>
/// </summary>
[DbContext(typeof(DriversDbContext))]
[Migration(MigrationId)]
public sealed class AddInPersonDriverDocuments : Migration
{
    public const string MigrationId = "20261102000100_AddInPersonDriverDocuments";
    public const string DecisionId = "DRV-DOC-001-IN-PERSON-2026-11-02";
    public const string DowngradeBlockedMessage = "DRVDOC001_DOWNGRADE_BLOCKED_DATA_PRESENT";

    /// <summary>The seven new columns, in the order AI-06 adds them.</summary>
    public static IReadOnlyList<string> NewColumns { get; } = Array.AsReadOnly(new[]
    {
        "evidence_kind",
        "expiry_basis",
        "rejection_code",
        "reviewed_by",
        "reviewed_at",
        "revoked_by",
        "revoked_at",
    });

    /// <summary>The new columns and the two that become nullable, as <c>name:type:nullable:default</c> ordered by name.</summary>
    public static IReadOnlyList<string> ContractColumns { get; } = Array.AsReadOnly(new[]
    {
        "evidence_kind:text:NO:'FILE'::text",
        "expiry_basis:text:YES:",
        "object_key:text:YES:",
        "rejection_code:text:YES:",
        "reviewed_at:timestamp with time zone:YES:",
        "reviewed_by:uuid:YES:",
        "revoked_at:timestamp with time zone:YES:",
        "revoked_by:uuid:YES:",
        "sha256:bytea:YES:",
    });

    /// <summary>The five checks as <c>name:pg_get_constraintdef:validated</c>, ordered by name.</summary>
    public static IReadOnlyList<string> ContractChecks { get; } = Array.AsReadOnly(new[]
    {
        "driver_documents_evidence_check:CHECK ((((evidence_kind = 'FILE'::text) AND (object_key IS NOT NULL) AND (sha256 IS NOT NULL)) OR ((evidence_kind = 'IN_PERSON'::text) AND (object_key IS NULL) AND (sha256 IS NULL)))):true",
        "driver_documents_expiry_basis_check:CHECK (((expiry_basis IS NULL) OR ((expires_at IS NOT NULL) AND ((expiry_basis = 'PRINTED_EXPIRY'::text) OR ((expiry_basis = 'PERMANENT_LICENSE_REVIEW'::text) AND (document_type = 'DRIVER_LICENSE'::text)))))):true",
        "driver_documents_in_person_check:CHECK (((evidence_kind <> 'IN_PERSON'::text) OR ((reviewed_at IS NOT NULL) AND (document_type = ANY (ARRAY['IDENTITY'::text, 'DRIVER_LICENSE'::text, 'VEHICLE_CARD'::text, 'INSURANCE'::text])) AND (status = ANY (ARRAY['VALID'::text, 'REJECTED'::text, 'REVOKED'::text])) AND ((status = 'REJECTED'::text) = (rejection_code IS NOT NULL)) AND ((status = 'REJECTED'::text) = (expires_at IS NULL)) AND ((status = 'REJECTED'::text) = (expiry_basis IS NULL)) AND ((status = 'REVOKED'::text) = (revoked_at IS NOT NULL))))):true",
        "driver_documents_rejection_code_check:CHECK (((rejection_code IS NULL) OR ((status = 'REJECTED'::text) AND (rejection_code = ANY (ARRAY['ILLEGIBLE'::text, 'EXPIRED'::text, 'WRONG_DOCUMENT'::text, 'HOLDER_MISMATCH'::text, 'INCOMPLETE'::text, 'NOT_ACCEPTED'::text]))))):true",
        "driver_documents_review_check:CHECK ((((reviewed_by IS NULL) = (reviewed_at IS NULL)) AND ((revoked_by IS NULL) = (revoked_at IS NULL)) AND ((revoked_at IS NULL) OR (status = 'REVOKED'::text)))):true",
    });

    /// <summary>The two reviewer references, as <c>pg_get_constraintdef</c> renders them, ordered.</summary>
    public static IReadOnlyList<string> ContractForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        "FOREIGN KEY (reviewed_by) REFERENCES identity.users(id)",
        "FOREIGN KEY (revoked_by) REFERENCES identity.users(id)",
    });

    /// <summary>The DDL AI-06 carries for fresh installations; the Up runs exactly this text when the columns are absent.</summary>
    public const string CanonicalDdl =
        """
        ALTER TABLE drivers.driver_documents
          ALTER COLUMN object_key DROP NOT NULL,
          ALTER COLUMN sha256 DROP NOT NULL,
          ADD COLUMN evidence_kind text NOT NULL DEFAULT 'FILE',
          ADD COLUMN expiry_basis text,
          ADD COLUMN rejection_code text,
          ADD COLUMN reviewed_by uuid REFERENCES identity.users(id),
          ADD COLUMN reviewed_at timestamptz,
          ADD COLUMN revoked_by uuid REFERENCES identity.users(id),
          ADD COLUMN revoked_at timestamptz,
          ADD CONSTRAINT driver_documents_evidence_check CHECK (
            (evidence_kind='FILE' AND object_key IS NOT NULL AND sha256 IS NOT NULL)
            OR (evidence_kind='IN_PERSON' AND object_key IS NULL AND sha256 IS NULL)),
          ADD CONSTRAINT driver_documents_expiry_basis_check CHECK (
            expiry_basis IS NULL OR (expires_at IS NOT NULL AND (expiry_basis='PRINTED_EXPIRY'
              OR (expiry_basis='PERMANENT_LICENSE_REVIEW' AND document_type='DRIVER_LICENSE')))),
          ADD CONSTRAINT driver_documents_rejection_code_check CHECK (
            rejection_code IS NULL OR (status='REJECTED' AND rejection_code IN
              ('ILLEGIBLE','EXPIRED','WRONG_DOCUMENT','HOLDER_MISMATCH','INCOMPLETE','NOT_ACCEPTED'))),
          ADD CONSTRAINT driver_documents_review_check CHECK (
            (reviewed_by IS NULL) = (reviewed_at IS NULL)
            AND (revoked_by IS NULL) = (revoked_at IS NULL)
            AND (revoked_at IS NULL OR status='REVOKED')),
          ADD CONSTRAINT driver_documents_in_person_check CHECK (
            evidence_kind<>'IN_PERSON' OR (
              reviewed_at IS NOT NULL
              AND document_type IN ('IDENTITY','DRIVER_LICENSE','VEHICLE_CARD','INSURANCE')
              AND status IN ('VALID','REJECTED','REVOKED')
              AND (status='REJECTED') = (rejection_code IS NOT NULL)
              AND (status='REJECTED') = (expires_at IS NULL)
              AND (status='REJECTED') = (expiry_basis IS NULL)
              AND (status='REVOKED') = (revoked_at IS NOT NULL)));
        """;

    private const string UpTemplate =
        """
        DO $drvdoc001_documents$
        DECLARE
          v_present integer;
        BEGIN
          IF to_regclass('drivers.driver_documents') IS NULL THEN
            RAISE EXCEPTION 'DRV-DOC-001 requires the canonical AI-06 drivers.driver_documents table';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='drivers.driver_documents'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'DRV-DOC-001 requires FORCE ROW LEVEL SECURITY on drivers.driver_documents';
          END IF;

          SELECT count(*) INTO v_present
          FROM information_schema.columns
          WHERE table_schema='drivers' AND table_name='driver_documents'
            AND column_name IN ({{NewColumns}});
          IF v_present = 0 THEN
        {{CanonicalDdl}}
          ELSIF v_present <> {{NewColumnCount}} THEN
            RAISE EXCEPTION 'drivers.driver_documents in-person columns are partially present';
          END IF;
        END
        $drvdoc001_documents$;

        DO $drvdoc001_verify$
        BEGIN
          IF (SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
                ORDER BY column_name)
              FROM information_schema.columns
              WHERE table_schema='drivers' AND table_name='driver_documents'
                AND column_name IN ({{NewColumns}},'object_key','sha256'))
             IS DISTINCT FROM ARRAY[{{ContractColumns}}]
          THEN
            RAISE EXCEPTION 'drivers.driver_documents in-person columns differ from the DRV-DOC-001 contract';
          END IF;

          IF (SELECT array_agg(conname || ':' || pg_get_constraintdef(oid) || ':' || convalidated::text ORDER BY conname)
              FROM pg_constraint
              WHERE conrelid='drivers.driver_documents'::regclass AND contype='c'
                AND conname IN ('driver_documents_evidence_check','driver_documents_expiry_basis_check',
                                'driver_documents_in_person_check','driver_documents_rejection_code_check',
                                'driver_documents_review_check'))
             IS DISTINCT FROM ARRAY[{{ContractChecks}}]
          THEN
            RAISE EXCEPTION 'drivers.driver_documents in-person checks differ from the DRV-DOC-001 contract';
          END IF;

          IF (SELECT array_agg(pg_get_constraintdef(c.oid) ORDER BY pg_get_constraintdef(c.oid))
              FROM pg_constraint c
              JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=ANY(c.conkey)
              WHERE c.conrelid='drivers.driver_documents'::regclass AND c.contype='f'
                AND a.attname IN ('reviewed_by','revoked_by'))
             IS DISTINCT FROM ARRAY[{{ContractForeignKeys}}]
          THEN
            RAISE EXCEPTION 'drivers.driver_documents reviewer references differ from the DRV-DOC-001 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid='drivers.driver_documents'::regclass AND relrowsecurity AND relforcerowsecurity
          ) THEN
            RAISE EXCEPTION 'DRV-DOC-001 requires FORCE ROW LEVEL SECURITY on drivers.driver_documents';
          END IF;
        END
        $drvdoc001_verify$;
        """;

    /// <summary>
    /// Rollback. It refuses while any document is IN_PERSON or carries a review, a reason or a revocation: they are the
    /// record of dispatch decisions, never erased silently (retention is a GATE-007 matter, Y9). On an installation
    /// without such rows it drops exactly what <see cref="CanonicalDdl"/> added.
    /// </summary>
    public const string DownSql =
        """
        DO $drvdoc001_downgrade$
        BEGIN
          ALTER TABLE drivers.driver_documents
            ADD CONSTRAINT driver_documents_in_person_downgrade_guard CHECK (
              evidence_kind='FILE' AND expiry_basis IS NULL AND rejection_code IS NULL
              AND reviewed_by IS NULL AND reviewed_at IS NULL AND revoked_by IS NULL AND revoked_at IS NULL);
        EXCEPTION WHEN check_violation THEN
          RAISE EXCEPTION USING
            MESSAGE = 'DRVDOC001_DOWNGRADE_BLOCKED_DATA_PRESENT',
            DETAIL = 'Driver documents hold in-person records, reviews, reasons or revocations; DRV-DOC-001-IN-PERSON-2026-11-02 is rolled back with Drivers:Documents:Enabled=false instead.',
            ERRCODE = 'P0001';
        END
        $drvdoc001_downgrade$;

        ALTER TABLE drivers.driver_documents
          DROP CONSTRAINT driver_documents_in_person_downgrade_guard,
          DROP CONSTRAINT driver_documents_in_person_check,
          DROP CONSTRAINT driver_documents_review_check,
          DROP CONSTRAINT driver_documents_rejection_code_check,
          DROP CONSTRAINT driver_documents_expiry_basis_check,
          DROP CONSTRAINT driver_documents_evidence_check,
          DROP COLUMN revoked_at,
          DROP COLUMN revoked_by,
          DROP COLUMN reviewed_at,
          DROP COLUMN reviewed_by,
          DROP COLUMN rejection_code,
          DROP COLUMN expiry_basis,
          DROP COLUMN evidence_kind,
          ALTER COLUMN sha256 SET NOT NULL,
          ALTER COLUMN object_key SET NOT NULL;
        """;

    /// <summary>The Up SQL with the canonical DDL and the expected catalog filled in, exactly as the migration runs it.</summary>
    public static string RenderUpSql() => UpTemplate
        .Replace("{{CanonicalDdl}}", CanonicalDdl.TrimEnd('\n'), StringComparison.Ordinal)
        .Replace("{{NewColumns}}", string.Join(',', NewColumns.Select(Literal)), StringComparison.Ordinal)
        .Replace("{{NewColumnCount}}", NewColumns.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{{ContractColumns}}", string.Join(',', ContractColumns.Select(Literal)), StringComparison.Ordinal)
        .Replace("{{ContractChecks}}", string.Join(',', ContractChecks.Select(Literal)), StringComparison.Ordinal)
        .Replace("{{ContractForeignKeys}}", string.Join(',', ContractForeignKeys.Select(Literal)), StringComparison.Ordinal);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RenderUpSql());

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
