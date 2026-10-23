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

    /// <summary>
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: grants <c>paqueteria_realtime</c> EXECUTE on the six REALTIME and
    /// location lease-token functions, USAGE on its schemas and its evidence column reads.
    /// </summary>
    public const string RealtimeDispatcherRoleMigrationId = "20261012000100_AddRealtimeDispatcherRole";

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-003): the recipient notice lane step. It replaces the published
    /// 8-argument <c>security.apply_notification_outcome</c> with v2 and adds <c>expand_recipient_notice_request</c>,
    /// <c>cancel_notification</c> and <c>is_recipient_opted_out</c>, all owned by <c>paqueteria_outbox_executor</c> and
    /// executable only by <c>paqueteria_worker</c>.
    /// </summary>
    public const string RecipientNoticeLaneMigrationId = "20261020000100_AddRecipientNoticeLane";

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (AI-08 NTF-005): adds <c>security.read_recipient_notice_counts</c>, owned by
    /// <c>paqueteria_outbox_executor</c> and executable only by <c>paqueteria_worker</c>, and two partial indexes.
    /// </summary>
    public const string RecipientNoticeCountsMigrationId = "20261023000100_AddRecipientNoticeCounts";

    private static readonly string[] ExpectedHistory =
    [
        Ntf001MigrationId,
        "20260827000100_RouteExternalOfferRealtime",
        "20260829000100_RouteManualRouteRealtime",
        DispatchLaneMigrationId,
        // NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02: replaces only the apply_notification_outcome body.
        "20261002000100_FailAmbiguousWhatsAppNotifications",
        // NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03: replaces only the recover_stale_notifications_outbox body.
        "20261003000100_FailStaleWhatsAppNotificationLeases",
        RealtimeDispatcherRoleMigrationId,
        // OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: replaces only the emit_notification_status_changed and
        // expand_order_created_notifications bodies.
        "20261013000200_CarryOutboxTraceContext",
        RecipientNoticeLaneMigrationId,
        RecipientNoticeCountsMigrationId,
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

    /// <summary>
    /// RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12: whether the realtime dispatcher role grants are recorded. Recorded
    /// history, not the catalog, decides, so a recorded lane whose grants are missing fails the map.
    /// </summary>
    public static async Task<bool> IsRealtimeDispatcherRoleAppliedAsync(
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
        command.Parameters.AddWithValue("id", RealtimeDispatcherRoleMigrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20: whether the recipient notice lane step is recorded. Recorded history, not the
    /// catalog, decides, so a recorded step whose routines are missing fails the map.
    /// </summary>
    public static Task<bool> IsRecipientNoticeLaneAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        IsRecordedAsync(connection, transaction, RecipientNoticeLaneMigrationId, cancellationToken);

    /// <summary>
    /// NTF-RECIPIENT-NOTICE-CAPS-2026-10-23: whether the recipient notice count step is recorded. Recorded history, not the
    /// catalog, decides, so a recorded step whose routine is missing fails the map.
    /// </summary>
    public static Task<bool> IsRecipientNoticeCountsAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        IsRecordedAsync(connection, transaction, RecipientNoticeCountsMigrationId, cancellationToken);

    private static async Task<bool> IsRecordedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string migrationId,
        CancellationToken cancellationToken)
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
        command.Parameters.AddWithValue("id", migrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public static E002RoutineMapState SelectMap(E002NotificationState state) => state switch
    {
        E002NotificationState.Pending => E002RoutineMapState.Pending,
        E002NotificationState.Applied => E002RoutineMapState.Applied,
        _ => throw new InvalidOperationException("E002_ROUTINE_MAP_STATE_UNRESOLVED; STOP_FOR_CONTRACT_REVIEW"),
    };
}
