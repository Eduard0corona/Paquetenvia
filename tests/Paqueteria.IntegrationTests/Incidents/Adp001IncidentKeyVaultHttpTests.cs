using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Paqueteria.Infrastructure.Security.Pii;

namespace Paqueteria.IntegrationTests.Incidents;

/// <summary>
/// ADP-001-PII-KEYVAULT-ENVELOPE through the real API and PostgreSQL with the production
/// <c>AzureKeyVault</c> protector selected by configuration and only the Key Vault key replaced by an
/// in-memory fake (no Azure access): an unreachable vault answers 503 with no effects, a reachable
/// one stores the server-chosen key version, and the plaintext description never reaches the logs,
/// the audit log or the outbox.
/// </summary>
public sealed class Adp001IncidentKeyVaultHttpTests(IncidentResolutionHttpFixture fixture)
    : IClassFixture<IncidentResolutionHttpFixture>
{
    [Fact]
    public async Task An_unavailable_key_vault_answers_503_without_effects_or_plaintext_logs()
    {
        var vault = new InMemoryKeyVault { Unavailable = true };
        var logs = new CapturingLoggerProvider();
        await using var host = Host(vault, logs);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);
        var key = $"adp001-http-down-{Guid.NewGuid():N}";
        var before = await fixture.ReadOpeningsAsync(seeded.OrderId, key);

        using var response = await OpenAsync(host, seeded, key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var after = await fixture.ReadOpeningsAsync(seeded.OrderId, key);
        Assert.Equal(before, after);
        Assert.Equal(0, after.Reservations);
        Assert.DoesNotContain(IncidentResolutionHttpFixture.SeededDescription, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        logs.AssertNoPlaintext(IncidentResolutionHttpFixture.SeededDescription);
    }

    [Fact]
    public async Task A_reachable_key_vault_stores_the_server_chosen_version_and_no_plaintext()
    {
        var vault = new InMemoryKeyVault();
        var logs = new CapturingLoggerProvider();
        await using var host = Host(vault, logs);
        var seeded = await fixture.SeedIncidentAsync(IncidentResolutionHttpFixture.TenantId);

        using var response = await OpenAsync(host, seeded, $"adp001-http-up-{Guid.NewGuid():N}");

        Assert.True(response.StatusCode == HttpStatusCode.Created, logs.Dump());
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("pii_key_version", body, StringComparison.Ordinal);
        using var created = JsonDocument.Parse(body);
        var incidentId = created.RootElement.GetProperty("id").GetGuid();

        await using var connection = new NpgsqlConnection(fixture.Api.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT i.pii_key_version,
                   position(convert_to(@plain,'UTF8') in i.description_ciphertext),
                   (SELECT count(*) FROM platform.audit_logs a
                     WHERE a.org_id=i.owner_org_id AND row_to_json(a)::text LIKE '%'||@plain||'%'),
                   (SELECT count(*) FROM platform.outbox_events o
                     WHERE o.owner_org_id=i.owner_org_id AND row_to_json(o)::text LIKE '%'||@plain||'%')
            FROM incidents.incidents i WHERE i.id=@incident
            """,
            connection);
        command.Parameters.AddWithValue("plain", IncidentResolutionHttpFixture.SeededDescription);
        command.Parameters.AddWithValue("incident", incidentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(vault.CurrentVersion, reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
        logs.AssertNoPlaintext(IncidentResolutionHttpFixture.SeededDescription);
    }

    private WebApplicationFactory<Program> Host(InMemoryKeyVault vault, CapturingLoggerProvider logs) =>
        fixture.Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Incidents:PiiProtector"] = "AzureKeyVault",
                ["PiiProtection:AzureKeyVault:KeyId"] = "https://paquetenvia-test.vault.azure.net/keys/pii-envelope",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPiiKeyWrapClient>();
                services.AddSingleton<IPiiKeyWrapClient>(vault);
            });
        });

    private static async Task<HttpResponseMessage> OpenAsync(
        WebApplicationFactory<Program> host,
        SeededIncident seeded,
        string key)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "FAILED_DELIVERY_ATTEMPT",
            ["severity"] = "MEDIUM",
            ["description"] = IncidentResolutionHttpFixture.SeededDescription,
            ["reason_code"] = "RECIPIENT_ABSENT",
            ["next_action"] = "RESCHEDULED",
            ["evidence_proof_ids"] = new[] { seeded.ProofId },
            ["occurred_at"] = DateTimeOffset.UtcNow.AddMinutes(-30).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        });
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{seeded.OrderId:D}/incidents")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MockIdentityProfiles.ActiveDispatcher);
        request.Headers.Add("X-Organization-Id", IncidentResolutionHttpFixture.TenantId.ToString("D"));
        return await client.SendAsync(request);
    }

    private sealed class InMemoryKeyVault : IPiiKeyWrapClient
    {
        private readonly RSA _rsa = RSA.Create(2048);

        public bool Unavailable { get; init; }

        public string CurrentVersion { get; } = $"akv:pii-envelope/{Guid.NewGuid():N}";

        public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken) =>
            Unavailable
                ? Task.FromException<string>(new HttpRequestException("Key Vault is unreachable."))
                : Task.FromResult(CurrentVersion);

        public Task<byte[]> WrapKeyAsync(string keyVersion, byte[] dataKey, CancellationToken cancellationToken) =>
            Task.FromResult(_rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256));

        public Task<byte[]> UnwrapKeyAsync(string keyVersion, byte[] wrappedKey, CancellationToken cancellationToken) =>
            Task.FromResult(_rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public string Dump() => string.Join("\n", _entries.Where(entry => entry.Contains("Error", StringComparison.Ordinal) || entry.Contains("Exception", StringComparison.Ordinal)));

        public void AssertNoPlaintext(string plaintext)
        {
            Assert.NotEmpty(_entries);
            Assert.DoesNotContain(_entries, entry => entry.Contains(plaintext, StringComparison.Ordinal));
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                entries.Enqueue($"{category} scope {state}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue($"{category} {logLevel} {formatter(state, exception)} {exception}");
        }
    }
}
