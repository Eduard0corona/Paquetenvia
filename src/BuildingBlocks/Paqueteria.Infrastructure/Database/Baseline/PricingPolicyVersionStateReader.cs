using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// PRC-POLICY-VERSION-PER-ORG: whether the Pricing lane step that makes the MDM-001 loader store tariff policy
/// versions is recorded. It selects the two extra master data executor column grants
/// (<see cref="DatabaseBaselineAssertions.MasterDataExecutorPolicyVersionGrants"/>).
/// </summary>
public static class PricingPolicyVersionStateReader
{
    public const string MigrationId = "20260928000300_StoreTariffPolicyVersionInMasterDataLoader";

    public static async Task<bool> IsLoaderStoringAsync(
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
