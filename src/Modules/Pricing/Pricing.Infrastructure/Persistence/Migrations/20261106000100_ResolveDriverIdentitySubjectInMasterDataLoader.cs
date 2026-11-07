using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pricing.Infrastructure.Persistence.Migrations;

/// <summary>
/// MDM-001-DRIVER-SUB-2026-11-06 (owner answer S7: "Por su sub de AuthCenter (Recommended)"). A driver-profile entry
/// of the reviewed master-data file names its driver by exactly one of <c>user_id</c> (the internal user id, kept so
/// the synthetic fixtures stay valid) or <c>identity_subject</c> (the AuthCenter <c>sub</c>, a lowercase UUID, which
/// the owner sees in the AuthCenter console; the documented way for real data). This migration:
/// <list type="bullet">
/// <item>replaces <c>security.load_master_data</c> with a body derived from the one installed by
/// <see cref="RequireMazatlanTimeZoneInMasterDataLoader"/> by the exact, reviewed edits in <see cref="FunctionEdits"/>
/// (each must match exactly once or the migration refuses to build its SQL): the entry shape admits either identifier,
/// never both or neither; a subject must be a lowercase UUID other than the nil UUID; it is resolved to
/// <c>identity.users.id</c>, and a subject that matches no user (or, defensively, more than one) is refused with
/// <c>MDM001_DRIVER_SUBJECT_UNKNOWN</c>, which says nothing more about it. Every existing rule still applies to the
/// resolved user: an ACTIVE user with an ACTIVE DRIVER membership of the loaded organization
/// (<c>MDM001_DRIVER_MEMBERSHIP_REQUIRED</c>), one profile per user (a user named twice, by id and by subject, is a
/// duplicate), GATE-007 and the classification;</item>
/// <item>grants the executor <c>SELECT (identity_subject)</c> on <c>identity.users</c>, its only new privilege
/// (column grants 122 to 123). Nothing is granted to the loader or the runtime roles. Like the policy-version and
/// hardening grants, it belongs to this lane step and AI-18 records it in comments: the published steps verify
/// exact counts (116, 118, 122) before it on every installation;</item>
/// <item>re-applies the published ACL and the hardening verification with the count raised to 123, and verifies the
/// executor's exact grants on <c>identity.users</c>.</item>
/// </list>
/// The document format stays <c>paquetenvia.master-data.v1</c>: the change is additive (every v1 document keeps its
/// meaning), and an older loader refuses the new property instead of misreading it. No stored row is rewritten.
/// The rollback revokes the grant and restores the America/Mazatlan step's function; profiles loaded meanwhile stay.
/// </summary>
[DbContext(typeof(PricingDbContext))]
[Migration(MigrationId)]
public sealed class ResolveDriverIdentitySubjectInMasterDataLoader : Migration
{
    public const string MigrationId = "20261106000100_ResolveDriverIdentitySubjectInMasterDataLoader";

    public const string DecisionId = "MDM-001-DRIVER-SUB-2026-11-06";

    public const string DriverSubjectUnknown = "MDM001_DRIVER_SUBJECT_UNKNOWN";

    public const int ExecutorColumnGrantCount = 123;

    /// <summary>The executor column grant this step adds (schema.table.column:privilege).</summary>
    public static IReadOnlyList<string> AddedExecutorColumnGrants { get; } = Array.AsReadOnly(new[]
    {
        "identity.users.identity_subject:SELECT",
    });

    private const string Uuid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";

    /// <summary>The only differences from the previous loader function, as (previous text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> FunctionEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "       OR (SELECT array_agg(k ORDER BY k COLLATE \"C\") FROM pg_catalog.jsonb_object_keys(v_item) k)\n" +
            "          IS DISTINCT FROM ARRAY['driver_type','home_city','service_areas','status','user_id','vehicle_type']\n",
            "       -- MDM-001-DRIVER-SUB-2026-11-06: exactly one of user_id or identity_subject names the driver.\n" +
            "       OR ((SELECT array_agg(k ORDER BY k COLLATE \"C\") FROM pg_catalog.jsonb_object_keys(v_item) k)\n" +
            "             IS DISTINCT FROM ARRAY['driver_type','home_city','service_areas','status','user_id','vehicle_type']\n" +
            "           AND (SELECT array_agg(k ORDER BY k COLLATE \"C\") FROM pg_catalog.jsonb_object_keys(v_item) k)\n" +
            "             IS DISTINCT FROM ARRAY['driver_type','home_city','identity_subject','service_areas','status','vehicle_type'])\n"
        ),
        (
            "       OR pg_catalog.jsonb_typeof(v_item->'user_id') IS DISTINCT FROM 'string'\n" +
            "       OR (v_item->>'user_id') !~ '" + Uuid + "' THEN\n" +
            "      RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ENTRY_SHAPE', HINT = v_ref;\n" +
            "    END IF;\n" +
            "    v_user_id := (v_item->>'user_id')::uuid;\n",
            "       OR (v_item ? 'user_id' AND (pg_catalog.jsonb_typeof(v_item->'user_id') IS DISTINCT FROM 'string'\n" +
            "           OR (v_item->>'user_id') !~ '" + Uuid + "'))\n" +
            "       OR (v_item ? 'identity_subject' AND (pg_catalog.jsonb_typeof(v_item->'identity_subject') IS DISTINCT FROM 'string'\n" +
            "           OR (v_item->>'identity_subject') !~ '" + Uuid + "'\n" +
            "           OR (v_item->>'identity_subject') = '00000000-0000-0000-0000-000000000000')) THEN\n" +
            "      RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ENTRY_SHAPE', HINT = v_ref;\n" +
            "    END IF;\n" +
            "    IF v_item ? 'identity_subject' THEN\n" +
            "      -- MDM-001-DRIVER-SUB-2026-11-06 (S7): the AuthCenter sub names the user. A subject that matches no\n" +
            "      -- user (or, defensively, more than one) is refused with one code that says nothing more about it;\n" +
            "      -- the resolved user then meets every rule below, as a user_id does.\n" +
            "      SELECT pg_catalog.count(*) INTO v_count\n" +
            "      FROM identity.users u WHERE u.identity_subject = (v_item->>'identity_subject');\n" +
            "      IF v_count <> 1 THEN\n" +
            "        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DRIVER_SUBJECT_UNKNOWN', HINT = v_ref;\n" +
            "      END IF;\n" +
            "      SELECT u.id INTO v_user_id\n" +
            "      FROM identity.users u WHERE u.identity_subject = (v_item->>'identity_subject');\n" +
            "    ELSE\n" +
            "      v_user_id := (v_item->>'user_id')::uuid;\n" +
            "    END IF;\n"
        ),
    });

    /// <summary>The function definition installed by the previous step (CREATE OR REPLACE ... $function$;).</summary>
    public static string PreviousFunctionSql => RequireMazatlanTimeZoneInMasterDataLoader.FunctionSql;

    /// <summary>The function definition this migration installs.</summary>
    public static string FunctionSql { get; } = FunctionEdits.Aggregate(
        PreviousFunctionSql,
        (sql, edit) => ReplaceOnce(sql, edit.Published, edit.Replacement));

    /// <summary>The previous verification (the hardening step's, 122 executor column grants).</summary>
    public static string PreviousVerifySql => HardenMasterDataLoaderOperatorBoundary.VerifySql;

    /// <summary>The same verification for the 123 grants this step leaves.</summary>
    public static string VerifySql { get; } = ReplaceOnce(
        PreviousVerifySql,
        $"WHERE a.grantee = executor AND NOT a.is_grantable) <> {HardenMasterDataLoaderOperatorBoundary.ExecutorColumnGrantCount}",
        $"WHERE a.grantee = executor AND NOT a.is_grantable) <> {ExecutorColumnGrantCount}");

    public static string UpSql { get; } = string.Concat(
        """
        RESET ROLE;

        DO $mdm001_driver_sub_adoption$
        BEGIN
          IF to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NULL
             OR position('MDM001_CITY_TIMEZONE_NOT_IN_PILOT' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0
             OR position('MDM001_DEPLOYMENT_PRINCIPAL_REFUSED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0 THEN
            RAISE EXCEPTION 'MDM-001 driver subject loader step requires the America/Mazatlan loader function';
          END IF;
        END
        $mdm001_driver_sub_adoption$;

        -- MDM-001-DRIVER-SUB-2026-11-06: the executor reads the AuthCenter subject to resolve a driver; nothing
        -- else is granted, and nothing at all to the loader or the runtime roles.
        GRANT SELECT (identity_subject) ON identity.users TO paqueteria_master_data_executor;

        -- As in the previous steps: the ungated jsonb overload of the first published version must never stay
        -- callable, and the verification below requires the executor to own exactly one function.

        """,
        StoreTariffPolicyVersionInMasterDataLoader.LegacyOverloadDrop,
        "\n\n",
        FunctionSql,
        "\n",
        StoreTariffPolicyVersionInMasterDataLoader.FunctionAclSql,
        "\n",
        VerifySql,
        """

        DO $mdm001_driver_sub_verify$
        BEGIN
          IF (SELECT string_agg(att.attname || ':' || a.privilege_type || ':' || a.is_grantable::text, ','
                ORDER BY att.attname, a.privilege_type)
              FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
              WHERE att.attrelid = 'identity.users'::regclass
                AND a.grantee = 'paqueteria_master_data_executor'::regrole)
             IS DISTINCT FROM 'id:SELECT:false,identity_subject:SELECT:false,status:SELECT:false'
             OR position('MDM001_DRIVER_SUBJECT_UNKNOWN' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0
             OR position('MDM001_CITY_TIMEZONE_NOT_IN_PILOT' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0
             OR position('MDM001_DEPLOYMENT_PRINCIPAL_REFUSED' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0 THEN
            RAISE EXCEPTION 'MDM-001 driver subject loader step differs from the contract';
          END IF;
        END
        $mdm001_driver_sub_verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """);

    /// <summary>
    /// Rollback: revokes the grant and restores the America/Mazatlan step's function, ACL and verification (122
    /// grants). Driver profiles loaded meanwhile stay; the restored function names drivers by user_id only again.
    /// </summary>
    public static string DownSql { get; } = string.Concat(
        """
        RESET ROLE;
        REVOKE SELECT (identity_subject) ON identity.users FROM paqueteria_master_data_executor;

        """,
        StoreTariffPolicyVersionInMasterDataLoader.LegacyOverloadDrop,
        "\n\n",
        PreviousFunctionSql,
        "\n",
        StoreTariffPolicyVersionInMasterDataLoader.FunctionAclSql,
        "\n",
        PreviousVerifySql,
        """

        SET LOCAL ROLE paqueteria_migrator;
        """);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    private static string ReplaceOnce(string sql, string published, string replacement)
    {
        var at = sql.IndexOf(published, StringComparison.Ordinal);
        if (at < 0 || sql.IndexOf(published, at + published.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"MDM-001 driver subject loader step: an edit must match the previous text exactly once: '{published.Trim()}'.");
        }

        return string.Concat(sql.AsSpan(0, at), replacement, sql.AsSpan(at + published.Length));
    }
}
