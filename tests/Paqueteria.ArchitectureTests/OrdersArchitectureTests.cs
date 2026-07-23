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
    public void Cross_schema_access_is_limited_to_named_coordinators_and_narrow_readers()
    {
        var root = TestRepository.GetPath("src/Modules/Orders");
        var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path))).ToArray();
        var pricingReaders = sources.Where(item => item.Source.Contains("pricing.quotes", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, pricingReaders.Length);
        Assert.Contains(pricingReaders, item =>
            item.Path.EndsWith("QuoteSnapshotToOrderCoordinator.cs", StringComparison.Ordinal));
        Assert.Contains(pricingReaders, item =>
            item.Path.EndsWith("PostgreSqlOrderTransitionReaders.cs", StringComparison.Ordinal));
        Assert.Equal("QuoteSnapshotToOrderCoordinator", typeof(QuoteSnapshotToOrderCoordinator).Name);
        Assert.All(pricingReaders, item =>
        {
            Assert.DoesNotContain("PricingDbContext", item.Source, StringComparison.Ordinal);
            Assert.DoesNotContain("LocationsDbContext", item.Source, StringComparison.Ordinal);
        });

        var readers = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Orders/Orders.Infrastructure/Orders/PostgreSqlOrderTransitionReaders.cs"));
        Assert.DoesNotContain("INSERT INTO", readers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", readers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", readers, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Orders_contains_no_generic_repository_and_state_matrix_lives_only_in_domain()
    {
        var root = TestRepository.GetPath("src/Modules/Orders");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        var source = string.Join('\n', files.Select(File.ReadAllText));
        Assert.DoesNotContain("Repository<", source, StringComparison.Ordinal);
        Assert.Single(files, path =>
            File.ReadAllText(path).Contains("class OrderTransitionMatrix", StringComparison.Ordinal));
        Assert.EndsWith(
            Path.Combine("Orders.Domain", "OrderTransition.cs"),
            files.Single(path => File.ReadAllText(path).Contains(
                "class OrderTransitionMatrix",
                StringComparison.Ordinal)),
            StringComparison.Ordinal);

        var endpoints = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Orders/Orders.Endpoints/OrderEndpoints.cs"));
        Assert.DoesNotContain("IOrderTransitionGuard", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("valid_active_quote", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("custody.proofs", endpoints, StringComparison.Ordinal);
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
        var transition = File.ReadAllText(TestRepository.GetPath(
            "src/Modules/Orders/Orders.Infrastructure/Orders/PostgreSqlOrderTransitionService.cs"));
        Assert.DoesNotContain(" RETURNING ", transition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IHubContext", transition, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", transition, StringComparison.Ordinal);
        Assert.Contains("AND owner_org_id=@org", transition, StringComparison.Ordinal);
    }

    [Fact]
    public void Transition_application_contracts_are_framework_and_infrastructure_independent()
    {
        Assert.Equal("Orders.Application", typeof(IOrderTransitionService).Assembly.GetName().Name);
        Assert.Equal("Orders.Application", typeof(IOrderTransitionGuard).Assembly.GetName().Name);
        Assert.Equal("Orders.Application", typeof(IOrderTransitionAuthorizer).Assembly.GetName().Name);
        var references = Names(SolutionCatalog.Orders.Application.Assembly);
        Assert.DoesNotContain(references, reference =>
            reference.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("SignalR", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] Names(System.Reflection.Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
}
