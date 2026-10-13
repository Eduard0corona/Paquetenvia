using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>
/// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12 (project owner: "Rol propio y mínimo (Recommended)"). The API hosts the
/// realtime outbox dispatchers in process (RTM-002-INPROCESS-DISPATCHER-COLOCATION) and needs a database credential
/// for them; the Worker credential would hand the API every Worker privilege. This lane grants the dedicated runtime
/// role <c>paqueteria_realtime</c> (NOLOGIN NOBYPASSRLS, declared by AI-18) exactly what those dispatchers run:
/// EXECUTE on the lease-token claim, settle and stale-requeue functions of the REALTIME business lane and of the
/// location lane, USAGE on the schemas the evidence reads touch, and SELECT on exactly the columns those reads use to
/// re-derive every audience from committed state under FORCE RLS. It lives in this lane because the lane owns the
/// REALTIME claim and requeue functions (NTF-001) and runs after every lane that adds a table the reads touch.
/// AI-18 creates the role on fresh installations; populated installations predate it, so this lane creates it
/// idempotently and refuses any pre-existing role or privilege shape that differs from the contract.
/// </summary>
/// <remarks>
/// Rollback revokes every privilege this lane granted. The role stays (AI-18 declares it) and is inert: the API's
/// realtime login can then claim nothing and its readiness check reports the dispatcher unhealthy. No row is written.
/// NTF-001 rollback blocked: this step adds grants only; it never touches a function, table or row of the lane.
/// </remarks>
[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class AddRealtimeDispatcherRole : Migration
{
    public const string MigrationId = "20261012000100_AddRealtimeDispatcherRole";
    public const string DecisionId = "RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12";
    public const string Role = "paqueteria_realtime";

    /// <summary>The six lease-token lifecycle functions the in-process dispatchers call, in regprocedure form.</summary>
    public static IReadOnlyList<string> ExecutableFunctions { get; } = Array.AsReadOnly(new[]
    {
        "security.claim_realtime_outbox(text,integer,interval)",
        "security.requeue_stale_realtime_outbox(interval,integer,integer)",
        "security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.claim_location_outbox(text,integer,interval)",
        "security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.requeue_stale_location_outbox(interval,integer,integer)",
    });

    /// <summary>The only schemas the role may use: the lifecycle functions and the evidence reads.</summary>
    public static IReadOnlyList<string> UsageSchemas { get; } = Array.AsReadOnly(new[]
    {
        "dispatch", "drivers", "identity", "orders", "organizations", "routes", "security",
    });

    /// <summary>
    /// The exact column grants (schema.table.column:privilege), ordered as the catalog check sorts them (C collation):
    /// every column the evidence reads select, join or filter on, and nothing else.
    /// </summary>
    public static IReadOnlyList<string> ColumnGrants { get; } = Array.AsReadOnly(new[]
    {
        "dispatch.assignments.assignment_type:SELECT",
        "dispatch.assignments.driver_id:SELECT",
        "dispatch.assignments.id:SELECT",
        "dispatch.assignments.operator_org_id:SELECT",
        "dispatch.assignments.order_id:SELECT",
        "dispatch.assignments.owner_org_id:SELECT",
        "dispatch.assignments.status:SELECT",
        "dispatch.external_offers.accepted_by_driver_id:SELECT",
        "dispatch.external_offers.commission_cents:SELECT",
        "dispatch.external_offers.expires_at:SELECT",
        "dispatch.external_offers.id:SELECT",
        "dispatch.external_offers.owner_org_id:SELECT",
        "dispatch.external_offers.status:SELECT",
        "dispatch.external_offers.version:SELECT",
        "drivers.driver_positions.accuracy_m:SELECT",
        "drivers.driver_positions.captured_at:SELECT",
        "drivers.driver_positions.driver_id:SELECT",
        "drivers.driver_positions.id:SELECT",
        "drivers.driver_positions.org_id:SELECT",
        "drivers.driver_positions.point:SELECT",
        "drivers.driver_positions.publish_realtime:SELECT",
        "drivers.driver_profiles.driver_type:SELECT",
        "drivers.driver_profiles.id:SELECT",
        "drivers.driver_profiles.org_id:SELECT",
        "drivers.driver_profiles.status:SELECT",
        "drivers.driver_profiles.user_id:SELECT",
        "identity.users.id:SELECT",
        "identity.users.status:SELECT",
        "orders.order_events.aggregate_version:SELECT",
        "orders.order_events.event_type:SELECT",
        "orders.order_events.id:SELECT",
        "orders.order_events.occurred_at:SELECT",
        "orders.order_events.order_id:SELECT",
        "orders.order_events.owner_org_id:SELECT",
        "orders.order_events.payload:SELECT",
        "orders.order_events.public_event_code:SELECT",
        "orders.orders.id:SELECT",
        "orders.orders.operator_org_id:SELECT",
        "orders.orders.owner_org_id:SELECT",
        "orders.orders.public_id:SELECT",
        "organizations.organization_memberships.organization_id:SELECT",
        "organizations.organization_memberships.role:SELECT",
        "organizations.organization_memberships.status:SELECT",
        "organizations.organization_memberships.user_id:SELECT",
        "routes.route_stops.id:SELECT",
        "routes.route_stops.operator_org_id:SELECT",
        "routes.route_stops.route_id:SELECT",
        "routes.route_stops.sequence:SELECT",
        "routes.routes.driver_id:SELECT",
        "routes.routes.id:SELECT",
        "routes.routes.operator_org_id:SELECT",
        "routes.routes.updated_at:SELECT",
        "routes.routes.version:SELECT",
    });

    /// <summary>The tables the evidence reads touch; each must force RLS before the role may read it.</summary>
    public static IReadOnlyList<string> EvidenceTables { get; } = Array.AsReadOnly(ColumnGrants
        .Select(static grant => string.Join('.', grant.Split('.').Take(2)))
        .Distinct(StringComparer.Ordinal)
        .ToArray());

    /// <summary>The column grant statements, one per table, identical in Up and Down.</summary>
    public static string ColumnGrantStatements(string verb, string preposition) => string.Join(
        "\n",
        ColumnGrants
            .GroupBy(static grant => string.Join('.', grant.Split('.').Take(2)), StringComparer.Ordinal)
            .Select(table =>
                $"{verb} SELECT ({string.Join(",", table.Select(static grant => grant.Split('.')[2].Split(':')[0]))}) ON {table.Key} {preposition} paqueteria_realtime;"));

    private static string FunctionList => string.Join(",\n  ", ExecutableFunctions);

    private static string SqlTextArray(IEnumerable<string> values) =>
        "ARRAY[" + string.Join(",", values.Select(static value => "'" + value + "'")) + "]::text[]";

    private static string RegprocedureArray =>
        "ARRAY[" + string.Join(",", ExecutableFunctions.Select(static function => "to_regprocedure('" + function + "')::oid")) + "]";

    /// <summary>The grants this lane owns, issued by each object's owner so the grantor is the owner everywhere.</summary>
    public static readonly string GrantSql =
        $$"""
        SET ROLE paqueteria_migrator;
        GRANT USAGE ON SCHEMA {{string.Join(",", UsageSchemas)}} TO paqueteria_realtime;
        {{ColumnGrantStatements("GRANT", "TO")}}
        SET ROLE paqueteria_outbox_executor;
        GRANT EXECUTE ON FUNCTION
          {{FunctionList}}
          TO paqueteria_realtime;
        RESET ROLE;
        """;

    /// <summary>The exact privilege set, read from the catalog ACLs; any difference raises and writes nothing.</summary>
    public static readonly string VerifySql =
        $$"""
        DO $verify$
        DECLARE
          realtime oid := 'paqueteria_realtime'::regrole;
        BEGIN
          -- Exactly SELECT on the evidence columns, none grantable, and no table-wide grant anywhere.
          IF EXISTS (
               SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
               WHERE c.relacl IS NOT NULL AND acl.grantee=realtime)
             OR (SELECT array_agg(n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type
                   || CASE WHEN acl.is_grantable THEN ':grantable' ELSE '' END
                   ORDER BY (n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type) COLLATE "C")
                 FROM pg_attribute a
                 JOIN pg_class c ON c.oid=a.attrelid
                 JOIN pg_namespace n ON n.oid=c.relnamespace
                 CROSS JOIN LATERAL aclexplode(a.attacl) acl
                 WHERE a.attacl IS NOT NULL AND acl.grantee=realtime)
               IS DISTINCT FROM {{SqlTextArray(ColumnGrants)}}
             OR EXISTS (
               SELECT 1 FROM pg_default_acl d CROSS JOIN LATERAL aclexplode(d.defaclacl) acl
               WHERE acl.grantee=realtime)
          THEN
            RAISE EXCEPTION 'paqueteria_realtime table privileges differ from the RTM-API-REALTIME-DISPATCHER-ROLE contract';
          END IF;

          -- The API gains no read: every column the role reads is readable by paqueteria_app already.
          IF EXISTS (
            SELECT 1 FROM unnest({{SqlTextArray(ColumnGrants)}}) grant_text
            WHERE NOT has_column_privilege(
              'paqueteria_app',
              split_part(grant_text, '.', 1) || '.' || split_part(grant_text, '.', 2),
              split_part(split_part(grant_text, '.', 3), ':', 1),
              'SELECT')
          ) THEN
            RAISE EXCEPTION 'paqueteria_realtime reads a column paqueteria_app cannot read';
          END IF;

          -- Never either outbox table directly (claim, settle and requeue only through the lease-token functions).
          IF has_table_privilege(realtime, 'platform.outbox_events', 'SELECT,INSERT,UPDATE,DELETE')
             OR has_table_privilege(realtime, 'platform.location_outbox_events', 'SELECT,INSERT,UPDATE,DELETE')
          THEN
            RAISE EXCEPTION 'paqueteria_realtime has direct outbox access contrary to the RTM-API-REALTIME-DISPATCHER-ROLE contract';
          END IF;

          -- EXECUTE exactly on the six lease-token functions, none grantable; nothing else beyond what PUBLIC has.
          IF (SELECT array_agg(p.oid ORDER BY p.oid)
              FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl
              WHERE p.proacl IS NOT NULL AND acl.grantee=realtime)
               IS DISTINCT FROM (SELECT array_agg(f ORDER BY f) FROM unnest({{RegprocedureArray}}) f)
             OR EXISTS (
               SELECT 1 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl
               WHERE p.proacl IS NOT NULL AND acl.grantee=realtime
                 AND (acl.privilege_type<>'EXECUTE' OR acl.is_grantable))
             OR EXISTS (
               SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
               WHERE n.nspname IN ('security','platform','notifications','extensions')
                 AND has_function_privilege(realtime, p.oid, 'EXECUTE')
                 AND NOT has_function_privilege('public', p.oid, 'EXECUTE')
                 AND p.oid <> ALL ({{RegprocedureArray}}))
          THEN
            RAISE EXCEPTION 'paqueteria_realtime function privileges differ from the RTM-API-REALTIME-DISPATCHER-ROLE contract';
          END IF;

          -- USAGE on the evidence schemas and security only; CREATE nowhere.
          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','security','extensions')
              AND has_schema_privilege(realtime, n.oid, 'USAGE') IS DISTINCT FROM
                (n.nspname = ANY ({{SqlTextArray(UsageSchemas)}}))
          ) OR EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','security','extensions',
                'public')
              AND has_schema_privilege(realtime, n.oid, 'CREATE')
          ) THEN
            RAISE EXCEPTION 'paqueteria_realtime schema privileges differ from the RTM-API-REALTIME-DISPATCHER-ROLE contract';
          END IF;

          -- It inherits nothing, and no group role (every canonical role is NOLOGIN) can reach it: only LOGIN members.
          IF EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member=realtime)
             OR EXISTS (
               SELECT 1 FROM pg_auth_members m JOIN pg_roles member ON member.oid=m.member
               WHERE m.roleid=realtime AND NOT member.rolcanlogin)
             OR EXISTS (SELECT 1 FROM pg_class WHERE relowner=realtime)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner=realtime)
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner=realtime)
             OR EXISTS (SELECT 1 FROM pg_proc WHERE proowner=realtime)
             OR EXISTS (
               SELECT 1 FROM pg_roles
               WHERE oid=realtime
                 AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                   OR rolbypassrls OR NOT rolinherit))
          THEN
            RAISE EXCEPTION 'paqueteria_realtime ownership, membership or attributes differ from the RTM-API-REALTIME-DISPATCHER-ROLE contract';
          END IF;
        END
        $verify$;
        """;

    public static readonly string UpSql =
        $$"""
        -- NTF-001 rollback blocked: this step only grants the dedicated realtime dispatcher role its exact privileges.
        RESET ROLE;

        DO $adoption$
        DECLARE
          missing text;
        BEGIN
          SELECT string_agg(signature, ', ') INTO missing
          FROM unnest({{SqlTextArray(ExecutableFunctions)}}) signature
          WHERE to_regprocedure(signature) IS NULL
             OR NOT (SELECT p.prosecdef FROM pg_proc p WHERE p.oid=to_regprocedure(signature))
             OR (SELECT pg_get_userbyid(p.proowner) FROM pg_proc p WHERE p.oid=to_regprocedure(signature))
                <> 'paqueteria_outbox_executor';
          IF missing IS NOT NULL THEN
            RAISE EXCEPTION 'RTM-API-REALTIME-DISPATCHER-ROLE requires the canonical lease-token outbox functions: %', missing;
          END IF;

          SELECT string_agg(qualified, ', ') INTO missing
          FROM unnest({{SqlTextArray(EvidenceTables)}}) qualified
          WHERE to_regclass(qualified) IS NULL
             OR NOT EXISTS (
               SELECT 1 FROM pg_class
               WHERE oid=to_regclass(qualified) AND relrowsecurity AND relforcerowsecurity);
          IF missing IS NOT NULL THEN
            RAISE EXCEPTION 'RTM-API-REALTIME-DISPATCHER-ROLE requires FORCE ROW LEVEL SECURITY on %', missing;
          END IF;
        END
        $adoption$;

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_realtime NOLOGIN NOBYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $existing_role$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_realtime'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR rolbypassrls OR NOT rolinherit)
          ) THEN
            RAISE EXCEPTION 'paqueteria_realtime exists with attributes outside the RTM-API-REALTIME-DISPATCHER-ROLE contract';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member='paqueteria_realtime'::regrole) THEN
            RAISE EXCEPTION 'paqueteria_realtime must not inherit any other role';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m JOIN pg_roles member ON member.oid=m.member
            WHERE m.roleid='paqueteria_realtime'::regrole AND NOT member.rolcanlogin
              AND member.rolname NOT IN ('paqueteria_app','paqueteria_worker','paqueteria_bootstrap')
          ) THEN
            RAISE EXCEPTION 'paqueteria_realtime is granted to a group role outside the RTM-API-REALTIME-DISPATCHER-ROLE contract';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_realtime'::regrole)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner='paqueteria_realtime'::regrole)
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner='paqueteria_realtime'::regrole)
             OR EXISTS (SELECT 1 FROM pg_proc WHERE proowner='paqueteria_realtime'::regrole) THEN
            RAISE EXCEPTION 'paqueteria_realtime owns objects outside the RTM-API-REALTIME-DISPATCHER-ROLE contract';
          END IF;
        END
        $existing_role$;

        REVOKE paqueteria_realtime FROM paqueteria_app, paqueteria_worker, paqueteria_bootstrap;

        {{GrantSql}}

        {{VerifySql}}

        SET ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// Rollback: every privilege of the role is revoked by the grantor that issued it; the role stays (AI-18 declares
    /// it) and holds nothing, so the API's realtime login can claim, settle and read nothing.
    /// </summary>
    public static readonly string DownSql =
        $$"""
        -- NTF-001 rollback blocked: only the realtime dispatcher role's own grants are withdrawn.
        RESET ROLE;
        SET ROLE paqueteria_outbox_executor;
        REVOKE EXECUTE ON FUNCTION
          {{FunctionList}}
          FROM paqueteria_realtime;
        SET ROLE paqueteria_migrator;
        {{ColumnGrantStatements("REVOKE", "FROM")}}
        REVOKE USAGE ON SCHEMA {{string.Join(",", UsageSchemas)}} FROM paqueteria_realtime;
        RESET ROLE;

        DO $verify_down$
        DECLARE
          realtime oid := 'paqueteria_realtime'::regrole;
        BEGIN
          IF EXISTS (
               SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
               WHERE c.relacl IS NOT NULL AND acl.grantee=realtime)
             OR EXISTS (
               SELECT 1 FROM pg_attribute a CROSS JOIN LATERAL aclexplode(a.attacl) acl
               WHERE a.attacl IS NOT NULL AND acl.grantee=realtime)
             OR EXISTS (
               SELECT 1 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl
               WHERE p.proacl IS NOT NULL AND acl.grantee=realtime)
             OR EXISTS (
               SELECT 1 FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) acl
               WHERE n.nspacl IS NOT NULL AND acl.grantee=realtime)
          THEN
            RAISE EXCEPTION 'paqueteria_realtime still holds privileges after the RTM-API-REALTIME-DISPATCHER-ROLE rollback';
          END IF;
        END
        $verify_down$;

        SET ROLE paqueteria_migrator;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
