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

    public static async Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
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
        command.Parameters.AddWithValue("id", MigrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
