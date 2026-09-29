using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
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
    public Task<PublicTrackingTokenGrant> GetOrCreateAsync(
        GetOrCreatePublicTrackingLinkCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException<PublicTrackingTokenGrant>(
            new PublicTrackingTokenInfrastructureException("Public tracking is unavailable."));

    public Task RevokeAsync(
        RevokePublicTrackingTokenCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException(
            new PublicTrackingTokenInfrastructureException("Public tracking is unavailable."));
}

/// <summary>
/// TRK-002-AUTO-LINK: get-or-create and revoke of an order's public tracking link, inside one tenant transaction
/// that locks the order (owned by the selected organization, otherwise the uniform 404).
/// </summary>
/// <remarks>
/// <para>
/// Get-or-create re-derives the token of the current generation and writes nothing, so a retry with the same or
/// another Idempotency-Key returns the same link and adds no audit row. It creates a link (audited
/// <c>TRACKING_TOKEN_ISSUED</c>) only when the order has none that can be re-derived: none yet, a revoked one (the
/// next generation), a pre-derivation random one, or one whose key version is no longer configured (those two are
/// retired in the same transaction). A finished order never gets a new link.
/// </para>
/// <para>
/// Revoke retires the live link (audited <c>TRACKING_TOKEN_REVOKED</c>); the public lookup then answers the uniform
/// 404, and a later get-or-create derives the next generation, never the revoked one.
/// </para>
/// </remarks>
public sealed class PostgreSqlPublicTrackingTokenService(
    TenantTransactionContext<OrdersDbContext> transactionContext,
    TrackingTokenHasher tokenHasher,
    PublicTrackingLinkKeyRing keyRing,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IClock clock) : IPublicTrackingTokenService
{
    private readonly PublicOrderStatusPolicy statusPolicy = new();

    public async Task<PublicTrackingTokenGrant> GetOrCreateAsync(
        GetOrCreatePublicTrackingLinkCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateShape(command.ActorId, command.OrganizationId, command.OrderId, command.RequestId);
        var now = RequireUtc(clock.UtcNow);
        if (!keyRing.IsAvailable)
        {
            throw new PublicTrackingTokenInfrastructureException("Public tracking links are unavailable.");
        }

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                (dbContext, token) => GetOrCreateWithinTransactionAsync(dbContext, command, now, token),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is PublicTrackingTokenNotFoundException
                or PublicTrackingTokenConflictException
                or PublicTrackingLinkOrderFinishedException
                or PublicTrackingTokenInfrastructureException)
        {
            throw;
        }
        catch (PublicStatusMappingException exception)
        {
            // AI-01 invariant 12: an unmapped internal status fails loudly, never as a link.
            throw new PublicTrackingTokenInfrastructureException(
                "The order status has no public mapping.",
                exception);
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new PublicTrackingTokenInfrastructureException(
                "Public tracking link retrieval failed safely.",
                exception);
        }
    }

    public async Task RevokeAsync(
        RevokePublicTrackingTokenCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateShape(command.ActorId, command.OrganizationId, command.OrderId, command.RequestId);
        var now = RequireUtc(clock.UtcNow);
        try
        {
            await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = GetDatabase(dbContext);
                    await PublicTrackingLinkStore.AcquireOrderLockAsync(connection, transaction, command.OrderId, token);
                    if (await PublicTrackingLinkStore.ReadOwnedOrderStatusForUpdateAsync(
                            connection,
                            transaction,
                            command.OrganizationId,
                            command.OrderId,
                            token) is null)
                    {
                        throw new PublicTrackingTokenNotFoundException();
                    }

                    var revoked = await PublicTrackingLinkStore.RetireActiveLinksAsync(
                        connection,
                        transaction,
                        command.OrderId,
                        now,
                        token);
                    if (revoked == 0)
                    {
                        return true;
                    }

                    await PublicTrackingLinkStore.WriteRevokedAuditAsync(
                        auditWriter,
                        auditRedactor,
                        connection,
                        transaction,
                        command.ActorId,
                        command.OrganizationId,
                        command.OrderId,
                        command.RequestId,
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

    private async Task<PublicTrackingTokenGrant> GetOrCreateWithinTransactionAsync(
        OrdersDbContext dbContext,
        GetOrCreatePublicTrackingLinkCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (connection, transaction) = GetDatabase(dbContext);
        await PublicTrackingLinkStore.AcquireOrderLockAsync(connection, transaction, command.OrderId, cancellationToken);
        var status = await PublicTrackingLinkStore.ReadOwnedOrderStatusForUpdateAsync(
            connection,
            transaction,
            command.OrganizationId,
            command.OrderId,
            cancellationToken) ?? throw new PublicTrackingTokenNotFoundException();

        var publicStatus = statusPolicy.Map(status);
        var finished = PublicTrackingLinkPolicy.IsFinal(publicStatus);
        var validUntil = finished
            ? PublicTrackingLinkPolicy.ValidUntil(
                publicStatus,
                await PublicTrackingLinkStore.ReadFirstFinalEventAtAsync(
                    connection,
                    transaction,
                    command.OrderId,
                    cancellationToken),
                now)
            : null;

        // A finished order whose grace is over has no link the public lookup would still show.
        if (validUntil is { } end && end <= now)
        {
            throw new PublicTrackingLinkOrderFinishedException();
        }

        var active = await PublicTrackingLinkStore.ReadActiveLinksAsync(
            connection,
            transaction,
            command.OrderId,
            now,
            cancellationToken);
        var derived = active.FirstOrDefault(link => link.KeyVersion is not null);
        if (derived is not null &&
            keyRing.TryDerive(derived.KeyVersion!.Value, command.OrderId, derived.Generation, out var existing))
        {
            if (!CryptographicOperations.FixedTimeEquals(tokenHasher.HashToken(existing), derived.TokenHash))
            {
                // The configured key of that version is not the one the link was derived with: fail closed rather
                // than hand out a link that does not resolve or silently replace the one customers hold.
                throw new PublicTrackingTokenInfrastructureException(
                    "The public tracking link key does not match its recorded version.");
            }

            return new PublicTrackingTokenGrant(
                derived.Id,
                command.OrderId,
                existing,
                derived.Generation,
                validUntil);
        }

        if (finished)
        {
            throw new PublicTrackingLinkOrderFinishedException();
        }

        // No link that can be re-derived: retire whatever the lookup could still accept (a pre-derivation random
        // token, or a link whose key version is no longer configured) and derive the next generation.
        var revoked = await PublicTrackingLinkStore.RetireActiveLinksAsync(
            connection,
            transaction,
            command.OrderId,
            now,
            cancellationToken);
        var generation = await PublicTrackingLinkStore.ReadNextGenerationAsync(
            connection,
            transaction,
            command.OrderId,
            cancellationToken);
        var token = keyRing.DeriveCurrent(command.OrderId, generation);
        var tokenId = Guid.NewGuid();
        await PublicTrackingLinkStore.InsertDerivedLinkAsync(
            connection,
            transaction,
            tokenId,
            command.OrderId,
            command.OrganizationId,
            generation,
            keyRing.CurrentKeyVersion,
            tokenHasher.HashToken(token),
            now,
            cancellationToken);
        await PublicTrackingLinkStore.WriteIssuedAuditAsync(
            auditWriter,
            auditRedactor,
            connection,
            transaction,
            command.ActorId,
            command.OrganizationId,
            command.OrderId,
            command.RequestId,
            tokenId,
            generation,
            keyRing.CurrentKeyVersion,
            revoked,
            now,
            cancellationToken);
        return new PublicTrackingTokenGrant(tokenId, command.OrderId, token, generation, null);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset now) =>
        now.Offset == TimeSpan.Zero
            ? now
            : throw new PublicTrackingTokenConflictException("The server clock must be UTC.");

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

    private static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) GetDatabase(
        OrdersDbContext dbContext) =>
        (
            (NpgsqlConnection)dbContext.Database.GetDbConnection(),
            (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction()
        );
}
