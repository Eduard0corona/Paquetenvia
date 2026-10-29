using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Persistence.Migrations;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (project owner: E4-P1 "Casilla en la orden (Recommended)"; AI-08 NTF-004; AI-24
/// <c>recipient_notices.consent.record</c>; AI-06 and AI-18 declared contract, AI-18 recipient notices step 2) and
/// NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 (N3 choice 2). The Orders lane step that records the WhatsApp consent box of
/// "Nueva orden" and of the CSV-001 commit: <c>orders.recipient_notice_consents</c>, one row per order and channel with
/// the order, its owner organization, the channel (WHATSAPP only), the consent version
/// (NTF-RECIPIENT-WHATSAPP-CONSENT-V1 only), who attested it and when. No phone, e-mail or name.
/// <list type="bullet">
/// <item>ENABLE and FORCE ROW LEVEL SECURITY with the owner-organization tenant policy (the
/// <c>orders.order_acceptances</c> one).</item>
/// <item>Append-only: the AI-06 <c>platform.reject_runtime_mutation()</c> trigger on UPDATE and DELETE.</item>
/// <item>The default module privileges are replaced by exactly SELECT and INSERT for <c>paqueteria_app</c> and SELECT for
/// <c>paqueteria_worker</c>; no other role holds anything.</item>
/// </list>
/// AI-06 and AI-18 declare <see cref="TableDdl"/> and <see cref="GrantsDdl"/> byte for byte; this step holds the DDL
/// (the #302 and #312 precedent), so a fresh installation and an upgraded one run the same statements. A table of that
/// name that already exists is refused, never altered.
/// </summary>
/// <remarks>
/// Rollback: the consents are evidence of what a dispatcher attested, so the Down refuses while any row exists (FORCE RLS
/// hides rows from the migrator, so the guard is a constraint: validating it reads every row) and otherwise drops only
/// the table. Recipient notices are rolled back with their switches instead (AI-24 <c>recipient_notices.rollback</c>).
/// </remarks>
[DbContext(typeof(OrdersDbContext))]
[Migration(MigrationId)]
public sealed class AddRecipientNoticeConsents : Migration
{
    public const string MigrationId = "20261023000200_AddRecipientNoticeConsents";
    public const string DecisionId = "NTF-RECIPIENT-NOTICES-2026-10-20";
    public const string ProducerDecisionId = "NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23";
    public const string DowngradeBlockedMessage = "NTF004_DOWNGRADE_BLOCKED_CONSENT_PRESENT";
    public const string TableName = "orders.recipient_notice_consents";

    /// <summary>The table, its row security and its append-only trigger, exactly as AI-06 declares them.</summary>
    public const string TableDdl =
        """
        CREATE TABLE orders.recipient_notice_consents (
          order_id uuid NOT NULL REFERENCES orders.orders(id),
          owner_org_id uuid NOT NULL REFERENCES organizations.organizations(id),
          channel text NOT NULL CONSTRAINT recipient_notice_consents_channel_check CHECK (channel='WHATSAPP'),
          consent_version text NOT NULL CONSTRAINT recipient_notice_consents_version_check CHECK (consent_version='NTF-RECIPIENT-WHATSAPP-CONSENT-V1'),
          attested_by uuid NOT NULL REFERENCES identity.users(id),
          attested_at timestamptz NOT NULL,
          CONSTRAINT recipient_notice_consents_pkey PRIMARY KEY (order_id,channel)
        );
        ALTER TABLE orders.recipient_notice_consents ENABLE ROW LEVEL SECURITY;
        ALTER TABLE orders.recipient_notice_consents FORCE ROW LEVEL SECURITY;
        CREATE POLICY recipient_notice_consents_tenant ON orders.recipient_notice_consents USING (security.app_allowed_org(owner_org_id)) WITH CHECK (security.app_allowed_org(owner_org_id));
        CREATE TRIGGER recipient_notice_consents_append_only BEFORE UPDATE OR DELETE ON orders.recipient_notice_consents
          FOR EACH ROW EXECUTE FUNCTION platform.reject_runtime_mutation();
        """;

    /// <summary>The runtime privileges, exactly as AI-18 recipient notices step 2 declares them.</summary>
    public const string GrantsDdl =
        """
        REVOKE ALL ON orders.recipient_notice_consents FROM PUBLIC,paqueteria_app,paqueteria_worker;
        GRANT SELECT,INSERT ON orders.recipient_notice_consents TO paqueteria_app;
        GRANT SELECT ON orders.recipient_notice_consents TO paqueteria_worker;
        """;

    /// <summary>The non-owner privileges the step leaves, as <c>aclexplode</c> lists them.</summary>
    public const string ExpectedAcl =
        "paqueteria_app:INSERT:false,paqueteria_app:SELECT:false,paqueteria_worker:SELECT:false";

    /// <summary>The columns as <c>information_schema.columns</c> renders them, in name order.</summary>
    public static IReadOnlyList<string> Columns { get; } = Array.AsReadOnly(new[]
    {
        "attested_at:timestamp with time zone:NO:",
        "attested_by:uuid:NO:",
        "channel:text:NO:",
        "consent_version:text:NO:",
        "order_id:uuid:NO:",
        "owner_org_id:uuid:NO:",
    });

    /// <summary>
    /// The constraints as <c>pg_get_constraintdef</c> renders them, in name order, without the NOT NULL entries
    /// PostgreSQL 18 also lists (<c>contype</c> 'n'; <see cref="Columns"/> carries them).
    /// </summary>
    public static IReadOnlyList<string> Constraints { get; } = Array.AsReadOnly(new[]
    {
        "recipient_notice_consents_attested_by_fkey:FOREIGN KEY (attested_by) REFERENCES identity.users(id)",
        "recipient_notice_consents_channel_check:CHECK ((channel = 'WHATSAPP'::text))",
        "recipient_notice_consents_order_id_fkey:FOREIGN KEY (order_id) REFERENCES orders.orders(id)",
        "recipient_notice_consents_owner_org_id_fkey:FOREIGN KEY (owner_org_id) REFERENCES organizations.organizations(id)",
        "recipient_notice_consents_pkey:PRIMARY KEY (order_id, channel)",
        "recipient_notice_consents_version_check:CHECK ((consent_version = 'NTF-RECIPIENT-WHATSAPP-CONSENT-V1'::text))",
    });

    /// <summary>The policy expressions as <c>pg_get_expr</c> renders them.</summary>
    public const string PolicyExpression = "security.app_allowed_org(owner_org_id)";

    private const string UpTemplate =
        """
        -- The table belongs to paqueteria_migrator whatever role an earlier step of the lane left active.
        SET LOCAL ROLE paqueteria_migrator;

        DO $ntf004_adoption$
        BEGIN
          IF to_regclass('orders.orders') IS NULL OR to_regclass('organizations.organizations') IS NULL
             OR to_regclass('identity.users') IS NULL
             OR to_regprocedure('platform.reject_runtime_mutation()') IS NULL
             OR to_regprocedure('security.app_allowed_org(uuid)') IS NULL THEN
            RAISE EXCEPTION 'NTF-004 requires the canonical AI-06 orders, organizations and identity tables and functions';
          END IF;
          IF to_regclass('orders.recipient_notice_consents') IS NOT NULL THEN
            RAISE EXCEPTION 'NTF-004 found orders.recipient_notice_consents already present; it is never altered';
          END IF;
          IF current_user <> 'paqueteria_migrator' THEN
            RAISE EXCEPTION 'NTF-004 runs as paqueteria_migrator, which owns every Orders table';
          END IF;
        END
        $ntf004_adoption$;

        {{TableDdl}}
        {{GrantsDdl}}

        DO $ntf004_verify$
        DECLARE
          v_differs text[] := ARRAY[]::text[];
        BEGIN
          IF (SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
                ORDER BY column_name COLLATE "C")
              FROM information_schema.columns
              WHERE table_schema='orders' AND table_name='recipient_notice_consents')
             IS DISTINCT FROM ARRAY[{{Columns}}]::text[] THEN
            v_differs := v_differs || 'columns'::text;
          END IF;
          -- PostgreSQL 18 also lists each NOT NULL as a constraint (contype 'n'); the columns above already carry them.
          IF (SELECT array_agg(conname || ':' || pg_get_constraintdef(oid) ORDER BY conname COLLATE "C")
              FROM pg_constraint WHERE conrelid='orders.recipient_notice_consents'::regclass AND contype <> 'n')
             IS DISTINCT FROM ARRAY[{{Constraints}}]::text[] THEN
            v_differs := v_differs || 'constraints'::text;
          END IF;
          IF NOT (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class
                  WHERE oid='orders.recipient_notice_consents'::regclass) THEN
            v_differs := v_differs || 'row security'::text;
          END IF;
          IF pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid='orders.recipient_notice_consents'::regclass))
             <> 'paqueteria_migrator' THEN
            v_differs := v_differs || 'owner'::text;
          END IF;
          IF (SELECT count(*) FROM pg_policy p
              WHERE p.polrelid='orders.recipient_notice_consents'::regclass AND p.polname='recipient_notice_consents_tenant'
                AND p.polcmd='*' AND p.polpermissive AND p.polroles='{0}'::oid[]
                AND pg_get_expr(p.polqual,p.polrelid)='{{PolicyExpression}}'
                AND pg_get_expr(p.polwithcheck,p.polrelid)='{{PolicyExpression}}') <> 1
             OR (SELECT count(*) FROM pg_policy p WHERE p.polrelid='orders.recipient_notice_consents'::regclass) <> 1 THEN
            v_differs := v_differs || 'policy'::text;
          END IF;
          IF NOT EXISTS (
               SELECT 1 FROM pg_trigger t
               WHERE t.tgrelid='orders.recipient_notice_consents'::regclass AND t.tgname='recipient_notice_consents_append_only'
                 AND NOT t.tgisinternal AND t.tgenabled='O'
                 AND t.tgfoid='platform.reject_runtime_mutation()'::regprocedure
                 AND pg_get_triggerdef(t.oid) LIKE '% BEFORE DELETE OR UPDATE ON orders.recipient_notice_consents FOR EACH ROW %')
             OR (SELECT count(*) FROM pg_trigger t
                 WHERE t.tgrelid='orders.recipient_notice_consents'::regclass AND NOT t.tgisinternal) <> 1 THEN
            v_differs := v_differs || 'trigger'::text;
          END IF;
          IF (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type || ':' || acl.is_grantable::text,
                ',' ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C", acl.privilege_type COLLATE "C")
              FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
              WHERE c.oid='orders.recipient_notice_consents'::regclass AND acl.grantee <> c.relowner)
             IS DISTINCT FROM '{{ExpectedAcl}}' THEN
            v_differs := v_differs || 'privileges'::text;
          END IF;
          -- Catalog names only: which part differs, never a row.
          IF cardinality(v_differs) > 0 THEN
            RAISE EXCEPTION 'NTF-004 orders.recipient_notice_consents differs from the AI-06 and AI-18 contract (%)',
              array_to_string(v_differs, ', ');
          END IF;
        END
        $ntf004_verify$;
        """;

    /// <summary>
    /// Rollback. It refuses while any consent exists anywhere; otherwise it drops exactly the table this step created
    /// (its policy, trigger and privileges go with it).
    /// </summary>
    public const string DownSql =
        """
        SET LOCAL ROLE paqueteria_migrator;

        DO $ntf004_downgrade$
        BEGIN
          ALTER TABLE orders.recipient_notice_consents
            ADD CONSTRAINT recipient_notice_consents_downgrade_guard CHECK (false);
        EXCEPTION WHEN check_violation THEN
          RAISE EXCEPTION USING
            MESSAGE = 'NTF004_DOWNGRADE_BLOCKED_CONSENT_PRESENT',
            DETAIL = 'Orders recorded a WhatsApp consent; NTF-RECIPIENT-NOTICES-2026-10-20 is rolled back with the recipient notice switches instead.',
            ERRCODE = 'P0001';
        END
        $ntf004_downgrade$;

        DROP TABLE orders.recipient_notice_consents;
        """;

    /// <summary>The Up SQL with the declared DDL and the expected catalog filled in, exactly as the migration runs it.</summary>
    public static string RenderUpSql() => UpTemplate
        .Replace("{{TableDdl}}", TableDdl.TrimEnd('\n'), StringComparison.Ordinal)
        .Replace("{{GrantsDdl}}", GrantsDdl.TrimEnd('\n'), StringComparison.Ordinal)
        .Replace("{{Columns}}", SqlArray(Columns), StringComparison.Ordinal)
        .Replace("{{Constraints}}", SqlArray(Constraints), StringComparison.Ordinal)
        .Replace("{{PolicyExpression}}", PolicyExpression, StringComparison.Ordinal)
        .Replace("{{ExpectedAcl}}", ExpectedAcl, StringComparison.Ordinal);

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(RenderUpSql());

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);

    private static string SqlArray(IEnumerable<string> values) =>
        string.Join(",", values.Select(value => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"));
}
