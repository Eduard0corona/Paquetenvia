using Drivers.Application.Eligibility;
using Drivers.Infrastructure.Eligibility;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class DriversArchitectureTests
{
    [Fact]
    public void Drivers_has_the_canonical_layers_and_location_endpoint()
    {
        Assert.Equal(
            [ProjectRole.ModuleDomain, ProjectRole.ModuleApplication, ProjectRole.ModuleInfrastructure,
                ProjectRole.ModuleEndpoints],
            SolutionCatalog.Drivers.Components.Select(component => component.Role));
        Assert.Contains(
            SolutionCatalog.Drivers.Endpoints.Assembly.GetTypes(),
            type => type.Name == "DriverLocationEndpoints");
    }

    [Fact]
    public void Drivers_owns_one_DbContext_and_exposes_only_the_three_aggregate_sets()
    {
        var contexts = SolutionCatalog.Drivers.Components
            .SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsAssignableTo(typeof(DbContext)))
            .ToArray();
        Assert.Equal([typeof(DriversDbContext)], contexts);
        Assert.Equal(
            ["DriverDocuments", "DriverProfiles", "DriverServiceAreas"],
            typeof(DriversDbContext).GetProperties()
                .Where(property => property.PropertyType.IsGenericType &&
                    property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Eligibility_port_and_policy_are_framework_independent()
    {
        Assert.Equal("Drivers.Application", typeof(IDriverEligibilityService).Assembly.GetName().Name);
        Assert.Contains(typeof(IDriverEligibilityService), typeof(PostgreSqlDriverEligibilityService).GetInterfaces());
        var references = typeof(IDriverEligibilityService).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(references, reference =>
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Driver_location_lane_has_no_realtime_worker_storage_or_generic_repository_dependency()
    {
        var root = TestRepository.GetPath("src/Modules/Drivers");
        var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        var source = string.Join('\n', sources);

        Assert.Contains("MapPost(\"/api/v1/driver/me/location-updates\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IObjectStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OCR", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Repository<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IRealtimePublisher", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IHubContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Realtime.Infrastructure", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Realtime.Endpoints", source, StringComparison.Ordinal);

        var domainReferences = typeof(Drivers.Domain.AssemblyReference).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(domainReferences, reference =>
            reference.Name is not null &&
            (reference.Name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
             reference.Name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
             reference.Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
             reference.Name.Contains("NetTopologySuite", StringComparison.OrdinalIgnoreCase)));

        var endpointReferences = typeof(Drivers.Endpoints.AssemblyReference).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(endpointReferences, reference =>
            reference.Name is not null &&
            (reference.Name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
             reference.Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)));
    }
}
