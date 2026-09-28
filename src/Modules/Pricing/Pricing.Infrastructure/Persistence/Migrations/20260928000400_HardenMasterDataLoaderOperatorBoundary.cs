using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pricing.Infrastructure.Persistence.Migrations;

/// <summary>
/// MDM-001 loader hardening (review findings X1 and X2 deferred from the MDM-001 pull request). This migration:
/// <list type="bullet">
/// <item>X1: <c>security.load_master_data</c> refuses, at call time, any session whose login is a member of
/// <c>paqueteria_migrator</c> (<c>MDM001_DEPLOYMENT_PRINCIPAL_REFUSED</c>). The lane and the baseline
/// assertions already refuse a deployment principal that holds the loader with INHERIT or SET, but a
/// migrator member holding only ADMIN on the loader can grant itself SET and call the function before any
/// assertion runs again; the function now refuses it whatever its grants say;</item>
/// <item>X2: <c>operator_ref</c> in the audit payload is no longer an unsalted SHA-256 of a guessable login
/// but a random uuid per operator login, kept in <c>platform.master_data_operator_refs</c>: platform-only,
/// FORCE RLS with a single policy for <c>paqueteria_migrator</c> (its owner and only administrator), no
/// grant to PUBLIC or the runtime roles, and column SELECT/INSERT on (operator_login, operator_ref) for the
/// executor only, so it is read and written only through the SECURITY DEFINER function. No secret is
/// introduced. Existing audit rows are append-only and keep the 64-hex SHA-256 format; new rows carry a
/// uuid;</item>
/// <item>replaces the function with a body derived from the one installed by
/// <see cref="StoreTariffPolicyVersionInMasterDataLoader"/> by the exact, reviewed edits in
/// <see cref="FunctionEdits"/>; each edit must match exactly once or the migration refuses to build its SQL.
/// It then re-applies the published ACL and runs the published verification with the executor's exact
/// column grant count raised from 118 to 122 and the operator reference table added to its relations.</item>
/// </list>
/// The table and the four grants are owned by this lane step, not by AI-06/AI-18's GRANT statements: the
/// published MDM-001 step (116 grants) and the policy-version step (118) run before it on every installation
/// and verify exact counts. The rollback revokes the four grants and restores the previous function; the
/// table and its rows stay (they resolve the pseudonyms of append-only audit rows) and are inert without
/// the grants.
/// </summary>
[DbContext(typeof(PricingDbContext))]
[Migration(MigrationId)]
public sealed class HardenMasterDataLoaderOperatorBoundary : Migration
{
    public const string MigrationId = "20260928000400_HardenMasterDataLoaderOperatorBoundary";

    public const string DeploymentPrincipalRefused = "MDM001_DEPLOYMENT_PRINCIPAL_REFUSED";

    public const string OperatorRefUnavailable = "MDM001_OPERATOR_REF_UNAVAILABLE";

    public const string OperatorRefTable = "platform.master_data_operator_refs";

    public const int ExecutorColumnGrantCount = 122;

    /// <summary>The executor column grants this step adds (schema.table.column:privilege).</summary>
    public static IReadOnlyList<string> AddedExecutorColumnGrants { get; } = Array.AsReadOnly(new[]
    {
        "platform.master_data_operator_refs.operator_login:INSERT",
        "platform.master_data_operator_refs.operator_login:SELECT",
        "platform.master_data_operator_refs.operator_ref:INSERT",
        "platform.master_data_operator_refs.operator_ref:SELECT",
    });

    /// <summary>The only differences from the previous loader function, as (previous text, new text).</summary>
    public static IReadOnlyList<(string Published, string Replacement)> FunctionEdits { get; } = Array.AsReadOnly(new[]
    {
        (
            "  v_result jsonb;\n",
            "  v_result jsonb;\n" +
            "  v_operator_ref uuid;\n"
        ),
        (
            "  -- Tenant scope: the transaction must carry exactly this organization (AI-01 §4.1-§4.2).\n",
            "  -- MDM-001 X1: whoever deploys never loads. A member of paqueteria_migrator that holds only ADMIN on\n" +
            "  -- the loader role could grant itself SET and call this function; it is refused here, at call time,\n" +
            "  -- whatever its grants say (a superuser counts as a member of every role and is refused too).\n" +
            "  IF pg_catalog.pg_has_role(SESSION_USER, 'paqueteria_migrator', 'MEMBER') THEN\n" +
            "    RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'MDM001_DEPLOYMENT_PRINCIPAL_REFUSED';\n" +
            "  END IF;\n" +
            "\n" +
            "  -- Tenant scope: the transaction must carry exactly this organization (AI-01 §4.1-§4.2).\n"
        ),
        (
            "    -- MDM-001 M1/N3: the operator is the actor of record (no application user performs a load). The\n" +
            "    -- tenant-readable payload carries only a pseudonym: SHA-256 over a domain-separated login name, which\n" +
            "    -- platform staff match against the operator logins they created.\n",
            "    -- MDM-001 M1/N3/X2: the operator is the actor of record (no application user performs a load). The\n" +
            "    -- tenant-readable payload carries only a pseudonym: a random uuid per operator login, created on the\n" +
            "    -- login's first real load in platform.master_data_operator_refs (platform-only, FORCE RLS, read by\n" +
            "    -- platform staff as the migrator). Nothing derived from the login name leaves this function.\n" +
            "    INSERT INTO platform.master_data_operator_refs(operator_login, operator_ref)\n" +
            "    VALUES (SESSION_USER::text, pg_catalog.gen_random_uuid())\n" +
            "    ON CONFLICT (operator_login) DO NOTHING;\n" +
            "    SELECT r.operator_ref INTO v_operator_ref\n" +
            "    FROM platform.master_data_operator_refs r WHERE r.operator_login = SESSION_USER::text;\n" +
            "    IF v_operator_ref IS NULL THEN\n" +
            "      RAISE EXCEPTION USING ERRCODE = '55000', MESSAGE = 'MDM001_OPERATOR_REF_UNAVAILABLE';\n" +
            "    END IF;\n"
        ),
        (
            "      v_result || pg_catalog.jsonb_build_object('operator_ref', pg_catalog.encode(pg_catalog.sha256(\n" +
            "        pg_catalog.convert_to('paquetenvia.mdm-001.operator:' || SESSION_USER::text, 'UTF8')), 'hex')),\n",
            "      v_result || pg_catalog.jsonb_build_object('operator_ref', v_operator_ref::text),\n"
        ),
    });

    /// <summary>The function definition installed by the previous step (CREATE OR REPLACE ... $function$;).</summary>
    public static string PreviousFunctionSql => StoreTariffPolicyVersionInMasterDataLoader.FunctionSql;

    /// <summary>The function definition this migration installs.</summary>
    public static string FunctionSql { get; } = FunctionEdits.Aggregate(
        PreviousFunctionSql,
        (sql, edit) => ReplaceOnce(sql, edit.Published, edit.Replacement));

    /// <summary>The previous verification (118 executor column grants).</summary>
    public static string PreviousVerifySql => StoreTariffPolicyVersionInMasterDataLoader.VerifySql;

    /// <summary>The same verification for the 122 grants and the operator reference table this step leaves.</summary>
    public static string VerifySql { get; } = ReplaceOnce(
        ReplaceOnce(
            PreviousVerifySql,
            $"WHERE a.grantee = executor AND NOT a.is_grantable) <> {StoreTariffPolicyVersionInMasterDataLoader.ExecutorColumnGrantCount}",
            $"WHERE a.grantee = executor AND NOT a.is_grantable) <> {ExecutorColumnGrantCount}"),
        "'platform.master_data_deployment_gate'))\n",
        "'platform.master_data_deployment_gate','platform.master_data_operator_refs'))\n");

    public static string UpSql { get; } = string.Concat(
        """
        -- As paqueteria_migrator: the operator reference table is owned by the migrator, its only administrator,
        -- like the deployment marker. Set explicitly: an earlier step of this lane ends its own transaction
        -- after a session-level RESET ROLE.
        SET LOCAL ROLE paqueteria_migrator;

        DO $mdm_hardening_adoption$
        BEGIN
          IF to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NULL
             OR position('MDM001_TARIFF_POLICY_VERSION_IMMUTABLE' IN (SELECT prosrc FROM pg_proc
                 WHERE oid=to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)'))) = 0 THEN
            RAISE EXCEPTION 'MDM-001 loader hardening requires the policy-version loader function';
          END IF;

          IF to_regclass('platform.master_data_operator_refs') IS NULL THEN
            CREATE TABLE platform.master_data_operator_refs (
              operator_login text NOT NULL,
              operator_ref uuid NOT NULL,
              created_at timestamptz NOT NULL DEFAULT now(),
              CONSTRAINT master_data_operator_refs_pkey PRIMARY KEY (operator_login),
              CONSTRAINT master_data_operator_refs_ref_key UNIQUE (operator_ref),
              CONSTRAINT master_data_operator_refs_login_ck CHECK (octet_length(operator_login) BETWEEN 1 AND 63)
            );
          END IF;
          ALTER TABLE platform.master_data_operator_refs ENABLE ROW LEVEL SECURITY;
          ALTER TABLE platform.master_data_operator_refs FORCE ROW LEVEL SECURITY;
          IF NOT EXISTS (SELECT 1 FROM pg_policy WHERE polrelid='platform.master_data_operator_refs'::regclass) THEN
            CREATE POLICY master_data_operator_refs_migrator ON platform.master_data_operator_refs
              TO paqueteria_migrator USING (true) WITH CHECK (true);
          END IF;
          IF (SELECT string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod) || ':' || a.attnotnull::text, ','
                ORDER BY a.attnum)
              FROM pg_attribute a
              WHERE a.attrelid='platform.master_data_operator_refs'::regclass AND a.attnum > 0 AND NOT a.attisdropped)
             IS DISTINCT FROM 'operator_login:text:true,operator_ref:uuid:true,created_at:timestamp with time zone:true'
             OR (SELECT string_agg(pg_get_constraintdef(oid), ',' ORDER BY conname) FROM pg_constraint
                 WHERE conrelid='platform.master_data_operator_refs'::regclass AND contype IN ('c','p','u'))
                IS DISTINCT FROM 'CHECK (((octet_length(operator_login) >= 1) AND (octet_length(operator_login) <= 63))),PRIMARY KEY (operator_login),UNIQUE (operator_ref)'
             OR EXISTS (
               SELECT 1 FROM pg_trigger
               WHERE tgrelid='platform.master_data_operator_refs'::regclass AND NOT tgisinternal)
             OR (SELECT string_agg(pol.polname || ':' || pol.polcmd::text || ':' || pol.polpermissive::text || ':'
                   || (SELECT string_agg(CASE WHEN r = 0 THEN 'public' ELSE pg_get_userbyid(r) END, '+') FROM unnest(pol.polroles) r)
                   || ':' || COALESCE(pg_get_expr(pol.polqual, pol.polrelid), '') || ':'
                   || COALESCE(pg_get_expr(pol.polwithcheck, pol.polrelid), ''), ',')
                 FROM pg_policy pol WHERE pol.polrelid='platform.master_data_operator_refs'::regclass)
                IS DISTINCT FROM 'master_data_operator_refs_migrator:*:true:paqueteria_migrator:true:true'
             OR pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid='platform.master_data_operator_refs'::regclass))
                <> 'paqueteria_migrator' THEN
            RAISE EXCEPTION 'MDM-001 requires the canonical platform.master_data_operator_refs';
          END IF;
        END
        $mdm_hardening_adoption$;

        -- Platform-only: nothing for PUBLIC or the runtime roles (whatever default privileges gave them); the
        -- executor reads and inserts the two columns it needs, never UPDATE or DELETE: a reference is permanent.
        REVOKE ALL ON platform.master_data_operator_refs FROM PUBLIC, paqueteria_app, paqueteria_worker;
        GRANT SELECT (operator_login,operator_ref), INSERT (operator_login,operator_ref)
          ON platform.master_data_operator_refs TO paqueteria_master_data_executor;

        RESET ROLE;

        -- As in the published step: the ungated jsonb overload of the first published version must never stay
        -- callable, and the verification below requires the executor to own exactly one function.
        DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,jsonb,bytea,boolean);


        """,
        FunctionSql,
        "\n",
        StoreTariffPolicyVersionInMasterDataLoader.FunctionAclSql,
        "\n",
        VerifySql,
        """

        DO $mdm_hardening_verify$
        DECLARE
          fn oid := to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)');
          refs oid := 'platform.master_data_operator_refs'::regclass;
        BEGIN
          IF (SELECT string_agg(att.attname || ':' || a.privilege_type || ':' || a.is_grantable::text, ','
                ORDER BY att.attname, a.privilege_type)
              FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
              WHERE att.attrelid = refs AND a.grantee = 'paqueteria_master_data_executor'::regrole)
             IS DISTINCT FROM 'operator_login:INSERT:false,operator_login:SELECT:false,operator_ref:INSERT:false,operator_ref:SELECT:false'
             OR EXISTS (
               SELECT 1 FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
               WHERE att.attrelid = refs AND a.grantee <> 'paqueteria_master_data_executor'::regrole)
             OR EXISTS (
               SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
               WHERE c.oid = refs AND a.grantee <> c.relowner)
             OR NOT (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = refs)
             OR position('MDM001_DEPLOYMENT_PRINCIPAL_REFUSED' IN (SELECT prosrc FROM pg_proc WHERE oid = fn)) = 0
             OR position('platform.master_data_operator_refs' IN (SELECT prosrc FROM pg_proc WHERE oid = fn)) = 0
             OR position('paquetenvia.mdm-001.operator:' IN (SELECT prosrc FROM pg_proc WHERE oid = fn)) <> 0 THEN
            RAISE EXCEPTION 'MDM-001 loader hardening differs from the contract';
          END IF;
        END
        $mdm_hardening_verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """);

    /// <summary>
    /// Rollback: revokes the four grants and restores the previous function, ACL and verification. The
    /// operator reference table and its rows stay: they resolve the pseudonyms of append-only audit rows,
    /// and without the grants nothing but the migrator reaches them. Re-applying the step reuses them.
    /// </summary>
    public static string DownSql { get; } = string.Concat(
        """
        SET LOCAL ROLE paqueteria_migrator;
        REVOKE SELECT (operator_login,operator_ref), INSERT (operator_login,operator_ref)
          ON platform.master_data_operator_refs FROM paqueteria_master_data_executor;

        RESET ROLE;

        DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,jsonb,bytea,boolean);


        """,
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
                $"MDM-001 loader hardening: an edit must match the previous function exactly once: '{published.Trim()}'.");
        }

        return string.Concat(sql.AsSpan(0, at), replacement, sql.AsSpan(at + published.Length));
    }
}
