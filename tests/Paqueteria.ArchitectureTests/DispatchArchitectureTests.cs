using Dispatch.Application.Assignments;
using Dispatch.Infrastructure.Assignments;
using Dispatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class DispatchArchitectureTests
{
    [Fact]
    public void Dispatch_has_the_four_canonical_layers()
    {
        Assert.Equal(
            [
                ProjectRole.ModuleDomain,
                ProjectRole.ModuleApplication,
                ProjectRole.ModuleInfrastructure,
                ProjectRole.ModuleEndpoints,
            ],
            SolutionCatalog.Dispatch.Components.Select(component => component.Role));
    }

    [Fact]
    public void Domain_and_application_do_not_reference_forbidden_frameworks()
    {
        var domainReferences = SolutionCatalog.Dispatch.Domain.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        var applicationReferences = SolutionCatalog.Dispatch.Application.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(domainReferences, IsFrameworkOrInfrastructure);
        Assert.DoesNotContain(applicationReferences, reference =>
            reference is "Dispatch.Infrastructure" ||
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Endpoints_contain_no_EF_Core_or_Npgsql()
    {
        var references = SolutionCatalog.Dispatch.Endpoints.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);
        Assert.DoesNotContain(references, reference =>
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Infrastructure_references_only_pure_orders_and_drivers_contracts()
    {
        var references = SolutionCatalog.Dispatch.Infrastructure.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain("Orders.Infrastructure", references);
        Assert.DoesNotContain("Drivers.Infrastructure", references);
        Assert.DoesNotContain("Locations.Infrastructure", references);
        Assert.Contains("Orders.Domain", references);
        Assert.Contains("Orders.Application", references);
        Assert.Contains("Drivers.Application", references);
    }

    [Fact]
    public void Dispatch_owns_one_DbContext_and_does_not_expose_its_set()
    {
        var contexts = SolutionCatalog.Dispatch.Components
            .SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsAssignableTo(typeof(DbContext)))
            .ToArray();
        Assert.Equal([typeof(DispatchDbContext)], contexts);
        Assert.DoesNotContain(
            typeof(DispatchDbContext).GetProperties(),
            property => property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>));
    }

    [Fact]
    public void Only_the_allowlisted_cross_module_coordinator_is_present()
    {
        Assert.Equal(
            "assignment_to_order_status_event",
            PostgreSqlAssignmentToOrderCoordinator.CoordinationFlow);
        Assert.Contains(
            typeof(IAssignmentService),
            typeof(PostgreSqlAssignmentToOrderCoordinator).GetInterfaces());
    }

    [Fact]
    public void Dispatch_does_not_implement_external_offers_routes_positions_signalr_or_generic_repositories()
    {
        var root = TestRepository.GetPath("src/Modules/Dispatch");
        var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        var source = string.Join('\n', sources);

        Assert.DoesNotContain("ExternalOffer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RouteStop", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DriverPosition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Repository<", source, StringComparison.Ordinal);
    }

    private static bool IsFrameworkOrInfrastructure(string reference) =>
        reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
        reference.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase);
}
