using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Domain.Tenancy;
using Realtime.Application.Authorization;
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
    public void Realtime_dispatchers_use_canonical_functions_without_mutable_authoritative_state()
    {
        var sourceFiles = Directory.GetFiles(
            TestRepository.GetPath("src/Modules/Realtime"),
            "*.cs",
            SearchOption.AllDirectories);
        var source = string.Join('\n', sourceFiles.Select(File.ReadAllText));
        Assert.Contains("security.claim_outbox", source, StringComparison.Ordinal);
        Assert.Contains("security.settle_outbox", source, StringComparison.Ordinal);
        Assert.Contains("security.requeue_stale_outbox", source, StringComparison.Ordinal);
        Assert.Contains("security.claim_location_outbox", source, StringComparison.Ordinal);
        Assert.Contains("security.settle_location_outbox", source, StringComparison.Ordinal);
        Assert.Contains("security.requeue_stale_location_outbox", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE platform.outbox_events", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM platform.outbox_events", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE platform.location_outbox_events", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM platform.location_outbox_events", source, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Operations_role_blocker_cannot_be_silently_implemented_or_mapped()
    {
        Assert.Equal(
            [
                "PlatformAdmin",
                "Dispatcher",
                "Finance",
                "AllyAdmin",
                "AllyOperator",
                "BusinessAdmin",
                "BusinessOperator",
                "Driver",
                "Viewer",
            ],
            Enum.GetNames<OrganizationRole>());
        Assert.True(RealtimeOperationsRolePolicy.IsAllowed(OrganizationRole.PlatformAdmin, true));
        Assert.False(RealtimeOperationsRolePolicy.IsAllowed(OrganizationRole.PlatformAdmin, false));
        Assert.True(RealtimeOperationsRolePolicy.IsAllowed(OrganizationRole.Dispatcher, false));
        Assert.False(RealtimeOperationsRolePolicy.IsAllowed(OrganizationRole.Viewer, true));
        Assert.False(RealtimeOperationsRolePolicy.IsAllowed(OrganizationRole.Driver, true));

        var productiveSources = Directory.GetFiles(
                TestRepository.GetPath("src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText);
        Assert.DoesNotContain(
            productiveSources,
            source => source.Contains("CUSTOMER_SUPPORT", StringComparison.OrdinalIgnoreCase));
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
