using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.Infrastructure.DataProtection;
using Realtime.Application.Configuration;

namespace Paqueteria.UnitTests.Scaling;

/// <summary>
/// SCL-007: one connection pool per database role in each process, sized by validated configuration that fails closed,
/// within the B1ms budget. Nothing here opens a connection.
/// </summary>
public sealed class Scl007RuntimeDataSourceTests
{
    private const string Unreachable =
        "Host=127.0.0.1;Port=1;Database=paqueteria;Username=scl007_unused;Password=unused;Maximum Pool Size=5";

    [Fact]
    public void The_budget_is_seventy_percent_of_the_b1ms_user_connections_and_the_defaults_fill_it()
    {
        // B1ms: max_connections 50, of which Azure reserves 15; AZR-001-INV-014 caps the runtime pools at 70 %.
        Assert.Equal((50 - 15) * 70 / 100, DatabasePoolOptions.RuntimeConnectionBudget);
        var defaults = new DatabasePoolOptions();
        Assert.Equal(10, defaults.ApplicationMaximumPoolSize);
        Assert.Equal(4, defaults.RealtimeMaximumPoolSize);
        Assert.Equal(10, defaults.WorkerMaximumPoolSize);
        Assert.Equal(DatabasePoolOptions.RuntimeConnectionBudget, defaults.TotalMaximumPoolSize);
        Assert.Empty(DatabasePoolOptionsValidator.Errors(defaults));
    }

    [Theory]
    [InlineData("ApplicationMaximumPoolSize", "0")]
    [InlineData("RealtimeMaximumPoolSize", "-1")]
    [InlineData("WorkerMaximumPoolSize", "25")]
    [InlineData("WorkerMaximumPoolSize", "2147483647")]
    public void A_pool_size_outside_one_to_the_budget_fails_the_start(string key, string value)
    {
        var failure = Assert.Throws<OptionsValidationException>(
            () => ResolveOptions(new() { [$"DatabasePools:{key}"] = value }));

        Assert.Contains($"DatabasePools:{key} must be between 1 and 24.", failure.Failures);
    }

    [Fact]
    public void Pool_sizes_whose_sum_exceeds_the_budget_fail_the_start_even_when_each_one_is_in_range()
    {
        var failure = Assert.Throws<OptionsValidationException>(() => ResolveOptions(new()
        {
            ["DatabasePools:ApplicationMaximumPoolSize"] = "12",
        }));

        Assert.Contains(
            "DatabasePools: ApplicationMaximumPoolSize + RealtimeMaximumPoolSize + WorkerMaximumPoolSize must not " +
            "exceed the SCL-007 budget of 24 connections.",
            failure.Failures);
    }

    [Fact]
    public void A_pool_size_that_is_not_a_number_fails_the_start()
    {
        Assert.ThrowsAny<InvalidOperationException>(() => ResolveOptions(new()
        {
            ["DatabasePools:WorkerMaximumPoolSize"] = "ten",
        }));
    }

    [Fact]
    public void Smaller_pools_within_the_budget_are_accepted()
    {
        var options = ResolveOptions(new()
        {
            ["DatabasePools:ApplicationMaximumPoolSize"] = "6",
            ["DatabasePools:RealtimeMaximumPoolSize"] = "2",
            ["DatabasePools:WorkerMaximumPoolSize"] = "4",
        });

        Assert.Equal(12, options.TotalMaximumPoolSize);
    }

    [Fact]
    public async Task Every_registration_of_a_role_shares_one_pool_and_its_data_source()
    {
        var services = Services(new()
        {
            ["ConnectionStrings:Paqueteria"] = Unreachable,
            ["ConnectionStrings:PaqueteriaWorker"] = Unreachable.Replace("scl007_unused", "scl007_worker"),
        });
        var configuration = services.Configuration();
        for (var module = 0; module < 5; module++)
        {
            services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Application);
        }

        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);

        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(RuntimeDataSource) &&
            Equals(descriptor.ServiceKey, RuntimeDatabaseRole.Application));
        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(RuntimeDataSource) &&
            Equals(descriptor.ServiceKey, RuntimeDatabaseRole.Worker));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(RuntimeDataSource) &&
            Equals(descriptor.ServiceKey, RuntimeDatabaseRole.Realtime));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(NpgsqlDataSource) && !descriptor.IsKeyedService);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var application = provider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Application);
        var worker = provider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Worker);
        Assert.Same(application, provider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Application));
        Assert.Same(
            application.DataSource,
            provider.GetRequiredKeyedService<NpgsqlDataSource>(RuntimeDatabaseRole.Application));
        Assert.Same(application.DataSource, provider.GetRuntimeDataSource(RuntimeDatabaseRole.Application));
        Assert.NotSame(application.DataSource, worker.DataSource);
        Assert.Equal(RuntimeDatabaseRole.Application, application.Role);
        Assert.Equal(RuntimeDatabaseRole.Worker, worker.Role);
    }

    [Fact]
    public async Task The_configured_size_replaces_the_connection_string_maximum()
    {
        var services = Services(new()
        {
            ["ConnectionStrings:PaqueteriaRealtime"] = Unreachable,
            ["DatabasePools:RealtimeMaximumPoolSize"] = "3",
        });
        services.AddRuntimeDataSource(
            services.Configuration(),
            RuntimeDatabaseRole.Realtime);
        await using var provider = services.BuildServiceProvider();

        var realtime = provider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Realtime);

        Assert.Equal(3, realtime.MaximumPoolSize);
        var built = new NpgsqlConnectionStringBuilder(realtime.DataSource.ConnectionString);
        Assert.Equal(3, built.MaxPoolSize);
        Assert.Equal("scl007_unused", built.Username);
    }

    [Fact]
    public async Task A_missing_connection_string_fails_closed_on_first_use_naming_only_its_entry()
    {
        var services = Services(new());
        services.AddRuntimeDataSource(
            services.Configuration(),
            RuntimeDatabaseRole.Worker);
        await using var provider = services.BuildServiceProvider();

        // Resolving the pool never needs the connection string: a host whose Worker jobs are disabled still starts.
        var worker = provider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Worker);
        Assert.False(worker.IsCreated);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.OpenConnectionAsync(CancellationToken.None).AsTask());
        Assert.Equal(
            "ConnectionStrings:PaqueteriaWorker is required by the paqueteria_worker connection pool.",
            failure.Message);
    }

    [Fact]
    public async Task A_minimum_pool_size_above_the_configured_maximum_fails_closed_without_echoing_the_secret()
    {
        await using var pool = RuntimeDataSource.Create(
            RuntimeDatabaseRole.Application,
            "Host=127.0.0.1;Port=1;Username=scl007_unused;Password=scl007-not-a-secret;Minimum Pool Size=8",
            maximumPoolSize: 4);

        var failure = Assert.Throws<InvalidOperationException>(() => pool.DataSource);

        Assert.Contains("ConnectionStrings:Paqueteria ", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("scl007-not-a-secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Type_plugins_reach_only_their_role_pool_once_whichever_module_registered_them_first()
    {
        var applied = new List<string>();
        var services = Services(new()
        {
            ["ConnectionStrings:Paqueteria"] = Unreachable,
            ["ConnectionStrings:PaqueteriaWorker"] = Unreachable,
        });
        var configuration = services.Configuration();
        services.AddRuntimeDataSourcePlugin(RuntimeDatabaseRole.Application, "types", _ => applied.Add("application"));
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Application);
        services.AddRuntimeDataSourcePlugin(RuntimeDatabaseRole.Application, "types", _ => applied.Add("duplicate"));
        services.AddRuntimeDataSourcePlugin(RuntimeDatabaseRole.Worker, "types", _ => applied.Add("worker"));
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);
        await using var provider = services.BuildServiceProvider();

        _ = provider.GetRuntimeDataSource(RuntimeDatabaseRole.Application);
        Assert.Equal(["application"], applied);
        _ = provider.GetRuntimeDataSource(RuntimeDatabaseRole.Worker);
        Assert.Equal(["application", "worker"], applied);
    }

    [Fact]
    public void Each_role_names_its_connection_string_and_its_nobypassrls_role()
    {
        Assert.Equal(
            [RuntimeDatabaseRole.Application, RuntimeDatabaseRole.Worker, RuntimeDatabaseRole.Realtime],
            RuntimeDatabaseRoles.All);
        Assert.Equal("Paqueteria", RuntimeDatabaseRoles.ConnectionStringName(RuntimeDatabaseRole.Application));
        Assert.Equal("PaqueteriaWorker", RuntimeDatabaseRoles.ConnectionStringName(RuntimeDatabaseRole.Worker));
        Assert.Equal(
            OutboxDispatcherOptions.ConnectionStringName,
            RuntimeDatabaseRoles.ConnectionStringName(RuntimeDatabaseRole.Realtime));
        Assert.Equal("paqueteria_app", RuntimeDatabaseRoles.PostgreSqlRole(RuntimeDatabaseRole.Application));
        Assert.Equal("paqueteria_worker", RuntimeDatabaseRoles.PostgreSqlRole(RuntimeDatabaseRole.Worker));
        Assert.Equal(
            OutboxDispatcherOptions.DatabaseRole,
            RuntimeDatabaseRoles.PostgreSqlRole(RuntimeDatabaseRole.Realtime));
        foreach (var role in RuntimeDatabaseRoles.All)
        {
            Assert.True(RuntimeDatabaseRoles.TryFromConnectionStringName(
                RuntimeDatabaseRoles.ConnectionStringName(role),
                out var parsed));
            Assert.Equal(role, parsed);
        }

        Assert.False(RuntimeDatabaseRoles.TryFromConnectionStringName("paqueteria", out _));
        Assert.False(RuntimeDatabaseRoles.TryFromConnectionStringName("PaqueteriaMigration", out _));
        Assert.False(RuntimeDatabaseRoles.TryFromConnectionStringName(null, out _));
    }

    [Theory]
    [InlineData("Paqueteria", true)]
    [InlineData("PaqueteriaWorker", true)]
    [InlineData("PaqueteriaRealtime", false)]
    [InlineData("PaqueteriaKeyRing", false)]
    [InlineData("", false)]
    public void The_key_ring_reads_only_through_the_api_or_worker_role_pool(string connectionStringName, bool valid)
    {
        Assert.Equal(valid, DataProtectionOptionsValidator.IsValid(new DataProtectionOptions
        {
            Provider = DataProtectionProviderKind.PostgreSql,
            ConnectionStringName = connectionStringName,
        }));
    }

    private static DatabasePoolOptions ResolveOptions(Dictionary<string, string?> settings)
    {
        var services = Services(settings);
        services.AddRuntimeDataSource(
            services.Configuration(),
            RuntimeDatabaseRole.Application);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<DatabasePoolOptions>>().Value;
    }

    private static ServiceCollection Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        return services;
    }
}

internal static class Scl007ServiceCollectionTestExtensions
{
    public static IConfiguration Configuration(this IServiceCollection services) =>
        (IConfiguration)services.Single(descriptor => descriptor.ServiceType == typeof(IConfiguration))
            .ImplementationInstance!;
}
