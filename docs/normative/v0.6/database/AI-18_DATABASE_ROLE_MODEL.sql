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
