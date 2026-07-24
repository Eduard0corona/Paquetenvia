using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using Paqueteria.Infrastructure.Database.Baseline;
using Testcontainers.PostgreSql;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Infrastructure.Tenancy;
using Locations.Infrastructure.Persistence;
using Pricing.Infrastructure.Persistence;
using Orders.Infrastructure.Persistence;
using Drivers.Infrastructure.Persistence;
using Dispatch.Infrastructure.Persistence;

namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

public sealed class PostgreSqlContractFixture : IAsyncLifetime
{
    public const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";
    public const string SchemaSha256 = CanonicalBaselineContract.SchemaSha256;
    public const string RolesSha256 = CanonicalBaselineContract.RolesSha256;
    public const string AppLogin = "paqueteria_app_login_test";
    public const string WorkerLogin = "paqueteria_worker_login_test";

    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly HashSet<string> _isolatedDatabases = new(StringComparer.Ordinal);
    private PostgreSqlContainer? _container;

    public NpgsqlDataSource AdminDataSource { get; private set; } = null!;
    public NpgsqlDataSource AppDataSource { get; private set; } = null!;
    public NpgsqlDataSource WorkerDataSource { get; private set; } = null!;
    public string DeploymentConnectionString { get; private set; } = string.Empty;
    public TimeSpan BootstrapDuration { get; private set; }
    public TimeSpan SchemaDuration { get; private set; }
    public TimeSpan RolesDuration { get; private set; }
    public TimeSpan AssertionsDuration { get; private set; }
    public DatabaseBaselineApplyStatus ApplyStatus { get; private set; }
    public string PostgreSqlVersion { get; private set; } = string.Empty;
    public string PostGisVersion { get; private set; } = string.Empty;

    public NpgsqlDataSource CreateAppDataSource(
        ILoggerFactory? loggerFactory = null,
        int maxPoolSize = 1,
        string applicationName = "Paqueteria.TEN002.ContractTests")
    {
        if (_container is null)
        {
            throw new InvalidOperationException("The PostgreSQL contract fixture has not been initialized.");
        }

        var connectionString = WithPooling(_container.GetConnectionString(), AppLogin, _appPassword);
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPoolSize,
            ApplicationName = applicationName,
        };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionStringBuilder.ConnectionString);
        dataSourceBuilder.UseNetTopologySuite();
        if (loggerFactory is not null)
        {
            dataSourceBuilder.UseLoggerFactory(loggerFactory);
            dataSourceBuilder.EnableParameterLogging();
        }

        return dataSourceBuilder.Build();
    }

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("paqueteria_contracts")
            .WithUsername("postgres")
            .WithPassword(_adminPassword)
            .WithCleanUp(true)
            .Build();

        try
        {
            await _container.StartAsync().ConfigureAwait(false);
            DeploymentConnectionString = WithPooling(_container.GetConnectionString(), "postgres", _adminPassword);
            AdminDataSource = NpgsqlDataSource.Create(DeploymentConnectionString);

            var stopwatch = Stopwatch.StartNew();
            var baseline = await new DatabaseBaselineVerifier().VerifyAsync().ConfigureAwait(false);
            var deployment = await new DatabaseBaselineDeployer().ApplyAsync(
                baseline,
                DeploymentConnectionString).ConfigureAwait(false);
            ApplyStatus = deployment.Status;
            SchemaDuration = deployment.Timings.Schema;
            RolesDuration = deployment.Timings.Roles;
            AssertionsDuration = deployment.Timings.Assertions;
            await ApplyAdoptionMigrationsAsync().ConfigureAwait(false);
            await CreateRuntimeLoginsAsync().ConfigureAwait(false);
            stopwatch.Stop();
            BootstrapDuration = stopwatch.Elapsed;

            AppDataSource = NpgsqlDataSource.Create(WithPooling(_container.GetConnectionString(), AppLogin, _appPassword));
            WorkerDataSource = NpgsqlDataSource.Create(WithPooling(_container.GetConnectionString(), WorkerLogin, _workerPassword));

            await using var command = AdminDataSource.CreateCommand(
                "SELECT current_setting('server_version'), public.PostGIS_Version()");
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.True(await reader.ReadAsync().ConfigureAwait(false));
            PostgreSqlVersion = reader.GetString(0);
            PostGisVersion = reader.GetString(1);
        }
        catch (Exception exception)
        {
            var diagnostics = await GetContainerDiagnosticsAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"PostgreSQL contract fixture bootstrap failed. Container diagnostics:{Environment.NewLine}{diagnostics}",
                exception);
        }
    }

    public async Task DisposeAsync()
    {
        if (WorkerDataSource is not null)
        {
            await WorkerDataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (AppDataSource is not null)
        {
            await AppDataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (AdminDataSource is not null)
        {
            foreach (var database in _isolatedDatabases.ToArray())
            {
                await DropIsolatedDatabaseAsync(database).ConfigureAwait(false);
            }

            await AdminDataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<string> GetContainerDiagnosticsAsync()
    {
        if (_container is null)
        {
            return "Container was not created.";
        }

        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
            return string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (Exception exception)
        {
            return $"Container logs unavailable: {exception.GetType().Name}: {exception.Message}";
        }
    }

    public async Task<string> CreateIsolatedDatabaseAsync(string purpose)
    {
        var normalizedPurpose = new string(purpose.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).Take(16).ToArray());
        var database = $"dba001_{normalizedPurpose}_{Guid.NewGuid():N}";
        await ExecuteAdminScriptAsync($"CREATE DATABASE \"{database}\"").ConfigureAwait(false);
        _isolatedDatabases.Add(database);
        var builder = new NpgsqlConnectionStringBuilder(DeploymentConnectionString)
        {
            Database = database,
            Pooling = false,
            ApplicationName = "Paqueteria.DBA001.ContractTests",
        };
        return builder.ConnectionString;
    }

    public async Task DropIsolatedDatabaseAsync(string connectionStringOrDatabase)
    {
        var database = connectionStringOrDatabase.Contains('=', StringComparison.Ordinal)
            ? new NpgsqlConnectionStringBuilder(connectionStringOrDatabase).Database
            : connectionStringOrDatabase;
        if (string.IsNullOrWhiteSpace(database) || !_isolatedDatabases.Remove(database))
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminScriptAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)").ConfigureAwait(false);
    }

    private async Task CreateRuntimeLoginsAsync()
    {
        var sql = $$"""
            CREATE ROLE {{AppLogin}} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{{_appPassword}}';
            GRANT paqueteria_app TO {{AppLogin}};
            CREATE ROLE {{WorkerLogin}} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{{_workerPassword}}';
            GRANT paqueteria_worker TO {{WorkerLogin}};
            """;
        await ExecuteAdminScriptAsync(sql).ConfigureAwait(false);
    }

    private async Task ApplyAdoptionMigrationsAsync()
    {
        await using (var identityConnection = new NpgsqlConnection(DeploymentConnectionString))
        {
            await identityConnection.OpenAsync().ConfigureAwait(false);
            await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", identityConnection))
            {
                await role.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var identityOptions = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(identityConnection, postgres =>
                {
                    postgres.MigrationsAssembly(typeof(IdentityDbContext).Assembly.FullName);
                    postgres.MigrationsHistoryTable("__ef_migrations_history_identity", "platform");
                }).Options;
            await using var identity = new IdentityDbContext(identityOptions, new TenantDatabaseExecutionState());
            await identity.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var organizationsConnection = new NpgsqlConnection(DeploymentConnectionString);
        await organizationsConnection.OpenAsync().ConfigureAwait(false);
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", organizationsConnection))
        {
            await role.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var organizationsOptions = new DbContextOptionsBuilder<OrganizationsDbContext>()
            .UseNpgsql(organizationsConnection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrganizationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_organizations", "platform");
            }).Options;
        await using var organizations = new OrganizationsDbContext(
            organizationsOptions,
            new TenantDatabaseExecutionState());
        await organizations.Database.MigrateAsync().ConfigureAwait(false);

        await using var locationsConnection = new NpgsqlConnection(DeploymentConnectionString);
        await locationsConnection.OpenAsync().ConfigureAwait(false);
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", locationsConnection))
        {
            await role.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var locationsOptions = new DbContextOptionsBuilder<LocationsDbContext>()
            .UseNpgsql(locationsConnection, postgres =>
            {
                postgres.UseNetTopologySuite();
                postgres.MigrationsAssembly(typeof(LocationsDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_locations", "platform");
            }).Options;
        await using var locations = new LocationsDbContext(
            locationsOptions,
            new TenantDatabaseExecutionState());
        await locations.Database.MigrateAsync().ConfigureAwait(false);

        await using var driversConnection = new NpgsqlConnection(DeploymentConnectionString);
        await driversConnection.OpenAsync().ConfigureAwait(false);
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", driversConnection))
        {
            await role.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var driversOptions = new DbContextOptionsBuilder<DriversDbContext>()
            .UseNpgsql(driversConnection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(DriversDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_drivers", "platform");
            }).Options;
        await using var drivers = new DriversDbContext(
            driversOptions,
            new TenantDatabaseExecutionState());
        await drivers.Database.MigrateAsync().ConfigureAwait(false);

        await using var pricingConnection = new NpgsqlConnection(DeploymentConnectionString);
        await pricingConnection.OpenAsync().ConfigureAwait(false);
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", pricingConnection))
        {
            await role.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var pricingOptions = new DbContextOptionsBuilder<PricingDbContext>()
            .UseNpgsql(pricingConnection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(PricingDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_pricing", "platform");
            }).Options;
        await using var pricing = new PricingDbContext(pricingOptions, new TenantDatabaseExecutionState());
        await pricing.Database.MigrateAsync().ConfigureAwait(false);

        await using var ordersConnection = new NpgsqlConnection(DeploymentConnectionString);
        await ordersConnection.OpenAsync().ConfigureAwait(false);
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", ordersConnection))
        {
            await role.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var ordersOptions = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(ordersConnection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(OrdersDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_orders", "platform");
            }).Options;
        await using var orders = new OrdersDbContext(ordersOptions, new TenantDatabaseExecutionState());
        await orders.Database.MigrateAsync().ConfigureAwait(false);

        await using var dispatchConnection = new NpgsqlConnection(DeploymentConnectionString);
        await dispatchConnection.OpenAsync().ConfigureAwait(false);
        await using (var role = new NpgsqlCommand("SET ROLE paqueteria_migrator", dispatchConnection))
        {
            await role.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var dispatchOptions = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(dispatchConnection, postgres =>
            {
                postgres.MigrationsAssembly(typeof(DispatchDbContext).Assembly.FullName);
                postgres.MigrationsHistoryTable("__ef_migrations_history_dispatch", "platform");
            }).Options;
        await using var dispatch = new DispatchDbContext(
            dispatchOptions,
            new TenantDatabaseExecutionState());
        await dispatch.Database.MigrateAsync().ConfigureAwait(false);
    }

    private async Task ExecuteAdminScriptAsync(string sql)
    {
        await using var command = AdminDataSource.CreateCommand(sql);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string WithPooling(string connectionString, string username, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = username,
            Password = password,
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 12,
            Timeout = 15,
            CommandTimeout = 30,
            ApplicationName = "Paqueteria.ContractTests",
        };
        return builder.ConnectionString;
    }
}
