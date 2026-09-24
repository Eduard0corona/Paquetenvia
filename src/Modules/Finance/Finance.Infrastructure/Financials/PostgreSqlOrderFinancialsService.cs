using Finance.Application;
using Finance.Application.Financials;
using Finance.Domain;
using Finance.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using static Finance.Infrastructure.Persistence.FinanceSql;

namespace Finance.Infrastructure.Financials;

/// <summary>
/// FIN-001 unit economics read path. Nothing here stores money: revenue comes from the priced order,
/// cost from the cost-bearing dispatch assignments grouped by modality, and the COD position from
/// finance.cod_transactions. The arithmetic is done by <see cref="OrderUnitEconomics"/> in integer cents,
/// so order and route figures always reconcile against the authoritative rows they are derived from.
/// </summary>
public sealed class PostgreSqlOrderFinancialsService(FinanceTenantGateway gateway) : IOrderFinancialsService
{
    /// <summary>
    /// Assignment statuses that have incurred a cost. OFFERED has not been accepted and CANCELLED was
    /// withdrawn, so neither contributes.
    /// </summary>
    internal static readonly string[] CostBearingAssignmentStatuses = ["ACCEPTED", "ACTIVE", "COMPLETED"];

    private const string OrderSql =
        """
        SELECT o.id,o.status,o.total_cents,o.cod_expected_cents,c.status,c.amount_cents
        FROM orders.orders o
        LEFT JOIN finance.cod_transactions c ON c.order_id=o.id
        WHERE o.id=@order AND (o.owner_org_id=@organization OR o.operator_org_id=@organization)
        """;

    private const string OrderCostSql =
        """
        SELECT a.order_id,a.assignment_type,sum(a.cost_cents)::bigint,count(*)::integer
        FROM dispatch.assignments a
        WHERE a.order_id = ANY(@orders)
          AND a.status = ANY(@statuses)
          AND (a.owner_org_id=@organization OR a.operator_org_id=@organization)
        GROUP BY a.order_id,a.assignment_type
        """;

    private const string RouteSql =
        """
        SELECT id,status
        FROM routes.routes
        WHERE id=@route AND operator_org_id=@organization
        """;

    private const string RouteOrdersSql =
        """
        SELECT DISTINCT o.id,o.status,o.total_cents,o.cod_expected_cents,c.status,c.amount_cents
        FROM routes.route_stops s
        JOIN orders.orders o ON o.id=s.order_id
        LEFT JOIN finance.cod_transactions c ON c.order_id=o.id
        WHERE s.route_id=@route AND s.operator_org_id=@organization
        ORDER BY o.id
        """;

    public async Task<OrderFinancialsResult> GetOrderFinancialsAsync(
        GetOrderFinancialsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!FinancialsInputPolicy.IsValid(query))
        {
            throw Conflict(FinanceConflictCode.InvalidRequest);
        }

        return await gateway.ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            async (connection, transaction, token) =>
            {
                await EnsureAuthorizedAsync(
                    connection, transaction, query.ActorId, query.OrganizationId, query.MfaSatisfied, token);

                await using var command = Create(connection, transaction, OrderSql, gateway.CommandTimeoutSeconds);
                command.Parameters.Add(P("order", NpgsqlDbType.Uuid, query.OrderId));
                command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                OrderRow row;
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    if (!await reader.ReadAsync(token))
                    {
                        throw new FinanceNotFoundException();
                    }

                    row = ReadOrder(reader);
                }

                var costs = await ReadCostsAsync(
                    connection, transaction, query.OrganizationId, [row.Id], token);
                return OrderFinancialsResult.From(row.Status, ToEconomics(row, costs));
            },
            cancellationToken);
    }

    public async Task<RouteFinancialsResult> GetRouteFinancialsAsync(
        GetRouteFinancialsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!FinancialsInputPolicy.IsValid(query))
        {
            throw Conflict(FinanceConflictCode.InvalidRequest);
        }

        return await gateway.ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            async (connection, transaction, token) =>
            {
                await EnsureAuthorizedAsync(
                    connection, transaction, query.ActorId, query.OrganizationId, query.MfaSatisfied, token);

                string routeStatus;
                await using (var routeCommand = Create(
                    connection, transaction, RouteSql, gateway.CommandTimeoutSeconds))
                {
                    routeCommand.Parameters.Add(P("route", NpgsqlDbType.Uuid, query.RouteId));
                    routeCommand.Parameters.Add(P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                    await using var reader = await routeCommand.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token))
                    {
                        throw new FinanceNotFoundException();
                    }

                    routeStatus = reader.GetString(1);
                }

                var rows = new List<OrderRow>();
                await using (var ordersCommand = Create(
                    connection, transaction, RouteOrdersSql, gateway.CommandTimeoutSeconds))
                {
                    ordersCommand.Parameters.Add(P("route", NpgsqlDbType.Uuid, query.RouteId));
                    ordersCommand.Parameters.Add(P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                    await using var reader = await ordersCommand.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        rows.Add(ReadOrder(reader));
                    }
                }

                var costs = await ReadCostsAsync(
                    connection,
                    transaction,
                    query.OrganizationId,
                    rows.Select(row => row.Id).ToArray(),
                    token);
                var economics = rows.Select(row => ToEconomics(row, costs)).ToArray();
                return RouteFinancialsResult.From(
                    query.RouteId,
                    routeStatus,
                    RouteUnitEconomics.Aggregate(economics));
            },
            cancellationToken);
    }

    private async Task EnsureAuthorizedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        bool mfaSatisfied,
        CancellationToken cancellationToken)
    {
        var authorization = await gateway.ReadAuthorizationAsync(
            connection, transaction, actorId, organizationId, mfaSatisfied, cancellationToken);
        if (!FinanceAuthorizationPolicy.CanReadFinancials(authorization))
        {
            throw new FinanceForbiddenException();
        }
    }

    private async Task<IReadOnlyDictionary<Guid, List<ModalityCost>>> ReadCostsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        IReadOnlyList<Guid> orderIds,
        CancellationToken cancellationToken)
    {
        var costs = new Dictionary<Guid, List<ModalityCost>>();
        if (orderIds.Count == 0)
        {
            return costs;
        }

        await using var command = Create(connection, transaction, OrderCostSql, gateway.CommandTimeoutSeconds);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("orders", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = orderIds.ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = CostBearingAssignmentStatuses,
        });
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var orderId = reader.GetGuid(0);
            if (!FinanceContractValues.TryParseModality(reader.GetString(1), out var modality))
            {
                throw new FinanceUnavailableException("An assignment carries an unknown delivery modality.");
            }

            if (!costs.TryGetValue(orderId, out var bucket))
            {
                bucket = [];
                costs[orderId] = bucket;
            }

            bucket.Add(new(modality, MoneyCents.FromNonNegative(reader.GetInt64(2)), reader.GetInt32(3)));
        }

        return costs;
    }

    private static OrderUnitEconomics ToEconomics(
        OrderRow row,
        IReadOnlyDictionary<Guid, List<ModalityCost>> costs) =>
        OrderUnitEconomics.Calculate(
            row.Id,
            MoneyCents.FromNonNegative(row.TotalCents),
            costs.TryGetValue(row.Id, out var bucket) ? bucket : [],
            ToCodPosition(row));

    private static CodPosition ToCodPosition(OrderRow row)
    {
        var expected = MoneyCents.FromNonNegative(row.CodExpectedCents);
        if (row.CodStatus is null ||
            !FinanceContractValues.TryParseCodStatus(row.CodStatus, out var status))
        {
            return new(expected, null, null);
        }

        return new(expected, status, row.CodAmountCents is { } amount ? new MoneyCents(amount) : null);
    }

    private static OrderRow ReadOrder(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5));

    private sealed record OrderRow(
        Guid Id,
        string Status,
        long TotalCents,
        long CodExpectedCents,
        string? CodStatus,
        long? CodAmountCents);
}
