using System.Reflection;
using Dispatch.Infrastructure;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.ExternalOffers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Orders.Application.Orders;
using Orders.Infrastructure;
using Orders.Infrastructure.Orders;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// ORD-002-API-GUARD-REGISTRY: every composition of the ORD-002 transition and of the Dispatch assignment and external
/// offer flows holds the AI-04 guards. The registry used to expose a public guard-list constructor next to the
/// parameterless one; a container activates the longest constructor it can satisfy and an
/// <c>IEnumerable&lt;IOrderTransitionGuard&gt;</c> is always satisfiable (empty), so the type registrations of the
/// Orders and Dispatch modules composed a registry without a single guard.
/// </summary>
public sealed class OrderTransitionGuardRegistryCompositionTests
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Pooling=false";

    private static readonly string[] Ai04GuardCodes =
        new OrderTransitionGuardRegistry().Guards.Select(guard => guard.Code).ToArray();

    [Fact]
    public void Only_the_AI04_defaults_can_be_built_outside_Orders_Application()
    {
        var constructor = Assert.Single(typeof(OrderTransitionGuardRegistry).GetConstructors());

        Assert.Empty(constructor.GetParameters());
        Assert.Equal(24, Ai04GuardCodes.Length);
    }

    [Fact]
    public void A_type_registration_composes_every_AI04_guard()
    {
        // The registration the Orders and Dispatch modules used to carry: it must never compose an empty registry.
        var services = new ServiceCollection();
        services.AddSingleton<OrderTransitionGuardRegistry>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.Equal(Ai04GuardCodes, Codes(provider.GetRequiredService<OrderTransitionGuardRegistry>()));
    }

    [Fact]
    public async Task Orders_composition_hands_every_AI04_guard_to_the_transition()
    {
        var configuration = Configuration();
        var services = Services(configuration);
        services.AddOrdersInfrastructure(configuration, new HostingEnvironment { EnvironmentName = "Production" });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var registry = provider.GetRequiredService<OrderTransitionGuardRegistry>();
        Assert.Equal(Ai04GuardCodes, Codes(registry));
        await using var scope = provider.CreateAsyncScope();
        Assert.Same(
            registry,
            HeldRegistry(scope.ServiceProvider.GetRequiredService<PostgreSqlOrderTransitionService>()));
    }

    [Fact]
    public async Task Dispatch_composition_hands_every_AI04_guard_to_the_assignment_and_external_offer_flows()
    {
        var configuration = Configuration();
        var services = Services(configuration);
        services.AddDispatchInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var registry = provider.GetRequiredService<OrderTransitionGuardRegistry>();
        Assert.Equal(Ai04GuardCodes, Codes(registry));
        await using var scope = provider.CreateAsyncScope();
        Assert.Same(
            registry,
            HeldRegistry(scope.ServiceProvider.GetRequiredService<PostgreSqlAssignmentToOrderCoordinator>()));
        Assert.Same(
            registry,
            HeldRegistry(scope.ServiceProvider.GetRequiredService<PostgreSqlExternalOfferService>()));
    }

    [Fact]
    public async Task The_api_module_order_composes_one_AI04_registry_for_Orders_and_Dispatch()
    {
        // Program.cs adds Dispatch before Orders: whichever registration wins, both flows hold every guard.
        var configuration = Configuration();
        var services = Services(configuration);
        services.AddDispatchInfrastructure(configuration);
        services.AddOrdersInfrastructure(configuration, new HostingEnvironment { EnvironmentName = "Production" });
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(OrderTransitionGuardRegistry));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var registry = provider.GetRequiredService<OrderTransitionGuardRegistry>();
        Assert.Equal(Ai04GuardCodes, Codes(registry));
        await using var scope = provider.CreateAsyncScope();
        Assert.Same(
            registry,
            HeldRegistry(scope.ServiceProvider.GetRequiredService<PostgreSqlOrderTransitionService>()));
        Assert.Same(
            registry,
            HeldRegistry(scope.ServiceProvider.GetRequiredService<PostgreSqlAssignmentToOrderCoordinator>()));
        Assert.Same(
            registry,
            HeldRegistry(scope.ServiceProvider.GetRequiredService<PostgreSqlExternalOfferService>()));
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Paqueteria"] = UnreachableDatabase,
            })
            .Build();

    private static ServiceCollection Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        return services;
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
}
