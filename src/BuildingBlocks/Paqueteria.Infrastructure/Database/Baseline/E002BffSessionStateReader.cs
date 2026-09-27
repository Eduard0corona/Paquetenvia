using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// BFF-SESSION-TABLE-SHAPE: whether the Identity session-store migration and the Custody purge
/// migration are recorded, which selects their routines into the E-002 map. Recorded history, not the
/// catalog, decides, so a recorded migration whose functions are missing or altered fails the map
/// instead of silently shrinking it.
/// </summary>
public static class E002BffSessionStateReader
{
    public const string SessionStoreMigrationId = "20260927000400_AddBffSessionStore";
    public const string PurgeMigrationId = "20260927000400_AddBffSessionPurge";

    public static Task<bool> IsSessionStoreAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        IsRecordedAsync(connection, transaction, History.Identity, SessionStoreMigrationId, cancellationToken);

    public static Task<bool> IsPurgeAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        IsRecordedAsync(connection, transaction, History.Custody, PurgeMigrationId, cancellationToken);

    private enum History
    {
        Identity,
        Custody,
    }

    private static async Task<bool> IsRecordedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        History history,
        string migrationId,
        CancellationToken cancellationToken)
    {
        // Fixed identifiers only: the history table is chosen from this closed set, never from input.
        var table = history == History.Identity
            ? "__ef_migrations_history_identity"
            : "__ef_migrations_history_custody";
        await using (var exists = new NpgsqlCommand(
            $"SELECT to_regclass('platform.{table}') IS NOT NULL",
            connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM platform.{table} WHERE \"MigrationId\"=@id)",
            connection, transaction);
        command.Parameters.AddWithValue("id", migrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
