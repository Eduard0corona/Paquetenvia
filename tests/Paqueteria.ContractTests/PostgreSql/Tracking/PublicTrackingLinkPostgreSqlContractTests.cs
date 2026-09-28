using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Orders.Application.Tracking;
using Orders.Infrastructure.Persistence;
using Orders.Infrastructure.Tracking;
using Paqueteria.Application.Auditing;
using Paqueteria.Contracts.Tracking;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql.Tracking;

/// <summary>
/// TRK-002-ISSUE-ENDPOINT against the real schema and the least-privilege runtime role (NOBYPASSRLS): the service
/// behind issueTrackingLink and revokeTrackingLink stores only the SHA-256 of the token, hands the plaintext out
/// exactly once, audits every issue, rotation and revocation, makes a revoked link the uniform not-found of the
/// public lookup and refuses every other organization without touching its rows.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class PublicTrackingLinkPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Token_is_issued_exactly_once_audited_and_revocation_is_the_uniform_public_not_found()
    {
        await using var scenario = new SyntheticOrderScenario(fixture);
        await scenario.InitializeAsync(orderStatus: "DELIVERING");

        var issued = await WithServiceAsync(service => service.IssueAsync(
            new IssuePublicTrackingTokenCommand(
                scenario.UserId, scenario.OrganizationId, scenario.OrderId, "trk002-contract-issue-0001"),
            CancellationToken.None));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", issued.Token);
        Assert.DoesNotContain(issued.Token, issued.ToString(), StringComparison.Ordinal);

        // Only the hash is stored: one row whose hash is SHA-256 over the exact UTF-8 bytes of the token, and no
        // column of the token row or of any audit row of the order carries the plaintext.
        var stored = await ReadTokenRowsAsync(scenario.OrderId);
        var row = Assert.Single(stored);
        Assert.Equal(issued.TokenId, row.Id);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Token)), row.Hash);
        Assert.DoesNotContain(issued.Token, row.Json, StringComparison.Ordinal);
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(issued.Token));

        // Exactly once: a second plain issue is refused inside its own transaction and writes nothing.
        await Assert.ThrowsAsync<PublicTrackingTokenConflictException>(() => WithServiceAsync(service =>
            service.IssueAsync(
                new IssuePublicTrackingTokenCommand(
                    scenario.UserId, scenario.OrganizationId, scenario.OrderId, "trk002-contract-issue-0002"),
                CancellationToken.None)));
        Assert.Single(await ReadTokenRowsAsync(scenario.OrderId));

        var rotated = await WithServiceAsync(service => service.RotateAsync(
            new RotatePublicTrackingTokenCommand(
                scenario.UserId, scenario.OrganizationId, scenario.OrderId, "trk002-contract-rotate-0001"),
            CancellationToken.None));
        Assert.NotEqual(issued.Token, rotated.Token);
        Assert.Null(await ReadProjectionPublicIdKeyAsync(issued.Token));
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(rotated.Token));

        await WithServiceAsync(async service =>
        {
            await service.RevokeAsync(
                new RevokePublicTrackingTokenCommand(
                    scenario.UserId, scenario.OrganizationId, scenario.OrderId, "trk002-contract-revoke-0001"),
                CancellationToken.None);
            return true;
        });
        Assert.Null(await ReadProjectionPublicIdKeyAsync(rotated.Token));
        Assert.All(await ReadTokenRowsAsync(scenario.OrderId), token => Assert.True(token.Revoked));

        // A replayed revocation is harmless: nothing is left to revoke and no audit row is added.
        await WithServiceAsync(async service =>
        {
            await service.RevokeAsync(
                new RevokePublicTrackingTokenCommand(
                    scenario.UserId, scenario.OrganizationId, scenario.OrderId, "trk002-contract-revoke-0001"),
                CancellationToken.None);
            return true;
        });

        var audits = await ReadAuditsAsync(scenario.OrderId);
        Assert.Equal(
            ["TRACKING_TOKEN_ISSUED", "TRACKING_TOKEN_ROTATED", "TRACKING_TOKEN_REVOKED"],
            audits.Select(audit => audit.Action));
        Assert.All(audits, audit =>
        {
            Assert.Equal(scenario.OrganizationId, audit.OrganizationId);
            Assert.Equal(scenario.UserId, audit.ActorId);
            Assert.Equal("PublicTrackingToken", audit.EntityType);
            Assert.DoesNotContain(issued.Token, audit.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(rotated.Token, audit.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rotated.Token))),
                audit.Payload,
                StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(
            ["trk002-contract-issue-0001", "trk002-contract-rotate-0001", "trk002-contract-revoke-0001"],
            audits.Select(audit => audit.RequestId));
        AssertAuditTokenId(issued.TokenId, audits[0].Payload);
        AssertAuditTokenId(rotated.TokenId, audits[1].Payload);
    }

    [PostgreSqlContractFact]
    public async Task Another_organization_can_neither_see_issue_rotate_nor_revoke_the_link()
    {
        await using var owner = new SyntheticOrderScenario(fixture);
        await owner.InitializeAsync(orderStatus: "DELIVERING");
        await using var foreign = new SyntheticOrderScenario(fixture);
        await foreign.InitializeAsync(orderStatus: "DELIVERING");

        var grant = await WithServiceAsync(service => service.IssueAsync(
            new IssuePublicTrackingTokenCommand(
                owner.UserId, owner.OrganizationId, owner.OrderId, "trk002-contract-owner-0001"),
            CancellationToken.None));
        var before = await ReadTokenRowsAsync(owner.OrderId);

        await Assert.ThrowsAsync<PublicTrackingTokenNotFoundException>(() => WithServiceAsync(service =>
            service.IssueAsync(
                new IssuePublicTrackingTokenCommand(
                    foreign.UserId, foreign.OrganizationId, owner.OrderId, "trk002-contract-foreign-0001"),
                CancellationToken.None)));
        await Assert.ThrowsAsync<PublicTrackingTokenNotFoundException>(() => WithServiceAsync(service =>
            service.RotateAsync(
                new RotatePublicTrackingTokenCommand(
                    foreign.UserId, foreign.OrganizationId, owner.OrderId, "trk002-contract-foreign-0002"),
                CancellationToken.None)));
        await Assert.ThrowsAsync<PublicTrackingTokenNotFoundException>(() => WithServiceAsync(async service =>
        {
            await service.RevokeAsync(
                new RevokePublicTrackingTokenCommand(
                    foreign.UserId, foreign.OrganizationId, owner.OrderId, "trk002-contract-foreign-0003"),
                CancellationToken.None);
            return true;
        }));

        // Nothing changed for the owner, the link still resolves, the foreign organization wrote no audit and RLS
        // hides the owner's token rows from the foreign tenant context entirely.
        Assert.Equal(before, await ReadTokenRowsAsync(owner.OrderId));
        Assert.Equal("public_id", await ReadProjectionPublicIdKeyAsync(grant.Token));
        Assert.Single(await ReadAuditsAsync(owner.OrderId));
        Assert.Equal(0L, await CountAuditsForOrganizationAsync(foreign.OrganizationId));
        Assert.Equal(0L, await CountTokenRowsAsTenantAsync(foreign.UserId, foreign.OrganizationId, owner.OrderId));
        Assert.Equal(1L, await CountTokenRowsAsTenantAsync(owner.UserId, owner.OrganizationId, owner.OrderId));
    }

    /// <summary>
    /// The audit names the token row it created. The audit redactor keeps well-formed UUIDs (AUD-001), so the
    /// stored value is exactly the id.
    /// </summary>
    private static void AssertAuditTokenId(Guid tokenId, string payload)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal(
            tokenId.ToString("D"),
            document.RootElement.GetProperty("token_id").GetString(),
            ignoreCase: true);
    }

    private async Task<T> WithServiceAsync<T>(Func<IPublicTrackingTokenService, Task<T>> operation)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(fixture.AppDataSource)
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        await using var context = new OrdersDbContext(options, state);
        var service = new PostgreSqlPublicTrackingTokenService(
            new TenantTransactionContext<OrdersDbContext>(context, state),
            new TrackingTokenHasher(),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            Options.Create(new PublicTrackingOptions
            {
                Provider = PublicTrackingProviderKind.PostgreSql,
                CommandTimeoutSeconds = 30,
            }),
            new SystemClock());
        return await operation(service);
    }

    private async Task<IReadOnlyList<TokenRow>> ReadTokenRowsAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT id, token_hash, revoked_at IS NOT NULL, pg_catalog.to_jsonb(t)::text
            FROM orders.public_tracking_tokens t
            WHERE order_id=@order
            ORDER BY created_at, id;
            """);
        command.Parameters.AddWithValue("order", orderId);
        var rows = new List<TokenRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new TokenRow(
                reader.GetGuid(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetBoolean(2),
                reader.GetString(3)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<AuditRow>> ReadAuditsAsync(Guid orderId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT action, org_id, actor_id, entity_type, request_id, payload_redacted::text
            FROM platform.audit_logs
            WHERE entity_id=@order
              AND action LIKE 'TRACKING_TOKEN_%'
            ORDER BY occurred_at, action DESC;
            """);
        command.Parameters.AddWithValue("order", orderId);
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private async Task<long> CountAuditsForOrganizationAsync(Guid organizationId)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT count(*) FROM platform.audit_logs WHERE org_id=@org AND action LIKE 'TRACKING_TOKEN_%';");
        command.Parameters.AddWithValue("org", organizationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountTokenRowsAsTenantAsync(Guid actorId, Guid organizationId, Guid orderId)
    {
        await using var tenant = await TenantTransaction.BeginAsync(
            fixture.AppDataSource,
            "paqueteria_app",
            actorId,
            [organizationId]);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM orders.public_tracking_tokens WHERE order_id=@order;",
            tenant.Connection,
            tenant.Transaction);
        command.Parameters.AddWithValue("order", orderId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// What publicTracking reads: the SQL projection as the runtime role. Returns the first key of the projection
    /// (<c>public_id</c>) or null for the uniform not-found.
    /// </summary>
    private async Task<string?> ReadProjectionPublicIdKeyAsync(string token)
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_app", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT security.get_public_tracking_projection(@token)::text",
            connection,
            transaction);
        command.Parameters.AddWithValue("token", token);
        var result = await command.ExecuteScalarAsync();
        if (result is null or DBNull)
        {
            return null;
        }

        using var document = System.Text.Json.JsonDocument.Parse((string)result);
        return document.RootElement.TryGetProperty("public_id", out _) ? "public_id" : "unexpected";
    }

    private sealed record TokenRow(Guid Id, byte[] Hash, bool Revoked, string Json)
    {
        public bool Equals(TokenRow? other) =>
            other is not null &&
            Id == other.Id &&
            Hash.AsSpan().SequenceEqual(other.Hash) &&
            Revoked == other.Revoked &&
            Json == other.Json;

        public override int GetHashCode() => HashCode.Combine(Id, Revoked, Json);
    }

    private sealed record AuditRow(
        string Action,
        Guid OrganizationId,
        Guid ActorId,
        string EntityType,
        string RequestId,
        string Payload);
}
