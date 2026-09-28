using Microsoft.Extensions.Configuration;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.ContractTests;

/// <summary>
/// PILOT-KEYVAULT-PRIVATE-APP-READ for the DatabaseMigrator: every named setting it reads through
/// <c>DatabaseMigratorProgram.ReadSetting</c> (the connection and the ENV-001 runtime-login verifiers)
/// resolves to the mapped Key Vault secret first and otherwise to the live environment. Fake reader, no Azure.
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
        var keyVault = Mapped(
            ("pg-migration-connection", "PAQUETERIA_MIGRATION_CONNECTION", Connection),
            ("pg-api-login-verifier", "PAQUETERIA_API_LOGIN_VERIFIER", ApiVerifier),
            ("pg-worker-login-verifier", "PAQUETERIA_WORKER_LOGIN_VERIFIER", WorkerVerifier));

        Assert.Equal(Connection, DatabaseMigratorProgram.ResolveSetting("PAQUETERIA_MIGRATION_CONNECTION", keyVault, _ => null));
        Assert.Equal(ApiVerifier, DatabaseMigratorProgram.ResolveSetting("PAQUETERIA_API_LOGIN_VERIFIER", keyVault, _ => null));
        Assert.Equal(WorkerVerifier, DatabaseMigratorProgram.ResolveSetting("PAQUETERIA_WORKER_LOGIN_VERIFIER", keyVault, _ => null));
        Assert.Null(DatabaseMigratorProgram.ResolveSetting("PAQUETERIA_UNMAPPED_SETTING", keyVault, _ => null));
    }

    [Fact]
    public void A_mapped_secret_overrides_the_environment_and_unmapped_names_read_the_environment()
    {
        var keyVault = Mapped(("pg-api-login-verifier", "PAQUETERIA_API_LOGIN_VERIFIER", ApiVerifier));
        Func<string, string?> environment = name => name switch
        {
            "PAQUETERIA_API_LOGIN_VERIFIER" => "from-environment",
            "PAQUETERIA_MIGRATION_CONNECTION" => "env-connection",
            _ => null,
        };

        Assert.Equal(ApiVerifier, DatabaseMigratorProgram.ResolveSetting("PAQUETERIA_API_LOGIN_VERIFIER", keyVault, environment));
        Assert.Equal("env-connection", DatabaseMigratorProgram.ResolveSetting("PAQUETERIA_MIGRATION_CONNECTION", keyVault, environment));
    }

    [Fact]
    public void The_real_lookup_reads_the_environment_at_call_time_while_the_source_is_off()
    {
        // Regression: a process-wide snapshot of the environment hid variables set after the first
        // lookup (E002SemanticContractTests drives the migrator in-process with fresh variables).
        var name = $"PAQUETERIA_ADP001_LIVE_{Guid.NewGuid():N}";
        Assert.Null(DatabaseMigratorProgram.ReadSetting(name));
        Environment.SetEnvironmentVariable(name, "first");
        try
        {
            Assert.Equal("first", DatabaseMigratorProgram.ReadSetting(name));
            Environment.SetEnvironmentVariable(name, "second");
            Assert.Equal("second", DatabaseMigratorProgram.ReadSetting(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static IReadOnlyDictionary<string, string> Mapped(params (string Secret, string Key, string Value)[] mappings)
    {
        var settings = new Dictionary<string, string?> { ["KeyVaultSecrets:VaultUri"] = Vault };
        for (var index = 0; index < mappings.Length; index++)
        {
            settings[$"KeyVaultSecrets:Mappings:{index}:SecretName"] = mappings[index].Secret;
            settings[$"KeyVaultSecrets:Mappings:{index}:ConfigurationKey"] = mappings[index].Key;
        }

        return KeyVaultSecretsConfiguration.LoadMappedSecrets(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            (_, _) => new Reader(mappings.ToDictionary(item => item.Secret, item => item.Value)));
    }

    private sealed class Reader(Dictionary<string, string> values) : IKeyVaultSecretReader
    {
        public Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken) =>
            Task.FromResult(values[secretName]);
    }
}
