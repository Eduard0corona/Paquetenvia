extern alias WorkerHost;

using System.Reflection;
using Dispatch.Application.Assignments;
using Dispatch.Application.ExternalOffers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application.Orders;
using Paqueteria.IntegrationTests.Hosting;
using WorkerProgram = WorkerHost::WorkerProgram;

namespace Paqueteria.IntegrationTests.Orders;

/// <summary>
/// ORD-002-API-GUARD-REGISTRY in the real host compositions: the API <c>Program</c> and the Worker host resolve the
/// AI-04 guard registry, and every service that moves an order (the transitionOrder service, the own-driver assignment
/// coordinator and the external-offer service of the API, the system close of the Worker) holds that same registry.
/// Before the fix the API composed the registry from its public guard-list constructor with no guard registered, so
/// every manual transition and every Dispatch assignment skipped the AI-04 guards.
/// </summary>
public sealed class OrderTransitionGuardHostCompositionTests
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=ord002guards;Username=unused;Password=unused;Timeout=3;Pooling=false";

    private static readonly string[] Ai04GuardCodes =
        new OrderTransitionGuardRegistry().Guards.Select(guard => guard.Code).ToArray();

    [Fact]
    public async Task Api_host_hands_every_AI04_guard_to_transitions_assignments_and_external_offers()
    {
        await using var api = new ApiFactory();

        var registry = api.Services.GetRequiredService<OrderTransitionGuardRegistry>();
        Assert.Equal(Ai04GuardCodes, Codes(registry));
        Assert.Contains("no_unresolved_incident", Codes(registry));
        Assert.Contains("eligible_driver", Codes(registry));

        // The services the endpoints resolve, under the PostgreSQL providers the pilot runs.
        await using var scope = api.Services.CreateAsyncScope();
        var transitions = scope.ServiceProvider.GetRequiredService<IOrderTransitionService>();
        var assignments = scope.ServiceProvider.GetRequiredService<IAssignmentService>();
        var externalOffers = scope.ServiceProvider.GetRequiredService<IExternalOfferService>();
        Assert.Equal("PostgreSqlOrderTransitionService", transitions.GetType().Name);
        Assert.Equal("PostgreSqlAssignmentToOrderCoordinator", assignments.GetType().Name);
        Assert.Equal("PostgreSqlExternalOfferService", externalOffers.GetType().Name);
        Assert.Same(registry, HeldRegistry(transitions));
        Assert.Same(registry, HeldRegistry(assignments));
        Assert.Same(registry, HeldRegistry(externalOffers));
    }

    [Fact]
    public async Task Worker_host_hands_every_AI04_guard_to_the_system_close()
    {
        await using var worker = new WorkerFactory();

        var registry = worker.Services.GetRequiredService<OrderTransitionGuardRegistry>();
        Assert.Equal(Ai04GuardCodes, Codes(registry));
        await using var scope = worker.Services.CreateAsyncScope();
        Assert.Same(
            registry,
            HeldRegistry(scope.ServiceProvider.GetRequiredService<IOrderSystemTransitionService>()));
    }

    private static string[] Codes(OrderTransitionGuardRegistry registry) =>
        registry.Guards.Select(guard => guard.Code).ToArray();

    /// <summary>The registry a composed service captured from its constructor.</summary>
    private static OrderTransitionGuardRegistry HeldRegistry(object service)
    {
        var field = Assert.Single(
            service.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            candidate => candidate.FieldType == typeof(OrderTransitionGuardRegistry));
        return Assert.IsType<OrderTransitionGuardRegistry>(field.GetValue(service));
    }

    /// <summary>The API with the Orders, Drivers and Dispatch PostgreSQL providers; nothing here opens a connection.</summary>
    private sealed class ApiFactory : StartupFailureSurfacingWebApplicationFactory<Program>
    {
        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>(OrderTransitionGuardsApiFactory.DriverEligibilitySettings)
                    {
                        ["ConnectionStrings:Paqueteria"] = UnreachableDatabase,
                        ["Orders:Provider"] = "PostgreSql",
                        ["Drivers:Provider"] = "PostgreSql",
                        ["Dispatch:Provider"] = "PostgreSql",
                    }));
        }
    }

    private sealed class WorkerFactory : StartupFailureSurfacingWebApplicationFactory<WorkerProgram>
    {
        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:PaqueteriaWorker"] = UnreachableDatabase,
                }));
        }
    }
}
