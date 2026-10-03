using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dispatch.Infrastructure.Persistence.Migrations;

/// <summary>
/// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03 (project owner, 2026-10-03: "Función segura a nombre del dueño";
/// "Sí, el dueño lo ve"). When the operator organization of an order (<c>orders.operator_org_id</c>, distinct
/// from <c>owner_org_id</c>) assigns its own driver through DSP-002, the transaction runs with the operator as
/// the only tenant, so the AI-06 policies <c>outbox_tenant</c> and <c>audit_logs_tenant</c> refuse the
/// owner-tagged outbox and audit rows and the whole assignment rolled back. This lane installs the only path
/// that writes those rows for the operator: two SECURITY DEFINER functions owned by the dedicated
/// <c>paqueteria_operator_outbox_executor NOLOGIN BYPASSRLS</c> role (AI-03 §24.3) and executable only by
/// <c>paqueteria_app</c>:
/// <list type="bullet">
/// <item><c>security.append_operator_order_outbox(uuid,uuid,jsonb,text,text,uuid,integer,jsonb,smallint,timestamptz,timestamptz)</c></item>
/// <item><c>security.append_operator_order_audit(uuid,uuid,uuid,text,text,uuid,text,jsonb,timestamptz)</c></item>
/// </list>
/// Each one inserts exactly one row, inside the caller's transaction, with every value supplied by the caller
/// (no default, no RETURNING), and only after it proves in the same call that the caller's transaction-local
/// tenant context is exactly the order's operator, that the actor is an active dispatcher or platform admin of
/// that operator, that the operator differs from the owner, and that the row describes the DSP-002 assignment
/// committed by this same caller at the order's current version (allow-listed topic, audience, action and
/// payload shape, tied to the persisted order, order event and assignment). Anything else raises 42501 and
/// writes nothing. AI-18 creates the role on fresh installations; populated installations predate it, so this
/// lane creates it idempotently and refuses any pre-existing role or table shape that differs from the contract.
/// </summary>
/// <remarks>
/// Rollback drops only the two functions: the role and its column grants are declared by AI-18 and stay inert
/// without them, and the rows already written are legitimate append-only facts that are never touched. After
/// the rollback an operator assignment of another owner's order fails closed exactly as before this lane.
/// </remarks>
[DbContext(typeof(DispatchDbContext))]
[Migration(MigrationId)]
public sealed class AddOperatorOwnerOutboxExecutor : Migration
{
    public const string MigrationId = "20261003000100_AddOperatorOwnerOutboxExecutor";
    public const string ExecutorRole = "paqueteria_operator_outbox_executor";
    public const string DecisionId = "DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03";

    public const string OutboxFunctionSignature =
        "security.append_operator_order_outbox(uuid,uuid,jsonb,text,text,uuid,integer,jsonb,smallint,timestamp with time zone,timestamp with time zone)";

    public const string AuditFunctionSignature =
        "security.append_operator_order_audit(uuid,uuid,uuid,text,text,uuid,text,jsonb,timestamp with time zone)";

    public const string SearchPath = "search_path=pg_catalog, pg_temp";

    public static IReadOnlyList<string> OwnedFunctions { get; } =
        Array.AsReadOnly(new[] { OutboxFunctionSignature, AuditFunctionSignature });

    /// <summary>The executor's exact column grants (schema.table.column:privilege), ordered as the catalog check sorts them.</summary>
    public static IReadOnlyList<string> ExecutorColumnGrants { get; } = Array.AsReadOnly(new[]
    {
        "dispatch.assignments.assignment_type:SELECT",
        "dispatch.assignments.cost_cents:SELECT",
        "dispatch.assignments.driver_id:SELECT",
        "dispatch.assignments.id:SELECT",
        "dispatch.assignments.operator_org_id:SELECT",
        "dispatch.assignments.order_id:SELECT",
        "dispatch.assignments.owner_org_id:SELECT",
        "dispatch.assignments.status:SELECT",
        "identity.users.id:SELECT",
        "identity.users.status:SELECT",
        "orders.order_events.actor_id:SELECT",
        "orders.order_events.aggregate_version:SELECT",
        "orders.order_events.event_type:SELECT",
        "orders.order_events.id:SELECT",
        "orders.order_events.occurred_at:SELECT",
        "orders.order_events.operator_org_id:SELECT",
        "orders.order_events.order_id:SELECT",
        "orders.order_events.owner_org_id:SELECT",
        "orders.order_events.payload:SELECT",
        "orders.order_events.public_event_code:SELECT",
        "orders.orders.id:SELECT",
        "orders.orders.operator_org_id:SELECT",
        "orders.orders.owner_org_id:SELECT",
        "orders.orders.public_id:SELECT",
        "orders.orders.status:SELECT",
        "orders.orders.version:SELECT",
        "organizations.organization_memberships.organization_id:SELECT",
        "organizations.organization_memberships.role:SELECT",
        "organizations.organization_memberships.status:SELECT",
        "organizations.organization_memberships.user_id:SELECT",
        "platform.audit_logs.action:INSERT",
        "platform.audit_logs.action:SELECT",
        "platform.audit_logs.actor_id:INSERT",
        "platform.audit_logs.entity_id:INSERT",
        "platform.audit_logs.entity_id:SELECT",
        "platform.audit_logs.entity_type:INSERT",
        "platform.audit_logs.entity_type:SELECT",
        "platform.audit_logs.id:INSERT",
        "platform.audit_logs.occurred_at:INSERT",
        "platform.audit_logs.org_id:INSERT",
        "platform.audit_logs.org_id:SELECT",
        "platform.audit_logs.payload_redacted:INSERT",
        "platform.audit_logs.payload_redacted:SELECT",
        "platform.audit_logs.request_id:INSERT",
        "platform.outbox_events.aggregate_id:INSERT",
        "platform.outbox_events.aggregate_id:SELECT",
        "platform.outbox_events.aggregate_type:INSERT",
        "platform.outbox_events.aggregate_type:SELECT",
        "platform.outbox_events.aggregate_version:INSERT",
        "platform.outbox_events.aggregate_version:SELECT",
        "platform.outbox_events.attempts:INSERT",
        "platform.outbox_events.available_at:INSERT",
        "platform.outbox_events.created_at:INSERT",
        "platform.outbox_events.id:INSERT",
        "platform.outbox_events.last_error:INSERT",
        "platform.outbox_events.lease_expires_at:INSERT",
        "platform.outbox_events.lease_token:INSERT",
        "platform.outbox_events.locked_at:INSERT",
        "platform.outbox_events.locked_by:INSERT",
        "platform.outbox_events.owner_org_id:INSERT",
        "platform.outbox_events.owner_org_id:SELECT",
        "platform.outbox_events.payload:INSERT",
        "platform.outbox_events.priority:INSERT",
        "platform.outbox_events.processed_at:INSERT",
        "platform.outbox_events.status:INSERT",
        "platform.outbox_events.tenant_context:INSERT",
        "platform.outbox_events.topic:INSERT",
        "platform.outbox_events.topic:SELECT",
    });

    /// <summary>
    /// The grant statements, identical to the ones AI-18 declares for fresh installations; the verification
    /// block below proves the resulting catalog ACL is exactly <see cref="ExecutorColumnGrants"/>.
    /// </summary>
    public const string GrantSql =
        """
        REVOKE paqueteria_operator_outbox_executor FROM paqueteria_app, paqueteria_worker;
        GRANT USAGE ON SCHEMA identity,organizations,orders,dispatch,platform TO paqueteria_operator_outbox_executor;
        GRANT SELECT (id,status) ON identity.users TO paqueteria_operator_outbox_executor;
        GRANT SELECT (user_id,organization_id,role,status) ON organizations.organization_memberships TO paqueteria_operator_outbox_executor;
        GRANT SELECT (id,public_id,owner_org_id,operator_org_id,status,version) ON orders.orders TO paqueteria_operator_outbox_executor;
        GRANT SELECT (id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,public_event_code,payload,actor_id,occurred_at) ON orders.order_events TO paqueteria_operator_outbox_executor;
        GRANT SELECT (id,order_id,owner_org_id,operator_org_id,driver_id,assignment_type,status,cost_cents) ON dispatch.assignments TO paqueteria_operator_outbox_executor;
        GRANT SELECT (owner_org_id,topic,aggregate_type,aggregate_id,aggregate_version) ON platform.outbox_events TO paqueteria_operator_outbox_executor;
        GRANT INSERT (id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,last_error,created_at,processed_at) ON platform.outbox_events TO paqueteria_operator_outbox_executor;
        GRANT SELECT (org_id,action,entity_type,entity_id,payload_redacted) ON platform.audit_logs TO paqueteria_operator_outbox_executor;
        GRANT INSERT (id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at) ON platform.audit_logs TO paqueteria_operator_outbox_executor;
        """;

    /// <summary>
    /// Shared preamble of both functions: the caller's transaction-local tenant context must be exactly one
    /// organization with an internal user, and that user must be an ACTIVE user with an ACTIVE DISPATCHER or
    /// PLATFORM_ADMIN membership of it (DSP-002 roles). The two values are read with the same expressions as
    /// security.app_allowed_org and security.app_current_user (AI-06), so a malformed setting raises.
    /// </summary>
    private const string ContextPreamble =
        """
          v_orgs := COALESCE(NULLIF(pg_catalog.current_setting('app.current_org_ids', true),'')::uuid[], ARRAY[]::uuid[]);
          v_actor := NULLIF(pg_catalog.current_setting('app.current_user_id', true),'')::uuid;
          IF pg_catalog.cardinality(v_orgs) <> 1 OR v_orgs[1] IS NULL OR v_actor IS NULL THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_CONTEXT_REFUSED';
          END IF;
          v_operator := v_orgs[1];

          IF NOT EXISTS (
            SELECT 1
            FROM identity.users u
            JOIN organizations.organization_memberships m ON m.user_id = u.id
            WHERE u.id = v_actor
              AND u.status = 'ACTIVE'
              AND m.organization_id = v_operator
              AND m.status = 'ACTIVE'
              AND m.role IN ('DISPATCHER','PLATFORM_ADMIN')
          ) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_ACTOR_REFUSED';
          END IF;
        """;

    /// <summary>
    /// Shared order evidence: the order exists, its operator is the caller's only organization and differs from
    /// its owner, the given owner is the order owner, the order is ASSIGNED, and the ORDER_STATUS_CHANGED event at
    /// the order's current version was written by this actor for this operator and names an ACCEPTED OWN
    /// assignment of this order whose operator is the caller.
    /// </summary>
    private const string OrderEvidence =
        """
          SELECT o.id, o.public_id, o.owner_org_id, o.operator_org_id, o.status, o.version
          INTO v_order
          FROM orders.orders o
          WHERE o.id = v_order_id;
          IF NOT FOUND
             OR v_order.operator_org_id IS NULL
             OR v_order.operator_org_id <> v_operator
             OR v_order.owner_org_id = v_order.operator_org_id
             OR v_order.owner_org_id IS DISTINCT FROM v_owner
             OR v_order.status <> 'ASSIGNED' THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_ORDER_REFUSED';
          END IF;

          SELECT e.id, e.payload, e.occurred_at
          INTO v_event
          FROM orders.order_events e
          WHERE e.order_id = v_order.id
            AND e.aggregate_version = v_order.version
            AND e.owner_org_id = v_order.owner_org_id
            AND e.operator_org_id = v_order.operator_org_id
            AND e.event_type = 'ORDER_STATUS_CHANGED'
            AND e.public_event_code IS NULL
            AND e.actor_id = v_actor
            AND pg_catalog.jsonb_typeof(e.payload) = 'object'
            AND e.payload ->> 'new_status' = 'ASSIGNED'
            AND (e.payload ->> 'assignment_id') ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
          IF NOT FOUND THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_EVENT_REFUSED';
          END IF;

          SELECT a.id, a.driver_id, a.status, a.assignment_type, a.cost_cents
          INTO v_assignment
          FROM dispatch.assignments a
          WHERE a.id = (v_event.payload ->> 'assignment_id')::uuid
            AND a.order_id = v_order.id
            AND a.owner_org_id = v_order.owner_org_id
            AND a.operator_org_id = v_order.operator_org_id
            AND a.assignment_type = 'OWN'
            AND a.status = 'ACCEPTED';
          IF NOT FOUND THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_ASSIGNMENT_REFUSED';
          END IF;
        """;

    public const string OutboxFunctionSql =
        $$"""
        CREATE OR REPLACE FUNCTION security.append_operator_order_outbox(
          p_id uuid,
          p_owner_org_id uuid,
          p_tenant_context jsonb,
          p_topic text,
          p_aggregate_type text,
          p_aggregate_id uuid,
          p_aggregate_version integer,
          p_payload jsonb,
          p_priority smallint,
          p_available_at timestamptz,
          p_created_at timestamptz)
        RETURNS void
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, pg_temp
        AS $function$
        DECLARE
          v_orgs uuid[];
          v_actor uuid;
          v_operator uuid;
          v_owner uuid := p_owner_org_id;
          v_order_id uuid := p_aggregate_id;
          v_order record;
          v_event record;
          v_assignment record;
          v_keys text[];
        BEGIN
          IF p_id IS NULL OR p_owner_org_id IS NULL OR p_tenant_context IS NULL OR p_topic IS NULL
             OR p_aggregate_type IS NULL OR p_aggregate_id IS NULL OR p_aggregate_version IS NULL
             OR p_payload IS NULL OR p_priority IS NULL OR p_available_at IS NULL OR p_created_at IS NULL THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_INPUT_REFUSED';
          END IF;

        {{ContextPreamble}}
        {{OrderEvidence}}
          -- The row must describe the transition just committed at the order's current version, for the owner's
          -- operations audience only, at the event's own instant, with the DSP-002 priority.
          IF p_aggregate_type <> 'Order'
             OR p_aggregate_version <> v_order.version
             OR p_priority <> 50
             OR p_available_at <> v_event.occurred_at
             OR p_created_at <> v_event.occurred_at
             OR p_tenant_context <> pg_catalog.jsonb_build_object(
                  'organization_ids', pg_catalog.jsonb_build_array(v_order.owner_org_id::text))
             OR pg_catalog.jsonb_typeof(p_payload) <> 'object' THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_OUTBOX_REFUSED';
          END IF;

          SELECT array_agg(k ORDER BY k COLLATE "C") INTO v_keys FROM pg_catalog.jsonb_object_keys(p_payload) k;
          IF p_topic = 'orders.status-changed' THEN
            IF v_keys IS DISTINCT FROM ARRAY['assignment_id','authorized_driver_id','new_status','occurred_at',
                   'order_event_id','order_id','previous_status','public_event_code','public_order_id','schema_version']
               OR p_payload ->> 'schema_version' IS DISTINCT FROM 'order-status-changed-v1'
               OR p_payload ->> 'order_event_id' IS DISTINCT FROM v_event.id::text
               OR p_payload ->> 'order_id' IS DISTINCT FROM v_order.id::text
               OR p_payload ->> 'public_order_id' IS DISTINCT FROM v_order.public_id
               OR p_payload ->> 'previous_status' IS DISTINCT FROM v_event.payload ->> 'previous_status'
               OR pg_catalog.jsonb_typeof(p_payload -> 'previous_status') IS DISTINCT FROM 'string'
               OR p_payload ->> 'new_status' IS DISTINCT FROM 'ASSIGNED'
               OR pg_catalog.jsonb_typeof(p_payload -> 'public_event_code') IS DISTINCT FROM 'null'
               OR p_payload ->> 'authorized_driver_id' IS DISTINCT FROM v_assignment.driver_id::text
               OR p_payload ->> 'assignment_id' IS DISTINCT FROM v_assignment.id::text
               OR pg_catalog.jsonb_typeof(p_payload -> 'occurred_at') IS DISTINCT FROM 'string'
               OR (p_payload ->> 'occurred_at')::timestamptz IS DISTINCT FROM v_event.occurred_at THEN
              RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_PAYLOAD_REFUSED';
            END IF;
          ELSIF p_topic = 'orders.timeline-event-added' THEN
            IF v_keys IS DISTINCT FROM ARRAY['category','occurred_at','order_id','schema_version','summary',
                   'timeline_event_id']
               OR p_payload ->> 'schema_version' IS DISTINCT FROM 'order-timeline-event-added-v1'
               OR p_payload ->> 'order_id' IS DISTINCT FROM v_order.id::text
               OR p_payload ->> 'timeline_event_id' IS DISTINCT FROM v_event.id::text
               OR p_payload ->> 'category' IS DISTINCT FROM 'ORDER_STATUS'
               OR p_payload ->> 'summary' IS DISTINCT FROM 'Order status changed to ASSIGNED.'
               OR pg_catalog.jsonb_typeof(p_payload -> 'occurred_at') IS DISTINCT FROM 'string'
               OR (p_payload ->> 'occurred_at')::timestamptz IS DISTINCT FROM v_event.occurred_at THEN
              RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_PAYLOAD_REFUSED';
            END IF;
          ELSIF p_topic = 'dispatch.assignment-changed' THEN
            IF v_keys IS DISTINCT FROM ARRAY['assignment_id','assignment_status','driver_id','occurred_at',
                   'order_id','schema_version']
               OR p_payload ->> 'schema_version' IS DISTINCT FROM 'assignment-changed-v1'
               OR p_payload ->> 'order_id' IS DISTINCT FROM v_order.id::text
               OR p_payload ->> 'assignment_id' IS DISTINCT FROM v_assignment.id::text
               OR p_payload ->> 'driver_id' IS DISTINCT FROM v_assignment.driver_id::text
               OR p_payload ->> 'assignment_status' IS DISTINCT FROM v_assignment.status
               OR pg_catalog.jsonb_typeof(p_payload -> 'occurred_at') IS DISTINCT FROM 'string'
               OR (p_payload ->> 'occurred_at')::timestamptz IS DISTINCT FROM v_event.occurred_at THEN
              RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_PAYLOAD_REFUSED';
            END IF;
          ELSE
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_TOPIC_REFUSED';
          END IF;

          -- Exactly once per topic and order version.
          IF EXISTS (
            SELECT 1 FROM platform.outbox_events x
            WHERE x.owner_org_id = v_order.owner_org_id
              AND x.topic = p_topic
              AND x.aggregate_type = 'Order'
              AND x.aggregate_id = v_order.id
              AND x.aggregate_version = v_order.version
          ) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_DUPLICATE_REFUSED';
          END IF;

          INSERT INTO platform.outbox_events(
            id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,
            priority,status,attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,
            last_error,created_at,processed_at)
          VALUES (
            p_id,v_order.owner_org_id,p_tenant_context,p_topic,'Order',v_order.id,v_order.version,p_payload,
            50,'PENDING',0,p_available_at,NULL,NULL,NULL,NULL,
            NULL,p_created_at,NULL);
        END
        $function$;
        """;

    public const string AuditFunctionSql =
        $$"""
        CREATE OR REPLACE FUNCTION security.append_operator_order_audit(
          p_id uuid,
          p_org_id uuid,
          p_actor_id uuid,
          p_action text,
          p_entity_type text,
          p_entity_id uuid,
          p_request_id text,
          p_payload_redacted jsonb,
          p_occurred_at timestamptz)
        RETURNS void
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, pg_temp
        AS $function$
        DECLARE
          v_orgs uuid[];
          v_actor uuid;
          v_operator uuid;
          v_owner uuid := p_org_id;
          v_order_id uuid;
          v_order record;
          v_event record;
          v_assignment record;
          v_keys text[];
        BEGIN
          IF p_id IS NULL OR p_org_id IS NULL OR p_actor_id IS NULL OR p_action IS NULL
             OR p_entity_type IS NULL OR p_entity_id IS NULL OR p_payload_redacted IS NULL
             OR p_occurred_at IS NULL THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_INPUT_REFUSED';
          END IF;

        {{ContextPreamble}}
          IF p_actor_id <> v_actor THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_ACTOR_REFUSED';
          END IF;

          IF p_action = 'ASSIGNMENT_CREATED' AND p_entity_type = 'Assignment' THEN
            SELECT a.order_id INTO v_order_id FROM dispatch.assignments a WHERE a.id = p_entity_id;
          ELSIF p_action = 'ORDER_STATUS_CHANGED' AND p_entity_type = 'Order' THEN
            v_order_id := p_entity_id;
          ELSE
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_ACTION_REFUSED';
          END IF;

        {{OrderEvidence}}
          IF p_occurred_at <> v_event.occurred_at OR pg_catalog.jsonb_typeof(p_payload_redacted) <> 'object' THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_AUDIT_REFUSED';
          END IF;

          SELECT array_agg(k ORDER BY k COLLATE "C") INTO v_keys FROM pg_catalog.jsonb_object_keys(p_payload_redacted) k;
          IF p_action = 'ASSIGNMENT_CREATED' THEN
            IF p_entity_id <> v_assignment.id
               OR v_keys IS DISTINCT FROM ARRAY['assignment_id','assignment_type','cost_cents','driver_id',
                   'operator_organization_id','order_id','owner_organization_id','policy_version','request_id','status']
               OR p_payload_redacted ->> 'assignment_id' IS DISTINCT FROM v_assignment.id::text
               OR p_payload_redacted ->> 'order_id' IS DISTINCT FROM v_order.id::text
               OR p_payload_redacted ->> 'driver_id' IS DISTINCT FROM v_assignment.driver_id::text
               OR p_payload_redacted ->> 'owner_organization_id' IS DISTINCT FROM v_order.owner_org_id::text
               OR p_payload_redacted ->> 'operator_organization_id' IS DISTINCT FROM v_order.operator_org_id::text
               OR p_payload_redacted ->> 'assignment_type' IS DISTINCT FROM v_assignment.assignment_type
               OR p_payload_redacted ->> 'status' IS DISTINCT FROM v_assignment.status
               OR (p_payload_redacted -> 'cost_cents') IS DISTINCT FROM pg_catalog.to_jsonb(v_assignment.cost_cents)
               OR pg_catalog.jsonb_typeof(p_payload_redacted -> 'policy_version') IS DISTINCT FROM 'string'
               OR pg_catalog.jsonb_typeof(p_payload_redacted -> 'request_id') NOT IN ('string','null') THEN
              RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_PAYLOAD_REFUSED';
            END IF;

            IF EXISTS (
              SELECT 1 FROM platform.audit_logs l
              WHERE l.org_id = v_order.owner_org_id
                AND l.action = 'ASSIGNMENT_CREATED'
                AND l.entity_type = 'Assignment'
                AND l.entity_id = v_assignment.id
            ) THEN
              RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_DUPLICATE_REFUSED';
            END IF;
          ELSE
            IF p_entity_id <> v_order.id
               OR v_keys IS DISTINCT FROM ARRAY['assignment_id','new_status','new_version','order_id',
                   'previous_status','previous_version','request_id']
               OR p_payload_redacted ->> 'order_id' IS DISTINCT FROM v_order.id::text
               OR p_payload_redacted ->> 'assignment_id' IS DISTINCT FROM v_assignment.id::text
               OR p_payload_redacted ->> 'previous_status' IS DISTINCT FROM v_event.payload ->> 'previous_status'
               OR pg_catalog.jsonb_typeof(p_payload_redacted -> 'previous_status') IS DISTINCT FROM 'string'
               OR p_payload_redacted ->> 'new_status' IS DISTINCT FROM 'ASSIGNED'
               OR (p_payload_redacted -> 'new_version') IS DISTINCT FROM pg_catalog.to_jsonb(v_order.version)
               OR (p_payload_redacted -> 'previous_version') IS DISTINCT FROM pg_catalog.to_jsonb(v_order.version - 1)
               OR pg_catalog.jsonb_typeof(p_payload_redacted -> 'request_id') NOT IN ('string','null') THEN
              RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_PAYLOAD_REFUSED';
            END IF;

            IF EXISTS (
              SELECT 1 FROM platform.audit_logs l
              WHERE l.org_id = v_order.owner_org_id
                AND l.action = 'ORDER_STATUS_CHANGED'
                AND l.entity_type = 'Order'
                AND l.entity_id = v_order.id
                AND (l.payload_redacted -> 'new_version') = pg_catalog.to_jsonb(v_order.version)
            ) THEN
              RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'DSP_OPERATOR_OWNER_DUPLICATE_REFUSED';
            END IF;
          END IF;

          INSERT INTO platform.audit_logs(
            id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
          VALUES (
            p_id,v_order.owner_org_id,v_actor,p_action,p_entity_type,p_entity_id,p_request_id,p_payload_redacted,
            p_occurred_at);
        END
        $function$;
        """;

    public static readonly string UpSql =
        $$"""
        RESET ROLE;

        DO $adoption$
        DECLARE
          actual text[];
        BEGIN
          IF to_regclass('platform.outbox_events') IS NULL OR to_regclass('platform.audit_logs') IS NULL
             OR to_regclass('orders.orders') IS NULL OR to_regclass('orders.order_events') IS NULL
             OR to_regclass('dispatch.assignments') IS NULL
             OR to_regclass('organizations.organization_memberships') IS NULL
             OR to_regclass('identity.users') IS NULL THEN
            RAISE EXCEPTION 'DSP-OPERATOR-OWNER-OUTBOX requires the canonical AI-06 tables';
          END IF;

          SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || data_type || ':' || is_nullable
                   ORDER BY table_schema, table_name, column_name)
          INTO actual
          FROM information_schema.columns
          WHERE (table_schema, table_name) IN (('platform','outbox_events'),('platform','audit_logs'));
          IF actual IS DISTINCT FROM ARRAY[
            'platform.audit_logs.action:text:NO',
            'platform.audit_logs.actor_id:uuid:YES',
            'platform.audit_logs.entity_id:uuid:NO',
            'platform.audit_logs.entity_type:text:NO',
            'platform.audit_logs.id:uuid:NO',
            'platform.audit_logs.occurred_at:timestamp with time zone:NO',
            'platform.audit_logs.org_id:uuid:NO',
            'platform.audit_logs.payload_redacted:jsonb:NO',
            'platform.audit_logs.request_id:text:YES',
            'platform.outbox_events.aggregate_id:uuid:NO',
            'platform.outbox_events.aggregate_type:text:NO',
            'platform.outbox_events.aggregate_version:integer:YES',
            'platform.outbox_events.attempts:integer:NO',
            'platform.outbox_events.available_at:timestamp with time zone:NO',
            'platform.outbox_events.created_at:timestamp with time zone:NO',
            'platform.outbox_events.id:uuid:NO',
            'platform.outbox_events.last_error:text:YES',
            'platform.outbox_events.lease_expires_at:timestamp with time zone:YES',
            'platform.outbox_events.lease_token:uuid:YES',
            'platform.outbox_events.locked_at:timestamp with time zone:YES',
            'platform.outbox_events.locked_by:text:YES',
            'platform.outbox_events.owner_org_id:uuid:NO',
            'platform.outbox_events.payload:jsonb:NO',
            'platform.outbox_events.priority:smallint:NO',
            'platform.outbox_events.processed_at:timestamp with time zone:YES',
            'platform.outbox_events.status:text:NO',
            'platform.outbox_events.tenant_context:jsonb:NO',
            'platform.outbox_events.topic:text:NO'
          ] THEN
            RAISE EXCEPTION 'platform.outbox_events or platform.audit_logs columns do not match the canonical AI-06 contract';
          END IF;

          SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || data_type
                   ORDER BY table_schema, table_name, column_name)
          INTO actual
          FROM information_schema.columns
          WHERE (table_schema = 'orders' AND table_name = 'orders'
                  AND column_name IN ('id','public_id','owner_org_id','operator_org_id','status','version'))
             OR (table_schema = 'orders' AND table_name = 'order_events'
                  AND column_name IN ('id','order_id','owner_org_id','operator_org_id','aggregate_version','event_type',
                                      'public_event_code','payload','actor_id','occurred_at'))
             OR (table_schema = 'dispatch' AND table_name = 'assignments'
                  AND column_name IN ('id','order_id','owner_org_id','operator_org_id','driver_id','assignment_type',
                                      'status','cost_cents'));
          IF actual IS DISTINCT FROM ARRAY[
            'dispatch.assignments.assignment_type:text',
            'dispatch.assignments.cost_cents:bigint',
            'dispatch.assignments.driver_id:uuid',
            'dispatch.assignments.id:uuid',
            'dispatch.assignments.operator_org_id:uuid',
            'dispatch.assignments.order_id:uuid',
            'dispatch.assignments.owner_org_id:uuid',
            'dispatch.assignments.status:text',
            'orders.order_events.actor_id:uuid',
            'orders.order_events.aggregate_version:integer',
            'orders.order_events.event_type:text',
            'orders.order_events.id:uuid',
            'orders.order_events.occurred_at:timestamp with time zone',
            'orders.order_events.operator_org_id:uuid',
            'orders.order_events.order_id:uuid',
            'orders.order_events.owner_org_id:uuid',
            'orders.order_events.payload:jsonb',
            'orders.order_events.public_event_code:text',
            'orders.orders.id:uuid',
            'orders.orders.operator_org_id:uuid',
            'orders.orders.owner_org_id:uuid',
            'orders.orders.public_id:text',
            'orders.orders.status:text',
            'orders.orders.version:integer'
          ] THEN
            RAISE EXCEPTION 'orders or dispatch columns do not match the canonical AI-06 contract';
          END IF;

          IF EXISTS (
            SELECT 1 FROM pg_class
            WHERE oid IN ('platform.outbox_events'::regclass, 'platform.audit_logs'::regclass)
              AND (NOT relrowsecurity OR NOT relforcerowsecurity)
          ) THEN
            RAISE EXCEPTION 'DSP-OPERATOR-OWNER-OUTBOX requires FORCE ROW LEVEL SECURITY on the outbox and audit tables';
          END IF;

          -- The definer writes only through INSERT; the append-only and immutable-content triggers must stay.
          IF NOT EXISTS (
               SELECT 1 FROM pg_trigger
               WHERE tgrelid = 'platform.audit_logs'::regclass AND tgname = 'audit_logs_append_only' AND tgenabled <> 'D')
             OR NOT EXISTS (
               SELECT 1 FROM pg_trigger
               WHERE tgrelid = 'platform.outbox_events'::regclass AND tgname = 'outbox_content_immutable' AND tgenabled <> 'D')
          THEN
            RAISE EXCEPTION 'DSP-OPERATOR-OWNER-OUTBOX requires the AI-06 append-only and outbox content triggers';
          END IF;
        END
        $adoption$;

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_operator_outbox_executor NOLOGIN BYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $existing_role$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_operator_outbox_executor'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR NOT rolbypassrls OR NOT rolinherit)
          ) THEN
            RAISE EXCEPTION 'paqueteria_operator_outbox_executor exists with attributes outside the DSP-OPERATOR-OWNER-OUTBOX contract';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.member='paqueteria_operator_outbox_executor'::regrole
          ) THEN
            RAISE EXCEPTION 'paqueteria_operator_outbox_executor must not inherit any other role';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_operator_outbox_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner='paqueteria_operator_outbox_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner='paqueteria_operator_outbox_executor'::regrole)
             OR EXISTS (
               SELECT 1 FROM pg_proc
               WHERE proowner='paqueteria_operator_outbox_executor'::regrole
                 AND oid IS DISTINCT FROM to_regprocedure('{{OutboxFunctionSignature}}')
                 AND oid IS DISTINCT FROM to_regprocedure('{{AuditFunctionSignature}}')
             ) THEN
            RAISE EXCEPTION 'paqueteria_operator_outbox_executor already owns objects outside the DSP-OPERATOR-OWNER-OUTBOX contract';
          END IF;
        END
        $existing_role$;

        {{GrantSql}}

        {{OutboxFunctionSql}}

        {{AuditFunctionSql}}

        -- ACL first, while the deploying role still owns the functions: ALTER ... OWNER rewrites the grantor to
        -- the new owner, and a managed-service deployer then needs no inherited executor rights.
        REVOKE ALL ON FUNCTION {{OutboxFunctionSignature}} FROM PUBLIC;
        REVOKE ALL ON FUNCTION {{AuditFunctionSignature}} FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION {{OutboxFunctionSignature}} TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION {{AuditFunctionSignature}} TO paqueteria_app;
        ALTER FUNCTION {{OutboxFunctionSignature}} OWNER TO paqueteria_operator_outbox_executor;
        ALTER FUNCTION {{AuditFunctionSignature}} OWNER TO paqueteria_operator_outbox_executor;

        DO $verify$
        DECLARE
          executor oid := 'paqueteria_operator_outbox_executor'::regrole;
          fn oid;
        BEGIN
          FOREACH fn IN ARRAY ARRAY[
            to_regprocedure('{{OutboxFunctionSignature}}'),
            to_regprocedure('{{AuditFunctionSignature}}')]::oid[]
          LOOP
            IF fn IS NULL
               OR (SELECT proowner FROM pg_proc WHERE oid=fn) <> executor
               OR NOT (SELECT prosecdef FROM pg_proc WHERE oid=fn)
               OR (SELECT proconfig FROM pg_proc WHERE oid=fn) IS DISTINCT FROM ARRAY['{{SearchPath}}']
               OR (SELECT prosrc FROM pg_proc WHERE oid=fn) ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR (SELECT prosrc FROM pg_proc WHERE oid=fn) ~* 'RETURNING'
               OR (SELECT string_agg(
                     CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END
                       || ':' || acl.privilege_type || ':' || acl.is_grantable::text, ','
                     ORDER BY (CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END) COLLATE "C")
                   FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) acl WHERE p.oid=fn)
                  IS DISTINCT FROM 'paqueteria_app:EXECUTE:false,paqueteria_operator_outbox_executor:EXECUTE:false'
            THEN
              RAISE EXCEPTION 'DSP-OPERATOR-OWNER-OUTBOX function security verification failed';
            END IF;
          END LOOP;

          -- Exactly the column grants of the contract, none grantable, and no table-wide grant anywhere.
          IF EXISTS (
               SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) acl
               WHERE c.relacl IS NOT NULL AND acl.grantee=executor)
             OR (SELECT array_agg(n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type
                   || CASE WHEN acl.is_grantable THEN ':grantable' ELSE '' END
                   ORDER BY (n.nspname || '.' || c.relname || '.' || a.attname || ':' || acl.privilege_type) COLLATE "C")
                 FROM pg_attribute a
                 JOIN pg_class c ON c.oid=a.attrelid
                 JOIN pg_namespace n ON n.oid=c.relnamespace
                 CROSS JOIN LATERAL aclexplode(a.attacl) acl
                 WHERE a.attacl IS NOT NULL AND acl.grantee=executor)
               IS DISTINCT FROM ARRAY[{{string.Join(",", ExecutorColumnGrants.Select(grant => "'" + grant + "'"))}}]
          THEN
            RAISE EXCEPTION 'paqueteria_operator_outbox_executor privileges differ from the DSP-OPERATOR-OWNER-OUTBOX contract';
          END IF;

          -- USAGE on exactly the five schemas it reads or writes; CREATE nowhere once any E-002 temporary grant
          -- is revoked by the deployer (asserted again after the bridge cleanup).
          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','security','extensions')
              AND has_schema_privilege(executor, n.oid, 'USAGE')
                  IS DISTINCT FROM (n.nspname IN ('identity','organizations','orders','dispatch','platform'))
          ) OR EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','extensions')
              AND has_schema_privilege(executor, n.oid, 'CREATE')
          ) THEN
            RAISE EXCEPTION 'paqueteria_operator_outbox_executor schema privileges differ from the DSP-OPERATOR-OWNER-OUTBOX contract';
          END IF;

          IF pg_has_role('paqueteria_app', executor, 'MEMBER')
             OR pg_has_role('paqueteria_worker', executor, 'MEMBER')
             OR pg_has_role('paqueteria_bootstrap', executor, 'MEMBER')
             OR (SELECT count(*) FROM pg_proc WHERE proowner=executor) <> 2
          THEN
            RAISE EXCEPTION 'paqueteria_operator_outbox_executor ownership or membership differs from the DSP-OPERATOR-OWNER-OUTBOX contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// Rollback: only the two functions go. The role and its grants are declared by AI-18 and are inert without
    /// them; every outbox and audit row already written stays (append-only facts, never rewritten). After the
    /// rollback an operator assignment of another owner's order fails closed (RLS 42501) exactly as before.
    /// </summary>
    public const string DownSql =
        $$"""
        RESET ROLE;
        DROP FUNCTION IF EXISTS {{OutboxFunctionSignature}};
        DROP FUNCTION IF EXISTS {{AuditFunctionSignature}};
        SET LOCAL ROLE paqueteria_migrator;
        """;


    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
