using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Paqueteria.Infrastructure.Cloud;

namespace Paqueteria.UnitTests.Security;

/// <summary>
/// PILOT-KEYVAULT-PRIVATE-APP-READ: the opt-in, allowlisted Key Vault configuration source for API,
/// Worker and the DatabaseMigrator, with fake readers and a fake Key Vault REST surface (no Azure).
/// </summary>
public sealed class Adp001KeyVaultSecretsConfigurationTests
{
    private const string Vault = "https://kv-paquetenvia-test.vault.azure.net/";
    private const string SecretValue = "Host=db;Username=app;Password=synthetic-secret-value-9f3a";

    [Fact]
    public void Without_a_vault_uri_the_source_is_off_and_never_builds_a_reader()
    {
        var created = 0;
        var configuration = Build(
            new() { ["ConnectionStrings:Paqueteria"] = "local" },
            (_, _) =>
            {
                created++;
                return new FakeReader();
            });

        Assert.Equal("local", configuration["ConnectionStrings:Paqueteria"]);
        Assert.Equal(0, created);
    }

    [Fact]
    public void Only_the_mapped_secrets_are_read_and_they_override_earlier_sources()
    {
        var reader = new FakeReader { ["paqueteria-app-connection"] = SecretValue, ["not-mapped"] = "never" };
        var configuration = Build(
            Settings(("paqueteria-app-connection", "ConnectionStrings:Paqueteria")),
            (uri, _) =>
            {
                Assert.Equal(new Uri(Vault), uri);
                return reader;
            },
            ("ConnectionStrings:Paqueteria", "from-environment"));

        Assert.Equal(SecretValue, configuration["ConnectionStrings:Paqueteria"]);
        Assert.Equal(["paqueteria-app-connection"], reader.Requested);
        Assert.Null(configuration["not-mapped"]);
    }

    [Fact]
    public void A_missing_secret_stops_the_host_without_leaking_any_value()
    {
        var reader = new FakeReader { ["present"] = SecretValue };
        var failure = Assert.Throws<KeyVaultSecretsStartupException>(() => Build(
            Settings(("present", "ConnectionStrings:Paqueteria"), ("absent", "ConnectionStrings:PaqueteriaWorker")),
            (_, _) => reader));

        Assert.Contains("'absent'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:PaqueteriaWorker", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_secret_stops_the_host()
    {
        Assert.Throws<KeyVaultSecretsStartupException>(() => Build(
            Settings(("empty", "ConnectionStrings:Paqueteria")),
            (_, _) => new FakeReader { ["empty"] = string.Empty }));
    }

    [Theory]
    [InlineData("http://kv-paquetenvia-test.vault.azure.net/", "a", "ConnectionStrings:Paqueteria")]
    [InlineData("https://kv-paquetenvia-test.vault.azure.net/secrets", "a", "ConnectionStrings:Paqueteria")]
    [InlineData(Vault, "*", "ConnectionStrings:Paqueteria")]
    [InlineData(Vault, "all/secrets", "ConnectionStrings:Paqueteria")]
    [InlineData(Vault, "a", "")]
    [InlineData(Vault, "a", "KeyVaultSecrets:VaultUri")]
    [InlineData(Vault, "a", "Connection Strings")]
    public void Invalid_configuration_stops_the_host(string vault, string secretName, string configurationKey)
    {
        var settings = Settings((secretName, configurationKey));
        settings["KeyVaultSecrets:VaultUri"] = vault;
        Assert.Throws<KeyVaultSecretsStartupException>(() =>
            Build(settings, (_, _) => new FakeReader { [secretName] = SecretValue }));
    }

    [Fact]
    public void A_vault_without_mappings_loads_nothing_implicitly_and_fails()
    {
        Assert.Throws<KeyVaultSecretsStartupException>(() =>
            Build(new() { ["KeyVaultSecrets:VaultUri"] = Vault }, (_, _) => new FakeReader()));
    }

    [Fact]
    public void One_secret_may_feed_several_explicit_configuration_keys_and_is_read_once()
    {
        var reader = new FakeReader { ["pg-worker-connection"] = SecretValue };
        var configuration = Build(
            Settings(
                ("pg-worker-connection", "ConnectionStrings:PaqueteriaWorker"),
                ("pg-worker-connection", "ConnectionStrings:Paqueteria")),
            (_, _) => reader);

        Assert.Equal(SecretValue, configuration["ConnectionStrings:PaqueteriaWorker"]);
        Assert.Equal(SecretValue, configuration["ConnectionStrings:Paqueteria"]);
        Assert.Equal(["pg-worker-connection"], reader.Requested);
    }

    [Fact]
    public void Duplicated_configuration_keys_fail()
    {
        Assert.Throws<KeyVaultSecretsStartupException>(() => Build(
            Settings(("a", "ConnectionStrings:Paqueteria"), ("b", "connectionstrings:paqueteria")),
            (_, _) => new FakeReader { ["a"] = SecretValue, ["b"] = SecretValue }));
    }

    [Fact]
    public void Mapped_secrets_are_returned_alone_and_nothing_when_the_source_is_off()
    {
        var mapped = KeyVaultSecretsConfiguration.LoadMappedSecrets(
            new ConfigurationBuilder().AddInMemoryCollection(Settings(
                ("paqueteria-migration-connection", "PAQUETERIA_MIGRATION_CONNECTION"))).Build(),
            (_, _) => new FakeReader { ["paqueteria-migration-connection"] = SecretValue });
        Assert.Equal(SecretValue, Assert.Single(mapped).Value);
        Assert.True(mapped.ContainsKey("PAQUETERIA_MIGRATION_CONNECTION"));

        var off = KeyVaultSecretsConfiguration.LoadMappedSecrets(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["X"] = "y" }).Build(),
            (_, _) => throw new InvalidOperationException("must not be built"));
        Assert.Empty(off);
    }

    [Fact]
    public async Task The_real_sdk_reader_reads_one_secret_and_refuses_disabled_or_denied_ones()
    {
        var handler = new FakeSecretsHandler();
        var reader = new AzureKeyVaultSecretReader(
            new Uri(Vault),
            new FakeTokenCredential(),
            TimeSpan.FromSeconds(10),
            new HttpClientTransport(new HttpClient(handler)));

        Assert.Equal(SecretValue, await reader.GetSecretValueAsync("paqueteria-app-connection", default));
        Assert.Equal(["/secrets/paqueteria-app-connection/"], handler.Paths);
        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => reader.GetSecretValueAsync("disabled-secret", default));
        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => reader.GetSecretValueAsync("denied-secret", default));

        // Through the source, a denied secret stops the host and the message carries no body.
        var failure = Assert.Throws<KeyVaultSecretsStartupException>(() => Build(
            Settings(("denied-secret", "ConnectionStrings:Paqueteria")),
            (_, _) => reader));
        Assert.Contains("status 403", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", failure.ToString(), StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> Settings(params (string Secret, string Key)[] mappings)
    {
        var settings = new Dictionary<string, string?> { ["KeyVaultSecrets:VaultUri"] = Vault };
        for (var index = 0; index < mappings.Length; index++)
        {
            settings[$"KeyVaultSecrets:Mappings:{index}:SecretName"] = mappings[index].Secret;
            settings[$"KeyVaultSecrets:Mappings:{index}:ConfigurationKey"] = mappings[index].Key;
        }

        return settings;
    }

    private static IConfiguration Build(
        Dictionary<string, string?> settings,
        Func<Uri, TimeSpan, IKeyVaultSecretReader> readerFactory,
        params (string Key, string Value)[] earlier)
    {
        var builder = new ConfigurationManager();
        builder.AddInMemoryCollection(earlier.ToDictionary(item => item.Key, item => (string?)item.Value));
        builder.AddInMemoryCollection(settings);
        builder.AddPaqueteriaKeyVaultSecrets(readerFactory);
        return builder;
    }

    private sealed class FakeReader : Dictionary<string, string>, IKeyVaultSecretReader
    {
        public List<string> Requested { get; } = [];

        public Task<string> GetSecretValueAsync(string secretName, CancellationToken cancellationToken)
        {
            Requested.Add(secretName);
            return TryGetValue(secretName, out var value)
                ? Task.FromResult(value)
                : Task.FromException<string>(new Azure.RequestFailedException(404, "SecretNotFound synthetic", "SecretNotFound", null));
        }
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class FakeSecretsHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is null)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Bearer",
                    "authorization=\"https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000\", resource=\"https://vault.azure.net\""));
                return Task.FromResult(challenge);
            }

            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var name = path.Trim('/').Split('/')[1];
            if (name == "denied-secret")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        "{\"error\":{\"code\":\"Forbidden\",\"message\":\"synthetic body\"}}", Encoding.UTF8, "application/json"),
                });
            }

            var body = new JsonObject
            {
                ["value"] = SecretValue,
                ["id"] = $"{Vault}secrets/{name}/0123456789abcdef0123456789abcdef",
                ["attributes"] = new JsonObject { ["enabled"] = name != "disabled-secret" },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
