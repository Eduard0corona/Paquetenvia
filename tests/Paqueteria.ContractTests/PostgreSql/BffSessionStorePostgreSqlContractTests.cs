using System.Security.Cryptography;
using System.Text;
using Custody.Infrastructure.Cleanup;
using Custody.Infrastructure.Persistence;
using Custody.Infrastructure.Persistence.Migrations;
using Identity.Application.Bootstrap;
using Identity.Application.Session;
using Identity.Infrastructure.Persistence;
using Identity.Infrastructure.Persistence.Migrations;
using Identity.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// BFF-SESSION-TABLE-SHAPE against real PostgreSQL: exact executor boundary, no direct runtime access,
/// resolution by hash only, revocation by key, by AuthCenter sid and by subject before a moment, the
/// purge through the OPS-003 cleanup role, the pre-tenant (RLS-free) nature of the store and both lane
/// migrations up and down. Every session uses fresh random subjects and sids, so tests never see each
/// other's rows even though the functions are global.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class BffSessionStorePostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Executor = AddBffSessionStore.ExecutorRole;
    private const string CleanupExecutor = AddOperationalCleanupExecutor.ExecutorRole;

    [PostgreSqlContractFact]
    public async Task Session_executor_role_functions_and_grants_match_the_contract_exactly()
    {
        Assert.Equal(
            "f|t|f|f|f|f|t",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|',rolcanlogin,rolbypassrls,rolsuper,rolcreatedb,rolcreaterole,rolreplication,rolinherit)
                FROM pg_roles WHERE rolname=@executor
                """,
                ("executor", Executor)));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM pg_auth_members WHERE member=@executor::regrole", ("executor", Executor)));
        foreach (var runtime in new[] { "paqueteria_app", "paqueteria_worker", PostgreSqlContractFixture.AppLogin, PostgreSqlContractFixture.WorkerLogin })
        {
            Assert.False(await ScalarAsync<bool>(
                "SELECT pg_has_role(@runtime,@executor,'MEMBER') OR pg_has_role(@runtime,@executor,'SET')",
                ("runtime", runtime), ("executor", Executor)));
        }

        Assert.Equal(
            string.Join(',', AddBffSessionStore.OwnedFunctions.Order(StringComparer.Ordinal)),
            await ScalarAsync<string>(
                "SELECT string_agg(oid::regprocedure::text, ',' ORDER BY oid::regprocedure::text COLLATE \"C\") FROM pg_proc WHERE proowner=@executor::regrole",
                ("executor", Executor)));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT (SELECT count(*) FROM pg_class WHERE relowner=@executor::regrole)
                 + (SELECT count(*) FROM pg_namespace WHERE nspowner=@executor::regrole)
                 + (SELECT count(*) FROM pg_type WHERE typowner=@executor::regrole)
            """,
            ("executor", Executor)));
        Assert.Equal(
            "identity.bff_logout_jtis.created_at:INSERT,identity.bff_logout_jtis.expires_at:INSERT," +
            "identity.bff_logout_jtis.jti_hash:INSERT," +
            "identity.bff_sessions.authcenter_sid:INSERT,identity.bff_sessions.authcenter_sid:SELECT," +
            "identity.bff_sessions.created_at:INSERT,identity.bff_sessions.created_at:SELECT," +
            "identity.bff_sessions.expires_at:INSERT,identity.bff_sessions.expires_at:SELECT," +
            "identity.bff_sessions.identity_subject:INSERT,identity.bff_sessions.identity_subject:SELECT," +
            "identity.bff_sessions.revoked_at:SELECT,identity.bff_sessions.revoked_at:UPDATE," +
            "identity.bff_sessions.session_key_hash:INSERT,identity.bff_sessions.session_key_hash:SELECT," +
            "identity.bff_sessions.ticket_ciphertext:INSERT,identity.bff_sessions.ticket_ciphertext:SELECT," +
            "identity.bff_sessions.ticket_ciphertext:UPDATE",
            await ScalarAsync<string>(
                """
                SELECT string_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type, ','
                  ORDER BY table_schema, table_name, column_name, privilege_type)
                FROM information_schema.column_privileges WHERE grantee=@executor
                """,
                ("executor", Executor)));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.table_privileges WHERE grantee=@executor", ("executor", Executor)));
        Assert.False(await ScalarAsync<bool>(
            """
            SELECT has_table_privilege(@executor,'identity.bff_sessions','DELETE')
                OR has_table_privilege(@executor,'identity.bff_sessions','TRUNCATE')
                OR has_column_privilege(@executor,'identity.bff_sessions','session_key_hash','UPDATE')
                OR has_column_privilege(@executor,'identity.bff_sessions','identity_subject','UPDATE')
                OR has_column_privilege(@executor,'identity.bff_sessions','created_at','UPDATE')
                OR has_column_privilege(@executor,'identity.bff_sessions','expires_at','UPDATE')
                OR has_column_privilege(@executor,'identity.bff_sessions','revoked_at','INSERT')
            """,
            ("executor", Executor)));
        foreach (var table in new[] { "identity.users", "organizations.organization_memberships", "platform.audit_logs", "platform.outbox_events", "orders.orders" })
        {
            Assert.False(await ScalarAsync<bool>(
                """
                SELECT has_table_privilege(@executor,@table,'SELECT') OR has_table_privilege(@executor,@table,'INSERT')
                    OR has_table_privilege(@executor,@table,'UPDATE') OR has_table_privilege(@executor,@table,'DELETE')
                    OR has_any_column_privilege(@executor,@table,'SELECT')
                """,
                ("executor", Executor), ("table", table)));
        }

        Assert.Equal("identity", await ScalarAsync<string>(
            """
            SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace
            WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'USAGE')
            """,
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace WHERE nspname=ANY(@schemas::text[]) AND has_schema_privilege(@executor,oid,'CREATE')",
            ("executor", Executor), ("schemas", DatabaseSchemaCatalog.ApplicationSchemas.ToArray())));

        foreach (var (signature, result) in new[]
                 {
                     (AddBffSessionStore.CreateSignature, "void"),
                     (AddBffSessionStore.ResolveSignature, "bytea"),
                     (AddBffSessionStore.RevokeByKeySignature, "integer"),
                     (AddBffSessionStore.RevokeBySessionIdSignature, "integer"),
                     (AddBffSessionStore.RevokeBySubjectSignature, "integer"),
                     (AddBffSessionStore.RegisterLogoutJtiSignature, "boolean"),
                 })
        {
            // SECURITY DEFINER, pinned search_path, EXECUTE for paqueteria_app only, no dynamic SQL.
            Assert.Equal(
                $"t|{AddBffSessionStore.SearchPath}|f|t|f|{result}",
                await ScalarAsync<string>(
                    """
                    SELECT concat_ws('|',p.prosecdef,array_to_string(p.proconfig,';'),
                      has_function_privilege('public',p.oid,'EXECUTE'),
                      has_function_privilege('paqueteria_app',p.oid,'EXECUTE'),
                      has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
                      pg_get_function_result(p.oid))
                    FROM pg_proc p WHERE p.oid=@function::regprocedure
                    """,
                    ("function", signature)));
            Assert.Equal(0, await ScalarAsync<long>(
                "SELECT count(*) FROM pg_proc WHERE oid=@function::regprocedure AND (proretset OR prosrc ~* '(^|[^a-z_])EXECUTE([^a-z_]|$)')",
                ("function", signature)));
            Assert.Equal(
                $"{{{Executor}=X/{Executor},paqueteria_app=X/{Executor}}}",
                await ScalarAsync<string>("SELECT proacl::text FROM pg_proc WHERE oid=@function::regprocedure", ("function", signature)));
        }
    }

    [PostgreSqlContractFact]
    public async Task Runtime_roles_have_no_direct_access_to_the_table()
    {
        foreach (var role in new[] { "paqueteria_app", "paqueteria_worker", "public" })
        {
            foreach (var table in new[] { AddBffSessionStore.Table, AddBffSessionStore.LogoutJtiTable })
            {
                Assert.False(await ScalarAsync<bool>(
                    """
                    SELECT has_table_privilege(@role,@table,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR has_any_column_privilege(@role,@table,'SELECT,INSERT,UPDATE,REFERENCES')
                    """,
                    ("role", role), ("table", table)));
            }
        }

        foreach (var (source, role) in new[] { (fixture.AppDataSource, "paqueteria_app"), (fixture.WorkerDataSource, "paqueteria_worker") })
        {
            foreach (var statement in new[]
                     {
                         "SELECT count(*) FROM identity.bff_sessions",
                         "SELECT session_key_hash FROM identity.bff_sessions LIMIT 1",
                         "INSERT INTO identity.bff_sessions(session_key_hash,identity_subject,created_at,expires_at) VALUES (decode(repeat('00',32),'hex'),'x',now(),now()+interval '1 hour')",
                         "UPDATE identity.bff_sessions SET revoked_at=now()",
                         "DELETE FROM identity.bff_sessions",
                         "TRUNCATE identity.bff_sessions",
                         "SELECT count(*) FROM identity.bff_logout_jtis",
                         "INSERT INTO identity.bff_logout_jtis(jti_hash,created_at,expires_at) VALUES (decode(repeat('00',32),'hex'),now(),now()+interval '1 hour')",
                         "DELETE FROM identity.bff_logout_jtis",
                     })
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await ExecuteAsync(connection, transaction, $"SET LOCAL ROLE {role};");
                var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, statement));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
        }

        // The Worker cannot reach the session functions, and neither login can without assuming its role.
        await using (var worker = await fixture.WorkerDataSource.OpenConnectionAsync())
        {
            await using var transaction = await worker.BeginTransactionAsync();
            await ExecuteAsync(worker, transaction, "SET LOCAL ROLE paqueteria_worker;");
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(worker, transaction, "SELECT security.resolve_bff_session(decode(repeat('00',32),'hex'))"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await using (var app = await fixture.AppDataSource.OpenConnectionAsync())
        {
            await using var noRole = await app.BeginTransactionAsync();
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(app, noRole, "SELECT security.resolve_bff_session(decode(repeat('00',32),'hex'))"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        foreach (var source in new[] { fixture.AppDataSource, fixture.WorkerDataSource })
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var escalate = await connection.BeginTransactionAsync();
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, escalate, $"SET LOCAL ROLE {Executor};"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
    }

    [PostgreSqlContractFact]
    public async Task A_session_resolves_only_by_the_hash_of_its_key_and_the_key_is_never_stored()
    {
        var store = Store();
        var key = NewKey();
        var ticket = RandomNumberGenerator.GetBytes(1_500);
        var subject = NewSubject();
        await store.CreateAsync(new BffSessionRecord(Hash(key), subject, NewSid(), ticket, DateTimeOffset.UtcNow.AddHours(8)), CancellationToken.None);

        Assert.Equal(ticket, await store.ResolveAsync(Hash(key), CancellationToken.None));
        Assert.Null(await store.ResolveAsync(Hash(key + "x"), CancellationToken.None));
        Assert.Null(await store.ResolveAsync(RandomNumberGenerator.GetBytes(32), CancellationToken.None));

        // The raw key bytes, as sent by a browser, appear nowhere in the row; only their SHA-256 does.
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM identity.bff_sessions WHERE identity_subject=@subject AND session_key_hash=@hash",
            ("subject", subject), ("hash", Hash(key))));
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*) FROM identity.bff_sessions s
            WHERE s.identity_subject=@subject
              AND (position(@raw IN s.session_key_hash) > 0 OR position(@raw IN s.ticket_ciphertext) > 0
                OR s.authcenter_sid=@text OR s.identity_subject=@text)
            """,
            ("subject", subject), ("raw", Encoding.UTF8.GetBytes(key)), ("text", key)));

        // A malformed hash is refused by the function itself, not by the port alone.
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_app;");
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, transaction, "SELECT security.resolve_bff_session(decode(repeat('00',31),'hex'))"));
        Assert.Equal("22023", refused.SqlState);
        Assert.Equal("BFF_SESSION_ARGUMENT_OUT_OF_RANGE", refused.MessageText);
    }

    [PostgreSqlContractFact]
    public async Task Revoking_a_key_erases_its_ticket_at_once_and_is_idempotent()
    {
        var store = Store();
        var key = NewKey();
        var other = NewKey();
        var subject = NewSubject();
        await store.CreateAsync(Record(key, subject, NewSid()), CancellationToken.None);
        await store.CreateAsync(Record(other, subject, NewSid()), CancellationToken.None);

        Assert.Equal(1, await store.RevokeAsync(Hash(key), CancellationToken.None));
        Assert.Null(await store.ResolveAsync(Hash(key), CancellationToken.None));
        Assert.NotNull(await store.ResolveAsync(Hash(other), CancellationToken.None));
        Assert.Equal(
            "t|t",
            await ScalarAsync<string>(
                "SELECT concat_ws('|', ticket_ciphertext IS NULL, revoked_at IS NOT NULL) FROM identity.bff_sessions WHERE session_key_hash=@hash",
                ("hash", Hash(key))));
        Assert.Equal(0, await store.RevokeAsync(Hash(key), CancellationToken.None));
        Assert.Equal(0, await store.RevokeAsync(RandomNumberGenerator.GetBytes(32), CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task A_back_channel_sid_revokes_every_session_of_that_sid_and_nothing_else()
    {
        var store = Store();
        var subject = NewSubject();
        var sid = NewSid();
        var sameSidA = NewKey();
        var sameSidB = NewKey();
        var otherSid = NewKey();
        var noSid = NewKey();
        await store.CreateAsync(Record(sameSidA, subject, sid), CancellationToken.None);
        await store.CreateAsync(Record(sameSidB, NewSubject(), sid), CancellationToken.None);
        await store.CreateAsync(Record(otherSid, subject, NewSid()), CancellationToken.None);
        await store.CreateAsync(Record(noSid, subject, null), CancellationToken.None);

        Assert.Equal(2, await store.RevokeByAuthCenterSessionAsync(sid, CancellationToken.None));
        Assert.Null(await store.ResolveAsync(Hash(sameSidA), CancellationToken.None));
        Assert.Null(await store.ResolveAsync(Hash(sameSidB), CancellationToken.None));
        Assert.NotNull(await store.ResolveAsync(Hash(otherSid), CancellationToken.None));
        Assert.NotNull(await store.ResolveAsync(Hash(noSid), CancellationToken.None));
        Assert.Equal(0, await store.RevokeByAuthCenterSessionAsync(sid, CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task A_sub_only_logout_revokes_the_sessions_created_before_it_and_keeps_later_ones()
    {
        var store = Store();
        var subject = NewSubject();
        var older = NewKey();
        var newer = NewKey();
        var otherSubject = NewKey();
        await store.CreateAsync(Record(older, subject, NewSid()), CancellationToken.None);
        await store.CreateAsync(Record(newer, subject, NewSid()), CancellationToken.None);
        await store.CreateAsync(Record(otherSubject, NewSubject(), NewSid()), CancellationToken.None);
        // Age the first session: created_at is always the database clock, so the test moves it back.
        await ExecuteAdminAsync(
            "UPDATE identity.bff_sessions SET created_at=clock_timestamp()-interval '10 minutes' WHERE session_key_hash=@hash",
            ("hash", Hash(older)));
        var cutoff = await ScalarAsync<DateTime>("SELECT clock_timestamp()-interval '5 minutes'");

        Assert.Equal(1, await store.RevokeBySubjectAsync(subject, new DateTimeOffset(cutoff, TimeSpan.Zero), CancellationToken.None));
        Assert.Null(await store.ResolveAsync(Hash(older), CancellationToken.None));
        Assert.NotNull(await store.ResolveAsync(Hash(newer), CancellationToken.None));
        Assert.NotNull(await store.ResolveAsync(Hash(otherSubject), CancellationToken.None));

        // A moment in the future is clamped to the function's clock: existing sessions end, and a
        // session created afterwards is untouched by that earlier call.
        Assert.Equal(1, await store.RevokeBySubjectAsync(subject, DateTimeOffset.UtcNow.AddDays(365), CancellationToken.None));
        Assert.Null(await store.ResolveAsync(Hash(newer), CancellationToken.None));
        var afterwards = NewKey();
        await store.CreateAsync(Record(afterwards, subject, NewSid()), CancellationToken.None);
        Assert.NotNull(await store.ResolveAsync(Hash(afterwards), CancellationToken.None));
        Assert.NotNull(await store.ResolveAsync(Hash(otherSubject), CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task An_expired_session_never_resolves_and_arguments_outside_the_contract_are_refused()
    {
        var store = Store();
        var key = NewKey();
        await store.CreateAsync(Record(key, NewSubject(), NewSid()), CancellationToken.None);
        await ExecuteAdminAsync(
            """
            UPDATE identity.bff_sessions
            SET created_at=clock_timestamp()-interval '2 hours', expires_at=clock_timestamp()-interval '1 second'
            WHERE session_key_hash=@hash
            """,
            ("hash", Hash(key)));
        Assert.Null(await store.ResolveAsync(Hash(key), CancellationToken.None));

        var hash = "decode(repeat('ab',32),'hex')";
        foreach (var call in new[]
                 {
                     "SELECT security.create_bff_session(decode(repeat('ab',31),'hex'),'s','sid','\\x01'::bytea,clock_timestamp()+interval '1 hour')",
                     $"SELECT security.create_bff_session({hash},NULL,'sid','\\x01'::bytea,clock_timestamp()+interval '1 hour')",
                     $"SELECT security.create_bff_session({hash},'','sid','\\x01'::bytea,clock_timestamp()+interval '1 hour')",
                     $"SELECT security.create_bff_session({hash},repeat('s',257),'sid','\\x01'::bytea,clock_timestamp()+interval '1 hour')",
                     $"SELECT security.create_bff_session({hash},'s',repeat('d',257),'\\x01'::bytea,clock_timestamp()+interval '1 hour')",
                     $"SELECT security.create_bff_session({hash},'s','sid',NULL,clock_timestamp()+interval '1 hour')",
                     $"SELECT security.create_bff_session({hash},'s','sid',''::bytea,clock_timestamp()+interval '1 hour')",
                     $"SELECT security.create_bff_session({hash},'s','sid','\\x01'::bytea,clock_timestamp()-interval '1 second')",
                     $"SELECT security.create_bff_session({hash},'s','sid','\\x01'::bytea,clock_timestamp()+interval '25 hours')",
                     $"SELECT security.create_bff_session({hash},'s','sid','\\x01'::bytea,NULL)",
                     "SELECT security.revoke_bff_session(NULL::bytea)",
                     "SELECT security.revoke_bff_session(''::text)",
                     "SELECT security.revoke_bff_session(NULL::text,clock_timestamp())",
                     "SELECT security.revoke_bff_session('s'::text,NULL::timestamptz)",
                 })
        {
            await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_app;");
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, call));
            Assert.Equal("22023", refused.SqlState);
        }

        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM identity.bff_sessions WHERE session_key_hash=decode(repeat('ab',32),'hex')"));
    }

    [PostgreSqlContractFact]
    public async Task The_store_is_pre_tenant_and_row_level_security_is_not_involved()
    {
        foreach (var table in new[] { AddBffSessionStore.Table, AddBffSessionStore.LogoutJtiTable })
        {
            Assert.Equal(
                "t|t|0",
                await ScalarAsync<string>(
                    """
                    SELECT concat_ws('|',c.relrowsecurity,c.relforcerowsecurity,
                      (SELECT count(*) FROM pg_policy p WHERE p.polrelid=c.oid))
                    FROM pg_class c WHERE c.oid=@table::regclass
                    """,
                    ("table", table)));
        }
        Assert.Equal(0, await ScalarAsync<long>(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema='identity' AND table_name='bff_sessions'
              AND column_name IN ('owner_org_id','operator_org_id','organization_id','org_id','user_id')
            """));

        // A session created inside one tenant context resolves identically in another tenant context and
        // with no tenant context at all: the store never reads app.current_org_ids.
        var key = NewKey();
        var ticket = RandomNumberGenerator.GetBytes(64);
        var orgA = Guid.NewGuid();
        await using (var tenantA = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), [orgA]))
        {
            await using var create = new NpgsqlCommand(
                "SELECT security.create_bff_session(@hash,@subject,NULL,@ticket,clock_timestamp()+interval '1 hour')",
                tenantA.Connection,
                tenantA.Transaction);
            create.Parameters.Add(new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = Hash(key) });
            create.Parameters.Add(new NpgsqlParameter<string>("subject", NpgsqlDbType.Text) { TypedValue = NewSubject() });
            create.Parameters.Add(new NpgsqlParameter<byte[]>("ticket", NpgsqlDbType.Bytea) { TypedValue = ticket });
            await create.ExecuteNonQueryAsync();
            await tenantA.Transaction.CommitAsync();
        }

        foreach (var organizations in new[] { new[] { Guid.NewGuid() }, Array.Empty<Guid>() })
        {
            await using var tenant = await TenantTransaction.BeginAsync(fixture.AppDataSource, "paqueteria_app", Guid.NewGuid(), organizations);
            await using var resolve = new NpgsqlCommand("SELECT security.resolve_bff_session(@hash)", tenant.Connection, tenant.Transaction);
            resolve.Parameters.Add(new NpgsqlParameter<byte[]>("hash", NpgsqlDbType.Bytea) { TypedValue = Hash(key) });
            Assert.Equal(ticket, (byte[])(await resolve.ExecuteScalarAsync())!);
        }

        Assert.Equal(ticket, await Store().ResolveAsync(Hash(key), CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task The_purge_runs_only_as_the_worker_through_the_cleanup_role_and_deletes_only_dead_rows()
    {
        await DrainAsync();
        var store = Store();
        var live = NewKey();
        var revoked = NewKey();
        var expired = NewKey();
        var subject = NewSubject();
        await store.CreateAsync(Record(live, subject, NewSid()), CancellationToken.None);
        await store.CreateAsync(Record(revoked, subject, NewSid()), CancellationToken.None);
        await store.CreateAsync(Record(expired, subject, NewSid()), CancellationToken.None);
        await store.RevokeAsync(Hash(revoked), CancellationToken.None);
        await ExecuteAdminAsync(
            """
            UPDATE identity.bff_sessions
            SET created_at=clock_timestamp()-interval '9 hours', expires_at=clock_timestamp()-interval '1 hour'
            WHERE session_key_hash=@hash
            """,
            ("hash", Hash(expired)));

        var gateway = new PostgreSqlOperationalCleanupGateway(fixture.WorkerDataSource, 30);
        Assert.Equal(1, await gateway.PurgeBffSessionsAsync(1, CancellationToken.None));
        Assert.Equal(1, await gateway.PurgeBffSessionsAsync(AddBffSessionPurge.MaximumBatchSize, CancellationToken.None));
        Assert.Equal(0, await gateway.PurgeBffSessionsAsync(AddBffSessionPurge.MaximumBatchSize, CancellationToken.None));
        Assert.Equal(
            [Convert.ToHexString(Hash(live))],
            await HexHashesAsync(subject));
        Assert.NotNull(await store.ResolveAsync(Hash(live), CancellationToken.None));

        foreach (var size in new[] { "0", "1001", "NULL" })
        {
            await using var worker = await fixture.WorkerDataSource.OpenConnectionAsync();
            await using var transaction = await worker.BeginTransactionAsync();
            await ExecuteAsync(worker, transaction, "SET LOCAL ROLE paqueteria_worker;");
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(worker, transaction, $"SELECT security.purge_bff_sessions({size}::integer)"));
            Assert.Equal("22023", refused.SqlState);
        }

        await using (var app = await fixture.AppDataSource.OpenConnectionAsync())
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ExecuteAsync(app, transaction, "SET LOCAL ROLE paqueteria_app;");
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(app, transaction, "SELECT security.purge_bff_sessions(1)"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        Assert.Equal(
            $"t|{AddBffSessionPurge.SearchPath}|f|f|t|{CleanupExecutor}|integer",
            await ScalarAsync<string>(
                """
                SELECT concat_ws('|',p.prosecdef,array_to_string(p.proconfig,';'),
                  has_function_privilege('public',p.oid,'EXECUTE'),
                  has_function_privilege('paqueteria_app',p.oid,'EXECUTE'),
                  has_function_privilege('paqueteria_worker',p.oid,'EXECUTE'),
                  pg_get_userbyid(p.proowner),
                  pg_get_function_result(p.oid))
                FROM pg_proc p WHERE p.oid=@function::regprocedure
                """,
                ("function", AddBffSessionPurge.PurgeSignature)));
        Assert.Equal(
            "identity.bff_logout_jtis.expires_at:SELECT,identity.bff_logout_jtis.jti_hash:SELECT," +
            "identity.bff_sessions.expires_at:SELECT,identity.bff_sessions.revoked_at:SELECT,identity.bff_sessions.session_key_hash:SELECT",
            await ScalarAsync<string>(
                """
                SELECT string_agg(table_schema || '.' || table_name || '.' || column_name || ':' || privilege_type, ','
                  ORDER BY table_name, column_name)
                FROM information_schema.column_privileges
                WHERE grantee=@executor AND table_schema='identity'
                """,
                ("executor", CleanupExecutor)));
        Assert.False(await ScalarAsync<bool>(
            """
            SELECT has_column_privilege(@executor,'identity.bff_sessions','ticket_ciphertext','SELECT')
                OR has_column_privilege(@executor,'identity.bff_sessions','identity_subject','SELECT')
                OR has_column_privilege(@executor,'identity.bff_sessions','authcenter_sid','SELECT')
                OR has_table_privilege(@executor,'identity.bff_sessions','INSERT')
                OR has_table_privilege(@executor,'identity.bff_sessions','UPDATE')
                OR has_table_privilege(@executor,'identity.users','SELECT')
            """,
            ("executor", CleanupExecutor)));
    }

    [PostgreSqlContractFact]
    public async Task Baseline_assertions_accept_the_contract_and_fail_closed_on_widened_privileges()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);

        foreach (var widening in new[]
                 {
                     "GRANT SELECT ON identity.bff_sessions TO paqueteria_app;",
                     "GRANT SELECT (session_key_hash) ON identity.bff_sessions TO paqueteria_worker;",
                     "GRANT DELETE ON identity.bff_sessions TO PUBLIC;",
                     "GRANT SELECT ON identity.bff_logout_jtis TO paqueteria_app;",
                     $"GRANT DELETE ON identity.bff_logout_jtis TO {Executor};",
                     $"GRANT SELECT (jti_hash) ON identity.bff_logout_jtis TO {Executor};",
                     $"GRANT INSERT (jti_hash) ON identity.bff_logout_jtis TO {CleanupExecutor};",
                     "ALTER TABLE identity.bff_logout_jtis NO FORCE ROW LEVEL SECURITY;",
                     "GRANT EXECUTE ON FUNCTION security.register_bff_logout_jti(bytea,timestamptz) TO paqueteria_worker;",
                     $"GRANT DELETE ON identity.bff_sessions TO {Executor};",
                     $"GRANT UPDATE (expires_at) ON identity.bff_sessions TO {Executor};",
                     $"REVOKE UPDATE (revoked_at) ON identity.bff_sessions FROM {Executor};",
                     $"GRANT SELECT ON identity.users TO {Executor};",
                     $"GRANT USAGE ON SCHEMA security TO {Executor};",
                     $"GRANT CREATE ON SCHEMA identity TO {Executor};",
                     $"GRANT SELECT (ticket_ciphertext) ON identity.bff_sessions TO {CleanupExecutor};",
                     "CREATE POLICY bff_open ON identity.bff_sessions USING (true);",
                     "ALTER TABLE identity.bff_sessions NO FORCE ROW LEVEL SECURITY;",
                     "GRANT EXECUTE ON FUNCTION security.resolve_bff_session(bytea) TO paqueteria_worker;",
                     "GRANT EXECUTE ON FUNCTION security.revoke_bff_session(text) TO PUBLIC;",
                     "REVOKE EXECUTE ON FUNCTION security.create_bff_session(bytea,text,text,bytea,timestamptz) FROM paqueteria_app;",
                     "ALTER FUNCTION security.revoke_bff_session(text,timestamptz) SET search_path = public, pg_temp;",
                     "ALTER FUNCTION security.resolve_bff_session(bytea) SECURITY INVOKER;",
                     "ALTER FUNCTION security.revoke_bff_session(bytea) OWNER TO paqueteria_migrator;",
                     "GRANT EXECUTE ON FUNCTION security.purge_bff_sessions(integer) TO paqueteria_app;",
                     $"GRANT {Executor} TO paqueteria_app;",
                     $"GRANT paqueteria_app TO {Executor};",
                     $"""
                     CREATE FUNCTION security.bff_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                     ALTER FUNCTION security.bff_rogue() OWNER TO {Executor};
                     """,
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, widening);
            await Assert.ThrowsAsync<DatabaseAssertionException>(() =>
                new DatabaseBaselineAssertions().AssertAsync(connection, transaction));
            await transaction.RollbackAsync();
        }

        await new DatabaseBaselineAssertions().AssertAsync(connection);
        await using var installed = await connection.BeginTransactionAsync();
        await DatabaseBaselineAssertions.AssertSessionExecutorInstalledAsync(connection, installed);
        await DatabaseBaselineAssertions.AssertCleanupExecutorInstalledAsync(connection, installed);
        await installed.RollbackAsync();
    }

    [PostgreSqlContractFact]
    public async Task Identity_migration_reapplies_idempotently_refuses_a_non_canonical_table_and_never_downgrades()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        var definitions = await FunctionDefinitionsAsync(connection, null, Executor);
        await using (var reapply = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, reapply, AddBffSessionStore.UpSql);
            Assert.Equal(definitions, await FunctionDefinitionsAsync(connection, reapply, Executor));
            await reapply.RollbackAsync();
        }

        foreach (var (violation, message) in new[]
                 {
                     ("ALTER TABLE identity.bff_sessions ADD COLUMN refresh_token text;", "columns do not match"),
                     ("ALTER TABLE identity.bff_sessions DROP CONSTRAINT bff_sessions_revocation_ck;", "constraints do not match"),
                     ("DROP INDEX identity.bff_sessions_sid_idx;", "indexes do not match"),
                     ("ALTER TABLE identity.bff_logout_jtis ADD COLUMN issuer text;", "bff_logout_jtis columns do not match"),
                     ("CREATE POLICY jti_open ON identity.bff_logout_jtis USING (true);", "bff_logout_jtis must be owned by paqueteria_migrator"),
                     ("ALTER TABLE identity.bff_sessions NO FORCE ROW LEVEL SECURITY;", "FORCE RLS, no policy and no user trigger"),
                     ("CREATE POLICY bff_open ON identity.bff_sessions USING (true);", "FORCE RLS, no policy and no user trigger"),
                     ($"ALTER ROLE {Executor} LOGIN;", "exists with attributes outside the BFF-SESSION-TABLE-SHAPE contract"),
                     ($"GRANT paqueteria_worker TO {Executor};", "must not inherit any other role"),
                     ($"""
                      CREATE FUNCTION security.bff_rogue() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                      ALTER FUNCTION security.bff_rogue() OWNER TO {Executor};
                      """, "already owns objects outside the BFF-SESSION-TABLE-SHAPE contract"),
                     ($"GRANT SELECT ON identity.users TO {Executor};", "privileges differ from the BFF-SESSION-TABLE-SHAPE contract"),
                     ($"GRANT USAGE ON SCHEMA orders TO {Executor};", "schema privileges differ from the BFF-SESSION-TABLE-SHAPE contract"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, violation);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddBffSessionStore.UpSql));
            Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        // Down through EF itself, down to an empty lane: the first Down refuses, leaving history, table and functions intact.
        await using (var migrator = await OpenAsMigratorAsync())
        {
            await using var context = new IdentityDbContext(
                new DbContextOptionsBuilder<IdentityDbContext>()
                    .UseNpgsql(migrator, postgres => postgres.MigrationsHistoryTable("__ef_migrations_history_identity", "platform"))
                    .Options,
                new TenantDatabaseExecutionState());
            var blocked = await Assert.ThrowsAnyAsync<Exception>(() =>
                context.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase));
            Assert.Equal("BFF_SESSION_SCHEMA_DOWNGRADE_NOT_SUPPORTED", FindPostgresException(blocked).MessageText);
        }

        Assert.True(await E002BffSessionStateReader.IsSessionStoreAppliedAsync(connection));
        Assert.Equal(definitions, await FunctionDefinitionsAsync(connection, null, Executor));
        await using (var rollback = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, rollback, AddBffSessionStore.OperationalRollbackSql);
            await using var check = new NpgsqlCommand(
                "SELECT bool_or(has_function_privilege('paqueteria_app', oid, 'EXECUTE')) FROM pg_proc WHERE proowner=@executor::regrole",
                connection,
                rollback);
            check.Parameters.AddWithValue("executor", Executor);
            Assert.Equal(false, await check.ExecuteScalarAsync());
            await rollback.RollbackAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task Custody_purge_migration_reapplies_idempotently_and_never_downgrades()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        var definitions = await FunctionDefinitionsAsync(connection, null, CleanupExecutor);
        await using (var reapply = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, reapply, AddBffSessionPurge.UpSql);
            Assert.Equal(definitions, await FunctionDefinitionsAsync(connection, reapply, CleanupExecutor));
            await reapply.RollbackAsync();
        }

        foreach (var (violation, message) in new[]
                 {
                     ("""
                      CREATE FUNCTION identity.bff_touch() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN OLD; END';
                      CREATE TRIGGER bff_touch BEFORE DELETE ON identity.bff_sessions FOR EACH ROW EXECUTE FUNCTION identity.bff_touch();
                      """, "refuses identity.bff_sessions or identity.bff_logout_jtis with user triggers"),
                     ("""
                      CREATE FUNCTION identity.jti_touch() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN OLD; END';
                      CREATE TRIGGER jti_touch BEFORE DELETE ON identity.bff_logout_jtis FOR EACH ROW EXECUTE FUNCTION identity.jti_touch();
                      """, "refuses identity.bff_sessions or identity.bff_logout_jtis with user triggers"),
                     ("DROP INDEX identity.bff_logout_jtis_expiry_idx;", "requires the canonical AI-06 identity.bff_logout_jtis"),
                     ($"GRANT SELECT (created_at) ON identity.bff_logout_jtis TO {CleanupExecutor};", "privileges differ from the OPS-003-CLEANUP-ROLE contract"),
                     ("DROP INDEX identity.bff_sessions_revoked_idx;", "requires the canonical AI-06 expiry and revocation indexes"),
                     ($"GRANT SELECT (ticket_ciphertext) ON identity.bff_sessions TO {CleanupExecutor};", "privileges differ from the OPS-003-CLEANUP-ROLE contract"),
                     ($"GRANT USAGE ON SCHEMA orders TO {CleanupExecutor};", "schema privileges differ from the OPS-003-CLEANUP-ROLE contract"),
                 })
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, violation);
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connection, transaction, AddBffSessionPurge.UpSql));
            Assert.Contains(message, refused.MessageText, StringComparison.Ordinal);
            await transaction.RollbackAsync();
        }

        await using (var migrator = await OpenAsMigratorAsync())
        {
            await using var context = new CustodyDbContext(
                new DbContextOptionsBuilder<CustodyDbContext>()
                    .UseNpgsql(migrator, postgres =>
                    {
                        postgres.UseNetTopologySuite();
                        postgres.MigrationsHistoryTable("__ef_migrations_history_custody", "platform");
                    })
                    .Options,
                new TenantDatabaseExecutionState());
            var blocked = await Assert.ThrowsAnyAsync<Exception>(() =>
                context.GetService<IMigrator>().MigrateAsync(AddOperationalCleanupExecutor.MigrationId));
            Assert.Equal("OPS003_BFF_SESSION_PURGE_DOWNGRADE_NOT_SUPPORTED", FindPostgresException(blocked).MessageText);
        }

        Assert.True(await E002BffSessionStateReader.IsPurgeAppliedAsync(connection));
        Assert.Equal(definitions, await FunctionDefinitionsAsync(connection, null, CleanupExecutor));
        await using (var rollback = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, rollback, AddBffSessionPurge.OperationalRollbackSql);
            await using var check = new NpgsqlCommand(
                "SELECT has_function_privilege('paqueteria_worker','security.purge_bff_sessions(integer)','EXECUTE')",
                connection,
                rollback);
            Assert.Equal(false, await check.ExecuteScalarAsync());
            await rollback.RollbackAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task A_pre_bff_installation_upgrades_through_the_identity_and_custody_lanes()
    {
        var coordinator = new ModuleMigrationCoordinator();
        try
        {
            // Roll this database back to an installation provisioned before AI-06/AI-18 knew the BFF
            // session store: no table, no session executor, no session functions, no BFF purge, and both
            // lane histories one migration behind.
            await ExecuteAdminAsync(
                $"""
                DROP FUNCTION security.purge_bff_sessions(integer);
                DROP TABLE identity.bff_sessions;
                DROP TABLE identity.bff_logout_jtis;
                REVOKE USAGE ON SCHEMA identity FROM {CleanupExecutor};
                DROP OWNED BY {Executor};
                DROP ROLE {Executor};
                DELETE FROM platform."__ef_migrations_history_identity" WHERE "MigrationId"='{AddBffSessionStore.MigrationId}';
                DELETE FROM platform."__ef_migrations_history_custody" WHERE "MigrationId"='{AddBffSessionPurge.MigrationId}';
                """);
            await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                await new DatabaseBaselineAssertions().AssertAsync(connection);
            }

            var pending = await coordinator.PlanAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "Identity").Status);
            Assert.Equal("PENDING", pending.Single(state => state.Module == "Custody").Status);

            await coordinator.ApplyAsync(fixture.DeploymentConnectionString, CancellationToken.None);

            Assert.All(
                await coordinator.AssertAsync(fixture.DeploymentConnectionString, CancellationToken.None),
                state => Assert.Equal("APPLIED", state.Status));
            await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync())
            {
                await new DatabaseBaselineAssertions().AssertAsync(connection);
                var semantic = await new E002SemanticAssertions().AssertAsync(
                    connection, await E002NotificationStateReader.ReadAsync(connection));
                Assert.EndsWith("_PLUS_OPS003_PLUS_BFFSESSION_PLUS_BFFPURGE_V1", semantic.RoutineMap, StringComparison.Ordinal);
                Assert.Equal("paqueteria_migrator", await ScalarAsync<string>(
                    "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='identity.bff_sessions'::regclass"));
            }

            var store = Store();
            var key = NewKey();
            await store.CreateAsync(Record(key, NewSubject(), NewSid()), CancellationToken.None);
            Assert.NotNull(await store.ResolveAsync(Hash(key), CancellationToken.None));
        }
        finally
        {
            var states = await coordinator.PlanAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            if (states.Any(state => state.Status != "APPLIED"))
            {
                await coordinator.ApplyAsync(fixture.DeploymentConnectionString, CancellationToken.None);
            }
        }
    }

    [PostgreSqlContractFact]
    public async Task E002_semantic_map_includes_the_session_and_purge_routines_once_their_lanes_are_recorded()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        Assert.True(await E002BffSessionStateReader.IsSessionStoreAppliedAsync(connection));
        Assert.True(await E002BffSessionStateReader.IsPurgeAppliedAsync(connection));
        var map = E002RoutineMap.Select(
            E002RoutineMapState.Applied,
            await E002LifecycleStateReader.IsAppliedAsync(connection),
            ops003Applied: true,
            bffSessionApplied: true,
            bffPurgeApplied: true);
        foreach (var signature in AddBffSessionStore.OwnedFunctions)
        {
            var entry = Assert.Single(map, routine => routine.Signature == signature);
            Assert.Equal(Executor, entry.Owner);
            Assert.Equal(["paqueteria_app"], entry.Grantees);
        }

        var purge = Assert.Single(map, routine => routine.Signature == AddBffSessionPurge.PurgeSignature);
        Assert.Equal(CleanupExecutor, purge.Owner);
        Assert.Equal(["paqueteria_worker"], purge.Grantees);
        var semantic = await new E002SemanticAssertions().AssertAsync(
            connection, await E002NotificationStateReader.ReadAsync(connection));
        Assert.EndsWith("_PLUS_BFFSESSION_PLUS_BFFPURGE_V1", semantic.RoutineMap, StringComparison.Ordinal);
    }

    [PostgreSqlContractFact]
    public async Task A_logout_jti_registers_once_and_only_its_hash_is_kept()
    {
        var store = Store();
        var jti = Hash(NewSid());
        var retainUntil = DateTimeOffset.UtcNow.AddMinutes(7);

        Assert.True(await store.ApplyLogoutTokenAsync(new BffLogoutTokenEffect(jti, retainUntil, null, null, retainUntil), CancellationToken.None));
        Assert.False(await store.ApplyLogoutTokenAsync(new BffLogoutTokenEffect(jti, retainUntil, null, null, retainUntil), CancellationToken.None));
        Assert.True(await store.ApplyLogoutTokenAsync(
            new BffLogoutTokenEffect(Hash(NewSid()), retainUntil, null, null, retainUntil), CancellationToken.None));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM identity.bff_logout_jtis WHERE jti_hash=@hash", ("hash", jti)));

        // An exp far in the future is retained at most one day; a past retention is refused.
        var far = Hash(NewSid());
        Assert.True(await store.ApplyLogoutTokenAsync(
            new BffLogoutTokenEffect(far, DateTimeOffset.UtcNow.AddDays(30), null, null, DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.True(await ScalarAsync<bool>(
            "SELECT expires_at - created_at <= interval '1 day' FROM identity.bff_logout_jtis WHERE jti_hash=@hash", ("hash", far)));
        foreach (var call in new[]
                 {
                     "SELECT security.register_bff_logout_jti(decode(repeat('ab',31),'hex'),clock_timestamp()+interval '5 minutes')",
                     "SELECT security.register_bff_logout_jti(NULL,clock_timestamp()+interval '5 minutes')",
                     "SELECT security.register_bff_logout_jti(decode(repeat('ab',32),'hex'),clock_timestamp()-interval '1 second')",
                     "SELECT security.register_bff_logout_jti(decode(repeat('ab',32),'hex'),NULL)",
                 })
        {
            await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_app;");
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, call));
            Assert.Equal("22023", refused.SqlState);
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_registrations_of_one_jti_from_many_replicas_accept_exactly_one()
    {
        var jti = Hash(NewSid());
        var retainUntil = DateTimeOffset.UtcNow.AddMinutes(7);
        var subject = NewSubject();
        var key = NewKey();
        await Store().CreateAsync(Record(key, subject, NewSid()), CancellationToken.None);

        // Each call has its own store and connection, as separate API replicas would.
        using var start = new SemaphoreSlim(0);
        var calls = Enumerable.Range(0, 12).Select(async _ =>
        {
            await start.WaitAsync();
            return await Store().ApplyLogoutTokenAsync(
                new BffLogoutTokenEffect(jti, retainUntil, null, subject, DateTimeOffset.UtcNow), CancellationToken.None);
        }).ToArray();
        start.Release(calls.Length);
        var results = await Task.WhenAll(calls);

        Assert.Single(results, first => first);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM identity.bff_logout_jtis WHERE jti_hash=@hash", ("hash", jti)));
        Assert.Null(await Store().ResolveAsync(Hash(key), CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task A_replayed_logout_token_revokes_nothing_and_a_failed_revocation_does_not_burn_the_jti()
    {
        var store = Store();
        var subject = NewSubject();
        var jti = Hash(NewSid());
        var retainUntil = DateTimeOffset.UtcNow.AddMinutes(7);
        var before = NewKey();
        await store.CreateAsync(Record(before, subject, NewSid()), CancellationToken.None);
        var first = await ScalarAsync<DateTime>("SELECT clock_timestamp()");
        Assert.True(await store.ApplyLogoutTokenAsync(
            new BffLogoutTokenEffect(jti, retainUntil, null, subject, new DateTimeOffset(first, TimeSpan.Zero)), CancellationToken.None));
        Assert.Null(await store.ResolveAsync(Hash(before), CancellationToken.None));

        var after = NewKey();
        await store.CreateAsync(Record(after, subject, NewSid()), CancellationToken.None);
        Assert.False(await store.ApplyLogoutTokenAsync(
            new BffLogoutTokenEffect(jti, retainUntil, null, subject, DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None));
        Assert.NotNull(await store.ResolveAsync(Hash(after), CancellationToken.None));

        // Registration and revocation are one transaction: when the revocation fails, the jti is not
        // kept, so AuthCenter's retry of the same token is still accepted.
        var retried = Hash(NewSid());
        await Assert.ThrowsAsync<IdentityContextInfrastructureException>(() => store.ApplyLogoutTokenAsync(
            new BffLogoutTokenEffect(retried, retainUntil, null, new string('s', 257), DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM identity.bff_logout_jtis WHERE jti_hash=@hash", ("hash", retried)));
        Assert.True(await store.ApplyLogoutTokenAsync(
            new BffLogoutTokenEffect(retried, retainUntil, null, subject, DateTimeOffset.UtcNow), CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task The_purge_removes_only_logout_jtis_past_their_retention_within_the_batch_bound()
    {
        await DrainAsync();
        var store = Store();
        var live = Hash(NewSid());
        var expiredA = Hash(NewSid());
        var expiredB = Hash(NewSid());
        foreach (var jti in new[] { live, expiredA, expiredB })
        {
            Assert.True(await store.ApplyLogoutTokenAsync(
                new BffLogoutTokenEffect(jti, DateTimeOffset.UtcNow.AddMinutes(7), null, null, DateTimeOffset.UtcNow),
                CancellationToken.None));
        }

        await ExecuteAdminAsync(
            """
            UPDATE identity.bff_logout_jtis
            SET created_at=clock_timestamp()-interval '1 hour', expires_at=clock_timestamp()-interval '1 second'
            WHERE jti_hash=ANY(@hashes)
            """,
            ("hashes", new[] { expiredA, expiredB }));
        var revokedSession = NewKey();
        await store.CreateAsync(Record(revokedSession, NewSubject(), NewSid()), CancellationToken.None);
        await store.RevokeAsync(Hash(revokedSession), CancellationToken.None);

        // Sessions first, then jtis with what remains of the batch: 1 revoked session + 1 expired jti.
        var gateway = new PostgreSqlOperationalCleanupGateway(fixture.WorkerDataSource, 30);
        Assert.Equal(2, await gateway.PurgeBffSessionsAsync(2, CancellationToken.None));
        Assert.Equal(1, await gateway.PurgeBffSessionsAsync(AddBffSessionPurge.MaximumBatchSize, CancellationToken.None));
        Assert.Equal(0, await gateway.PurgeBffSessionsAsync(AddBffSessionPurge.MaximumBatchSize, CancellationToken.None));
        Assert.Equal(1, await ScalarAsync<long>(
            "SELECT count(*) FROM identity.bff_logout_jtis WHERE jti_hash=ANY(@hashes)",
            ("hashes", new[] { live, expiredA, expiredB })));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM identity.bff_logout_jtis WHERE jti_hash=@hash", ("hash", live)));
    }

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected a PostgresException, observed {exception.GetType().Name}: {exception.Message}");
    }

    private PostgreSqlBffSessionStore Store() => new(
        fixture.AppDataSource,
        Options.Create(new IdentityBootstrapOptions { CommandTimeoutSeconds = 5 }),
        NullLogger<PostgreSqlBffSessionStore>.Instance);

    private static BffSessionRecord Record(string key, string subject, string? sid) =>
        new(Hash(key), subject, sid, RandomNumberGenerator.GetBytes(256), DateTimeOffset.UtcNow.AddHours(8));

    private static string NewKey() =>
        "paquetenvia:authcenter:session:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewSubject() => $"bff-contract-sub-{Guid.NewGuid():N}";

    private static string NewSid() => $"bff-contract-sid-{Guid.NewGuid():N}";

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private async Task DrainAsync()
    {
        var gateway = new PostgreSqlOperationalCleanupGateway(fixture.WorkerDataSource, 30);
        while (await gateway.PurgeBffSessionsAsync(AddBffSessionPurge.MaximumBatchSize, CancellationToken.None) > 0)
        {
        }
    }

    private async Task<List<string>> HexHashesAsync(string subject)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT encode(session_key_hash,'hex') FROM identity.bff_sessions WHERE identity_subject=@subject ORDER BY 1");
        command.Parameters.AddWithValue("subject", subject);
        var hashes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            hashes.Add(reader.GetString(0).ToUpperInvariant());
        }

        return hashes;
    }

    private async Task<NpgsqlConnection> OpenAsMigratorAsync()
    {
        var connection = new NpgsqlConnection(fixture.DeploymentConnectionString);
        await connection.OpenAsync();
        await using var role = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection);
        await role.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<string> FunctionDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string owner)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(pg_get_functiondef(p.oid) || pg_get_userbyid(p.proowner) || p.proacl::text, '|'
              ORDER BY p.oid::regprocedure::text)
            FROM pg_proc p WHERE p.proowner=@owner::regrole
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("owner", owner);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is T typed
            ? typed
            : (T)Convert.ChangeType(result!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAdminAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
