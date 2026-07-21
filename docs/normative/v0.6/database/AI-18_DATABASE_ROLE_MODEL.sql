-- AI-18_DATABASE_ROLE_MODEL.sql v0.6
-- Run after AI-06 with privileged deployment credentials.
-- Login bindings and passwords are provisioned only by IaC/secret manager.

DO $$ BEGIN CREATE ROLE paqueteria_migrator NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_app NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_worker NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_bootstrap NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_outbox_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_maintenance NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Normalize ownership after AI-06 was applied by the privileged deployment owner.
DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY[
    'extensions','identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
    'custody','incidents','finance','allies','notifications','reporting','platform','security'
  ] LOOP
    EXECUTE format('ALTER SCHEMA %I OWNER TO paqueteria_migrator',s);
  END LOOP;
END $$;

DO $$
DECLARE o record;
BEGIN
  FOR o IN
    SELECT n.nspname AS schema_name,c.relname,c.relkind
    FROM pg_class c
    JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname=ANY(ARRAY[
      'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
      'custody','incidents','finance','allies','notifications','reporting','platform','security'
    ])
      AND c.relkind IN ('r','p','S')
  LOOP
    IF o.relkind='S' THEN
      EXECUTE format('ALTER SEQUENCE %I.%I OWNER TO paqueteria_migrator',o.schema_name,o.relname);
    ELSE
      EXECUTE format('ALTER TABLE %I.%I OWNER TO paqueteria_migrator',o.schema_name,o.relname);
    END IF;
  END LOOP;
END $$;

DO $$
DECLARE o record;
BEGIN
  FOR o IN
    SELECT n.nspname AS schema_name,p.proname,pg_get_function_identity_arguments(p.oid) AS args
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid=p.pronamespace
    WHERE n.nspname IN ('platform','security')
  LOOP
    EXECUTE format('ALTER FUNCTION %I.%I(%s) OWNER TO paqueteria_migrator',o.schema_name,o.proname,o.args);
  END LOOP;
END $$;

-- Runtime roles cannot inherit or SET ROLE into privileged roles.
REVOKE paqueteria_migrator, paqueteria_bootstrap, paqueteria_outbox_executor, paqueteria_maintenance
  FROM paqueteria_app, paqueteria_worker;

REVOKE ALL ON SCHEMA extensions FROM PUBLIC;
GRANT USAGE ON SCHEMA extensions TO paqueteria_bootstrap;
REVOKE EXECUTE ON FUNCTION extensions.digest(bytea,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION extensions.digest(bytea,text) TO paqueteria_bootstrap;

-- PostGIS intentionally remains in public. Runtime may resolve spatial types/operators but cannot create public objects.
REVOKE CREATE ON SCHEMA public FROM PUBLIC,paqueteria_app,paqueteria_worker;
GRANT USAGE ON SCHEMA public TO paqueteria_app,paqueteria_worker;

-- Schemas: one business schema per normative module plus platform/security.
DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY[
    'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
    'custody','incidents','finance','allies','notifications','reporting','platform','security'
  ] LOOP
    EXECUTE format('REVOKE ALL ON SCHEMA %I FROM PUBLIC',s);
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO paqueteria_app,paqueteria_worker',s);
  END LOOP;
END $$;

GRANT USAGE ON SCHEMA identity,organizations,orders,security TO paqueteria_bootstrap;
GRANT USAGE ON SCHEMA platform,security TO paqueteria_outbox_executor,paqueteria_maintenance;

-- Runtime table grants. RLS remains authoritative and FORCEd.
DO $$
DECLARE s text;
BEGIN
  FOREACH s IN ARRAY ARRAY[
    'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
    'custody','incidents','finance','allies','notifications','reporting'
  ] LOOP
    EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA %I TO paqueteria_app',s);
    EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA %I TO paqueteria_worker',s);
    EXECUTE format('GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA %I TO paqueteria_app,paqueteria_worker',s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE paqueteria_migrator IN SCHEMA %I GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO paqueteria_app,paqueteria_worker',s);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE paqueteria_migrator IN SCHEMA %I GRANT USAGE,SELECT ON SEQUENCES TO paqueteria_app,paqueteria_worker',s);
  END LOOP;
END $$;

-- Platform schema uses explicit least-privilege grants; no broad default table grants.
GRANT SELECT,INSERT ON platform.audit_logs TO paqueteria_app,paqueteria_worker;
GRANT SELECT,INSERT,UPDATE,DELETE ON platform.idempotency_keys TO paqueteria_app,paqueteria_worker;

-- Global geographic references are read-only at runtime.
REVOKE INSERT,UPDATE,DELETE ON locations.cities FROM paqueteria_app,paqueteria_worker;

-- Append-only records: runtime may insert/read within tenant context, never update/delete.
REVOKE UPDATE,DELETE ON
  orders.order_events,
  orders.order_acceptances,
  custody.proofs,
  platform.audit_logs
FROM paqueteria_app,paqueteria_worker;

-- Outbox producers must supply all values and emit INSERT without RETURNING.
-- Runtime roles cannot inspect or mutate lifecycle rows directly.
REVOKE SELECT,UPDATE,DELETE ON platform.outbox_events,platform.location_outbox_events
  FROM paqueteria_app,paqueteria_worker;
GRANT INSERT ON platform.outbox_events,platform.location_outbox_events
  TO paqueteria_app,paqueteria_worker;

-- Bootstrap role owns only two auditable data-access functions and has column-limited reads.
GRANT SELECT (id,identity_subject,status) ON identity.users TO paqueteria_bootstrap;
GRANT SELECT (id,user_id,organization_id,role,status,is_default)
  ON organizations.organization_memberships TO paqueteria_bootstrap;
GRANT SELECT (id,order_id,token_hash,expires_at,revoked_at)
  ON orders.public_tracking_tokens TO paqueteria_bootstrap;
GRANT SELECT (id,public_id,status) ON orders.orders TO paqueteria_bootstrap;
GRANT SELECT (order_id,public_event_code,occurred_at) ON orders.order_events TO paqueteria_bootstrap;

ALTER FUNCTION security.resolve_identity_context(text) OWNER TO paqueteria_bootstrap;
ALTER FUNCTION security.get_public_tracking_projection(text) OWNER TO paqueteria_bootstrap;
REVOKE ALL ON FUNCTION security.resolve_identity_context(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.get_public_tracking_projection(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.resolve_identity_context(text) TO paqueteria_app;
GRANT EXECUTE ON FUNCTION security.get_public_tracking_projection(text) TO paqueteria_app;
ALTER FUNCTION security.map_public_order_status(text) OWNER TO paqueteria_migrator;
REVOKE ALL ON FUNCTION security.map_public_order_status(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.map_public_order_status(text) TO paqueteria_app,paqueteria_worker,paqueteria_bootstrap;

-- Active lifecycle: claim, settle and stale requeue. The Worker credential itself cannot bypass RLS.
GRANT SELECT,UPDATE ON platform.outbox_events,platform.location_outbox_events TO paqueteria_outbox_executor;

ALTER FUNCTION security.claim_outbox(text,integer,interval) OWNER TO paqueteria_outbox_executor;
ALTER FUNCTION security.settle_outbox(uuid,uuid,text,text,timestamptz) OWNER TO paqueteria_outbox_executor;
ALTER FUNCTION security.requeue_stale_outbox(interval,integer,integer) OWNER TO paqueteria_outbox_executor;
ALTER FUNCTION security.claim_location_outbox(text,integer,interval) OWNER TO paqueteria_outbox_executor;
ALTER FUNCTION security.settle_location_outbox(uuid,uuid,text,text,timestamptz) OWNER TO paqueteria_outbox_executor;
ALTER FUNCTION security.requeue_stale_location_outbox(interval,integer,integer) OWNER TO paqueteria_outbox_executor;

REVOKE ALL ON FUNCTION security.claim_outbox(text,integer,interval) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.settle_outbox(uuid,uuid,text,text,timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.requeue_stale_outbox(interval,integer,integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.claim_location_outbox(text,integer,interval) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.settle_location_outbox(uuid,uuid,text,text,timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.requeue_stale_location_outbox(interval,integer,integer) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION security.claim_outbox(text,integer,interval) TO paqueteria_worker;
GRANT EXECUTE ON FUNCTION security.settle_outbox(uuid,uuid,text,text,timestamptz) TO paqueteria_worker;
GRANT EXECUTE ON FUNCTION security.requeue_stale_outbox(interval,integer,integer) TO paqueteria_worker;
GRANT EXECUTE ON FUNCTION security.claim_location_outbox(text,integer,interval) TO paqueteria_worker;
GRANT EXECUTE ON FUNCTION security.settle_location_outbox(uuid,uuid,text,text,timestamptz) TO paqueteria_worker;
GRANT EXECUTE ON FUNCTION security.requeue_stale_location_outbox(interval,integer,integer) TO paqueteria_worker;

-- Retention is a separate security capability from message processing.
-- ADR-025 addendum/ADR-030 restrict maintenance to old terminal outbox rows.
GRANT SELECT,DELETE ON platform.outbox_events,platform.location_outbox_events TO paqueteria_maintenance;
ALTER FUNCTION security.purge_outbox(timestamptz,timestamptz,integer,boolean) OWNER TO paqueteria_maintenance;
ALTER FUNCTION security.purge_location_outbox(timestamptz,timestamptz,integer,boolean) OWNER TO paqueteria_maintenance;
REVOKE ALL ON FUNCTION security.purge_outbox(timestamptz,timestamptz,integer,boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.purge_location_outbox(timestamptz,timestamptz,integer,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.purge_outbox(timestamptz,timestamptz,integer,boolean) TO paqueteria_worker;
GRANT EXECUTE ON FUNCTION security.purge_location_outbox(timestamptz,timestamptz,integer,boolean) TO paqueteria_worker;

-- Mandatory deployment assertions:
-- 1. API login SET ROLE paqueteria_app; Worker login SET ROLE paqueteria_worker.
-- 2. all application schemas/tables/sequences are owned by paqueteria_migrator before specialized function ownership; runtime roles own nothing and rolbypassrls=false.
-- 3. privileged NOLOGIN roles have no login membership granted to runtime roles.
-- 4. runtime roles have no SELECT/UPDATE/DELETE on either outbox table.
-- 5. only paqueteria_outbox_executor owns claim/settle/requeue functions.
-- 6. only paqueteria_maintenance owns purge functions and it has no rights outside the two outbox tables.
-- 7. every tenant transaction uses parameterized set_config(..., true) after BEGIN.
-- 8. bootstrap/tracking functions execute successfully in real PostgreSQL and do not enumerate missing/expired/foreign tokens.
