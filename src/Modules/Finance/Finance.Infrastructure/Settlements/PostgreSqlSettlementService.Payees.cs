using Finance.Application.Settlements;
using NpgsqlTypes;
using static Finance.Infrastructure.Persistence.FinanceSql;

namespace Finance.Infrastructure.Settlements;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11 (listSettlementPayees): the drivers Finance picks a payee from in "Calcular
/// liquidación". One tenant transaction with the createSettlement capability decided first, exactly as for every
/// settlement operation (FINANCE, and PLATFORM_ADMIN with MFA), then one keyset page of the selected organization's
/// driver profiles - the same rows createSettlement accepts as payee - reading only their id and vehicle. Nothing is
/// written and no row is locked.
/// </summary>
public sealed partial class PostgreSqlSettlementService : ISettlementPayeeQuery
{
    private const string PayeesSql =
        """
        SELECT p.id,p.vehicle_type
        FROM drivers.driver_profiles p
        WHERE p.org_id=@organization
          AND (@after::uuid IS NULL OR p.id>@after::uuid)
        ORDER BY p.id
        LIMIT @limit
        """;

    public async Task<SettlementPayeePage> ListPayeesAsync(
        ListSettlementPayeesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!SettlementPayeePolicy.IsValid(query))
        {
            throw Conflict(SettlementConflictCode.InvalidRequest);
        }

        return await gateway.ExecuteAsync(
            query.ActorId,
            query.OrganizationId,
            async (connection, transaction, token) =>
            {
                await AuthorizeAsync(
                    connection, transaction, query.ActorId, query.OrganizationId, query.MfaSatisfied, token);
                var rows = new List<(Guid DriverId, string VehicleType)>();
                await using (var command = Create(connection, transaction, PayeesSql, gateway.CommandTimeoutSeconds))
                {
                    command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, query.OrganizationId));
                    command.Parameters.Add(P("after", NpgsqlDbType.Uuid, query.AfterDriverId));
                    command.Parameters.Add(P("limit", NpgsqlDbType.Integer, SettlementPayeePolicy.PageSize + 1));
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        rows.Add((reader.GetGuid(0), reader.GetString(1)));
                    }
                }

                var listed = rows.Take(SettlementPayeePolicy.PageSize)
                    .Select(row => SettlementPayeePolicy.ToResult(row.DriverId, row.VehicleType))
                    .ToArray();
                return new SettlementPayeePage(
                    listed,
                    rows.Count > SettlementPayeePolicy.PageSize
                        ? SettlementPayeeCursorCodec.Encode(listed[^1].DriverId)
                        : null);
            },
            cancellationToken);
    }
}
