\set ON_ERROR_STOP on

DO $ops002$
DECLARE
  history_count integer;
  invalid_history_count integer;
BEGIN
  IF (SELECT count(*) FROM organizations.organizations) <> 2 OR
     (SELECT count(*) FROM identity.users) <> 2 OR
     (SELECT count(*) FROM organizations.organization_memberships) <> 2 OR
     (SELECT count(*) FROM locations.cities) <> 1 OR
     (SELECT count(*) FROM locations.service_areas) <> 1 OR
     (SELECT count(*) FROM locations.operating_zones) <> 1 OR
     (SELECT count(*) FROM locations.locations) <> 4 OR
     (SELECT count(*) FROM pricing.tariff_rules) <> 1 OR
     (SELECT policy_version FROM pricing.tariff_rules) IS DISTINCT FROM 'ops002-synthetic-v1' OR
     (SELECT count(*) FROM pricing.quotes) <> 2 OR
     (SELECT count(*) FROM orders.orders) <> 2 OR
     (SELECT count(*) FROM orders.order_events WHERE order_id='10000000-0000-4000-8000-000000000010') <> 9 OR
     (SELECT count(*) FROM orders.order_acceptances) <> 1 OR
     (SELECT count(*) FROM dispatch.assignments) <> 1 OR
     (SELECT count(*) FROM custody.proof_upload_sessions) <> 2 OR
     (SELECT count(*) FROM custody.proofs) <> 2 OR
     (SELECT count(*) FROM orders.public_tracking_tokens) <> 1 OR
     (SELECT count(*) FROM drivers.driver_profiles) <> 1 OR
     (SELECT count(*) FROM drivers.driver_documents) <> 1 OR
     (SELECT count(*) FROM drivers.driver_positions) <> 1 OR
     (SELECT count(*) FROM platform.audit_logs) <> 2 OR
     (SELECT count(*) FROM platform.outbox_events WHERE status='PROCESSED') <> 1 OR
     (SELECT count(*) FROM platform.outbox_events WHERE status='DEAD' AND last_error='OPS002_SYNTHETIC_ALLOWLISTED') <> 1 OR
     (SELECT count(*) FROM platform.location_outbox_events WHERE status='PROCESSED') <> 1 OR
     NOT EXISTS (
       SELECT 1 FROM orders.orders
       WHERE id='10000000-0000-4000-8000-000000000010'
         AND owner_org_id='10000000-0000-4000-8000-000000000001'
         AND status='DELIVERED' AND version=9
         AND created_at='2026-01-01T00:01:00Z'
         AND updated_at='2026-01-01T00:09:00Z'
     ) OR
     NOT EXISTS (
       SELECT 1 FROM orders.orders
       WHERE id='20000000-0000-4000-8000-000000000010'
         AND owner_org_id='20000000-0000-4000-8000-000000000001'
         AND status='DELIVERED' AND version=1
     ) OR
     NOT EXISTS (
       SELECT 1 FROM orders.order_acceptances
       WHERE id='10000000-0000-4000-8000-000000000011'
         AND order_id='10000000-0000-4000-8000-000000000010'
         AND acceptance_channel='API'
         AND accepted_at_client='2026-01-01T00:00:59Z'
         AND recorded_at_server='2026-01-01T00:01:00Z'
     ) OR
     (SELECT array_agg(event_type ORDER BY aggregate_version)
      FROM orders.order_events
      WHERE order_id='10000000-0000-4000-8000-000000000010') <>
       ARRAY[
         'ORDER_CREATED','ORDER_CONFIRMED','ORDER_ASSIGNED',
         'ARRIVED_AT_PICKUP','ORDER_PICKED_UP','ORDER_IN_TRANSIT',
         'ORDER_DELIVERING','DELIVERY_CONFIRMED','ORDER_DELIVERED'
       ] OR
     NOT EXISTS (
       SELECT 1 FROM drivers.driver_profiles
       WHERE id='10000000-0000-4000-8000-000000000022'
         AND driver_type='OWN' AND status='ACTIVE'
     ) OR
     NOT EXISTS (
       SELECT 1 FROM drivers.driver_documents
       WHERE id='10000000-0000-4000-8000-000000000023'
         AND document_type='IDENTITY' AND status='VALID'
     ) OR
     NOT EXISTS (
       SELECT 1 FROM dispatch.assignments
       WHERE id='10000000-0000-4000-8000-000000000024'
         AND assignment_type='OWN' AND status='COMPLETED'
         AND accepted_at='2026-01-01T00:02:00Z'
     ) OR
     (SELECT array_agg(status ORDER BY id)
      FROM custody.proof_upload_sessions) <>
       ARRAY['CONSUMED','CONSUMED'] OR
     (SELECT array_agg(proof_type ORDER BY id)
      FROM custody.proofs) <>
       ARRAY['PICKUP_PHOTO','DELIVERY_PHOTO'] OR
     (SELECT count(DISTINCT object_key) FROM custody.proofs) <> 2 OR
     (SELECT count(DISTINCT sha256) FROM custody.proofs) <> 2 OR
     NOT EXISTS (
       SELECT 1 FROM orders.public_tracking_tokens
       WHERE id='10000000-0000-4000-8000-000000000021'
         AND octet_length(token_hash)=32
         AND token_hash=decode(repeat('40',32),'hex')
     ) OR
     (SELECT array_agg(action ORDER BY occurred_at)
      FROM platform.audit_logs) <>
       ARRAY['PICKUP_PROOF_FINALIZED','DELIVERY_PROOF_FINALIZED'] THEN
    RAISE EXCEPTION 'OPS002_DATA_ASSERTION_FAILED';
  END IF;

  SELECT count(*),count(*) FILTER (WHERE row_count < 1)
  INTO history_count,invalid_history_count
  FROM (VALUES
    ((SELECT count(*) FROM platform.__ef_migrations_history_custody)),
    ((SELECT count(*) FROM platform.__ef_migrations_history_dispatch)),
    ((SELECT count(*) FROM platform.__ef_migrations_history_drivers)),
    ((SELECT count(*) FROM platform.__ef_migrations_history_identity)),
    ((SELECT count(*) FROM platform.__ef_migrations_history_locations)),
    ((SELECT count(*) FROM platform.__ef_migrations_history_notifications)),
    ((SELECT count(*) FROM platform.__ef_migrations_history_orders)),
    ((SELECT count(*) FROM platform.__ef_migrations_history_organizations)),
    ((SELECT count(*) FROM platform.__ef_migrations_history_pricing))
  ) histories(row_count);
  IF history_count <> 9 OR invalid_history_count <> 0 THEN
    RAISE EXCEPTION 'OPS002_MIGRATION_HISTORY_ASSERTION_FAILED';
  END IF;

  IF current_setting('server_version_num')::integer < 180000 OR
     (SELECT extversion FROM pg_extension WHERE extname='postgis') NOT LIKE '3.6%' OR
     (SELECT extnamespace::regnamespace::text FROM pg_extension WHERE extname='postgis') <> 'public' OR
     (SELECT extnamespace::regnamespace::text FROM pg_extension WHERE extname='pgcrypto') <> 'extensions' THEN
    RAISE EXCEPTION 'OPS002_EXTENSION_ASSERTION_FAILED';
  END IF;

  IF EXISTS (
    SELECT 1
    FROM pg_roles
    WHERE rolname IN ('paqueteria_migrator','paqueteria_app','paqueteria_worker')
      AND (rolcanlogin OR rolbypassrls)
  ) OR
  (SELECT count(*) FROM pg_roles WHERE rolname IN (
    'paqueteria_migrator','paqueteria_app','paqueteria_worker',
    'paqueteria_bootstrap','paqueteria_outbox_executor','paqueteria_maintenance')) <> 6 THEN
    RAISE EXCEPTION 'OPS002_ROLE_FLAG_ASSERTION_FAILED';
  END IF;

  IF EXISTS (
    SELECT 1
    FROM pg_class c
    JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname IN (
      'identity','organizations','clients','locations','pricing','orders','dispatch',
      'drivers','routes','custody','incidents','finance','allies','notifications',
      'reporting','platform','security')
      AND c.relkind IN ('r','p','v','m','S')
      AND pg_get_userbyid(c.relowner) <> 'paqueteria_migrator'
  ) THEN
    RAISE EXCEPTION 'OPS002_OWNERSHIP_ASSERTION_FAILED';
  END IF;

  IF has_table_privilege('paqueteria_app','platform.outbox_events','SELECT') OR
     has_table_privilege('paqueteria_worker','platform.outbox_events','UPDATE') OR
     NOT has_table_privilege('paqueteria_app','platform.outbox_events','INSERT') OR
     has_function_privilege('paqueteria_worker','security.claim_outbox(text,integer,interval)','EXECUTE') OR
     NOT has_function_privilege('paqueteria_worker','security.claim_realtime_outbox(text,integer,interval)','EXECUTE') OR
     NOT has_function_privilege('paqueteria_worker','security.claim_notifications_outbox(text,integer,interval)','EXECUTE') OR
     NOT has_function_privilege('paqueteria_worker','security.claim_unowned_outbox(text,integer,interval)','EXECUTE') THEN
    RAISE EXCEPTION 'OPS002_GRANT_ASSERTION_FAILED';
  END IF;

  IF EXISTS (
    SELECT 1
    FROM pg_class c
    JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname IN (
      'identity','organizations','clients','locations','pricing','orders','dispatch',
      'drivers','routes','custody','incidents','finance','allies','notifications',
      'reporting','platform')
      AND c.relkind='r'
      AND c.relname <> 'cities'
      AND c.relname NOT LIKE '__ef_migrations_history_%'
      AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity)
  ) THEN
    RAISE EXCEPTION 'OPS002_FORCE_RLS_ASSERTION_FAILED';
  END IF;
END
$ops002$;

BEGIN;
SET LOCAL ROLE paqueteria_app;
SELECT set_config('app.current_user','10000000-0000-4000-8000-000000000002',true);
SELECT set_config('app.current_org_ids','{10000000-0000-4000-8000-000000000001}',true);
DO $ops002$
BEGIN
  IF (SELECT count(*) FROM orders.orders) <> 1 OR
     EXISTS (
       SELECT 1 FROM orders.orders
       WHERE owner_org_id='20000000-0000-4000-8000-000000000001'
     ) THEN
    RAISE EXCEPTION 'OPS002_CROSS_TENANT_ASSERTION_FAILED';
  END IF;
END
$ops002$;
COMMIT;

BEGIN;
SET LOCAL ROLE paqueteria_app;
DO $ops002$
BEGIN
  IF (SELECT count(*) FROM orders.orders) <> 0 THEN
    RAISE EXCEPTION 'OPS002_MISSING_CONTEXT_MUST_FAIL_CLOSED';
  END IF;
END
$ops002$;
COMMIT;

BEGIN;
SET LOCAL ROLE paqueteria_app;
SELECT set_config('app.current_user','20000000-0000-4000-8000-000000000002',true);
SELECT set_config('app.current_org_ids','{20000000-0000-4000-8000-000000000001}',true);
DO $ops002$
BEGIN
  IF (SELECT count(*) FROM orders.orders) <> 1 OR
     EXISTS (
       SELECT 1 FROM orders.orders
       WHERE owner_org_id='10000000-0000-4000-8000-000000000001'
     ) THEN
    RAISE EXCEPTION 'OPS002_CONTEXT_RESET_ASSERTION_FAILED';
  END IF;
END
$ops002$;
COMMIT;

SELECT 'OPS002_ASSERTIONS_OK';
