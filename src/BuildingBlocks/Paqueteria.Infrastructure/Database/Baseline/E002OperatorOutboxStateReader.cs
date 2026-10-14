using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: whether the Dispatch operator outbox migration is recorded, which
/// selects the two operator outbox routines into the E-002 map. Recorded history, not the catalog, decides, so a
/// recorded migration whose functions are missing or altered fails the map instead of silently shrinking it.
/// </summary>
public static class E002OperatorOutboxStateReader
{
    public const string MigrationId = "20261003000100_AddOperatorOwnerOutboxExecutor";

    /// <summary>
    /// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: the Dispatch lane step that adds the outbox trace_parent column and the
    /// executor's INSERT on it.
    /// </summary>
    public const string OutboxTraceContextMigrationId = "20261013000100_AddOutboxTraceContext";

    public static Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        IsRecordedAsync(connection, transaction, MigrationId, cancellationToken);

    /// <summary>
    /// OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13: whether the Dispatch trace context step is recorded, which adds INSERT on
    /// platform.outbox_events.trace_parent to the executor's exact grants. Recorded history, not the catalog, decides.
    /// </summary>
    public static Task<bool> IsOutboxTraceContextAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        IsRecordedAsync(connection, transaction, OutboxTraceContextMigrationId, cancellationToken);

    private static async Task<bool> IsRecordedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string migrationId,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('platform.__ef_migrations_history_dispatch') IS NOT NULL",
            connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_dispatch WHERE \"MigrationId\"=@id)",
            connection, transaction);
        command.Parameters.AddWithValue("id", migrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
