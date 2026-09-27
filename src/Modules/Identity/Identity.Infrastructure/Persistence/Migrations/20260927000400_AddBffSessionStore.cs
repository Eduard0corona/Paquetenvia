using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// BFF-SESSION-TABLE-SHAPE (BFF-SESSION-STORE-POSTGRESQL, AUTH-001-BACKCHANNEL-LOGOUT) installs the
/// PostgreSQL BFF session store: the pre-tenant <c>identity.bff_sessions</c> table and the five
/// SECURITY DEFINER functions <c>security.create_bff_session</c>, <c>security.resolve_bff_session(bytea)</c>
/// and the three <c>security.revoke_bff_session</c> overloads (by key hash, by AuthCenter <c>sid</c>, by
/// subject issued at or before a moment), owned by the dedicated
/// <c>paqueteria_session_executor NOLOGIN BYPASSRLS</c> role and executable only by <c>paqueteria_app</c>.
/// The table stores the SHA-256 of the opaque session key and a Data Protection ciphertext of the
/// ticket; runtime roles hold no privilege on it and FORCE RLS without any policy denies every role
/// that does not bypass RLS. Fresh installations receive the table and the role grants from AI-06/AI-18
/// and this lane adopts them only when they are exactly canonical; populated installations predate
/// them, so the lane creates both. Nothing is dropped and no row is rewritten.
/// </summary>
[DbContext(typeof(IdentityDbContext))]
[Migration(MigrationId)]
public sealed class AddBffSessionStore : Migration
{
    public const string MigrationId = "20260927000400_AddBffSessionStore";
    public const string ExecutorRole = "paqueteria_session_executor";
    public const string Table = "identity.bff_sessions";
    public const string CreateSignature =
        "security.create_bff_session(bytea,text,text,bytea,timestamp with time zone)";
    public const string ResolveSignature = "security.resolve_bff_session(bytea)";
    public const string RevokeByKeySignature = "security.revoke_bff_session(bytea)";
    public const string RevokeBySessionIdSignature = "security.revoke_bff_session(text)";
    public const string RevokeBySubjectSignature =
        "security.revoke_bff_session(text,timestamp with time zone)";
    public const string SearchPath = "search_path=pg_catalog, identity, pg_temp";

    /// <summary>The longest session the function accepts: AuthCenter:SessionLifetimeMinutes caps at 1440.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);

    /// <summary>Clock tolerance between an API replica and PostgreSQL on the requested expiry.</summary>
    public static readonly TimeSpan ExpiryClockTolerance = TimeSpan.FromMinutes(5);

    public const int KeyHashLength = 32;
    public const int MaximumIdentifierLength = 256;
    public const int MaximumTicketLength = 65_536;

    public static IReadOnlyList<string> OwnedFunctions { get; } = Array.AsReadOnly(new[]
    {
        CreateSignature,
        ResolveSignature,
        RevokeByKeySignature,
        RevokeBySessionIdSignature,
        RevokeBySubjectSignature,
    });

    public const string UpSql =
        """
        RESET ROLE;

        DO $adoption$
        DECLARE
          v_columns text[];
          v_constraints text[];
          v_indexes text[];
        BEGIN
          IF to_regnamespace('identity') IS NULL OR to_regnamespace('security') IS NULL THEN
            RAISE EXCEPTION 'BFF-SESSION-TABLE-SHAPE requires the canonical AI-06 identity and security schemas';
          END IF;

          -- A fresh AI-06 baseline already created the table; it is adopted only when it is exactly canonical.
          IF to_regclass('identity.bff_sessions') IS NOT NULL THEN
            SELECT array_agg(column_name || ':' || data_type || ':' || is_nullable ORDER BY column_name)
            INTO v_columns
            FROM information_schema.columns
            WHERE table_schema='identity' AND table_name='bff_sessions';
            IF v_columns IS DISTINCT FROM ARRAY[
              'authcenter_sid:text:YES',
              'created_at:timestamp with time zone:NO',
              'expires_at:timestamp with time zone:NO',
              'identity_subject:text:NO',
              'revoked_at:timestamp with time zone:YES',
              'session_key_hash:bytea:NO',
              'ticket_ciphertext:bytea:YES'
            ] THEN
              RAISE EXCEPTION 'identity.bff_sessions columns do not match the canonical AI-06 contract';
            END IF;

            SELECT array_agg(conname || ':' || pg_get_constraintdef(oid) ORDER BY conname)
            INTO v_constraints
            FROM pg_constraint
            WHERE conrelid='identity.bff_sessions'::regclass AND contype IN ('c','p','u','f','x');
            IF v_constraints IS DISTINCT FROM ARRAY[
              'bff_sessions_key_hash_ck:CHECK ((octet_length(session_key_hash) = 32))',
              'bff_sessions_lifetime_ck:CHECK ((expires_at > created_at))',
              'bff_sessions_pkey:PRIMARY KEY (session_key_hash)',
              'bff_sessions_revocation_ck:CHECK (((revoked_at IS NULL) = (ticket_ciphertext IS NOT NULL)))',
              'bff_sessions_sid_ck:CHECK (((authcenter_sid IS NULL) OR ((char_length(authcenter_sid) >= 1) AND (char_length(authcenter_sid) <= 256))))',
              'bff_sessions_subject_ck:CHECK (((char_length(identity_subject) >= 1) AND (char_length(identity_subject) <= 256)))',
              'bff_sessions_ticket_ck:CHECK (((ticket_ciphertext IS NULL) OR ((octet_length(ticket_ciphertext) >= 1) AND (octet_length(ticket_ciphertext) <= 65536))))'
            ] THEN
              RAISE EXCEPTION 'identity.bff_sessions constraints do not match the canonical AI-06 contract';
            END IF;

            SELECT array_agg(pg_get_indexdef(indexrelid) ORDER BY pg_get_indexdef(indexrelid))
            INTO v_indexes
            FROM pg_index
            WHERE indrelid='identity.bff_sessions'::regclass;
            IF v_indexes IS DISTINCT FROM ARRAY[
              'CREATE INDEX bff_sessions_expiry_idx ON identity.bff_sessions USING btree (expires_at)',
              'CREATE INDEX bff_sessions_revoked_idx ON identity.bff_sessions USING btree (revoked_at) WHERE (revoked_at IS NOT NULL)',
              'CREATE INDEX bff_sessions_sid_idx ON identity.bff_sessions USING btree (authcenter_sid) WHERE ((authcenter_sid IS NOT NULL) AND (revoked_at IS NULL))',
              'CREATE INDEX bff_sessions_subject_idx ON identity.bff_sessions USING btree (identity_subject, created_at) WHERE (revoked_at IS NULL)',
              'CREATE UNIQUE INDEX bff_sessions_pkey ON identity.bff_sessions USING btree (session_key_hash)'
            ] THEN
              RAISE EXCEPTION 'identity.bff_sessions indexes do not match the canonical AI-06 contract';
            END IF;

            IF NOT EXISTS (
                 SELECT 1 FROM pg_class
                 WHERE oid='identity.bff_sessions'::regclass AND relrowsecurity AND relforcerowsecurity
                   AND pg_get_userbyid(relowner)='paqueteria_migrator')
               OR EXISTS (SELECT 1 FROM pg_policy WHERE polrelid='identity.bff_sessions'::regclass)
               OR EXISTS (
                 SELECT 1 FROM pg_trigger WHERE tgrelid='identity.bff_sessions'::regclass AND NOT tgisinternal)
            THEN
              RAISE EXCEPTION 'identity.bff_sessions must be owned by paqueteria_migrator with FORCE RLS, no policy and no user trigger';
            END IF;
          END IF;
        END
        $adoption$;

        DO $role$
        BEGIN
          CREATE ROLE paqueteria_session_executor NOLOGIN BYPASSRLS;
        EXCEPTION WHEN duplicate_object THEN NULL;
        END
        $role$;

        DO $existing_role$
        BEGIN
          IF EXISTS (
            SELECT 1 FROM pg_roles
            WHERE rolname='paqueteria_session_executor'
              AND (rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                OR NOT rolbypassrls OR NOT rolinherit)
          ) THEN
            RAISE EXCEPTION 'paqueteria_session_executor exists with attributes outside the BFF-SESSION-TABLE-SHAPE contract';
          END IF;
          IF EXISTS (
            SELECT 1 FROM pg_auth_members m
            WHERE m.member='paqueteria_session_executor'::regrole
          ) THEN
            RAISE EXCEPTION 'paqueteria_session_executor must not inherit any other role';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_class WHERE relowner='paqueteria_session_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_namespace WHERE nspowner='paqueteria_session_executor'::regrole)
             OR EXISTS (SELECT 1 FROM pg_type WHERE typowner='paqueteria_session_executor'::regrole)
             OR EXISTS (
               SELECT 1 FROM pg_proc
               WHERE proowner='paqueteria_session_executor'::regrole
                 AND oid IS DISTINCT FROM to_regprocedure('security.create_bff_session(bytea,text,text,bytea,timestamp with time zone)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.resolve_bff_session(bytea)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.revoke_bff_session(bytea)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.revoke_bff_session(text)')
                 AND oid IS DISTINCT FROM to_regprocedure('security.revoke_bff_session(text,timestamp with time zone)')
             ) THEN
            RAISE EXCEPTION 'paqueteria_session_executor already owns objects outside the BFF-SESSION-TABLE-SHAPE contract';
          END IF;
        END
        $existing_role$;

        REVOKE paqueteria_session_executor FROM paqueteria_app, paqueteria_worker;

        -- The table is created, protected and granted by its owner, exactly as AI-06/AI-18 declare it.
        -- CREATE ... IF NOT EXISTS is a no-op on a fresh baseline, whose shape was verified above.
        SET LOCAL ROLE paqueteria_migrator;

        CREATE TABLE IF NOT EXISTS identity.bff_sessions (
          session_key_hash bytea NOT NULL,
          identity_subject text NOT NULL,
          authcenter_sid text,
          ticket_ciphertext bytea,
          created_at timestamptz NOT NULL,
          expires_at timestamptz NOT NULL,
          revoked_at timestamptz,
          CONSTRAINT bff_sessions_pkey PRIMARY KEY (session_key_hash),
          CONSTRAINT bff_sessions_key_hash_ck CHECK (octet_length(session_key_hash)=32),
          CONSTRAINT bff_sessions_subject_ck CHECK (char_length(identity_subject) BETWEEN 1 AND 256),
          CONSTRAINT bff_sessions_sid_ck CHECK (authcenter_sid IS NULL OR char_length(authcenter_sid) BETWEEN 1 AND 256),
          CONSTRAINT bff_sessions_ticket_ck CHECK (ticket_ciphertext IS NULL OR octet_length(ticket_ciphertext) BETWEEN 1 AND 65536),
          CONSTRAINT bff_sessions_lifetime_ck CHECK (expires_at > created_at),
          CONSTRAINT bff_sessions_revocation_ck CHECK ((revoked_at IS NULL) = (ticket_ciphertext IS NOT NULL))
        );
        CREATE INDEX IF NOT EXISTS bff_sessions_sid_idx ON identity.bff_sessions(authcenter_sid)
          WHERE authcenter_sid IS NOT NULL AND revoked_at IS NULL;
        CREATE INDEX IF NOT EXISTS bff_sessions_subject_idx ON identity.bff_sessions(identity_subject,created_at)
          WHERE revoked_at IS NULL;
        CREATE INDEX IF NOT EXISTS bff_sessions_expiry_idx ON identity.bff_sessions(expires_at);
        CREATE INDEX IF NOT EXISTS bff_sessions_revoked_idx ON identity.bff_sessions(revoked_at)
          WHERE revoked_at IS NOT NULL;
        ALTER TABLE identity.bff_sessions ENABLE ROW LEVEL SECURITY;
        ALTER TABLE identity.bff_sessions FORCE ROW LEVEL SECURITY;

        -- The identity schema's default privileges hand every new table to both runtime roles.
        REVOKE ALL ON identity.bff_sessions FROM PUBLIC, paqueteria_app, paqueteria_worker;
        GRANT USAGE ON SCHEMA identity TO paqueteria_session_executor;
        GRANT SELECT (session_key_hash,identity_subject,authcenter_sid,ticket_ciphertext,created_at,expires_at,revoked_at)
          ON identity.bff_sessions TO paqueteria_session_executor;
        GRANT INSERT (session_key_hash,identity_subject,authcenter_sid,ticket_ciphertext,created_at,expires_at)
          ON identity.bff_sessions TO paqueteria_session_executor;
        GRANT UPDATE (ticket_ciphertext,revoked_at) ON identity.bff_sessions TO paqueteria_session_executor;

        RESET ROLE;

        CREATE OR REPLACE FUNCTION security.create_bff_session(
          p_session_key_hash bytea,
          p_identity_subject text,
          p_authcenter_sid text,
          p_ticket_ciphertext bytea,
          p_expires_at timestamptz)
        RETURNS void
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, pg_temp
        AS $function$
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
        BEGIN
          -- The creation moment is the database clock, so subject-wide revocation compares like with like.
          IF p_session_key_hash IS NULL OR pg_catalog.octet_length(p_session_key_hash) <> 32
             OR p_identity_subject IS NULL
             OR pg_catalog.char_length(p_identity_subject) NOT BETWEEN 1 AND 256
             OR (p_authcenter_sid IS NOT NULL AND pg_catalog.char_length(p_authcenter_sid) NOT BETWEEN 1 AND 256)
             OR p_ticket_ciphertext IS NULL
             OR pg_catalog.octet_length(p_ticket_ciphertext) NOT BETWEEN 1 AND 65536
             OR p_expires_at IS NULL
             OR p_expires_at <= v_now
             OR p_expires_at > v_now + interval '24 hours 5 minutes' THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'BFF_SESSION_ARGUMENT_OUT_OF_RANGE';
          END IF;

          INSERT INTO identity.bff_sessions
            (session_key_hash, identity_subject, authcenter_sid, ticket_ciphertext, created_at, expires_at)
          VALUES
            (p_session_key_hash, p_identity_subject, p_authcenter_sid, p_ticket_ciphertext, v_now, p_expires_at);
        END
        $function$;

        CREATE OR REPLACE FUNCTION security.resolve_bff_session(p_session_key_hash bytea)
        RETURNS bytea
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, pg_temp
        AS $function$
        DECLARE
          v_ticket bytea;
        BEGIN
          IF p_session_key_hash IS NULL OR pg_catalog.octet_length(p_session_key_hash) <> 32 THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'BFF_SESSION_ARGUMENT_OUT_OF_RANGE';
          END IF;

          -- Unknown, revoked and expired sessions are indistinguishable: all of them resolve to NULL.
          SELECT s.ticket_ciphertext
          INTO v_ticket
          FROM identity.bff_sessions s
          WHERE s.session_key_hash = p_session_key_hash
            AND s.revoked_at IS NULL
            AND s.expires_at > pg_catalog.clock_timestamp();
          RETURN v_ticket;
        END
        $function$;

        CREATE OR REPLACE FUNCTION security.revoke_bff_session(p_session_key_hash bytea)
        RETURNS integer
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, pg_temp
        AS $function$
        DECLARE
          v_count integer;
        BEGIN
          IF p_session_key_hash IS NULL OR pg_catalog.octet_length(p_session_key_hash) <> 32 THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'BFF_SESSION_ARGUMENT_OUT_OF_RANGE';
          END IF;

          -- Revocation erases the ticket at once; the cleanup executor purges the row later.
          UPDATE identity.bff_sessions s
          SET revoked_at = pg_catalog.clock_timestamp(), ticket_ciphertext = NULL
          WHERE s.session_key_hash = p_session_key_hash
            AND s.revoked_at IS NULL;
          GET DIAGNOSTICS v_count = ROW_COUNT;
          RETURN v_count;
        END
        $function$;

        CREATE OR REPLACE FUNCTION security.revoke_bff_session(p_authcenter_sid text)
        RETURNS integer
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, pg_temp
        AS $function$
        DECLARE
          v_count integer;
        BEGIN
          IF p_authcenter_sid IS NULL OR pg_catalog.char_length(p_authcenter_sid) NOT BETWEEN 1 AND 256 THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'BFF_SESSION_ARGUMENT_OUT_OF_RANGE';
          END IF;

          -- AUTH-001-BACKCHANNEL-LOGOUT: every BFF session created from this AuthCenter session ends.
          UPDATE identity.bff_sessions s
          SET revoked_at = pg_catalog.clock_timestamp(), ticket_ciphertext = NULL
          WHERE s.authcenter_sid = p_authcenter_sid
            AND s.revoked_at IS NULL;
          GET DIAGNOSTICS v_count = ROW_COUNT;
          RETURN v_count;
        END
        $function$;

        CREATE OR REPLACE FUNCTION security.revoke_bff_session(
          p_identity_subject text,
          p_issued_before timestamptz)
        RETURNS integer
        LANGUAGE plpgsql
        VOLATILE
        SECURITY DEFINER
        SET search_path = pg_catalog, identity, pg_temp
        AS $function$
        DECLARE
          v_now timestamptz := pg_catalog.clock_timestamp();
          v_count integer;
        BEGIN
          IF p_identity_subject IS NULL OR pg_catalog.char_length(p_identity_subject) NOT BETWEEN 1 AND 256
             OR p_issued_before IS NULL THEN
            RAISE EXCEPTION USING
              ERRCODE = '22023',
              MESSAGE = 'BFF_SESSION_ARGUMENT_OUT_OF_RANGE';
          END IF;

          -- A sub-only logout token ends the sessions signed in at or before it; a later or infinite
          -- moment is clamped to this function's clock, so a session created afterwards survives.
          UPDATE identity.bff_sessions s
          SET revoked_at = v_now, ticket_ciphertext = NULL
          WHERE s.identity_subject = p_identity_subject
            AND s.created_at <= LEAST(p_issued_before, v_now)
            AND s.revoked_at IS NULL;
          GET DIAGNOSTICS v_count = ROW_COUNT;
          RETURN v_count;
        END
        $function$;

        -- ACL first, while the deploying role still owns the functions: ALTER ... OWNER rewrites the
        -- grantor to the new owner, and a managed-service deployer then needs no inherited executor rights.
        REVOKE ALL ON FUNCTION security.create_bff_session(bytea,text,text,bytea,timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.resolve_bff_session(bytea) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.revoke_bff_session(bytea) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.revoke_bff_session(text) FROM PUBLIC;
        REVOKE ALL ON FUNCTION security.revoke_bff_session(text,timestamptz) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION security.create_bff_session(bytea,text,text,bytea,timestamptz) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.resolve_bff_session(bytea) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.revoke_bff_session(bytea) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.revoke_bff_session(text) TO paqueteria_app;
        GRANT EXECUTE ON FUNCTION security.revoke_bff_session(text,timestamptz) TO paqueteria_app;
        ALTER FUNCTION security.create_bff_session(bytea,text,text,bytea,timestamptz) OWNER TO paqueteria_session_executor;
        ALTER FUNCTION security.resolve_bff_session(bytea) OWNER TO paqueteria_session_executor;
        ALTER FUNCTION security.revoke_bff_session(bytea) OWNER TO paqueteria_session_executor;
        ALTER FUNCTION security.revoke_bff_session(text) OWNER TO paqueteria_session_executor;
        ALTER FUNCTION security.revoke_bff_session(text,timestamptz) OWNER TO paqueteria_session_executor;

        DO $verify$
        BEGIN
          IF (SELECT count(*) FROM pg_proc p
              WHERE p.oid IN (
                  to_regprocedure('security.create_bff_session(bytea,text,text,bytea,timestamp with time zone)'),
                  to_regprocedure('security.resolve_bff_session(bytea)'),
                  to_regprocedure('security.revoke_bff_session(bytea)'),
                  to_regprocedure('security.revoke_bff_session(text)'),
                  to_regprocedure('security.revoke_bff_session(text,timestamp with time zone)'))
                AND pg_get_userbyid(p.proowner)='paqueteria_session_executor'
                AND p.prosecdef
                AND p.prosrc !~* '(^|[^a-z_])EXECUTE([^a-z_]|$)'
                AND 'search_path=pg_catalog, identity, pg_temp' = ANY(COALESCE(p.proconfig, ARRAY[]::text[]))
                AND NOT has_function_privilege('public', p.oid, 'EXECUTE')
                AND NOT has_function_privilege('paqueteria_worker', p.oid, 'EXECUTE')
                AND has_function_privilege('paqueteria_app', p.oid, 'EXECUTE')) <> 5
             OR (SELECT count(*) FROM pg_proc WHERE proowner='paqueteria_session_executor'::regrole) <> 5
          THEN
            RAISE EXCEPTION 'BFF session function security verification failed';
          END IF;

          -- Exactly: SELECT on the seven columns, INSERT on the six non-revocation columns and
          -- UPDATE(ticket_ciphertext,revoked_at) of identity.bff_sessions; no table-wide privilege.
          IF EXISTS (SELECT 1 FROM information_schema.table_privileges WHERE grantee='paqueteria_session_executor')
             OR (SELECT array_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type
                   ORDER BY table_schema, table_name, privilege_type, column_name)
                 FROM information_schema.column_privileges
                 WHERE grantee='paqueteria_session_executor')
               IS DISTINCT FROM ARRAY[
                 'identity.bff_sessions.authcenter_sid:INSERT',
                 'identity.bff_sessions.created_at:INSERT',
                 'identity.bff_sessions.expires_at:INSERT',
                 'identity.bff_sessions.identity_subject:INSERT',
                 'identity.bff_sessions.session_key_hash:INSERT',
                 'identity.bff_sessions.ticket_ciphertext:INSERT',
                 'identity.bff_sessions.authcenter_sid:SELECT',
                 'identity.bff_sessions.created_at:SELECT',
                 'identity.bff_sessions.expires_at:SELECT',
                 'identity.bff_sessions.identity_subject:SELECT',
                 'identity.bff_sessions.revoked_at:SELECT',
                 'identity.bff_sessions.session_key_hash:SELECT',
                 'identity.bff_sessions.ticket_ciphertext:SELECT',
                 'identity.bff_sessions.revoked_at:UPDATE',
                 'identity.bff_sessions.ticket_ciphertext:UPDATE'
               ]
          THEN
            RAISE EXCEPTION 'paqueteria_session_executor privileges differ from the BFF-SESSION-TABLE-SHAPE contract';
          END IF;

          -- CREATE on security is the one E-002 transaction-scoped grant the ownership transfer needs
          -- under the managed-service model; the Identity lane asserts it is gone after the bridge cleanup.
          IF EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'organizations','clients','locations','pricing','orders','dispatch','drivers','routes','custody',
                'incidents','finance','allies','notifications','reporting','platform','security','extensions')
              AND has_schema_privilege('paqueteria_session_executor', n.oid, 'USAGE')
          ) OR EXISTS (
            SELECT 1 FROM pg_namespace n
            WHERE n.nspname IN (
                'identity','organizations','clients','locations','pricing','orders','dispatch','drivers','routes',
                'custody','incidents','finance','allies','notifications','reporting','platform','extensions')
              AND has_schema_privilege('paqueteria_session_executor', n.oid, 'CREATE')
          ) OR NOT has_schema_privilege('paqueteria_session_executor', 'identity', 'USAGE')
          THEN
            RAISE EXCEPTION 'paqueteria_session_executor schema privileges differ from the BFF-SESSION-TABLE-SHAPE contract';
          END IF;

          -- No runtime role and no PUBLIC grant reaches the table directly.
          IF EXISTS (
               SELECT 1
               FROM pg_class c
               CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl, acldefault('r', c.relowner))) acl
               WHERE c.oid='identity.bff_sessions'::regclass
                 AND (acl.grantee = 0
                   OR acl.grantee IN ('paqueteria_app'::regrole, 'paqueteria_worker'::regrole)))
             OR has_any_column_privilege('paqueteria_app', 'identity.bff_sessions', 'SELECT,INSERT,UPDATE,REFERENCES')
             OR has_any_column_privilege('paqueteria_worker', 'identity.bff_sessions', 'SELECT,INSERT,UPDATE,REFERENCES')
             OR NOT EXISTS (
               SELECT 1 FROM pg_class
               WHERE oid='identity.bff_sessions'::regclass AND relrowsecurity AND relforcerowsecurity
                 AND pg_get_userbyid(relowner)='paqueteria_migrator')
             OR EXISTS (SELECT 1 FROM pg_policy WHERE polrelid='identity.bff_sessions'::regclass)
          THEN
            RAISE EXCEPTION 'identity.bff_sessions is reachable outside the BFF-SESSION-TABLE-SHAPE functions';
          END IF;

          IF pg_has_role('paqueteria_app', 'paqueteria_session_executor', 'MEMBER')
             OR pg_has_role('paqueteria_worker', 'paqueteria_session_executor', 'MEMBER')
             OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member='paqueteria_session_executor'::regrole)
          THEN
            RAISE EXCEPTION 'paqueteria_session_executor membership differs from the BFF-SESSION-TABLE-SHAPE contract';
          END IF;
        END
        $verify$;

        SET LOCAL ROLE paqueteria_migrator;
        """;

    /// <summary>
    /// BFF-SESSION-TABLE-SHAPE rollback: the schema is never downgraded, because dropping the table or the
    /// role would also break a fresh AI-06/AI-18 baseline that declares them. Switch the API back to the
    /// single-instance store with <c>AuthCenter:SessionStore=Memory</c> and, when the database itself has to
    /// stop serving sessions, apply <see cref="OperationalRollbackSql"/> with the deployment credential.
    /// </summary>
    public const string SchemaDowngradeNotSupportedSql =
        """
        DO $schema_downgrade$
        BEGIN
          RAISE EXCEPTION USING
            MESSAGE = 'BFF_SESSION_SCHEMA_DOWNGRADE_NOT_SUPPORTED',
            DETAIL = 'BFF session store rollback blocked: set AuthCenter:SessionStore=Memory and revoke paqueteria_app EXECUTE; the canonical table and role stay.',
            ERRCODE = 'P0001';
        END
        $schema_downgrade$;
        """;

    public const string OperationalRollbackSql =
        """
        REVOKE EXECUTE ON FUNCTION security.create_bff_session(bytea,text,text,bytea,timestamptz) FROM paqueteria_app;
        REVOKE EXECUTE ON FUNCTION security.resolve_bff_session(bytea) FROM paqueteria_app;
        REVOKE EXECUTE ON FUNCTION security.revoke_bff_session(bytea) FROM paqueteria_app;
        REVOKE EXECUTE ON FUNCTION security.revoke_bff_session(text) FROM paqueteria_app;
        REVOKE EXECUTE ON FUNCTION security.revoke_bff_session(text,timestamptz) FROM paqueteria_app;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpSql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaDowngradeNotSupportedSql);
}
