using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// MDM-001 loader hardening: whether the Pricing lane step that adds the platform-only operator reference
/// table and the deployment-principal refusal is recorded. It selects the four extra master data executor
/// column grants (<see cref="DatabaseBaselineAssertions.MasterDataExecutorOperatorRefGrants"/>) and requires
/// the table and the refusal. Recorded history, not the catalog, decides.
/// </summary>
public static class MasterDataLoaderHardeningStateReader
{
    public const string MigrationId = "20260928000400_HardenMasterDataLoaderOperatorBoundary";

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
