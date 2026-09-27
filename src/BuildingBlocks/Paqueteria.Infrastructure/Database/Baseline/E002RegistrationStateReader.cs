using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

/// <summary>
/// REG-001 and REG-002: whether the Organizations REG-001 (and REG-002) migration is recorded, which
/// selects the registration-executor routines into the E-002 map. Recorded history, not the catalog, decides, so a
/// recorded migration whose functions are missing or altered fails the map instead of shrinking it.
/// </summary>
public static class E002RegistrationStateReader
{
    public const string Reg001MigrationId = "20260927000400_AddSelfServiceRegistration";
    public const string Reg002MigrationId = "20260927000500_AddPendingMemberships";

    public static Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        IsRecordedAsync(Reg001MigrationId, connection, transaction, cancellationToken);

    public static Task<bool> IsReg002AppliedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        IsRecordedAsync(Reg002MigrationId, connection, transaction, cancellationToken);

    private static async Task<bool> IsRecordedAsync(
        string migrationId,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(
            "SELECT to_regclass('platform.__ef_migrations_history_organizations') IS NOT NULL",
            connection, transaction))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM platform.__ef_migrations_history_organizations WHERE \"MigrationId\"=@id)",
            connection, transaction);
        command.Parameters.AddWithValue("id", migrationId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
