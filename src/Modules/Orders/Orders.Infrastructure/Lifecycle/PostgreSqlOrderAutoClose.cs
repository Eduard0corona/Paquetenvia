using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Lifecycle;
using Orders.Application.Orders;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Orders.Infrastructure.Lifecycle;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10: invokes <c>security.list_auto_close_owner_organizations(uuid, integer)</c> as
/// <c>paqueteria_worker</c>, which holds only EXECUTE on it. Like LIF-001 no tenant context is set: the function is
/// the only cross-tenant step and returns owner organization identifiers only, never an order.
/// </summary>
public sealed class PostgreSqlOrderAutoCloseOwnerDiscovery(NpgsqlDataSource dataSource) : IOrderAutoCloseOwnerDiscovery
{
    private const int CommandTimeoutSeconds = 30;

    public async Task<IReadOnlyList<Guid>> ListOwnersAsync(
        Guid? afterOwnerOrganizationId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker;", connection, transaction))
        {
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        var owners = new List<Guid>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT owners.owner_org_id
            FROM security.list_auto_close_owner_organizations(@after, @limit) AS owners(owner_org_id);
            """,
            connection,
            transaction)
        {
            CommandTimeout = CommandTimeoutSeconds,
        })
        {
            command.Parameters.Add(new NpgsqlParameter<Guid?>("after", NpgsqlDbType.Uuid)
            {
                TypedValue = afterOwnerOrganizationId,
            });
            command.Parameters.Add(new NpgsqlParameter<int>("limit", NpgsqlDbType.Integer) { TypedValue = limit });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                owners.Add(reader.GetGuid(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return owners;
    }
}

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10: the DELIVERED orders of one owner organization, read in an explicit Worker tenant
/// transaction (<c>BEGIN</c>, then <c>set_config(..., true)</c> with only that owner in <c>app.current_org_ids</c>,
/// then <c>SET LOCAL ROLE paqueteria_worker</c>), so FORCE RLS bounds what is visible. Only orders the organization
/// owns are listed; an order it merely operates belongs to its owner's pass.
/// </summary>
public sealed class PostgreSqlOrderAutoCloseCandidateReader(IServiceScopeFactory scopes) : IOrderAutoCloseCandidateReader
{
    private const int CommandTimeoutSeconds = 30;

    public async Task<IReadOnlyList<OrderAutoCloseCandidate>> ListDeliveredAsync(
        Guid ownerOrganizationId,
        Guid? afterOrderId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var transactions = scope.ServiceProvider.GetRequiredService<ITenantTransactionRunner<OrdersDbContext>>();

        // The system actor has no user: a fresh random identity names only this read's app.current_user_id.
        return await transactions.ExecuteAsync(
            new TenantDatabaseExecutionContext(Guid.NewGuid(), [ownerOrganizationId]),
            async (dbContext, token) =>
            {
                var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
                await using var command = new NpgsqlCommand(
                    """
                    SELECT o.id,o.version
                    FROM orders.orders o
                    WHERE o.owner_org_id=@owner
                      AND o.status='DELIVERED'
                      AND (@after IS NULL OR o.id > @after)
                    ORDER BY o.id
                    LIMIT @limit
                    """,
                    connection,
                    transaction)
                {
                    CommandTimeout = CommandTimeoutSeconds,
                };
                command.Parameters.Add(new NpgsqlParameter<Guid>("owner", NpgsqlDbType.Uuid)
                {
                    TypedValue = ownerOrganizationId,
                });
                command.Parameters.Add(new NpgsqlParameter<Guid?>("after", NpgsqlDbType.Uuid) { TypedValue = afterOrderId });
                command.Parameters.Add(new NpgsqlParameter<int>("limit", NpgsqlDbType.Integer) { TypedValue = limit });
                var candidates = new List<OrderAutoCloseCandidate>();
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    candidates.Add(new OrderAutoCloseCandidate(ownerOrganizationId, reader.GetGuid(0), reader.GetInt32(1)));
                }

                return (IReadOnlyList<OrderAutoCloseCandidate>)candidates;
            },
            cancellationToken);
    }
}

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10: one automatic close through <see cref="IOrderSystemTransitionService"/> in its own DI
/// scope and owner tenant transaction. A failed CLOSED guard means the order stays DELIVERED; a version, state,
/// visibility or concurrency conflict means it changed since it was read. Any other error propagates and is counted
/// as a failed attempt; nothing was written either way.
/// </summary>
public sealed class OrderAutoCloseAttempter(IServiceScopeFactory scopes) : IOrderAutoCloseAttempter
{
    public async Task<OrderAutoCloseAttempt> CloseAsync(
        OrderAutoCloseCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var scope = scopes.CreateAsyncScope();
        var transitions = scope.ServiceProvider.GetRequiredService<IOrderSystemTransitionService>();
        try
        {
            await transitions.CloseDeliveredAsync(
                new SystemCloseOrderCommand(candidate.OwnerOrganizationId, candidate.OrderId, candidate.Version),
                cancellationToken);
            return OrderAutoCloseAttempt.Closed;
        }
        catch (OrderTransitionConflictException conflict) when (
            conflict.Code == OrderTransitionConflictCode.GuardNotSatisfied)
        {
            return OrderAutoCloseAttempt.NotEligible(conflict.RejectionCode);
        }
        catch (OrderTransitionConflictException conflict) when (conflict.Code is
            OrderTransitionConflictCode.VersionConflict or
            OrderTransitionConflictCode.InvalidState or
            OrderTransitionConflictCode.TerminalState or
            OrderTransitionConflictCode.OrderUnavailable or
            OrderTransitionConflictCode.ConcurrencyConflict)
        {
            return OrderAutoCloseAttempt.Superseded;
        }
    }
}
