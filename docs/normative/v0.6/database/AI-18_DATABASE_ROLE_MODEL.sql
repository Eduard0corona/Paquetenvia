-- AI-18_DATABASE_ROLE_MODEL.sql v0.6
-- Run after AI-06 with privileged deployment credentials.
-- Login bindings and passwords are provisioned only by IaC/secret manager.

DO $$ BEGIN CREATE ROLE paqueteria_migrator NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_app NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_worker NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_bootstrap NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_outbox_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_maintenance NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_lifecycle_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_cleanup_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_registration_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_session_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_master_data_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_master_data_loader NOLOGIN NOBYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_operator_outbox_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN CREATE ROLE paqueteria_auto_close_executor NOLOGIN BYPASSRLS; EXCEPTION WHEN duplicate_object THEN NULL; END $$;

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

-- BFF-SESSION-TABLE-SHAPE and BFF-LOGOUT-JTI-PERSISTENCE: the pre-tenant BFF session and logout-jti
-- tables hold no runtime grant at all; the API reaches them only through the SECURITY DEFINER
-- functions of paqueteria_session_executor.
REVOKE ALL ON identity.bff_sessions FROM paqueteria_app,paqueteria_worker;
REVOKE ALL ON identity.bff_logout_jtis FROM paqueteria_app,paqueteria_worker;

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
-- IDENTITY-ORG-ACTIVE-REQUIRED: identity resolution only counts memberships of ACTIVE organizations.
GRANT SELECT (id,status) ON organizations.organizations TO paqueteria_bootstrap;
GRANT SELECT (id,order_id,token_hash,expires_at,revoked_at)
  ON orders.public_tracking_tokens TO paqueteria_bootstrap;
GRANT SELECT (id,public_id,status,version) ON orders.orders TO paqueteria_bootstrap;
-- AI05-TIMELINE-ORDER: the public timeline is ordered by (occurred_at, aggregate_version).
GRANT SELECT (order_id,aggregate_version,public_event_code,occurred_at) ON orders.order_events TO paqueteria_bootstrap;

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

-- Order lifecycle finalization is a separate security capability (ADR-034, LIF-001).
-- The executor only discovers, locks and finalizes expired CLOSED claim windows inside
-- security.finalize_expired_orders(integer), installed by the Orders migration lane after this
-- baseline. It has no outbox, bootstrap or purge rights and no broad business-schema grant.
REVOKE paqueteria_lifecycle_executor FROM paqueteria_app, paqueteria_worker;
GRANT USAGE ON SCHEMA orders TO paqueteria_lifecycle_executor;
GRANT SELECT (id,status,claim_window_ends_at,finalized_at) ON orders.orders TO paqueteria_lifecycle_executor;
GRANT UPDATE (finalized_at) ON orders.orders TO paqueteria_lifecycle_executor;

-- D8 DISPATCH outbox lane (D8-DISPATCH-OUTBOX-CLOSURE, D8-OUTBOX-LANE-DISPATCH, project owner 2026-09-27).
-- The Notifications migration lane (20260927000200_AddDispatchOutboxLane) installs, after NTF-001 and
-- therefore after this baseline, security.claim_dispatch_outbox(text,integer,interval) and
-- security.requeue_stale_dispatch_outbox(interval,integer,integer): the REALTIME lane claim/requeue
-- bodies with the consumer literal 'DISPATCH'. It adds to security.resolve_outbox_consumer(text) the
-- branch dispatch.order-status-reaction-requested -> DISPATCH; orders.status-changed stays REALTIME.
-- Both functions are SECURITY DEFINER with search_path=pg_catalog, platform, security, pg_temp, are
-- owned by paqueteria_outbox_executor, have EXECUTE revoked from PUBLIC and granted only to
-- paqueteria_worker. Settlement reuses security.settle_outbox. No table grant changes.

-- Operational cleanup is a separate security capability (OPS-003-CLEANUP-ROLE, ADR-034 pattern).
-- The executor only discovers and removes expired idempotency keys older than the fixed 72-hour
-- floor (OPS-003-OFFLINE-72H) and marks expired proof upload sessions EXPIRED, inside
-- security.purge_expired_idempotency_keys(timestamptz,integer,boolean) and
-- security.expire_proof_upload_sessions(integer), installed by the Custody migration lane after this
-- baseline. Both return only a row count. It has no outbox, bootstrap, lifecycle or purge-outbox
-- rights and no broad business-schema grant; DELETE has no column form in PostgreSQL, so the
-- idempotency table is its only table-level grant.
-- BFF session purge (BFF-SESSION-TABLE-SHAPE, OPS-003-CLEANUP-ROLE): the Custody migration lane
-- (20260927000400_AddBffSessionPurge), after the OPS-003 lane, adds security.purge_bff_sessions(integer)
-- to this role together with USAGE on schema identity, SELECT(session_key_hash,expires_at,revoked_at) and
-- DELETE on identity.bff_sessions, and SELECT(jti_hash,expires_at) and DELETE on identity.bff_logout_jtis
-- (BFF-LOGOUT-JTI-PERSISTENCE). The function deletes only revoked sessions, sessions whose expires_at is
-- not after its own clock_timestamp() and, with what remains of the batch, logout jtis past their
-- retention, in batches of 1..1000 in total, returns only a count, is SECURITY DEFINER
-- with search_path=pg_catalog, identity, pg_temp and only paqueteria_worker may EXECUTE it. Those grants
-- live in that lane and not in this baseline, because the OPS-003 lane asserts its own exact grant set.
REVOKE paqueteria_cleanup_executor FROM paqueteria_app, paqueteria_worker;
GRANT USAGE ON SCHEMA platform,custody TO paqueteria_cleanup_executor;
GRANT SELECT (owner_org_id,scope,idempotency_key,created_at,expires_at) ON platform.idempotency_keys TO paqueteria_cleanup_executor;
GRANT DELETE ON platform.idempotency_keys TO paqueteria_cleanup_executor;
GRANT SELECT (id,status,expires_at) ON custody.proof_upload_sessions TO paqueteria_cleanup_executor;
GRANT UPDATE (status,updated_at) ON custody.proof_upload_sessions TO paqueteria_cleanup_executor;

-- Open registration is a separate security capability (REG-001: AUTH-OPEN-REGISTRATION,
-- REG-SELF-SERVICE-ORGANIZATION, REG-ONE-ORGANIZATION-PER-PERSON, REG-ALLY-APPROVAL-PATH,
-- REG-OWN-APPLICATIONS-ENDPOINT; ADR-034 pattern). The bootstrap role never writes, so the executor
-- owns the only pre-tenant and cross-tenant registration writes, inside five SECURITY DEFINER
-- functions installed by the Organizations migration lane (20260927000400_AddSelfServiceRegistration)
-- after this baseline, each with a pinned search_path starting with pg_catalog and ending with pg_temp,
-- EXECUTE revoked from PUBLIC and granted only to paqueteria_app:
--   security.register_identity_subject(text,uuid): first sign-in of a subject with a verified email,
--     exactly once per subject (UNIQUE(identity_subject) arbitrates concurrent sign-ins);
--   security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text): one BUSINESS
--     (ACTIVE) or ALLY (PENDING_APPROVAL), the creator's admin membership and its audit row;
--     organizations_one_open_self_service_uq enforces one organization not CLOSED per creator; a default
--     membership whose organization is not ACTIVE is released first (REG-DEFAULT-MEMBERSHIP-RELEASE);
--   security.list_own_organization_applications(uuid): the caller's own organizations and status;
--   security.list_pending_ally_organizations(uuid,uuid,integer) and
--   security.decide_ally_organization(uuid,uuid,uuid,boolean,text): only for an ACTIVE PLATFORM_ADMIN
--     member of an ACTIVE PLATFORM organization; approval only activates the ALLY, rejection closes it,
--     with one audit row in the platform organization and one in the ALLY organization.
-- It has no outbox, bootstrap, lifecycle, cleanup or purge rights, no USAGE on schema security and no
-- broad business-schema grant.
REVOKE paqueteria_registration_executor FROM paqueteria_app, paqueteria_worker;
GRANT USAGE ON SCHEMA identity,organizations,platform TO paqueteria_registration_executor;
GRANT SELECT (id,identity_subject,status) ON identity.users TO paqueteria_registration_executor;
GRANT INSERT (id,identity_subject,status,created_at) ON identity.users TO paqueteria_registration_executor;
GRANT SELECT (id,organization_type,legal_name,display_name,status,self_service_creator_user_id,created_at) ON organizations.organizations TO paqueteria_registration_executor;
GRANT INSERT (id,organization_type,legal_name,display_name,status,self_service_creator_user_id,created_at) ON organizations.organizations TO paqueteria_registration_executor;
GRANT UPDATE (status) ON organizations.organizations TO paqueteria_registration_executor;
GRANT SELECT (user_id,organization_id,role,status,is_default) ON organizations.organization_memberships TO paqueteria_registration_executor;
GRANT INSERT (id,user_id,organization_id,role,status,is_default,granted_at) ON organizations.organization_memberships TO paqueteria_registration_executor;
-- REG-DEFAULT-MEMBERSHIP-RELEASE: creation releases the caller's default membership in an organization that is
-- no longer ACTIVE, so the new organization can become the usable default.
GRANT UPDATE (is_default) ON organizations.organization_memberships TO paqueteria_registration_executor;
GRANT INSERT (id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at) ON platform.audit_logs TO paqueteria_registration_executor;

-- REG-002 (REG-JOIN-EXISTING-BY-EMAIL): the same executor owns four more SECURITY DEFINER functions of the
-- Organizations lane (20260927000500_AddPendingMemberships), EXECUTE only for paqueteria_app:
--   security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text): an ACTIVE PLATFORM_ADMIN,
--     ALLY_ADMIN or BUSINESS_ADMIN of the ACTIVE organization adds an email HMAC and a role within its
--     ceiling (REG-ROLE-CEILING); PLATFORM_ADMIN is grantable only inside an organization of type PLATFORM;
--   security.renew_pending_membership(uuid,uuid,uuid,text,text) and
--   security.revoke_pending_membership(uuid,uuid,uuid,text,text): same actor rule, same organization only;
--   security.apply_pending_memberships(text,bytea[],integer[]): at every sign-in with a verified email, each
--     PENDING unexpired entry of an ACTIVE organization matching the HMAC becomes a membership exactly once.
-- Runtime roles never write pending memberships directly: paqueteria_app keeps SELECT under RLS only and
-- paqueteria_worker has no privilege on the table.
REVOKE INSERT,UPDATE,DELETE ON organizations.pending_memberships FROM paqueteria_app;
REVOKE ALL ON organizations.pending_memberships FROM paqueteria_worker;
GRANT SELECT (id,organization_id,email_hmac,email_hmac_key_version,role,status,created_at,expires_at) ON organizations.pending_memberships TO paqueteria_registration_executor;
GRANT INSERT (id,organization_id,email_hmac,email_hmac_key_version,role,status,invited_by,created_at,expires_at) ON organizations.pending_memberships TO paqueteria_registration_executor;
GRANT UPDATE (status,expires_at,accepted_user_id,accepted_at,revoked_at) ON organizations.pending_memberships TO paqueteria_registration_executor;
-- BFF session store is a separate security capability (BFF-SESSION-TABLE-SHAPE, BFF-SESSION-STORE-POSTGRESQL).
-- paqueteria_session_executor owns only the SECURITY DEFINER functions the Identity migration lane
-- (20260927000400_AddBffSessionStore) installs after this baseline, each with
-- search_path=pg_catalog, identity, pg_temp, EXECUTE revoked from PUBLIC and granted only to paqueteria_app:
--   security.create_bff_session(bytea,text,text,bytea,timestamptz) inserts one session (SHA-256 key hash,
--     AuthCenter subject and sid, Data Protection ticket, expiry at most 24 hours ahead) and returns nothing;
--   security.resolve_bff_session(bytea) returns the ticket of a live session (not revoked, not expired) or NULL;
--   security.revoke_bff_session(bytea) revokes one session by key hash (logout, session replacement);
--   security.revoke_bff_session(text) revokes every live session of an AuthCenter sid (back-channel logout);
--   security.revoke_bff_session(text,timestamptz) revokes every live session of a subject created at or
--     before the given moment, never after the function's own clock (sub-only back-channel logout);
--   security.register_bff_logout_jti(bytea,timestamptz) records the SHA-256 of a back-channel logout_token
--     jti until its retention (exp + 5 minutes, at most one day ahead) with INSERT ... ON CONFLICT DO
--     NOTHING and returns true only the first time any replica registers it (BFF-LOGOUT-JTI-PERSISTENCE).
-- Revocation sets revoked_at and erases ticket_ciphertext; the revoke functions return only a count.
-- The role has no DELETE: revoked and expired rows are purged by paqueteria_cleanup_executor.
REVOKE paqueteria_session_executor FROM paqueteria_app, paqueteria_worker;
GRANT USAGE ON SCHEMA identity TO paqueteria_session_executor;
GRANT SELECT (session_key_hash,identity_subject,authcenter_sid,ticket_ciphertext,created_at,expires_at,revoked_at) ON identity.bff_sessions TO paqueteria_session_executor;
GRANT INSERT (session_key_hash,identity_subject,authcenter_sid,ticket_ciphertext,created_at,expires_at) ON identity.bff_sessions TO paqueteria_session_executor;
GRANT UPDATE (ticket_ciphertext,revoked_at) ON identity.bff_sessions TO paqueteria_session_executor;
GRANT INSERT (jti_hash,created_at,expires_at) ON identity.bff_logout_jtis TO paqueteria_session_executor;

-- Pilot master data is a separate security capability (MDM-001-OPERATOR-LOADER; ADR-034 pattern). Cities,
-- service areas, operating zones, tariff rules and driver profiles are loaded by an operator job from a
-- reviewed file, never through a public API. paqueteria_master_data_executor owns only
-- security.load_master_data(uuid,uuid,json,bytea,boolean), installed by the Pricing migration lane
-- (20260928000100_AddMasterDataLoader) after this baseline, SECURITY DEFINER with
-- search_path=pg_catalog, pg_temp (PostGIS is schema-qualified as public.*), EXECUTE revoked from PUBLIC and
-- granted only to paqueteria_master_data_loader.
-- paqueteria_master_data_loader is a platform-operator capability, not a tenant boundary: its caller names
-- the organization and also sets it as app.current_org_ids (the function refuses any other context), so a
-- mistyped organization fails instead of loading elsewhere; the audit row names the operator by a
-- pseudonymous operator_ref, never the login itself or anything derived from it, because tenants can read
-- their audit rows (a random uuid per login since the MDM-001 hardening step below; rows written before it
-- keep the former SHA-256 of 'paquetenvia.mdm-001.operator:' || login, as audit rows are append-only).
-- It is NOLOGIN NOBYPASSRLS with USAGE on schema security and that EXECUTE only, and its only members are named operator LOGINs (NOINHERIT, SET ROLE per transaction)
-- that the owner creates and removes; it is never granted to paqueteria_app, paqueteria_worker or
-- paqueteria_bootstrap, no member that can use it (INHERIT or SET) may also be a member of
-- paqueteria_migrator, and only deployment principals (members of paqueteria_migrator) may hold
-- paqueteria_master_data_executor.
-- platform.master_data_deployment_gate forces RLS; its single policy admits only paqueteria_migrator, the
-- owner that master-data-gate writes it as (each change is audited against the PLATFORM organization, and
-- the command never turns a REAL database back into a SYNTHETIC one).
-- The function enforces GATE-007 from platform.master_data_deployment_gate (REAL unless the migrator marked
-- the database SYNTHETIC): a SYNTHETIC database takes only SYNTHETIC files, a REAL one never takes them, and
-- driver profiles load only in a SYNTHETIC database or once the migrator recorded gate_007_closed. It
-- validates the whole document (exact keys without duplicates, integer cents from the raw json token,
-- MultiPolygon validity, range and vertex budget, zone within its service area, references to ACTIVE
-- cities, natural-key uniqueness, no overlapping ACTIVE tariff windows) before any write, writes
-- idempotently by natural key under advisory locks, creates cities (ACTIVE, Mexican IANA zones only) only
-- for a PLATFORM organization and never rewrites one, never rewrites the amount or tax mode of an existing
-- tariff rule, requires an ACTIVE DRIVER membership for a driver profile, writes one append-only audit row
-- per load and, in a dry run, writes nothing. The runtime roles gain no privilege.
-- PRC-POLICY-VERSION-PER-ORG: the Pricing lane step 20260928000300_StoreTariffPolicyVersionInMasterDataLoader
-- also grants the executor SELECT (policy_version) and INSERT (policy_version) on pricing.tariff_rules (never
-- UPDATE) and makes the function store each rule's policy_version and refuse to change a stored one. Those two
-- grants are owned by that lane, not declared here, because the MDM-001 lane step, which runs first on every
-- installation, verifies exactly the grants below; the lane's rollback revokes them.
-- MDM-001-LOADER-HARDENING: the Pricing lane step 20260928000400_HardenMasterDataLoaderOperatorBoundary makes
-- the function refuse, at every call, a session whose login is a member of paqueteria_migrator
-- (MDM001_DEPLOYMENT_PRINCIPAL_REFUSED; a member holding only ADMIN on the loader could otherwise grant itself
-- SET), and replaces the hashed operator_ref with a random uuid per login kept in
-- platform.master_data_operator_refs (operator_login text PRIMARY KEY, operator_ref uuid UNIQUE, created_at):
-- owned by paqueteria_migrator, ENABLE and FORCE ROW LEVEL SECURITY with the single policy
-- master_data_operator_refs_migrator for paqueteria_migrator, nothing for PUBLIC or the runtime roles, and
-- SELECT and INSERT on (operator_login, operator_ref) for the executor only (no UPDATE, no DELETE). The table
-- and those four grants are owned by that lane step for the same reason as the policy_version grants; its
-- rollback revokes the grants and keeps the table, which resolves the pseudonyms of append-only audit rows.
-- master-data-gate records the deployment login's reference from the same table as paqueteria_migrator and
-- serializes its runs on pg_advisory_xact_lock(2026092803) before reading the deployment marker.
REVOKE paqueteria_master_data_executor FROM paqueteria_app, paqueteria_worker;
REVOKE paqueteria_master_data_loader FROM paqueteria_app, paqueteria_worker;
REVOKE ALL ON platform.master_data_deployment_gate FROM paqueteria_app, paqueteria_worker;
CREATE POLICY master_data_deployment_gate_migrator ON platform.master_data_deployment_gate
  TO paqueteria_migrator USING (true) WITH CHECK (true);
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
GRANT USAGE ON SCHEMA security TO paqueteria_master_data_loader;

-- Operator actions on another owner's order are a separate security capability
-- (DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03, project owner: "Función segura a nombre del dueño";
-- "Sí, el dueño lo ve"; AI-03 §24.3). When the operator organization of an order (orders.operator_org_id,
-- distinct from owner_org_id) assigns its own driver (DSP-002), its transaction carries only the operator in
-- app.current_org_ids, so outbox_tenant and audit_logs_tenant (AI-06) refuse the owner-tagged rows the owner's
-- operations audience needs. paqueteria_operator_outbox_executor owns the only path that writes them: two
-- SECURITY DEFINER functions installed by the Dispatch migration lane (20261003000100_AddOperatorOwnerOutboxExecutor)
-- after this baseline, each with search_path=pg_catalog, pg_temp, EXECUTE revoked from PUBLIC and granted only
-- to paqueteria_app:
--   security.append_operator_order_outbox(uuid,uuid,jsonb,text,text,uuid,integer,jsonb,smallint,timestamptz,timestamptz)
--     inserts one platform.outbox_events row (orders.status-changed, orders.timeline-event-added or
--     dispatch.assignment-changed, aggregate Order, priority 50, PENDING, tenant_context exactly the owner);
--   security.append_operator_order_audit(uuid,uuid,uuid,text,text,uuid,text,jsonb,timestamptz)
--     inserts one platform.audit_logs row (ASSIGNMENT_CREATED/Assignment or ORDER_STATUS_CHANGED/Order) for
--     the owner organization and the calling actor.
-- Each runs inside the caller's transaction, takes every inserted value from the caller (no default, no
-- RETURNING) and writes only after proving in the same call that app.current_org_ids is exactly one
-- organization, that it is the order's operator_org_id and differs from owner_org_id, that the given owner is
-- the order owner, that app.current_user_id is an ACTIVE user with an ACTIVE DISPATCHER or PLATFORM_ADMIN
-- membership of the operator, that the order is ASSIGNED and its ORDER_STATUS_CHANGED event at the current
-- version was written by that actor for that operator and names an ACCEPTED OWN assignment of the order whose
-- operator is the caller, that topic or action, audience, payload keys and identifiers match that evidence, and
-- that the same row was not already written for that order version or assignment; anything else raises 42501
-- and writes nothing. When the owner itself acts, the runtime keeps inserting directly under RLS. The executor
-- has no outbox lifecycle (UPDATE/DELETE), bootstrap, lifecycle, cleanup, registration, session, master data or
-- purge right, no USAGE on schema security and no broad business-schema grant; its outbox and audit SELECT is
-- limited to the columns that prove a row is not repeated. The functions' rollback drops only them; the role and
-- these grants then stay inert.
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

-- Automatic close of delivered orders is a separate security capability (ORD-AUTO-CLOSE-2026-10-10, project owner:
-- "Sí, que se cierre sola"; ADR-025 and ADR-034 pattern). A Worker job closes every DELIVERED order whose CLOSED
-- guards hold through the ORD-002 transition, in a tenant transaction of the owner organization only
-- (paqueteria_worker, set_config(..., true) after BEGIN), recorded with no actor and the reason "Cierre automático";
-- the guards are evaluated only by that transition. The Worker is NOBYPASSRLS, so it cannot know which organizations
-- to visit: paqueteria_auto_close_executor owns the only cross-tenant step,
-- security.list_auto_close_owner_organizations(uuid,integer), installed by the Orders migration lane
-- (20261010000100_AddOrderAutoCloseDiscovery) after this baseline, STABLE SECURITY DEFINER with
-- search_path=pg_catalog, orders, pg_temp, EXECUTE revoked from PUBLIC and granted only to paqueteria_worker. It
-- validates a limit of 1..1000 and returns, in ascending order and strictly after the given organization, only the
-- owner organization identifiers that hold at least one DELIVERED order: no order identifier, status, amount or guard
-- input, no write and no decision. The executor has no INSERT, UPDATE or DELETE, no outbox, bootstrap, lifecycle,
-- cleanup, registration, session, master data or purge right, no USAGE on schema security and no other grant. The
-- function's rollback drops only it; the role and these grants then stay inert.
REVOKE paqueteria_auto_close_executor FROM paqueteria_app, paqueteria_worker;
GRANT USAGE ON SCHEMA orders TO paqueteria_auto_close_executor;
GRANT SELECT (owner_org_id,status) ON orders.orders TO paqueteria_auto_close_executor;

-- Mandatory deployment assertions:
-- 1. API login SET ROLE paqueteria_app; Worker login SET ROLE paqueteria_worker.
-- 2. all application schemas/tables/sequences are owned by paqueteria_migrator before specialized function ownership; runtime roles own nothing and rolbypassrls=false.
-- 3. privileged NOLOGIN roles have no login membership granted to runtime roles.
-- 4. runtime roles have no SELECT/UPDATE/DELETE on either outbox table.
-- 5. only paqueteria_outbox_executor owns claim/settle/requeue functions.
-- 6. only paqueteria_maintenance owns purge functions and it has no rights outside the two outbox tables.
-- 7. every tenant transaction uses parameterized set_config(..., true) after BEGIN.
-- 8. bootstrap/tracking functions execute successfully in real PostgreSQL and do not enumerate missing/expired/foreign tokens.
-- 9. paqueteria_lifecycle_executor is NOLOGIN BYPASSRLS, inherits no role, is granted to no runtime role and owns only security.finalize_expired_orders(integer).
-- 10. paqueteria_lifecycle_executor holds only USAGE on schema orders plus SELECT(id,status,claim_window_ends_at,finalized_at) and UPDATE(finalized_at) on orders.orders: no INSERT/DELETE, no table-wide grant, no other table, no outbox, bootstrap or purge privilege.
-- 11. security.finalize_expired_orders(integer) is SECURITY DEFINER with search_path=pg_catalog, orders, pg_temp, accepts only a bounded batch size, and only paqueteria_worker may EXECUTE it; PUBLIC and paqueteria_app may not.
-- 12. once the D8 DISPATCH lane is recorded, security.claim_dispatch_outbox(text,integer,interval) and security.requeue_stale_dispatch_outbox(interval,integer,integer) exist, are owned by paqueteria_outbox_executor, are SECURITY DEFINER, and only paqueteria_worker may EXECUTE them; PUBLIC and paqueteria_app may not.
-- 13. paqueteria_cleanup_executor is NOLOGIN BYPASSRLS, inherits no role, is granted to no runtime role and owns only security.purge_expired_idempotency_keys(timestamptz,integer,boolean) and security.expire_proof_upload_sessions(integer), plus the BFF session purge of assertion 24.
-- 14. paqueteria_cleanup_executor holds only USAGE on schemas platform and custody, SELECT(owner_org_id,scope,idempotency_key,created_at,expires_at) plus DELETE on platform.idempotency_keys, and SELECT(id,status,expires_at) plus UPDATE(status,updated_at) on custody.proof_upload_sessions: no INSERT, no other table except the BFF session purge grants of assertion 24, no outbox, bootstrap, lifecycle or purge-outbox privilege.
-- 15. both cleanup functions are SECURITY DEFINER with search_path=pg_catalog, platform, pg_temp (idempotency) and pg_catalog, custody, pg_temp (sessions), accept only bounded batch sizes, return only a count, and only paqueteria_worker may EXECUTE them; PUBLIC and paqueteria_app may not.
-- 16. security.purge_expired_idempotency_keys never deletes a key created less than 72 hours before its own clock_timestamp() nor one that has not expired, whatever cutoff, batch size or mode it receives; dry-run mutates nothing.
-- 17. paqueteria_registration_executor is NOLOGIN BYPASSRLS, inherits no role, is granted to no runtime role and owns only the five REG-001 functions and, once REG-002 is recorded, the four REG-002 functions.
-- 18. paqueteria_registration_executor holds only USAGE on schemas identity, organizations and platform and exactly the column grants above: no table-wide grant, no DELETE, no other table, no outbox, bootstrap, lifecycle, cleanup or purge privilege.
-- 19. the five REG-001 functions are SECURITY DEFINER with a pinned search_path and only paqueteria_app may EXECUTE them; PUBLIC and paqueteria_worker may not.
-- 20. paqueteria_session_executor is NOLOGIN BYPASSRLS, inherits no role, is granted to no runtime role and owns only security.create_bff_session(bytea,text,text,bytea,timestamptz), security.resolve_bff_session(bytea), security.revoke_bff_session(bytea), security.revoke_bff_session(text), security.revoke_bff_session(text,timestamptz) and security.register_bff_logout_jti(bytea,timestamptz).
-- 21. paqueteria_session_executor holds only USAGE on schema identity plus SELECT on the seven columns, INSERT on the six non-revocation columns and UPDATE(ticket_ciphertext,revoked_at) of identity.bff_sessions, plus INSERT(jti_hash,created_at,expires_at) on identity.bff_logout_jtis: no DELETE, no table-wide grant, no other table, no outbox, bootstrap, lifecycle or cleanup privilege.
-- 22. the six session functions are SECURITY DEFINER with search_path=pg_catalog, identity, pg_temp, validate every argument, and only paqueteria_app may EXECUTE them; PUBLIC and paqueteria_worker may not.
-- 23. identity.bff_sessions and identity.bff_logout_jtis have ENABLE and FORCE ROW LEVEL SECURITY with no policy, and paqueteria_app and paqueteria_worker hold no table or column privilege on them.
-- 24. once the Custody BFF purge lane is recorded, paqueteria_cleanup_executor additionally owns security.purge_bff_sessions(integer) and holds USAGE on schema identity, SELECT(session_key_hash,expires_at,revoked_at) and DELETE on identity.bff_sessions and SELECT(jti_hash,expires_at) and DELETE on identity.bff_logout_jtis, and nothing else there; only paqueteria_worker may EXECUTE the purge.
-- 25. once REG-002 is recorded, the four REG-002 functions are SECURITY DEFINER with a pinned search_path, owned by paqueteria_registration_executor, and only paqueteria_app may EXECUTE them; organizations.pending_memberships has ENABLE and FORCE ROW LEVEL SECURITY with the tenant policy, paqueteria_app holds only SELECT on it and paqueteria_worker holds nothing.
-- 26. paqueteria_master_data_executor is NOLOGIN BYPASSRLS, inherits no role, is granted to no runtime role and only to deployment principals (members of paqueteria_migrator) and, once the Pricing MDM-001 lane is recorded, owns only security.load_master_data(uuid,uuid,json,bytea,boolean) (the first published jsonb overload is dropped by that lane's Up and Down). Both master data roles and their grants persist after that lane is rolled back: AI-18 declares them, and without the function they are inert.
-- 27. paqueteria_master_data_executor holds only USAGE on schemas identity, organizations, locations, pricing, drivers and platform and exactly the column grants above (including SELECT(deployment_class,gate_007_closed) on platform.master_data_deployment_gate), plus SELECT and INSERT on pricing.tariff_rules.policy_version once the PRC-POLICY-VERSION-PER-ORG loader step is recorded and SELECT and INSERT on platform.master_data_operator_refs(operator_login, operator_ref) once the MDM-001-LOADER-HARDENING step is recorded: no table-wide grant, no DELETE, no other table, no outbox, bootstrap, lifecycle, cleanup, registration or session privilege.
-- 28. paqueteria_master_data_loader is NOLOGIN NOBYPASSRLS, inherits no role, is granted to no runtime role and to no member that can use it while also being a member of paqueteria_migrator, owns nothing and holds only USAGE on schema security plus EXECUTE on security.load_master_data(uuid,uuid,json,bytea,boolean); that function's ACL is exactly its owner and the loader, it is SECURITY DEFINER with search_path=pg_catalog, pg_temp, and platform.master_data_deployment_gate forces RLS with only the master_data_deployment_gate_migrator policy and grants nothing to PUBLIC or the runtime roles; once the MDM-001-LOADER-HARDENING step is recorded, the function refuses any session whose login is a member of paqueteria_migrator, and platform.master_data_operator_refs forces RLS with only the master_data_operator_refs_migrator policy, is owned by paqueteria_migrator and grants nothing to PUBLIC, the runtime roles or the loader.
-- 29. paqueteria_operator_outbox_executor is NOLOGIN BYPASSRLS, inherits no role, is granted to no runtime or bootstrap role and, once the Dispatch DSP-OPERATOR-OWNER-OUTBOX lane is recorded, owns only security.append_operator_order_outbox(uuid,uuid,jsonb,text,text,uuid,integer,jsonb,smallint,timestamptz,timestamptz) and security.append_operator_order_audit(uuid,uuid,uuid,text,text,uuid,text,jsonb,timestamptz). The role and its grants persist after that lane is rolled back: AI-18 declares them, and without the functions they are inert.
-- 30. paqueteria_operator_outbox_executor holds only USAGE on schemas identity, organizations, orders, dispatch and platform and exactly the column grants above, none grantable: no table-wide grant, no UPDATE or DELETE, no CREATE, no other table, no outbox lifecycle, bootstrap, lifecycle, cleanup, registration, session, master data or purge privilege.
-- 31. both operator outbox functions are SECURITY DEFINER with search_path=pg_catalog, pg_temp, contain no dynamic SQL and no RETURNING, and only paqueteria_app may EXECUTE them; PUBLIC, paqueteria_worker and paqueteria_bootstrap may not.
-- 32. paqueteria_auto_close_executor is NOLOGIN BYPASSRLS, inherits no role, is granted to no runtime or bootstrap role and, once the Orders ORD-AUTO-CLOSE lane is recorded, owns only security.list_auto_close_owner_organizations(uuid,integer). The role and its grants persist after that lane is rolled back: AI-18 declares them, and without the function they are inert.
-- 33. paqueteria_auto_close_executor holds only USAGE on schema orders and SELECT(owner_org_id,status) on orders.orders, none grantable: no table-wide grant, no INSERT, UPDATE or DELETE, no CREATE, no other table, no outbox, bootstrap, lifecycle, cleanup, registration, session, master data or purge privilege.
-- 34. security.list_auto_close_owner_organizations(uuid,integer) is STABLE SECURITY DEFINER with search_path=pg_catalog, orders, pg_temp, contains no dynamic SQL and no RETURNING, accepts only a bounded limit, returns only owner organization identifiers, and only paqueteria_worker may EXECUTE it; PUBLIC, paqueteria_app and paqueteria_bootstrap may not.
