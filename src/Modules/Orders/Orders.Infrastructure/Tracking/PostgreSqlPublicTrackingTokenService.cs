using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Tracking;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Contracts.Tracking;
using Paqueteria.Infrastructure.Tenancy;

namespace Orders.Infrastructure.Tracking;

public sealed class DisabledPublicTrackingTokenService : IPublicTrackingTokenService
{
    public Task<PublicTrackingTokenGrant> IssueAsync(
        IssuePublicTrackingTokenCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException<PublicTrackingTokenGrant>(
            new PublicTrackingTokenInfrastructureException("Public tracking is unavailable."));

    public Task<PublicTrackingTokenGrant> RotateAsync(
        RotatePublicTrackingTokenCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException<PublicTrackingTokenGrant>(
            new PublicTrackingTokenInfrastructureException("Public tracking is unavailable."));

    public Task RevokeAsync(
        RevokePublicTrackingTokenCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException(
            new PublicTrackingTokenInfrastructureException("Public tracking is unavailable."));
}

public sealed class PostgreSqlPublicTrackingTokenService(
    TenantTransactionContext<OrdersDbContext> transactionContext,
    TrackingTokenHasher tokenHasher,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IOptions<PublicTrackingOptions> options,
    IClock clock) : IPublicTrackingTokenService
{
    internal const int AdvisoryLockNamespace = 0x54524B31; // TRK1
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public Task<PublicTrackingTokenGrant> IssueAsync(
        IssuePublicTrackingTokenCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.UtcNow;
        var expiresAt = Validate(command, command.RequestedExpiration, now);
        return ExecuteGrantAsync(
            command.ActorId,
            command.OrganizationId,
            command.OrderId,
            command.RequestId,
            now,
            expiresAt,
            rotate: false,
            cancellationToken);
    }

    public Task<PublicTrackingTokenGrant> RotateAsync(
        RotatePublicTrackingTokenCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = clock.UtcNow;
        var expiresAt = Validate(command, command.RequestedExpiration, now);
        return ExecuteGrantAsync(
            command.ActorId,
            command.OrganizationId,
            command.OrderId,
            command.RequestId,
            now,
            expiresAt,
            rotate: true,
            cancellationToken);
    }

    public async Task RevokeAsync(
        RevokePublicTrackingTokenCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateShape(command.ActorId, command.OrganizationId, command.OrderId, command.RequestId);
        var now = clock.UtcNow;
        try
        {
            await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = GetDatabase(dbContext);
                    await AcquireOrderLockAsync(connection, transaction, command.OrderId, token);
                    await RequireOwnedOrderAsync(
                        connection,
                        transaction,
                        command.OrganizationId,
                        command.OrderId,
                        token);
                    var revoked = await RevokeActiveAsync(
                        connection,
                        transaction,
                        command.OrderId,
                        now,
                        token);
                    if (revoked == 0)
                    {
                        return true;
                    }

                    await WriteAuditAsync(
                        connection,
                        transaction,
                        command.ActorId,
                        command.OrganizationId,
                        command.OrderId,
                        command.RequestId,
                        "TRACKING_TOKEN_REVOKED",
                        tokenId: null,
                        expiresAt: null,
                        revoked,
                        now,
                        token);
                    return true;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is PublicTrackingTokenNotFoundException
                or PublicTrackingTokenConflictException
                or PublicTrackingTokenInfrastructureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new PublicTrackingTokenInfrastructureException(
                "Public tracking token revocation failed safely.",
                exception);
        }
    }

    private async Task<PublicTrackingTokenGrant> ExecuteGrantAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        string requestId,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        bool rotate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = GetDatabase(dbContext);
                    await AcquireOrderLockAsync(connection, transaction, orderId, token);
                    await RequireOwnedOrderAsync(
                        connection,
                        transaction,
                        organizationId,
                        orderId,
                        token);

                    var revoked = 0;
                    if (rotate)
                    {
                        revoked = await RevokeUnrevokedAsync(
                            connection,
                            transaction,
                            orderId,
                            now,
                            token);
                    }
                    else if (await HasActiveTokenAsync(
                                 connection,
                                 transaction,
                                 orderId,
                                 now,
                                 token))
                    {
                        throw new PublicTrackingTokenConflictException(
                            "An active public tracking token already exists.");
                    }

                    var grant = await InsertGrantWithCollisionRetryAsync(
                        connection,
                        transaction,
                        orderId,
                        organizationId,
                        expiresAt,
                        now,
                        token);
                    await WriteAuditAsync(
                        connection,
                        transaction,
                        actorId,
                        organizationId,
                        orderId,
                        requestId,
                        rotate ? "TRACKING_TOKEN_ROTATED" : "TRACKING_TOKEN_ISSUED",
                        grant.TokenId,
                        expiresAt,
                        revoked,
                        now,
                        token);
                    return grant;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is PublicTrackingTokenNotFoundException
                or PublicTrackingTokenConflictException
                or PublicTrackingTokenInfrastructureException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new PublicTrackingTokenInfrastructureException(
                "Public tracking token issuance failed safely.",
                exception);
        }
    }

    private DateTimeOffset Validate(
        object command,
        DateTimeOffset? requestedExpiration,
        DateTimeOffset now)
    {
        switch (command)
        {
            case IssuePublicTrackingTokenCommand issue:
                ValidateShape(
                    issue.ActorId,
                    issue.OrganizationId,
                    issue.OrderId,
                    issue.RequestId);
                break;
            case RotatePublicTrackingTokenCommand rotate:
                ValidateShape(
                    rotate.ActorId,
                    rotate.OrganizationId,
                    rotate.OrderId,
                    rotate.RequestId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (now.Offset != TimeSpan.Zero)
        {
            throw new PublicTrackingTokenConflictException("The server clock must be UTC.");
        }

        var expiresAt = requestedExpiration ?? now.AddHours(options.Value.TokenLifetimeHours);
        var lifetime = expiresAt - now;
        if (expiresAt.Offset != TimeSpan.Zero ||
            lifetime < MinimumLifetime ||
            lifetime > MaximumLifetime)
        {
            throw new PublicTrackingTokenConflictException(
                "The requested expiration is outside the supported range.");
        }

        return expiresAt;
    }

    private static void ValidateShape(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        string requestId)
    {
        if (actorId == Guid.Empty ||
            organizationId == Guid.Empty ||
            orderId == Guid.Empty ||
            string.IsNullOrWhiteSpace(requestId))
        {
            throw new PublicTrackingTokenConflictException(
                "The public tracking token command is invalid.");
        }
    }

    private async Task<PublicTrackingTokenGrant> InsertGrantWithCollisionRetryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        Guid organizationId,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < options.Value.TokenCollisionRetryCount; attempt++)
        {
            var token = tokenHasher.CreateToken();
            var tokenHash = tokenHasher.HashToken(token);
            var tokenId = Guid.NewGuid();
            var savepoint = $"trk_token_{attempt}";
            await transaction.SaveAsync(savepoint, cancellationToken);
            try
            {
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO orders.public_tracking_tokens
                        (id,order_id,owner_org_id,token_hash,expires_at,revoked_at,created_at)
                    VALUES
                        (@id,@order_id,@owner_org_id,@token_hash,@expires_at,NULL,@created_at);
                    """,
                    connection,
                    transaction);
                command.Parameters.Add(
                    new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid)
                    { TypedValue = tokenId });
                command.Parameters.Add(
                    new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid)
                    { TypedValue = orderId });
                command.Parameters.Add(
                    new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid)
                    { TypedValue = organizationId });
                command.Parameters.Add(
                    new NpgsqlParameter<byte[]>("token_hash", NpgsqlDbType.Bytea)
                    { TypedValue = tokenHash });
                command.Parameters.Add(
                    new NpgsqlParameter<DateTimeOffset>(
                        "expires_at",
                        NpgsqlDbType.TimestampTz)
                    { TypedValue = expiresAt });
                command.Parameters.Add(
                    new NpgsqlParameter<DateTimeOffset>(
                        "created_at",
                        NpgsqlDbType.TimestampTz)
                    { TypedValue = now });
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.ReleaseAsync(savepoint, cancellationToken);
                return new PublicTrackingTokenGrant(tokenId, orderId, token, expiresAt);
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackAsync(savepoint, cancellationToken);
                await transaction.ReleaseAsync(savepoint, cancellationToken);
            }
        }

        throw new PublicTrackingTokenInfrastructureException(
            "Public tracking token generation failed safely.");
    }

    private static async Task AcquireOrderLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(orderId.ToByteArray(), digest);
        var orderKey = BinaryPrimitives.ReadInt32BigEndian(digest);
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(@namespace,@order_key);",
            connection,
            transaction);
        command.Parameters.Add(
            new NpgsqlParameter<int>("namespace", NpgsqlDbType.Integer)
            { TypedValue = AdvisoryLockNamespace });
        command.Parameters.Add(
            new NpgsqlParameter<int>("order_key", NpgsqlDbType.Integer)
            { TypedValue = orderKey });
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task RequireOwnedOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM orders.orders
            WHERE id=@order_id
              AND owner_org_id=@owner_org_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.Add(
            new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid)
            { TypedValue = orderId });
        command.Parameters.Add(
            new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid)
            { TypedValue = organizationId });
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new PublicTrackingTokenNotFoundException();
        }
    }

    private static async Task<bool> HasActiveTokenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM orders.public_tracking_tokens
                WHERE order_id=@order_id
                  AND revoked_at IS NULL
                  AND expires_at>@now);
            """,
            connection,
            transaction);
        command.Parameters.Add(
            new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid)
            { TypedValue = orderId });
        command.Parameters.Add(
            new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz)
            { TypedValue = now });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static Task<int> RevokeUnrevokedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        RevokeAsync(connection, transaction, orderId, now, activeOnly: false, cancellationToken);

    private static Task<int> RevokeActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        RevokeAsync(connection, transaction, orderId, now, activeOnly: true, cancellationToken);

    private static async Task<int> RevokeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        DateTimeOffset now,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        var sql = activeOnly
            ? """
              UPDATE orders.public_tracking_tokens
              SET revoked_at=@now
              WHERE order_id=@order_id
                AND revoked_at IS NULL
                AND expires_at>@now;
              """
            : """
              UPDATE orders.public_tracking_tokens
              SET revoked_at=@now
              WHERE order_id=@order_id
                AND revoked_at IS NULL;
              """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(
            new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid)
            { TypedValue = orderId });
        command.Parameters.Add(
            new NpgsqlParameter<DateTimeOffset>("now", NpgsqlDbType.TimestampTz)
            { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task WriteAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        string requestId,
        string action,
        Guid? tokenId,
        DateTimeOffset? expiresAt,
        int previousTokensRevokedCount,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            order_id = orderId,
            token_id = tokenId,
            expires_at = expiresAt,
            previous_tokens_revoked_count = previousTokensRevokedCount,
            request_id = requestId,
        }, JsonOptions);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                organizationId,
                actorId,
                action,
                "PublicTrackingToken",
                orderId,
                requestId,
                auditRedactor.Redact(payload),
                occurredAt),
            cancellationToken);
    }

    private static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) GetDatabase(
        OrdersDbContext dbContext) =>
        (
            (NpgsqlConnection)dbContext.Database.GetDbConnection(),
            (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction()
        );
}
