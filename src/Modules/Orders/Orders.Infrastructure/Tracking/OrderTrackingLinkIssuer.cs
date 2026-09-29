using Npgsql;
using Orders.Application.Tracking;
using Paqueteria.Application.Auditing;
using Paqueteria.Contracts.Tracking;

namespace Orders.Infrastructure.Tracking;

/// <summary>
/// TRK-002-AUTO-LINK: issues the first public tracking link of a new order inside the order-creation transaction
/// (AI-13 section 4 flow <c>quote_snapshot_to_order</c>). It is a write of the Orders module within that existing
/// flow, not a new cross-module atomic flow: the token row and its <c>TRACKING_TOKEN_ISSUED</c> audit commit or
/// roll back with the order. The plaintext token is not returned; operators get it through get-or-create.
/// </summary>
public interface IOrderTrackingLinkIssuer
{
    Task IssueInitialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        Guid ownerOrganizationId,
        Guid actorId,
        string? requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>Public tracking is disabled: orders are created without a link.</summary>
public sealed class DisabledOrderTrackingLinkIssuer : IOrderTrackingLinkIssuer
{
    public Task IssueInitialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        Guid ownerOrganizationId,
        Guid actorId,
        string? requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class PostgreSqlOrderTrackingLinkIssuer(
    TrackingTokenHasher tokenHasher,
    PublicTrackingLinkKeyRing keyRing,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor) : IOrderTrackingLinkIssuer
{
    public const int InitialGeneration = 1;

    public async Task IssueInitialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        Guid ownerOrganizationId,
        Guid actorId,
        string? requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!keyRing.IsAvailable)
        {
            throw new PublicTrackingTokenInfrastructureException("Public tracking links are unavailable.");
        }

        var token = keyRing.DeriveCurrent(orderId, InitialGeneration);
        var tokenId = Guid.NewGuid();
        await PublicTrackingLinkStore.InsertDerivedLinkAsync(
            connection,
            transaction,
            tokenId,
            orderId,
            ownerOrganizationId,
            InitialGeneration,
            keyRing.CurrentKeyVersion,
            tokenHasher.HashToken(token),
            now,
            cancellationToken);
        await PublicTrackingLinkStore.WriteIssuedAuditAsync(
            auditWriter,
            auditRedactor,
            connection,
            transaction,
            actorId,
            ownerOrganizationId,
            orderId,
            requestId,
            tokenId,
            InitialGeneration,
            keyRing.CurrentKeyVersion,
            previousTokensRevokedCount: 0,
            now,
            cancellationToken);
    }
}
