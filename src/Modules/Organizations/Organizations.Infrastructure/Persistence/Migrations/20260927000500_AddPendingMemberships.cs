using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Organizations.Infrastructure.Persistence.Migrations;

/// <summary>
/// REG-002 (REG-JOIN-EXISTING-BY-EMAIL, owner decisions REG-JOIN-BY-ADMIN-EMAIL,
/// REG-PENDING-MEMBERSHIP-STORAGE, REG-JOIN-ADDERS-MFA, REG-ROLE-CEILING, REG-PLATFORM-ADMIN-ADDS,
/// REG-DRIVER-PROFILE-LATER, REG-PENDING-RENEW-ONLY, REG-PENDING-EXPIRY-7D and REG-ACCEPT-ALL-ORGANIZATIONS)
/// lets an organization administrator add a person by email and role:
/// <list type="bullet">
/// <item><c>organizations.pending_memberships</c> keeps only a keyed HMAC of the normalized email and
/// its key version, never the address; FORCE RLS with the tenant policy, SELECT only for
/// <c>paqueteria_app</c> and nothing for <c>paqueteria_worker</c>;</item>
/// <item>four more SECURITY DEFINER functions owned by <c>paqueteria_registration_executor</c>,
/// executable only by <c>paqueteria_app</c>:
/// <c>security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text)</c>,
/// <c>security.renew_pending_membership(uuid,uuid,uuid,text,text)</c>,
/// <c>security.revoke_pending_membership(uuid,uuid,uuid,text,text)</c> and
/// <c>security.apply_pending_memberships(text,bytea[],integer[])</c>.</item>
/// </list>
/// AI-06/AI-18 already carry the table, its policy and the executor grants on fresh installations;
/// this lane adopts them there, refusing any shape that differs, and creates them on populated
/// installations. The functions exist only in this lane (ADR-034 pattern).
/// </summary>
[DbContext(typeof(OrganizationsDbContext))]
[Migration(MigrationId)]
public sealed class AddPendingMemberships : Migration
{
    public const string MigrationId = "20260927000500_AddPendingMemberships";
    public const string AddSignature =
        "security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text)";
    public const string RenewSignature = "security.renew_pending_membership(uuid,uuid,uuid,text,text)";
    public const string RevokeSignature = "security.revoke_pending_membership(uuid,uuid,uuid,text,text)";
    public const string ApplySignature = "security.apply_pending_memberships(text,bytea[],integer[])";
    public const int LifetimeDays = 7;
    public const int MaximumEmailHashes = 8;

    public static IReadOnlyList<string> OwnedFunctions { get; } = Array.AsReadOnly(new[]
    {
        AddSignature, RenewSignature, RevokeSignature, ApplySignature,
    });

    public const string UpSql =
        """
        RESET ROLE;

        DO $adoption$
        DECLARE
          v_columns text[];
          v_constraints text[];
          v_indexes text[];
          v_policies text[];
        BEGIN
          IF to_regclass('organizations.organizations') IS NULL
             OR to_regclass('organizations.organization_memberships') IS NULL
             OR to_regclass('identity.users') IS NULL
             OR to_regclass('platform.audit_logs') IS NULL
             OR to_regprocedure('security.app_allowed_org(uuid)') IS NULL
             OR to_regprocedure('security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)') IS NULL THEN
            RAISE EXCEPTION 'REG-002 requires the canonical AI-06 tables and the REG-001 registration lane';
          END IF;

          -- A fresh AI-06 baseline already created the table; it is adopted only when it is exactly canonical.
          IF to_regclass('organizations.pending_memberships') IS NOT NULL THEN
            SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
            INTO v_columns
            FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='pending_memberships';
            IF v_columns IS DISTINCT FROM ARRAY[
              'accepted_at:timestamp with time zone:YES',
              'accepted_user_id:uuid:YES',
              'created_at:timestamp with time zone:NO',
              'email_hmac:bytea:NO',
              'email_hmac_key_version:integer:NO',
              'expires_at:timestamp with time zone:NO',
              'id:uuid:NO',
              'invited_by:uuid:NO',
              'organization_id:uuid:NO',
              'revoked_at:timestamp with time zone:YES',
              'role:text:NO',
              'status:text:NO'
            ] OR EXISTS (
              SELECT 1 FROM information_schema.columns
              WHERE table_schema='organizations' AND table_name='pending_memberships' AND column_default IS NOT NULL)
            THEN
              RAISE EXCEPTION 'organizations.pending_memberships columns do not match the canonical AI-06 contract';
            END IF;

            SELECT array_agg(conname || ':' || pg_get_constraintdef(oid) ORDER BY conname)
            INTO v_constraints
            FROM pg_constraint
            WHERE conrelid='organizations.pending_memberships'::regclass AND contype IN ('c','p','u','f','x');
            IF v_constraints IS DISTINCT FROM ARRAY[
              'pending_memberships_accepted_ck:CHECK (((status = ''ACCEPTED''::text) = ((accepted_user_id IS NOT NULL) AND (accepted_at IS NOT NULL))))',
              'pending_memberships_accepted_user_fk:FOREIGN KEY (accepted_user_id) REFERENCES identity.users(id)',
              'pending_memberships_email_hmac_ck:CHECK ((octet_length(email_hmac) = 32))',
              'pending_memberships_invited_by_fk:FOREIGN KEY (invited_by) REFERENCES identity.users(id)',
              'pending_memberships_key_version_ck:CHECK (((email_hmac_key_version >= 1) AND (email_hmac_key_version <= 32767)))',
              'pending_memberships_lifetime_ck:CHECK ((expires_at > created_at))',
              'pending_memberships_organization_fk:FOREIGN KEY (organization_id) REFERENCES organizations.organizations(id)',
              'pending_memberships_pkey:PRIMARY KEY (id)',
              'pending_memberships_revoked_ck:CHECK (((status = ''REVOKED''::text) = (revoked_at IS NOT NULL)))',
              'pending_memberships_role_ck:CHECK ((role = ANY (ARRAY[''PLATFORM_ADMIN''::text, ''DISPATCHER''::text, ''FINANCE''::text, ''ALLY_ADMIN''::text, ''ALLY_OPERATOR''::text, ''BUSINESS_ADMIN''::text, ''BUSINESS_OPERATOR''::text, ''DRIVER''::text, ''VIEWER''::text])))',
              'pending_memberships_status_ck:CHECK ((status = ANY (ARRAY[''PENDING''::text, ''ACCEPTED''::text, ''REVOKED''::text])))'
            ] THEN
              RAISE EXCEPTION 'organizations.pending_memberships constraints do not match the canonical AI-06 contract';
            END IF;

            SELECT array_agg(pg_get_indexdef(indexrelid) ORDER BY pg_get_indexdef(indexrelid))
            INTO v_indexes
            FROM pg_index
            WHERE indrelid='organizations.pending_memberships'::regclass;
            IF v_indexes IS DISTINCT FROM ARRAY[
              'CREATE INDEX pending_memberships_lookup_idx ON organizations.pending_memberships USING btree (email_hmac_key_version, email_hmac) WHERE (status = ''PENDING''::text)',
              'CREATE INDEX pending_memberships_org_idx ON organizations.pending_memberships USING btree (organization_id, created_at)',
              'CREATE UNIQUE INDEX pending_memberships_one_pending_uq ON organizations.pending_memberships USING btree (organization_id, email_hmac_key_version, email_hmac, role) WHERE (status = ''PENDING''::text)',
              'CREATE UNIQUE INDEX pending_memberships_pkey ON organizations.pending_memberships USING btree (id)'
            ] THEN
              RAISE EXCEPTION 'organizations.pending_memberships indexes do not match the canonical AI-06 contract';
            END IF;

            SELECT array_agg(concat_ws('|', polname, polcmd::text, polpermissive::text,
                     pg_get_expr(polqual, polrelid), pg_get_expr(polwithcheck, polrelid), polroles::text)
                   ORDER BY polname)
            INTO v_policies
            FROM pg_policy
            WHERE polrelid='organizations.pending_memberships'::regclass;
            IF v_policies IS DISTINCT FROM ARRAY[
              'pending_memberships_tenant|*|true|security.app_allowed_org(organization_id)|security.app_allowed_org(organization_id)|{0}'
            ] THEN
              RAISE EXCEPTION 'organizations.pending_memberships policy does not match the canonical AI-06 contract';
            END IF;

            IF NOT EXISTS (
                 SELECT 1 FROM pg_class
                 WHERE oid='organizations.pending_memberships'::regclass AND relrowsecurity AND relforcerowsecurity
                   AND pg_get_userbyid(relowner)='paqueteria_migrator')
               OR EXISTS (
                 SELECT 1 FROM pg_trigger
                 WHERE tgrelid='organizations.pending_memberships'::regclass AND NOT tgisinternal)
            THEN
              RAISE EXCEPTION 'organizations.pending_memberships must be owned by paqueteria_migrator with FORCE RLS and no user trigger';
            END IF;
          END IF;

          -- The functions write memberships only through the columns granted to the executor; a user
          -- trigger on memberships could widen that.
          IF EXISTS (
            SELECT 1 FROM pg_trigger t
            WHERE t.tgrelid = 'organizations.organization_memberships'::regclass AND NOT t.tgisinternal
          ) THEN
            RAISE EXCEPTION 'REG-002 refuses an organization_memberships table with user triggers';
          END IF;

          IF EXISTS (
            SELECT 1 FROM pg_proc
            WHERE proowner='paqueteria_registration_executor'::regrole
              AND oid IS DISTINCT FROM to_regprocedure('security.register_identity_subject(text,uuid)')
              AND oid IS DISTINCT FROM to_regprocedure('security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)')
              AND oid IS DISTINCT FROM to_regprocedure('security.list_own_organization_applications(uuid)')
              AND oid IS DISTINCT FROM to_regprocedure('security.list_pending_ally_organizations(uuid,uuid,integer)')
              AND oid IS DISTINCT FROM to_regprocedure('security.decide_ally_organization(uuid,uuid,uuid,boolean,text)')
              AND oid IS DISTINCT FROM to_regprocedure('security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text)')
              AND oid IS DISTINCT FROM to_regprocedure('security.renew_pending_membership(uuid,uuid,uuid,text,text)')
              AND oid IS DISTINCT FROM to_regprocedure('security.revoke_pending_membership(uuid,uuid,uuid,text,text)')
              AND oid IS DISTINCT FROM to_regprocedure('security.apply_pending_memberships(text,bytea[],integer[])')
          ) OR EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_registration_executor'::regrole)
          THEN
            RAISE EXCEPTION 'paqueteria_registration_executor already owns objects outside the REG-002 contract';
          END IF;
        END
        $adoption$;

        -- The table is created, protected and granted by its owner, exactly as AI-06/AI-18 declare it.
        -- CREATE ... IF NOT EXISTS is a no-op on a fresh baseline, whose shape was verified above.
        SET LOCAL ROLE paqueteria_migrator;

        CREATE TABLE IF NOT EXISTS organizations.pending_memberships (
          id uuid NOT NULL,
          organization_id uuid NOT NULL,
          email_hmac bytea NOT NULL,
          email_hmac_key_version integer NOT NULL,
          role text NOT NULL,
          status text NOT NULL,
          invited_by uuid NOT NULL,
          created_at timestamptz NOT NULL,
          expires_at timestamptz NOT NULL,
          accepted_user_id uuid,
          accepted_at timestamptz,
          revoked_at timestamptz,
          CONSTRAINT pending_memberships_pkey PRIMARY KEY (id),
          CONSTRAINT pending_memberships_organization_fk FOREIGN KEY (organization_id) REFERENCES organizations.organizations(id),
          CONSTRAINT pending_memberships_invited_by_fk FOREIGN KEY (invited_by) REFERENCES identity.users(id),
          CONSTRAINT pending_memberships_accepted_user_fk FOREIGN KEY (accepted_user_id) REFERENCES identity.users(id),
          CONSTRAINT pending_memberships_email_hmac_ck CHECK (octet_length(email_hmac)=32),
          CONSTRAINT pending_memberships_key_version_ck CHECK (email_hmac_key_version BETWEEN 1 AND 32767),
          CONSTRAINT pending_memberships_role_ck CHECK (role IN ('PLATFORM_ADMIN','DISPATCHER','FINANCE','ALLY_ADMIN','ALLY_OPERATOR','BUSINESS_ADMIN','BUSINESS_OPERATOR','DRIVER','VIEWER')),
          CONSTRAINT pending_memberships_status_ck CHECK (status IN ('PENDING','ACCEPTED','REVOKED')),
          CONSTRAINT pending_memberships_lifetime_ck CHECK (expires_at > created_at),
          CONSTRAINT pending_memberships_accepted_ck CHECK ((status='ACCEPTED') = (accepted_user_id IS NOT NULL AND accepted_at IS NOT NULL)),
          CONSTRAINT pending_memberships_revoked_ck CHECK ((status='REVOKED') = (revoked_at IS NOT NULL))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS pending_memberships_one_pending_uq
          ON organizations.pending_memberships(organization_id, email_hmac_key_version, email_hmac, role)
          WHERE status='PENDING';
        CREATE INDEX IF NOT EXISTS pending_memberships_lookup_idx
          ON organizations.pending_memberships(email_hmac_key_version, email_hmac)
          WHERE status='PENDING';
        CREATE INDEX IF NOT EXISTS pending_memberships_org_idx
          ON organizations.pending_memberships(organization_id, created_at);
        ALTER TABLE organizations.pending_memberships ENABLE ROW LEVEL SECURITY;
        ALTER TABLE organizations.pending_memberships FORCE ROW LEVEL SECURITY;

        DO $policy$
        BEGIN
          IF NOT EXISTS (
            SELECT 1 FROM pg_policy
            WHERE polrelid='organizations.pending_memberships'::regclass AND polname='pending_memberships_tenant')
          THEN
            CREATE POLICY pending_memberships_tenant ON organizations.pending_memberships
              USING (security.app_allowed_org(organization_id)) WITH CHECK (security.app_allowed_org(organization_id));
          END IF;
        END
        $policy$;

        -- The organizations schema's default privileges hand every new table to both runtime roles:
        -- the API keeps SELECT under RLS for the tenant list and every write goes through the functions.
        REVOKE INSERT,UPDATE,DELETE ON organizations.pending_memberships FROM paqueteria_app;
        REVOKE ALL ON organizations.pending_memberships FROM paqueteria_worker;
        GRANT SELECT ON organizations.pending_memberships TO paqueteria_app;
        GRANT SELECT (id,organization_id,email_hmac,email_hmac_key_version,role,status,created_at,expires_at)
          ON organizations.pending_memberships TO paqueteria_registration_executor;
        GRANT INSERT (id,organization_id,email_hmac,email_hmac_key_version,role,status,invited_by,created_at,expires_at)
          ON organizations.pending_memberships TO paqueteria_registration_executor;
        GRANT UPDATE (status,expires_at,accepted_user_id,accepted_at,revoked_at)
          ON organizations.pending_memberships TO paqueteria_registration_executor;

        RESET ROLE;

        -- REG-JOIN-BY-ADMIN-EMAIL, REG-ROLE-CEILING and REG-PENDING-RENEW-ONLY: an ACTIVE PLATFORM_ADMIN,
        -- ALLY_ADMIN or BUSINESS_ADMIN of the ACTIVE organization adds an email HMAC with a role inside its
        -- ceiling. The entry id is derived by the caller from the actor, the organization and the
        -- Idempotency-Key: the same key with the same request replays, with another request is
        -- IDEMPOTENCY_CONFLICT. A PENDING entry for the same email and role is re-armed for seven days
        -- instead of duplicated (RENEWED). Whether the email belongs to an account is never looked at.
        CREATE OR REPLACE FUNCTION security.add_pending_membership(
          p_actor_user_id uuid,
          p_organization_id uuid,
          p_pending_id uuid,
          p_email_hmac bytea,
          p_email_hmac_key_version integer,
          p_role text,
          p_idempotency_key text,
          p_request_id text)
        RETURNS TABLE(
          outcome text,
          pending_id uuid,
          role text,
          status text,
          created_at timestamptz,
          expires_at timestamptz)
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, platform, pg_temp
        AS $function$
        #variable_conflict use_column
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_org_type text;
          v_roles text[];
          v_row record;
          v_audit_id uuid;
          v_attempt integer;
        BEGIN
          IF p_actor_user_id IS NULL OR p_organization_id IS NULL OR p_pending_id IS NULL
             OR p_email_hmac IS NULL OR pg_catalog.octet_length(p_email_hmac) <> 32
             OR p_email_hmac_key_version IS NULL OR p_email_hmac_key_version < 1 OR p_email_hmac_key_version > 32767
             OR p_role IS NULL OR p_role NOT IN (
               'PLATFORM_ADMIN','DISPATCHER','FINANCE','ALLY_ADMIN','ALLY_OPERATOR',
               'BUSINESS_ADMIN','BUSINESS_OPERATOR','DRIVER','VIEWER')
             OR p_idempotency_key IS NULL OR pg_catalog.length(p_idempotency_key) < 1
             OR pg_catalog.length(p_idempotency_key) > 128 OR p_idempotency_key ~ '[[:cntrl:]]'
             OR (p_request_id IS NOT NULL AND pg_catalog.length(p_request_id) > 128) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG002_ARGUMENT_OUT_OF_RANGE';
          END IF;

          SELECT o.organization_type INTO v_org_type
          FROM organizations.organizations o
          WHERE o.id = p_organization_id AND o.status = 'ACTIVE';
          SELECT pg_catalog.array_agg(m.role) INTO v_roles
          FROM organizations.organization_memberships m
          JOIN identity.users u ON u.id = m.user_id
          WHERE m.user_id = p_actor_user_id
            AND m.organization_id = p_organization_id
            AND m.status = 'ACTIVE'
            AND u.status = 'ACTIVE'
            AND m.role IN ('PLATFORM_ADMIN','ALLY_ADMIN','BUSINESS_ADMIN');
          IF v_org_type IS NULL OR v_roles IS NULL THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'REG002_ADMIN_REQUIRED';
          END IF;

          -- REG-ROLE-CEILING and REG-PLATFORM-ADMIN-ADDS.
          IF NOT (
               ('PLATFORM_ADMIN' = ANY(v_roles) AND (p_role <> 'PLATFORM_ADMIN' OR v_org_type = 'PLATFORM'))
            OR ('ALLY_ADMIN' = ANY(v_roles) AND p_role IN ('ALLY_ADMIN','ALLY_OPERATOR','DRIVER','VIEWER'))
            OR ('BUSINESS_ADMIN' = ANY(v_roles) AND p_role IN ('BUSINESS_ADMIN','BUSINESS_OPERATOR','VIEWER')))
          THEN
            RETURN QUERY SELECT 'ROLE_NOT_ALLOWED'::text, NULL::uuid, NULL::text, NULL::text, NULL::timestamptz, NULL::timestamptz;
            RETURN;
          END IF;

          v_audit_id := pg_catalog.encode(pg_catalog.substr(pg_catalog.sha256(pg_catalog.convert_to(
            'REG002|ADD|' || p_actor_user_id::text || '|' || p_organization_id::text || '|' || p_idempotency_key,
            'UTF8')), 1, 16), 'hex')::uuid;

          -- Two passes: a concurrent request that wins the insert is committed and visible to the second.
          FOR v_attempt IN 1..2 LOOP
            SELECT p.id, p.organization_id, p.email_hmac, p.email_hmac_key_version, p.role, p.status,
                   p.created_at, p.expires_at
            INTO v_row
            FROM organizations.pending_memberships p
            WHERE p.id = p_pending_id;
            IF FOUND THEN
              IF v_row.organization_id IS DISTINCT FROM p_organization_id
                 OR v_row.email_hmac IS DISTINCT FROM p_email_hmac
                 OR v_row.email_hmac_key_version IS DISTINCT FROM p_email_hmac_key_version
                 OR v_row.role IS DISTINCT FROM p_role THEN
                RETURN QUERY SELECT 'IDEMPOTENCY_CONFLICT'::text, NULL::uuid, NULL::text, NULL::text, NULL::timestamptz, NULL::timestamptz;
                RETURN;
              END IF;
              RETURN QUERY SELECT 'REPLAYED'::text, v_row.id, v_row.role, v_row.status, v_row.created_at, v_row.expires_at;
              RETURN;
            END IF;

            SELECT p.id, p.role, p.status, p.created_at, p.expires_at
            INTO v_row
            FROM organizations.pending_memberships p
            WHERE p.organization_id = p_organization_id
              AND p.email_hmac_key_version = p_email_hmac_key_version
              AND p.email_hmac = p_email_hmac
              AND p.role = p_role
              AND p.status = 'PENDING'
            FOR UPDATE;
            IF FOUND THEN
              -- The audit row id is derived from the Idempotency-Key: a repeated key re-arms nothing.
              INSERT INTO platform.audit_logs(
                id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
              VALUES (
                v_audit_id,p_organization_id,p_actor_user_id,'PENDING_MEMBERSHIP_RENEWED','PENDING_MEMBERSHIP',
                v_row.id,p_request_id,pg_catalog.jsonb_build_object('role', p_role),v_now)
              ON CONFLICT DO NOTHING;
              IF NOT FOUND THEN
                RETURN QUERY SELECT 'REPLAYED'::text, v_row.id, v_row.role, v_row.status, v_row.created_at, v_row.expires_at;
                RETURN;
              END IF;
              UPDATE organizations.pending_memberships p
              SET expires_at = v_now + pg_catalog.make_interval(days => 7)
              WHERE p.id = v_row.id;
              RETURN QUERY SELECT 'RENEWED'::text, v_row.id, v_row.role, v_row.status, v_row.created_at,
                v_now + pg_catalog.make_interval(days => 7);
              RETURN;
            END IF;

            BEGIN
              INSERT INTO organizations.pending_memberships(
                id,organization_id,email_hmac,email_hmac_key_version,role,status,invited_by,created_at,expires_at)
              VALUES (
                p_pending_id,p_organization_id,p_email_hmac,p_email_hmac_key_version,p_role,'PENDING',
                p_actor_user_id,v_now,v_now + pg_catalog.make_interval(days => 7));
              INSERT INTO platform.audit_logs(
                id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
              VALUES (
                pg_catalog.gen_random_uuid(),p_organization_id,p_actor_user_id,'PENDING_MEMBERSHIP_ADDED','PENDING_MEMBERSHIP',
                p_pending_id,p_request_id,pg_catalog.jsonb_build_object('role', p_role),v_now);
              RETURN QUERY SELECT 'ADDED'::text, p_pending_id, p_role, 'PENDING'::text, v_now,
                v_now + pg_catalog.make_interval(days => 7);
              RETURN;
            EXCEPTION WHEN unique_violation THEN
              -- The same key or the same email and role won concurrently; the next pass reads the winner.
              NULL;
            END;
          END LOOP;

          RAISE EXCEPTION USING ERRCODE = '40001', MESSAGE = 'REG002_CONCURRENT_UPDATE';
        END
        $function$;

        -- REG-PENDING-RENEW-ONLY: re-arm a PENDING entry (expired or not) for seven days from now. No
        -- notification is sent. The audit row id is derived from the Idempotency-Key, so a repeated key
        -- replays without re-arming again.
        CREATE OR REPLACE FUNCTION security.renew_pending_membership(
          p_actor_user_id uuid,
          p_organization_id uuid,
          p_pending_id uuid,
          p_idempotency_key text,
          p_request_id text)
        RETURNS TABLE(
          outcome text,
          pending_id uuid,
          role text,
          status text,
          created_at timestamptz,
          expires_at timestamptz)
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, platform, pg_temp
        AS $function$
        #variable_conflict use_column
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_org_type text;
          v_roles text[];
          v_row record;
        BEGIN
          IF p_actor_user_id IS NULL OR p_organization_id IS NULL OR p_pending_id IS NULL
             OR p_idempotency_key IS NULL OR pg_catalog.length(p_idempotency_key) < 1
             OR pg_catalog.length(p_idempotency_key) > 128 OR p_idempotency_key ~ '[[:cntrl:]]'
             OR (p_request_id IS NOT NULL AND pg_catalog.length(p_request_id) > 128) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG002_ARGUMENT_OUT_OF_RANGE';
          END IF;

          SELECT o.organization_type INTO v_org_type
          FROM organizations.organizations o
          WHERE o.id = p_organization_id AND o.status = 'ACTIVE';
          SELECT pg_catalog.array_agg(m.role) INTO v_roles
          FROM organizations.organization_memberships m
          JOIN identity.users u ON u.id = m.user_id
          WHERE m.user_id = p_actor_user_id
            AND m.organization_id = p_organization_id
            AND m.status = 'ACTIVE'
            AND u.status = 'ACTIVE'
            AND m.role IN ('PLATFORM_ADMIN','ALLY_ADMIN','BUSINESS_ADMIN');
          IF v_org_type IS NULL OR v_roles IS NULL THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'REG002_ADMIN_REQUIRED';
          END IF;

          SELECT p.id, p.role, p.status, p.created_at, p.expires_at
          INTO v_row
          FROM organizations.pending_memberships p
          WHERE p.id = p_pending_id AND p.organization_id = p_organization_id
          FOR UPDATE;
          IF NOT FOUND THEN
            RETURN QUERY SELECT 'NOT_FOUND'::text, NULL::uuid, NULL::text, NULL::text, NULL::timestamptz, NULL::timestamptz;
            RETURN;
          END IF;

          IF NOT (
               ('PLATFORM_ADMIN' = ANY(v_roles) AND (v_row.role <> 'PLATFORM_ADMIN' OR v_org_type = 'PLATFORM'))
            OR ('ALLY_ADMIN' = ANY(v_roles) AND v_row.role IN ('ALLY_ADMIN','ALLY_OPERATOR','DRIVER','VIEWER'))
            OR ('BUSINESS_ADMIN' = ANY(v_roles) AND v_row.role IN ('BUSINESS_ADMIN','BUSINESS_OPERATOR','VIEWER')))
          THEN
            RETURN QUERY SELECT 'ROLE_NOT_ALLOWED'::text, NULL::uuid, NULL::text, NULL::text, NULL::timestamptz, NULL::timestamptz;
            RETURN;
          END IF;

          IF v_row.status <> 'PENDING' THEN
            RETURN QUERY SELECT 'NOT_PENDING'::text, v_row.id, v_row.role, v_row.status, v_row.created_at, v_row.expires_at;
            RETURN;
          END IF;

          INSERT INTO platform.audit_logs(
            id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
          VALUES (
            pg_catalog.encode(pg_catalog.substr(pg_catalog.sha256(pg_catalog.convert_to(
              'REG002|RENEW|' || p_actor_user_id::text || '|' || p_pending_id::text || '|' || p_idempotency_key,
              'UTF8')), 1, 16), 'hex')::uuid,
            p_organization_id,p_actor_user_id,'PENDING_MEMBERSHIP_RENEWED','PENDING_MEMBERSHIP',
            v_row.id,p_request_id,pg_catalog.jsonb_build_object('role', v_row.role),v_now)
          ON CONFLICT DO NOTHING;
          IF NOT FOUND THEN
            RETURN QUERY SELECT 'REPLAYED'::text, v_row.id, v_row.role, v_row.status, v_row.created_at, v_row.expires_at;
            RETURN;
          END IF;

          UPDATE organizations.pending_memberships p
          SET expires_at = v_now + pg_catalog.make_interval(days => 7)
          WHERE p.id = v_row.id;
          RETURN QUERY SELECT 'RENEWED'::text, v_row.id, v_row.role, v_row.status, v_row.created_at,
            v_now + pg_catalog.make_interval(days => 7);
        END
        $function$;

        -- Revoke a PENDING entry of the caller's organization. Revoking a REVOKED entry returns it again
        -- without a new audit row; an ACCEPTED entry already became a membership and is NOT_PENDING.
        CREATE OR REPLACE FUNCTION security.revoke_pending_membership(
          p_actor_user_id uuid,
          p_organization_id uuid,
          p_pending_id uuid,
          p_idempotency_key text,
          p_request_id text)
        RETURNS TABLE(
          outcome text,
          pending_id uuid,
          role text,
          status text,
          created_at timestamptz,
          expires_at timestamptz)
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, platform, pg_temp
        AS $function$
        #variable_conflict use_column
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_org_type text;
          v_roles text[];
          v_row record;
        BEGIN
          IF p_actor_user_id IS NULL OR p_organization_id IS NULL OR p_pending_id IS NULL
             OR p_idempotency_key IS NULL OR pg_catalog.length(p_idempotency_key) < 1
             OR pg_catalog.length(p_idempotency_key) > 128 OR p_idempotency_key ~ '[[:cntrl:]]'
             OR (p_request_id IS NOT NULL AND pg_catalog.length(p_request_id) > 128) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG002_ARGUMENT_OUT_OF_RANGE';
          END IF;

          SELECT o.organization_type INTO v_org_type
          FROM organizations.organizations o
          WHERE o.id = p_organization_id AND o.status = 'ACTIVE';
          SELECT pg_catalog.array_agg(m.role) INTO v_roles
          FROM organizations.organization_memberships m
          JOIN identity.users u ON u.id = m.user_id
          WHERE m.user_id = p_actor_user_id
            AND m.organization_id = p_organization_id
            AND m.status = 'ACTIVE'
            AND u.status = 'ACTIVE'
            AND m.role IN ('PLATFORM_ADMIN','ALLY_ADMIN','BUSINESS_ADMIN');
          IF v_org_type IS NULL OR v_roles IS NULL THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'REG002_ADMIN_REQUIRED';
          END IF;

          SELECT p.id, p.role, p.status, p.created_at, p.expires_at
          INTO v_row
          FROM organizations.pending_memberships p
          WHERE p.id = p_pending_id AND p.organization_id = p_organization_id
          FOR UPDATE;
          IF NOT FOUND THEN
            RETURN QUERY SELECT 'NOT_FOUND'::text, NULL::uuid, NULL::text, NULL::text, NULL::timestamptz, NULL::timestamptz;
            RETURN;
          END IF;

          IF NOT (
               ('PLATFORM_ADMIN' = ANY(v_roles) AND (v_row.role <> 'PLATFORM_ADMIN' OR v_org_type = 'PLATFORM'))
            OR ('ALLY_ADMIN' = ANY(v_roles) AND v_row.role IN ('ALLY_ADMIN','ALLY_OPERATOR','DRIVER','VIEWER'))
            OR ('BUSINESS_ADMIN' = ANY(v_roles) AND v_row.role IN ('BUSINESS_ADMIN','BUSINESS_OPERATOR','VIEWER')))
          THEN
            RETURN QUERY SELECT 'ROLE_NOT_ALLOWED'::text, NULL::uuid, NULL::text, NULL::text, NULL::timestamptz, NULL::timestamptz;
            RETURN;
          END IF;

          IF v_row.status = 'REVOKED' THEN
            RETURN QUERY SELECT 'REVOKED'::text, v_row.id, v_row.role, v_row.status, v_row.created_at, v_row.expires_at;
            RETURN;
          END IF;
          IF v_row.status <> 'PENDING' THEN
            RETURN QUERY SELECT 'NOT_PENDING'::text, v_row.id, v_row.role, v_row.status, v_row.created_at, v_row.expires_at;
            RETURN;
          END IF;

          UPDATE organizations.pending_memberships p
          SET status = 'REVOKED', revoked_at = v_now
          WHERE p.id = v_row.id;
          INSERT INTO platform.audit_logs(
            id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
          VALUES (
            pg_catalog.encode(pg_catalog.substr(pg_catalog.sha256(pg_catalog.convert_to(
              'REG002|REVOKE|' || p_actor_user_id::text || '|' || p_pending_id::text || '|' || p_idempotency_key,
              'UTF8')), 1, 16), 'hex')::uuid,
            p_organization_id,p_actor_user_id,'PENDING_MEMBERSHIP_REVOKED','PENDING_MEMBERSHIP',
            v_row.id,p_request_id,pg_catalog.jsonb_build_object('role', v_row.role),v_now);
          RETURN QUERY SELECT 'REVOKED'::text, v_row.id, v_row.role, 'REVOKED'::text, v_row.created_at, v_row.expires_at;
        END
        $function$;

        -- REG-ACCEPT-ALL-ORGANIZATIONS: at every sign-in with a verified email, each PENDING, unexpired
        -- entry of an ACTIVE organization whose HMAC matches (one per configured key version) becomes a
        -- membership of that ACTIVE user, exactly once: the entry row lock serializes concurrent sign-ins
        -- and the loser re-reads it as ACCEPTED. An identical ACTIVE or SUSPENDED membership consumes the
        -- entry without a duplicate and without reactivating anything. The default follows
        -- REG-DEFAULT-MEMBERSHIP-RELEASE. Returns the number of entries accepted.
        CREATE OR REPLACE FUNCTION security.apply_pending_memberships(
          p_identity_subject text,
          p_email_hmacs bytea[],
          p_email_hmac_key_versions integer[])
        RETURNS integer
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, platform, pg_temp
        AS $function$
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_user_id uuid;
          v_entry record;
          v_existing boolean;
          v_count integer := 0;
        BEGIN
          IF p_identity_subject IS NULL
             OR pg_catalog.length(p_identity_subject) < 1 OR pg_catalog.length(p_identity_subject) > 256
             OR p_identity_subject ~ '[[:cntrl:]]'
             OR p_email_hmacs IS NULL OR p_email_hmac_key_versions IS NULL
             OR pg_catalog.cardinality(p_email_hmacs) < 1 OR pg_catalog.cardinality(p_email_hmacs) > 8
             OR pg_catalog.cardinality(p_email_hmacs) <> pg_catalog.cardinality(p_email_hmac_key_versions)
             OR EXISTS (
               SELECT 1 FROM ROWS FROM (pg_catalog.unnest(p_email_hmacs), pg_catalog.unnest(p_email_hmac_key_versions)) AS k(email_hmac, key_version)
               WHERE k.email_hmac IS NULL OR pg_catalog.octet_length(k.email_hmac) <> 32
                  OR k.key_version IS NULL OR k.key_version < 1 OR k.key_version > 32767) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG002_ARGUMENT_OUT_OF_RANGE';
          END IF;

          SELECT u.id INTO v_user_id
          FROM identity.users u
          WHERE u.identity_subject = p_identity_subject AND u.status = 'ACTIVE';
          IF NOT FOUND THEN
            RETURN 0;
          END IF;

          FOR v_entry IN
            SELECT p.id, p.organization_id, p.role
            FROM organizations.pending_memberships p
            WHERE p.status = 'PENDING'
              AND p.expires_at > v_now
              AND EXISTS (
                SELECT 1 FROM ROWS FROM (pg_catalog.unnest(p_email_hmacs), pg_catalog.unnest(p_email_hmac_key_versions)) AS k(email_hmac, key_version)
                WHERE k.email_hmac = p.email_hmac AND k.key_version = p.email_hmac_key_version)
              AND EXISTS (
                SELECT 1 FROM organizations.organizations o
                WHERE o.id = p.organization_id AND o.status = 'ACTIVE')
            ORDER BY p.created_at, p.id
            FOR UPDATE OF p
          LOOP
            v_existing := EXISTS (
              SELECT 1 FROM organizations.organization_memberships m
              WHERE m.user_id = v_user_id
                AND m.organization_id = v_entry.organization_id
                AND m.role = v_entry.role
                AND m.status IN ('ACTIVE','SUSPENDED'));
            IF NOT v_existing THEN
              -- REG-DEFAULT-MEMBERSHIP-RELEASE: a default in an organization that is no longer ACTIVE
              -- gives up the slot; a default inside an ACTIVE organization is never touched.
              UPDATE organizations.organization_memberships m
              SET is_default = false
              WHERE m.user_id = v_user_id
                AND m.status = 'ACTIVE'
                AND m.is_default
                AND NOT EXISTS (
                  SELECT 1 FROM organizations.organizations o
                  WHERE o.id = m.organization_id AND o.status = 'ACTIVE');
              BEGIN
                INSERT INTO organizations.organization_memberships(
                  id,user_id,organization_id,role,status,is_default,granted_at)
                VALUES (
                  pg_catalog.gen_random_uuid(),v_user_id,v_entry.organization_id,v_entry.role,'ACTIVE',
                  NOT EXISTS (
                    SELECT 1
                    FROM organizations.organization_memberships m
                    JOIN organizations.organizations o ON o.id = m.organization_id
                    WHERE m.user_id = v_user_id AND m.status = 'ACTIVE' AND m.is_default
                      AND o.status = 'ACTIVE'),
                  v_now);
              EXCEPTION WHEN unique_violation THEN
                -- A concurrent registration took the default slot or granted the same role meanwhile.
                IF NOT EXISTS (
                  SELECT 1 FROM organizations.organization_memberships m
                  WHERE m.user_id = v_user_id
                    AND m.organization_id = v_entry.organization_id
                    AND m.role = v_entry.role
                    AND m.status = 'ACTIVE') THEN
                  INSERT INTO organizations.organization_memberships(
                    id,user_id,organization_id,role,status,is_default,granted_at)
                  VALUES (
                    pg_catalog.gen_random_uuid(),v_user_id,v_entry.organization_id,v_entry.role,'ACTIVE',
                    false,v_now);
                END IF;
              END;
            END IF;

            UPDATE organizations.pending_memberships p
            SET status = 'ACCEPTED', accepted_user_id = v_user_id, accepted_at = v_now
            WHERE p.id = v_entry.id;
            INSERT INTO platform.audit_logs(
              id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
            VALUES (
              pg_catalog.gen_random_uuid(),v_entry.organization_id,v_user_id,'PENDING_MEMBERSHIP_ACCEPTED',
              'PENDING_MEMBERSHIP',v_entry.id,NULL,
              pg_catalog.jsonb_build_object('role', v_entry.role, 'membership_existed', v_existing),v_now);
            v_count := v_count + 1;
          END LOOP;

          RETURN v_count;
        END
        $function$;

        -- ACL first, while the deploying role still owns the functions: ALTER ... OWNER rewrites the
        -- grantor to the new owner.
        REVOKE ALL ON FUNCTION security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.renew_pending_membership(uuid,uuid,uuid,text,text) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.revoke_pending_membership(uuid,uuid,uuid,text,text) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.apply_pending_memberships(text,bytea[],integer[]) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.renew_pending_membership(uuid,uuid,uuid,text,text) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.revoke_pending_membership(uuid,uuid,uuid,text,text) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.apply_pending_memberships(text,bytea[],integer[]) TO paqueteria_app;
        ALTER FUNCTION security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text) OWNER TO paqueteria_registration_executor;
        ALTER FUNCTION security.renew_pending_membership(uuid,uuid,uuid,text,text) OWNER TO paqueteria_registration_executor;
        ALTER FUNCTION security.revoke_pending_membership(uuid,uuid,uuid,text,text) OWNER TO paqueteria_registration_executor;
        ALTER FUNCTION security.apply_pending_memberships(text,bytea[],integer[]) OWNER TO paqueteria_registration_executor;

        DO $verify$
        BEGIN
          IF EXISTS (
               SELECT 1
               FROM (VALUES
                 ('security.register_identity_subject(text,uuid)', 'search_path=pg_catalog, identity, pg_temp'),
                 ('security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)', 'search_path=pg_catalog, identity, organizations, platform, pg_temp'),
                 ('security.list_own_organization_applications(uuid)', 'search_path=pg_catalog, identity, organizations, pg_temp'),
                 ('security.list_pending_ally_organizations(uuid,uuid,integer)', 'search_path=pg_catalog, identity, organizations, pg_temp'),
                 ('security.decide_ally_organization(uuid,uuid,uuid,boolean,text)', 'search_path=pg_catalog, identity, organizations, platform, pg_temp'),
                 ('security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text)', 'search_path=pg_catalog, identity, organizations, platform, pg_temp'),
                 ('security.renew_pending_membership(uuid,uuid,uuid,text,text)', 'search_path=pg_catalog, identity, organizations, platform, pg_temp'),
                 ('security.revoke_pending_membership(uuid,uuid,uuid,text,text)', 'search_path=pg_catalog, identity, organizations, platform, pg_temp'),
                 ('security.apply_pending_memberships(text,bytea[],integer[])', 'search_path=pg_catalog, identity, organizations, platform, pg_temp')
               ) expected(signature, search_path)
               LEFT JOIN pg_proc p ON p.oid = to_regprocedure(expected.signature)
               WHERE p.oid IS NULL
                  OR pg_get_userbyid(p.proowner) <> 'paqueteria_registration_executor'
                  OR NOT p.prosecdef
                  OR p.prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                  OR NOT (expected.search_path = ANY(COALESCE(p.proconfig, ARRAY[]::text[])))
                  OR has_function_privilege('public', p.oid, 'EXECUTE')
                  OR has_function_privilege('paqueteria_worker', p.oid, 'EXECUTE')
                  OR NOT has_function_privilege('paqueteria_app', p.oid, 'EXECUTE'))
             OR (SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_registration_executor'::regrole) <> 9
          THEN
            RAISE EXCEPTION 'REG-002 registration function security verification failed';
          END IF;

          IF (SELECT array_agg(table_schema || '.' || table_name || ':' || privilege_type)
              FROM information_schema.table_privileges
              WHERE grantee='paqueteria_registration_executor') IS NOT NULL
             OR (SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
                   ORDER BY table_schema::text COLLATE "C", table_name::text COLLATE "C",
                     privilege_type::text COLLATE "C", column_name::text COLLATE "C")
                 FROM information_schema.column_privileges
                 WHERE grantee='paqueteria_registration_executor')
               IS DISTINCT FROM ARRAY[
                 'identity.users.created_at:INSERT',
                 'identity.users.id:INSERT',
                 'identity.users.identity_subject:INSERT',
                 'identity.users.status:INSERT',
                 'identity.users.id:SELECT',
                 'identity.users.identity_subject:SELECT',
                 'identity.users.status:SELECT',
                 'organizations.organization_memberships.granted_at:INSERT',
                 'organizations.organization_memberships.id:INSERT',
                 'organizations.organization_memberships.is_default:INSERT',
                 'organizations.organization_memberships.organization_id:INSERT',
                 'organizations.organization_memberships.role:INSERT',
                 'organizations.organization_memberships.status:INSERT',
                 'organizations.organization_memberships.user_id:INSERT',
                 'organizations.organization_memberships.is_default:SELECT',
                 'organizations.organization_memberships.organization_id:SELECT',
                 'organizations.organization_memberships.role:SELECT',
                 'organizations.organization_memberships.status:SELECT',
                 'organizations.organization_memberships.user_id:SELECT',
                 'organizations.organization_memberships.is_default:UPDATE',
                 'organizations.organizations.created_at:INSERT',
                 'organizations.organizations.display_name:INSERT',
                 'organizations.organizations.id:INSERT',
                 'organizations.organizations.legal_name:INSERT',
                 'organizations.organizations.organization_type:INSERT',
                 'organizations.organizations.self_service_creator_user_id:INSERT',
                 'organizations.organizations.status:INSERT',
                 'organizations.organizations.created_at:SELECT',
                 'organizations.organizations.display_name:SELECT',
                 'organizations.organizations.id:SELECT',
                 'organizations.organizations.legal_name:SELECT',
                 'organizations.organizations.organization_type:SELECT',
                 'organizations.organizations.self_service_creator_user_id:SELECT',
                 'organizations.organizations.status:SELECT',
                 'organizations.organizations.status:UPDATE',
                 'organizations.pending_memberships.created_at:INSERT',
                 'organizations.pending_memberships.email_hmac:INSERT',
                 'organizations.pending_memberships.email_hmac_key_version:INSERT',
                 'organizations.pending_memberships.expires_at:INSERT',
                 'organizations.pending_memberships.id:INSERT',
                 'organizations.pending_memberships.invited_by:INSERT',
                 'organizations.pending_memberships.organization_id:INSERT',
                 'organizations.pending_memberships.role:INSERT',
                 'organizations.pending_memberships.status:INSERT',
                 'organizations.pending_memberships.created_at:SELECT',
                 'organizations.pending_memberships.email_hmac:SELECT',
                 'organizations.pending_memberships.email_hmac_key_version:SELECT',
                 'organizations.pending_memberships.expires_at:SELECT',
                 'organizations.pending_memberships.id:SELECT',
                 'organizations.pending_memberships.organization_id:SELECT',
                 'organizations.pending_memberships.role:SELECT',
                 'organizations.pending_memberships.status:SELECT',
                 'organizations.pending_memberships.accepted_at:UPDATE',
                 'organizations.pending_memberships.accepted_user_id:UPDATE',
                 'organizations.pending_memberships.expires_at:UPDATE',
                 'organizations.pending_memberships.revoked_at:UPDATE',
                 'organizations.pending_memberships.status:UPDATE',
                 'platform.audit_logs.action:INSERT',
                 'platform.audit_logs.actor_id:INSERT',
                 'platform.audit_logs.entity_id:INSERT',
                 'platform.audit_logs.entity_type:INSERT',
                 'platform.audit_logs.id:INSERT',
                 'platform.audit_logs.occurred_at:INSERT',
                 'platform.audit_logs.org_id:INSERT',
                 'platform.audit_logs.payload_redacted:INSERT',
                 'platform.audit_logs.request_id:INSERT'
               ]
          THEN
            RAISE EXCEPTION 'paqueteria_registration_executor privileges differ from the REG-002 contract';
          END IF;

          -- Runtime roles never write pending memberships directly.
          IF NOT has_table_privilege('paqueteria_app', 'organizations.pending_memberships', 'SELECT')
             OR has_table_privilege('paqueteria_app', 'organizations.pending_memberships', 'INSERT')
             OR has_table_privilege('paqueteria_app', 'organizations.pending_memberships', 'UPDATE')
             OR has_table_privilege('paqueteria_app', 'organizations.pending_memberships', 'DELETE')
             OR has_any_column_privilege('paqueteria_app', 'organizations.pending_memberships', 'INSERT')
             OR has_any_column_privilege('paqueteria_app', 'organizations.pending_memberships', 'UPDATE')
             OR has_any_column_privilege('paqueteria_worker', 'organizations.pending_memberships', 'SELECT')
             OR has_any_column_privilege('paqueteria_worker', 'organizations.pending_memberships', 'INSERT')
             OR has_any_column_privilege('paqueteria_worker', 'organizations.pending_memberships', 'UPDATE')
             OR has_table_privilege('paqueteria_worker', 'organizations.pending_memberships', 'DELETE')
             OR NOT EXISTS (
               SELECT 1 FROM pg_class
               WHERE oid='organizations.pending_memberships'::regclass AND relrowsecurity AND relforcerowsecurity)
             OR NOT EXISTS (
               SELECT 1 FROM pg_policy
               WHERE polrelid='organizations.pending_memberships'::regclass AND polname='pending_memberships_tenant')
          THEN
            RAISE EXCEPTION 'organizations.pending_memberships runtime privileges differ from the REG-002 contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// REG-002 rollback. It removes the four functions: the add, renew and revoke endpoints answer 503
    /// and sign-in stops applying entries. The table, its rows, its policy and the executor grants stay:
    /// on fresh installations AI-06/AI-18 own them, entries are inert without the functions, and
    /// re-applying the lane adopts them and resumes exactly where it stopped.
    /// </summary>
    public const string DownSql =
        """
        RESET ROLE;
        DROP FUNCTION IF EXISTS security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text);
        DROP FUNCTION IF EXISTS security.renew_pending_membership(uuid,uuid,uuid,text,text);
        DROP FUNCTION IF EXISTS security.revoke_pending_membership(uuid,uuid,uuid,text,text);
        DROP FUNCTION IF EXISTS security.apply_pending_memberships(text,bytea[],integer[]);
        SET LOCAL ROLE paqueteria_migrator;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
