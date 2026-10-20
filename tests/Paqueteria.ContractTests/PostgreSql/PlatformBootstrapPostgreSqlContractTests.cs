using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application.Provisioning;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// TEN-003-PLATFORM-BOOTSTRAP-2026-10-19 against real PostgreSQL/PostGIS, each test on its own fully migrated
/// database: the DatabaseMigrator <c>platform-bootstrap</c> command, run as an Azure-like deployment login
/// (NOSUPERUSER, BYPASSRLS, SET and USAGE on <c>paqueteria_migrator</c>, no membership in <c>paqueteria_app</c>),
/// creates the ACTIVE PLATFORM organization, the user of the given AuthCenter subject, its ACTIVE default
/// PLATFORM_ADMIN membership and one append-only audit row in one transaction; it refuses, writing nothing, a
/// second run, a subject that already signed in, a login that does not bypass RLS and a failure at any stage; its
/// output names no subject, name or id; and the new PLATFORM_ADMIN then signs in to the same user and runs the
/// existing REG-001/REG-002 platform flows.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed partial class PlatformBootstrapPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string LegalName = "Plataforma Sintética TEN-003 S.A. de C.V.";
    private const string DisplayName = "Plataforma Sintética TEN-003";
    private static readonly string Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex UuidPattern();

    [PostgreSqlContractFact]
    public async Task Platform_bootstrap_runs_once_as_the_deployment_login_audited_and_refuses_every_later_run()
    {
        var database = await fixture.CreateIsolatedDatabaseAsync("ten003boot");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var deployLogin = $"pv_ten003_deploy_{suffix}";
        var plainLogin = $"pv_ten003_plain_{suffix}";
        var variable = $"PAQUETERIA_TEN003_BOOTSTRAP_{suffix.ToUpperInvariant()}";
        var subject = Guid.NewGuid().ToString("D");
        var stdout = Console.Out;
        var stderr = Console.Error;
        try
        {
            await MigrateAsync(database);
            await ExecuteAsync(database, $"""
                CREATE ROLE {deployLogin} LOGIN NOSUPERUSER NOCREATEDB CREATEROLE NOREPLICATION BYPASSRLS PASSWORD '{Password}';
                GRANT paqueteria_migrator TO {deployLogin} WITH INHERIT TRUE, SET TRUE;
                CREATE ROLE {plainLogin} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{Password}';
                GRANT paqueteria_migrator TO {plainLogin} WITH INHERIT TRUE, SET TRUE;
                """);
            var deploy = Login(database, deployLogin);
            var plain = Login(database, plainLogin);
            var app = AppLogin(database);

            // Like the Azure deployment login (E-002 capability gate): migrator yes, the API's runtime role no.
            Assert.Equal("t|t|f|f", await ScalarAsync<string>(database,
                """
                SELECT concat_ws('|', pg_has_role(@login,'paqueteria_migrator','SET'), pg_has_role(@login,'paqueteria_migrator','USAGE'),
                  pg_has_role(@login,'paqueteria_app','SET'), pg_has_role(@login,'paqueteria_app','MEMBER'))
                """,
                ("login", deployLogin)));
            var empty = await CountsAsync(database);
            Assert.Equal("0|0|0|0|0", empty);

            // A subject that already signed in (security.register_identity_subject at the AuthCenter callback): the
            // whole command (baseline and lane assertions first) refuses it.
            var signedIn = Guid.NewGuid().ToString("D");
            await AsAppAsync<Guid>(app, "SELECT security.register_identity_subject(@subject, @id)",
                ("subject", signedIn), ("id", Guid.NewGuid()));
            var beforeRefusals = await CountsAsync(database);
            var (signedInExit, signedInOutput) = await RunMigratorAsync(variable, database, signedIn);
            Assert.True(signedInExit == PlatformBootstrap.RefusedExitCode, signedInOutput);
            Assert.Contains("TEN003_PLATFORM_BOOTSTRAP_SUBJECT_ALREADY_SIGNED_IN", signedInOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(signedIn, signedInOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(UuidPattern(), signedInOutput);
            var signedInRefused = await Assert.ThrowsAsync<PlatformBootstrapException>(() => PlatformBootstrap.RunAsync(
                deploy, Options(signedIn), TextWriter.Null, CancellationToken.None));
            Assert.StartsWith("TEN003_PLATFORM_BOOTSTRAP_SUBJECT_ALREADY_SIGNED_IN", signedInRefused.Message, StringComparison.Ordinal);

            // A login that cannot see every tenant would miss an earlier run: refused, also the API runtime login.
            foreach (var connection in new[] { plain, app })
            {
                var refused = await Assert.ThrowsAsync<PlatformBootstrapException>(() => PlatformBootstrap.RunAsync(
                    connection, Options(subject), TextWriter.Null, CancellationToken.None));
                Assert.Equal(PlatformBootstrap.RefusedExitCode, refused.ExitCode);
                Assert.StartsWith("TEN003_PLATFORM_BOOTSTRAP_DEPLOYMENT_LOGIN_REQUIRED", refused.Message, StringComparison.Ordinal);
            }

            // A failure after any insert rolls the whole transaction back.
            foreach (var stage in Enum.GetValues<ProvisioningStage>())
            {
                await Assert.ThrowsAsync<SyntheticStageException>(() => PlatformBootstrap.RunAsync(
                    deploy, Options(subject), TextWriter.Null, CancellationToken.None, new ThrowAtStage(stage)));
            }

            Assert.Equal(beforeRefusals, await CountsAsync(database));

            // The one run, as the Azure-like deployment login: no superuser and no paqueteria_app membership needed.
            await using var output = new StringWriter();
            var result = await PlatformBootstrap.RunAsync(deploy, Options(subject), output, CancellationToken.None);
            Assert.Equal(
                "TEN003_PLATFORM_BOOTSTRAP_OK organization_type=PLATFORM status=ACTIVE role=PLATFORM_ADMIN (audited)" +
                Environment.NewLine,
                output.ToString());

            var created = await ReadBootstrapAsync(database, subject);
            Assert.Equal((result.UserId, result.OrganizationId), (created.UserId, created.OrganizationId));
            Assert.Equal(
                $"PLATFORM|ACTIVE|{LegalName}|{DisplayName}|-|ACTIVE|-|PLATFORM_ADMIN|ACTIVE|t|-",
                created.Shape);
            Assert.Equal(
                $"{result.AuditId:D}|INITIAL_ORGANIZATION_PROVISIONED|ORGANIZATION|{created.OrganizationId:D}|{created.UserId:D}|{PlatformBootstrap.RequestId}|{{}}",
                await ScalarAsync<string>(database,
                    """
                    SELECT concat_ws('|', id, action, entity_type, entity_id, actor_id, request_id, payload_redacted::text)
                    FROM platform.audit_logs WHERE org_id=@org
                    """,
                    ("org", created.OrganizationId)));
            var afterRun = await CountsAsync(database);
            Assert.Equal(Increment(beforeRefusals, organizations: 1, users: 1, memberships: 1, audit: 1), afterRun);

            // Every later run is refused and writes nothing: the whole command with the same subject and with
            // another one, and the bootstrap transaction as the Azure-like login.
            foreach (var next in new[] { subject, Guid.NewGuid().ToString("D") })
            {
                var (refusedExit, refusedOutput) = await RunMigratorAsync(variable, database, next);
                Assert.True(refusedExit == PlatformBootstrap.RefusedExitCode, refusedOutput);
                Assert.Contains("TEN003_PLATFORM_BOOTSTRAP_ALREADY_DONE", refusedOutput, StringComparison.Ordinal);
                Assert.DoesNotContain(next, refusedOutput, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotMatch(UuidPattern(), refusedOutput);
            }

            var again = await Assert.ThrowsAsync<PlatformBootstrapException>(() => PlatformBootstrap.RunAsync(
                deploy, Options(Guid.NewGuid().ToString("D")), TextWriter.Null, CancellationToken.None));
            Assert.StartsWith("TEN003_PLATFORM_BOOTSTRAP_ALREADY_DONE", again.Message, StringComparison.Ordinal);
            Assert.Equal(afterRun, await CountsAsync(database));
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Environment.SetEnvironmentVariable(variable, null);
            NpgsqlConnection.ClearAllPools();
            await fixture.DropIsolatedDatabaseAsync(database);
            await using var cleanup = fixture.AdminDataSource.CreateCommand(
                $"DROP ROLE IF EXISTS {deployLogin}; DROP ROLE IF EXISTS {plainLogin};");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task Concurrent_first_runs_create_one_root_identity_and_its_admin_runs_the_existing_platform_flows()
    {
        var database = await fixture.CreateIsolatedDatabaseAsync("ten003flows");
        try
        {
            await MigrateAsync(database);
            var app = AppLogin(database);

            // Two first runs at once, through the whole command: both wait on the advisory lock held here, then
            // one creates the root identity and the other sees it and writes nothing.
            var subjects = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
            var variables = subjects.Select(_ => $"PAQUETERIA_TEN003_FLOWS_{Guid.NewGuid():N}".ToUpperInvariant()).ToArray();
            (string Subject, int ExitCode)[] runs;
            try
            {
                await using (var holder = new NpgsqlConnection(database))
                {
                    await holder.OpenAsync();
                    await using var hold = await holder.BeginTransactionAsync();
                    await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", holder, hold))
                    {
                        lockCommand.Parameters.Add(new NpgsqlParameter<long>("key", PlatformBootstrap.AdvisoryLockKey));
                        await lockCommand.ExecuteNonQueryAsync();
                    }

                    var started = subjects.Select((subject, index) => Task.Run(async () =>
                    {
                        Environment.SetEnvironmentVariable(variables[index], database);
                        return (subject, await DatabaseMigratorProgram.RunAsync(
                            ["platform-bootstrap", "--connection-env", variables[index], "--identity-subject", subject,
                             "--legal-name", LegalName, "--display-name", DisplayName]));
                    })).ToArray();
                    await WaitForLockWaitersAsync(database, 2);
                    await hold.CommitAsync();
                    runs = await Task.WhenAll(started);
                }
            }
            finally
            {
                foreach (var name in variables)
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
            }

            var winner = Assert.Single(runs, run => run.ExitCode == 0);
            var loser = Assert.Single(runs, run => run.ExitCode == PlatformBootstrap.RefusedExitCode);
            Assert.Equal("1|1|1|1|0", await CountsAsync(database));
            Assert.Equal(0L, await ScalarAsync<long>(database,
                "SELECT count(*) FROM identity.users WHERE identity_subject=@subject", ("subject", loser.Subject)));
            var (platformAdmin, platform, _) = await ReadBootstrapAsync(database, winner.Subject);

            // The owner's first sign-in reuses the bootstrapped user and resolves the PLATFORM_ADMIN membership.
            Assert.Equal(platformAdmin, await AsAppAsync<Guid>(app, "SELECT security.register_identity_subject(@subject, @id)",
                ("subject", winner.Subject), ("id", Guid.NewGuid())));
            using (var identity = JsonDocument.Parse(await AsAppAsync<string>(app,
                       "SELECT security.resolve_identity_context(@subject)::text", ("subject", winner.Subject))))
            {
                Assert.Equal(platformAdmin, identity.RootElement.GetProperty("user_id").GetGuid());
                var membership = Assert.Single(identity.RootElement.GetProperty("memberships").EnumerateArray());
                Assert.Equal(platform, membership.GetProperty("organization_id").GetGuid());
                Assert.Equal("PLATFORM_ADMIN", membership.GetProperty("role").GetString());
                Assert.True(membership.GetProperty("is_default").GetBoolean());
            }

            Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM identity.users"));

            // REG-ROLE-CEILING / REG-PLATFORM-ADMIN-ADDS: only a PLATFORM_ADMIN may add DISPATCHER and FINANCE, and
            // PLATFORM_ADMIN only inside a PLATFORM organization.
            foreach (var role in new[] { "DISPATCHER", "FINANCE", "PLATFORM_ADMIN" })
            {
                Assert.Equal($"ADDED|{role}|PENDING", await AsAppAsync<string>(app,
                    """
                    SELECT concat_ws('|', outcome, role, status)
                    FROM security.add_pending_membership(@actor, @org, @pending, @hmac, 1, @role, @key, 'ten-003-bootstrap-test')
                    """,
                    ("actor", platformAdmin), ("org", platform), ("pending", Guid.NewGuid()),
                    ("hmac", RandomNumberGenerator.GetBytes(32)), ("role", role), ("key", $"ten003-{role}-{Guid.NewGuid():N}")));
            }

            // REG-ALLY-APPROVAL-PATH: a self-registered ALLY waits for the PLATFORM_ADMIN, who approves it.
            var allyCreator = await AsAppAsync<Guid>(app, "SELECT security.register_identity_subject(@subject, @id)",
                ("subject", Guid.NewGuid().ToString("D")), ("id", Guid.NewGuid()));
            var ally = Guid.NewGuid();
            Assert.Equal("CREATED|PENDING_APPROVAL|ALLY_ADMIN", await AsAppAsync<string>(app,
                """
                SELECT concat_ws('|', outcome, status, membership_role)
                FROM security.create_self_service_organization(@user, @org, @membership, @audit, 'ALLY',
                  'Aliado Sintético TEN-003', 'Aliado TEN-003', 'ten-003-bootstrap-test')
                """,
                ("user", allyCreator), ("org", ally), ("membership", Guid.NewGuid()), ("audit", Guid.NewGuid())));
            Assert.Equal(ally, await AsAppAsync<Guid>(app,
                "SELECT organization_id FROM security.list_pending_ally_organizations(@actor, @platform, 50)",
                ("actor", platformAdmin), ("platform", platform)));
            var notAdmin = await Assert.ThrowsAsync<PostgresException>(() => AsAppAsync<string>(app,
                "SELECT security.decide_ally_organization(@actor, @platform, @ally, true, 'ten-003-bootstrap-test')",
                ("actor", allyCreator), ("platform", platform), ("ally", ally)));
            Assert.Equal(("42501", "REG001_PLATFORM_ADMIN_REQUIRED"), (notAdmin.SqlState, notAdmin.MessageText));
            Assert.Equal("ACTIVE", await AsAppAsync<string>(app,
                "SELECT security.decide_ally_organization(@actor, @platform, @ally, true, 'ten-003-bootstrap-test')",
                ("actor", platformAdmin), ("platform", platform), ("ally", ally)));
            Assert.Equal("ACTIVE", await ScalarAsync<string>(database,
                "SELECT status FROM organizations.organizations WHERE id=@ally", ("ally", ally)));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await fixture.DropIsolatedDatabaseAsync(database);
        }
    }

    private static PlatformBootstrapOptions Options(string subject) => new(subject, LegalName, DisplayName);

    private static async Task MigrateAsync(string database)
    {
        var baseline = await new DatabaseBaselineVerifier().VerifyAsync();
        await new DatabaseBaselineDeployer().ApplyAsync(baseline, database);
        await new ModuleMigrationCoordinator().ApplyAsync(database, CancellationToken.None);
    }

    /// <summary>The whole command in process, with its output captured (the console is restored by the caller).</summary>
    private static async Task<(int ExitCode, string Output)> RunMigratorAsync(string variable, string connectionString, string subject)
    {
        Environment.SetEnvironmentVariable(variable, connectionString);
        var stdout = Console.Out;
        var stderr = Console.Error;
        await using var capture = new StringWriter();
        Console.SetOut(capture);
        Console.SetError(capture);
        try
        {
            var exitCode = await DatabaseMigratorProgram.RunAsync(
                ["platform-bootstrap", "--connection-env", variable, "--identity-subject", subject,
                 "--legal-name", LegalName, "--display-name", DisplayName]);
            return (exitCode, capture.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    private static async Task WaitForLockWaitersAsync(string database, int waiters)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (await ScalarAsync<long>(database,
                   """
                   SELECT count(*) FROM pg_locks
                   WHERE locktype='advisory' AND NOT granted AND classid=0 AND objid=@key::oid AND objsubid=1
                     AND database=(SELECT oid FROM pg_database WHERE datname=current_database())
                   """,
                   ("key", PlatformBootstrap.AdvisoryLockKey)) < waiters)
        {
            Assert.True(DateTime.UtcNow < deadline, "the platform-bootstrap runs never waited on the advisory lock");
            await Task.Delay(50);
        }
    }

    private static string Login(string database, string login) =>
        new NpgsqlConnectionStringBuilder(database) { Username = login, Password = Password, Pooling = false }.ConnectionString;

    private string AppLogin(string database) =>
        new NpgsqlConnectionStringBuilder(fixture.AppConnectionString)
        {
            Database = new NpgsqlConnectionStringBuilder(database).Database,
            Pooling = false,
        }.ConnectionString;

    /// <summary>organizations|users|memberships|audit rows|pending memberships, read as the superuser.</summary>
    private static Task<string> CountsAsync(string database) => ScalarAsync<string>(database,
        """
        SELECT concat_ws('|',
          (SELECT count(*) FROM organizations.organizations),
          (SELECT count(*) FROM identity.users),
          (SELECT count(*) FROM organizations.organization_memberships),
          (SELECT count(*) FROM platform.audit_logs),
          (SELECT count(*) FROM organizations.pending_memberships))
        """);

    private static string Increment(string counts, int organizations, int users, int memberships, int audit)
    {
        var values = counts.Split('|').Select(long.Parse).ToArray();
        return string.Join('|', values[0] + organizations, values[1] + users, values[2] + memberships, values[3] + audit, values[4]);
    }

    private static async Task<(Guid UserId, Guid OrganizationId, string Shape)> ReadBootstrapAsync(string database, string subject)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT u.id, o.id, concat_ws('|', o.organization_type, o.status, o.legal_name, o.display_name,
                     COALESCE(o.self_service_creator_user_id::text, '-'), u.status, COALESCE(encode(u.email_ciphertext, 'hex'), '-'),
                     m.role, m.status, m.is_default, COALESCE(m.revoked_at::text, '-'))
            FROM identity.users u
            JOIN organizations.organization_memberships m ON m.user_id = u.id
            JOIN organizations.organizations o ON o.id = m.organization_id
            WHERE u.identity_subject = @subject
            """, connection);
        command.Parameters.Add(new NpgsqlParameter<string>("subject", NpgsqlDbType.Text) { TypedValue = subject });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var row = (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2));
        Assert.False(await reader.ReadAsync());
        return row;
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>One explicit transaction as the API's runtime role, the way the REG-001/REG-002 services call.</summary>
    private static async Task<T> AsAppAsync<T>(string appConnection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(appConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private sealed class ThrowAtStage(ProvisioningStage expected) : IProvisioningFailureInjector
    {
        public ValueTask AfterAsync(ProvisioningStage stage, CancellationToken cancellationToken) =>
            stage == expected ? throw new SyntheticStageException() : ValueTask.CompletedTask;
    }

    private sealed class SyntheticStageException : Exception;
}
