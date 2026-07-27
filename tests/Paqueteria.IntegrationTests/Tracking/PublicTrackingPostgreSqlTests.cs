using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task Lifecycle_is_tenant_safe_audited_and_plaintext_is_returned_once()
    {
        var order = await CreateOrderAsync();
        var grant = await WithServiceAsync(service => service.IssueAsync(
            new IssuePublicTrackingTokenCommand(
                ActorId,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                order.OrderId,
                "trk-issue"),
            default));

        Assert.Equal(43, grant.Token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", grant.Token);
        await AssertStoredSafelyAsync(order.OrderId, grant, "TRACKING_TOKEN_ISSUED");

        await Assert.ThrowsAsync<PublicTrackingTokenConflictException>(() =>
            WithServiceAsync(service => service.IssueAsync(
                new IssuePublicTrackingTokenCommand(
                    ActorId,
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                    order.OrderId,
                    "trk-duplicate"),
                default)));

        var rotated = await WithServiceAsync(service => service.RotateAsync(
            new RotatePublicTrackingTokenCommand(
                ActorId,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                order.OrderId,
                "trk-rotate"),
            default));
        await AssertLookupAsync(grant.Token, HttpStatusCode.NotFound);
        await AssertLookupAsync(rotated.Token, HttpStatusCode.OK);

        await WithServiceAsync(async service =>
        {
            await service.RevokeAsync(
                new RevokePublicTrackingTokenCommand(
                    ActorId,
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                    order.OrderId,
                    "trk-revoke"),
                default);
            return true;
        });
        await AssertLookupAsync(rotated.Token, HttpStatusCode.NotFound);

        var auditsAfterFirstRevoke = await CountAuditsAsync(order.OrderId);
        await WithServiceAsync(async service =>
        {
            await service.RevokeAsync(
                new RevokePublicTrackingTokenCommand(
                    ActorId,
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                    order.OrderId,
                    "trk-revoke-repeat"),
                default);
            return true;
        });
        Assert.Equal(auditsAfterFirstRevoke, await CountAuditsAsync(order.OrderId));

        await Assert.ThrowsAsync<PublicTrackingTokenNotFoundException>(() =>
            WithServiceAsync(service => service.RotateAsync(
                new RotatePublicTrackingTokenCommand(
                    ActorId,
                    PostgreSqlSecurityWebApplicationFactory.OperationsOrganizationId,
                    order.OrderId,
                    "trk-cross-tenant"),
                default)));
    }

    [Fact]
    public async Task Twenty_five_concurrent_rotations_leave_exactly_one_valid_grant()
    {
        var order = await CreateOrderAsync();
        await WithServiceAsync(service => service.IssueAsync(
            new IssuePublicTrackingTokenCommand(
                ActorId,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                order.OrderId,
                "trk-concurrency-seed"),
            default));

        var rotations = Enumerable.Range(0, 25)
            .Select(index => WithServiceAsync(service => service.RotateAsync(
                new RotatePublicTrackingTokenCommand(
                    ActorId,
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                    order.OrderId,
                    $"trk-concurrency-{index}"),
                default)))
            .ToArray();
        var grants = await Task.WhenAll(rotations);
        var finalGrant = await WithServiceAsync(service => service.RotateAsync(
            new RotatePublicTrackingTokenCommand(
                ActorId,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                order.OrderId,
                "trk-concurrency-final"),
            default));

        Assert.Equal(grants.Length, grants.Select(value => value.Token).Distinct().Count());
        var state = await ReadTokenStateAsync(order.OrderId);
        Assert.Equal(27, state.Total);
        Assert.Equal(1, state.Active);
        Assert.Equal(27, state.DistinctHashes);
        foreach (var superseded in grants)
        {
            await AssertLookupAsync(superseded.Token, HttpStatusCode.NotFound);
        }
        await AssertLookupAsync(finalGrant.Token, HttpStatusCode.OK);
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
    public async Task Token_hash_collisions_retry_three_times_and_fail_closed_without_partial_rows()
    {
        var unique = new TrackingTokenHasher().CreateToken();
        var candidates = new SequenceTrackingTokenHasher(
            PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken,
            unique,
            PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken,
            PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken,
            PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken);
        var collisionFactory =
            new PostgreSqlSecurityWebApplicationFactory(candidates);
        await collisionFactory.InitializeAsync();
        try
        {
            var recoveredOrder = await CreateOrderAsync(collisionFactory);
            var recovered = await WithServiceAsync(
                collisionFactory,
                service => service.IssueAsync(
                    new IssuePublicTrackingTokenCommand(
                        ActorId,
                        PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                        recoveredOrder.OrderId,
                        "trk-collision-recovered"),
                    default));
            Assert.Equal(unique, recovered.Token);

            var failedOrder = await CreateOrderAsync(collisionFactory);
            await Assert.ThrowsAsync<PublicTrackingTokenInfrastructureException>(() =>
                WithServiceAsync(
                    collisionFactory,
                    service => service.IssueAsync(
                        new IssuePublicTrackingTokenCommand(
                            ActorId,
                            PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                            failedOrder.OrderId,
                            "trk-collision-exhausted"),
                        default)));
            var failedState = await ReadTokenStateAsync(
                collisionFactory,
                failedOrder.OrderId);
            Assert.Equal((0, 0, 0), failedState);
            Assert.Equal(0, await CountAuditsAsync(
                collisionFactory,
                failedOrder.OrderId));
        }
        finally
        {
            await collisionFactory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Expiration_stops_authoritative_lookup_without_sliding_renewal()
    {
        var order = await CreateOrderAsync();
        var before = DateTimeOffset.UtcNow;
        var grant = await WithServiceAsync(service => service.IssueAsync(
            new IssuePublicTrackingTokenCommand(
                ActorId,
                PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                order.OrderId,
                "trk-expiration"),
            default));
        Assert.InRange(
            grant.ExpiresAt,
            before.AddHours(168),
            DateTimeOffset.UtcNow.AddHours(168).AddSeconds(1));
        await AssertLookupAsync(grant.Token, HttpStatusCode.OK);

        await ExpireTokenAsync(grant.Token);
        await AssertLookupAsync(grant.Token, HttpStatusCode.NotFound);
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

    private async Task<T> WithServiceAsync<T>(
        Func<IPublicTrackingTokenService, Task<T>> operation)
        => await WithServiceAsync(factory, operation);

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

    private sealed class SequenceTrackingTokenHasher(params string[] candidates)
        : TrackingTokenHasher
    {
        private readonly Queue<string> _candidates = new(candidates);

        public override string CreateToken() =>
            _candidates.Count > 0
                ? _candidates.Dequeue()
                : PostgreSqlSecurityWebApplicationFactory.ValidTrackingToken;
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
