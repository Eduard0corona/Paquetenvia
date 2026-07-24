using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Paqueteria.ArchitectureTests.Architecture;
using Realtime.Application.Clients;
using Realtime.Endpoints.Hubs;

namespace Paqueteria.ArchitectureTests;

public sealed class RealtimeArchitectureTests
{
    [Fact]
    public void Realtime_has_the_four_canonical_layers_without_artificial_domain_types()
    {
        Assert.Equal(
            [
                ProjectRole.ModuleDomain,
                ProjectRole.ModuleApplication,
                ProjectRole.ModuleInfrastructure,
                ProjectRole.ModuleEndpoints,
            ],
            SolutionCatalog.Realtime.Components.Select(component => component.Role));
        Assert.Equal(
            [typeof(Realtime.Domain.AssemblyReference)],
            SolutionCatalog.Realtime.Domain.Assembly.GetExportedTypes());
    }

    [Fact]
    public void Application_and_domain_are_free_of_ASPNET_Npgsql_and_EF()
    {
        foreach (var component in new[]
                 {
                     SolutionCatalog.Realtime.Domain,
                     SolutionCatalog.Realtime.Application,
                 })
        {
            var references = component.Assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .ToArray();
            Assert.DoesNotContain(references, reference =>
                reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
                reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Hubs_are_exact_typed_and_expose_only_lifecycle_overrides()
    {
        AssertHub<OperationsHub, IOperationsClient>();
        AssertHub<DriverHub, IDriverClient>();
        AssertHub<TrackingHub, ITrackingClient>();
        Assert.Equal(
            [typeof(DriverHub), typeof(OperationsHub), typeof(TrackingHub)],
            SolutionCatalog.Realtime.Endpoints.Assembly.GetExportedTypes()
                .Where(type => type.IsAssignableTo(typeof(Hub)))
                .OrderBy(type => type.Name)
                .ToArray());
    }

    [Fact]
    public void Realtime_contains_no_outbox_consumer_business_state_or_mutable_connection_singleton()
    {
        var sourceFiles = Directory.GetFiles(
            TestRepository.GetPath("src/Modules/Realtime"),
            "*.cs",
            SearchOption.AllDirectories);
        var source = string.Join('\n', sourceFiles.Select(File.ReadAllText));
        Assert.DoesNotContain("claim_outbox", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settle_outbox", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("location_outbox_events", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dictionary<string", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", source, StringComparison.Ordinal);

        var workerReferences = SolutionCatalog.Worker.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);
        Assert.DoesNotContain(workerReferences, reference =>
            reference.StartsWith("Realtime.", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_uses_only_application_contracts_from_other_modules()
    {
        var references = SolutionCatalog.Realtime.Infrastructure.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.Contains("Orders.Application", references);
        Assert.Contains("Organizations.Application", references);
        Assert.DoesNotContain("Orders.Infrastructure", references);
        Assert.DoesNotContain("Organizations.Infrastructure", references);
        Assert.DoesNotContain("Dispatch.Infrastructure", references);
        Assert.DoesNotContain("Drivers.Infrastructure", references);
    }

    private static void AssertHub<THub, TClient>()
        where THub : Hub<TClient>
        where TClient : class
    {
        var methods = typeof(THub).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["OnConnectedAsync", "OnDisconnectedAsync"], methods);
    }
}
