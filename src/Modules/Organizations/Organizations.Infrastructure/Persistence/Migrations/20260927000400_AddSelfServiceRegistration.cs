using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Organizations.Infrastructure.Persistence.Migrations;

/// <summary>
/// REG-001 (owner decisions AUTH-OPEN-REGISTRATION, AUTH-EMAIL-VERIFIED-REQUIRED,
/// REG-SELF-SERVICE-ORGANIZATION, REG-ALLY-APPROVAL-PATH, REG-ALLY-APPROVAL-ACTIVATE-ONLY,
/// REG-ALLY-REJECTION-CLOSED and REG-OWN-APPLICATIONS-ENDPOINT) installs open registration:
/// <list type="bullet">
/// <item><c>organizations.organizations.self_service_creator_user_id</c> and the partial unique index
/// <c>organizations_one_open_self_service_uq</c> (REG-ONE-ORGANIZATION-PER-PERSON);</item>
/// <item><c>organizations.organizations.status</c> admits <c>PENDING_APPROVAL</c>, the state of a
/// self-registered ALLY until a PLATFORM_ADMIN approves (ACTIVE) or rejects (CLOSED) it;</item>
/// <item>the <c>paqueteria_registration_executor NOLOGIN BYPASSRLS</c> role with exact column grants;</item>
/// <item>five SECURITY DEFINER functions it owns, executable only by <c>paqueteria_app</c>:
/// <c>security.register_identity_subject(text,uuid)</c> (first sign-in, exactly once per subject),
/// <c>security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)</c>
/// (onboarding, one organization not CLOSED per creator),
/// <c>security.list_own_organization_applications(uuid)</c>,
/// <c>security.list_pending_ally_organizations(uuid,uuid,integer)</c> and
/// <c>security.decide_ally_organization(uuid,uuid,uuid,boolean,text)</c>.</item>
/// </list>
/// AI-06/AI-18 already carry the status value, the role and its grants on fresh installations; this
/// lane adopts them there and creates them on populated installations, refusing any shape that
/// differs. The functions exist only in this lane (ADR-034 pattern). The bootstrap role never writes.
/// </summary>
[DbContext(typeof(OrganizationsDbContext))]
[Migration(MigrationId)]
public sealed class AddSelfServiceRegistration : Migration
{
    public const string MigrationId = "20260927000400_AddSelfServiceRegistration";
    public const string ExecutorRole = "paqueteria_registration_executor";
    public const string RegisterSubjectSignature = "security.register_identity_subject(text,uuid)";
    public const string CreateOrganizationSignature =
        "security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)";
    public const string OwnApplicationsSignature = "security.list_own_organization_applications(uuid)";
    public const string PendingAlliesSignature = "security.list_pending_ally_organizations(uuid,uuid,integer)";
    public const string DecideAllySignature = "security.decide_ally_organization(uuid,uuid,uuid,boolean,text)";
    public const int MaximumPendingPageSize = 200;
    public const int MaximumOwnApplications = 50;

    public static IReadOnlyList<string> OwnedFunctions { get; } = Array.AsReadOnly(new[]
    {
        RegisterSubjectSignature, CreateOrganizationSignature, OwnApplicationsSignature, PendingAlliesSignature, DecideAllySignature,
    });

    public const string PreviousStatusCheck =
        "CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'SUSPENDED'::text, 'CLOSED'::text])))";

    public const string StatusCheck =
        "CHECK ((status = ANY (ARRAY['ACTIVE'::text, 'PENDING_APPROVAL'::text, 'SUSPENDED'::text, 'CLOSED'::text])))";

    public const string UpSql =
        """
        DO $adoption$
        DECLARE
          v_status_check text;
        BEGIN
          IF to_regclass('organizations.organizations') IS NULL
             OR to_regclass('organizations.organization_memberships') IS NULL
             OR to_regclass('identity.users') IS NULL
             OR to_regclass('platform.audit_logs') IS NULL THEN
            RAISE EXCEPTION 'REG-001 requires the canonical AI-06 identity, organization and audit tables';
          END IF;

          SELECT pg_get_constraintdef(c.oid) INTO v_status_check
          FROM pg_constraint c
          WHERE c.conrelid='organizations.organizations'::regclass
            AND c.conname='organizations_status_check';
          IF v_status_check = 'CHECK ((status = ANY (ARRAY[''ACTIVE''::text, ''SUSPENDED''::text, ''CLOSED''::text])))' THEN
            ALTER TABLE organizations.organizations DROP CONSTRAINT organizations_status_check;
            ALTER TABLE organizations.organizations ADD CONSTRAINT organizations_status_check
              CHECK (status IN ('ACTIVE','PENDING_APPROVAL','SUSPENDED','CLOSED'));
          ELSIF v_status_check IS DISTINCT FROM
            'CHECK ((status = ANY (ARRAY[''ACTIVE''::text, ''PENDING_APPROVAL''::text, ''SUSPENDED''::text, ''CLOSED''::text])))' THEN
            RAISE EXCEPTION 'organizations.organizations status check does not match the canonical AI-06 contract';
          END IF;

          IF NOT EXISTS (
            SELECT 1 FROM pg_index i
            JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=i.indkey[0]
            WHERE i.indrelid='identity.users'::regclass AND i.indisunique AND i.indnatts=1
              AND a.attname='identity_subject'
          ) THEN
            RAISE EXCEPTION 'REG-001 requires UNIQUE(identity_subject) on identity.users';
          END IF;

          -- REG-ONE-ORGANIZATION-PER-PERSON: who created an organization through self-service
          -- onboarding. NULL for every organization created any other way.
          IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organizations'
              AND column_name='self_service_creator_user_id')
          THEN
            ALTER TABLE organizations.organizations
              ADD COLUMN self_service_creator_user_id uuid REFERENCES identity.users(id);
          ELSIF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='organizations' AND table_name='organizations'
              AND column_name='self_service_creator_user_id' AND data_type='uuid' AND is_nullable='YES')
            OR NOT EXISTS (
            SELECT 1 FROM pg_constraint c
            WHERE c.conrelid='organizations.organizations'::regclass AND c.contype='f'
              AND c.confrelid='identity.users'::regclass
              AND c.conkey = ARRAY[(
                SELECT attnum FROM pg_attribute
                WHERE attrelid='organizations.organizations'::regclass
                  AND attname='self_service_creator_user_id')]::smallint[])
          THEN
            RAISE EXCEPTION 'organizations.organizations.self_service_creator_user_id does not match the canonical AI-06 contract';
          END IF;

          -- The race-safe limit: one organization not CLOSED per self-service creator.
          IF to_regclass('organizations.organizations_one_open_self_service_uq') IS NULL THEN
            CREATE UNIQUE INDEX organizations_one_open_self_service_uq
              ON organizations.organizations(self_service_creator_user_id)
              WHERE self_service_creator_user_id IS NOT NULL AND status <> 'CLOSED';
          ELSIF pg_get_indexdef('organizations.organizations_one_open_self_service_uq'::regclass) IS DISTINCT FROM
            'CREATE UNIQUE INDEX organizations_one_open_self_service_uq ON organizations.organizations USING btree (self_service_creator_user_id) WHERE ((self_service_creator_user_id IS NOT NULL) AND (status <> ''CLOSED''::text))'
          THEN
            RAISE EXCEPTION 'organizations_one_open_self_service_uq does not match the canonical AI-06 contract';
          END IF;

          -- Registration writes only the columns granted below; a user trigger could widen that. The one
          -- known trigger is NTF-001's organizations_provision_notification_templates, which gives every
          -- new organization its default notification templates and is exactly what a self-registered
          -- organization needs; any other trigger is refused.
          IF EXISTS (
            SELECT 1 FROM pg_trigger t
            WHERE t.tgrelid IN ('identity.users'::regclass, 'organizations.organizations'::regclass)
              AND NOT t.tgisinternal
              AND NOT (t.tgrelid = 'organizations.organizations'::regclass
                AND t.tgname = 'organizations_provision_notification_templates'
                AND t.tgfoid = to_regprocedure('notifications.provision_default_templates()'))
          ) THEN
            RAISE EXCEPTION 'REG-001 refuses identity or organization tables with user triggers';
          END IF;
        END
        $adoption$;

        RESET ROLE;

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_registration_executor NOLOGIN BYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $existing_role$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_registration_executor'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR NOT rolbypassrls OR NOT rolinherit)
          ) THEN
            RAISE EXCEPTION 'paqueteria_registration_executor exists with attributes outside the REG-001 contract';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.member='paqueteria_registration_executor'::regrole
          ) THEN
            RAISE EXCEPTION 'paqueteria_registration_executor must not inherit any other role';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_registration_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner='paqueteria_registration_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner='paqueteria_registration_executor'::regrole)
             OR EXISTS (
               SELECT 1 FROM pg_proc
               WHERE proowner='paqueteria_registration_executor'::regrole
                 AND oid IS DISTINCT FROM to_regprocedure('security.register_identity_subject(text,uuid)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.list_own_organization_applications(uuid)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.list_pending_ally_organizations(uuid,uuid,integer)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.decide_ally_organization(uuid,uuid,uuid,boolean,text)')
             ) THEN
            RAISE EXCEPTION 'paqueteria_registration_executor already owns objects outside the REG-001 contract';
          END IF;
        END
        $existing_role$;

        REVOKE paqueteria_registration_executor FROM paqueteria_app, paqueteria_worker;

        GRANT USAGE ON SCHEMA identity,organizations,platform TO paqueteria_registration_executor;
        GRANT SELECT (id,identity_subject,status) ON identity.users TO paqueteria_registration_executor;
        GRANT INSERT (id,identity_subject,status,created_at) ON identity.users TO paqueteria_registration_executor;
        GRANT SELECT (id,organization_type,legal_name,display_name,status,self_service_creator_user_id,created_at) ON organizations.organizations TO paqueteria_registration_executor;
        GRANT INSERT (id,organization_type,legal_name,display_name,status,self_service_creator_user_id,created_at) ON organizations.organizations TO paqueteria_registration_executor;
        GRANT UPDATE (status) ON organizations.organizations TO paqueteria_registration_executor;
        GRANT SELECT (user_id,organization_id,role,status,is_default) ON organizations.organization_memberships TO paqueteria_registration_executor;
        GRANT INSERT (id,user_id,organization_id,role,status,is_default,granted_at) ON organizations.organization_memberships TO paqueteria_registration_executor;
        GRANT UPDATE (is_default) ON organizations.organization_memberships TO paqueteria_registration_executor;
        GRANT INSERT (id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at) ON platform.audit_logs TO paqueteria_registration_executor;

        -- AUTH-OPEN-REGISTRATION: the first sign-in of a verified subject creates its user exactly once.
        -- UNIQUE(identity_subject) arbitrates concurrent first sign-ins; an existing subject is never
        -- relinked, re-activated or modified, whatever its status.
        CREATE OR REPLACE FUNCTION security.register_identity_subject(
          p_identity_subject text,
          p_user_id uuid)
        RETURNS uuid
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, pg_temp
        AS $function$
        DECLARE
          v_user_id uuid;
        BEGIN
          IF p_identity_subject IS NULL OR p_user_id IS NULL
             OR pg_catalog.length(p_identity_subject) < 1 OR pg_catalog.length(p_identity_subject) > 256
             OR p_identity_subject ~ '[[:cntrl:]]' THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG001_ARGUMENT_OUT_OF_RANGE';
          END IF;

          INSERT INTO identity.users AS u (id,identity_subject,status,created_at)
          VALUES (p_user_id,p_identity_subject,'ACTIVE',pg_catalog.clock_timestamp())
          ON CONFLICT (identity_subject) DO NOTHING;

          SELECT u.id INTO v_user_id
          FROM identity.users u
          WHERE u.identity_subject = p_identity_subject;
          RETURN v_user_id;
        END
        $function$;

        -- REG-SELF-SERVICE-ORGANIZATION and REG-ONE-ORGANIZATION-PER-PERSON: an ACTIVE user creates one
        -- BUSINESS (ACTIVE, creator BUSINESS_ADMIN) or one ALLY (PENDING_APPROVAL, creator ALLY_ADMIN)
        -- with its membership and audit row in one transaction. The organization id is derived by the
        -- caller from the user and the Idempotency-Key: a repeated key with the same request replays
        -- the original organization, a repeated key with another request is IDEMPOTENCY_CONFLICT.
        -- organizations_one_open_self_service_uq holds the limit under concurrency: a second
        -- organization that is not CLOSED is LIMIT_REACHED; a CLOSED (rejected) one frees the slot.
        CREATE OR REPLACE FUNCTION security.create_self_service_organization(
          p_user_id uuid,
          p_organization_id uuid,
          p_membership_id uuid,
          p_audit_id uuid,
          p_organization_type text,
          p_legal_name text,
          p_display_name text,
          p_request_id text)
        RETURNS TABLE(
          outcome text,
          organization_id uuid,
          organization_type text,
          legal_name text,
          display_name text,
          status text,
          membership_role text)
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, platform, pg_temp
        AS $function$
        #variable_conflict use_column
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_status text;
          v_role text;
          v_existing record;
        BEGIN
          IF p_user_id IS NULL OR p_organization_id IS NULL OR p_membership_id IS NULL OR p_audit_id IS NULL
             OR p_organization_type IS NULL OR p_organization_type NOT IN ('BUSINESS','ALLY')
             OR p_legal_name IS NULL OR pg_catalog.length(p_legal_name) < 1 OR pg_catalog.length(p_legal_name) > 200
             OR p_legal_name <> pg_catalog.btrim(p_legal_name) OR p_legal_name ~ '[[:cntrl:]]'
             OR p_display_name IS NULL OR pg_catalog.length(p_display_name) < 1 OR pg_catalog.length(p_display_name) > 80
             OR p_display_name <> pg_catalog.btrim(p_display_name) OR p_display_name ~ '[[:cntrl:]]'
             OR (p_request_id IS NOT NULL AND pg_catalog.length(p_request_id) > 128) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG001_ARGUMENT_OUT_OF_RANGE';
          END IF;

          IF NOT EXISTS (SELECT 1 FROM identity.users u WHERE u.id = p_user_id AND u.status = 'ACTIVE') THEN
            RETURN QUERY SELECT 'USER_INACTIVE'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text;
            RETURN;
          END IF;

          v_status := CASE p_organization_type WHEN 'BUSINESS' THEN 'ACTIVE' ELSE 'PENDING_APPROVAL' END;
          v_role := CASE p_organization_type WHEN 'BUSINESS' THEN 'BUSINESS_ADMIN' ELSE 'ALLY_ADMIN' END;

          BEGIN
            INSERT INTO organizations.organizations(
              id,legal_name,display_name,organization_type,status,self_service_creator_user_id,created_at)
            VALUES (
              p_organization_id,p_legal_name,p_display_name,p_organization_type,v_status,p_user_id,v_now);
          EXCEPTION WHEN unique_violation THEN
            -- Either the same Idempotency-Key (the derived primary key already exists) or the
            -- one-open-organization limit. The row that won is committed by now.
            SELECT o.id, o.organization_type, o.legal_name, o.display_name, o.status,
                   o.self_service_creator_user_id
            INTO v_existing
            FROM organizations.organizations o
            WHERE o.id = p_organization_id;
            IF NOT FOUND THEN
              RETURN QUERY SELECT 'LIMIT_REACHED'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text;
              RETURN;
            END IF;
            IF v_existing.self_service_creator_user_id IS DISTINCT FROM p_user_id
               OR v_existing.organization_type IS DISTINCT FROM p_organization_type
               OR v_existing.legal_name IS DISTINCT FROM p_legal_name
               OR v_existing.display_name IS DISTINCT FROM p_display_name THEN
              RETURN QUERY SELECT 'IDEMPOTENCY_CONFLICT'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text;
              RETURN;
            END IF;
            RETURN QUERY SELECT 'REPLAYED'::text, v_existing.id, v_existing.organization_type,
              v_existing.legal_name, v_existing.display_name, v_existing.status, v_role;
            RETURN;
          END;

          -- REG-DEFAULT-MEMBERSHIP-RELEASE: a default membership whose organization is no longer ACTIVE
          -- (a rejected ALLY, for example) grants no access and must not keep the one-default slot;
          -- releasing it lets the new organization become the usable default. A default inside an
          -- ACTIVE organization is never touched.
          UPDATE organizations.organization_memberships m
          SET is_default = false
          WHERE m.user_id = p_user_id
            AND m.status = 'ACTIVE'
            AND m.is_default
            AND NOT EXISTS (
              SELECT 1 FROM organizations.organizations o
              WHERE o.id = m.organization_id AND o.status = 'ACTIVE');

          INSERT INTO organizations.organization_memberships(
            id,user_id,organization_id,role,status,is_default,granted_at)
          VALUES (
            p_membership_id,p_user_id,p_organization_id,v_role,'ACTIVE',
            NOT EXISTS (
              SELECT 1
              FROM organizations.organization_memberships m
              JOIN organizations.organizations o ON o.id = m.organization_id
              WHERE m.user_id = p_user_id AND m.status = 'ACTIVE' AND m.is_default
                AND o.status = 'ACTIVE'),
            v_now);

          INSERT INTO platform.audit_logs(
            id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
          VALUES (
            p_audit_id,p_organization_id,p_user_id,'ORGANIZATION_SELF_REGISTERED','ORGANIZATION',
            p_organization_id,p_request_id,
            pg_catalog.jsonb_build_object('organization_type', p_organization_type),v_now);

          RETURN QUERY SELECT 'CREATED'::text, p_organization_id, p_organization_type,
            p_legal_name, p_display_name, v_status, v_role;
        END
        $function$;

        -- REG-OWN-APPLICATIONS-ENDPOINT: the organizations the caller administers, whatever their
        -- status, so a pending or rejected ALLY is visible to its creator without granting access.
        CREATE OR REPLACE FUNCTION security.list_own_organization_applications(p_user_id uuid)
        RETURNS TABLE(
          organization_id uuid,
          organization_type text,
          display_name text,
          status text,
          created_at timestamptz)
        LANGUAGE plpgsql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, pg_temp
        AS $function$
        #variable_conflict use_column
        BEGIN
          IF p_user_id IS NULL THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG001_ARGUMENT_OUT_OF_RANGE';
          END IF;

          RETURN QUERY
          SELECT o.id, o.organization_type, o.display_name, o.status, o.created_at
          FROM organizations.organizations o
          WHERE o.organization_type IN ('ALLY','BUSINESS')
            AND EXISTS (
              SELECT 1 FROM identity.users u
              WHERE u.id = p_user_id AND u.status = 'ACTIVE')
            AND EXISTS (
              SELECT 1 FROM organizations.organization_memberships m
              WHERE m.user_id = p_user_id
                AND m.organization_id = o.id
                AND m.status = 'ACTIVE'
                AND m.role IN ('ALLY_ADMIN','BUSINESS_ADMIN'))
          ORDER BY o.created_at DESC, o.id
          LIMIT 50;
        END
        $function$;

        -- REG-ALLY-APPROVAL-PATH: only an ACTIVE PLATFORM_ADMIN member of an ACTIVE PLATFORM organization
        -- may list the pending ALLY organizations; anyone else receives 42501 and reads nothing.
        CREATE OR REPLACE FUNCTION security.list_pending_ally_organizations(
          p_actor_user_id uuid,
          p_platform_org_id uuid,
          p_limit integer)
        RETURNS TABLE(
          organization_id uuid,
          legal_name text,
          display_name text,
          created_at timestamptz)
        LANGUAGE plpgsql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, pg_temp
        AS $function$
        #variable_conflict use_column
        BEGIN
          IF p_actor_user_id IS NULL OR p_platform_org_id IS NULL
             OR p_limit IS NULL OR p_limit < 1 OR p_limit > 200 THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG001_ARGUMENT_OUT_OF_RANGE';
          END IF;

          IF NOT EXISTS (
            SELECT 1
            FROM organizations.organization_memberships m
            JOIN organizations.organizations p ON p.id = m.organization_id
            JOIN identity.users u ON u.id = m.user_id
            WHERE m.user_id = p_actor_user_id
              AND m.organization_id = p_platform_org_id
              AND m.role = 'PLATFORM_ADMIN'
              AND m.status = 'ACTIVE'
              AND u.status = 'ACTIVE'
              AND p.organization_type = 'PLATFORM'
              AND p.status = 'ACTIVE'
          ) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'REG001_PLATFORM_ADMIN_REQUIRED';
          END IF;

          RETURN QUERY
          SELECT o.id, o.legal_name, o.display_name, o.created_at
          FROM organizations.organizations o
          WHERE o.organization_type = 'ALLY'
            AND o.status = 'PENDING_APPROVAL'
          ORDER BY o.created_at, o.id
          LIMIT p_limit;
        END
        $function$;

        -- REG-ALLY-APPROVAL-ACTIVATE-ONLY and REG-ALLY-REJECTION-CLOSED: approval only activates the ALLY
        -- (no allies.ally_relationships row); rejection closes it. One audit row in the platform
        -- organization and one in the ALLY organization, in the same transaction. Repeating the decision
        -- already taken returns it again without new audit rows; any other state is a conflict.
        CREATE OR REPLACE FUNCTION security.decide_ally_organization(
          p_actor_user_id uuid,
          p_platform_org_id uuid,
          p_ally_org_id uuid,
          p_approve boolean,
          p_request_id text)
        RETURNS text
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, organizations, platform, pg_temp
        AS $function$
        DECLARE
          v_status text;
          v_target text;
          v_action text;
          v_now timestamptz := pg_catalog.clock_timestamp();
        BEGIN
          IF p_actor_user_id IS NULL OR p_platform_org_id IS NULL OR p_ally_org_id IS NULL
             OR p_approve IS NULL
             OR (p_request_id IS NOT NULL AND pg_catalog.length(p_request_id) > 128) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'REG001_ARGUMENT_OUT_OF_RANGE';
          END IF;

          IF NOT EXISTS (
            SELECT 1
            FROM organizations.organization_memberships m
            JOIN organizations.organizations p ON p.id = m.organization_id
            JOIN identity.users u ON u.id = m.user_id
            WHERE m.user_id = p_actor_user_id
              AND m.organization_id = p_platform_org_id
              AND m.role = 'PLATFORM_ADMIN'
              AND m.status = 'ACTIVE'
              AND u.status = 'ACTIVE'
              AND p.organization_type = 'PLATFORM'
              AND p.status = 'ACTIVE'
          ) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'REG001_PLATFORM_ADMIN_REQUIRED';
          END IF;

          SELECT o.status INTO v_status
          FROM organizations.organizations o
          WHERE o.id = p_ally_org_id AND o.organization_type = 'ALLY'
          FOR UPDATE;
          IF NOT FOUND THEN
            RETURN 'NOT_FOUND';
          END IF;

          v_target := CASE WHEN p_approve THEN 'ACTIVE' ELSE 'CLOSED' END;
          IF v_status = v_target THEN
            RETURN v_target;
          END IF;
          IF v_status <> 'PENDING_APPROVAL' THEN
            RETURN 'CONFLICT';
          END IF;

          UPDATE organizations.organizations o
          SET status = v_target
          WHERE o.id = p_ally_org_id AND o.status = 'PENDING_APPROVAL';

          v_action := CASE WHEN p_approve THEN 'ALLY_ORGANIZATION_APPROVED' ELSE 'ALLY_ORGANIZATION_REJECTED' END;
          INSERT INTO platform.audit_logs(
            id,org_id,actor_id,action,entity_type,entity_id,request_id,payload_redacted,occurred_at)
          VALUES
            (pg_catalog.gen_random_uuid(), p_platform_org_id, p_actor_user_id, v_action,
             'ORGANIZATION', p_ally_org_id, p_request_id, '{}'::jsonb, v_now),
            (pg_catalog.gen_random_uuid(), p_ally_org_id, p_actor_user_id, v_action,
             'ORGANIZATION', p_ally_org_id, p_request_id, '{}'::jsonb, v_now);
          RETURN v_target;
        END
        $function$;

        -- ACL first, while the deploying role still owns the functions: ALTER ... OWNER rewrites the
        -- grantor to the new owner.
        REVOKE ALL ON FUNCTION security.register_identity_subject(text,uuid) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.list_own_organization_applications(uuid) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.list_pending_ally_organizations(uuid,uuid,integer) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.decide_ally_organization(uuid,uuid,uuid,boolean,text) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.register_identity_subject(text,uuid) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.list_own_organization_applications(uuid) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.list_pending_ally_organizations(uuid,uuid,integer) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.decide_ally_organization(uuid,uuid,uuid,boolean,text) TO paqueteria_app;
        ALTER FUNCTION security.register_identity_subject(text,uuid) OWNER TO paqueteria_registration_executor;
        ALTER FUNCTION security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text) OWNER TO paqueteria_registration_executor;
        ALTER FUNCTION security.list_own_organization_applications(uuid) OWNER TO paqueteria_registration_executor;
        ALTER FUNCTION security.list_pending_ally_organizations(uuid,uuid,integer) OWNER TO paqueteria_registration_executor;
        ALTER FUNCTION security.decide_ally_organization(uuid,uuid,uuid,boolean,text) OWNER TO paqueteria_registration_executor;

        DO $verify$
        BEGIN
          IF EXISTS (
               SELECT 1
               FROM (VALUES
                 ('security.register_identity_subject(text,uuid)', 'search_path=pg_catalog, identity, pg_temp'),
                 ('security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)', 'search_path=pg_catalog, identity, organizations, platform, pg_temp'),
                 ('security.list_own_organization_applications(uuid)', 'search_path=pg_catalog, identity, organizations, pg_temp'),
                 ('security.list_pending_ally_organizations(uuid,uuid,integer)', 'search_path=pg_catalog, identity, organizations, pg_temp'),
                 ('security.decide_ally_organization(uuid,uuid,uuid,boolean,text)', 'search_path=pg_catalog, identity, organizations, platform, pg_temp')
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
          THEN
            RAISE EXCEPTION 'REG-001 registration function security verification failed';
          END IF;

          IF (SELECT array_agg(table_schema || '.' || table_name || ':' || privilege_type
                ORDER BY table_schema, table_name, privilege_type)
              FROM information_schema.table_privileges
              WHERE grantee='paqueteria_registration_executor') IS NOT NULL
             OR (SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
                   ORDER BY table_schema, table_name, privilege_type, column_name)
                 FROM information_schema.column_privileges
                 WHERE grantee='paqueteria_registration_executor'
                   -- REG-002 owns the pending_memberships grants; a fresh AI-18 already carries them.
                   AND table_name <> 'pending_memberships')
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
            RAISE EXCEPTION 'paqueteria_registration_executor privileges differ from the REG-001 contract';
          END IF;

          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'clients','locations','pricing','orders','dispatch','drivers','routes','custody',
                'incidents','finance','allies','notifications','reporting','security','extensions')
              AND has_schema_privilege('paqueteria_registration_executor', n.oid, 'USAGE')
          ) OR EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','extensions')
              AND has_schema_privilege('paqueteria_registration_executor', n.oid, 'CREATE')
          ) OR NOT has_schema_privilege('paqueteria_registration_executor', 'identity', 'USAGE')
            OR NOT has_schema_privilege('paqueteria_registration_executor', 'organizations', 'USAGE')
            OR NOT has_schema_privilege('paqueteria_registration_executor', 'platform', 'USAGE')
          THEN
            RAISE EXCEPTION 'paqueteria_registration_executor schema privileges differ from the REG-001 contract';
          END IF;

          IF pg_has_role('paqueteria_app', 'paqueteria_registration_executor', 'MEMBER')
             OR pg_has_role('paqueteria_worker', 'paqueteria_registration_executor', 'MEMBER')
             OR (SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_registration_executor'::regrole) <> 5
          THEN
            RAISE EXCEPTION 'paqueteria_registration_executor ownership or membership differs from the REG-001 contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// REG-001 rollback. It removes the five functions (sign-in stops creating users and the
    /// onboarding and approval endpoints answer 503) and restores the three-value status check. It
    /// refuses while any organization is still <c>PENDING_APPROVAL</c>: those rows are decisions a
    /// PLATFORM_ADMIN still owes and are never rewritten silently. Users and organizations already
    /// registered are legitimate records and stay. The role and its grants stay too: on fresh
    /// installations AI-18 owns them, and without the functions they are inert. The creator column and
    /// its partial unique index stay as well, so re-applying the lane keeps the one-organization limit
    /// of everyone who already registered.
    /// </summary>
    public const string DownSql =
        """
        RESET ROLE;
        DROP FUNCTION IF EXISTS security.register_identity_subject(text,uuid);
        DROP FUNCTION IF EXISTS security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text);
        DROP FUNCTION IF EXISTS security.list_own_organization_applications(uuid);
        DROP FUNCTION IF EXISTS security.list_pending_ally_organizations(uuid,uuid,integer);
        DROP FUNCTION IF EXISTS security.decide_ally_organization(uuid,uuid,uuid,boolean,text);
        SET LOCAL ROLE paqueteria_migrator;

        -- FORCE ROW LEVEL SECURITY hides every row from the migrator, so the constraint itself is the
        -- guard: re-adding the three-value check fails on any PENDING_APPROVAL row, and the whole
        -- downgrade (functions included) rolls back.
        DO $downgrade$
        BEGIN
          ALTER TABLE organizations.organizations DROP CONSTRAINT organizations_status_check;
          ALTER TABLE organizations.organizations ADD CONSTRAINT organizations_status_check
            CHECK (status IN ('ACTIVE','SUSPENDED','CLOSED'));
        EXCEPTION WHEN check_violation THEN
          RAISE EXCEPTION USING
            MESSAGE = 'REG001_DOWNGRADE_BLOCKED_PENDING_ORGANIZATIONS',
            DETAIL = 'Approve or reject every PENDING_APPROVAL organization before rolling REG-001 back.',
            ERRCODE = 'P0001';
        END
        $downgrade$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(DownSql);
}
