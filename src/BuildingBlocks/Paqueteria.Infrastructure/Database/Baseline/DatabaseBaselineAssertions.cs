using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed record DatabaseAssertionReport(
    string PostgreSqlVersion,
    string PostGisVersion,
    int Checks,
    TimeSpan Duration);

public sealed class DatabaseBaselineAssertions
{
    private static readonly string[] RuntimeRoles = ["paqueteria_app", "paqueteria_worker"];
    private static readonly string[] PrivilegedRoles =
    [
        "paqueteria_migrator",
        "paqueteria_bootstrap",
        "paqueteria_outbox_executor",
        "paqueteria_maintenance",
        "paqueteria_lifecycle_executor",
        "paqueteria_cleanup_executor",
        "paqueteria_registration_executor",
        "paqueteria_session_executor",
        // MDM-001-OPERATOR-LOADER: neither the executor nor the operator grantee may be assumed by a runtime role.
        "paqueteria_master_data_executor",
        "paqueteria_master_data_loader",
        // DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: the operator outbox executor is never assumable at runtime.
        "paqueteria_operator_outbox_executor",
        // ORD-AUTO-CLOSE-2026-10-10: neither is the auto-close executor.
        "paqueteria_auto_close_executor",
        // NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: nor the receipt executor (AI-18 recipient notices step 3).
        "paqueteria_notification_receipt_executor",
    ];

    /// <summary>
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the API's realtime dispatcher runtime role. AI-18 creates it with no
    /// privilege; the Notifications lane step 20261012000100_AddRealtimeDispatcherRole grants exactly
    /// <see cref="RealtimeDispatcherFunctions"/>, <see cref="RealtimeDispatcherUsageSchemas"/> and
    /// <see cref="RealtimeDispatcherColumnGrants"/>, and its rollback revokes them.
    /// </summary>
    public const string RealtimeDispatcherRole = "paqueteria_realtime";

    /// <summary>RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the six lease-token lifecycle functions the role executes.</summary>
    public static IReadOnlyList<string> RealtimeDispatcherFunctions { get; } = Array.AsReadOnly(new[]
    {
        "security.claim_realtime_outbox(text,integer,interval)",
        "security.requeue_stale_realtime_outbox(interval,integer,integer)",
        "security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.claim_location_outbox(text,integer,interval)",
        "security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.requeue_stale_location_outbox(interval,integer,integer)",
    });

    /// <summary>RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the only schemas the role may use.</summary>
    public static IReadOnlyList<string> RealtimeDispatcherUsageSchemas { get; } = Array.AsReadOnly(new[]
    {
        "dispatch", "drivers", "identity", "orders", "organizations", "routes", "security",
    });

    /// <summary>
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the role's exact column grants (schema.table.column:privilege), the
    /// columns the realtime evidence reads select, join or filter on under FORCE RLS. Each is also readable by
    /// <c>paqueteria_app</c>, so these table reads give the API no new column (its two claim functions, by design, return
    /// whole REALTIME and location outbox rows of every tenant; see AI-18).
    /// </summary>
    public static IReadOnlyList<string> RealtimeDispatcherColumnGrants { get; } = Array.AsReadOnly(new[]
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

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-18 recipient notices step 3, amended by O5): the receipt executor.
    /// </summary>
    public const string ReceiptExecutor = "paqueteria_notification_receipt_executor";

    /// <summary>NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: the three functions only <c>paqueteria_app</c> executes.</summary>
    public static IReadOnlyList<string> ReceiptExecutorApiFunctions { get; } = Array.AsReadOnly(new[]
    {
        "security.record_messaging_receipt(text,text,text,text,timestamp with time zone)",
        "security.record_recipient_opt_out(text,text,bytea,integer,bytea[])",
        "security.request_recipient_auto_reply(uuid,text,bytea,integer,bytea[],bytea,text,timestamp with time zone)",
    });

    /// <summary>NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: the two functions only <c>paqueteria_worker</c> executes.</summary>
    public static IReadOnlyList<string> ReceiptExecutorWorkerFunctions { get; } = Array.AsReadOnly(new[]
    {
        "security.claim_recipient_auto_replies(integer,interval)",
        "security.settle_recipient_auto_reply(uuid,uuid,text,text,timestamp with time zone)",
    });

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: the receipt executor's exact column grants (schema.table.column:privilege),
    /// as AI-18 declares them and the Notifications lane step 20261029000100_AddRecipientNoticeReceipts grants them.
    /// </summary>
    public static IReadOnlyList<string> ReceiptExecutorColumnGrants { get; } = Array.AsReadOnly(new[]
    {
        "notifications.notification_status_events.attempts:INSERT",
        "notifications.notification_status_events.id:INSERT",
        "notifications.notification_status_events.notification_id:INSERT",
        "notifications.notification_status_events.occurred_at:INSERT",
        "notifications.notification_status_events.owner_org_id:INSERT",
        "notifications.notification_status_events.provider_attempt_code:INSERT",
        "notifications.notification_status_events.status:INSERT",
        "notifications.notification_status_events.version:INSERT",
        "notifications.notifications.attempts:SELECT",
        "notifications.notifications.channel:SELECT",
        "notifications.notifications.delivered_at:UPDATE",
        "notifications.notifications.id:SELECT",
        "notifications.notifications.last_provider_attempt_code:UPDATE",
        "notifications.notifications.owner_org_id:SELECT",
        "notifications.notifications.provider_reference:SELECT",
        "notifications.notifications.status:SELECT",
        "notifications.notifications.status:UPDATE",
        "notifications.notifications.updated_at:UPDATE",
        "notifications.notifications.version:SELECT",
        "notifications.notifications.version:UPDATE",
        "notifications.recipient_auto_replies.attempts:INSERT",
        "notifications.recipient_auto_replies.attempts:SELECT",
        "notifications.recipient_auto_replies.attempts:UPDATE",
        "notifications.recipient_auto_replies.available_at:INSERT",
        "notifications.recipient_auto_replies.available_at:SELECT",
        "notifications.recipient_auto_replies.available_at:UPDATE",
        "notifications.recipient_auto_replies.contact_hash:INSERT",
        "notifications.recipient_auto_replies.contact_hash:SELECT",
        "notifications.recipient_auto_replies.contact_key_version:INSERT",
        "notifications.recipient_auto_replies.id:INSERT",
        "notifications.recipient_auto_replies.id:SELECT",
        "notifications.recipient_auto_replies.kind:INSERT",
        "notifications.recipient_auto_replies.kind:SELECT",
        "notifications.recipient_auto_replies.last_code:UPDATE",
        "notifications.recipient_auto_replies.lease_expires_at:SELECT",
        "notifications.recipient_auto_replies.lease_expires_at:UPDATE",
        "notifications.recipient_auto_replies.lease_token:SELECT",
        "notifications.recipient_auto_replies.lease_token:UPDATE",
        "notifications.recipient_auto_replies.number_key_version:INSERT",
        "notifications.recipient_auto_replies.number_key_version:SELECT",
        "notifications.recipient_auto_replies.number_key_version:UPDATE",
        "notifications.recipient_auto_replies.received_at:INSERT",
        "notifications.recipient_auto_replies.received_at:SELECT",
        "notifications.recipient_auto_replies.sealed_number:INSERT",
        "notifications.recipient_auto_replies.sealed_number:SELECT",
        "notifications.recipient_auto_replies.sealed_number:UPDATE",
        "notifications.recipient_auto_replies.settled_at:UPDATE",
        "notifications.recipient_auto_replies.status:INSERT",
        "notifications.recipient_auto_replies.status:SELECT",
        "notifications.recipient_auto_replies.status:UPDATE",
        "notifications.recipient_opt_outs.channel:INSERT",
        "notifications.recipient_opt_outs.channel:SELECT",
        "notifications.recipient_opt_outs.contact_hash:INSERT",
        "notifications.recipient_opt_outs.contact_hash:SELECT",
        "notifications.recipient_opt_outs.key_version:INSERT",
        "notifications.recipient_opt_outs.recorded_at:INSERT",
        "notifications.recipient_opt_outs.recorded_at:SELECT",
        "notifications.recipient_opt_outs.source:INSERT",
        "notifications.recipient_opt_outs.source:SELECT",
    });

    /// <summary>ORD-AUTO-CLOSE-2026-10-10: the auto-close owner discovery function.</summary>
    public const string AutoCloseFunction = "security.list_auto_close_owner_organizations(uuid,integer)";

    /// <summary>
    /// ORD-AUTO-CLOSE-2026-10-10: the auto-close executor's exact column grants (schema.table.column:privilege), as AI-18
    /// declares them and the Orders lane verifies them.
    /// </summary>
    public static IReadOnlyList<string> AutoCloseExecutorGrants { get; } = Array.AsReadOnly(new[]
    {
        "orders.orders.owner_org_id:SELECT",
        "orders.orders.status:SELECT",
    });

    /// <summary>DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: the two operator outbox functions.</summary>
    public static IReadOnlyList<string> OperatorOutboxFunctions { get; } = Array.AsReadOnly(new[]
    {
        "security.append_operator_order_outbox(uuid,uuid,jsonb,text,text,uuid,integer,jsonb,smallint,timestamp with time zone,timestamp with time zone)",
        "security.append_operator_order_audit(uuid,uuid,uuid,text,text,uuid,text,jsonb,timestamp with time zone)",
    });

    /// <summary>
    /// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: the operator outbox executor's exact column grants
    /// (schema.table.column:privilege), as AI-18 declares them and the Dispatch lane verifies them.
    /// </summary>
    public static IReadOnlyList<string> OperatorOutboxExecutorGrants { get; } = Array.AsReadOnly(new[]
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
    /// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: the operator outbox executor column grant the Dispatch lane step
    /// 20261013000100_AddOutboxTraceContext adds to <see cref="OperatorOutboxExecutorGrants"/> once it is recorded (owned
    /// by that step, not by AI-18, because the published DSP-OPERATOR-OWNER-OUTBOX step verifies exactly the AI-18 set).
    /// </summary>
    public static IReadOnlyList<string> OperatorOutboxExecutorTraceContextGrants { get; } = Array.AsReadOnly(new[]
    {
        "platform.outbox_events.trace_parent:INSERT",
    });

    /// <summary>MDM-001-OPERATOR-LOADER: the executor's exact column grants (schema.table.column:privilege).</summary>
    private static readonly string[] MasterDataExecutorColumnGrants =
    [
        "drivers.driver_profiles.driver_type:INSERT",
        "drivers.driver_profiles.driver_type:SELECT",
        "drivers.driver_profiles.driver_type:UPDATE",
        "drivers.driver_profiles.home_city_id:INSERT",
        "drivers.driver_profiles.home_city_id:SELECT",
        "drivers.driver_profiles.home_city_id:UPDATE",
        "drivers.driver_profiles.id:INSERT",
        "drivers.driver_profiles.id:SELECT",
        "drivers.driver_profiles.org_id:INSERT",
        "drivers.driver_profiles.org_id:SELECT",
        "drivers.driver_profiles.status:INSERT",
        "drivers.driver_profiles.status:SELECT",
        "drivers.driver_profiles.status:UPDATE",
        "drivers.driver_profiles.user_id:INSERT",
        "drivers.driver_profiles.user_id:SELECT",
        "drivers.driver_profiles.vehicle_type:INSERT",
        "drivers.driver_profiles.vehicle_type:SELECT",
        "drivers.driver_profiles.vehicle_type:UPDATE",
        "drivers.driver_service_areas.driver_id:INSERT",
        "drivers.driver_service_areas.driver_id:SELECT",
        "drivers.driver_service_areas.org_id:INSERT",
        "drivers.driver_service_areas.org_id:SELECT",
        "drivers.driver_service_areas.service_area_id:INSERT",
        "drivers.driver_service_areas.service_area_id:SELECT",
        "drivers.driver_service_areas.status:INSERT",
        "drivers.driver_service_areas.status:SELECT",
        "drivers.driver_service_areas.status:UPDATE",
        "identity.users.id:SELECT",
        "identity.users.status:SELECT",
        "locations.cities.country_code:INSERT",
        "locations.cities.country_code:SELECT",
        "locations.cities.id:INSERT",
        "locations.cities.id:SELECT",
        "locations.cities.name:INSERT",
        "locations.cities.name:SELECT",
        "locations.cities.state_code:INSERT",
        "locations.cities.state_code:SELECT",
        "locations.cities.status:INSERT",
        "locations.cities.status:SELECT",
        "locations.cities.timezone:INSERT",
        "locations.cities.timezone:SELECT",
        "locations.operating_zones.id:INSERT",
        "locations.operating_zones.id:SELECT",
        "locations.operating_zones.name:INSERT",
        "locations.operating_zones.name:SELECT",
        "locations.operating_zones.owner_org_id:INSERT",
        "locations.operating_zones.owner_org_id:SELECT",
        "locations.operating_zones.polygon:INSERT",
        "locations.operating_zones.polygon:SELECT",
        "locations.operating_zones.polygon:UPDATE",
        "locations.operating_zones.service_area_id:INSERT",
        "locations.operating_zones.service_area_id:SELECT",
        "locations.operating_zones.status:INSERT",
        "locations.operating_zones.status:SELECT",
        "locations.operating_zones.status:UPDATE",
        "locations.operating_zones.zone_type:INSERT",
        "locations.operating_zones.zone_type:SELECT",
        "locations.operating_zones.zone_type:UPDATE",
        "locations.service_areas.city_id:INSERT",
        "locations.service_areas.city_id:SELECT",
        "locations.service_areas.id:INSERT",
        "locations.service_areas.id:SELECT",
        "locations.service_areas.name:INSERT",
        "locations.service_areas.name:SELECT",
        "locations.service_areas.owner_org_id:INSERT",
        "locations.service_areas.owner_org_id:SELECT",
        "locations.service_areas.polygon:INSERT",
        "locations.service_areas.polygon:SELECT",
        "locations.service_areas.polygon:UPDATE",
        "locations.service_areas.status:INSERT",
        "locations.service_areas.status:SELECT",
        "locations.service_areas.status:UPDATE",
        "organizations.organization_memberships.organization_id:SELECT",
        "organizations.organization_memberships.role:SELECT",
        "organizations.organization_memberships.status:SELECT",
        "organizations.organization_memberships.user_id:SELECT",
        "organizations.organizations.id:SELECT",
        "organizations.organizations.organization_type:SELECT",
        "organizations.organizations.status:SELECT",
        "platform.audit_logs.action:INSERT",
        "platform.audit_logs.actor_id:INSERT",
        "platform.audit_logs.entity_id:INSERT",
        "platform.audit_logs.entity_type:INSERT",
        "platform.audit_logs.id:INSERT",
        "platform.audit_logs.occurred_at:INSERT",
        "platform.audit_logs.org_id:INSERT",
        "platform.audit_logs.payload_redacted:INSERT",
        "platform.audit_logs.request_id:INSERT",
        "platform.master_data_deployment_gate.deployment_class:SELECT",
        "platform.master_data_deployment_gate.gate_007_closed:SELECT",
        "pricing.tariff_rules.active_from:INSERT",
        "pricing.tariff_rules.active_from:SELECT",
        "pricing.tariff_rules.active_to:INSERT",
        "pricing.tariff_rules.active_to:SELECT",
        "pricing.tariff_rules.active_to:UPDATE",
        "pricing.tariff_rules.amount_cents:INSERT",
        "pricing.tariff_rules.amount_cents:SELECT",
        "pricing.tariff_rules.city_id:INSERT",
        "pricing.tariff_rules.city_id:SELECT",
        "pricing.tariff_rules.id:INSERT",
        "pricing.tariff_rules.id:SELECT",
        "pricing.tariff_rules.operating_zone_id:INSERT",
        "pricing.tariff_rules.operating_zone_id:SELECT",
        "pricing.tariff_rules.owner_org_id:INSERT",
        "pricing.tariff_rules.owner_org_id:SELECT",
        "pricing.tariff_rules.pricing_tier:INSERT",
        "pricing.tariff_rules.pricing_tier:SELECT",
        "pricing.tariff_rules.service_area_id:INSERT",
        "pricing.tariff_rules.service_area_id:SELECT",
        "pricing.tariff_rules.service_type:INSERT",
        "pricing.tariff_rules.service_type:SELECT",
        "pricing.tariff_rules.status:INSERT",
        "pricing.tariff_rules.status:SELECT",
        "pricing.tariff_rules.status:UPDATE",
        "pricing.tariff_rules.tax_mode:INSERT",
        "pricing.tariff_rules.tax_mode:SELECT",
    ];

    /// <summary>The MDM-001 executor column grants, for the lane and contract tests.</summary>
    public static IReadOnlyList<string> MasterDataExecutorGrants { get; } = Array.AsReadOnly(MasterDataExecutorColumnGrants);

    /// <summary>
    /// PRC-POLICY-VERSION-PER-ORG: the column grants the Pricing lane adds to the MDM-001 set once the loader
    /// stores tariff policy versions (<see cref="PricingPolicyVersionStateReader"/>). AI-18 keeps the MDM-001 set
    /// because the published MDM-001 migration verifies exactly that set before this lane step runs.
    /// </summary>
    public static IReadOnlyList<string> MasterDataExecutorPolicyVersionGrants { get; } = Array.AsReadOnly(new[]
    {
        "pricing.tariff_rules.policy_version:INSERT",
        "pricing.tariff_rules.policy_version:SELECT",
    });

    /// <summary>
    /// MDM-001 loader hardening (X2): the column grants the Pricing lane adds once the loader keeps a random
    /// per-operator reference in <c>platform.master_data_operator_refs</c>
    /// (<see cref="MasterDataLoaderHardeningStateReader"/>). Owned by that lane step, not by AI-18, for the
    /// same reason as the policy_version grants.
    /// </summary>
    public static IReadOnlyList<string> MasterDataExecutorOperatorRefGrants { get; } = Array.AsReadOnly(new[]
    {
        "platform.master_data_operator_refs.operator_login:INSERT",
        "platform.master_data_operator_refs.operator_login:SELECT",
        "platform.master_data_operator_refs.operator_ref:INSERT",
        "platform.master_data_operator_refs.operator_ref:SELECT",
    });

    private static readonly string[] SensitiveFunctions =
    [
        "security.resolve_identity_context(text)",
        "security.get_public_tracking_projection(text)",
        "security.map_public_order_status(text)",
        "security.claim_outbox(text,integer,interval)",
        "security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.requeue_stale_outbox(interval,integer,integer)",
        "security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
        "security.claim_location_outbox(text,integer,interval)",
        "security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)",
        "security.requeue_stale_location_outbox(interval,integer,integer)",
        "security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
    ];

    public static IReadOnlyList<string> AssertionNames { get; } = Array.AsReadOnly(
    new[]
    {
        "PostgreSQL 18 and PostGIS 3.6",
        "module/shared schemas and extension placement",
        "NOLOGIN role flags and runtime separation",
        "schema/table/sequence/function ownership",
        "per-module table and sequence default privileges",
        "runtime and PUBLIC schema restrictions",
        "append-only triggers",
        "outbox direct grants and lifecycle function grants",
        "forced RLS and sensitive-function PUBLIC revocation",
        "bootstrap function security and column-level grants",
        "pilot operational and outbox purge indexes (AI06-PILOT-INDEXES)",
        "lifecycle executor boundary (ADR-034)",
        "cleanup executor boundary (OPS-003-CLEANUP-ROLE)",
        "registration executor boundary (REG-001)",
        "BFF session executor boundary (BFF-SESSION-TABLE-SHAPE)",
        "master data executor and loader boundary (MDM-001-OPERATOR-LOADER)",
        "auto-close executor boundary (ORD-AUTO-CLOSE-2026-10-10)",
        "realtime dispatcher role boundary (RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12)",
        "real default-privilege inheritance probes",
    });

    public async Task<DatabaseAssertionReport> AssertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var ownTransaction = transaction is null;
        await using var localTransaction = ownTransaction
            ? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var activeTransaction = transaction ?? localTransaction!;
        var stopwatch = Stopwatch.StartNew();
        var violations = new List<string>();
        var checks = 0;

        try
        {
            var versions = await QuerySingleRowAsync(
                connection,
                activeTransaction,
                "SELECT current_setting('server_version'), public.PostGIS_Version()",
                cancellationToken).ConfigureAwait(false);
            var postgresVersion = versions[0];
            var postGisVersion = versions[1];
            checks += 2;
            if (!postgresVersion.StartsWith("18.", StringComparison.Ordinal))
            {
                violations.Add($"PostgreSQL major version must be 18, actual {postgresVersion}.");
            }

            if (!postGisVersion.StartsWith("3.6", StringComparison.Ordinal))
            {
                violations.Add($"PostGIS version must be 3.6, actual {postGisVersion}.");
            }

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'missing schema ' || expected.name
                FROM unnest(@schemas::text[]) expected(name)
                WHERE pg_catalog.to_regnamespace(expected.name) IS NULL
                UNION ALL
                SELECT 'schema owner ' || n.nspname || ' is ' || owner.rolname || ', expected paqueteria_migrator'
                FROM pg_catalog.pg_namespace n
                JOIN pg_catalog.pg_roles owner ON owner.oid=n.nspowner
                WHERE n.nspname=ANY(@schemas::text[]) AND owner.rolname<>'paqueteria_migrator'
                ORDER BY 1
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'extension ' || expected.name || ' is missing or installed in ' || COALESCE(n.nspname,'<missing>')
                FROM (VALUES ('postgis','public'),('pgcrypto','extensions')) expected(name,schema_name)
                LEFT JOIN pg_catalog.pg_extension e ON e.extname=expected.name
                LEFT JOIN pg_catalog.pg_namespace n ON n.oid=e.extnamespace
                WHERE n.nspname IS DISTINCT FROM expected.schema_name
                """,
                cancellationToken).ConfigureAwait(false);
            checks++;

            var digest = await ScalarAsync<byte[]>(
                connection,
                activeTransaction,
                "SELECT extensions.digest(pg_catalog.convert_to('dba-001','UTF8'),'sha256')",
                cancellationToken).ConfigureAwait(false);
            checks++;
            if (digest.Length != 32)
            {
                violations.Add("extensions.digest(bytea,text) did not return a 32-byte SHA-256 value.");
            }

            var realtimeDispatcherApplied = await E002NotificationStateReader
                .IsRealtimeDispatcherRoleAppliedAsync(connection, activeTransaction, cancellationToken)
                .ConfigureAwait(false);
            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                WITH expected(name,bypass_rls) AS (VALUES
                  ('paqueteria_migrator',false),
                  ('paqueteria_app',false),
                  ('paqueteria_worker',false),
                  ('paqueteria_bootstrap',true),
                  ('paqueteria_outbox_executor',true),
                  ('paqueteria_maintenance',true)),
                -- ADR-034: installations that predate LIF-001 have no lifecycle executor yet; once the
                -- role or its function exists it is held to the same NOLOGIN contract.
                lifecycle(name,bypass_rls) AS (
                  SELECT 'paqueteria_lifecycle_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_lifecycle_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.finalize_expired_orders(integer)') IS NOT NULL),
                -- OPS-003-CLEANUP-ROLE: the same rule for installations that predate the cleanup executor.
                cleanup(name,bypass_rls) AS (
                  SELECT 'paqueteria_cleanup_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_cleanup_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.expire_proof_upload_sessions(integer)') IS NOT NULL),
                -- REG-001: the same rule for installations that predate the registration executor.
                registration(name,bypass_rls) AS (
                  SELECT 'paqueteria_registration_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_registration_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.register_identity_subject(text,uuid)') IS NOT NULL),
                -- BFF-SESSION-TABLE-SHAPE: the same rule for installations that predate the session executor.
                session_store(name,bypass_rls) AS (
                  SELECT 'paqueteria_session_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_session_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.resolve_bff_session(bytea)') IS NOT NULL),
                -- MDM-001-OPERATOR-LOADER: the same rule for installations that predate the master data roles.
                master_data(name,bypass_rls) AS (
                  SELECT 'paqueteria_master_data_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_master_data_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NOT NULL
                  UNION ALL
                  SELECT 'paqueteria_master_data_loader',false
                  WHERE pg_catalog.to_regrole('paqueteria_master_data_loader') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NOT NULL),
                -- DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: the same rule for installations that predate the
                -- operator outbox executor.
                operator_outbox(name,bypass_rls) AS (
                  SELECT 'paqueteria_operator_outbox_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_operator_outbox_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.append_operator_order_outbox(uuid,uuid,jsonb,text,text,uuid,integer,jsonb,smallint,timestamp with time zone,timestamp with time zone)') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.append_operator_order_audit(uuid,uuid,uuid,text,text,uuid,text,jsonb,timestamp with time zone)') IS NOT NULL),
                -- ORD-AUTO-CLOSE-2026-10-10: the same rule for installations that predate the auto-close executor.
                auto_close(name,bypass_rls) AS (
                  SELECT 'paqueteria_auto_close_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_auto_close_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.list_auto_close_owner_organizations(uuid,integer)') IS NOT NULL),
                -- RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the same rule for installations that predate the realtime
                -- dispatcher runtime role (its lane step installs no function, so its recorded history row decides).
                realtime_dispatcher(name,bypass_rls) AS (
                  SELECT 'paqueteria_realtime',false
                  WHERE pg_catalog.to_regrole('paqueteria_realtime') IS NOT NULL OR @realtime_applied),
                -- NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: the same rule for installations that predate the receipt
                -- executor (AI-18 recipient notices step 3).
                receipt_executor(name,bypass_rls) AS (
                  SELECT 'paqueteria_notification_receipt_executor',true
                  WHERE pg_catalog.to_regrole('paqueteria_notification_receipt_executor') IS NOT NULL
                     OR pg_catalog.to_regprocedure('security.record_messaging_receipt(text,text,text,text,timestamp with time zone)') IS NOT NULL)
                SELECT 'role ' || expected.name || ' flags differ from least-privilege NOLOGIN contract'
                FROM (SELECT name,bypass_rls FROM expected UNION ALL SELECT name,bypass_rls FROM lifecycle
                      UNION ALL SELECT name,bypass_rls FROM cleanup
                      UNION ALL SELECT name,bypass_rls FROM registration
                      UNION ALL SELECT name,bypass_rls FROM session_store
                      UNION ALL SELECT name,bypass_rls FROM master_data
                      UNION ALL SELECT name,bypass_rls FROM operator_outbox
                      UNION ALL SELECT name,bypass_rls FROM auto_close
                      UNION ALL SELECT name,bypass_rls FROM realtime_dispatcher
                      UNION ALL SELECT name,bypass_rls FROM receipt_executor) expected
                LEFT JOIN pg_catalog.pg_roles r ON r.rolname=expected.name
                WHERE r.oid IS NULL OR r.rolcanlogin OR r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication
                  OR r.rolbypassrls IS DISTINCT FROM expected.bypass_rls
                """,
                cancellationToken,
                new NpgsqlParameter<bool>("realtime_applied", realtimeDispatcherApplied)).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'runtime role ' || member.rolname || ' can assume privileged role ' || granted.rolname
                FROM pg_catalog.pg_auth_members membership
                JOIN pg_catalog.pg_roles member ON member.oid=membership.member
                JOIN pg_catalog.pg_roles granted ON granted.oid=membership.roleid
                WHERE member.rolname=ANY(@runtime::text[]) AND granted.rolname=ANY(@privileged::text[])
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("runtime", RuntimeRoles),
                new NpgsqlParameter<string[]>("privileged", PrivilegedRoles)).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'object owner ' || n.nspname || '.' || c.relname || ' is ' || owner.rolname || ', expected paqueteria_migrator'
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_catalog.pg_roles owner ON owner.oid=c.relowner
                WHERE n.nspname=ANY(@schemas::text[]) AND c.relkind IN ('r','p','S') AND owner.rolname<>'paqueteria_migrator'
                ORDER BY 1
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
            checks++;

            await AssertFunctionOwnersAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertBootstrapContractsAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertPilotIndexesAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertRecipientNoticeContactAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertLifecycleExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertCleanupExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertRegistrationExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertSessionExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertMasterDataBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertOperatorOutboxExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertAutoCloseExecutorBoundaryAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertRealtimeDispatcherBoundaryAsync(
                connection, activeTransaction, realtimeDispatcherApplied, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertReceiptExecutorBoundaryAsync(
                connection,
                activeTransaction,
                await E002NotificationStateReader
                    .IsRecipientNoticeReceiptsAppliedAsync(connection, activeTransaction, cancellationToken)
                    .ConfigureAwait(false),
                violations,
                cancellationToken).ConfigureAwait(false);
            checks++;

            await AssertDefaultAclCatalogAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            foreach (var role in RuntimeRoles)
            {
                checks += 2;
                if (!await ScalarAsync<bool>(connection, activeTransaction, "SELECT has_schema_privilege(@role,'public','USAGE')", cancellationToken, new NpgsqlParameter<string>("role", role)).ConfigureAwait(false))
                {
                    violations.Add($"{role} is missing USAGE on public.");
                }

                if (await ScalarAsync<bool>(connection, activeTransaction, "SELECT has_schema_privilege(@role,'public','CREATE')", cancellationToken, new NpgsqlParameter<string>("role", role)).ConfigureAwait(false))
                {
                    violations.Add($"{role} has forbidden CREATE on public.");
                }
            }

            checks++;
            if (await ScalarAsync<bool>(connection, activeTransaction, "SELECT has_schema_privilege('public','public','CREATE')", cancellationToken).ConfigureAwait(false))
            {
                violations.Add("PUBLIC has forbidden CREATE on public.");
            }

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'PUBLIC has CREATE on application schema ' || name
                FROM unnest(@schemas::text[]) name
                WHERE has_schema_privilege('public',name,'CREATE')
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
            checks++;

            await AssertOutboxPrivilegesAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'sensitive function is executable by PUBLIC: ' || name
                FROM unnest(@functions::text[]) name
                WHERE has_function_privilege('public',name,'EXECUTE')
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("functions", SensitiveFunctions)).ConfigureAwait(false);
            checks++;

            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                WITH expected(name) AS (VALUES
                  ('order_events_append_only'),('order_acceptances_append_only'),('proofs_append_only'),('audit_logs_append_only'),
                  ('outbox_content_immutable'),('location_outbox_content_immutable'))
                SELECT 'append-only trigger missing or disabled: ' || expected.name
                FROM expected
                LEFT JOIN pg_catalog.pg_trigger t ON t.tgname=expected.name AND NOT t.tgisinternal AND t.tgenabled<>'D'
                WHERE t.oid IS NULL
                """,
                cancellationToken).ConfigureAwait(false);
            checks++;

            var unprotectedTables = await ScalarAsync<int>(
                connection,
                activeTransaction,
                """
                SELECT count(*)::integer
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname=ANY(@schemas::text[]) AND c.relkind IN ('r','p')
                  AND c.relname NOT IN (
                    'cities',
                    '__ef_migrations_history_identity',
                    '__ef_migrations_history_organizations',
                    '__ef_migrations_history_locations',
                    '__ef_migrations_history_drivers',
                    '__ef_migrations_history_pricing',
                    '__ef_migrations_history_orders',
                    '__ef_migrations_history_dispatch',
                    '__ef_migrations_history_custody',
                    '__ef_migrations_history_incidents',
                    '__ef_migrations_history_finance',
                    '__ef_migrations_history_notifications',
                    '__ef_migrations_history_platform',
                    '__ef_migrations_history_platform_evolution'
                  )
                  AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity)
                """,
                cancellationToken,
                new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
            checks++;
            if (unprotectedTables != 0)
            {
                violations.Add($"{unprotectedTables} tenant tables are missing ENABLE/FORCE RLS.");
            }

            // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-06 and AI-18 declared contract): once the Notifications lane step
            // installs the platform-wide opt-out store, it has no policy and no runtime role holds any privilege on it,
            // so only the SECURITY DEFINER functions reach it.
            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                SELECT 'notifications.recipient_opt_outs has a row security policy'
                WHERE pg_catalog.to_regclass('notifications.recipient_opt_outs') IS NOT NULL
                  AND EXISTS (SELECT 1 FROM pg_catalog.pg_policy
                              WHERE polrelid=pg_catalog.to_regclass('notifications.recipient_opt_outs'))
                UNION ALL
                SELECT 'runtime role ' || role.name || ' holds a privilege on notifications.recipient_opt_outs'
                FROM unnest(ARRAY['paqueteria_app','paqueteria_worker','paqueteria_realtime','paqueteria_bootstrap']) role(name)
                CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) privilege(name)
                WHERE pg_catalog.to_regclass('notifications.recipient_opt_outs') IS NOT NULL
                  AND pg_catalog.to_regrole(role.name) IS NOT NULL
                  AND pg_catalog.has_table_privilege(role.name,'notifications.recipient_opt_outs',privilege.name)
                """,
                cancellationToken).ConfigureAwait(false);
            checks++;

            // NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-004; AI-18 recipient notices step 2 and deployment assertion 40):
            // once the Orders lane step installs the WhatsApp consent table, it keeps FORCE RLS, its one tenant policy and
            // its append-only trigger, and the runtime roles hold exactly SELECT and INSERT (paqueteria_app) and SELECT
            // (paqueteria_worker) on it.
            await AddRowsAsync(
                violations,
                connection,
                activeTransaction,
                """
                WITH consent AS (SELECT pg_catalog.to_regclass('orders.recipient_notice_consents') AS oid)
                SELECT 'orders.recipient_notice_consents is missing ENABLE/FORCE RLS'
                FROM consent JOIN pg_catalog.pg_class c ON c.oid=consent.oid
                WHERE NOT c.relrowsecurity OR NOT c.relforcerowsecurity
                UNION ALL
                SELECT 'orders.recipient_notice_consents does not have exactly its tenant policy'
                FROM consent
                WHERE consent.oid IS NOT NULL
                  AND ((SELECT count(*) FROM pg_catalog.pg_policy p
                        WHERE p.polrelid=consent.oid AND p.polname='recipient_notice_consents_tenant' AND p.polcmd='*'
                          AND pg_catalog.pg_get_expr(p.polqual,p.polrelid)='security.app_allowed_org(owner_org_id)'
                          AND pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid)='security.app_allowed_org(owner_org_id)') <> 1
                       OR (SELECT count(*) FROM pg_catalog.pg_policy p WHERE p.polrelid=consent.oid) <> 1)
                UNION ALL
                SELECT 'append-only trigger missing or disabled: recipient_notice_consents_append_only'
                FROM consent
                WHERE consent.oid IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_trigger t
                                  WHERE t.tgrelid=consent.oid AND t.tgname='recipient_notice_consents_append_only'
                                    AND NOT t.tgisinternal AND t.tgenabled<>'D'
                                    AND t.tgfoid='platform.reject_runtime_mutation()'::regprocedure)
                UNION ALL
                SELECT 'runtime role ' || role.name || ' privilege ' || privilege.name || ' on orders.recipient_notice_consents differs from AI-18'
                FROM consent
                CROSS JOIN unnest(ARRAY['paqueteria_app','paqueteria_worker','paqueteria_realtime','paqueteria_bootstrap']) role(name)
                CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) privilege(name)
                WHERE consent.oid IS NOT NULL
                  AND pg_catalog.to_regrole(role.name) IS NOT NULL
                  AND pg_catalog.has_table_privilege(role.name,consent.oid,privilege.name)
                      IS DISTINCT FROM ((role.name='paqueteria_app' AND privilege.name IN ('SELECT','INSERT'))
                                        OR (role.name='paqueteria_worker' AND privilege.name='SELECT'))
                """,
                cancellationToken).ConfigureAwait(false);
            checks++;

            await ProbeDefaultPrivilegesAsync(connection, activeTransaction, violations, cancellationToken).ConfigureAwait(false);
            checks++;

            if (violations.Count != 0)
            {
                throw new DatabaseAssertionException(violations.AsReadOnly());
            }

            stopwatch.Stop();
            if (ownTransaction)
            {
                await activeTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            return new DatabaseAssertionReport(postgresVersion, postGisVersion, checks, stopwatch.Elapsed);
        }
        catch
        {
            if (ownTransaction && activeTransaction.Connection is not null)
            {
                await activeTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task AssertFunctionOwnersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected_all(signature,owner,ntf) AS (VALUES
              ('security.resolve_identity_context(text)','paqueteria_bootstrap',false),
              ('security.get_public_tracking_projection(text)','paqueteria_bootstrap',false),
              ('security.claim_outbox(text,integer,interval)','paqueteria_outbox_executor',false),
              ('security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)','paqueteria_outbox_executor',false),
              ('security.requeue_stale_outbox(interval,integer,integer)','paqueteria_outbox_executor',false),
              ('security.claim_location_outbox(text,integer,interval)','paqueteria_outbox_executor',false),
              ('security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)','paqueteria_outbox_executor',false),
              ('security.requeue_stale_location_outbox(interval,integer,integer)','paqueteria_outbox_executor',false),
              ('security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)','paqueteria_maintenance',false),
              ('security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)','paqueteria_maintenance',false),
              ('security.resolve_outbox_consumer(text)','paqueteria_outbox_executor',true),
              ('security.claim_realtime_outbox(text,integer,interval)','paqueteria_outbox_executor',true),
              ('security.claim_notifications_outbox(text,integer,interval)','paqueteria_outbox_executor',true),
              ('security.claim_unowned_outbox(text,integer,interval)','paqueteria_outbox_executor',true),
              ('security.requeue_stale_realtime_outbox(interval,integer,integer)','paqueteria_outbox_executor',true),
              ('security.recover_stale_notifications_outbox(text,integer,integer,interval)','paqueteria_outbox_executor',true),
              ('security.requeue_stale_unowned_outbox(interval,integer,integer)','paqueteria_outbox_executor',true),
              ('security.read_owner_dispatcher_ids(uuid,integer)','paqueteria_outbox_executor',true),
              ('security.read_active_notification_users(uuid[])','paqueteria_outbox_executor',true),
              ('security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamp with time zone)','paqueteria_outbox_executor',true),
              ('security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamp with time zone,uuid[])','paqueteria_outbox_executor',true),
              ('security.read_notification_delivery(uuid,uuid)','paqueteria_outbox_executor',true),
              ('security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)','paqueteria_outbox_executor',true),
              ('security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamp with time zone)','paqueteria_outbox_executor',true),
              ('notifications.provision_default_templates()','paqueteria_outbox_executor',true)),
            -- NTF-RECIPIENT-NOTICES-2026-10-20: the Notifications lane step 20261020000100_AddRecipientNoticeLane drops the
            -- published 8-argument settle and installs v2 with three more routines; any one of them marks the step, so a
            -- partial installation is reported.
            recipient_lane(installed) AS (
              SELECT to_regprocedure('security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamp with time zone,timestamp with time zone)') IS NOT NULL
                OR to_regprocedure('security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time without time zone,time without time zone,text,text[])') IS NOT NULL
                OR to_regprocedure('security.cancel_notification(uuid,uuid,uuid,integer,text,timestamp with time zone)') IS NOT NULL
                OR to_regprocedure('security.is_recipient_opted_out(text,bytea[])') IS NOT NULL),
            expected(signature,owner) AS (
              SELECT signature,owner FROM expected_all
              WHERE (NOT ntf OR to_regprocedure('security.resolve_outbox_consumer(text)') IS NOT NULL)
                AND NOT (signature='security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)'
                  AND (SELECT installed FROM recipient_lane))
              UNION ALL
              SELECT signature,'paqueteria_outbox_executor'
              FROM (VALUES
                ('security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time without time zone,time without time zone,text,text[])'),
                ('security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamp with time zone,timestamp with time zone)'),
                ('security.cancel_notification(uuid,uuid,uuid,integer,text,timestamp with time zone)'),
                ('security.is_recipient_opted_out(text,bytea[])')) recipient(signature)
              WHERE (SELECT installed FROM recipient_lane)
              UNION ALL
              -- NTF-RECIPIENT-NOTICE-CAPS-2026-10-23: the Notifications lane step 20261023000100_AddRecipientNoticeCounts
              -- installs the read-only count of the recipient notice caps after the recipient notice lane step.
              SELECT 'security.read_recipient_notice_counts(uuid,uuid)','paqueteria_outbox_executor'
              WHERE to_regprocedure('security.read_recipient_notice_counts(uuid,uuid)') IS NOT NULL
              UNION ALL
              -- ADR-034: installed by the Orders LIF-001 lane after the baseline.
              SELECT 'security.finalize_expired_orders(integer)','paqueteria_lifecycle_executor'
              WHERE to_regprocedure('security.finalize_expired_orders(integer)') IS NOT NULL
              UNION ALL
              -- D8-OUTBOX-LANE-DISPATCH: installed by the Notifications lane after NTF-001.
              SELECT signature,'paqueteria_outbox_executor'
              FROM (VALUES
                ('security.claim_dispatch_outbox(text,integer,interval)'),
                ('security.requeue_stale_dispatch_outbox(interval,integer,integer)')) dispatch_lane(signature)
              WHERE to_regprocedure('security.claim_dispatch_outbox(text,integer,interval)') IS NOT NULL
                 OR to_regprocedure('security.requeue_stale_dispatch_outbox(interval,integer,integer)') IS NOT NULL
              UNION ALL
              -- OPS-003-CLEANUP-ROLE: installed by the Custody OPS-003 lane after the baseline.
              SELECT signature,'paqueteria_cleanup_executor'
              FROM (VALUES
                ('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)'),
                ('security.expire_proof_upload_sessions(integer)'),
                -- BFF-SESSION-TABLE-SHAPE: the BFF session purge, installed by the Custody lane after OPS-003.
                ('security.purge_bff_sessions(integer)')) cleanup(signature)
              WHERE to_regprocedure(signature) IS NOT NULL
              UNION ALL
              -- BFF-SESSION-TABLE-SHAPE: installed by the Identity session-store lane after the baseline.
              SELECT signature,'paqueteria_session_executor'
              FROM (VALUES
                ('security.create_bff_session(bytea,text,text,bytea,timestamp with time zone)'),
                ('security.resolve_bff_session(bytea)'),
                ('security.revoke_bff_session(bytea)'),
                ('security.revoke_bff_session(text)'),
                ('security.revoke_bff_session(text,timestamp with time zone)'),
                ('security.register_bff_logout_jti(bytea,timestamp with time zone)')) session_store(signature)
              WHERE to_regprocedure(signature) IS NOT NULL
              UNION ALL
              -- REG-001: installed by the Organizations lane after the baseline.
              SELECT signature,'paqueteria_registration_executor'
              FROM (VALUES
                ('security.register_identity_subject(text,uuid)'),
                ('security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)'),
                ('security.list_own_organization_applications(uuid)'),
                ('security.list_pending_ally_organizations(uuid,uuid,integer)'),
                ('security.decide_ally_organization(uuid,uuid,uuid,boolean,text)'),
                -- REG-002: installed by the same lane after REG-001, with the same owner.
                ('security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text)'),
                ('security.renew_pending_membership(uuid,uuid,uuid,text,text)'),
                ('security.revoke_pending_membership(uuid,uuid,uuid,text,text)'),
                ('security.apply_pending_memberships(text,bytea[],integer[])')) registration(signature)
              WHERE to_regprocedure(signature) IS NOT NULL
              UNION ALL
              -- MDM-001-OPERATOR-LOADER: installed by the Pricing lane after the baseline.
              SELECT 'security.load_master_data(uuid,uuid,json,bytea,boolean)','paqueteria_master_data_executor'
              WHERE to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NOT NULL
              UNION ALL
              -- DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: installed by the Dispatch lane after the baseline.
              SELECT signature,'paqueteria_operator_outbox_executor'
              FROM (VALUES
                ('security.append_operator_order_outbox(uuid,uuid,jsonb,text,text,uuid,integer,jsonb,smallint,timestamp with time zone,timestamp with time zone)'),
                ('security.append_operator_order_audit(uuid,uuid,uuid,text,text,uuid,text,jsonb,timestamp with time zone)')) operator_outbox(signature)
              WHERE to_regprocedure(signature) IS NOT NULL
              UNION ALL
              -- ORD-AUTO-CLOSE-2026-10-10: installed by the Orders lane after the baseline.
              SELECT 'security.list_auto_close_owner_organizations(uuid,integer)','paqueteria_auto_close_executor'
              WHERE to_regprocedure('security.list_auto_close_owner_organizations(uuid,integer)') IS NOT NULL
              UNION ALL
              -- NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: installed by the Notifications lane after the recipient notice
              -- count step; any one of them marks the step, so a partial installation is reported.
              SELECT signature,'paqueteria_notification_receipt_executor'
              FROM (VALUES
                ('security.record_messaging_receipt(text,text,text,text,timestamp with time zone)'),
                ('security.record_recipient_opt_out(text,text,bytea,integer,bytea[])'),
                ('security.request_recipient_auto_reply(uuid,text,bytea,integer,bytea[],bytea,text,timestamp with time zone)'),
                ('security.claim_recipient_auto_replies(integer,interval)'),
                ('security.settle_recipient_auto_reply(uuid,uuid,text,text,timestamp with time zone)')) receipts(signature)
              WHERE to_regprocedure('security.record_messaging_receipt(text,text,text,text,timestamp with time zone)') IS NOT NULL
                 OR to_regprocedure('security.record_recipient_opt_out(text,text,bytea,integer,bytea[])') IS NOT NULL
                 OR to_regprocedure('security.request_recipient_auto_reply(uuid,text,bytea,integer,bytea[],bytea,text,timestamp with time zone)') IS NOT NULL
                 OR to_regprocedure('security.claim_recipient_auto_replies(integer,interval)') IS NOT NULL
                 OR to_regprocedure('security.settle_recipient_auto_reply(uuid,uuid,text,text,timestamp with time zone)') IS NOT NULL
            )
            SELECT 'function owner mismatch for ' || expected.signature || ', expected ' || expected.owner
            FROM expected
            LEFT JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(expected.signature)
            LEFT JOIN pg_catalog.pg_roles owner ON owner.oid=p.proowner
            WHERE owner.rolname IS DISTINCT FROM expected.owner
            UNION ALL
            SELECT 'privileged role owns unapproved function ' || n.nspname || '.' || p.proname
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid=p.proowner
            WHERE owner.rolname IN ('paqueteria_bootstrap','paqueteria_outbox_executor','paqueteria_maintenance',
                'paqueteria_lifecycle_executor','paqueteria_cleanup_executor','paqueteria_registration_executor',
                'paqueteria_session_executor','paqueteria_master_data_executor','paqueteria_master_data_loader',
                'paqueteria_operator_outbox_executor','paqueteria_auto_close_executor','paqueteria_realtime',
                'paqueteria_notification_receipt_executor')
              AND NOT EXISTS (SELECT 1 FROM expected WHERE pg_catalog.to_regprocedure(expected.signature)=p.oid AND expected.owner=owner.rolname)
            UNION ALL
            SELECT 'general function owner mismatch for ' || n.nspname || '.' || p.proname || ', actual ' || owner.rolname
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            JOIN pg_catalog.pg_roles owner ON owner.oid=p.proowner
            WHERE n.nspname IN ('platform','security') AND owner.rolname<>'paqueteria_migrator'
              AND NOT EXISTS (SELECT 1 FROM expected WHERE pg_catalog.to_regprocedure(expected.signature)=p.oid)
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// AI06-PILOT-INDEXES: the four operational indexes and one partial index per terminal purge arm
    /// of each outbox, exactly as AI-06 declares them and valid.
    /// </summary>
    private static async Task AssertPilotIndexesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected(name,definition) AS (VALUES
              ('orders.orders_updated_at_idx',
               'CREATE INDEX orders_updated_at_idx ON orders.orders USING btree (updated_at)'),
              ('dispatch.assignments_driver_idx',
               'CREATE INDEX assignments_driver_idx ON dispatch.assignments USING btree (driver_id)'),
              ('dispatch.assignments_route_idx',
               'CREATE INDEX assignments_route_idx ON dispatch.assignments USING btree (route_id) WHERE (route_id IS NOT NULL)'),
              ('routes.route_stops_order_idx',
               'CREATE INDEX route_stops_order_idx ON routes.route_stops USING btree (order_id)'),
              ('platform.outbox_purge_processed_idx',
               'CREATE INDEX outbox_purge_processed_idx ON platform.outbox_events USING btree (processed_at) WHERE (status = ''PROCESSED''::text)'),
              ('platform.outbox_purge_dead_idx',
               'CREATE INDEX outbox_purge_dead_idx ON platform.outbox_events USING btree (COALESCE(processed_at, created_at)) WHERE (status = ''DEAD''::text)'),
              ('platform.location_outbox_purge_processed_idx',
               'CREATE INDEX location_outbox_purge_processed_idx ON platform.location_outbox_events USING btree (processed_at) WHERE (status = ''PROCESSED''::text)'),
              ('platform.location_outbox_purge_dead_idx',
               'CREATE INDEX location_outbox_purge_dead_idx ON platform.location_outbox_events USING btree (COALESCE(processed_at, created_at)) WHERE (status = ''DEAD''::text)'))
            SELECT 'pilot index missing, invalid or different: ' || e.name
            FROM expected e
            LEFT JOIN pg_catalog.pg_index i ON i.indexrelid=pg_catalog.to_regclass(e.name)
            WHERE i.indexrelid IS NULL
               OR NOT i.indisvalid
               OR pg_catalog.pg_get_indexdef(i.indexrelid)<>e.definition
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-002; AI-06 declared contract): the Locations lane step that adds the
    /// recipient contact columns of <c>locations.locations</c>. AI-06 carries the same DDL on fresh installations.
    /// </summary>
    public const string RecipientNoticeContactMigrationId = "20261020000100_AddRecipientNoticeContact";

    /// <summary>The four columns as <c>name:type:nullable:default</c>, ordered by name.</summary>
    public static IReadOnlyList<string> RecipientNoticeContactColumns { get; } = Array.AsReadOnly(new[]
    {
        "email_ciphertext:bytea:YES:",
        "notice_email_ciphertext:bytea:YES:",
        "notice_key_version:text:YES:",
        "notice_phone_ciphertext:bytea:YES:",
    });

    /// <summary>The two checks as <c>name:pg_get_constraintdef:validated</c>, ordered by name.</summary>
    public static IReadOnlyList<string> RecipientNoticeContactChecks { get; } = Array.AsReadOnly(new[]
    {
        "locations_notice_email_check:CHECK (((notice_email_ciphertext IS NULL) OR (email_ciphertext IS NOT NULL))):true",
        "locations_notice_key_version_check:CHECK (((notice_key_version IS NULL) = ((notice_phone_ciphertext IS NULL) AND (notice_email_ciphertext IS NULL)))):true",
    });

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20: <c>locations.locations</c> carries <see cref="RecipientNoticeContactColumns"/>
    /// and <see cref="RecipientNoticeContactChecks"/> exactly. Recorded history, not the catalog, decides: on a fresh
    /// baseline (no Locations lane history yet) and once <see cref="RecipientNoticeContactMigrationId"/> is recorded
    /// they must be present; while the Locations lane has not recorded that step (an installation upgraded before the
    /// lane runs, or after the step's guarded rollback) they must be either exactly present or entirely absent, never
    /// partial or different. The columns keep the table's FORCE RLS policy and runtime privileges, checked elsewhere.
    /// </summary>
    private static async Task AssertRecipientNoticeContactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        var required = await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT pg_catalog.to_regclass('platform.__ef_migrations_history_locations') IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        if (!required)
        {
            required = await ScalarAsync<bool>(
                connection,
                transaction,
                """
                SELECT EXISTS (
                  SELECT 1 FROM platform.__ef_migrations_history_locations WHERE "MigrationId"=@migration_id)
                """,
                cancellationToken,
                new NpgsqlParameter<string>("migration_id", RecipientNoticeContactMigrationId)).ConfigureAwait(false);
        }

        var columns = await ListAsync(
            connection,
            transaction,
            """
            SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'')
            FROM information_schema.columns
            WHERE table_schema='locations' AND table_name='locations'
              AND column_name IN ('email_ciphertext','notice_phone_ciphertext','notice_email_ciphertext','notice_key_version')
            ORDER BY column_name
            """,
            cancellationToken).ConfigureAwait(false);
        var checks = await ListAsync(
            connection,
            transaction,
            """
            SELECT conname || ':' || pg_catalog.pg_get_constraintdef(oid) || ':' || convalidated::text
            FROM pg_catalog.pg_constraint
            WHERE conrelid=pg_catalog.to_regclass('locations.locations') AND contype='c'
              AND conname IN ('locations_notice_email_check','locations_notice_key_version_check')
            ORDER BY conname
            """,
            cancellationToken).ConfigureAwait(false);
        var exact = columns.SequenceEqual(RecipientNoticeContactColumns, StringComparer.Ordinal) &&
            checks.SequenceEqual(RecipientNoticeContactChecks, StringComparer.Ordinal);
        var absent = columns.Count == 0 && checks.Count == 0;
        if (required ? !exact : !(exact || absent))
        {
            violations.Add(
                "locations.locations recipient contact columns or checks differ from the NTF-RECIPIENT-NOTICES-2026-10-20 contract (AI-06).");
        }
    }

    private static async Task<IReadOnlyList<string>> ListAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task AssertBootstrapContractsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected(signature,search_path) AS (VALUES
              ('security.resolve_identity_context(text)','search_path=pg_catalog, identity, organizations, security, pg_temp'),
              ('security.get_public_tracking_projection(text)','search_path=pg_catalog, extensions, orders, security, pg_temp'))
            SELECT 'bootstrap function missing or not SECURITY DEFINER: ' || expected.signature
            FROM expected
            LEFT JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(expected.signature)
            WHERE p.oid IS NULL OR NOT p.prosecdef
            UNION ALL
            SELECT 'bootstrap function search_path mismatch: ' || expected.signature
            FROM expected
            JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(expected.signature)
            WHERE NOT (expected.search_path=ANY(COALESCE(p.proconfig,ARRAY[]::text[])))
            UNION ALL
            SELECT 'bootstrap function contains dynamic SQL EXECUTE: ' || expected.signature
            FROM expected
            JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(expected.signature)
            WHERE p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
            UNION ALL
            SELECT 'paqueteria_app cannot execute bootstrap function: ' || expected.signature
            FROM expected
            WHERE NOT has_function_privilege('paqueteria_app',expected.signature,'EXECUTE')
            UNION ALL
            SELECT 'paqueteria_worker can execute forbidden bootstrap function: ' || expected.signature
            FROM expected
            WHERE has_function_privilege('paqueteria_worker',expected.signature,'EXECUTE')
            """,
            cancellationToken).ConfigureAwait(false);

        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected(table_schema,table_name,column_name) AS (VALUES
              ('identity','users','id'),
              ('identity','users','identity_subject'),
              ('identity','users','status'),
              ('organizations','organization_memberships','id'),
              ('organizations','organization_memberships','user_id'),
              ('organizations','organization_memberships','organization_id'),
              ('organizations','organization_memberships','role'),
              ('organizations','organization_memberships','status'),
              ('organizations','organization_memberships','is_default'),
              ('organizations','organizations','id'),
              ('organizations','organizations','status'),
              ('orders','public_tracking_tokens','id'),
              ('orders','public_tracking_tokens','order_id'),
              ('orders','public_tracking_tokens','token_hash'),
              ('orders','public_tracking_tokens','expires_at'),
              ('orders','public_tracking_tokens','revoked_at'),
              ('orders','orders','id'),
              ('orders','orders','public_id'),
              ('orders','orders','status'),
              ('orders','orders','version'),
              ('orders','order_events','order_id'),
              ('orders','order_events','aggregate_version'),
              ('orders','order_events','public_event_code'),
              ('orders','order_events','occurred_at')),
            actual AS (
              SELECT table_schema,table_name,column_name
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_bootstrap' AND privilege_type='SELECT'
                AND table_schema IN ('identity','organizations','orders'))
            SELECT 'missing bootstrap SELECT column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name
            FROM expected e LEFT JOIN actual a USING(table_schema,table_name,column_name)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected bootstrap SELECT column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'bootstrap has non-SELECT column privilege: ' || table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
            FROM information_schema.column_privileges
            WHERE grantee='paqueteria_bootstrap' AND privilege_type<>'SELECT'
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR-034 lane contract: after the Orders LIF-001 migration (and after any E-002 temporary grant
    /// is revoked) the role and its function must exist and satisfy the exact executor boundary,
    /// including no CREATE on any application or shared schema.
    /// </summary>
    public static async Task AssertLifecycleExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'lifecycle executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_lifecycle_executor') IS NULL
            UNION ALL
            SELECT 'lifecycle finalization function is missing: security.finalize_expired_orders(integer)'
            WHERE pg_catalog.to_regprocedure('security.finalize_expired_orders(integer)') IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertLifecycleExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// ADR-034: once the lifecycle executor exists it holds exactly USAGE on <c>orders</c> plus
    /// SELECT(id,status,claim_window_ends_at,finalized_at) and UPDATE(finalized_at) on
    /// <c>orders.orders</c>, inherits nothing, and its single function is a pinned SECURITY DEFINER
    /// executable by <c>paqueteria_worker</c> only.
    /// </summary>
    private static async Task AssertLifecycleExecutorBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_lifecycle_executor'
            ),
            expected(table_schema,table_name,column_name,privilege_type) AS (VALUES
              ('orders','orders','id','SELECT'),
              ('orders','orders','status','SELECT'),
              ('orders','orders','claim_window_ends_at','SELECT'),
              ('orders','orders','finalized_at','SELECT'),
              ('orders','orders','finalized_at','UPDATE')),
            actual AS (
              SELECT table_schema,table_name,column_name,privilege_type
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_lifecycle_executor'),
            fn AS (
              SELECT p.oid,p.prosecdef,p.proconfig,p.prosrc
              FROM pg_catalog.pg_proc p
              WHERE p.oid=pg_catalog.to_regprocedure('security.finalize_expired_orders(integer)'))
            SELECT 'missing lifecycle executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected lifecycle executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'unexpected lifecycle executor table grant: ' || table_schema || '.' || table_name || ':' || privilege_type
            FROM information_schema.table_privileges
            WHERE grantee='paqueteria_lifecycle_executor'
            UNION ALL
            SELECT 'lifecycle executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM (n.nspname='orders'))
            UNION ALL
            SELECT 'lifecycle executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m JOIN executor ON m.member=executor.oid
            UNION ALL
            SELECT 'lifecycle executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'lifecycle finalization function is unsafe: security.finalize_expired_orders(integer)'
            FROM fn
            WHERE NOT fn.prosecdef
               OR NOT ('search_path=pg_catalog, orders, pg_temp'=ANY(COALESCE(fn.proconfig,ARRAY[]::text[])))
               OR fn.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR has_function_privilege('public',fn.oid,'EXECUTE')
               OR has_function_privilege('paqueteria_app',fn.oid,'EXECUTE')
               OR NOT has_function_privilege('paqueteria_worker',fn.oid,'EXECUTE')
            UNION ALL
            SELECT 'lifecycle finalization function exists without the lifecycle executor role'
            FROM fn WHERE NOT EXISTS (SELECT 1 FROM executor)
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// OPS-003-CLEANUP-ROLE lane contract: after the Custody OPS-003 migration (and after any E-002
    /// temporary grant is revoked) the role and both functions must exist and satisfy the exact
    /// executor boundary, including no CREATE on any application or shared schema.
    /// </summary>
    public static async Task AssertCleanupExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'cleanup executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_cleanup_executor') IS NULL
            UNION ALL
            SELECT 'cleanup function is missing: ' || signature
            FROM (VALUES
              ('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)'),
              ('security.expire_proof_upload_sessions(integer)'),
              ('security.purge_bff_sessions(integer)')) expected(signature)
            WHERE pg_catalog.to_regprocedure(signature) IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertCleanupExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// OPS-003-CLEANUP-ROLE: once the cleanup executor exists it holds exactly USAGE on
    /// <c>platform</c> and <c>custody</c>, SELECT on five key columns plus DELETE on
    /// <c>platform.idempotency_keys</c>, SELECT(id,status,expires_at) plus UPDATE(status,updated_at) on
    /// <c>custody.proof_upload_sessions</c>, inherits nothing, and its two functions are pinned
    /// SECURITY DEFINERs executable by <c>paqueteria_worker</c> only. Once the BFF session purge exists
    /// (BFF-SESSION-TABLE-SHAPE), the boundary widens by exactly USAGE on <c>identity</c>,
    /// SELECT(session_key_hash,expires_at,revoked_at) and DELETE on <c>identity.bff_sessions</c> and that
    /// third function.
    /// </summary>
    private static async Task AssertCleanupExecutorBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_cleanup_executor'
            ),
            bff AS (
              SELECT 1 WHERE pg_catalog.to_regprocedure('security.purge_bff_sessions(integer)') IS NOT NULL
            ),
            expected(table_schema,table_name,column_name,privilege_type) AS (
              SELECT * FROM (VALUES
                ('platform','idempotency_keys','owner_org_id','SELECT'),
                ('platform','idempotency_keys','scope','SELECT'),
                ('platform','idempotency_keys','idempotency_key','SELECT'),
                ('platform','idempotency_keys','created_at','SELECT'),
                ('platform','idempotency_keys','expires_at','SELECT'),
                ('custody','proof_upload_sessions','id','SELECT'),
                ('custody','proof_upload_sessions','status','SELECT'),
                ('custody','proof_upload_sessions','expires_at','SELECT'),
                ('custody','proof_upload_sessions','status','UPDATE'),
                ('custody','proof_upload_sessions','updated_at','UPDATE')) ops003(a,b,c,d)
              UNION ALL
              SELECT * FROM (VALUES
                ('identity','bff_sessions','session_key_hash','SELECT'),
                ('identity','bff_sessions','expires_at','SELECT'),
                ('identity','bff_sessions','revoked_at','SELECT'),
                ('identity','bff_logout_jtis','jti_hash','SELECT'),
                ('identity','bff_logout_jtis','expires_at','SELECT')) bff_purge(a,b,c,d)
              WHERE EXISTS (SELECT 1 FROM bff)),
            actual AS (
              SELECT table_schema,table_name,column_name,privilege_type
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_cleanup_executor'),
            expected_tables(table_schema,table_name,privilege_type) AS (
              SELECT 'platform','idempotency_keys','DELETE'
              UNION ALL
              SELECT 'identity','bff_sessions','DELETE' WHERE EXISTS (SELECT 1 FROM bff)
              UNION ALL
              SELECT 'identity','bff_logout_jtis','DELETE' WHERE EXISTS (SELECT 1 FROM bff)),
            actual_tables AS (
              SELECT table_schema,table_name,privilege_type
              FROM information_schema.table_privileges
              WHERE grantee='paqueteria_cleanup_executor'),
            fn(signature,search_path) AS (VALUES
              ('security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)',
               'search_path=pg_catalog, platform, pg_temp'),
              ('security.expire_proof_upload_sessions(integer)',
               'search_path=pg_catalog, custody, pg_temp'),
              ('security.purge_bff_sessions(integer)',
               'search_path=pg_catalog, identity, pg_temp')),
            installed AS (
              SELECT fn.signature,fn.search_path,p.oid,p.prosecdef,p.proconfig,p.prosrc
              FROM fn JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(fn.signature))
            SELECT 'missing cleanup executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected cleanup executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'missing cleanup executor table grant: ' || e.table_schema || '.' || e.table_name || ':' || e.privilege_type
            FROM expected_tables e CROSS JOIN executor
            LEFT JOIN actual_tables a USING(table_schema,table_name,privilege_type)
            WHERE a.table_name IS NULL
            UNION ALL
            SELECT 'unexpected cleanup executor table grant: ' || a.table_schema || '.' || a.table_name || ':' || a.privilege_type
            FROM actual_tables a LEFT JOIN expected_tables e USING(table_schema,table_name,privilege_type)
            WHERE e.table_name IS NULL
            UNION ALL
            SELECT 'cleanup executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM
                  (n.nspname IN ('platform','custody') OR (n.nspname='identity' AND EXISTS (SELECT 1 FROM bff))))
            UNION ALL
            SELECT 'cleanup executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m JOIN executor ON m.member=executor.oid
            UNION ALL
            SELECT 'cleanup executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'cleanup function is unsafe: ' || installed.signature
            FROM installed
            WHERE NOT installed.prosecdef
               OR NOT (installed.search_path=ANY(COALESCE(installed.proconfig,ARRAY[]::text[])))
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR has_function_privilege('public',installed.oid,'EXECUTE')
               OR has_function_privilege('paqueteria_app',installed.oid,'EXECUTE')
               OR NOT has_function_privilege('paqueteria_worker',installed.oid,'EXECUTE')
            UNION ALL
            SELECT 'cleanup function exists without the cleanup executor role: ' || installed.signature
            FROM installed WHERE NOT EXISTS (SELECT 1 FROM executor)
            UNION ALL
            SELECT 'cleanup functions are only partially installed'
            FROM (SELECT count(*) AS present FROM installed
                  WHERE signature<>'security.purge_bff_sessions(integer)') c
            WHERE c.present=1
            UNION ALL
            SELECT 'BFF session purge exists without the OPS-003 cleanup functions'
            FROM bff
            WHERE (SELECT count(*) FROM installed WHERE signature<>'security.purge_bff_sessions(integer)')<>2
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// MDM-001-OPERATOR-LOADER lane contract: after the Pricing lane (and after any E-002 temporary grant is
    /// revoked) both roles and the loader function must exist and satisfy the exact boundary.
    /// </summary>
    public static async Task AssertMasterDataLoaderInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'master data executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_master_data_executor') IS NULL
            UNION ALL
            SELECT 'master data loader role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_master_data_loader') IS NULL
            UNION ALL
            SELECT 'master data loader function is missing'
            WHERE pg_catalog.to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)') IS NULL
            UNION ALL
            SELECT 'master data deployment gate table is missing'
            WHERE pg_catalog.to_regclass('platform.master_data_deployment_gate') IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertMasterDataBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// MDM-001-OPERATOR-LOADER: once the master data executor exists it holds exactly USAGE on the six
    /// schemas it reads or writes and the AI-18 column grants, none grantable, no table-wide grant, inherits
    /// nothing, owns no relation, and only deployment principals (members of <c>paqueteria_migrator</c>) hold
    /// it; the operator grantee holds only USAGE on <c>security</c>, no table or column privilege, and neither
    /// role is reachable from a runtime or bootstrap role. The loader function, once installed, is a pinned
    /// SECURITY DEFINER owned by the executor whose ACL is exactly the owner and the operator grantee. The
    /// GATE-007 deployment marker grants nothing but the executor's two column reads. Privileges are read
    /// from the catalog ACLs (aclexplode), not from information_schema, which only shows privileges the
    /// current role can see. The roles may exist without the function (fresh AI-18 before the lane, or after
    /// the lane is rolled back).
    /// </summary>
    private static async Task AssertMasterDataBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        // Recorded history, not the catalog, decides whether the two policy_version grants belong to the set, so
        // a recorded step whose grants are missing (or grants without the step) is reported, never absorbed.
        IEnumerable<string> expectedGrants = MasterDataExecutorColumnGrants;
        if (await PricingPolicyVersionStateReader.IsLoaderStoringAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false))
        {
            expectedGrants = expectedGrants.Concat(MasterDataExecutorPolicyVersionGrants);
        }

        var hardened = await MasterDataLoaderHardeningStateReader.IsAppliedAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (hardened)
        {
            expectedGrants = expectedGrants.Concat(MasterDataExecutorOperatorRefGrants);
        }

        var grants = expectedGrants.ToArray();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_master_data_executor'
            ),
            loader AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_master_data_loader'
            ),
            expected AS (
              SELECT split_part(grant_text, '.', 1) AS table_schema,
                     split_part(grant_text, '.', 2) AS table_name,
                     split_part(split_part(grant_text, '.', 3), ':', 1) AS column_name,
                     split_part(grant_text, ':', 2) AS privilege_type
              FROM unnest(@grants::text[]) grant_text),
            column_acl AS (
              SELECT n.nspname::text AS table_schema, c.relname::text AS table_name, att.attname::text AS column_name,
                     acl.privilege_type, acl.is_grantable, acl.grantee
              FROM pg_catalog.pg_attribute att
              JOIN pg_catalog.pg_class c ON c.oid=att.attrelid
              JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(att.attacl) acl
              WHERE att.attacl IS NOT NULL),
            table_acl AS (
              SELECT n.nspname::text AS table_schema, c.relname::text AS table_name, c.oid, c.relowner,
                     acl.privilege_type, acl.grantee
              FROM pg_catalog.pg_class c
              JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) acl
              WHERE c.relacl IS NOT NULL),
            actual AS (
              SELECT table_schema,table_name,column_name,privilege_type,is_grantable
              FROM column_acl WHERE grantee IN (SELECT oid FROM executor)),
            installed AS (
              SELECT p.oid,p.proowner,p.prosecdef,p.proconfig,p.prosrc,
                     (SELECT string_agg(entry, ',' ORDER BY entry)
                      FROM (SELECT CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(acl.grantee) END
                                     || ':' || acl.privilege_type || ':' || acl.is_grantable::text AS entry
                            FROM pg_catalog.aclexplode(p.proacl) acl) entries) AS acl
              FROM pg_catalog.pg_proc p
              WHERE p.oid=pg_catalog.to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)')),
            gate AS (
              SELECT c.oid, c.relowner FROM pg_catalog.pg_class c
              WHERE c.oid=pg_catalog.to_regclass('platform.master_data_deployment_gate')),
            operator_refs AS (
              SELECT c.oid, c.relowner, c.relrowsecurity, c.relforcerowsecurity FROM pg_catalog.pg_class c
              WHERE c.oid=pg_catalog.to_regclass('platform.master_data_operator_refs'))
            SELECT 'missing master data executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected master data executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'master data executor column grant is grantable: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name
            FROM actual a WHERE a.is_grantable
            UNION ALL
            SELECT 'unexpected master data table grant: ' || pg_catalog.pg_get_userbyid(t.grantee) || ' ' || t.table_schema || '.' || t.table_name || ':' || t.privilege_type
            FROM table_acl t
            WHERE t.grantee IN (SELECT oid FROM executor UNION ALL SELECT oid FROM loader)
            UNION ALL
            SELECT 'unexpected master data loader column grant: ' || c.table_schema || '.' || c.table_name || '.' || c.column_name
            FROM column_acl c WHERE c.grantee IN (SELECT oid FROM loader)
            UNION ALL
            SELECT 'master data executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM
                   (n.nspname IN ('identity','organizations','locations','pricing','drivers','platform')))
            UNION ALL
            SELECT 'master data loader schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN loader
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(loader.oid,n.oid,'CREATE')
                OR has_schema_privilege(loader.oid,n.oid,'USAGE') IS DISTINCT FROM (n.nspname='security'))
            UNION ALL
            SELECT 'master data role inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m
            WHERE m.member IN (SELECT oid FROM executor UNION ALL SELECT oid FROM loader)
            UNION ALL
            SELECT 'master data role is reachable from ' || member.rolname
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            WHERE m.roleid IN (SELECT oid FROM executor UNION ALL SELECT oid FROM loader)
              AND member.rolname IN ('paqueteria_app','paqueteria_worker','paqueteria_bootstrap',
                     'paqueteria_master_data_loader','paqueteria_master_data_executor')
            UNION ALL
            SELECT 'master data executor has a member that is not a deployment principal: ' || member.rolname
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            WHERE m.roleid IN (SELECT oid FROM executor)
              AND NOT pg_catalog.pg_has_role(m.member,'paqueteria_migrator','MEMBER')
            UNION ALL
            SELECT 'master data role owns a relation, schema or type'
            FROM (SELECT oid FROM executor UNION ALL SELECT oid FROM loader) r
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=r.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=r.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=r.oid)
            UNION ALL
            SELECT 'master data loader role owns a function'
            FROM loader WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_proc WHERE proowner=loader.oid)
            UNION ALL
            SELECT 'master data loader function is unsafe'
            FROM installed
            WHERE NOT installed.prosecdef
               OR NOT ('search_path=pg_catalog, pg_temp'=ANY(COALESCE(installed.proconfig,ARRAY[]::text[])))
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR installed.proowner IS DISTINCT FROM (SELECT oid FROM executor)
            UNION ALL
            SELECT 'master data loader function ACL differs: ' || COALESCE(installed.acl, 'default')
            FROM installed
            WHERE installed.acl IS DISTINCT FROM
              'paqueteria_master_data_executor:EXECUTE:false,paqueteria_master_data_loader:EXECUTE:false'
            UNION ALL
            SELECT 'master data loader function exists without both master data roles'
            FROM installed
            WHERE NOT EXISTS (SELECT 1 FROM executor) OR NOT EXISTS (SELECT 1 FROM loader)
            UNION ALL
            SELECT 'master data loader function exists without platform.master_data_deployment_gate'
            FROM installed WHERE NOT EXISTS (SELECT 1 FROM gate)
            UNION ALL
            SELECT 'platform.master_data_deployment_gate grants ' || t.privilege_type || ' to ' ||
                   CASE WHEN t.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(t.grantee) END
            FROM table_acl t JOIN gate ON gate.oid=t.oid
            WHERE t.grantee<>gate.relowner
            UNION ALL
            SELECT 'platform.master_data_deployment_gate column grant to ' ||
                   CASE WHEN c.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(c.grantee) END
            FROM column_acl c
            WHERE c.table_schema='platform' AND c.table_name='master_data_deployment_gate'
              AND c.grantee NOT IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'platform.master_data_deployment_gate must be owned by paqueteria_migrator'
            FROM gate WHERE pg_catalog.pg_get_userbyid(gate.relowner)<>'paqueteria_migrator'
            UNION ALL
            SELECT 'platform.master_data_deployment_gate must force RLS with only the migrator policy'
            FROM gate JOIN pg_catalog.pg_class c ON c.oid=gate.oid
            WHERE NOT c.relrowsecurity OR NOT c.relforcerowsecurity
               OR (SELECT string_agg(pol.polname || ':' || pol.polcmd::text || ':' || pol.polpermissive::text || ':'
                     || (SELECT string_agg(CASE WHEN r=0 THEN 'public' ELSE pg_catalog.pg_get_userbyid(r) END, '+')
                         FROM unnest(pol.polroles) r)
                     || ':' || COALESCE(pg_catalog.pg_get_expr(pol.polqual, pol.polrelid), '') || ':'
                     || COALESCE(pg_catalog.pg_get_expr(pol.polwithcheck, pol.polrelid), ''), ',')
                   FROM pg_catalog.pg_policy pol WHERE pol.polrelid=gate.oid)
                  IS DISTINCT FROM 'master_data_deployment_gate_migrator:*:true:paqueteria_migrator:true:true'
            UNION ALL
            SELECT 'master data executor owns another function: ' || p.oid::regprocedure::text
            FROM pg_catalog.pg_proc p
            WHERE p.proowner IN (SELECT oid FROM executor)
              AND p.oid IS DISTINCT FROM pg_catalog.to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)')
            UNION ALL
            SELECT 'master data loader has a member that is also a deployment principal: ' || member.rolname
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            WHERE m.roleid IN (SELECT oid FROM loader)
              AND (m.inherit_option OR m.set_option)
              AND pg_catalog.pg_has_role(m.member,'paqueteria_migrator','MEMBER')
            UNION ALL
            -- MDM-001 loader hardening (X1/X2): once recorded, the operator reference table exists and the
            -- function refuses deployment principals and no longer hashes the login.
            SELECT 'platform.master_data_operator_refs is missing'
            WHERE @hardened AND NOT EXISTS (SELECT 1 FROM operator_refs)
            UNION ALL
            SELECT 'master data loader function lacks the MDM-001 hardening'
            FROM installed
            WHERE @hardened
              AND (position('MDM001_DEPLOYMENT_PRINCIPAL_REFUSED' IN installed.prosrc)=0
                OR position('platform.master_data_operator_refs' IN installed.prosrc)=0
                OR position('paquetenvia.mdm-001.operator:' IN installed.prosrc)<>0)
            UNION ALL
            SELECT 'platform.master_data_operator_refs grants ' || t.privilege_type || ' to ' ||
                   CASE WHEN t.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(t.grantee) END
            FROM table_acl t JOIN operator_refs r ON r.oid=t.oid
            WHERE t.grantee<>r.relowner
            UNION ALL
            SELECT 'platform.master_data_operator_refs column grant to ' ||
                   CASE WHEN c.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(c.grantee) END
            FROM column_acl c
            WHERE c.table_schema='platform' AND c.table_name='master_data_operator_refs'
              AND c.grantee NOT IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'platform.master_data_operator_refs must be owned by paqueteria_migrator'
            FROM operator_refs WHERE pg_catalog.pg_get_userbyid(operator_refs.relowner)<>'paqueteria_migrator'
            UNION ALL
            SELECT 'platform.master_data_operator_refs must force RLS with only the migrator policy'
            FROM operator_refs r
            WHERE NOT r.relrowsecurity OR NOT r.relforcerowsecurity
               OR (SELECT string_agg(pol.polname || ':' || pol.polcmd::text || ':' || pol.polpermissive::text || ':'
                     || (SELECT string_agg(CASE WHEN x=0 THEN 'public' ELSE pg_catalog.pg_get_userbyid(x) END, '+')
                         FROM unnest(pol.polroles) x)
                     || ':' || COALESCE(pg_catalog.pg_get_expr(pol.polqual, pol.polrelid), '') || ':'
                     || COALESCE(pg_catalog.pg_get_expr(pol.polwithcheck, pol.polrelid), ''), ',')
                   FROM pg_catalog.pg_policy pol WHERE pol.polrelid=r.oid)
                  IS DISTINCT FROM 'master_data_operator_refs_migrator:*:true:paqueteria_migrator:true:true'
            """,
            cancellationToken,
            new NpgsqlParameter<bool>("hardened", hardened),
            new NpgsqlParameter<string[]>("grants", grants),
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03 lane contract: after the Dispatch lane (and after any E-002
    /// temporary grant is revoked) the role and both functions must exist and satisfy the exact executor boundary.
    /// </summary>
    public static async Task AssertOperatorOutboxExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'operator outbox executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_operator_outbox_executor') IS NULL
            UNION ALL
            SELECT 'operator outbox function is missing: ' || signature
            FROM unnest(@functions::text[]) signature
            WHERE pg_catalog.to_regprocedure(signature) IS NULL
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("functions", OperatorOutboxFunctions.ToArray())).ConfigureAwait(false);
        await AssertOperatorOutboxExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: once the operator outbox executor exists it holds exactly
    /// USAGE on identity, organizations, orders, dispatch and platform and the AI-18 column grants, none
    /// grantable, no table-wide grant and no CREATE anywhere; it inherits nothing, no runtime or bootstrap role
    /// can reach it, and it owns no relation, schema or type and no function but its two. Each installed function
    /// is a SECURITY DEFINER with search_path=pg_catalog, pg_temp, contains no dynamic SQL and no RETURNING, and
    /// its ACL is exactly the owner and <c>paqueteria_app</c>. Privileges are read from the catalog ACLs. The role
    /// may exist without the functions (fresh AI-18 before the lane, or after the lane is rolled back). Once the Dispatch
    /// OBS-003-OUTBOX-TRACE-CONTEXT step is recorded (history, not the catalog, decides), the exact grants also include
    /// <see cref="OperatorOutboxExecutorTraceContextGrants"/>.
    /// </summary>
    private static async Task AssertOperatorOutboxExecutorBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        var grants = await E002OperatorOutboxStateReader
            .IsOutboxTraceContextAppliedAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ? OperatorOutboxExecutorGrants.Concat(OperatorOutboxExecutorTraceContextGrants).ToArray()
            : OperatorOutboxExecutorGrants.ToArray();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_operator_outbox_executor'
            ),
            expected AS (
              SELECT split_part(grant_text, '.', 1) AS table_schema,
                     split_part(grant_text, '.', 2) AS table_name,
                     split_part(split_part(grant_text, '.', 3), ':', 1) AS column_name,
                     split_part(grant_text, ':', 2) AS privilege_type
              FROM unnest(@grants::text[]) grant_text),
            actual AS (
              SELECT n.nspname::text AS table_schema, c.relname::text AS table_name, att.attname::text AS column_name,
                     acl.privilege_type, acl.is_grantable
              FROM pg_catalog.pg_attribute att
              JOIN pg_catalog.pg_class c ON c.oid=att.attrelid
              JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(att.attacl) acl
              WHERE att.attacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM executor)),
            installed AS (
              SELECT signature,p.oid,p.proowner,p.prosecdef,p.proconfig,p.prosrc,
                     (SELECT string_agg(entry, ',' ORDER BY entry COLLATE "C")
                      FROM (SELECT CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(acl.grantee) END
                                     || ':' || acl.privilege_type || ':' || acl.is_grantable::text AS entry
                            FROM pg_catalog.aclexplode(p.proacl) acl) entries) AS acl
              FROM unnest(@functions::text[]) signature
              JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(signature))
            SELECT 'missing operator outbox executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected operator outbox executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'operator outbox executor column grant is grantable: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name
            FROM actual a WHERE a.is_grantable
            UNION ALL
            SELECT 'unexpected operator outbox executor table grant: ' || n.nspname || '.' || c.relname || ':' || acl.privilege_type
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) acl
            WHERE c.relacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'operator outbox executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM
                   (n.nspname IN ('identity','organizations','orders','dispatch','platform')))
            UNION ALL
            SELECT 'operator outbox executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m WHERE m.member IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'operator outbox executor is reachable from ' || member.rolname
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            WHERE m.roleid IN (SELECT oid FROM executor)
              AND member.rolname IN ('paqueteria_app','paqueteria_worker','paqueteria_bootstrap')
            UNION ALL
            SELECT 'operator outbox executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'operator outbox executor owns another function: ' || p.oid::regprocedure::text
            FROM pg_catalog.pg_proc p
            WHERE p.proowner IN (SELECT oid FROM executor)
              AND NOT EXISTS (SELECT 1 FROM installed WHERE installed.oid=p.oid)
            UNION ALL
            SELECT 'operator outbox function is unsafe: ' || installed.signature
            FROM installed
            WHERE NOT installed.prosecdef
               OR installed.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog, pg_temp']
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR installed.prosrc ~* 'RETURNING'
               OR installed.proowner IS DISTINCT FROM (SELECT oid FROM executor)
            UNION ALL
            SELECT 'operator outbox function ACL differs: ' || installed.signature || ' ' || COALESCE(installed.acl, 'default')
            FROM installed
            WHERE installed.acl IS DISTINCT FROM
              'paqueteria_app:EXECUTE:false,paqueteria_operator_outbox_executor:EXECUTE:false'
            UNION ALL
            SELECT 'operator outbox function exists without the operator outbox executor role: ' || installed.signature
            FROM installed WHERE NOT EXISTS (SELECT 1 FROM executor)
            UNION ALL
            SELECT 'operator outbox functions are only partially installed'
            FROM (SELECT count(*) AS present FROM installed) c
            WHERE c.present = 1
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("grants", grants),
            new NpgsqlParameter<string[]>("functions", OperatorOutboxFunctions.ToArray()),
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// ORD-AUTO-CLOSE-2026-10-10 lane contract: after the Orders lane (and after any E-002 temporary grant is revoked)
    /// the role and its function must exist and satisfy the exact executor boundary.
    /// </summary>
    public static async Task AssertAutoCloseExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'auto-close executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_auto_close_executor') IS NULL
            UNION ALL
            SELECT 'auto-close discovery function is missing: security.list_auto_close_owner_organizations(uuid,integer)'
            WHERE pg_catalog.to_regprocedure('security.list_auto_close_owner_organizations(uuid,integer)') IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertAutoCloseExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// ORD-AUTO-CLOSE-2026-10-10: once the auto-close executor exists it holds exactly USAGE on <c>orders</c> and
    /// SELECT(owner_org_id,status) on <c>orders.orders</c>, none grantable, no table-wide grant and no CREATE anywhere;
    /// it inherits nothing, no runtime or bootstrap role can reach it, and it owns no relation, schema or type and no
    /// function but its own. That function is a STABLE SECURITY DEFINER with search_path=pg_catalog, orders, pg_temp,
    /// contains no dynamic SQL and no RETURNING, and its ACL is exactly the owner and <c>paqueteria_worker</c>.
    /// Privileges are read from the catalog ACLs. The role may exist without the function (fresh AI-18 before the
    /// lane, or after the lane is rolled back).
    /// </summary>
    private static async Task AssertAutoCloseExecutorBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_auto_close_executor'
            ),
            expected AS (
              SELECT split_part(grant_text, '.', 1) AS table_schema,
                     split_part(grant_text, '.', 2) AS table_name,
                     split_part(split_part(grant_text, '.', 3), ':', 1) AS column_name,
                     split_part(grant_text, ':', 2) AS privilege_type
              FROM unnest(@grants::text[]) grant_text),
            actual AS (
              SELECT n.nspname::text AS table_schema, c.relname::text AS table_name, att.attname::text AS column_name,
                     acl.privilege_type, acl.is_grantable
              FROM pg_catalog.pg_attribute att
              JOIN pg_catalog.pg_class c ON c.oid=att.attrelid
              JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(att.attacl) acl
              WHERE att.attacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM executor)),
            installed AS (
              SELECT p.oid,p.proowner,p.prosecdef,p.provolatile,p.proconfig,p.prosrc,
                     (SELECT string_agg(entry, ',' ORDER BY entry COLLATE "C")
                      FROM (SELECT CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(acl.grantee) END
                                     || ':' || acl.privilege_type || ':' || acl.is_grantable::text AS entry
                            FROM pg_catalog.aclexplode(p.proacl) acl) entries) AS acl
              FROM pg_catalog.pg_proc p
              WHERE p.oid=pg_catalog.to_regprocedure(@function))
            SELECT 'missing auto-close executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected auto-close executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'auto-close executor column grant is grantable: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name
            FROM actual a WHERE a.is_grantable
            UNION ALL
            SELECT 'unexpected auto-close executor table grant: ' || n.nspname || '.' || c.relname || ':' || acl.privilege_type
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) acl
            WHERE c.relacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'auto-close executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM (n.nspname='orders'))
            UNION ALL
            SELECT 'auto-close executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m WHERE m.member IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'auto-close executor is reachable from ' || member.rolname
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            WHERE m.roleid IN (SELECT oid FROM executor)
              AND member.rolname IN ('paqueteria_app','paqueteria_worker','paqueteria_bootstrap')
            UNION ALL
            SELECT 'auto-close executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'auto-close executor owns another function: ' || p.oid::regprocedure::text
            FROM pg_catalog.pg_proc p
            WHERE p.proowner IN (SELECT oid FROM executor)
              AND NOT EXISTS (SELECT 1 FROM installed WHERE installed.oid=p.oid)
            UNION ALL
            SELECT 'auto-close discovery function is unsafe: security.list_auto_close_owner_organizations(uuid,integer)'
            FROM installed
            WHERE NOT installed.prosecdef
               OR installed.provolatile <> 's'
               OR installed.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog, orders, pg_temp']
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR installed.prosrc ~* 'RETURNING'
               OR installed.proowner IS DISTINCT FROM (SELECT oid FROM executor)
            UNION ALL
            SELECT 'auto-close discovery function ACL differs: ' || COALESCE(installed.acl, 'default')
            FROM installed
            WHERE installed.acl IS DISTINCT FROM
              'paqueteria_auto_close_executor:EXECUTE:false,paqueteria_worker:EXECUTE:false'
            UNION ALL
            SELECT 'auto-close discovery function exists without the auto-close executor role'
            FROM installed WHERE NOT EXISTS (SELECT 1 FROM executor)
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("grants", AutoCloseExecutorGrants.ToArray()),
            new NpgsqlParameter<string>("function", AutoCloseFunction),
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-18 recipient notices step 3, amended by O5; deployment assertion 41):
    /// once <c>paqueteria_notification_receipt_executor</c> exists it is NOLOGIN BYPASSRLS (checked with the other role
    /// flags), inherits nothing, no runtime or bootstrap role reaches it, it owns no relation, schema or type and no
    /// function but its five, has no default privilege, no table-wide grant, no direct privilege on either outbox table and
    /// no CREATE anywhere. With its Notifications lane step recorded it holds exactly USAGE on notifications and security,
    /// <see cref="ReceiptExecutorColumnGrants"/> (none grantable) and EXECUTE on
    /// <c>security.emit_notification_status_changed</c> and no other function it does not own; its five functions are
    /// SECURITY DEFINER with a pinned search_path, no dynamic SQL and no RETURNING, with EXECUTE for exactly their owner and
    /// one runtime role; and <c>notifications.recipient_auto_replies</c> forces row security with no policy and grants no
    /// runtime role anything. Without the step (fresh AI-18, or after the step is rolled back) it holds none of them.
    /// </summary>
    private static async Task AssertReceiptExecutorBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool laneApplied,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_notification_receipt_executor'
            ),
            expected AS (
              SELECT split_part(grant_text, '.', 1) AS table_schema,
                     split_part(grant_text, '.', 2) AS table_name,
                     split_part(split_part(grant_text, '.', 3), ':', 1) AS column_name,
                     split_part(grant_text, ':', 2) AS privilege_type
              FROM unnest(@grants::text[]) grant_text
              WHERE @applied),
            actual AS (
              SELECT n.nspname::text AS table_schema, c.relname::text AS table_name, att.attname::text AS column_name,
                     acl.privilege_type, acl.is_grantable
              FROM pg_catalog.pg_attribute att
              JOIN pg_catalog.pg_class c ON c.oid=att.attrelid
              JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(att.attacl) acl
              WHERE att.attacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM executor)),
            functions AS (
              SELECT signature, pg_catalog.to_regprocedure(signature)::oid AS oid, grantee
              FROM (SELECT unnest(@api_functions::text[]) AS signature, 'paqueteria_app' AS grantee
                    UNION ALL SELECT unnest(@worker_functions::text[]), 'paqueteria_worker') listed),
            installed AS (
              SELECT f.signature, f.grantee, p.oid, p.proowner, p.prosecdef, p.proconfig, p.prosrc,
                     (SELECT string_agg(entry, ',' ORDER BY entry COLLATE "C")
                      FROM (SELECT CASE WHEN acl.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(acl.grantee) END
                                     || ':' || acl.privilege_type || ':' || acl.is_grantable::text AS entry
                            FROM pg_catalog.aclexplode(p.proacl) acl) entries) AS acl
              FROM functions f JOIN pg_catalog.pg_proc p ON p.oid=f.oid),
            store AS (SELECT pg_catalog.to_regclass('notifications.recipient_auto_replies') AS oid)
            SELECT 'receipt executor role is missing while its lane step is recorded'
            WHERE @applied AND NOT EXISTS (SELECT 1 FROM executor)
            UNION ALL
            SELECT 'missing receipt executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected receipt executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'receipt executor column grant is grantable: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name
            FROM actual a WHERE a.is_grantable
            UNION ALL
            SELECT 'unexpected receipt executor table grant: ' || n.nspname || '.' || c.relname || ':' || acl.privilege_type
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) acl
            WHERE c.relacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'receipt executor has direct outbox access: ' || lane
            FROM unnest(ARRAY['platform.outbox_events','platform.location_outbox_events']) lane CROSS JOIN executor
            WHERE has_table_privilege(executor.oid, lane, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE')
            UNION ALL
            SELECT 'receipt executor has a default privilege'
            FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) acl
            WHERE acl.grantee IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'receipt executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM
                  (@applied AND n.nspname IN ('notifications','security')))
            UNION ALL
            SELECT 'receipt executor has CREATE on public'
            FROM executor WHERE has_schema_privilege(executor.oid,'public','CREATE')
            UNION ALL
            SELECT 'receipt executor executes a function it does not own: ' || p.oid::regprocedure::text
            FROM pg_catalog.pg_proc p CROSS JOIN LATERAL pg_catalog.aclexplode(p.proacl) acl
            WHERE p.proacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM executor)
              AND p.proowner NOT IN (SELECT oid FROM executor)
              AND NOT (@applied AND p.oid=pg_catalog.to_regprocedure(@emit))
            UNION ALL
            SELECT 'receipt executor is missing EXECUTE on the status-changed writer'
            FROM executor
            WHERE @applied AND NOT has_function_privilege(executor.oid, @emit, 'EXECUTE')
            UNION ALL
            SELECT 'receipt executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m WHERE m.member IN (SELECT oid FROM executor)
            UNION ALL
            SELECT 'receipt executor is reachable from ' || member.rolname
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            WHERE m.roleid IN (SELECT oid FROM executor)
              AND member.rolname IN ('paqueteria_app','paqueteria_worker','paqueteria_realtime','paqueteria_bootstrap')
            UNION ALL
            SELECT 'receipt executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'receipt executor owns another function: ' || p.oid::regprocedure::text
            FROM pg_catalog.pg_proc p
            WHERE p.proowner IN (SELECT oid FROM executor)
              AND NOT EXISTS (SELECT 1 FROM installed WHERE installed.oid=p.oid)
            UNION ALL
            SELECT 'receipt executor function is missing while its lane step is recorded: ' || f.signature
            FROM functions f
            WHERE @applied AND f.oid IS NULL
            UNION ALL
            SELECT 'receipt executor function is unsafe: ' || installed.signature
            FROM installed
            WHERE NOT installed.prosecdef
               OR installed.proconfig IS NULL
               OR installed.proconfig[1] NOT LIKE 'search_path=pg_catalog, %'
               OR installed.proconfig[1] NOT LIKE '%, pg_temp'
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR installed.prosrc ~* '(^|[^a-z_])RETURNING([^a-z_]|$)'
               OR installed.proowner IS DISTINCT FROM (SELECT oid FROM executor)
            UNION ALL
            SELECT 'receipt executor function ACL differs: ' || installed.signature || ' ' || COALESCE(installed.acl, 'default')
            FROM installed
            WHERE installed.acl IS DISTINCT FROM (SELECT string_agg(entry, ',' ORDER BY entry COLLATE "C")
              FROM unnest(ARRAY['paqueteria_notification_receipt_executor:EXECUTE:false',
                                installed.grantee || ':EXECUTE:false']) entry)
            UNION ALL
            SELECT 'receipt executor function exists without the receipt executor role: ' || installed.signature
            FROM installed WHERE NOT EXISTS (SELECT 1 FROM executor)
            UNION ALL
            SELECT 'notifications.recipient_auto_replies is missing ENABLE/FORCE RLS'
            FROM store JOIN pg_catalog.pg_class c ON c.oid=store.oid
            WHERE NOT c.relrowsecurity OR NOT c.relforcerowsecurity
            UNION ALL
            SELECT 'notifications.recipient_auto_replies has a row security policy'
            FROM store WHERE store.oid IS NOT NULL AND EXISTS (SELECT 1 FROM pg_catalog.pg_policy WHERE polrelid=store.oid)
            UNION ALL
            SELECT 'runtime role ' || role.name || ' holds a privilege on notifications.recipient_auto_replies'
            FROM store
            CROSS JOIN unnest(ARRAY['paqueteria_app','paqueteria_worker','paqueteria_realtime','paqueteria_bootstrap']) role(name)
            CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) privilege(name)
            WHERE store.oid IS NOT NULL
              AND pg_catalog.to_regrole(role.name) IS NOT NULL
              AND pg_catalog.has_table_privilege(role.name,store.oid,privilege.name)
            UNION ALL
            SELECT 'notifications.recipient_auto_replies presence differs from the recorded lane step'
            FROM store WHERE (store.oid IS NOT NULL) IS DISTINCT FROM @applied
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("grants", ReceiptExecutorColumnGrants.ToArray()),
            new NpgsqlParameter<bool>("applied", laneApplied),
            new NpgsqlParameter<string[]>("api_functions", ReceiptExecutorApiFunctions.ToArray()),
            new NpgsqlParameter<string[]>("worker_functions", ReceiptExecutorWorkerFunctions.ToArray()),
            new NpgsqlParameter<string>("emit", "security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamp with time zone)"),
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12 lane contract: after the Notifications lane step the role must exist
    /// and hold exactly its lane grants.
    /// </summary>
    public static async Task AssertRealtimeDispatcherRoleInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            "SELECT 'realtime dispatcher role is missing' WHERE pg_catalog.to_regrole('paqueteria_realtime') IS NULL",
            cancellationToken).ConfigureAwait(false);
        await AssertRealtimeDispatcherBoundaryAsync(connection, transaction, laneApplied: true, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: once <c>paqueteria_realtime</c> exists it is NOLOGIN NOBYPASSRLS
    /// (checked with the other role flags), inherits nothing, no group role reaches it, it owns nothing and has no
    /// default privilege, no table-wide grant, no direct privilege on either outbox table and no CREATE anywhere. With its
    /// Notifications lane step recorded it holds exactly USAGE on <see cref="RealtimeDispatcherUsageSchemas"/>, EXECUTE
    /// on <see cref="RealtimeDispatcherFunctions"/> and SELECT on <see cref="RealtimeDispatcherColumnGrants"/>, none
    /// grantable, every column also readable by <c>paqueteria_app</c>, and no EXECUTE beyond what PUBLIC holds on any
    /// other security, platform, notifications or extensions routine; without it (fresh AI-18, or after the step is
    /// rolled back) it holds none of them. Privileges are read from the catalog ACLs.
    /// </summary>
    private static async Task AssertRealtimeDispatcherBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        bool laneApplied,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH realtime AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_realtime'
            ),
            expected AS (
              SELECT split_part(grant_text, '.', 1) AS table_schema,
                     split_part(grant_text, '.', 2) AS table_name,
                     split_part(split_part(grant_text, '.', 3), ':', 1) AS column_name,
                     split_part(grant_text, ':', 2) AS privilege_type
              FROM unnest(@grants::text[]) grant_text
              WHERE @applied),
            actual AS (
              SELECT n.nspname::text AS table_schema, c.relname::text AS table_name, att.attname::text AS column_name,
                     acl.privilege_type, acl.is_grantable
              FROM pg_catalog.pg_attribute att
              JOIN pg_catalog.pg_class c ON c.oid=att.attrelid
              JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(att.attacl) acl
              WHERE att.attacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM realtime)),
            functions AS (
              SELECT signature, pg_catalog.to_regprocedure(signature)::oid AS oid
              FROM unnest(@functions::text[]) signature)
            SELECT 'realtime dispatcher role is missing while its lane step is recorded'
            WHERE @applied AND NOT EXISTS (SELECT 1 FROM realtime)
            UNION ALL
            SELECT 'missing realtime dispatcher column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN realtime
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected realtime dispatcher column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'realtime dispatcher column grant is grantable: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name
            FROM actual a WHERE a.is_grantable
            UNION ALL
            SELECT 'realtime dispatcher reads a column paqueteria_app cannot read: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name
            FROM expected e
            WHERE NOT has_column_privilege('paqueteria_app', e.table_schema || '.' || e.table_name, e.column_name, 'SELECT')
            UNION ALL
            SELECT 'unexpected realtime dispatcher table grant: ' || n.nspname || '.' || c.relname || ':' || acl.privilege_type
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) acl
            WHERE c.relacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM realtime)
            UNION ALL
            SELECT 'realtime dispatcher has direct outbox access: ' || lane
            FROM unnest(ARRAY['platform.outbox_events','platform.location_outbox_events']) lane CROSS JOIN realtime
            WHERE has_table_privilege(realtime.oid, lane, 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE')
            UNION ALL
            SELECT 'realtime dispatcher has a default privilege'
            FROM pg_catalog.pg_default_acl d CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) acl
            WHERE acl.grantee IN (SELECT oid FROM realtime)
            UNION ALL
            SELECT 'realtime dispatcher schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN realtime
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(realtime.oid,n.oid,'CREATE')
                OR has_schema_privilege(realtime.oid,n.oid,'USAGE') IS DISTINCT FROM
                  (@applied AND n.nspname=ANY(@usage::text[])))
            UNION ALL
            SELECT 'realtime dispatcher has CREATE on public'
            FROM realtime WHERE has_schema_privilege(realtime.oid,'public','CREATE')
            UNION ALL
            SELECT 'missing realtime dispatcher EXECUTE: ' || f.signature
            FROM functions f CROSS JOIN realtime
            WHERE @applied AND (f.oid IS NULL OR NOT has_function_privilege(realtime.oid,f.oid,'EXECUTE'))
            UNION ALL
            SELECT 'unexpected realtime dispatcher EXECUTE: ' || p.oid::regprocedure::text
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
            CROSS JOIN realtime
            WHERE n.nspname IN ('security','platform','notifications','extensions')
              AND has_function_privilege(realtime.oid,p.oid,'EXECUTE')
              AND NOT has_function_privilege('public',p.oid,'EXECUTE')
              AND NOT (@applied AND p.oid IN (SELECT oid FROM functions WHERE oid IS NOT NULL))
            UNION ALL
            SELECT 'realtime dispatcher routine grant is grantable or not EXECUTE: ' || p.oid::regprocedure::text
            FROM pg_catalog.pg_proc p CROSS JOIN LATERAL pg_catalog.aclexplode(p.proacl) acl
            WHERE p.proacl IS NOT NULL AND acl.grantee IN (SELECT oid FROM realtime)
              AND (acl.privilege_type<>'EXECUTE' OR acl.is_grantable)
            UNION ALL
            SELECT 'realtime dispatcher inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m WHERE m.member IN (SELECT oid FROM realtime)
            UNION ALL
            SELECT 'realtime dispatcher is reachable from group role ' || member.rolname
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            WHERE m.roleid IN (SELECT oid FROM realtime) AND NOT member.rolcanlogin
            UNION ALL
            SELECT 'realtime dispatcher owns a relation, schema, type or function'
            FROM realtime
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=realtime.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=realtime.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=realtime.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc WHERE proowner=realtime.oid)
            """,
            cancellationToken,
            new NpgsqlParameter<bool>("applied", laneApplied),
            new NpgsqlParameter<string[]>("grants", RealtimeDispatcherColumnGrants.ToArray()),
            new NpgsqlParameter<string[]>("functions", RealtimeDispatcherFunctions.ToArray()),
            new NpgsqlParameter<string[]>("usage", RealtimeDispatcherUsageSchemas.ToArray()),
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// BFF-SESSION-TABLE-SHAPE lane contract: after the Identity session-store migration (and after any
    /// E-002 temporary grant is revoked) the role, the table and the five functions must exist and satisfy
    /// the exact executor boundary.
    /// </summary>
    public static async Task AssertSessionExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'session executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_session_executor') IS NULL
            UNION ALL
            SELECT 'BFF session table is missing'
            WHERE pg_catalog.to_regclass('identity.bff_sessions') IS NULL
            UNION ALL
            SELECT 'BFF logout jti table is missing'
            WHERE pg_catalog.to_regclass('identity.bff_logout_jtis') IS NULL
            UNION ALL
            SELECT 'BFF session function is missing: ' || signature
            FROM (VALUES
              ('security.create_bff_session(bytea,text,text,bytea,timestamp with time zone)'),
              ('security.resolve_bff_session(bytea)'),
              ('security.revoke_bff_session(bytea)'),
              ('security.revoke_bff_session(text)'),
              ('security.revoke_bff_session(text,timestamp with time zone)'),
              ('security.register_bff_logout_jti(bytea,timestamp with time zone)')) expected(signature)
            WHERE pg_catalog.to_regprocedure(signature) IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        await AssertSessionExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    /// <summary>
    /// BFF-SESSION-TABLE-SHAPE: once the session executor exists it holds exactly USAGE on
    /// <c>identity</c>, SELECT on the seven columns, INSERT on the six non-revocation columns and
    /// UPDATE(ticket_ciphertext,revoked_at) of <c>identity.bff_sessions</c> plus INSERT(jti_hash,created_at,
    /// expires_at) on <c>identity.bff_logout_jtis</c> (BFF-LOGOUT-JTI-PERSISTENCE), inherits nothing, and
    /// its six functions are pinned SECURITY DEFINERs executable by <c>paqueteria_app</c> only. Both tables
    /// force RLS without any policy and grant nothing to the runtime roles or PUBLIC.
    /// </summary>
    private static async Task AssertSessionExecutorBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_session_executor'
            ),
            sessions AS (
              SELECT c.oid,c.relname,c.relrowsecurity,c.relforcerowsecurity
              FROM pg_catalog.pg_class c
              WHERE c.oid IN (pg_catalog.to_regclass('identity.bff_sessions'),
                              pg_catalog.to_regclass('identity.bff_logout_jtis'))
            ),
            expected(table_schema,table_name,column_name,privilege_type) AS (VALUES
              ('identity','bff_sessions','session_key_hash','SELECT'),
              ('identity','bff_sessions','identity_subject','SELECT'),
              ('identity','bff_sessions','authcenter_sid','SELECT'),
              ('identity','bff_sessions','ticket_ciphertext','SELECT'),
              ('identity','bff_sessions','created_at','SELECT'),
              ('identity','bff_sessions','expires_at','SELECT'),
              ('identity','bff_sessions','revoked_at','SELECT'),
              ('identity','bff_sessions','session_key_hash','INSERT'),
              ('identity','bff_sessions','identity_subject','INSERT'),
              ('identity','bff_sessions','authcenter_sid','INSERT'),
              ('identity','bff_sessions','ticket_ciphertext','INSERT'),
              ('identity','bff_sessions','created_at','INSERT'),
              ('identity','bff_sessions','expires_at','INSERT'),
              ('identity','bff_sessions','ticket_ciphertext','UPDATE'),
              ('identity','bff_sessions','revoked_at','UPDATE'),
              ('identity','bff_logout_jtis','jti_hash','INSERT'),
              ('identity','bff_logout_jtis','created_at','INSERT'),
              ('identity','bff_logout_jtis','expires_at','INSERT')),
            actual AS (
              SELECT table_schema,table_name,column_name,privilege_type
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_session_executor'),
            fn(signature) AS (VALUES
              ('security.create_bff_session(bytea,text,text,bytea,timestamp with time zone)'),
              ('security.resolve_bff_session(bytea)'),
              ('security.revoke_bff_session(bytea)'),
              ('security.revoke_bff_session(text)'),
              ('security.revoke_bff_session(text,timestamp with time zone)'),
              ('security.register_bff_logout_jti(bytea,timestamp with time zone)')),
            installed AS (
              SELECT fn.signature,p.oid,p.prosecdef,p.proconfig,p.prosrc
              FROM fn JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(fn.signature))
            SELECT 'missing session executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected session executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'unexpected session executor table grant: ' || table_schema || '.' || table_name || ':' || privilege_type
            FROM information_schema.table_privileges
            WHERE grantee='paqueteria_session_executor'
            UNION ALL
            SELECT 'session executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM (n.nspname='identity'))
            UNION ALL
            SELECT 'session executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m JOIN executor ON m.member=executor.oid
            UNION ALL
            SELECT 'session executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'BFF session function is unsafe: ' || installed.signature
            FROM installed
            WHERE NOT installed.prosecdef
               OR NOT ('search_path=pg_catalog, identity, pg_temp'=ANY(COALESCE(installed.proconfig,ARRAY[]::text[])))
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR has_function_privilege('public',installed.oid,'EXECUTE')
               OR has_function_privilege('paqueteria_worker',installed.oid,'EXECUTE')
               OR NOT has_function_privilege('paqueteria_app',installed.oid,'EXECUTE')
            UNION ALL
            SELECT 'BFF session function exists without the session executor role: ' || installed.signature
            FROM installed WHERE NOT EXISTS (SELECT 1 FROM executor)
            UNION ALL
            SELECT 'BFF session functions are only partially installed'
            FROM (SELECT count(*) AS present FROM installed) c
            WHERE c.present BETWEEN 1 AND 5
            UNION ALL
            SELECT 'session executor exists without identity.bff_sessions and identity.bff_logout_jtis'
            FROM executor WHERE (SELECT count(*) FROM sessions) <> 2
            UNION ALL
            SELECT 'identity.' || sessions.relname || ' must force RLS without any policy'
            FROM sessions
            WHERE NOT sessions.relrowsecurity OR NOT sessions.relforcerowsecurity
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy WHERE polrelid=sessions.oid)
            UNION ALL
            SELECT 'identity.' || sessions.relname || ' is directly reachable by ' || grantee.name
            FROM sessions CROSS JOIN (VALUES ('paqueteria_app'),('paqueteria_worker'),('public')) grantee(name)
            WHERE has_table_privilege(grantee.name,sessions.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
               OR has_any_column_privilege(grantee.name,sessions.oid,'SELECT,INSERT,UPDATE,REFERENCES')
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())).ConfigureAwait(false);
    }

    /// <summary>
    /// REG-001 and REG-002 lane contract: after the Organizations lane (and after any E-002 temporary
    /// grant is revoked) the role and its nine functions must exist and satisfy the exact executor boundary.
    /// </summary>
    public static async Task AssertRegistrationExecutorInstalledAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var violations = new List<string>();
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            SELECT 'registration executor role is missing'
            WHERE pg_catalog.to_regrole('paqueteria_registration_executor') IS NULL
            UNION ALL
            SELECT 'registration function is missing: ' || signature
            FROM unnest(@signatures::text[]) expected(signature)
            WHERE pg_catalog.to_regprocedure(signature) IS NULL
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("signatures", RegistrationFunctions.Concat(PendingMembershipFunctions)
                .Select(item => item.Signature).ToArray()))
            .ConfigureAwait(false);
        await AssertRegistrationExecutorBoundaryAsync(connection, transaction, violations, cancellationToken)
            .ConfigureAwait(false);
        if (violations.Count != 0)
        {
            throw new DatabaseAssertionException(violations.AsReadOnly());
        }
    }

    private static readonly (string Signature, string SearchPath)[] RegistrationFunctions =
    [
        ("security.register_identity_subject(text,uuid)", "search_path=pg_catalog, identity, pg_temp"),
        ("security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)",
            "search_path=pg_catalog, identity, organizations, platform, pg_temp"),
        ("security.list_own_organization_applications(uuid)", "search_path=pg_catalog, identity, organizations, pg_temp"),
        ("security.list_pending_ally_organizations(uuid,uuid,integer)",
            "search_path=pg_catalog, identity, organizations, pg_temp"),
        ("security.decide_ally_organization(uuid,uuid,uuid,boolean,text)",
            "search_path=pg_catalog, identity, organizations, platform, pg_temp"),
    ];

    /// <summary>
    /// REG-001: once the registration executor exists it holds exactly USAGE on <c>identity</c>,
    /// <c>organizations</c> and <c>platform</c> and the AI-18 column grants, no table-wide grant, inherits
    /// nothing, owns no relation, and its functions are pinned SECURITY DEFINERs executable by
    /// <c>paqueteria_app</c> only. The role may exist without functions (fresh AI-18 before the lane, or
    /// after the lane is rolled back); a function without the role, or a partial set, is a violation.
    /// </summary>
    private static async Task AssertRegistrationExecutorBoundaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH executor AS (
              SELECT oid FROM pg_catalog.pg_roles WHERE rolname='paqueteria_registration_executor'
            ),
            expected_reg001(table_schema,table_name,column_name,privilege_type) AS (VALUES
              ('identity','users','id','SELECT'),
              ('identity','users','identity_subject','SELECT'),
              ('identity','users','status','SELECT'),
              ('identity','users','id','INSERT'),
              ('identity','users','identity_subject','INSERT'),
              ('identity','users','status','INSERT'),
              ('identity','users','created_at','INSERT'),
              ('organizations','organizations','id','SELECT'),
              ('organizations','organizations','organization_type','SELECT'),
              ('organizations','organizations','legal_name','SELECT'),
              ('organizations','organizations','display_name','SELECT'),
              ('organizations','organizations','status','SELECT'),
              ('organizations','organizations','self_service_creator_user_id','SELECT'),
              ('organizations','organizations','created_at','SELECT'),
              ('organizations','organizations','id','INSERT'),
              ('organizations','organizations','organization_type','INSERT'),
              ('organizations','organizations','legal_name','INSERT'),
              ('organizations','organizations','display_name','INSERT'),
              ('organizations','organizations','status','INSERT'),
              ('organizations','organizations','self_service_creator_user_id','INSERT'),
              ('organizations','organizations','created_at','INSERT'),
              ('organizations','organizations','status','UPDATE'),
              ('organizations','organization_memberships','user_id','SELECT'),
              ('organizations','organization_memberships','organization_id','SELECT'),
              ('organizations','organization_memberships','role','SELECT'),
              ('organizations','organization_memberships','status','SELECT'),
              ('organizations','organization_memberships','is_default','SELECT'),
              ('organizations','organization_memberships','id','INSERT'),
              ('organizations','organization_memberships','user_id','INSERT'),
              ('organizations','organization_memberships','organization_id','INSERT'),
              ('organizations','organization_memberships','role','INSERT'),
              ('organizations','organization_memberships','status','INSERT'),
              ('organizations','organization_memberships','is_default','INSERT'),
              ('organizations','organization_memberships','granted_at','INSERT'),
              ('organizations','organization_memberships','is_default','UPDATE'),
              ('platform','audit_logs','id','INSERT'),
              ('platform','audit_logs','org_id','INSERT'),
              ('platform','audit_logs','actor_id','INSERT'),
              ('platform','audit_logs','action','INSERT'),
              ('platform','audit_logs','entity_type','INSERT'),
              ('platform','audit_logs','entity_id','INSERT'),
              ('platform','audit_logs','request_id','INSERT'),
              ('platform','audit_logs','payload_redacted','INSERT'),
              ('platform','audit_logs','occurred_at','INSERT')),
            -- REG-002: AI-06/AI-18 or the REG-002 lane add organizations.pending_memberships and its grants.
            expected_reg002(table_schema,table_name,column_name,privilege_type) AS (VALUES
              ('organizations','pending_memberships','id','SELECT'),
              ('organizations','pending_memberships','organization_id','SELECT'),
              ('organizations','pending_memberships','email_hmac','SELECT'),
              ('organizations','pending_memberships','email_hmac_key_version','SELECT'),
              ('organizations','pending_memberships','role','SELECT'),
              ('organizations','pending_memberships','status','SELECT'),
              ('organizations','pending_memberships','created_at','SELECT'),
              ('organizations','pending_memberships','expires_at','SELECT'),
              ('organizations','pending_memberships','id','INSERT'),
              ('organizations','pending_memberships','organization_id','INSERT'),
              ('organizations','pending_memberships','email_hmac','INSERT'),
              ('organizations','pending_memberships','email_hmac_key_version','INSERT'),
              ('organizations','pending_memberships','role','INSERT'),
              ('organizations','pending_memberships','status','INSERT'),
              ('organizations','pending_memberships','invited_by','INSERT'),
              ('organizations','pending_memberships','created_at','INSERT'),
              ('organizations','pending_memberships','expires_at','INSERT'),
              ('organizations','pending_memberships','status','UPDATE'),
              ('organizations','pending_memberships','expires_at','UPDATE'),
              ('organizations','pending_memberships','accepted_user_id','UPDATE'),
              ('organizations','pending_memberships','accepted_at','UPDATE'),
              ('organizations','pending_memberships','revoked_at','UPDATE')),
            expected AS (
              SELECT * FROM expected_reg001
              UNION ALL
              SELECT * FROM expected_reg002
              WHERE pg_catalog.to_regclass('organizations.pending_memberships') IS NOT NULL),
            actual AS (
              SELECT table_schema,table_name,column_name,privilege_type
              FROM information_schema.column_privileges
              WHERE grantee='paqueteria_registration_executor'),
            actual_tables AS (
              SELECT table_schema,table_name,privilege_type
              FROM information_schema.table_privileges
              WHERE grantee='paqueteria_registration_executor'),
            fn AS (
              SELECT signature,search_path
              FROM unnest(@signatures::text[],@search_paths::text[]) f(signature,search_path)),
            installed AS (
              SELECT fn.signature,fn.search_path,p.oid,p.prosecdef,p.proconfig,p.prosrc
              FROM fn JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(fn.signature)),
            fn_reg002 AS (
              SELECT signature,search_path
              FROM unnest(@reg002_signatures::text[],@reg002_search_paths::text[]) f(signature,search_path)),
            installed_reg002 AS (
              SELECT fn_reg002.signature,fn_reg002.search_path,p.oid,p.prosecdef,p.proconfig,p.prosrc
              FROM fn_reg002 JOIN pg_catalog.pg_proc p ON p.oid=pg_catalog.to_regprocedure(fn_reg002.signature)),
            installed_all AS (
              SELECT * FROM installed UNION ALL SELECT * FROM installed_reg002)
            SELECT 'missing registration executor column grant: ' || e.table_schema || '.' || e.table_name || '.' || e.column_name || ':' || e.privilege_type
            FROM expected e CROSS JOIN executor
            LEFT JOIN actual a USING(table_schema,table_name,column_name,privilege_type)
            WHERE a.column_name IS NULL
            UNION ALL
            SELECT 'unexpected registration executor column grant: ' || a.table_schema || '.' || a.table_name || '.' || a.column_name || ':' || a.privilege_type
            FROM actual a LEFT JOIN expected e USING(table_schema,table_name,column_name,privilege_type)
            WHERE e.column_name IS NULL
            UNION ALL
            SELECT 'unexpected registration executor table grant: ' || a.table_schema || '.' || a.table_name || ':' || a.privilege_type
            FROM actual_tables a
            UNION ALL
            SELECT 'registration executor schema privilege differs: ' || n.nspname
            FROM pg_catalog.pg_namespace n CROSS JOIN executor
            WHERE n.nspname=ANY(@schemas::text[])
              AND (has_schema_privilege(executor.oid,n.oid,'CREATE')
                OR has_schema_privilege(executor.oid,n.oid,'USAGE') IS DISTINCT FROM
                   (n.nspname IN ('identity','organizations','platform')))
            UNION ALL
            SELECT 'registration executor inherits role ' || pg_catalog.pg_get_userbyid(m.roleid)
            FROM pg_catalog.pg_auth_members m JOIN executor ON m.member=executor.oid
            UNION ALL
            SELECT 'registration executor owns a relation, schema or type'
            FROM executor
            WHERE EXISTS (SELECT 1 FROM pg_catalog.pg_class WHERE relowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspowner=executor.oid)
               OR EXISTS (SELECT 1 FROM pg_catalog.pg_type WHERE typowner=executor.oid)
            UNION ALL
            SELECT 'registration function is unsafe: ' || installed.signature
            FROM installed_all installed
            WHERE NOT installed.prosecdef
               OR NOT (installed.search_path=ANY(COALESCE(installed.proconfig,ARRAY[]::text[])))
               OR installed.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
               OR has_function_privilege('public',installed.oid,'EXECUTE')
               OR has_function_privilege('paqueteria_worker',installed.oid,'EXECUTE')
               OR NOT has_function_privilege('paqueteria_app',installed.oid,'EXECUTE')
            UNION ALL
            SELECT 'registration function exists without the registration executor role: ' || installed.signature
            FROM installed_all installed WHERE NOT EXISTS (SELECT 1 FROM executor)
            UNION ALL
            SELECT 'registration functions are only partially installed'
            FROM (SELECT count(*) AS present FROM installed) c
            WHERE c.present BETWEEN 1 AND 4
            UNION ALL
            SELECT 'pending membership functions are only partially installed'
            FROM (SELECT count(*) AS present FROM installed_reg002) c
            WHERE c.present BETWEEN 1 AND 3
            UNION ALL
            SELECT 'pending membership functions exist without the REG-001 functions'
            WHERE EXISTS (SELECT 1 FROM installed_reg002) AND NOT EXISTS (SELECT 1 FROM installed)
            UNION ALL
            SELECT 'pending membership functions exist without organizations.pending_memberships'
            WHERE EXISTS (SELECT 1 FROM installed_reg002)
              AND pg_catalog.to_regclass('organizations.pending_memberships') IS NULL
            UNION ALL
            -- REG-002: runtime roles never write pending memberships directly.
            SELECT 'organizations.pending_memberships runtime privileges differ from REG-002'
            WHERE pg_catalog.to_regclass('organizations.pending_memberships') IS NOT NULL
              AND (NOT has_table_privilege('paqueteria_app','organizations.pending_memberships','SELECT')
                OR has_any_column_privilege('paqueteria_app','organizations.pending_memberships','INSERT')
                OR has_any_column_privilege('paqueteria_app','organizations.pending_memberships','UPDATE')
                OR has_table_privilege('paqueteria_app','organizations.pending_memberships','DELETE')
                OR has_any_column_privilege('paqueteria_worker','organizations.pending_memberships','SELECT')
                OR has_any_column_privilege('paqueteria_worker','organizations.pending_memberships','INSERT')
                OR has_any_column_privilege('paqueteria_worker','organizations.pending_memberships','UPDATE')
                OR has_table_privilege('paqueteria_worker','organizations.pending_memberships','DELETE')
                OR NOT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_class
                  WHERE oid='organizations.pending_memberships'::regclass AND relrowsecurity AND relforcerowsecurity))
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray()),
            new NpgsqlParameter<string[]>("signatures", RegistrationFunctions.Select(item => item.Signature).ToArray()),
            new NpgsqlParameter<string[]>("search_paths", RegistrationFunctions.Select(item => item.SearchPath).ToArray()),
            new NpgsqlParameter<string[]>("reg002_signatures", PendingMembershipFunctions.Select(item => item.Signature).ToArray()),
            new NpgsqlParameter<string[]>("reg002_search_paths", PendingMembershipFunctions.Select(item => item.SearchPath).ToArray()))
            .ConfigureAwait(false);
    }

    /// <summary>REG-002: installed by the Organizations lane after REG-001, with the same owner and rule.</summary>
    private static readonly (string Signature, string SearchPath)[] PendingMembershipFunctions =
    [
        ("security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text)",
            "search_path=pg_catalog, identity, organizations, platform, pg_temp"),
        ("security.renew_pending_membership(uuid,uuid,uuid,text,text)",
            "search_path=pg_catalog, identity, organizations, platform, pg_temp"),
        ("security.revoke_pending_membership(uuid,uuid,uuid,text,text)",
            "search_path=pg_catalog, identity, organizations, platform, pg_temp"),
        ("security.apply_pending_memberships(text,bytea[],integer[])",
            "search_path=pg_catalog, identity, organizations, platform, pg_temp"),
    ];

    private static async Task AssertDefaultAclCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        await AddRowsAsync(
            violations,
            connection,
            transaction,
            """
            WITH expected AS (
              SELECT schema_name,role_name,object_type,privilege
              FROM unnest(@schemas::text[]) schema_name
              CROSS JOIN unnest(@roles::text[]) role_name
              CROSS JOIN (VALUES
                ('r'::"char",'SELECT'),('r'::"char",'INSERT'),('r'::"char",'UPDATE'),('r'::"char",'DELETE'),
                ('S'::"char",'USAGE'),('S'::"char",'SELECT')) rights(object_type,privilege)
            )
            SELECT 'missing default privilege ' || schema_name || ':' || role_name || ':' || object_type::text || ':' || privilege
            FROM expected
            WHERE NOT EXISTS (
              SELECT 1
              FROM pg_catalog.pg_default_acl d
              JOIN pg_catalog.pg_namespace n ON n.oid=d.defaclnamespace
              JOIN pg_catalog.pg_roles owner ON owner.oid=d.defaclrole
              CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) acl
              JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
              WHERE n.nspname=expected.schema_name AND owner.rolname='paqueteria_migrator'
                AND d.defaclobjtype=expected.object_type AND grantee.rolname=expected.role_name
                AND acl.privilege_type=expected.privilege)
            ORDER BY 1
            """,
            cancellationToken,
            new NpgsqlParameter<string[]>("schemas", DatabaseSchemaCatalog.Modules.Select(item => item.Schema).ToArray()),
            new NpgsqlParameter<string[]>("roles", RuntimeRoles)).ConfigureAwait(false);
    }

    private static async Task AssertOutboxPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        foreach (var lane in new[] { "platform.outbox_events", "platform.location_outbox_events" })
        {
            foreach (var role in RuntimeRoles)
            {
                if (!await HasTablePrivilegeAsync(connection, transaction, role, lane, "INSERT", cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"{role} is missing INSERT on {lane}.");
                }

                foreach (var privilege in new[] { "SELECT", "UPDATE", "DELETE" })
                {
                    if (await HasTablePrivilegeAsync(connection, transaction, role, lane, privilege, cancellationToken).ConfigureAwait(false))
                    {
                        violations.Add($"{role} has forbidden {privilege} on {lane}.");
                    }
                }
            }

            foreach (var privilege in new[] { "SELECT", "UPDATE" })
            {
                if (!await HasTablePrivilegeAsync(connection, transaction, "paqueteria_outbox_executor", lane, privilege, cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"paqueteria_outbox_executor is missing {privilege} on {lane}.");
                }
            }

            if (await HasTablePrivilegeAsync(connection, transaction, "paqueteria_outbox_executor", lane, "DELETE", cancellationToken).ConfigureAwait(false))
            {
                violations.Add($"paqueteria_outbox_executor has forbidden DELETE on {lane}.");
            }

            foreach (var privilege in new[] { "SELECT", "DELETE" })
            {
                if (!await HasTablePrivilegeAsync(connection, transaction, "paqueteria_maintenance", lane, privilege, cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"paqueteria_maintenance is missing {privilege} on {lane}.");
                }
            }

            if (await HasTablePrivilegeAsync(connection, transaction, "paqueteria_maintenance", lane, "UPDATE", cancellationToken).ConfigureAwait(false))
            {
                violations.Add($"paqueteria_maintenance has forbidden UPDATE on {lane}.");
            }
        }

        var ntfInstalled = await ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT to_regprocedure('security.resolve_outbox_consumer(text)') IS NOT NULL",
            cancellationToken).ConfigureAwait(false);
        var workerFunctions = ntfInstalled
            ? new[]
            {
                "security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)",
                "security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
                "security.claim_location_outbox(text,integer,interval)",
                "security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)",
                "security.requeue_stale_location_outbox(interval,integer,integer)",
                "security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)",
                "security.resolve_outbox_consumer(text)",
                "security.claim_realtime_outbox(text,integer,interval)",
                "security.claim_notifications_outbox(text,integer,interval)",
                "security.claim_unowned_outbox(text,integer,interval)",
                "security.requeue_stale_realtime_outbox(interval,integer,integer)",
                "security.recover_stale_notifications_outbox(text,integer,integer,interval)",
                "security.requeue_stale_unowned_outbox(interval,integer,integer)",
                "security.read_owner_dispatcher_ids(uuid,integer)",
                "security.read_active_notification_users(uuid[])",
                "security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamp with time zone,uuid[])",
                "security.read_notification_delivery(uuid,uuid)",
                "security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)",
                "security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamp with time zone)",
            }
            : SensitiveFunctions.Skip(3).ToArray();
        var dispatchLaneInstalled = await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT to_regprocedure('security.claim_dispatch_outbox(text,integer,interval)') IS NOT NULL
                OR to_regprocedure('security.requeue_stale_dispatch_outbox(interval,integer,integer)') IS NOT NULL
            """,
            cancellationToken).ConfigureAwait(false);
        var recipientLaneInstalled = ntfInstalled && await ScalarAsync<bool>(
            connection,
            transaction,
            """
            SELECT to_regprocedure('security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamp with time zone,timestamp with time zone)') IS NOT NULL
                OR to_regprocedure('security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time without time zone,time without time zone,text,text[])') IS NOT NULL
                OR to_regprocedure('security.cancel_notification(uuid,uuid,uuid,integer,text,timestamp with time zone)') IS NOT NULL
                OR to_regprocedure('security.is_recipient_opted_out(text,bytea[])') IS NOT NULL
            """,
            cancellationToken).ConfigureAwait(false);
        if (recipientLaneInstalled)
        {
            // NTF-RECIPIENT-NOTICES-2026-10-20: v2 replaces the published settle; EXECUTE for paqueteria_worker only.
            workerFunctions =
            [
                .. workerFunctions.Where(signature => signature !=
                    "security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)"),
                "security.expand_recipient_notice_request(uuid,uuid,boolean,interval,time without time zone,time without time zone,text,text[])",
                "security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,text,timestamp with time zone,timestamp with time zone)",
                "security.cancel_notification(uuid,uuid,uuid,integer,text,timestamp with time zone)",
                "security.is_recipient_opted_out(text,bytea[])",
            ];
        }

        if (ntfInstalled && await ScalarAsync<bool>(
                connection,
                transaction,
                "SELECT to_regprocedure('security.read_recipient_notice_counts(uuid,uuid)') IS NOT NULL",
                cancellationToken).ConfigureAwait(false))
        {
            // NTF-RECIPIENT-NOTICE-CAPS-2026-10-23: the count of the caps; EXECUTE for paqueteria_worker only.
            workerFunctions = [.. workerFunctions, "security.read_recipient_notice_counts(uuid,uuid)"];
        }

        var receiptsInstalled = ntfInstalled && await ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT to_regprocedure('security.record_messaging_receipt(text,text,text,text,timestamp with time zone)') IS NOT NULL",
            cancellationToken).ConfigureAwait(false);
        if (receiptsInstalled)
        {
            // NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25: the automatic-reply claim and settle; EXECUTE for paqueteria_worker
            // only. The receipt, opt-out and request writers are the reverse: EXECUTE for paqueteria_app only.
            workerFunctions = [.. workerFunctions, .. ReceiptExecutorWorkerFunctions];
            foreach (var signature in ReceiptExecutorApiFunctions)
            {
                if (!await HasFunctionPrivilegeAsync(connection, transaction, "paqueteria_app", signature, cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"paqueteria_app is missing EXECUTE on {signature}.");
                }

                if (await HasFunctionPrivilegeAsync(connection, transaction, "paqueteria_worker", signature, cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"paqueteria_worker has forbidden EXECUTE on {signature}.");
                }
            }
        }

        if (dispatchLaneInstalled)
        {
            // D8-OUTBOX-LANE-DISPATCH: EXECUTE for paqueteria_worker only.
            workerFunctions =
            [
                .. workerFunctions,
                "security.claim_dispatch_outbox(text,integer,interval)",
                "security.requeue_stale_dispatch_outbox(interval,integer,integer)",
            ];
        }

        foreach (var signature in workerFunctions)
        {
            if (!await HasFunctionPrivilegeAsync(connection, transaction, "paqueteria_worker", signature, cancellationToken).ConfigureAwait(false))
            {
                violations.Add($"paqueteria_worker is missing EXECUTE on {signature}.");
            }

            if (await HasFunctionPrivilegeAsync(connection, transaction, "paqueteria_app", signature, cancellationToken).ConfigureAwait(false))
            {
                violations.Add($"paqueteria_app has forbidden EXECUTE on {signature}.");
            }
        }

        if (ntfInstalled)
        {
            foreach (var obsolete in new[]
            {
                "security.claim_outbox(text,integer,interval)",
                "security.requeue_stale_outbox(interval,integer,integer)",
            })
            {
                if (await HasFunctionPrivilegeAsync(connection, transaction, "paqueteria_worker", obsolete, cancellationToken).ConfigureAwait(false))
                {
                    violations.Add($"paqueteria_worker retains forbidden EXECUTE on {obsolete} after NTF-001 cutover.");
                }
            }
        }
    }

    private static async Task ProbeDefaultPrivilegesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ICollection<string> violations,
        CancellationToken cancellationToken)
    {
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var table = $"dba001_privilege_probe_{suffix}";
        const string savepoint = "dba001_default_privileges";
        await ExecuteAsync(connection, transaction, $"SAVEPOINT {savepoint}", cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_migrator", cancellationToken).ConfigureAwait(false);
            foreach (var schema in DatabaseSchemaCatalog.Modules.Select(item => item.Schema))
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"CREATE TABLE \"{schema}\".\"{table}\" (id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY)",
                    cancellationToken).ConfigureAwait(false);
            }

            await ExecuteAsync(connection, transaction, "RESET ROLE", cancellationToken).ConfigureAwait(false);
            foreach (var schema in DatabaseSchemaCatalog.Modules.Select(item => item.Schema))
            {
                foreach (var role in RuntimeRoles)
                {
                    foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
                    {
                        if (!await HasTablePrivilegeAsync(connection, transaction, role, $"{schema}.{table}", privilege, cancellationToken).ConfigureAwait(false))
                        {
                            violations.Add($"Default table privilege {privilege} was not inherited by {role} in {schema}.");
                        }
                    }

                    var sequence = $"{schema}.{table}_id_seq";
                    foreach (var privilege in new[] { "USAGE", "SELECT" })
                    {
                        if (!await HasSequencePrivilegeAsync(connection, transaction, role, sequence, privilege, cancellationToken).ConfigureAwait(false))
                        {
                            violations.Add($"Default sequence privilege {privilege} was not inherited by {role} in {schema}.");
                        }
                    }
                }
            }
        }
        finally
        {
            await ExecuteAsync(connection, transaction, $"ROLLBACK TO SAVEPOINT {savepoint}", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"RELEASE SAVEPOINT {savepoint}", cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<bool> HasTablePrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        string table,
        string privilege,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT has_table_privilege(@role,@object,@privilege)",
            cancellationToken,
            new NpgsqlParameter<string>("role", role),
            new NpgsqlParameter<string>("object", table),
            new NpgsqlParameter<string>("privilege", privilege));

    private static Task<bool> HasSequencePrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        string sequence,
        string privilege,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT has_sequence_privilege(@role,@object,@privilege)",
            cancellationToken,
            new NpgsqlParameter<string>("role", role),
            new NpgsqlParameter<string>("object", sequence),
            new NpgsqlParameter<string>("privilege", privilege));

    private static Task<bool> HasFunctionPrivilegeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role,
        string function,
        CancellationToken cancellationToken) =>
        ScalarAsync<bool>(
            connection,
            transaction,
            "SELECT has_function_privilege(@role,@object,'EXECUTE')",
            cancellationToken,
            new NpgsqlParameter<string>("role", role),
            new NpgsqlParameter<string>("object", function));

    private static async Task AddRowsAsync(
        ICollection<string> destination,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            destination.Add(reader.GetString(0));
        }
    }

    private static async Task<IReadOnlyList<string>> QuerySingleRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Database version query returned no row.");
        }

        return Array.AsReadOnly(new[] { reader.GetString(0), reader.GetString(1) });
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is T typed ? typed : (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
