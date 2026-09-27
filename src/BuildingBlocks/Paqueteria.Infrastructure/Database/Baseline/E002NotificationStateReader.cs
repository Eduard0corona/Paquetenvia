using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

public enum E002NotificationState
{
    Pending,
    Applied,
    Drift,
}

public static class E002NotificationStateReader
{
    public const string Ntf001MigrationId = "20260815000100_AddTenantSafeOutboxNotifications";

    /// <summary>D8-OUTBOX-LANE-DISPATCH: installs the DISPATCH lane claim/requeue functions.</summary>
    public const string DispatchLaneMigrationId = "20260927000200_AddDispatchOutboxLane";

    private static readonly string[] ExpectedHistory =
    [
        Ntf001MigrationId,
        "20260827000100_RouteExternalOfferRealtime",
        "20260829000100_RouteManualRouteRealtime",
        DispatchLaneMigrationId,
    ];

    public static async Task<E002NotificationState> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('platform.__ef_migrations_history_notifications') IS NOT NULL",
            connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return E002NotificationState.Pending;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM platform.__ef_migrations_history_notifications ORDER BY \"MigrationId\"",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var history = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            history.Add(reader.GetString(0));
        }

        if (history.Count > ExpectedHistory.Length ||
            !history.SequenceEqual(ExpectedHistory.Take(history.Count), StringComparer.Ordinal))
        {
            return E002NotificationState.Drift;
        }

        return history.Count == 0 ? E002NotificationState.Pending : E002NotificationState.Applied;
    }

    /// <summary>
    /// D8-OUTBOX-LANE-DISPATCH: whether the DISPATCH lane migration is recorded. Recorded history, not
    /// the catalog, decides, so a recorded lane whose functions are missing fails the map.
    /// </summary>
    public static async Task<bool> IsDispatchLaneAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('platform.__ef_migrations_history_notifications') IS NOT NULL",
            connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_notifications WHERE \"MigrationId\"=@id)",
            connection, transaction);
        command.Parameters.AddWithValue("id", DispatchLaneMigrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public static E002RoutineMapState SelectMap(E002NotificationState state) => state switch
    {
        E002NotificationState.Pending => E002RoutineMapState.Pending,
        E002NotificationState.Applied => E002RoutineMapState.Applied,
        _ => throw new InvalidOperationException("E002_ROUTINE_MAP_STATE_UNRESOLVED; STOP_FOR_CONTRACT_REVIEW"),
    };
}
