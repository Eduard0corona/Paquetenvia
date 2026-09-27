using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Options;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.UnitTests.Security;

/// <summary>
/// The production Key Vault client driven through the real Azure SDK against an in-process fake of
/// the Key Vault REST surface (challenge authentication, get key, wrap/unwrap). No Azure access: the
/// fake answers on a <see cref="HttpMessageHandler"/> plugged in as the SDK transport.
/// </summary>
public sealed class Adp001KeyVaultWrapClientHttpTests
{
    private const string KeyId = "https://paquetenvia-test.vault.azure.net/keys/pii-envelope";

    [Fact]
    public async Task The_server_reads_the_current_version_from_key_vault_and_rotation_keeps_old_rows_readable()
    {
        var vault = new FakeKeyVaultHandler();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-27T12:00:00Z"));
        var client = CreateClient(vault, time);
        var protector = new PiiEnvelopeProtector(client);

        var before = await protector.ProtectAsync([new PiiPlaintext("incidents.description", "Descripcion sintetica N")], default);
        Assert.Equal($"akv:pii-envelope/{vault.CurrentVersion}", before.KeyVersion);

        var previous = vault.CurrentVersion;
        vault.Rotate();
        time.Advance(TimeSpan.FromSeconds(301));
        var after = await protector.ProtectAsync([new PiiPlaintext("incidents.description", "Descripcion sintetica N+1")], default);

        Assert.Equal($"akv:pii-envelope/{vault.CurrentVersion}", after.KeyVersion);
        Assert.NotEqual(before.KeyVersion, after.KeyVersion);
        Assert.Equal(
            "Descripcion sintetica N",
            await protector.UnprotectAsync("incidents.description", before.Ciphertexts[0], before.KeyVersion, default));
        Assert.Contains(vault.UnwrapPaths, path => path.Contains($"/keys/pii-envelope/{previous}/unwrapkey", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "Descripcion sintetica N+1",
            await protector.UnprotectAsync("incidents.description", after.Ciphertexts[0], after.KeyVersion, default));
    }

    [Fact]
    public async Task The_cached_version_is_reused_until_the_refresh_interval_elapses()
    {
        var vault = new FakeKeyVaultHandler();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-27T12:00:00Z"));
        var client = CreateClient(vault, time);

        var first = await client.GetCurrentKeyVersionAsync(default);
        vault.Rotate();
        time.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(first, await client.GetCurrentKeyVersionAsync(default));
        time.Advance(TimeSpan.FromSeconds(200));
        Assert.NotEqual(first, await client.GetCurrentKeyVersionAsync(default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task A_denied_or_disabled_key_fails_closed(HttpStatusCode status, bool disableKey)
    {
        var vault = new FakeKeyVaultHandler { GetKeyStatus = status, KeyEnabled = !disableKey };
        var protector = new PiiEnvelopeProtector(CreateClient(vault, new ManualTimeProvider(DateTimeOffset.UtcNow)));

        await Assert.ThrowsAsync<PiiProtectionUnavailableException>(() =>
            protector.ProtectAsync([new PiiPlaintext("locations.address_text", "Calle Sintetica 1")], default));
    }

    [Fact]
    public async Task A_row_naming_another_key_is_never_sent_to_key_vault()
    {
        var vault = new FakeKeyVaultHandler();
        var client = CreateClient(vault, new ManualTimeProvider(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<PiiProtectionUnavailableException>(() =>
            client.UnwrapKeyAsync($"akv:another-key/{vault.CurrentVersion}", [1, 2, 3], default));
        Assert.Empty(vault.UnwrapPaths);
    }

    private static AzureKeyVaultPiiKeyWrapClient CreateClient(FakeKeyVaultHandler vault, TimeProvider time) =>
        new(
            Options.Create(new PiiProtectionOptions
            {
                AzureKeyVault = new AzureKeyVaultPiiOptions { KeyId = KeyId, CurrentVersionRefreshSeconds = 300 },
            }),
            new FakeTokenCredential(),
            time,
            new HttpClientTransport(new HttpClient(vault)));

    private sealed class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>Minimal Key Vault keys API: challenge, get key (by name or version), wrap, unwrap.</summary>
    private sealed class FakeKeyVaultHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, RSA> _keys = new(StringComparer.Ordinal);

        public FakeKeyVaultHandler()
        {
            Rotate();
        }

        public string CurrentVersion { get; private set; } = string.Empty;

        public HttpStatusCode GetKeyStatus { get; set; } = HttpStatusCode.OK;

        public bool KeyEnabled { get; set; } = true;

        public List<string> UnwrapPaths { get; } = [];

        public void Rotate()
        {
            CurrentVersion = Guid.NewGuid().ToString("N");
            _keys[CurrentVersion] = RSA.Create(2048);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is null)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Bearer",
                    "authorization=\"https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000\", resource=\"https://vault.azure.net\""));
                return challenge;
            }

            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            if (segments is not ["keys", "pii-envelope", ..])
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Get)
            {
                if (GetKeyStatus != HttpStatusCode.OK)
                {
                    return Error(GetKeyStatus, "Forbidden");
                }

                var version = segments.Length >= 3 && segments[2].Length > 0 ? segments[2] : CurrentVersion;
                return _keys.TryGetValue(version, out var rsa) ? Json(KeyBundle(version, rsa)) : Error(HttpStatusCode.NotFound, "KeyNotFound");
            }

            if (request.Method == HttpMethod.Post && segments.Length == 4)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var value = Base64UrlDecode(body["value"]!.GetValue<string>());
                var rsa = _keys[segments[2]];
                switch (segments[3].ToLowerInvariant())
                {
                    case "wrapkey":
                        return Json(new JsonObject
                        {
                            ["kid"] = Kid(segments[2]),
                            ["value"] = Base64UrlEncode(rsa.Encrypt(value, RSAEncryptionPadding.OaepSHA256)),
                        });
                    case "unwrapkey":
                        UnwrapPaths.Add(request.RequestUri.AbsolutePath);
                        return Json(new JsonObject
                        {
                            ["kid"] = Kid(segments[2]),
                            ["value"] = Base64UrlEncode(rsa.Decrypt(value, RSAEncryptionPadding.OaepSHA256)),
                        });
                }
            }

            return Error(HttpStatusCode.BadRequest, "BadParameter");
        }

        private JsonObject KeyBundle(string version, RSA rsa)
        {
            var parameters = rsa.ExportParameters(false);
            return new JsonObject
            {
                ["key"] = new JsonObject
                {
                    ["kid"] = Kid(version),
                    ["kty"] = "RSA",
                    ["key_ops"] = new JsonArray("wrapKey", "unwrapKey"),
                    ["n"] = Base64UrlEncode(parameters.Modulus!),
                    ["e"] = Base64UrlEncode(parameters.Exponent!),
                },
                ["attributes"] = new JsonObject
                {
                    ["enabled"] = KeyEnabled,
                    ["created"] = 1_790_000_000,
                    ["updated"] = 1_790_000_000,
                    ["recoveryLevel"] = "Recoverable",
                },
            };
        }

        private static string Kid(string version) => $"https://paquetenvia-test.vault.azure.net/keys/pii-envelope/{version}";

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

        private static byte[] Base64UrlDecode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
        }
    }
}
