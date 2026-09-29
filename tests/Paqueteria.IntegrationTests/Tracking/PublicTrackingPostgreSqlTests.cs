using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Orders.Application.Tracking;
using Paqueteria.Contracts.Tracking;
using Paqueteria.IntegrationTests.Security;

namespace Paqueteria.IntegrationTests.Tracking;

[Collection(PublicTrackingPostgreSqlCollection.Name)]
[Trait("Category", "PublicTrackingPostgreSql")]
public sealed class PublicTrackingPostgreSqlTests(
    PostgreSqlSecurityWebApplicationFactory factory)
{
    private static readonly Guid ActorId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");

    /// <summary>
    /// REL-000 required evidence for TRK-001 (item-evidence), kept under its original name and adapted to
    /// TRK-002-AUTO-LINK: the lifecycle is tenant-safe (another organization gets the uniform not-found and RLS hides
    /// the rows), every state change is audited (issue and revocation, never a read), and the plaintext token is
    /// never persisted or logged: it is only ever returned in the no-store get-or-create response, re-derived for
    /// the same generation on every call.
    /// </summary>
    [Fact]
    public async Task Lifecycle_is_tenant_safe_audited_and_plaintext_is_returned_once()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
        var order = await CreateOrderAsync();
        var grant = await WithServiceAsync(host, service => service.GetOrCreateAsync(
            new GetOrCreatePublicTrackingLinkCommand(
                ActorId,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                order.OrderId,
                "trk-issue"),
            default));

        Assert.Equal(43, grant.Token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", grant.Token);
        Assert.Equal(1, grant.Generation);
        Assert.Equal(
            TrackingLinkTokenDerivation.DeriveToken(
                PostgreSqlSecurityWebApplicationFactory.TestTrackingLinkKey, 1, order.OrderId, 1),
            grant.Token);
        Assert.DoesNotContain(grant.Token, grant.ToString(), StringComparison.Ordinal);
        await AssertStoredSafelyAsync(order.OrderId, grant, "TRACKING_TOKEN_ISSUED");
        await AssertLookupAsync(grant.Token, HttpStatusCode.OK);

        // The only place the plaintext leaves the service: the no-store body of the authenticated get-or-create.
        using (var client = host.CreateClient())
        using (var request = new HttpRequestMessage(
                   HttpMethod.Post,
                   $"/api/v1/orders/{order.OrderId:D}/tracking-link"))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                Identity.Infrastructure.Mock.MockIdentityProfiles.ActivePlatformAdminMfa);
            request.Headers.Add(
                "X-Organization-Id",
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId.ToString("D"));
            request.Headers.Add("Idempotency-Key", $"trk001-http-read-{Guid.NewGuid():N}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(grant.Token, json.RootElement.GetProperty("token").GetString());
        }

        // Same link for any retry or key: nothing is written again, so reads are never audited.
        var audits = await CountAuditsAsync(order.OrderId);
        foreach (var key in new[] { "trk-issue", "trk-another-key" })
        {
            Assert.Equal(grant.Token, (await GetOrCreateAsync(order.OrderId, key)).Token);
        }

        Assert.Equal(audits, await CountAuditsAsync(order.OrderId));
        Assert.Equal((1, 1, 1), await ReadTokenStateAsync(order.OrderId));

        await RevokeAsync(order.OrderId, "trk-revoke");
        await AssertLookupAsync(grant.Token, HttpStatusCode.NotFound);
        var auditsAfterFirstRevoke = await CountAuditsAsync(order.OrderId);
        Assert.Equal(audits + 1, auditsAfterFirstRevoke);
        await RevokeAsync(order.OrderId, "trk-revoke-repeat");
        Assert.Equal(auditsAfterFirstRevoke, await CountAuditsAsync(order.OrderId));

        var next = await GetOrCreateAsync(order.OrderId, "trk-next");
        Assert.Equal(2, next.Generation);
        Assert.NotEqual(grant.Token, next.Token);
        await AssertStoredSafelyAsync(order.OrderId, next, "TRACKING_TOKEN_ISSUED");
        await AssertLookupAsync(grant.Token, HttpStatusCode.NotFound);
        await AssertLookupAsync(next.Token, HttpStatusCode.OK);

        // Tenant-safe: another organization gets the uniform not-found and changes nothing.
        foreach (var operation in new Func<IPublicTrackingTokenService, Task>[]
                 {
                     service => service.GetOrCreateAsync(
                         new GetOrCreatePublicTrackingLinkCommand(
                             ActorId,
                             PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
                             order.OrderId,
                             "trk-cross-tenant"),
                         default),
                     service => service.RevokeAsync(
                         new RevokePublicTrackingTokenCommand(
                             ActorId,
                             PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
                             order.OrderId,
                             "trk-cross-tenant-revoke"),
                         default),
                 })
        {
            await Assert.ThrowsAsync<PublicTrackingTokenNotFoundException>(() =>
                WithServiceAsync(async service =>
                {
                    await operation(service);
                    return true;
                }));
        }

        Assert.Equal(auditsAfterFirstRevoke + 1, await CountAuditsAsync(order.OrderId));
        await AssertLookupAsync(next.Token, HttpStatusCode.OK);

        // Never persisted anywhere readable and never logged by the host that served the response.
        foreach (var token in new[] { grant.Token, next.Token })
        {
            Assert.Equal(0L, await CountPlaintextAsync(token));
            Assert.DoesNotContain(logs.Entries, entry => entry.Contains(token, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Twenty_five_concurrent_get_or_create_calls_create_one_link()
    {
        var order = await CreateOrderAsync();
        var grants = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(index => GetOrCreateAsync(order.OrderId, $"trk-concurrency-{index}")));

        Assert.Single(grants.Select(value => value.Token).Distinct(StringComparer.Ordinal));
        Assert.Single(grants.Select(value => value.TokenId).Distinct());
        Assert.Equal((1, 1, 1), await ReadTokenStateAsync(order.OrderId));
        Assert.Equal(1, await CountAuditsAsync(order.OrderId));
        await AssertLookupAsync(grants[0].Token, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Product_endpoint_has_exact_contract_uniform_privacy_and_no_enumeration()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var success = await client.GetAsync(
            $"/api/v1/tracking/{PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken}");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        AssertPrivacyHeaders(success);
        var json = await success.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(json);
        Assert.Equal(
            ["aggregate_version", "estimated_window", "public_id", "public_status", "timeline"],
            json.Keys.Order(StringComparer.Ordinal).ToArray());

        var mutations = new[]
        {
            PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken + "=",
            PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken.ToLowerInvariant(),
            "short",
            "unknown_unknown_unknown_unknown_unknown_123456",
            PostgreSqlSecurityWebApplicationFactory.ExpiredTrackingToken,
            PostgreSqlSecurityWebApplicationFactory.RevokedTrackingToken,
        };
        foreach (var token in mutations)
        {
            using var response = await client.GetAsync($"/api/v1/tracking/{token}");
            await AssertUniformNotFoundAsync(response);
        }

        for (var index = 0; index < 100; index++)
        {
            var random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            using var response = await client.GetAsync($"/api/v1/tracking/{random}");
            await AssertUniformNotFoundAsync(response);
        }

        using var allowedPreflight = new HttpRequestMessage(
            HttpMethod.Options,
            "/api/v1/tracking/canonical");
        allowedPreflight.Headers.Add("Origin", "https://tracking.synthetic.local");
        allowedPreflight.Headers.Add("Access-Control-Request-Method", "GET");
        allowedPreflight.Headers.Add("Access-Control-Request-Headers", "Accept");
        using var allowedResponse = await client.SendAsync(allowedPreflight);
        Assert.Equal(HttpStatusCode.NoContent, allowedResponse.StatusCode);
        Assert.Equal(
            "https://tracking.synthetic.local",
            allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(allowedResponse.Headers.Contains("Access-Control-Allow-Credentials"));

        using var deniedPreflight = new HttpRequestMessage(
            HttpMethod.Options,
            "/api/v1/tracking/canonical");
        deniedPreflight.Headers.Add("Origin", "https://untrusted.synthetic.local");
        deniedPreflight.Headers.Add("Access-Control-Request-Method", "GET");
        using var deniedResponse = await client.SendAsync(deniedPreflight);
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_token_hash_collision_fails_closed_without_partial_rows()
    {
        // Another order already holds the hash the next link of this order would get: the insert is refused by the
        // unique token_hash, and nothing of this order (row or audit) is left behind.
        var order = await CreateOrderAsync();
        var colliding = TrackingLinkTokenDerivation.DeriveToken(
            PostgreSqlSecurityWebApplicationFactory.TestTrackingLinkKey, 1, order.OrderId, 1);
        await using (var connection = new NpgsqlConnection(factory.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO orders.public_tracking_tokens(id,order_id,owner_org_id,token_hash,expires_at,revoked_at)
                VALUES (gen_random_uuid(),'66666666-6666-6666-6666-666666666666','11111111-1111-1111-1111-111111111111',
                  extensions.digest(pg_catalog.convert_to(@token,'UTF8'),'sha256'),clock_timestamp()+interval '1 day',clock_timestamp());
                """,
                connection);
            command.Parameters.AddWithValue("token", colliding);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<PublicTrackingTokenInfrastructureException>(() =>
            GetOrCreateAsync(order.OrderId, "trk-collision"));
        Assert.Equal((0, 0, 0), await ReadTokenStateAsync(order.OrderId));
        Assert.Equal(0, await CountAuditsAsync(order.OrderId));
    }

    [Fact]
    public async Task A_finished_order_link_resolves_for_24_hours_then_is_the_uniform_404()
    {
        var order = await CreateOrderAsync();
        var grant = await GetOrCreateAsync(order.OrderId, "trk-final");
        await AssertLookupAsync(grant.Token, HttpStatusCode.OK);

        await FinishAsync(order.OrderId, "23 hours");
        await AssertLookupAsync(grant.Token, HttpStatusCode.OK);
        var withinGrace = await GetOrCreateAsync(order.OrderId, "trk-final-read");
        Assert.Equal(grant.Token, withinGrace.Token);
        Assert.NotNull(withinGrace.ValidUntil);

        // Order events are append-only: past the grace is another order delivered 25 hours ago.
        var expiredOrder = await CreateOrderAsync();
        var expired = await GetOrCreateAsync(expiredOrder.OrderId, "trk-final-expired");
        await FinishAsync(expiredOrder.OrderId, "25 hours");
        await AssertLookupAsync(expired.Token, HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<PublicTrackingLinkOrderFinishedException>(() =>
            GetOrCreateAsync(expiredOrder.OrderId, "trk-final-after-grace"));
    }

    [Fact]
    public async Task A_pre_derivation_token_keeps_its_fixed_expiry_without_sliding_renewal()
    {
        await AssertLookupAsync(PostgreSqlSecurityWebApplicationFactory.ExpiredTrackingToken, HttpStatusCode.NotFound);
        var order = await CreateOrderAsync();
        var legacy = new TrackingTokenHasher().CreateToken();
        await using (var connection = new NpgsqlConnection(factory.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO orders.public_tracking_tokens(id,order_id,owner_org_id,token_hash,expires_at)
                VALUES (gen_random_uuid(),@order_id,'11111111-1111-1111-1111-111111111111',
                  extensions.digest(pg_catalog.convert_to(@token,'UTF8'),'sha256'),clock_timestamp()+interval '1 day');
                """,
                connection);
            command.Parameters.AddWithValue("order_id", order.OrderId);
            command.Parameters.AddWithValue("token", legacy);
            await command.ExecuteNonQueryAsync();
        }

        await AssertLookupAsync(legacy, HttpStatusCode.OK);
        await ExpireTokenAsync(legacy);
        await AssertLookupAsync(legacy, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rate_limit_rejection_is_generic_and_keeps_privacy_headers()
    {
        using var limited = new PublicTrackingRateLimitedWebApplicationFactory();
        using var client = limited.CreateClient();
        using var first = await client.GetAsync("/api/v1/tracking/first");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

        using var second = await client.GetAsync("/api/v1/tracking/second");
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        AssertPrivacyHeaders(second);
        Assert.Equal(
            """{"type":"about:blank","title":"Too Many Requests","status":429}""",
            await second.Content.ReadAsStringAsync());
    }

    private Task<PublicTrackingTokenGrant> GetOrCreateAsync(Guid orderId, string requestId) =>
        WithServiceAsync(service => service.GetOrCreateAsync(
            new GetOrCreatePublicTrackingLinkCommand(
                ActorId,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                orderId,
                requestId),
            default));

    private Task RevokeAsync(Guid orderId, string requestId) =>
        WithServiceAsync(async service =>
        {
            await service.RevokeAsync(
                new RevokePublicTrackingTokenCommand(
                    ActorId,
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                    orderId,
                    requestId),
                default);
            return true;
        });

    /// <summary>The DELIVERED transition as the productive path writes it: status and its public event.</summary>
    private async Task FinishAsync(Guid orderId, string ago)
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE orders.orders SET status='DELIVERED' WHERE id=@order_id;
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at)
            VALUES (gen_random_uuid(),@order_id,'11111111-1111-1111-1111-111111111111',
              (SELECT COALESCE(max(aggregate_version),0)+1 FROM orders.order_events WHERE order_id=@order_id),
              'ORDER_STATUS_CHANGED','DELIVERED',jsonb_build_object('new_status','DELIVERED'),clock_timestamp()-interval '{ago}');
            """,
            connection);
        command.Parameters.AddWithValue("order_id", orderId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> WithServiceAsync<T>(
        Func<IPublicTrackingTokenService, Task<T>> operation)
        => await WithServiceAsync(factory, operation);

    private static async Task<T> WithServiceAsync<T>(
        WebApplicationFactory<Program> host,
        Func<IPublicTrackingTokenService, Task<T>> operation)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<IPublicTrackingTokenService>());
    }

    /// <summary>Rows anywhere in the audit log, the token table or the outbox whose text contains the plaintext.</summary>
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

    private static async Task<T> WithServiceAsync<T>(
        PostgreSqlSecurityWebApplicationFactory targetFactory,
        Func<IPublicTrackingTokenService, Task<T>> operation)
    {
        await using var scope = targetFactory.Services.CreateAsyncScope();
        return await operation(
            scope.ServiceProvider.GetRequiredService<IPublicTrackingTokenService>());
    }

    private async Task<(Guid OrderId, string PublicId)> CreateOrderAsync()
        => await CreateOrderAsync(factory);

    private static async Task<(Guid OrderId, string PublicId)> CreateOrderAsync(
        PostgreSqlSecurityWebApplicationFactory targetFactory)
    {
        var orderId = Guid.NewGuid();
        var quoteId = Guid.NewGuid();
        var publicId = $"ORD_{Convert.ToHexString(RandomNumberGenerator.GetBytes(11))}";
        await using var connection = new NpgsqlConnection(targetFactory.AdminConnectionString);
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
        return (orderId, publicId);
    }

    private async Task AssertStoredSafelyAsync(
        Guid orderId,
        PublicTrackingTokenGrant grant,
        string action)
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT octet_length(token_hash),
                   encode(token_hash,'hex'),
                   payload_redacted::text
            FROM orders.public_tracking_tokens token
            JOIN platform.audit_logs audit
              ON audit.entity_id=token.order_id
             AND audit.action=@action
            WHERE token.id=@token_id
              AND token.order_id=@order_id;
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("token_id", grant.TokenId);
        command.Parameters.AddWithValue("order_id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(32, reader.GetInt32(0));
        Assert.DoesNotContain(grant.Token, reader.GetString(2), StringComparison.Ordinal);
        Assert.DoesNotContain(reader.GetString(1), reader.GetString(2), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(int Total, int Active, int DistinctHashes)> ReadTokenStateAsync(
        Guid orderId)
        => await ReadTokenStateAsync(factory, orderId);

    private static async Task<(int Total, int Active, int DistinctHashes)> ReadTokenStateAsync(
        PostgreSqlSecurityWebApplicationFactory targetFactory,
        Guid orderId)
    {
        await using var connection = new NpgsqlConnection(targetFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::integer,
                   count(*) FILTER (
                       WHERE revoked_at IS NULL AND expires_at>clock_timestamp())::integer,
                   count(DISTINCT token_hash)::integer
            FROM orders.public_tracking_tokens
            WHERE order_id=@order_id;
            """,
            connection);
        command.Parameters.AddWithValue("order_id", orderId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private async Task<int> CountAuditsAsync(Guid orderId)
        => await CountAuditsAsync(factory, orderId);

    private static async Task<int> CountAuditsAsync(
        PostgreSqlSecurityWebApplicationFactory targetFactory,
        Guid orderId)
    {
        await using var connection = new NpgsqlConnection(targetFactory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::integer
            FROM platform.audit_logs
            WHERE entity_id=@order_id
              AND action LIKE 'TRACKING_TOKEN_%';
            """,
            connection);
        command.Parameters.AddWithValue("order_id", orderId);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private async Task AssertLookupAsync(string token, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/tracking/{token}");
        Assert.Equal(expected, response.StatusCode);
    }

    private async Task ExpireTokenAsync(string token)
    {
        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE orders.public_tracking_tokens
            SET expires_at=clock_timestamp()-interval '1 second'
            WHERE token_hash=extensions.digest(
                pg_catalog.convert_to(@token,'UTF8'),
                'sha256');
            """,
            connection);
        command.Parameters.AddWithValue("token", token);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task AssertUniformNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertPrivacyHeaders(response);
        Assert.Equal(
            """{"type":"about:blank","title":"Not Found","status":404}""",
            await response.Content.ReadAsStringAsync());
    }

    private static void AssertPrivacyHeaders(HttpResponseMessage response)
    {
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(
            "noindex, nofollow, noarchive",
            response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PublicTrackingPostgreSqlCollection
    : ICollectionFixture<PostgreSqlSecurityWebApplicationFactory>
{
    public const string Name = "PublicTrackingPostgreSql";
}

internal sealed class PublicTrackingRateLimitedWebApplicationFactory
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Provider"] = "Disabled",
                ["IdentityBootstrap:Provider"] = "Disabled",
                ["PublicTracking:Provider"] = "Disabled",
                ["PublicTracking:LookupPermitLimit"] = "1",
                ["PublicTracking:LookupWindowSeconds"] = "300",
                ["Tenancy:Provider"] = "Disabled",
                ["Realtime:Provider"] = "Disabled",
            }));
    }
}
