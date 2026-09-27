using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// OPS-003-CLEANUP-ROLE: whether the Custody OPS-003 migration is recorded, which selects the
/// cleanup-executor routines into the E-002 map. Recorded history, not the catalog, decides, so a
/// recorded migration whose functions are missing or altered fails the map instead of shrinking it.
/// </summary>
public static class E002CleanupStateReader
{
    public const string Ops003MigrationId = "20260927000100_AddOperationalCleanupExecutor";

    public static async Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('platform.__ef_migrations_history_custody') IS NOT NULL",
            connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_custody WHERE \"MigrationId\"=@id)",
            connection, transaction);
        command.Parameters.AddWithValue("id", Ops003MigrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
