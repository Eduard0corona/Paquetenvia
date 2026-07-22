using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Paqueteria.ContractTests.Support;
using Testcontainers.PostgreSql;

namespace Paqueteria.ContractTests.PostgreSql.Fixtures;

public sealed class PostgreSqlContractFixture : IAsyncLifetime
{
    public const string Image = "postgis/postgis:18-3.6@sha256:b410052c6f0d7d37b83cac1369df144e1c843971155dea3317961001704d0a9d";
    public const string SchemaSha256 = "c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96";
    public const string RolesSha256 = "7b4d263843e3ba49812fedb1167bd8ab92b2e33efa2558abf0833af1c13760dd";
    public const string AppLogin = "paqueteria_app_login_test";
    public const string WorkerLogin = "paqueteria_worker_login_test";

    private readonly string _adminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _workerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private PostgreSqlContainer? _container;

    public NpgsqlDataSource AdminDataSource { get; private set; } = null!;
    public NpgsqlDataSource AppDataSource { get; private set; } = null!;
    public NpgsqlDataSource WorkerDataSource { get; private set; } = null!;
    public TimeSpan BootstrapDuration { get; private set; }
    public string PostgreSqlVersion { get; private set; } = string.Empty;
    public string PostGisVersion { get; private set; } = string.Empty;

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
            AdminDataSource = NpgsqlDataSource.Create(WithPooling(_container.GetConnectionString(), "postgres", _adminPassword));

            var stopwatch = Stopwatch.StartNew();
            await ApplyNormativeSqlAsync().ConfigureAwait(false);
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

    private async Task ApplyNormativeSqlAsync()
    {
        var schemaPath = RepositoryPaths.Normative("database", "AI-06_SCHEMA.sql");
        var rolesPath = RepositoryPaths.Normative("database", "AI-18_DATABASE_ROLE_MODEL.sql");
        Assert.Equal(SchemaSha256, await ComputeSha256Async(schemaPath).ConfigureAwait(false));
        Assert.Equal(RolesSha256, await ComputeSha256Async(rolesPath).ConfigureAwait(false));

        await ExecuteAdminScriptAsync(await File.ReadAllTextAsync(schemaPath).ConfigureAwait(false)).ConfigureAwait(false);
        await ExecuteAdminScriptAsync(await File.ReadAllTextAsync(rolesPath).ConfigureAwait(false)).ConfigureAwait(false);
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

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
    }
}
