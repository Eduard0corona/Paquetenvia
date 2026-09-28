using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Identity.Infrastructure.Mock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Tracking;

/// <summary>
/// TRK-002-ISSUE-ENDPOINT end to end: authenticated HTTP, the real tenant pipeline, the least-privilege runtime role
/// under RLS and the anonymous public lookup. The plaintext is returned once, only its hash is stored, every issue,
/// rotation and revocation is audited, a revoked link is the uniform 404 and another organization is refused with
/// the same 404 without any effect.
/// </summary>
[Collection(PublicTrackingPostgreSqlCollection.Name)]
[Trait("Category", "PublicTrackingPostgreSql")]
public sealed class PublicTrackingLinkPostgreSqlHttpTests(PostgreSqlSecurityWebApplicationFactory factory)
{
    private const string UniformNotFound = """{"type":"about:blank","title":"Not Found","status":404}""";
    private static readonly Guid PlatformAdminNoMfaId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3");

    [Fact]
    public async Task Issue_rotate_and_revoke_over_HTTP_return_the_token_once_audit_and_close_the_public_link()
    {
        // The authenticated operations run on a host whose every log line and scope is captured. The anonymous
        // lookups run on the plain host: the public URL carries the token by design (TRK-001), so the framework's
        // request-path scope of a lookup is outside what this test proves.
        var logs = new CapturingLoggerProvider();
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
        using var client = host.CreateClient();
        using var lookup = factory.CreateClient();
        var orderId = await CreateOrderAsync();

        var issueKey = Key();
        using var issued = await PostAsync(client, MockIdentityProfiles.ActivePlatformAdminNoMfa, IssuePath(orderId), issueKey);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        Assert.Equal("no-store", issued.Headers.CacheControl?.ToString());
        var first = await ReadLinkAsync(issued);
        Assert.Equal(orderId, first.OrderId);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.Token);
        await AssertLookupAsync(lookup, first.Token, HttpStatusCode.OK);

        var rotateKey = Key();
        using var rotated = await PostAsync(client, MockIdentityProfiles.ActivePlatformAdminNoMfa, IssuePath(orderId), rotateKey);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);
        var second = await ReadLinkAsync(rotated);
        Assert.NotEqual(first.Token, second.Token);
        await AssertLookupAsync(lookup, first.Token, HttpStatusCode.NotFound);
        await AssertLookupAsync(lookup, second.Token, HttpStatusCode.OK);

        var revokeKey = Key();
        using var revoked = await PostAsync(client, MockIdentityProfiles.ActivePlatformAdminNoMfa, RevokePath(orderId), revokeKey);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        await AssertLookupAsync(lookup, second.Token, HttpStatusCode.NotFound);

        // Only hashes are stored; the audit trail names each step and the key of the request that caused it.
        var state = await ReadTokenStateAsync(orderId);
        Assert.Equal(2, state.Hashes.Count);
        Assert.Contains(state.Hashes, hash => hash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(first.Token))));
        Assert.Contains(state.Hashes, hash => hash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(second.Token))));
        Assert.Equal(0, state.Active);
        Assert.Equal(
            [
                ("TRACKING_TOKEN_ISSUED", issueKey),
                ("TRACKING_TOKEN_ROTATED", rotateKey),
                ("TRACKING_TOKEN_REVOKED", revokeKey),
            ],
            await ReadAuditsAsync(orderId, PlatformAdminNoMfaId));
        foreach (var token in new[] { first.Token, second.Token })
        {
            Assert.Equal(0L, await CountPlaintextAsync(token));
            var leaked = logs.Entries
                .Where(entry => entry.Contains(token, StringComparison.Ordinal))
                .Select(entry => entry.Replace(token, "<token>", StringComparison.Ordinal))
                .ToArray();
            Assert.True(leaked.Length == 0, string.Join('\n', leaked));
        }

        // Replaying the revocation succeeds without another audit row.
        using var replay = await PostAsync(client, MockIdentityProfiles.ActivePlatformAdminNoMfa, RevokePath(orderId), revokeKey);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        Assert.Equal(3, (await ReadAuditsAsync(orderId, PlatformAdminNoMfaId)).Count);
    }

    [Fact]
    public async Task A_viewer_is_403_and_another_organization_is_the_uniform_404_without_effects()
    {
        using var client = factory.CreateClient();
        var orderId = await CreateOrderAsync();
        using var issued = await PostAsync(client, MockIdentityProfiles.ActivePlatformAdminNoMfa, IssuePath(orderId), Key());
        var link = await ReadLinkAsync(issued);
        var before = await ReadTokenStateAsync(orderId);

        foreach (var path in new[] { IssuePath(orderId), RevokePath(orderId) })
        {
            using var viewer = await PostAsync(client, MockIdentityProfiles.ActiveViewer, path, Key());
            Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);

            // The multi-organization user is a DISPATCHER of the PLATFORM organization, which does not own the order.
            using var foreign = await PostAsync(
                client,
                MockIdentityProfiles.ActiveMultiOrganization,
                path,
                Key(),
                PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.DoesNotContain(link.Token, await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // The same 404 as an order that does not exist at all.
        using var missing = await PostAsync(
            client,
            MockIdentityProfiles.ActivePlatformAdminNoMfa,
            IssuePath(Guid.NewGuid()),
            Key());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var after = await ReadTokenStateAsync(orderId);
        Assert.Equal(before.Active, after.Active);
        Assert.Equal(before.Hashes.Count, after.Hashes.Count);
        Assert.Equal(1, after.Active);
        await AssertLookupAsync(client, link.Token, HttpStatusCode.OK);
        Assert.Single(await ReadAuditsAsync(orderId, PlatformAdminNoMfaId));
        Assert.Equal(0L, await CountForeignAuditsAsync(orderId));
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string profile,
        string path,
        string key,
        Guid? organizationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile);
        request.Headers.Add(
            "X-Organization-Id",
            (organizationId ?? PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId).ToString("D"));
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<(Guid OrderId, string Token)> ReadLinkAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("order_id").GetGuid(), json.RootElement.GetProperty("token").GetString()!);
    }

    private static async Task AssertLookupAsync(HttpClient client, string token, HttpStatusCode expected)
    {
        using var response = await client.GetAsync($"/api/v1/tracking/{token}");
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.NotFound)
        {
            Assert.Equal(UniformNotFound, await response.Content.ReadAsStringAsync());
        }
    }

    private async Task<Guid> CreateOrderAsync()
    {
        var orderId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var publicId = $"ORD_{Convert.ToHexString(RandomNumberGenerator.GetBytes(11))}";
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pricing.quotes
            SELECT (pg_catalog.jsonb_populate_record(
                NULL::pricing.quotes,
                pg_catalog.to_jsonb(source) ||
                pg_catalog.jsonb_build_object('id',@quote_id::text))).*
            FROM pricing.quotes source
            WHERE id='55555555-5555-5555-5555-555555555555';

            INSERT INTO orders.orders
            SELECT (pg_catalog.jsonb_populate_record(
                NULL::orders.orders,
                pg_catalog.to_jsonb(source) ||
                pg_catalog.jsonb_build_object(
                    'id',@order_id::text,
                    'quote_id',@quote_id::text,
                    'public_id',@public_id))).*
            FROM orders.orders source
            WHERE id='66666666-6666-6666-6666-666666666666';
            """,
            connection);
        command.Parameters.AddWithValue("quote_id", quoteId);
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("public_id", publicId);
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
        return orderId;
    }

    private async Task<(IReadOnlyList<byte[]> Hashes, int Active)> ReadTokenStateAsync(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT token_hash, revoked_at IS NULL AND expires_at>clock_timestamp()
            FROM orders.public_tracking_tokens
            WHERE order_id=@order_id
            ORDER BY created_at, id;
            """,
            connection);
        command.Parameters.AddWithValue("order_id", orderId);
        var hashes = new List<byte[]>();
        var active = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            hashes.Add(reader.GetFieldValue<byte[]>(0));
            active += reader.GetBoolean(1) ? 1 : 0;
        }

        return (hashes, active);
    }

    private async Task<List<(string Action, string RequestId)>> ReadAuditsAsync(Guid orderId, Guid actorId)
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT action, request_id
            FROM platform.audit_logs
            WHERE entity_id=@order_id
              AND org_id='11111111-1111-1111-1111-111111111111'
              AND actor_id=@actor_id
              AND action LIKE 'TRACKING_TOKEN_%'
            ORDER BY occurred_at;
            """,
            connection);
        command.Parameters.AddWithValue("order_id", orderId);
        command.Parameters.AddWithValue("actor_id", actorId);
        var audits = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            audits.Add((reader.GetString(0), reader.GetString(1)));
        }

        return audits;
    }

    private async Task<long> CountForeignAuditsAsync(Guid orderId)
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM platform.audit_logs
            WHERE entity_id=@order_id
              AND org_id<>'11111111-1111-1111-1111-111111111111';
            """,
            connection);
        command.Parameters.AddWithValue("order_id", orderId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Rows anywhere in the audit log or the token table whose text contains the plaintext.</summary>
    private async Task<long> CountPlaintextAsync(string token)
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM platform.audit_logs a WHERE pg_catalog.to_jsonb(a)::text LIKE '%'||@token||'%')
                 + (SELECT count(*) FROM orders.public_tracking_tokens t WHERE pg_catalog.to_jsonb(t)::text LIKE '%'||@token||'%')
                 + (SELECT count(*) FROM platform.outbox_events o WHERE pg_catalog.to_jsonb(o)::text LIKE '%'||@token||'%');
            """,
            connection);
        command.Parameters.AddWithValue("token", token);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string IssuePath(Guid orderId) => $"/api/v1/orders/{orderId:D}/tracking-link";

    private static string RevokePath(Guid orderId) => $"/api/v1/orders/{orderId:D}/tracking-link/revoke";

    private static string Key() => $"trk002-pg-{Guid.NewGuid():N}";

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> entries = new();

        internal IReadOnlyCollection<string> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            System.Collections.Concurrent.ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                entries.Enqueue($"{category} scope {state}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue($"{category} {logLevel} {formatter(state, exception)} {exception}");
        }
    }
}
