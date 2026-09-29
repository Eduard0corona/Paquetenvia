using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Orders.Application.Tracking;
using Paqueteria.Application.Security;
using Paqueteria.IntegrationTests.Security;
using Paqueteria.SyntheticVerification;

namespace Paqueteria.IntegrationTests.Tracking;

[Collection(PublicTrackingPostgreSqlCollection.Name)]
[Trait("Category", "PublicTrackingPostgreSql")]
public sealed class SyntheticTrackingVerificationPostgreSqlTests(
    PostgreSqlSecurityWebApplicationFactory factory)
{
    private static readonly Guid ActorId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");

    [Fact]
    public async Task Stable_link_revoke_and_next_generation_use_productive_tenant_audit_and_anonymous_projection_paths()
    {
        using var environment = new ProcessEnvironmentVariable("DOTNET_ENVIRONMENT", "DevSynthetic");
        using var deployment = new ProcessEnvironmentVariable(
            SyntheticEnvironmentPolicy.DeploymentClassVariable,
            SyntheticEnvironmentPolicy.DeploymentClass);
        using var optIn = new ProcessEnvironmentVariable(
            SyntheticTrackingVerifier.TrackingOptInVariable,
            "true");
        var order = await CreateOrderAsync();
        SyntheticTrackingVerificationResult result;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var tokenService = scope.ServiceProvider.GetRequiredService<IPublicTrackingTokenService>();
            var projectionReader = scope.ServiceProvider.GetRequiredService<IPublicTrackingProjectionReader>();
            Assert.Equal(
                "Orders.Infrastructure.Tracking.PostgreSqlPublicTrackingTokenService",
                tokenService.GetType().FullName);
            Assert.Equal(
                "Orders.Infrastructure.Tracking.PostgreSqlPublicTrackingProjectionReader",
                projectionReader.GetType().FullName);
            result = await new SyntheticTrackingVerifier(tokenService, projectionReader)
                .VerifyAsync(new SyntheticTrackingVerificationRequest(
                    ActorId,
                    PostgreSqlSecurityWebApplicationFactory.ViewerOrganizationId,
                    order.OrderId,
                    "sec003-postgresql-run"));
        }

        Assert.Equal(order.PublicId, result.PublicId);
        Assert.True(result.FirstLinkFound);
        Assert.True(result.RepeatReturnedSameLink);
        Assert.True(result.FirstLinkInvalidAfterRevoke);
        Assert.True(result.NextLinkValid);
        Assert.Equal(1, result.FirstGeneration);
        Assert.Equal(2, result.NextGeneration);

        await using var connection = new NpgsqlConnection(factory.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
              (SELECT count(*)::integer
                 FROM orders.public_tracking_tokens
                WHERE order_id=@order_id),
              (SELECT count(*)::integer
                 FROM orders.public_tracking_tokens
                WHERE order_id=@order_id
                  AND revoked_at IS NULL
                  AND expires_at>clock_timestamp()),
              (SELECT count(DISTINCT token_hash)::integer
                 FROM orders.public_tracking_tokens
                WHERE order_id=@order_id),
              (SELECT count(*)::integer
                 FROM platform.audit_logs
                WHERE entity_id=@order_id
                  AND ((action='TRACKING_TOKEN_ISSUED' AND request_id IN (@first,@next))
                    OR (action='TRACKING_TOKEN_REVOKED' AND request_id=@revoke))),
              (SELECT count(*)::integer
                 FROM platform.audit_logs
                WHERE entity_id=@order_id
                  AND request_id=@repeat),
              (SELECT NOT rolbypassrls
                 FROM pg_catalog.pg_roles
                WHERE rolname='paqueteria_sec002_api'),
              pg_catalog.pg_has_role('paqueteria_sec002_api','paqueteria_app','member');
            """,
            connection);
        command.Parameters.AddWithValue("order_id", order.OrderId);
        command.Parameters.AddWithValue("first", result.FirstRequestId);
        command.Parameters.AddWithValue("repeat", result.RepeatRequestId);
        command.Parameters.AddWithValue("revoke", result.RevokeRequestId);
        command.Parameters.AddWithValue("next", result.NextRequestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(3, reader.GetInt32(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
    }

    private async Task<(Guid OrderId, string PublicId)> CreateOrderAsync()
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
        return (orderId, publicId);
    }

    private sealed class ProcessEnvironmentVariable : IDisposable
    {
        private readonly string name;
        private readonly string? originalValue;

        public ProcessEnvironmentVariable(string name, string? value)
        {
            this.name = name;
            originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(name, originalValue);
    }
}
