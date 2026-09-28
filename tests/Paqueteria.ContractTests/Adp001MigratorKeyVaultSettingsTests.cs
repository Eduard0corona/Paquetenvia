using Microsoft.Extensions.Configuration;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.ContractTests;

/// <summary>
/// PILOT-KEYVAULT-PRIVATE-APP-READ for the DatabaseMigrator: every named setting it reads through
/// <c>DatabaseMigratorProgram.ReadSetting</c> (the connection and the ENV-001 runtime-login verifiers)
/// resolves from environment variables overlaid by the mapped Key Vault secrets. Fake reader, no Azure.
/// </summary>
public sealed class Adp001MigratorKeyVaultSettingsTests
{
    private const string Vault = "https://kv-paquetenvia-test.vault.azure.net/";
    private const string Connection = "Host=db;Username=migrator;Password=synthetic-migration-secret";
    private const string ApiVerifier = "SCRAM-SHA-256$4096:c2FsdA==$c3RvcmVk:c2VydmVy";
    private const string WorkerVerifier = "SCRAM-SHA-256$4096:d29ya2Vy$c3RvcmVk:c2VydmVy";

    [Fact]
    public void Mapped_connection_and_runtime_login_verifiers_are_visible_to_the_migrator_lookup()
    {
        var read = DatabaseMigratorProgram.CreateSettingReader(Settings(
            ("pg-migration-connection", "PAQUETERIA_MIGRATION_CONNECTION", Connection),
            ("pg-api-login-verifier", "PAQUETERIA_API_LOGIN_VERIFIER", ApiVerifier),
            ("pg-worker-login-verifier", "PAQUETERIA_WORKER_LOGIN_VERIFIER", WorkerVerifier)));

        Assert.Equal(Connection, read("PAQUETERIA_MIGRATION_CONNECTION"));
        Assert.Equal(ApiVerifier, read("PAQUETERIA_API_LOGIN_VERIFIER"));
        Assert.Equal(WorkerVerifier, read("PAQUETERIA_WORKER_LOGIN_VERIFIER"));
        Assert.Null(read("PAQUETERIA_UNMAPPED_SETTING"));
    }

    [Fact]
    public void A_mapped_secret_overrides_the_same_environment_name()
    {
        var read = DatabaseMigratorProgram.CreateSettingReader(Settings(
            [("pg-api-login-verifier", "PAQUETERIA_API_LOGIN_VERIFIER", ApiVerifier)],
            ("PAQUETERIA_API_LOGIN_VERIFIER", "from-environment")));

        Assert.Equal(ApiVerifier, read("PAQUETERIA_API_LOGIN_VERIFIER"));
    }

    [Fact]
    public void Without_the_source_the_lookup_is_the_environment_only()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["PAQUETERIA_MIGRATION_CONNECTION"] = "env" });
        configuration.AddPaqueteriaKeyVaultSecrets((_, _) => throw new InvalidOperationException("must not be built"));

        Assert.Equal("env", DatabaseMigratorProgram.CreateSettingReader(configuration)("PAQUETERIA_MIGRATION_CONNECTION"));
    }

    private static IConfiguration Settings(params (string Secret, string Key, string Value)[] mappings) =>
        Settings(mappings, []);

    private static IConfiguration Settings(
        (string Secret, string Key, string Value)[] mappings,
        params (string Key, string Value)[] environment)
    {
        var settings = new Dictionary<string, string?> { ["KeyVaultSecrets:VaultUri"] = Vault };
        for (var index = 0; index < mappings.Length; index++)
        {
            settings[$"KeyVaultSecrets:Mappings:{index}:SecretName"] = mappings[index].Secret;
            settings[$"KeyVaultSecrets:Mappings:{index}:ConfigurationKey"] = mappings[index].Key;
        }

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(environment.ToDictionary(item => item.Key, item => (string?)item.Value));
        configuration.AddInMemoryCollection(settings);
        configuration.AddPaqueteriaKeyVaultSecrets((_, _) => new Reader(mappings.ToDictionary(item => item.Secret, item => item.Value)));
        return configuration;
    }

    private sealed class Reader(Dictionary<string, string> values) : IKeyVaultSecretReader
    {
        public Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken) =>
            Task.FromResult(values[secretName]);
    }
}
