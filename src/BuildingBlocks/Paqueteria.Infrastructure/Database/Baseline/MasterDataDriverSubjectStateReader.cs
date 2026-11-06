using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// MDM-001-DRIVER-SUB-2026-11-06: whether the Pricing lane step that lets the loader name a driver by the AuthCenter
/// sub is recorded. It selects the extra master data executor column grant
/// (<see cref="DatabaseBaselineAssertions.MasterDataExecutorIdentitySubjectGrants"/>). Recorded history, not the
/// catalog, decides.
/// </summary>
public static class MasterDataDriverSubjectStateReader
{
    public const string MigrationId = "20261106000100_ResolveDriverIdentitySubjectInMasterDataLoader";

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
