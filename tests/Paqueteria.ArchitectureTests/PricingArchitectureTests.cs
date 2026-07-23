using Locations.Application.Locations;
using Microsoft.EntityFrameworkCore;
using Paqueteria.ArchitectureTests.Architecture;
using Pricing.Application.Quotes;
using Pricing.Infrastructure.Persistence;
using Pricing.Infrastructure.Quotes;

namespace Paqueteria.ArchitectureTests;

public sealed class PricingArchitectureTests
{
    [Fact]
    public void Pricing_has_exactly_the_four_canonical_layers() => Assert.Equal(
        [ProjectRole.ModuleDomain, ProjectRole.ModuleApplication, ProjectRole.ModuleInfrastructure, ProjectRole.ModuleEndpoints],
        SolutionCatalog.Pricing.Components.Select(component => component.Role));

    [Fact]
    public void Domain_is_framework_json_and_cross_module_independent()
    {
        var references = SolutionCatalog.Pricing.Domain.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, reference =>
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Locations", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Organizations", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pricing_owns_one_DbContext_only_in_infrastructure()
    {
        var contexts = SolutionCatalog.Pricing.Components.SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsAssignableTo(typeof(DbContext))).ToArray();
        Assert.Equal([typeof(PricingDbContext)], contexts);
        Assert.Equal("Pricing.Infrastructure", contexts[0].Assembly.GetName().Name);
    }

    [Fact]
    public void Cross_module_dependency_is_the_public_quote_location_port_only()
    {
        Assert.Equal("Locations.Application", typeof(IQuoteLocationResolver).Assembly.GetName().Name);
        Assert.Contains(typeof(IQuoteLocationResolver), typeof(PostgreSqlQuoteService).GetConstructors()
            .Single().GetParameters().Select(parameter => parameter.ParameterType));
        var references = SolutionCatalog.Pricing.Infrastructure.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.Contains("Locations.Application", references);
        Assert.DoesNotContain("Locations.Infrastructure", references);
        Assert.DoesNotContain("Locations.Domain", references);
        Assert.DoesNotContain("Locations.Endpoints", references);
    }

    [Fact]
    public void Endpoints_have_no_EF_Npgsql_or_infrastructure_reference()
    {
        var references = SolutionCatalog.Pricing.Endpoints.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, reference =>
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference == "Pricing.Infrastructure");
        Assert.Equal("Pricing.Application", typeof(IQuoteService).Assembly.GetName().Name);
    }

    [Fact]
    public void Pricing_contains_no_generic_repository_map_tax_provider_or_hardcoded_price()
    {
        var root = TestRepository.GetPath("src/Modules/Pricing");
        var source = string.Join('\n', Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("Repository<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GoogleMaps", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mapbox", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VatRate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TaxProvider", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("decimal", ReadDomainSource(root), StringComparison.Ordinal);
        Assert.DoesNotContain("double", ReadDomainSource(root), StringComparison.Ordinal);
    }

    private static string ReadDomainSource(string pricingRoot)
    {
        var domain = Path.Combine(pricingRoot, "Pricing.Domain");
        return string.Join('\n', Directory.GetFiles(domain, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
    }
}
