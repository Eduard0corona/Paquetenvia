using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// ORD-AUTO-CLOSE-2026-10-10: whether the Orders auto-close discovery migration is recorded, which selects its routine
/// into the E-002 map. Recorded history, not the catalog, decides, so a recorded migration whose function is missing or
/// altered fails the map instead of silently shrinking it.
/// </summary>
public static class E002OrderAutoCloseStateReader
{
    public const string MigrationId = "20261010000100_AddOrderAutoCloseDiscovery";

    public static async Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('platform.__ef_migrations_history_orders') IS NOT NULL",
            connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_orders WHERE \"MigrationId\"=@id)",
            connection, transaction);
        command.Parameters.AddWithValue("id", MigrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
