extern alias WorkerHost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Paqueteria.Infrastructure.Database.Pooling;
using Paqueteria.IntegrationTests.Hosting;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Scaling;

/// <summary>
/// SCL-007 in the real API and Worker compositions: each process registers exactly one pool per database role it uses
/// (API: <c>paqueteria_app</c> and the realtime dispatchers' <c>paqueteria_realtime</c>; Worker:
/// <c>paqueteria_worker</c>), no unkeyed or second pool, and every module's EF Core context runs on that one pool, sized
/// by <c>DatabasePools</c> whatever <c>Maximum Pool Size</c> the connection string carries. Nothing opens a connection.
/// </summary>
public sealed class Scl007PoolPerRoleCompositionTests
{
    // The pilot runtime secrets carry Maximum Pool Size=5 (deploy-azure-pilot.yml); DatabasePools replaces it.
    private const string Api =
        "Host=127.0.0.1;Port=1;Database=paqueteria;Username=scl007_api;Password=unused;Maximum Pool Size=5;Minimum Pool Size=0";
    private const string Realtime =
        "Host=127.0.0.1;Port=1;Database=paqueteria;Username=scl007_realtime;Password=unused;Maximum Pool Size=5;Minimum Pool Size=0";
    private const string Worker =
        "Host=127.0.0.1;Port=1;Database=paqueteria;Username=scl007_worker;Password=unused;Maximum Pool Size=5;Minimum Pool Size=0";

    [Fact]
    public async Task The_api_holds_one_application_pool_and_one_realtime_pool_shared_by_every_module()
    {
        await using var api = new CompositionFactory<Program>(new()
        {
            ["Authentication:Provider"] = "Mock",
            ["IdentityBootstrap:Provider"] = "Mock",
            ["ConnectionStrings:Paqueteria"] = Api,
            ["ConnectionStrings:PaqueteriaRealtime"] = Realtime,
        });
        var provider = api.Services;
        var services = api.Registrations;

        AssertOnlyPools(services, RuntimeDatabaseRole.Application, RuntimeDatabaseRole.Realtime);
        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(RuntimeDataSourcePlugin) &&
            descriptor.ImplementationInstance is RuntimeDataSourcePlugin
            {
                Role: RuntimeDatabaseRole.Application,
                Name: RuntimeDataSourcePlugin.NetTopologySuite,
            });

        var application = provider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Application);
        var realtime = provider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Realtime);
        AssertPool(application, "scl007_api", 10);
        AssertPool(realtime, "scl007_realtime", 4);

        var contexts = AssertContextsUse(services, provider, application.DataSource);
        // NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007): the recipient notice panel's tenant transactions run on
        // the Notifications context, on the same shared pool.
        Assert.Equal(
            [
                "CustodyDbContext", "DispatchDbContext", "DriversDbContext", "FinanceDbContext", "IdentityDbContext",
                "IncidentsDbContext", "LocationsDbContext", "NotificationsDbContext", "OrdersDbContext",
                "OrganizationsDbContext", "PricingDbContext", "RoutingDbContext",
            ],
            contexts);
    }

    [Fact]
    public async Task The_worker_holds_one_worker_pool_shared_by_every_module_and_job()
    {
        await using var worker = new CompositionFactory<WorkerProgram>(new()
        {
            // The pilot Worker receives its one secret under both names (apps.bicep); only the Worker name is read.
            ["ConnectionStrings:Paqueteria"] = Worker,
            ["ConnectionStrings:PaqueteriaWorker"] = Worker,
            ["DataProtection:Provider"] = "PostgreSql",
            ["Orders:AutoClose:Enabled"] = "false",
        });
        var provider = worker.Services;
        var services = worker.Registrations;

        AssertOnlyPools(services, RuntimeDatabaseRole.Worker);
        var pool = provider.GetRequiredKeyedService<RuntimeDataSource>(RuntimeDatabaseRole.Worker);
        AssertPool(pool, "scl007_worker", 10);

        var contexts = AssertContextsUse(services, provider, pool.DataSource);
        Assert.Equal(["CustodyDbContext", "DispatchDbContext", "OrdersDbContext"], contexts);
    }

    [Fact]
    public async Task A_pool_size_above_the_budget_refuses_the_start_of_either_process()
    {
        await using var api = new CompositionFactory<Program>(new()
        {
            ["Authentication:Provider"] = "Mock",
            ["IdentityBootstrap:Provider"] = "Mock",
            ["ConnectionStrings:Paqueteria"] = Api,
            ["DatabasePools:ApplicationMaximumPoolSize"] = "20",
        });
        await using var worker = new CompositionFactory<WorkerProgram>(new()
        {
            ["ConnectionStrings:PaqueteriaWorker"] = Worker,
            ["DatabasePools:WorkerMaximumPoolSize"] = "11",
        });

        foreach (var exception in new[] { Record.Exception(() => api.Services), Record.Exception(() => worker.Services) })
        {
            Assert.NotNull(exception);
            Assert.Contains(
                "must not exceed the SCL-007 budget of 24 connections",
                exception.ToString(),
                StringComparison.Ordinal);
        }
    }

    private static void AssertOnlyPools(IServiceCollection services, params RuntimeDatabaseRole[] roles)
    {
        foreach (var role in RuntimeDatabaseRoles.All)
        {
            var expected = roles.Contains(role) ? 1 : 0;
            Assert.Equal(expected, services.Count(descriptor =>
                descriptor.ServiceType == typeof(RuntimeDataSource) && Equals(descriptor.ServiceKey, role)));
            Assert.Equal(expected, services.Count(descriptor =>
                descriptor.ServiceType == typeof(NpgsqlDataSource) && Equals(descriptor.ServiceKey, role)));
        }

        Assert.Equal(roles.Length, services.Count(descriptor => descriptor.ServiceType == typeof(RuntimeDataSource)));
        Assert.Equal(roles.Length, services.Count(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(NpgsqlDataSource) && !descriptor.IsKeyedService);
    }

    private static void AssertPool(RuntimeDataSource pool, string login, int maximumPoolSize)
    {
        Assert.Equal(maximumPoolSize, pool.MaximumPoolSize);
        var settings = new NpgsqlConnectionStringBuilder(pool.DataSource.ConnectionString);
        Assert.Equal(login, settings.Username);
        Assert.Equal(maximumPoolSize, settings.MaxPoolSize);
        Assert.Equal(0, settings.MinPoolSize);
    }

    /// <summary>Resolves every registered EF Core context and returns their names, each checked on <paramref name="pool"/>.</summary>
    private static string[] AssertContextsUse(
        IServiceCollection services,
        IServiceProvider provider,
        NpgsqlDataSource pool)
    {
        var contextTypes = services
            .Where(descriptor => typeof(DbContext).IsAssignableFrom(descriptor.ServiceType) && !descriptor.IsKeyedService)
            .Select(descriptor => descriptor.ServiceType)
            .Distinct()
            .ToArray();
        using var scope = provider.CreateScope();
        foreach (var type in contextTypes)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(type);
#pragma warning disable EF1001 // The data source EF Core was configured with is only reachable through its options extension.
            var dataSource = context.GetService<IDbContextOptions>().FindExtension<NpgsqlOptionsExtension>()?.DataSource;
#pragma warning restore EF1001
            Assert.True(ReferenceEquals(pool, dataSource), $"{type.Name} does not use the shared pool of its role.");
        }

        return contextTypes.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class CompositionFactory<TEntryPoint>(Dictionary<string, string?> settings)
        : StartupFailureSurfacingWebApplicationFactory<TEntryPoint>
        where TEntryPoint : class
    {
        private IServiceCollection? _registrations;

        public IServiceCollection Registrations =>
            _registrations ?? throw new InvalidOperationException("The host has not been built.");

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Host settings reach the composition itself (Identity registers its context only with a connection
            // string, and Data Protection reads its provider while composing), not only the started host.
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services => _registrations = services);
        }
    }
}
