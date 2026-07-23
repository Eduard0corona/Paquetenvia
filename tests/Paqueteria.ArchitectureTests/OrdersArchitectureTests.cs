using Microsoft.EntityFrameworkCore;
using Orders.Application.Orders;
using Orders.Infrastructure.Orders;
using Orders.Infrastructure.Persistence;
using Paqueteria.ArchitectureTests.Architecture;
using Paqueteria.Contracts.Legal;

namespace Paqueteria.ArchitectureTests;

public sealed class OrdersArchitectureTests
{
    [Fact]
    public void Orders_has_exactly_the_four_canonical_layers() => Assert.Equal(
        [ProjectRole.ModuleDomain, ProjectRole.ModuleApplication, ProjectRole.ModuleInfrastructure, ProjectRole.ModuleEndpoints],
        SolutionCatalog.Orders.Components.Select(component => component.Role));

    [Fact]
    public void Domain_is_framework_json_and_cross_module_independent()
    {
        var references = SolutionCatalog.Orders.Domain.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, reference =>
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Pricing", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Locations", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Json", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Orders_owns_one_DbContext_only_in_infrastructure()
    {
        var contexts = SolutionCatalog.Orders.Components.SelectMany(component => component.Assembly.GetTypes())
            .Where(type => type.IsAssignableTo(typeof(DbContext))).ToArray();
        Assert.Equal([typeof(OrdersDbContext)], contexts);
        Assert.Equal("Orders.Infrastructure", contexts[0].Assembly.GetName().Name);
    }

    [Fact]
    public void Application_and_endpoints_do_not_depend_on_infrastructure_drivers()
    {
        var application = Names(SolutionCatalog.Orders.Application.Assembly);
        var endpoints = Names(SolutionCatalog.Orders.Endpoints.Assembly);
        Assert.DoesNotContain("Orders.Infrastructure", application);
        Assert.DoesNotContain(endpoints, reference =>
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference == "Orders.Infrastructure");
        Assert.Equal("Orders.Application", typeof(IOrderService).Assembly.GetName().Name);
    }

    [Fact]
    public void Infrastructure_has_no_pricing_or_locations_implementation_dependency()
    {
        var references = Names(SolutionCatalog.Orders.Infrastructure.Assembly);
        Assert.DoesNotContain("Pricing.Infrastructure", references);
        Assert.DoesNotContain("Locations.Infrastructure", references);
        Assert.DoesNotContain("Pricing.Application", references);
        Assert.DoesNotContain("Locations.Application", references);
    }

    [Fact]
    public void Cross_schema_coordination_is_the_single_named_quote_to_order_coordinator()
    {
        var root = TestRepository.GetPath("src/Modules/Orders");
        var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path))).ToArray();
        var pricingReaders = sources.Where(item => item.Source.Contains("pricing.quotes", StringComparison.Ordinal)).ToArray();
        Assert.Single(pricingReaders);
        Assert.EndsWith("QuoteSnapshotToOrderCoordinator.cs", pricingReaders[0].Path, StringComparison.Ordinal);
        Assert.Equal("QuoteSnapshotToOrderCoordinator", typeof(QuoteSnapshotToOrderCoordinator).Name);
        Assert.DoesNotContain("PricingDbContext", pricingReaders[0].Source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocationsDbContext", pricingReaders[0].Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ORD001_contains_no_generic_repository_or_later_state_transition_rules()
    {
        var root = TestRepository.GetPath("src/Modules/Orders");
        var source = string.Join('\n', Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("Repository<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmOrder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AssignOrder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatchOrder", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonicalizer_is_product_contract_code_and_responses_exclude_sensitive_fields()
    {
        Assert.Equal("Paqueteria.Contracts", typeof(OrderAcceptanceCanonicalizer).Assembly.GetName().Name);
        var responseProperties = typeof(Orders.Endpoints.OrderResponse).GetProperties()
            .Select(property => property.Name).ToArray();
        Assert.DoesNotContain(responseProperties, name =>
            name.Contains("Acceptance", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Evidence", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cipher", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Idempotency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Adoption_migration_and_runtime_inserts_are_non_destructive_and_use_no_RETURNING()
    {
        var migration = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Orders/Orders.Infrastructure/Persistence/Migrations/20260722_AdoptCanonicalOrdersBaseline.cs"));
        Assert.DoesNotContain("CreateTable", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("AlterColumn", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("DropTable", migration, StringComparison.Ordinal);

        var coordinator = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Orders/Orders.Infrastructure/Orders/QuoteSnapshotToOrderCoordinator.cs"));
        Assert.DoesNotContain(" RETURNING ", coordinator, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Names(System.Reflection.Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
}
