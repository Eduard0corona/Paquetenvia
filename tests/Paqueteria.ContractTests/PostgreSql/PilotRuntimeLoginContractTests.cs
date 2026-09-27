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
            Environment.SetEnvironmentVariable(variable, fixture.DeploymentConnectionString);
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.ApiVerifierVariable, ScramVerifier(apiPassword));
            Environment.SetEnvironmentVariable(RuntimeLoginProvisioner.WorkerVerifierVariable, ScramVerifier(workerPassword));

            Assert.Equal(0, await DatabaseMigratorProgram.RunAsync(["runtime-logins", "--connection-env", variable]));
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.ApiLogin, apiPassword, "paqueteria_app", "paqueteria_worker");
            await AssertRuntimeShapeAsync(RuntimeLoginProvisioner.WorkerLogin, workerPassword, "paqueteria_worker", "paqueteria_app");

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
                $"DROP ROLE IF EXISTS {RuntimeLoginProvisioner.ApiLogin}; DROP ROLE IF EXISTS {RuntimeLoginProvisioner.WorkerLogin};");
        }
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
        new[] { connectionVariable, RuntimeLoginProvisioner.ApiVerifierVariable, RuntimeLoginProvisioner.WorkerVerifierVariable }
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
