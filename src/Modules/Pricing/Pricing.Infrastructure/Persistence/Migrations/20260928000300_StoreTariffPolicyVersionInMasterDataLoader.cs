using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pricing.Infrastructure.Persistence.Migrations;

/// <summary>
/// PRC-POLICY-VERSION-PER-ORG x MDM-001-OPERATOR-LOADER. <c>security.load_master_data</c> (installed by
/// <see cref="AddMasterDataLoader"/>) already requires and validates a <c>policy_version</c> on every tariff
/// rule of the reviewed file but could not store it before
/// <see cref="VersionPricingPolicyPerOrganization"/> added the column. This migration:
/// <list type="bullet">
/// <item>grants <c>paqueteria_master_data_executor</c> <c>SELECT</c> and <c>INSERT</c> on
/// <c>pricing.tariff_rules.policy_version</c> (no <c>UPDATE</c>: a stored version is immutable);</item>
/// <item>replaces the function with a body derived from the published one by the exact, reviewed edits in
/// <see cref="FunctionEdits"/>: a created rule stores its version, a reloaded rule must carry the stored
/// version (<c>MDM001_TARIFF_POLICY_VERSION_IMMUTABLE</c> otherwise, like a price), and the result no longer
/// reports <c>policy_version_persisted</c>. Every other statement of the function, including the GATE-007
/// gate, PLATFORM-only cities, overlap, limits, tenant context, <c>operator_ref</c> and audit, is kept byte
/// for byte; each edit must match exactly once or the migration refuses to build its SQL;</item>
/// <item>re-applies the published ACL and runs the published verification with the executor's exact column
/// grant count raised from 116 to 118, plus a check that the stored version is in place.</item>
/// </list>
/// AI-18's executor GRANT statements stay at the MDM-001 set: the published MDM-001 migration, which runs
/// before this one on every installation, verifies exactly 116 column grants. This lane owns the two
/// added grants, like it owns the function. The rollback revokes them and restores the published function.
/// </summary>
[DbContext(typeof(PricingDbContext))]
[Migration(MigrationId)]
public sealed class StoreTariffPolicyVersionInMasterDataLoader : Migration
{
    public const string MigrationId = "20260928000300_StoreTariffPolicyVersionInMasterDataLoader";

    public const string ImmutableVersionError = "MDM001_TARIFF_POLICY_VERSION_IMMUTABLE";

    public const int PublishedExecutorColumnGrantCount = 116;

    public const int ExecutorColumnGrantCount = 118;

    /// <summary>The only function this migration may drop: the first published, ungated jsonb overload.</summary>
    public const string LegacyOverloadDrop = "DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,jsonb,bytea,boolean);";

    /// <summary>The executor column grants this lane adds to the MDM-001 set (schema.table.column:privilege).</summary>
    public static IReadOnlyList<string> AddedExecutorColumnGrants { get; } = Array.AsReadOnly(new[]
    {
        "pricing.tariff_rules.policy_version:INSERT",
        "pricing.tariff_rules.policy_version:SELECT",
    });

    /// <summary>The only differences from the published loader function, as (published text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> FunctionEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "  v_policy_version text;\n",
            "  v_policy_version text;\n" +
            "  v_existing_policy_version text;\n"
        ),
        (
            "       -- PRC policy version per organization: required on every rule, same pattern as the pending\n" +
            "       -- pricing.tariff_rules.policy_version CHECK. Validated now, not yet stored (see TODO below).\n",
            "       -- PRC-POLICY-VERSION-PER-ORG: required on every rule, same pattern as the\n" +
            "       -- pricing.tariff_rules.policy_version CHECK; stored with the rule and immutable afterwards.\n"
        ),
        (
            "    SELECT r.id, r.amount_cents, r.tax_mode, r.active_to, r.status\n" +
            "    INTO v_id, v_existing_amount, v_existing_text1, v_existing_to, v_existing_text2\n",
            "    SELECT r.id, r.amount_cents, r.tax_mode, r.active_to, r.status, r.policy_version\n" +
            "    INTO v_id, v_existing_amount, v_existing_text1, v_existing_to, v_existing_text2, v_existing_policy_version\n"
        ),
        (
            "        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_AMOUNT_IMMUTABLE', HINT = v_ref;\n" +
            "      END IF;\n",
            "        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_AMOUNT_IMMUTABLE', HINT = v_ref;\n" +
            "      END IF;\n" +
            "      -- PRC-POLICY-VERSION-PER-ORG: quotes and orders freeze a rule's policy version, so a stored\n" +
            "      -- version is never rewritten (a legacy rule without one is not versioned by a reload either).\n" +
            "      IF v_existing_policy_version IS DISTINCT FROM v_policy_version THEN\n" +
            "        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_POLICY_VERSION_IMMUTABLE', HINT = v_ref;\n" +
            "      END IF;\n"
        ),
        (
            "            -- TODO(PRC policy_version per organization, feature/prc-policy-version-per-org): once that lane\n" +
            "            -- adds the NOT NULL pricing.tariff_rules.policy_version column, insert v_step->>'policy_version'\n" +
            "            -- here, compare it on reload (like amount_cents: a published rule's version is not rewritten),\n" +
            "            -- set policy_version_persisted in the result and add policy_version to the executor's\n" +
            "            -- SELECT/INSERT column grants (AI-18, validator, DatabaseBaselineAssertions). Until then it is\n" +
            "            -- validated and planned but not stored, and the NOT NULL column makes this INSERT fail closed\n" +
            "            -- (MDM001_WRITE_CONFLICT) if that lane lands first.\n",
            "            -- PRC-POLICY-VERSION-PER-ORG: a created rule stores its organization's policy version; an\n" +
            "            -- update only closes or deactivates a rule and never touches its version.\n"
        ),
        (
            "                amount_cents,tax_mode,active_from,active_to,status)\n",
            "                amount_cents,tax_mode,active_from,active_to,status,policy_version)\n"
        ),
        (
            "                v_step->>'tax_mode', (v_step->>'active_from')::timestamptz, (v_step->>'active_to')::timestamptz,\n" +
            "                v_step->>'status');\n",
            "                v_step->>'tax_mode', (v_step->>'active_from')::timestamptz, (v_step->>'active_to')::timestamptz,\n" +
            "                v_step->>'status', v_step->>'policy_version');\n"
        ),
        (
            "    'policy_version_persisted', false,\n",
            string.Empty
        ),
    });

    /// <summary>The published function definition (CREATE OR REPLACE ... $function$;).</summary>
    public static string PublishedFunctionSql { get; } = Segment(
        AddMasterDataLoader.UpSql,
        "CREATE OR REPLACE FUNCTION security.load_master_data(",
        "$function$;\n");

    /// <summary>The function definition this migration installs.</summary>
    public static string FunctionSql { get; } = FunctionEdits.Aggregate(
        PublishedFunctionSql,
        (sql, edit) => ReplaceOnce(sql, edit.Published, edit.Replacement));

    /// <summary>The published ACL statements (EXECUTE for the loader only, owned by the executor).</summary>
    public static string FunctionAclSql { get; } = Segment(
        AddMasterDataLoader.UpSql,
        "-- ACL first, while the deploying role still owns the function:",
        "OWNER TO paqueteria_master_data_executor;\n");

    /// <summary>The published MDM-001 boundary verification (116 executor column grants).</summary>
    public static string PublishedVerifySql { get; } = Segment(
        AddMasterDataLoader.UpSql,
        "DO $verify$",
        "$verify$;\n");

    /// <summary>The same verification for the 118 grants this lane leaves.</summary>
    public static string VerifySql { get; } = ReplaceOnce(
        PublishedVerifySql,
        $"WHERE a.grantee = executor AND NOT a.is_grantable) <> {PublishedExecutorColumnGrantCount}",
        $"WHERE a.grantee = executor AND NOT a.is_grantable) <> {ExecutorColumnGrantCount}");

    public static string UpSql { get; } = string.Concat(
        """
        RESET ROLE;

        -- MDM-001 N1, as in the published step: the ungated jsonb overload of the first published version must
        -- never stay callable, and the verification below requires the executor to own exactly one function.
        DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,jsonb,bytea,boolean);

        DO $prc_mdm_adoption$
        BEGIN
          IF to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NULL
             OR (SELECT pg_get_userbyid(proowner) FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))
                <> 'paqueteria_master_data_executor' THEN
            RAISE EXCEPTION 'PRC-POLICY-VERSION-PER-ORG requires the MDM-001 loader function owned by its executor';
          END IF;
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='tariff_rules' AND column_name='policy_version'
              AND data_type='text') THEN
            RAISE EXCEPTION 'PRC-POLICY-VERSION-PER-ORG requires pricing.tariff_rules.policy_version';
          END IF;
        END
        $prc_mdm_adoption$;

        -- SELECT for the reload comparison, INSERT for new rules; no UPDATE: a stored version is immutable.
        GRANT SELECT (policy_version), INSERT (policy_version) ON pricing.tariff_rules TO paqueteria_master_data_executor;


        """,
        FunctionSql,
        "\n",
        FunctionAclSql,
        "\n",
        VerifySql,
        """

        DO $prc_mdm_verify$
        BEGIN
          IF (SELECT string_agg(a.privilege_type || ':' || a.is_grantable::text, ',' ORDER BY a.privilege_type)
              FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
              WHERE att.attrelid = 'pricing.tariff_rules'::regclass AND att.attname = 'policy_version'
                AND a.grantee = 'paqueteria_master_data_executor'::regrole)
             IS DISTINCT FROM 'INSERT:false,SELECT:false'
             OR position('MDM001_TARIFF_POLICY_VERSION_IMMUTABLE' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0
             OR position('policy_version_persisted' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) <> 0 THEN
            RAISE EXCEPTION 'PRC-POLICY-VERSION-PER-ORG loader policy_version storage differs from the contract';
          END IF;
        END
        $prc_mdm_verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """);

    /// <summary>
    /// Rollback: revokes the two grants and restores the published function, ACL and verification. Loaded
    /// rules keep their stored versions. The published function then fails closed on a new tariff rule
    /// (NOT NULL policy_version) until this migration is applied again.
    /// </summary>
    public static string DownSql { get; } = string.Concat(
        """
        RESET ROLE;

        DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,jsonb,bytea,boolean);
        REVOKE SELECT (policy_version), INSERT (policy_version) ON pricing.tariff_rules FROM paqueteria_master_data_executor;


        """,
        PublishedFunctionSql,
        "\n",
        FunctionAclSql,
        "\n",
        PublishedVerifySql,
        """

        SET LOCAL ROLE paqueteria_migrator;
        """);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    private static string Segment(string sql, string start, string end)
    {
        var from = sql.IndexOf(start, StringComparison.Ordinal);
        if (from < 0 || sql.IndexOf(start, from + start.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"The published MDM-001 SQL must contain '{start.Trim()}' exactly once.");
        }

        var to = sql.IndexOf(end, from, StringComparison.Ordinal);
        if (to < 0)
        {
            throw new InvalidOperationException($"The published MDM-001 SQL has no '{end.Trim()}' after '{start.Trim()}'.");
        }

        return sql[from..(to + end.Length)];
    }

    private static string ReplaceOnce(string sql, string published, string replacement)
    {
        var at = sql.IndexOf(published, StringComparison.Ordinal);
        if (at < 0 || sql.IndexOf(published, at + published.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"PRC-POLICY-VERSION-PER-ORG: a loader edit must match the published function exactly once: '{published.Trim()}'.");
        }

        return string.Concat(sql.AsSpan(0, at), replacement, sql.AsSpan(at + published.Length));
    }
}
