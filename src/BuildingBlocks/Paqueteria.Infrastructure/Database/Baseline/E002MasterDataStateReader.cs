using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// MDM-001-OPERATOR-LOADER: whether the Pricing master-data loader migration is recorded, which selects the
/// loader routine into the E-002 map. Recorded history, not the catalog, decides, so a recorded migration
/// whose function is missing or altered fails the map instead of silently shrinking it.
/// </summary>
public static class E002MasterDataStateReader
{
    public const string MigrationId = "20260928000100_AddMasterDataLoader";

    public static async Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('platform.__ef_migrations_history_pricing') IS NOT NULL",
            connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_pricing WHERE \"MigrationId\"=@id)",
            connection, transaction);
        command.Parameters.AddWithValue("id", MigrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
