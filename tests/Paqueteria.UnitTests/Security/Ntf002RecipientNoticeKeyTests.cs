using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;
using Locations.Application.Geocoding;
using Locations.Infrastructure;
using Locations.Infrastructure.Locations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.UnitTests.Security;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (E4-P2; AI-24 <c>recipient_notices.contact.notice_key</c>; AI-08 NTF-002): the
/// recipient-notice key client driven through the real Azure SDK against an in-process fake of the Key Vault keys API.
/// It only reads the key and wraps in process with its public part, so no wrap, unwrap or decrypt request ever reaches
/// Key Vault; the wrapped data key opens with the private key under RSA-OAEP-256 (the Worker's future unwrapKey). The
/// Locations selector keeps it off by default and validates it on start like the ADP-001 PII key.
/// </summary>
public sealed class Ntf002RecipientNoticeKeyTests
{
    private const string NoticeKeyId = "https://paquetenvia-test.vault.azure.net/keys/recipient-notice-kek";
    private const string PiiKeyId = "https://paquetenvia-test.vault.azure.net/keys/pii-envelope";
    private static readonly PiiBinding Row = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Fact]
    public async Task The_api_reads_the_key_and_wraps_locally_so_only_the_private_key_opens_the_copy()
    {
        var vault = new FakeNoticeKeyVault();
        var client = CreateClient(vault, new Adp001KeyVaultWrapClientHttpTests.ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T12:00:00Z")));
        var sealer = new RecipientNoticeEnvelopeSealer(client);

        var batch = await sealer.SealAsync(Row, [new PiiPlaintext("locations.notice_email", "destinatario@example.invalid")], default);

        Assert.Equal($"akv:recipient-notice-kek/{vault.CurrentVersion}", batch.KeyVersion);
        Assert.All(vault.Requests, request => Assert.StartsWith("GET /keys/recipient-notice-kek", request, StringComparison.Ordinal));
        Assert.DoesNotContain(vault.Requests, request => request.Contains("wrapkey", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("decrypt", StringComparison.OrdinalIgnoreCase));
        var worker = new PiiEnvelopeProtector(vault.PrivateKeyClient());
        Assert.Equal(
            "destinatario@example.invalid",
            await worker.UnprotectAsync(Row, "locations.notice_email", batch.Ciphertexts[0], batch.KeyVersion, default));
    }

    [Fact]
    public async Task Rotation_seals_under_the_new_version_and_earlier_copies_stay_readable()
    {
        var vault = new FakeNoticeKeyVault();
        var time = new Adp001KeyVaultWrapClientHttpTests.ManualTimeProvider(DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
        var sealer = new RecipientNoticeEnvelopeSealer(CreateClient(vault, time));

        var before = await sealer.SealAsync(Row, [new PiiPlaintext("locations.notice_phone", "6672222222")], default);
        vault.Rotate();
        time.Advance(TimeSpan.FromSeconds(120));
        var cached = await sealer.SealAsync(Row, [new PiiPlaintext("locations.notice_phone", "6672222222")], default);
        time.Advance(TimeSpan.FromSeconds(200));
        var after = await sealer.SealAsync(Row, [new PiiPlaintext("locations.notice_phone", "6673333333")], default);

        Assert.Equal(before.KeyVersion, cached.KeyVersion);
        Assert.NotEqual(before.KeyVersion, after.KeyVersion);
        var worker = new PiiEnvelopeProtector(vault.PrivateKeyClient());
        Assert.Equal("6672222222", await worker.UnprotectAsync(Row, "locations.notice_phone", before.Ciphertexts[0], before.KeyVersion, default));
        Assert.Equal("6673333333", await worker.UnprotectAsync(Row, "locations.notice_phone", after.Ciphertexts[0], after.KeyVersion, default));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("disabled")]
    [InlineData("expired")]
    [InlineData("not-rsa")]
    [InlineData("no-wrap")]
    [InlineData("short")]
    public async Task An_unusable_key_fails_closed_without_the_value(string defect)
    {
        var vault = new FakeNoticeKeyVault(defect);
        var sealer = new RecipientNoticeEnvelopeSealer(CreateClient(vault, TimeProvider.System));

        var failure = await Assert.ThrowsAsync<PiiProtectionUnavailableException>(() =>
            sealer.SealAsync(Row, [new PiiPlaintext("locations.notice_email", "destinatario@example.invalid")], default));

        Assert.DoesNotContain("destinatario@example.invalid", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_version_naming_another_key_is_never_wrapped_and_the_client_has_no_unwrap()
    {
        var vault = new FakeNoticeKeyVault();
        var client = CreateClient(vault, TimeProvider.System);

        await Assert.ThrowsAsync<PiiProtectionUnavailableException>(() =>
            client.WrapKeyAsync($"akv:pii-envelope/{vault.CurrentVersion}", new byte[32], default));
        Assert.Empty(vault.Requests);
        Assert.DoesNotContain(
            typeof(IRecipientNoticeKeyWrapClient).GetMethods().Concat(typeof(AzureKeyVaultRecipientNoticeKeyWrapClient).GetMethods()),
            method => method.Name.Contains("Unwrap", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Decrypt", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Unprotect", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(IRecipientNoticeEnvelopeSealer).GetMethods(),
            method => method.Name.Contains("Unprotect", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Open", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task By_default_no_notice_copy_is_written_and_nothing_azure_is_built()
    {
        await using var provider = Build([], "Production");

        Assert.Equal(LocationNoticeKeyProtectorKind.Disabled, provider.GetRequiredService<IOptions<LocationsOptions>>().Value.NoticeKeyProtector);
        using var scope = provider.CreateScope();
        Assert.IsType<DisabledLocationNoticeContactSealer>(scope.ServiceProvider.GetRequiredService<ILocationNoticeContactSealer>());
        var check = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(registration => registration.Name == RecipientNoticeKeyServiceCollectionExtensions.HealthCheckName);
        Assert.Contains("ready", check.Tags);
        Assert.Equal(
            HealthStatus.Healthy,
            (await check.Factory(provider).CheckHealthAsync(new HealthCheckContext { Registration = check })).Status);
    }

    [Fact]
    public void The_key_vault_notice_key_is_selected_by_configuration_next_to_the_key_vault_pii_key()
    {
        var vault = new FakeNoticeKeyVault();
        using var provider = Build(
            new()
            {
                ["Locations:PiiProtector"] = "AzureKeyVault",
                ["Locations:NoticeKeyProtector"] = "AzureKeyVault",
                ["PiiProtection:AzureKeyVault:KeyId"] = PiiKeyId,
                ["PiiProtection:RecipientNoticeKey:KeyId"] = NoticeKeyId,
            },
            "Production",
            CreateClient(vault, TimeProvider.System));

        using var scope = provider.CreateScope();
        Assert.IsType<AzureKeyVaultLocationNoticeContactSealer>(scope.ServiceProvider.GetRequiredService<ILocationNoticeContactSealer>());
        Assert.Equal(NoticeKeyId, provider.GetRequiredService<IOptions<RecipientNoticeKeyOptions>>().Value.KeyId);
    }

    [Theory]
    [InlineData("Mock", "Mock", "Production", null, false)]
    [InlineData("Mock", "Mock", "Development", null, true)]
    [InlineData("Mock", "Mock", "Testing", null, true)]
    [InlineData("Mock", "AzureKeyVault", "Development", NoticeKeyId, false)]
    [InlineData("AzureKeyVault", "AzureKeyVault", "Production", PiiKeyId, false)]
    [InlineData("AzureKeyVault", "AzureKeyVault", "Production", "", false)]
    [InlineData("AzureKeyVault", "AzureKeyVault", "Production", "https://paquetenvia-test.vault.azure.net/keys/recipient-notice-kek/0123456789abcdef0123456789abcdef", false)]
    [InlineData("AzureKeyVault", "AzureKeyVault", "Production", NoticeKeyId, true)]
    [InlineData("AzureKeyVault", "Disabled", "Production", null, true)]
    [InlineData("AzureKeyVault", "Unknown", "Production", null, false)]
    public void The_selector_is_validated_on_start(
        string piiProtector,
        string noticeKeyProtector,
        string environment,
        string? noticeKeyId,
        bool valid)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Locations:PiiProtector"] = piiProtector,
            ["Locations:NoticeKeyProtector"] = noticeKeyProtector,
            ["PiiProtection:AzureKeyVault:KeyId"] = PiiKeyId,
        };
        if (noticeKeyId is not null)
        {
            settings["PiiProtection:RecipientNoticeKey:KeyId"] = noticeKeyId;
        }

        using var provider = Build(settings, environment);

        Exception? failure = Record.Exception(() =>
        {
            _ = provider.GetRequiredService<IOptions<LocationsOptions>>().Value;
            _ = provider.GetRequiredService<IOptions<RecipientNoticeKeyOptions>>().Value;
        });
        Assert.Equal(valid, failure is null);
        if (!valid)
        {
            Assert.True(failure is OptionsValidationException or InvalidOperationException, failure?.GetType().FullName);
        }
    }

    private static AzureKeyVaultRecipientNoticeKeyWrapClient CreateClient(FakeNoticeKeyVault vault, TimeProvider time) =>
        new(
            Options.Create(new RecipientNoticeKeyOptions { KeyId = NoticeKeyId, CurrentVersionRefreshSeconds = 300 }),
            new FakeTokenCredential(),
            time,
            new HttpClientTransport(new HttpClient(vault)));

    private static ServiceProvider Build(
        Dictionary<string, string?> settings,
        string environment,
        IRecipientNoticeKeyWrapClient? noticeClient = null)
    {
        settings["ConnectionStrings:Paqueteria"] = "Host=localhost;Database=not-used;Username=not-used;Password=not-used";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IPiiKeyWrapClient>(new FakePiiKeyVault());
        if (noticeClient is not null)
        {
            services.AddSingleton(noticeClient);
        }

        services.AddLocationsInfrastructure(configuration, new FixedEnvironment(environment));
        return services.BuildServiceProvider();
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Paqueteria.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    /// <summary>The keys API of Key Vault, GET only: any POST (wrap, unwrap, decrypt) is recorded and refused.</summary>
    private sealed class FakeNoticeKeyVault : HttpMessageHandler
    {
        private readonly Dictionary<string, RSA> _keys = new(StringComparer.Ordinal);

        public FakeNoticeKeyVault(string? defect = null)
        {
            Defect = defect;
            Rotate();
        }

        public string CurrentVersion { get; private set; } = string.Empty;

        public string? Defect { get; }

        public List<string> Requests { get; } = [];

        public void Rotate()
        {
            CurrentVersion = Guid.NewGuid().ToString("N");
            _keys[CurrentVersion] = RSA.Create(Defect == "short" ? 1024 : 3072);
        }

        public IPiiKeyWrapClient PrivateKeyClient() => new PrivateKeys(_keys);

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

            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            var segments = request.RequestUri.AbsolutePath.Trim('/').Split('/');
            if (request.Method != HttpMethod.Get || segments is not ["keys", "recipient-notice-kek", ..])
            {
                return Task.FromResult(Error(HttpStatusCode.Forbidden, "Forbidden"));
            }

            if (Defect == "denied")
            {
                return Task.FromResult(Error(HttpStatusCode.Forbidden, "Forbidden"));
            }

            var version = segments.Length >= 3 && segments[2].Length > 0 ? segments[2] : CurrentVersion;
            return Task.FromResult(_keys.TryGetValue(version, out var rsa)
                ? Json(KeyBundle(version, rsa))
                : Error(HttpStatusCode.NotFound, "KeyNotFound"));
        }

        private JsonObject KeyBundle(string version, RSA rsa)
        {
            var parameters = rsa.ExportParameters(false);
            var attributes = new JsonObject
            {
                ["enabled"] = Defect != "disabled",
                ["created"] = 1_790_000_000,
                ["updated"] = 1_790_000_000,
                ["recoveryLevel"] = "Recoverable",
            };
            if (Defect == "expired")
            {
                attributes["exp"] = 1_700_000_000;
            }

            return new JsonObject
            {
                ["key"] = new JsonObject
                {
                    ["kid"] = $"https://paquetenvia-test.vault.azure.net/keys/recipient-notice-kek/{version}",
                    ["kty"] = Defect == "not-rsa" ? "EC" : "RSA",
                    ["key_ops"] = Defect == "no-wrap" ? new JsonArray("unwrapKey") : new JsonArray("wrapKey", "unwrapKey"),
                    ["n"] = Base64UrlEncode(parameters.Modulus!),
                    ["e"] = Base64UrlEncode(parameters.Exponent!),
                },
                ["attributes"] = attributes,
            };
        }

        private static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { code, message = "synthetic" } }),
                Encoding.UTF8,
                "application/json"),
        };

        private static string Base64UrlEncode(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Stands in for Key Vault unwrapKey (RSA-OAEP-256) on the Worker side; the API never has it.</summary>
        private sealed class PrivateKeys(Dictionary<string, RSA> keys) : IPiiKeyWrapClient
        {
            public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken) =>
                Task.FromResult(keys[keyVersion[(keyVersion.LastIndexOf('/') + 1)..]].Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256));
        }
    }
}
