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
    public void Locations_contains_no_unapproved_map_provider_generic_repository_or_product_crypto()
    {
        var root = TestRepository.GetPath("src/Modules/Locations");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(root, path))
            .ToArray();
        var source = string.Join('\n', files.Select(File.ReadAllText));
        // GATE-003-PROVIDER-GOOGLE approves Google Maps Platform only; other map providers stay out.
        Assert.DoesNotContain("Mapbox", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HereMaps", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("here.com", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Repository<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Aes", source, StringComparison.Ordinal);
        Assert.Contains("DisabledLocationPiiProtector", source, StringComparison.Ordinal);
        Assert.Contains("DeterministicMockLocationPiiProtector", source, StringComparison.Ordinal);
        Assert.Contains("DeterministicMockGeocodingProvider", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Google_Maps_lives_only_behind_the_geocoding_port_in_infrastructure()
    {
        var root = TestRepository.GetPath("src/Modules/Locations");
        var allowed = new[]
        {
            "Locations.Infrastructure/Geocoding/GoogleMaps/",
            "Locations.Infrastructure/LocationsOptions.cs",
            "Locations.Infrastructure/DependencyInjection.cs",
        };
        var offenders = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(root, path))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !allowed.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(relative =>
            {
                var text = File.ReadAllText(Path.Combine(root, relative));
                return text.Contains("Google", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("googleapis", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        Assert.Empty(offenders);
        Assert.Contains(
            typeof(IGeocodingProvider),
            typeof(Locations.Infrastructure.Geocoding.GoogleMaps.GoogleMapsGeocodingProvider).GetInterfaces());
    }

    private static bool IsBuildOutput(string root, string path)
    {
        var segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }
}
