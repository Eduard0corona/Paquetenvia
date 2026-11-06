using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.ContractTests.Support;
using Paqueteria.Infrastructure.Database.Baseline;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// ENV-001: the pilot selects the E-002 ownership bridge with its own exact classification, the bridge covers every
/// privileged NOLOGIN role that AI-18 declares, and the migrator provisions least-privilege runtime logins from
/// SCRAM verifiers only.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class PilotRuntimeLoginContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Runtime_logins_are_least_privilege_idempotent_and_rekeyed_from_verifiers()
    {
        const string variable = "PAQUETERIA_ENV001_CONTRACT_CONNECTION";
        var saved = SaveEnvironment(variable);
        try
        {
            var apiPassword = RandomPassword();
            var workerPassword = RandomPassword();
            var realtimePassword = RandomPassword();
            Environment.SetEnvironmentVariable(variable, fixture.DeploymentConnectionString);
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.ApiVerifierVariable, ScramVerifier(apiPassword));
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.WorkerVerifierVariable, ScramVerifier(workerPassword));
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.RealtimeVerifierVariable, ScramVerifier(realtimePassword));

            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.ApiLogin, apiPassword, "paqueteria_app", "paqueteria_worker");
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.WorkerLogin, workerPassword, "paqueteria_worker", "paqueteria_app");
            // RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: the API's realtime login reaches neither the Worker nor the API role.
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.RealtimeLogin, realtimePassword, "paqueteria_realtime", "paqueteria_worker");
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.RealtimeLogin, realtimePassword, "paqueteria_realtime", "paqueteria_app");

            // A Worker membership granted to the realtime login by hand is removed on the next run.
            await ExecuteAdminAsync($"GRANT paqueteria_worker TO {RuntimeLoginProvisioner.RealtimeLogin}");
            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.RealtimeLogin, realtimePassword, "paqueteria_realtime", "paqueteria_worker");

            // Without its verifier no login is touched.
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.RealtimeVerifierVariable, null);
            Assert.Equal(7, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.RealtimeVerifierVariable, ScramVerifier(realtimePassword));

            // A stale extra membership is removed and a re-run re-keys the login; the old password stops working.
            await ExecuteAdminAsync($"GRANT paqueteria_worker TO {RuntimeLoginProvisioner.ApiLogin}");
            var rotated = RandomPassword();
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.ApiVerifierVariable, ScramVerifier(rotated));
            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.ApiLogin, rotated, "paqueteria_app", "paqueteria_worker");
            await Assert.ThrowsAsync<PostgresException>(() => OpenAsAsync(RuntimeLoginProvisioner.ApiLogin, apiPassword));

            // A plaintext password is refused before any statement runs.
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.WorkerVerifierVariable, RandomPassword());
            Assert.Equal(7, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.WorkerLogin, workerPassword, "paqueteria_worker", "paqueteria_app");
        }
        finally
        {
            RestoreEnvironment(saved);
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(
                $"DROP ROLE IF EXISTS {RuntimeLoginProvisioner.ApiLogin}; DROP ROLE IF EXISTS {RuntimeLoginProvisioner.WorkerLogin}; " +
                $"DROP ROLE IF EXISTS {RuntimeLoginProvisioner.RealtimeLogin};");
        }
    }

    /// <summary>
    /// MDM-001 route S2 (S6): the optional master-data login exists only while its verifier is present, has the runtime
    /// logins' attributes, is a member of <c>paqueteria_master_data_loader</c> only (so it can SET ROLE to it and reaches
    /// no runtime or migrator role) and is dropped when the input is absent. That the loader accepts it is proved by
    /// <c>MasterDataLoaderPostgreSqlContractTests.The_provisioned_master_data_login_passes_the_operator_check_and_loads</c>.
    /// </summary>
    [PostgreSqlContractFact]
    public async Task The_master_data_login_exists_only_while_its_verifier_is_present()
    {
        const string variable = "PAQUETERIA_ENV001_CONTRACT_CONNECTION";
        const string login = RuntimeLoginProvisioner.MasterDataLogin;
        var saved = SaveEnvironment(variable);
        try
        {
            var apiPassword = RandomPassword();
            var password = RandomPassword();
            Environment.SetEnvironmentVariable(variable, fixture.DeploymentConnectionString);
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.ApiVerifierVariable, ScramVerifier(apiPassword));
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.WorkerVerifierVariable, ScramVerifier(RandomPassword()));
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.RealtimeVerifierVariable, ScramVerifier(RandomPassword()));

            // Absent from the start: nothing is created and the three runtime logins are provisioned as before.
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.MasterDataVerifierVariable, null);
            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            Assert.False(await RoleExistsAsync(login));

            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.MasterDataVerifierVariable, ScramVerifier(password));
            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertMasterDataShapeAsync(password);

            // A runtime membership granted by hand is removed on the next run. (A migrator membership is refused even
            // earlier: the baseline assertions that runtime-logins runs first reject a loader member of the migrator.)
            await ExecuteAdminAsync($"GRANT paqueteria_app TO {login}; GRANT paqueteria_worker TO {login};");
            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertMasterDataShapeAsync(password);
            await ExecuteAdminAsync($"GRANT paqueteria_migrator TO {login};");
            Assert.Equal(5, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await ExecuteAdminAsync($"REVOKE paqueteria_migrator FROM {login};");

            // A new verifier re-keys it; the old password stops working.
            var rotated = RandomPassword();
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.MasterDataVerifierVariable, ScramVerifier(rotated));
            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertMasterDataShapeAsync(rotated);
            await Assert.ThrowsAsync<PostgresException>(() => OpenAsAsync(login, password));

            // A plaintext password is refused before any statement runs: nothing changes, the runtime logins included.
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.MasterDataVerifierVariable, RandomPassword());
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.ApiVerifierVariable, ScramVerifier(RandomPassword()));
            Assert.Equal(7, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertMasterDataShapeAsync(rotated);
            await using (await OpenAsAsync(RuntimeLoginProvisioner.ApiLogin, apiPassword))
            {
            }

            // Absent again (unset or empty): the login is dropped with its membership; the runtime logins stay.
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.ApiVerifierVariable, ScramVerifier(apiPassword));
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.MasterDataVerifierVariable, string.Empty);
            Assert.Equal(RuntimeLoginProvisioner.MasterDataLoginAbsent,
                await RuntimeLoginProvisioner.RunAsync(fixture.DeploymentConnectionString, Environment.GetEnvironmentVariable,
                    CancellationToken.None));
            Assert.False(await RoleExistsAsync(login));
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.ApiLogin, apiPassword, "paqueteria_app", "paqueteria_worker");

            // Running again while absent is a no-op.
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.MasterDataVerifierVariable, null);
            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            Assert.False(await RoleExistsAsync(login));
        }
        finally
        {
            RestoreEnvironment(saved);
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(
                $"DROP ROLE IF EXISTS {RuntimeLoginProvisioner.ApiLogin}; DROP ROLE IF EXISTS {RuntimeLoginProvisioner.WorkerLogin}; " +
                $"DROP ROLE IF EXISTS {RuntimeLoginProvisioner.RealtimeLogin}; DROP ROLE IF EXISTS {login};");
        }
    }

    /// <summary>The loader's own operator check, run as the login, plus the exact attributes and membership.</summary>
    private async Task AssertMasterDataShapeAsync(string password)
    {
        await using var connection = await OpenAsAsync(RuntimeLoginProvisioner.MasterDataLogin, password);
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""
            SELECT r.rolcanlogin AND NOT r.rolinherit AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole
                   AND NOT r.rolreplication AND NOT r.rolbypassrls
                   AND (SELECT array_agg(g.rolname ORDER BY g.rolname) FROM pg_auth_members m
                        JOIN pg_roles g ON g.oid = m.roleid WHERE m.member = r.oid) = ARRAY['paqueteria_master_data_loader']::name[]
                   AND pg_catalog.pg_has_role(session_user, 'paqueteria_master_data_loader', 'SET')
                   AND NOT pg_catalog.pg_has_role(session_user, 'paqueteria_master_data_loader', 'USAGE')
                   AND NOT pg_catalog.pg_has_role(session_user, 'paqueteria_app', 'MEMBER')
                   AND NOT pg_catalog.pg_has_role(session_user, 'paqueteria_worker', 'MEMBER')
                   AND NOT pg_catalog.pg_has_role(session_user, 'paqueteria_realtime', 'MEMBER')
                   AND NOT pg_catalog.pg_has_role(session_user, 'paqueteria_migrator', 'MEMBER')
            FROM pg_catalog.pg_roles r WHERE r.rolname = session_user
            """, connection, transaction))
        {
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
        }

        await using (var setRole = new NpgsqlCommand(
            "SET LOCAL ROLE paqueteria_master_data_loader; SELECT current_user::text", connection, transaction))
        {
            Assert.Equal("paqueteria_master_data_loader", await setRole.ExecuteScalarAsync());
        }

        await transaction.RollbackAsync();
    }

    private async Task<bool> RoleExistsAsync(string role) =>
        await ScalarAdminAsync<bool>($"SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{role}')");

    private async Task<T> ScalarAdminAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.DeploymentConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task AssertRuntimeShapeAsync(string login, string password, string runtimeRole, string foreignRole)
    {
        await using var connection = await OpenAsAsync(login, password);
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"""
            SELECT r.rolcanlogin AND NOT r.rolinherit AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole
                   AND NOT r.rolreplication AND NOT r.rolbypassrls
                   AND pg_catalog.pg_has_role(current_user, '{runtimeRole}', 'SET')
                   AND NOT pg_catalog.pg_has_role(current_user, '{foreignRole}', 'MEMBER')
                   AND NOT pg_catalog.pg_has_role(current_user, 'paqueteria_migrator', 'MEMBER')
            FROM pg_catalog.pg_roles r WHERE r.rolname = current_user
            """, connection, transaction))
        {
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
        }

        await using (var setRole = new NpgsqlCommand($"SET LOCAL ROLE {runtimeRole}; SELECT current_user::text", connection, transaction))
        {
            Assert.Equal(runtimeRole, await setRole.ExecuteScalarAsync());
        }

        await transaction.RollbackAsync();
    }

    private async Task<NpgsqlConnection> OpenAsAsync(string login, string password)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.DeploymentConnectionString)
        {
            Username = login,
            Password = password,
            Pooling = false,
        }.ConnectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.DeploymentConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static string RandomPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>RFC 5802 / RFC 7677 verifier in PostgreSQL's stored format, as the deployment workflow computes it.</summary>
    internal static string ScramVerifier(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 4096, HashAlgorithmName.SHA256, 32);
        var storedKey = SHA256.HashData(HMACSHA256.HashData(salted, "Client Key"u8));
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        return $"SCRAM-SHA-256$4096:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }

    private static Dictionary<string, string?> SaveEnvironment(string connectionVariable) =>
        new[]
            {
                connectionVariable,
                RuntimeLoginProvisioner.ApiVerifierVariable,
                RuntimeLoginProvisioner.WorkerVerifierVariable,
                RuntimeLoginProvisioner.RealtimeVerifierVariable,
                RuntimeLoginProvisioner.MasterDataVerifierVariable,
            }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);

    private static void RestoreEnvironment(Dictionary<string, string?> saved)
    {
        foreach (var (name, value) in saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}

/// <summary>ENV-001 checks that need no database: bridge classification, AI-18 role coverage, verifier format.</summary>
public sealed partial class PilotAzureOwnershipBridgeContractTests
{
    [Theory]
    [InlineData("DevSynthetic", "DEV_SYNTHETIC")]
    [InlineData("Production", "PILOT_REAL_PEOPLE")]
    public void Ownership_bridge_accepts_the_exact_azure_classifications(string environment, string deploymentClass)
    {
        new AzureOwnershipBridgeSelection(environment, deploymentClass,
            AzureOwnershipBridgeSelection.AzureFlexibleServerProvider).AssertAllowed();
    }

    [Theory]
    [InlineData("Production", "DEV_SYNTHETIC", "AZURE_POSTGRESQL_FLEXIBLE_SERVER")]
    [InlineData("DevSynthetic", "PILOT_REAL_PEOPLE", "AZURE_POSTGRESQL_FLEXIBLE_SERVER")]
    [InlineData("Production", "PRODUCTION", "AZURE_POSTGRESQL_FLEXIBLE_SERVER")]
    [InlineData("Staging", "PILOT_REAL_PEOPLE", "AZURE_POSTGRESQL_FLEXIBLE_SERVER")]
    [InlineData("production", "PILOT_REAL_PEOPLE", "AZURE_POSTGRESQL_FLEXIBLE_SERVER")]
    [InlineData("Production", "PILOT_REAL_PEOPLE", "LOCAL_POSTGRESQL")]
    [InlineData("", "", "")]
    public void Ownership_bridge_rejects_any_other_classification(string environment, string deploymentClass, string provider)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AzureOwnershipBridgeSelection(environment, deploymentClass, provider).AssertAllowed());
        Assert.Contains("PILOT_REAL_PEOPLE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ownership_bridge_covers_every_privileged_nologin_role_declared_by_ai18()
    {
        var ai18 = File.ReadAllText(RepositoryPaths.Normative("database", "AI-18_DATABASE_ROLE_MODEL.sql"));
        var declared = CreateRole().Matches(ai18)
            .Select(match => (Name: match.Groups["name"].Value, BypassRls: match.Groups["rls"].Value == "BYPASSRLS"))
            .ToArray();
        Assert.NotEmpty(declared);
        Assert.Equal(declared.OrderBy(role => role.Name, StringComparer.Ordinal),
            E002Guards.CanonicalRoles.OrderBy(role => role.Name, StringComparer.Ordinal));
        Assert.Equal(declared.Where(role => role.BypassRls).Select(role => role.Name).Order(StringComparer.Ordinal),
            E002Guards.SpecializedOwners.Order(StringComparer.Ordinal));
        Assert.Contains("paqueteria_lifecycle_executor", E002Guards.SpecializedOwners);

        // Module lanes that adopt a role on an older database only (re)declare an AI-18 role with the same
        // attributes, and every OWNER TO target is a bridged specialized owner.
        var moduleSources = Directory.EnumerateFiles(Path.Combine(RepositoryPaths.Root, "src", "Modules"), "*.cs",
                SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        var laneRoles = moduleSources.SelectMany(source => ModuleCreateRole().Matches(source))
            .Select(match => (Name: match.Groups["name"].Value, BypassRls: match.Groups["rls"].Value == "BYPASSRLS"))
            .ToArray();
        Assert.Equal(
            moduleSources.Sum(source => Regex.Count(source, "CREATE ROLE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            laneRoles.Length);
        Assert.All(laneRoles, role => Assert.Contains(role, E002Guards.CanonicalRoles));
        var owners = moduleSources.SelectMany(source => OwnerTo().Matches(source).Select(match => match.Groups["role"].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(owners);
        Assert.Subset(E002Guards.SpecializedOwners.ToHashSet(StringComparer.Ordinal), owners);
    }

    [Fact]
    public void Runtime_login_provisioning_accepts_only_scram_verifiers()
    {
        Assert.True(RuntimeLoginProvisioner.IsScramVerifier(PilotRuntimeLoginContractTests.ScramVerifier(PilotRuntimeLoginContractTests.RandomPassword())));
        Assert.False(RuntimeLoginProvisioner.IsScramVerifier(null));
        Assert.False(RuntimeLoginProvisioner.IsScramVerifier(PilotRuntimeLoginContractTests.RandomPassword()));
        Assert.False(RuntimeLoginProvisioner.IsScramVerifier("md5" + new string('a', 32)));
        Assert.False(RuntimeLoginProvisioner.IsScramVerifier(PilotRuntimeLoginContractTests.ScramVerifier(PilotRuntimeLoginContractTests.RandomPassword()) + "'"));
        Assert.False(RuntimeLoginProvisioner.IsScramVerifier(PilotRuntimeLoginContractTests.ScramVerifier(PilotRuntimeLoginContractTests.RandomPassword()).Replace("$4096:", "$1000:", StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"CREATE ROLE (?<name>[a-z_]+) NOLOGIN (?<rls>NOBYPASSRLS|BYPASSRLS);", RegexOptions.CultureInvariant)]
    private static partial Regex CreateRole();

    [GeneratedRegex(@"CREATE ROLE (?<name>[a-z_]+) NOLOGIN (?<rls>NOBYPASSRLS|BYPASSRLS)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleCreateRole();

    [GeneratedRegex(@"OWNER TO (?<role>paqueteria_[a-z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerTo();
}
