using System.Security.Cryptography;
using System.Xml.Linq;
using Azure.Core.Cryptography;
using Incidents.Application.Incidents;
using Incidents.Infrastructure;
using Incidents.Infrastructure.Incidents;
using Locations.Application.Geocoding;
using Locations.Infrastructure;
using Locations.Infrastructure.Geocoding;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.DataProtection;
using Paqueteria.Infrastructure.Security.Pii;
using DataProtectionOptions = Paqueteria.Infrastructure.DataProtection.DataProtectionOptions;

namespace Paqueteria.UnitTests.Security;

/// <summary>
/// ADP-001 provider selection: synthetic/mock defaults stay the default, production providers are
/// selected only by configuration and validate their options on start.
/// </summary>
public sealed class Adp001ConfigurationTests
{
    private const string PiiKeyId = "https://paquetenvia-test.vault.azure.net/keys/pii-envelope";
    private const string DataProtectionKeyId = "https://paquetenvia-test.vault.azure.net/keys/dataprotection-kek";

    [Fact]
    public void Defaults_keep_the_disabled_protectors_and_register_no_key_vault()
    {
        using var provider = BuildModules([], "Production");

        Assert.IsType<DisabledLocationPiiProtector>(provider.GetRequiredService<ILocationPiiProtector>());
        Assert.IsType<DisabledIncidentPiiProtector>(provider.GetRequiredService<IIncidentPiiProtector>());
        Assert.Null(provider.GetService<IPiiEnvelopeProtector>());
    }

    [Fact]
    public void Key_vault_is_selected_per_module_and_shares_one_protector_and_one_health_check()
    {
        using var provider = BuildModules(
            new()
            {
                ["Locations:PiiProtector"] = "AzureKeyVault",
                ["Incidents:PiiProtector"] = "AzureKeyVault",
                ["PiiProtection:AzureKeyVault:KeyId"] = PiiKeyId,
            },
            "Production",
            new FakePiiKeyVault());

        Assert.IsType<AzureKeyVaultLocationPiiProtector>(provider.GetRequiredService<ILocationPiiProtector>());
        Assert.IsType<AzureKeyVaultIncidentPiiProtector>(provider.GetRequiredService<IIncidentPiiProtector>());
        Assert.IsType<FakePiiKeyVault>(provider.GetRequiredService<IPiiKeyWrapClient>());
        var checks = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(registration => registration.Name == PiiProtectionServiceCollectionExtensions.HealthCheckName)
            .ToArray();
        var check = Assert.Single(checks);
        Assert.Contains("ready", check.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://paquetenvia-test.vault.azure.net/keys/pii-envelope/0123456789abcdef0123456789abcdef")]
    [InlineData("http://paquetenvia-test.vault.azure.net/keys/pii-envelope")]
    public void An_invalid_key_id_fails_the_start(string keyId)
    {
        using var provider = BuildModules(
            new()
            {
                ["Incidents:PiiProtector"] = "AzureKeyVault",
                ["PiiProtection:AzureKeyVault:KeyId"] = keyId,
            },
            "Production",
            new FakePiiKeyVault());

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PiiProtectionOptions>>().Value);
    }

    [Fact]
    public void The_data_protection_kek_is_off_by_default_and_requires_the_shared_ring()
    {
        Assert.Equal(DataProtectionKeyEncryptionKind.None, new DataProtectionOptions().KeyEncryption.Provider);
        Assert.False(DataProtectionOptionsValidator.IsValid(new DataProtectionOptions
        {
            Provider = DataProtectionProviderKind.Disabled,
            KeyEncryption = new DataProtectionKeyEncryptionOptions
            {
                Provider = DataProtectionKeyEncryptionKind.AzureKeyVault,
                AzureKeyVault = new DataProtectionAzureKeyVaultOptions { KeyId = DataProtectionKeyId },
            },
        }));
        Assert.False(DataProtectionOptionsValidator.IsValid(new DataProtectionOptions
        {
            Provider = DataProtectionProviderKind.PostgreSql,
            KeyEncryption = new DataProtectionKeyEncryptionOptions { Provider = DataProtectionKeyEncryptionKind.AzureKeyVault },
        }));
        Assert.True(DataProtectionOptionsValidator.IsValid(new DataProtectionOptions
        {
            Provider = DataProtectionProviderKind.PostgreSql,
            KeyEncryption = new DataProtectionKeyEncryptionOptions
            {
                Provider = DataProtectionKeyEncryptionKind.AzureKeyVault,
                AzureKeyVault = new DataProtectionAzureKeyVaultOptions { KeyId = DataProtectionKeyId },
            },
        }));
    }

    [Fact]
    public async Task With_the_kek_every_new_ring_key_is_wrapped_and_stays_readable()
    {
        var kek = new FakeKeyEncryptionKeyResolver();
        var repository = new InMemoryXmlRepository();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=not-used;Username=not-used;Password=not-used",
            ["DataProtection:Provider"] = "PostgreSql",
            ["DataProtection:KeyEncryption:Provider"] = "AzureKeyVault",
            ["DataProtection:KeyEncryption:AzureKeyVault:KeyId"] = DataProtectionKeyId,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DataProtectionKeyEncryptionKeyResolver(kek));
        services.AddPlatformDataProtection(configuration);
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
        await using var provider = services.BuildServiceProvider();

        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("adp001");
        var payload = protector.Protect("synthetic-session");

        Assert.Equal("synthetic-session", protector.Unprotect(payload));
        var element = Assert.Single(repository.GetAllElements());
        var xml = element.ToString(SaveOptions.DisableFormatting);
        Assert.Contains("encryptedKey", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
        Assert.True(kek.Wraps > 0);

        var check = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(registration => registration.Name == "data_protection_key_encryption");
        Assert.Contains("ready", check.Tags);
        Assert.Equal(HealthStatus.Healthy, (await check.Factory(provider).CheckHealthAsync(new HealthCheckContext { Registration = check })).Status);
        kek.Fail = true;
        var failing = new DataProtectionKeyEncryptionHealthCheck(
            new DataProtectionKeyEncryptionKeyResolver(kek),
            new DataProtectionKeyEncryptionKeyId(new Uri(DataProtectionKeyId)));
        Assert.Equal(HealthStatus.Unhealthy, (await failing.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    private static ServiceProvider BuildModules(
        Dictionary<string, string?> settings,
        string environment,
        IPiiKeyWrapClient? keyWrapClient = null)
    {
        settings["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=not-used;Username=not-used;Password=not-used";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        if (keyWrapClient is not null)
        {
            services.AddSingleton(keyWrapClient);
        }

        var host = new FixedEnvironment(environment);
        services.AddLocationsInfrastructure(configuration, host);
        services.AddIncidentsInfrastructure(configuration, host);
        return services.BuildServiceProvider();
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Adp001";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class InMemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements() => _elements.Select(element => new XElement(element)).ToArray();

        public void StoreElement(XElement element, string friendlyName) => _elements.Add(new XElement(element));
    }

    private sealed class FakeKeyEncryptionKeyResolver : IKeyEncryptionKeyResolver
    {
        private readonly FakeKeyEncryptionKey _key = new();

        public int Wraps => _key.Wraps;

        public bool Fail
        {
            get => _key.Fail;
            set => _key.Fail = value;
        }

        public IKeyEncryptionKey Resolve(string keyId, CancellationToken cancellationToken = default) => _key;

        public Task<IKeyEncryptionKey> ResolveAsync(string keyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IKeyEncryptionKey>(_key);
    }

    private sealed class FakeKeyEncryptionKey : IKeyEncryptionKey
    {
        private readonly RSA _rsa = RSA.Create(2048);

        public string KeyId => DataProtectionKeyId + "/0123456789abcdef0123456789abcdef";

        public int Wraps { get; private set; }

        public bool Fail { get; set; }

        public byte[] WrapKey(string algorithm, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Key Vault is unreachable.");
            }

            Wraps++;
            return _rsa.Encrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA1);
        }

        public Task<byte[]> WrapKeyAsync(string algorithm, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            Task.FromResult(WrapKey(algorithm, key, cancellationToken));

        public byte[] UnwrapKey(string algorithm, ReadOnlyMemory<byte> encryptedKey, CancellationToken cancellationToken = default) =>
            Fail
                ? throw new InvalidOperationException("Key Vault is unreachable.")
                : _rsa.Decrypt(encryptedKey.ToArray(), RSAEncryptionPadding.OaepSHA1);

        public Task<byte[]> UnwrapKeyAsync(string algorithm, ReadOnlyMemory<byte> encryptedKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(UnwrapKey(algorithm, encryptedKey, cancellationToken));
    }
}
