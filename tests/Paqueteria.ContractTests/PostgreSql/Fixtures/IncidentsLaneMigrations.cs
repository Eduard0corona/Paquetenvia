using Incidents.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

/// <summary>
/// Drives single steps of the Incidents lane against an isolated database exactly as EF Core's migrator does, as
/// <c>paqueteria_migrator</c>: the step's Down operations through the provider's SQL generator and the removal of its
/// history row, in one transaction. The pre-EF-format adoption identifier cannot be named as a migrator target, so the
/// steps are driven through the same services instead.
/// </summary>
internal static class IncidentsLaneMigrations
{
    internal const string HistoryTable = "__ef_migrations_history_incidents";

    internal static async Task RollBackAsMigratorAsync(string connectionString, string migrationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<IncidentsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(IncidentsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable(HistoryTable, "platform");
            })
            .Options;
        await using var context = new IncidentsDbContext(options, new TenantDatabaseExecutionState());
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[migrationId], context.Database.ProviderName!);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.DownOperations);
        var history = context.GetService<IHistoryRepository>().GetDeleteScript(migrationId);

        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var sql in commands.Select(command => command.CommandText).Append(history))
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    internal static async Task MigrateSqlAsMigratorAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", connection))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<string[]> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT \"MigrationId\" FROM platform.{HistoryTable} ORDER BY \"MigrationId\"",
            connection);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return [.. ids];
    }

    internal static async Task<string> LaneStatusAsync(string connectionString) =>
        (await new ModuleMigrationCoordinator().PlanAsync(connectionString, CancellationToken.None))
            .Single(state => state.Module == "Incidents")
            .Status;
}
