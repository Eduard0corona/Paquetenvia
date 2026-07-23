using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure.Locations;
using Locations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Paqueteria.ArchitectureTests.Architecture;

namespace Paqueteria.ArchitectureTests;

public sealed class LocationsArchitectureTests
{
    [Fact]
    public void Locations_has_exactly_the_four_canonical_layers() => Assert.Equal(
        [ProjectRole.ModuleDomain, ProjectRole.ModuleApplication, ProjectRole.ModuleInfrastructure, ProjectRole.ModuleEndpoints],
        SolutionCatalog.Locations.Components.Select(component => component.Role));

    [Fact]
    public void Locations_owns_one_DbContext_and_only_in_infrastructure()
    {
        var contexts = SolutionCatalog.Locations.Components
            .SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsAssignableTo(typeof(DbContext)))
            .ToArray();
        Assert.Equal([typeof(LocationsDbContext)], contexts);
        Assert.Equal("Locations.Infrastructure", contexts[0].Assembly.GetName().Name);
    }

    [Fact]
    public void Geographic_ports_are_framework_and_Npgsql_independent()
    {
        Assert.Equal("Locations.Application", typeof(IGeocodingProvider).Assembly.GetName().Name);
        Assert.Equal("Locations.Application", typeof(IServiceabilityEvaluator).Assembly.GetName().Name);
        Assert.Contains(typeof(IServiceabilityEvaluator), typeof(PostgreSqlLocationService).GetInterfaces());
        var references = typeof(IGeocodingProvider).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);
        Assert.DoesNotContain(references, reference =>
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Endpoints_do_not_reference_Npgsql_or_expose_geometries()
    {
        var references = SolutionCatalog.Locations.Endpoints.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Assert.DoesNotContain(references, reference => reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, reference => reference.Contains("NetTopologySuite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Locations_contains_no_real_map_provider_generic_repository_or_product_crypto()
    {
        var root = TestRepository.GetPath("src/Modules/Locations");
        var source = string.Join('\n', Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("GoogleMaps", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mapbox", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HereMaps", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("here.com", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Repository<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Aes", source, StringComparison.Ordinal);
        Assert.Contains("DisabledLocationPiiProtector", source, StringComparison.Ordinal);
        Assert.Contains("DeterministicMockLocationPiiProtector", source, StringComparison.Ordinal);
    }
}
