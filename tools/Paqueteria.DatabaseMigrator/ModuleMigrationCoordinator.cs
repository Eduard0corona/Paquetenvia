using Identity.Infrastructure.Persistence;
using Identity.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Locations.Infrastructure.Persistence;
using Locations.Infrastructure.Persistence.Migrations;
using Organizations.Infrastructure.Persistence;
using Organizations.Infrastructure.Persistence.Migrations;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Tenancy;

internal sealed record ModuleMigrationState(
    string Module,
    string HistoryTable,
    string MigrationId,
    string Status);

internal sealed class ModuleMigrationCoordinator
{
    private static readonly (string Module, string HistoryTable, string MigrationId, string SourcePath)[] Contracts =
    [
        ("Identity", "__ef_migrations_history_identity", AdoptCanonicalIdentityBaseline.MigrationId,
            "src/Modules/Identity/Identity.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalIdentityBaseline.cs"),
        ("Organizations", "__ef_migrations_history_organizations", AdoptCanonicalOrganizationsBaseline.MigrationId,
            "src/Modules/Organizations/Organizations.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalOrganizationsBaseline.cs"),
        ("Locations", "__ef_migrations_history_locations", AdoptCanonicalLocationsBaseline.MigrationId,
            "src/Modules/Locations/Locations.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalLocationsBaseline.cs"),
    ];

    public static IReadOnlyList<ModuleMigrationState> VerifySources()
    {
        var root = RepositoryRootLocator.Find();
        var result = new List<ModuleMigrationState>();
        foreach (var contract in Contracts)
        {
            var path = Path.Combine(root, contract.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new BaselineVerificationException($"{contract.Module} adoption migration is missing.");
            }

            var source = File.ReadAllText(path);
            if (!source.Contains(contract.MigrationId, StringComparison.Ordinal) ||
                source.Contains("migrationBuilder.CreateTable", StringComparison.Ordinal) ||
                source.Contains("migrationBuilder.Alter", StringComparison.Ordinal) ||
                source.Contains("migrationBuilder.DropTable", StringComparison.Ordinal))
            {
                throw new BaselineVerificationException(
                    $"{contract.Module} adoption migration is destructive or has an unexpected identifier.");
            }

            result.Add(new ModuleMigrationState(
                contract.Module,
                $"platform.{contract.HistoryTable}",
                contract.MigrationId,
                "VERIFIED"));
        }

        return result;
    }

    public async Task<IReadOnlyList<ModuleMigrationState>> PlanAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var result = new List<ModuleMigrationState>();
        foreach (var contract in Contracts)
        {
            result.Add(await ReadStateAsync(connection, contract, cancellationToken));
        }

        return result;
    }

    public async Task ApplyAsync(string connectionString, CancellationToken cancellationToken)
    {
        var before = await PlanAsync(connectionString, cancellationToken);
        var drift = before.FirstOrDefault(state => state.Status == "DRIFT");
        if (drift is not null)
        {
            throw new InvalidOperationException($"Migration history drift detected for {drift.Module}.");
        }

        if (before.Single(state => state.Module == "Identity").Status == "PENDING")
        {
            await MigrateIdentityAsync(connectionString, cancellationToken);
        }

        if (before.Single(state => state.Module == "Organizations").Status == "PENDING")
        {
            await MigrateOrganizationsAsync(connectionString, cancellationToken);
        }
        if (before.Single(state => state.Module == "Locations").Status == "PENDING")
        {
            await MigrateLocationsAsync(connectionString, cancellationToken);
        }
        await AssertAsync(connectionString, cancellationToken);
    }

    public async Task<IReadOnlyList<ModuleMigrationState>> AssertAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var states = await PlanAsync(connectionString, cancellationToken);
        var invalid = states.FirstOrDefault(state => state.Status != "APPLIED");
        if (invalid is not null)
        {
            throw new InvalidOperationException(
                $"Expected {invalid.Module} migration {invalid.MigrationId} to be applied; detected {invalid.Status}.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var contract in Contracts)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT pg_get_userbyid(c.relowner)
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname='platform' AND c.relname=@history_table;
                """,
                connection);
            command.Parameters.AddWithValue("history_table", contract.HistoryTable);
            var owner = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (owner != "paqueteria_migrator")
            {
                throw new InvalidOperationException(
                    $"platform.{contract.HistoryTable} must be owned by paqueteria_migrator; detected {owner ?? "missing"}.");
            }
        }

        return states;
    }

    private static async Task<ModuleMigrationState> ReadStateAsync(
        NpgsqlConnection connection,
        (string Module, string HistoryTable, string MigrationId, string SourcePath) contract,
        CancellationToken cancellationToken)
    {
        await using var existsCommand = new NpgsqlCommand(
            "SELECT to_regclass('platform.' || @history_table)::text;",
            connection);
        existsCommand.Parameters.AddWithValue("history_table", contract.HistoryTable);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
        if (exists is null or DBNull)
        {
            return new ModuleMigrationState(contract.Module, $"platform.{contract.HistoryTable}", contract.MigrationId, "PENDING");
        }

        await using var historyCommand = new NpgsqlCommand(
            $"SELECT \"MigrationId\" FROM platform.\"{contract.HistoryTable}\" ORDER BY \"MigrationId\";",
            connection);
        var ids = new List<string>();
        await using var reader = await historyCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        var status = ids.Count == 1 && ids[0] == contract.MigrationId ? "APPLIED" : "DRIFT";
        return new ModuleMigrationState(contract.Module, $"platform.{contract.HistoryTable}", contract.MigrationId, status);
    }

    private static async Task MigrateIdentityAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(IdentityDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_identity", "platform");
            })
            .Options;
        await using var context = new IdentityDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigrateOrganizationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrganizationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_organizations", "platform");
            })
            .Options;
        await using var context = new OrganizationsDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task MigrateLocationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsMigratorAsync(connectionString, cancellationToken);
        var options = new DbContextOptionsBuilder<LocationsDbContext>()
            .UseNpgsql(connection, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.MigrationsAssembly(typeof(LocationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_locations", "platform");
            })
            .Options;
        await using var context = new LocationsDbContext(options, new TenantDatabaseExecutionState());
        await context.Database.MigrateAsync(cancellationToken);
    }

    private static async Task<NpgsqlConnection> OpenAsMigratorAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SET ROLE paqueteria_migrator;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
