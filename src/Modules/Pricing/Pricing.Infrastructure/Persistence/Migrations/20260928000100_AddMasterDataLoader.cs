using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pricing.Infrastructure.Persistence.Migrations;

/// <summary>
/// MDM-001 (owner decision MDM-001-OPERATOR-LOADER) installs the only write path for pilot master data:
/// cities, service areas, operating zones, tariff rules and driver profiles with their service areas.
/// <list type="bullet">
/// <item><c>paqueteria_master_data_executor NOLOGIN BYPASSRLS</c> owns the function and holds exact
/// column grants on the six master-data tables, read-only lookups, the GATE-007 deployment marker and the
/// audit insert;</item>
/// <item><c>paqueteria_master_data_loader NOLOGIN NOBYPASSRLS</c> is the operator grantee: USAGE on schema
/// <c>security</c> and EXECUTE on the function, nothing else. It is a platform-operator capability, not a
/// tenant boundary: named operator LOGINs the owner controls are its only members, never
/// <c>paqueteria_app</c> or <c>paqueteria_worker</c>;</item>
/// <item><c>security.load_master_data(uuid,uuid,json,bytea,boolean)</c>: enforces the GATE-007 deployment
/// marker, validates the whole reviewed document first (exact keys without duplicates, enums, integer cents
/// from the raw token, geometry validity, range, size and containment, references to ACTIVE cities, natural
/// keys, non-overlapping ACTIVE tariff windows) and only then writes, idempotently by natural key, inside
/// the caller's transaction, with one append-only audit row per load that names the operator by a
/// pseudonymous reference (<c>operator_ref</c>). A dry
/// run validates and diffs without writing anything.</item>
/// </list>
/// The lane lives in Pricing because Pricing runs after the Locations and Drivers lanes, so every table it
/// touches has been adopted. AI-06 carries the deployment marker and AI-18 the roles and grants on fresh
/// installations; this lane adopts them there and creates them on populated installations, refusing any
/// shape that differs. Runtime roles gain nothing. Nothing is dropped and no existing row is rewritten by
/// the migration itself.
/// </summary>
[DbContext(typeof(PricingDbContext))]
[Migration(MigrationId)]
public sealed class AddMasterDataLoader : Migration
{
    public const string MigrationId = "20260928000100_AddMasterDataLoader";
    public const string ExecutorRole = "paqueteria_master_data_executor";
    public const string LoaderRole = "paqueteria_master_data_loader";
    public const string FunctionSignature = "security.load_master_data(uuid,uuid,json,bytea,boolean)";
    public const string GateTable = "platform.master_data_deployment_gate";
    public const string LegacyFunctionSignature = "security.load_master_data(uuid,uuid,jsonb,bytea,boolean)";
    public const string OperatorReferencePrefix = "paquetenvia.mdm-001.operator:";
    public const string SearchPath = "search_path=pg_catalog, pg_temp";
    public const string DocumentFormat = "paquetenvia.master-data.v1";
    public const int MaximumSectionEntries = 5000;
    public const int MaximumTotalEntries = 20000;
    public const int MaximumVertices = 200000;
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;

    /// <summary>MDM-001 M2: the IANA zones a new Mexican city may use (links and retired zones excluded).</summary>
    public static IReadOnlyList<string> MexicoTimeZones { get; } = Array.AsReadOnly(new[]
    {
        "America/Bahia_Banderas", "America/Cancun", "America/Chihuahua", "America/Ciudad_Juarez",
        "America/Hermosillo", "America/Matamoros", "America/Mazatlan", "America/Merida",
        "America/Mexico_City", "America/Monterrey", "America/Ojinaga", "America/Tijuana",
    });

    /// <summary>The executor's exact column grants (schema.table.column:privilege).</summary>
    public static IReadOnlyList<string> ExecutorColumnGrants { get; } = Array.AsReadOnly(new[]
    {
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
    });

    public const string UpSql =
        """
        DO $adoption$
        BEGIN
          IF to_regclass('locations.cities') IS NULL
             OR to_regclass('locations.service_areas') IS NULL
             OR to_regclass('locations.operating_zones') IS NULL
             OR to_regclass('pricing.tariff_rules') IS NULL
             OR to_regclass('drivers.driver_profiles') IS NULL
             OR to_regclass('drivers.driver_service_areas') IS NULL
             OR to_regclass('platform.audit_logs') IS NULL THEN
            RAISE EXCEPTION 'MDM-001 requires the canonical AI-06 master-data and audit tables';
          END IF;

          -- The natural keys the loader relies on for idempotency.
          IF NOT EXISTS (
               SELECT 1 FROM pg_constraint
               WHERE conrelid='locations.cities'::regclass AND contype='u'
                 AND pg_get_constraintdef(oid)='UNIQUE (country_code, state_code, name)')
             OR NOT EXISTS (
               SELECT 1 FROM pg_constraint
               WHERE conrelid='locations.service_areas'::regclass AND contype='u'
                 AND pg_get_constraintdef(oid)='UNIQUE (owner_org_id, city_id, name)')
             OR NOT EXISTS (
               SELECT 1 FROM pg_constraint
               WHERE conrelid='locations.operating_zones'::regclass AND contype='u'
                 AND pg_get_constraintdef(oid)='UNIQUE (owner_org_id, service_area_id, name)')
             OR NOT EXISTS (
               SELECT 1 FROM pg_constraint
               WHERE conrelid='drivers.driver_profiles'::regclass AND contype='u'
                 AND pg_get_constraintdef(oid)='UNIQUE (user_id)')
             OR NOT EXISTS (
               SELECT 1 FROM pg_constraint
               WHERE conrelid='drivers.driver_service_areas'::regclass AND contype='p'
                 AND pg_get_constraintdef(oid)='PRIMARY KEY (driver_id, service_area_id)') THEN
            RAISE EXCEPTION 'MDM-001 requires the canonical AI-06 natural keys of the master-data tables';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='pricing' AND table_name='tariff_rules'
              AND column_name='amount_cents' AND data_type='bigint' AND is_nullable='NO') THEN
            RAISE EXCEPTION 'MDM-001 requires pricing.tariff_rules.amount_cents as bigint cents';
          END IF;

          IF (SELECT format_type(atttypid, atttypmod) FROM pg_attribute
              WHERE attrelid='locations.service_areas'::regclass AND attname='polygon') IS DISTINCT FROM 'geometry(MultiPolygon,4326)'
             OR (SELECT format_type(atttypid, atttypmod) FROM pg_attribute
              WHERE attrelid='locations.operating_zones'::regclass AND attname='polygon') IS DISTINCT FROM 'geometry(MultiPolygon,4326)' THEN
            RAISE EXCEPTION 'MDM-001 requires geometry(MultiPolygon,4326) service-area and zone polygons';
          END IF;

          -- The loader writes only the columns granted below; a user trigger could widen that.
          IF EXISTS (
            SELECT 1 FROM pg_trigger
            WHERE tgrelid IN (
                'locations.cities'::regclass, 'locations.service_areas'::regclass,
                'locations.operating_zones'::regclass, 'pricing.tariff_rules'::regclass,
                'drivers.driver_profiles'::regclass, 'drivers.driver_service_areas'::regclass)
              AND NOT tgisinternal
          ) THEN
            RAISE EXCEPTION 'MDM-001 refuses master-data tables with user triggers';
          END IF;

          -- GATE-007 deployment marker (AI-06). Installations whose baseline predates it get it here, created
          -- as paqueteria_migrator (the lane runs under that role); an existing one must have the exact shape.
          IF to_regclass('platform.master_data_deployment_gate') IS NULL THEN
            CREATE TABLE platform.master_data_deployment_gate (
              singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
              deployment_class text NOT NULL CHECK (deployment_class IN ('SYNTHETIC','REAL')),
              gate_007_closed boolean NOT NULL DEFAULT false,
              updated_at timestamptz NOT NULL DEFAULT now(),
              updated_by text NOT NULL DEFAULT session_user,
              CONSTRAINT master_data_deployment_gate_real_only_ck CHECK (deployment_class = 'REAL' OR NOT gate_007_closed)
            );
          END IF;
          -- Like every application table except the global cities, the marker forces RLS. Its only policy is
          -- for paqueteria_migrator, its owner and only writer; the executor reads it through BYPASSRLS and the
          -- runtime roles hold no grant at all. An existing marker without them is brought to that shape.
          ALTER TABLE platform.master_data_deployment_gate ENABLE ROW LEVEL SECURITY;
          ALTER TABLE platform.master_data_deployment_gate FORCE ROW LEVEL SECURITY;
          IF NOT EXISTS (SELECT 1 FROM pg_policy WHERE polrelid='platform.master_data_deployment_gate'::regclass) THEN
            CREATE POLICY master_data_deployment_gate_migrator ON platform.master_data_deployment_gate
              TO paqueteria_migrator USING (true) WITH CHECK (true);
          END IF;
          IF (SELECT string_agg(a.attname || ':' || format_type(a.atttypid, a.atttypmod) || ':' || a.attnotnull::text, ','
                ORDER BY a.attnum)
              FROM pg_attribute a
              WHERE a.attrelid='platform.master_data_deployment_gate'::regclass AND a.attnum > 0 AND NOT a.attisdropped)
             IS DISTINCT FROM 'singleton:boolean:true,deployment_class:text:true,gate_007_closed:boolean:true,updated_at:timestamp with time zone:true,updated_by:text:true'
             OR NOT EXISTS (
               SELECT 1 FROM pg_constraint
               WHERE conrelid='platform.master_data_deployment_gate'::regclass AND contype='p'
                 AND pg_get_constraintdef(oid)='PRIMARY KEY (singleton)')
             OR (SELECT count(*) FROM pg_constraint
                 WHERE conrelid='platform.master_data_deployment_gate'::regclass AND contype='c') <> 3
             OR EXISTS (
               SELECT 1 FROM pg_trigger
               WHERE tgrelid='platform.master_data_deployment_gate'::regclass AND NOT tgisinternal)
             OR (SELECT string_agg(pol.polname || ':' || pol.polcmd::text || ':' || pol.polpermissive::text || ':'
                   || (SELECT string_agg(CASE WHEN r = 0 THEN 'public' ELSE pg_get_userbyid(r) END, '+') FROM unnest(pol.polroles) r)
                   || ':' || COALESCE(pg_get_expr(pol.polqual, pol.polrelid), '') || ':'
                   || COALESCE(pg_get_expr(pol.polwithcheck, pol.polrelid), ''), ',')
                 FROM pg_policy pol WHERE pol.polrelid='platform.master_data_deployment_gate'::regclass)
                IS DISTINCT FROM 'master_data_deployment_gate_migrator:*:true:paqueteria_migrator:true:true'
             OR pg_get_userbyid((SELECT relowner FROM pg_class WHERE oid='platform.master_data_deployment_gate'::regclass))
                <> 'paqueteria_migrator' THEN
            RAISE EXCEPTION 'MDM-001 requires the canonical AI-06 platform.master_data_deployment_gate';
          END IF;
        END
        $adoption$;

        RESET ROLE;

        -- MDM-001 N1: an installation that ran the first published version of this lane has the ungated
        -- jsonb overload; it must never stay callable next to the gated json function.
        DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,jsonb,bytea,boolean);

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_master_data_executor NOLOGIN BYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_master_data_loader NOLOGIN NOBYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $existing_role$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_master_data_executor'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR NOT rolbypassrls OR NOT rolinherit)
          ) OR EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_master_data_loader'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR rolbypassrls OR NOT rolinherit)
          ) THEN
            RAISE EXCEPTION 'MDM-001 roles exist with attributes outside the MDM-001 contract';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.member IN ('paqueteria_master_data_executor'::regrole, 'paqueteria_master_data_loader'::regrole)
          ) THEN
            RAISE EXCEPTION 'MDM-001 roles must not inherit any other role';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.roleid IN ('paqueteria_master_data_executor'::regrole, 'paqueteria_master_data_loader'::regrole)
              AND m.member IN (
                SELECT oid FROM pg_roles
                WHERE rolname IN ('paqueteria_app','paqueteria_worker','paqueteria_bootstrap'))
          ) THEN
            RAISE EXCEPTION 'MDM-001 roles must never be granted to a runtime or bootstrap role';
          END IF;
          -- Only deployment principals (members of paqueteria_migrator) may hold the executor.
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.roleid='paqueteria_master_data_executor'::regrole
              AND NOT pg_has_role(m.member, 'paqueteria_migrator', 'MEMBER')
          ) THEN
            RAISE EXCEPTION 'MDM-001 executor has a member that is not a deployment principal';
          END IF;
          -- MDM-001 N2: an operator login is never also a deployment principal. Only a membership that can be
          -- used counts (INHERIT or SET): a CREATEROLE creator's automatic ADMIN-only grant cannot load.
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.roleid='paqueteria_master_data_loader'::regrole
              AND (m.inherit_option OR m.set_option)
              AND pg_has_role(m.member, 'paqueteria_migrator', 'MEMBER')
          ) THEN
            RAISE EXCEPTION 'MDM-001 loader has a member that is also a deployment principal';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_class WHERE relowner IN ('paqueteria_master_data_executor'::regrole, 'paqueteria_master_data_loader'::regrole))
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner IN ('paqueteria_master_data_executor'::regrole, 'paqueteria_master_data_loader'::regrole))
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner IN ('paqueteria_master_data_executor'::regrole, 'paqueteria_master_data_loader'::regrole))
             OR EXISTS (SELECT 1 FROM pg_proc WHERE proowner='paqueteria_master_data_loader'::regrole)
             OR EXISTS (
               SELECT 1 FROM pg_proc
               WHERE proowner='paqueteria_master_data_executor'::regrole
                 AND oid IS DISTINCT FROM to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)')
             ) THEN
            RAISE EXCEPTION 'MDM-001 roles already own objects outside the MDM-001 contract';
          END IF;
        END
        $existing_role$;

        GRANT USAGE ON SCHEMA identity,organizations,locations,pricing,drivers,platform TO paqueteria_master_data_executor;
        GRANT SELECT (id,status) ON identity.users TO paqueteria_master_data_executor;
        GRANT SELECT (id,organization_type,status) ON organizations.organizations TO paqueteria_master_data_executor;
        GRANT SELECT (user_id,organization_id,role,status) ON organizations.organization_memberships TO paqueteria_master_data_executor;
        GRANT SELECT (id,country_code,state_code,name,timezone,status) ON locations.cities TO paqueteria_master_data_executor;
        GRANT INSERT (id,country_code,state_code,name,timezone,status) ON locations.cities TO paqueteria_master_data_executor;
        GRANT SELECT (id,owner_org_id,city_id,name,polygon,status) ON locations.service_areas TO paqueteria_master_data_executor;
        GRANT INSERT (id,owner_org_id,city_id,name,polygon,status) ON locations.service_areas TO paqueteria_master_data_executor;
        GRANT UPDATE (polygon,status) ON locations.service_areas TO paqueteria_master_data_executor;
        GRANT SELECT (id,owner_org_id,service_area_id,name,zone_type,polygon,status) ON locations.operating_zones TO paqueteria_master_data_executor;
        GRANT INSERT (id,owner_org_id,service_area_id,name,zone_type,polygon,status) ON locations.operating_zones TO paqueteria_master_data_executor;
        GRANT UPDATE (zone_type,polygon,status) ON locations.operating_zones TO paqueteria_master_data_executor;
        GRANT SELECT (id,owner_org_id,city_id,service_area_id,operating_zone_id,pricing_tier,service_type,amount_cents,tax_mode,active_from,active_to,status) ON pricing.tariff_rules TO paqueteria_master_data_executor;
        GRANT INSERT (id,owner_org_id,city_id,service_area_id,operating_zone_id,pricing_tier,service_type,amount_cents,tax_mode,active_from,active_to,status) ON pricing.tariff_rules TO paqueteria_master_data_executor;
        GRANT UPDATE (active_to,status) ON pricing.tariff_rules TO paqueteria_master_data_executor;
        GRANT SELECT (id,user_id,org_id,home_city_id,driver_type,vehicle_type,status) ON drivers.driver_profiles TO paqueteria_master_data_executor;
        GRANT INSERT (id,user_id,org_id,home_city_id,driver_type,vehicle_type,status) ON drivers.driver_profiles TO paqueteria_master_data_executor;
        GRANT UPDATE (home_city_id,driver_type,vehicle_type,status) ON drivers.driver_profiles TO paqueteria_master_data_executor;
        GRANT SELECT (driver_id,service_area_id,org_id,status) ON drivers.driver_service_areas TO paqueteria_master_data_executor;
        GRANT INSERT (driver_id,service_area_id,org_id,status) ON drivers.driver_service_areas TO paqueteria_master_data_executor;
        GRANT UPDATE (status) ON drivers.driver_service_areas TO paqueteria_master_data_executor;
        GRANT SELECT (deployment_class,gate_007_closed) ON platform.master_data_deployment_gate TO paqueteria_master_data_executor;
        GRANT INSERT (id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at) ON platform.audit_logs TO paqueteria_master_data_executor;
        REVOKE ALL ON platform.master_data_deployment_gate FROM PUBLIC, paqueteria_app, paqueteria_worker;
        GRANT USAGE ON SCHEMA security TO paqueteria_master_data_loader;

        -- MDM-001-OPERATOR-LOADER: one call loads one reviewed document for one organization. The loader role
        -- is a platform-operator capability, not a tenant boundary: the operator names the organization and
        -- also sets it as tenant context (set_config after BEGIN), so a mistyped organization fails instead of
        -- landing elsewhere, and the audit row names the operator login. GATE-007 is enforced here from the
        -- deployment marker, whatever the operator's machine says. Validation of the whole document precedes
        -- every write; any failure raises and the caller's single transaction leaves nothing behind. Natural
        -- keys make a re-run of the same document change nothing but its audit row. Only reference indexes
        -- (section[n]) and field names leave the function: no name, identifier or coordinate is echoed.
        CREATE OR REPLACE FUNCTION security.load_master_data(
          p_organization_id uuid,
          p_load_id uuid,
          p_document json,
          p_document_sha256 bytea,
          p_dry_run boolean)
        RETURNS jsonb
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, pg_temp
        AS $function$
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_doc jsonb;
          v_context uuid[];
          v_org_status text;
          v_org_type text;
          v_class text;
          v_gate boolean;
          v_item jsonb;
          v_sub jsonb;
          v_ordinal bigint;
          v_sub_ordinal bigint;
          v_ref text;
          v_key text;
          v_keys text[];
          v_total bigint;
          v_vertices bigint := 0;
          v_amount_tokens text[];
          v_seen jsonb;
          v_sub_seen jsonb;
          v_city_ids jsonb := '{}'::jsonb;
          v_area_ids jsonb := '{}'::jsonb;
          v_zone_ids jsonb := '{}'::jsonb;
          v_area_geom_index jsonb := '{}'::jsonb;
          v_geoms public.geometry[] := ARRAY[]::public.geometry[];
          v_steps jsonb[] := ARRAY[]::jsonb[];
          v_step jsonb;
          v_fields text[];
          v_action text;
          v_id uuid;
          v_city_id uuid;
          v_area_id uuid;
          v_zone_id uuid;
          v_count integer;
          v_geom public.geometry;
          v_parent_geom public.geometry;
          v_country text;
          v_state text;
          v_name text;
          v_timezone text;
          v_status text;
          v_zone_type text;
          v_tier text;
          v_service_type text;
          v_tax_mode text;
          v_policy_version text;
          v_amount bigint;
          v_active_from timestamptz;
          v_active_to timestamptz;
          v_user_id uuid;
          v_driver_type text;
          v_vehicle_type text;
          v_existing_text1 text;
          v_existing_text2 text;
          v_existing_text3 text;
          v_existing_text4 text;
          v_existing_uuid uuid;
          v_existing_amount bigint;
          v_existing_to timestamptz;
          v_existing_geom public.geometry;
          v_sqlstate text;
          v_counts jsonb;
          v_changes jsonb;
          v_result jsonb;
        BEGIN
          IF p_organization_id IS NULL OR p_load_id IS NULL OR p_document IS NULL OR p_dry_run IS NULL
             OR p_document_sha256 IS NULL OR pg_catalog.octet_length(p_document_sha256) <> 32 THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ARGUMENT_OUT_OF_RANGE';
          END IF;

          -- Tenant scope: the transaction must carry exactly this organization (AI-01 §4.1-§4.2).
          BEGIN
            v_context := NULLIF(pg_catalog.current_setting('app.current_org_ids', true), '')::uuid[];
          EXCEPTION WHEN others THEN
            v_context := NULL;
          END;
          IF v_context IS DISTINCT FROM ARRAY[p_organization_id] THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'MDM001_TENANT_CONTEXT_MISMATCH';
          END IF;

          SELECT o.status, o.organization_type INTO v_org_status, v_org_type
          FROM organizations.organizations o WHERE o.id = p_organization_id;
          IF v_org_status IS DISTINCT FROM 'ACTIVE' THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'MDM001_ORGANIZATION_NOT_ACTIVE';
          END IF;

          -- Input limits (MDM-001 m1), repeated from the job: size first, before any parsing work.
          IF pg_catalog.octet_length(p_document::text) > 16777216 THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DOCUMENT_TOO_LARGE', HINT = 'document';
          END IF;
          BEGIN
            v_doc := p_document::jsonb;
          EXCEPTION WHEN others THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DOCUMENT_SHAPE', HINT = 'document';
          END;
          IF pg_catalog.jsonb_typeof(v_doc) <> 'object' THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DOCUMENT_SHAPE', HINT = 'document';
          END IF;

          -- The json text still carries duplicate keys that jsonb silently collapses; the job refuses them,
          -- and so does PostgreSQL (coordinate arrays hold no objects and are not walked).
          IF EXISTS (
            WITH RECURSIVE node(v) AS (
              SELECT p_document
              UNION ALL
              SELECT c.child
              FROM node,
                LATERAL (
                  SELECT e.value
                  FROM pg_catalog.json_each(
                    CASE WHEN pg_catalog.json_typeof(node.v) = 'object' THEN node.v ELSE '{}'::json END) e
                  WHERE e.key <> 'coordinates'
                  UNION ALL
                  SELECT a.value
                  FROM pg_catalog.json_array_elements(
                    CASE WHEN pg_catalog.json_typeof(node.v) = 'array' THEN node.v ELSE '[]'::json END) a
                ) c(child)
            )
            SELECT 1 FROM node
            WHERE pg_catalog.json_typeof(node.v) = 'object'
              AND (SELECT count(*) FROM pg_catalog.json_object_keys(node.v))
                  <> (SELECT count(DISTINCT k) FROM pg_catalog.json_object_keys(node.v) k)
          ) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DUPLICATE_PROPERTY', HINT = 'document';
          END IF;

          SELECT array_agg(k ORDER BY k COLLATE "C") INTO v_keys FROM pg_catalog.jsonb_object_keys(v_doc) k;
          IF v_keys IS DISTINCT FROM ARRAY['cities','classification','driver_profiles','format',
              'operating_zones','owner_org_id','service_areas','tariff_rules'] THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DOCUMENT_SHAPE', HINT = 'document';
          END IF;
          IF v_doc->>'format' IS DISTINCT FROM 'paquetenvia.master-data.v1'
             OR pg_catalog.jsonb_typeof(v_doc->'classification') IS DISTINCT FROM 'string'
             OR v_doc->>'classification' NOT IN ('SYNTHETIC','REVIEWED')
             OR v_doc->>'owner_org_id' IS DISTINCT FROM p_organization_id::text THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DOCUMENT_HEADER', HINT = 'document';
          END IF;
          FOREACH v_key IN ARRAY ARRAY['cities','service_areas','operating_zones','tariff_rules','driver_profiles'] LOOP
            IF pg_catalog.jsonb_typeof(v_doc->v_key) IS DISTINCT FROM 'array'
               OR pg_catalog.jsonb_array_length(v_doc->v_key) > 5000 THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DOCUMENT_SHAPE', HINT = v_key;
            END IF;
          END LOOP;
          SELECT COALESCE(sum(pg_catalog.jsonb_array_length(e->'service_areas')), 0) INTO v_total
          FROM pg_catalog.jsonb_array_elements(v_doc->'driver_profiles') e
          WHERE pg_catalog.jsonb_typeof(e) = 'object' AND pg_catalog.jsonb_typeof(e->'service_areas') = 'array';
          v_total := v_total
            + pg_catalog.jsonb_array_length(v_doc->'cities') + pg_catalog.jsonb_array_length(v_doc->'service_areas')
            + pg_catalog.jsonb_array_length(v_doc->'operating_zones') + pg_catalog.jsonb_array_length(v_doc->'tariff_rules')
            + pg_catalog.jsonb_array_length(v_doc->'driver_profiles');
          IF v_total > 20000 THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DOCUMENT_TOO_LARGE', HINT = 'document';
          END IF;

          -- GATE-007 (MDM-001 B1): the deployment marker, written only through the migrator, decides what this
          -- database may hold. No row means REAL with GATE-007 open (fail closed).
          SELECT g.deployment_class, g.gate_007_closed INTO v_class, v_gate FROM platform.master_data_deployment_gate g;
          v_class := COALESCE(v_class, 'REAL');
          v_gate := COALESCE(v_gate, false);
          IF (v_class = 'SYNTHETIC') IS DISTINCT FROM (v_doc->>'classification' = 'SYNTHETIC') THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'MDM001_CLASSIFICATION_NOT_ALLOWED', HINT = 'document.classification';
          END IF;
          IF pg_catalog.jsonb_array_length(v_doc->'driver_profiles') > 0 AND v_class <> 'SYNTHETIC' AND NOT v_gate THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'MDM001_DRIVER_PROFILES_REQUIRE_GATE_007', HINT = 'driver_profiles';
          END IF;

          -- MDM-001 M2: global cities are created only by a PLATFORM organization's load.
          IF pg_catalog.jsonb_array_length(v_doc->'cities') > 0 AND v_org_type IS DISTINCT FROM 'PLATFORM' THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'MDM001_CITIES_REQUIRE_PLATFORM_ORGANIZATION', HINT = 'cities';
          END IF;

          -- Global writes (cities, driver profiles) serialize across organizations; every load serializes per
          -- organization. Lock order is always global, then organization. The natural-key lookups below are
          -- race-free under these locks.
          IF pg_catalog.jsonb_array_length(v_doc->'cities') > 0 OR pg_catalog.jsonb_array_length(v_doc->'driver_profiles') > 0 THEN
            PERFORM pg_catalog.pg_advisory_xact_lock(2026092802);
          END IF;
          PERFORM pg_catalog.pg_advisory_xact_lock(2026092801, pg_catalog.hashtext(p_organization_id::text));

          -- Raw amount tokens from the json text: the job and PostgreSQL accept exactly the same spellings.
          SELECT array_agg((e->'amount_cents')::text ORDER BY o) INTO v_amount_tokens
          FROM pg_catalog.json_array_elements(p_document->'tariff_rules') WITH ORDINALITY AS t(e, o);

          ---------------------------------------------------------------------------------------------
          -- Phase 1: validate and resolve the whole document. Nothing is written in this phase.
          ---------------------------------------------------------------------------------------------

          -- Cities (PLATFORM loads only): created ACTIVE when missing, never rewritten.
          FOR v_item, v_ordinal IN SELECT e, o FROM pg_catalog.jsonb_array_elements(v_doc->'cities') WITH ORDINALITY AS t(e, o) LOOP
            v_ref := 'cities[' || v_ordinal || ']';
            IF pg_catalog.jsonb_typeof(v_item) <> 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item) k)
                  IS DISTINCT FROM ARRAY['country_code','name','state_code','timezone'] THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ENTRY_SHAPE', HINT = v_ref;
            END IF;
            v_country := v_item->>'country_code';
            v_state := v_item->>'state_code';
            v_name := v_item->>'name';
            v_timezone := v_item->>'timezone';
            IF v_country IS NULL OR v_country !~ '^[A-Z]{2}$'
               OR v_state IS NULL OR v_state !~ '^[A-Z0-9]{1,10}$'
               OR v_name IS NULL OR pg_catalog.length(v_name) NOT BETWEEN 1 AND 200 OR v_name <> pg_catalog.btrim(v_name)
               OR v_timezone IS NULL THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_INVALID', HINT = v_ref;
            END IF;
            IF v_country <> 'MX' THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_COUNTRY_NOT_SUPPORTED', HINT = v_ref;
            END IF;
            IF v_timezone NOT IN ('America/Bahia_Banderas','America/Cancun','America/Chihuahua','America/Ciudad_Juarez','America/Hermosillo','America/Matamoros','America/Mazatlan','America/Merida','America/Mexico_City','America/Monterrey','America/Ojinaga','America/Tijuana')
               OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_timezone_names tz WHERE tz.name = v_timezone) THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_TIMEZONE_NOT_ALLOWED', HINT = v_ref;
            END IF;
            v_key := v_country || '|' || v_state || '|' || v_name;
            IF v_city_ids ? v_key THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DUPLICATE_NATURAL_KEY', HINT = v_ref;
            END IF;
            v_id := NULL;
            SELECT c.id, c.timezone, c.status INTO v_id, v_existing_text1, v_existing_text2
            FROM locations.cities c
            WHERE c.country_code = v_country AND c.state_code = v_state AND c.name = v_name;
            IF v_id IS NULL THEN
              v_id := pg_catalog.gen_random_uuid();
              v_action := 'CREATE';
            ELSIF v_existing_text1 = v_timezone AND v_existing_text2 = 'ACTIVE' THEN
              v_action := 'UNCHANGED';
            ELSE
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_CONFLICT', HINT = v_ref;
            END IF;
            v_city_ids := v_city_ids || pg_catalog.jsonb_build_object(v_key, v_id::text);
            v_steps := pg_catalog.array_append(v_steps, pg_catalog.jsonb_build_object(
              'entity','cities','ref',v_ref,'action',v_action,'fields','[]'::jsonb,'id',v_id::text,
              'country_code',v_country,'state_code',v_state,'name',v_name,'timezone',v_timezone));
          END LOOP;

          FOR v_item, v_ordinal IN SELECT e, o FROM pg_catalog.jsonb_array_elements(v_doc->'service_areas') WITH ORDINALITY AS t(e, o) LOOP
            v_ref := 'service_areas[' || v_ordinal || ']';
            IF pg_catalog.jsonb_typeof(v_item) <> 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item) k)
                  IS DISTINCT FROM ARRAY['city','name','polygon','status']
               OR pg_catalog.jsonb_typeof(v_item->'city') IS DISTINCT FROM 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item->'city') k)
                  IS DISTINCT FROM ARRAY['country_code','name','state_code'] THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ENTRY_SHAPE', HINT = v_ref;
            END IF;
            v_name := v_item->>'name';
            v_status := v_item->>'status';
            IF v_name IS NULL OR pg_catalog.length(v_name) NOT BETWEEN 1 AND 200 OR v_name <> pg_catalog.btrim(v_name)
               OR v_status IS NULL OR v_status NOT IN ('ACTIVE','INACTIVE') THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_SERVICE_AREA_INVALID', HINT = v_ref;
            END IF;
            v_key := (v_item->'city'->>'country_code') || '|' || (v_item->'city'->>'state_code') || '|' || (v_item->'city'->>'name');
            v_city_id := NULL;
            IF v_city_ids ? v_key THEN
              v_city_id := (v_city_ids->>v_key)::uuid;
            ELSE
              SELECT c.id INTO v_city_id FROM locations.cities c
              WHERE c.country_code = v_item->'city'->>'country_code'
                AND c.state_code = v_item->'city'->>'state_code'
                AND c.name = v_item->'city'->>'name'
                AND c.status = 'ACTIVE';
            END IF;
            IF v_city_id IS NULL THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_NOT_FOUND', HINT = v_ref;
            END IF;
            BEGIN
              v_geom := public.ST_SetSRID(public.ST_GeomFromGeoJSON((v_item->'polygon')::text), 4326);
            EXCEPTION WHEN others THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_GEOMETRY_INVALID', HINT = v_ref;
            END;
            IF v_geom IS NULL OR public.ST_GeometryType(v_geom) <> 'ST_MultiPolygon'
               OR public.ST_IsEmpty(v_geom) OR NOT public.ST_IsValid(v_geom) THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_GEOMETRY_INVALID', HINT = v_ref;
            END IF;
            IF public.ST_XMin(v_geom) < -180 OR public.ST_XMax(v_geom) > 180
               OR public.ST_YMin(v_geom) < -90 OR public.ST_YMax(v_geom) > 90 THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_GEOMETRY_OUT_OF_RANGE', HINT = v_ref;
            END IF;
            v_vertices := v_vertices + public.ST_NPoints(v_geom);
            IF v_vertices > 200000 THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_GEOMETRY_TOO_LARGE', HINT = v_ref;
            END IF;
            v_key := v_city_id::text || '|' || v_name;
            IF v_area_ids ? v_key THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DUPLICATE_NATURAL_KEY', HINT = v_ref;
            END IF;
            v_id := NULL;
            SELECT a.id, a.status, a.polygon INTO v_id, v_existing_text1, v_existing_geom
            FROM locations.service_areas a
            WHERE a.owner_org_id = p_organization_id AND a.city_id = v_city_id AND a.name = v_name;
            v_fields := ARRAY[]::text[];
            IF v_id IS NULL THEN
              v_id := pg_catalog.gen_random_uuid();
              v_action := 'CREATE';
            ELSE
              IF public.ST_AsBinary(v_existing_geom) IS DISTINCT FROM public.ST_AsBinary(v_geom) THEN
                v_fields := v_fields || 'polygon'::text;
              END IF;
              IF v_existing_text1 IS DISTINCT FROM v_status THEN
                v_fields := v_fields || 'status'::text;
              END IF;
              v_action := CASE WHEN pg_catalog.cardinality(v_fields) = 0 THEN 'UNCHANGED' ELSE 'UPDATE' END;
            END IF;
            -- Polygons are carried by index into one geometry array, so the plan never copies them.
            v_geoms := pg_catalog.array_append(v_geoms, v_geom);
            v_area_ids := v_area_ids || pg_catalog.jsonb_build_object(v_key, v_id::text);
            v_area_geom_index := v_area_geom_index || pg_catalog.jsonb_build_object(v_id::text, pg_catalog.cardinality(v_geoms));
            v_steps := pg_catalog.array_append(v_steps, pg_catalog.jsonb_build_object(
              'entity','service_areas','ref',v_ref,'action',v_action,'fields',pg_catalog.to_jsonb(v_fields),
              'id',v_id::text,'city_id',v_city_id::text,'name',v_name,'status',v_status,
              'geom',pg_catalog.cardinality(v_geoms)));
          END LOOP;

          FOR v_item, v_ordinal IN SELECT e, o FROM pg_catalog.jsonb_array_elements(v_doc->'operating_zones') WITH ORDINALITY AS t(e, o) LOOP
            v_ref := 'operating_zones[' || v_ordinal || ']';
            IF pg_catalog.jsonb_typeof(v_item) <> 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item) k)
                  IS DISTINCT FROM ARRAY['name','polygon','service_area','status','zone_type']
               OR pg_catalog.jsonb_typeof(v_item->'service_area') IS DISTINCT FROM 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item->'service_area') k)
                  IS DISTINCT FROM ARRAY['city','name']
               OR pg_catalog.jsonb_typeof(v_item->'service_area'->'city') IS DISTINCT FROM 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item->'service_area'->'city') k)
                  IS DISTINCT FROM ARRAY['country_code','name','state_code'] THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ENTRY_SHAPE', HINT = v_ref;
            END IF;
            v_name := v_item->>'name';
            v_status := v_item->>'status';
            v_zone_type := v_item->>'zone_type';
            IF v_name IS NULL OR pg_catalog.length(v_name) NOT BETWEEN 1 AND 200 OR v_name <> pg_catalog.btrim(v_name)
               OR v_status IS NULL OR v_status NOT IN ('ACTIVE','INACTIVE')
               OR v_zone_type IS NULL OR v_zone_type NOT IN ('CORE','STANDARD','EXTENDED','EXCLUDED') THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_OPERATING_ZONE_INVALID', HINT = v_ref;
            END IF;
            v_key := (v_item->'service_area'->'city'->>'country_code') || '|' || (v_item->'service_area'->'city'->>'state_code')
              || '|' || (v_item->'service_area'->'city'->>'name');
            v_city_id := NULL;
            IF v_city_ids ? v_key THEN
              v_city_id := (v_city_ids->>v_key)::uuid;
            ELSE
              SELECT c.id INTO v_city_id FROM locations.cities c
              WHERE c.country_code = v_item->'service_area'->'city'->>'country_code'
                AND c.state_code = v_item->'service_area'->'city'->>'state_code'
                AND c.name = v_item->'service_area'->'city'->>'name'
                AND c.status = 'ACTIVE';
            END IF;
            v_area_id := NULL;
            IF v_city_id IS NOT NULL THEN
              v_key := v_city_id::text || '|' || (v_item->'service_area'->>'name');
              IF v_area_ids ? v_key THEN
                v_area_id := (v_area_ids->>v_key)::uuid;
              ELSE
                SELECT a.id INTO v_area_id FROM locations.service_areas a
                WHERE a.owner_org_id = p_organization_id AND a.city_id = v_city_id
                  AND a.name = v_item->'service_area'->>'name';
              END IF;
            END IF;
            IF v_area_id IS NULL THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_SERVICE_AREA_NOT_FOUND', HINT = v_ref;
            END IF;
            BEGIN
              v_geom := public.ST_SetSRID(public.ST_GeomFromGeoJSON((v_item->'polygon')::text), 4326);
            EXCEPTION WHEN others THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_GEOMETRY_INVALID', HINT = v_ref;
            END;
            IF v_geom IS NULL OR public.ST_GeometryType(v_geom) <> 'ST_MultiPolygon'
               OR public.ST_IsEmpty(v_geom) OR NOT public.ST_IsValid(v_geom) THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_GEOMETRY_INVALID', HINT = v_ref;
            END IF;
            IF public.ST_XMin(v_geom) < -180 OR public.ST_XMax(v_geom) > 180
               OR public.ST_YMin(v_geom) < -90 OR public.ST_YMax(v_geom) > 90 THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_GEOMETRY_OUT_OF_RANGE', HINT = v_ref;
            END IF;
            v_vertices := v_vertices + public.ST_NPoints(v_geom);
            IF v_vertices > 200000 THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_GEOMETRY_TOO_LARGE', HINT = v_ref;
            END IF;
            -- A zone subdivides its service area: it must lie within the area as this load leaves it.
            IF v_area_geom_index ? v_area_id::text THEN
              v_parent_geom := v_geoms[(v_area_geom_index->>(v_area_id::text))::integer];
            ELSE
              SELECT a.polygon INTO v_parent_geom FROM locations.service_areas a WHERE a.id = v_area_id;
            END IF;
            IF NOT public.ST_CoveredBy(v_geom, v_parent_geom) THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ZONE_OUTSIDE_SERVICE_AREA', HINT = v_ref;
            END IF;
            v_key := v_area_id::text || '|' || v_name;
            IF v_zone_ids ? v_key THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DUPLICATE_NATURAL_KEY', HINT = v_ref;
            END IF;
            v_id := NULL;
            SELECT z.id, z.zone_type, z.status, z.polygon INTO v_id, v_existing_text1, v_existing_text2, v_existing_geom
            FROM locations.operating_zones z
            WHERE z.owner_org_id = p_organization_id AND z.service_area_id = v_area_id AND z.name = v_name;
            v_fields := ARRAY[]::text[];
            IF v_id IS NULL THEN
              v_id := pg_catalog.gen_random_uuid();
              v_action := 'CREATE';
            ELSE
              IF v_existing_text1 IS DISTINCT FROM v_zone_type THEN
                v_fields := v_fields || 'zone_type'::text;
              END IF;
              IF public.ST_AsBinary(v_existing_geom) IS DISTINCT FROM public.ST_AsBinary(v_geom) THEN
                v_fields := v_fields || 'polygon'::text;
              END IF;
              IF v_existing_text2 IS DISTINCT FROM v_status THEN
                v_fields := v_fields || 'status'::text;
              END IF;
              v_action := CASE WHEN pg_catalog.cardinality(v_fields) = 0 THEN 'UNCHANGED' ELSE 'UPDATE' END;
            END IF;
            v_geoms := pg_catalog.array_append(v_geoms, v_geom);
            v_zone_ids := v_zone_ids || pg_catalog.jsonb_build_object(v_key, v_id::text);
            v_steps := pg_catalog.array_append(v_steps, pg_catalog.jsonb_build_object(
              'entity','operating_zones','ref',v_ref,'action',v_action,'fields',pg_catalog.to_jsonb(v_fields),
              'id',v_id::text,'service_area_id',v_area_id::text,'name',v_name,'zone_type',v_zone_type,
              'status',v_status,'geom',pg_catalog.cardinality(v_geoms)));
          END LOOP;

          v_seen := '{}'::jsonb;
          FOR v_item, v_ordinal IN SELECT e, o FROM pg_catalog.jsonb_array_elements(v_doc->'tariff_rules') WITH ORDINALITY AS t(e, o) LOOP
            v_ref := 'tariff_rules[' || v_ordinal || ']';
            IF pg_catalog.jsonb_typeof(v_item) <> 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item) k)
                  IS DISTINCT FROM ARRAY['active_from','active_to','amount_cents','city','operating_zone','policy_version',
                    'pricing_tier','service_area','service_type','status','tax_mode']
               OR pg_catalog.jsonb_typeof(v_item->'city') IS DISTINCT FROM 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item->'city') k)
                  IS DISTINCT FROM ARRAY['country_code','name','state_code']
               OR pg_catalog.jsonb_typeof(v_item->'service_area') NOT IN ('string','null')
               OR pg_catalog.jsonb_typeof(v_item->'operating_zone') NOT IN ('string','null')
               OR pg_catalog.jsonb_typeof(v_item->'active_to') NOT IN ('string','null')
               OR pg_catalog.jsonb_typeof(v_item->'active_from') IS DISTINCT FROM 'string'
               OR pg_catalog.jsonb_typeof(v_item->'policy_version') IS DISTINCT FROM 'string'
               OR pg_catalog.jsonb_typeof(v_item->'amount_cents') IS DISTINCT FROM 'number' THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ENTRY_SHAPE', HINT = v_ref;
            END IF;
            -- Money is integer cents only (AI-01 §4.15), judged on the raw json token exactly like the job:
            -- a fraction, exponent, sign or leading zero is refused.
            IF v_amount_tokens[v_ordinal::integer] IS NULL OR v_amount_tokens[v_ordinal::integer] !~ '^(0|[1-9][0-9]{0,17})$' THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_AMOUNT_NOT_INTEGER_CENTS', HINT = v_ref;
            END IF;
            v_amount := v_amount_tokens[v_ordinal::integer]::bigint;
            v_tier := v_item->>'pricing_tier';
            v_service_type := v_item->>'service_type';
            v_tax_mode := v_item->>'tax_mode';
            v_status := v_item->>'status';
            v_policy_version := v_item->>'policy_version';
            IF v_tier IS NULL OR v_tier NOT IN ('OCCASIONAL','BUSINESS_1_49','BUSINESS_50_199','BUSINESS_200_499','BUSINESS_500_PLUS','CUSTOM')
               OR v_service_type IS NULL OR v_service_type NOT IN ('SAME_DAY','URGENT','SCHEDULED_ROUTE')
               OR v_tax_mode IS NULL OR v_tax_mode NOT IN ('PLUS_VAT','VAT_INCLUDED','EXEMPT')
               OR v_status IS NULL OR v_status NOT IN ('ACTIVE','INACTIVE')
               OR (v_item->>'active_from') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$'
               OR (v_item->>'active_to' IS NOT NULL
                   AND (v_item->>'active_to') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$')
               OR (v_item->>'operating_zone' IS NOT NULL AND v_item->>'service_area' IS NULL)
               -- PRC policy version per organization: required on every rule, same pattern as the pending
               -- pricing.tariff_rules.policy_version CHECK. Validated now, not yet stored (see TODO below).
               OR v_policy_version !~ '^[A-Za-z0-9._-]{1,64}$' THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_INVALID', HINT = v_ref;
            END IF;
            BEGIN
              v_active_from := (v_item->>'active_from')::timestamptz;
              v_active_to := (v_item->>'active_to')::timestamptz;
            EXCEPTION WHEN others THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_INVALID', HINT = v_ref;
            END;
            IF v_active_to IS NOT NULL AND v_active_to <= v_active_from THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_INVALID', HINT = v_ref;
            END IF;
            v_key := (v_item->'city'->>'country_code') || '|' || (v_item->'city'->>'state_code') || '|' || (v_item->'city'->>'name');
            v_city_id := NULL;
            IF v_city_ids ? v_key THEN
              v_city_id := (v_city_ids->>v_key)::uuid;
            ELSE
              SELECT c.id INTO v_city_id FROM locations.cities c
              WHERE c.country_code = v_item->'city'->>'country_code'
                AND c.state_code = v_item->'city'->>'state_code'
                AND c.name = v_item->'city'->>'name'
                AND c.status = 'ACTIVE';
            END IF;
            IF v_city_id IS NULL THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_NOT_FOUND', HINT = v_ref;
            END IF;
            v_area_id := NULL;
            IF v_item->>'service_area' IS NOT NULL THEN
              v_key := v_city_id::text || '|' || (v_item->>'service_area');
              IF v_area_ids ? v_key THEN
                v_area_id := (v_area_ids->>v_key)::uuid;
              ELSE
                SELECT a.id INTO v_area_id FROM locations.service_areas a
                WHERE a.owner_org_id = p_organization_id AND a.city_id = v_city_id AND a.name = v_item->>'service_area';
              END IF;
              IF v_area_id IS NULL THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_SERVICE_AREA_NOT_FOUND', HINT = v_ref;
              END IF;
            END IF;
            v_zone_id := NULL;
            IF v_item->>'operating_zone' IS NOT NULL THEN
              v_key := v_area_id::text || '|' || (v_item->>'operating_zone');
              IF v_zone_ids ? v_key THEN
                v_zone_id := (v_zone_ids->>v_key)::uuid;
              ELSE
                SELECT z.id INTO v_zone_id FROM locations.operating_zones z
                WHERE z.owner_org_id = p_organization_id AND z.service_area_id = v_area_id
                  AND z.name = v_item->>'operating_zone';
              END IF;
              IF v_zone_id IS NULL THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_OPERATING_ZONE_NOT_FOUND', HINT = v_ref;
              END IF;
            END IF;
            v_key := v_city_id::text || '|' || COALESCE(v_area_id::text, '-') || '|' || COALESCE(v_zone_id::text, '-')
              || '|' || v_tier || '|' || v_service_type || '|' || pg_catalog.to_char(v_active_from AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS');
            IF v_seen ? v_key THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DUPLICATE_NATURAL_KEY', HINT = v_ref;
            END IF;
            v_seen := v_seen || pg_catalog.jsonb_build_object(v_key, true);
            SELECT count(*) INTO v_count FROM pricing.tariff_rules r
            WHERE r.owner_org_id = p_organization_id AND r.city_id = v_city_id
              AND r.service_area_id IS NOT DISTINCT FROM v_area_id
              AND r.operating_zone_id IS NOT DISTINCT FROM v_zone_id
              AND r.pricing_tier = v_tier AND r.service_type = v_service_type
              AND r.active_from = v_active_from;
            IF v_count > 1 THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_AMBIGUOUS', HINT = v_ref;
            END IF;
            v_id := NULL;
            SELECT r.id, r.amount_cents, r.tax_mode, r.active_to, r.status
            INTO v_id, v_existing_amount, v_existing_text1, v_existing_to, v_existing_text2
            FROM pricing.tariff_rules r
            WHERE r.owner_org_id = p_organization_id AND r.city_id = v_city_id
              AND r.service_area_id IS NOT DISTINCT FROM v_area_id
              AND r.operating_zone_id IS NOT DISTINCT FROM v_zone_id
              AND r.pricing_tier = v_tier AND r.service_type = v_service_type
              AND r.active_from = v_active_from;
            v_fields := ARRAY[]::text[];
            IF v_id IS NULL THEN
              v_id := pg_catalog.gen_random_uuid();
              v_action := 'CREATE';
            ELSE
              -- A published price is never rewritten: a new price is a new rule with a later active_from.
              IF v_existing_amount IS DISTINCT FROM v_amount OR v_existing_text1 IS DISTINCT FROM v_tax_mode THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_AMOUNT_IMMUTABLE', HINT = v_ref;
              END IF;
              IF v_existing_to IS DISTINCT FROM v_active_to THEN
                v_fields := v_fields || 'active_to'::text;
              END IF;
              IF v_existing_text2 IS DISTINCT FROM v_status THEN
                v_fields := v_fields || 'status'::text;
              END IF;
              v_action := CASE WHEN pg_catalog.cardinality(v_fields) = 0 THEN 'UNCHANGED' ELSE 'UPDATE' END;
            END IF;
            v_steps := pg_catalog.array_append(v_steps, pg_catalog.jsonb_build_object(
              'entity','tariff_rules','ref',v_ref,'action',v_action,'fields',pg_catalog.to_jsonb(v_fields),
              'id',v_id::text,'city_id',v_city_id::text,'service_area_id',v_area_id::text,'operating_zone_id',v_zone_id::text,
              'pricing_tier',v_tier,'service_type',v_service_type,'amount_cents',v_amount,'tax_mode',v_tax_mode,
              'policy_version',v_policy_version,'active_from',v_active_from,'active_to',v_active_to,'status',v_status));
          END LOOP;

          -- MDM-001 M3: ACTIVE rules of one (organization, city, area, zone, tier, service type) never overlap,
          -- as this load would leave them: the file's rules (new or updated) plus every other stored rule. A new
          -- price therefore has to close the previous open-ended rule (its active_to, in the same file).
          WITH planned AS (
            SELECT (e->>'id')::uuid AS id, e->>'ref' AS ref, e->>'city_id' AS city_id,
                   e->>'service_area_id' AS area_id, e->>'operating_zone_id' AS zone_id,
                   e->>'pricing_tier' AS tier, e->>'service_type' AS service_type,
                   (e->>'active_from')::timestamptz AS active_from, (e->>'active_to')::timestamptz AS active_to,
                   e->>'status' AS status, o AS ordinal
            FROM pg_catalog.unnest(v_steps) WITH ORDINALITY AS s(e, o)
            WHERE e->>'entity' = 'tariff_rules'
          ),
          final AS (
            SELECT id, ref, city_id, area_id, zone_id, tier, service_type, active_from, active_to, status, ordinal
            FROM planned
            UNION ALL
            SELECT r.id, NULL, r.city_id::text, r.service_area_id::text, r.operating_zone_id::text, r.pricing_tier,
                   r.service_type, r.active_from, r.active_to, r.status, NULL
            FROM pricing.tariff_rules r
            WHERE r.owner_org_id = p_organization_id
              AND NOT EXISTS (SELECT 1 FROM planned p WHERE p.id = r.id)
          )
          SELECT COALESCE(a.ref, b.ref) INTO v_ref
          FROM final a
          JOIN final b ON a.id < b.id
            AND a.city_id = b.city_id
            AND a.area_id IS NOT DISTINCT FROM b.area_id
            AND a.zone_id IS NOT DISTINCT FROM b.zone_id
            AND a.tier = b.tier AND a.service_type = b.service_type
          WHERE a.status = 'ACTIVE' AND b.status = 'ACTIVE'
            AND (a.ref IS NOT NULL OR b.ref IS NOT NULL)
            AND pg_catalog.tstzrange(a.active_from, a.active_to, '[)') && pg_catalog.tstzrange(b.active_from, b.active_to, '[)')
          ORDER BY LEAST(a.ordinal, b.ordinal)
          LIMIT 1;
          IF FOUND THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_TARIFF_OVERLAP', HINT = v_ref;
          END IF;

          v_seen := '{}'::jsonb;
          FOR v_item, v_ordinal IN SELECT e, o FROM pg_catalog.jsonb_array_elements(v_doc->'driver_profiles') WITH ORDINALITY AS t(e, o) LOOP
            v_ref := 'driver_profiles[' || v_ordinal || ']';
            IF pg_catalog.jsonb_typeof(v_item) <> 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item) k)
                  IS DISTINCT FROM ARRAY['driver_type','home_city','service_areas','status','user_id','vehicle_type']
               OR pg_catalog.jsonb_typeof(v_item->'home_city') IS DISTINCT FROM 'object'
               OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_item->'home_city') k)
                  IS DISTINCT FROM ARRAY['country_code','name','state_code']
               OR pg_catalog.jsonb_typeof(v_item->'service_areas') IS DISTINCT FROM 'array'
               OR pg_catalog.jsonb_array_length(v_item->'service_areas') > 100
               OR pg_catalog.jsonb_typeof(v_item->'user_id') IS DISTINCT FROM 'string'
               OR (v_item->>'user_id') !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ENTRY_SHAPE', HINT = v_ref;
            END IF;
            v_user_id := (v_item->>'user_id')::uuid;
            v_driver_type := v_item->>'driver_type';
            v_vehicle_type := v_item->>'vehicle_type';
            v_status := v_item->>'status';
            IF v_driver_type IS NULL OR v_driver_type NOT IN ('OWN','EXTERNAL','ALLY')
               OR v_vehicle_type IS NULL OR v_vehicle_type NOT IN ('MOTORCYCLE','CAR','VAN','BICYCLE','WALKER')
               OR v_status IS NULL OR v_status NOT IN ('PENDING','ACTIVE','SUSPENDED','INACTIVE') THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DRIVER_PROFILE_INVALID', HINT = v_ref;
            END IF;
            IF v_seen ? v_user_id::text THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DUPLICATE_NATURAL_KEY', HINT = v_ref;
            END IF;
            v_seen := v_seen || pg_catalog.jsonb_build_object(v_user_id::text, true);
            -- REG-DRIVER-PROFILE-LATER: the profile completes an existing DRIVER membership of this organization.
            IF NOT EXISTS (
              SELECT 1 FROM identity.users u
              JOIN organizations.organization_memberships m ON m.user_id = u.id
              WHERE u.id = v_user_id AND u.status = 'ACTIVE'
                AND m.organization_id = p_organization_id AND m.role = 'DRIVER' AND m.status = 'ACTIVE'
            ) THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DRIVER_MEMBERSHIP_REQUIRED', HINT = v_ref;
            END IF;
            v_key := (v_item->'home_city'->>'country_code') || '|' || (v_item->'home_city'->>'state_code') || '|' || (v_item->'home_city'->>'name');
            v_city_id := NULL;
            IF v_city_ids ? v_key THEN
              v_city_id := (v_city_ids->>v_key)::uuid;
            ELSE
              SELECT c.id INTO v_city_id FROM locations.cities c
              WHERE c.country_code = v_item->'home_city'->>'country_code'
                AND c.state_code = v_item->'home_city'->>'state_code'
                AND c.name = v_item->'home_city'->>'name'
                AND c.status = 'ACTIVE';
            END IF;
            IF v_city_id IS NULL THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_CITY_NOT_FOUND', HINT = v_ref;
            END IF;
            v_id := NULL;
            SELECT d.id, d.org_id, d.home_city_id::text, d.driver_type, d.vehicle_type, d.status
            INTO v_id, v_existing_uuid, v_existing_text1, v_existing_text2, v_existing_text3, v_existing_text4
            FROM drivers.driver_profiles d WHERE d.user_id = v_user_id;
            v_fields := ARRAY[]::text[];
            IF v_id IS NULL THEN
              v_id := pg_catalog.gen_random_uuid();
              v_action := 'CREATE';
            ELSIF v_existing_uuid IS DISTINCT FROM p_organization_id THEN
              RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DRIVER_MEMBERSHIP_REQUIRED', HINT = v_ref;
            ELSE
              IF v_existing_text1 IS DISTINCT FROM v_city_id::text THEN
                v_fields := v_fields || 'home_city'::text;
              END IF;
              IF v_existing_text2 IS DISTINCT FROM v_driver_type THEN
                v_fields := v_fields || 'driver_type'::text;
              END IF;
              IF v_existing_text3 IS DISTINCT FROM v_vehicle_type THEN
                v_fields := v_fields || 'vehicle_type'::text;
              END IF;
              IF v_existing_text4 IS DISTINCT FROM v_status THEN
                v_fields := v_fields || 'status'::text;
              END IF;
              v_action := CASE WHEN pg_catalog.cardinality(v_fields) = 0 THEN 'UNCHANGED' ELSE 'UPDATE' END;
            END IF;
            v_steps := pg_catalog.array_append(v_steps, pg_catalog.jsonb_build_object(
              'entity','driver_profiles','ref',v_ref,'action',v_action,'fields',pg_catalog.to_jsonb(v_fields),
              'id',v_id::text,'user_id',v_user_id::text,'home_city_id',v_city_id::text,'driver_type',v_driver_type,
              'vehicle_type',v_vehicle_type,'status',v_status));

            v_sub_seen := '{}'::jsonb;
            FOR v_sub, v_sub_ordinal IN SELECT e, o FROM pg_catalog.jsonb_array_elements(v_item->'service_areas') WITH ORDINALITY AS t(e, o) LOOP
              v_ref := 'driver_profiles[' || v_ordinal || '].service_areas[' || v_sub_ordinal || ']';
              IF pg_catalog.jsonb_typeof(v_sub) <> 'object'
                 OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_sub) k)
                    IS DISTINCT FROM ARRAY['city','name','status']
                 OR pg_catalog.jsonb_typeof(v_sub->'city') IS DISTINCT FROM 'object'
                 OR (SELECT array_agg(k ORDER BY k COLLATE "C") FROM pg_catalog.jsonb_object_keys(v_sub->'city') k)
                    IS DISTINCT FROM ARRAY['country_code','name','state_code']
                 OR v_sub->>'status' IS NULL OR v_sub->>'status' NOT IN ('ACTIVE','INACTIVE') THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_ENTRY_SHAPE', HINT = v_ref;
              END IF;
              v_key := (v_sub->'city'->>'country_code') || '|' || (v_sub->'city'->>'state_code') || '|' || (v_sub->'city'->>'name');
              v_city_id := NULL;
              IF v_city_ids ? v_key THEN
                v_city_id := (v_city_ids->>v_key)::uuid;
              ELSE
                SELECT c.id INTO v_city_id FROM locations.cities c
                WHERE c.country_code = v_sub->'city'->>'country_code'
                  AND c.state_code = v_sub->'city'->>'state_code'
                  AND c.name = v_sub->'city'->>'name'
                  AND c.status = 'ACTIVE';
              END IF;
              v_area_id := NULL;
              IF v_city_id IS NOT NULL THEN
                v_key := v_city_id::text || '|' || (v_sub->>'name');
                IF v_area_ids ? v_key THEN
                  v_area_id := (v_area_ids->>v_key)::uuid;
                ELSE
                  SELECT a.id INTO v_area_id FROM locations.service_areas a
                  WHERE a.owner_org_id = p_organization_id AND a.city_id = v_city_id AND a.name = v_sub->>'name';
                END IF;
              END IF;
              IF v_area_id IS NULL THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_SERVICE_AREA_NOT_FOUND', HINT = v_ref;
              END IF;
              IF v_sub_seen ? v_area_id::text THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DUPLICATE_NATURAL_KEY', HINT = v_ref;
              END IF;
              v_sub_seen := v_sub_seen || pg_catalog.jsonb_build_object(v_area_id::text, true);
              v_existing_text1 := NULL;
              v_existing_uuid := NULL;
              SELECT s.status, s.org_id INTO v_existing_text1, v_existing_uuid
              FROM drivers.driver_service_areas s WHERE s.driver_id = v_id AND s.service_area_id = v_area_id;
              IF v_existing_text1 IS NULL THEN
                v_action := 'CREATE';
                v_fields := ARRAY[]::text[];
              ELSIF v_existing_uuid IS DISTINCT FROM p_organization_id THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'MDM001_DRIVER_MEMBERSHIP_REQUIRED', HINT = v_ref;
              ELSIF v_existing_text1 = v_sub->>'status' THEN
                v_action := 'UNCHANGED';
                v_fields := ARRAY[]::text[];
              ELSE
                v_action := 'UPDATE';
                v_fields := ARRAY['status'];
              END IF;
              v_steps := pg_catalog.array_append(v_steps, pg_catalog.jsonb_build_object(
                'entity','driver_service_areas','ref',v_ref,'action',v_action,'fields',pg_catalog.to_jsonb(v_fields),
                'driver_id',v_id::text,'service_area_id',v_area_id::text,'status',v_sub->>'status'));
            END LOOP;
          END LOOP;

          ---------------------------------------------------------------------------------------------
          -- Phase 2: apply the plan (skipped by a dry run), then one append-only audit row.
          ---------------------------------------------------------------------------------------------
          IF NOT p_dry_run THEN
            BEGIN
              FOR v_step IN SELECT e FROM pg_catalog.unnest(v_steps) e LOOP
                CONTINUE WHEN v_step->>'action' = 'UNCHANGED';
                CASE v_step->>'entity'
                  WHEN 'cities' THEN
                    INSERT INTO locations.cities(id,country_code,state_code,name,timezone,status)
                    VALUES ((v_step->>'id')::uuid, v_step->>'country_code', v_step->>'state_code', v_step->>'name',
                      v_step->>'timezone', 'ACTIVE');
                  WHEN 'service_areas' THEN
                    IF v_step->>'action' = 'CREATE' THEN
                      INSERT INTO locations.service_areas(id,owner_org_id,city_id,name,polygon,status)
                      VALUES ((v_step->>'id')::uuid, p_organization_id, (v_step->>'city_id')::uuid, v_step->>'name',
                        v_geoms[(v_step->>'geom')::integer], v_step->>'status');
                    ELSE
                      UPDATE locations.service_areas a
                      SET polygon = v_geoms[(v_step->>'geom')::integer], status = v_step->>'status'
                      WHERE a.id = (v_step->>'id')::uuid AND a.owner_org_id = p_organization_id;
                    END IF;
                  WHEN 'operating_zones' THEN
                    IF v_step->>'action' = 'CREATE' THEN
                      INSERT INTO locations.operating_zones(id,owner_org_id,service_area_id,name,zone_type,polygon,status)
                      VALUES ((v_step->>'id')::uuid, p_organization_id, (v_step->>'service_area_id')::uuid, v_step->>'name',
                        v_step->>'zone_type', v_geoms[(v_step->>'geom')::integer], v_step->>'status');
                    ELSE
                      UPDATE locations.operating_zones z
                      SET zone_type = v_step->>'zone_type', polygon = v_geoms[(v_step->>'geom')::integer],
                          status = v_step->>'status'
                      WHERE z.id = (v_step->>'id')::uuid AND z.owner_org_id = p_organization_id;
                    END IF;
                  WHEN 'tariff_rules' THEN
                    -- TODO(PRC policy_version per organization, feature/prc-policy-version-per-org): once that lane
                    -- adds the NOT NULL pricing.tariff_rules.policy_version column, insert v_step->>'policy_version'
                    -- here, compare it on reload (like amount_cents: a published rule's version is not rewritten),
                    -- set policy_version_persisted in the result and add policy_version to the executor's
                    -- SELECT/INSERT column grants (AI-18, validator, DatabaseBaselineAssertions). Until then it is
                    -- validated and planned but not stored, and the NOT NULL column makes this INSERT fail closed
                    -- (MDM001_WRITE_CONFLICT) if that lane lands first.
                    IF v_step->>'action' = 'CREATE' THEN
                      INSERT INTO pricing.tariff_rules(
                        id,owner_org_id,city_id,service_area_id,operating_zone_id,pricing_tier,service_type,
                        amount_cents,tax_mode,active_from,active_to,status)
                      VALUES ((v_step->>'id')::uuid, p_organization_id, (v_step->>'city_id')::uuid,
                        (v_step->>'service_area_id')::uuid, (v_step->>'operating_zone_id')::uuid,
                        v_step->>'pricing_tier', v_step->>'service_type', (v_step->>'amount_cents')::bigint,
                        v_step->>'tax_mode', (v_step->>'active_from')::timestamptz, (v_step->>'active_to')::timestamptz,
                        v_step->>'status');
                    ELSE
                      UPDATE pricing.tariff_rules r
                      SET active_to = (v_step->>'active_to')::timestamptz, status = v_step->>'status'
                      WHERE r.id = (v_step->>'id')::uuid AND r.owner_org_id = p_organization_id;
                    END IF;
                  WHEN 'driver_profiles' THEN
                    IF v_step->>'action' = 'CREATE' THEN
                      INSERT INTO drivers.driver_profiles(id,user_id,org_id,home_city_id,driver_type,vehicle_type,status)
                      VALUES ((v_step->>'id')::uuid, (v_step->>'user_id')::uuid, p_organization_id,
                        (v_step->>'home_city_id')::uuid, v_step->>'driver_type', v_step->>'vehicle_type', v_step->>'status');
                    ELSE
                      UPDATE drivers.driver_profiles d
                      SET home_city_id = (v_step->>'home_city_id')::uuid, driver_type = v_step->>'driver_type',
                          vehicle_type = v_step->>'vehicle_type', status = v_step->>'status'
                      WHERE d.id = (v_step->>'id')::uuid AND d.org_id = p_organization_id;
                    END IF;
                  WHEN 'driver_service_areas' THEN
                    IF v_step->>'action' = 'CREATE' THEN
                      INSERT INTO drivers.driver_service_areas(driver_id,service_area_id,org_id,status)
                      VALUES ((v_step->>'driver_id')::uuid, (v_step->>'service_area_id')::uuid, p_organization_id,
                        v_step->>'status');
                    ELSE
                      UPDATE drivers.driver_service_areas s SET status = v_step->>'status'
                      WHERE s.driver_id = (v_step->>'driver_id')::uuid AND s.service_area_id = (v_step->>'service_area_id')::uuid
                        AND s.org_id = p_organization_id;
                    END IF;
                END CASE;
              END LOOP;
            EXCEPTION WHEN integrity_constraint_violation THEN
              -- MDM-001 m8: a constraint error would print key values in the server log; report the reference
              -- and SQLSTATE only. Nothing of the load survives (the caller's transaction is aborted).
              GET STACKED DIAGNOSTICS v_sqlstate = RETURNED_SQLSTATE;
              RAISE EXCEPTION USING ERRCODE = '23000', MESSAGE = 'MDM001_WRITE_CONFLICT',
                HINT = COALESCE(v_step->>'ref', 'apply') || ' sqlstate=' || v_sqlstate;
            END;
          END IF;

          SELECT pg_catalog.jsonb_object_agg(entity, pg_catalog.jsonb_build_object(
                   'created', created, 'updated', updated, 'unchanged', unchanged))
          INTO v_counts
          FROM (
            SELECT n.entity,
                   count(p.e) FILTER (WHERE p.e->>'action' = 'CREATE') AS created,
                   count(p.e) FILTER (WHERE p.e->>'action' = 'UPDATE') AS updated,
                   count(p.e) FILTER (WHERE p.e->>'action' = 'UNCHANGED') AS unchanged
            FROM pg_catalog.unnest(ARRAY['cities','service_areas','operating_zones','tariff_rules','driver_profiles',
                   'driver_service_areas']) AS n(entity)
            LEFT JOIN pg_catalog.unnest(v_steps) AS p(e) ON p.e->>'entity' = n.entity
            GROUP BY n.entity
          ) q;

          SELECT COALESCE(pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
                   'entity', p.e->>'entity', 'ref', p.e->>'ref', 'action', p.e->>'action', 'fields', p.e->'fields')
                   ORDER BY p.o), '[]'::jsonb)
          INTO v_changes
          FROM pg_catalog.unnest(v_steps) WITH ORDINALITY AS p(e, o)
          WHERE p.e->>'action' <> 'UNCHANGED';

          v_result := pg_catalog.jsonb_build_object(
            'format', v_doc->>'format',
            'classification', v_doc->>'classification',
            'deployment_class', v_class,
            -- The job's SHA-256 of the file bytes, and PostgreSQL's own SHA-256 of the exact document text it
            -- received (MDM-001 m4); they are equal for a UTF-8 file without a byte-order mark.
            'file_sha256_reported', pg_catalog.encode(p_document_sha256, 'hex'),
            'document_sha256_computed', pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(p_document::text, 'UTF8')), 'hex'),
            'dry_run', p_dry_run,
            'policy_version_persisted', false,
            'counts', v_counts);

          IF NOT p_dry_run THEN
            -- MDM-001 M1/N3: the operator is the actor of record (no application user performs a load). The
            -- tenant-readable payload carries only a pseudonym: SHA-256 over a domain-separated login name, which
            -- platform staff match against the operator logins they created.
            INSERT INTO platform.audit_logs(
              id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
            VALUES (
              pg_catalog.gen_random_uuid(), p_organization_id, NULL, 'MASTER_DATA_LOADED', 'MASTER_DATA_LOAD',
              p_load_id, 'mdm-001',
              v_result || pg_catalog.jsonb_build_object('operator_ref', pg_catalog.encode(pg_catalog.sha256(
                pg_catalog.convert_to('paquetenvia.mdm-001.operator:' || SESSION_USER::text, 'UTF8')), 'hex')),
              v_now);
          END IF;

          RETURN v_result || pg_catalog.jsonb_build_object('changes', v_changes);
        END
        $function$;

        -- ACL first, while the deploying role still owns the function: ALTER ... OWNER rewrites the
        -- grantor to the new owner, and a managed-service deployer then needs no inherited executor rights.
        REVOKE ALL ON FUNCTION security.load_master_data(uuid,uuid,json,bytea,boolean) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.load_master_data(uuid,uuid,json,bytea,boolean) TO paqueteria_master_data_loader;
        ALTER FUNCTION security.load_master_data(uuid,uuid,json,bytea,boolean) OWNER TO paqueteria_master_data_executor;

        DO $verify$
        DECLARE
          fn oid := to_regprocedure('security.load_master_data(uuid,uuid,json,bytea,boolean)');
          executor oid := 'paqueteria_master_data_executor'::regrole;
          loader oid := 'paqueteria_master_data_loader'::regrole;
        BEGIN
          IF fn IS NULL
             OR (SELECT proowner FROM pg_proc WHERE oid=fn) <> executor
             OR NOT (SELECT prosecdef FROM pg_proc WHERE oid=fn)
             OR NOT ('search_path=pg_catalog, pg_temp' = ANY(
               COALESCE((SELECT proconfig FROM pg_proc WHERE oid=fn), ARRAY[]::text[])))
             OR (SELECT prosrc FROM pg_proc WHERE oid=fn) ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
          THEN
            RAISE EXCEPTION 'MDM-001 loader function security verification failed';
          END IF;

          -- MDM-001 m3: the exact ACL, read from the catalog rather than information_schema (which shows only
          -- privileges visible to the current role): EXECUTE for the owner and the operator grantee, nothing else.
          IF (SELECT string_agg(entry, ',' ORDER BY entry)
              FROM (
                SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END || ':' || a.privilege_type
                  || ':' || a.is_grantable::text AS entry
                FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) a WHERE p.oid = fn) acl)
             IS DISTINCT FROM 'paqueteria_master_data_executor:EXECUTE:false,paqueteria_master_data_loader:EXECUTE:false'
          THEN
            RAISE EXCEPTION 'MDM-001 loader function ACL differs from the MDM-001 contract';
          END IF;

          IF EXISTS (
               SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
               WHERE a.grantee IN (executor, loader))
             OR EXISTS (
               SELECT 1 FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
               WHERE a.grantee = loader)
             OR (SELECT count(*) FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
                 WHERE a.grantee = executor AND NOT a.is_grantable) <> 116
             OR EXISTS (
               SELECT 1 FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
               WHERE a.grantee = executor AND a.is_grantable)
             OR EXISTS (
               SELECT 1 FROM pg_attribute att
               JOIN pg_class c ON c.oid = att.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
               CROSS JOIN LATERAL aclexplode(att.attacl) a
               WHERE a.grantee = executor
                 AND n.nspname || '.' || c.relname NOT IN (
                   'identity.users','organizations.organizations','organizations.organization_memberships',
                   'locations.cities','locations.service_areas','locations.operating_zones','pricing.tariff_rules',
                   'drivers.driver_profiles','drivers.driver_service_areas','platform.audit_logs',
                   'platform.master_data_deployment_gate'))
             OR EXISTS (
               SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a
               WHERE c.oid = 'platform.master_data_deployment_gate'::regclass
                 AND a.grantee <> c.relowner)
             OR EXISTS (
               SELECT 1 FROM pg_attribute att CROSS JOIN LATERAL aclexplode(att.attacl) a
               WHERE att.attrelid = 'platform.master_data_deployment_gate'::regclass AND a.grantee <> executor)
          THEN
            RAISE EXCEPTION 'MDM-001 executor or loader privileges differ from the MDM-001 contract';
          END IF;

          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN ('clients','orders','dispatch','routes','custody','incidents','finance','allies',
                'notifications','reporting','security','extensions')
              AND has_schema_privilege('paqueteria_master_data_executor', n.oid, 'USAGE')
          ) OR EXISTS (
            -- security is left out for the executor: the E-002 bridge holds a transaction-scoped CREATE there
            -- while this lane runs and revokes it before asserting the final boundary.
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN ('identity','organizations','clients','locations','pricing','orders','dispatch','drivers',
                'routes','custody','incidents','finance','allies','notifications','reporting','platform','extensions')
              AND has_schema_privilege('paqueteria_master_data_executor', n.oid, 'CREATE')
          ) OR EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN ('identity','organizations','clients','locations','pricing','orders','dispatch','drivers',
                'routes','custody','incidents','finance','allies','notifications','reporting','platform','security','extensions')
              AND has_schema_privilege('paqueteria_master_data_loader', n.oid, 'CREATE')
          ) OR EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN ('identity','organizations','clients','locations','pricing','orders','dispatch','drivers',
                'routes','custody','incidents','finance','allies','notifications','reporting','platform','extensions')
              AND has_schema_privilege('paqueteria_master_data_loader', n.oid, 'USAGE')
          ) OR NOT has_schema_privilege('paqueteria_master_data_loader', 'security', 'USAGE')
          THEN
            RAISE EXCEPTION 'MDM-001 schema privileges differ from the MDM-001 contract';
          END IF;

          IF pg_has_role('paqueteria_app', 'paqueteria_master_data_executor', 'MEMBER')
             OR pg_has_role('paqueteria_worker', 'paqueteria_master_data_executor', 'MEMBER')
             OR pg_has_role('paqueteria_app', 'paqueteria_master_data_loader', 'MEMBER')
             OR pg_has_role('paqueteria_worker', 'paqueteria_master_data_loader', 'MEMBER')
             OR pg_has_role('paqueteria_master_data_loader', 'paqueteria_master_data_executor', 'MEMBER')
             OR EXISTS (
               SELECT 1 FROM pg_auth_members m
               WHERE m.roleid = executor AND NOT pg_has_role(m.member, 'paqueteria_migrator', 'MEMBER'))
             OR EXISTS (
               SELECT 1 FROM pg_auth_members m
               WHERE m.roleid = loader AND (m.inherit_option OR m.set_option)
                 AND pg_has_role(m.member, 'paqueteria_migrator', 'MEMBER'))
             OR (SELECT count(*) FROM pg_proc WHERE proowner=executor) <> 1
          THEN
            RAISE EXCEPTION 'MDM-001 ownership or membership differs from the MDM-001 contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// MDM-001 rollback: removes the loader function, so no further master-data load is possible. Rows it
    /// already loaded are legitimate master data referenced by quotes, orders and assignments, and its audit
    /// rows are append-only, so both stay. The first published jsonb overload is dropped too (MDM-001 N1).
    /// The two roles, their grants and the deployment marker stay too
    /// (MDM-001 m7): on fresh installations AI-06 and AI-18 own them, a rollback must not make an installation
    /// differ from the baseline it was built from, and without the function they are inert (the executor
    /// owns nothing and no login can reach it). Re-applying the lane restores the function.
    /// </summary>
    public const string DownSql =
        """
        RESET ROLE;
        DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,json,bytea,boolean);
        DROP FUNCTION IF EXISTS security.load_master_data(uuid,uuid,jsonb,bytea,boolean);
        SET LOCAL ROLE paqueteria_migrator;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
